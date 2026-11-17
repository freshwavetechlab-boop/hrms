using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class PayslipDesignRepository
{
    internal Task<bool> RequireNativeCurrencyBeforeFallbackAsync(MySqlConnection db,MySqlTransaction tx,int clientId,int runId,PayslipDesignTemplate? template)
        => new PayRunRepository(configuration,new TaxEngineRepository(configuration),nativeCurrencyProtector:protector)
            .RequireNativeCurrencyPresentationBeforeFallbackAsync(db,tx,clientId,runId,template);
}
