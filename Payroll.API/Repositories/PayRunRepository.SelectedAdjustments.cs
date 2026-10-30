using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    private sealed class SelectedAdjustmentState
    {
        public List<PayrollAdjustment> Rows { get; init; } = [];
        public List<SelectedPayRunAdjustmentReview> Reviews { get; init; } = [];
        public List<string> Issues { get; init; } = [];
        public long ApplicableCount { get; init; }
        public List<AppliedAdjustmentReview> AppliedReviews { get; init; } = [];
    }
    private sealed record SelectedSourceImport(string Id, string BatchId, string RequestJson, string ResultJson);

    private async Task<SelectedAdjustmentState> ReadSelectedAdjustmentsAsync(MySqlConnection connection,
        MySqlTransaction? transaction, CreatePayRunRequest request, bool lockRows, bool requireFrozen)
    {
        var rows = new List<PayrollAdjustment>(); var reviews = new List<SelectedPayRunAdjustmentReview>();
        var appliedReviews=new List<AppliedAdjustmentReview>();
        var issues = new List<string>(); long count = 0;
        try
        {
            if (request.IncludedEmployeeIds is null || request.AdjustmentIds is null || request.IncludedEmployeeIds.Count == 0)
                throw new SelectedPayRunConflictException("An exact employee/adjustment selection is required.");
            var tail = lockRows ? " FOR UPDATE" : "";
            // In the calculation transaction this range is read under Serializable,
            // preventing a newly Approved unselected adjustment from becoming a phantom.
            var applicable = (await connection.QueryAsync<PayrollAdjustment>(@"
SELECT * FROM payrolladjustments WHERE ClientId=@ClientId AND PayPeriod=@PayPeriod
AND EmployeeId IN @EmployeeIds AND Status='Approved' AND PayRunType='Regular' ORDER BY Id" + tail,
                new { request.ClientId, request.PayPeriod, EmployeeIds=request.IncludedEmployeeIds }, transaction)).ToList();
            count = applicable.Count;
            if (request.AdjustmentIds.Count > 0)
            {
                // Validate the complete raw selected-ID set before employee filtering.
                rows = (await connection.QueryAsync<PayrollAdjustment>(@"
SELECT * FROM payrolladjustments WHERE ClientId=@ClientId AND Id IN @Ids ORDER BY Id" + tail,
                    new { request.ClientId, Ids=request.AdjustmentIds }, transaction)).ToList();
                if (!request.AdjustmentIds.ToHashSet().SetEquals(rows.Select(a => a.Id)))
                    throw new SelectedPayRunConflictException("A selected adjustment is missing or belongs to another client.");
            }
            SelectedPayRunAdjustments.RequireExactIds(request, applicable);
            foreach (var adjustment in rows)
            {
                SelectedPayRunAdjustments.RequireUnbound(request, adjustment);
                var reference = SelectedPayRunAdjustments.Reference(adjustment);
                var imported = await connection.QuerySingleOrDefaultAsync<SelectedSourceImport>(@"
SELECT id Id,batch_id BatchId,CAST(request_json AS CHAR) RequestJson,CAST(result_json AS CHAR) ResultJson
FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND id=@ImportId AND batch_id=@BatchId AND status='Applied'" + tail,
                    new { request.ClientId, reference.ImportId, reference.BatchId }, transaction);
                if (imported is null) throw new SelectedPayRunConflictException("The exact Applied source import is unavailable; no adjustment is applied.");
                if(string.IsNullOrWhiteSpace(imported.RequestJson)||string.IsNullOrWhiteSpace(imported.ResultJson))
                    throw new SelectedPayRunConflictException("Saved adjustment receipt JSON is missing; review its source before payroll.");
                var batchJson = await connection.ExecuteScalarAsync<string?>(@"
SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@BatchId" + tail,
                    new { request.ClientId, reference.BatchId }, transaction);
                // Read the payload in the locking SELECT itself. A separate ordinary
                // read could reuse an earlier Repeatable Read snapshot after this lock.
                var sourceJson = await connection.ExecuteScalarAsync<string?>(@"
SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=@ClientId AND batch_id=@BatchId" + tail,
                    new { request.ClientId, reference.BatchId }, transaction);
                var source = string.IsNullOrWhiteSpace(sourceJson) ? null
                    : JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(sourceJson, ExcelPayslipRepository.JsonOptions);
                var batch = batchJson is null ? null : JsonSerializer.Deserialize<ExcelPayslipBatch>(batchJson, ExcelPayslipRepository.JsonOptions);
                var sourceRequest = JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(imported.RequestJson, ExcelPayslipRepository.JsonOptions);
                var receipt = JsonSerializer.Deserialize<ExcelPayrollSetupResult>(imported.ResultJson, ExcelPayslipRepository.JsonOptions);
                if (batch is null || source is null || sourceRequest is null || receipt is null)
                    throw new SelectedPayRunConflictException("Saved source/receipt metadata is missing; it cannot be reconstructed from assumptions.");
                // Compute before response-only employment annotation; never hash a mutated response.
                var receiptFingerprint=SelectedPayRunAdjustments.ReceiptFingerprint(imported.RequestJson, imported.ResultJson);
                if(sourceRequest.TransferReviewVersion==0)
                {
                    var appliedReview=await ReviewAppliedAdjustmentAsync(connection,transaction,request,adjustment,reference,batch,source,sourceRequest,receipt,receiptFingerprint);
                    appliedReviews.Add(appliedReview);reviews.Add(appliedReview.Review);
                }
                else reviews.Add(SelectedPayRunAdjustments.Review(request, adjustment, reference, batch, source, sourceRequest, receipt,
                    ExcelPayslipRepository.EmploymentSourceHash(batch, source),receiptFingerprint));
            }
            if (reviews.Select(SelectedPayRunAdjustments.BindingKey).Distinct(StringComparer.Ordinal).Count() != reviews.Count)
                throw new SelectedPayRunConflictException("Several approved adjustment IDs reference the same source component. Review duplicates before payroll.");
            if (rows.Count > 0)
            {
                var history = await connection.QueryAsync<PayrollAdjustment>(@"
SELECT * FROM payrolladjustments WHERE ClientId=@ClientId AND PayPeriod=@PayPeriod AND EmployeeId IN @EmployeeIds
AND ReasonCode='EXCEL_SETUP' AND (Status='Applied' OR PayRunId IS NOT NULL) ORDER BY Id" + tail,
                    new { request.ClientId, request.PayPeriod, EmployeeIds=request.IncludedEmployeeIds }, transaction);
                foreach (var prior in history)
                foreach (var reviewed in reviews.Where(r => r.EmployeeId == prior.EmployeeId && r.ComponentCode.Equals(prior.ComponentCode, StringComparison.OrdinalIgnoreCase)))
                {
                    var priorReference = SelectedPayRunAdjustments.Reference(prior);
                    if (priorReference.BatchId == reviewed.SourceBatchId && priorReference.SourceRow == reviewed.SourceRow)
                        throw new SelectedPayRunConflictException("This source component already has an Applied/bound adjustment, including another import. Existing history is retained and cannot be paid twice.");
                }
            }
            if (requireFrozen)
            {
                SelectedPayRunAdjustments.RequireFrozen(request, reviews);
                foreach(var review in reviews.Where(r=>r.SourceWitness is not null))
                    SelectedPayRunAppliedSourceReview.RequireStoredAttestation(request,review);
            }
        }
        catch (SelectedPayRunConflictException error) { issues.Add(error.Message); }
        catch (JsonException) { issues.Add("Saved adjustment source/receipt JSON is invalid; no adjustment is applied."); }
        return new() { Rows=rows, Reviews=reviews, Issues=issues, ApplicableCount=count, AppliedReviews=appliedReviews };
    }

    private static void RequireSelectedAdjustmentState(SelectedAdjustmentState state)
    {
        if (state.Issues.Count > 0) throw new SelectedPayRunConflictException(string.Join(" ",state.Issues));
    }

    private static async Task ApplySelectedAdjustmentsAsync(MySqlConnection connection, MySqlTransaction transaction,
        int payRunId, CreatePayRunRequest request, SelectedAdjustmentState state)
    {
        RequireSelectedAdjustmentState(state);
        SelectedPayRunAdjustments.RequireFrozen(request, state.Reviews);
        var affected = await connection.ExecuteAsync(@"
UPDATE payrolladjustments SET Status='Applied',PayRunId=@PayRunId
WHERE ClientId=@ClientId AND PayPeriod=@PayPeriod AND PayRunType='Regular'
AND Status='Approved' AND PayRunId IS NULL AND Id IN @Ids",
            new { PayRunId=payRunId,request.ClientId,request.PayPeriod,Ids=state.Rows.Select(a=>a.Id).ToArray() }, transaction);
        SelectedPayRunAdjustments.RequireAppliedCount(state.Rows.Count,affected);
    }
}
