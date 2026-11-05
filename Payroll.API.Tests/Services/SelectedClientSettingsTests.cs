using System.Text.Json.Nodes;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public class SelectedClientSettingsTests
{
    private static JsonObject Current() => JsonNode.Parse("""
    {"schedule":{"fixedDays":"26"},"statutory":{"epf":true},"salaryComponents":[{"id":7,"name":"Basic"}],
     "salaryStructures":[
      {"id":1,"clientId":"55","name":"Own","annualCtc":"120000","lines":[]},
      {"id":2,"clientId":"20,55","name":"Shared","annualCtc":"240000","lines":[]},
      {"id":3,"clientId":"20","name":"Other","annualCtc":"360000","lines":[]},
      {"id":4,"clientId":"0","name":"Global","annualCtc":"0","lines":[]}],
     "payslipTemplates":[{"id":11,"clientId":"55","name":"Own"},{"id":12,"clientId":"55,20","name":"Shared"},{"id":13,"clientId":"20","name":"Other"}],
     "tax":{"slabs":[{"id":99}],"clientSettings":[{"id":101,"clientId":"55","defaultRegime":"New"},{"id":102,"clientId":"20","defaultRegime":"Old"}]}}
    """)!.AsObject();
    private static JsonObject Find(JsonObject root, string property, long id) => root[property]!.AsArray().OfType<JsonObject>().Single(row => SettingsRepository.ReadRowId(row) == id);

    [Fact]
    public void ProjectionShowsApplicableDefinitionsWithoutOtherClientReferences()
    {
        var current = Current(); var original = current.ToJsonString();
        var view = SettingsRepository.ProjectSelectedClientSetup(current, 55);
        Assert.Equal(new long[] { 1, 2, 4 }, view["salaryStructures"]!.AsArray().OfType<JsonObject>().Select(SettingsRepository.ReadRowId));
        Assert.All(view["salaryStructures"]!.AsArray(), row => Assert.Equal("55", row!["clientId"]!.ToString()));
        Assert.Single(view["tax"]!["clientSettings"]!.AsArray());
        Assert.Equal(original, current.ToJsonString());
    }

    [Fact]
    public void UnchangedProjectionPreservesSharedReferencesAndAllOriginalValues()
    {
        var current = Current(); var view = SettingsRepository.ProjectSelectedClientSetup(current, 55);
        var result = SettingsRepository.MergeSelectedClientSetup(current, view, 55);
        Assert.True(JsonNode.DeepEquals(current, result));
        Assert.Equal("20,55", Find(result, "salaryStructures", 2)["clientId"]!.ToString());
    }

    [Fact]
    public void OwnUpdateAndDeletePreserveForeignSharedAndGlobalSettings()
    {
        var current = Current(); var view = SettingsRepository.ProjectSelectedClientSetup(current, 55);
        Find(view, "salaryStructures", 1)["annualCtc"] = "144000";
        view["payslipTemplates"]!.AsArray().Remove(Find(view, "payslipTemplates", 11));
        view["salaryComponents"] = new JsonArray(); view["schedule"]!["fixedDays"] = "999"; view["statutory"]!["epf"] = false;
        var result = SettingsRepository.MergeSelectedClientSetup(current, view, 55);
        Assert.Equal("144000", Find(result, "salaryStructures", 1)["annualCtc"]!.ToString());
        Assert.DoesNotContain(result["payslipTemplates"]!.AsArray().OfType<JsonObject>(), row => SettingsRepository.ReadRowId(row) == 11);
        foreach (var id in new long[] { 2, 3, 4 }) Assert.True(JsonNode.DeepEquals(Find(current, "salaryStructures", id), Find(result, "salaryStructures", id)));
        Assert.True(JsonNode.DeepEquals(current["schedule"], result["schedule"]));
        Assert.True(JsonNode.DeepEquals(current["salaryComponents"], result["salaryComponents"]));
        Assert.True(JsonNode.DeepEquals(current["statutory"], result["statutory"]));
    }

    [Fact]
    public void SharedChangesAndDeletionRequireUnrestrictedAdministration()
    {
        foreach (var delete in new[] { false, true })
        {
            var current = Current(); var view = SettingsRepository.ProjectSelectedClientSetup(current, 55); var shared = Find(view, "salaryStructures", 2);
            if (delete) view["salaryStructures"]!.AsArray().Remove(shared); else shared["annualCtc"] = "1";
            Assert.Throws<InvalidOperationException>(() => SettingsRepository.MergeSelectedClientSetup(current, view, 55));
        }
    }

    [Fact]
    public void ForeignIdsAndInvalidClientReferencesCannotBeRebound()
    {
        foreach (var addition in new[] { "{\"id\":3,\"clientId\":\"55\"}", "{\"Id\":3,\"ClientId\":\"55\"}", "{\"id\":8,\"clientId\":\"20,55\"}", "{\"id\":8,\"clientId\":\"55,invalid\"}" })
        {
            var current = Current(); var view = SettingsRepository.ProjectSelectedClientSetup(current, 55); view["salaryStructures"]!.AsArray().Add(JsonNode.Parse(addition));
            Assert.Throws<InvalidOperationException>(() => SettingsRepository.MergeSelectedClientSetup(current, view, 55));
        }
    }

    [Fact]
    public void OmittedCollectionsRemainUntouchedAndNewOwnIdsCanBeAdded()
    {
        var current = Current();
        Assert.True(JsonNode.DeepEquals(current, SettingsRepository.MergeSelectedClientSetup(current, new JsonObject(), 55)));
        var view = SettingsRepository.ProjectSelectedClientSetup(current, 55);
        view["salaryStructures"]!.AsArray().Add(JsonNode.Parse("{\"id\":8,\"clientId\":\"55\",\"name\":\"New own\",\"annualCtc\":\"0\",\"lines\":[]}"));
        var result = SettingsRepository.MergeSelectedClientSetup(current, view, 55);
        Assert.Equal("55", Find(result, "salaryStructures", 8)["clientId"]!.ToString());
        Assert.True(JsonNode.DeepEquals(Find(current, "salaryStructures", 3), Find(result, "salaryStructures", 3)));
    }
}
