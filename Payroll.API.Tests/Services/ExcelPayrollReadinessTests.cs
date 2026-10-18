using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollReadinessTests
{
    private static ExcelPayrollSetupRepository.Identity Employee(int id,string name,string location="Pune",int client=16) => new()
    { Id=id,ClientId=client,EmployeeCode=$"MNGL{id:000}",Name=name,WorkLocation=location,IsActive=true,EligibleForMonth=true };
    private static (ExcelPayslipBatch Batch,ExcelPayslipCalculationSource Source) Sheet(params string[] names) => (
        new() { ClientId=16,Month="2026-09",Rows=names.Select((name,i)=>new ExcelPayslipRow { Id=$"row-{i+2}",SourceRow=i+2,EmployeeName=name,EmployeeCode=$"MNGL{i+50:000}" }).ToList() },
        new() { HeaderRow=1,ColumnCount=2,Columns=[new() { ColumnIndex=0,SourceHeader="Name",Kind="employeeName" },new() { ColumnIndex=1,SourceHeader="Work Location (City name e.g. Pune, Nasik etc)",Kind="ignore" }],
            Rows=names.Select((name,i)=>new ExcelPayslipCalculationSourceRow { SourceRow=i+2,Cells=[new() {Value=name},new() {Value="Pune"}] }).ToList() });

    [Fact] public void RawHiddenLocationMatchesExistingWhileNewAndAmbiguousRowsStaySeparate()
    {
        var (batch,source)=Sheet("Existing Person","New Person","Duplicate Person","Applied Person");
        var identities=new[] { Employee(1,"Existing Person"),Employee(2,"Duplicate Person"),Employee(3,"Duplicate Person"),Employee(4,"Applied Person"),Employee(5,"Absent Person") };
        var result=ExcelPayrollSetupRepository.ClassifyRoster(batch,source,identities,new HashSet<string> {"row-5"});
        Assert.Equal(new[] {"Existing","New","Review","Applied"},result.Rows.Select(r=>r.Status));
        Assert.Equal("MNGL001",result.Rows[0].EmployeeCode);
        Assert.Equal(5,Assert.Single(result.MissingEmployees).EmployeeId);
        Assert.Equal("MNGL050",batch.Rows[0].EmployeeCode); // snapshot is immutable
    }

    [Fact] public void DuplicateSourceAssignmentsAndExitedEmployeesCannotJoinExistingCohort()
    {
        var (batch,source)=Sheet("Repeated Person","Repeated Person","Exited Person");
        var exited=Employee(2,"Exited Person");exited.EligibleForMonth=false;
        var result=ExcelPayrollSetupRepository.ClassifyRoster(batch,source,[Employee(1,"Repeated Person"),exited],new HashSet<string>());
        Assert.All(result.Rows,row=>Assert.Equal("Review",row.Status));
        Assert.Contains("Several source rows",result.Rows[0].Message);
    }

    [Fact] public void PartialPayslipBatchDoesNotTreatOtherEmployeesStillInTheOriginalWorkbookAsMissing()
    {
        var (batch,source)=Sheet("Selected Person","Source-only Person");
        batch.Rows.RemoveAt(1);
        var result=ExcelPayrollSetupRepository.ClassifyRoster(batch,source,[Employee(1,"Selected Person"),Employee(2,"Source-only Person")],new HashSet<string>());
        Assert.Empty(result.MissingEmployees);
    }

    [Theory]
    [InlineData("2026-09","2026-09-30")]
    [InlineData("2026-08","2026-08-31")]
    [InlineData("2028-02","2028-02-29")]
    [InlineData("2026-13",null)]
    [InlineData("9999-12",null)]
    [InlineData(null,null)]
    public void ExitDefaultUsesCalendarMonthEndOnlyWhenPayrollMonthIsKnown(string? month,string? expected) => Assert.Equal(expected,EmployeeRepository.PayrollMonthEnd(month));

    [Fact] public void LatestPayrollDefaultResolvesHiddenSourceIdentityWithoutUsingAnotherClientOrAmbiguousNames()
    {
        var (batch,source)=Sheet("Existing Person","Duplicate Person");
        var identities=new[] { Employee(1,"Existing Person"),Employee(2,"Duplicate Person"),Employee(3,"Duplicate Person"),Employee(4,"Existing Person",client:99) };
        var months=new Dictionary<int,string> { [1]="2026-08",[2]="2026-07" };
        EmployeeRepository.AccumulatePayrollMonths(batch,source,identities,new HashSet<int> {1,2,3,4},months);
        Assert.Equal("2026-09",months[1]);Assert.Equal("2026-07",months[2]);Assert.False(months.ContainsKey(3));Assert.False(months.ContainsKey(4));
        batch.Month="2026-06";
        EmployeeRepository.AccumulatePayrollMonths(batch,source,identities,new HashSet<int> {1},months);
        Assert.Equal("2026-09",months[1]);
    }

    [Fact] public void FixedPayrollDivisorIsReviewedWithoutAddingASourceColumn()
    {
        var request=new ExcelPayrollSetupRequest { BatchId=new string('a',32),RowIds=["row-2"],TemplateName="Fixed basis",EffectiveFrom="2026-09-01",WorkWeek="Sunday off",
            PayrollDaysColumnIndex=-1,FixedPayrollDays=26,PayableDaysColumnIndex=0,Components=[new() { Key="base",Code="TEST_BASIC",Name="Basic",SourceColumnIndex=1,ExpectedColumnIndex=2 }] };
        ExcelPayrollSetupRepository.ValidateRequest(request);
        var source=new ExcelPayslipCalculationSourceRow { SourceRow=2,Cells=[new() {Value="13"},new() {Value="26000"},new() {Value="13000"}] };
        var result=ExcelPayrollSetupRepository.CalculateRow(16,request,[new(request.Components[0],"101",true)],source,new() {Id="row-2",NetPay=13000});
        Assert.Equal(26,result.Preview.PayrollDays);Assert.Equal(13000,result.Preview.Net);Assert.Equal(312000,result.Preview.AnnualCtc);
        Assert.DoesNotContain(result.Issues,issue=>issue.Severity=="Error");
        request.PayrollDaysColumnIndex=1;
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateRequest(request));
    }

    [Fact] public void OldAppliedRequestCanReplayWithAddedDefaultFieldsButNotWithChangedFinancialSettings()
    {
        const string saved="{\"batchId\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"rowIds\":[\"row-2\"],\"templateName\":\"Old setup\",\"effectiveFrom\":\"2026-09-01\",\"payrollDaysColumnIndex\":3,\"payableDaysColumnIndex\":4}";
        var request=System.Text.Json.JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(saved,new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.True(ExcelPayrollSetupRepository.SameReviewedRequest(saved,request));
        request.FixedPayrollDays=26;
        Assert.False(ExcelPayrollSetupRepository.SameReviewedRequest(saved,request));
        request.FixedPayrollDays=null;request.ImportAttendance=false;
        Assert.False(ExcelPayrollSetupRepository.SameReviewedRequest(saved,request));
    }
}
