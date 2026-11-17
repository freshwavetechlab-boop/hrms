using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Payroll.API.Models;

namespace Payroll.API.Services;

internal sealed record ReviewedSourceTdsRule(string Id, string Code, string Category, string StatutoryType,
    string CalculationType, bool ProRata, string Formula);

internal sealed record ReviewedSourceTdsEvidence(int ClientId, int EmployeeId, string PayPeriod,
    string ImportId, string SourceBatchId, string SourceRowId, int SourceRow, string SourceHash,
    string ComponentId, string ComponentCode, decimal Amount, string InputKind, decimal ProjectedMonthlyTds = 0);

// A closed, reviewed source-period withholding is a cash deduction, not a
// declaration, tax exemption or replacement for the annual tax computation.
internal static class ReviewedSourceTds
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static bool IsTds(string code, string statutoryType) => code.Equals("TDS", StringComparison.OrdinalIgnoreCase)
        || statutoryType.Equals("TDS", StringComparison.OrdinalIgnoreCase);

    internal static bool ClosedSourceMonth(ExcelPayrollSetupRequest request, string period)
    {
        if (!DateOnly.TryParseExact(period + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)) return false;
        return request.TransferReviewVersion == 1 && request.TemplateScopeReviewVersion == 1
            && request.ReviewedFixedInputs && request.ReviewedPolicyScope
            && request.EffectiveFrom == start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            && request.TemplateScope is { Reviewed: true, OpenEnded: false, PolicyBasis: "SavedSourceRules", PayableMeaning: "AlreadyPayable" } scope
            && scope.EffectiveTo == start.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    internal static ReviewedSourceTdsEvidence? Resolve(string importId, ExcelPayrollSetupRequest request,
        ExcelPayrollSetupResult receipt, ExcelPayslipBatch batch, ExcelPayslipCalculationSource source,
        Employee employee, string period, ReviewedSourceTdsRule rule, decimal currentInput)
    {
        if (!ClosedSourceMonth(request, period) || receipt.Transfer is not { Version: 1 } transfer
            || transfer.SourceMonth != period || batch.Month != period || batch.ClientId != employee.ClientId
            || batch.Id != request.BatchId || transfer.SourceBatchId != batch.Id
            || request.LocationAllocations is not null || employee.SalaryStructureId.Length == 0) return null;
        var scope = request.TemplateScope!;
        if (scope.LocationMode != "AllClientLocations" && (scope.LocationMode != "Listed" || !scope.WorkLocationIds.Contains(employee.WorkLocationId))) return null;
        if (scope.CategoryKind != "AllClientCategories") return null;
        var links = receipt.MasterLinks.Where(link => link.EmployeeId == employee.Id).ToArray();
        var rows = receipt.TransferRows.Where(row => row.EmployeeId == employee.Id).ToArray();
        if (links.Length != 1 || rows.Length != 1 || links[0].RowId != rows[0].RowId
            || receipt.EmployeeIds.Count(id => id == employee.Id) != 1) return null;
        var row = rows[0];
        var templates = transfer.Templates.Where(template => template.TemplateId == employee.SalaryStructureId && template.RowIds.Contains(row.RowId)).ToArray();
        if (templates.Length != 1 || templates[0].Applicability is not { } applicability
            || applicability.ClientId != employee.ClientId || applicability.SourceBatchId != batch.Id || applicability.SourceMonth != period
            || applicability.TemplateId != employee.SalaryStructureId || applicability.EffectiveFrom != request.EffectiveFrom
            || applicability.RulesSignature.Length != 64 || !applicability.RulesSignature.All(Uri.IsHexDigit)
            || applicability.PolicyKey != "SOURCE_RULES:" + applicability.RulesSignature
            || JsonSerializer.Serialize(applicability.Scope, Json) != JsonSerializer.Serialize(scope, Json)) return null;
        var selected = batch.Rows.Where(item => item.Id == row.RowId && item.SourceRow == links[0].SourceRow).ToArray();
        if (selected.Length != 1 || !selected[0].EmployeeCode.Trim().Equals(employee.EmployeeCode.Trim(), StringComparison.OrdinalIgnoreCase)
            || batch.Rows.Count(item => item.EmployeeCode.Trim().Equals(employee.EmployeeCode.Trim(), StringComparison.OrdinalIgnoreCase)) != 1
            || !request.RowIds.Contains(row.RowId)) return null;
        var deductions = request.Components.Where(item => IsTds(item.Code, item.StatutoryType)).ToArray();
        if (deductions.Length != 1) return null;
        var component = deductions[0];
        if (component.Category != "Deduction" || component.CalculationType != "Fixed Amount" || component.ProRata
            || component.IsPeriodAdjustment || component.ExemptWhenSourceZero || !string.IsNullOrWhiteSpace(component.Formula)
            || component.SourceColumnIndex is not >= 0 || component.ExpectedColumnIndex != component.SourceColumnIndex) return null;
        var masters = transfer.Components.Where(item => item.Code == component.Code && item.Category == "Deduction").ToArray();
        if (masters.Length != 1 || masters[0].MasterId != rule.Id || component.ExistingComponentId is not null && component.ExistingComponentId != rule.Id
            || rule.Code != component.Code || rule.Category != "Deduction" || !IsTds(rule.Code, rule.StatutoryType)
            || rule.CalculationType != "Fixed Amount" || rule.ProRata || !string.IsNullOrWhiteSpace(rule.Formula)) return null;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Batch = batch, Source = source }, Json)))).ToLowerInvariant();
        if (hash != transfer.SourceHash) throw Changed();
        var capturedRows = source.Rows.Where(item => item.SourceRow == selected[0].SourceRow).ToArray();
        var column = component.SourceColumnIndex.Value;
        if (capturedRows.Length != 1 || column >= capturedRows[0].Cells.Count) throw Changed();
        var cell = capturedRows[0].Cells[column];
        if (cell is null || cell.Error is not null || cell.CalculationError is not null || cell.MissingCachedValue
            || !string.IsNullOrWhiteSpace(cell.FormulaText)) throw Changed();
        var blank = string.IsNullOrWhiteSpace(cell.Value);
        decimal amount;
        if (blank)
        {
            if (!component.BlankInputAsZero || cell.DataType != "Blank") throw Changed();
            amount = 0;
        }
        else if (cell.DataType != "Number" || !decimal.TryParse(cell.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out amount)) throw Changed();
        if (amount < 0 || amount > 1_000_000_000m || decimal.Round(amount, 4) != amount || currentInput != amount) throw Changed();
        var lines = row.Lines.Where(line => line.Code == component.Code).ToArray();
        var proofs = transfer.Formulas.Where(proof => proof.RowId == row.RowId && proof.Code == component.Code && proof.SourceRow == selected[0].SourceRow).ToArray();
        var cellHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            selected[0].SourceRow, column, cell, source.WorkbookKey, source.WorkbookFiles, source.ExternalMappings
        }, Json)))).ToLowerInvariant();
        if (lines.Length != 1 || lines[0].SourceAmount != amount || lines[0].CalculatedAmount != amount
            || proofs.Length != 1 || proofs[0].SourceColumn != column || proofs[0].Status != "Reviewed fixed source"
            || proofs[0].SourceHash != cellHash) throw Changed();
        return new(employee.ClientId, employee.Id, period, importId, batch.Id, row.RowId, selected[0].SourceRow,
            hash, rule.Id, rule.Code, amount, blank ? "ReviewedBlankZero" : "ReviewedLiteral");
    }

    private static InvalidOperationException Changed() => new("Reviewed source-period TDS evidence or its saved employee input changed. Review that source salary configuration before calculating payroll; no tax amount was substituted.");
}
