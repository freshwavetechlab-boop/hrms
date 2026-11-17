using Dapper;
using MySqlConnector;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    private async Task LoadReportedPeriodInputsAsync(MySqlConnection db,MySqlTransaction tx,List<PayRunSourceEmployee> employees,string month)
    {
        var ordinary=employees.ToArray();
        if(ordinary.Length==0||!await TableExistsAsync(db,tx,"modulesettings"))return;
        foreach(var group in ordinary.GroupBy(e=>e.ClientId))
        {
            var indices=await ReportedPeriodHistoricalInputStore.ReadMonthIndicesAsync(db,tx,configuration,group.Key,month,group.Select(e=>e.Id).ToArray());
            foreach(var e in group.Where(e=>indices.ContainsKey(e.Id)))
                if(e.LocationParts is not null||e.EffectiveSalaryHistoryId!=indices[e.Id].HistoryId||e.SalaryStructureId!=indices[e.Id].TemplateId)
                    throw new InvalidOperationException("The indexed reported employee/month requires its exact real closed history and ordinary template; removing it cannot restore today's physical salary or location fallback.");
            var templates=group.Select(e=>e.SalaryStructureId).Where(id=>!string.IsNullOrWhiteSpace(id)).Concat(indices.Values.Select(i=>i.TemplateId)).Distinct(StringComparer.Ordinal).ToArray();
            var profiles=await ReportedPeriodInputProfileStore.ReadProfilesAsync(db,tx,configuration,group.Key,templates);
            var historyKeys=group.Where(e=>e.EffectiveSalaryHistoryId>0).Select(e=>ReportedPeriodHistoricalInputStore.Key(e.EffectiveSalaryHistoryId)).Distinct(StringComparer.Ordinal).ToArray();
            var settings=historyKeys.Length==0?[]:(await db.QueryAsync<ReportedPeriodInputProfileStore.Setting>("SELECT ModuleCode Code,IsEnabled Enabled,CAST(SettingsJson AS CHAR) Json FROM modulesettings WHERE client_id=@Client AND ModuleCode IN @Keys",new{Client=group.Key,Keys=historyKeys},tx)).ToArray();
            var manifests=settings.ToDictionary(s=>s.Code,StringComparer.Ordinal);
            var managed=group.Where(e=>indices.ContainsKey(e.Id)||profiles.ContainsKey(e.SalaryStructureId)||manifests.ContainsKey(ReportedPeriodHistoricalInputStore.Key(e.EffectiveSalaryHistoryId))).ToArray();
            if(managed.Length==0)continue;
            if(managed.Any(e=>e.NamedInputs is not null))throw new InvalidOperationException("A salary history cannot mix v1 named operands with a reported-period declaration.");
            foreach(var e in managed)
                if(e.LocationParts is not null||!indices.ContainsKey(e.Id)||e.EffectiveSalaryHistoryId<=0||!profiles.ContainsKey(e.SalaryStructureId)||!manifests.TryGetValue(ReportedPeriodHistoricalInputStore.Key(e.EffectiveSalaryHistoryId),out var setting)||!setting.Enabled)
                    throw new InvalidOperationException("The managed reported template requires its exact enabled closed-history sidecar; missing data never becomes a legacy/current salary fallback.");
            var cache=await ReportedPeriodHistoricalInputStore.CreateReadScopeAsync(db,tx,group.Key,month,managed.Select(e=>e.Id).ToArray(),managed.Select(e=>e.EffectiveSalaryHistoryId).ToArray());
            foreach(var e in managed)
            {
                e.NamedInputs=await ReportedPeriodHistoricalInputStore.LoadAsync(cache,configuration,e.Id,e.EffectiveSalaryHistoryId,e.SalaryStructureId,
                    profiles[e.SalaryStructureId],manifests[ReportedPeriodHistoricalInputStore.Key(e.EffectiveSalaryHistoryId)].Json,indices[e.Id]);
                // Named-only closed history deliberately has no physical amount
                // overrides. Never inherit today's source-imported cash values.
                e.SalaryComponents=ParseSalaryComponentMap(e.SalaryJson);
            }
        }
    }
}
