using System.Globalization;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class EmployeeRepository
{
    private const string SalaryInputDate = "Salary Effective From";
    private const decimal MaxAnnualCtc = 9999999999999999.99m;

    internal static decimal? ReadImportAnnualCtc(List<string> row, Dictionary<string, int> map)
    {
        var annualText = Cell(row, map, "Annual CTC");
        var monthlyText = Cell(row, map, "Monthly CTC");
        var styles = HasHeader(map, SalaryInputDate)
            ? NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint
            : NumberStyles.Number;
        decimal Parse(string value, string label)
        {
            var dot = value.IndexOf('.');
            var unsigned = value.AsSpan().TrimEnd("+-");
            var excessFraction = dot >= 0 && unsigned.Length > dot + 3 && unsigned[(dot + 3)..].IndexOfAnyExcept('0') >= 0;
            if (!decimal.TryParse(value, styles, CultureInfo.InvariantCulture, out var amount)
                || amount < 0 || amount > MaxAnnualCtc || excessFraction || decimal.Round(amount, 2) != amount)
                throw new InvalidOperationException($"{label} must be a non-negative amount with up to 16 integer digits and two decimal places.");
            return amount;
        }
        decimal? annual = annualText.Length == 0 ? null : Parse(annualText, "Annual CTC");
        if (monthlyText.Length == 0) return annual;
        var monthly = Parse(monthlyText, "Monthly CTC");
        if (annual.HasValue)
        {
            var expected = decimal.Round(annual.Value / 12m, 2, MidpointRounding.AwayFromZero);
            if (monthly != expected)
                throw new InvalidOperationException($"Annual CTC and Monthly CTC do not match. Annual CTC {ExcelMasterNumber(annual.Value)} gives Monthly CTC {ExcelMasterNumber(expected)}.");
            return annual;
        }
        var annualized = monthly * 12m;
        if (annualized > MaxAnnualCtc)
            throw new InvalidOperationException("Monthly CTC multiplied by 12 exceeds the maximum Annual CTC of 9999999999999999.99.");
        return annualized;
    }
    private const string AssignSalaryOnly = "Assign Salary Template Only";
    private const string ApplyTemplateForward = "Apply Template Forward";
    private const string AssignTemplatePolicy = "Assign Payroll Template Policy";
    private const string RemoveTestPayroll = "Remove Superseded Test Payroll";
    private const string TestPayslipsField = "removeTestPayslips";
    private const string SalaryInputMarker = "[SALARY_INPUT:";
    private const string BulkSalaryAction = "Bulk Salary Input";
    private static string MissingSalaryBasis(DateTime from, DateTime until) =>
        $"This date falls before the next saved salary record ({until.AddDays(1):yyyy-MM-dd}). No salary basis is effective for {from:yyyy-MM-dd} through {until:yyyy-MM-dd}.";
    private const string ProtectedGapPayroll = "Approved or paid payroll already covers this employee in the affected period. Review the employee’s payroll before changing this assignment.";
    internal const string IsBulkSalaryInputSql = $@"(h.ActionType='{BulkSalaryAction}' OR EXISTS(
 SELECT 1 FROM employee_audit_trail a WHERE a.EmployeeId=h.EmployeeId AND a.InfotypeCode='0008'
 AND a.ActionType='{BulkSalaryAction}' AND a.EffectiveFrom=h.EffectiveFrom
 AND a.FieldName='Salary input version' AND a.NewValue=CAST(h.Id AS CHAR)))";
    internal sealed record SalaryInputColumn(int Index, string Id, string Header);
    internal sealed record BulkSalaryPatch(string TemplateId, DateTime From, decimal? AnnualCtc, Dictionary<string, decimal> Inputs, bool AssignmentOnly = false, bool ForceSalaryDefaults = false, bool IsPolicy = false)
    {
        public bool NewHireDefaults => !AssignmentOnly && AnnualCtc is null && Inputs.Count == 0;
    }
    internal sealed record BulkSalaryRule(string TemplateId, int ClientId, string ClientRef, string ComponentId,
        string Code, string Name, string CalculationType, string Formula)
    {
        public bool Editable => Code.ToUpperInvariant() is not ("GROSS_EARNED" or "NET_PAY" or "EMPLOYER_COST")
            && PayRunRepository.IsEditableTemplateInput(CalculationType, Formula);
    }

    internal static List<SalaryInputColumn> ReadSalaryInputColumns(List<string> headers)
    {
        var columns = new List<SalaryInputColumn>();
        foreach (var (header, index) in headers.Select((header, index) => (header.Trim(), index)))
        {
            var marker = header.LastIndexOf(SalaryInputMarker, StringComparison.OrdinalIgnoreCase);
            if (marker < 0) continue;
            var id = header.EndsWith(']') ? header[(marker + SalaryInputMarker.Length)..^1] : "";
            if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0 ||
                id != number.ToString(CultureInfo.InvariantCulture))
                throw new InvalidOperationException($"Invalid salary input header '{header}'. Map the column to a saved template input again.");
            if (columns.Any(column => column.Id == id))
                throw new InvalidOperationException($"Salary input {id} is mapped more than once.");
            columns.Add(new(index, id, header));
        }
        return columns;
    }

    private static decimal ReadSalaryComponentAmount(string cell, string header)
    {
        const decimal maximum = 1000000000m;
        var point = cell.IndexOf('.');
        var fraction = point < 0 ? ReadOnlySpan<char>.Empty : cell.AsSpan(point + 1);
        // Keeping the fifth digit is sufficient for midpoint-away rounding and avoids
        // a second rounding when an Excel formula cache exceeds .NET decimal precision.
        var exactForRounding = fraction.Length > 5 ? cell.AsSpan(0, point + 6) : cell.AsSpan();
        if (fraction.IndexOfAnyExceptInRange('0', '9') >= 0
            || !decimal.TryParse(exactForRounding, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
            || amount < 0 || amount > maximum
            || ((cell[0] == '-' || amount == maximum) && fraction.IndexOfAnyExcept('0') >= 0))
            throw new InvalidOperationException($"{header} must be a plain number between 0 and 1000000000.");
        return decimal.Round(amount, 4, MidpointRounding.AwayFromZero);
    }

    private static async Task<ILookup<string, BulkSalaryRule>> ReadBulkSalaryRulesAsync(MySqlConnection db, MySqlTransaction? tx,
        int clientId, string[] templateIds, bool forUpdate = false)
    {
        const string sql = @"SELECT CAST(s.Id AS CHAR) TemplateId,s.ClientId,s.ClientRef,CAST(c.Id AS CHAR) ComponentId,c.Code,c.Name,
COALESCE(NULLIF(TRIM(l.CalculationType),''),c.CalculationType) CalculationType,
COALESCE(NULLIF(TRIM(l.Formula),''),NULLIF(TRIM(l.ValueText),''),NULLIF(TRIM(c.Formula),''),c.ValueText,'') Formula
FROM salarystructures s JOIN salarystructurelines l ON l.StructureId=s.Id
JOIN salarycomponents c ON l.ComponentId=CAST(c.Id AS CHAR)
JOIN clients target ON target.Id=@ClientId AND target.IsActive=TRUE
WHERE s.Id IN @Ids AND s.Active=TRUE AND c.Active=TRUE";
        var rows = await db.QueryAsync<BulkSalaryRule>(sql + (forUpdate ? " LOCK IN SHARE MODE" : ""), new { ClientId = clientId, Ids = templateIds }, tx);
        return rows.Where(row => SelectedClientOwnershipStore.ResolveSalaryStructureOwner(row.ClientId, row.ClientRef, clientId) is { } owner
                && (owner == 0 || owner == clientId))
            .ToLookup(row => row.TemplateId, StringComparer.Ordinal);
    }

    internal static BulkSalaryPatch? ReadBulkSalaryPatch(List<string> row, Dictionary<string, int> map,
        List<SalaryInputColumn> columns, ILookup<string, BulkSalaryRule> rules, string importMode = UpdateImportMode)
    {
        var assignmentOnly = HasHeader(map, AssignSalaryOnly);
        var policyHeader = HasHeader(map, AssignTemplatePolicy) ? AssignTemplatePolicy : ApplyTemplateForward;
        var policy = HasHeader(map, policyHeader);
        if (policy && (!assignmentOnly || HasHeader(map, SalaryInputDate) || !Cell(row, map, policyHeader).Equals("TRUE", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Use the employee template assignment action without a Salary Effective From column.");
        if (assignmentOnly)
        {
            if (importMode != UpdateImportMode || !Cell(row, map, AssignSalaryOnly).Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(Cell(row, map, "Employee ID"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0
                || Cell(row, map, "Employee Code").Length == 0)
                throw new InvalidOperationException("A template-only assignment requires update mode, TRUE, and an existing Employee ID and Employee Code.");
            if (HasHeader(map, RemoveTestPayroll) && (!HasHeader(map, AssignTemplatePolicy) || !Cell(row, map, RemoveTestPayroll).Equals("TRUE", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Select the test payroll cleanup option in Bulk assign salary template and preview it again.");
            var allowedHeaders = new[] { "Employee ID", "Employee Code", "Salary Template Id", SalaryInputDate, AssignSalaryOnly, policyHeader, RemoveTestPayroll, "Change Reason" }.Select(Norm).ToHashSet();
            if (map.Keys.Any(header => !allowedHeaders.Contains(header)))
                throw new InvalidOperationException("Template-only assignments cannot change employee details, CTC or component amounts.");
        }
        if (columns.Count == 0 && !HasHeader(map, SalaryInputDate) && !assignmentOnly) return null;
        var inputs = new Dictionary<string, decimal>();
        foreach (var column in columns)
        {
            var cell = row.ElementAtOrDefault(column.Index)?.Trim();
            if (string.IsNullOrEmpty(cell)) continue;
            inputs.Add(column.Id, ReadSalaryComponentAmount(cell, column.Header));
        }
        var from = Cell(row, map, SalaryInputDate);
        var templateId = Cell(row, map, "Salary Template Id");
        var annual = ReadImportAnnualCtc(row, map);
        if (Cell(row, map, "Salary Json").Length > 0 || Cell(row, map, "Salary Template").Length > 0)
            throw new InvalidOperationException("Use the selected salary template and its component columns for this row. Remove the separate Salary Json or Salary Template name mapping.");
        if (!assignmentOnly && inputs.Count == 0 && annual is null)
        {
            if (from.Length == 0 && templateId.Length == 0) return null;
            if (importMode == UpdateImportMode)
                throw new InvalidOperationException("Enter a salary input, Annual CTC or Monthly CTC for this dated assignment.");
        }
        DateTime date;
        if (policy) date = EmployeeEmploymentRules.Today;
        else if (!DateTime.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) || date.Year < 1000)
            throw new InvalidOperationException("Salary Effective From must be a valid yyyy-MM-dd date for mapped salary inputs.");
        var allowed = rules[templateId].ToArray();
        if (allowed.Length == 0 || inputs.Keys.Any(id => allowed.Count(rule => rule.ComponentId == id && rule.Editable) != 1))
            throw new InvalidOperationException("Choose an active salary template for this client and map only its editable inputs. Calculated amounts cannot be imported.");
        return new(templateId, date, annual, inputs, assignmentOnly, IsPolicy: policy);
    }

    private static async Task<List<ClosedSalaryHistoryRow>> ReadBulkSalaryHistoryAsync(MySqlConnection db, MySqlTransaction? tx, int clientId, int[] ids, bool forUpdate = false) =>
        (await db.QueryAsync<ClosedSalaryHistoryRow>($@"SELECT h.Id,h.EmployeeId,h.ClientId,h.EffectiveFrom,h.EffectiveTo,h.Status,h.ActionType,h.SalaryStructureId,h.AnnualCtc,
CAST(h.SalaryJson AS CHAR) SalaryJson,{IsBulkSalaryInputSql} IsBulkSalaryInput FROM employee_it0008_basic_pay h
WHERE h.ClientId=@ClientId AND h.EmployeeId IN @Ids ORDER BY h.EffectiveFrom,h.Id" + (forUpdate ? " FOR UPDATE" : ""), new { ClientId = clientId, Ids = ids }, tx)).ToList();

    internal static ClosedSalaryHistoryRow? EffectiveBulkSalary(IEnumerable<ClosedSalaryHistoryRow> history, DateTime from) =>
        history.Where(row => row.EffectiveFrom <= from && (row.EffectiveTo == null || row.EffectiveTo >= from))
            .OrderByDescending(row => row.EffectiveFrom).ThenByDescending(row => row.Id).FirstOrDefault();

    internal static bool AlreadyAssignedAtDate(List<ClosedSalaryHistoryRow> history, BulkSalaryPatch patch)
    {
        if (!patch.AssignmentOnly) return false;
        var effective = history.Where(row => row.EffectiveFrom <= patch.From && (row.EffectiveTo == null || row.EffectiveTo >= patch.From)).ToList();
        return effective.Count > 0 && effective.All(row => row.SalaryStructureId == patch.TemplateId);
    }

    // The payroll reader uses current Master as a fallback across unversioned gaps and
    // for historical records with empty inputs. Only replace that fallback if every
    // earlier employed day has its own salary snapshot.
    internal static bool CanProjectBulkSalary(Employee current, IEnumerable<ClosedSalaryHistoryRow> history, DateTime from)
    {
        if (from > EmployeeEmploymentRules.Today ||
            !DateTime.TryParseExact(current.DateOfJoining, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var coveredUntil)) return false;
        if (coveredUntil >= from) return true;
        var joined = coveredUntil;
        foreach (var row in history.Where(row => row.EffectiveFrom < from).OrderBy(row => row.EffectiveFrom))
        {
            if (row.EffectiveTo < joined) continue;
            if (row.EffectiveFrom > coveredUntil || !TrySalaryMap(row.SalaryJson ?? "{}", out var inputs, true) || (!row.IsBulkSalaryInput && inputs.Count == 0)) return false;
            if (row.EffectiveTo == null || row.EffectiveTo >= from.AddDays(-1))
                coveredUntil = from;
            else if (row.EffectiveTo >= coveredUntil) coveredUntil = row.EffectiveTo.Value.AddDays(1);
        }
        return coveredUntil >= from;
    }

    // The directory reports the salary effective today. The raw master can still supply earlier payrolls.
    private static async Task<Dictionary<int, ClosedSalaryHistoryRow>> CurrentDirectorySalaryAsync(MySqlConnection db, int? clientId, int? employeeId = null, MySqlTransaction? tx = null)
    {
        var rows = await db.QueryAsync<ClosedSalaryHistoryRow>($@"SELECT h.EmployeeId,h.AnnualCtc,h.SalaryStructureId,CAST(h.SalaryJson AS CHAR) SalaryJson,
{IsBulkSalaryInputSql} IsBulkSalaryInput
FROM employee_it0008_basic_pay h JOIN employees e ON e.Id=h.EmployeeId AND e.ClientId=h.ClientId
WHERE (@ClientId IS NULL OR h.ClientId=@ClientId) AND (@EmployeeId IS NULL OR h.EmployeeId=@EmployeeId)
AND h.EffectiveFrom<=@Today AND (h.EffectiveTo IS NULL OR h.EffectiveTo>=@Today)
AND NOT EXISTS (SELECT 1 FROM employee_it0008_basic_pay n WHERE n.EmployeeId=h.EmployeeId AND n.ClientId=h.ClientId
 AND n.EffectiveFrom<=@Today AND (n.EffectiveTo IS NULL OR n.EffectiveTo>=@Today)
 AND (n.EffectiveFrom>h.EffectiveFrom OR (n.EffectiveFrom=h.EffectiveFrom AND n.Id>h.Id)))",
            new { ClientId = clientId, EmployeeId = employeeId, Today = EmployeeEmploymentRules.Today }, tx);
        return rows.ToDictionary(row => row.EmployeeId);
    }

    private static void ApplyDirectorySalaryView(Employee employee, ClosedSalaryHistoryRow row)
    {
        employee.SalaryStructureId = row.SalaryStructureId;
        employee.UsesDatedSalaryInputs = row.IsBulkSalaryInput;
        // Legacy salary amounts still follow the master fallback. Only a managed row owns its input snapshot.
        if (!row.IsBulkSalaryInput) return;
        employee.AnnualCtc = row.AnnualCtc;
        if (!TrySalaryMap(row.SalaryJson, out var amounts, true)) return;
        employee.SalaryComponents = amounts;
        employee.SalaryJson = row.SalaryJson;
    }

    private static async Task<bool> PrepareDirectorySalaryEditAsync(MySqlConnection db, MySqlTransaction tx, Employee employee, string? infotypeCode, string actor)
    {
        var codes = NormalizeInfotypeCodes(infotypeCode);
        var basicPayOnly = codes?.SetEquals(["0008"]) == true;
        var policy = (await ReadTemplatePoliciesAsync(db, employee.ClientId, [employee.Id], tx, basicPayOnly)).GetValueOrDefault(employee.Id);
        if (basicPayOnly && (policy ?? "") != (employee.SalaryTemplatePolicyId ?? ""))
            throw new InvalidOperationException("This employee’s payroll template changed. Reopen Basic Pay to review the saved assignment.");
        var views = await CurrentDirectorySalaryAsync(db, employee.ClientId, employee.Id, tx);
        views.TryGetValue(employee.Id, out var dated);
        if (dated is null && policy is null && !basicPayOnly) return false;
        var raw = await LoadEmployeeAsync(db, employee.Id, tx) ?? throw new InvalidOperationException("Employee not found.");
        var shown = CloneEmployee(raw);
        if (dated is not null) ApplyDirectorySalaryView(shown, dated);
        if (policy is not null) shown.SalaryStructureId = policy;
        var amountsChanged = employee.AnnualCtc != shown.AnnualCtc || employee.SalaryComponents.Count != shown.SalaryComponents.Count
            || employee.SalaryComponents.Any(pair => !shown.SalaryComponents.TryGetValue(pair.Key, out var value) || value != pair.Value);
        if (basicPayOnly && employee.SalaryStructureId != shown.SalaryStructureId && (policy is not null || !string.IsNullOrWhiteSpace(employee.SalaryStructureId)))
        {
            await ValidateSalaryTemplateAssignmentAsync(db, employee, null, tx);
            await WriteTemplatePolicyAsync(db, tx, employee, policy ?? "", employee.SalaryStructureId, actor);
            if (!amountsChanged) return true;
            employee.SalaryStructureId = raw.SalaryStructureId;
            return false;
        }
        if ((codes is null || codes.Contains("0008")) &&
            (employee.SalaryStructureId != shown.SalaryStructureId || amountsChanged))
        {
            if (policy is not null) employee.SalaryStructureId = raw.SalaryStructureId;
            return false;
        }
        // Edits to other fields can post the entire directory row. Do not promote its
        // effective salary into the legacy fallback as a side effect of that save.
        employee.SalaryStructureId = raw.SalaryStructureId;
        employee.AnnualCtc = raw.AnnualCtc;
        employee.SalaryComponents = raw.SalaryComponents;
        employee.SalaryJson = raw.SalaryJson;
        return false;
    }

    internal static Employee BulkSalaryBasis(Employee current, List<ClosedSalaryHistoryRow> history, BulkSalaryPatch patch)
    {
        var basis = BulkSalaryBasis(current, history, patch, out var gapEnd);
        if (gapEnd.HasValue && !patch.ForceSalaryDefaults) throw new InvalidOperationException(MissingSalaryBasis(patch.From, gapEnd.Value));
        return basis;
    }

    private static Employee BulkSalaryBasis(Employee current, List<ClosedSalaryHistoryRow> history, BulkSalaryPatch patch, out DateTime? gapEnd)
    {
        gapEnd = null;
        var basis = CloneEmployee(current);
        if (basis.SalaryComponents.Count == 0 && !string.IsNullOrWhiteSpace(current.SalaryJson))
        {
            if (!TrySalaryMap(current.SalaryJson, out var masterInputs, true))
                throw new InvalidOperationException("The saved salary inputs cannot be read. Review this employee's Basic Pay.");
            basis.SalaryComponents = masterInputs;
        }
        var effective = EffectiveBulkSalary(history, patch.From);
        if (effective is not null)
        {
            if (patch.AssignmentOnly && history.Any(row => row.Id != effective.Id && row.EffectiveFrom <= patch.From &&
                (row.EffectiveTo == null || row.EffectiveTo >= patch.From)))
                throw new InvalidOperationException("More than one salary record is effective on this date. Review the overlapping Basic Pay assignments for this employee.");
            if (effective.ActionType == "Closed Excel Salary")
                throw new InvalidOperationException("This effective date belongs to a reviewed, closed salary record. Choose a date after its closed period.");
            basis.AnnualCtc = effective.AnnualCtc;
            basis.SalaryStructureId = effective.SalaryStructureId;
            if (!TrySalaryMap(string.IsNullOrWhiteSpace(effective.SalaryJson) ? "{}" : effective.SalaryJson, out var values, true))
                throw new InvalidOperationException("The effective salary inputs cannot be read. Review this employee's Basic Pay history.");
            if (effective.IsBulkSalaryInput || values.Count > 0) basis.SalaryComponents = values;
        }
        else if (history.Any(row => row.EffectiveFrom > patch.From && (patch.AssignmentOnly || row.ActionType != BulkSalaryAction)))
        {
            if (patch.AssignmentOnly && (basis.AnnualCtc != 0 || basis.SalaryComponents.Count > 0))
                gapEnd = history.Where(row => row.EffectiveFrom > patch.From).Min(row => row.EffectiveFrom).AddDays(-1);
            // No salary effective yet; do not populate an earlier period from a later assignment.
            basis.SalaryStructureId = "";
            basis.AnnualCtc = 0;
            basis.SalaryComponents = [];
        }
        basis.SalaryJson = JsonSerializer.Serialize(basis.SalaryComponents);
        return basis;
    }

    private static async Task<ILookup<int, string>> ReadProtectedGapPayrollAsync(MySqlConnection db, MySqlTransaction? tx,
        int clientId, int[] ids, DateTime from, DateTime until, bool forUpdate = false)
    {
        var rows = await db.QueryAsync<(int EmployeeId, string PayPeriod)>(@"SELECT e.EmployeeId,r.PayPeriod
FROM payruns r JOIN payrunemployees e ON e.PayRunId=r.Id
WHERE r.ClientId=@clientId AND e.EmployeeId IN @ids AND r.PayPeriod BETWEEN @FirstMonth AND @LastMonth
AND r.Status IN ('Approved','Partially Paid','Paid') AND e.IsSkipped=FALSE" + (forUpdate ? " FOR UPDATE" : ""),
            new { clientId, ids, FirstMonth = from.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                LastMonth = until.ToString("yyyy-MM", CultureInfo.InvariantCulture) }, tx);
        return rows.ToLookup(row => row.EmployeeId, row => row.PayPeriod);
    }

    private static bool CoversPayrollMonth(IEnumerable<string> months, DateTime from, DateTime until)
    {
        var first = from.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var last = until.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        return months.Any(month => string.CompareOrdinal(month, first) >= 0 && string.CompareOrdinal(month, last) <= 0);
    }

    internal static Dictionary<string, decimal> MergeBulkSalaryInputs(IReadOnlyDictionary<string, decimal> basis, BulkSalaryPatch patch,
        IReadOnlyDictionary<string, BulkSalaryRule> rules)
    {
        var merged = new Dictionary<string, decimal>(basis, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, amount) in patch.Inputs)
        {
            var code = rules[id].Code;
            if (!string.Equals(code, id, StringComparison.OrdinalIgnoreCase)) merged.Remove(code);
            merged[id] = amount;
        }
        return merged;
    }

    private static string BulkSalaryWitness(Employee employee, List<ClosedSalaryHistoryRow> history) =>
        ClosedSalaryHash(new { employee.ClientId, employee.SalaryStructureId, employee.AnnualCtc,
            LegacyInputs = employee.SalaryComponents.Count == 0 ? employee.SalaryJson : null,
            Inputs = employee.SalaryComponents.OrderBy(pair => pair.Key, StringComparer.Ordinal), History = history });

    private static string SalaryTemplateWitness(IEnumerable<BulkSalaryRule> rules) =>
        ClosedSalaryHash(rules.OrderBy(rule => rule.ComponentId, StringComparer.Ordinal));

    private static List<EmployeeImportFieldChange> BulkSalaryChanges(List<EmployeeImportFieldChange> original, Employee basis,
        BulkSalaryPatch patch, ILookup<string, BulkSalaryRule> rules)
    {
        var changes = original.Where(c => c.Field is not ("salaryTemplateId" or "annualCtc")).ToList();
        void Add(string field, string label, string oldValue, string newValue)
        {
            if (oldValue != newValue) changes.Add(new(field, $"{label} (from {patch.From:yyyy-MM-dd})", oldValue, newValue, false, true));
        }
        Add("salaryTemplateId", "Salary Template Id", basis.SalaryStructureId ?? "", patch.TemplateId);
        if (patch.AnnualCtc is { } annual) Add("annualCtc", "Annual CTC", ExcelMasterNumber(basis.AnnualCtc), ExcelMasterNumber(annual));
        foreach (var rule in rules[patch.TemplateId].Where(rule => patch.Inputs.ContainsKey(rule.ComponentId)))
        {
            var existing = basis.SalaryComponents.Where(p => p.Key.Equals(rule.ComponentId, StringComparison.OrdinalIgnoreCase) ||
                p.Key.Equals(rule.Code, StringComparison.OrdinalIgnoreCase)).Select(p => ExcelMasterNumber(p.Value)).Distinct().ToArray();
            Add("salaryInput:" + rule.ComponentId, $"{rule.Name} ({rule.Code})", string.Join(" / ", existing), ExcelMasterNumber(patch.Inputs[rule.ComponentId]));
        }
        return changes;
    }

    private static void ValidateBulkSalaryDate(string joiningDate, BulkSalaryPatch patch)
    {
        if (DateTime.TryParse(joiningDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var joined) && joined.Date > patch.From)
            throw new InvalidOperationException($"Salary Effective From cannot be earlier than the employee's joining date ({joined:yyyy-MM-dd}).");
    }

    private static async Task<HashSet<int>> EligibleAssignmentEmployeesAsync(MySqlConnection db, MySqlTransaction? tx,
        int clientId, int[] ids, DateTime from)
    {
        var sql = "SELECT e.Id FROM employees e WHERE e.ClientId=@ClientId AND e.Id IN @Ids AND "
            + EmployeeEmploymentRules.EligibleOn("e", "@Today") + " AND " + EmployeeEmploymentRules.EligibleOn("e", "@From");
        return (await db.QueryAsync<int>(sql, new { ClientId = clientId, Ids = ids, From = from, Today = EmployeeEmploymentRules.Today }, tx)).ToHashSet();
    }

    private static int AssignmentEmployeeId(List<string> row, Dictionary<string, int> map) =>
        int.TryParse(Cell(row, map, "Employee ID"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : 0;

    private static async Task<List<Employee>> ReadTemplateAssignmentTargetsAsync(MySqlConnection db, int clientId, int[] ids, MySqlTransaction? tx, bool policyOnly)
    {
        if (ids.Length == 0) return [];
        var rows = (await db.QueryAsync<Employee>(@"SELECT Id,ClientId,EmployeeCode,FirstName,LastName,DateOfJoining,SalaryStructureId,AnnualCtc,SalaryJson
FROM employees WHERE ClientId=@clientId AND Id IN @ids" + (tx is null ? "" : " FOR UPDATE"), new { clientId, ids }, tx)).ToList();
        if (!policyOnly) await PayrollDataTableStore.ApplyEmployeeSalaryTableAsync(db, rows, tx);
        return rows;
    }

    // The picker already identified the target by its employee id in this client.
    // The registered code is a guard against stale selections, never a fallback lookup.
    internal static List<EmployeeImportPreflightRow> BuildTemplateAssignmentRows(List<List<string>> rows,
        Dictionary<string, int> map, string sheet, IReadOnlyList<Employee> employees)
    {
        var byId = employees.ToDictionary(employee => employee.Id);
        var duplicates = rows.Skip(1).Select(row => AssignmentEmployeeId(row, map)).Where(id => id > 0)
            .GroupBy(id => id).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        var result = new List<EmployeeImportPreflightRow>();
        for (var index = 1; index < rows.Count; index++)
        {
            var source = rows[index];
            if (Blank(source)) continue;
            var id = AssignmentEmployeeId(source, map);
            var code = Cell(source, map, "Employee Code");
            byId.TryGetValue(id, out var employee);
            var error = employee is null ? "The selected employee is no longer in this client. Reopen Employee Master and select them again."
                : code.Length == 0 || !code.Equals(employee.EmployeeCode, StringComparison.OrdinalIgnoreCase) ? "The registered employee code has changed. Reopen Employee Master to use the current code."
                : duplicates.Contains(id) ? "The same employee is selected more than once. Include this employee only once."
                : null;
            var matched = employee != null && error is null;
            result.Add(new(index + 1, sheet, code, matched ? "MatchedByEmployeeId" : "Blocked",
                matched ? id : null, matched ? employee!.EmployeeCode : null, matched ? $"{employee!.FirstName} {employee.LastName}".Trim() : null,
                matched ? ["Exact Employee ID", "Exact Employee Code"] : [], error is null ? [] : [error], []));
        }
        return result;
    }

    private static async Task<EmployeeImportResult> ImportTemplateAssignmentsAsync(MySqlConnection db, int clientId,
        EmployeeImportWorkbook workbook, List<List<string>> rows, string importMode, Func<int, int, int, Task>? progress)
    {
        var total = CountImportRows(workbook);
        if (total == 0) return new(0, 0, 0, []);
        var map = HeaderMap(rows[0]);
        if (HasHeader(map, AssignTemplatePolicy) || HasHeader(map, ApplyTemplateForward))
            await EnsureTemplatePolicyAvailableForWriteAsync(db);
        if (HasHeader(map, RemoveTestPayroll))
            return await ImportReviewedTestPayrollCleanupAsync(db, clientId, workbook, rows, map, importMode);
        var templateIds = rows.Skip(1).Select(row => Cell(row, map, "Salary Template Id")).Distinct().ToArray();
        var rules = await ReadBulkSalaryRulesAsync(db, null, clientId, templateIds);
        var updated = 0;
        var completed = 0;
        var errors = new List<string>();
        for (var index = 1; index < rows.Count; index++)
        {
            var source = rows[index];
            if (Blank(source)) continue;
            var code = Cell(source, map, "Employee Code");
            try
            {
                var patch = ReadBulkSalaryPatch(source, map, [], rules, importMode)
                    ?? throw new InvalidOperationException("Choose an active salary template and effective date.");
                patch = patch with { ForceSalaryDefaults = workbook.ForceSalaryDefaults };
                var employee = new Employee { Id = AssignmentEmployeeId(source, map), ClientId = clientId, EmployeeCode = code };
                var draft = new EmployeeImportDraft(employee, index + 1) { SalaryPatch = patch };
                var reason = Cell(source, map, "Change Reason");
                if (reason.Length > 0) draft.ChangeReason = reason;
                await using var tx = await db.BeginTransactionAsync();
                await db.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=@clientId FOR UPDATE", new { clientId }, tx);
                await SaveBulkSalaryDraftAsync(db, tx, draft, workbook, null);
                await tx.CommitAsync();
                if (draft.SalaryPatch != null) updated++;
            }
            catch (InvalidOperationException error) { errors.Add($"{code}: {error.Message}"); }
            catch (Exception error) { errors.Add($"{code}: {error.Message}"); break; }
            completed++;
            if (progress is not null && (completed == total || completed % 25 == 0)) await progress(completed, 0, updated);
        }
        return new(total, 0, updated, errors) { ProcessedRows = completed };
    }

    private static async Task<EmployeeImportResult> ImportReviewedTestPayrollCleanupAsync(MySqlConnection db, int clientId,
        EmployeeImportWorkbook workbook, List<List<string>> rows, Dictionary<string, int> map, string importMode)
    {
        var actor = workbook.CleanupActor ?? throw new InvalidOperationException("An administrator must review test payroll removal.");
        var sources = rows.Skip(1).Where(row => !Blank(row)).ToList();
        var ids = sources.Select(row => AssignmentEmployeeId(row, map)).ToArray();
        var templates = sources.Select(row => Cell(row, map, "Salary Template Id")).Distinct().ToArray();
        if (ids.Length != ids.Distinct().Count() || templates.Length != 1 || !HasHeader(map, AssignTemplatePolicy))
            throw new InvalidOperationException("Select each employee once and review one payroll template.");
        var templateId = templates[0];
        // Keep an entire reviewed roster atomic. Some payslip designs sign the
        // whole run, and a partial commit would invalidate the other employees.
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        await db.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=@clientId AND IsActive=TRUE FOR UPDATE", new { clientId }, tx);
        var rules = await ReadBulkSalaryRulesAsync(db, tx, clientId, templates, true);
        if (!workbook.SalaryTemplateWitnesses.TryGetValue(templateId, out var witness) || witness != SalaryTemplateWitness(rules[templateId]))
            throw new InvalidOperationException("The template changed after review. Preview the assignment again.");
        var employees = (await ReadTemplateAssignmentTargetsAsync(db, clientId, ids, tx, true)).ToDictionary(row => row.Id);
        var eligible = await EligibleAssignmentEmployeesAsync(db, tx, clientId, ids, EmployeeEmploymentRules.Today);
        var policies = await ReadTemplatePoliciesAsync(db, clientId, ids, tx, true);
        foreach (var source in sources)
        {
            var patch = ReadBulkSalaryPatch(source, map, [], rules, importMode);
            var id = AssignmentEmployeeId(source, map);
            var code = Cell(source, map, "Employee Code");
            if (patch?.IsPolicy != true || !employees.TryGetValue(id, out var employee) || !eligible.Contains(id)
                || !code.Equals(employee.EmployeeCode, StringComparison.OrdinalIgnoreCase)
                || !workbook.TemplatePolicyWitnesses.TryGetValue(id, out var prior) || prior != policies.GetValueOrDefault(id, ""))
                throw new InvalidOperationException($"{code}: the employee or template assignment changed. Preview the selection again.");
        }
        var updated = await PayRunRepository.RemoveReviewedTestPayslipsAsync(db, tx, clientId, ids, templateId, actor, workbook.TestPayrollReviews);
        foreach (var employee in employees.Values)
        {
            var previous = policies.GetValueOrDefault(employee.Id, "");
            if (previous == templateId) continue;
            await WriteTemplatePolicyAsync(db, tx, employee, previous, templateId, actor);
            updated.Add(employee.Id);
        }
        await tx.CommitAsync();
        return new(sources.Count, 0, updated.Count, []) { ProcessedRows = sources.Count };
    }

    private static async Task ReviewBulkSalaryAsync(MySqlConnection db, MySqlTransaction? tx, int clientId, EmployeeImportWorkbook workbook,
        List<List<string>> rows, Dictionary<string, int> map, List<Employee> employees, List<EmployeeImportPreflightRow> reviews, string importMode)
    {
        if (!HasHeader(map, AssignSalaryOnly) && !HasHeader(map, SalaryInputDate) && !rows[0].Any(header => header.Contains(SalaryInputMarker, StringComparison.OrdinalIgnoreCase))) return;
        List<SalaryInputColumn> columns;
        try { columns = ReadSalaryInputColumns(rows[0]); }
        catch (InvalidOperationException error)
        {
            for (var i = 0; i < reviews.Count; i++) reviews[i] = BlockSalaryRow(reviews[i], error.Message);
            return;
        }
        var templateIds = rows.Skip(1).Select(row => Cell(row, map, "Salary Template Id")).Where(id => id.Length > 0).Distinct().ToArray();
        var rules = await ReadBulkSalaryRulesAsync(db, tx, clientId, templateIds);
        foreach (var template in rules) workbook.SalaryTemplateWitnesses[template.Key] = SalaryTemplateWitness(template);
        var employeesById = employees.ToDictionary(employee => employee.Id);
        var ids = reviews.SelectMany(review => (review.CandidateEmployees ?? []).Select(c => c.EmployeeId)
            .Append(review.MatchedEmployeeId ?? 0)).Where(id => id > 0).Distinct().ToArray();
        var isPolicy = HasHeader(map, AssignTemplatePolicy) || HasHeader(map, ApplyTemplateForward);
        var history = (isPolicy ? [] : await ReadBulkSalaryHistoryAsync(db, tx, clientId, ids)).ToLookup(item => item.EmployeeId);
        var policies = isPolicy ? await ReadTemplatePoliciesAsync(db, clientId, ids, tx) : [];
        if (workbook.CleanupActor is not null)
        {
            if (templateIds.Length != 1)
            {
                for (var i = 0; i < reviews.Count; i++) reviews[i] = BlockSalaryRow(reviews[i], "Choose one salary template for this cleanup.");
                return;
            }
            var cleanup = await PayRunRepository.ReviewTemplateCleanupAsync(db, tx, clientId, ids);
            foreach (var (id, entry) in cleanup) workbook.TestPayrollReviews[id] = entry;
        }
        var eligibleByDate = new Dictionary<DateTime, HashSet<int>>();
        var salaryPeriods = new List<(int Index, int EmployeeId, DateTime From, DateTime Until, bool NeedsForce)>();
        for (var i = 0; i < reviews.Count; i++)
        {
            var review = reviews[i];
            if (HasHeader(map, AssignSalaryOnly) && review.BlockingReasons.Count > 0) continue;
            var source = rows[review.RowNumber - 1];
            try
            {
                var patch = ReadBulkSalaryPatch(source, map, columns, rules, importMode);
                if (patch is null) continue;
                if (patch.AssignmentOnly)
                {
                    if (!eligibleByDate.TryGetValue(patch.From, out var eligible))
                        eligibleByDate[patch.From] = eligible = await EligibleAssignmentEmployeesAsync(db, tx, clientId, ids, patch.From);
                    var employeeId = AssignmentEmployeeId(source, map);
                    ValidateBulkSalaryDate(employeesById[employeeId].DateOfJoining, patch);
                    if (!eligible.Contains(employeeId))
                        throw new InvalidOperationException("This employee must be active for this client now and on the selected effective date.");
                }
                if (!patch.NewHireDefaults || review.MatchedEmployeeId is null)
                    ValidateBulkSalaryDate(Cell(source, map, "Date Of Joining"), patch);
                List<EmployeeImportFieldChange> Changes(int id, List<EmployeeImportFieldChange> prior)
                {
                    if (patch.NewHireDefaults) return prior.Where(change => change.Field is not ("salaryTemplateId" or "annualCtc")).ToList();
                    var employee = employeesById[id];
                    ValidateBulkSalaryDate(Cell(source, map, "Date Of Joining") is { Length: > 0 } joining ? joining : employee.DateOfJoining, patch);
                    if (patch.IsPolicy)
                    {
                        var previous = policies.GetValueOrDefault(id, "");
                        workbook.TemplatePolicyWitnesses[id] = previous;
                        var cleanup = workbook.TestPayrollReviews.GetValueOrDefault(id);
                        if (cleanup?.Blocker is string blocker) throw new InvalidOperationException(blocker);
                        List<EmployeeImportFieldChange> changes = previous == patch.TemplateId ? [] : [new("salaryTemplateId", "Employee payroll template", previous, patch.TemplateId, false, true)];
                        var count = cleanup?.Payslips.Count ?? 0;
                        if (count > 0) changes.Add(new(TestPayslipsField, "Old test payslips", count.ToString(CultureInfo.InvariantCulture), "0", false, true));
                        return changes;
                    }
                    var versions = history[id].ToList();
                    workbook.SalaryWitnesses[id] = BulkSalaryWitness(employee, versions);
                    if (AlreadyAssignedAtDate(versions, patch)) return [];
                    var basis = BulkSalaryBasis(employee, versions, patch, out var gapEnd);
                    if (gapEnd.HasValue) salaryPeriods.Add((i, id, patch.From, gapEnd.Value, true));
                    if (patch.AssignmentOnly && EffectiveBulkSalary(versions, patch.From) is null) basis.SalaryStructureId = "";
                    return BulkSalaryChanges(prior, basis, patch, rules);
                }
                reviews[i] = review with {
                    Changes = review.MatchedEmployeeId is { } id ? Changes(id, review.Changes) : review.Changes,
                    TestPayslips = review.MatchedEmployeeId is { } target ? workbook.TestPayrollReviews.GetValueOrDefault(target)?.Payslips : null,
                    CandidateEmployees = review.CandidateEmployees?.Select(candidate => candidate with { Changes = Changes(candidate.EmployeeId, candidate.Changes) }).ToList()
                };
            }
            catch (InvalidOperationException error) { reviews[i] = BlockSalaryRow(review, error.Message); }
        }
        if (salaryPeriods.Count == 0) return;
        var lockedPeriods = await ReadProtectedGapPayrollAsync(db, tx, clientId, salaryPeriods.Select(period => period.EmployeeId).Distinct().ToArray(),
            salaryPeriods.Min(period => period.From), salaryPeriods.Max(period => period.Until));
        foreach (var periods in salaryPeriods.GroupBy(period => period.Index))
        {
            var review = reviews[periods.Key];
            if (review.BlockingReasons.Count > 0) continue;
            if (periods.Any(p => CoversPayrollMonth(lockedPeriods[p.EmployeeId], p.From, p.Until)))
                reviews[periods.Key] = BlockSalaryRow(review, ProtectedGapPayroll);
            else if (!workbook.ForceSalaryDefaults && periods.FirstOrDefault(p => p.NeedsForce) is { NeedsForce: true } gap)
                reviews[periods.Key] = BlockSalaryRow(review, MissingSalaryBasis(gap.From, gap.Until)) with { CanForceSalaryAssignment = true };
        }
    }

    private static EmployeeImportPreflightRow BlockSalaryRow(EmployeeImportPreflightRow row, string error) =>
        row with { MatchStatus = "Blocked", BlockingReasons = row.BlockingReasons.Append(error).Distinct().ToList(), CanResolveConflict = false, CanGenerateEmployeeCode = false };

    private static async Task SaveBulkSalaryDraftAsync(MySqlConnection db, MySqlTransaction tx, EmployeeImportDraft draft,
        EmployeeImportWorkbook workbook, string? infotypes)
    {
        var employee = draft.Employee;
        var patch = draft.SalaryPatch!;
        var rules = (await ReadBulkSalaryRulesAsync(db, tx, employee.ClientId, [patch.TemplateId], true))[patch.TemplateId]
            .GroupBy(rule => rule.ComponentId).Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single());
        if (rules.Count == 0 || patch.Inputs.Keys.Any(id => !rules.TryGetValue(id, out var rule) || !rule.Editable)
            || !workbook.SalaryTemplateWitnesses.TryGetValue(patch.TemplateId, out var templateWitness)
            || templateWitness != SalaryTemplateWitness(rules.Values))
            throw new InvalidOperationException("The selected salary template or an input changed. Review the workbook again.");
        ValidateBulkSalaryDate(employee.DateOfJoining, patch);
        var isNew = employee.Id == 0;
        var current = patch.AssignmentOnly
            ? (await ReadTemplateAssignmentTargetsAsync(db, employee.ClientId, [employee.Id], tx, patch.IsPolicy)).SingleOrDefault()
                ?? throw new InvalidOperationException("This employee is no longer in the selected client.")
            : isNew ? employee : await ReadExcelMasterTargetAsync(db, tx, employee.Id, employee.ClientId, lockRow: true);
        if (patch.AssignmentOnly)
        {
            if (isNew || !current.EmployeeCode.Equals(employee.EmployeeCode, StringComparison.OrdinalIgnoreCase) ||
                !(await EligibleAssignmentEmployeesAsync(db, tx, employee.ClientId, [employee.Id], patch.From)).Contains(employee.Id))
                throw new InvalidOperationException("This employee is no longer eligible for the reviewed template assignment. Run preflight again.");
            ValidateBulkSalaryDate(current.DateOfJoining, patch);
        }
        if (patch.IsPolicy)
        {
            var previous = (await ReadTemplatePoliciesAsync(db, current.ClientId, [current.Id], tx, true)).GetValueOrDefault(current.Id, "");
            if (!workbook.TemplatePolicyWitnesses.TryGetValue(current.Id, out var reviewed))
                throw new InvalidOperationException("Review this employee's payroll template before assigning it.");
            if (previous == patch.TemplateId) { draft.SalaryPatch = null; return; }
            if (previous != reviewed) throw new InvalidOperationException("This employee's payroll template changed after review. Refresh Employee Master and review again.");
            await WriteTemplatePolicyAsync(db, tx, current, previous, patch.TemplateId, workbook.AssignmentActor);
            return;
        }
        var history = isNew ? [] : await ReadBulkSalaryHistoryAsync(db, tx, employee.ClientId, [employee.Id], true);
        if (!isNew && (!workbook.SalaryWitnesses.TryGetValue(current.Id, out var expected) || expected != BulkSalaryWitness(current, history)))
            throw new InvalidOperationException($"Salary data changed for {employee.EmployeeCode}. Run preflight again.");
        if (AlreadyAssignedAtDate(history, patch)) { draft.SalaryPatch = null; return; }
        var basis = BulkSalaryBasis(current, history, patch, out var gapEnd);
        var actor = "Bulk Upload";
        if (gapEnd.HasValue)
        {
            if (!patch.ForceSalaryDefaults || !workbook.ForceSalaryDefaults || string.IsNullOrWhiteSpace(workbook.ForceSalaryActor))
                throw new InvalidOperationException(MissingSalaryBasis(patch.From, gapEnd.Value));
            var protectedPeriods = await ReadProtectedGapPayrollAsync(db, tx, current.ClientId, [current.Id], patch.From, gapEnd.Value, true);
            if (protectedPeriods.Contains(current.Id)) throw new InvalidOperationException(ProtectedGapPayroll);
            actor = workbook.ForceSalaryActor;
            draft.ChangeReason = $"Admin force assignment with zero CTC and template defaults through {gapEnd.Value:yyyy-MM-dd}.";
        }

        // Other imported fields follow the standard import flow and keep their own effective dates.
        // A new hire gets its first IT0008 on the reviewed effective date.
        employee.SalaryComponents = new(current.SalaryComponents);
        employee.SalaryStructureId = current.SalaryStructureId;
        employee.SalaryJson = current.SalaryJson;
        employee.AnnualCtc = current.AnnualCtc;
        if (isNew || draft.Infotypes.Count > 0)
            await SaveWithOpenConnectionAsync(db, employee, "Bulk Upload", infotypes, draft.ChangeReason, tx, datedSalaryForHire: isNew);

        var salary = CloneEmployee(employee);
        salary.SalaryStructureId = patch.TemplateId;
        salary.AnnualCtc = patch.AnnualCtc ?? basis.AnnualCtc;
        salary.SalaryComponents = patch.AssignmentOnly ? new(basis.SalaryComponents) : MergeBulkSalaryInputs(basis.SalaryComponents, patch, rules);
        salary.SalaryJson = JsonSerializer.Serialize(salary.SalaryComponents);
        var effective = EffectiveBulkSalary(history, patch.From);
        // A legacy amount can be a cached calculation, even when it equals the uploaded value.
        // Record the first explicit input from this date so payroll can distinguish it from a default.
        if (effective?.IsBulkSalaryInput == true && basis.SalaryStructureId == salary.SalaryStructureId && basis.AnnualCtc == salary.AnnualCtc
            && basis.SalaryComponents.Count == salary.SalaryComponents.Count && basis.SalaryComponents.All(pair =>
                salary.SalaryComponents.TryGetValue(pair.Key, out var amount) && amount == pair.Value))
        {
            draft.SalaryPatch = null;
            return;
        }

        var next = history.Where(item => item.EffectiveFrom > patch.From).Select(item => (DateTime?)item.EffectiveFrom).Min();
        var end = next?.AddDays(-1);
        if (effective?.EffectiveTo is { } priorEnd && (!end.HasValue || priorEnd < end)) end = priorEnd;
        var args = new { Action = BulkSalaryAction, HistoryId = effective?.Id, EmployeeId = salary.Id, salary.ClientId, salary.EmployeeCode, patch.From, End = end,
            Until = patch.From.AddDays(-1), Status = end is null ? "Active" : "Historical",
            salary.SalaryStructureId, salary.AnnualCtc, salary.SalaryJson, draft.ChangeReason, Actor = actor };
        if (effective?.EffectiveFrom == patch.From)
        {
            // Preserve the stable IT0008 id used by existing dated payroll evidence.
            // A legacy empty snapshot may have been using nonempty employee-level amounts.
            // Capture that existing fallback before making this date authoritative.
            var inherited = patch.AssignmentOnly && !effective.IsBulkSalaryInput && basis.SalaryComponents.Count > 0 &&
                TrySalaryMap(string.IsNullOrWhiteSpace(effective.SalaryJson) ? "{}" : effective.SalaryJson, out var previousInputs, true) && previousInputs.Count == 0;
            var assignments = patch.AssignmentOnly && !inherited ? "SalaryStructureId=@SalaryStructureId" :
                "SalaryStructureId=@SalaryStructureId,AnnualCtc=@AnnualCtc,SalaryJson=@SalaryJson";
            await db.ExecuteAsync("UPDATE employee_it0008_basic_pay SET " + assignments +
                " WHERE Id=@HistoryId AND ClientId=@ClientId AND EmployeeId=@EmployeeId", args, tx);
            await db.ExecuteAsync(@"INSERT INTO employee_audit_trail
(EmployeeId,EmployeeCode,ActionType,InfotypeCode,FieldName,OldValue,NewValue,EffectiveFrom,ChangedBy)
VALUES(@EmployeeId,@EmployeeCode,@Action,'0008','Salary input version','',@HistoryId,@From,'Bulk Upload')", args, tx);
        }
        else
        {
            if (effective is not null)
                await db.ExecuteAsync(@"UPDATE employee_it0008_basic_pay SET Status='Historical',EffectiveTo=@Until
WHERE Id=@HistoryId AND ClientId=@ClientId AND EmployeeId=@EmployeeId", args, tx);
            await db.ExecuteAsync(@"INSERT INTO employee_it0008_basic_pay
(EmployeeId,ClientId,ActionType,EffectiveFrom,EffectiveTo,Status,SalaryStructureId,AnnualCtc,SalaryJson,ChangeReason,CreatedBy)
VALUES(@EmployeeId,@ClientId,@Action,@From,@End,@Status,@SalaryStructureId,@AnnualCtc,@SalaryJson,@ChangeReason,@Actor)", args, tx);
        }
        if (end is null && CanProjectBulkSalary(current, history, patch.From))
        {
            if (patch.AssignmentOnly)
                await db.ExecuteAsync("UPDATE employees SET SalaryStructureId=@SalaryStructureId WHERE Id=@EmployeeId AND ClientId=@ClientId", args, tx);
            else await WriteExcelPayrollSalaryStateAsync(db, tx, salary);
        }
        // The master fallback can already carry the same template despite having no assignment on this date.
        if (patch.AssignmentOnly && effective is null) basis.SalaryStructureId = "";
        await WriteAuditRowsAsync(db, salary, basis, BulkSalaryAction, patch.From, actor, ["0008"], tx);
        // The usual field audit covers the template and annual CTC, not individual component amounts.
        var differences = BulkSalaryChanges([], basis, patch, rules.Values.ToLookup(rule => rule.TemplateId))
            .Where(change => change.Field.StartsWith("salaryInput:", StringComparison.Ordinal)).ToList();
        if (differences.Count > 0)
            await db.ExecuteAsync(@"INSERT INTO employee_audit_trail
(EmployeeId,EmployeeCode,ActionType,InfotypeCode,FieldName,OldValue,NewValue,EffectiveFrom,ChangedBy)
VALUES(@EmployeeId,@EmployeeCode,@Action,'0008',@Field,@OldValue,@NewValue,@From,'Bulk Upload')",
                differences.Select(change => new { Action = BulkSalaryAction, EmployeeId = salary.Id, salary.EmployeeCode, Field = change.Field, change.OldValue, change.NewValue, patch.From }), tx);
    }
}
