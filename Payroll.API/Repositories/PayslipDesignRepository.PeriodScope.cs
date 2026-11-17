using System.Text.Json;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class PayslipDesignRepository
{
    internal const string ScopedWritesFlag = "Payroll:ScopedPayslipDesignWritesEnabled";
    private void RequireScopedPresentationWrite(List<string>? periods,PayslipDesignProfile? existing)
    {
        if ((periods is not null || existing?.PreviousClassicTemplate is not null)
            && !configuration.GetValue<bool>(ScopedWritesFlag))
            throw new InvalidOperationException("Scoped payslip design saving is disabled. Deploy compatible payroll, ESS and worker readers before enabling scoped presentation writes against the shared database. No configuration was saved.");
    }
    private static PayslipDesignTemplate? RetainClassicPresentation(PayslipDesignProfile? existing,
        PayslipDesignTemplate? original, List<string>? periods)
    {
        // This value only comes from a previously authenticated profile or an
        // existing locked client Classic row. There is no fallback request DTO.
        if (existing?.PreviousClassicTemplate is { } retained)
            return JsonSerializer.Deserialize<PayslipDesignTemplate>(JsonSerializer.Serialize(retained, Json), Json);
        if (periods is null) return null;
        if (original is not { Theme: "Classic", Active: true, ClientId: > 0, Id: > 0 })
            throw new InvalidOperationException("To select payroll months, edit an existing active client Classic template so its exact prior presentation can be retained.");
        return JsonSerializer.Deserialize<PayslipDesignTemplate>(JsonSerializer.Serialize(original, Json), Json);
    }

    // Select presentation only; outside-scope callers must not load stale
    // source metadata, currency stages or reference-information providers.
    internal async Task<PayslipDesignTemplate?> ReadClassicFallbackForPeriodAsync(MySqlConnection db,
        int clientId, PayslipDesignTemplate template, string period, MySqlTransaction? tx = null)
    {
        if (template.ClientId != clientId || template.Theme != NativePayslipDesignService.Theme)
            return null;
        var profile = await ReadProfileAsync(db, clientId, template.Id, tx)
            ?? throw new InvalidOperationException("The client source design has no authenticated presentation review.");
        NativePayslipDesignService.ValidateProfile(profile, template);
        return NativePayslipDesignScope.Applies(profile, period) ? null
            : JsonSerializer.Deserialize<PayslipDesignTemplate>(JsonSerializer.Serialize(profile.PreviousClassicTemplate!, Json), Json);
    }
}
