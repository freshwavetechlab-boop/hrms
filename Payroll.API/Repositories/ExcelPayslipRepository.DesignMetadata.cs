using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    // Reuse proven source identity/profile projection without invoking salary simulation.
    // The caller retains only labels; no profile or financial values enter the blueprint.
    internal static async Task AnnotateDesignMetadataAsync(MySqlConnection db,MySqlTransaction tx,ExcelPayslipBatch batch,ExcelPayslipCalculationSource source)
    {
        await AnnotateEmploymentAsync(db,batch,tx,annotateMasterDetails:false);
        var masters=await ReadProfileMastersAsync(db,tx,batch.ClientId,batch.Month);
        await PayrollDataTableStore.ApplyEmployeeTablesAsync(db,masters,tx);
        AnnotateCurrentProfiles(batch,source,masters);
        await AnnotateCurrentMasterAsync(db,tx,batch,source,configuration:null,metadataOnly:true);
    }
}
