using System.Text.Json;
using MySqlConnector;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class EssMssRepository
{
    private readonly PortableIntegrationCredentialProtector? payslipDesignProtector;

    // Keep the original one-argument constructor for non-payslip integrations.
    // Normal application DI selects this overload and supplies its existing
    // registered protector. No key is guessed by a legacy manual caller.
    public EssMssRepository(IConfiguration configuration, PortableIntegrationCredentialProtector protector) : this(configuration)
        => payslipDesignProtector = protector;

    private async Task<EssPayslipTemplate> ResolvePayslipPeriodTemplateAsync(MySqlConnection db,
        EssPayslipTemplate selected, int clientId, string period)
    {
        if (selected.ClientId != clientId || selected.Theme != NativePayslipDesignService.Theme) return selected;
        var protector = payslipDesignProtector
            ?? throw new InvalidOperationException("Authenticated source payslip selection is unavailable. Supply the registered presentation protector.");
        var template = JsonSerializer.Deserialize<Models.PayslipDesignTemplate>(JsonSerializer.Serialize(selected))!;
        var fallback = await new PayslipDesignRepository(configuration, protector, new ExcelPayslipPdfService())
            .ReadClassicFallbackForPeriodAsync(db, clientId, template, period);
        return fallback is null ? selected : JsonSerializer.Deserialize<EssPayslipTemplate>(JsonSerializer.Serialize(fallback))!;
    }
}
