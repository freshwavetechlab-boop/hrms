using System.Data;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollNullableNameReaderTests
{
    private sealed class ReadOnlyNameTheoryAttribute : TheoryAttribute
    {
        public ReadOnlyNameTheoryAttribute()
        {
            if (Environment.GetEnvironmentVariable("FREVO_PAYROLL_NAME_READONLY_TEST_DB") is null)
                Skip = "An explicitly supplied MySQL connection is required for bound-literal SELECT-only name projection tests.";
        }
    }

    [ReadOnlyNameTheory]
    [InlineData("Synthetic", "Person", "Synthetic Person")]
    [InlineData("Synthetic", null, "Synthetic")]
    [InlineData(null, "Person", "Person")]
    [InlineData(null, null, "")]
    [InlineData("", null, "")]
    [InlineData(null, "", "")]
    [InlineData("Single", "", "Single")]
    [InlineData("", "Single", "Single")]
    [InlineData("  Single ", null, "Single")]
    [InlineData("Synthetic", "  Person ", "Synthetic   Person")]
    public async Task ActualDapperNameProjectionPreservesSingleNamesAndStrictIdentityChecks(string? first, string? last, string expected)
    {
        var connection = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FREVO_PAYROLL_NAME_READONLY_TEST_DB")
            ?? throw new InvalidOperationException("An explicit SELECT-only test connection is required."))
        { GuidFormat = MySqlGuidFormat.None };
        await using var db = new MySqlConnection(connection.ConnectionString);
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, isReadOnly: true);
        // The exact projection used by Context, roster and normal BuildPlan is
        // evaluated by MySQL and mapped by Dapper. No customer table is read.
        var master = await db.QuerySingleAsync<ExcelPayrollSetupRepository.Identity>(
            "SELECT 7 Id,11 ClientId,'SYN001' EmployeeCode," + ExcelPayrollSetupRepository.MasterEmployeeNameSql
            + " Name,TRUE IsActive,TRUE EligibleForMonth FROM (SELECT @FirstName FirstName,@LastName LastName) e",
            new { FirstName = first, LastName = last }, tx);
        Assert.Equal(expected, master.Name);

        var row = new ExcelPayslipRow { Id = "synthetic-row-2", SourceRow = 2, EmployeeCode = "SYN001",
            EmployeeName = expected.Length == 0 ? "Source Person" : expected };
        var issues = new List<ExcelPayrollSetupIssue>();
        var matched = ExcelPayrollSetupRepository.MatchIdentity(row, [], [master],
            new() { RowId = row.Id, MatchedEmployeeId = 7, AllowCreate = false }, issues);
        Assert.Equal(7, matched!.Id);
        Assert.Equal(expected.Length == 0, issues.Any(i => i.Message == "The source name differs from the matched employee. Resolve identity before importing salary."));

        row.EmployeeName = "Different Source Person";
        issues.Clear();
        ExcelPayrollSetupRepository.MatchIdentity(row, [], [master],
            new() { RowId = row.Id, MatchedEmployeeId = 7, AllowCreate = false }, issues);
        Assert.Contains(issues, i => i.Severity == "Error" && i.Message == "The source name differs from the matched employee. Resolve identity before importing salary.");
        await tx.RollbackAsync();
    }
}
