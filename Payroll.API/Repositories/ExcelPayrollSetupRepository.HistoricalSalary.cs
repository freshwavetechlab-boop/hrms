using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    private sealed class HistoricalReview
    {
        public int Version { get; set; } = 1;
        public string Purpose { get; set; } = "ClosedHistoricalSalary";
        public int ActorId { get; set; }
        public int ClientId { get; set; }
        public string BatchId { get; set; } = "";
        public DateTime ExpiresAtUtc { get; set; }
        public string Fingerprint { get; set; } = "";
        public HistoricalPeriodSalaryRequest Request { get; set; } = new();
    }
    private sealed class HistoricalStoredReceipt
    {
        public int ActorId { get; set; }
        public string SourceHash { get; set; } = "";
        public string RetainedMasterHash { get; set; } = "";
        public HistoricalPeriodSalaryReceipt Receipt { get; set; } = new();
        [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public HistoricalPeriodSalaryRequest? ReviewedRequest { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public List<PayRunRepository.HistoricalPtPrevious>? PreviousProfessionalTax { get; set; }
    }
    private PortableIntegrationCredentialProtector HistoricalProtector => historicalReviewProtector
        ?? new PortableIntegrationCredentialProtector(configuration, new EphemeralDataProtectionProvider(), NullLogger<PortableIntegrationCredentialProtector>.Instance);
    internal static bool CanManageHistoricalSalary(AuthUser actor, int clientId) => actor is not null && SelectedPayRunAuthorization.CanAccess(actor, clientId)
        && !actor.MustChangePassword && SelectedClientAccess.Allows(actor, clientId)
        && actor.Permissions?.Contains("employees.manage", StringComparer.OrdinalIgnoreCase) == true;
    private static void RequireHistoricalActor(AuthUser actor, int clientId)
    {
        if (!CanManageHistoricalSalary(actor, clientId)) throw new UnauthorizedAccessException("A global active payroll operator with Employee Master management permission is required.");
    }

    public async Task<HistoricalPeriodSalaryPreview> PreviewHistoricalSalaryAsync(int clientId, HistoricalPeriodSalaryRequest request,
        AuthUser actor)
    {
        RequireHistoricalActor(actor, clientId); ValidateHistoricalSalaryRequest(request);
        await using var db = Db(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, isReadOnly: true);
        var plan = await BuildHistoricalPlanAsync(db, tx, clientId, request, false,actor);
        await tx.RollbackAsync();
        if (plan.Preview.CanApply)
        {
            var expires = DateTime.UtcNow.AddMinutes(30);
            var review = new HistoricalReview { ActorId = actor.Id, ClientId = clientId, BatchId = request.Setup.BatchId,
                ExpiresAtUtc = expires, Fingerprint = plan.Preview.Fingerprint, Request = request };
            var plaintext = JsonSerializer.Serialize(review, Json);
            if (Encoding.UTF8.GetByteCount(plaintext) > 2_000_000) throw new InvalidOperationException("Review fewer historical salary rows; the protected review exceeds its bounded size.");
            plan.Preview.ReviewToken = HistoricalProtector.ProtectHistoricalSalary(plaintext);
            plan.Preview.ExpiresAtUtc = expires;
        }
        return plan.Preview;
    }

    public async Task<HistoricalPeriodSalaryReceipt> ApplyHistoricalSalaryAsync(int clientId, HistoricalPeriodSalaryApplyRequest command,
        AuthUser actor)
    {
        RequireHistoricalActor(actor, clientId);
        if (!SelectedRunRuntime.Read(configuration).Safe)
            throw new SelectedPayRunConflictException("Historical salary save requires migrations/global workers disabled and outbound delivery suppressed. No record was saved.");
        if (command is null || !command.Acknowledged || !Guid.TryParse(command.RequestId, out var parsed)
            || command.ReviewToken is null || command.ReviewToken.Length is < 1 or > 3_000_000)
            throw new InvalidOperationException("Review the exact closed-period comparison and acknowledge it with one stable request ID.");
        var review = ReadHistoricalReview(command.ReviewToken);
        if (review.ActorId != actor.Id || review.ClientId != clientId || review.BatchId != review.Request.Setup.BatchId)
            throw new UnauthorizedAccessException("This historical review belongs to a different operator or client.");
        var requestId = parsed.ToString("N");
        var code = "closed_salary_receipt:" + clientId + ":" + requestId;
        await using var db = Db(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        // Serialize against the same client writer boundary used by normal payroll/setup.
        if (await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@Id AND IsActive=TRUE FOR UPDATE", new { Id = clientId }, tx) is null)
            throw new InvalidOperationException("The selected client is unavailable.");
        var prior = await ReadHistoricalReceiptAsync(db, tx, clientId, code);
        if (prior is not null)
        {
            if (prior.ActorId != actor.Id || prior.Receipt.BatchId != review.BatchId || prior.Receipt.Fingerprint != review.Fingerprint)
                throw new InvalidOperationException("This historical request ID already belongs to a different reviewed action.");
            await tx.RollbackAsync(); prior.Receipt.Replayed = true; return prior.Receipt;
        }
        if (review.ExpiresAtUtc <= DateTime.UtcNow) throw new InvalidOperationException("This historical review expired. Refresh the exact source/native comparison before saving.");
        var plan = await BuildHistoricalPlanAsync(db, tx, clientId, review.Request, true,actor);
        if (!plan.Preview.CanApply || plan.Preview.Fingerprint != review.Fingerprint)
            throw new SelectedPayRunConflictException("Source, identity, current Master, salary history, template, attendance, policy or run coverage changed. Refresh and review; no historical record was saved.");
        var receipt = new HistoricalPeriodSalaryReceipt { ClientId = clientId, BatchId = review.BatchId,
            PayPeriod = plan.Preview.PayPeriod, RequestId = requestId, Fingerprint = review.Fingerprint, AppliedAtUtc = DateTime.UtcNow };
        var reportedSeeds=plan.Writes.Where(w=>w.ReportedInputs is not null).ToDictionary(w=>w.Assignment.EmployeeId,w=>w.ReportedInputs!.Seed);
        var savedReportedSeeds=await InsertHistoricalPayableSeedsAsync(db,tx,reportedSeeds,requestId,actor);
        foreach (var write in plan.Writes)
        {
            var reference = "Historical Excel salary " + requestId + "; batch " + review.BatchId + "; row " + write.Assignment.RowId;
            var historyId = write.InitialTarget is null
                ?await EmployeeRepository.InsertClosedSalaryHistoryAsync(db, tx, plan.State, write.Assignment.EmployeeId,
                    write.EmployeeCode, write.Assignment.TemplateId, write.AnnualTarget, write.Inputs, plan.From, plan.To, reference, actor.Email)
                :await EmployeeRepository.InsertClosedInitialSalaryHistoryAsync(db,tx,plan.State,write.Assignment.EmployeeId,
                    write.EmployeeCode,write.Assignment.TemplateId,write.InitialTarget,write.Inputs,plan.From,plan.To,reference,actor.Email,write.NamedInputs?.Context);
            receipt.Rows.Add(new(write.Assignment.EmployeeId, write.Assignment.RowId, historyId, write.Assignment.TemplateId));
            if(write.InitialTarget is not null)(receipt.InitialSourceTargets??=[]).Add(write.InitialTarget);
            if(write.LiteralSelfInputs is not null)(receipt.LiteralSelfInputs??=[]).AddRange(write.LiteralSelfInputs);
            if(write.SourceIdentity is not null)(receipt.SourceIdentities??=[]).Add(write.SourceIdentity);
            if(write.NamedInputs is{} named)
            {
                var (namedBatch,namedSource)=await ReadSourceAsync(db,tx,clientId,review.BatchId);
                var namedRow=namedBatch.Rows.Single(row=>row.Id==write.Assignment.RowId);
                var manifest=await HistoricalNamedFormulaInputStore.InsertAsync(db,tx,configuration,named.Profile,named.Context,named.Cells,
                    historyId,write.Assignment.EmployeeId,namedBatch,namedSource,namedRow,plan.SourceHash,requestId,actor.Id,receipt.AppliedAtUtc);
                (receipt.NamedInputs??=[]).Add(manifest);
            }
            if(write.ReportedInputs is{} reported)
            {
                var assignment=await ReportedPeriodHistoricalInputStore.InsertAsync(db,tx,configuration,reported.Profile,reported.Bound,
                    savedReportedSeeds[write.Assignment.EmployeeId],historyId,write.Assignment.EmployeeId,requestId,actor.Id,receipt.AppliedAtUtc);
                (receipt.ReportedInputs??=[]).Add(assignment);
            }
            var amountReview=plan.Preview.Rows.Single(row=>row.RowId==write.Assignment.RowId).AmountReview;
            if(amountReview is{Status:BoundedHistoricalDifferenceStatus,BoundedDifference:not null})
            {
                if(!IsAcknowledgedHistoricalAmountDifference(review.Request,amountReview))throw new InvalidOperationException("The exact bounded amount difference was not acknowledged; the whole save must roll back.");
                (receipt.AmountDifferences??=[]).Add(new(write.Assignment.EmployeeId,write.Assignment.RowId,write.Assignment.TemplateId,
                    JsonSerializer.Deserialize<ExcelPayrollTransferAmountReview>(JsonSerializer.Serialize(amountReview,Json),Json)!));
            }
        }
        var stored = new HistoricalStoredReceipt { ActorId = actor.Id, SourceHash = plan.SourceHash, RetainedMasterHash = plan.State.CurrentMasterHash, Receipt = receipt,
            ReviewedRequest=review.Request.AmountDifferenceReview is null&&review.Request.NamedInputReview is null&&review.Request.Setup.ReportedPeriodInputs is null?null:review.Request };
        var composedPt=requestHasPt(review.Request);
        var earlyNamedReceipt=receipt.NamedInputs is {Count:>0}||receipt.ReportedInputs is {Count:>0};
        string? newNamedReceiptEnvelope=null;
        if(earlyNamedReceipt)
        {
            // Complete named manifests/roster are authenticated before the
            // actual saved-history PT basis is read. This is a NEW row in this
            // uncommitted transaction; no existing receipt is ever rewritten.
            newNamedReceiptEnvelope=await InsertHistoricalReceiptAsync(db,tx,clientId,code,stored);
        }
        if(composedPt)
        {
            stored.ReviewedRequest=review.Request;stored.PreviousProfessionalTax=[];
            var controlContext=configuration.GetValue<bool>("Payroll:HistoricalBatchProofEnabled")
                ?await HistoricalRuns.CreateHistoricalSimulationContextAsync(db,tx,clientId,receipt.PayPeriod,
                    plan.Writes.Select(w=>w.Assignment.EmployeeId).ToArray(),controlBasisOnly:true):null;
            foreach(var write in plan.Writes)
            {
                if(write.ProfessionalTax is null)throw new InvalidOperationException("The reviewed proposed-basis PT evidence is missing. The complete transaction must roll back.");
                var saved=await HistoricalRuns.SaveHistoricalProfessionalTaxAsync(db,tx,clientId,review.Request,write.ProfessionalTax,requestId,actor,write.SourceIdentity,controlContext);
                (receipt.ProfessionalTax??=[]).Add(saved.Evidence);stored.PreviousProfessionalTax.Add(saved.Previous);
            }
            // A protected receipt is visible only inside this transaction. Ordinary
            // Employee simulation authenticates its source/identity/actual-basis
            // control; failure below rolls back the history, control and receipt.
            if(newNamedReceiptEnvelope is not null)await FinalizeNewNamedHistoricalReceiptAsync(db,tx,clientId,code,newNamedReceiptEnvelope,stored);
            else await InsertHistoricalReceiptAsync(db,tx,clientId,code,stored);
        }
        // Read through the ordinary effective-history resolver after insertion.
        // No in-memory salary, day, policy or amount override is used in this final guard.
        var savedContext=configuration.GetValue<bool>("Payroll:HistoricalBatchProofEnabled")
            ?await HistoricalRuns.CreateHistoricalSimulationContextAsync(db,tx,clientId,receipt.PayPeriod,
                plan.Writes.Select(w=>w.Assignment.EmployeeId).ToArray()):null;
        foreach (var write in plan.Writes)
        {
            var actual = await HistoricalRuns.SimulateHistoricalSalaryInTransactionAsync(db, tx,
                new() { Mode = "Employee", ClientId = clientId, EmployeeId = write.Assignment.EmployeeId, PayPeriod = receipt.PayPeriod }, null,context:savedContext);
            var row = plan.Preview.Rows.Single(r => r.RowId == write.Assignment.RowId);
            if (!actual.Simulation.Complete || !HistoricalSavedCalculationMatches(row, actual.Simulation))
                throw new InvalidOperationException("The saved effective-period Employee simulation differs from the reviewed full source comparison. The complete historical transaction was rolled back.");
        }
        var after = await EmployeeRepository.ReadClosedSalaryHistoryStateAsync(db, tx, clientId,
            plan.Writes.Select(w => w.Assignment.EmployeeId).ToArray(), true);
        EmployeeRepository.RequireClosedHistoryPreserved(plan.State, after, receipt.Rows.Select(r => r.HistoryId).ToArray());
        if(!composedPt&&!earlyNamedReceipt)await InsertHistoricalReceiptAsync(db,tx,clientId,code,stored);
        await tx.CommitAsync();
        return receipt;
    }

    private static bool requestHasPt(HistoricalPeriodSalaryRequest request)=>request.PeriodProfessionalTaxReview is not null;
    private async Task<string> InsertHistoricalReceiptAsync(MySqlConnection db,MySqlTransaction tx,int clientId,string code,HistoricalStoredReceipt stored)
    {
        var payload=HistoricalProtector.ProtectHistoricalSalary(JsonSerializer.Serialize(stored,Json));
        var envelope=JsonSerializer.Serialize(new{purpose="ClosedHistoricalSalaryReceipt",payload},Json);
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@ClientId,@Code,TRUE,CAST(@Json AS JSON))",
            new{ClientId=clientId,Code=code,Json=envelope},tx);
        return envelope;
    }

    private async Task FinalizeNewNamedHistoricalReceiptAsync(MySqlConnection db,MySqlTransaction tx,int clientId,string code,string insertedEnvelope,HistoricalStoredReceipt stored)
    {
        if(tx.Connection!=db||string.IsNullOrEmpty(insertedEnvelope)||(stored.Receipt.NamedInputs is not{Count:>0}&&stored.Receipt.ReportedInputs is not{Count:>0})
            ||code!="closed_salary_receipt:"+clientId+":"+stored.Receipt.RequestId)
            throw new InvalidOperationException("Only the exact new uncommitted named-history receipt may be finalized.");
        var payload=HistoricalProtector.ProtectHistoricalSalary(JsonSerializer.Serialize(stored,Json));
        var affected=await db.ExecuteAsync("UPDATE modulesettings SET SettingsJson=CAST(@Json AS JSON) WHERE client_id=@ClientId AND ModuleCode=@Code AND IsEnabled=TRUE AND SettingsJson=CAST(@InsertedEnvelope AS JSON)",
            new{ClientId=clientId,Code=code,InsertedEnvelope=insertedEnvelope,Json=JsonSerializer.Serialize(new{purpose="ClosedHistoricalSalaryReceipt",payload},Json)},tx);
        if(affected!=1)throw new InvalidOperationException("The exact new named-history receipt could not be finalized; the complete transaction must roll back.");
    }

    private HistoricalReview ReadHistoricalReview(string token)
    {
        try
        {
            var plaintext = HistoricalProtector.UnprotectHistoricalSalary(token)
                ?? throw new InvalidOperationException("Invalid protected historical salary review.");
            var review = JsonSerializer.Deserialize<HistoricalReview>(plaintext, Json);
            if (review is not { Version: 1, Purpose: "ClosedHistoricalSalary", ActorId: > 0, ClientId: > 0 }
                || review.Fingerprint.Length != 64 || !Guid.TryParseExact(review.BatchId, "N", out _))
                throw new InvalidOperationException("Invalid protected historical salary review scope.");
            ValidateHistoricalSalaryRequest(review.Request); return review;
        }
        catch (Exception error) when (error is JsonException or FormatException or System.Security.Cryptography.CryptographicException)
        { throw new InvalidOperationException("The historical salary review is invalid; refresh the comparison without changing any data."); }
    }

    private async Task<HistoricalStoredReceipt?> ReadHistoricalReceiptAsync(MySqlConnection db, MySqlTransaction tx, int clientId, string code)
    {
        var text = await db.ExecuteScalarAsync<string?>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Code FOR UPDATE",
            new { ClientId = clientId, Code = code }, tx);
        if (text is null) return null;
        try
        {
            var node = JsonNode.Parse(text)!;
            if (node["purpose"]?.GetValue<string>() != "ClosedHistoricalSalaryReceipt") throw new InvalidOperationException("Historical receipt purpose is invalid.");
            var value = HistoricalProtector.UnprotectHistoricalSalary(node["payload"]!.GetValue<string>())
                ?? throw new InvalidOperationException("Historical receipt could not be verified.");
            return JsonSerializer.Deserialize<HistoricalStoredReceipt>(value, Json)
                ?? throw new InvalidOperationException("Historical receipt is invalid.");
        }
        catch (JsonException) { throw new InvalidOperationException("Historical receipt is invalid. No repeat salary action is permitted."); }
    }

    private static bool HistoricalSavedCalculationMatches(HistoricalPeriodSalaryPreviewRow reviewed, PayrollSimulationResult actual)
    {
        if (actual.Gross != reviewed.Gross || actual.Deductions != reviewed.TotalDeductions
            || actual.Benefits != reviewed.EmployerCost || actual.Net != reviewed.NetPay) return false;
        var codes = reviewed.Lines.Select(l => l.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (actual.Lines.Any(line => line.Earned != 0 && !codes.Contains(line.Code))) return false;
        return reviewed.Lines.All(line => actual.Lines.Count(l => l.Code.Equals(line.Code, StringComparison.OrdinalIgnoreCase)) is 0 or 1
            && (actual.Lines.SingleOrDefault(l => l.Code.Equals(line.Code, StringComparison.OrdinalIgnoreCase))?.Earned ?? 0) == line.CalculatedAmount);
    }

    private static async Task LockHistoricalInputsAsync(MySqlConnection db, MySqlTransaction tx, int clientId, HistoricalPeriodSalaryRequest request)
    {
        var ids = request.Assignments.Select(a => a.EmployeeId).Order().ToArray();
        var templates = request.Assignments.Select(a => a.TemplateId).Distinct().Order(StringComparer.Ordinal).ToArray();
        var components = request.Setup.Components.Select(c => c.ExistingComponentId!).Distinct().Order(StringComparer.Ordinal).ToArray();
        await db.QueryAsync<string>("SELECT id FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@BatchId FOR UPDATE", new { ClientId = clientId, request.Setup.BatchId }, tx);
        await db.QueryAsync<string>("SELECT batch_id FROM excel_payslip_calculation_sources WHERE client_id=@ClientId AND batch_id=@BatchId FOR UPDATE", new { ClientId = clientId, request.Setup.BatchId }, tx);
        await db.QueryAsync<string>("SELECT CAST(Id AS CHAR) FROM salarystructures WHERE CAST(Id AS CHAR) IN @Ids FOR UPDATE", new { Ids = templates }, tx);
        await db.QueryAsync<long>("SELECT Id FROM salarystructurelines WHERE CAST(StructureId AS CHAR) IN @Ids FOR UPDATE", new { Ids = templates }, tx);
        await db.QueryAsync<string>("SELECT CAST(Id AS CHAR) FROM salarycomponents WHERE CAST(Id AS CHAR) IN @Ids FOR UPDATE", new { Ids = components }, tx);
        await db.QueryAsync<string>("SELECT ModuleCode FROM modulesettings WHERE client_id IN (0,@ClientId) FOR UPDATE", new { ClientId = clientId }, tx);
        var period = await db.ExecuteScalarAsync<string>("SELECT JSON_UNQUOTE(JSON_EXTRACT(batch_json,'$.month')) FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@BatchId FOR UPDATE", new { ClientId = clientId, request.Setup.BatchId }, tx);
        var cycle = PayRunRepository.NativeAttendanceCycle(period!, request.Setup.AttendanceCycleStartDay, request.Setup.AttendanceCycleEndDay);
        await db.QueryAsync<long>("SELECT id FROM employee_monthly_attendance WHERE client_id=@ClientId AND attendance_month=@Period AND employee_id IN @Ids FOR UPDATE", new { ClientId = clientId, Period = period, Ids = ids }, tx);
        await db.QueryAsync<int>("SELECT employee_id FROM employee_daily_attendance WHERE client_id=@ClientId AND employee_id IN @Ids AND attendance_date BETWEEN @Start AND @End FOR UPDATE", new { ClientId = clientId, Ids = ids, Start = cycle.Start, End = cycle.End }, tx);
        await db.QueryAsync<int>("SELECT employee_id FROM attendance_group_employees WHERE employee_id IN @Ids FOR UPDATE", new { Ids = ids }, tx);
        await db.QueryAsync<int>("SELECT g.id FROM attendance_groups g JOIN attendance_group_employees m ON m.attendance_group_id=g.id WHERE g.client_id=@ClientId AND m.employee_id IN @Ids FOR UPDATE", new { ClientId = clientId, Ids = ids }, tx);
        await db.QueryAsync<int>("SELECT Id FROM payruns WHERE ClientId=@ClientId AND PayPeriod=@Period FOR UPDATE", new { ClientId = clientId, Period = period }, tx);
        await db.QueryAsync<int>("SELECT p.Id FROM payrunemployees p JOIN payruns r ON r.Id=p.PayRunId WHERE r.ClientId=@ClientId AND r.PayPeriod=@Period AND p.EmployeeId IN @Ids FOR UPDATE", new { ClientId = clientId, Period = period, Ids = ids }, tx);
    }
}
