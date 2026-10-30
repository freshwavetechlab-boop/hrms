using System.Globalization;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    private static async Task ValidateSourcePayableOverrideAsync(MySqlConnection db, MySqlTransaction tx, int clientId,
        string month, PayRunAttendance row)
    {
        if (row.SourceType != SourcePayableOverride.SourceType) return;
        if (decimal.Truncate(row.WorkingDays) != row.WorkingDays) throw new InvalidOperationException("Excel payable override needs an integer divisor.");
        var evidence = SourcePayableOverride.Read(row.Remarks, clientId, row.EmployeeId, month, (int)row.WorkingDays, row.PayableDays, row.PresentDays);
        var raw = await db.QuerySingleOrDefaultAsync<PayableOverrideReceiptJson>(@"SELECT CAST(request_json AS CHAR) RequestJson, CAST(result_json AS CHAR) ResultJson
FROM excel_payroll_setup_imports WHERE id=@ImportId AND client_id=@ClientId AND batch_id=@BatchId AND status='Applied'",
            new { evidence.ImportId, ClientId = clientId, evidence.BatchId }, tx);
        if (raw is null) throw new InvalidOperationException("Excel payable override has no applied source transfer receipt.");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(raw.RequestJson, json);
        var result = JsonSerializer.Deserialize<ExcelPayrollSetupResult>(raw.ResultJson, json);
        var receipt = result?.PayableOverrides.SingleOrDefault(r => r.EmployeeId == row.EmployeeId && r.RowId == evidence.RowId);
        if (request is null || !request.OverrideSourcePayableAttendance || request.ImportAttendance || request.LocationAllocations is not null
            || result?.Transfer?.SourceHash != evidence.SourceHash || receipt is null || receipt.Month != month
            || receipt.PayableDays != row.PayableDays || receipt.PayrollDays != row.WorkingDays || receipt.PreviousPresentDays != row.PresentDays
            || result.TransferRows.Count(r => r.EmployeeId == row.EmployeeId && r.RowId == evidence.RowId && r.PayableDays == row.PayableDays) != 1)
            throw new InvalidOperationException("Excel payable override differs from its applied source receipt.");
        SourcePayableAttendance.EnsureIndependent(request.Components.SelectMany(c => new[] { c.Formula, c.BaseComponent }.Concat(c.RowFormulas.Values)));
        if (await ExcelPayrollSetupRepository.CurrentSourceHashAsync(db, tx, clientId, evidence.BatchId) != evidence.SourceHash)
            throw new InvalidOperationException("The saved Excel source changed after payable override. Review its current source transfer before payroll.");
        row.InputSource = "Reviewed Excel payable override; native physical presence retained separately";
        row.PolicySnapshot = new(1, DateOnly.ParseExact(evidence.CycleStart, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateOnly.ParseExact(evidence.CycleEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            $"Excel payable override; batch {evidence.BatchId}; import {evidence.ImportId}; source {evidence.SourceHash}; physical presence not certified by Excel", [], []);
    }
    private sealed class PayableOverrideReceiptJson
    {
        public string RequestJson { get; set; } = "";
        public string ResultJson { get; set; } = "";
    }
    private static void ValidateSourcePayableTemplate(PayRunSourceEmployee employee, PayrollSetupData setup)
    {
        if (employee.AttendancePolicySnapshot?.Source.StartsWith("Excel payable override;", StringComparison.Ordinal) != true) return;
        var template = setup.Structures.SingleOrDefault(t => t.Active && t.Id == employee.SalaryStructureId
            && t.ClientId.Split([';', ',', '|']).Any(value => value.Split(':')[0].Trim() == employee.ClientId.ToString(CultureInfo.InvariantCulture)))
            ?? throw new InvalidOperationException("Excel payable override requires an explicitly assigned active salary template.");
        var expressions = template.Lines.SelectMany(line => {
            var component = setup.Components.SingleOrDefault(c => c.Id == line.ComponentId && c.Active)
                ?? throw new InvalidOperationException("Excel payable override references an unavailable component.");
            return new[] { ComponentFormula(line, component), FirstText(line.BaseComponent, component.BaseComponent) };
        });
        SourcePayableAttendance.EnsureIndependent(expressions, hasSplitPf: employee.PfPolicyPeriod?.Versions.Count > 1,
            hasLocationParts: employee.LocationParts is not null);
    }
}
