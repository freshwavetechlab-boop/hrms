using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollSalaryBankReviewTests
{
    private static Employee Master(int id=1,string bank="1.2346E+11",string pan="ABCDE1001F") => new() {
        Id=id,ClientId=11,EmployeeCode=$"SYN{id:000}",FirstName="Synthetic",LastName=$"Employee{id}",
        PersonalDetails=new(){PanNumber=pan},PaymentDetails=new(){BankAccountNo=bank}
    };
    private static List<EmployeeImportPreflightRow> Check(Employee candidate,params Employee[] masters) =>
        EmployeeRepository.ExcelPayrollIdentityRows([candidate],masters);
    private static ExcelPayslipBatch Batch() => new() {
        ClientId=11,Month="2026-09",Rows=[new(){Id="row-one",SourceRow=9,EmployeeCode="SOURCE001",EmployeeName="Synthetic Employee1"}]
    };
    private static ExcelPayslipCalculationSource Source() => new() {
        Columns=[new(){ColumnIndex=0,SourceHeader="PAN"}],Rows=[new(){SourceRow=9,Cells=[new(){Value="ABCDE1001F"}]}]
    };
    private static ExcelPayrollSetupRepository.Identity Identity(int id=1,string pan="ABCDE1001F") => new() {
        Id=id,ClientId=11,EmployeeCode=$"SYN{id:000}",Name=$"Synthetic Employee{id}",Pan=pan
    };

    [Fact] public void VerifiedExistingSalaryOnlyWriteRetainsAnExplicitBankReviewWithoutChangingData()
    {
        var employee=Master();var before=JsonSerializer.Serialize(employee);
        var bindings=ExcelPayrollSetupRepository.SalaryOnlyBankReviewRows(Batch(),Source(),[Identity()]);
        Assert.Contains("row-one",bindings);
        var issues=ExcelPayrollSetupRepository.FullIdentityIssues([("row-one",1)],Check(employee,employee),bindings);
        var issue=Assert.Single(issues);
        Assert.Equal("Review",issue.Severity);Assert.Equal("BANK_DATA_QUALITY",issue.Code);
        Assert.Contains("does not certify payment readiness",issue.Message);
        Assert.DoesNotContain(employee.PaymentDetails.BankAccountNo,issue.Message);
        Assert.Equal(before,JsonSerializer.Serialize(employee));
        Assert.Contains(EmployeeRepository.UnreliableBankIdentityMessage,Assert.Single(Check(employee,employee)).BlockingReasons);
    }

    [Theory]
    [InlineData("missing-proof")][InlineData("other-row")][InlineData("wrong-master")][InlineData("new")]
    public void MissingSourceBindingWrongIdentityOrNewEmployeesCannotDeferBankReview(string kind)
    {
        var employee=Master();var checks=Check(employee,employee);int target=1;
        IReadOnlySet<string>? binding=kind=="missing-proof" ? null : new HashSet<string>{kind=="other-row"?"other":"row-one"};
        if(kind=="wrong-master")checks[0]=checks[0] with {MatchedEmployeeId=2};
        if(kind=="new")target=0;
        Assert.Contains(ExcelPayrollSetupRepository.FullIdentityIssues([("row-one",target)],checks,binding),i=>i.Severity=="Error");
    }

    [Fact] public void ARealCompleteBankCollisionIsNeverADeferrableFormatWarning()
    {
        var a=Master(bank:"SYNTHETICBANK001");var b=Master(2,"SYNTHETICBANK001","ABCDE1002F");
        var issues=ExcelPayrollSetupRepository.FullIdentityIssues([("row-one",1)],Check(a,a,b),new HashSet<string>{"row-one"});
        Assert.Contains(issues,i=>i.Severity=="Error"&&i.Message.Contains("Bank Account"));
        Assert.DoesNotContain(issues,i=>i.Code=="BANK_DATA_QUALITY");
    }

    [Fact] public void ScientificBankReviewNeverClearsAnotherStrongIdentifierConflict()
    {
        var a=Master();var b=Master(2,pan:"ABCDE1001F");
        var issues=ExcelPayrollSetupRepository.FullIdentityIssues([("row-one",1)],Check(a,a,b),new HashSet<string>{"row-one"});
        Assert.Contains(issues,i=>i.Severity=="Error"&&i.Message.Contains("PAN"));
    }

    [Theory]
    [InlineData("name")][InlineData("pan")][InlineData("no-strong")][InlineData("duplicate-pan")]
    [InlineData("source-code-owner")][InlineData("wrong-client")][InlineData("missing-source-row")]
    public void SourceProofRequiresAnUnambiguousCurrentCodeOrStrongIdentifier(string kind)
    {
        var batch=Batch();var source=Source();var identity=Identity();var all=new List<ExcelPayrollSetupRepository.Identity>{identity};
        if(kind=="name")batch.Rows[0].EmployeeName="Different Employee";
        if(kind=="pan")source.Rows[0].Cells[0]!.Value="FGHIJ2002K";
        if(kind=="no-strong")source.Rows[0].Cells[0]!.Value="";
        if(kind=="duplicate-pan")all.Add(Identity(2));
        if(kind=="source-code-owner"){var other=Identity(2,"FGHIJ2002K");other.EmployeeCode="SOURCE001";all.Add(other);}
        if(kind=="wrong-client")identity.ClientId=12;
        if(kind=="missing-source-row")source.Rows.Clear();
        Assert.Empty(ExcelPayrollSetupRepository.SalaryOnlyBankReviewRows(batch,source,all));
    }

    [Fact] public void TwoSourceRowsForOneEmployeeNeverQualifyForTheNarrowSalaryPath()
    {
        var batch=Batch();var source=Source();
        batch.Rows.Add(new(){Id="row-two",SourceRow=10,EmployeeCode="SOURCE002",EmployeeName="Synthetic Employee1"});
        source.Rows.Add(new(){SourceRow=10,Cells=[new(){Value="ABCDE1001F"}]});
        Assert.Empty(ExcelPayrollSetupRepository.SalaryOnlyBankReviewRows(batch,source,[Identity()]));
    }

    [Fact] public void SalaryRosterShowsTheUnresolvedBankReviewAndPreservesAppliedHistory()
    {
        var roster=new ExcelPayrollRoster([new("row-one",9,"Synthetic Employee1","SYN001","Existing",1,"Matched"),new("old",8,"Old","SYN002","Applied",2,"Saved")],[]);
        var issue=new ExcelPayrollSetupIssue("Review",ExcelPayrollSetupRepository.BankDataReviewMessage,"row-one","BANK_DATA_QUALITY");
        var updated=ExcelPayrollSetupRepository.WithFullIdentityIssues(roster,[issue]);
        Assert.Equal("Existing",updated.Rows[0].Status);Assert.Contains("payment-data review",updated.Rows[0].Message);
        Assert.Equal("Saved",updated.Rows[1].Message);Assert.Equal("Matched",roster.Rows[0].Message);
        var result=new ExcelPayrollSetupResult{DataQualityReviews=[issue]};
        var saved=JsonSerializer.Deserialize<ExcelPayrollSetupResult>(JsonSerializer.Serialize(result))!;
        Assert.Equal("BANK_DATA_QUALITY",Assert.Single(saved.DataQualityReviews).Code);
    }
}
