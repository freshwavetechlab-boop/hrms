using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class InitialBudgetMarkerTests
{
    private static Employee Master()=>new(){Id=41,ClientId=31,AnnualCtc=120000,SalaryStructureId="",SalaryJson="{}",SalaryComponents=[]};
    private static ExcelPayrollSetupRequest Request()=>new(){UpdateExistingConfiguration=true,ReviewedFixedInputs=true};
    private static EmployeeRepository.ExcelPayrollSalaryHistory Marker(decimal annual=120000,string salary="{}",string template="",string status="Active")=>
        new(){Id=51,EffectiveFrom=new(2026,11,15),AnnualCtc=annual,SalaryStructureId=template,SalaryJson=salary,Status=status};
    [Fact]
    public void Positive_budget_marker_requires_internal_review_without_changing_global_unconfigured_semantics()
    {
        var marker=Marker();var before=JsonSerializer.Serialize(marker);
        Assert.False(EmployeeRepository.IsUnconfiguredExcelSalaryHistory(marker));
        Assert.Throws<InvalidOperationException>(()=>EmployeeRepository.ExcelSalaryHistoryMarkers([marker],new(2026,10,1)));
        Assert.Equal([new DateTime(2026,11,15)],EmployeeRepository.ExcelSalaryHistoryMarkers([marker],new(2026,10,1),reviewedInitialBudgetAnnual:120000));
        Assert.Equal(before,JsonSerializer.Serialize(marker));
    }
    [Theory]
    [InlineData("template",120000,"{}","Active")][InlineData("",120001,"{}","Active")]
    [InlineData("",120000,"{\"101\":0}","Active")][InlineData("",120000,"[]","Active")]
    [InlineData("",120000,"null","Active")][InlineData("",120000,"","Active")]
    [InlineData("",120000,"malformed","Active")][InlineData("",120000,"{}","Pending")]
    public void Complete_different_or_unknown_future_rows_remain_blocked(string template,decimal annual,string salary,string status)
    {
        var error=Assert.Throws<InvalidOperationException>(()=>EmployeeRepository.ExcelSalaryHistoryMarkers([Marker(annual,salary,template,status)],new(2026,10,1),reviewedInitialBudgetAnnual:120000));
        Assert.Contains("later configured basic-pay record",error.Message);
    }
    [Fact]
    public void Existing_zero_empty_markers_and_default_request_bytes_are_unchanged()
    {
        var request=Request();var before=JsonSerializer.Serialize(request);var zero=Marker(0,"null");
        var legacy=EmployeeRepository.ExcelSalaryHistoryMarkers([zero],new(2026,10,1));
        Assert.Equal(legacy,EmployeeRepository.ExcelSalaryHistoryMarkers([zero],new(2026,10,1),reviewedInitialBudgetAnnual:120000));
        Assert.Equal(120000,ExcelPayrollSetupRepository.ReviewedInitialBudgetAnnual(request,Master(),120000,()=>120000));
        Assert.Equal(before,JsonSerializer.Serialize(request));Assert.DoesNotContain("reviewedInitialBudget",before,StringComparison.OrdinalIgnoreCase);
    }
    [Theory][InlineData(0)][InlineData(-1)][InlineData(120000.001)][InlineData(1000000001)]
    public void Invalid_reviewed_annual_does_not_authorize_history(decimal annual)=>
        Assert.Throws<InvalidOperationException>(()=>EmployeeRepository.ExcelSalaryHistoryMarkers([Marker()],new(2026,10,1),reviewedInitialBudgetAnnual:annual));
    [Fact]
    public void Initial_budget_authority_cannot_waive_correction_ownership()
    {
        var marker=Marker();marker.ChangeReason="original";
        Assert.Equal([new DateTime(2026,11,15)],EmployeeRepository.ExcelSalaryHistoryMarkers([marker],new(2026,10,1),"original"));
        Assert.Throws<InvalidOperationException>(()=>EmployeeRepository.ExcelSalaryHistoryMarkers([marker],new(2026,10,1),"original",120000));
    }
    [Fact]
    public void Mixed_zero_and_matching_budget_markers_keep_all_original_distinct_dates()
    {
        var zero=Marker(0);zero.EffectiveFrom=new(2026,11,1);var budget=Marker();var duplicate=Marker();duplicate.Status="Historical";
        Assert.Equal([new DateTime(2026,11,1),new DateTime(2026,11,15)],EmployeeRepository.ExcelSalaryHistoryMarkers([budget,zero,duplicate],new(2026,10,1),reviewedInitialBudgetAnnual:120000));
    }
    [Theory]
    [InlineData("no-update")][InlineData("no-fixed-review")][InlineData("correction")][InlineData("location-parts")]
    [InlineData("new-master")][InlineData("foreign-scope")][InlineData("template")][InlineData("input-zero")]
    [InlineData("nonempty-json")][InlineData("unknown-json")][InlineData("zero-budget")][InlineData("planned-different")]
    public void Only_original_empty_existing_master_and_existing_review_flags_can_request_independent_proof(string change)
    {
        var request=Request();var master=Master();var planned=120000m;var calls=0;
        switch(change){case"no-update":request.UpdateExistingConfiguration=false;break;case"no-fixed-review":request.ReviewedFixedInputs=false;break;
            case"correction":request.CorrectionOfImportId=new string('a',32);break;case"location-parts":request.LocationAllocations=new();break;
            case"new-master":master.Id=0;break;case"foreign-scope":master.ClientId=0;break;case"template":master.SalaryStructureId="existing";break;
            case"input-zero":master.SalaryComponents["101"]=0;break;case"nonempty-json":master.SalaryJson="{\"101\":0}";break;
            case"unknown-json":master.SalaryJson="null";break;case"zero-budget":master.AnnualCtc=0;break;case"planned-different":planned++;break;}
        Assert.Null(ExcelPayrollSetupRepository.ReviewedInitialBudgetAnnual(request,master,planned,()=>{calls++;return 120000;}));Assert.Equal(0,calls);
    }
    [Theory][InlineData(0)][InlineData(119999)][InlineData(120001)]
    public void Matching_retained_target_is_not_a_substitute_for_independently_matching_source_budget(decimal sourceAnnual)
    {
        var calls=0;Assert.Null(ExcelPayrollSetupRepository.ReviewedInitialBudgetAnnual(Request(),Master(),120000,()=>{calls++;return sourceAnnual;}));Assert.Equal(1,calls);
    }
    [Fact]
    public void Unavailable_independent_budget_stays_held_instead_of_reusing_current_target()=>
        Assert.Null(ExcelPayrollSetupRepository.ReviewedInitialBudgetAnnual(Request(),Master(),120000,()=>throw new InvalidOperationException("Unproven source budget")));
}
