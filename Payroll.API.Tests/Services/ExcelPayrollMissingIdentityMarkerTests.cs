using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollMissingIdentityMarkerTests
{
    [Theory]
    [InlineData("N/A")][InlineData("-")][InlineData("0")][InlineData("000000000000")]
    public void Missing_markers_are_not_identifiers_and_blank_codes_need_independent_unique_anchors(string marker)
    {
        var batch=new ExcelPayslipBatch {Id=new('a',32),ClientId=11,Month="2026-10",Rows=[
            new(){Id="a",SourceRow=2,EmployeeCode="",EmployeeName="Example One"},
            new(){Id="b",SourceRow=3,EmployeeCode="",EmployeeName="Example Two"}]};
        var source=new ExcelPayslipCalculationSource {ColumnCount=2,Columns=[new(){ColumnIndex=0,SourceHeader="PAN No"},new(){ColumnIndex=1,SourceHeader="Aadhaar"}],Rows=[
            new(){SourceRow=2,Cells=[new(){Value=marker},new(){Value="234567890121"}]},
            new(){SourceRow=3,Cells=[new(){Value=marker},new(){Value="234567890122"}]}]};
        ExcelPayslipRepository.EmploymentMaster[] masters=[
            new(){Id=1,ClientId=11,EmployeeCode="NATIVE001",FirstName="Example",LastName="One",Pan="-",Aadhaar="234567890121",IsActive=true,Eligible=true},
            new(){Id=2,ClientId=11,EmployeeCode="NATIVE002",FirstName="Example",LastName="Two",Pan="-",Aadhaar="234567890122",IsActive=true,Eligible=true}];
        ExcelPayslipRepository.AnnotateEmployment(batch,source,masters,[]);
        Assert.Equal(1,batch.Rows[0].Employment!.EmployeeId);Assert.Equal(2,batch.Rows[1].Employment!.EmployeeId);
        Assert.All(batch.Rows,row=>Assert.False(row.Employment!.IdentityReviewRequired));
        Assert.All(batch.Rows,row=>Assert.Equal("",row.EmployeeCode));
        source.Rows[0].Cells[1]!.Value="";
        ExcelPayslipRepository.AnnotateEmployment(batch,source,masters,[]);
        Assert.True(batch.Rows[0].Employment is null || batch.Rows[0].Employment!.IdentityReviewRequired);
    }
    [Fact]
    public void A_real_conflicting_identifier_is_still_a_review_error()
    {
        var issues=new List<ExcelPayrollSetupIssue>();
        var master=new ExcelPayrollSetupRepository.Identity {Id=7,ClientId=11,EmployeeCode="NATIVE007",Name="Example Person",Pan="ABCDE1234F",Aadhaar="234567890123"};
        ExcelPayrollSetupRepository.MatchIdentity(new(){Id="a",EmployeeName="Example Person",EmployeeCode=""},
            new(){{"PAN","BCDEF2345G"},{"AADHAAR","234567890123"}},[master],null,issues);
        Assert.Contains(issues,issue=>issue.Severity=="Error" && issue.Message.Contains("conflicts"));
    }
}
