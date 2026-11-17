using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class EmployeeRepository
{
    internal sealed class ClosedSalaryHistoryRow
    {
        public long Id { get; set; }
        public int EmployeeId { get; set; }
        public int ClientId { get; set; }
        public string ActionType { get; set; } = "";
        public DateTime EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
        public string Status { get; set; } = "";
        public string SalaryStructureId { get; set; } = "";
        public decimal AnnualCtc { get; set; }
        public string SalaryJson { get; set; } = "{}";
        public string ChangeReason { get; set; } = "";
        public string CreatedBy { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }
    internal sealed record ClosedSalaryHistoryState(int ClientId, string CurrentMasterHash,
        IReadOnlyList<ClosedSalaryHistoryRow> History, IReadOnlyDictionary<long, string> OriginalRowHashes);

    internal static string ClosedSalaryHash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)))));

    internal static async Task<ClosedSalaryHistoryState> ReadClosedSalaryHistoryStateAsync(MySqlConnection db, MySqlTransaction tx,
        int clientId, IReadOnlyCollection<int> employeeIds, bool forUpdate = false)
    {
        var ids = employeeIds.Distinct().Order().ToArray();
        if (clientId <= 0 || ids.Length == 0 || ids.Length > 100 || ids.Any(id => id <= 0))
            throw new InvalidOperationException("Select bounded existing employee IDs for a historical salary review.");
        var suffix = forUpdate ? " FOR UPDATE" : "";
        var employees = (await db.QueryAsync("SELECT * FROM employees WHERE ClientId=@ClientId AND Id IN @Ids" + suffix,
            new { ClientId = clientId, Ids = ids }, tx)).ToArray();
        if (employees.Length != ids.Length) throw new InvalidOperationException("A historical salary employee is missing or outside the selected client.");
        // These are witnesses only. No personal, bank, current salary or future history is rewritten.
        var personal = (await db.QueryAsync("SELECT * FROM employeepersonaldetails WHERE EmployeeId IN @Ids" + suffix, new { Ids = ids }, tx)).ToArray();
        var payments = (await db.QueryAsync("SELECT * FROM employeepaymentdetails WHERE EmployeeId IN @Ids" + suffix, new { Ids = ids }, tx)).ToArray();
        var salaries = (await db.QueryAsync("SELECT * FROM employeesalarycomponents WHERE EmployeeId IN @Ids" + suffix, new { Ids = ids }, tx)).ToArray();
        var history = (await db.QueryAsync<ClosedSalaryHistoryRow>("SELECT * FROM employee_it0008_basic_pay WHERE EmployeeId IN @Ids" + suffix,
            new { Ids = ids }, tx)).OrderBy(row => row.Id).ToArray();
        if (history.Any(row => row.ClientId != clientId)) throw new InvalidOperationException("A historical salary row has inconsistent employee/client ownership.");
        static object[] Stable(dynamic[] rows) => rows.Select(row => ((IDictionary<string, object>)row)
                .OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value))
            .OrderBy(row => JsonSerializer.Serialize(row), StringComparer.Ordinal).Cast<object>().ToArray();
        return new(clientId, ClosedSalaryHash(new { Employees = Stable(employees), Personal = Stable(personal), Payments = Stable(payments), Salaries = Stable(salaries) }),
            history, history.ToDictionary(row => row.Id, row => ClosedSalaryHash(row)));
    }

    internal static ClosedSalaryHistoryRow RequireClosedSalaryBasis(ClosedSalaryHistoryState state, int employeeId,
        DateTime from, DateTime to)
    {
        var effective = state.History.Where(row => row.EmployeeId == employeeId && row.EffectiveFrom.Date <= to.Date
            && (row.EffectiveTo is null || row.EffectiveTo.Value.Date >= from.Date)).OrderByDescending(row => row.EffectiveFrom).ThenByDescending(row => row.Id).ToArray();
        if (effective.Length == 0 || effective[0].AnnualCtc <= 0)
            throw new InvalidOperationException("A positive effective historical annual target is required. Source monthly wages or earned pay are not automatically declared as CTC.");
        if (effective.Any(row => row.EffectiveFrom.Date > from.Date))
            throw new InvalidOperationException("Salary history changes inside this month. Review the dated salary periods; a whole-month override cannot hide an intra-month assignment.");
        if (effective[0].EffectiveTo is { } end && end.Date < to.Date)
            throw new InvalidOperationException("The retained salary basis does not cover the complete closed month. Review its dated salary periods first.");
        return effective[0];
    }

    internal static async Task<long> InsertClosedSalaryHistoryAsync(MySqlConnection db, MySqlTransaction tx,
        ClosedSalaryHistoryState before, int employeeId, string employeeCode, string templateId, decimal retainedAnnualTarget,
        IReadOnlyDictionary<string, decimal> sourceInputs, DateTime from, DateTime to, string reference, string actor)
    {
        if (from.Day != 1 || to.Date != from.AddMonths(1).AddDays(-1).Date || to.Date >= new DateTime(Services.EmployeeEmploymentRules.Today.Year, Services.EmployeeEmploymentRules.Today.Month, 1)
            || string.IsNullOrWhiteSpace(templateId) || retainedAnnualTarget <= 0 || retainedAnnualTarget > 1_000_000_000m || decimal.Round(retainedAnnualTarget, 2) != retainedAnnualTarget
            || sourceInputs.Count > 100 || sourceInputs.Any(pair => pair.Key.Length is < 1 or > 80 || pair.Value < 0
                || pair.Value > 1_000_000_000m || decimal.Round(pair.Value, 4) != pair.Value)
            || reference.Length is < 1 or > 500 || actor.Length is < 1 or > 190)
            throw new InvalidOperationException("Historical salary inserts require an exact closed calendar month and representable reviewed source inputs; no implicit rounding or current-period write is allowed.");
        var basis = RequireClosedSalaryBasis(before, employeeId, from, to);
        if (retainedAnnualTarget != basis.AnnualCtc)
            throw new InvalidOperationException("This lane preserves the effective historical annual target. A different CTC interpretation needs a separate review.");
        if (before.History.Any(row => row.EmployeeId == employeeId && row.ActionType == "Closed Excel Salary" && row.EffectiveFrom.Date == from.Date && row.EffectiveTo?.Date == to.Date))
            throw new InvalidOperationException("A closed Excel salary review already exists for this employee/month. Reopen its receipt; a new request cannot stack another assignment.");
        var salaryJson = JsonSerializer.Serialize(sourceInputs.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value));
        var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO employee_it0008_basic_pay
(EmployeeId,ClientId,ActionType,EffectiveFrom,EffectiveTo,Status,SalaryStructureId,AnnualCtc,SalaryJson,ChangeReason,CreatedBy)
VALUES(@EmployeeId,@ClientId,'Closed Excel Salary',@From,@To,'Historical',@TemplateId,@Target,CAST(@Salary AS JSON),@Reference,@Actor); SELECT LAST_INSERT_ID();",
            new { EmployeeId = employeeId, before.ClientId, From = from.Date, To = to.Date, TemplateId = templateId, Target = retainedAnnualTarget, Salary = salaryJson, Reference = reference, Actor = actor }, tx);
        await db.ExecuteAsync(@"INSERT INTO employee_audit_trail
(EmployeeId,EmployeeCode,ActionType,InfotypeCode,FieldName,OldValue,NewValue,EffectiveFrom,ChangedBy)
VALUES(@EmployeeId,@EmployeeCode,'Closed Excel Salary','0008','Closed period salary',@OldValue,@NewValue,@From,@Actor)",
            new { EmployeeId = employeeId, EmployeeCode = employeeCode, OldValue = JsonSerializer.Serialize(new { basis.SalaryStructureId, basis.AnnualCtc, basis.SalaryJson }),
                NewValue = JsonSerializer.Serialize(new { TemplateId = templateId, RetainedAnnualSalaryTarget = retainedAnnualTarget, SourceInputs = sourceInputs, EffectiveTo = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Reference = reference }),
                From = from.Date, Actor = actor }, tx);
        return id;
    }

    internal static void RequireClosedInitialSalaryWindow(ClosedSalaryHistoryState state,int employeeId,DateTime from,DateTime to,
        decimal sourceAnnual,decimal currentAnnual)
    {
        if(from.Day!=1 || to.Date!=from.AddMonths(1).AddDays(-1).Date || sourceAnnual<0 || sourceAnnual>1_000_000_000m
            || decimal.Round(sourceAnnual,2)!=sourceAnnual || sourceAnnual!=currentAnnual)
            throw new InvalidOperationException("Initial source salary requires an exact closed calendar month and an unrounded source/current annual-target match.");
        if(state.History.Any(row=>row.EmployeeId==employeeId && (row.ClientId!=state.ClientId || row.EffectiveFrom.Date<=to.Date)))
            throw new InvalidOperationException("An earlier, overlapping or superseded basic-pay history already exists. Initial source mode cannot replace a prior salary basis; use the retained-history review.");
    }

    internal static async Task<long> InsertClosedInitialSalaryHistoryAsync(MySqlConnection db,MySqlTransaction tx,
        ClosedSalaryHistoryState before,int employeeId,string employeeCode,string templateId,HistoricalInitialSourceTargetEvidence source,
        IReadOnlyDictionary<string,decimal> inputs,DateTime from,DateTime to,string reference,string actor,NamedFormulaInputContext? namedInputs=null)
    {
        if(namedInputs is not null)Services.ReviewedNamedFormulaInputs.ValidateContext(namedInputs,[]);
        var currentAnnual=await db.ExecuteScalarAsync<decimal>("SELECT AnnualCtc FROM employees WHERE Id=@Id AND ClientId=@ClientId",new{Id=employeeId,before.ClientId},tx);
        RequireClosedInitialSalaryWindow(before,employeeId,from,to,source.AnnualSalaryTarget,currentAnnual);
        if(source.EmployeeId!=employeeId || source.SourceMonthlyBudget<=0 || source.SourceMonthlyBudget*12m!=source.AnnualSalaryTarget
            || source.Meaning!=ExcelPayrollSetupRepository.InitialSourceTargetMeaning || !source.ActualJoiningDateNotEstablished
            || !System.Text.RegularExpressions.Regex.IsMatch(source.SourceHash??"","\\A[a-f0-9]{64}\\z")
            || source.MonthlyCtcColumnIndex is <0 or >1499 || string.IsNullOrWhiteSpace(templateId)
            || to.Date>=new DateTime(Services.EmployeeEmploymentRules.Today.Year,Services.EmployeeEmploymentRules.Today.Month,1)
            || inputs.Count>100 || inputs.Count==0&&namedInputs is null || inputs.Any(p=>p.Key.Length is <1 or >80 || p.Value<0 || p.Value>1_000_000_000m || decimal.Round(p.Value,4)!=p.Value)
            || reference.Length is <1 or >500 || actor.Length is <1 or >190)
            throw new InvalidOperationException("Initial closed salary requires complete reviewed source provenance and literal inputs. No missing/zero basis, current-period mutation or fallback is permitted.");
        var salaryJson=JsonSerializer.Serialize(inputs.OrderBy(p=>p.Key,StringComparer.Ordinal).ToDictionary(p=>p.Key,p=>p.Value));
        var id=await db.ExecuteScalarAsync<long>(@"INSERT INTO employee_it0008_basic_pay
(EmployeeId,ClientId,ActionType,EffectiveFrom,EffectiveTo,Status,SalaryStructureId,AnnualCtc,SalaryJson,ChangeReason,CreatedBy)
VALUES(@EmployeeId,@ClientId,'Closed Excel Salary',@From,@To,'Historical',@TemplateId,@Target,CAST(@Salary AS JSON),@Reference,@Actor);SELECT LAST_INSERT_ID();",
            new{EmployeeId=employeeId,before.ClientId,From=from.Date,To=to.Date,TemplateId=templateId,Target=source.AnnualSalaryTarget,Salary=salaryJson,Reference=reference,Actor=actor},tx);
        await db.ExecuteAsync(@"INSERT INTO employee_audit_trail(EmployeeId,EmployeeCode,ActionType,InfotypeCode,FieldName,OldValue,NewValue,EffectiveFrom,ChangedBy)
VALUES(@EmployeeId,@EmployeeCode,'Closed Excel Salary','0008','Initial closed source salary',@OldValue,@NewValue,@From,@Actor)",
            new{EmployeeId=employeeId,EmployeeCode=employeeCode,OldValue=JsonSerializer.Serialize(new{PriorEffectiveSalaryBasis="Not established",CurrentMasterPreserved=true}),
                NewValue=JsonSerializer.Serialize(new{TemplateId=templateId,InitialSourceTarget=source,SourceInputs=inputs,EffectiveTo=to.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),Reference=reference}),From=from.Date,Actor=actor},tx);
        return id;
    }

    internal static void RequireClosedHistoryPreserved(ClosedSalaryHistoryState before, ClosedSalaryHistoryState after,
        IReadOnlyCollection<long> insertedIds)
    {
        if (before.ClientId != after.ClientId || before.CurrentMasterHash != after.CurrentMasterHash
            || before.OriginalRowHashes.Any(pair => !after.OriginalRowHashes.TryGetValue(pair.Key, out var hash) || hash != pair.Value)
            || !after.OriginalRowHashes.Keys.Except(before.OriginalRowHashes.Keys).Order().SequenceEqual(insertedIds.Order()))
            throw new InvalidOperationException("Current Master or retained salary-history records changed. The historical transaction cannot commit.");
    }
}
