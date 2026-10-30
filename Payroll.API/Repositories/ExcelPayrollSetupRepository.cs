using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository(IConfiguration configuration, EmployeeRepository employees)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));
    internal const string Schema = @"CREATE TABLE IF NOT EXISTS excel_payroll_setup_imports (
id CHAR(32) PRIMARY KEY,client_id INT NOT NULL,batch_id CHAR(32) NOT NULL,request_id VARCHAR(64) NOT NULL,
selection_hash CHAR(64) NOT NULL,request_hash CHAR(64) NOT NULL,plan_hash VARCHAR(64) NOT NULL DEFAULT '',
status VARCHAR(20) NOT NULL DEFAULT 'Draft',request_json JSON NOT NULL,result_json JSON NOT NULL,created_by VARCHAR(190) NOT NULL,
created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
UNIQUE KEY ux_excel_payroll_setup_request(client_id,request_id),
UNIQUE KEY ux_excel_payroll_setup_selection(client_id,batch_id,selection_hash));";

    // Explicit migration command only. Preview and apply never run DDL or seed routines.
    public async Task MigrateAsync()
    {
        await using var db = Db(); await db.OpenAsync(); await db.ExecuteAsync(Schema);
    }

    private static Task<bool> HasReceiptTableAsync(MySqlConnection db, MySqlTransaction? tx = null) =>
        db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='excel_payroll_setup_imports')", transaction: tx);

    public async Task<object> ContextAsync(int clientId, string batchId, IReadOnlyList<string>? rowIds = null)
    {
        await using var db = Db(); await db.OpenAsync();
        var (batch, source) = await ReadSourceAsync(db, null, clientId, batchId);
        var componentRows = await db.QueryAsync(@"SELECT CAST(Id AS CHAR) id,Code code,Name name,Category category,ComponentRole componentRole,StatutoryType statutoryType,
CalculationType calculationType,PayType payType,ValueText value,Formula formula,BaseComponent baseComponent,ProRata proRata,Taxable taxable,Active active,Priority priority FROM salarycomponents ORDER BY Priority,Code");
        var workWeeks = await db.QueryAsync("SELECT DISTINCT Value value,Value label FROM dropdownmasters WHERE Type='Work Week' AND IsActive=TRUE AND ClientId IN (0,@ClientId) ORDER BY Value", new { ClientId = clientId });
        var workLocations = await db.QueryAsync("SELECT Id id,Name name FROM worklocations WHERE ClientId=@ClientId AND IsActive=TRUE ORDER BY Name", new { ClientId = clientId });
        var employeeRows = await db.QueryAsync(@"SELECT e.Id id,e.EmployeeCode employeeCode,TRIM(CONCAT(e.FirstName,' ',e.LastName)) employeeName,
e.WorkLocationId workLocationId,COALESCE(w.Name,'') workLocation,e.Designation designation FROM employees e LEFT JOIN worklocations w ON w.Id=e.WorkLocationId
WHERE e.ClientId=@ClientId AND " + EmployeeEmploymentRules.EligibleInMonth("e", "@Month") + " ORDER BY e.FirstName,e.LastName", new { ClientId = clientId, Month = batch.Month });
        var ready = await HasReceiptTableAsync(db);
        var selectedHash = rowIds is { Count: > 0 } ? SelectionHash(rowIds) : "";
        // Sort only small keys: MySQL filesort can exhaust its buffer on a large captured receipt JSON.
        var savedId = ready ? await db.ExecuteScalarAsync<string?>(@"SELECT id FROM excel_payroll_setup_imports
WHERE client_id=@ClientId AND batch_id=@BatchId AND (@SelectionHash='' OR selection_hash=@SelectionHash
OR (JSON_UNQUOTE(JSON_EXTRACT(request_json,'$.correctionOfImportId')) IS NOT NULL
AND JSON_LENGTH(JSON_EXTRACT(request_json,'$.rowIds'))=@RowCount AND JSON_CONTAINS(JSON_EXTRACT(request_json,'$.rowIds'),CAST(@Rows AS JSON))))
ORDER BY updated_at DESC,id DESC LIMIT 1",new { ClientId=clientId,BatchId=batchId,SelectionHash=selectedHash,RowCount=rowIds?.Count??0,Rows=JsonSerializer.Serialize(rowIds??[],Json) }) : null;
        var saved = savedId is not null ? await db.QueryFirstOrDefaultAsync<SavedSetup>(@"SELECT id ImportId,request_id RequestId,status Status,CAST(request_json AS CHAR) RequestJson,CAST(result_json AS CHAR) ResultJson
FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND batch_id=@BatchId AND id=@ImportId",new { ClientId=clientId,BatchId=batchId,ImportId=savedId }) : null;
        var savedMetadata = saved is null ? null : JsonNode.Parse(saved.ResultJson)?.AsObject();
        var lastImport = saved is null ? null : new { saved.ImportId, saved.RequestId, saved.Status, savedRequest = JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(saved.RequestJson, Json), result = JsonSerializer.Deserialize<ExcelPayrollSetupResult>(saved.ResultJson, Json), editableDefaultsDraft = savedMetadata?["editableDefaultsDraft"]?.DeepClone(), defaultsRevision = savedMetadata?["editableDefaultsRevision"]?.GetValue<int>() ?? 0 };
        var defaults = await ReadClientDefaultsAsync(db, clientId, batch.Month);
        var allocationSettings = JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db))!.AsObject();
        var salaryTemplates = (allocationSettings["salaryStructures"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(template => RefId(Text(template,"clientId")) == clientId && Text(template,"active") != "false").ToList();
        var reviewedIdentityColumns = await AppliedIdentityColumnsAsync(db,batch,source);
        var roster = ClassifyRoster(batch,source,await ReadIdentitiesAsync(db,null,clientId,batch.Month),await AppliedSourceRowsAsync(db,null,clientId,batchId),reviewedIdentityColumns);
        roster = await ReviewRosterIdentitiesAsync(db,clientId,batch,source,roster);
        var appliedIds = ready ? (await db.QueryAsync<string>("SELECT id FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND batch_id=@BatchId AND status='Applied' ORDER BY updated_at DESC,id DESC",new { ClientId=clientId,BatchId=batchId })).ToArray() : [];
        var appliedJson = appliedIds.Length>0 ? await db.QueryAsync<AppliedSetupRowSet>("SELECT id ImportId,CAST(JSON_EXTRACT(request_json,'$.rowIds') AS CHAR) RowsJson,CAST(result_json AS CHAR) ResultJson FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND batch_id=@BatchId AND status='Applied' AND id IN @Ids",new { ClientId=clientId,BatchId=batchId,Ids=appliedIds }) : [];
        var appliedOrder=appliedIds.Select((id,index)=>new {id,index}).ToDictionary(value=>value.id,value=>value.index,StringComparer.Ordinal);
        var appliedSetups=appliedJson.OrderBy(value=>appliedOrder[value.ImportId]).Select(value=>AppliedReceiptRows(value.RowsJson,value.ResultJson).ToList()).ToList();
        return new { batchId = batch.Id, month = batch.Month, source, components = componentRows, workWeeks, workLocations, employees = employeeRows, defaults, migrationRequired = !ready, salaryCorrectionSupported = true, sourcePayableOverrideSupported = true, sharedHeaderDivisorSupported = true, templateApplicabilitySupported = true, calculationOnlySimulationSupported = true, identityColumnReviewSupported = true, identityColumnCandidates = IdentityColumnCandidates(batch,source,rowIds is { Count: > 0 } ? rowIds : batch.Rows.Select(r=>r.Id).ToArray()), payableOnlyAttendanceSupported = PayableOnlyRolloutAvailable(configuration, await HasNullablePresenceSchemaAsync(db, null)), locationAllocationSupported = true, locationAllocationImportDate = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), salaryTemplates, lastImport, roster, appliedSetups };
    }

    public async Task<object> SaveDraftAsync(int clientId, ExcelPayrollSetupDraftRequest draft, string actor)
    {
        ValidateRequest(draft.Request);
        if (!Guid.TryParse(draft.RequestId, out _)) throw new InvalidOperationException("Provide a stable draft request ID.");
        var json = JsonSerializer.Serialize(draft.Request, Json);
        // Historical result reads/replays remain available; a fresh saved draft uses the working source.
        if (Encoding.UTF8.GetByteCount(json) > 1_000_000) throw new InvalidOperationException("The reviewed setup draft is too large.");
        await using var db = Db(); await db.OpenAsync();
        if (!await HasReceiptTableAsync(db)) throw new InvalidOperationException("Complete the explicit Excel payroll setup migration before saving a setup draft.");
        await using var tx = await db.BeginTransactionAsync();
        if (await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@ClientId AND IsActive=TRUE FOR UPDATE", new { ClientId = clientId }, tx) is null) throw new InvalidOperationException("Select an active client.");
        await ExcelPayslipRepository.RequireCurrentConsolidationSourceAsync(db,tx,clientId,draft.Request.BatchId);
        var (batch, _) = await ReadSourceAsync(db, tx, clientId, draft.Request.BatchId);
        if (draft.Request.RowIds.Any(id => !batch.Rows.Any(r => r.Id == id))) throw new InvalidOperationException("The draft includes rows outside the selected client batch.");
        var selectionHash = SetupSelectionHash(draft.Request);
        var existing = await db.QueryFirstOrDefaultAsync<SavedSetup>(@"SELECT id ImportId,request_id RequestId,status Status,CAST(request_json AS CHAR) RequestJson,CAST(result_json AS CHAR) ResultJson
FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND (request_id=@RequestId OR (batch_id=@BatchId AND selection_hash=@SelectionHash)) LIMIT 1 FOR UPDATE",
            new { ClientId = clientId, draft.RequestId, draft.Request.BatchId, SelectionHash = selectionHash }, tx);
        if (existing is not null && (existing.Status is not ("Draft" or "Kept") || existing.RequestId != draft.RequestId)) throw new InvalidOperationException("This selection already has a saved setup. Open its existing draft or result before saving changes.");
        var id = existing?.ImportId ?? Guid.NewGuid().ToString("N");
        var draftMetadata = existing?.ResultJson ?? "{}";
        if (draft.Request.TransferReviewVersion == 1) draftMetadata = TransferDraftMetadata(draftMetadata, (await BuildPlanAsync(db, tx, clientId, draft.Request)).Preview);
        if (existing is null)
            await db.ExecuteAsync(@"INSERT INTO excel_payroll_setup_imports(id,client_id,batch_id,request_id,selection_hash,request_hash,plan_hash,status,request_json,result_json,created_by)
VALUES(@Id,@ClientId,@BatchId,@RequestId,@SelectionHash,@Hash,'','Draft',CAST(@Json AS JSON),CAST(@DraftMetadata AS JSON),@Actor)",
                new { Id = id, ClientId = clientId, draft.Request.BatchId, draft.RequestId, SelectionHash = selectionHash, Hash = Hash(json), Json = json, DraftMetadata = draftMetadata, Actor = actor.Length > 190 ? actor[..190] : actor }, tx);
        else await db.ExecuteAsync(@"UPDATE excel_payroll_setup_imports SET status='Draft',batch_id=@BatchId,selection_hash=@SelectionHash,request_hash=@Hash,request_json=CAST(@Json AS JSON),result_json=CAST(@DraftMetadata AS JSON),plan_hash='' WHERE id=@Id AND status IN ('Draft','Kept')",
            new { Id = id, draft.Request.BatchId, SelectionHash = selectionHash, Hash = Hash(json), Json = json, DraftMetadata = draftMetadata }, tx);
        await tx.CommitAsync();
        return new { importId = id, draft.RequestId, status = "Draft", savedRequest = draft.Request };
    }

    public async Task<ExcelPayrollSetupPreview> PreviewAsync(int clientId, ExcelPayrollSetupRequest request)
    {
        ValidateRequest(request);
        await using var db = Db(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, isReadOnly: true);
        var plan = await BuildPlanAsync(db, tx, clientId, request);
        await tx.RollbackAsync(); return plan.Preview;
    }

    public async Task<ExcelPayrollSetupResult> ApplyAsync(int clientId, ExcelPayrollSetupApplyRequest apply, string actor)
    {
        ValidateRequest(apply.Request);
        if (apply.Request.LocationAllocations is not null) return await ApplyLocationAllocationsAsync(clientId, apply, actor);
        if (!Regex.IsMatch(apply.PlanHash ?? "", "^[a-fA-F0-9]{64}$") || !Guid.TryParse(apply.RequestId, out _))
            throw new InvalidOperationException("Preview the reviewed setup first and provide its plan hash and request ID.");
        await using var db = Db(); await db.OpenAsync();
        if (!await HasReceiptTableAsync(db)) throw new InvalidOperationException("The explicit Excel payroll setup migration must be completed before applying a reviewed setup.");
        await using var tx = await db.BeginTransactionAsync();
        if (await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@ClientId AND IsActive=TRUE FOR UPDATE", new { ClientId = clientId }, tx) is null)
            throw new InvalidOperationException("Select an active client.");
        var requestHash = Hash(JsonSerializer.Serialize(apply.Request, Json));
        var selectionHash = SetupSelectionHash(apply.Request);
        var receipt = await db.QueryFirstOrDefaultAsync<Receipt>(@"SELECT id Id,status Status,request_id RequestId,request_hash RequestHash,CAST(request_json AS CHAR) RequestJson,CAST(result_json AS CHAR) ResultJson
FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND (request_id=@RequestId OR (batch_id=@BatchId AND selection_hash=@SelectionHash)) LIMIT 1 FOR UPDATE",
            new { ClientId = clientId, apply.RequestId, apply.Request.BatchId, SelectionHash = selectionHash }, tx);
        if (receipt is { Status: "Kept" } && SameReviewedRequest(receipt.RequestJson,apply.Request))
        { var keptResult=JsonSerializer.Deserialize<ExcelPayrollSetupResult>(receipt.ResultJson,Json)!; keptResult.Replayed=true; await tx.CommitAsync(); return keptResult; }
        if (receipt is { Status: "Applied" })
        {
            if (receipt.RequestHash != requestHash && !SameReviewedRequest(receipt.RequestJson,apply.Request)) throw new InvalidOperationException("This import request or selected source rows were already applied with different settings. Open the existing result; use a new source batch for a revised import.");
            var previous = JsonSerializer.Deserialize<ExcelPayrollSetupResult>(receipt.ResultJson, Json)!; previous.Replayed = true;
            await tx.CommitAsync(); return previous;
        }
        if (receipt is not null && receipt.RequestId != apply.RequestId) throw new InvalidOperationException("Open the existing setup draft for this source selection before applying it.");
        // Current reads take all target locks before the first consistent source read establishes
        // the final plan snapshot. This also covers employee actions that do not take the client lock.
        await LockApplyInputsAsync(db, tx, clientId, apply.Request);
        var plan = await BuildPlanAsync(db, tx, clientId, apply.Request);
        if (!plan.Preview.CanApply) throw new InvalidOperationException(string.Join(" ", plan.Preview.Issues.Where(i => i.Severity == "Error").Take(5).Select(i => i.Message)));
        RequireMatchingSetupPlanHash(plan.Preview.PlanHash, apply.PlanHash!);
        var result = new ExcelPayrollSetupResult { ImportId = receipt?.Id ?? Guid.NewGuid().ToString("N"), TemplateId = plan.Preview.TemplateId, TemplateIds = plan.Preview.TemplateIds.ToList(), Transfer = plan.Preview.Transfer, CorrectionOfImportId=apply.Request.CorrectionOfImportId };
        SeparateKeptTransferRows(plan,result);
        if (plan.Rows.Count > 0) {
        await InsertMastersAsync(db, tx, clientId, apply.Request, plan);
        await InsertReviewedDropdownsAsync(db, tx, clientId, apply.Request, plan);
        foreach (var name in plan.Rows.Where(r => r.NewWorkLocationName.Length > 0).Select(r => r.NewWorkLocationName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var locationId = (int)await db.ExecuteScalarAsync<long>(@"INSERT INTO worklocations(ClientId,ClientName,Name,Address,City,State,PostalCode,GSTIN,IsPrimary,IsActive)
SELECT Id,Name,@LocationName,'','',@State,'','',FALSE,TRUE FROM clients WHERE Id=@ClientId; SELECT LAST_INSERT_ID();", new { ClientId = clientId, LocationName = name, State = plan.Rows.First(row => row.NewWorkLocationName.Equals(name, StringComparison.OrdinalIgnoreCase)).NewWorkLocationState }, tx);
            foreach (var row in plan.Rows.Where(r => r.NewWorkLocationName.Equals(name, StringComparison.OrdinalIgnoreCase))) row.Employee.WorkLocationId = locationId;
        }
        foreach (var row in plan.Rows)
        {
            IReadOnlyList<ExcelPayrollSetupIssue> lockedIssues;
            if (plan.ExistingSalaryOnlyIdentityIssues is { } reviewed)
                lockedIssues = reviewed[row.RowId];
            else
            {
                var check = (await employees.PreviewRecruitmentAsync(row.Employee, db, tx)).Rows.Single();
                lockedIssues = FullIdentityIssues([(row.RowId,row.Employee.Id)],[check],plan.SalaryOnlyBankReviewRows);
            }
            if(lockedIssues.Any(issue=>issue.Severity=="Error"))
                throw new InvalidOperationException("Employee identity changed after review: "+string.Join(" ",lockedIssues.Where(issue=>issue.Severity=="Error").Select(issue=>issue.Message)));
            result.DataQualityReviews.AddRange(lockedIssues.Where(issue=>issue.Code==BankDataReviewCode));
            var isNew = row.Employee.Id == 0;
            var id = await EmployeeRepository.SaveExcelPayrollSetupEmployeeAsync(db, tx, row.Employee, plan.EffectiveFrom, actor,
                $"Reviewed salary Excel {apply.Request.BatchId}, row {row.SourceRow}, import {result.ImportId}",
                apply.Request.CorrectionOfImportId is null ? null
                    : $"Reviewed salary Excel {apply.Request.BatchId}, row {row.SourceRow}, import {apply.Request.CorrectionOfImportId}");
            row.Employee.Id = id; result.EmployeeIds.Add(id);
            if (apply.Request.TransferReviewVersion == 1) result.TransferRows.Add(new(row.RowId,id,row.Preview.PayrollDays,apply.Request.ImportPayableOnlyAttendance?null:row.Preview.PresentDays,row.Preview.PayableDays,plan.Batch.Rows.Single(r => r.Id == row.RowId).NetPay,row.Preview.Lines.ToList()));
            if (isNew) result.CreatedCount++; else result.UpdatedCount++;
        }
        if (apply.Request.ImportAttendance) result.AttendanceGroupId = await InsertAttendanceAsync(db, tx, clientId, apply.Request, plan, result.ImportId, result);
        if (apply.Request.OverrideSourcePayableAttendance) await WritePayableOverrideAsync(db, tx, clientId, apply.Request, plan, result);
        await InsertAdjustmentsAsync(db, tx, clientId, apply.Request, plan, result.ImportId, result);
        }
        var receiptStatus = result.EmployeeIds.Count == 0 && result.KeptRows.Count > 0 ? "Kept" : "Applied";
        if (result.Transfer is not null) result.Transfer.PreparationStatus = receiptStatus == "Kept" ? "Keep decisions recorded — existing assignments unchanged; Excel parity unresolved" : "Configuration applied — kept rows unchanged; regular payroll parity not verified";
        var receiptValues = new { Id = result.ImportId, ClientId = clientId, apply.Request.BatchId, apply.RequestId, SelectionHash = selectionHash, RequestHash = requestHash, apply.PlanHash,
            Status = receiptStatus, RequestJson = JsonSerializer.Serialize(apply.Request, Json), ResultJson = JsonSerializer.Serialize(result, Json), Actor = actor.Length > 190 ? actor[..190] : actor };
        if (receipt is null) await db.ExecuteAsync(@"INSERT INTO excel_payroll_setup_imports(id,client_id,batch_id,request_id,selection_hash,request_hash,plan_hash,status,request_json,result_json,created_by)
VALUES(@Id,@ClientId,@BatchId,@RequestId,@SelectionHash,@RequestHash,@PlanHash,@Status,CAST(@RequestJson AS JSON),CAST(@ResultJson AS JSON),@Actor)", receiptValues, tx);
        else await db.ExecuteAsync(@"UPDATE excel_payroll_setup_imports SET status=@Status,batch_id=@BatchId,selection_hash=@SelectionHash,request_hash=@RequestHash,plan_hash=@PlanHash,request_json=CAST(@RequestJson AS JSON),result_json=CAST(@ResultJson AS JSON) WHERE id=@Id AND status IN ('Draft','Kept')", receiptValues, tx);
        await tx.CommitAsync(); return result;
    }

    private static async Task<(ExcelPayslipBatch Batch, ExcelPayslipCalculationSource Source)> ReadSourceAsync(MySqlConnection db, MySqlTransaction? tx, int clientId, string batchId)
    {
        var raw = await db.ExecuteScalarAsync<string?>(@"SELECT CAST(b.batch_json AS CHAR) FROM excel_payslip_batches b JOIN clients c ON c.Id=b.client_id AND c.IsActive=TRUE WHERE b.client_id=@ClientId AND b.id=@BatchId", new { ClientId = clientId, BatchId = batchId }, tx);
        var source = await ExcelPayslipRepository.ReadCalculationSourceAsync(db, clientId, batchId, tx);
        if (raw is null || source is null) throw new InvalidOperationException("Select a saved salary batch with its authoritative calculation source for this client.");
        var batch=JsonSerializer.Deserialize<ExcelPayslipBatch>(raw, Json) ?? throw new InvalidOperationException("The saved source batch is unavailable.");
        if(batch.Id!=batchId || batch.ClientId!=clientId)throw new InvalidOperationException("The saved batch identity does not match this client/version. Reopen the intended Excel Payslip batch.");
        return (batch, source);
    }

    private static async Task LockApplyInputsAsync(MySqlConnection db, MySqlTransaction tx, int clientId, ExcelPayrollSetupRequest request)
    {
        var lockedEmployeeIds=(await db.QueryAsync<int>("SELECT Id FROM employees WHERE ClientId=@ClientId ORDER BY Id FOR UPDATE",new { ClientId=clientId },tx)).ToArray();
        await db.QueryAsync<long>("SELECT Id FROM salarycomponents WHERE Code IN @Codes ORDER BY Id FOR UPDATE",new { Codes=request.Components.Select(c=>c.Code.ToUpperInvariant()).ToArray() },tx);
        await db.QueryAsync<long>("SELECT Id FROM salarystructures WHERE ClientId=@ClientId ORDER BY Id FOR UPDATE",new { ClientId=clientId },tx);
        await db.QueryAsync<int>("SELECT Id FROM modulesettings WHERE client_id=@ClientId ORDER BY Id FOR UPDATE",new { ClientId=clientId },tx);
        var month=await db.ExecuteScalarAsync<string?>("SELECT JSON_UNQUOTE(JSON_EXTRACT(batch_json,'$.month')) FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@BatchId FOR UPDATE",new { ClientId=clientId,request.BatchId },tx)
            ?? throw new InvalidOperationException("The selected client batch no longer exists.");
        await db.QueryAsync<string>("SELECT a.id FROM excel_payslip_batch_originals l JOIN excel_payslip_originals o ON o.client_id=l.client_id AND o.id=l.original_id JOIN entity_attachments a ON a.id=o.attachment_id WHERE l.client_id=@ClientId AND l.batch_id=@BatchId ORDER BY l.original_id FOR UPDATE",new {ClientId=clientId,request.BatchId},tx);
        await db.QueryAsync<long>("SELECT id FROM employee_monthly_attendance WHERE client_id=@ClientId AND attendance_month=@Month ORDER BY id FOR UPDATE",new { ClientId=clientId,Month=month },tx);
        await db.QueryAsync<int>("SELECT Id FROM payrolladjustments WHERE ClientId=@ClientId AND PayPeriod=@Month ORDER BY Id FOR UPDATE",new { ClientId=clientId,Month=month },tx);
        if(request.CorrectionOfImportId is not null)await db.QueryAsync<string>("SELECT id FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND id=@ImportId FOR UPDATE",new{ClientId=clientId,ImportId=request.CorrectionOfImportId},tx);
        if(request.AttendanceReview is {Mode:"VerifyExistingNative",Reviewed:true} review && lockedEmployeeIds.Length>0) {
            await db.QueryAsync<int>("SELECT employee_id FROM employee_daily_attendance WHERE client_id=@ClientId AND attendance_date BETWEEN @Start AND @End ORDER BY employee_id,attendance_date FOR UPDATE",new {ClientId=clientId,Start=review.CycleStart,End=review.CycleEnd},tx);
            await db.QueryAsync<int>("SELECT employee_id FROM attendance_group_employees WHERE employee_id IN @Ids ORDER BY employee_id,attendance_group_id FOR UPDATE",new {Ids=lockedEmployeeIds},tx);
            await db.QueryAsync<int>("SELECT g.id FROM attendance_groups g JOIN attendance_group_employees m ON m.attendance_group_id=g.id WHERE g.client_id=@ClientId AND m.employee_id IN @Ids ORDER BY g.id FOR UPDATE",new {ClientId=clientId,Ids=lockedEmployeeIds},tx);
        }
    }

    private async Task<Plan> BuildPlanAsync(MySqlConnection db, MySqlTransaction tx, int clientId, ExcelPayrollSetupRequest request)
    {
        if (request.LocationAllocations is not null) return (await BuildLocationAllocationPlanAsync(db, tx, clientId, request)).Evidence;
        await ExcelPayslipRepository.RequireCurrentConsolidationSourceAsync(db,tx,clientId,request.BatchId);
        var (batch, source) = await ReadSourceAsync(db, tx, clientId, request.BatchId);
        var reviewedIdentityColumn = ReviewedIdentityColumn(batch,source,request);
        var reviewedIdentityColumns = reviewedIdentityColumn is int selectedColumn
            ? request.RowIds.ToDictionary(id=>id,_=>selectedColumn,StringComparer.Ordinal) : null;
        var plan = new Plan { Batch = batch, Source = source, EffectiveFrom = DateTime.ParseExact(request.EffectiveFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture) };
        if(source.CaptureVersion==1)plan.LinkedOriginals=(await ExcelPayslipRepository.ReadLinkedOriginalsAsync(db,tx,clientId,batch.Id)).ToList();
        plan.Preview.MigrationRequired = !await HasReceiptTableAsync(db, tx);
        void Error(string text, string? rowId = null, string? code = null) => plan.Preview.Issues.Add(new("Error", text, rowId, code));
        var selected = batch.Rows.Where(r => request.RowIds.Contains(r.Id)).ToList();
        plan.Preview.SelectedSourceRows = DescribeSelectedSources(batch, request);
        if (selected.Count != request.RowIds.Count) Error("Some selected source rows no longer belong to this batch.");
        selected = ReviewSourceLocationParts(batch, selected, plan.Preview);
        if (plan.EffectiveFrom > DateTime.ParseExact(batch.Month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture).AddMonths(1).AddDays(-1))
            Error("The salary effective date must not be after the imported payroll period.");
        var sourceIndexes = request.Components.SelectMany(c => new[] { c.SourceColumnIndex, c.ExpectedColumnIndex }).Where(i => i.HasValue).Select(i => i!.Value)
            .Concat(request.FixedPayrollDays.HasValue ? [request.PayableDaysColumnIndex] : new[] { request.PayrollDaysColumnIndex, request.PayableDaysColumnIndex })
            .Concat(request.PresentDaysColumnIndex.HasValue ? [request.PresentDaysColumnIndex.Value] : []);
        if (sourceIndexes.Any(i => i < 0 || i >= source.ColumnCount)) Error("A reviewed source column is outside the saved worksheet.");
        ValidateSharedPayrollDaysBinding(request, source, selected, batch.Rows);
        var appliedRows = await AppliedSourceRowsAsync(db,tx,clientId,request.BatchId);
        if(request.CorrectionOfImportId is not null)await ReviewSalaryCorrectionAsync(db,tx,clientId,request,plan,appliedRows);
        else foreach (var row in selected.Where(row=>appliedRows.Contains(row.Id))) Error("Master setup was already applied for this source row. Use the explicit salary-only correction review to revise a verified existing assignment; native attendance and previous receipts are retained.",row.Id);
        var setupJson = await PayrollDataTableStore.GetSetupJsonAsync(db, tx);
        var setup = JsonNode.Parse(setupJson)!.AsObject();
        var masters = (setup["salaryComponents"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var templates = (setup["salaryStructures"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (request.TransferReviewVersion == 0 && templates.Any(t => RefId(Text(t,"clientId")) == clientId && string.Equals(Text(t,"name"), request.TemplateName.Trim(), StringComparison.OrdinalIgnoreCase)))
            Error("A client salary template already uses this name. Choose a new import template name to preserve its current assignments.");
        plan.Preview.TemplateId = StableId($"template:{clientId}:{request.BatchId}:{request.TemplateName.Trim()}").ToString(CultureInfo.InvariantCulture);
        if (request.TransferReviewVersion == 0 && templates.Any(t => Text(t,"id") == plan.Preview.TemplateId)) Error("The proposed template identifier is already in use.");
        foreach (var component in request.Components)
        {
            var existing = !string.IsNullOrWhiteSpace(component.ExistingComponentId)
                ? masters.SingleOrDefault(m => Text(m,"id") == component.ExistingComponentId)
                : masters.SingleOrDefault(m => string.Equals(Text(m,"code"), component.Code, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(component.ExistingComponentId) && existing is null) Error("The selected component master no longer exists.", code: component.Code);
            if (existing is not null && ReusedComponentDifferences(existing, component) is { Count: > 0 } differences)
                Error("The selected global component differs in " + string.Join(", ", differences) + ". Review the mapping or another component code; shared masters will not be changed.", code: component.Code);
            var id = existing is null ? StableId("component:" + component.Code.ToUpperInvariant()).ToString(CultureInfo.InvariantCulture) : Text(existing,"id");
            if (existing is null && masters.Any(m => Text(m,"id") == id)) Error("A proposed component identifier is already in use.", code: component.Code);
            plan.Components.Add(new(component, id, existing is null));
        }
        var week = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dropdownmasters WHERE Type='Work Week' AND Value=@Week AND IsActive=TRUE AND ClientId IN (0,@ClientId)", new { Week = request.WorkWeek.Trim(), ClientId = clientId }, tx);
        if (week == 0 && !request.CreateMissingDropdownValues) Error("Select an existing active work week or approve creating the reviewed dropdown value.");
        if (week == 0 && request.CreateMissingDropdownValues && Normalize(request.WorkWeek) != "SUNDAYOFF")
            Error("Choose a configured work week or the Sunday off default. A custom work-week calendar must be reviewed in Dropdown Masters before it can be reused.");
        var modeColumns = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='attendance_groups' AND column_name IN ('attendance_mode','allowed_shift_ids_json')", transaction: tx);
        if (modeColumns != 2) Error("The additive attendance scheduling schema upgrade is required for an explicit Monthly policy.");
        if (request.DefaultWorkLocationId is > 0 && await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM worklocations WHERE Id=@Id AND ClientId=@ClientId AND IsActive=TRUE", new { Id = request.DefaultWorkLocationId, ClientId = clientId }, tx) == 0)
            Error("The default work location must be active and belong to this client.");
        var identities = (await db.QueryAsync<Identity>(@"SELECT e.Id,e.ClientId,e.EmployeeCode,CONCAT(e.FirstName,' ',e.LastName) Name,e.IsActive," + EmployeeEmploymentRules.EligibleInMonth("e", "@Month") + @" EligibleForMonth,
COALESCE(p.PanNumber,'') Pan,COALESCE(p.AadhaarNumber,'') Aadhaar,COALESCE(p.UanNumber,'') Uan,COALESCE(w.Name,'') WorkLocation
FROM employees e LEFT JOIN employeepersonaldetails p ON p.EmployeeId=e.Id LEFT JOIN worklocations w ON w.Id=e.WorkLocationId WHERE e.ClientId=@ClientId ORDER BY e.Id", new { ClientId = clientId, Month = batch.Month }, tx)).ToList();
        var locations = (await db.QueryAsync<Location>("SELECT Id,Name FROM worklocations WHERE ClientId=@ClientId AND IsActive=TRUE ORDER BY Id", new { ClientId = clientId }, tx)).ToList();
        var existingState = new List<object>();
        foreach (var row in selected)
        {
            var sourceRow = source.Rows.SingleOrDefault(r => r.SourceRow == row.SourceRow);
            if (sourceRow is null) { Error("The selected row has no authoritative source cells.", row.Id); continue; }
            var decision = request.EmployeeDecisions.SingleOrDefault(d => d.RowId == row.Id);
            var identifierValues = SourceInformation(row,source,request.SourceWorkLocationColumnIndex,reviewedIdentityColumn);
            var match = MatchIdentity(row, identifierValues, identities, decision, plan.Preview.Issues);
            Employee employee;
            if (match is not null)
            {
                employee = (await EmployeeRepository.LoadRecruitmentEmployeeAsync(db, match.Id, tx))!;
                existingState.Add(JsonSerializer.Serialize(employee, Json));
                if (!match.EligibleForMonth)
                    Error("The matched employee is not employed in this salary month. Review their last working date; importing salary does not rehire them.", row.Id);
            }
            else
            {
                employee = NewEmployee(clientId, row, request, identifierValues);
                if (decision?.AllowCreate != true) Error("Review and explicitly allow creation of this unmatched employee.", row.Id);
            }
            var planned = new PlannedRow { RowId = row.Id, SourceRow = row.SourceRow, Employee = employee, ExistingTemplateId=employee.SalaryStructureId, ExistingAnnualCtc=employee.AnnualCtc, ExistingSalaryInputs=new(employee.SalaryComponents ?? []) };
            var sourceLocationName = request.SourceWorkLocationColumnIndex is int sourceLocationIndex ? ReadCellText(sourceRow, sourceLocationIndex)
                : Info(identifierValues,"Work Location","Location","Place of Posting","Posting");
            if (employee.Id == 0 && (employee.WorkLocationId <= 0 || sourceLocationName.Length > 0))
            {
                employee.WorkLocationId = 0;
                var locationName = First(sourceLocationName,request.DefaultWorkLocationName.Trim());
                var location = locations.SingleOrDefault(l => Normalize(l.Name) == Normalize(locationName));
                if (location is not null) employee.WorkLocationId = location.Id;
                else if (locationName.Length > 0 && request.CreateMissingWorkLocations)
                {
                    planned.NewWorkLocationName = locationName;
                    planned.NewWorkLocationState = First(employee.PersonalDetails.State, request.DefaultWorkLocationState.Trim());
                    if (planned.NewWorkLocationState.Length == 0) Error("Review the state for this new work location. It affects payroll and cannot be inferred from salary amounts.", row.Id);
                }
                else if (locationName.Length > 0) Error("The source work location does not exist. Review and explicitly allow creating it.", row.Id);
            }
            plan.Rows.Add(planned);
            try
            {
                var arithmetic = CalculateRow(clientId, request, plan.Components, sourceRow, row, source.Columns.Where(c => c.BlankAsZero).Select(c => c.ColumnIndex).ToHashSet());
                planned.Preview = arithmetic.Preview;
                planned.SalaryInputs = arithmetic.Inputs;
                planned.Adjustments = arithmetic.Adjustments;
                planned.Components = ComponentsForRow(plan.Components, row.Id);
                var variantSignature = ScopedTemplateSignature(planned.Components,request);
                planned.TemplateId = StableId($"template:{clientId}:{request.BatchId}:{request.TemplateName.Trim()}:{variantSignature}").ToString(CultureInfo.InvariantCulture);
                plan.Preview.Issues.AddRange(arithmetic.Issues);
                employee.SalaryStructureId = planned.TemplateId;
                employee.AnnualCtc = arithmetic.Preview.AnnualCtc;
                employee.SalaryComponents = new(arithmetic.Inputs, StringComparer.OrdinalIgnoreCase);
                employee.SalaryJson = JsonSerializer.Serialize(employee.SalaryComponents, Json);
                planned.Preview.EmployeeId = employee.Id > 0 ? employee.Id : null;
                planned.Preview.TemplateId = planned.TemplateId;
                planned.Preview.EmployeeCode = employee.EmployeeCode;
                planned.Preview.EmployeeName = (employee.FirstName + " " + employee.LastName).Trim();
                planned.Preview.Action = employee.Id > 0 ? "Update salary" : "Create employee";
                plan.Preview.Rows.Add(planned.Preview);
                if (employee.Id > 0)
                {
                    var conflicts = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM payrunemployees pe JOIN payruns pr ON pr.Id=pe.PayRunId
WHERE pe.EmployeeId=@Id AND pr.ClientId=@ClientId AND pr.PayPeriod=@Month AND pe.IsSkipped=FALSE AND pr.Status IN ('Draft','Queued','Processing','Pending Approval','Approved','Partially Paid','Paid')", new { employee.Id, ClientId = clientId, Month = batch.Month }, tx);
                    if (conflicts > 0) Error("The selected employee already has payroll for this period. Review that run before changing its salary setup.", row.Id);
                    var approvedAdjustments = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payrolladjustments WHERE ClientId=@ClientId AND EmployeeId=@Id AND PayPeriod=@Month AND Status='Approved'",new { ClientId=clientId,employee.Id,Month=batch.Month },tx);
                    if (approvedAdjustments > 0 && request.CorrectionOfImportId is null) Error("Approved payroll adjustments already exist for this employee and period. Review them before importing amounts that could be counted twice.",row.Id);
                    var groups = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM attendance_group_employees m JOIN attendance_groups g ON g.id=m.attendance_group_id WHERE m.employee_id=@Id AND g.client_id=@ClientId AND g.is_active=TRUE", new { employee.Id, ClientId = clientId }, tx);
                    if (groups > 1) Error("The selected employee belongs to multiple active attendance policies. Resolve the ambiguous membership before applying.", row.Id);
                    else if (groups == 1 && !request.PreserveExistingAttendancePolicies) Error("This employee already has an attendance policy. Enable Preserve existing attendance policies to reuse it unchanged.", row.Id);
                    else if (groups == 1) plan.Preview.Issues.Add(new("Info", "Existing attendance membership will be retained; reviewed monthly totals will be saved without replacing that policy.", row.Id));
                    var later = (await db.QueryAsync<EmployeeRepository.ExcelPayrollSalaryHistory>(@"SELECT Id,EffectiveFrom,Status,SalaryStructureId,AnnualCtc,CAST(SalaryJson AS CHAR) SalaryJson,COALESCE(ChangeReason,'') ChangeReason
FROM employee_it0008_basic_pay WHERE EmployeeId=@Id AND EffectiveFrom>=@Date ORDER BY EffectiveFrom,Id", new { employee.Id, Date = plan.EffectiveFrom }, tx)).ToList();
                    try
                    {
                        var correctionReference = request.CorrectionOfImportId is null ? null
                            : $"Reviewed salary Excel {request.BatchId}, row {row.SourceRow}, import {request.CorrectionOfImportId}";
                        var markers = EmployeeRepository.ExcelSalaryHistoryMarkers(later, plan.EffectiveFrom, correctionReference);
                        if (markers.Length > 0) plan.Preview.Issues.Add(new("Review",
                            correctionReference is null
                                ? $"Initial empty salary-history snapshots will be retained. Salary starts {plan.EffectiveFrom:yyyy-MM-dd}, with matching salary versions at the original setup dates ({string.Join(", ", markers.Select(d=>d.ToString("yyyy-MM-dd")))}). No configured future salary can be replaced."
                                : $"Original-import salary history will be retained. Corrected versions are appended at its original effective dates ({string.Join(", ", markers.Select(d=>d.ToString("yyyy-MM-dd")))}); unrelated configured history cannot be replaced.", row.Id));
                    }
                    catch (InvalidOperationException e) { Error(e.Message, row.Id); }
                }
            }
            catch (InvalidOperationException e) { Error(e.Message, row.Id); }
        }
        if (plan.Rows.Count > 0)
        {
            plan.SalaryOnlyBankReviewRows=SalaryOnlyBankReviewRows(batch,source,identities,reviewedIdentityColumns);
            if(reviewedIdentityColumn.HasValue)
                foreach(var row in plan.Rows.Where(row=>row.Employee.Id<=0 || !plan.SalaryOnlyBankReviewRows.Contains(row.RowId)))
                    Error("The reviewed identity column still requires one uniquely source-anchored existing employee. Name-only or conflicting identity cannot be imported.",row.RowId);
            var identityReview = await ReviewApplyIdentitiesAsync(clientId, request.TransferReviewVersion, request.ImportAttendance,
                plan.Rows.Select(row => (row.RowId, row.Employee)).ToArray(), plan.SalaryOnlyBankReviewRows,
                async candidates => (await employees.PreviewExcelPayrollIdentitiesAsync(candidates, db, tx)).Rows);
            plan.Preview.Issues.AddRange(identityReview.Issues);
            plan.ExistingSalaryOnlyIdentityIssues = identityReview.ExistingSalaryOnlyRows;
        }
        if (plan.Rows.Where(r => r.Employee.Id > 0).Select(r => r.Employee.Id).Distinct().Count() != plan.Rows.Count(r => r.Employee.Id > 0)) Error("Several source rows resolve to the same employee.");
        plan.Preview.TemplateIds = plan.Rows.Where(r => r.TemplateId.Length > 0).Select(r => r.TemplateId).Distinct().ToList();
        plan.Preview.TemplateId = plan.Preview.TemplateIds.FirstOrDefault() ?? plan.Preview.TemplateId;
        foreach (var id in plan.Preview.TemplateIds)
            if (request.TransferReviewVersion == 0 && templates.Any(t => Text(t,"id") == id)) Error("A proposed salary template variant already exists. Refresh the saved import result.");
        var policyJson = await db.ExecuteScalarAsync<string?>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@ClientId AND ModuleCode='payroll'", new { ClientId = clientId }, tx);
        var attendanceJson = await db.ExecuteScalarAsync<string?>("SELECT JSON_EXTRACT(SettingsJson,'$.attendanceIntegration.WeeklyOffRuleVersions') FROM modulesettings WHERE client_id=@ClientId AND ModuleCode='leave_attendance'", new { ClientId = clientId }, tx);
        if (request.ImportAttendance && !string.IsNullOrWhiteSpace(attendanceJson) && attendanceJson is not "null" and not "[]") Error("This client has published weekly-off rules. A monthly-total import cannot establish dated weekly-off entitlement. Turn off Import source-month attendance to retain the existing attendance workflow and configure salary masters only.");
        if (!request.ImportAttendance) plan.Preview.Issues.Add(new("Review","Source-month attendance and attendance-group membership will not be saved. Enter/review attendance through the regular module before running payroll."));
        plan.Preview.Issues.Add(new("Review", "After applying setup, run Employee simulation for every selected row before queuing regular payroll. Existing statutory and tax rules may change the draft arithmetic."));
        plan.Preview.Issues.Add(new("Info", "Leave, holiday and shift defaults are saved with this editable setup draft. No invented quota, holiday dates, leave balances or punch shifts will be activated; source monthly payable totals are retained."));
        plan.Preview.Issues.Add(new("Info", "Known source fields are copied for reviewed new employees. Existing employee organization, personal and bank details are preserved. Approved missing department/designation/grade/work-week values are added for this client."));
        var existingIds = plan.Rows.Select(r => r.Employee.Id).Where(id => id > 0).Distinct().ToArray();
        if (plan.Rows.Where(row => row.NewWorkLocationName.Length > 0).GroupBy(row => Normalize(row.NewWorkLocationName)).Any(group => group.Select(row => Normalize(row.NewWorkLocationState)).Distinct().Count() > 1))
            Error("The same new location has conflicting states in the selected source rows. Review the source before applying.");
        var salaryHistory = existingIds.Length == 0 ? [] : (await db.QueryAsync(@"SELECT Id,EmployeeId,EffectiveFrom,EffectiveTo,Status,SalaryStructureId,AnnualCtc,CAST(SalaryJson AS CHAR) SalaryJson,COALESCE(ChangeReason,'') ChangeReason
FROM employee_it0008_basic_pay WHERE EmployeeId IN @Ids ORDER BY EmployeeId,Id", new { Ids = existingIds }, tx)).ToList();
        var attendanceState = existingIds.Length == 0 ? [] : (await db.QueryAsync(@"SELECT id,employee_id,attendance_month,working_days,present_days,payable_days,lop_days,source_type,remarks
FROM employee_monthly_attendance WHERE client_id=@ClientId AND attendance_month=@Month AND employee_id IN @Ids ORDER BY employee_id", new { ClientId = clientId, Month = batch.Month, Ids = existingIds }, tx)).ToList();
        var payMonth = DateTime.ParseExact(batch.Month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var fyStart = payMonth.Month >= 4 ? payMonth.Year : payMonth.Year - 1;
        var fy = $"{fyStart}-{(fyStart+1)%100:00}";
        var taxSettings = (await db.QueryAsync("SELECT * FROM tax_client_settings WHERE client_id=@ClientId AND financial_year=@Fy ORDER BY id", new { ClientId = clientId, Fy = fy }, tx)).ToList();
        var taxVersions = (await db.QueryAsync("SELECT * FROM tax_rule_versions WHERE financial_year=@Fy ORDER BY id", new { Fy = fy }, tx)).ToList();
        var selectedRegimes = existingIds.Length == 0 ? [] : (await db.QueryAsync("SELECT * FROM employee_tax_regime_selections WHERE employee_id IN @Ids AND financial_year=@Fy ORDER BY employee_id,id", new { Ids = existingIds, Fy = fy }, tx)).ToList();
        var payrollHistory = existingIds.Length == 0 ? [] : (await db.QueryAsync(@"SELECT pr.Id,pr.Status,pr.PayPeriod,pe.EmployeeId,pe.GrossPay,pe.StatutoryDeductions,pe.NetPay
FROM payruns pr JOIN payrunemployees pe ON pe.PayRunId=pr.Id WHERE pr.ClientId=@ClientId AND pe.EmployeeId IN @Ids AND pr.PayPeriod<=@Month ORDER BY pr.Id,pe.EmployeeId",
            new { ClientId = clientId, Ids = existingIds, Month = batch.Month }, tx)).ToList();
        var adjustmentsState = existingIds.Length == 0 ? [] : (await db.QueryAsync("SELECT * FROM payrolladjustments WHERE ClientId=@ClientId AND EmployeeId IN @Ids AND PayPeriod=@Month ORDER BY EmployeeId,Id",new { ClientId=clientId,Ids=existingIds,Month=batch.Month },tx)).ToList();
        var membershipState = existingIds.Length == 0 ? [] : (await db.QueryAsync(@"SELECT m.employee_id,g.* FROM attendance_group_employees m JOIN attendance_groups g ON g.id=m.attendance_group_id WHERE g.client_id=@ClientId AND m.employee_id IN @Ids ORDER BY m.employee_id,g.id", new { ClientId = clientId, Ids = existingIds }, tx)).ToList();
        if(request.AttendanceReview?.Mode=="VerifyExistingNative")plan.NativeAttendanceInputs=await PayRunRepository.ReadNativeAttendanceProofAsync(db,tx,clientId,batch.Month,existingIds,allowRecordedMonthlyForSalaryCorrection:request.CorrectionOfImportId is not null);
        if(request.TransferReviewVersion==1 && request.TemplateScopeReviewVersion!=1 && !request.TemplateDecisions.All(d=>d.KeepExisting)) Error("Refresh the saved-batch setup and complete explicit template applicability review before source transfer.");
        if(request.TemplateScopeReviewVersion==1)plan.TemplateScopes=await ReadTemplateScopesAsync(db,tx,clientId);
        FinalizeTransferReview(plan, request, clientId, templates, masters);
        await ValidatePayableOnlyAttendanceStateAsync(db, tx, clientId, request, plan);
        await ValidatePayableOverrideAsync(db, tx, clientId, request, plan);
        FinalizePresentDaysEvidence(plan, request, clientId);
        await ReviewSourceAdjustmentReusesAsync(db, tx, clientId, plan,request.CorrectionOfImportId is not null);
        plan.Preview.PlanHash = Hash(JsonSerializer.Serialize(new { clientId, request, batch, source, setupJson, identities, locations, existingState, salaryHistory, attendanceState, membershipState,
            templateScopes=plan.TemplateScopes, linkedOriginals=plan.LinkedOriginals, nativeAttendanceInputs=plan.NativeAttendanceInputs, policyJson, attendanceJson, taxSettings, taxVersions, selectedRegimes, payrollHistory,adjustmentsState, adjustmentReuses=plan.AdjustmentReuses,correctionReceiptFingerprint=plan.CorrectionReceiptFingerprint }, Json));
        return plan;
    }

    internal static IReadOnlyList<string> ReusedComponentDifferences(JsonObject existing, ExcelPayrollSetupComponent proposed)
    {
        var differences = new List<string>();
        if (!string.Equals(Text(existing,"code"),proposed.Code,StringComparison.OrdinalIgnoreCase)
            || Text(existing,"category") != proposed.Category || Text(existing,"componentRole") != proposed.ComponentRole
            || Text(existing,"statutoryType", "None") != proposed.StatutoryType || Text(existing,"active") == "false"
            || (Text(existing,"calculationType") == "Daily Rate") != (proposed.CalculationType == "Daily Rate"))
            differences.Add("classification or pay basis");
        // Template lines can override arithmetic and proration, but these fields
        // are read from the shared master by regular Employee payroll. A draft
        // must not simulate requested metadata that Apply cannot persist.
        if (!bool.TryParse(Text(existing,"taxable"), out var taxable) || taxable != proposed.Taxable)
            differences.Add("Taxable");
        if (!int.TryParse(Text(existing,"priority"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var priority) || priority != proposed.Priority)
            differences.Add("Priority");
        if (Text(existing,"payType") != ResolvedPayType(proposed))
            differences.Add("PayType");
        return differences;
    }

    internal static string ResolvedPayType(ExcelPayrollSetupComponent component) => string.IsNullOrWhiteSpace(component.PayType)
        ? component.IsPeriodAdjustment ? "Variable Pay" : "Fixed Pay"
        : component.PayType;

    internal static bool SameReviewedRequest(string savedJson,ExcelPayrollSetupRequest current)
    {
        try { var saved=JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(savedJson,Json); return saved is not null && JsonNode.DeepEquals(JsonSerializer.SerializeToNode(saved,Json),JsonSerializer.SerializeToNode(current,Json)); }
        catch(JsonException) { return false; }
    }
    private sealed class Receipt { public string Id { get; set; } = ""; public string RequestId { get; set; } = ""; public string Status { get; set; } = ""; public string RequestHash { get; set; } = ""; public string RequestJson { get; set; } = "{}"; public string ResultJson { get; set; } = ""; }
    private sealed class SavedSetup { public string ImportId { get; set; } = ""; public string RequestId { get; set; } = ""; public string Status { get; set; } = ""; public string RequestJson { get; set; } = "{}"; public string ResultJson { get; set; } = "{}"; }
    internal sealed class Plan
    {
        public ExcelPayrollSetupPreview Preview { get; } = new();
        public ExcelPayslipBatch Batch { get; set; } = new();
        public ExcelPayslipCalculationSource Source { get; set; } = new();
        public List<ExcelPayslipOriginal> LinkedOriginals { get; set; } = [];
        public Dictionary<int,NativePayrollAttendanceInput> NativeAttendanceInputs { get; set; } = [];
        public DateTime EffectiveFrom { get; set; }
        public HashSet<string> ReusedTemplateIds { get; } = new();
        public HashSet<string> SalaryOnlyBankReviewRows { get; set; } = new();
        public IReadOnlyDictionary<string, IReadOnlyList<ExcelPayrollSetupIssue>>? ExistingSalaryOnlyIdentityIssues { get; set; }
        public List<ExcelPayrollRecordedTemplateScope> TemplateScopes { get; set; } = [];
        public List<ResolvedComponent> Components { get; } = [];
        public List<ExcelPayrollReusedAdjustment> AdjustmentReuses { get; } = [];
        public string? CorrectionReceiptFingerprint { get; set; }
        public List<PlannedRow> Rows { get; } = [];
    }
    internal sealed record ResolvedComponent(ExcelPayrollSetupComponent Definition, string Id, bool IsNew);
    internal sealed class PlannedRow
    {
        public bool KeepExisting { get; set; }
        public string ExistingTemplateId { get; set; } = "";
        public decimal ExistingAnnualCtc { get; set; }
        public Dictionary<string, decimal> ExistingSalaryInputs { get; set; } = [];
        public string RowId { get; set; } = ""; public int SourceRow { get; set; }
        public Employee Employee { get; set; } = new();
        public ExcelPayrollSetupPreviewRow Preview { get; set; } = new();
        public Dictionary<string, decimal> SalaryInputs { get; set; } = [];
        public List<(ResolvedComponent Component, decimal Amount)> Adjustments { get; set; } = [];
        public string NewWorkLocationName { get; set; } = "";
        public string NewWorkLocationState { get; set; } = "";
        public string TemplateId { get; set; } = "";
        public IReadOnlyList<ResolvedComponent> Components { get; set; } = [];
    }
    internal sealed class Identity
    {
        public int Id { get; set; } public int ClientId { get; set; } public string EmployeeCode { get; set; } = "";
        public string Name { get; set; } = ""; public bool IsActive { get; set; }
        public bool EligibleForMonth { get; set; }
        public string Pan { get; set; } = ""; public string Aadhaar { get; set; } = ""; public string Uan { get; set; } = "";
        public string WorkLocation { get; set; } = "";
    }
    private sealed class Location { public int Id { get; set; } public string Name { get; set; } = ""; }
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static string SelectionHash(IEnumerable<string> ids) => Hash(string.Join("|", ids.Order(StringComparer.Ordinal)));
    private static long StableId(string seed) => 1_000_000_000L + long.Parse(Hash(seed)[..11], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    private static string Text(JsonObject obj, string key, string fallback = "") => obj[key]?.ToString() ?? fallback;
    private static int RefId(string raw) => int.TryParse(raw.Split(':')[0], out var result) ? result : 0;
}
