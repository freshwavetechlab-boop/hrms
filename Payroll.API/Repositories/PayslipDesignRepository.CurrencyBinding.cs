using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class PayslipDesignRepository
{
    internal sealed record NativeSourceDesignSelection(PayslipDesignTemplate Template, PayslipDesignProfile Profile, bool Applies);
    // Caller owns this exact transaction. No independent connection, commit,
    // configuration write, source mutation or payroll calculation occurs here.
    internal async Task<NativeSourceDesignBinding?> ReadNativeSourceDesignBindingAsync(
        MySqlConnection db, MySqlTransaction tx, int clientId, bool locked = false, string? payPeriod = null)
        => await BindNativeSourceDesignAsync(db, tx, clientId,
            await ReadNativeSourceDesignSelectionAsync(db, tx, clientId, locked, payPeriod), locked);

    internal async Task<NativeSourceDesignSelection?> ReadNativeSourceDesignSelectionAsync(
        MySqlConnection db, MySqlTransaction tx, int clientId, bool locked = false, string? payPeriod = null)
    {
        if (clientId <= 0 || tx.Connection != db)
            throw new InvalidOperationException("A same-connection transaction and explicit client are required for source design evidence.");
        var template = await db.QueryFirstOrDefaultAsync<PayslipDesignTemplate>(
            "SELECT * FROM paysliptemplates WHERE ClientId=@ClientId AND Active=TRUE ORDER BY Id DESC LIMIT 1"
            + (locked ? " FOR UPDATE" : ""), new { ClientId = clientId }, tx);
        if (template is null || template.Theme != NativePayslipDesignService.Theme) return null;
        if (locked)
            await db.QueryAsync<string>("SELECT ModuleCode FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Code FOR UPDATE",
                new { ClientId = clientId, Code = Code(template.Id) }, tx);
        var profile = await ReadProfileAsync(db, clientId, template.Id, tx)
            ?? throw new InvalidOperationException("The active client source design has no authenticated review.");
        NativePayslipDesignService.ValidateProfile(profile, template);
        return new(template, profile, NativePayslipDesignScope.Applies(profile, payPeriod));
    }

    internal async Task<NativeSourceDesignBinding?> BindNativeSourceDesignAsync(MySqlConnection db, MySqlTransaction tx,
        int clientId, NativeSourceDesignSelection? selection, bool locked = false)
    {
        if (clientId <= 0 || tx.Connection != db) throw new InvalidOperationException("A same-connection client transaction is required.");
        if (selection is null || !selection.Applies) return null;
        var template = selection.Template; var profile = selection.Profile;
        NativePayslipDesignService.ValidateProfile(profile, template);
        if (profile.ClientId != clientId) throw new InvalidOperationException("The selected source design belongs to another client.");
        var stored = await SourceAsync(db, clientId, profile.SourceBatchId, tx, locked);
        var metadata = await MetadataAsync(db, tx, stored, clientId);
        if (profile.SourceHash != metadata.Hash)
            throw new InvalidOperationException("The reviewed source design is stale. Review its current source presentation before capturing native evidence.");
        var source = JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(stored.SourceJson!, Json)
            ?? throw new InvalidOperationException("The reviewed source calculation metadata is unavailable.");
        return new(template, profile, metadata.Batch, source, metadata.Hash, stored.BatchJson, stored.SourceJson!);
    }
}
