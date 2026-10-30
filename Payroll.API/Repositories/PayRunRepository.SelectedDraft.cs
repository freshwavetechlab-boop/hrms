using System.Text.Json;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    // Unlike QueueAsync, this creates no globally claimable job. A separate exact-run
    // preview and fingerprint-confirmed process action are required afterward.
    public async Task<PayRun?> CreateSelectedDraftAsync(CreatePayRunRequest request, string performedBy, int performedByUserId=0)
    {
        SelectedPayRunDraft.RequireCreation(request, SelectedRunRuntime.Read(configuration));
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = request.AdjustmentIds.Count>0
            ? await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable) : await connection.BeginTransactionAsync();
        var client = await connection.QueryFirstOrDefaultAsync<Client>(
            "SELECT * FROM clients WHERE Id=@ClientId AND IsActive=TRUE FOR UPDATE", new { request.ClientId }, transaction);
        if (client is null) throw new SelectedPayRunConflictException("The selected client is missing or inactive. No draft was saved.");
        var running = await FindRunningRegularRunAsync(connection, transaction, request);
        if (running is not null) throw new PayRunAttemptConflictException(running.Id, running.Status);

        var groups = SelectedPayRunDraft.Groups(request);
        var validGroups = (await connection.QueryAsync<int>(@"
SELECT id FROM attendance_groups
WHERE client_id=@ClientId AND is_active=TRUE AND id IN @GroupIds
AND COALESCE(policy_batch_id,'')=@PolicyBatchId FOR UPDATE",
            new { request.ClientId, GroupIds = groups, PolicyBatchId = request.AttendancePolicyBatchId.Trim() }, transaction)).ToHashSet();
        if (!validGroups.SetEquals(groups)) throw new SelectedPayRunConflictException("The attendance policy groups no longer belong to this active client and policy batch. Refresh before creating a draft.");
        var policyMembers = (await connection.QueryAsync<int>(@"
SELECT DISTINCT m.employee_id FROM attendance_group_employees m
JOIN attendance_groups g ON g.id=m.attendance_group_id AND g.client_id=@ClientId AND g.is_active=TRUE
WHERE g.id IN @GroupIds AND COALESCE(g.policy_batch_id,'')=@PolicyBatchId FOR UPDATE",
            new { request.ClientId, GroupIds = groups, PolicyBatchId = request.AttendancePolicyBatchId.Trim() }, transaction)).ToHashSet();
        var eligible = (await connection.QueryAsync<int>("SELECT e.Id FROM employees e WHERE e.ClientId=@ClientId AND e.Id IN @EmployeeIds AND "
            + EmployeeEmploymentRules.EligibleInMonth("e", "@PayPeriod") + " FOR UPDATE",
            new { request.ClientId, request.PayPeriod, EmployeeIds = request.IncludedEmployeeIds }, transaction)).ToHashSet();
        var covered = (await connection.QueryAsync<int>(@"
SELECT DISTINCT e.EmployeeId FROM payrunemployees e JOIN payruns r ON r.Id=e.PayRunId
WHERE r.ClientId=@ClientId AND r.PayPeriod=@PayPeriod AND r.RunType='Regular'
AND r.Status IN ('Draft','Pending Approval','Approved','Partially Paid','Paid')
AND e.IsSkipped=FALSE AND e.EmployeeId IN @EmployeeIds FOR UPDATE",
            new { request.ClientId, request.PayPeriod, EmployeeIds = request.IncludedEmployeeIds }, transaction)).ToArray();
        var adjustments = await ReadSelectedAdjustmentsAsync(connection,transaction,request,true,false);
        RequireSelectedAdjustmentState(adjustments);
        foreach(var review in adjustments.Reviews.Where(r=>r.SourceWitness is not null))
        {
            SelectedPayRunAppliedSourceReview.RequireAcknowledged(request,review,performedByUserId);
            request.SourceReviewAttestation=new(performedByUserId,DateTime.UtcNow,review.SourceWitness!.WitnessFingerprint);
        }
        if(request.AdjustmentIds.Count>0) request.SelectedAdjustmentReviews=adjustments.Reviews;
        SelectedPayRunDraft.RequireExactSelection(request, eligible, policyMembers, covered, adjustments.ApplicableCount,true);
        request.AttendancePolicyBatchId = request.AttendancePolicyBatchId.Trim();
        var runCode = RegularRunCode(request);
        var existing = await FindExistingRegularRunAsync(connection, transaction, request, runCode);
        if (existing is not null) throw new PayRunAttemptConflictException(existing.Id, existing.Status);
        var requestJson = JsonSerializer.Serialize(request);
        var payRunId = (int)await connection.ExecuteScalarAsync<long>(@"
INSERT INTO payruns (ClientId,ClientName,PayPeriod,RunCode,AttendancePolicyBatchId,RunType,RunName,Reason,PayDate,TotalWorkingDays,Status,RequestJson,ProcessingError)
VALUES (@ClientId,@ClientName,@PayPeriod,@RunCode,@AttendancePolicyBatchId,'Regular',@RunName,@Reason,@PayDate,@TotalWorkingDays,@Status,@RequestJson,'');
SELECT LAST_INSERT_ID();", new { request.ClientId, ClientName=client.Name, request.PayPeriod, RunCode=runCode,
            request.AttendancePolicyBatchId, RunName=string.IsNullOrWhiteSpace(request.RunName)?"Selected regular payroll":request.RunName.Trim(),
            Reason=request.Reason?.Trim()??"", PayDate=request.PayDate.ToDateTime(TimeOnly.MinValue),request.TotalWorkingDays,
            Status=SelectedPayRunDraft.HeldStatus,RequestJson=requestJson }, transaction);
        await WritePipelineStepLogsAsync(connection, transaction, payRunId, performedBy, []);
        await transaction.CommitAsync();
        return await GetAsync(payRunId);
    }
}
