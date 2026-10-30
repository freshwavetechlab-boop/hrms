using System.Globalization;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
namespace Payroll.API.Repositories;
public sealed partial class ExcelPayrollSetupRepository
{
    internal static void ValidateTemplateScopeFields(ExcelPayrollSetupRequest q) {
        if(q.TemplateScopeReviewVersion is not(0 or 1))throw new InvalidOperationException("Unsupported template applicability review version.");
        var s=q.TemplateScope;if(s is null)return;
        if(s.EffectiveTo is null||s.EffectiveTo.Length>10||s.PolicyVersion is null||s.PolicyVersion.Length>120
            ||s.WorkLocationIds is null||s.WorkLocationIds.Count>100||s.WorkLocationIds.Any(i=>i<=0)||s.WorkLocationIds.Distinct().Count()!=s.WorkLocationIds.Count
            ||s.NewLocationNames is null||s.NewLocationNames.Count>100||s.NewLocationNames.Any(v=>string.IsNullOrWhiteSpace(v)||v.Length>180)
            ||s.Categories is null||s.Categories.Count>40||s.Categories.Any(v=>string.IsNullOrWhiteSpace(v)||v.Length>100)
            ||s.LocationMode is not("" or "AllClientLocations" or "Listed")||s.CategoryKind is not("" or "AllClientCategories" or "SkillCategory" or "EmploymentType")
            ||s.DivisorMeaning is not("" or "CalendarDays" or "ContractFixedDays" or "WorkingDays")||s.PayableMeaning is not("" or "AlreadyPayable")||s.PolicyBasis is not("" or "SavedSourceRules" or "DatedPolicy"))
            throw new InvalidOperationException("Review bounded location/category, day meaning, policy version and effective-period applicability fields.");
    }
    internal static IReadOnlyList<string> TemplateScopeIssues(ExcelPayrollSetupRequest q,IReadOnlyList<PlannedRow> rows) {
        var issues=new List<string>();var s=q.TemplateScope;
        if(q.TemplateScopeReviewVersion!=1||s is null)return ["Review template applicability in the current saved-batch setup before transferring source configuration."];
        if(!s.Reviewed)issues.Add("Confirm this source cohort's location/category, day meanings, policy basis and effective period.");
        if(s.LocationMode==""||s.LocationMode=="Listed"&&s.WorkLocationIds.Count+s.NewLocationNames.Count==0)issues.Add("Choose all client locations explicitly or list the reviewed locations.");
        if(s.CategoryKind==""||s.CategoryKind!="AllClientCategories"&&s.Categories.Count==0)issues.Add("Choose all client categories explicitly or list the selected skill/employment categories.");
        if(s.DivisorMeaning==""||s.PayableMeaning!="AlreadyPayable")issues.Add("Review what the salary divisor and source payable days mean; worked days are not automatically paid days.");
        if(s.PolicyBasis==""||s.PolicyBasis=="DatedPolicy"&&string.IsNullOrWhiteSpace(s.PolicyVersion))issues.Add("Choose reviewed saved-source rules or identify the approved dated policy version. No policy is invented or activated.");
        if(!DateOnly.TryParseExact(q.EffectiveFrom,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var from))issues.Add("Review a valid salary effective date.");
        if(s.OpenEnded&&s.EffectiveTo.Length>0||!s.OpenEnded&&(!DateOnly.TryParseExact(s.EffectiveTo,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var end)||end<from))issues.Add("Choose an explicit end date at/after the effective date or explicitly approve an open-ended scope.");
        foreach(var row in rows.Where(r=>!r.KeepExisting)) {
            if(s.LocationMode=="Listed"&&!(row.Employee.WorkLocationId>0&&s.WorkLocationIds.Contains(row.Employee.WorkLocationId))&&!s.NewLocationNames.Any(n=>Normalize(n)==Normalize(row.NewWorkLocationName)&&row.NewWorkLocationName.Length>0))issues.Add("A selected source row lies outside the reviewed location scope.");
            var category=s.CategoryKind=="SkillCategory"?row.Employee.PersonalDetails.SkillCategory:s.CategoryKind=="EmploymentType"?row.Employee.PersonalDetails.EmploymentType:"";
            if(s.CategoryKind is "SkillCategory" or "EmploymentType"&&(string.IsNullOrWhiteSpace(category)||!s.Categories.Any(c=>Normalize(c)==Normalize(category))))issues.Add("A selected row's category is missing or outside the reviewed scope; do not infer it from its pay.");
        }
        return issues.Distinct().ToArray();
    }
    private static string ScopePolicyKey(ExcelPayrollTemplateScope scope,string rules)=>scope.PolicyBasis=="SavedSourceRules"?"SOURCE_RULES:"+rules:"DATED_POLICY:"+scope.PolicyVersion.Trim();
    internal static string ScopedTemplateSignature(IReadOnlyList<ResolvedComponent> components,ExcelPayrollSetupRequest q) {
        var rules=TransferRulesSignature(components);if(q.TemplateScopeReviewVersion!=1||q.TemplateScope is not {} s)return rules;
        return Hash(JsonSerializer.Serialize(new {rules,q.EffectiveFrom,s.EffectiveTo,s.OpenEnded,s.LocationMode,Locations=s.WorkLocationIds.Order(),Names=s.NewLocationNames.Select(Normalize).Order(),s.CategoryKind,Categories=s.Categories.Select(Normalize).Order(),s.DivisorMeaning,s.PayableMeaning,s.PolicyBasis,PolicyKey=ScopePolicyKey(s,rules)},Json));
    }
    private static bool SameScopePolicy(ExcelPayrollRecordedTemplateScope old,ExcelPayrollTemplateScope current,string rules)=>old.RulesSignature==rules&&old.PolicyKey==ScopePolicyKey(current,rules)&&old.Scope.DivisorMeaning==current.DivisorMeaning&&old.Scope.PayableMeaning==current.PayableMeaning;
    private static bool ScopeCovers(ExcelPayrollRecordedTemplateScope old,ExcelPayrollSetupRequest q) {
        var s=q.TemplateScope!;var o=old.Scope;
        var dates=string.CompareOrdinal(old.EffectiveFrom,q.EffectiveFrom)<=0&&(o.OpenEnded||!s.OpenEnded&&string.CompareOrdinal(o.EffectiveTo,s.EffectiveTo)>=0);
        var locations=o.LocationMode=="AllClientLocations"||s.LocationMode=="Listed"&&s.WorkLocationIds.All(o.WorkLocationIds.Contains)&&s.NewLocationNames.All(n=>o.NewLocationNames.Any(v=>Normalize(v)==Normalize(n)));
        var categories=o.CategoryKind=="AllClientCategories"||o.CategoryKind==s.CategoryKind&&s.Categories.All(c=>o.Categories.Any(v=>Normalize(v)==Normalize(c)));
        return dates&&locations&&categories;
    }
    internal static (bool Compatible,string Status,string Reason) CompareTemplateScope(string templateId,string rules,ExcelPayrollSetupRequest q,int clientId,IReadOnlyList<ExcelPayrollRecordedTemplateScope> records,ExcelPayrollTransferTemplateDecision? decision) {
        if(q.TemplateScopeReviewVersion!=1)return(true,"Legacy comparison","Configuration-only legacy comparison; not applicability certification.");
        if(TemplateScopeIssues(q,[]).Count>0)return(false,"Review required","Complete the explicit scope review before reuse or creation.");
        var known=records.Where(r=>r.ClientId==clientId&&r.TemplateId==templateId&&r.Scope.Reviewed).ToArray();
        if(known.Any(r=>SameScopePolicy(r,q.TemplateScope!,rules)&&ScopeCovers(r,q)))return(true,"Compatible","Recorded reviewed rules, locations/categories and period cover this source cohort.");
        var selected=decision?.ExistingTemplateId==templateId;
        if(known.Length==0)return selected&&decision?.ScopeAction=="AttestLegacyScope"?(true,"Legacy scope attested","Explicit owner review applies only to this selected source cohort; global/shared configuration remains unchanged."):(false,"Unknown legacy scope","Review the existing template's applicability, or explain why a distinct scoped variant is needed.");
        if(known.Any(r=>SameScopePolicy(r,q.TemplateScope!,rules)))return selected&&decision?.ScopeAction=="ExtendReviewedScope"?(true,"Reviewed scope extension","Explicit owner review extends applicability for selected source assignments only; prior receipts/history remain unchanged."):(false,"Extension review required","Rules/policy match, but dates or location/category coverage need explicit extension review.");
        return(false,"Different policy/basis","Recorded policy version, rules or day meanings differ. Use a reviewed new effective variant; do not force reuse.");
    }
    private static async Task<List<ExcelPayrollRecordedTemplateScope>> ReadTemplateScopesAsync(MySqlConnection db,MySqlTransaction tx,int clientId) {
        if(!await HasReceiptTableAsync(db,tx))return [];
        // JSON in the sort's WHERE clause also makes MySQL carry the large receipt
        // into filesort. Select bounded metadata keys first; filter absent scopes only
        // after the ordered key query, without changing stored receipts or scope rules.
        var scopeIds=(await db.QueryAsync<string>("SELECT id FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND status='Applied' ORDER BY updated_at DESC,id DESC LIMIT 2001",new{ClientId=clientId},tx)).ToArray();
        if(scopeIds.Length>2000)throw new InvalidOperationException("The client's scope receipt catalogue exceeds the bounded review size; refine or archive through the supported review workflow.");
        var payloads=scopeIds.Length>0?await db.QueryAsync<ReceiptScopePayload>("SELECT id ImportId,CAST(JSON_EXTRACT(result_json,'$.transfer.templates[*].applicability') AS CHAR) ScopeJson FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND status='Applied' AND id IN @Ids",new{ClientId=clientId,Ids=scopeIds},tx):[];
        var scopeOrder=scopeIds.Select((id,index)=>new{id,index}).ToDictionary(value=>value.id,value=>value.index,StringComparer.Ordinal);
        var saved=payloads.OrderBy(value=>scopeOrder[value.ImportId]).Select(value=>value.ScopeJson).Where(value=>!string.IsNullOrWhiteSpace(value)&&value!="null");
        var result=new List<ExcelPayrollRecordedTemplateScope>();foreach(var json in saved){var scopes=JsonSerializer.Deserialize<List<ExcelPayrollRecordedTemplateScope?>>(json,Json)??[];foreach(var scope in scopes){if(scope is not null&&scope.ClientId==clientId)result.Add(scope);}}
        return result;
    }
    private sealed class ReceiptScopePayload { public string ImportId { get; set; } = ""; public string ScopeJson { get; set; } = "[]"; }
}
