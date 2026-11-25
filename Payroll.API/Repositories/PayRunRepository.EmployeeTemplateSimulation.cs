using System.Data;
using System.Globalization;
using Dapper;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    public async Task<PayrollSimulationEmployeeRoster> SimulationClientEmployeesAsync(int clientId, string? search, CancellationToken token)
    {
        if (clientId <= 0) throw new InvalidOperationException("Select a permitted client.");
        await using var db = CreateConnection();
        await db.OpenAsync(token);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, isReadOnly: true, token);
        // A what-if roster is the client's Master, not a payroll-eligible or assigned roster.
        // Count independently of search so an empty search result is not an empty Master.
        var total = await db.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM employees WHERE ClientId=@ClientId", new { ClientId = clientId }, tx, cancellationToken: token));
        var text = search?.Trim() ?? "";
        var rows = (await db.QueryAsync<PayrollSimulationEmployeeOption>(new CommandDefinition(@"
SELECT Id,EmployeeCode Code,TRIM(CONCAT(FirstName,' ',LastName)) Name,IsActive
FROM employees
WHERE ClientId=@ClientId AND (@Search='' OR EmployeeCode LIKE @Pattern OR CONCAT(FirstName,' ',LastName) LIKE @Pattern)
ORDER BY EmployeeCode,Id", new { ClientId = clientId, Search = text, Pattern = "%" + text + "%" }, tx, cancellationToken: token))).ToList();
        await tx.RollbackAsync(token);
        return new(rows, total);
    }

    // Separate from SimulateEmployeeAsync: this uses the selected draft's arithmetic,
    // not the employee's actual salary assignment, statutory policies or adjustments.
    public async Task<PayrollSimulationResult> SimulateEmployeeTemplateAsync(PayrollSimulationRequest request, CancellationToken token)
    {
        ValidateSimulationRequest(request);
        if (request.Mode != "EmployeeTemplate") throw new InvalidOperationException("Select employee template what-if mode.");
        await using var db = CreateConnection();
        await db.OpenAsync(token);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, isReadOnly: true, token);
        var employee = await db.QuerySingleOrDefaultAsync<EmployeeTemplateMaster>(new CommandDefinition(@"
SELECT e.Id,e.ClientId,e.EmployeeCode,e.FirstName,e.LastName,e.IsActive,e.WorkLocationId,
       e.AnnualCtc,e.SalaryStructureId,CAST(e.SalaryJson AS CHAR) SalaryJson,COALESCE(w.Name,'') WorkLocationName
FROM employees e LEFT JOIN worklocations w ON w.Id=e.WorkLocationId AND w.ClientId=e.ClientId
WHERE e.Id=@EmployeeId AND e.ClientId=@ClientId", new { request.EmployeeId, request.ClientId }, tx, cancellationToken: token));
        if (employee is null) throw new InvalidOperationException("This employee does not belong to the selected client.");
        var start = PayPeriodStart(request.PayPeriod);
        var history = await db.QuerySingleOrDefaultAsync<EmployeeBasicPayInfotypeRow>(new CommandDefinition(@"
SELECT Id,EmployeeId,SalaryStructureId,AnnualCtc,CAST(SalaryJson AS CHAR) SalaryJson
FROM employee_it0008_basic_pay
WHERE EmployeeId=@EmployeeId AND EffectiveFrom<=@PeriodEnd AND (EffectiveTo IS NULL OR EffectiveTo>=@PeriodStart)
ORDER BY EffectiveFrom DESC,Id DESC LIMIT 1", new { EmployeeId = employee.Id, PeriodStart = start, PeriodEnd = start.AddMonths(1).AddDays(-1) }, tx, cancellationToken: token));
        var salary = history is null ? new Dictionary<string, decimal>() : ParseSalaryComponentMap(history.SalaryJson);
        var usedCurrentSalary = history is null;
        var salarySource = history is null ? "Current Employee Master amount (temporary scenario)" : "Effective Basic Pay amount (temporary scenario)";
        if (salary.Count == 0)
        {
            usedCurrentSalary = true;
            var rows = await db.QueryAsync<EmployeeSalaryTableRow>(new CommandDefinition(
                "SELECT EmployeeId,ComponentId,Amount FROM employeesalarycomponents WHERE EmployeeId=@EmployeeId", new { EmployeeId = employee.Id }, tx, cancellationToken: token));
            // Retain presence, including explicit zero, and do not guess conflicting aliases.
            salary = rows.GroupBy(row => row.ComponentId, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Select(row => row.Amount).Distinct().Count() == 1)
                .ToDictionary(group => group.Key, group => group.First().Amount, StringComparer.OrdinalIgnoreCase);
            if (salary.Count == 0) salary = ParseSalaryComponentMap(employee.SalaryJson);
            salarySource = "Current Employee Master amount (temporary scenario)";
        }

        PayRunAttendance? attendance = null;
        string attendanceError = "";
        try
        {
            attendance = (await GetPayRunAttendanceAsync(db, tx, request.ClientId, request.PayPeriod, employee.Id, includeApprovalState: false)).SingleOrDefault();
            attendanceError = attendance?.PolicyError ?? "";
        }
        catch (InvalidOperationException e)
        {
            // Invalid old source evidence must not be reused as trusted new-template attendance.
            // The user can still enter temporary days without repairing or saving any payroll.
            attendanceError = e.Message;
        }
        var usableAttendance = string.IsNullOrWhiteSpace(attendanceError) ? attendance : null;
        var usesSavedDivisor = usableAttendance?.WorkingDays > 0;
        var divisor = usesSavedDivisor ? (int)Math.Ceiling(usableAttendance!.WorkingDays) : DateTime.DaysInMonth(start.Year, start.Month);
        if (divisor is < 1 or > 31)
        {
            attendanceError = "The saved payroll divisor is outside 1–31 days; saved attendance was not used.";
            usableAttendance = null;
            usesSavedDivisor = false;
            divisor = DateTime.DaysInMonth(start.Year, start.Month);
        }
        if (request.PayrollDays.HasValue && request.PayrollDays.Value != divisor)
            throw new InvalidOperationException("Payroll divisor is read-only. Reload the employee/month to use its saved divisor or calendar days.");

        var annual = request.ClearAnnualCtc ? null : request.AnnualCtc
            ?? (history is not null ? history.AnnualCtc > 0 ? history.AnnualCtc : (decimal?)null : employee.AnnualCtc > 0 ? employee.AnnualCtc : null);
        var annualSource = request.ClearAnnualCtc ? "Cleared temporary input" : request.AnnualCtc.HasValue ? "Temporary what-if input"
            : annual.HasValue ? history is null ? "Current Employee Master annual CTC (temporary scenario)" : "Effective Basic Pay annual CTC (temporary scenario)" : "Not supplied";
        var savedSetup = ReadPayrollSetup(await PayrollDataTableStore.GetSetupJsonAsync(db, tx));
        var savedComponents = savedSetup.Components;
        var sourceTemplateId = usedCurrentSalary ? employee.SalaryStructureId : history!.SalaryStructureId;
        var sourceStructure = savedSetup.Structures.FirstOrDefault(s => s.Id == sourceTemplateId && s.ClientId.Split([';', ',', '|'])
            .Any(value => value.Split(':')[0].Trim() == request.ClientId.ToString(CultureInfo.InvariantCulture)));
        // A stored daily rate and a stored monthly amount have different units.
        // Confirm that unit against the persisted master before using an exact alias
        // in a draft whose calculation style may have been edited temporarily.
        var salaryWithMatchingBasis = new Dictionary<string, decimal>(salary);
        var (draftComponents, draftStructure) = SimulationSetup(request.Snapshot!, request.TemplateId);
        foreach (var line in draftStructure.Lines)
        {
            var component = draftComponents.Single(c => c.Id == line.ComponentId);
            var source = savedComponents.FirstOrDefault(c => c.Id == component.Id && c.Code.Equals(component.Code, StringComparison.OrdinalIgnoreCase));
            var sourceLine = sourceStructure?.Lines.FirstOrDefault(l => l.ComponentId == source?.Id);
            var draftDaily = NormalizeCalculationType(FirstText(line.CalculationType, component.CalculationType)) == "Daily Rate";
            var sourceDaily = NormalizeCalculationType(FirstText(sourceLine?.CalculationType ?? "", source?.CalculationType ?? "")) == "Daily Rate";
            if (source is not null && (NormalizeCalculationType(source.CalculationType) == "Daily Rate") == draftDaily && sourceDaily == draftDaily) continue;
            // Monthly values for unsaved non-daily components can still match their
            // stable code. A daily draft needs affirmative saved daily-rate evidence.
            if (source is null && !draftDaily && !savedComponents.Any(c => c.Code.Equals(component.Code, StringComparison.OrdinalIgnoreCase) && NormalizeCalculationType(c.CalculationType) == "Daily Rate")) continue;
            foreach (var key in salaryWithMatchingBasis.Keys.Where(key => key.Equals(component.Id, StringComparison.OrdinalIgnoreCase) || key.Equals(component.Code, StringComparison.OrdinalIgnoreCase)).ToList())
                salaryWithMatchingBasis.Remove(key);
        }
        var componentInputs = ResolveEmployeeTemplateComponentInputs(request, salaryWithMatchingBasis);
        var temporary = new PayrollSimulationRequest
        {
            Mode = "Template", ClientId = request.ClientId, TemplateId = request.TemplateId, Snapshot = request.Snapshot,
            PayPeriod = request.PayPeriod, PayrollDays = divisor, PresentDays = request.PresentDays ?? usableAttendance?.PresentDays,
            PayableDays = request.PayableDays ?? usableAttendance?.PayableDays, AnnualCtc = annual,
            AmountOverrides = componentInputs.Where(input => input.Value.HasValue).ToDictionary(input => input.Key, input => input.Value!.Value),
            NamedInputs = request.NamedInputs
        };
        var missing = componentInputs.Where(input => !input.Value.HasValue).ToList();
        // Never calculate plausible zero totals from missing Master input amounts.
        var result = missing.Count == 0 ? SimulateTemplate(temporary) : new PayrollSimulationResult();
        foreach (var input in missing)
        {
            var component = request.Snapshot!.Components.Single(c => c.Id == input.Key);
            result.Issues.Add(new("Error", component.Code, "Employee amount is not supplied; it is not zero.", "Enter a temporary amount, including zero when intentional. No Employee Master or salary assignment will be changed."));
        }
        if (missing.Count > 0) CheckDays(result, temporary.PayrollDays, temporary.PresentDays, temporary.PayableDays);
        if (!string.IsNullOrWhiteSpace(attendanceError))
            result.Issues.Add(new("Warning", "Attendance", "Saved attendance was not used: " + attendanceError, "Enter reviewed temporary present and payable days for this what-if. Actual saved payroll remains unchanged."));
        result.Mode = "EmployeeTemplate";
        result.ClientId = request.ClientId;
        result.EmployeeId = employee.Id;
        result.TemplateId = request.TemplateId;
        result.PayPeriod = request.PayPeriod;
        result.EmployeeCode = employee.EmployeeCode;
        result.EmployeeName = $"{employee.FirstName} {employee.LastName}".Trim();
        result.Employee = $"{result.EmployeeCode} - {result.EmployeeName}";
        SetSimulationLocations(result, [new(employee.WorkLocationId, employee.WorkLocationName, request.TemplateId)]);
        result.ComponentInputs = componentInputs;
        result.Inputs =
        [
            new("PAYROLL_DAYS", divisor, usesSavedDivisor ? "Saved attendance divisor (read-only)" : "Calendar month (read-only)", "Days"),
            new("PRESENT_DAYS", temporary.PresentDays, request.PresentDays.HasValue ? "Temporary what-if input" : usableAttendance?.PresentDays is not null ? "Saved attendance" : "Not supplied", "Days"),
            new("PAYABLE_DAYS", temporary.PayableDays, request.PayableDays.HasValue ? "Temporary what-if input" : usableAttendance is not null ? "Saved attendance" : "Not supplied", "Days"),
            new("ANNUAL_CTC", annual, annualSource, "Amount")
        ];
        result.Inputs.AddRange(componentInputs.Select(input => new PayrollSimulationInput(input.Key, input.Value,
            request.AmountOverrides.ContainsKey(input.Key) ? "Temporary what-if input" : request.ClearedComponentIds?.Contains(input.Key) == true ? "Cleared temporary input" : input.Value.HasValue ? salarySource : "Not supplied", "Amount")));
        result.AttendanceEvidence = DescribeAttendanceInput(usableAttendance, request) with
        {
            PolicyValidationError = attendanceError,
            Explanation = "Selected-template what-if only. " + DescribeAttendanceInput(usableAttendance, request).Explanation
        };
        result.Coverage = "Selected salary template applied temporarily to this Employee Master record. Saved salary assignment, policies, attendance and payroll are unchanged. Only selected-template arithmetic is calculated; actual dated statutory policies, projected TDS and approved adjustments require Saved employee calculation.";
        if (!employee.IsActive) result.Issues.Add(new("Info", "Employee", "This Employee Master record is inactive; it is available for a temporary what-if only.", "Actual payroll retains its employment eligibility checks."));
        await tx.RollbackAsync(token);
        return result;
    }

    internal static Dictionary<string, decimal?> ResolveEmployeeTemplateComponentInputs(PayrollSimulationRequest request, IReadOnlyDictionary<string, decimal> salary)
    {
        var (components, structure) = SimulationSetup(request.Snapshot!, request.TemplateId);
        ValidateOverrides(structure, request.AmountOverrides);
        var editable = structure.Lines.Select(line => (Line: line, Component: components.Single(c => c.Id == line.ComponentId)))
            .Where(item => IsEmployeeTemplateManualInput(item.Line, item.Component)).ToList();
        var editableIds = editable.Select(item => item.Component.Id).ToHashSet(StringComparer.Ordinal);
        var cleared = request.ClearedComponentIds?.ToHashSet(StringComparer.Ordinal) ?? [];
        if (cleared.Any(id => !editableIds.Contains(id)) || request.AmountOverrides.Keys.Any(id => !editableIds.Contains(id))
            || cleared.Any(request.AmountOverrides.ContainsKey))
            throw new InvalidOperationException("Only manual inputs from this selected salary template can be entered or cleared. Formula results remain calculated.");
        var output = new Dictionary<string, decimal?>();
        foreach (var (_, component) in editable)
        {
            decimal? value = request.AmountOverrides.TryGetValue(component.Id, out var entered) ? entered
                : cleared.Contains(component.Id) ? null : EmployeeTemplateStoredAmount(salary, component.Id, component.Code);
            if (value is < 0 or > 1000000000m) value = null;
            output[component.Id] = value;
        }
        return output;
    }

    private static bool IsEmployeeTemplateManualInput(SalaryStructureLine line, PayrollComponent component)
    {
        var method = NormalizeCalculationType(FirstText(line.CalculationType, component.CalculationType));
        return method is "Fixed Amount" or "Daily Rate" or "Manual / Variable"
            || method == "Formula" && decimal.TryParse(ComponentFormula(line, component), NumberStyles.Number, CultureInfo.InvariantCulture, out _);
    }

    private static decimal? EmployeeTemplateStoredAmount(IReadOnlyDictionary<string, decimal> salary, string id, string code)
    {
        var matches = salary.Where(input => input.Key.Equals(id, StringComparison.OrdinalIgnoreCase) || input.Key.Equals(code, StringComparison.OrdinalIgnoreCase))
            .Select(input => input.Value).Distinct().Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private sealed class EmployeeTemplateMaster : Employee
    {
        public string WorkLocationName { get; set; } = "";
    }
}
