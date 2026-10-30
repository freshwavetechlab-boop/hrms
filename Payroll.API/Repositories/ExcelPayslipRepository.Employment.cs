using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    internal static async Task AnnotateEmploymentAsync(MySqlConnection db, ExcelPayslipBatch batch, MySqlTransaction? tx = null)
    {
        var masters = (await db.QueryAsync<EmploymentMaster>(@"SELECT e.Id,e.ClientId,e.EmployeeCode,e.FirstName,e.LastName,e.IsActive,e.WorkLocationId,
COALESCE(p.PanNumber,'') Pan,COALESCE(p.AadhaarNumber,'') Aadhaar,COALESCE(p.UanNumber,'') Uan,
COALESCE(w.Name,'') WorkLocation,COALESCE(w.City,'') LocationCity," + EmployeeEmploymentRules.EligibleInMonth("e", "@Month") + @" Eligible
FROM employees e LEFT JOIN employeepersonaldetails p ON p.EmployeeId=e.Id
LEFT JOIN worklocations w ON w.Id=e.WorkLocationId AND w.ClientId=e.ClientId WHERE e.ClientId=@ClientId",
            new { batch.ClientId, batch.Month }, tx)).ToList();
        await EmployeeRepository.ApplyEmploymentStatusAsync(db, masters, tx);
        var source = await ReadCalculationSourceAsync(db, batch.ClientId, batch.Id, tx);
        var reviews = new List<AppliedEmploymentReview>();
        if (await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='excel_payroll_setup_imports')", transaction: tx))
        {
            var saved = await db.QueryAsync<EmploymentSetupJson>(@"SELECT CAST(request_json AS CHAR) RequestJson,CAST(result_json AS CHAR) ResultJson
FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND batch_id=@Id AND status='Applied'", new { batch.ClientId, batch.Id }, tx);
            foreach (var item in saved)
            {
                try
                {
                    var request = JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(item.RequestJson, JsonOptions);
                    var result = JsonSerializer.Deserialize<ExcelPayrollSetupResult>(item.ResultJson, JsonOptions);
                    if (request is not null && result is not null) reviews.Add(new(request, result));
                }
                catch (JsonException) { /* Malformed/legacy receipts never establish a reviewed link. */ }
            }
        }
        AnnotateEmployment(batch, source, masters, reviews);
    }

    // Response-only annotation. The source codes, captured identifiers, masters and receipts stay immutable.
    internal static void AnnotateEmployment(ExcelPayslipBatch batch, ExcelPayslipCalculationSource? source,
        IReadOnlyList<EmploymentMaster> allMasters, IReadOnlyList<AppliedEmploymentReview> reviews)
    {
        foreach (var row in batch.Rows) row.Employment = null;
        var sourceHash = EmploymentSourceHash(batch, source);
        var masters = allMasters.Where(m => m.ClientId == batch.ClientId).ToList();
        var identities = masters.Select(m => new ExcelPayrollSetupRepository.Identity {
            Id=m.Id, ClientId=m.ClientId, EmployeeCode=m.EmployeeCode, Name=m.FirstName+" "+m.LastName,
            Pan=m.Pan, Aadhaar=m.Aadhaar, Uan=m.Uan, WorkLocation=m.WorkLocation, EligibleForMonth=m.Eligible, IsActive=m.IsActive
        }).ToList();
        var reviewedColumns=source is null ? null : ExcelPayrollSetupRepository.ReviewedIdentityColumns(batch,source,reviews.Select(r=>(r.Request,r.Result)));
        foreach (var row in batch.Rows)
        {
            // Exactly the same captured-column semantics as setup (including UAN-E versus UAN-AB).
            var information = ExcelPayrollSetupRepository.SourceInformation(row, source, reviewedUanColumn:ExcelPayrollSetupRepository.ReviewedColumnForRow(reviewedColumns,row.Id));
            var bound = reviews.Where(r => ReviewedEmploymentBinding(batch, row, sourceHash, r)).ToList();
            var ids = bound.SelectMany(r => r.Result.TransferRows.Where(v => v.RowId == row.Id).Select(v => v.EmployeeId)).Distinct().ToArray();
            var issues = new List<ExcelPayrollSetupIssue>();
            var selected = ExcelPayrollSetupRepository.MatchIdentity(row, information, identities,
                ids.Length == 1 ? new() { RowId=row.Id, MatchedEmployeeId=ids[0] } : null, issues);
            if (selected is null) continue;
            var master = masters.Single(m => m.Id == selected.Id);
            string Info(params string[] keys) => keys.Select(ExcelPayrollSetupRepository.Normalize)
                .Where(information.ContainsKey).Select(k => information[k]).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
            var pan = Info("PAN","PAN Number","PAN No");
            var aadhaar = Info("Aadhaar","Aadhar","Aadhaar Number");
            var uan = Info("UAN","UAN Number","UAN No");
            bool UniqueStrong(string value, string pattern, Func<EmploymentMaster,string> read) =>
                Regex.IsMatch(ExcelPayrollSetupRepository.Normalize(value), pattern)
                && masters.Count(m => ExcelPayrollSetupRepository.Normalize(read(m)) == ExcelPayrollSetupRepository.Normalize(value)) == 1
                && ExcelPayrollSetupRepository.Normalize(read(master)) == ExcelPayrollSetupRepository.Normalize(value);
            var anchored = masters.Count(m => m.EmployeeCode.Equals(row.EmployeeCode, StringComparison.OrdinalIgnoreCase)) == 1
                || UniqueStrong(pan,"^[A-Z]{5}[0-9]{4}[A-Z]$",m => m.Pan)
                || UniqueStrong(aadhaar,"^[2-9][0-9]{11}$",m => m.Aadhaar)
                || UniqueStrong(uan,"^[0-9]{12}$",m => m.Uan);
            var location = Info("Work Location","Location","Place of Posting","Posting","Location Name (Tehsil/Sub Tehsil)");
            var district = Info("District");
            var exactLocation = location.Length == 0 || ExcelPayrollSetupRepository.Normalize(location) == ExcelPayrollSetupRepository.Normalize(master.WorkLocation);
            var reviewedLocation = ids.Length == 1 && ids[0] == master.Id && bound.Any(r => ReviewedLocation(r.Request.TemplateScope, master))
                && (district.Length == 0 || ExcelPayrollSetupRepository.Normalize(district) == ExcelPayrollSetupRepository.Normalize(master.LocationCity));
            var review = issues.Count > 0 || !anchored || ids.Length > 1 || !exactLocation && !reviewedLocation
                || batch.Rows.Count(r => r.Id == row.Id || r.SourceRow == row.SourceRow) != 1
                || batch.Rows.Count(r => r.EmployeeCode.Equals(row.EmployeeCode, StringComparison.OrdinalIgnoreCase)) != 1;
            row.Employment = new(review ? null : master.Id, master.EmployeeCode, master.EmploymentStatus, master.LastWorkingDate,
                !master.Eligible, review);
        }
        foreach (var group in batch.Rows.Where(r => r.Employment?.EmployeeId is not null).GroupBy(r => r.Employment!.EmployeeId).Where(g => g.Count() > 1))
            foreach (var row in group) row.Employment = row.Employment! with { EmployeeId=null, IdentityReviewRequired=true };
    }

    internal static string EmploymentSourceHash(ExcelPayslipBatch batch, ExcelPayslipCalculationSource? source) =>
        ExcelPayrollSetupRepository.Hash(JsonSerializer.Serialize(new { Batch=batch, Source=source }, JsonOptions));

    private static bool ReviewedEmploymentBinding(ExcelPayslipBatch batch, ExcelPayslipRow row, string hash, AppliedEmploymentReview review)
    {
        var r = review.Request; var result = review.Result; var transfer = result.Transfer;
        if (r.BatchId != batch.Id || r.TransferReviewVersion != 1 || r.RowIds.Count(id => id == row.Id) != 1
            || transfer is not { Version:1 } || transfer.SourceBatchId != batch.Id || transfer.SourceMonth != batch.Month || transfer.SourceHash != hash
            || result.KeptRows.Any(k => k.RowId == row.Id)) return false;
        var rows = result.TransferRows.Where(v => v.RowId == row.Id).ToArray();
        var decisions = r.EmployeeDecisions.Where(d => d.RowId == row.Id).ToArray();
        return rows.Length == 1 && result.EmployeeIds.Count(id => id == rows[0].EmployeeId) == 1
            && decisions.Length == 1 && decisions[0].MatchedEmployeeId == rows[0].EmployeeId;
    }

    private static bool ReviewedLocation(ExcelPayrollTemplateScope? scope, EmploymentMaster master) =>
        scope is { Reviewed:true } && (scope.LocationMode == "AllClientLocations"
            || scope.LocationMode == "Listed" && scope.WorkLocationIds.Contains(master.WorkLocationId));

    internal sealed record AppliedEmploymentReview(ExcelPayrollSetupRequest Request, ExcelPayrollSetupResult Result);
    internal sealed class EmploymentMaster : Employee
    {
        public string Pan { get; set; } = "";
        public string Aadhaar { get; set; } = "";
        public string Uan { get; set; } = "";
        public string WorkLocation { get; set; } = "";
        public string LocationCity { get; set; } = "";
        public bool Eligible { get; set; }
    }
    private sealed class EmploymentSetupJson
    {
        public string RequestJson { get; set; } = "{}";
        public string ResultJson { get; set; } = "{}";
    }
}
