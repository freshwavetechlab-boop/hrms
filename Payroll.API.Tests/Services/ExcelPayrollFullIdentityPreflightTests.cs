using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollFullIdentityPreflightTests
{
    private static Employee Master(int id, string bank = "", string email = "") => new()
    {
        Id=id,ClientId=11,EmployeeCode=$"SYN{id:000}",FirstName=$"Synthetic{id}",
        WorkEmail=email,PaymentDetails=new() { BankAccountNo=bank },
    };
    private static List<EmployeeImportPreflightRow> Check(Employee[] selected, params Employee[] masters) =>
        EmployeeRepository.BuildImportIdentityPreflightRows(EmployeeRepository.RecruitmentIdentityWorkbook(selected),"upsert",masters);

    [Fact] public void StoredBankConflictIsBlockedEvenWhenCodeAndOtherIdentityAgree()
    {
        var first=Master(1,"SYNTHETICBANK001","first@example.invalid");
        var other=Master(2,"SYNTHETICBANK001","second@example.invalid");
        var check=Assert.Single(Check([first],first,other));
        Assert.Contains(check.BlockingReasons,reason=>reason.Contains("Bank Account") && reason.Contains("SYN002"));
        var issue=Assert.Single(ExcelPayrollSetupRepository.FullIdentityIssues([("source-707",1)],[check]));
        Assert.Equal("source-707",issue.RowId);
        Assert.Equal("Error",issue.Severity);
        Assert.DoesNotContain(first.PaymentDetails.BankAccountNo,issue.Message);
    }

    [Theory]
    [InlineData("email")]
    [InlineData("mobile")]
    public void AdditionalMasterIdentifiersUseTheExistingImportRules(string kind)
    {
        var first=Master(1); var other=Master(2);
        if(kind=="email")first.WorkEmail=other.WorkEmail="synthetic@example.invalid";
        else first.PersonalDetails.Mobile=other.PersonalDetails.Mobile="9876501234";
        var checks=Check([first],first,other);
        Assert.NotEmpty(ExcelPayrollSetupRepository.FullIdentityIssues([("source-2",1)],checks));
    }

    [Fact] public void CleanSubsetRemainsEligibleWithoutChangingConflictingMasters()
    {
        var clean=Master(1,"SYNTHETICBANK001"); var a=Master(2,"SYNTHETICBANK002"); var b=Master(3,"SYNTHETICBANK002");
        var checks=Check([clean],clean,a,b);
        Assert.Empty(ExcelPayrollSetupRepository.FullIdentityIssues([("clean",1)],checks));
        Assert.Equal("SYNTHETICBANK002",a.PaymentDetails.BankAccountNo);
        Assert.Equal(a.PaymentDetails.BankAccountNo,b.PaymentDetails.BankAccountNo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("000000")]
    [InlineData("NA")]
    public void BlankOrPlaceholderBankValuesDoNotCreateFalseDuplicateMatches(string bank)
    {
        var first=Master(1,bank); var other=Master(2,bank);
        Assert.Empty(ExcelPayrollSetupRepository.FullIdentityIssues([("one",1)],Check([first],first,other)));
    }

    [Fact] public void DuplicateCandidateRowsAreRejectedBeforeApply()
    {
        var a=Master(1,"SYNTHETICBANK001");
        var issues=ExcelPayrollSetupRepository.FullIdentityIssues([("one",1),("two",1)],Check([a,a],a));
        Assert.Contains(issues,issue=>issue.RowId=="one" && issue.Message.Contains("reviewed source selection"));
        Assert.Contains(issues,issue=>issue.RowId=="two" && issue.Message.Contains("reviewed source selection"));
    }

    [Fact] public void CheckRowsAreBoundBySourcePositionRatherThanResultOrder()
    {
        var a=Master(1,"SYNTHETICBANK001"); var b=Master(2,"SYNTHETICBANK002");
        var checks=Check([a,b],a,b);
        Assert.Empty(ExcelPayrollSetupRepository.FullIdentityIssues([("a",1),("b",2)],checks.AsEnumerable().Reverse().ToList()));
        Assert.All(ExcelPayrollSetupRepository.FullIdentityIssues([("a",2),("b",1)],checks),issue=>Assert.Contains("different master",issue.Message));
    }

    [Fact] public void MissingOrDuplicatedCheckCannotAdvertiseAnEligibleRow()
    {
        var a=Master(1); var check=Assert.Single(Check([a],a));
        Assert.Single(ExcelPayrollSetupRepository.FullIdentityIssues([("a",1)],[]));
        Assert.Single(ExcelPayrollSetupRepository.FullIdentityIssues([("a",1)],[check,check]));
    }

    [Fact] public void UnmatchedNewEmployeeIsAllowedOnlyWithoutAnExistingIdentifierMatch()
    {
        var candidate=Master(0,"SYNTHETICBANK001");candidate.EmployeeCode="SYNNEW";
        var existing=Master(2,"SYNTHETICBANK002");
        Assert.Empty(ExcelPayrollSetupRepository.FullIdentityIssues([("new",0)],Check([candidate],existing)));
        candidate.PaymentDetails.BankAccountNo=existing.PaymentDetails.BankAccountNo;
        Assert.Single(ExcelPayrollSetupRepository.FullIdentityIssues([("new",0)],Check([candidate],existing)));
    }

    [Fact] public void RosterReportsConflictWithoutChangingAppliedHistoryOrMasterLink()
    {
        var roster=new ExcelPayrollRoster([
            new("bad",707,"Synthetic","SYN001","Existing",1,"Matched"),
            new("clean",701,"Synthetic","SYN002","Existing",2,"Matched"),
            new("applied",700,"Synthetic","SYN003","Applied",3,"Applied")],[]);
        var result=ExcelPayrollSetupRepository.WithFullIdentityIssues(roster,[new("Error","Bank Account conflicts","bad"),new("Error","Conflict","applied")]);
        Assert.Equal(new[] {"Review","Existing","Applied"},result.Rows.Select(row=>row.Status));
        Assert.Equal(1,result.Rows[0].EmployeeId);
        Assert.Equal("Existing",roster.Rows[0].Status);
        Assert.Equal("Applied",result.Rows[2].Message);
    }

    [Fact] public void LargeDuplicateGroupsRemainBlockedWithoutUnboundedRosterMessages()
    {
        var masters=Enumerable.Range(1,200).Select(id=>Master(id,"SYNTHETICSHAREDBANK")).ToArray();
        var checks=Check([masters[0]],masters);
        var issue=Assert.Single(ExcelPayrollSetupRepository.FullIdentityIssues([("source-707",1)],checks));
        Assert.Equal("Error",issue.Severity);
        Assert.Contains("other master records",issue.Message);
        Assert.True(issue.Message.Length<800);
        Assert.DoesNotContain("SYNTHETICSHAREDBANK",issue.Message);
    }

    [Fact] public void TemporaryMatcherRowNumbersAreNotMisrepresentedAsOriginalExcelRows()
    {
        var employee=Master(1,"SYNTHETICBANK001");
        var checks=Check([employee,employee],employee);
        var issues=ExcelPayrollSetupRepository.FullIdentityIssues([("source-702",1),("source-706",1)],checks);
        Assert.All(issues,issue=>Assert.DoesNotContain("workbook row",issue.Message));
        Assert.Contains(issues,issue=>issue.RowId=="source-702");
        Assert.Contains(issues,issue=>issue.RowId=="source-706");
    }

    [Fact] public void LightweightReadinessHasExactlyTheSameIdentityOutcomeWithoutFieldDiffExpansion()
    {
        var masters=Enumerable.Range(1,50).Select(id=>Master(id,"SYNTHETICSHAREDBANK")).Append(Master(51,"SYNTHETICUNIQUEBANK")).ToArray();
        var selected=new[] {masters[0],masters[50]};
        var full=Check(selected,masters);
        var light=EmployeeRepository.ExcelPayrollIdentityRows(selected,masters);
        Assert.Equal(full.Count,light.Count);
        for(var index=0;index<full.Count;index++)
        {
            Assert.Equal(full[index].MatchStatus,light[index].MatchStatus);
            Assert.Equal(full[index].MatchedEmployeeId,light[index].MatchedEmployeeId);
            Assert.Equal(full[index].BlockingReasons,light[index].BlockingReasons);
            Assert.Equal(full[index].CandidateEmployees!.Select(item=>item.EmployeeId),light[index].CandidateEmployees!.Select(item=>item.EmployeeId));
            Assert.Empty(light[index].Changes);
            Assert.Empty(light[index].IdentityEvidence!);
            Assert.All(light[index].CandidateEmployees!,candidate=>Assert.Empty(candidate.Changes));
        }
    }
}
