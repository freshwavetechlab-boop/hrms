using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    private static HistoricalPeriodSalaryRequest SourceNamedBankAdapter(ExcelPayrollSetupRequest request) => new()
    {
        Setup = request,
        SourceIdentityReview = request.SourceIdentityReview,
        Assignments = request.LocationAllocations?.Groups.SelectMany(group => group.Parts.Select(part =>
            new HistoricalPeriodSalaryAssignment(part.RowId, group.EmployeeId, part.TemplateId))).ToList() ?? []
    };

    internal static void ValidateSourceNamedBankReview(ExcelPayrollSetupRequest request)
    {
        if (request.SourceIdentityReview is null) return;
        if (request.NamedInputReview is null || request.LocationAllocations is null
            || request.LocationAllocations.Groups is null || request.LocationAllocations.Groups.Any(g => g is null || g.Parts is null)
            || request.SourceIdentityReview.Bindings is null || request.SourceIdentityReview.Bindings.Any(b => b is null || b.ReviewedNameChange))
            throw new InvalidOperationException("First-source bank identity review requires the exact named location setup and unchanged source/Master names.");
        ValidateHistoricalSourceIdentityReview(SourceNamedBankAdapter(request));
    }

    internal static HistoricalVerifiedSourceIdentity? ReviewSourceNamedBankIdentity(ExcelPayrollSetupRequest request,
        ExcelPayslipBatch batch, ExcelPayslipCalculationSource source, ExcelPayslipRow row, int employeeId,
        IReadOnlyList<Identity> identities, IReadOnlyList<HistoricalBankIdentity> banks)
    {
        if (request.SourceIdentityReview is null || !request.SourceIdentityReview.Bindings.Any(b => b.RowId == row.Id)) return null;
        ValidateSourceNamedBankReview(request);
        var target = identities.SingleOrDefault(i => i.Id == employeeId && i.ClientId == batch.ClientId)
            ?? throw new InvalidOperationException("The reviewed bank source employee is unavailable in this client.");
        var issues = new List<ExcelPayrollSetupIssue>();
        var match = MatchIdentity(row, SourceInformation(row, source, request.SourceWorkLocationColumnIndex),
            identities.Where(i => i.ClientId == batch.ClientId).ToArray(), new() { RowId = row.Id, MatchedEmployeeId = employeeId }, issues);
        if (match?.Id != employeeId || issues.Any(i => i.Severity == "Error"))
            throw new InvalidOperationException("Bank review cannot bypass a source code/name or government identity conflict.");
        var sourceHash = ExcelPayslipRepository.EmploymentSourceHash(batch, source);
        var proof = ReviewHistoricalSourceIdentity(SourceNamedBankAdapter(request), batch, source, row, target, identities, banks, sourceHash);
        var binding = request.SourceIdentityReview.Bindings.Single(b => b.RowId == row.Id && b.EmployeeId == employeeId);
        var cell = source.Rows.Single(r => r.SourceRow == row.SourceRow).Cells.ElementAtOrDefault(binding.BankColumnIndex);
        var account = HistoricalLiteralBankAccount(cell);
        var owners = banks.Where(b => HistoricalBankAccount(b.BankAccount) == account).Select(b => (b.EmployeeId, b.ClientId)).Distinct().ToArray();
        if (owners.Length != 1 || owners[0].EmployeeId != employeeId || owners[0].ClientId != batch.ClientId)
            throw new InvalidOperationException("The reviewed source bank account is not globally unique to the selected existing employee.");
        return proof;
    }

    private sealed class SourceNamedBankOwner
    {
        public int EmployeeId { get; set; }
        public int ClientId { get; set; }
        public bool HasPaymentRow { get; set; }
        public string BankAccount { get; set; } = "";
        public string PaymentJson { get; set; } = "{}";
    }

    internal static async Task<List<HistoricalBankIdentity>> ReadSourceNamedBankOwnersAsync(MySqlConnection db, MySqlTransaction tx)
    {
        var rows = await db.QueryAsync<SourceNamedBankOwner>(@"SELECT e.Id EmployeeId,e.ClientId,(p.EmployeeId IS NOT NULL) HasPaymentRow,
COALESCE(p.BankAccountNo,'') BankAccount,CAST(e.PaymentJson AS CHAR) PaymentJson FROM employees e LEFT JOIN employeepaymentdetails p ON p.EmployeeId=e.Id ORDER BY e.Id", transaction: tx);
        return rows.Select(row =>
        {
            var account = row.BankAccount;
            if (!row.HasPaymentRow)
            {
                var json = JsonNode.Parse(row.PaymentJson) as JsonObject;
                account = json?["bankAccountNo"]?.ToString() ?? json?["account"]?.ToString() ?? json?["accountNumber"]?.ToString() ?? "";
            }
            return new HistoricalBankIdentity(row.EmployeeId, row.ClientId, account);
        }).ToList();
    }

    private static async Task LockSourceNamedBankOwnersAsync(MySqlConnection db, MySqlTransaction tx, ExcelPayrollSetupRequest request)
    {
        if (request.SourceIdentityReview is null) return;
        // This explicit bank-only lane must also exclude concurrent foreign
        // account owners. The existing government/null lane is unchanged.
        await db.QueryAsync<int>("SELECT Id FROM employees ORDER BY Id FOR UPDATE", transaction: tx);
        await db.QueryAsync<int>("SELECT EmployeeId FROM employeepaymentdetails ORDER BY EmployeeId FOR UPDATE", transaction: tx);
    }
}
