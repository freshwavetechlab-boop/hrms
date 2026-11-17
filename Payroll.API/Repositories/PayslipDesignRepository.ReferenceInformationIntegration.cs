using Dapper;
using System.Text.RegularExpressions;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class PayslipDesignRepository
{
    private static void RequireMonthlyReferenceTarget(PayslipDesignField field)
    {
        var label=Regex.Replace(field.Label,"[^a-zA-Z]","").ToLowerInvariant();
        if(field.Kind!="info"||field.InfoToken!="SAVED_MONTHLY_GROSS"||label.Contains("annual",StringComparison.Ordinal)
            ||label.Contains("yearly",StringComparison.Ordinal)||label.Contains("perannum",StringComparison.Ordinal)||label.Contains("peryear",StringComparison.Ordinal)
            ||Regex.IsMatch(field.Label,@"\b(?:year|annum)\b|\bLPA|\bP\s*\.?\s*A\.?(?:\b|$)",RegexOptions.IgnoreCase))
            throw new InvalidOperationException("A monthly salary budget must bind a monthly SAVED_MONTHLY_GROSS Information field, never current annual CTC or an annual-labelled field.");
    }
    private static string ReferenceTemplateCoreHash(PayslipDesignTemplate template)=>ReferenceHash(new{Version=1,template.Id,template.ClientId,template.Theme});
    private static string ReferenceProfileCoreHash(PayslipDesignProfile profile,PayslipDesignTemplate template,int column,string period)
    {
        var target=profile.Fields.SingleOrDefault(field=>field.SourceColumn==column)??throw new InvalidOperationException("The reference target Information column is missing.");
        RequireMonthlyReferenceTarget(target);
        return ReferenceHash(new{Version=1,ProfileVersion=profile.Version,profile.ClientId,profile.TemplateId,TemplateClientId=template.ClientId,template.Theme,
            AppliesToRunPeriod=NativePayslipDesignScope.Applies(profile,period),
            Target=new{target.SourceColumn,target.Label,target.Kind,target.InfoToken,target.NativeCode}});
    }
    private static void ValidateReferenceStoredManifest(ReferenceManifest manifest,int clientId,int runId)
    {
        if(manifest is null||manifest.Version!=1||manifest.Kind!="ReferenceInformationManifest"||manifest.ActorId<=0||manifest.Request is null
            ||manifest.Request.ClientId!=clientId||manifest.Request.RunId!=runId||manifest.Employees is null||manifest.Employees.Count is <1 or >ReferenceRosterLimit
            ||manifest.Employees.Any(row=>row is null||row.Binding is null||row.Snapshot is null)||manifest.Declaration is null
            ||ReferenceHash(manifest.Declaration)!=ReferenceHash(ReferenceBudgetDeclaration())||!Guid.TryParseExact(manifest.RequestId,"N",out _)
            ||!ReferenceDigest(manifest.PlanHash)||!ReferenceDigest(manifest.StateWitnessHash)||!ReferenceDigest(manifest.SourceHash)||!ReferenceDigest(manifest.SourceDataHash)
            ||manifest.Employees.Select(row=>row.Binding.Scope.EmployeeId).Distinct().Count()!=manifest.Employees.Count
            ||manifest.PlanHash!=ReferenceHash(new{Version=1,Request=manifest.Request,StateWitnessHash=manifest.StateWitnessHash,Label=manifest.Label,Declaration=manifest.Declaration,Employees=manifest.Employees}))
            throw new InvalidOperationException("The authenticated reference manifest's complete-roster schema or immutable plan proof is invalid.");
        foreach(var row in manifest.Employees)
        {
            if(row.Binding.Scope.ClientId!=clientId||row.Binding.Scope.PayRunId!=runId||row.Binding.Scope.PayPeriod!=manifest.Request.PayPeriod
                ||row.Binding.Field.TemplateId!=manifest.Request.TemplateId||row.Binding.Field.ProfileHash!=manifest.ProfileHash
                ||row.Binding.Field.SourceColumn!=manifest.Request.SourceInfoColumn||row.Binding.Field.Label!=manifest.Label
                ||row.Binding.DeclarationHash!=PayrollReferenceInformation.DeclarationHash(manifest.Declaration)||row.Snapshot.Unit!=PayrollReferenceUnit.Amount)
                throw new InvalidOperationException("The immutable reference manifest contains a foreign or invalid binding.");
            PayrollReferenceInformation.ValidateSnapshot(row.Snapshot,row.Binding,row.Binding.Field);
        }
    }
    public static bool CanManageReferenceInformation(AuthUser actor,int clientId)=>CanManage(actor,clientId)&&WorkspacePermissions.CanReadPayslips(actor);

    // Endpoint's selected client is independently bound to the authenticated
    // token before the ordinary same-transaction review/apply path is invoked.
    public Task<PayrollReferenceInformationReceipt> ApplyScopedReferenceInformationAsync(int clientId,
        PayrollReferenceInformationApplyRequest command,AuthUser actor)
    {
        RequireReferenceWriter(actor,clientId);
        if(command is null||string.IsNullOrWhiteSpace(command.ReviewToken)||command.ReviewToken.Length>16000)
            throw new InvalidOperationException("Supply the compact reviewed reference Information command.");
        var review=ReadReferenceReview(command.ReviewToken);
        if(review.Request.ClientId!=clientId)throw new UnauthorizedAccessException("The selected client does not match this protected reference Information review.");
        return ApplyReferenceInformationAsync(command,actor);
    }

    private static async Task RequireReferenceWorkflowIsolationAsync(MySqlConnection db,MySqlTransaction tx)
    {
        // Apply is metadata-only, not a payroll transition. Any generic workflow
        // matching this route must be reviewed separately; never bypass it or
        // allow AfterSuccess to create an unrelated approval/notification.
        // The Serializable range lock also covers rule additions during apply.
        var rules=await db.QueryAsync<WorkflowActionRule>("SELECT HttpMethod,PathPattern FROM workflow_action_rules WHERE IsActive=TRUE FOR UPDATE",transaction:tx);
        if(rules.Any(rule=>rule.HttpMethod.Equals("POST",StringComparison.OrdinalIgnoreCase)
            &&WorkflowActionMiddleware.Match(rule.PathPattern,"/api/payslip-designs/reference-information/apply") is not null))
            throw new InvalidOperationException("A configured workflow applies to reference Information configuration. Use its reviewed transition; no metadata was saved.");
    }
}
