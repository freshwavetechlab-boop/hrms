using System.Data;
using System.Globalization;
using Dapper;
using MySqlConnector;

namespace Payroll.API.Repositories;

public static partial class PayrollDataTableStore
{
    // Reservation is compatibility protection, not salary authority. Native
    // calculation separately authenticates the protected declaration/receipt.
    internal static async Task<Dictionary<long,int>> ReadNamedTemplateReservationsAsync(MySqlConnection db,IDbTransaction? tx)
    {
        if(!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='modulesettings')",transaction:tx))return [];
        var rows=await db.QueryAsync<(int ClientId,string Code)>("SELECT client_id ClientId,ModuleCode Code FROM modulesettings WHERE ModuleCode LIKE 'named-input-t:%'",transaction:tx);
        var result=new Dictionary<long,int>();
        foreach(var row in rows)
        {
            if(!long.TryParse(row.Code["named-input-t:".Length..],NumberStyles.None,CultureInfo.InvariantCulture,out var id)||id<=0||row.ClientId<=0||!result.TryAdd(id,row.ClientId))
                throw new InvalidOperationException("A managed named-operand template reservation is invalid or duplicated; use the reviewed configuration path.");
        }
        return result;
    }
    internal static bool RetainNamedTemplate(Dictionary<long,int> reservations,long id,string clientRef)
    {
        if(!reservations.TryGetValue(id,out var client))return false;
        if(clientRef.Trim()!=client.ToString(CultureInfo.InvariantCulture))throw new InvalidOperationException("A managed named-operand template cannot be reassigned to another client by legacy setup.");
        return true;
    }
}
