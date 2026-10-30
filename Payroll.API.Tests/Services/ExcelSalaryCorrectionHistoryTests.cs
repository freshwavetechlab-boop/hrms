using Payroll.API.Repositories;
using Xunit;

namespace Payroll.API.Tests.Services;

public sealed class ExcelSalaryCorrectionHistoryTests
{
    private const string Parent = "Reviewed salary Excel synthetic-batch, row 19, import synthetic-parent";
    private static readonly DateTime From = new(2026,9,1);
    private static EmployeeRepository.ExcelPayrollSalaryHistory Row(int day, bool configured = true,
        string status = "Active", string reason = Parent) => new()
    {
        Id=day,EffectiveFrom=new(2026,9,day),Status=status,ChangeReason=reason,
        SalaryStructureId=configured ? "original-template" : "",AnnualCtc=configured ? 120000 : 0,
        SalaryJson=configured ? "{\"BASIC\":10000}" : "{}"
    };

    [Fact]
    public void Corrected_versions_retain_later_source_dates_so_period_lookup_cannot_choose_old_rules()
    {
        var old=Row(1,status:"Historical"); var marker=Row(20,false,"Historical","initial setup"); var active=Row(20);
        var history=new[]{old,marker,active}; var before=System.Text.Json.JsonSerializer.Serialize(history);
        var dates=EmployeeRepository.ExcelSalaryHistoryMarkers(history,From,Parent);
        Assert.Equal(new[]{new DateTime(2026,9,20)},dates);
        Assert.Equal(before,System.Text.Json.JsonSerializer.Serialize(history));
        // Mirrors the existing core period resolver's effective-date then ID ordering.
        var retained=history.Select(r=>(r.EffectiveFrom,Id:r.Id,Template:r.SalaryStructureId));
        var corrected=dates.Prepend(From).Select((d,i)=>(EffectiveFrom:d,Id:100L+i,Template:"corrected-template"));
        Assert.Equal("corrected-template",retained.Concat(corrected).OrderByDescending(r=>r.EffectiveFrom).ThenByDescending(r=>r.Id).First().Template);
    }

    [Theory]
    [InlineData("Active",1)] [InlineData("Active",20)] [InlineData("Historical",20)]
    public void Other_configured_history_is_never_silently_replaced(string status,int day)
    {
        var row=Row(day,status:status,reason:"manual salary change");
        Assert.Throws<InvalidOperationException>(()=>EmployeeRepository.ExcelSalaryHistoryMarkers([row],From,Parent));
        Assert.Equal("original-template",row.SalaryStructureId);
    }

    [Fact]
    public void Historical_empty_markers_are_retained_without_becoming_new_salary_dates()
    {
        Assert.Empty(EmployeeRepository.ExcelSalaryHistoryMarkers([Row(20,false,"Historical","initial")],From,Parent));
        Assert.Equal(new[]{new DateTime(2026,9,20)},EmployeeRepository.ExcelSalaryHistoryMarkers([Row(20,false,"Active","initial")],From,Parent));
    }

    [Fact]
    public void Normal_import_keeps_its_existing_future_history_guard()
    {
        Assert.Throws<InvalidOperationException>(()=>EmployeeRepository.ExcelSalaryHistoryMarkers([Row(20)],From));
        Assert.Equal(new[]{new DateTime(2026,9,20)},EmployeeRepository.ExcelSalaryHistoryMarkers([Row(20,false,"Historical","initial")],From));
    }

    [Theory]
    [InlineData(Parent,true)] [InlineData(Parent+"; retained marker",true)]
    [InlineData(Parent+"-other",false)] [InlineData("manual "+Parent,false)] [InlineData("",false)]
    public void Ownership_requires_the_exact_server_computed_source_reference(string reason,bool expected)
        => Assert.Equal(expected,EmployeeRepository.OwnsExcelSalaryHistory(Row(20,reason:reason),Parent));

    [Fact]
    public void Empty_correction_reference_cannot_waive_the_guard()
        => Assert.Throws<InvalidOperationException>(()=>EmployeeRepository.ExcelSalaryHistoryMarkers([Row(20)],From,""));
}
