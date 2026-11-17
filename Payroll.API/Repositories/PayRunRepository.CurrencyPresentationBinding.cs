using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    private sealed class CurrencyPointerSetting { public bool Enabled{get;set;}public string Json{get;set;}=""; }
    private sealed record CurrencyProtectedReceipt(CurrencyActivePointer Pointer,NativeCurrencyStageReceipt Receipt);

    private async Task<CurrencyProtectedReceipt?> ReadExistingCurrencyReceiptAsync(MySqlConnection db,MySqlTransaction tx,int clientId,int runId)
    {
        var stored=await db.QuerySingleOrDefaultAsync<CurrencyPointerSetting>("SELECT IsEnabled Enabled,CAST(SettingsJson AS CHAR) Json FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Code",new{ClientId=clientId,Code=CurrencyPointerCode(runId)},tx);
        if(stored is null)return null;
        if(!stored.Enabled)throw new InvalidOperationException("The original native currency pointer is disabled; no other display silently replaces it.");
        var envelope=JsonSerializer.Deserialize<CurrencyProtectedValue>(stored.Json,CurrencyJson);
        var plain=nativeCurrencyProtector?.UnprotectNativeCurrencyStage(envelope?.Value??"");
        var pointer=plain is null?null:JsonSerializer.Deserialize<CurrencyActivePointer>(plain,CurrencyJson);
        if(pointer is null||pointer.Version!=1||pointer.ClientId!=clientId||pointer.RunId!=runId
            ||pointer.EpochHash is not{Length:64}||pointer.PayloadHash is not{Length:64}
            ||!pointer.EpochHash.All(Uri.IsHexDigit)||!pointer.PayloadHash.All(Uri.IsHexDigit)||pointer.ReceiptCode!="pcst:"+pointer.EpochHash)
            throw new InvalidOperationException("The original native currency pointer cannot be authenticated for this run.");
        var receipt=await ReadProtectedCurrencyValueAsync<NativeCurrencyStageReceipt>(db,tx,clientId,pointer.ReceiptCode);
        if(receipt is null||receipt.Version!=1||receipt.RunId!=runId||receipt.Profile.ClientId!=clientId||!receipt.Profile.Enabled
            ||receipt.Profile.AmountDecimalPlaces!=0||receipt.ProcessingEpochHash!=pointer.EpochHash||receipt.PayloadHash!=pointer.PayloadHash)
            throw new InvalidOperationException("The complete original native currency receipt is missing or changed.");
        return new(pointer,receipt);
    }

    // Call before any theme/period fallback. An absent pointer exits before
    // crypto, source, trace or provider work; a present receipt remains mandatory
    // even when the operational NEW-capture switch has subsequently been off.
    internal async Task<bool> RequireNativeCurrencyPresentationBeforeFallbackAsync(MySqlConnection db,MySqlTransaction tx,
        int clientId,int runId,PayslipDesignTemplate? selectedTemplate)
    {
        if(tx.Connection!=db||clientId<=0||runId<=0)throw new InvalidOperationException("Read currency presentation in the exact client/run transaction.");
        var stored=await ReadExistingCurrencyReceiptAsync(db,tx,clientId,runId);if(stored is null)return false;
        if(selectedTemplate is null||selectedTemplate.ClientId!=clientId||selectedTemplate.Theme!=NativePayslipDesignService.Theme)
            throw new InvalidOperationException("This captured payroll's native financial presentation changed; no Classic fallback silently replaces it.");
        var period=await db.ExecuteScalarAsync<string?>("SELECT PayPeriod FROM payruns WHERE Id=@RunId AND ClientId=@ClientId",new{RunId=runId,ClientId=clientId},tx);
        var protector=nativeCurrencyProtector??throw new InvalidOperationException("Native currency protection is unavailable.");
        var selection=await new PayslipDesignRepository(configuration,protector,new ExcelPayslipPdfService())
            .ReadNativeSourceDesignSelectionAsync(db,tx,clientId,payPeriod:period);
        if(period is null||stored.Receipt.Profile.PayPeriod!=period||selection is null||selection.Template.Id!=selectedTemplate.Id
            ||selection.Profile.AmountDecimalPlaces!=0||!selection.Applies
            ||NativeCurrencyPresentation.Hash(selection.Template,selection.Profile,period)!=stored.Receipt.Profile.ProfileHash)
            throw new InvalidOperationException("The captured native financial mapping, precision or payroll-month scope changed; no other layout replaces its protected currency stage.");
        return true;
    }
}
