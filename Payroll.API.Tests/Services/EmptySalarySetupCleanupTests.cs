using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class EmptySalarySetupCleanupTests
{
    private static Employee Employee() => new() { Id=71, ClientId=9, EmployeeCode="SYNTH-71",
        SalaryStructureId="501", AnnualCtc=0, SalaryJson="{\"101\":13000}" };
    private static List<EmployeeRepository.ClosedSalaryHistoryRow> History() => [
        new() { Id=1, EmployeeId=71, ClientId=9, EffectiveFrom=new(2026,10,1), Status="Active",
            SalaryStructureId="501", AnnualCtc=0, SalaryJson="{\"101\":13000}" },
        new() { Id=2, EmployeeId=71, ClientId=9, EffectiveFrom=new(2026,10,6), Status="Active",
            SalaryStructureId="", AnnualCtc=0, SalaryJson="{}", ActionType="Hire" },
        new() { Id=3, EmployeeId=71, ClientId=9, EffectiveFrom=new(2026,9,1), EffectiveTo=new(2026,9,30),
            Status="Historical", SalaryStructureId="490", AnnualCtc=0, SalaryJson="{\"101\":12000}" }
    ];
    [Theory]
    [InlineData("12636",true)]
    [InlineData("12636.0",true)]
    [InlineData("12636.0000",true)]
    [InlineData("12636.0001",false)]
    public void Database_decimal_scale_is_not_a_salary_change_but_real_precision_still_blocks(string amount,bool canApply)
    {
        var employee=Employee();var history=History();
        employee.SalaryJson="{\"101\":"+amount+"}";
        history[0].SalaryJson="{\"101\":12636}";
        var before=JsonSerializer.Serialize(new{employee,history});
        var plan=EmployeeRepository.PlanEmptySalaryCleanup(employee,history,"2026-10",new(2026,11,20),false);
        Assert.Equal(canApply,plan.Preview.CanApply);
        Assert.Equal(before,JsonSerializer.Serialize(new{employee,history}));
    }
    [Fact]
    public void Only_later_empty_active_markers_in_the_reviewed_month_are_selected_without_mutation()
    {
        var employee=Employee();var history=History();var before=JsonSerializer.Serialize(new{employee,history});
        var plan=EmployeeRepository.PlanEmptySalaryCleanup(employee,history,"2026-10",new(2026,11,20),false);
        Assert.True(plan.Preview.CanApply);Assert.Equal(1,plan.Basis!.Id);
        Assert.Equal(["2026-10-06"],plan.Preview.EmptyEffectiveDates);Assert.Equal(2,Assert.Single(plan.Empty).Id);
        Assert.Equal(before,JsonSerializer.Serialize(new{employee,history}));
    }
    [Theory]
    [InlineData("saved-payroll")][InlineData("current-changed")][InlineData("later-salary")][InlineData("earlier-active")]
    public void Configured_or_earlier_salary_and_saved_payroll_are_not_cleaned(string scenario)
    {
        var employee=Employee();var history=History();
        if(scenario=="current-changed")employee.SalaryJson="{\"101\":14000}";
        if(scenario=="later-salary")history.Add(new(){Id=4,EmployeeId=71,ClientId=9,EffectiveFrom=new(2026,10,10),Status="Active",SalaryStructureId="501",SalaryJson="{\"101\":14000}"});
        if(scenario=="earlier-active")history[2].Status="Active";
        var plan=EmployeeRepository.PlanEmptySalaryCleanup(employee,history,"2026-10",new(2026,11,20),scenario=="saved-payroll");
        Assert.False(plan.Preview.CanApply);Assert.NotEmpty(plan.Preview.Blockers);
    }
    [Fact]
    public void Cross_client_future_month_and_nonempty_markers_cannot_be_silently_repaired()
    {
        var history=History();history[1].ClientId=10;
        Assert.Throws<InvalidOperationException>(()=>EmployeeRepository.PlanEmptySalaryCleanup(Employee(),history,"2026-10",new(2026,11,20),false));
        Assert.Throws<InvalidOperationException>(()=>EmployeeRepository.PlanEmptySalaryCleanup(Employee(),History(),"2026-11",new(2026,11,20),false));
        history=History();history[1].AnnualCtc=1;
        Assert.False(EmployeeRepository.PlanEmptySalaryCleanup(Employee(),history,"2026-10",new(2026,11,20),false).Preview.CanApply);
    }
    [Fact]
    public void A_changed_history_witness_invalidates_the_preview_and_other_month_markers_stay_outside()
    {
        var history=History();var employee=Employee();
        var first=EmployeeRepository.PlanEmptySalaryCleanup(employee,history,"2026-10",new(2026,11,20),false);
        history.Add(new(){Id=4,EmployeeId=71,ClientId=9,EffectiveFrom=new(2026,11,1),Status="Active",SalaryJson="{}"});
        var next=EmployeeRepository.PlanEmptySalaryCleanup(employee,history,"2026-10",new(2026,11,20),false);
        Assert.NotEqual(first.Preview.PlanHash,next.Preview.PlanHash);Assert.Equal(2,Assert.Single(next.Empty).Id);
    }
    [Fact]
    public void Ambiguous_configured_month_start_entries_are_preserved_for_review()
    {
        var history=History();history.Add(new(){Id=5,EmployeeId=71,ClientId=9,EffectiveFrom=new(2026,10,1),Status="Active",SalaryStructureId="501",SalaryJson="{\"101\":13000}"});
        Assert.False(EmployeeRepository.PlanEmptySalaryCleanup(Employee(),history,"2026-10",new(2026,11,20),false).Preview.CanApply);
    }
}
