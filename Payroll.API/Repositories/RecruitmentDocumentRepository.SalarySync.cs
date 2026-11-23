using System.Globalization;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentDocumentRepository
{
    private static decimal RosterSalaryAmount(IReadOnlyDictionary<string, string> values, string key)
    {
        var text = values.GetValueOrDefault(key, "").Trim().Replace(",", "");
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
            && amount >= 0 && amount <= 999999999999.99m && decimal.Round(amount, 2) == amount ? amount : -1;
    }

    internal static Dictionary<string, decimal> RosterMonthlySalary(IReadOnlyDictionary<string, string> values)
    {
        var basic = RosterSalaryAmount(values, "monthlyBasic"); var gross = RosterSalaryAmount(values, "monthlyGross");
        if (basic <= 0 || gross < basic) return [];
        var rateText = values.GetValueOrDefault("hraRate", "").Trim();
        var rate = decimal.TryParse(rateText.TrimEnd('%'), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? rateText.EndsWith('%') || parsed > 1 ? parsed / 100m : parsed : -1;
        var hra = RosterSalaryAmount(values, "monthlyHra");
        if (hra < 0 && rate is >= 0 and <= 1) hra = decimal.Round(basic * rate, 2, MidpointRounding.AwayFromZero);
        if (hra < 0 && gross == basic) hra = 0;
        // A remuneration-only offer does not imply a Basic/HRA allocation.
        if (hra < 0 || basic + hra != gross) return [];
        return new(StringComparer.OrdinalIgnoreCase) { ["BASIC"] = basic, ["HRA"] = hra };
    }

    internal sealed record RosterSalarySeed(decimal AnnualCtc, decimal MonthlyRemuneration, Dictionary<string, decimal> Components);

    internal static bool AnnualizeRosterRemuneration(IConfiguration settings, int clientId) => clientId > 0
        && (settings.GetSection("RecruitmentSalarySync:AnnualizeMonthlyRemunerationClientIds").Get<int[]>() ?? []).Contains(clientId);

    internal static RosterSalarySeed? RosterSalarySeedFor(IReadOnlyList<IReadOnlyDictionary<string, string>> values, bool annualizeRemuneration)
    {
        if (values.Count == 0) return null;
        var gross = values.Select(v => RosterSalaryAmount(v, "monthlyGross")).ToArray();
        // All released assignments must agree, including remuneration-only rows.
        if (gross.Any(v => v <= 0) || gross.Distinct().Count() != 1) return null;
        var plans = values.Select(RosterMonthlySalary).ToList();
        var components = plans.All(p => p.Count > 0) && plans.Select(p => JsonSerializer.Serialize(p, Json)).Distinct().Count() == 1 ? plans[0] : [];
        decimal annualCtc = 0;
        if (annualizeRemuneration)
        {
            // Explicit annual terms take precedence over the approved monthly x 12 fallback.
            var annual = values.Select(v => string.IsNullOrWhiteSpace(v.GetValueOrDefault("annualCtc")) ? gross[0] * 12 : RosterSalaryAmount(v, "annualCtc")).ToArray();
            if (annual.Any(v => v <= 0) || annual.Distinct().Count() != 1) return null;
            annualCtc = annual[0];
        }
        return annualCtc > 0 || components.Count > 0 ? new(annualCtc, gross[0], components) : null;
    }

    private static async Task<Dictionary<string, decimal>> RosterSalaryInputsAsync(MySqlConnection db, MySqlTransaction tx, IReadOnlyDictionary<string, decimal> amounts)
    {
        if (amounts.Count == 0) return [];
        var components = (await db.QueryAsync<RosterSalaryComponent>("SELECT CAST(Id AS CHAR) Id,Code FROM salarycomponents WHERE Active=TRUE AND Category='Earning' AND UPPER(Code) IN ('BASIC','HRA')", transaction: tx)).ToList();
        if (amounts.Any(pair => pair.Value > 0 && components.Count(c => c.Code.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)) != 1)) return [];
        return amounts.Where(pair => pair.Value > 0).ToDictionary(pair => components.Single(c => c.Code.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)).Id, pair => pair.Value);
    }

    // Existing identity links only. Components are initialized only when empty;
    // the configured clients may also initialize a missing annual remuneration base.
    public async Task<int> SyncEmptyEmployeeSalariesAsync(int afterEmployeeId, CancellationToken cancellationToken)
    {
        await using var db = Db(); await db.OpenAsync(cancellationToken);
        var ready = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.columns WHERE TABLE_SCHEMA=DATABASE() AND ((TABLE_NAME='recruitment_document_roster' AND COLUMN_NAME='EmployeeId') OR (TABLE_NAME='recruitment_document_roster_releases' AND COLUMN_NAME='InputsHash'))");
        if (ready != 2) return 0;
        var employees = (await db.QueryAsync<Employee>("""
SELECT e.Id,e.ClientId FROM employees e WHERE e.Id>@AfterId AND e.IsActive=TRUE
 AND EXISTS(SELECT 1 FROM clients client WHERE client.Id=e.ClientId AND client.IsActive=TRUE)
 AND COALESCE(TRIM(e.SalaryStructureId),'')='' AND e.AnnualCtc=0
 AND NOT EXISTS(SELECT 1 FROM recruitment_document_roster supplied JOIN recruitment_candidates sc ON sc.Id=supplied.CandidateId AND sc.ClientId=supplied.ClientId
   WHERE supplied.ClientId=e.ClientId AND COALESCE(supplied.EmployeeId,sc.EmployeeId)=e.Id AND JSON_UNQUOTE(JSON_EXTRACT(supplied.ValuesJson,'$.standardTemplateVersion'))='FREVO-EMPLOYEE-1')
 AND EXISTS(SELECT 1 FROM recruitment_document_roster r JOIN recruitment_candidates c ON c.Id=r.CandidateId AND c.ClientId=r.ClientId
   JOIN recruitment_document_roster_releases rr ON rr.RosterId=r.Id AND rr.ClientId=r.ClientId
   WHERE r.ClientId=e.ClientId AND COALESCE(r.EmployeeId,c.EmployeeId)=e.Id
    AND (r.EmployeeId IS NULL OR c.EmployeeId IS NULL OR r.EmployeeId=c.EmployeeId))
ORDER BY e.Id LIMIT 50
""", new { AfterId = afterEmployeeId })).ToList();
        foreach (var employee in employees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var tx = await db.BeginTransactionAsync(cancellationToken);
            if (await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@ClientId AND IsActive=TRUE FOR UPDATE", employee, tx) is null) continue;
            await db.ExecuteScalarAsync<int>("SELECT Id FROM employees WHERE Id=@Id AND ClientId=@ClientId FOR UPDATE", employee, tx);
            var current = await EmployeeRepository.LoadRecruitmentEmployeeAsync(db, employee.Id, tx);
            if (current is null || current.ClientId != employee.ClientId) continue;
            var rows = (await db.QueryAsync<RecruitmentDocumentRosterRow>(RosterJoiningRowsSql + """
 WHERE r.ClientId=@ClientId AND COALESCE(r.EmployeeId,c.EmployeeId)=@Id AND c.ProfileStatus<>'Archived'
 AND (r.EmployeeId IS NULL OR c.EmployeeId IS NULL OR r.EmployeeId=c.EmployeeId)
 AND EXISTS(SELECT 1 FROM recruitment_document_roster_releases rr WHERE rr.RosterId=r.Id AND rr.ClientId=r.ClientId)
 ORDER BY r.Id FOR UPDATE
""", employee, tx)).ToList();
            // Use the same current Master projection as draft/release/mail, so a
            // corrected and re-released letter becomes eligible automatically.
            await RecruitmentEmployeeMasterSync.ApplyAsync(db, rows, tx);
            var clientName = await db.ExecuteScalarAsync<string>("SELECT Name FROM clients WHERE Id=@ClientId", employee, tx) ?? "";
            var organizationName = await db.ExecuteScalarAsync<string>("SELECT Name FROM organizations ORDER BY Id LIMIT 1", transaction: tx) ?? "";
            var currentReleases = rows.Count > 0 && rows.All(row => row.WorkLocationId == current.WorkLocationId);
            foreach (var row in rows)
            {
                var releasedInputs = await db.ExecuteScalarAsync<string>("SELECT InputsHash FROM recruitment_document_roster_releases WHERE RosterId=@Id AND ClientId=@ClientId ORDER BY ReleasedAt DESC,Id DESC LIMIT 1", row, tx);
                var values = ReadValues(row.ValuesJson);
                if (row.WorkLocationId is not > 0) { currentReleases = false; break; }
                ApplyLocationValues(values, await ActiveLocationAsync(db, row.ClientId, row.WorkLocationId.Value, tx));
                PrepareReleaseValues(row, values, clientName, organizationName, row.OfferNumber);
                if (Hash(SerializeValues(values)) != releasedInputs) { currentReleases = false; break; }
            }
            if (!currentReleases) continue;
            var plan = RosterSalarySeedFor(rows.Select(row => (IReadOnlyDictionary<string, string>)ReadValues(row.ValuesJson)).ToList(), AnnualizeRosterRemuneration(configuration, employee.ClientId));
            if (plan is null) continue;
            var inputs = await RosterSalaryInputsAsync(db, tx, plan.Components);
            if (!await EmployeeRepository.InitializeRecruitmentSalaryAsync(db, tx, current, inputs, plan.AnnualCtc)) continue;
            foreach (var row in rows)
                await db.ExecuteAsync("INSERT INTO recruitment_document_roster_audit(RosterId,ClientId,Action,NewValuesJson,ActorUserId) VALUES(@Id,@ClientId,'Salary synced to Employee Master',@Receipt,0)", new { row.Id, row.ClientId, Receipt = JsonSerializer.Serialize(new { EmployeeId = current.Id, Source = "Released employee-list salary", plan.MonthlyRemuneration, AnnualCtc = current.AnnualCtc, RequestedComponents = plan.Components, SavedComponents = current.SalaryComponents }, Json) }, tx);
            await tx.CommitAsync(cancellationToken);
        }
        return employees.Count == 0 ? 0 : employees[^1].Id;
    }

    private sealed class RosterSalaryComponent { public string Id { get; set; } = ""; public string Code { get; set; } = ""; }
}
