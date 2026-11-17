using System.Text.Json.Nodes;
using Payroll.API.Repositories;
namespace Payroll.API.Tests.Services;

public sealed class SingleSalaryTemplateAppendTests
{
    [Fact]
    public void Exact_one_owned_append_preserves_every_existing_section_and_row()
    {
        var before=JsonNode.Parse("{\"salaryStructures\":[{\"id\":1,\"clientId\":\"10\",\"lines\":[]}],\"salaryComponents\":[{\"id\":2}],\"tax\":{\"enabled\":true},\"payslipTemplates\":[{\"id\":3}],\"schedule\":{\"days\":30},\"unknown\":{\"keep\":true}}")!.AsObject();
        var after=before.DeepClone().AsObject();after["salaryStructures"]!.AsArray().Add(new JsonObject{["id"]=4,["clientId"]="20",["lines"]=new JsonArray()});
        PayrollDataTableStore.RequireSingleSalaryStructureAppend(before,after,20);
    }
    [Theory]
    [InlineData("otherClient")][InlineData("shared")][InlineData("oldEdit")][InlineData("remove")][InlineData("extra")][InlineData("component")][InlineData("unknown")][InlineData("reorder")][InlineData("duplicate")]
    public void The_append_guard_rejects_foreign_shared_sparse_or_concurrent_other_edits(string mutation)
    {
        var before=JsonNode.Parse("{\"salaryStructures\":[{\"id\":1,\"clientId\":\"10\"},{\"id\":2,\"clientId\":\"20\"}],\"salaryComponents\":[{\"id\":2}],\"unknown\":true}")!.AsObject();
        var after=before.DeepClone().AsObject();var rows=after["salaryStructures"]!.AsArray();rows.Add(new JsonObject{["id"]=3,["clientId"]="20"});
        switch(mutation){case "otherClient":rows[2]!["clientId"]="30";break;case "shared":rows[2]!["clientId"]="20,30";break;case "oldEdit":rows[0]!["name"]="Changed";break;case "remove":rows.RemoveAt(0);break;case "extra":rows.Add(new JsonObject{["id"]=4,["clientId"]="20"});break;case "component":after["salaryComponents"]![0]!["id"]=9;break;case "unknown":after["unknown"]=false;break;case "reorder":var first=rows[0]!.DeepClone();rows.RemoveAt(0);rows.Insert(1,first);break;default:rows[2]!["id"]=1;break;}
        Assert.Throws<InvalidOperationException>(()=>PayrollDataTableStore.RequireSingleSalaryStructureAppend(before,after,20));
    }
}
