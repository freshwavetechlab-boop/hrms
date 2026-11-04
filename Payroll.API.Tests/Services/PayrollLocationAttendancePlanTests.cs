using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public class PayrollLocationAttendancePlanTests
{
    private static PayrollLocationAttendanceRow Row(string key,int location,decimal? days=null,bool native=true)=>new()
    {Key=key,EmployeeId=1,EmployeeCode="TEST001",WorkLocationId=location,TemplateId="T",NativePart=native,
        PayrollDays=30,MonthlyPayrollDays=30,PresentDays=days,PayableDays=days,
        InputVersion=PayrollLocationAttendancePlan.InputVersion(key,30,days,days)};
    private static PayrollLocationAttendanceContext Context(params PayrollLocationAttendanceRow[] rows)=>new()
    {ClientId=7,Month="2026-09",PolicyBatchId="policy",GroupIds=[3],Version="configuration",StorageAvailable=true,Rows=rows.ToList()};
    private static PayrollLocationAttendanceRequest Request(PayrollLocationAttendanceContext context,string key,decimal days)=>new()
    {RequestId=Guid.NewGuid().ToString("N"),ClientId=7,Month="2026-09",PolicyBatchId="policy",GroupIds=[3],ContextVersion=context.Version,
        Rows=[new(key,1,context.Rows.Single(r=>r.Key==key).WorkLocationId,30,days,days,context.Rows.Single(r=>r.Key==key).InputVersion)]};

    [Fact] public void First_location_stays_pending_without_manufacturing_the_other_locations_days()
    {
        var context=Context(Row("A",10),Row("B",20));var before=JsonSerializer.Serialize(context);
        var plan=PayrollLocationAttendancePlan.Build(context,Request(context,"A",17));
        Assert.Single(plan.Changed);Assert.Empty(plan.Publish);Assert.Equal(1,plan.PendingEmployees);
        Assert.Null(context.Rows[1].PresentDays);Assert.Equal(before,JsonSerializer.Serialize(context));
    }
    [Fact] public void Second_location_completes_one_employee_total_without_replacing_first_location()
    {
        var context=Context(Row("A",10,17),Row("B",20));var plan=PayrollLocationAttendancePlan.Build(context,Request(context,"B",13));
        var employee=Assert.Single(plan.Publish);Assert.Equal(2,employee.Count());Assert.Equal(30,employee.Sum(r=>r.PayableDays));Assert.Equal(0,plan.PendingEmployees);
    }
    [Fact] public void Independent_location_upload_does_not_require_a_new_template_after_another_part_was_saved()
    {
        var downloaded=Context(Row("A",10),Row("B",20));var request=Request(downloaded,"B",13);
        var refreshed=Context(Row("A",10,17),Row("B",20));
        Assert.Single(PayrollLocationAttendancePlan.Build(refreshed,request).Publish);
    }
    [Fact] public void Changed_same_location_input_is_a_conflict_even_when_salary_configuration_is_unchanged()
    {
        var downloaded=Context(Row("A",10),Row("B",20));var request=Request(downloaded,"A",17);
        var refreshed=Context(Row("A",10,16),Row("B",20));
        Assert.Contains("row changed",Assert.Throws<InvalidOperationException>(()=>PayrollLocationAttendancePlan.Build(refreshed,request)).Message);
    }
    [Fact] public void Ordinary_one_location_and_explicit_zero_keep_the_existing_monthly_input_contract()
    {
        var context=Context(Row("employee:1",10,native:false));var plan=PayrollLocationAttendancePlan.Build(context,Request(context,"employee:1",0));
        Assert.Single(plan.Publish);Assert.Equal(0,plan.Changed[0].PayableDays);Assert.False(plan.Changed[0].NativePart);
    }
    [Theory][InlineData("client")][InlineData("month")][InlineData("policy")][InlineData("group")][InlineData("configuration")]
    public void Foreign_or_stale_scope_never_becomes_attendance(string mismatch)
    {
        var context=Context(Row("A",10));var request=Request(context,"A",17);
        if(mismatch=="client")request.ClientId=8;if(mismatch=="month")request.Month="2026-10";
        if(mismatch=="policy")request.PolicyBatchId="other";if(mismatch=="group")request.GroupIds=[4];if(mismatch=="configuration")request.ContextVersion="old";
        Assert.Throws<InvalidOperationException>(()=>PayrollLocationAttendancePlan.Build(context,request));
    }
    [Theory][InlineData("location")][InlineData("employee")][InlineData("duplicate")][InlineData("locked")][InlineData("issue")][InlineData("overflow")]
    public void Unsafe_rows_are_rejected_before_a_publish_plan(string problem)
    {
        var context=Context(Row("A",10),Row("B",20,13));var request=Request(context,"A",17);
        if(problem=="location")request.WorkLocationId=20;
        if(problem=="employee")request.Rows[0]=request.Rows[0] with{EmployeeId=2};
        if(problem=="duplicate")request.Rows.Add(request.Rows[0]);
        if(problem=="locked")context.Rows[0].LockedPayRunId=81;
        if(problem=="issue")context.Rows[0].Issue="Review formula version";
        if(problem=="overflow")request.Rows[0]=request.Rows[0] with{PresentDays=18,PayableDays=18};
        Assert.Throws<InvalidOperationException>(()=>PayrollLocationAttendancePlan.Build(context,request));
    }
    [Fact] public void Missing_json_days_cannot_be_deserialized_as_explicit_zero()
    {
        Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<PayrollLocationAttendanceInput>("{\"Key\":\"A\",\"EmployeeId\":1,\"WorkLocationId\":10,\"PayrollDays\":30}"));
    }
}
