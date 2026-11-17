using System.Text.Json.Nodes;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    // Extra equivalence is restricted to reviewed location corrections. Ordinary
    // transfer/history signatures and evidence hashes retain their exact formulas.
    internal static IReadOnlyList<string> LocationTemplateDifferences(JsonObject candidate,IReadOnlyList<ResolvedComponent> proposed)
    {
        var differences=TransferTemplateDifferences(candidate,proposed);
        if(proposed.Select(component=>component.Definition.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=proposed.Count)return differences;
        var lines=(candidate["lines"] as JsonArray??[]).OfType<JsonObject>().ToArray();
        var equivalent=new HashSet<string>(StringComparer.Ordinal);
        for(var index=0;index<Math.Min(lines.Length,proposed.Count);index++)
        {
            var definition=proposed[index].Definition;
            if(Text(lines[index],"componentId")!=proposed[index].Id)continue;
            try
            {
                if(PayrollFormulaEvaluator.CanonicalStructure(Text(lines[index],"formula"))==PayrollFormulaEvaluator.CanonicalStructure(definition.Formula))
                    equivalent.Add(definition.Code+": formula differs");
            }
            catch(Exception error)when(error is InvalidOperationException or FormatException or OverflowException)
            { /* Invalid/unsupported rules cannot establish equivalence. */ }
        }
        return differences.Where(difference=>!equivalent.Contains(difference)).ToArray();
    }
}
