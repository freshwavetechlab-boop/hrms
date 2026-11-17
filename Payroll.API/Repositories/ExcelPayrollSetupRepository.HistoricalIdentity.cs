using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal sealed record HistoricalVerifiedSourceIdentity(HistoricalSourceIdentityEvidence Evidence,
        bool ReviewedNameChange);
    internal sealed record HistoricalBankIdentity(int EmployeeId, int ClientId, string BankAccount);

    internal static void ValidateHistoricalSourceIdentityReview(HistoricalPeriodSalaryRequest request)
    {
        if (request.SourceIdentityReview is not { } review) return;
        if (review.Version != 1 || !review.Reviewed || !Regex.IsMatch(review.SourceHash ?? "", @"\A[a-f0-9]{64}\z")
            || string.IsNullOrWhiteSpace(review.Reason) || review.Reason.Trim().Length is < 10 or > 500
            || review.Bindings is null || review.Bindings.Count is < 1 or > 100
            || review.Bindings.Any(binding => binding is null || binding.EmployeeId <= 0
                || binding.BankColumnIndex is < 0 or > 1499 || string.IsNullOrWhiteSpace(binding.RowId)
                || binding.RowId.Length > 100 || string.IsNullOrWhiteSpace(binding.ReviewedSourceName)
                || string.IsNullOrWhiteSpace(binding.ReviewedMasterName) || binding.ReviewedSourceName.Length > 250
                || binding.ReviewedMasterName.Length > 250
                || !request.Assignments.Any(assignment => assignment.EmployeeId == binding.EmployeeId && assignment.RowId == binding.RowId))
            || review.Bindings.Select(binding => binding.RowId).Distinct(StringComparer.Ordinal).Count() != review.Bindings.Count
            || review.Bindings.Select(binding => binding.EmployeeId).Distinct().Count() != review.Bindings.Count)
            throw new InvalidOperationException("Review a bounded version-one exact saved-source identity binding and reason for each selected existing employee; guessed names, code changes and duplicate bindings are not accepted.");
    }

    private static async Task<IReadOnlyDictionary<string, HistoricalVerifiedSourceIdentity>> ReviewHistoricalSourceIdentitiesAsync(
        MySqlConnection db, MySqlTransaction tx, HistoricalPeriodSalaryRequest request, ExcelPayslipBatch batch,
        ExcelPayslipCalculationSource source, IReadOnlyList<Identity> identities, string sourceHash, bool lockState)
    {
        ValidateHistoricalSourceIdentityReview(request);
        if (request.SourceIdentityReview is not { } review)
            return new Dictionary<string, HistoricalVerifiedSourceIdentity>(StringComparer.Ordinal);
        if (sourceHash != review.SourceHash)
            throw new InvalidOperationException("The reviewed source identity snapshot changed. Refresh the exact saved source before reviewing ownership.");

        // Lock the complete client bank ownership range only in this opt-in
        // transaction. A second employee acquiring the account must invalidate
        // the review, even if that employee is outside the salary selection.
        if (lockState)
        {
            await db.QueryAsync<int>("SELECT Id FROM employees WHERE ClientId=@ClientId ORDER BY Id FOR UPDATE", new { ClientId = batch.ClientId }, tx);
            await db.QueryAsync<int>(@"SELECT b.EmployeeId FROM employeepaymentdetails b JOIN employees e ON e.Id=b.EmployeeId
WHERE e.ClientId=@ClientId ORDER BY b.EmployeeId FOR UPDATE", new { ClientId = batch.ClientId }, tx);
        }
        var masters = await EmployeeRepository.ReadExcelPayrollIdentitySnapshotAsync(db, tx, batch.ClientId);
        var banks = masters.Select(employee => new HistoricalBankIdentity(employee.Id, employee.ClientId,
            employee.PaymentDetails.BankAccountNo)).ToArray();
        var output = new Dictionary<string, HistoricalVerifiedSourceIdentity>(StringComparer.Ordinal);
        foreach (var binding in review.Bindings)
        {
            var row = batch.Rows.SingleOrDefault(row => row.Id == binding.RowId)
                ?? throw new InvalidOperationException("The reviewed source identity row is missing.");
            var target = identities.SingleOrDefault(identity => identity.Id == binding.EmployeeId && identity.ClientId == batch.ClientId)
                ?? throw new InvalidOperationException("The reviewed existing employee is missing or belongs to another client.");
            output.Add(row.Id, ReviewHistoricalSourceIdentity(request, batch, source, row, target, identities, banks, sourceHash));
        }
        return output;
    }

    internal static HistoricalVerifiedSourceIdentity ReviewHistoricalSourceIdentity(HistoricalPeriodSalaryRequest request,
        ExcelPayslipBatch batch, ExcelPayslipCalculationSource source, ExcelPayslipRow row, Identity target,
        IReadOnlyList<Identity> identities, IReadOnlyList<HistoricalBankIdentity> banks, string sourceHash)
    {
        ValidateHistoricalSourceIdentityReview(request);
        var review = request.SourceIdentityReview ?? throw new InvalidOperationException("An explicit source identity review is required.");
        var binding = review.Bindings.SingleOrDefault(binding => binding.RowId == row.Id && binding.EmployeeId == target.Id)
            ?? throw new InvalidOperationException("This exact employee/source row has no reviewed identity binding.");
        if (review.SourceHash != sourceHash || batch.ClientId != target.ClientId || !target.EligibleForMonth
            || identities.Count(identity => identity.Id == target.Id && identity.ClientId == batch.ClientId) != 1
            || string.IsNullOrWhiteSpace(row.EmployeeCode)
            || !row.EmployeeCode.Trim().Equals(target.EmployeeCode.Trim(), StringComparison.OrdinalIgnoreCase)
            || identities.Count(identity => identity.ClientId == batch.ClientId
                && identity.EmployeeCode.Trim().Equals(row.EmployeeCode.Trim(), StringComparison.OrdinalIgnoreCase)) != 1
            || batch.Rows.Count(candidate => candidate.EmployeeCode.Trim().Equals(row.EmployeeCode.Trim(), StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidOperationException("Reviewed bank ownership also requires one exact existing client employee code, one source row and valid employment in this month; it cannot authorize code changes.");

        static string Name(string text) => Regex.Replace(text.Normalize(NormalizationForm.FormKC).Trim(), @"\s+", " ").ToUpperInvariant();
        var changedName = Name(row.EmployeeName) != Name(target.Name);
        if (Name(binding.ReviewedSourceName) != Name(row.EmployeeName) || Name(binding.ReviewedMasterName) != Name(target.Name)
            || binding.ReviewedNameChange != changedName)
            throw new InvalidOperationException("The exact reviewed source/current Master names changed or the name-change decision is inconsistent. No fuzzy name association is permitted.");

        var originalIssues = HistoricalIdentityIssues(batch, source, row, target, identities);
        if (originalIssues.Any(error => error != MissingHistoricalSourceIdentity && error != ChangedHistoricalSourceIdentity))
            throw new InvalidOperationException("A government identity conflict or invalid source employment cannot be bypassed by bank ownership review.");
        if (changedName && !HistoricalGovernmentAnchorExists(source, row, target, identities))
            throw new InvalidOperationException("A reviewed source/Master name correction also requires an independently matching unique literal government identifier; bank and name review alone are insufficient.");

        var column = source.Columns.SingleOrDefault(column => column.ColumnIndex == binding.BankColumnIndex);
        static bool BankHeader(ExcelPayslipCalculationColumn candidate) => new[] { candidate.SourceHeader, candidate.Label }
            .Select(value => Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]", ""))
            .Any(key => key is "accountno" or "accountnumber" or "bankaccountno" or "bankaccountnumber" or "bankaccount" or "acno" or "bankacno");
        if (column is null || !BankHeader(column) || column.Kind is not ("info" or "ignore")
            || request.Setup.SourceColumnRoles.GetValueOrDefault(column.ColumnIndex, column.Kind) != "info"
            || request.Setup.Components.Any(component => component.ExpectedColumnIndex == column.ColumnIndex || component.SourceColumnIndex == column.ColumnIndex)
            || column.ColumnIndex == request.Setup.PayrollDaysColumnIndex || column.ColumnIndex == request.Setup.PayableDaysColumnIndex
            || column.ColumnIndex == request.Setup.PresentDaysColumnIndex)
            throw new InvalidOperationException("Bank ownership needs the explicitly reviewed independent account Information column; pay, attendance and generic numeric columns cannot identify an employee.");
        var captured = source.Rows.SingleOrDefault(captured => captured.SourceRow == row.SourceRow)
            ?? throw new InvalidOperationException("The exact captured bank identity row is missing.");
        var cell = captured.Cells.ElementAtOrDefault(column.ColumnIndex);
        var account = HistoricalLiteralBankAccount(cell);
        if (account.Length == 0)
            throw new InvalidOperationException("The source bank account must be an exact independent literal. Scientific notation, imprecise numeric accounts, formulas, errors, placeholders and missing values cannot prove ownership.");
        var otherAccounts = source.Columns.Where(BankHeader).Select(candidate => HistoricalLiteralBankAccount(captured.Cells.ElementAtOrDefault(candidate.ColumnIndex)))
            .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (otherAccounts.Length != 1)
            throw new InvalidOperationException("The source has conflicting account columns; review the source identity instead of selecting a convenient account.");
        var owners = banks.Where(bank => bank.ClientId == batch.ClientId && HistoricalBankAccount(bank.BankAccount) == account)
            .OrderBy(bank => bank.EmployeeId).ToArray();
        if (owners.Length != 1 || owners[0].EmployeeId != target.Id)
            throw new InvalidOperationException("The exact source bank account is shared, unmatched or owned by another employee; code/name alone cannot select an owner.");

        var evidence = new HistoricalSourceIdentityEvidence(batch.ClientId, target.Id, row.Id, row.SourceRow, column.ColumnIndex,
            sourceHash, Hash(JsonSerializer.Serialize(new { column.ColumnIndex, column.SourceHeader, column.Label, column.Kind, Cell = cell }, Json)),
            Hash(JsonSerializer.Serialize(new { Account = account, Owners = owners }, Json)),
            Hash(JsonSerializer.Serialize(new { row.EmployeeCode, SourceName = Name(row.EmployeeName), MasterName = Name(target.Name) }, Json)),
            changedName, review.Reason.Trim());
        return new(evidence, changedName);
    }

    internal static string HistoricalBankAccount(string? text)
    {
        var value = (text ?? "").Trim();
        // A single leading comma/apostrophe is an observed text marker, not a
        // thousands separator. Preserve every digit, including leading zeroes.
        if (value.StartsWith(',') || value.StartsWith('\'')) value = value[1..].Trim();
        return Regex.IsMatch(value, @"\A[0-9]{6,24}\z") && value.Any(character => character != value[0]) ? value : "";
    }

    private static string HistoricalLiteralBankAccount(ExcelPayslipCalculationCell? cell)
    {
        if (cell is null || cell.DataType is not ("" or "Text" or "Number") || cell.MissingCachedValue
            || !string.IsNullOrWhiteSpace(cell.FormulaText) || cell.Error is not null || cell.CalculationError is not null) return "";
        var account = HistoricalBankAccount(cell.Value);
        return cell.DataType == "Number" && (account.Length > 15 || !Regex.IsMatch(cell.Value, @"\A[0-9]+\z")) ? "" : account;
    }

    private static bool HistoricalGovernmentAnchorExists(ExcelPayslipCalculationSource source, ExcelPayslipRow row,
        Identity target, IReadOnlyList<Identity> identities)
    {
        var cells = source.Rows.SingleOrDefault(captured => captured.SourceRow == row.SourceRow);
        if (cells is null) return false;
        foreach (var column in source.Columns)
        {
            var kind = ExcelPayslipMasterSynchronization.ProfileIdentityKey(column.SourceHeader);
            if (kind.Length == 0) kind = ExcelPayslipMasterSynchronization.ProfileIdentityKey(column.Label);
            if (kind is not ("uan" or "pan" or "aadhaar")) continue;
            var cell = cells.Cells.ElementAtOrDefault(column.ColumnIndex);
            if (cell is null || cell.DataType is not ("" or "Text" or "Number") || cell.MissingCachedValue
                || !string.IsNullOrWhiteSpace(cell.FormulaText) || cell.Error is not null || cell.CalculationError is not null) continue;
            string Canonical(string? text)
            {
                var value = Regex.Replace((text ?? "").ToUpperInvariant(), @"[\s-]", "");
                return Regex.IsMatch(value, kind == "pan" ? @"\A[A-Z]{5}[0-9]{4}[A-Z]\z" : kind == "aadhaar" ? @"\A[2-9][0-9]{11}\z" : @"\A[0-9]{12}\z")
                    && (kind == "pan" || value.Any(character => character != value[0])) ? value : "";
            }
            var value = Canonical(cell.Value);
            if (value.Length == 0) continue;
            var owners = identities.Where(identity => identity.ClientId == target.ClientId
                && Canonical(kind == "uan" ? identity.Uan : kind == "pan" ? identity.Pan : identity.Aadhaar) == value).ToArray();
            if (owners.Length == 1 && owners[0].Id == target.Id) return true;
        }
        return false;
    }
}
