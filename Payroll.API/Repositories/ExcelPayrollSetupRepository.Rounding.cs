using System.Globalization;
using System.Text.Json;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    // Same 15-significant-digit representation boundary as formatExcelPayslipAmount;
    // this is not a money epsilon and is never used to change a financial result.
    internal static decimal SourceDisplayPrecision(decimal value) => decimal.Parse(
        ((double)value).ToString("G15", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
    internal static decimal SourceCurrencyDisplay(decimal value) => decimal.Round(SourceDisplayPrecision(value), 2, MidpointRounding.AwayFromZero);

    internal static ExcelPayrollTransferAmountReview ClassifyTransferAmounts(Plan plan, PlannedRow row,
        ExcelPayrollSetupRequest request, string sourceHash, IReadOnlyList<ExcelPayrollTransferFormula> formulas)
    {
        var source = plan.Batch.Rows.Single(r => r.Id == row.RowId);
        var preview = row.Preview;
        var result = new ExcelPayrollTransferAmountReview
        {
            RowId = row.RowId, SourceRow = row.SourceRow, SourceNetRaw = source.NetPay,
            SourceNetDisplay = SourceCurrencyDisplay(source.NetPay), CoreNet = preview.Net,
            RawNetDelta = preview.Net - source.NetPay, DisplayNetDelta = preview.Net - SourceCurrencyDisplay(source.NetPay)
        };
        ExcelPayrollTransferAmountReview Stop(string status, string reason) { result.Status = status; result.Reason = reason; return result; }
        if (TransferAmountsMatch(preview, source, row.Components.Count))
            return Stop("Exact at raw precision", "All components, employer contributions and totals meet the existing raw precision. Identity, source formulas, attendance, applicability and policy checks still apply.");
        if (row.Components.Count == 0 || preview.Lines.Count != row.Components.Count
            || preview.Lines.Select(l => l.Code.ToUpperInvariant()).Distinct().Count() != preview.Lines.Count
            || row.Components.Select(c => c.Definition.Code.ToUpperInvariant()).Distinct().Count() != row.Components.Count)
            return Stop("Unverified", "Complete, uniquely coded component coverage is required before explaining rounding. Refresh the saved-batch comparison.");
        if (formulas.Count != row.Components.Count || formulas.Any(f => f.Status != "Exact supported translation")
            || formulas.Select(f => f.Code.ToUpperInvariant()).Distinct().Count() != formulas.Count)
            return Stop("Unverified", "Rounding cannot approve an unsupported/unproven source formula or a cached fixed amount. Resolve captured formula translation first; existing structural blockers remain.");

        foreach (var component in row.Components)
        {
            var definition = component.Definition;
            var lines = preview.Lines.Where(l => l.Code.Equals(definition.Code, StringComparison.OrdinalIgnoreCase)).ToList();
            var columns = plan.Source.Columns.Where(c => c.ColumnIndex == definition.ExpectedColumnIndex).ToList();
            var expectedRole = definition.Category switch { "Earning" or "Reimbursement" => "earning", "Deduction" => "deduction", "Benefit" => "employer", _ => "" };
            if (definition.IsPeriodAdjustment || lines.Count != 1 || columns.Count != 1 || expectedRole.Length == 0
                || request.SourceColumnRoles.GetValueOrDefault(columns[0].ColumnIndex, columns[0].Kind) != expectedRole
                || lines[0].Category != definition.Category || !formulas.Any(f => f.RowId == row.RowId && f.Code.Equals(definition.Code, StringComparison.OrdinalIgnoreCase)))
                return Stop("Unverified", "Source output role, component classification or recurring formula coverage is missing/ambiguous. Review the exact earning/deduction/employer mapping; rounding cannot alias components.");
            var line = lines[0];
            if (line.SourceAmount is not decimal amount || line.UnroundedEarned is not decimal before || line.Variance is null)
                return Stop("Unverified", "The current core pre-currency-rounding trace or mapped source amount is unavailable. Refresh against the updated calculation API; legacy snapshots are not a rounding certificate.");
            if (amount < 0 || before < 0 || SourceDisplayPrecision(amount) != SourceDisplayPrecision(before)
                || decimal.Round(before, 2, MidpointRounding.ToEven) != line.CalculatedAmount
                || SourceDisplayPrecision(line.Variance.Value) != SourceDisplayPrecision(line.CalculatedAmount - amount))
                return Stop("Mismatch", $"{definition.Code}: source and core pre-rounding amounts or the actual currency result differ. A small unexplained delta is still a mismatch; review formula, basis, attendance and proration.");
            var sourceCells = plan.Source.Rows.Where(r => r.SourceRow == row.SourceRow).ToList();
            if (sourceCells.Count != 1) return Stop("Unverified", "A unique saved source row is required for rounding evidence.");
            try
            {
                if (SourceDisplayPrecision(ReadNumber(sourceCells[0], definition.ExpectedColumnIndex!.Value)) != SourceDisplayPrecision(amount))
                    return Stop("Mismatch", "The captured source cell and comparison amount differ. Refresh the saved version before reviewing rounding.");
            }
            catch (InvalidOperationException) { return Stop("Unverified", "The captured output is missing/invalid. Missing source data cannot be explained as rounding."); }
            var display = SourceCurrencyDisplay(amount);
            result.Lines.Add(new(definition.Code, definition.Category, amount, display, before, line.CalculatedAmount,
                line.CalculatedAmount - amount, line.CalculatedAmount - display));
        }

        decimal Raw(params string[] categories) => result.Lines.Where(l => categories.Contains(l.Category)).Sum(l => l.SourceRaw);
        decimal Core(params string[] categories) => result.Lines.Where(l => categories.Contains(l.Category)).Sum(l => l.Calculated);
        bool Same(decimal left, decimal right) => SourceDisplayPrecision(left) == SourceDisplayPrecision(right);
        var rawGross = Raw("Earning", "Reimbursement"); var rawDeductions = Raw("Deduction"); var rawBenefits = Raw("Benefit");
        if (source.Earnings.Count != result.Lines.Count(l => l.Category is "Earning" or "Reimbursement")
            || source.Deductions.Count != result.Lines.Count(l => l.Category == "Deduction")
            || source.EmployerContributions.Count != result.Lines.Count(l => l.Category == "Benefit")
            || !Same(source.Earnings.Sum(a => a.Amount), rawGross) || !Same(source.Deductions.Sum(a => a.Amount), rawDeductions)
            || !Same(source.EmployerContributions.Sum(a => a.Amount), rawBenefits)
            || !Same(source.DeclaredGross ?? rawGross, rawGross) || !Same(source.DeclaredDeductions ?? rawDeductions, rawDeductions)
            || !Same(source.NetPay, Math.Max(0, rawGross - rawDeductions)))
            return Stop("Mismatch", "Source totals do not prove the raw-component aggregate stage. Review the source totals/formulas and coverage; a rounded-looking net or a small unexplained delta cannot be approved automatically.");
        if (preview.Gross != Core("Earning", "Reimbursement") || preview.Deductions != Core("Deduction")
            || preview.Benefits != Core("Benefit") || preview.Net != Math.Max(0, Core("Earning", "Reimbursement") - Core("Deduction")))
            return Stop("Mismatch", "The core totals do not equal its actual rounded components. Review the calculation; rounding review cannot force or replace an output.");

        result.Status = "Explained rounding";
        result.Reason = "Every captured formula has an exact supported native translation. Source and core pre-rounding amounts agree at the existing source formatter's 15-significant-digit boundary. Source net is the raw-component aggregate; core totals sum its actual 2-decimal, midpoint-to-even component results. Source display uses the existing 2-decimal half-away formatter. Raw and displayed deltas remain visible; this is calculation evidence, not statutory or saved Employee parity certification.";
        var reviewedRequest = JsonSerializer.SerializeToNode(request, Json)!.AsObject(); reviewedRequest.Remove("roundingReviews");
        result.ProofHash = Hash(JsonSerializer.Serialize(new { result.Version, sourceHash, reviewedRequest, source,
            preview.PayrollDays, preview.PresentDays, preview.PayableDays, preview.Gross, preview.Deductions, preview.Benefits,
            preview.Net, result.Lines, formulas }, Json));
        return result;
    }
}
