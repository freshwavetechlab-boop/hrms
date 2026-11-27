using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    private sealed class SourceTdsReceiptKey { public string Id { get; set; } = ""; public DateTime Updated { get; set; } }
    private sealed class SourceTdsReceipt { public string Id { get; set; } = ""; public string RequestJson { get; set; } = ""; public string ResultJson { get; set; } = ""; }
    private sealed class SourceTdsSnapshot { public string BatchJson { get; set; } = ""; public string SourceJson { get; set; } = ""; }

    private async Task<ReviewedSourceTdsEvidence?> ReadSourceTdsAsync(MySqlConnection db, MySqlTransaction tx,
        PayRunSourceEmployee employee, string period, string setupJson)
    {
        if (employee.LocationParts is not null || string.IsNullOrWhiteSpace(employee.SalaryStructureId)) return null;
        var setup = ReadPayrollSetup(setupJson);
        var currentRule = SourceTdsRule(setup, employee.SalaryStructureId);
        if (!string.IsNullOrEmpty(employee.SalaryInputTemplateId) && employee.SalaryInputTemplateId != employee.SalaryStructureId)
        {
            // The receipt identifies the original period assignment. Verify it there before
            // using its withholding as an input on the employee's newly selected template.
            var source = employee.HistoricalContextClone();
            source.SalaryStructureId = employee.SalaryInputTemplateId;
            var sourceRule = SourceTdsRule(setup, source.SalaryStructureId, includeInactiveTemplate: true);
            var reviewed = await ReadSourceTdsForRuleAsync(db, tx, source, period, sourceRule);
            if (reviewed is not null)
            {
                if (currentRule is not { Category: "Deduction", ProRata: false } || currentRule.Id != reviewed.ComponentId
                    || currentRule.Code != reviewed.ComponentCode || !IsEditableTemplateInput(currentRule.CalculationType, currentRule.Formula)
                    || currentRule.CalculationType == "Daily Rate")
                    throw new InvalidOperationException($"{employee.EmployeeCode}: the selected salary template needs a matching employee TDS input to apply {period}'s reviewed withholding.");
                return reviewed;
            }
        }
        return await ReadSourceTdsForRuleAsync(db, tx, employee, period, currentRule);
    }

    private static ReviewedSourceTdsRule? SourceTdsRule(PayrollSetupData setup, string templateId, bool includeInactiveTemplate = false)
    {
        var structure = setup.Structures.SingleOrDefault(value => value.Id == templateId && (value.Active || includeInactiveTemplate));
        if (structure is null) return null;
        var rules = structure.Lines.Select(line => (line, component: setup.Components.SingleOrDefault(value => value.Id == line.ComponentId)))
            .Where(value => value.component is not null && ReviewedSourceTds.IsTds(value.component.Code, value.component.StatutoryType)).ToArray();
        if (rules.Length != 1) return null;
        var (line, component) = rules[0];
        return new ReviewedSourceTdsRule(component!.Id, component.Code, component.Category, component.StatutoryType,
            NormalizeCalculationType(FirstText(line.CalculationType, component.CalculationType)),
            NullableBool(line.ProRataOverride) ?? component.ProRata, FirstText(line.Formula, component.Formula));
    }

    private static async Task<ReviewedSourceTdsEvidence?> ReadSourceTdsForRuleAsync(MySqlConnection db, MySqlTransaction tx,
        PayRunSourceEmployee employee, string period, ReviewedSourceTdsRule? rule)
    {
        if (rule is null) return null;
        var inputs = employee.SalaryComponents.Count > 0 ? employee.SalaryComponents : ParseSalaryComponentMap(employee.SalaryJson);
        if (rule.CalculationType != "Fixed Amount" || rule.ProRata || !string.IsNullOrWhiteSpace(rule.Formula)
            || !inputs.TryGetValue(rule.Id, out var input) || !await PtTableAsync(db, tx, "excel_payroll_setup_imports")) return null;

        // No JSON-bearing ORDER BY/filesort. Bound matching scalar keys first,
        // fetch those payloads separately and order the small key list in memory.
        var keys = (await db.QueryAsync<SourceTdsReceiptKey>(@"SELECT id Id,updated_at Updated
FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND status='Applied'
AND JSON_UNQUOTE(JSON_EXTRACT(result_json,'$.transfer.sourceMonth'))=@Period
AND JSON_CONTAINS(JSON_EXTRACT(result_json,'$.employeeIds'),CAST(@EmployeeId AS JSON)) LIMIT 51",
            new { employee.ClientId, EmployeeId = employee.Id, Period = period }, tx)).ToArray();
        if (keys.Length == 0) return null;
        if (keys.Length > 50) throw new InvalidOperationException("This employee has too many source-period salary receipts. Review the current assignment before selecting TDS.");
        var saved = (await db.QueryAsync<SourceTdsReceipt>(@"SELECT id Id,CAST(request_json AS CHAR) RequestJson,CAST(result_json AS CHAR) ResultJson
FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND status='Applied' AND id IN @Ids",
            new { employee.ClientId, Ids = keys.Select(key => key.Id).ToArray() }, tx)).ToDictionary(value => value.Id);
        foreach (var key in keys.OrderByDescending(value => value.Updated).ThenByDescending(value => value.Id, StringComparer.Ordinal))
        {
            var record = saved[key.Id];
            var receipt = JsonSerializer.Deserialize<ExcelPayrollSetupResult>(record.ResultJson, ExcelPayslipRepository.JsonOptions)
                ?? throw new InvalidOperationException("A source salary receipt cannot be read for TDS review.");
            var assignments = receipt.Transfer?.Templates.Where(template => template.TemplateId == employee.SalaryStructureId
                && receipt.TransferRows.Any(row => row.EmployeeId == employee.Id && template.RowIds.Contains(row.RowId))).ToArray() ?? [];
            if (assignments.Length == 0) continue;
            // A newer assignment supersedes older source receipts, even when it
            // deliberately chooses dated policy or an open-ended automatic rule.
            var request = JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(record.RequestJson, ExcelPayslipRepository.JsonOptions)
                ?? throw new InvalidOperationException("A source salary request cannot be read for TDS review.");
            if (!ReviewedSourceTds.ClosedSourceMonth(request, period)) return null;
            await ExcelPayslipRepository.RequireCurrentConsolidationSourceAsync(db, tx, employee.ClientId, request.BatchId);
            var raw = await db.QuerySingleOrDefaultAsync<SourceTdsSnapshot>(@"SELECT CAST(b.batch_json AS CHAR) BatchJson,CAST(s.source_json AS CHAR) SourceJson
FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id
WHERE b.client_id=@ClientId AND b.id=@BatchId", new { employee.ClientId, request.BatchId }, tx)
                ?? throw new InvalidOperationException("The reviewed source-period TDS workbook snapshot is unavailable.");
            var batch = JsonSerializer.Deserialize<ExcelPayslipBatch>(raw.BatchJson, ExcelPayslipRepository.JsonOptions)!;
            var source = JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(raw.SourceJson, ExcelPayslipRepository.JsonOptions)!;
            return ReviewedSourceTds.Resolve(record.Id, request, receipt, batch, source, employee, period, rule, input);
        }
        return null;
    }

    private static async Task<string> RecordSourceTdsAsync(MySqlConnection db, MySqlTransaction tx,
        PayRunEmployee row, string projectedSnapshot, ReviewedSourceTdsEvidence evidence, bool persist)
    {
        var snapshot = JsonNode.Parse(projectedSnapshot)?.AsObject()
            ?? throw new InvalidOperationException("The annual tax projection snapshot is unavailable.");
        // The annual liability, regime, declarations, rules and projected monthly
        // amount remain intact. Existing monthly_tds reconciliation reads the
        // actual selected withholding, including an explicitly reviewed zero.
        snapshot["payrollWithholding"] = JsonSerializer.SerializeToNode(evidence, ExcelPayslipRepository.JsonOptions);
        var json = snapshot.ToJsonString(ExcelPayslipRepository.JsonOptions);
        if (persist)
        {
            if (row.PayRunId <= 0) throw new InvalidOperationException("Saved source TDS requires a real current payroll draft.");
            var count = await db.ExecuteAsync(@"UPDATE tax_computation_snapshots SET monthly_tds=@Amount,snapshot_json=CAST(@Json AS JSON)
WHERE pay_run_id=@PayRunId AND employee_id=@EmployeeId AND client_id=@ClientId AND pay_period=@PayPeriod",
                new { row.PayRunId, evidence.EmployeeId, evidence.ClientId, evidence.PayPeriod, evidence.Amount, Json = json }, tx);
            if (count != 1) throw new InvalidOperationException("The exact current payroll tax snapshot could not record reviewed source withholding.");
        }
        return json;
    }

    private static PayRunEmployee WithSourceTdsEvidence(PayRunEmployee row, TdsProjectionResult tax)
    {
        if (tax.ReviewedSource is null) return row;
        var lines = JsonNode.Parse(row.DetailsJson)?.AsArray()
            ?? throw new InvalidOperationException("The native payroll detail lines are unavailable for source TDS evidence.");
        lines.Add(new JsonObject
        {
            ["Id"] = "REVIEWED_TDS_INPUT", ["Name"] = "Reviewed source-period tax withholding; annual tax projection retained",
            ["Category"] = "Summary", ["monthlyAmount"] = 0, ["amount"] = 0,
            ["sourceTds"] = JsonSerializer.SerializeToNode(tax.ReviewedSource, ExcelPayslipRepository.JsonOptions)
        });
        row.DetailsJson = lines.ToJsonString(ExcelPayslipRepository.JsonOptions);
        return row;
    }
}
