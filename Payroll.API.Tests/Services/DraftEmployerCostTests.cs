using System.Text.Json;
using System.Text.Json.Nodes;
using Payroll.API.Models;
using Payroll.API.Repositories;
namespace Payroll.API.Tests.Services;

public class DraftEmployerCostTests
{
    private static DraftEmployerCostRequest Request()=>new(9,"2026-09",900,1000,true,[new(11,"EPF_ER",12.34m,"source-row-1","Reviewed source extra duty")]);
    [Fact]public void EmployerCostImportRequiresExplicitScopeMoneyReferenceAndConfirmation()
    {
        PayRunRepository.ValidateEmployerCosts(Request());
        foreach(var request in new[]{Request() with{Confirmed=false},Request() with{ClientId=0},Request() with{PayPeriod="2026-99"},Request() with{Rows=[]},Request() with{Rows=[Request().Rows[0],Request().Rows[0]]},Request() with{Rows=[Request().Rows[0] with{Amount=-1}]},Request() with{Rows=[Request().Rows[0] with{Amount=1.001m}]},Request() with{Rows=[Request().Rows[0] with{Reference=""}]}})
            Assert.Throws<InvalidOperationException>(()=>PayRunRepository.ValidateEmployerCosts(request));
    }
    [Fact]public void RebuildingEmployerDetailsRetainsCashLinesAndReplacesOnlyOwnedPeriodCosts()
    {
        const string original="[{\"Id\":\"WAGE\",\"Category\":\"Earning\",\"amount\":1000},{\"Id\":\"EPF_ER\",\"Category\":\"Benefit\",\"amount\":130},{\"Id\":\"NET_PAY\",\"Category\":\"Summary\",\"amount\":900}]";
        var costs=new[]{new PayrollAdjustment{Id=1,ComponentCode="EPF_ER",ComponentName="Extra employer PF",AdjustmentType="Benefit",Amount=20},new PayrollAdjustment{Id=2,ComponentCode="EPF_ER",ComponentName="Extra employer PF",AdjustmentType="Benefit",Amount=10}};
        var first=PayRunRepository.EmployerCostDetails(original,costs);var second=PayRunRepository.EmployerCostDetails(first,costs);
        Assert.Equal(first,second);
        var before=JsonNode.Parse(original)!.AsArray();var after=JsonNode.Parse(second)!.AsArray();
        Assert.Equal(4,after.Count);for(var i=0;i<3;i++)Assert.True(JsonNode.DeepEquals(before[i],after[i]));
        Assert.Equal("Benefit",after[3]!["Category"]!.GetValue<string>());Assert.Equal(30,after[3]!["amount"]!.GetValue<decimal>());
    }
}
