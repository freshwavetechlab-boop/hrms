using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    private PortableIntegrationCredentialProtector? reviewedPtProtector;
    private PortableIntegrationCredentialProtector PtProtector => reviewedPtProtector ??= new(configuration,
        new EphemeralDataProtectionProvider(), NullLogger<PortableIntegrationCredentialProtector>.Instance);
    private sealed record PtEnvelope(int Version, string Kind, string Payload);
    private sealed class PtSetting { public string Code { get; set; } = ""; public string Json { get; set; } = ""; public bool Enabled { get; set; } }
    private sealed class PtSourceJson { public string Batch { get; set; } = ""; public string Source { get; set; } = ""; }
    private sealed record PtSource(ExcelPayslipBatch Batch, string Hash, ExcelPayslipCalculationSource Source);
    private sealed record PtReceipt(int ActorId, string Fingerprint, ReviewedProfessionalTaxSaveResult Result,
        IReadOnlyList<ReviewedProfessionalTaxControl?> Previous);
    private sealed record PtPlan(ReviewedProfessionalTaxPreview Preview, List<PayRunSourceEmployee> Employees,
        Dictionary<int, ReviewedProfessionalTaxControl?> Previous, Dictionary<int, string> RecordHashes);
    private static async Task LockPtNativeInputsAsync(MySqlConnection db, MySqlTransaction tx,
        int clientId, string month, int[] ids)
    {
        // Existing rows and indexed employee/month gaps stay stable until the
        // reviewed control and receipt commit. No schema or business data write.
        foreach (var table in new[] { "employeesalarycomponents", "employeepersonaldetails", "employeepaymentdetails",
            "employee_it0000_actions", "employee_it0008_basic_pay", "employee_it0009_bank_details" })
            await db.QueryAsync<int>($"SELECT EmployeeId FROM `{table}` WHERE EmployeeId IN @Ids FOR UPDATE", new { Ids = ids }, tx);
        await db.QueryAsync<int>("SELECT employee_id FROM employee_monthly_attendance WHERE client_id=@ClientId AND attendance_month=@Month AND employee_id IN @Ids FOR UPDATE", new { ClientId = clientId, Month = month, Ids = ids }, tx);
        var start = PayPeriodStart(month).AddMonths(-1); var end = start.AddMonths(3).AddDays(-1);
        await db.QueryAsync<int>("SELECT employee_id FROM employee_daily_attendance WHERE client_id=@ClientId AND employee_id IN @Ids AND attendance_date BETWEEN @Start AND @End FOR UPDATE", new { ClientId = clientId, Ids = ids, Start = start, End = end }, tx);
        if (await PtTableAsync(db, tx, "attendance_groups"))
        {
            var groups = (await db.QueryAsync<int>("SELECT id FROM attendance_groups WHERE client_id=@ClientId FOR UPDATE", new { ClientId = clientId }, tx)).ToArray();
            if (await PtTableAsync(db, tx, "attendance_group_employees"))
                await db.QueryAsync<int>("SELECT employee_id FROM attendance_group_employees WHERE attendance_group_id IN @Groups FOR UPDATE", new { Groups = groups }, tx);
        }
        await db.QueryAsync<string>("SELECT ModuleCode FROM modulesettings WHERE client_id=@ClientId FOR UPDATE", new { ClientId = clientId }, tx);
        await db.QueryAsync<int>("SELECT Id FROM payruns WHERE ClientId=@ClientId AND PayPeriod=@Month AND RunType='Regular' FOR UPDATE", new { ClientId = clientId, Month = month }, tx);
    }
    private static async Task LockPtConfigurationAsync(MySqlConnection db, MySqlTransaction tx, List<PayRunSourceEmployee> employees)
    {
        var structures = employees.Select(employee => employee.SalaryStructureId).Where(value => value.Length > 0).Distinct().ToArray();
        var components = (await db.QueryAsync<string>("SELECT ComponentId FROM salarystructurelines WHERE StructureId IN @Ids FOR UPDATE", new { Ids = structures }, tx)).ToArray();
        await db.QueryAsync<long>("SELECT Id FROM salarystructures WHERE Id IN @Ids FOR UPDATE", new { Ids = structures }, tx);
        await db.QueryAsync<long>("SELECT Id FROM salarycomponents WHERE Id IN @Ids FOR UPDATE", new { Ids = components }, tx);
        await db.QueryAsync<long>("SELECT Id FROM payrollsetups ORDER BY Id LIMIT 1 FOR UPDATE", transaction: tx);
        // PT is a shared statutory catalog. Lock its small range while saving;
        // the fingerprint itself still contains only the applicable state rules.
        await db.QueryAsync<long>("SELECT Id FROM professionaltaxslabs FOR UPDATE", transaction: tx);
    }
    internal static bool CanManageReviewedProfessionalTax(AuthUser user, int clientId) => SelectedPayRunAuthorization.CanAccess(user, clientId) && user.Id > 0 && user.IsActive
        && !user.MustChangePassword && SelectedClientAccess.Allows(user, clientId)
        && new[] { "settings.manage", "employees.manage", "payroll.run" }.All(permission => user.Permissions?.Contains(permission, StringComparer.OrdinalIgnoreCase) == true);
    private static void RequirePtActor(AuthUser actor, int clientId)
    {
        if (!CanManageReviewedProfessionalTax(actor, clientId)) throw new UnauthorizedAccessException("Reviewed period Professional Tax requires payroll, employee and settings management for this client.");
    }
    private static Task<bool> PtTableAsync(MySqlConnection db, MySqlTransaction tx, string table) => db.ExecuteScalarAsync<bool>(
        "SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name=@Table)", new { Table = table }, tx);
    private string ProtectPt<T>(T value, string kind) => JsonSerializer.Serialize(new PtEnvelope(1, kind,
        PtProtector.ProtectReviewedProfessionalTax(JsonSerializer.Serialize(value, ReviewedPeriodProfessionalTax.Json))), ReviewedPeriodProfessionalTax.Json);
    private T ReadPt<T>(string json, string kind) where T : class
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<PtEnvelope>(json, ReviewedPeriodProfessionalTax.Json);
            var plaintext = envelope is { Version: 1 } && envelope.Kind == kind ? PtProtector.UnprotectReviewedProfessionalTax(envelope.Payload) : null;
            return plaintext is null ? throw new InvalidOperationException() : JsonSerializer.Deserialize<T>(plaintext, ReviewedPeriodProfessionalTax.Json) ?? throw new InvalidOperationException();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { throw new InvalidOperationException("Reviewed period Professional Tax evidence cannot be authenticated. No policy fallback is assumed.", error); }
    }
    private static string PtBasis(PayRunSourceEmployee employee, string setupJson, string month)
    {
        var setup = ReadPayrollSetup(setupJson);
        var structure = setup.Structures.SingleOrDefault(value => value.Id == employee.SalaryStructureId);
        var componentIds = structure?.Lines.Select(value => value.ComponentId).ToHashSet(StringComparer.Ordinal) ?? [];
        // Match the native resolver's effective map, not incidental JSON spacing
        // or a stale serialized map that a populated physical map supersedes.
        var salary = employee.SalaryComponents.Count > 0 ? employee.SalaryComponents : ParseSalaryComponentMap(employee.SalaryJson);
        var legacy=ReviewedPeriodProfessionalTax.Hash(new
        {
            employee.Id, employee.ClientId, employee.EmployeeCode, employee.FirstName, employee.LastName, employee.Gender, employee.WorkLocationId,
            employee.SalaryStructureId, AnnualCtc = employee.AnnualCtc.ToString("G29", CultureInfo.InvariantCulture),
            SalaryInputs = salary.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new { pair.Key, Value = pair.Value.ToString("G29", CultureInfo.InvariantCulture) }),
            employee.PersonalState, employee.WorkState, employee.PanNumber, employee.UanNumber, employee.BankAccountNo,
            employee.HasPersonalDetails, PtLwfWorkmenComp = employee.PtLwfWorkmenComp.ToString("G29", CultureInfo.InvariantCulture),
            Structure = structure,
            Components = setup.Components.Where(value => componentIds.Contains(value.Id)).OrderBy(value => value.Id, StringComparer.Ordinal),
            ProfessionalTax = new { setup.ProfessionalTax.Enabled, setup.ProfessionalTax.DefaultState, setup.ProfessionalTax.Cycle,
                Slabs = ApplicableProfessionalTaxSlabs(employee, setup, month).OrderBy(value => value.SalaryFrom).ThenBy(value => value.SalaryTo).ThenBy(value => value.DeductionAmount).ThenBy(value => value.Gender, StringComparer.Ordinal) }
        });
        return ReviewedNamedFormulaInputs.Signature(legacy,employee.NamedInputs);
    }
    private static async Task<PtSource> ReadPtSourceAsync(MySqlConnection db, MySqlTransaction tx, int clientId, string id, bool locked)
    {
        await ExcelPayslipRepository.RequireCurrentConsolidationSourceAsync(db, tx, clientId, id);
        var raw = await db.QuerySingleOrDefaultAsync<PtSourceJson>(@"SELECT CAST(b.batch_json AS CHAR) Batch,CAST(s.source_json AS CHAR) Source
FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id
WHERE b.client_id=@ClientId AND b.id=@Id" + (locked ? " FOR UPDATE" : ""), new { ClientId = clientId, Id = id }, tx)
            ?? throw new InvalidOperationException("The exact saved source batch/calculation snapshot is unavailable for this client.");
        var batch = JsonSerializer.Deserialize<ExcelPayslipBatch>(raw.Batch, ExcelPayslipRepository.JsonOptions)
            ?? throw new InvalidOperationException("The saved source batch is invalid.");
        if (batch.ClientId != clientId || batch.Id != id) throw new InvalidOperationException("The saved source ownership differs from this client and batch.");
        // Hash exact raw source text before any response-only employment annotation.
        var hash = ExcelPayslipLegacyReview.SnapshotHash(raw.Batch, raw.Source);
        await ExcelPayslipRepository.AnnotateEmploymentAsync(db, batch, tx, annotateMasterDetails: false);
        var source = JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(raw.Source, ExcelPayslipRepository.JsonOptions)
            ?? throw new InvalidOperationException("The captured statutory calculation source is invalid.");
        return new(batch, hash, source);
    }
    private static void RequirePtIdentity(PtSource source, PayRunSourceEmployee employee, string rowId, string month, bool verifiedSourceCode = false)
    {
        var rows = source.Batch.Rows.Where(row => row.Id == rowId).ToArray();
        if (source.Batch.Month != month || rows.Length != 1 || rows[0].Employment is not { IdentityReviewRequired: false, BlockedForMonth: false } employment
            || employment.EmployeeId != employee.Id || source.Batch.ClientId != employee.ClientId
            || !(verifiedSourceCode ? employment.EmployeeCode : rows[0].EmployeeCode).Trim().Equals(employee.EmployeeCode.Trim(), StringComparison.OrdinalIgnoreCase)
            || (verifiedSourceCode ? source.Batch.Rows.Count(row => row.Employment?.EmployeeId == employee.Id)
                : source.Batch.Rows.Count(row => row.EmployeeCode.Trim().Equals(employee.EmployeeCode.Trim(), StringComparison.OrdinalIgnoreCase))) != 1)
            throw new InvalidOperationException("The source row is not one uniquely verified existing employee in this exact client and payroll month.");
    }
    private static void RequirePeriodSourceZeros(PtSource source, PayRunSourceEmployee employee, string rowId,
        IReadOnlyDictionary<string, int>? columns, string setupJson, bool requirePtZero)
    {
        var setup = ReadPayrollSetup(setupJson);
        var structure = setup.Structures.SingleOrDefault(value => value.Id == employee.SalaryStructureId && value.Active)
            ?? throw new InvalidOperationException("The source-period review requires an active saved salary template.");
        var ids = structure.Lines.Select(line => line.ComponentId).ToHashSet(StringComparer.Ordinal);
        var components = setup.Components.Where(component => component.Active && ids.Contains(component.Id))
            .Select(component => new SourcePeriodStatutoryComponent(component.Code, component.Category, component.StatutoryType)).ToArray();
        var sourceRow=source.Batch.Rows.Single(row=>row.Id==rowId);
        if(requirePtZero)ReviewedSourcePeriodStatutory.RequireNoPositiveSourcePt(source.Source,sourceRow);
        ReviewedSourcePeriodStatutory.RequireCapturedZeros(source.Source, sourceRow, columns, components);
    }
    private async Task LoadReviewedProfessionalTaxAsync(MySqlConnection db, MySqlTransaction tx, List<PayRunSourceEmployee> employees, string payPeriod)
    {
        employees.ForEach(employee => employee.ReviewedProfessionalTax = null);
        if (employees.Count == 0 || !await PtTableAsync(db, tx, "modulesettings")) return;
        string? setupJson = null;
        var sources = new Dictionary<(int, string), PtSource>();
        foreach (var client in employees.GroupBy(employee => employee.ClientId))
        {
            var settings = (await db.QueryAsync<PtSetting>(@"SELECT ModuleCode Code,CAST(SettingsJson AS CHAR) Json,IsEnabled Enabled FROM modulesettings
WHERE client_id=@ClientId AND ModuleCode IN @Codes", new { ClientId = client.Key, Codes = client.Select(employee => ReviewedPeriodProfessionalTax.Key(employee.Id, payPeriod)).ToArray() }, tx)).ToDictionary(setting => setting.Code.Trim(), StringComparer.OrdinalIgnoreCase);
            foreach (var employee in client)
            {
                if (!settings.TryGetValue(ReviewedPeriodProfessionalTax.Key(employee.Id, payPeriod), out var setting)) continue;
                if (!setting.Enabled) throw new InvalidOperationException("Reviewed Professional Tax metadata was disabled outside its reviewed flow. Review the exact period control.");
                var control = ReadPt<ReviewedProfessionalTaxControl>(setting.Json, "PeriodControl");
                ReviewedPeriodProfessionalTax.RequireControl(control, employee.ClientId, employee.Id, payPeriod);
                if (control.Mode == ReviewedPeriodProfessionalTax.Automatic) continue;
                var key = (employee.ClientId, control.SourceBatchId);
                if (!sources.TryGetValue(key, out var source)) sources[key] = source = await ReadPtSourceAsync(db, tx, key.ClientId, key.SourceBatchId, false);
                setupJson ??= await PayrollDataTableStore.GetSetupJsonAsync(db, tx);
                if(control.HistoricalReceiptId is null)RequirePtIdentity(source, employee, control.SourceRowId, payPeriod, control.Version == 2);
                else await RequireHistoricalPtReceiptAsync(db,tx,control,employee,setupJson,payPeriod);
                var seeded = ReadSeededPayrollDeductions(employee).Where(row => row.Code is "PT" or "PT_LWF_WC").Sum(row => row.Amount);
                setupJson ??= await PayrollDataTableStore.GetSetupJsonAsync(db, tx);
                if (control.Version == 2) RequirePeriodSourceZeros(source, employee, control.SourceRowId, control.SourceZeroStatutoryColumns, setupJson,control.Mode==ReviewedPeriodProfessionalTax.Reviewed);
                ReviewedPeriodProfessionalTax.Resolve(control, employee.ClientId, employee.Id, payPeriod, source.Hash,
                    PtBasis(employee, setupJson, payPeriod), employee.LocationParts is not null, seeded, false, false, 0);
                employee.ReviewedProfessionalTax = control;
            }
        }
    }
    private async Task<PtPlan> BuildPtPlanAsync(MySqlConnection db, MySqlTransaction tx, ReviewedProfessionalTaxRequest request, AuthUser actor, bool locked)
    {
        ReviewedPeriodProfessionalTax.Validate(request, saving: locked);
        if (request.SourcePeriodReviewVersion == 2) ReviewedSourcePeriodStatutory.RequireClosedZeroReview(request, EmployeeEmploymentRules.Today);
        var preview = new ReviewedProfessionalTaxPreview { Request = request };
        if (!await PtTableAsync(db, tx, "modulesettings")) throw new InvalidOperationException("Existing module-settings storage is unavailable. No schema is created by this workflow.");
        var source = await ReadPtSourceAsync(db, tx, request.ClientId, request.SourceBatchId, locked);
        preview.SourceHash = source.Hash;
        if (request.SourceHash.Length > 0 && request.SourceHash != source.Hash) preview.Blockers.Add("The saved source changed. Review the current raw snapshot before saving period PT.");
        var ids = request.Rows.Select(row => row.EmployeeId).Order().ToArray();
        var employees = (await db.QueryAsync<PayRunSourceEmployee>(@"SELECT e.*,COALESCE(w.State,'') WorkState FROM employees e
LEFT JOIN worklocations w ON w.Id=e.WorkLocationId AND w.ClientId=e.ClientId
WHERE e.ClientId=@ClientId AND e.Id IN @Ids AND " + EmployeeEmploymentRules.EligibleInMonth("e", "@Month") + (locked ? " FOR UPDATE" : ""),
            new { request.ClientId, Ids = ids, Month = request.PayPeriod }, tx)).ToList();
        if (employees.Count != ids.Length) preview.Blockers.Add("One selected employee is missing, outside the client or ineligible in the source period.");
        if (locked) await LockPtNativeInputsAsync(db, tx, request.ClientId, request.PayPeriod, ids);
        await LoadEmployeeTablesAsync(db, tx, employees, request.PayPeriod, loadReviewedProfessionalTax: false);
        if (locked) await LockPtConfigurationAsync(db, tx, employees);
        var attendance = (await GetPayRunAttendanceAsync(db, tx, request.ClientId, request.PayPeriod)).ToDictionary(row => row.EmployeeId);
        var setupJson = await PayrollDataTableStore.GetSetupJsonAsync(db, tx); var setup = ReadPayrollSetup(setupJson);
        await LoadPfPolicyPeriodsAsync(db, tx, employees.Where(employee => request.Rows.Any(row => row.EmployeeId == employee.Id && row.Mode == ReviewedPeriodProfessionalTax.Reviewed)).ToList(), request.PayPeriod, setupJson);
        var records = (await db.QueryAsync<PtSetting>(@"SELECT ModuleCode Code,CAST(SettingsJson AS CHAR) Json,IsEnabled Enabled FROM modulesettings
WHERE client_id=@ClientId AND ModuleCode IN @Codes" + (locked ? " FOR UPDATE" : ""),
            new { request.ClientId, Codes = ids.Select(id => ReviewedPeriodProfessionalTax.Key(id, request.PayPeriod)).ToArray() }, tx)).ToDictionary(setting => setting.Code.Trim(), StringComparer.OrdinalIgnoreCase);
        var previous = new Dictionary<int, ReviewedProfessionalTaxControl?>(); var hashes = new Dictionary<int, string>();
        if (!await PtTableAsync(db, tx, "payruns") || !await PtTableAsync(db, tx, "payrunemployees")) preview.Blockers.Add("Core payroll history is unavailable for a complete period conflict check.");
        else
        {
            if (await db.ExecuteScalarAsync<bool>(@"SELECT EXISTS(SELECT 1 FROM payruns WHERE ClientId=@ClientId AND PayPeriod=@PayPeriod
AND RunType='Regular' AND Status IN ('AwaitingProcessing','Queued','Processing'))", new { request.ClientId, request.PayPeriod }, tx))
                preview.Blockers.Add("A regular run is held, queued or processing for this client/month. Existing payroll is retained; do not change its period PT inputs.");
            if (await db.ExecuteScalarAsync<bool>(@"SELECT EXISTS(SELECT 1 FROM payrunemployees p JOIN payruns r ON r.Id=p.PayRunId
WHERE r.ClientId=@ClientId AND r.PayPeriod=@PayPeriod AND r.RunType='Regular' AND r.Status IN ('Draft','Pending Approval','Approved','Partially Paid','Paid')
AND p.IsSkipped=FALSE AND p.EmployeeId IN @Ids)", new { request.ClientId, request.PayPeriod, Ids = ids }, tx))
                preview.Blockers.Add("A selected employee already has a saved regular payroll result. Period PT cannot change historical payroll.");
        }
        foreach (var row in request.Rows)
        {
            var employee = employees.SingleOrDefault(employee => employee.Id == row.EmployeeId); if (employee is null) continue;
            var blockers = new List<string>();
            try {
                RequirePtIdentity(source, employee, row.SourceRowId, request.PayPeriod, request.SourcePeriodReviewVersion == 2);
                if (request.SourcePeriodReviewVersion == 2) RequirePeriodSourceZeros(source, employee, row.SourceRowId, row.SourceZeroStatutoryColumns, setupJson,row.Mode==ReviewedPeriodProfessionalTax.Reviewed);
            }
            catch (InvalidOperationException error) { blockers.Add(error.Message); }
            var key = ReviewedPeriodProfessionalTax.Key(employee.Id, request.PayPeriod);
            var old = records.TryGetValue(key, out var existing) ? ReadPt<ReviewedProfessionalTaxControl>(existing.Json, "PeriodControl") : null;
            if (old is not null) ReviewedPeriodProfessionalTax.RequireControl(old, request.ClientId, employee.Id, request.PayPeriod);
            previous[employee.Id] = old; hashes[employee.Id] = existing is null ? "" : ReviewedPeriodProfessionalTax.Hash(new { existing.Json, existing.Enabled });
            if (row.Mode == ReviewedPeriodProfessionalTax.Reviewed)
            {
                if (employee.LocationParts is not null) blockers.Add("Effective location allocations require their own statutory source-part review; this control cannot replace them.");
                if (ReadSeededPayrollDeductions(employee).Any(value => value.Code is "PT" or "PT_LWF_WC")) blockers.Add("A positive seeded personal PT input exists. Resolve the conflict explicitly before period review.");
                if (!attendance.TryGetValue(employee.Id, out var days) || days.WorkingDays is < 1 or > 31 || days.PolicyError.Length > 0) blockers.Add("Valid native monthly attendance is required to review existing configured PT amounts.");
                else try
                {
                    var salary = CalculateConfiguredSalary(employee, setup, (int)days.WorkingDays, days.PresentDays, days.PayableDays);
                    var pt = salary.Where(value => IsDeductionCategory(value.Component.Category) && ReviewedPeriodProfessionalTax.IsPt(value.Component.Code, value.Component.StatutoryType)).ToArray();
                    if (pt.Length > 1 || pt.Any(value => (value.EarnedOverride ?? value.Monthly) > 0)
                        || salary.Any(value => value.Component.Code.Equals("PT_LWF_WC", StringComparison.OrdinalIgnoreCase)))
                        blockers.Add("Positive, duplicate or mixed configured PT cannot be silently replaced by this period control.");
                    // Exercise the same reviewed-input branch as native payroll.
                    // A stored/inferred zero must not mask a positive template rule.
                    employee.ReviewedProfessionalTax = new()
                    {
                        Version = request.SourcePeriodReviewVersion == 2 ? 2 : 1,
                        SourceZeroStatutoryColumns = row.SourceZeroStatutoryColumns,
                        ClientId = request.ClientId, EmployeeId = employee.Id, PayPeriod = request.PayPeriod,
                        Mode = row.Mode, Amount = row.Amount, SourceBatchId = request.SourceBatchId,
                        SourceRowId = row.SourceRowId, SourceHash = source.Hash, NativeBasisHash = PtBasis(employee, setupJson, request.PayPeriod),
                        Reason = request.Reason.Trim(), ReviewedById = actor.Id, ReviewedBy = actor.Email,
                        ReviewedAtUtc = DateTime.UtcNow, RequestId = Guid.NewGuid().ToString("N")
                    };
                    _ = CalculateConfiguredSalary(employee, setup, (int)days.WorkingDays, days.PresentDays, days.PayableDays);
                }
                catch (InvalidOperationException error) { blockers.Add(error.Message); }
                finally { employee.ReviewedProfessionalTax = null; }
            }
            preview.Rows.Add(new(employee.Id, employee.EmployeeCode, ($"{employee.FirstName} {employee.LastName}").Trim(), row.SourceRowId,
                old?.Mode ?? ReviewedPeriodProfessionalTax.Automatic, old?.Amount, row.Mode, row.Amount, PtBasis(employee, setupJson, request.PayPeriod), blockers));
        }
        // Incoming sourceHash may be blank for the first inspection; the returned
        // normalized request is the exact source-bound payload required by Save.
        request.SourceHash = source.Hash;
        preview.PlanHash = ReviewedPeriodProfessionalTax.Hash(new { Actor = actor.Id, Request = request, SourceHash = source.Hash,
            preview.Rows, preview.Blockers, Records = hashes.OrderBy(pair => pair.Key),
            Attendance = attendance.Where(pair => ids.Contains(pair.Key)).OrderBy(pair => pair.Key) });
        return new(preview, employees, previous, hashes);
    }
    public async Task<ReviewedProfessionalTaxPreview> PreviewReviewedProfessionalTaxAsync(ReviewedProfessionalTaxRequest request, AuthUser actor)
    {
        RequirePtActor(actor, request.ClientId);
        await using var db = CreateConnection(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, isReadOnly: true);
        var plan = await BuildPtPlanAsync(db, tx, request, actor, false); await tx.RollbackAsync(); return plan.Preview;
    }
    public async Task<ReviewedProfessionalTaxSaveResult> SaveReviewedProfessionalTaxAsync(SaveReviewedProfessionalTaxRequest command, AuthUser actor, bool sourcePeriod = false)
    {
        if (sourcePeriod) ReviewedSourcePeriodStatutory.RequireClosedZeroReview(command.Request, EmployeeEmploymentRules.Today);
        else if (!SelectedRunRuntime.Read(configuration).Safe) throw new InvalidOperationException("Reviewed period PT saving requires migrations and workers disabled and outbound delivery suppressed. No control was saved.");
        RequirePtActor(actor, command.Request.ClientId); ReviewedPeriodProfessionalTax.Validate(command.Request, saving: true);
        if (!command.Acknowledged || !ReviewedPeriodProfessionalTax.ValidHash(command.ExpectedPlanHash) || !Guid.TryParse(command.RequestId, out var parsed))
            throw new InvalidOperationException("Preview and explicitly acknowledge the exact source-bound period PT changes before saving.");
        var requestId = parsed.ToString("N"); var receiptKey = ReviewedPeriodProfessionalTax.ReceiptPrefix + requestId;
        var fingerprint = ReviewedPeriodProfessionalTax.Hash(new { Actor = actor.Id, command.Request, command.ExpectedPlanHash });
        await using var db = CreateConnection(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        if (await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@ClientId AND IsActive=TRUE FOR UPDATE", new { command.Request.ClientId }, tx) is null)
            throw new InvalidOperationException("Select an active client. No period control was saved.");
        if (!await PtTableAsync(db, tx, "modulesettings")) throw new InvalidOperationException("Existing module-settings storage is unavailable; no DDL is performed.");
        var saved = await db.ExecuteScalarAsync<string?>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Code FOR UPDATE", new { command.Request.ClientId, Code = receiptKey }, tx);
        if (saved is not null)
        {
            var receipt = ReadPt<PtReceipt>(saved, "Receipt");
            if (receipt.ActorId != actor.Id || receipt.Fingerprint != fingerprint) throw new InvalidOperationException("This request reference belongs to a different reviewed period PT change.");
            await tx.RollbackAsync(); return receipt.Result with { Replayed = true };
        }
        var plan = await BuildPtPlanAsync(db, tx, command.Request, actor, true);
        if (!plan.Preview.CanSave || command.ExpectedPlanHash != plan.Preview.PlanHash)
            throw new InvalidOperationException("Source, employee, attendance, existing PT input or payroll state changed. Refresh the preview; no period controls were saved.");
        var controls = plan.Preview.Rows.Select(row => new ReviewedProfessionalTaxControl
        {
            Version = command.Request.SourcePeriodReviewVersion == 2 ? 2 : 1,
            SourceZeroStatutoryColumns = command.Request.Rows.Single(input => input.EmployeeId == row.EmployeeId).SourceZeroStatutoryColumns is { } zeros
                ? new Dictionary<string, int>(zeros, StringComparer.Ordinal) : null,
            ClientId = command.Request.ClientId, EmployeeId = row.EmployeeId, PayPeriod = command.Request.PayPeriod, Mode = row.Mode, Amount = row.Amount,
            SourceBatchId = command.Request.SourceBatchId, SourceRowId = row.SourceRowId, SourceHash = plan.Preview.SourceHash, NativeBasisHash = row.NativeBasisHash,
            Reason = command.Request.Reason.Trim(), ReviewedById = actor.Id, ReviewedBy = actor.Email, ReviewedAtUtc = DateTime.UtcNow, RequestId = requestId
        }).ToList();
        foreach (var control in controls)
        {
            ReviewedPeriodProfessionalTax.RequireControl(control, control.ClientId, control.EmployeeId, control.PayPeriod);
            await db.ExecuteAsync(@"INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@ClientId,@Code,TRUE,CAST(@Json AS JSON))
ON DUPLICATE KEY UPDATE IsEnabled=TRUE,SettingsJson=VALUES(SettingsJson)", new { control.ClientId, Code = ReviewedPeriodProfessionalTax.Key(control.EmployeeId, control.PayPeriod), Json = ProtectPt(control, "PeriodControl") }, tx);
        }
        var result = new ReviewedProfessionalTaxSaveResult(requestId, controls.Count, controls, false);
        // Permanent protected receipt retains prior control evidence, including
        // deliberate return to Automatic; no old receipt is overwritten.
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@ClientId,@Code,TRUE,CAST(@Json AS JSON))",
            new { command.Request.ClientId, Code = receiptKey, Json = ProtectPt(new PtReceipt(actor.Id, fingerprint, result, controls.Select(control => plan.Previous[control.EmployeeId]).ToArray()), "Receipt") }, tx);
        await tx.CommitAsync(); return result;
    }
}
