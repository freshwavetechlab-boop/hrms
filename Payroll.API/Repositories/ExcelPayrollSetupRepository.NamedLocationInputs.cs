using System.Text.Json.Nodes;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    private async Task<NamedFormulaInputProfile?> ReadCorrectionNamedProfileAsync(MySqlConnection db,MySqlTransaction tx,int client,
        ExcelPayrollLocationCorrectionRequest request,string setupJson,bool locked)
    {
        if(request.NamedInputReview is not{} review)return null;
        if(string.IsNullOrWhiteSpace(review.TemplateId)||review.Bindings is null||string.IsNullOrWhiteSpace(review.DeclarationHash)||review.DeclarationHash.Length!=64)
            throw new InvalidOperationException("Select the exact reviewed generic template declaration and complete source operand bindings.");
        var profile=await NamedFormulaInputStore.ReadProfileAsync(db,tx,configuration,client,review.TemplateId,locked)
            ??throw new InvalidOperationException("The named operand template declaration is unavailable.");
        var sourceMonth=request.SourceReview?.AttendanceReview?.SourceMonth;
        if(!DateTime.TryParseExact(sourceMonth+"-01","yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out var from)
            ||from.Year<1900||from>=new DateTime(EmployeeEmploymentRules.Today.Year,EmployeeEmploymentRules.Today.Month,1)
            ||request.SourceReview?.EffectiveFrom!=from.ToString("yyyy-MM-dd")||request.SourceReview.TemplateScope is not{Reviewed:true,OpenEnded:false} scope||scope.EffectiveTo!=from.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd"))
            throw new InvalidOperationException("Review the named operands for this exact closed source month; future assignments/defaults are not extended.");
        var setup=JsonNode.Parse(setupJson)!;var templates=setup["salaryStructures"]!.AsArray().OfType<JsonObject>();
        var template=templates.SingleOrDefault(t=>Text(t,"id")==profile.TemplateId&&Text(t,"active")=="true"&&Text(t,"clientId").Split([';',',','|']).Any(v=>v.Split(':')[0].Trim()==client.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            ??throw new InvalidOperationException("The managed named-operand template is inactive or out of client scope.");
        var masters=setup["salaryComponents"]!.AsArray().OfType<JsonObject>().ToArray();
        if(profile.DeclarationHash!=review.DeclarationHash||profile.RulesHash!=LocationSettingsRulesHash(template,masters))throw new InvalidOperationException("The exact named template declaration or monetary rules changed; refresh the preview.");
        ReviewedNamedFormulaInputs.ValidateDefinitions(profile.Definitions,masters.SelectMany(m=>new[]{Text(m,"id"),Text(m,"code")}));return profile;
    }
    internal static List<NamedFormulaInputManifest> PrepareNamedCorrectionManifests(PayrollLocationPartPeriod period,ExcelPayslipBatch batch,
        ExcelPayslipCalculationSource source,ExcelPayrollLocationCorrectionRequest request,NamedFormulaInputProfile? profile,string rawBatchHash,string rawSourceHash,IReadOnlyDictionary<string,string> sourceRowIds)
    {
        if(profile is null)return [];
        var output=new List<NamedFormulaInputManifest>();
        var group=request.SourceReview!.LocationAllocations!.Groups.Single(g=>g.EmployeeId==period.EmployeeId);
        if(group.Parts.Count!=period.Parts.Count||sourceRowIds.Count!=period.Parts.Count
            ||sourceRowIds.Values.Distinct(StringComparer.Ordinal).Count()!=period.Parts.Count
            ||period.Parts.Select(part=>part.PartKey).Distinct(StringComparer.Ordinal).Count()!=period.Parts.Count)
            throw new InvalidOperationException("Review every unique named-operand source part and its exact assignment/row together; no part is selected or dropped automatically.");
        foreach(var part in period.Parts)
        {
            var context=part.NamedInputs??throw new InvalidOperationException("A complete source part lost its named operands.");
            if(!sourceRowIds.TryGetValue(part.AssignmentId,out var rowId))throw new InvalidOperationException("The exact source row for a named assignment is missing.");
            var row=batch.Rows.Single(r=>r.Id==rowId&&group.Parts.Any(p=>p.RowId==r.Id&&p.WorkLocationId==part.WorkLocationId)
                &&batch.ProfileLinks!.Any(l=>l.RowId==r.Id&&l.EmployeeId==period.EmployeeId));
            var bound=ReviewedNamedFormulaInputs.Bind(profile,request.NamedInputReview!.Bindings,source,row,[]);
            if(context.DeclarationHash!=profile.DeclarationHash||!System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.JsonSerializer.SerializeToNode(context.Values),System.Text.Json.JsonSerializer.SerializeToNode(bound.Context.Values)))
                throw new InvalidOperationException("A source part's named operands differ from its exact row; values cannot be shared or selected from another part.");
            var m=new NamedFormulaInputManifest(1,period.ClientId,period.EmployeeId,period.PayPeriod,part.AssignmentId,part.SalaryStructureId,part.WorkLocationId,part.PartKey,
                batch.Id,row.Id,row.SourceRow,ExcelPayslipRepository.EmploymentSourceHash(batch,source),rawBatchHash,rawSourceHash,
                ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,row,source),profile.DeclarationHash,profile.Definitions,bound.Context.Values,bound.Cells,"",0,DateTime.MinValue,"")
                {CompleteSourcePartKeys=period.Parts.Select(p=>p.PartKey).Order(StringComparer.Ordinal).ToList(),ReferenceAssignmentId=period.ReferenceAssignmentId,NativeRulesHash=profile.RulesHash,
                    ComponentScopesHash=ReviewedNamedFormulaInputs.Hash(part.ComponentScopes.OrderBy(pair=>pair.Key,StringComparer.Ordinal))};
            m=m with{ManifestHash=NamedFormulaInputStore.ManifestHash(m)};context.ManifestHash=m.ManifestHash;output.Add(m);
        }
        return output;
    }
}
