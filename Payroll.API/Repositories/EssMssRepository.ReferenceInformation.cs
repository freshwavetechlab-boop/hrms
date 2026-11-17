using System.Data;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class EssMssRepository
{
    private sealed record ReferenceEssTemplateSelection(EssPayslipTemplate Template,bool Manifested);

    // Existing ESS document methods have no ambient transaction. Re-read the
    // exact active template and authenticated manifest in one tiny read-only
    // snapshot before any Classic/period fallback can change PdfPreferred.
    private async Task<ReferenceEssTemplateSelection> GuardReferenceInformationTemplateAsync(MySqlConnection db,
        EssPayslipTemplate selected,int clientId,int payRunId)
    {
        await using var tx=await db.BeginTransactionAsync(IsolationLevel.RepeatableRead,isReadOnly:true);
        var current=await db.QueryFirstOrDefaultAsync<PayslipDesignTemplate>("SELECT Id,ClientId,Name,Theme,ShowLogo,ShowClient,ShowYtd,ShowBank,Note,Active FROM paysliptemplates WHERE Active=TRUE AND (ClientId=@ClientId OR ClientId=0) ORDER BY CASE WHEN ClientId=@ClientId THEN 0 ELSE 1 END,Id DESC LIMIT 1",new{ClientId=clientId},tx);
        var frozen=await new PayslipDesignRepository(configuration,payslipDesignProtector!,new ExcelPayslipPdfService())
            .RequireReferenceProfileBeforeFallbackAsync(db,tx,clientId,payRunId,current);
        if(!frozen){await tx.RollbackAsync();return new(selected,false);}
        var verified=JsonSerializer.Deserialize<EssPayslipTemplate>(JsonSerializer.Serialize(current))!;
        await tx.RollbackAsync();return new(verified,true);
    }
}
