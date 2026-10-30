using Payroll.API.Models;
using Payroll.API.Repositories;
using Xunit;

namespace Payroll.API.Tests.Services;

public sealed class ExcelSalaryCorrectionScopeTests
{
    [Theory]
    [InlineData(true,1,"Monthly","Monthly snapshot","Monthly",true)]
    [InlineData(false,1,"Monthly","Monthly snapshot","Monthly",false)]
    [InlineData(true,2,"Monthly","Monthly snapshot","Monthly",false)]
    [InlineData(true,1,"DateWise","Monthly snapshot","Monthly",false)]
    [InlineData(true,1,"Monthly","Legacy daily aggregate","Daily",false)]
    public void Recorded_monthly_financial_inputs_require_the_explicit_correction_branch(bool explicitCorrection,int groups,string mode,string source,string type,bool allowed)
    {
        var row=new PayRunRepository.PayRunAttendance{WorkingDays=30,PresentDays=26,PayableDays=30,InputSource=source,SourceType=type};
        Assert.Equal(allowed,PayRunRepository.CanUseRecordedMonthlySalaryInput(explicitCorrection,row,groups,mode));
        row.PolicyError="Unresolved policy";
        Assert.False(PayRunRepository.CanUseRecordedMonthlySalaryInput(explicitCorrection,row,groups,mode));
    }
    private static (ExcelPayrollSetupRequest Request,ExcelPayrollSetupRequest Parent,ExcelPayrollSetupResult Receipt) Fixture()
    {
        var q=new ExcelPayrollSetupRequest{BatchId=new string('a',32),CorrectionOfImportId=new string('b',32),TransferReviewVersion=1,
            TemplateScopeReviewVersion=1,RowIds=["row"],ImportAttendance=false,CreateMissingDropdownValues=false,
            EmployeeDecisions=[new(){RowId="row",MatchedEmployeeId=11}],Components=[new(){ExistingComponentId="1"}],
            AttendanceReview=new(){Mode="VerifyExistingNative",Reviewed=true}};
        var parent=new ExcelPayrollSetupRequest{BatchId=q.BatchId,RowIds=["row","other"],EmployeeDecisions=[new(){RowId="row",MatchedEmployeeId=11}]};
        var receipt=new ExcelPayrollSetupResult{ImportId=q.CorrectionOfImportId,EmployeeIds=[11,12]};return(q,parent,receipt);
    }

    [Fact]
    public void Explicit_single_row_correction_retains_its_parent_without_claiming_another_payroll()
    {
        var(q,parent,receipt)=Fixture();var before=System.Text.Json.JsonSerializer.Serialize(new{parent,receipt});
        Assert.Null(ExcelPayrollSetupRepository.SalaryCorrectionScopeError(q,new HashSet<string>{"row"},parent,receipt,false));
        Assert.Equal(before,System.Text.Json.JsonSerializer.Serialize(new{parent,receipt}));
    }

    [Theory]
    [InlineData("no-parent")] [InlineData("new-row")] [InlineData("many-rows")] [InlineData("attendance")]
    [InlineData("payable-override")] [InlineData("new-masters")] [InlineData("new-locations")]
    [InlineData("new-component")] [InlineData("unverified-native")][InlineData("wrong-parent")]
    [InlineData("other-employee")] [InlineData("new-employee")] [InlineData("kept-only")][InlineData("prior-payroll")]
    public void Correction_never_bypasses_existing_workflow_or_replaces_payroll(string change)
    {
        var(q,parent,receipt)=Fixture();var applied=new HashSet<string>{"row"};var payroll=false;
        switch(change){case "no-parent":q.CorrectionOfImportId=null;break;case "new-row":applied.Clear();break;
            case "many-rows":q.RowIds.Add("other");break;case "attendance":q.ImportAttendance=true;break;
            case "payable-override":q.OverrideSourcePayableAttendance=true;break;case "new-masters":q.CreateMissingDropdownValues=true;break;
            case "new-locations":q.CreateMissingWorkLocations=true;break;case "new-component":q.Components[0].ExistingComponentId="";break;
            case "unverified-native":q.AttendanceReview!.Reviewed=false;break;case "wrong-parent":receipt.ImportId=new string('c',32);break;
            case "other-employee":parent.EmployeeDecisions[0].MatchedEmployeeId=12;break;case "new-employee":q.EmployeeDecisions[0].AllowCreate=true;break;
            case "kept-only":receipt.KeptRows.Add(new("row",11,"1","Kept"));break;case "prior-payroll":payroll=true;break;}
        Assert.NotNull(ExcelPayrollSetupRepository.SalaryCorrectionScopeError(q,applied,parent,receipt,payroll));
    }
}
