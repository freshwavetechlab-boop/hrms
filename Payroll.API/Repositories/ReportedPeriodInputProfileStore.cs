using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

// Separate keys/purposes preserve every existing v1 profile and receipt byte.
// These records are input declarations, never an employee assignment.
internal static class ReportedPeriodInputProfileStore
{
    internal static string Key(string templateId)=>"reported-input-t:"+templateId;
    internal static async Task InsertAsync(MySqlConnection db,MySqlTransaction tx,IConfiguration configuration,ReportedPeriodInputProfileV2 profile)
    {
        if(tx.Connection!=db)throw new InvalidOperationException("Reported profile persistence needs the caller's atomic reviewed transaction.");
        var envelope=ReportedPeriodInputV2Codec.ProtectProfile(profile,configuration);
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@Client,@Code,TRUE,CAST(@Json AS JSON))",new { Client=profile.ClientId,Code=Key(profile.TemplateId),Json=envelope },tx);
    }
    internal static async Task<Dictionary<string,ReportedPeriodInputProfileV2>> ReadAllAsync(MySqlConnection db,MySqlTransaction tx,IConfiguration configuration,int clientId)
    {
        if(tx.Connection!=db || clientId<=0)throw new InvalidOperationException("Read reported profiles in the exact client transaction.");
        var rows=await db.QueryAsync<Setting>("SELECT ModuleCode Code,IsEnabled Enabled,CAST(SettingsJson AS CHAR) Json FROM modulesettings WHERE client_id=@Client AND ModuleCode LIKE 'reported-input-t:%'",new { Client=clientId },tx);
        var profiles=new Dictionary<string,ReportedPeriodInputProfileV2>(StringComparer.Ordinal);
        foreach(var row in rows)
        {
            var id=row.Code["reported-input-t:".Length..];
            if(!row.Enabled)throw new InvalidOperationException("A reported input declaration is disabled; it cannot fall back to legacy salary inputs.");
            profiles.Add(id,ReportedPeriodInputV2Codec.ReadProfile(row.Json,configuration,clientId,id));
        }
        return profiles;
    }
    internal static async Task<Dictionary<string,ReportedPeriodInputProfileV2>> ReadProfilesAsync(MySqlConnection db,MySqlTransaction tx,IConfiguration configuration,int clientId,IReadOnlyList<string> templateIds)
    {
        if(tx.Connection!=db || clientId<=0)throw new InvalidOperationException("Read reported profiles in the exact client transaction.");
        var keys=templateIds.Distinct(StringComparer.Ordinal).Select(Key).ToArray();
        var profiles=new Dictionary<string,ReportedPeriodInputProfileV2>(StringComparer.Ordinal);
        if(keys.Length==0)return profiles;
        var rows=await db.QueryAsync<Setting>("SELECT ModuleCode Code,IsEnabled Enabled,CAST(SettingsJson AS CHAR) Json FROM modulesettings WHERE client_id=@Client AND ModuleCode IN @Keys",new { Client=clientId,Keys=keys },tx);
        foreach(var row in rows)
        {
            var id=row.Code["reported-input-t:".Length..];
            if(!row.Enabled)throw new InvalidOperationException("A reported input declaration is disabled; it cannot fall back to legacy salary inputs.");
            profiles.Add(id,ReportedPeriodInputV2Codec.ReadProfile(row.Json,configuration,clientId,id));
        }
        return profiles;
    }
    internal sealed class Setting { public string Code {get;set;}=""; public bool Enabled {get;set;} public string Json {get;set;}=""; }
}
