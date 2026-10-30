using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal sealed record LocationAllocationMaster(int Id, string Name, string State);
    internal sealed class LocationAllocationPlan
    {
        public Plan Evidence { get; init; } = new();
        public List<PayrollLocationPartPeriod> Periods { get; } = [];
        public string SetupJson { get; set; } = "";
        public LocationAllocationStorageState Storage { get; set; } = new([], []);
    }

    internal static void ValidateLocationAllocationFields(ExcelPayrollSetupRequest request)
    {
        if (request.LocationAllocations is not { } review) return;
        if (review.Version != 1 || !DateTime.TryParseExact(review.PayrollPeriod, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || !DateTime.TryParseExact(review.ConfigurationEffectiveFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || review.Groups is null || review.Groups.Count is < 1 or > 100
            || review.Groups.Any(group => group is null || group.EmployeeId <= 0 || group.Parts is null || group.Parts.Count is < 1 or > 100
                || group.ReferenceAssignmentId is { Length: > 0 } reference && !Regex.IsMatch(reference, "^[a-f0-9]{32}$")
                || group.Parts.Any(part => part is null || string.IsNullOrWhiteSpace(part.RowId) || part.RowId.Length > 100 || part.WorkLocationId <= 0
                    || !Regex.IsMatch(part.TemplateId ?? "", "^[0-9]{1,20}$") || part.ComponentScopes is null || part.ComponentScopes.Count is < 1 or > 100
                    || part.ComponentScopes.Any(scope => !Regex.IsMatch(scope.Key, "^[0-9]{1,20}$") || scope.Value is not ("SourceFormulaPerPart" or "Monthly"))))
            || review.Groups.Select(group => group.EmployeeId).Distinct().Count() != review.Groups.Count)
            throw new InvalidOperationException("Review bounded existing employee, Location Master, Settings template and per-component monthly/location scopes.");
        var rowIds = review.Groups.SelectMany(group => group.Parts).Select(part => part.RowId).ToList();
        if (rowIds.Distinct(StringComparer.Ordinal).Count() != rowIds.Count || !rowIds.Order(StringComparer.Ordinal).SequenceEqual(request.RowIds.Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("Every selected source row must belong to exactly one reviewed employee allocation group.");
    }

    internal static IReadOnlyList<string> LocationAllocationGroupIssues(ExcelPayslipBatch batch, ExcelPayrollSetupRequest request)
    {
        var issues = new List<string>();
        var review = request.LocationAllocations!;
        if (review.PayrollPeriod != batch.Month) issues.Add("The payroll allocation period must remain the saved source month.");
        if (!review.Reviewed || !review.ReviewedSettingsApplicability) issues.Add("Review all location allocations and exact Settings-template applicability before Apply.");
        foreach (var group in review.Groups)
        {
            var selected = group.Parts.Select(part => batch.Rows.SingleOrDefault(row => row.Id == part.RowId)).ToList();
            if (selected.Any(row => row is null)) { issues.Add("A location allocation refers to a row outside this saved source batch."); continue; }
            var rows = selected.Select(row => row!).ToList();
            var codes = rows.Select(row => row.EmployeeCode.Trim().ToUpperInvariant()).Distinct().ToList();
            if (codes.Count != 1 || codes[0].Length == 0) { issues.Add("Each allocation group needs one verified source employee code."); continue; }
            var complete = batch.Rows.Where(row => row.EmployeeCode.Trim().Equals(codes[0], StringComparison.OrdinalIgnoreCase)).ToList();
            if (!complete.Select(row => row.Id).Order(StringComparer.Ordinal).SequenceEqual(rows.Select(row => row.Id).Order(StringComparer.Ordinal)))
                issues.Add("Select every saved source part for an employee together; a partial location salary cannot replace their monthly configuration.");
            if (complete.Count > 1 && !ExcelPayslipRowIdentity.IsVerifiedLocationSplit(complete, batch.Month))
                issues.Add("Repeated source rows need verified employee/location-split evidence before allocation review.");
            if (rows.Select(row => ExcelPayslipRowIdentity.NormalizeName(row.EmployeeName)).Distinct().Count() != 1)
                issues.Add("Location parts have conflicting source employee names.");
        }
        return issues;
    }

    internal static string LocationSettingsRulesHash(JsonObject template, IReadOnlyList<JsonObject> masters)
    {
        var lines = (template["lines"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        return Hash(JsonSerializer.Serialize(new { Template = template,
            Components = lines.Select(line => masters.Single(master => Text(master,"id") == Text(line,"componentId"))) }, Json));
    }

    internal static LocationAllocationPlan ReviewLocationAllocationPlan(int clientId, ExcelPayrollSetupRequest request,
        ExcelPayslipBatch batch, ExcelPayslipCalculationSource source, string setupJson, IReadOnlyList<Identity> identities,
        IReadOnlyDictionary<int, Employee> employeeMasters, IReadOnlyList<LocationAllocationMaster> locations,
        IReadOnlyList<ExcelPayslipOriginal>? originals = null)
    {
        ValidateLocationAllocationFields(request);
        var review = request.LocationAllocations!;
        var sourceHash = Hash(JsonSerializer.Serialize(new { batch, source }, Json));
        var allocation = new ExcelPayrollLocationAllocationPreview { PayrollPeriod = batch.Month, ConfigurationEffectiveFrom = review.ConfigurationEffectiveFrom };
        var evidence = new Plan { Batch = batch, Source = source, LinkedOriginals = originals?.ToList() ?? [] };
        evidence.Preview.LocationAllocations = allocation;
        evidence.Preview.SelectedSourceRows = DescribeSelectedSources(batch, request);
        evidence.Preview.Transfer = new() { SourceMonth = batch.Month, SourceBatchId = batch.Id, SourceHash = sourceHash,
            EffectiveFrom = review.ConfigurationEffectiveFrom, PreparationStatus = "Blocked" };
        var result = new LocationAllocationPlan { Evidence = evidence, SetupJson = setupJson };
        void Error(string message, string? row = null, string? code = null) => evidence.Preview.Issues.Add(new("Error", message, row, code));
        foreach (var message in LocationAllocationGroupIssues(batch, request)) Error(message);
        if (batch.ClientId != clientId) Error("The source batch belongs to another client.");
        if (request.TransferReviewVersion != 1 || !request.ReviewedPolicyScope) Error("Complete saved-source formula and effective payroll-policy review before importing allocations.");
        if (request.PresentDaysColumnIndex is null) Error("Map and review actual present/worked days separately from payable days for location allocations.");
        if (request.ImportAttendance && request.AttendanceReview?.Mode != "SourceSeed" || !request.ImportAttendance && request.AttendanceReview?.Mode != "VerifyExistingNative")
            Error("Choose source-seed attendance when importing totals, or verify existing native employee totals when retaining attendance.");
        if (source.CaptureVersion > 0 && source.DependencyReviewStatus != "Reviewed") Error("Resolve the captured workbook dependencies in a reviewed saved batch before source transfer.");
        var setup = JsonNode.Parse(setupJson)!.AsObject();
        var masters = (setup["salaryComponents"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var templates = (setup["salaryStructures"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var resolved = new List<ResolvedComponent>();
        foreach (var component in request.Components)
        {
            var candidates = masters.Where(master => component.ExistingComponentId is { Length: > 0 }
                ? Text(master,"id") == component.ExistingComponentId : Text(master,"code").Equals(component.Code, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count != 1) { Error("Location allocations require an existing unambiguous Settings component.", code: component.Code); continue; }
            var master = candidates[0];
            var differences = ReusedComponentDifferences(master, component);
            if (differences.Count > 0) Error("The reused Settings component differs in " + string.Join(", ", differences) + "; shared masters are not changed.", code: component.Code);
            if (component.IsPeriodAdjustment) Error("Location salary assignments contain recurring inputs only. Review period adjustments through their existing workflow.", code: component.Code);
            resolved.Add(new(component, Text(master,"id"), false));
        }
        evidence.Components.AddRange(resolved);
        evidence.Preview.Transfer.Components = resolved.Select(component => new ExcelPayrollTransferComponent(component.Definition.Code, component.Definition.Category, "Reuse unchanged", component.Id)).ToList();
        foreach (var group in review.Groups)
        {
            var previewGroup = new ExcelPayrollLocationAllocationGroupPreview { EmployeeId = group.EmployeeId, ReferenceAssignmentId = group.ReferenceAssignmentId };
            allocation.Groups.Add(previewGroup);
            var period = new PayrollLocationPartPeriod { ClientId = clientId, EmployeeId = group.EmployeeId, PayPeriod = batch.Month, ReferenceAssignmentId = group.ReferenceAssignmentId };
            result.Periods.Add(period);
            if (!employeeMasters.TryGetValue(group.EmployeeId, out var employee) || employee.ClientId != clientId)
            { Error("Choose a verified existing employee of the selected client; this path never creates identities."); continue; }
            foreach (var part in group.Parts)
            {
                var row = batch.Rows.SingleOrDefault(row => row.Id == part.RowId);
                var cells = row is null ? null : source.Rows.SingleOrDefault(cells => cells.SourceRow == row.SourceRow);
                if (row is null || cells is null) { Error("The selected part has no authoritative source cells.", part.RowId); continue; }
                var matched = MatchIdentity(row, SourceInformation(row, source, request.SourceWorkLocationColumnIndex), identities,
                    new() { RowId = row.Id, MatchedEmployeeId = group.EmployeeId }, evidence.Preview.Issues);
                if (matched?.Id != group.EmployeeId || matched.EligibleForMonth != true) { Error("The reviewed employee identity is unavailable or not employed in the source month.", row.Id); continue; }
                var location = locations.SingleOrDefault(location => location.Id == part.WorkLocationId);
                if (location is null || string.IsNullOrWhiteSpace(location.State)) { Error("Choose an active client Location Master with a reviewed state. Source location text is not rewritten.", row.Id); continue; }
                var template = templates.SingleOrDefault(template => Text(template,"id") == part.TemplateId && RefId(Text(template,"clientId")) == clientId && Text(template,"active") != "false");
                if (template is null) { Error("Choose an active existing Settings template belonging to this client.", row.Id); continue; }
                try
                {
                    var components = ComponentsForRow(resolved, row.Id);
                    if (TransferTemplateDifferences(template, components).Count > 0) { Error("The selected Settings template does not exactly preserve the reviewed component formulas, order, inputs and proration. Prepare the proper Settings template first.", row.Id); continue; }
                    var ids = components.Select(component => component.Id).Order(StringComparer.Ordinal);
                    if (!ids.SequenceEqual(part.ComponentScopes.Keys.Order(StringComparer.Ordinal))) { Error("Review Monthly or SourceFormulaPerPart for every exact Settings component ID.", row.Id); continue; }
                    var arithmetic = CalculateRow(clientId, request, components, cells, row, source.Columns.Where(column => column.BlankAsZero).Select(column => column.ColumnIndex).ToHashSet());
                    if (decimal.Round(arithmetic.Preview.PresentDays, 4) != arithmetic.Preview.PresentDays
                        || decimal.Round(arithmetic.Preview.PayableDays, 4) != arithmetic.Preview.PayableDays
                        || decimal.Round(arithmetic.Preview.AnnualCtc, 4) != arithmetic.Preview.AnnualCtc)
                        throw new InvalidOperationException("Source allocation days or annual salary exceed the reviewed store's four-decimal precision. Review source precision; Apply cannot round the saved inputs.");
                    var scopeEmployee = JsonSerializer.Deserialize<Employee>(JsonSerializer.Serialize(employee, Json), Json)!;
                    scopeEmployee.WorkLocationId = location.Id;
                    var planned = new PlannedRow { RowId = row.Id, SourceRow = row.SourceRow, Employee = scopeEmployee,
                        TemplateId = part.TemplateId, Components = components, Preview = arithmetic.Preview, SalaryInputs = arithmetic.Inputs };
                    planned.Preview.EmployeeId = group.EmployeeId; planned.Preview.EmployeeCode = employee.EmployeeCode;
                    planned.Preview.EmployeeName = row.EmployeeName; planned.Preview.TemplateId = part.TemplateId;
                    planned.Preview.Action = "Save location salary assignment and source-period allocation";
                    evidence.Rows.Add(planned); evidence.Preview.Rows.Add(planned.Preview);
                    evidence.Preview.Issues.AddRange(arithmetic.Issues);
                    var formulas = components.Select(component => InspectTransferFormula(request, source, row, component.Definition)).ToList();
                    evidence.Preview.Transfer.Formulas.AddRange(formulas);
                    foreach (var formula in formulas.Where(formula => formula.Status == "Blocked")) Error(formula.Reason, row.Id, formula.Code);
                    var amounts = ClassifyTransferAmounts(evidence, planned, request, sourceHash, formulas);
                    evidence.Preview.Transfer.AmountComparisons.Add(amounts);
                    if (amounts.Status == "Explained rounding")
                    {
                        amounts.Acknowledged = request.RoundingReviews.GetValueOrDefault(row.Id) == amounts.ProofHash;
                        if (!amounts.Acknowledged) Error("Review this source-bound deterministic rounding proof and refresh preview. No output amount is forced.", row.Id);
                    }
                    else if (amounts.Status != "Exact at raw precision") Error("Source component comparison is unresolved. " + amounts.Reason, row.Id);
                    var attendance = ProveAttendanceInput(evidence, planned, request, clientId, sourceHash, reviewedLocationAllocation: true);
                    evidence.Preview.Transfer.AttendanceProofs.Add(attendance);
                    if (attendance.Status == "Blocked") Error(attendance.Reason, row.Id, "Attendance mapping");
                    var rulesHash = LocationSettingsRulesHash(template, masters);
                    var assignmentId = Hash(JsonSerializer.Serialize(new { clientId, group.EmployeeId, location.Id, location.State, part.TemplateId,
                        arithmetic.Preview.AnnualCtc, SalaryInputs = arithmetic.Inputs.OrderBy(pair => pair.Key), ComponentScopes = part.ComponentScopes.OrderBy(pair => pair.Key),
                        rulesHash, sourceHash, review.ConfigurationEffectiveFrom }, Json))[..32];
                    var partKey = Hash(batch.Id + ":" + row.Id)[..32];
                    previewGroup.Parts.Add(new() { RowId = row.Id, SourceRow = row.SourceRow, PartKey = partKey, AssignmentId = assignmentId,
                        WorkLocationId = location.Id, WorkLocationName = location.Name, WorkState = location.State, TemplateId = part.TemplateId,
                        PayrollDays = arithmetic.Preview.PayrollDays, PresentDays = arithmetic.Preview.PresentDays, PayableDays = arithmetic.Preview.PayableDays,
                        AnnualCtc = arithmetic.Preview.AnnualCtc, SalaryInputs = new(arithmetic.Inputs), ComponentScopes = new(part.ComponentScopes), RulesHash = rulesHash, SourceHash = sourceHash });
                    period.Parts.Add(new() { PartKey = partKey, AssignmentId = assignmentId, WorkLocationId = location.Id, WorkState = location.State,
                        SalaryStructureId = part.TemplateId, AnnualCtc = arithmetic.Preview.AnnualCtc, SalaryInputs = new(arithmetic.Inputs), ComponentScopes = new(part.ComponentScopes),
                        PayrollDays = arithmetic.Preview.PayrollDays, PresentDays = arithmetic.Preview.PresentDays, PayableDays = arithmetic.Preview.PayableDays });
                }
                catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException) { Error(error.Message, row.Id); }
            }
        }
        if (request.TemplateScopeReviewVersion != 1) Error("Review the current template applicability scope before importing source allocations.");
        else foreach (var issue in TemplateScopeIssues(request, evidence.Rows)) Error(issue);
        foreach (var column in source.Columns.Where(column => request.SourceColumnRoles.GetValueOrDefault(column.ColumnIndex, column.Kind) is "earning" or "deduction" or "employer"))
            if (!request.Components.Any(component => component.ExpectedColumnIndex == column.ColumnIndex)) Error("Every classified source financial output needs an exact component comparison mapping.");
        FinalizePresentDaysEvidence(evidence, request, clientId);
        evidence.Preview.TemplateIds = evidence.Rows.Select(row => row.TemplateId).Distinct().ToList();
        evidence.Preview.TemplateId = evidence.Preview.TemplateIds.FirstOrDefault() ?? "";
        return result;
    }

    private async Task<LocationAllocationPlan> BuildLocationAllocationPlanAsync(MySqlConnection db, MySqlTransaction tx, int clientId, ExcelPayrollSetupRequest request)
    {
        await ExcelPayslipRepository.RequireCurrentConsolidationSourceAsync(db, tx, clientId, request.BatchId);
        var (batch, source) = await ReadSourceAsync(db, tx, clientId, request.BatchId);
        var review = request.LocationAllocations!;
        var ids = review.Groups.Select(group => group.EmployeeId).Order().ToArray();
        var masters = new Dictionary<int, Employee>();
        foreach (var id in ids)
        {
            var employee = await EmployeeRepository.LoadRecruitmentEmployeeAsync(db, id, tx);
            if (employee?.ClientId == clientId) masters[id] = employee;
        }
        var settings = await PayrollDataTableStore.GetSetupJsonAsync(db, tx);
        var identities = await ReadIdentitiesAsync(db, tx, clientId, batch.Month);
        var locations = (await db.QueryAsync<LocationAllocationMaster>("SELECT Id,Name,State FROM worklocations WHERE ClientId=@ClientId AND IsActive=TRUE ORDER BY Id", new { ClientId = clientId }, tx)).ToList();
        var originals = source.CaptureVersion == 1 ? await ExcelPayslipRepository.ReadLinkedOriginalsAsync(db, tx, clientId, batch.Id) : [];
        var plan = ReviewLocationAllocationPlan(clientId, request, batch, source, settings, identities, masters, locations, originals.ToList());
        var preview = plan.Evidence.Preview;
        void Error(string message) => preview.Issues.Add(new("Error", message));
        var schemaReady = await HasLocationAllocationSchemaAsync(db, tx) && await HasReceiptTableAsync(db, tx);
        preview.MigrationRequired = !schemaReady;
        if (!schemaReady) Error("The reviewed additive location-assignment/allocation migration is required before Apply. Preview never provisions tables.");
        else
        {
            plan.Storage = await ReadLocationAllocationStateAsync(db, tx, clientId, ids, batch.Month);
            if (plan.Storage.Allocations.Count > 0) Error("An employee already has saved location allocations for this payroll period. Review the existing receipt; allocations cannot be silently replaced or merged.");
        }
        if (review.ConfigurationEffectiveFrom != DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) Error("Configuration effective date must equal the current server import date. Refresh context; the saved payroll month remains separate.");
        var applied = await AppliedSourceRowsAsync(db, tx, clientId, batch.Id);
        if (request.RowIds.Any(applied.Contains)) Error("Source rows were already transferred. Open the existing receipt; a different selection cannot replay them.");
        var conflicts = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM payrunemployees pe JOIN payruns pr ON pr.Id=pe.PayRunId
WHERE pr.ClientId=@ClientId AND pr.PayPeriod=@Month AND pe.EmployeeId IN @Ids AND pe.IsSkipped=FALSE
AND pr.Status IN ('Draft','Queued','Processing','Pending Approval','Approved','Partially Paid','Paid')", new { ClientId = clientId, Month = batch.Month, Ids = ids }, tx);
        if (conflicts > 0) Error("Review existing payroll for this employee period before creating location salary allocations.");
        var adjustments = (await db.QueryAsync("SELECT * FROM payrolladjustments WHERE ClientId=@ClientId AND EmployeeId IN @Ids AND PayPeriod=@Month ORDER BY EmployeeId,Id", new { ClientId = clientId, Ids = ids, Month = batch.Month }, tx)).ToList();
        if (adjustments.Count > 0) Error("Review existing period adjustments before importing source allocations that could count their amounts twice.");
        var attendanceJson = await db.ExecuteScalarAsync<string?>("SELECT JSON_EXTRACT(SettingsJson,'$.attendanceIntegration.WeeklyOffRuleVersions') FROM modulesettings WHERE client_id=@ClientId AND ModuleCode='leave_attendance'", new { ClientId = clientId }, tx);
        if (request.ImportAttendance && !string.IsNullOrWhiteSpace(attendanceJson) && attendanceJson is not "null" and not "[]") Error("Published weekly-off rules require the existing native attendance workflow. Retain attendance and verify employee aggregate totals instead of seeding monthly totals.");
        var attendance = await PayRunRepository.ReadNativeAttendanceProofAsync(db, tx, clientId, batch.Month, ids);
        plan.Evidence.NativeAttendanceInputs = attendance;
        foreach (var period in plan.Periods.Where(period => period.Parts.Count > 0))
        {
            var present = period.Parts.Sum(part => part.PresentDays); var payable = period.Parts.Sum(part => part.PayableDays);
            var days = request.ImportAttendance ? period.Parts[0].PayrollDays : attendance.GetValueOrDefault(period.EmployeeId)?.PayrollDays;
            if (days is null || days is < 1 or > 31 || present > days || payable > days || request.ImportAttendance && period.Parts.Any(part => part.PayrollDays != days))
            { Error("Employee totals need one reviewed native payroll divisor and bounded present/payable allocations; mixed source divisors require existing native attendance review."); continue; }
            if (!request.ImportAttendance && (attendance.GetValueOrDefault(period.EmployeeId) is not { } native || native.Error.Length > 0 || native.PresentDays != present || native.PayableDays != payable))
            { Error("Existing native employee attendance does not equal the reviewed location allocation totals. Review attendance; no source totals are invented."); continue; }
            try
            {
                var calculated = PayRunRepository.SimulateLocationParts(period, settings, days.Value, present, payable);
                foreach (var issue in calculated.Issues.Where(issue => issue.Severity == "Error")) Error(issue.Message);
                var sourceRows = plan.Evidence.Rows.Where(row => row.Employee.Id == period.EmployeeId).ToList();
                var expectedCodes = sourceRows.SelectMany(row => row.Preview.Lines).Select(line => line.Code.ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal);
                if (!expectedCodes.SequenceEqual(calculated.Lines.Select(line => line.Code.ToUpperInvariant()).Order(StringComparer.Ordinal)))
                    Error("Settings location calculation does not cover the exact source component-code set.");
                foreach (var line in calculated.Lines)
                    if (sourceRows.SelectMany(row => row.Preview.Lines).Any(sourceLine => sourceLine.Code.Equals(line.Code, StringComparison.OrdinalIgnoreCase) && sourceLine.Category != line.Category)
                        || line.Earned != sourceRows.Sum(row => row.Preview.Lines.Where(sourceLine => sourceLine.Code.Equals(line.Code, StringComparison.OrdinalIgnoreCase)).Sum(sourceLine => sourceLine.CalculatedAmount)))
                        Error($"{line.Code}: Settings monthly/location scope arithmetic differs from the reviewed source parts. Correct the scope/reference/template; no expected amount is forced.");
                if (calculated.Complete && calculated.Net != sourceRows.Sum(row => row.Preview.Net ?? 0)) Error("Settings monthly/location aggregate net differs from the reviewed source-part arithmetic.");
            }
            catch (InvalidOperationException error) { Error(error.Message); }
        }
        var monthly = (await db.QueryAsync("SELECT * FROM employee_monthly_attendance WHERE client_id=@ClientId AND employee_id IN @Ids AND attendance_month=@Month ORDER BY employee_id", new { ClientId = clientId, Ids = ids, Month = batch.Month }, tx)).ToList();
        var memberships = (await db.QueryAsync(@"SELECT m.employee_id,g.* FROM attendance_group_employees m JOIN attendance_groups g ON g.id=m.attendance_group_id WHERE g.client_id=@ClientId AND m.employee_id IN @Ids ORDER BY m.employee_id,g.id", new { ClientId = clientId, Ids = ids }, tx)).ToList();
        var policies = (await db.QueryAsync("SELECT * FROM modulesettings WHERE client_id=@ClientId AND ModuleCode IN ('payroll','leave_attendance') ORDER BY Id", new { ClientId = clientId }, tx)).ToList();
        preview.PlanHash = Hash(JsonSerializer.Serialize(new { clientId, request, batch, source, settings, identities, masters, locations, originals, plan.Storage, attendance, monthly, memberships, policies, adjustments, conflicts }, Json));
        preview.Transfer!.PreparationStatus = preview.CanApply ? "Ready for reviewed location salary and source-period allocation Apply; saved Employee parity pending" : "Blocked";
        return plan;
    }

    private async Task<ExcelPayrollSetupResult> ApplyLocationAllocationsAsync(int clientId, ExcelPayrollSetupApplyRequest apply, string actor)
    {
        if (!Regex.IsMatch(apply.PlanHash ?? "", "^[a-fA-F0-9]{64}$") || !Guid.TryParse(apply.RequestId, out _))
            throw new InvalidOperationException("Preview the source-bound location allocations and provide their plan hash and stable request ID.");
        await using var db = Db(); await db.OpenAsync();
        if (!await HasReceiptTableAsync(db) || !await HasLocationAllocationSchemaAsync(db)) throw new InvalidOperationException("Complete the reviewed additive location-allocation migration before Apply.");
        await using var tx = await db.BeginTransactionAsync();
        if (await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@ClientId AND IsActive=TRUE FOR UPDATE", new { ClientId = clientId }, tx) is null) throw new InvalidOperationException("Select an active client.");
        var requestHash = Hash(JsonSerializer.Serialize(apply.Request, Json)); var selectionHash = SelectionHash(apply.Request.RowIds);
        var receipt = await db.QueryFirstOrDefaultAsync<Receipt>(@"SELECT id Id,status Status,request_id RequestId,request_hash RequestHash,CAST(request_json AS CHAR) RequestJson,CAST(result_json AS CHAR) ResultJson
FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND (request_id=@RequestId OR (batch_id=@BatchId AND selection_hash=@SelectionHash)) LIMIT 1 FOR UPDATE", new { ClientId = clientId, apply.RequestId, apply.Request.BatchId, SelectionHash = selectionHash }, tx);
        if (receipt is { Status: "Applied" })
        {
            if (!SameReviewedRequest(receipt.RequestJson, apply.Request)) throw new InvalidOperationException("This source selection was already applied with different reviewed settings. Open its receipt.");
            var replay = JsonSerializer.Deserialize<ExcelPayrollSetupResult>(receipt.ResultJson, Json)!; replay.Replayed = true; await tx.CommitAsync(); return replay;
        }
        if (receipt is not null && (receipt.RequestId != apply.RequestId || receipt.Status is not ("Draft" or "Kept"))) throw new InvalidOperationException("Open the existing saved draft/result for this source selection.");
        await LockApplyInputsAsync(db, tx, clientId, apply.Request);
        var employeeIds = apply.Request.LocationAllocations!.Groups.Select(group => group.EmployeeId).Order().ToArray();
        await db.QueryAsync<int>("SELECT Id FROM worklocations WHERE ClientId=@ClientId ORDER BY Id FOR UPDATE", new { ClientId = clientId }, tx);
        await ReadLocationAllocationStateAsync(db, tx, clientId, employeeIds, apply.Request.LocationAllocations.PayrollPeriod, lockRows: true);
        var plan = await BuildLocationAllocationPlanAsync(db, tx, clientId, apply.Request);
        if (!plan.Evidence.Preview.CanApply) throw new InvalidOperationException(string.Join(" ", plan.Evidence.Preview.Issues.Where(issue => issue.Severity == "Error").Take(5).Select(issue => issue.Message)));
        RequireMatchingSetupPlanHash(plan.Evidence.Preview.PlanHash, apply.PlanHash!);
        var result = new ExcelPayrollSetupResult { ImportId = receipt?.Id ?? Guid.NewGuid().ToString("N"), LocationAllocations = plan.Evidence.Preview.LocationAllocations,
            Transfer = plan.Evidence.Preview.Transfer, EmployeeIds = employeeIds.ToList(), UpdatedCount = employeeIds.Length,
            TemplateId = plan.Evidence.Preview.TemplateId, TemplateIds = plan.Evidence.Preview.TemplateIds.ToList() };
        var assignments = new List<LocationAssignmentStorageRow>(); var allocations = new List<LocationAllocationStorageRow>();
        var date = DateTime.ParseExact(result.LocationAllocations!.ConfigurationEffectiveFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var auditActor = actor.Length > 190 ? actor[..190] : actor;
        foreach (var group in result.LocationAllocations.Groups)
        foreach (var part in group.Parts)
        {
            assignments.Add(new(part.AssignmentId, clientId, group.EmployeeId, part.WorkLocationId, part.WorkState, part.TemplateId, part.AnnualCtc,
                JsonSerializer.Serialize(part.SalaryInputs, Json), JsonSerializer.Serialize(part.ComponentScopes, Json), part.RulesHash, part.SourceHash, date,
                result.ImportId, apply.Request.BatchId, group.Parts.Where(other => other.AssignmentId == part.AssignmentId).Select(other => other.RowId).Order(StringComparer.Ordinal).First(), auditActor));
            allocations.Add(new(clientId, group.EmployeeId, result.LocationAllocations.PayrollPeriod, part.PartKey, part.AssignmentId, part.PayrollDays,
                part.PresentDays, part.PayableDays, group.ReferenceAssignmentId, result.ImportId, apply.Request.BatchId, part.RowId, part.SourceHash));
            result.LocationAssignmentReceipts.Add(new(part.RowId, group.EmployeeId, part.PartKey, part.AssignmentId, part.WorkLocationId, part.TemplateId,
                result.LocationAllocations.PayrollPeriod, result.LocationAllocations.ConfigurationEffectiveFrom, part.RulesHash, part.SourceHash));
        }
        await InsertLocationAllocationStorageAsync(db, tx, assignments, allocations);
        foreach (var row in plan.Evidence.Rows) result.TransferRows.Add(new(row.RowId, row.Employee.Id, row.Preview.PayrollDays, row.Preview.PresentDays, row.Preview.PayableDays,
            plan.Evidence.Batch.Rows.Single(sourceRow => sourceRow.Id == row.RowId).NetPay, row.Preview.Lines.ToList()));
        if (apply.Request.ImportAttendance)
        {
            var aggregate = new Plan { Batch = plan.Evidence.Batch, Source = plan.Evidence.Source };
            foreach (var group in result.LocationAllocations.Groups)
            {
                var first = plan.Evidence.Rows.First(row => row.Employee.Id == group.EmployeeId);
                aggregate.Rows.Add(new() { RowId = first.RowId, SourceRow = first.SourceRow, Employee = first.Employee,
                    Preview = new() { PayrollDays = group.Parts[0].PayrollDays, PresentDays = group.Parts.Sum(part => part.PresentDays), PayableDays = group.Parts.Sum(part => part.PayableDays) } });
            }
            result.AttendanceGroupId = await InsertAttendanceAsync(db, tx, clientId, apply.Request, aggregate, result.ImportId, result);
        }
        result.Transfer!.PreparationStatus = "Location salary assignments and source-period allocations saved; verify actual regular Employee payroll before processing.";
        var values = new { Id = result.ImportId, ClientId = clientId, apply.Request.BatchId, apply.RequestId, SelectionHash = selectionHash, RequestHash = requestHash,
            apply.PlanHash, RequestJson = JsonSerializer.Serialize(apply.Request, Json), ResultJson = JsonSerializer.Serialize(result, Json), Actor = auditActor };
        if (receipt is null) await db.ExecuteAsync(@"INSERT INTO excel_payroll_setup_imports(id,client_id,batch_id,request_id,selection_hash,request_hash,plan_hash,status,request_json,result_json,created_by)
VALUES(@Id,@ClientId,@BatchId,@RequestId,@SelectionHash,@RequestHash,@PlanHash,'Applied',CAST(@RequestJson AS JSON),CAST(@ResultJson AS JSON),@Actor)", values, tx);
        else await db.ExecuteAsync(@"UPDATE excel_payroll_setup_imports SET status='Applied',batch_id=@BatchId,selection_hash=@SelectionHash,request_hash=@RequestHash,plan_hash=@PlanHash,request_json=CAST(@RequestJson AS JSON),result_json=CAST(@ResultJson AS JSON) WHERE id=@Id AND status IN ('Draft','Kept')", values, tx);
        await tx.CommitAsync(); return result;
    }
}
