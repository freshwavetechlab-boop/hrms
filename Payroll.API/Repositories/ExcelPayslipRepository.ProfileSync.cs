using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;
using System.Text.RegularExpressions;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    private static async Task<List<EmploymentMaster>> ReadProfileMastersAsync(MySqlConnection db, MySqlTransaction tx, int clientId, string month) =>
        (await db.QueryAsync<EmploymentMaster>(@"SELECT e.*,COALESCE(p.PanNumber,'') Pan,
COALESCE(p.AadhaarNumber,'') Aadhaar,COALESCE(p.UanNumber,'') Uan,
COALESCE(w.Name,'') WorkLocation,COALESCE(w.City,'') LocationCity," + EmployeeEmploymentRules.EligibleInMonth("e", "@Month") + @" Eligible
FROM employees e LEFT JOIN employeepersonaldetails p ON p.EmployeeId=e.Id
LEFT JOIN worklocations w ON w.Id=e.WorkLocationId AND w.ClientId=e.ClientId WHERE e.ClientId=@ClientId",
            new { ClientId=clientId, Month=month }, tx)).ToList();

    internal static IReadOnlyList<ExcelPayslipProfileLink> ProfileBindings(ExcelPayslipBatch batch, ExcelPayslipCalculationSource? source,
        IReadOnlyList<EmploymentMaster> allMasters, IReadOnlyList<ExcelPayslipProfileLink>? trustedLinks = null)
    {
        var masters=allMasters.Where(m => m.ClientId == batch.ClientId).ToArray();
        var links=new List<ExcelPayslipProfileLink>();
        foreach(var row in batch.Rows)
        {
            if(batch.Rows.Count(r => r.Id == row.Id) != 1 || batch.Rows.Count(r => r.SourceRow == row.SourceRow) != 1
                || source is not null && source.Rows.Count(r => r.SourceRow == row.SourceRow) > 1) continue;
            var witness=ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,row,source);
            var stored=(trustedLinks ?? batch.ProfileLinks ?? []).Where(l => l.RowId == row.Id).ToArray();
            if(stored.Length > 1) continue;
            if(stored.Length == 1 && stored[0].SourceRow == row.SourceRow && stored[0].SourceIdentityHash == witness
                && masters.Count(m => m.Id == stored[0].EmployeeId) == 1)
            {
                // Current mutable identifiers may intentionally differ after ESS
                // saves. The reviewed source witness + same-client employee ID
                // is the authority; repeated source parts stay separate rows.
                links.Add(stored[0]);
                continue;
            }
            var byCode=masters.Where(m => m.EmployeeCode.Trim().Equals(row.EmployeeCode.Trim(),StringComparison.OrdinalIgnoreCase)).ToArray();
            if(string.IsNullOrWhiteSpace(row.EmployeeCode) || byCode.Length != 1
                || batch.Rows.Count(r => r.EmployeeCode.Trim().Equals(row.EmployeeCode.Trim(),StringComparison.OrdinalIgnoreCase)) != 1) continue;
            var master=byCode[0];
            var evidence=row.Information.Select(i => (Key:ExcelPayslipMasterSynchronization.ProfileIdentityKey(i.Label), Value:i.Value.Trim().ToUpperInvariant()))
                .Where(i => i.Key is "pan" or "aadhaar" or "uan").GroupBy(i => i.Key)
                .ToDictionary(g => g.Key,g => g.Select(i => i.Value).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToArray());
            if(evidence.Values.Any(values => values.Length > 1)) continue;
            var anchored=false; var conflict=false;
            foreach(var (key,pattern,read) in new (string,string,Func<EmploymentMaster,string>)[] {
                ("pan",@"\A[A-Z]{5}[0-9]{4}[A-Z]\z",m=>m.Pan),
                ("aadhaar",@"\A[2-9][0-9]{11}\z",m=>m.Aadhaar),
                ("uan",@"\A[0-9]{12}\z",m=>m.Uan) })
            {
                var value=evidence.GetValueOrDefault(key)?.SingleOrDefault();
                if(value is null || !Regex.IsMatch(value,pattern) || value.All(c => c == value[0])) continue;
                string Identity(EmploymentMaster m) => read(m).Trim().ToUpperInvariant();
                var owners=masters.Where(m => Identity(m) == value).ToArray();
                if(owners.Length > 1 || owners.Any(m => m.Id != master.Id)
                    || Identity(master).Length > 0 && Identity(master) != value) { conflict=true;break; }
                if(owners.Length == 1 && owners[0].Id == master.Id) anchored=true;
            }
            if(!conflict && anchored) links.Add(new(row.Id,row.SourceRow,master.Id,witness));
        }
        // Independently auto-matched rows never share a Master; explicitly
        // reviewed durable source-part associations are allowed to do so.
        var storedLinks=(trustedLinks ?? batch.ProfileLinks ?? []).ToHashSet();
        return links.Where(l => links.Count(other => other.EmployeeId == l.EmployeeId) == 1 || storedLinks.Contains(l)).ToArray();
    }

    internal static void AnnotateCurrentProfiles(ExcelPayslipBatch batch, ExcelPayslipCalculationSource? source,
        IReadOnlyList<EmploymentMaster> masters)
    {
        foreach(var link in ProfileBindings(batch,source,masters))
        {
            var row=batch.Rows.Single(r => r.Id == link.RowId);
            var master=masters.Single(m => m.ClientId == batch.ClientId && m.Id == link.EmployeeId);
            row.Employment=new(master.Id,master.EmployeeCode,master.EmploymentStatus,master.LastWorkingDate,!master.Eligible,false);
            row.MasterDetails=ExcelPayslipMasterSynchronization.Details(master,master.WorkLocation,true) with { ProfileOnly=true };
        }
    }

    private static async Task RetainProfileLinksAsync(MySqlConnection db, MySqlTransaction tx, ExcelPayslipBatch batch,
        ExcelPayslipCalculationSource? source, ExcelPayslipBatch? existing)
    {
        var masters=await ReadProfileMastersAsync(db,tx,batch.ClientId,batch.Month);
        // Only links loaded from the saved same-client batch are trusted. The
        // submitted ProfileLinks collection was discarded before validation.
        var links=SaveProfileBindings(batch,source,masters,existing);
        batch.ProfileLinks=links.Count > 0 ? links.ToList() : null;
    }

    internal static IReadOnlyList<ExcelPayslipProfileLink> SaveProfileBindings(ExcelPayslipBatch batch, ExcelPayslipCalculationSource? source,
        IReadOnlyList<EmploymentMaster> masters, ExcelPayslipBatch? existing) => ProfileBindings(batch,source,masters,
            existing is not null && existing.Id == batch.Id && existing.ClientId == batch.ClientId && existing.Month == batch.Month
                ? existing.ProfileLinks ?? [] : []);
}
