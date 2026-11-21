using System.Globalization;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentDocumentRepository
{
    internal static Dictionary<string, decimal> RosterMonthlySalary(IReadOnlyDictionary<string, string> values)
    {
        decimal Amount(string key)
        {
            var text = values.GetValueOrDefault(key, "").Trim().Replace(",", "");
            return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
                && amount >= 0 && amount <= 999999999999.99m && decimal.Round(amount, 2) == amount ? amount : -1;
        }
        var basic = Amount("monthlyBasic"); var gross = Amount("monthlyGross");
        if (basic <= 0 || gross < basic) return [];
        var rateText = values.GetValueOrDefault("hraRate", "").Trim();
        var rate = decimal.TryParse(rateText.TrimEnd('%'), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? rateText.EndsWith('%') || parsed > 1 ? parsed / 100m : parsed : -1;
        var hra = Amount("monthlyHra");
        if (hra < 0 && rate is >= 0 and <= 1) hra = decimal.Round(basic * rate, 2, MidpointRounding.AwayFromZero);
        if (hra < 0 && gross == basic) hra = 0;
        // Never invent CTC or allocate an unexplained difference to HRA.
        if (hra < 0 || basic + hra != gross) return [];
        return new(StringComparer.OrdinalIgnoreCase) { ["BASIC"] = basic, ["HRA"] = hra };
    }

    private static async Task<Dictionary<string, decimal>> RosterSalaryInputsAsync(MySqlConnection db, MySqlTransaction tx, IReadOnlyDictionary<string, string> values)
    {
        var amounts = RosterMonthlySalary(values);
        if (amounts.Count == 0) return [];
        var components = (await db.QueryAsync<RosterSalaryComponent>("SELECT CAST(Id AS CHAR) Id,Code FROM salarycomponents WHERE Active=TRUE AND Category='Earning' AND UPPER(Code) IN ('BASIC','HRA')", transaction: tx)).ToList();
        if (amounts.Any(pair => pair.Value > 0 && components.Count(c => c.Code.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)) != 1)) return [];
        return amounts.Where(pair => pair.Value > 0).ToDictionary(pair => components.Single(c => c.Code.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)).Id, pair => pair.Value);
    }

    // Existing identity links and wholly empty pay setups only. No hiring,
    // payroll recalculation, configured-pay overwrite or issued-letter edits.
    public async Task<int> SyncEmptyEmployeeSalariesAsync(int afterEmployeeId, CancellationToken cancellationToken)
    {
        await using var db = Db(); await db.OpenAsync(cancellationToken);
        var ready = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.columns WHERE TABLE_SCHEMA=DATABASE() AND ((TABLE_NAME='recruitment_document_roster' AND COLUMN_NAME='EmployeeId') OR (TABLE_NAME='recruitment_document_roster_releases' AND COLUMN_NAME='InputsHash'))");
        if (ready != 2) return 0;
        var employees = (await db.QueryAsync<Employee>("""
SELECT e.Id,e.ClientId FROM employees e WHERE e.Id>@AfterId AND e.IsActive=TRUE
 AND EXISTS(SELECT 1 FROM clients client WHERE client.Id=e.ClientId AND client.IsActive=TRUE)
 AND COALESCE(TRIM(e.SalaryStructureId),'')='' AND e.AnnualCtc=0
 AND NOT EXISTS(SELECT 1 FROM employeesalarycomponents pay WHERE pay.EmployeeId=e.Id AND pay.Amount<>0)
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
            var plans = rows.Select(row => RosterMonthlySalary(ReadValues(row.ValuesJson))).Where(plan => plan.Count > 0).ToList();
            // Conflicting historic assignments require review, never last-row wins.
            if (plans.Count == 0 || plans.Select(plan => JsonSerializer.Serialize(plan, Json)).Distinct().Count() != 1) continue;
            var inputs = await RosterSalaryInputsAsync(db, tx, ReadValues(rows.First(row => RosterMonthlySalary(ReadValues(row.ValuesJson)).Count > 0).ValuesJson));
            if (inputs.Count == 0 || !await EmployeeRepository.InitializeRecruitmentSalaryAsync(db, tx, current, inputs)) continue;
            foreach (var row in rows)
                await db.ExecuteAsync("INSERT INTO recruitment_document_roster_audit(RosterId,ClientId,Action,NewValuesJson,ActorUserId) VALUES(@Id,@ClientId,'Salary synced to Employee Master',@Receipt,0)", new { row.Id, row.ClientId, Receipt = JsonSerializer.Serialize(new { EmployeeId = current.Id, Source = "Released employee-list salary", Monthly = plans[0] }, Json) }, tx);
            await tx.CommitAsync(cancellationToken);
        }
        return employees.Count == 0 ? 0 : employees[^1].Id;
    }

    private sealed class RosterSalaryComponent { public string Id { get; set; } = ""; public string Code { get; set; } = ""; }
}
