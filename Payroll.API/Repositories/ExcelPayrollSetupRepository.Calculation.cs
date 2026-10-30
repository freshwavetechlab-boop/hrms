using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal static void ValidateRequest(ExcelPayrollSetupRequest request)
    {
        if (request is null || !Guid.TryParseExact(request.BatchId, "N", out _) || request.RowIds is null || request.RowIds.Count is < 1 or > 100
            || request.RowIds.Any(string.IsNullOrWhiteSpace) || request.RowIds.Distinct().Count() != request.RowIds.Count)
            throw new InvalidOperationException("Select 1–100 unique employee rows from a saved salary batch.");
        if(request.CorrectionOfImportId is not null&&!Guid.TryParseExact(request.CorrectionOfImportId,"N",out _))
            throw new InvalidOperationException("Salary correction requires the exact prior Applied receipt ID.");
        if (string.IsNullOrWhiteSpace(request.TemplateName) || request.TemplateName.Length > 160 || request.WorkWeek is null || request.WorkWeek.Length > 80
            || !DateTime.TryParseExact(request.EffectiveFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new InvalidOperationException("Enter a template name, work week and explicit effective date (YYYY-MM-DD).");
        if ((request.FixedPayrollDays is null ? request.PayrollDaysColumnIndex is < 0 or > 511 : request.FixedPayrollDays is < 1 or > 31 || request.PayrollDaysColumnIndex != -1)
            || request.PayableDaysColumnIndex is < 0 or > 511 || request.PresentDaysColumnIndex is < 0 or > 511
            || request.SourceWorkLocationColumnIndex is < 0 or > 511 || request.DefaultWorkLocationName is null || request.DefaultWorkLocationName.Length > 180)
            throw new InvalidOperationException("Choose valid source day/location columns.");
        if (request.SourceColumnRoles is null || request.SourceColumnRoles.Count > 512 || request.SourceColumnRoles.Any(p => p.Key is < 0 or > 511
            || p.Value is not ("earning" or "deduction" or "employer" or "info" or "ignore" or "netPay" or "grossTotal" or "deductionTotal" or "employeeName" or "employeeCode" or "email")))
            throw new InvalidOperationException("Review payroll source column classifications.");
        if (new[] { request.DefaultDepartment, request.DefaultDesignation, request.DefaultGrade, request.DefaultWorkLocationState }.Any(v => v is null || v.Length > 100)
            || request.AttendanceCycleStartDay is < 1 or > 31 || request.AttendanceCycleEndDay is < 1 or > 31 || request.PayrollReportGenerationDay is < 1 or > 31)
            throw new InvalidOperationException("Use bounded employee defaults and calendar days between 1 and 31.");
        var cycle = request.AttendanceCycleStartDay == 1 ? request.AttendanceCycleEndDay : 32 - request.AttendanceCycleStartDay + request.AttendanceCycleEndDay;
        var buffer = (request.PayrollReportGenerationDay - request.AttendanceCycleEndDay + 31) % 31;
        if (cycle > 31 || buffer is < 3 or > 7) throw new InvalidOperationException("Review the attendance cycle and the 3–7 day payroll-report buffer.");
        if (request.Components is null || request.Components.Count is < 1 or > 100 || request.Components.Any(c => c is null)
            || request.Components.Select(c => c.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Components.Count
            || request.Components.Select(c => c.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Components.Count)
            throw new InvalidOperationException("Review 1–100 unique component keys and codes.");
        foreach (var c in request.Components)
        {
            if (string.IsNullOrWhiteSpace(c.Key) || c.Key.Length > 80 || !Regex.IsMatch(c.Code ?? "", "^[A-Za-z][A-Za-z0-9_]{0,79}$")
                || PayrollValueCatalogue.Names.Contains(c.Code!) || Regex.IsMatch(c.Code!, "_(MONTHLY|EARNED)$", RegexOptions.IgnoreCase)
                || new[] { "MIN", "MAX", "ROUND", "ROUNDUP", "ROUNDDOWN", "SUM" }.Contains(c.Code, StringComparer.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 160 || c.Category is not ("Earning" or "Deduction" or "Benefit" or "Reimbursement")
                || c.CalculationType is not ("Fixed Amount" or "Formula" or "Manual / Variable" or "Daily Rate" or "Residual / Balancing" or "Slab Based")
                || new[] { c.Value, c.Formula, c.BaseComponent, c.ComponentRole, c.StatutoryType }.Any(v => v is null || v.Length > 1000)
                || c.SourceColumnIndex is < 0 or > 511 || c.ExpectedColumnIndex is < 0 or > 511)
                throw new InvalidOperationException("Use valid component names, codes, classifications, formulas and source columns.");
            if (ResolvedPayType(c) is not ("Fixed Pay" or "Variable Pay") || c.IsPeriodAdjustment && ResolvedPayType(c) != "Variable Pay")
                throw new InvalidOperationException($"{c.Code}: choose Fixed Pay or Variable Pay independently of source-month adjustments. A period adjustment requires Variable Pay.");
            if (c.SourceColumnIndex.HasValue && !c.IsPeriodAdjustment && c.CalculationType is not ("Fixed Amount" or "Daily Rate"))
                throw new InvalidOperationException($"{c.Code}: monthly source inputs require Monthly amount or Daily rate. Formula output columns are comparison values only.");
            if (c.IsPeriodAdjustment && (c.SourceColumnIndex is null || c.Category == "Benefit"))
                throw new InvalidOperationException($"{c.Code}: period adjustments need a source amount and an employee earning/deduction category.");
            if (c.ExemptWhenSourceZero && (c.ExpectedColumnIndex is null || c.Category != "Deduction" || c.StatutoryType != "PF Employee"))
                throw new InvalidOperationException("Explicit zero-source exemption is supported only for a reviewed employee PF deduction.");
            if (c.Category == "Benefit" && c.ComponentRole is not ("Employer Contribution" or "Employer Benefit")
                || c.Category != "Benefit" && c.ComponentRole is "Employer Contribution" or "Employer Benefit")
                throw new InvalidOperationException($"{c.Code}: employer contributions must use the Benefit category.");
            var employeeStatutory = new[] { "TDS", "Professional Tax", "PF Employee", "VPF", "ESI Employee", "LWF Employee", "NPS Employee" };
            var employerStatutory = new[] { "PF Employer", "EPS", "ESI Employer", "LWF Employer", "NPS Employer", "Workmen Compensation" };
            if (c.ComponentRole == "Employer Contribution" && !employerStatutory.Contains(c.StatutoryType)
                || c.ComponentRole == "Statutory Deduction" && (c.Category != "Deduction" || !employeeStatutory.Contains(c.StatutoryType))
                || c.ComponentRole == "Employer Benefit" && c.StatutoryType != "None")
                throw new InvalidOperationException($"{c.Code}: choose a statutory type supported by the salary component editor; unclassified employer costs use Employer Benefit / None.");
        }
        if (!request.Components.Any(c => c.Category is "Earning" or "Reimbursement" && !c.IsPeriodAdjustment))
            throw new InvalidOperationException("Add at least one recurring earning component.");
        if (request.EmployeeDecisions is null || request.EmployeeDecisions.Count > request.RowIds.Count
            || request.EmployeeDecisions.Any(d => d is null || !request.RowIds.Contains(d.RowId))
            || request.EmployeeDecisions.Select(d => d.RowId).Distinct().Count() != request.EmployeeDecisions.Count)
            throw new InvalidOperationException("Employee decisions must refer to unique selected source rows.");
        if (request.PayrollDaysCell is null || request.PayrollDaysCell.Length > 12 || request.PayrollDaysCell.Length > 0 && !Regex.IsMatch(request.PayrollDaysCell, @"^\$?[A-Za-z]{1,3}\$?[1-9]\d{0,6}$")) throw new InvalidOperationException("Review one bounded captured header-divisor cell.");
        ValidateTemplateScopeFields(request);
        ValidateLocationAllocationFields(request);
        ValidateTransferRequest(request);
        if (request.IdentityColumnReview is { } identityReview && (identityReview.Version != 1 || identityReview.Role != "UAN"
            || !identityReview.Reviewed || (identityReview.Reason?.Trim().Length ?? 0) is < 8 or > 1200
            || identityReview.ColumnIndex is < 0 or > 511 || !Regex.IsMatch(identityReview.SourceHash ?? "", "^[a-fA-F0-9]{64}$")
            || !Regex.IsMatch(identityReview.SelectionFingerprint ?? "", "^[a-fA-F0-9]{64}$")
            || request.TransferReviewVersion != 1 || request.ImportAttendance || request.LocationAllocations is not null
            || request.EmployeeDecisions.Any(d=>d.AllowCreate)))
            throw new InvalidOperationException("Review the exact UAN source column for an existing-employee salary-only selection; no source or Master identifiers are changed.");
        if (request.EditableDefaults is null || JsonSerializer.SerializeToUtf8Bytes(request.EditableDefaults, Json).Length > 64000)
            throw new InvalidOperationException("Editable default configuration must be a JSON object of at most 64 KiB.");
        foreach (var component in request.Components)
        {
            if (component.RowFormulas is null || component.RowFormulas.Count > request.RowIds.Count
                || component.RowFormulas.Any(p => !request.RowIds.Contains(p.Key) || p.Value is null || p.Value.Length > 1000))
                throw new InvalidOperationException("Formula variants must refer to selected rows and contain bounded native formulas.");
            ValidateSourceProration(component);
        }
    }

    // Transfer formulas must explicitly choose monthly or already-earned amounts. Never
    // repair this by changing a shared component or silently removing its proration.
    internal static void ValidateSourceProration(ExcelPayrollSetupComponent component)
    {
        if (component.IsPeriodAdjustment || component.CalculationType != "Formula" || !component.ProRata) return;
        var formulas = new[] { component.Formula }.Concat(component.RowFormulas.Values);
        if (formulas.Any(formula => Regex.IsMatch(formula,
            @"\b(?:[A-Z][A-Z0-9_]*_EARNED|PAYABLE_DAYS|PRESENT_DAYS|LOP_DAYS|EARNED_EARNINGS_BEFORE_THIS)\b", RegexOptions.IgnoreCase)))
            throw new InvalidOperationException($"{component.Code}: this formula already uses earned amounts or attendance days. Turn off 'Prorate using payable days', or review a genuinely monthly formula before transferring. No shared component or saved attendance has been changed.");
    }

    internal sealed record RowArithmetic(ExcelPayrollSetupPreviewRow Preview, Dictionary<string, decimal> Inputs,
        List<(ResolvedComponent Component, decimal Amount)> Adjustments, List<ExcelPayrollSetupIssue> Issues);

    internal static RowArithmetic CalculateRow(int clientId, ExcelPayrollSetupRequest request, IReadOnlyList<ResolvedComponent> components,
        ExcelPayslipCalculationSourceRow source, ExcelPayslipRow row, IReadOnlySet<int>? blankAsZeroColumns = null)
    {
        components = ComponentsForRow(components, row.Id);
        foreach (var component in components) ValidateSourceProration(component.Definition);
        var payrollNumber = request.FixedPayrollDays is int fixedDays ? fixedDays : ReadNumber(source, request.PayrollDaysColumnIndex);
        if (payrollNumber is < 1 or > 31 || decimal.Truncate(payrollNumber) != payrollNumber) throw new InvalidOperationException("Payroll denominator must be a whole number between 1 and 31; it is read separately for each employee.");
        var days = (int)payrollNumber;
        var payable = ReadNumber(source, request.PayableDaysColumnIndex);
        var present = request.PresentDaysColumnIndex is int p ? ReadNumber(source, p) : payable;
        if (payable < 0 || present < 0 || payable > days || present > days) throw new InvalidOperationException("Present/payable days must lie between zero and this employee's source payroll denominator.");
        var inputs = new Dictionary<string, decimal>();
        var adjustments = new List<(ResolvedComponent Component, decimal Amount)>();
        var issues = new List<ExcelPayrollSetupIssue>();
        foreach (var c in components)
        {
            if (c.Definition.SourceColumnIndex is not int column) continue;
            var amount = ReadNumber(source, column, c.Definition.BlankInputAsZero || blankAsZeroColumns?.Contains(column) == true);
            if (amount < 0 || amount > 1_000_000_000m) throw new InvalidOperationException($"{c.Definition.Code}: reviewed source amounts must be non-negative and within the import limit.");
            if (!c.Definition.IsPeriodAdjustment && decimal.Round(amount,4) != amount) throw new InvalidOperationException($"{c.Definition.Code}: monthly/rate input exceeds the employee salary store's four-decimal precision. Review an explicit source precision before applying.");
            if (c.Definition.IsPeriodAdjustment) adjustments.Add((c, decimal.Round(amount, 2)));
            else inputs[c.Id] = amount;
        }
        // Only explicit recurring bases/rates are persisted. Calculated output values never become employee overrides.
        var annual = components.Where(c => !c.Definition.IsPeriodAdjustment && c.Definition.Category is "Earning" or "Reimbursement")
            .Sum(c => inputs.TryGetValue(c.Id, out var amount) ? c.Definition.CalculationType == "Daily Rate" ? amount * days : amount
                : decimal.TryParse(c.Definition.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var fixedValue) ? fixedValue : 0) * 12m;
        var snapshotComponents = components.Select(c => new PayrollReviewComponent(c.Id, c.Definition.Code.ToUpperInvariant(), c.Definition.Category,
            c.Definition.ComponentRole, c.Definition.StatutoryType, c.Definition.IsPeriodAdjustment ? "Manual / Variable" : c.Definition.CalculationType,
            c.Definition.Value, c.Definition.Formula, c.Definition.BaseComponent, c.Definition.ProRata, c.Definition.Taxable, true, c.Definition.Priority,
            ResolvedPayType(c.Definition))).ToList();
        var lines = components.Select(c => new PayrollReviewLine(c.Id, c.Definition.Value, c.Definition.Formula,
            c.Definition.IsPeriodAdjustment ? "Manual / Variable" : c.Definition.CalculationType, c.Definition.BaseComponent,
            c.Definition.IsPeriodAdjustment ? "false" : c.Definition.ProRata.ToString().ToLowerInvariant())).ToList();
        annual = DeriveReviewedFormulaAnnualBasis(clientId, request, components, snapshotComponents, lines, inputs, days, annual);
        if (annual <= 0 && !components.Any(c => c.Definition.CalculationType == "Daily Rate"))
            throw new InvalidOperationException("A positive full-month salary input is required; attendance zero is valid and does not make the monthly salary zero. In Salary components and formulas, review Source input column for a recurring monthly amount or daily rate. An earned output/cache is not a monthly salary input. Review captured rate/day references and preserve the source formula and rounding before repreviewing; no salary amount is inferred.");
        var simulationRequest = new PayrollSimulationRequest
        {
            Mode = "Template", ClientId = clientId, TemplateId = "excel-draft", PayrollDays = days, PresentDays = present, PayableDays = payable,
            AnnualCtc = annual, AmountOverrides = new(inputs), Snapshot = new("Template", clientId, "", annual, snapshotComponents, lines)
        };
        var calculated = PayRunRepository.SimulateTemplate(simulationRequest);
        var exemptionChanged = false;
        foreach (var c in components.Where(c => c.Definition.ExemptWhenSourceZero))
        {
            // A zero-attendance period is not evidence of a permanent PF exemption.
            if (payable > 0 && ReadNumber(source, c.Definition.ExpectedColumnIndex!.Value, blankAsZeroColumns?.Contains(c.Definition.ExpectedColumnIndex!.Value) == true) == 0
                && calculated.Lines.Any(l => l.Code.Equals(c.Definition.Code, StringComparison.OrdinalIgnoreCase) && l.Earned > 0))
            {
                inputs[c.Id] = 0; simulationRequest.AmountOverrides[c.Id] = 0;
                exemptionChanged = true;
                issues.Add(new("Review", "Explicit source-zero PF exemption will be saved as an employee override. Review eligibility before applying.", row.Id, c.Definition.Code));
            }
        }
        if (exemptionChanged) calculated = PayRunRepository.SimulateTemplate(simulationRequest);
        foreach (var issue in calculated.Issues) issues.Add(new(issue.Severity, issue.Message, row.Id, issue.Component));
        var preview = new ExcelPayrollSetupPreviewRow { RowId = row.Id, PayrollDays = days, PayableDays = payable, PresentDays = present, AnnualCtc = annual,
            Gross = calculated.Gross, Deductions = calculated.Deductions, Benefits = calculated.Benefits, Net = calculated.Net };
        if (calculated.Complete)
        {
            foreach (var c in components)
            {
                var actual = c.Definition.IsPeriodAdjustment ? adjustments.Where(a => a.Component.Id == c.Id).Sum(a => a.Amount)
                    : calculated.Lines.Single(l => l.Code.Equals(c.Definition.Code, StringComparison.OrdinalIgnoreCase)).Earned;
                decimal? expected = c.Definition.ExpectedColumnIndex is int index ? ReadNumber(source, index, blankAsZeroColumns?.Contains(index) == true) : null;
                preview.Lines.Add(new(c.Definition.Code, expected, actual, expected is decimal e ? actual - e : null)
                {
                    UnroundedEarned = c.Definition.IsPeriodAdjustment ? null : calculated.Lines.Single(l => l.Code.Equals(c.Definition.Code, StringComparison.OrdinalIgnoreCase)).UnroundedEarned,
                    Category = c.Definition.Category
                });
                if (expected is decimal expectedAmount && Math.Abs(actual - expectedAmount) > 0.01m)
                    issues.Add(new("Review", $"Source comparison differs by {(actual - expectedAmount).ToString("0.####", CultureInfo.InvariantCulture)}. Review the formula, classification and rounding.", row.Id, c.Definition.Code));
            }
            preview.Gross += adjustments.Where(a => a.Component.Definition.Category != "Deduction").Sum(a => a.Amount);
            preview.Deductions += adjustments.Where(a => a.Component.Definition.Category == "Deduction").Sum(a => a.Amount);
            preview.Net = Math.Max(0, preview.Gross!.Value - preview.Deductions!.Value);
            var scenario = SourceWhatIfSimulation(simulationRequest, adjustments);
            var baseline = PayRunRepository.SimulateTemplate(scenario);
            if (baseline.Complete && baseline.Gross == preview.Gross && baseline.Deductions == preview.Deductions
                && baseline.Benefits == preview.Benefits && baseline.Net == preview.Net
                && baseline.Lines.Count == preview.Lines.Count && preview.Lines.All(line =>
                    baseline.Lines.SingleOrDefault(l => l.Code == line.Code)?.Earned == line.CalculatedAmount))
                preview.WhatIfSimulation = scenario;
            if (Math.Abs(preview.Net.Value - row.NetPay) > 0.01m)
                issues.Add(new("Review", $"Net pay differs from saved Excel by {(preview.Net.Value-row.NetPay).ToString("0.####", CultureInfo.InvariantCulture)}. Do not queue payroll until the difference is understood.", row.Id, "NET_PAY"));
        }
        return new(preview, inputs, adjustments, issues);
    }

    internal static PayrollSimulationRequest SourceWhatIfSimulation(PayrollSimulationRequest source,
        IReadOnlyList<(ResolvedComponent Component, decimal Amount)> adjustments)
    {
        var snapshot = source.Snapshot ?? throw new InvalidOperationException("Resolved source template is missing.");
        var periodAmounts = adjustments.ToDictionary(a => a.Component.Id, a => a.Amount);
        // The regular template calculator keeps approved transactions separate. In this temporary
        // scenario only, explicitly supplied source-period amounts are fixed, unprorated inputs.
        // Neither the stored template nor an approved adjustment is created/changed by this copy.
        var components = snapshot.Components.Select(c => periodAmounts.ContainsKey(c.Id)
            ? c with { CalculationType = "Fixed Amount", Formula = "", Value = "0", ProRata = false } : c).ToList();
        var lines = snapshot.Lines.Select(l => periodAmounts.ContainsKey(l.ComponentId)
            ? l with { CalculationType = "Fixed Amount", Formula = "", Value = "0", ProRataOverride = "false" } : l).ToList();
        var overrides = new Dictionary<string, decimal>(source.AmountOverrides);
        foreach (var (id, amount) in periodAmounts) overrides[id] = amount;
        return new PayrollSimulationRequest {
            Mode = "Template", ClientId = source.ClientId, TemplateId = "excel-what-if",
            PayrollDays = source.PayrollDays, PresentDays = source.PresentDays, PayableDays = source.PayableDays,
            AnnualCtc = source.AnnualCtc, AmountOverrides = overrides,
            Snapshot = snapshot with { Components = components, Lines = lines }
        };
    }

    internal static IReadOnlyList<ResolvedComponent> ComponentsForRow(IReadOnlyList<ResolvedComponent> components, string rowId) => components.Select(c =>
    {
        if (!c.Definition.RowFormulas.TryGetValue(rowId, out var formula)) return c;
        var definition = JsonSerializer.Deserialize<ExcelPayrollSetupComponent>(JsonSerializer.Serialize(c.Definition, Json), Json)!;
        definition.Formula = formula;
        return c with { Definition = definition };
    }).ToList();

    internal static decimal ReadNumber(ExcelPayslipCalculationSourceRow row, int column, bool reviewedBlankAsZero = false)
    {
        var valueCell = column >= 0 && column < row.Cells.Count ? row.Cells[column] : null;
        if (reviewedBlankAsZero && column >= 0 && (valueCell is null || string.IsNullOrWhiteSpace(valueCell.Value)
            && string.IsNullOrWhiteSpace(valueCell.FormulaText) && !valueCell.MissingCachedValue
            && string.IsNullOrWhiteSpace(valueCell.Error) && string.IsNullOrWhiteSpace(valueCell.CalculationError))) return 0;
        if (column < 0 || column >= row.Cells.Count || row.Cells[column] is not { } cell || cell.MissingCachedValue
            || !string.IsNullOrWhiteSpace(cell.Error) || !string.IsNullOrWhiteSpace(cell.CalculationError)
            || !decimal.TryParse(cell.Value.Trim().Replace(",", ""), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var amount))
            throw new InvalidOperationException($"Source row {row.SourceRow}, column {column + 1}: a numeric source value is missing or invalid. Blank does not mean zero.");
        return amount;
    }
    private static string ReadCellText(ExcelPayslipCalculationSourceRow row, int column) => column >= 0 && column < row.Cells.Count ? row.Cells[column]?.Value.Trim() ?? "" : "";
    internal static string Normalize(string value) => Regex.Replace(value ?? "", "[^A-Za-z0-9]", "").ToUpperInvariant();
    private static Dictionary<string,string> ReadInformation(ExcelPayslipRow row) => row.Information.GroupBy(v => Normalize(v.Label)).ToDictionary(g => g.Key, g => g.First().Value.Trim());
    private static string Info(Dictionary<string,string> information, params string[] labels) => labels.Select(Normalize).Where(information.ContainsKey).Select(k => information[k]).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    internal static Identity? MatchIdentity(ExcelPayslipRow row, Dictionary<string,string> information, IReadOnlyList<Identity> existing,
        ExcelPayrollSetupEmployeeDecision? decision, List<ExcelPayrollSetupIssue> issues)
    {
        void Error(string message) => issues.Add(new("Error", message, row.Id));
        var pan = Info(information,"PAN","PAN Number","PAN No"); var aadhaar = Info(information,"Aadhaar","Aadhar","Aadhaar Number"); var uan = Info(information,"UAN","UAN Number","UAN No");
        var code = existing.Where(e => e.EmployeeCode.Equals(row.EmployeeCode, StringComparison.OrdinalIgnoreCase)).ToList();
        var strong = existing.Where(e => pan.Length > 0 && Normalize(e.Pan) == Normalize(pan) || aadhaar.Length > 0 && Normalize(e.Aadhaar) == Normalize(aadhaar) || uan.Length > 0 && Normalize(e.Uan) == Normalize(uan)).ToList();
        if (code.Count > 1 || strong.Count > 1 || code.Count == 1 && strong.Count == 1 && code[0].Id != strong[0].Id) { Error("Employee code or identity fields match conflicting master records."); return null; }
        Identity? selected = null;
        if (decision?.MatchedEmployeeId is int explicitId)
        {
            selected = existing.SingleOrDefault(e => e.Id == explicitId);
            if (selected is null) { Error("The reviewed employee does not belong to this client."); return null; }
            if (code.Any(e => e.Id != explicitId) || strong.Any(e => e.Id != explicitId)) Error("The reviewed match conflicts with the source employee code or strong identifier.");
        }
        selected ??= code.SingleOrDefault() ?? strong.SingleOrDefault();
        if (selected is null)
        {
            var location = Info(information,"Work Location","Location","Place of Posting","Posting");
            var candidates = existing.Where(e => Normalize(e.Name) == Normalize(row.EmployeeName) && location.Length > 0 && Normalize(e.WorkLocation) == Normalize(location)).ToList();
            if (candidates.Count == 1) selected = candidates[0];
            else if (candidates.Count > 1) Error("Name and work location match several employee masters. Review an explicit employee match.");
            else if (existing.Any(e => Normalize(e.Name) == Normalize(row.EmployeeName))) Error("An employee with this name already exists. Review an explicit match using work location or identifiers before creating another record.");
        }
        if (selected is not null)
        {
            var anchoredName = code.Count == 1 && code[0].Id == selected.Id || strong.Count == 1 && strong[0].Id == selected.Id;
            var sourceName = anchoredName ? Regex.Replace(row.EmployeeName.Trim(), @"^(?:Mr|Ms)\.?\s+", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) : row.EmployeeName;
            if (Normalize(selected.Name) != Normalize(sourceName)) Error("The source name differs from the matched employee. Resolve identity before importing salary.");
            if (pan.Length > 0 && selected.Pan.Length > 0 && Normalize(pan) != Normalize(selected.Pan)
                || aadhaar.Length > 0 && selected.Aadhaar.Length > 0 && Normalize(aadhaar) != Normalize(selected.Aadhaar)
                || uan.Length > 0 && selected.Uan.Length > 0 && Normalize(uan) != Normalize(selected.Uan)) Error("A source identity field conflicts with the matched employee.");
        }
        return selected;
    }

    internal static Employee NewEmployee(int clientId, ExcelPayslipRow row, ExcelPayrollSetupRequest request, Dictionary<string,string> information) => new()
    {
        ClientId = clientId, EmployeeCode = row.EmployeeCode, FirstName = row.EmployeeName.Trim(), LastName = "", WorkEmail = string.IsNullOrWhiteSpace(row.Email) ? Info(information,"Work Email","Email","Email ID") : row.Email,
        Department = First(Info(information,"Department","Dept"),request.DefaultDepartment.Trim()),
        Designation = First(Info(information,"Designation","Job Title","Position"),request.DefaultDesignation.Trim()),
        Grade = First(Info(information,"Grade","Employee Grade"),request.DefaultGrade.Trim()),
        WorkLocationId = request.DefaultWorkLocationId ?? 0, DateOfJoining = SourceDate(Info(information,"Date of Joining","Joining Date","DOJ")), PortalAccess = false, IsActive = true,
        Gender = Info(information,"Gender","Sex"),
        PersonalDetails = new() { PanNumber = Info(information,"PAN","PAN Number","PAN No"), AadhaarNumber = Info(information,"Aadhaar","Aadhar","Aadhaar Number"),
            UanNumber = Info(information,"UAN","UAN Number","UAN No"), EsicNumber = Info(information,"ESIC","ESIC No","ESIC Number","ESI Number","Insurance Number"), Mobile = Info(information,"Mobile","Mobile Number","Mobile No","Phone","Contact No"),
            DateOfBirth = SourceDate(Info(information,"Date of Birth","Birth Date","DOB")), Address = Info(information,"Address"), State = Info(information,"State"), City = Info(information,"City"), District = Info(information,"District"),
            EmploymentType = Info(information,"Employment Type"), SkillCategory = Info(information,"Employee Category","Skill Category"), Source = "Reviewed salary Excel",
            RawDesignation = Info(information,"Designation","Job Title","Position") },
        PaymentDetails = new() { BankName = Info(information,"Bank","Bank Name"), BankAccountNo = Info(information,"Account No","Bank Account No","Account Number","Bank Account Number","A/C No","Bank A/C No","Account"), IfscCode = Info(information,"IFSC","IFSC Code"), PaymentMode = Info(information,"Payment Mode","Payment Method") }
    };
    private static string First(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
    private static string SourceDate(string text) => DateTime.TryParseExact(text,["yyyy-MM-dd","dd/MM/yyyy","dd-MM-yyyy","d/M/yyyy","d-M-yyyy"],CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)
        ? date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture) : "";
}
