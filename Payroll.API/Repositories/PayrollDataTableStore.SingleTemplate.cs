using System.Text.Json.Nodes;

namespace Payroll.API.Repositories;

public static partial class PayrollDataTableStore
{
    internal static void RequireSingleSalaryStructureAppend(JsonObject before,JsonObject after,int clientId)
    {
        if(clientId<=0||before["salaryStructures"] is not JsonArray oldRows||after["salaryStructures"] is not JsonArray newRows
            ||oldRows.Any(n=>n is not JsonObject)||newRows.Any(n=>n is not JsonObject))throw new InvalidOperationException("One valid client salary-template append is required.");
        var oldIds=oldRows.OfType<JsonObject>().Select(SettingsRepository.ReadRowId).ToArray();
        var newIds=newRows.OfType<JsonObject>().Select(SettingsRepository.ReadRowId).ToArray();
        if(oldIds.Any(id=>id<=0)||oldIds.Distinct().Count()!=oldIds.Length||newIds.Any(id=>id<=0)||newIds.Distinct().Count()!=newIds.Length
            ||newRows.Count!=oldRows.Count+1||!newIds.Take(oldIds.Length).SequenceEqual(oldIds)
            ||!oldRows.Zip(newRows.Take(oldRows.Count)).All(pair=>JsonNode.DeepEquals(pair.First,pair.Second))
            ||!SettingsRepository.IsPureClientRow(newRows[^1]!.AsObject(),clientId))
            throw new InvalidOperationException("Preserve every existing salary structure exactly and append only one new selected-client template.");
        var oldOther=before.DeepClone().AsObject();var newOther=after.DeepClone().AsObject();oldOther.Remove("salaryStructures");newOther.Remove("salaryStructures");
        if(!JsonNode.DeepEquals(oldOther,newOther))throw new InvalidOperationException("A single-template append cannot change components, payslip designs, statutory rules, schedules, tax, defaults or other settings.");
    }
}
