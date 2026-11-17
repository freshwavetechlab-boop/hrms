using Dapper;
using MySqlConnector;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    private async Task LoadHistoricalNamedInputsAsync(MySqlConnection db,MySqlTransaction tx,List<PayRunSourceEmployee> employees,string month)
    {
        employees.ForEach(e=>e.NamedInputs=null);
        var ordinary=employees.Where(e=>e.LocationParts is null&&!string.IsNullOrWhiteSpace(e.SalaryStructureId)).ToArray();
        if(ordinary.Length==0||!await TableExistsAsync(db,tx,"modulesettings"))return;
        foreach(var group in ordinary.GroupBy(e=>e.ClientId))
        {
            var templates=group.Select(e=>e.SalaryStructureId).Distinct(StringComparer.Ordinal).ToArray();
            var profiles=await NamedFormulaInputStore.ReadProfilesAsync(db,tx,configuration,group.Key,templates);
            var keys=group.Where(e=>e.EffectiveSalaryHistoryId>0).Select(e=>HistoricalNamedFormulaInputStore.Key(e.EffectiveSalaryHistoryId)).Distinct().ToArray();
            var rows=keys.Length==0?[]:(await db.QueryAsync<HistoricalNamedSetting>("SELECT ModuleCode Code,IsEnabled Enabled,CAST(SettingsJson AS CHAR) Json FROM modulesettings WHERE client_id=@Client AND ModuleCode IN @Keys",new{Client=group.Key,Keys=keys},tx)).ToArray();
            var manifests=rows.ToDictionary(r=>r.Code,StringComparer.Ordinal);
            foreach(var employee in group)
            {
                profiles.TryGetValue(employee.SalaryStructureId,out var profile);
                var key=HistoricalNamedFormulaInputStore.Key(employee.EffectiveSalaryHistoryId);
                manifests.TryGetValue(key,out var manifest);
                if(profile is null&&manifest is null)continue;
                if(profile is null||manifest is null||!manifest.Enabled||employee.EffectiveSalaryHistoryId<=0)
                    throw new InvalidOperationException("The ordinary salary assignment requires its exact enabled historical named-operand declaration and manifest; missing operands never become zero or CTC aliases.");
                // Reading authenticated existing operands is independent of the
                // default-OFF writer rollout flag. Disabling writes is no bypass.
                employee.NamedInputs=await HistoricalNamedFormulaInputStore.LoadAsync(db,tx,configuration,employee.ClientId,employee.Id,month,
                    employee.EffectiveSalaryHistoryId,employee.SalaryStructureId,profile,manifest.Json);
                // An authenticated named-only history can intentionally contain
                // no component inputs. Do not inherit today's physical inputs.
                employee.SalaryComponents=ParseSalaryComponentMap(employee.SalaryJson);
            }
        }
    }
    private sealed class HistoricalNamedSetting{public string Code{get;set;}="";public bool Enabled{get;set;}public string Json{get;set;}="";}
}
