using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ScientificBankIdentityTests
{
    private static Employee Master(int id,string bank,string pan="") => new()
    {
        Id=id,ClientId=11,EmployeeCode=$"SYN{id:000}",FirstName=$"Synthetic{id}",
        PersonalDetails=new(){PanNumber=pan},PaymentDetails=new(){BankAccountNo=bank}
    };
    private static EmployeeImportPreflightRow Check(Employee selected,params Employee[] current) =>
        Assert.Single(EmployeeRepository.BuildImportIdentityPreflightRows(EmployeeRepository.RecruitmentIdentityWorkbook([selected]),"upsert",current));

    [Theory]
    [InlineData("1.2346E+11")]
    [InlineData("1.2346e11")]
    [InlineData("12E3")]
    [InlineData(" 1.2346E+11 ")]
    [InlineData("'1.2346E+11")]
    [InlineData("1E-3")]
    public void ScientificTextNeverBecomesAnExactBankIdentityAndStillRequiresReview(string bank)
    {
        var selected=Master(1,bank,"ABCDE1001F");var other=Master(2,bank,"ABCDE1002F");
        var check=Check(selected,selected,other);
        Assert.Equal(1,check.MatchedEmployeeId);
        Assert.Equal("Blocked",check.MatchStatus);
        Assert.False(check.CanResolveConflict);
        Assert.Contains(EmployeeRepository.UnreliableBankIdentityMessage,check.BlockingReasons);
        Assert.DoesNotContain(check.BlockingReasons,r=>r.Contains("Identifiers point to different employees")||r.Contains("also supplied"));
        Assert.DoesNotContain(check.CandidateEmployees!,c=>c.EmployeeId==2);
        Assert.DoesNotContain(check.CandidateEmployees!.SelectMany(c=>c.MatchReasons),r=>r.Contains("Bank Account"));
        Assert.Equal(bank,selected.PaymentDetails.BankAccountNo);
        Assert.Equal(bank,other.PaymentDetails.BankAccountNo);
        Assert.All(ExcelPayrollSetupRepository.FullIdentityIssues([("source-2",1)],[check]),issue=>{
            Assert.Equal("Error",issue.Severity);Assert.DoesNotContain(bank.Trim(),issue.Message);
        });
    }

    [Fact] public void ScientificOnlyCandidateCannotSelectAnyExistingEmployee()
    {
        var current=Master(1,"1.2346E+11");var candidate=Master(0,"1.2346E+11");candidate.EmployeeCode="SYNNEW";
        var check=Check(candidate,current);
        Assert.Null(check.MatchedEmployeeId);
        Assert.Empty(check.CandidateEmployees!);
        Assert.Contains(EmployeeRepository.UnreliableBankIdentityMessage,check.BlockingReasons);
    }

    [Fact] public void EqualRoundedScientificValuesInSourceDoNotMeanDuplicatePeople()
    {
        var a=Master(1,"1.2346E+11");var b=Master(2,"1.2346E+11");
        var checks=EmployeeRepository.BuildImportIdentityPreflightRows(EmployeeRepository.RecruitmentIdentityWorkbook([a,b]),"upsert",[a,b]);
        Assert.All(checks,check=>{
            Assert.Contains(EmployeeRepository.UnreliableBankIdentityMessage,check.BlockingReasons);
            Assert.DoesNotContain(check.BlockingReasons,r=>r.Contains("also supplied on workbook row"));
        });
    }

    [Fact] public void ValidExactBankCollisionRemainsBlocked()
    {
        var a=Master(1,"12345678901234");var b=Master(2,"12345678901234");
        var check=Check(a,a,b);
        Assert.Contains(check.BlockingReasons,r=>r.Contains("Bank Account")&&r.Contains("SYN002"));
        Assert.DoesNotContain(EmployeeRepository.UnreliableBankIdentityMessage,check.BlockingReasons);
    }

    [Fact] public void ScientificBankDoesNotHideStrongIdentifierConflict()
    {
        var a=Master(1,"1.2346E+11","ABCDE1001F");var b=Master(2,"12345678901235","ABCDE1001F");
        var check=Check(a,a,b);
        Assert.Contains(EmployeeRepository.UnreliableBankIdentityMessage,check.BlockingReasons);
        Assert.Contains(check.BlockingReasons,r=>r.Contains("Identifiers point to different employees")&&r.Contains("PAN"));
        Assert.False(check.CanResolveConflict);
    }

    [Theory]
    [InlineData("12345678901234")]
    [InlineData("ABCD123456")]
    [InlineData("SYNTHETICBANK001")]
    public void CompleteTextAccountsRetainTheExistingExactMatchContract(string bank)
    {
        var a=Master(1,bank);var check=Check(a,a);
        Assert.Empty(check.BlockingReasons);
        Assert.Equal(1,check.MatchedEmployeeId);
        Assert.Contains("Exact Bank Account",check.MatchReasons);
    }
}
