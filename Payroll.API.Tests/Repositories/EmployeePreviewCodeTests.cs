using Payroll.API.Repositories;
using Payroll.API.Models;

namespace Payroll.API.Tests.Repositories;

public sealed class EmployeePreviewCodeTests
{
    [Fact]
    public void OnlyAnUnmatchedBlankCodeWithoutConflictsCanBeGenerated()
    {
        var workbook = new EmployeeImportWorkbook(new()
        {
            ["Employees"] = [
                ["Employee Code", "First Name", "Work Email", "Employee ID"],
                ["", "New", "new@example.test", ""],
                ["EX12", "Supplied", "", ""],
                ["", "Matched", "existing@example.test", ""],
                ["", "Duplicate", "same@example.test", ""],
                ["", "Duplicate again", "same@example.test", ""],
                ["", "Bad ID", "", "unknown"]
            ]
        });
        var employees = new Payroll.API.Models.Employee[] { new() { Id = 321, EmployeeCode = "EX321", WorkEmail = "existing@example.test", FirstName = "Matched" } };
        var rows = EmployeeRepository.BuildImportIdentityPreflightRows(workbook, "upsert", employees);
        Assert.True(rows[0].CanGenerateEmployeeCode);
        Assert.All(rows.Skip(1), row => Assert.False(row.CanGenerateEmployeeCode));
        Assert.Equal("EX321", rows[2].MatchedEmployeeCode);
        Assert.False(EmployeeRepository.BuildImportIdentityPreflightRows(workbook, "update", employees)[0].CanGenerateEmployeeCode);
    }

    [Fact]
    public void BlankCodeShowsUpToThreeMaskedNameHintsWithoutSelectingAnEmployee()
    {
        var employees = Enumerable.Range(1, 4).Select(id => new Employee
        {
            Id = id, ClientId = 11, EmployeeCode = $"SYN{id:000}", FirstName = "Example", LastName = "Person",
            PersonalDetails = new() { Mobile = "9876543210" }, PaymentDetails = new() { BankAccountNo = "123456789012" }
        }).ToArray();
        var workbook = new EmployeeImportWorkbook(new() { ["Employees"] = [
            ["Employee Code", "First Name", "Last Name"], ["", " EXAMPLE ", "Person"]
        ] });

        var row = Assert.Single(EmployeeRepository.BuildImportIdentityPreflightRows(workbook, "update", employees));
        Assert.Equal("Blocked", row.MatchStatus);
        Assert.Null(row.MatchedEmployeeId);
        Assert.Null(row.MatchedEmployeeCode);
        Assert.Empty(row.CandidateEmployees!);
        Assert.False(row.CanResolveConflict);
        Assert.False(row.CanGenerateEmployeeCode);
        Assert.Equal(new[] { "SYN001", "SYN002", "SYN003" }, row.CodeSuggestions!.Select(item => item.EmployeeCode));
        Assert.All(row.CodeSuggestions!, suggestion => {
            Assert.Equal("Example Person", suggestion.EmployeeName);
            Assert.Equal("98••••3210", suggestion.Mobile);
            Assert.Equal("••••9012", suggestion.BankAccountNo);
            Assert.Single(suggestion.MatchReasons);
            Assert.Contains("verify details", suggestion.MatchReasons[0]);
        });
        Assert.All(employees, employee => {
            Assert.Equal("9876543210", employee.PersonalDetails.Mobile);
            Assert.Equal("123456789012", employee.PaymentDetails.BankAccountNo);
        });
        // A hint alone does not change the existing unmatched/upsert rule.
        var upsert = Assert.Single(EmployeeRepository.BuildImportIdentityPreflightRows(workbook, "upsert", employees));
        Assert.True(upsert.CanGenerateEmployeeCode);
        Assert.Null(upsert.MatchedEmployeeId);
    }

    [Fact]
    public void StrongConflictingIdentitiesComeBeforeNameHintsAndRemainUnresolved()
    {
        Employee[] employees = [
            new() { Id = 1, ClientId = 11, EmployeeCode = "SYN100", FirstName = "Mobile", LastName = "Match", PersonalDetails = new() { Mobile = "9912345678" } },
            new() { Id = 2, ClientId = 11, EmployeeCode = "SYN200", FirstName = "Account", LastName = "Match", PaymentDetails = new() { BankAccountNo = "123456789012" } },
            new() { Id = 3, ClientId = 11, EmployeeCode = "SYN001", FirstName = "Example", LastName = "Person" }
        ];
        var workbook = new EmployeeImportWorkbook(new() { ["Employees"] = [
            ["Employee Code", "First Name", "Last Name", "Mobile", "Bank Account No"],
            ["", "Example", "Person", "9912345678", "123456789012"]
        ] });

        var row = Assert.Single(EmployeeRepository.BuildImportIdentityPreflightRows(workbook, "upsert", employees));
        Assert.Equal("IdentityConflict", row.MatchStatus);
        Assert.Null(row.MatchedEmployeeId);
        Assert.False(row.CanGenerateEmployeeCode);
        Assert.Equal(new[] { 1, 2 }, row.CandidateEmployees!.Select(candidate => candidate.EmployeeId));
        Assert.Equal(new[] { 1, 2, 3 }, row.CodeSuggestions!.Select(suggestion => suggestion.EmployeeId));
        Assert.Contains("Exact Mobile", row.CodeSuggestions![0].MatchReasons);
        Assert.Contains("Exact Bank Account", row.CodeSuggestions![1].MatchReasons);
        Assert.Contains(row.BlockingReasons, reason => reason.Contains("Identifiers point to different employees"));
        Assert.DoesNotContain(row.IdentityEvidence!.SelectMany(evidence => evidence.Candidates), candidate => candidate.EmployeeId == 3);
    }

    [Fact]
    public void SuppliedCodesAndIdentityOnlyWorkflowsDoNotExposePreviewHints()
    {
        Employee[] employees = [new() { Id = 1, EmployeeCode = "SYN001", FirstName = "Example", LastName = "Person" }];
        var workbook = new EmployeeImportWorkbook(new() { ["Employees"] = [
            ["Employee Code", "First Name", "Last Name"], ["SYN001", "Example", "Person"], ["", "Example", "Person"]
        ] });
        var rows = EmployeeRepository.BuildImportIdentityPreflightRows(workbook, "update", employees);
        Assert.Null(rows[0].CodeSuggestions);
        Assert.NotEmpty(rows[1].CodeSuggestions!);
        Assert.All(EmployeeRepository.BuildImportIdentityPreflightRows(workbook, "update", employees, includeFieldChanges: false), row => Assert.Null(row.CodeSuggestions));
    }
}
