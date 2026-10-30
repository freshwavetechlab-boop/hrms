using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    public static void ValidateSimulationRequest(PayrollSimulationRequest request)
    {
        if (request.Mode is not ("Template" or "Employee") || request.ClientId <= 0 || request.TemplateId is null || request.TemplateId.Length > 40
            || request.AnnualCtc is < 0 or > 1000000000m || request.PayrollDays is < 1 or > 31
            || request.PayableDays is < 0 or > 31 || request.PresentDays is < 0 or > 31
            || request.AmountOverrides is null || request.AmountOverrides.Count > 100 || request.AmountOverrides.Any(p => p.Key.Length > 40 || p.Value < 0 || p.Value > 1000000000m))
            throw new InvalidOperationException("Select a client and valid non-negative simulation inputs. Payroll days must be 1-31.");
        if (request.Mode == "Employee" && (request.EmployeeId is not > 0 || !DateTime.TryParseExact(request.PayPeriod + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
            throw new InvalidOperationException("Select an employee and a valid payroll month (YYYY-MM).");
        if (request.Mode == "Template")
        {
            if (request.Snapshot is null || request.Snapshot.Mode != "Template" || request.Snapshot.ClientId != request.ClientId) throw new InvalidOperationException("Provide the selected client's template draft.");
            PayrollAdvisorService.Validate(request.Snapshot);
        }
    }

    private static (List<PayrollComponent> Components, SalaryStructureSetup Structure) SimulationSetup(PayrollReviewSnapshot snapshot, string id)
    {
        var components = snapshot.Components.Select(c => new PayrollComponent(c.Id, c.Code, c.Code, c.Category, c.ComponentRole, c.StatutoryType,
            c.CalculationType, c.Value, c.Formula, c.BaseComponent, c.ProRata, c.Active, c.Priority, c.PayType, c.Taxable)).ToList();
        var structure = new SalaryStructureSetup(id, snapshot.ClientId.ToString(CultureInfo.InvariantCulture), snapshot.AnnualCtc.ToString(CultureInfo.InvariantCulture),
            snapshot.Lines.Select(l => new SalaryStructureLine(l.ComponentId, l.Value, l.CalculationType, l.Formula, l.BaseComponent, l.ProRataOverride, "")).ToList(), true);
        return (components, structure);
    }

    public static PayrollSimulationResult SimulateTemplate(PayrollSimulationRequest request)
    {
        ValidateSimulationRequest(request);
        var result = new PayrollSimulationResult { Mode = "Template", TemplateId = request.TemplateId, Coverage = "Draft template arithmetic using the payroll engine. Employee amounts, approved adjustments, dated PF, professional tax and projected TDS are included only in Employee mode. No data is saved." };
        var (components, structure) = SimulationSetup(request.Snapshot!, request.TemplateId);
        if (!CheckDays(result, request.PayrollDays, request.PresentDays, request.PayableDays)) return result;
        var usesCtc = structure.Lines.Any(l => {
            var c = components.First(x => x.Id == l.ComponentId);
            return FormulaReferences(ComponentFormula(l, c)).Any(t => t is "CTC" or "GROSS" or "MONTHLY_CTC" or "ANNUAL_CTC")
                || NormalizeCalculationType(FirstText(l.CalculationType, c.CalculationType)) == "Residual / Balancing" && FirstText(l.BaseComponent, c.BaseComponent, "GROSS").ToUpperInvariant() is "CTC" or "GROSS" or "MONTHLY_CTC" or "ANNUAL_CTC";
        });
        if (usesCtc && request.AnnualCtc is null) result.Issues.Add(new("Error", "CTC", "Annual CTC is missing, not zero.", "Supply an explicit annual CTC, including zero if that is intentional."));
        ValidateOverrides(structure, request.AmountOverrides);
        result.Issues.AddRange(InspectTemplate(components, structure, request.AmountOverrides));
        if (!result.Complete) return result;
        try
        {
            CalculateStructureLines(components, structure, decimal.Round((request.AnnualCtc ?? 0) / 12m, 2), request.PayrollDays!.Value, request.PresentDays!.Value, request.PayableDays!.Value,
                overrides: request.AmountOverrides, trace: result.Lines);
            result.Gross = result.Lines.Where(l => IsPayableEarningCategory(l.Category)).Sum(l => l.Earned);
            result.Deductions = result.Lines.Where(l => IsDeductionCategory(l.Category)).Sum(l => l.Earned);
            result.Benefits = result.Lines.Where(l => l.Category == "Benefit").Sum(l => l.Earned);
            result.Net = Math.Max(0, result.Gross.Value - result.Deductions.Value);
        }
        catch (Exception e) when (e is InvalidOperationException or OverflowException or FormatException) { result.Lines.Clear(); result.Issues.Add(new("Error", "Calculation", e.Message, "Correct the indicated component or add its dependency, then simulate again.")); }
        result.Inputs = [new("PAYROLL_DAYS", request.PayrollDays, "What-if input", "Days"), new("PRESENT_DAYS", request.PresentDays, "What-if input", "Days"), new("PAYABLE_DAYS", request.PayableDays, "What-if input", "Days"), new("ANNUAL_CTC", request.AnnualCtc, "Template / what-if input", "Amount")];
        return result;
    }

    private static bool CheckDays(PayrollSimulationResult result, int? days, decimal? present, decimal? payable,bool allowUnknownPresent=false)
    {
        if (days is null || present is null&&!allowUnknownPresent || payable is null) result.Issues.Add(new("Error", "Attendance", "Payroll, present or payable days are missing. Missing attendance cannot mean zero or a full month.", "Supply reviewed attendance for the selected period. Unknown presence is supported only by an explicit reviewed payable-only source import."));
        else if (days is < 1 or > 31 || present < 0 || payable < 0 || present > days || payable > days) result.Issues.Add(new("Error", "Attendance", "Present and payable days must be between zero and payroll days.", "Correct the attendance inputs; fractions such as 22.5 are allowed."));
        return result.Complete;
    }

    private static void ValidateOverrides(SalaryStructureSetup structure, Dictionary<string, decimal> overrides)
    {
        if (overrides.Keys.Any(id => !structure.Lines.Any(l => l.ComponentId == id))) throw new InvalidOperationException("A what-if amount references a component outside this template.");
    }

    private static List<PayrollSimulationIssue> InspectTemplate(List<PayrollComponent> components, SalaryStructureSetup structure, Dictionary<string, decimal> overrides)
    {
        var issues = new List<PayrollSimulationIssue>();
        foreach (var line in structure.Lines)
        {
            var c = components.FirstOrDefault(c => c.Id == line.ComponentId);
            if (c is null || !c.Active) { issues.Add(new("Error", c?.Code ?? line.ComponentId, "Component is missing or inactive.", "Choose an active component.")); continue; }
            var type = NormalizeCalculationType(FirstText(line.CalculationType, c.CalculationType));
            var formula = ComponentFormula(line, c);
            if (type is "Fixed Amount" or "Daily Rate" && !overrides.ContainsKey(c.Id) && !overrides.ContainsKey(c.Code) && !decimal.TryParse(formula, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) issues.Add(new("Error", c.Code, "Required amount or daily rate is missing or not numeric.", "Enter a component/template amount or supply an employee amount. An explicit zero is different from blank."));
            if (type == "Manual / Variable") issues.Add(new("Info", c.Code, "This component calculates zero; approved period adjustments are separate payroll lines.", "Review Employee mode for approved adjustments. No formula token for transaction units is currently supported."));
            if (type == "Formula" && (NullableBool(line.ProRataOverride) ?? c.ProRata) && Regex.IsMatch(formula, @"_EARNED\b|\b(PAYABLE_DAYS|PRESENT_DAYS|LOP_DAYS|GROSS_EARNED|EARNED_EARNINGS_BEFORE_THIS)\b", RegexOptions.IgnoreCase)) issues.Add(new("Warning", c.Code, "Formula uses earned/attendance values and applies attendance again.", "Review the pro-rata setting to avoid reducing the amount twice."));
            if (type == "Formula") foreach (var token in FormulaReferences(formula).Distinct())
            {
                var definition = PayrollValueCatalogue.SystemValues.FirstOrDefault(d => d.Token == token);
                if (definition?.Legacy == true) issues.Add(new("Warning", c.Code, $"{token}: {definition.Meaning}", "Keep existing semantics or explicitly choose the clearer token after review."));
                if (definition is not null) continue;
                var dependency = components.FirstOrDefault(d => d.Code.Equals(BaseComponentToken(token), StringComparison.OrdinalIgnoreCase) || d.Id == BaseComponentToken(token));
                if (dependency is null || !structure.Lines.Any(l => l.ComponentId == dependency.Id)) issues.Add(new("Error", c.Code, $"Required value {token} has no component in this template.", $"Add {dependency?.Code ?? BaseComponentToken(token)} to the template, or correct the formula."));
            }
        }
        return issues;
    }

    private static PayrollSimulationLine CreateSimulationTrace(PayrollComponent c, string formula, decimal raw, decimal monthly, decimal earned, decimal? rate, decimal payable, bool policy, bool overridden, Dictionary<string, decimal> values)
    {
        var source = policy ? "Effective-dated PF policy (overrides formula)" : overridden ? "Employee / what-if amount override" : "Component / template default";
        var normalized = Regex.Replace(formula, @"SUM\s*\(\s*(FIXED\s+)?EARNINGS(_BEFORE_THIS)?\s*\)", m => m.Groups[1].Success ? "FIXED_EARNINGS_BEFORE_THIS" : "EARNINGS_BEFORE_THIS", RegexOptions.IgnoreCase);
        var refs = FormulaReferences(normalized).Distinct(StringComparer.OrdinalIgnoreCase).Where(values.ContainsKey).ToDictionary(t => t, t => values[t]);
        var substituted = Regex.Replace(normalized, @"\b[A-Z_][A-Z0-9_]*\b", m => values.TryGetValue(m.Value, out var v) ? v.ToString(CultureInfo.InvariantCulture) : m.Value, RegexOptions.IgnoreCase);
        if (rate.HasValue) substituted = $"{rate.Value.ToString(CultureInfo.InvariantCulture)} x {payable.ToString(CultureInfo.InvariantCulture)} payable days";
        else if (policy || overridden) substituted = $"Resolved amount {raw.ToString(CultureInfo.InvariantCulture)}; configured formula not used for this amount";
        if (NormalizeCalculationType(c.CalculationType) == "Residual / Balancing") substituted = $"Target {c.BaseComponent} less earlier earnings {values.GetValueOrDefault("EARNINGS_BEFORE_THIS")}";
        if (NormalizeCalculationType(c.CalculationType) == "Slab Based") substituted = $"Select range for earlier earned earnings {values.GetValueOrDefault("GROSS_EARNED")}";
        return new(c.Code, c.Name, c.Category, formula, substituted, raw, monthly, earned, c.ProRata, source, "Engine: amounts to 2 decimals, midpoint-to-even, minimum zero. Formula ROUND functions and dated policies retain their own rounding.", refs);
    }

    internal static PayrollAttendanceInputEvidence DescribeAttendanceInput(PayRunAttendance? attendance, PayrollSimulationRequest request)
    {
        var overridden = request.PayrollDays.HasValue || request.PresentDays.HasValue || request.PayableDays.HasValue;
        if (attendance is null) return new("Missing native attendance", request.PayPeriod, null, null, null, null, null,
            null, null, overridden, "", "No native attendance row was resolved. Any supplied days are temporary what-if inputs, not recorded attendance or leave entitlement.");
        var explanation = attendance.InputSource switch
        {
            "Reviewed source payable-only" => "Reviewed saved-source payable days and salary divisor were supplied. Actual present/worked days were not supplied and remain unknown; no zero, paid leave, weekly off or calendar-day entitlement is inferred. Only salary formulas independent of actual presence may calculate.",
            "Monthly snapshot" => "The regular engine selected the stored monthly working/present/payable totals. This does not establish who originally entered/imported them or infer CL. Daily weekly-off/holiday units were not loaded in this branch and remain unknown.",
            "Legacy daily attendance" => "The regular calendar-month daily aggregate was used. Each stored WO/H row contributes one payable day in this legacy branch; present units come from Present rows. This is not proof of approved leave entitlement.",
            "Dated daily attendance" => "The regular dated weekly-off resolver supplied this cycle's units. Policy validation errors must be resolved before relying on a payroll result.",
            "Monthly snapshot validated against dated attendance" => "Stored monthly totals were checked against the regular dated attendance resolver. A validation error blocks payroll; the observation does not rewrite monthly attendance.",
            _ => "Native day totals were resolved, but their input branch was not identified by this runtime. Refresh against the updated API; no source or CL allocation is guessed."
        };
        return new(attendance.InputSource, request.PayPeriod, attendance.WorkingDays, attendance.PresentDays, attendance.PayableDays,
            attendance.WeeklyOffPayableUnits, attendance.HolidayPayableUnits,
            attendance.PolicySnapshot?.PeriodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            attendance.PolicySnapshot?.PeriodEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), overridden,
            attendance.PolicyError, explanation);
    }

    public async Task<IReadOnlyList<object>> SimulationEmployeesAsync(int clientId, string? search, CancellationToken token)
    {
        await using var db = CreateConnection(); await db.OpenAsync(token);
        var rows = await db.QueryAsync(new CommandDefinition("SELECT Id,EmployeeCode,CONCAT(FirstName,' ',LastName) Name FROM employees WHERE ClientId=@ClientId AND IsActive=TRUE AND (@Search='' OR EmployeeCode LIKE @Pattern OR CONCAT(FirstName,' ',LastName) LIKE @Pattern) ORDER BY EmployeeCode LIMIT 50", new { ClientId = clientId, Search = search?.Trim() ?? "", Pattern = "%" + (search?.Trim() ?? "") + "%" }, cancellationToken: token));
        return rows.Select(r => (object)new { id = (int)r.Id, code = (string)r.EmployeeCode, name = (string)r.Name }).ToList();
    }

    public async Task<PayrollSimulationResult> SimulateEmployeeAsync(PayrollSimulationRequest request, CancellationToken token)
    {
        ValidateSimulationRequest(request);
        await using var db = CreateConnection(); await db.OpenAsync(token);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, isReadOnly: true, token);
        var result=await SimulateEmployeeInTransactionAsync(db,tx,request,token);
        await tx.RollbackAsync(token);
        return result;
    }

    private sealed record EmployeeSimulationEvidence(object Employee, string SetupJson, object Attendance,
        IReadOnlyList<PayrollAdjustment> Adjustments, IReadOnlyList<object> TaxInputs, string DetailsJson,object PolicyInputs);

    // One calculation body for public read-only simulation and the held source
    // witness. The caller owns its transaction, locks and no-save boundary.
    private async Task<PayrollSimulationResult> SimulateEmployeeInTransactionAsync(MySqlConnection db, MySqlTransaction tx,
        PayrollSimulationRequest request, CancellationToken token, Action<EmployeeSimulationEvidence>? capture=null)
    {
        ValidateSimulationRequest(request);
        var result = new PayrollSimulationResult { Mode = "Employee", ClientId=request.ClientId,EmployeeId=request.EmployeeId,PayPeriod=request.PayPeriod,
            Coverage = "Saved salary assignment, attendance, eligible approved regular-period adjustments, dated PF, professional tax and projected TDS. What-if inputs are temporary; no payrun or tax snapshot is saved. Already-applied adjustments are excluded." };
        var employee = await db.QuerySingleOrDefaultAsync<PayRunSourceEmployee>(new CommandDefinition("SELECT e.*, c.Name ClientName,COALESCE(w.State,'') WorkState FROM employees e JOIN clients c ON c.Id=e.ClientId LEFT JOIN worklocations w ON w.Id=e.WorkLocationId WHERE e.Id=@EmployeeId AND e.ClientId=@ClientId AND " + EmployeeEmploymentRules.EligibleInMonth("e", "@PayPeriod"), new { request.EmployeeId, request.ClientId, request.PayPeriod }, tx, cancellationToken: token));
        if (employee is null) throw new InvalidOperationException("No active employee belongs to this client with the selected ID.");
        result.Employee = $"{employee.EmployeeCode} - {employee.FirstName} {employee.LastName}".Trim();
        await LoadEmployeeTablesAsync(db, tx, [employee], request.PayPeriod);
        result.LocationAllocations=employee.LocationParts;
        if(employee.LocationParts is not null && (request.PayrollDays.HasValue||request.PresentDays.HasValue||request.PayableDays.HasValue||request.AnnualCtc.HasValue||request.AmountOverrides.Count>0))
        {
            result.Issues.Add(new("Error","Location allocation","Aggregate what-if overrides cannot redistribute reviewed location attendance/rate inputs.","Review the per-location assignment and allocation inputs, then simulate their saved native totals."));
            return result;
        }
        var json = await PayrollDataTableStore.GetSetupJsonAsync(db, tx); var setup = ReadPayrollSetup(json);
        var structure = setup.Structures.FirstOrDefault(s => s.Active && s.Id == employee.SalaryStructureId && s.ClientId.Split([';', ',', '|']).Any(v => v.Split(':')[0].Trim() == request.ClientId.ToString(CultureInfo.InvariantCulture)));
        if (structure is null && employee.LocationParts is null) { result.Issues.Add(new("Error", "Salary assignment", "Employee has no explicit active salary template for this client.", "Assign the intended salary template in Employee Master. Simulation will not guess a template.")); return result; }
        var allocationTemplateIds=employee.LocationParts?.Parts.Select(part=>part.SalaryStructureId).Distinct().ToArray();
        result.TemplateId = allocationTemplateIds is null ? structure!.Id : string.Join(";",allocationTemplateIds);
        if (!string.IsNullOrWhiteSpace(request.TemplateId) && (allocationTemplateIds is null ? request.TemplateId!=structure!.Id : !allocationTemplateIds.Contains(request.TemplateId))) { result.Issues.Add(new("Error", "Salary assignment", "This employee is assigned to a different salary template for this period.", "Select an employee assigned to this template, or simulate their assigned template.")); return result; }
        if(employee.LocationParts is null)ValidateOverrides(structure!, request.AmountOverrides);
        var attendance = (await GetPayRunAttendanceAsync(db, tx, request.ClientId, request.PayPeriod, employee.Id)).FirstOrDefault(a => a.EmployeeId == employee.Id);
        result.AttendanceEvidence = DescribeAttendanceInput(attendance, request);
        var period = request.PayrollDays ?? (attendance?.WorkingDays > 0 ? (int?)Math.Ceiling(attendance.WorkingDays) : null);
        var present = request.PresentDays ?? attendance?.PresentDays; var payable = request.PayableDays ?? attendance?.PayableDays;
        result.Inputs = [new("PAYROLL_DAYS", period, request.PayrollDays.HasValue ? "What-if" : "Attendance / policy", "Days"), new("PRESENT_DAYS", present, request.PresentDays.HasValue ? "What-if" : present.HasValue ? "Attendance result" : "Actual presence not supplied", "Days"), new("PAYABLE_DAYS", payable, request.PayableDays.HasValue ? "What-if" : "Attendance result", "Days"), new("ANNUAL_CTC", request.AnnualCtc ?? employee.AnnualCtc, request.AnnualCtc.HasValue ? "What-if" : "Effective employee salary (engine may fall back to salary/template target)", "Amount")];
        if (!CheckDays(result, period, present, payable,attendance?.PayableOnlyEvidence is not null)) return result;
        var employmentError = await EmployeeAttendanceEligibility.ValidateMonthlyWithOptionalPresenceAsync(db, tx, request.ClientId, request.PayPeriod,
            [(employee.Id, present, payable!.Value)], lockEmployees: false);
        if (employmentError is not null) { result.Issues.Add(new("Error", "Employment dates", employmentError, "Use attendance within the last working / rehire dates.")); return result; }
        if (!string.IsNullOrWhiteSpace(attendance?.PolicyError)) { result.Issues.Add(new("Error", "Attendance policy", attendance.PolicyError, "Resolve attendance policy errors before relying on payroll.")); return result; }
        if (attendance is null) result.Issues.Add(new("Warning", "Attendance", "Using hypothetical days: no attendance record exists for this period.", "Record attendance before running actual payroll."));
        employee.SimulationAnnualCtc = request.AnnualCtc;
        foreach (var item in request.AmountOverrides) employee.SalaryComponents[item.Key] = item.Value;
        if(employee.LocationParts is null)result.Issues.AddRange(InspectTemplate(setup.Components, structure!, employee.SalaryComponents));
        else foreach(var part in employee.LocationParts.Parts)
        {
            var assigned=setup.Structures.SingleOrDefault(item=>item.Id==part.SalaryStructureId&&item.Active);
            if(assigned is null)result.Issues.Add(new("Error","Location assignment","A reviewed location salary template is missing or inactive.","Review the saved Settings template before simulation."));
            else result.Issues.AddRange(InspectTemplate(setup.Components,assigned,part.SalaryInputs));
        }
        if (!result.Complete) return result;
        try
        {
            employee.PayableOnlyAttendance = attendance?.PayableOnlyEvidence;
            await LoadPfPolicyPeriodsAsync(db, tx, [employee], request.PayPeriod, json);
            if (employee.PfPolicyPeriod?.Versions.Count > 1 && (request.PayrollDays.HasValue || request.PresentDays.HasValue || request.PayableDays.HasValue)) throw new InvalidOperationException("Date-split PF uses daily attendance. Aggregate day overrides cannot represent this split; use recorded attendance.");
            employee.AttendancePolicySnapshot = attendance?.PolicySnapshot;
            employee.PayableOnlyAttendance = attendance?.PayableOnlyEvidence;
            var adjustments = await GetApplicableAdjustmentsAsync(db, tx, request.ClientId, request.PayPeriod, "Regular", [], employee.Id);
            var row = BuildEmployee(0, employee, json, request.PayPeriod, period!.Value, present, payable!.Value, adjustments, 0, 0, -1, false);
            var taxInputs=new List<object>();
            var tds = await CalculateMonthlyTdsAsync(db, tx, employee, row, request.PayPeriod, persistSnapshot: false,
                recordInputs:capture is null ? null : taxInputs.Add);
            if (tds.Calculated) row = BuildEmployee(0, employee, json, request.PayPeriod, period.Value, present, payable.Value, adjustments, 0, 0, tds.Amount, false, tds.ComponentCode);
            CalculateConfiguredSalary(employee, setup, period.Value, present, payable.Value, result.Lines);
            if (result.Lines.Count == 0 && (structure?.Lines.Count > 0 || employee.LocationParts is not null)) throw new InvalidOperationException("Employee salary target is missing. The legacy salary fallback cannot simulate the assigned template. Supply an annual target or correct the salary assignment.");
            using var details = JsonDocument.Parse(row.DetailsJson);
            var final = new List<PayrollSimulationLine>();
            foreach (var line in details.RootElement.EnumerateArray().Where(l => Text(l, "Category") != "Summary"))
            {
                var code = Text(line, "Id"); var amount = NumberFrom(Text(line, "amount")); var monthly = NumberFrom(Text(line, "monthlyAmount"));
                var trace = result.Lines.FirstOrDefault(l => l.Code == code);
                var adjustment = adjustments.Any(a => a.ComponentCode == code || string.IsNullOrWhiteSpace(a.ComponentCode) && $"ADJ_{a.ComponentId}" == code) && !line.TryGetProperty("ComponentRole", out _);
                var projectedTax = tds.Calculated && code.Equals(tds.ComponentCode, StringComparison.OrdinalIgnoreCase);
                if (trace is not null && !adjustment && !projectedTax) final.Add(trace with { Monthly = monthly, Earned = amount });
                else final.Add(new(code, Text(line, "Name"), Text(line, "Category"), "", amount.ToString(CultureInfo.InvariantCulture), amount, monthly, amount, false, projectedTax ? "Projected TDS (published tax rules and prior approved payroll)" : adjustment ? "Approved period adjustment" : "Personal deduction / configured professional-tax policy", "Resolved by the same payroll calculation path", []));
            }
            result.Lines = final; result.Gross = row.GrossPay + row.OneTimeEarnings; result.Deductions = row.StatutoryDeductions + row.OneTimeDeductions; result.Net = row.NetPay; result.Benefits = final.Where(l => l.Category == "Benefit").Sum(l => l.Earned);
            if(attendance is not null)capture?.Invoke(new(employee,json,attendance,adjustments,taxInputs,row.DetailsJson,
                new{employee.PfPolicyPeriod,employee.AttendancePolicySnapshot,employee.PayableOnlyAttendance}));
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException) { result.Lines.Clear(); result.Issues.Add(new("Error", "Calculation", e.Message, "Correct the indicated salary, attendance or policy input, then simulate again.")); }
        return result;
    }
}
