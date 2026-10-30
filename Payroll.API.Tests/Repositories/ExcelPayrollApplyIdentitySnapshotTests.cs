using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class ExcelPayrollApplyIdentitySnapshotTests
{
    private static Employee Master(int id) => new()
    {
        Id = id, ClientId = 11, EmployeeCode = $"SYN{id:000}", FirstName = $"Synthetic{id}",
        Department = "Existing department", Designation = "Existing designation", Grade = "Existing grade",
        WorkLocationId = 7, SalaryStructureId = "reviewed-template", AnnualCtc = 120000m,
        SalaryJson = "{\"BASIC\":10000}", SalaryComponents = new() { ["BASIC"] = 10000m },
        PersonalJson = "{\"preserved\":\"personal\"}", PaymentJson = "{\"preserved\":\"payment\"}",
        PersonalDetails = new() { Address = "Existing address", EmploymentType = "Existing employment" },
        PaymentDetails = new() { BankName = "Existing bank", IfscCode = "SYNTHETIC01", PaymentMode = "Bank transfer" }
    };

    private static (string RowId, Employee Employee)[] Rows(params Employee[] employees) =>
        employees.Select((employee, index) => ($"source-{700 + index}", employee)).ToArray();

    private static Task<ExcelPayrollSetupRepository.ApplyIdentityReview> Review(
        (string RowId, Employee Employee)[] rows, IReadOnlyList<Employee> masters,
        bool attendance = false, IReadOnlySet<string>? bankReviewRows = null) =>
        ExcelPayrollSetupRepository.ReviewApplyIdentitiesAsync(11, 1, attendance, rows,
            bankReviewRows ?? new HashSet<string>(), candidates => Task.FromResult<IReadOnlyList<EmployeeImportPreflightRow>>(
                EmployeeRepository.ExcelPayrollIdentityRows(candidates, masters)));

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task ExistingSalaryOnlyCohortUsesOnePreflightInsteadOfOnePerEmployee(int count)
    {
        var masters = Enumerable.Range(1, count).Select(Master).ToArray();
        var rows = Rows(masters);
        var reads = 0;
        var review = await ExcelPayrollSetupRepository.ReviewApplyIdentitiesAsync(11, 1, false, rows,
            new HashSet<string>(), candidates =>
            {
                reads++;
                Assert.Equal(count, candidates.Count);
                return Task.FromResult<IReadOnlyList<EmployeeImportPreflightRow>>(
                    EmployeeRepository.ExcelPayrollIdentityRows(candidates, masters));
            });

        Assert.Equal(1, reads);
        Assert.Empty(review.Issues);
        Assert.NotNull(review.ExistingSalaryOnlyRows);
        Assert.Equal(count, review.ExistingSalaryOnlyRows.Count);
        var legacyReads = 0;
        foreach (var row in rows)
        {
            legacyReads++;
            var legacy = EmployeeRepository.ExcelPayrollIdentityRows([row.Employee], masters);
            var legacyIssues = ExcelPayrollSetupRepository.FullIdentityIssues([(row.RowId, row.Employee.Id)], legacy);
            Assert.Equal(legacyIssues.ToArray(), review.ExistingSalaryOnlyRows[row.RowId].ToArray());
        }
        Assert.Equal(count, legacyReads);
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData("new")]
    [InlineData("mixed")]
    [InlineData("attendance")]
    [InlineData("wrong-client")]
    [InlineData("legacy")]
    [InlineData("duplicate-employee")]
    [InlineData("duplicate-row")]
    public async Task OtherCohortsKeepThePerEmployeeFallback(string kind)
    {
        var existing = Master(1);
        var other = Master(2);
        var created = Master(0);
        created.EmployeeCode = "SYNNEW";
        if (kind == "duplicate-employee") existing.PersonalDetails.Mobile = "9876501234";
        var rows = kind switch
        {
            "new" => Rows(created),
            "mixed" => Rows(existing, created),
            "duplicate-employee" => Rows(existing, existing),
            "duplicate-row" => new[] { ("same-source", existing), ("same-source", other) },
            _ => Rows(existing)
        };
        if (kind == "wrong-client") existing.ClientId = 12;
        var reads = 0;
        var review = await ExcelPayrollSetupRepository.ReviewApplyIdentitiesAsync(11, kind == "legacy" ? 0 : 1, kind == "attendance", rows,
            new HashSet<string>(), candidates =>
            {
                reads++;
                return Task.FromResult<IReadOnlyList<EmployeeImportPreflightRow>>(
                    EmployeeRepository.ExcelPayrollIdentityRows(candidates, [existing, other]));
            });

        Assert.Equal(1, reads); // The existing BuildPlan cohort check remains in every nonempty path.
        Assert.Null(review.ExistingSalaryOnlyRows);
        if (kind == "duplicate-employee")
            Assert.Contains(review.Issues, issue => issue.Severity == "Error");
    }

    [Fact]
    public async Task EmptyCohortDoesNotReadOrCreateASnapshot()
    {
        var review = await ExcelPayrollSetupRepository.ReviewApplyIdentitiesAsync(11, 1, false, [],
            new HashSet<string>(), _ => throw new InvalidOperationException("An empty cohort must not read identities."));
        Assert.Empty(review.Issues);
        Assert.Null(review.ExistingSalaryOnlyRows);
    }

    [Theory]
    [InlineData("code")]
    [InlineData("email")]
    [InlineData("mobile")]
    [InlineData("pan")]
    [InlineData("aadhaar")]
    [InlineData("bank")]
    public async Task NonselectedMasterCollisionsRemainBlockedByTheSameFullIdentityRules(string identifier)
    {
        var selected = Master(1);
        var nonselected = Master(2);
        switch (identifier)
        {
            case "code": selected.EmployeeCode = nonselected.EmployeeCode = "SYNSHARED"; break;
            case "email": selected.WorkEmail = nonselected.WorkEmail = "shared@example.invalid"; break;
            case "mobile": selected.PersonalDetails.Mobile = nonselected.PersonalDetails.Mobile = "9876501234"; break;
            case "pan": selected.PersonalDetails.PanNumber = nonselected.PersonalDetails.PanNumber = "ABCDE1234F"; break;
            case "aadhaar": selected.PersonalDetails.AadhaarNumber = nonselected.PersonalDetails.AadhaarNumber = "234567890123"; break;
            case "bank": selected.PaymentDetails.BankAccountNo = nonselected.PaymentDetails.BankAccountNo = "123456789012345"; break;
        }
        var rows = Rows(selected);
        var masters = new[] { selected, nonselected };
        var review = await Review(rows, masters);
        var legacy = EmployeeRepository.BuildImportIdentityPreflightRows(
            EmployeeRepository.RecruitmentIdentityWorkbook([selected]), "upsert", masters);
        var legacyIssues = ExcelPayrollSetupRepository.FullIdentityIssues([(rows[0].RowId, selected.Id)], legacy);

        Assert.NotNull(review.ExistingSalaryOnlyRows);
        Assert.NotEmpty(review.Issues);
        Assert.All(review.Issues, issue => Assert.Equal("Error", issue.Severity));
        Assert.Equal(legacyIssues.ToArray(), review.ExistingSalaryOnlyRows[rows[0].RowId].ToArray());
    }

    [Fact]
    public async Task RepeatedIdentifiersAcrossDistinctSelectedEmployeesRemainBlockedBeforeWrites()
    {
        var first = Master(1);
        var second = Master(2);
        first.PersonalDetails.Mobile = second.PersonalDetails.Mobile = "9876501234";
        var rows = Rows(first, second);
        var review = await Review(rows, [first, second]);

        Assert.NotNull(review.ExistingSalaryOnlyRows);
        foreach (var row in rows)
            Assert.Contains(review.ExistingSalaryOnlyRows[row.RowId], issue =>
                issue.Severity == "Error" && issue.Message.Contains("reviewed source selection", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScientificBankTextNeedsTheSameExplicitSourceBindingForSeparatePaymentReview(bool bound)
    {
        var employee = Master(1);
        employee.PaymentDetails.BankAccountNo = "1.2346E+11";
        var rows = Rows(employee);
        var bindings = new HashSet<string> { bound ? rows[0].RowId : "another-source-row" };
        var review = await Review(rows, [employee], bankReviewRows: bindings);

        Assert.NotNull(review.ExistingSalaryOnlyRows);
        var issue = Assert.Single(review.ExistingSalaryOnlyRows[rows[0].RowId]);
        Assert.Equal(bound ? "Review" : "Error", issue.Severity);
        Assert.Equal(bound ? ExcelPayrollSetupRepository.BankDataReviewCode : null, issue.Code);
        Assert.Equal("1.2346E+11", employee.PaymentDetails.BankAccountNo);
    }

    [Fact]
    public async Task PaymentReviewCannotOverrideAnAdditionalRealIdentifierConflict()
    {
        var employee = Master(1);
        var other = Master(2);
        employee.PaymentDetails.BankAccountNo = "1.2346E+11";
        employee.WorkEmail = other.WorkEmail = "shared@example.invalid";
        var rows = Rows(employee);
        var review = await Review(rows, [employee, other], bankReviewRows: new HashSet<string> { rows[0].RowId });
        Assert.Contains(review.Issues, issue => issue.Severity == "Error");
    }

    [Fact]
    public async Task SnapshotContainsFrozenIssuesAndDoesNotRetainMutablePreflightOrEmployeeState()
    {
        var employee = Master(1);
        employee.PaymentDetails.BankAccountNo = "1.2346E+11";
        var rows = Rows(employee);
        var checks = EmployeeRepository.ExcelPayrollIdentityRows([employee], [employee]);
        var review = await ExcelPayrollSetupRepository.ReviewApplyIdentitiesAsync(11, 1, false, rows,
            new HashSet<string> { rows[0].RowId }, _ => Task.FromResult<IReadOnlyList<EmployeeImportPreflightRow>>(checks));
        Assert.NotNull(review.ExistingSalaryOnlyRows);
        var snapshot = review.ExistingSalaryOnlyRows[rows[0].RowId];
        var issue = Assert.Single(snapshot);

        checks.Clear();
        employee.EmployeeCode = "LATER-MUTATION";
        employee.PaymentDetails.BankAccountNo = "later account";
        Assert.Equal(issue, Assert.Single(snapshot));
        Assert.Equal(ExcelPayrollSetupRepository.BankDataReviewCode, issue.Code);
        Assert.Throws<NotSupportedException>(() => ((IList<ExcelPayrollSetupIssue>)snapshot).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ExcelPayrollSetupIssue>)review.Issues).Clear());
    }

    [Fact]
    public async Task CohortReviewLeavesSalaryRatesAndPersonalOrganizationPaymentStateUntouched()
    {
        var employee = Master(1);
        employee.PersonalDetails.Mobile = "9876501234";
        employee.PaymentDetails.BankAccountNo = "123456789012345";
        var personal = employee.PersonalDetails;
        var payment = employee.PaymentDetails;
        var components = employee.SalaryComponents;
        var before = JsonSerializer.Serialize(employee);
        var review = await Review(Rows(employee), [employee]);

        Assert.Empty(review.Issues);
        Assert.Equal(before, JsonSerializer.Serialize(employee));
        Assert.Same(personal, employee.PersonalDetails);
        Assert.Same(payment, employee.PaymentDetails);
        Assert.Same(components, employee.SalaryComponents);
        Assert.Equal(10000m, employee.SalaryComponents["BASIC"]);
        Assert.Equal(120000m, employee.AnnualCtc);
    }
}
