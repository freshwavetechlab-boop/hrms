using System.Globalization;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class PayslipDesignRepository
{
    // Call before the active-template/theme/period fallback can return null.
    // A configured run must not lose its authenticated Info through a profile
    // switch; unmanifested old runs return before any source/history/provider.
    internal async Task<bool> RequireReferenceProfileBeforeFallbackAsync(MySqlConnection db,MySqlTransaction tx,int clientId,int payRunId,
        PayslipDesignTemplate? template)
    {
        if(tx.Connection!=db||clientId<=0||payRunId<=0)throw new InvalidOperationException("Reference profile consistency requires the exact caller transaction and saved run.");
        var currency=await RequireNativeCurrencyBeforeFallbackAsync(db,tx,clientId,payRunId,template);
        var manifest=await ReadReferenceProtectedAsync<ReferenceManifest>(db,tx,clientId,ReferenceManifestCode(payRunId));
        if(manifest is null)return currency;
        if(manifest.Version!=1||manifest.Kind!="ReferenceInformationManifest"||manifest.Request is null||manifest.ActorId<=0
            ||manifest.Request.ClientId!=clientId||manifest.Request.RunId!=payRunId||template is null||template.ClientId!=clientId
            ||template.Theme!=NativePayslipDesignService.Theme||template.Id!=manifest.Request.TemplateId||ReferenceTemplateCoreHash(template)!=manifest.TemplateHash)
            throw new InvalidOperationException("This manifested payroll's active presentation changed; review it again before using another layout.");
        var profile=await ReadProfileAsync(db,clientId,template.Id,tx);
        var period=await db.ExecuteScalarAsync<string?>("SELECT PayPeriod FROM payruns WHERE Id=@RunId AND ClientId=@ClientId",new{RunId=payRunId,ClientId=clientId},tx);
        if(profile is null||ReferenceProfileCoreHash(profile,template,manifest.Request.SourceInfoColumn,manifest.Request.PayPeriod)!=manifest.ProfileHash||period!=manifest.Request.PayPeriod)
            throw new InvalidOperationException("This manifested payroll's profile or period is stale; no other layout silently replaces its reference Information.");
        return true;
    }

    // The one-line caller hook belongs AFTER the active design's period-scope
    // guard and currency load, before YTD append/PDF rendering. No connection,
    // transaction, profile, financial line or global InfoToken is replaced.
    internal async Task<ExcelPayslipRow> ProjectWithFrozenReferenceInformationAsync(MySqlConnection db,MySqlTransaction tx,
        PayslipDesignProfile profile,PayslipDesignTemplate template,NativePayslipDesignData data,NativeCurrencyFrozenRow? currency,int payRunId)
    {
        var row=NativePayslipDesignService.ProjectWithFrozenCurrency(profile,template,data,currency);
        var reference=await ReadReferenceInformationOverrideAsync(db,tx,profile,template,payRunId,data.Month,data.EmployeeId);
        ApplyFrozenReferenceInformation(profile,template,data,row,reference);return row;
    }

    internal static void ApplyFrozenReferenceInformation(PayslipDesignProfile profile,PayslipDesignTemplate template,
        NativePayslipDesignData data,ExcelPayslipRow projected,PayrollReferenceInformationOverride reference)
    {
        if(reference.Status==PayrollReferenceStatus.NotApplicable)return;
        if(reference.Status!=PayrollReferenceStatus.Resolved||reference.SourceColumn is null||reference.Unit!=PayrollReferenceUnit.Amount||reference.Value is null
            ||string.IsNullOrWhiteSpace(reference.ManifestHash)||profile.AmountDecimalPlaces is not(0 or 2))
            throw new InvalidOperationException("The frozen reference Information value is missing or unsupported; no legacy amount is substituted.");
        var bound=profile.Fields.SingleOrDefault(field=>field.SourceColumn==reference.SourceColumn);
        if(bound is null||bound.Kind!="info"||bound.Label!=reference.Label)
            throw new InvalidOperationException("The manifested source Information column/label is no longer present.");
        var visible=profile.Fields.Where(field=>field.Visible&&field.Kind=="info")
            .Where(field=>template.ShowBank||field.InfoToken is not("BANK_ACCOUNT" or "BANK_NAME" or "IFSC"))
            .Where(field=>field.SourceColumn<1500||!string.IsNullOrWhiteSpace(data.Information.GetValueOrDefault(field.InfoToken,""))).ToArray();
        if(projected.Information.Count!=visible.Length||!projected.Information.Select(field=>field.Label).SequenceEqual(visible.Select(field=>field.Label)))
            throw new InvalidOperationException("Apply reference Information before unrelated fields are appended to this exact projected row.");
        var index=Array.FindIndex(visible,field=>field.SourceColumn==reference.SourceColumn);
        if(index<0)return; // Existing reviewed visibility may hide this field.
        // Existing invariant Info-number convention: preserve every declared
        // decimal without grouping/exponent or a financial rounding stage. The
        // shared PDF can apply its existing known monetary-label display rules.
        projected.Information[index]=new(){Label=bound.Label,Value=reference.Value.Value.ToString("0.############################",CultureInfo.InvariantCulture)};
    }
}
