using Dapper;
using Microsoft.AspNetCore.Http;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace Payroll.API.Repositories;

public sealed class RecruitmentCaseRepository(
    IConfiguration configuration,
    AttachmentRepository attachments,
    TemplatePdfService templatePdf,
    WorkflowRepository workflows)
{
    private static readonly HashSet<string> WorkOrderStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Draft", "Active", "On Hold", "Completed", "Cancelled"
    };

    private static readonly HashSet<string> ProcessDocumentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "WORK_ORDER", "JD_ANNEXURE", "MOM", "SIGNED_MOM", "SCORE_ANNEXURE", "HR_PROPOSAL", "JOINING_INTIMATION", "CANDIDATE_PACK"
    };
    private static readonly HashSet<string> ProcessDocumentStatuses = new(StringComparer.OrdinalIgnoreCase) { "Draft", "Prepared", "Signed" };

    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));

    public async Task InitializeAsync()
    {
        await using var db = Db();
        await db.OpenAsync();
        await db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS recruitment_work_orders (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL,
    WorkOrderNumber VARCHAR(120) NOT NULL,
    ReceivedAtUtc DATETIME(6) NOT NULL,
    ReceivedFrom VARCHAR(190) NOT NULL DEFAULT '',
    Subject VARCHAR(300) NOT NULL DEFAULT '',
    Remarks TEXT NULL,
    Status VARCHAR(40) NOT NULL DEFAULT 'Draft',
    OverallSlaMinutes INT NOT NULL,
    DueAtUtc DATETIME(6) NULL,
    CreatedByUserId INT NOT NULL,
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    UNIQUE KEY UX_recruitment_work_order_number (ClientId,WorkOrderNumber),
    INDEX IX_recruitment_work_order_status (ClientId,Status,ReceivedAtUtc)
);
CREATE TABLE IF NOT EXISTS recruitment_work_order_lines (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    WorkOrderId BIGINT NOT NULL,
    LineNumber INT NOT NULL,
    PositionName VARCHAR(180) NOT NULL,
    PayBandLevelCode VARCHAR(80) NOT NULL DEFAULT '',
    NumberOfPositions INT NOT NULL,
    Location VARCHAR(180) NOT NULL DEFAULT '',
    Division VARCHAR(180) NOT NULL DEFAULT '',
    RequisitionId BIGINT NULL,
    PositionId BIGINT NULL,
    Status VARCHAR(40) NOT NULL DEFAULT 'Open',
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    UNIQUE KEY UX_recruitment_work_order_line_number (WorkOrderId,LineNumber),
    UNIQUE KEY UX_recruitment_work_order_line_requisition (RequisitionId),
    UNIQUE KEY UX_recruitment_work_order_line_position (PositionId),
    INDEX IX_recruitment_work_order_line_status (WorkOrderId,Status)
);
CREATE TABLE IF NOT EXISTS recruitment_position_pipeline_instances (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL,
    WorkOrderId BIGINT NOT NULL,
    WorkOrderLineId BIGINT NOT NULL,
    RequisitionId BIGINT NULL,
    PositionId BIGINT NULL,
    PipelineVersionId BIGINT NOT NULL,
    SlaAnchorAtUtc DATETIME(6) NOT NULL,
    OverallDueAtUtc DATETIME(6) NULL,
    CurrentStageInstanceId BIGINT NULL,
    Status VARCHAR(40) NOT NULL DEFAULT 'Active',
    StartedByUserId INT NOT NULL,
    StartedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    CompletedAtUtc DATETIME(6) NULL,
    UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    UNIQUE KEY UX_recruitment_position_pipeline_line (WorkOrderLineId),
    INDEX IX_recruitment_position_pipeline_status (ClientId,Status,OverallDueAtUtc),
    INDEX IX_recruitment_position_pipeline_version (PipelineVersionId)
);
CREATE TABLE IF NOT EXISTS recruitment_position_stage_instances (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    PositionPipelineInstanceId BIGINT NOT NULL,
    PipelineStageId BIGINT NOT NULL,
    Status VARCHAR(40) NOT NULL DEFAULT 'Pending',
    OutcomeCode VARCHAR(80) NOT NULL DEFAULT '',
    EnteredAtUtc DATETIME(6) NULL,
    DueAtUtc DATETIME(6) NULL,
    CompletedAtUtc DATETIME(6) NULL,
    PausedDurationSeconds BIGINT NOT NULL DEFAULT 0,
    UNIQUE KEY UX_recruitment_position_stage (PositionPipelineInstanceId,PipelineStageId),
    INDEX IX_recruitment_position_stage_status (PositionPipelineInstanceId,Status,DueAtUtc)
);
CREATE TABLE IF NOT EXISTS recruitment_position_stage_pause_periods (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    PositionStageInstanceId BIGINT NOT NULL,
    Reason VARCHAR(1000) NOT NULL,
    PausedByUserId INT NOT NULL,
    PausedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    ResumedByUserId INT NULL,
    ResumedAtUtc DATETIME(6) NULL,
    DurationSeconds BIGINT NOT NULL DEFAULT 0,
    INDEX IX_recruitment_position_pause_stage (PositionStageInstanceId,PausedAtUtc)
);
CREATE TABLE IF NOT EXISTS recruitment_position_stage_events (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    PositionPipelineInstanceId BIGINT NOT NULL,
    PositionStageInstanceId BIGINT NULL,
    EventType VARCHAR(80) NOT NULL,
    EventTitle VARCHAR(220) NOT NULL,
    EventDetails VARCHAR(2000) NOT NULL DEFAULT '',
    ActorUserId INT NULL,
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    INDEX IX_recruitment_position_event (PositionPipelineInstanceId,CreatedAtUtc)
);
CREATE TABLE IF NOT EXISTS recruitment_hiring_case_advance_requests (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    HiringCaseId BIGINT NOT NULL,
    PositionStageInstanceId BIGINT NOT NULL,
    PipelineStageId BIGINT NOT NULL,
    OutcomeCode VARCHAR(80) NOT NULL DEFAULT 'ADVANCE',
    Reason VARCHAR(1000) NOT NULL DEFAULT '',
    Status VARCHAR(40) NOT NULL DEFAULT 'Pending Approval',
    WorkflowInstanceId BIGINT NULL,
    RequestedByUserId INT NOT NULL,
    RequestedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    DecidedByUserId INT NULL,
    DecidedAtUtc DATETIME(6) NULL,
    AppliedAtUtc DATETIME(6) NULL,
    INDEX IX_recruitment_hiring_case_advance_stage (HiringCaseId,PositionStageInstanceId,Status),
    INDEX IX_recruitment_hiring_case_advance_workflow (WorkflowInstanceId)
);
CREATE TABLE IF NOT EXISTS recruitment_stage_default_panel_members (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    PipelineStageId BIGINT NOT NULL,
    PanelUserId INT NOT NULL,
    PanelRole VARCHAR(80) NOT NULL DEFAULT 'Panelist',
    IsRequired BOOLEAN NOT NULL DEFAULT TRUE,
    DisplayOrder INT NOT NULL DEFAULT 100,
    UNIQUE KEY UX_recruitment_stage_default_panel (PipelineStageId,PanelUserId),
    INDEX IX_recruitment_stage_default_panel_order (PipelineStageId,DisplayOrder)
);
CREATE TABLE IF NOT EXISTS recruitment_stage_action_recipients (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    StageActionId BIGINT NOT NULL,
    RecipientType VARCHAR(60) NOT NULL,
    UserId INT NULL,
    RoleCode VARCHAR(100) NOT NULL DEFAULT '',
    EmailAddress VARCHAR(190) NOT NULL DEFAULT '',
    DisplayOrder INT NOT NULL DEFAULT 100,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    INDEX IX_recruitment_stage_action_recipient (StageActionId,IsActive,DisplayOrder)
);
CREATE TABLE IF NOT EXISTS recruitment_stage_action_notification_deliveries (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    StageActionExecutionId BIGINT NOT NULL,
    RecipientType VARCHAR(60) NOT NULL,
    RecipientEmail VARCHAR(190) NOT NULL,
    NotificationQueueId BIGINT NOT NULL,
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY UX_recruitment_stage_action_delivery (StageActionExecutionId,RecipientEmail),
    INDEX IX_recruitment_stage_action_delivery_queue (NotificationQueueId)
);
CREATE TABLE IF NOT EXISTS recruitment_process_documents (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL,
    HiringCaseId BIGINT NULL,
    ApplicationId BIGINT NULL,
    InterviewId BIGINT NULL,
    PipelineStageId BIGINT NULL,
    DocumentType VARCHAR(80) NOT NULL,
    VersionNumber INT NOT NULL,
    TemplateId BIGINT NULL,
    AttachmentPublicId CHAR(36) NULL,
    Status VARCHAR(40) NOT NULL DEFAULT 'Draft',
    WorkflowInstanceId BIGINT NULL,
    CreatedByUserId INT NOT NULL,
    SignedByUserId INT NULL,
    SignedAtUtc DATETIME(6) NULL,
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    UNIQUE KEY UX_recruitment_process_document_version (ClientId,HiringCaseId,ApplicationId,InterviewId,DocumentType,VersionNumber),
    INDEX IX_recruitment_process_document_resource (ClientId,HiringCaseId,ApplicationId,DocumentType,Status)
);
CREATE TABLE IF NOT EXISTS recruitment_stage_process_document_requirements (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    PipelineStageId BIGINT NOT NULL,
    DocumentType VARCHAR(60) NOT NULL,
    TemplateId BIGINT NULL,
    IsRequired BOOLEAN NOT NULL DEFAULT TRUE,
    RequiresSignature BOOLEAN NOT NULL DEFAULT FALSE,
    DisplayOrder INT NOT NULL DEFAULT 100,
    UNIQUE KEY UX_recruitment_stage_process_document (PipelineStageId,DocumentType),
    INDEX IX_recruitment_stage_process_document_order (PipelineStageId,DisplayOrder),
    CONSTRAINT FK_recruitment_stage_process_document_stage FOREIGN KEY (PipelineStageId) REFERENCES recruitment_pipeline_stages(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS recruitment_profile_submission_batches (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL,
    HiringCaseId BIGINT NOT NULL,
    BatchNumber VARCHAR(100) NOT NULL,
    Status VARCHAR(40) NOT NULL DEFAULT 'Draft',
    CreatedByUserId INT NOT NULL,
    ApprovedByUserId INT NULL,
    ApprovedAtUtc DATETIME(6) NULL,
    ForwardedByUserId INT NULL,
    ForwardedAtUtc DATETIME(6) NULL,
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    UNIQUE KEY UX_recruitment_profile_batch_number (ClientId,BatchNumber),
    INDEX IX_recruitment_profile_batch_case (HiringCaseId,Status)
);
CREATE TABLE IF NOT EXISTS recruitment_profile_submission_batch_items (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    BatchId BIGINT NOT NULL,
    ApplicationId BIGINT NOT NULL,
    CandidateId BIGINT NOT NULL,
    ApplicationScoreId BIGINT NULL,
    ReadinessStatus VARCHAR(40) NOT NULL DEFAULT 'Pending',
    AddedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY UX_recruitment_profile_batch_item (BatchId,ApplicationId),
    INDEX IX_recruitment_profile_batch_candidate (CandidateId)
);
CREATE TABLE IF NOT EXISTS recruitment_profile_batch_notification_deliveries (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    BatchId BIGINT NOT NULL,
    StageActionId BIGINT NOT NULL,
    RecipientType VARCHAR(60) NOT NULL,
    RecipientEmail VARCHAR(190) NOT NULL,
    NotificationQueueId BIGINT NOT NULL,
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY UX_recruitment_profile_batch_delivery (BatchId,StageActionId,RecipientEmail),
    INDEX IX_recruitment_profile_batch_delivery_queue (NotificationQueueId)
);
");

        await EnsureColumnAsync(db, "recruitment_requisitions", "WorkOrderId", "BIGINT NULL");
        await EnsureColumnAsync(db, "recruitment_requisitions", "WorkOrderLineNumber", "INT NULL");
        await EnsureColumnAsync(db, "recruitment_requisitions", "PayBandLevelCode", "VARCHAR(80) NOT NULL DEFAULT ''");
        await EnsureColumnAsync(db, "recruitment_requisitions", "Division", "VARCHAR(180) NOT NULL DEFAULT ''");
        await EnsureColumnAsync(db, "recruitment_pipeline_versions", "ScopeType", "VARCHAR(40) NOT NULL DEFAULT 'Application'");
        await EnsureColumnAsync(db, "recruitment_pipeline_versions", "SlaMode", "VARCHAR(40) NOT NULL DEFAULT 'StageEntry'");
        await EnsureColumnAsync(db, "recruitment_pipeline_versions", "OverallSlaMinutes", "INT NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "recruitment_pipeline_stages", "TargetOffsetMinutes", "INT NULL");
        await EnsureColumnAsync(db, "recruitment_pipeline_stages", "StakeholderCode", "VARCHAR(80) NOT NULL DEFAULT ''");
        await EnsureColumnAsync(db, "recruitment_pipeline_stages", "AllowPause", "BOOLEAN NOT NULL DEFAULT TRUE");
        await EnsureColumnAsync(db, "recruitment_pipeline_stages", "PauseBehavior", "VARCHAR(40) NOT NULL DEFAULT 'ShiftStageAndOverall'");
        await EnsureColumnAsync(db, "recruitment_profile_submission_batches", "ForwardedByUserId", "INT NULL");

        var candidateTable = await TableExistsAsync(db, "recruitment_candidates");
        if (candidateTable)
        {
            await db.ExecuteAsync(@"ALTER TABLE recruitment_candidates MODIFY COLUMN NoticePeriodDays INT NULL;
ALTER TABLE recruitment_candidates MODIFY COLUMN CurrentCtc DECIMAL(18,2) NULL;
ALTER TABLE recruitment_candidates MODIFY COLUMN ExpectedCtc DECIMAL(18,2) NULL;");
        }
    }

    public async Task<IReadOnlyList<RecruitmentWorkOrder>> ListWorkOrdersAsync(AuthUser user, int? clientId, string query = "")
    {
        await using var db = Db();
        await db.OpenAsync();
        var effectiveClientId = user.ClientId ?? clientId;
        return (await db.QueryAsync<RecruitmentWorkOrder>(@"SELECT workOrder.*,client.Name ClientName,
(SELECT COUNT(*) FROM recruitment_work_order_lines line WHERE line.WorkOrderId=workOrder.Id) LineCount,
(SELECT COUNT(*) FROM recruitment_position_pipeline_instances hiringCase WHERE hiringCase.WorkOrderId=workOrder.Id AND hiringCase.Status='Active') OpenCaseCount
FROM recruitment_work_orders workOrder
JOIN clients client ON client.Id=workOrder.ClientId
WHERE (@ClientId IS NULL OR workOrder.ClientId=@ClientId)
AND (@Query='' OR workOrder.WorkOrderNumber LIKE CONCAT('%',@Query,'%') OR workOrder.Subject LIKE CONCAT('%',@Query,'%'))
ORDER BY workOrder.ReceivedAtUtc DESC,workOrder.Id DESC", new { ClientId = effectiveClientId, Query = (query ?? "").Trim() })).ToList();
    }

    public async Task<RecruitmentWorkOrder?> GetWorkOrderAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var row = await db.QueryFirstOrDefaultAsync<RecruitmentWorkOrder>(@"SELECT workOrder.*,client.Name ClientName,
(SELECT COUNT(*) FROM recruitment_work_order_lines line WHERE line.WorkOrderId=workOrder.Id) LineCount,
(SELECT COUNT(*) FROM recruitment_position_pipeline_instances hiringCase WHERE hiringCase.WorkOrderId=workOrder.Id AND hiringCase.Status='Active') OpenCaseCount
FROM recruitment_work_orders workOrder JOIN clients client ON client.Id=workOrder.ClientId
WHERE workOrder.Id=@Id AND (@ClientId IS NULL OR workOrder.ClientId=@ClientId)", new { Id = id, user.ClientId });
        if (row is null) return null;
        row.Lines = (await db.QueryAsync<RecruitmentWorkOrderLine>("SELECT * FROM recruitment_work_order_lines WHERE WorkOrderId=@Id ORDER BY LineNumber,Id", new { Id = id })).ToList();
        return row;
    }

    public async Task<(RecruitmentWorkOrder? Row, string Error)> SaveWorkOrderAsync(SaveRecruitmentWorkOrder request, AuthUser user)
    {
        request.Lines ??= [];
        request.WorkOrderNumber = (request.WorkOrderNumber ?? "").Trim();
        request.ReceivedFrom = (request.ReceivedFrom ?? "").Trim();
        request.Subject = (request.Subject ?? "").Trim();
        request.Remarks = (request.Remarks ?? "").Trim();
        request.Status = Canonical(WorkOrderStatuses, request.Status, "Draft");
        if (request.ClientId <= 0) return (null, "Client is required.");
        if (user.ClientId.HasValue && user.ClientId.Value != request.ClientId) return (null, "The selected client is outside your access.");
        if (request.WorkOrderNumber.Length == 0) return (null, "Work order number is required.");
        if (request.ReceivedAtUtc == default) return (null, "Work order received date is required.");
        if (request.OverallSlaMinutes < 0) return (null, "Overall SLA cannot be negative.");
        if (request.Lines.Count == 0) return (null, "Add at least one work order position.");
        var duplicateLine = request.Lines.GroupBy(row => row.LineNumber).FirstOrDefault(group => group.Key <= 0 || group.Count() > 1);
        if (duplicateLine is not null) return (null, "Every work order line needs a unique positive line number.");
        if (request.Lines.Any(row => string.IsNullOrWhiteSpace(row.PositionName) || row.NumberOfPositions <= 0))
            return (null, "Every work order line needs a position name and at least one position.");

        await using var db = Db();
        await db.OpenAsync();
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM clients WHERE Id=@Id", new { Id = request.ClientId }) == 0)
            return (null, "Client was not found.");
        var duplicate = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_work_orders
WHERE ClientId=@ClientId AND WorkOrderNumber=@WorkOrderNumber AND Id<>@Id", new { request.ClientId, request.WorkOrderNumber, request.Id });
        if (duplicate > 0) return (null, "This work order number already exists for the client.");
        var linkedPositionIds = request.Lines.Where(line => line.PositionId is > 0).Select(line => line.PositionId!.Value).ToArray();
        if (linkedPositionIds.Distinct().Count() != linkedPositionIds.Length) return (null, "An open position can be linked to only one work-order line.");
        if (linkedPositionIds.Length > 0)
        {
            var validPositions = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_open_positions positionRow
WHERE positionRow.Id IN @Ids AND positionRow.ClientId=@ClientId", new { Ids = linkedPositionIds, request.ClientId });
            if (validPositions != linkedPositionIds.Length) return (null, "One or more linked open positions are outside this work-order client.");
            var mismatchedRequisition = request.Lines.Any(line => line.PositionId is > 0 && (line.RequisitionId is null or <= 0));
            if (mismatchedRequisition) return (null, "Every linked position must retain its requisition reference.");
            foreach (var line in request.Lines.Where(line => line.PositionId is > 0))
            {
                var requisitionId = await db.ExecuteScalarAsync<long?>("SELECT RequisitionId FROM recruitment_open_positions WHERE Id=@Id", new { Id = line.PositionId });
                if (requisitionId != line.RequisitionId) return (null, "A linked position and requisition do not match.");
            }
        }

        await using var transaction = await db.BeginTransactionAsync();
        long id;
        var dueAt = request.OverallSlaMinutes > 0 ? request.ReceivedAtUtc.AddMinutes(request.OverallSlaMinutes) : (DateTime?)null;
        if (request.Id <= 0)
        {
            id = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_work_orders
(ClientId,WorkOrderNumber,ReceivedAtUtc,ReceivedFrom,Subject,Remarks,Status,OverallSlaMinutes,DueAtUtc,CreatedByUserId)
VALUES (@ClientId,@WorkOrderNumber,@ReceivedAtUtc,@ReceivedFrom,@Subject,@Remarks,@Status,@OverallSlaMinutes,@DueAtUtc,@UserId);
SELECT LAST_INSERT_ID();", new { request.ClientId, request.WorkOrderNumber, request.ReceivedAtUtc, request.ReceivedFrom, request.Subject, request.Remarks, request.Status, request.OverallSlaMinutes, DueAtUtc = dueAt, UserId = user.Id }, transaction);
        }
        else
        {
            var editable = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_work_orders
WHERE Id=@Id AND (@ClientId IS NULL OR ClientId=@ClientId)", new { request.Id, user.ClientId }, transaction);
            if (editable == 0) return (null, "Work order was not found.");
            id = request.Id;
            await db.ExecuteAsync(@"UPDATE recruitment_work_orders SET ClientId=@ClientId,WorkOrderNumber=@WorkOrderNumber,
ReceivedAtUtc=@ReceivedAtUtc,ReceivedFrom=@ReceivedFrom,Subject=@Subject,Remarks=@Remarks,Status=@Status,
OverallSlaMinutes=@OverallSlaMinutes,DueAtUtc=@DueAtUtc WHERE Id=@Id", new { Id = id, request.ClientId, request.WorkOrderNumber, request.ReceivedAtUtc, request.ReceivedFrom, request.Subject, request.Remarks, request.Status, request.OverallSlaMinutes, DueAtUtc = dueAt }, transaction);
        }

        var existingIds = (await db.QueryAsync<long>("SELECT Id FROM recruitment_work_order_lines WHERE WorkOrderId=@Id", new { Id = id }, transaction)).ToHashSet();
        foreach (var line in request.Lines)
        {
            if (line.Id > 0 && !existingIds.Remove(line.Id)) return (null, "A work order line does not belong to this work order.");
            var args = new
            {
                line.Id,
                WorkOrderId = id,
                line.LineNumber,
                PositionName = line.PositionName.Trim(),
                PayBandLevelCode = (line.PayBandLevelCode ?? "").Trim(),
                line.NumberOfPositions,
                Location = (line.Location ?? "").Trim(),
                Division = (line.Division ?? "").Trim(),
                line.RequisitionId,
                line.PositionId,
                Status = string.IsNullOrWhiteSpace(line.Status) ? "Open" : line.Status.Trim()
            };
            if (line.Id <= 0)
                await db.ExecuteAsync(@"INSERT INTO recruitment_work_order_lines
(WorkOrderId,LineNumber,PositionName,PayBandLevelCode,NumberOfPositions,Location,Division,RequisitionId,PositionId,Status)
VALUES (@WorkOrderId,@LineNumber,@PositionName,@PayBandLevelCode,@NumberOfPositions,@Location,@Division,@RequisitionId,@PositionId,@Status)", args, transaction);
            else
                await db.ExecuteAsync(@"UPDATE recruitment_work_order_lines SET LineNumber=@LineNumber,PositionName=@PositionName,
PayBandLevelCode=@PayBandLevelCode,NumberOfPositions=@NumberOfPositions,Location=@Location,Division=@Division,
RequisitionId=@RequisitionId,PositionId=@PositionId,Status=@Status WHERE Id=@Id AND WorkOrderId=@WorkOrderId", args, transaction);
            if (line.Id > 0)
            {
                await db.ExecuteAsync(@"UPDATE recruitment_position_pipeline_instances SET RequisitionId=@RequisitionId,PositionId=@PositionId
WHERE WorkOrderLineId=@LineId", new { line.RequisitionId, line.PositionId, LineId = line.Id }, transaction);
                if (line.PositionId is > 0)
                {
                    var linkedCase = await db.QueryFirstOrDefaultAsync<(long PipelineVersionId, int StartedByUserId)>(@"SELECT PipelineVersionId,StartedByUserId
FROM recruitment_position_pipeline_instances WHERE WorkOrderLineId=@LineId LIMIT 1", new { LineId = line.Id }, transaction);
                    if (linkedCase != default) await AssignPositionPipelineAsync(db, line.PositionId.Value, linkedCase.PipelineVersionId, linkedCase.StartedByUserId, transaction);
                }
            }
        }
        if (existingIds.Count > 0)
        {
            var inUse = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_position_pipeline_instances WHERE WorkOrderLineId IN @Ids", new { Ids = existingIds.ToArray() }, transaction);
            if (inUse > 0) return (null, "A line with an active hiring case cannot be removed.");
            await db.ExecuteAsync("DELETE FROM recruitment_work_order_lines WHERE Id IN @Ids", new { Ids = existingIds.ToArray() }, transaction);
        }
        await transaction.CommitAsync();
        return (await GetWorkOrderAsync(id, user), "");
    }

    public async Task<(bool Ok, string Error)> DeleteWorkOrderAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var row = await db.QueryFirstOrDefaultAsync<(long Id, int ClientId, string WorkOrderNumber)>(
            "SELECT Id,ClientId,WorkOrderNumber FROM recruitment_work_orders WHERE Id=@Id AND (@ClientId IS NULL OR ClientId=@ClientId)",
            new { Id = id, user.ClientId });
        if (row.Id <= 0) return (false, "Work order was not found in your permitted client scope.");

        var activeCases = await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM recruitment_position_pipeline_instances WHERE WorkOrderId=@Id", new { Id = id });
        if (activeCases > 0)
            return (false, $"Delete the {activeCases} linked live cumulative pipeline case(s) first.");

        await using var transaction = await db.BeginTransactionAsync();
        await db.ExecuteAsync("UPDATE recruitment_requisitions SET WorkOrderId=NULL,WorkOrderLineNumber=NULL WHERE WorkOrderId=@Id", new { Id = id }, transaction);
        await db.ExecuteAsync("DELETE FROM recruitment_work_order_lines WHERE WorkOrderId=@Id", new { Id = id }, transaction);
        await db.ExecuteAsync("DELETE FROM recruitment_work_orders WHERE Id=@Id", new { Id = id }, transaction);
        await transaction.CommitAsync();
        return (true, "");
    }

    public async Task<(bool Ok, string Error)> DeleteHiringCaseAsync(
        long id,
        AuthUser user,
        string ipAddress,
        string userAgent)
    {
        await using var db = Db();
        await db.OpenAsync();
        var row = await db.QueryFirstOrDefaultAsync<(long Id, int ClientId, long WorkOrderLineId)>(
            "SELECT Id,ClientId,WorkOrderLineId FROM recruitment_position_pipeline_instances WHERE Id=@Id AND (@ClientId IS NULL OR ClientId=@ClientId)",
            new { Id = id, user.ClientId });
        if (row.Id <= 0) return (false, "Hiring pipeline case was not found in your permitted client scope.");

        var attachmentIds = (await db.QueryAsync<string>(@"SELECT CAST(AttachmentPublicId AS CHAR)
FROM recruitment_process_documents
WHERE HiringCaseId=@Id AND AttachmentPublicId IS NOT NULL", new { Id = id })).ToList();
        foreach (var value in attachmentIds)
        {
            if (!Guid.TryParse(value, out var publicId)) continue;
            var (deleted, error) = await attachments.DeleteAsync(publicId, user, ipAddress, userAgent);
            if (!deleted) return (false, error ?? "A generated hiring document could not be safely deleted.");
        }

        await using var transaction = await db.BeginTransactionAsync();
        var batchIds = (await db.QueryAsync<long>("SELECT Id FROM recruitment_profile_submission_batches WHERE HiringCaseId=@Id", new { Id = id }, transaction)).ToArray();
        if (batchIds.Length > 0)
        {
            await db.ExecuteAsync("DELETE FROM recruitment_profile_batch_notification_deliveries WHERE BatchId IN @Ids", new { Ids = batchIds }, transaction);
            await db.ExecuteAsync("DELETE FROM recruitment_profile_submission_batch_items WHERE BatchId IN @Ids", new { Ids = batchIds }, transaction);
            await db.ExecuteAsync("DELETE FROM recruitment_profile_submission_batches WHERE Id IN @Ids", new { Ids = batchIds }, transaction);
        }
        var stageIds = (await db.QueryAsync<long>("SELECT Id FROM recruitment_position_stage_instances WHERE PositionPipelineInstanceId=@Id", new { Id = id }, transaction)).ToArray();
        if (stageIds.Length > 0)
            await db.ExecuteAsync("DELETE FROM recruitment_position_stage_pause_periods WHERE PositionStageInstanceId IN @Ids", new { Ids = stageIds }, transaction);
        await db.ExecuteAsync("DELETE FROM recruitment_hiring_case_advance_requests WHERE HiringCaseId=@Id", new { Id = id }, transaction);
        await db.ExecuteAsync("DELETE FROM recruitment_position_stage_events WHERE PositionPipelineInstanceId=@Id", new { Id = id }, transaction);
        await db.ExecuteAsync("DELETE FROM recruitment_process_documents WHERE HiringCaseId=@Id", new { Id = id }, transaction);
        await db.ExecuteAsync("UPDATE recruitment_position_pipeline_instances SET CurrentStageInstanceId=NULL WHERE Id=@Id", new { Id = id }, transaction);
        await db.ExecuteAsync("DELETE FROM recruitment_position_stage_instances WHERE PositionPipelineInstanceId=@Id", new { Id = id }, transaction);
        await db.ExecuteAsync("DELETE FROM recruitment_position_pipeline_instances WHERE Id=@Id", new { Id = id }, transaction);
        await db.ExecuteAsync("UPDATE recruitment_work_order_lines SET Status='Open' WHERE Id=@LineId", new { LineId = row.WorkOrderLineId }, transaction);
        await transaction.CommitAsync();
        return (true, "");
    }

    public async Task<(RecruitmentHiringCase? Row, string Error)> StartHiringCaseAsync(StartRecruitmentHiringCaseRequest request, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var source = await db.QueryFirstOrDefaultAsync<StartCaseSource>(@"SELECT line.Id WorkOrderLineId,line.WorkOrderId,line.RequisitionId,line.PositionId,
workOrder.ClientId,workOrder.ReceivedAtUtc,workOrder.OverallSlaMinutes,version.Id PipelineVersionId,version.Status PipelineStatus,
version.ScopeType,version.SlaMode,version.OverallSlaMinutes PipelineOverallSlaMinutes
FROM recruitment_work_order_lines line
JOIN recruitment_work_orders workOrder ON workOrder.Id=line.WorkOrderId
CROSS JOIN recruitment_pipeline_versions version
JOIN recruitment_pipeline_definitions definition ON definition.Id=version.PipelineDefinitionId AND definition.ClientId=workOrder.ClientId
WHERE line.Id=@WorkOrderLineId AND version.Id=@PipelineVersionId AND (@ClientId IS NULL OR workOrder.ClientId=@ClientId)", new { request.WorkOrderLineId, request.PipelineVersionId, user.ClientId });
        if (source is null) return (null, "Work order line or pipeline version was not found for this client.");
        if (!source.PipelineStatus.Equals("Published", StringComparison.OrdinalIgnoreCase)) return (null, "Publish the pipeline version before starting a hiring case.");
        if (source.ScopeType.Equals("Application", StringComparison.OrdinalIgnoreCase)) return (null, "Select a Position or Hybrid pipeline for the work-order hiring case.");
        var stages = (await db.QueryAsync<StageDefinition>(@"SELECT Id,StageCode,StageName,DisplayOrder,TargetOffsetMinutes,IsInitial,IsTerminal
FROM recruitment_pipeline_stages WHERE PipelineVersionId=@Id AND IsActive=TRUE ORDER BY DisplayOrder,Id", new { Id = request.PipelineVersionId })).ToList();
        var initial = stages.SingleOrDefault(row => row.IsInitial);
        if (initial is null) return (null, "The pipeline needs exactly one active initial stage.");

        await using var transaction = await db.BeginTransactionAsync();
        var overallMinutes = source.PipelineOverallSlaMinutes > 0 ? source.PipelineOverallSlaMinutes : source.OverallSlaMinutes;
        var overallDue = overallMinutes > 0 ? source.ReceivedAtUtc.AddMinutes(overallMinutes) : (DateTime?)null;
        var caseId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_position_pipeline_instances
(ClientId,WorkOrderId,WorkOrderLineId,RequisitionId,PositionId,PipelineVersionId,SlaAnchorAtUtc,OverallDueAtUtc,Status,StartedByUserId)
VALUES (@ClientId,@WorkOrderId,@WorkOrderLineId,@RequisitionId,@PositionId,@PipelineVersionId,@ReceivedAtUtc,@OverallDueAtUtc,'Active',@UserId);
SELECT LAST_INSERT_ID();", new { source.ClientId, source.WorkOrderId, source.WorkOrderLineId, source.RequisitionId, source.PositionId, source.PipelineVersionId, source.ReceivedAtUtc, OverallDueAtUtc = overallDue, UserId = user.Id }, transaction);
        long initialInstanceId = 0;
        foreach (var stage in stages)
        {
            var active = stage.Id == initial.Id;
            var dueAt = stage.TargetOffsetMinutes.HasValue ? source.ReceivedAtUtc.AddMinutes(stage.TargetOffsetMinutes.Value) : (DateTime?)null;
            var stageId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_position_stage_instances
(PositionPipelineInstanceId,PipelineStageId,Status,EnteredAtUtc,DueAtUtc)
VALUES (@CaseId,@StageId,@Status,@EnteredAtUtc,@DueAtUtc);SELECT LAST_INSERT_ID();", new { CaseId = caseId, StageId = stage.Id, Status = active ? "Active" : "Pending", EnteredAtUtc = active ? DateTime.UtcNow : (DateTime?)null, DueAtUtc = dueAt }, transaction);
            if (active) initialInstanceId = stageId;
        }
        await db.ExecuteAsync("UPDATE recruitment_position_pipeline_instances SET CurrentStageInstanceId=@StageId WHERE Id=@Id", new { Id = caseId, StageId = initialInstanceId }, transaction);
        await db.ExecuteAsync(@"INSERT INTO recruitment_position_stage_events
(PositionPipelineInstanceId,PositionStageInstanceId,EventType,EventTitle,EventDetails,ActorUserId)
VALUES (@CaseId,@StageId,'CaseStarted','Hiring case started','Work-order SLA clock started from the received timestamp.',@UserId)", new { CaseId = caseId, StageId = initialInstanceId, UserId = user.Id }, transaction);
        if (source.PositionId is > 0) await AssignPositionPipelineAsync(db, source.PositionId.Value, source.PipelineVersionId, user.Id, transaction);
        await transaction.CommitAsync();
        return (await GetHiringCaseAsync(caseId, user), "");
    }

    public async Task<IReadOnlyList<RecruitmentHiringCase>> ListHiringCasesAsync(AuthUser user, int? clientId = null)
    {
        await using var db = Db();
        await db.OpenAsync();
        return (await HiringCaseRowsAsync(db, user.ClientId ?? clientId, null)).ToList();
    }

    public async Task<RecruitmentHiringCase?> GetHiringCaseAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var row = (await HiringCaseRowsAsync(db, user.ClientId, id)).FirstOrDefault();
        if (row is null) return null;
        row.Stages = (await db.QueryAsync<RecruitmentHiringCaseStage>(@"SELECT instance.*,stage.StageCode,stage.StageName,stage.DisplayOrder,stage.StakeholderCode,stage.TargetOffsetMinutes,
stage.AllowPause,stage.PauseBehavior,stage.RequiresApproval,stage.ApprovalWorkflowId,stage.IsTerminal,
EXISTS(SELECT 1 FROM recruitment_position_stage_pause_periods pauseRow WHERE pauseRow.PositionStageInstanceId=instance.Id AND pauseRow.ResumedAtUtc IS NULL) IsPaused,
(instance.Status='Active' AND instance.DueAtUtc IS NOT NULL AND instance.DueAtUtc<UTC_TIMESTAMP(6)) IsSlaBreached
FROM recruitment_position_stage_instances instance
JOIN recruitment_pipeline_stages stage ON stage.Id=instance.PipelineStageId
WHERE instance.PositionPipelineInstanceId=@Id ORDER BY stage.DisplayOrder,stage.Id", new { Id = id })).ToList();
        if (row.Stages.Count > 0)
        {
            var requirements = (await db.QueryAsync<RecruitmentStageProcessDocumentRequirement>(@"SELECT * FROM recruitment_stage_process_document_requirements
WHERE PipelineStageId IN @Ids ORDER BY DisplayOrder,Id", new { Ids = row.Stages.Select(stage => stage.PipelineStageId).ToArray() })).ToLookup(requirement => requirement.PipelineStageId);
            var pauses = (await db.QueryAsync<RecruitmentHiringCasePausePeriod>(@"SELECT pauseRow.*,
COALESCE(pausedUser.DisplayName,pausedUser.Email,'System') PausedByName,
COALESCE(resumedUser.DisplayName,resumedUser.Email,'') ResumedByName
FROM recruitment_position_stage_pause_periods pauseRow
LEFT JOIN authusers pausedUser ON pausedUser.Id=pauseRow.PausedByUserId
LEFT JOIN authusers resumedUser ON resumedUser.Id=pauseRow.ResumedByUserId
WHERE pauseRow.PositionStageInstanceId IN @Ids ORDER BY pauseRow.PausedAtUtc DESC", new { Ids = row.Stages.Select(stage => stage.Id).ToArray() })).ToLookup(pause => pause.PositionStageInstanceId);
            foreach (var stage in row.Stages)
            {
                stage.PauseHistory = pauses[stage.Id].ToList();
                stage.ProcessDocumentRequirements = requirements[stage.PipelineStageId].ToList();
            }
        }
        if (row.CurrentStageInstanceId is > 0)
        {
            var pending = await db.QueryFirstOrDefaultAsync<HiringCaseAdvanceState>(@"SELECT Id,Status FROM recruitment_hiring_case_advance_requests
WHERE HiringCaseId=@CaseId AND PositionStageInstanceId=@StageInstanceId AND Status='Pending Approval' ORDER BY Id DESC LIMIT 1",
                new { CaseId = id, StageInstanceId = row.CurrentStageInstanceId });
            if (pending is not null) MarkAdvance(row, pending.Status, pending.Id, "Stage movement is pending in global My Tasks.");
        }
        return row;
    }

    public async Task<(RecruitmentHiringCase? Row, string Error)> AdvanceHiringCaseAsync(long id, MoveRecruitmentHiringCaseRequest request, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();
        var current = await db.QueryFirstOrDefaultAsync<CurrentStageSource>(@"SELECT hiringCase.Id HiringCaseId,hiringCase.ClientId,hiringCase.PipelineVersionId,
hiringCase.CurrentStageInstanceId,currentStage.PipelineStageId,stage.DisplayOrder,stage.StageName,stage.IsTerminal,
stage.RequiresApproval,stage.ApprovalWorkflowId
FROM recruitment_position_pipeline_instances hiringCase
JOIN recruitment_position_stage_instances currentStage ON currentStage.Id=hiringCase.CurrentStageInstanceId
JOIN recruitment_pipeline_stages stage ON stage.Id=currentStage.PipelineStageId
WHERE hiringCase.Id=@Id AND hiringCase.Status='Active' AND (@ClientId IS NULL OR hiringCase.ClientId=@ClientId) FOR UPDATE", new { Id = id, user.ClientId }, transaction);
        if (current is null) return (null, "Active hiring case was not found.");
        var validationError = await ValidateHiringCaseStageExitAsync(db, transaction, current);
        if (validationError.Length > 0) return (null, validationError);
        var outcome = string.IsNullOrWhiteSpace(request.OutcomeCode) ? "ADVANCE" : request.OutcomeCode.Trim().ToUpperInvariant();
        var reason = (request.Reason ?? "").Trim();
        if (current.RequiresApproval)
        {
            if (current.ApprovalWorkflowId is null or <= 0) return (null, $"Stage {current.StageName} requires approval but has no active workflow mapping.");
            var pendingRequest = await db.QueryFirstOrDefaultAsync<HiringCaseAdvanceState>(@"SELECT Id,Status,WorkflowInstanceId FROM recruitment_hiring_case_advance_requests
WHERE HiringCaseId=@CaseId AND PositionStageInstanceId=@StageInstanceId AND Status='Pending Approval' ORDER BY Id DESC LIMIT 1",
                new { CaseId = id, StageInstanceId = current.CurrentStageInstanceId }, transaction);
            if (pendingRequest is not null)
            {
                await transaction.RollbackAsync();
                if (pendingRequest.WorkflowInstanceId is > 0)
                {
                    var existingInstance = await workflows.GetInstanceAsync(pendingRequest.WorkflowInstanceId.Value);
                    if (existingInstance?.Status is "Approved" or "Rejected" or "Sent Back")
                        return await SyncHiringCaseAdvanceWorkflowStatusAsync(pendingRequest.Id, existingInstance.Status, user);
                }
                var pendingRow = await GetHiringCaseAsync(id, user);
                if (pendingRow is not null) MarkAdvance(pendingRow, "Pending Approval", pendingRequest.Id, "Stage movement is already pending in My Tasks.");
                return (pendingRow, "");
            }
            var requestId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_hiring_case_advance_requests
(HiringCaseId,PositionStageInstanceId,PipelineStageId,OutcomeCode,Reason,Status,RequestedByUserId)
VALUES (@CaseId,@StageInstanceId,@PipelineStageId,@Outcome,@Reason,'Pending Approval',@UserId);SELECT LAST_INSERT_ID();",
                new { CaseId = id, StageInstanceId = current.CurrentStageInstanceId, current.PipelineStageId, Outcome = outcome, Reason = reason, UserId = user.Id }, transaction);
            await transaction.CommitAsync();
            var workflow = await workflows.StartAsync(new StartWorkflowRequest
            {
                WorkflowId = checked((int)current.ApprovalWorkflowId.Value),
                ResourceType = "RecruitmentPipelineTransition",
                ResourceId = $"HIRING_CASE:{requestId}",
                PayloadJson = JsonSerializer.Serialize(new { HiringCaseId = id, StageInstanceId = current.CurrentStageInstanceId, current.PipelineStageId, current.StageName, OutcomeCode = outcome, Reason = reason })
            }, user.Id);
            if (workflow is null)
            {
                await db.ExecuteAsync("UPDATE recruitment_hiring_case_advance_requests SET Status='Workflow Failed',DecidedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id", new { Id = requestId });
                return (null, "Stage approval workflow could not start. Check the configured approver chain.");
            }
            await db.ExecuteAsync("UPDATE recruitment_hiring_case_advance_requests SET WorkflowInstanceId=@WorkflowId WHERE Id=@Id", new { Id = requestId, WorkflowId = workflow.Id });
            var row = await GetHiringCaseAsync(id, user);
            if (row is not null) MarkAdvance(row, "Pending Approval", requestId, "Stage movement was sent to global My Tasks for approval.");
            return (row, "");
        }
        await ApplyHiringCaseAdvanceAsync(db, transaction, current, outcome, reason, user.Id);
        await transaction.CommitAsync();
        return (await GetHiringCaseAsync(id, user), "");
    }

    public async Task<(RecruitmentHiringCase? Row, string Error)> SyncHiringCaseAdvanceWorkflowStatusAsync(long requestId, string workflowStatus, AuthUser user)
    {
        var normalized = workflowStatus is "Approved" or "Rejected" or "Sent Back" ? workflowStatus : "Pending Approval";
        await using var db = Db();
        await db.OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();
        var request = await db.QueryFirstOrDefaultAsync<HiringCaseAdvanceRequestSource>(@"SELECT advanceRequest.*,hiringCase.ClientId,
stage.DisplayOrder,stage.StageName,stage.IsTerminal,stage.RequiresApproval,stage.ApprovalWorkflowId
FROM recruitment_hiring_case_advance_requests advanceRequest
JOIN recruitment_position_pipeline_instances hiringCase ON hiringCase.Id=advanceRequest.HiringCaseId
JOIN recruitment_pipeline_stages stage ON stage.Id=advanceRequest.PipelineStageId
WHERE advanceRequest.Id=@Id AND (@ClientId IS NULL OR hiringCase.ClientId=@ClientId) FOR UPDATE", new { Id = requestId, user.ClientId }, transaction);
        if (request is null) return (null, "Hiring-case stage approval request was not found.");
        if (request.Status == "Applied")
        {
            await transaction.RollbackAsync();
            return (await GetHiringCaseAsync(request.HiringCaseId, user), "");
        }
        await db.ExecuteAsync(@"UPDATE recruitment_hiring_case_advance_requests SET Status=@Status,DecidedByUserId=@UserId,
DecidedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id", new { Id = requestId, Status = normalized, UserId = user.Id }, transaction);
        if (normalized != "Approved")
        {
            await transaction.CommitAsync();
            var declined = await GetHiringCaseAsync(request.HiringCaseId, user);
            if (declined is not null) MarkAdvance(declined, normalized, requestId, $"Stage movement was {normalized.ToLowerInvariant()}.");
            return (declined, "");
        }
        var current = await db.QueryFirstOrDefaultAsync<CurrentStageSource>(@"SELECT hiringCase.Id HiringCaseId,hiringCase.ClientId,hiringCase.PipelineVersionId,
hiringCase.CurrentStageInstanceId,currentStage.PipelineStageId,stage.DisplayOrder,stage.StageName,stage.IsTerminal,
stage.RequiresApproval,stage.ApprovalWorkflowId
FROM recruitment_position_pipeline_instances hiringCase
JOIN recruitment_position_stage_instances currentStage ON currentStage.Id=hiringCase.CurrentStageInstanceId
JOIN recruitment_pipeline_stages stage ON stage.Id=currentStage.PipelineStageId
WHERE hiringCase.Id=@Id AND hiringCase.Status='Active' AND currentStage.Id=@StageInstanceId FOR UPDATE",
            new { Id = request.HiringCaseId, StageInstanceId = request.PositionStageInstanceId }, transaction);
        if (current is null) return (null, "The hiring case is no longer at the stage that was approved.");
        var validationError = await ValidateHiringCaseStageExitAsync(db, transaction, current);
        if (validationError.Length > 0) return (null, validationError);
        await ApplyHiringCaseAdvanceAsync(db, transaction, current, request.OutcomeCode, request.Reason, user.Id);
        await db.ExecuteAsync("UPDATE recruitment_hiring_case_advance_requests SET Status='Applied',AppliedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id", new { Id = requestId }, transaction);
        await transaction.CommitAsync();
        var row = await GetHiringCaseAsync(request.HiringCaseId, user);
        if (row is not null) MarkAdvance(row, "Applied", requestId, "Approved stage movement was applied.");
        return (row, "");
    }

    private static async Task<string> ValidateHiringCaseStageExitAsync(MySqlConnection db, MySqlTransaction transaction, CurrentStageSource current)
    {
        var openPause = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_position_stage_pause_periods WHERE PositionStageInstanceId=@Id AND ResumedAtUtc IS NULL", new { Id = current.CurrentStageInstanceId }, transaction);
        if (openPause > 0) return "Resume the SLA before moving this hiring case.";
        var missingDocuments = (await db.QueryAsync<string>(@"SELECT requirement.DocumentType
FROM recruitment_stage_process_document_requirements requirement
WHERE requirement.PipelineStageId=@StageId AND requirement.IsRequired=TRUE
AND NOT EXISTS (SELECT 1 FROM recruitment_process_documents document
  WHERE document.HiringCaseId=@CaseId AND document.PipelineStageId=@StageId
    AND document.DocumentType=requirement.DocumentType
    AND (requirement.RequiresSignature=FALSE OR (document.Status='Signed' AND EXISTS (
      SELECT 1 FROM entity_attachments attachment WHERE attachment.entity_type='RECRUITMENT_PROCESS_DOCUMENT'
        AND attachment.entity_id=document.Id AND attachment.is_current=TRUE AND attachment.is_deleted=FALSE))))
ORDER BY requirement.DisplayOrder,requirement.Id", new { StageId = current.PipelineStageId, CaseId = current.HiringCaseId }, transaction)).ToArray();
        return missingDocuments.Length > 0 ? $"Complete required process documents before moving this stage: {string.Join(", ", missingDocuments)}." : "";
    }

    private static async Task ApplyHiringCaseAdvanceAsync(MySqlConnection db, MySqlTransaction transaction, CurrentStageSource current, string outcome, string reason, int actorUserId)
    {
        await db.ExecuteAsync(@"UPDATE recruitment_position_stage_instances SET Status='Completed',OutcomeCode=@Outcome,
CompletedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id", new { Id = current.CurrentStageInstanceId, Outcome = outcome }, transaction);
        var next = await db.QueryFirstOrDefaultAsync<NextStageSource>(@"SELECT instance.Id StageInstanceId,stage.Id PipelineStageId,stage.StageName,stage.IsTerminal
FROM recruitment_position_stage_instances instance
JOIN recruitment_pipeline_stages stage ON stage.Id=instance.PipelineStageId
WHERE instance.PositionPipelineInstanceId=@CaseId AND instance.Status='Pending' AND stage.DisplayOrder>@DisplayOrder
ORDER BY stage.DisplayOrder,stage.Id LIMIT 1", new { CaseId = current.HiringCaseId, current.DisplayOrder }, transaction);
        if (next is null || current.IsTerminal)
            await db.ExecuteAsync("UPDATE recruitment_position_pipeline_instances SET Status='Completed',CurrentStageInstanceId=NULL,CompletedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id", new { Id = current.HiringCaseId }, transaction);
        else
            await db.ExecuteAsync("UPDATE recruitment_position_stage_instances SET Status='Active',EnteredAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id;UPDATE recruitment_position_pipeline_instances SET CurrentStageInstanceId=@Id WHERE Id=@CaseId", new { Id = next.StageInstanceId, CaseId = current.HiringCaseId }, transaction);
        await db.ExecuteAsync(@"INSERT INTO recruitment_position_stage_events
(PositionPipelineInstanceId,PositionStageInstanceId,EventType,EventTitle,EventDetails,ActorUserId)
VALUES (@CaseId,@StageId,'StageMoved',@Title,@Details,@UserId)", new { CaseId = current.HiringCaseId, StageId = current.CurrentStageInstanceId, Title = next is null ? "Hiring case completed" : $"Moved to {next.StageName}", Details = reason, UserId = actorUserId }, transaction);
    }

    private static void MarkAdvance(RecruitmentHiringCase row, string status, long? requestId, string message)
    {
        row.AdvanceStatus = status;
        row.AdvanceRequestId = requestId;
        row.AdvanceMessage = message;
    }

    public async Task<(RecruitmentHiringCase? Row, string Error)> PauseHiringCaseAsync(long id, RecruitmentStagePauseRequest request, AuthUser user)
    {
        var reason = (request.Reason ?? "").Trim();
        if (reason.Length < 3) return (null, "Pause reason is required.");
        await using var db = Db();
        await db.OpenAsync();
        var stage = await ActiveCaseStageAsync(db, id, user.ClientId);
        if (stage is null) return (null, "Active hiring case stage was not found.");
        if (!stage.AllowPause) return (null, "SLA pause is disabled for this stage.");
        var inserted = await db.ExecuteAsync(@"INSERT INTO recruitment_position_stage_pause_periods
(PositionStageInstanceId,Reason,PausedByUserId)
SELECT @StageId,@Reason,@UserId WHERE NOT EXISTS (
SELECT 1 FROM recruitment_position_stage_pause_periods WHERE PositionStageInstanceId=@StageId AND ResumedAtUtc IS NULL)", new { StageId = stage.StageInstanceId, Reason = reason, UserId = user.Id });
        if (inserted == 0) return (null, "This stage SLA is already paused.");
        await db.ExecuteAsync(@"INSERT INTO recruitment_position_stage_events
(PositionPipelineInstanceId,PositionStageInstanceId,EventType,EventTitle,EventDetails,ActorUserId)
VALUES (@CaseId,@StageId,'SlaPaused','SLA paused',@Reason,@UserId)", new { CaseId = id, StageId = stage.StageInstanceId, Reason = reason, UserId = user.Id });
        return (await GetHiringCaseAsync(id, user), "");
    }

    public async Task<(RecruitmentHiringCase? Row, string Error)> ResumeHiringCaseAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();
        var stage = await ActiveCaseStageAsync(db, id, user.ClientId, transaction);
        if (stage is null) return (null, "Active hiring case stage was not found.");
        var pause = await db.QueryFirstOrDefaultAsync<PauseSource>(@"SELECT Id,PausedAtUtc FROM recruitment_position_stage_pause_periods
WHERE PositionStageInstanceId=@Id AND ResumedAtUtc IS NULL ORDER BY Id DESC LIMIT 1 FOR UPDATE", new { Id = stage.StageInstanceId }, transaction);
        if (pause is null) return (null, "This stage SLA is not paused.");
        var seconds = Math.Max(0, (long)(DateTime.UtcNow - pause.PausedAtUtc).TotalSeconds);
        await db.ExecuteAsync(@"UPDATE recruitment_position_stage_pause_periods SET ResumedByUserId=@UserId,ResumedAtUtc=UTC_TIMESTAMP(6),DurationSeconds=@Seconds WHERE Id=@Id;
UPDATE recruitment_position_stage_instances SET PausedDurationSeconds=PausedDurationSeconds+@Seconds,
DueAtUtc=CASE WHEN @ShiftStage=TRUE AND DueAtUtc IS NOT NULL THEN TIMESTAMPADD(SECOND,@Seconds,DueAtUtc) ELSE DueAtUtc END WHERE Id=@StageId;
UPDATE recruitment_position_pipeline_instances SET OverallDueAtUtc=CASE WHEN @ShiftOverall=TRUE AND OverallDueAtUtc IS NOT NULL THEN TIMESTAMPADD(SECOND,@Seconds,OverallDueAtUtc) ELSE OverallDueAtUtc END WHERE Id=@CaseId;",
            new { pause.Id, UserId = user.Id, Seconds = seconds, StageId = stage.StageInstanceId, CaseId = id, ShiftStage = !stage.PauseBehavior.Equals("NoShift", StringComparison.OrdinalIgnoreCase), ShiftOverall = stage.PauseBehavior.Equals("ShiftStageAndOverall", StringComparison.OrdinalIgnoreCase) }, transaction);
        await db.ExecuteAsync(@"INSERT INTO recruitment_position_stage_events
(PositionPipelineInstanceId,PositionStageInstanceId,EventType,EventTitle,EventDetails,ActorUserId)
VALUES (@CaseId,@StageId,'SlaResumed','SLA resumed',@Details,@UserId)", new { CaseId = id, StageId = stage.StageInstanceId, Details = $"Paused for {seconds} seconds.", UserId = user.Id }, transaction);
        await transaction.CommitAsync();
        return (await GetHiringCaseAsync(id, user), "");
    }

    public async Task<IReadOnlyList<RecruitmentProcessDocument>> ListProcessDocumentsAsync(AuthUser user, long? hiringCaseId, long? applicationId)
    {
        await using var db = Db();
        await db.OpenAsync();
        return (await db.QueryAsync<RecruitmentProcessDocument>(@"SELECT documentRow.*,
EXISTS (SELECT 1 FROM entity_attachments attachment
    WHERE attachment.entity_type='RECRUITMENT_PROCESS_DOCUMENT' AND attachment.entity_id=documentRow.Id
    AND attachment.is_current=TRUE AND attachment.is_deleted=FALSE
    AND NOT (attachment.public_id <=> documentRow.AttachmentPublicId)) HasFinalSignedAttachment
FROM recruitment_process_documents documentRow
WHERE (@ClientId IS NULL OR documentRow.ClientId=@ClientId) AND (@HiringCaseId IS NULL OR documentRow.HiringCaseId=@HiringCaseId)
AND (@ApplicationId IS NULL OR documentRow.ApplicationId=@ApplicationId) ORDER BY documentRow.DocumentType,documentRow.VersionNumber DESC,documentRow.Id DESC",
            new { user.ClientId, HiringCaseId = hiringCaseId, ApplicationId = applicationId })).ToList();
    }

    public async Task<(RecruitmentProcessDocument? Row, string Error)> SaveProcessDocumentAsync(SaveRecruitmentProcessDocument request, AuthUser user)
    {
        request.DocumentType = (request.DocumentType ?? "").Trim().ToUpperInvariant();
        var requestedStatus = (request.Status ?? "").Trim();
        if (requestedStatus.Length > 0 && !ProcessDocumentStatuses.Contains(requestedStatus)) return (null, "Select a supported recruitment document status.");
        request.Status = Canonical(ProcessDocumentStatuses, requestedStatus, "Draft");
        if (!ProcessDocumentTypes.Contains(request.DocumentType)) return (null, "Select a supported recruitment document type.");
        if (request.ClientId <= 0 || (user.ClientId.HasValue && user.ClientId.Value != request.ClientId)) return (null, "Client is outside your access.");
        if (!request.HiringCaseId.HasValue && !request.ApplicationId.HasValue && !request.InterviewId.HasValue) return (null, "Link the document to a hiring case, application or interview.");
        await using var db = Db();
        await db.OpenAsync();
        if (request.HiringCaseId.HasValue && await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_position_pipeline_instances WHERE Id=@Id AND ClientId=@ClientId", new { Id = request.HiringCaseId.Value, request.ClientId }) == 0)
            return (null, "The linked hiring case is outside this client.");
        if (request.ApplicationId.HasValue && await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_candidate_applications WHERE Id=@Id AND ClientId=@ClientId", new { Id = request.ApplicationId.Value, request.ClientId }) == 0)
            return (null, "The linked candidate application is outside this client.");
        if (request.InterviewId.HasValue && await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_interviews interviewRow
JOIN recruitment_candidate_applications applicationRow ON applicationRow.Id=interviewRow.ApplicationId
WHERE interviewRow.Id=@Id AND applicationRow.ClientId=@ClientId", new { Id = request.InterviewId.Value, request.ClientId }) == 0)
            return (null, "The linked interview is outside this client.");
        if (request.PipelineStageId is null or <= 0)
            return (null, "This document type is not configured for the selected pipeline stage.");
        var configuredTemplateId = await db.ExecuteScalarAsync<long?>(@"SELECT TemplateId FROM recruitment_stage_process_document_requirements
WHERE PipelineStageId=@StageId AND DocumentType=@DocumentType", new { StageId = request.PipelineStageId, request.DocumentType });
        var requirementExists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_stage_process_document_requirements
WHERE PipelineStageId=@StageId AND DocumentType=@DocumentType", new { StageId = request.PipelineStageId, request.DocumentType });
        if (requirementExists == 0) return (null, "This document type is not configured for the selected pipeline stage.");
        if (configuredTemplateId != request.TemplateId)
            return (null, "Use the process-document template configured on this pipeline stage.");
        if (request.TemplateId is > 0 && await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_templates
WHERE Id=@Id AND IsActive=TRUE AND ClientId IN (0,@ClientId)", new { Id = request.TemplateId.Value, request.ClientId }) == 0)
            return (null, "The configured process-document template is inactive or belongs to another client.");
        if (request.Id <= 0 && request.Status.Equals("Signed", StringComparison.OrdinalIgnoreCase)) return (null, "Prepare and attach the process document before signing it.");
        if (request.Id > 0)
        {
            var existingStatus = await db.ExecuteScalarAsync<string?>("SELECT Status FROM recruitment_process_documents WHERE Id=@Id AND (@ClientId IS NULL OR ClientId=@ClientId)", new { request.Id, user.ClientId });
            if (existingStatus is null) return (null, "Recruitment document was not found.");
            if (existingStatus.Equals("Signed", StringComparison.OrdinalIgnoreCase) && !request.Status.Equals("Signed", StringComparison.OrdinalIgnoreCase)) return (null, "A signed process document cannot be moved back to draft.");
            if (request.Status.Equals("Signed", StringComparison.OrdinalIgnoreCase))
            {
                var finalAttachmentCount = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*)
FROM entity_attachments attachment
JOIN recruitment_process_documents documentRow ON documentRow.Id=attachment.entity_id
WHERE attachment.entity_type='RECRUITMENT_PROCESS_DOCUMENT' AND attachment.entity_id=@Id
AND attachment.is_current=TRUE AND attachment.is_deleted=FALSE
AND NOT (attachment.public_id <=> documentRow.AttachmentPublicId)", new { request.Id });
                if (finalAttachmentCount == 0) return (null, "Upload the final signed document after the generated draft before marking it signed.");
            }
        }
        long id;
        if (request.Id <= 0)
        {
            var version = await db.ExecuteScalarAsync<int>(@"SELECT COALESCE(MAX(VersionNumber),0)+1 FROM recruitment_process_documents
WHERE ClientId=@ClientId AND HiringCaseId <=> @HiringCaseId AND ApplicationId <=> @ApplicationId AND InterviewId <=> @InterviewId AND DocumentType=@DocumentType", request);
            id = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_process_documents
(ClientId,HiringCaseId,ApplicationId,InterviewId,PipelineStageId,DocumentType,VersionNumber,TemplateId,AttachmentPublicId,Status,WorkflowInstanceId,CreatedByUserId,SignedByUserId,SignedAtUtc)
VALUES (@ClientId,@HiringCaseId,@ApplicationId,@InterviewId,@PipelineStageId,@DocumentType,@VersionNumber,@TemplateId,@AttachmentPublicId,@Status,@WorkflowInstanceId,@UserId,
CASE WHEN @Status='Signed' THEN @UserId ELSE NULL END,CASE WHEN @Status='Signed' THEN UTC_TIMESTAMP(6) ELSE NULL END);SELECT LAST_INSERT_ID();",
                new { request.ClientId, request.HiringCaseId, request.ApplicationId, request.InterviewId, request.PipelineStageId, request.DocumentType, VersionNumber = version, request.TemplateId, AttachmentPublicId = request.AttachmentPublicId?.ToString(), request.Status, request.WorkflowInstanceId, UserId = user.Id });
        }
        else
        {
            id = request.Id;
            var updated = await db.ExecuteAsync(@"UPDATE recruitment_process_documents SET TemplateId=@TemplateId,
Status=@Status,WorkflowInstanceId=@WorkflowInstanceId,SignedByUserId=CASE WHEN @Status='Signed' THEN @UserId ELSE SignedByUserId END,
SignedAtUtc=CASE WHEN @Status='Signed' THEN COALESCE(SignedAtUtc,UTC_TIMESTAMP(6)) ELSE SignedAtUtc END
WHERE Id=@Id AND (@ClientScope IS NULL OR ClientId=@ClientScope)", new { request.Id, request.TemplateId, request.Status, request.WorkflowInstanceId, UserId = user.Id, ClientScope = user.ClientId });
            if (updated == 0) return (null, "Recruitment document was not found.");
        }
        return (await db.QueryFirstOrDefaultAsync<RecruitmentProcessDocument>("SELECT * FROM recruitment_process_documents WHERE Id=@Id", new { Id = id }), "");
    }

    public async Task<(RecruitmentProcessDocument? Row, string Error)> GenerateProcessDocumentAsync(
        long id,
        AuthUser user,
        string ipAddress,
        string userAgent,
        CancellationToken cancellationToken)
    {
        await using var db = Db();
        await db.OpenAsync(cancellationToken);
        var context = await db.QueryFirstOrDefaultAsync<ProcessDocumentGenerationContext>(@"SELECT documentRow.*,
templateRow.ClientId TemplateClientId,templateRow.TemplateType,templateRow.SubjectTemplate,templateRow.BodyTemplate,templateRow.IsActive TemplateIsActive,
COALESCE(clientRow.Name,'') ClientName,COALESCE(workOrder.WorkOrderNumber,'') WorkOrderNumber,
workOrder.ReceivedAtUtc WorkOrderReceivedAt,COALESCE(applicationRow.PositionId,hiringCase.PositionId) PositionId,
COALESCE(workOrderLine.PositionName,positionRow.PositionTitle,'') PositionName,COALESCE(workOrderLine.PayBandLevelCode,'') PayBandLevelCode,
COALESCE(workOrderLine.Location,'') Location,COALESCE(workOrderLine.Division,'') Division,
COALESCE(stageRow.StageName,'') StageName,COALESCE(candidateRow.FirstName,'') CandidateFirstName,
COALESCE(candidateRow.LastName,'') CandidateLastName,COALESCE(candidateRow.Email,'') CandidateEmail,
interviewRow.ScheduledStart InterviewDate
FROM recruitment_process_documents documentRow
JOIN recruitment_templates templateRow ON templateRow.Id=documentRow.TemplateId
LEFT JOIN clients clientRow ON clientRow.Id=documentRow.ClientId
LEFT JOIN recruitment_position_pipeline_instances hiringCase ON hiringCase.Id=documentRow.HiringCaseId
LEFT JOIN recruitment_work_orders workOrder ON workOrder.Id=hiringCase.WorkOrderId
LEFT JOIN recruitment_work_order_lines workOrderLine ON workOrderLine.Id=hiringCase.WorkOrderLineId
LEFT JOIN recruitment_candidate_applications applicationRow ON applicationRow.Id=documentRow.ApplicationId
LEFT JOIN recruitment_candidates candidateRow ON candidateRow.Id=applicationRow.CandidateId
LEFT JOIN recruitment_open_positions positionRow ON positionRow.Id=COALESCE(applicationRow.PositionId,hiringCase.PositionId)
LEFT JOIN recruitment_pipeline_stages stageRow ON stageRow.Id=documentRow.PipelineStageId
LEFT JOIN recruitment_interviews interviewRow ON interviewRow.Id=documentRow.InterviewId
WHERE documentRow.Id=@Id AND (@ClientId IS NULL OR documentRow.ClientId=@ClientId)", new { Id = id, user.ClientId });
        if (context is null) return (null, "Process document or its configured template was not found.");
        if (context.Status.Equals("Signed", StringComparison.OrdinalIgnoreCase)) return (null, "A signed process document cannot be regenerated.");
        if (!context.TemplateIsActive || (context.TemplateClientId != 0 && context.TemplateClientId != context.ClientId))
            return (null, "The configured process-document template is inactive or belongs to another client.");
        var configured = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_stage_process_document_requirements
WHERE PipelineStageId=@StageId AND DocumentType=@DocumentType AND TemplateId=@TemplateId", new { StageId = context.PipelineStageId, context.DocumentType, TemplateId = context.TemplateId });
        if (configured == 0) return (null, "The process-document template no longer matches the published pipeline stage.");

        var culture = CultureInfo.GetCultureInfo("en-IN");
        var candidateName = $"{context.CandidateFirstName} {context.CandidateLastName}".Trim();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["date"] = DateTime.Today.ToString("dd MMMM yyyy", culture),
            ["clientName"] = context.ClientName,
            ["companyName"] = context.ClientName,
            ["workOrderNumber"] = context.WorkOrderNumber,
            ["positionName"] = context.PositionName,
            ["positionTitle"] = context.PositionName,
            ["payBandLevel"] = context.PayBandLevelCode,
            ["location"] = context.Location,
            ["division"] = context.Division,
            ["stageName"] = context.StageName,
            ["documentType"] = context.DocumentType.Replace('_', ' '),
            ["documentVersion"] = context.VersionNumber.ToString(CultureInfo.InvariantCulture),
            ["candidateName"] = candidateName,
            ["candidateFirstName"] = context.CandidateFirstName,
            ["candidateLastName"] = context.CandidateLastName,
            ["candidateEmail"] = context.CandidateEmail,
            ["interviewDate"] = context.InterviewDate?.ToString("dd MMMM yyyy hh:mm tt", culture) ?? ""
        };
        if (RequiresSelectionCommitteeData(context.SubjectTemplate, context.BodyTemplate))
        {
            var (selectionValues, selectionError) = await SelectionCommitteeTemplateValuesAsync(db, context, culture);
            if (selectionValues is null) return (null, selectionError);
            foreach (var pair in selectionValues) values[pair.Key] = pair.Value;
        }
        var (bytes, renderError) = templatePdf.Create(context.SubjectTemplate, context.BodyTemplate, values);
        if (bytes is null) return (null, renderError);

        var fieldConfigurationId = await db.ExecuteScalarAsync<long?>(@"SELECT field.id
FROM attachment_field_configurations field
JOIN attachment_attributes attribute ON attribute.id=field.attachment_attribute_id
WHERE field.is_active=TRUE AND attribute.is_active=TRUE
AND field.module_code='RECRUITMENT' AND field.form_code='PROCESS_DOCUMENT'
AND field.client_id IN (0,@ClientId)
AND (field.effective_from_utc IS NULL OR field.effective_from_utc<=UTC_TIMESTAMP(6))
AND (field.effective_until_utc IS NULL OR field.effective_until_utc>=UTC_TIMESTAMP(6))
ORDER BY CASE WHEN field.client_id=@ClientId THEN 0 ELSE 1 END,field.display_order,field.id DESC LIMIT 1", new { context.ClientId });
        if (fieldConfigurationId is null or <= 0)
            return (null, "No active secure attachment field is configured for Recruitment / Process Document for this client.");

        await using var source = new MemoryStream(bytes, writable: false);
        var baseName = Regex.Replace($"{context.DocumentType}-{context.Id}-v{context.VersionNumber}", @"[^A-Za-z0-9_-]+", "-").Trim('-');
        var file = new FormFile(source, 0, bytes.Length, "file", $"{baseName}.pdf")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf"
        };
        var upload = await attachments.UploadAsync(new AttachmentUploadMetadata
        {
            FieldConfigurationId = fieldConfigurationId.Value,
            EntityType = "RECRUITMENT_PROCESS_DOCUMENT",
            EntityId = context.Id,
            DocumentNumber = $"{context.DocumentType}-{context.Id}-V{context.VersionNumber}",
            IssueDate = DateTime.Today
        }, file, user, ipAddress, userAgent, cancellationToken);
        if (upload.Attachment is null) return (null, upload.Error ?? "Process document could not be stored.");

        var updated = await db.ExecuteAsync(@"UPDATE recruitment_process_documents
SET AttachmentPublicId=@PublicId,Status='Prepared',UpdatedAtUtc=UTC_TIMESTAMP(6)
WHERE Id=@Id AND Status<>'Signed'", new { Id = context.Id, PublicId = upload.Attachment.PublicId.ToString() });
        if (updated == 0)
        {
            await attachments.DeleteAsync(upload.Attachment.PublicId, user, ipAddress, userAgent);
            return (null, "Document status changed while it was being generated. Reload and retry.");
        }
        if (context.AttachmentPublicId.HasValue && context.AttachmentPublicId.Value != upload.Attachment.PublicId)
            await attachments.DeleteAsync(context.AttachmentPublicId.Value, user, ipAddress, userAgent);
        return (await db.QueryFirstOrDefaultAsync<RecruitmentProcessDocument>("SELECT * FROM recruitment_process_documents WHERE Id=@Id", new { Id = context.Id }), "");
    }

    private static bool RequiresSelectionCommitteeData(params string[] templates) =>
        templates.Any(template => new[]
        {
            "panelMembersList", "candidateAttendanceTable", "candidateResultTable", "scoreAnnexureTable",
            "panelSignatureBlock", "shortlistedCount", "presentCount"
        }.Any(key => (template ?? "").Contains(key, StringComparison.OrdinalIgnoreCase)));

    private static async Task<(Dictionary<string, string>? Values, string Error)> SelectionCommitteeTemplateValuesAsync(
        MySqlConnection db,
        ProcessDocumentGenerationContext context,
        CultureInfo culture)
    {
        if (context.HiringCaseId is null or <= 0 || context.PositionId is null or <= 0)
            return (null, "Selection-committee MoM generation requires a hiring case linked to its exact open position.");
        var candidates = (await db.QueryAsync<SelectionCommitteeCandidate>(@"SELECT DISTINCT applicationRow.Id ApplicationId,
CONCAT(candidateRow.FirstName,' ',candidateRow.LastName) CandidateName,
interviewRow.Id InterviewId,interviewRow.ScheduledStart,interviewRow.Status InterviewStatus,
interviewRow.Result,interviewRow.OverallScore,interviewRow.RoundConfigurationId
FROM recruitment_profile_submission_batches batch
JOIN recruitment_profile_submission_batch_items batchItem ON batchItem.BatchId=batch.Id
JOIN recruitment_candidate_applications applicationRow ON applicationRow.Id=batchItem.ApplicationId
JOIN recruitment_candidates candidateRow ON candidateRow.Id=applicationRow.CandidateId
LEFT JOIN recruitment_interviews interviewRow ON interviewRow.Id=(SELECT candidateInterview.Id FROM recruitment_interviews candidateInterview
WHERE candidateInterview.ApplicationId=applicationRow.Id
ORDER BY CASE WHEN candidateInterview.Status IN ('Completed','No Show') THEN 0 ELSE 1 END,candidateInterview.ScheduledStart DESC,candidateInterview.Id DESC LIMIT 1)
WHERE batch.HiringCaseId=@HiringCaseId AND batch.Status IN ('Approved','Forwarded')
AND applicationRow.PositionId=@PositionId
ORDER BY CandidateName", new { HiringCaseId = context.HiringCaseId.Value, PositionId = context.PositionId.Value })).ToList();
        if (candidates.Count == 0)
            return (null, "Approve the shortlisted candidate profile batch before generating the selection-committee MoM.");
        var incomplete = candidates.Where(row => row.InterviewId is null or <= 0 || !new[] { "Completed", "No Show" }.Contains(row.InterviewStatus, StringComparer.OrdinalIgnoreCase)).Select(row => row.CandidateName).ToArray();
        if (incomplete.Length > 0)
            return (null, $"Complete or mark No Show for every shortlisted candidate interview before generating MoM: {string.Join(", ", incomplete)}.");
        var pendingResults = candidates.Where(row => string.IsNullOrWhiteSpace(row.Result) || row.Result.Equals("Pending", StringComparison.OrdinalIgnoreCase)).Select(row => row.CandidateName).ToArray();
        if (pendingResults.Length > 0)
            return (null, $"Record the committee result for every candidate before generating MoM: {string.Join(", ", pendingResults)}.");

        var interviewIds = candidates.Select(row => row.InterviewId!.Value).Distinct().ToArray();
        var missingFeedback = (await db.QueryAsync<string>(@"SELECT CONCAT(candidateRow.FirstName,' ',candidateRow.LastName)
FROM recruitment_interviews interviewRow
JOIN recruitment_candidate_applications applicationRow ON applicationRow.Id=interviewRow.ApplicationId
JOIN recruitment_candidates candidateRow ON candidateRow.Id=applicationRow.CandidateId
WHERE interviewRow.Id IN @Ids AND EXISTS (
  SELECT 1 FROM recruitment_interview_panel_members panel
  LEFT JOIN recruitment_interview_feedback feedback ON feedback.InterviewId=panel.InterviewId AND feedback.PanelUserId=panel.PanelUserId
  WHERE panel.InterviewId=interviewRow.Id AND feedback.Id IS NULL
)", new { Ids = interviewIds })).ToArray();
        if (missingFeedback.Length > 0)
            return (null, $"Every assigned panel member must submit their own feedback before MoM generation: {string.Join(", ", missingFeedback)}.");

        var panelMembers = (await db.QueryAsync<SelectionCommitteePanelMember>(@"SELECT panel.PanelUserId,
COALESCE(userRow.DisplayName,userRow.Email,'Panel member') PanelName,panel.PanelRole,
COALESCE(employeeRow.Designation,'') Designation,COALESCE(clientRow.Name,'') OrganisationName
FROM recruitment_interview_panel_members panel
JOIN authusers userRow ON userRow.Id=panel.PanelUserId
LEFT JOIN employees employeeRow ON employeeRow.Id=userRow.EmployeeId
LEFT JOIN clients clientRow ON clientRow.Id=COALESCE(employeeRow.ClientId,userRow.ClientId)
WHERE panel.InterviewId IN @Ids
GROUP BY panel.PanelUserId,PanelName,panel.PanelRole,Designation,OrganisationName
ORDER BY CASE WHEN LOWER(panel.PanelRole) LIKE '%chair%' THEN 0 WHEN LOWER(panel.PanelRole) LIKE '%member%' THEN 1 ELSE 2 END,PanelName", new { Ids = interviewIds })).ToList();
        if (panelMembers.Count == 0) return (null, "Assign the selection committee before generating MoM.");

        var competencies = (await db.QueryAsync<SelectionCommitteeCompetency>(@"SELECT DISTINCT stageCompetency.Id StageCompetencyId,
definition.CompetencyName,stageCompetency.WeightPercent MaximumScore,stageCompetency.DisplayOrder
FROM recruitment_interviews interviewRow
JOIN recruitment_interview_stage_competencies stageCompetency ON stageCompetency.InterviewStageConfigurationId=interviewRow.RoundConfigurationId
JOIN recruitment_interview_competency_definitions definition ON definition.Id=stageCompetency.CompetencyId
WHERE interviewRow.Id IN @Ids ORDER BY stageCompetency.DisplayOrder,stageCompetency.Id", new { Ids = interviewIds })).ToList();
        var scoreRows = (await db.QueryAsync<SelectionCommitteeScore>(@"SELECT feedback.InterviewId,score.InterviewStageCompetencyId,
ROUND(AVG(score.WeightedScore),2) AwardedScore
FROM recruitment_interview_feedback feedback
JOIN recruitment_interview_feedback_competency_scores score ON score.InterviewFeedbackId=feedback.Id
WHERE feedback.InterviewId IN @Ids
GROUP BY feedback.InterviewId,score.InterviewStageCompetencyId", new { Ids = interviewIds })).ToList();
        var scoreLookup = scoreRows.ToDictionary(row => (row.InterviewId, row.InterviewStageCompetencyId), row => row.AwardedScore);
        if (competencies.Count > 0)
        {
            var missingScores = candidates.Where(candidate => competencies.Any(competency => !scoreLookup.ContainsKey((candidate.InterviewId!.Value, competency.StageCompetencyId)))).Select(candidate => candidate.CandidateName).ToArray();
            if (missingScores.Length > 0) return (null, $"Complete every configured score component before Annexure generation: {string.Join(", ", missingScores)}.");
        }

        var waitingList = candidates.Where(row => row.Result.Equals("On Hold", StringComparison.OrdinalIgnoreCase) || row.Result.Equals("Waiting List", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(row => row.OverallScore).ThenBy(row => row.CandidateName).Select((row, index) => new { row.InterviewId, Rank = index + 1 }).ToDictionary(row => row.InterviewId!.Value, row => row.Rank);
        string ResultText(SelectionCommitteeCandidate row)
        {
            if (row.Result.Equals("Selected", StringComparison.OrdinalIgnoreCase)) return "Selected";
            if (waitingList.TryGetValue(row.InterviewId!.Value, out var rank)) return $"Waiting List {rank}";
            if (row.Result.Equals("No Show", StringComparison.OrdinalIgnoreCase)) return "Not Present";
            return "Not Selected";
        }

        var attendanceRows = candidates.Select((row, index) => new[] { (index + 1).ToString(CultureInfo.InvariantCulture), row.CandidateName, row.InterviewStatus.Equals("Completed", StringComparison.OrdinalIgnoreCase) ? "Yes" : "No" }).ToList();
        var resultRows = candidates.Select((row, index) => new[] { (index + 1).ToString(CultureInfo.InvariantCulture), row.CandidateName, ResultText(row) }).ToList();
        var annexureHeaders = new List<string> { "S. No.", "Name" };
        annexureHeaders.AddRange(competencies.Select(row => $"{row.CompetencyName} (0-{FormatNumber(row.MaximumScore)})"));
        annexureHeaders.Add("Total (100)");
        var annexureRows = candidates.Select((candidate, index) =>
        {
            var cells = new List<string> { (index + 1).ToString(CultureInfo.InvariantCulture), candidate.CandidateName };
            cells.AddRange(competencies.Select(competency => scoreLookup.TryGetValue((candidate.InterviewId!.Value, competency.StageCompetencyId), out var score) ? FormatNumber(score) : "-"));
            cells.Add(FormatNumber(candidate.OverallScore));
            return cells.ToArray();
        }).ToList();
        var dates = candidates.Where(row => row.ScheduledStart.HasValue).Select(row => row.ScheduledStart!.Value.Date).Distinct().OrderBy(value => value).ToArray();
        var interviewDate = string.Join(", ", dates.Select(value => value.ToString("dd.MM.yyyy", culture)));
        var panelList = string.Join("\n", panelMembers.Select((row, index) => $"{(char)('a' + index)}) {PanelIdentity(row)} - {row.PanelRole}"));
        var signatures = string.Join("\n\n", panelMembers.Select(row => $"({row.PanelName})\n{JoinNonEmpty(row.Designation, row.OrganisationName)}\n{row.PanelRole}"));
        return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["approvalDate"] = context.WorkOrderReceivedAt?.ToString("dd.MM.yyyy", culture) ?? "",
            ["interviewDate"] = interviewDate,
            ["shortlistedCount"] = candidates.Count.ToString(CultureInfo.InvariantCulture),
            ["presentCount"] = candidates.Count(row => row.InterviewStatus.Equals("Completed", StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture),
            ["panelMembersList"] = panelList,
            ["candidateAttendanceTable"] = BuildTextTable(["Sl. No.", "Name of Candidate", "Present for Interview"], attendanceRows),
            ["candidateResultTable"] = BuildTextTable(["Sl. No.", "Candidate Name", "Result"], resultRows),
            ["scoreAnnexureTable"] = BuildTextTable(annexureHeaders, annexureRows),
            ["panelSignatureBlock"] = signatures
        }, "");
    }

    private static string BuildTextTable(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        var widths = headers.Select((header, index) => Math.Min(32, Math.Max(header.Length, rows.Count == 0 ? 0 : rows.Max(row => index < row.Length ? row[index].Length : 0)))).ToArray();
        string Line(IReadOnlyList<string> cells) => string.Join(" | ", cells.Select((cell, index) =>
        {
            var value = cell ?? "";
            return value.Length > widths[index] ? $"{value[..Math.Max(1, widths[index] - 3)]}..." : value.PadRight(widths[index]);
        })).TrimEnd();
        var builder = new StringBuilder().AppendLine(Line(headers)).AppendLine(string.Join("-+-", widths.Select(width => new string('-', width))));
        foreach (var row in rows) builder.AppendLine(Line(row));
        return builder.ToString().TrimEnd();
    }

    private static string FormatNumber(decimal value) => value.ToString(value == decimal.Truncate(value) ? "0" : "0.##", CultureInfo.InvariantCulture);
    private static string PanelIdentity(SelectionCommitteePanelMember row) => JoinNonEmpty(row.PanelName, row.Designation, row.OrganisationName);
    private static string JoinNonEmpty(params string[] values) => string.Join(", ", values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()));

    public async Task<(RecruitmentProfileSubmissionBatch? Row, string Error)> CreateProfileBatchAsync(SaveRecruitmentProfileSubmissionBatch request, AuthUser user)
    {
        request.ApplicationIds ??= [];
        var applicationIds = request.ApplicationIds.Where(id => id > 0).Distinct().ToArray();
        if (request.HiringCaseId <= 0 || applicationIds.Length == 0) return (null, "Select a hiring case and at least one candidate application.");
        await using var db = Db();
        await db.OpenAsync();
        var hiringCase = await db.QueryFirstOrDefaultAsync<(int ClientId, long? PositionId)>("SELECT ClientId,PositionId FROM recruitment_position_pipeline_instances WHERE Id=@Id AND (@ClientId IS NULL OR ClientId=@ClientId)", new { Id = request.HiringCaseId, user.ClientId });
        if (hiringCase == default) return (null, "Hiring case was not found.");
        if (!hiringCase.PositionId.HasValue) return (null, "Link this work-order line to its open position before batching candidate applications.");
        var applications = (await db.QueryAsync<BatchApplicationSource>(@"SELECT applicationRow.Id ApplicationId,applicationRow.CandidateId,applicationRow.PositionId,applicationRow.CurrentStage,
(SELECT score.Id FROM recruitment_application_scores score WHERE score.ApplicationId=applicationRow.Id ORDER BY score.Id DESC LIMIT 1) ApplicationScoreId
FROM recruitment_candidate_applications applicationRow WHERE applicationRow.Id IN @Ids AND applicationRow.ClientId=@ClientId", new { Ids = applicationIds, hiringCase.ClientId })).ToList();
        if (applications.Count != applicationIds.Length) return (null, "One or more applications are outside this client.");
        if (applications.Any(row => row.PositionId != hiringCase.PositionId.Value)) return (null, "All selected candidates must belong to this hiring case position.");
        if (applications.Any(row => new[] { "Rejected", "Withdrawn", "Joined" }.Contains(row.CurrentStage, StringComparer.OrdinalIgnoreCase))) return (null, "Rejected, withdrawn or joined applications cannot be included in a client shortlist.");
        await using var transaction = await db.BeginTransactionAsync();
        var sequence = await db.ExecuteScalarAsync<int>("SELECT COALESCE(COUNT(*),0)+1 FROM recruitment_profile_submission_batches WHERE ClientId=@ClientId", new { hiringCase.ClientId }, transaction);
        var batchNumber = $"SHORTLIST-{DateTime.UtcNow:yyyyMMdd}-{sequence:0000}";
        var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_profile_submission_batches
(ClientId,HiringCaseId,BatchNumber,Status,CreatedByUserId) VALUES (@ClientId,@HiringCaseId,@BatchNumber,'Draft',@UserId);SELECT LAST_INSERT_ID();", new { hiringCase.ClientId, request.HiringCaseId, BatchNumber = batchNumber, UserId = user.Id }, transaction);
        foreach (var application in applications)
            await db.ExecuteAsync(@"INSERT INTO recruitment_profile_submission_batch_items
(BatchId,ApplicationId,CandidateId,ApplicationScoreId,ReadinessStatus) VALUES (@BatchId,@ApplicationId,@CandidateId,@ApplicationScoreId,'Pending')", new { BatchId = id, application.ApplicationId, application.CandidateId, application.ApplicationScoreId }, transaction);
        await RefreshProfileBatchReadinessAsync(db, id, transaction);
        await db.ExecuteAsync(@"INSERT INTO recruitment_position_stage_events
(PositionPipelineInstanceId,PositionStageInstanceId,EventType,EventTitle,EventDetails,ActorUserId)
SELECT batch.HiringCaseId,hiringCase.CurrentStageInstanceId,'ShortlistBatchCreated','Candidate shortlist batch created',batch.BatchNumber,@UserId
FROM recruitment_profile_submission_batches batch
JOIN recruitment_position_pipeline_instances hiringCase ON hiringCase.Id=batch.HiringCaseId WHERE batch.Id=@BatchId", new { BatchId = id, UserId = user.Id }, transaction);
        await transaction.CommitAsync();
        return (await GetProfileBatchAsync(db, id), "");
    }

    public async Task<IReadOnlyList<RecruitmentProfileSubmissionBatch>> ListProfileBatchesAsync(long hiringCaseId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var ids = (await db.QueryAsync<long>(@"SELECT batch.Id FROM recruitment_profile_submission_batches batch
JOIN recruitment_position_pipeline_instances hiringCase ON hiringCase.Id=batch.HiringCaseId
WHERE batch.HiringCaseId=@HiringCaseId AND (@ClientId IS NULL OR hiringCase.ClientId=@ClientId)
ORDER BY batch.CreatedAtUtc DESC,batch.Id DESC", new { HiringCaseId = hiringCaseId, user.ClientId })).ToArray();
        var rows = new List<RecruitmentProfileSubmissionBatch>();
        foreach (var id in ids)
        {
            await RefreshProfileBatchReadinessAsync(db, id);
            var row = await GetProfileBatchAsync(db, id);
            if (row is not null) rows.Add(row);
        }
        return rows;
    }

    public async Task<(RecruitmentProfileSubmissionBatch? Row, string Error)> ApproveProfileBatchAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();
        var source = await db.QueryFirstOrDefaultAsync<ProfileBatchSource>(@"SELECT batch.Id,batch.ClientId,batch.HiringCaseId,batch.Status
FROM recruitment_profile_submission_batches batch
JOIN recruitment_position_pipeline_instances hiringCase ON hiringCase.Id=batch.HiringCaseId
WHERE batch.Id=@Id AND (@ClientId IS NULL OR hiringCase.ClientId=@ClientId) FOR UPDATE", new { Id = id, user.ClientId }, transaction);
        if (source is null) return (null, "Candidate shortlist batch was not found.");
        if (!source.Status.Equals("Draft", StringComparison.OrdinalIgnoreCase)) return (null, "Only a draft shortlist batch can be approved.");
        await RefreshProfileBatchReadinessAsync(db, id, transaction);
        var incomplete = (await db.QueryAsync<string>(@"SELECT CONCAT(candidate.FirstName,' ',candidate.LastName)
FROM recruitment_profile_submission_batch_items item JOIN recruitment_candidates candidate ON candidate.Id=item.CandidateId
WHERE item.BatchId=@Id AND item.ReadinessStatus<>'Ready' ORDER BY item.Id", new { Id = id }, transaction)).ToArray();
        if (incomplete.Length > 0) return (null, $"Complete the configured candidate information before approval: {string.Join(", ", incomplete)}.");
        await db.ExecuteAsync(@"UPDATE recruitment_profile_submission_batches
SET Status='Approved',ApprovedByUserId=@UserId,ApprovedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id", new { Id = id, UserId = user.Id }, transaction);
        await db.ExecuteAsync(@"INSERT INTO recruitment_position_stage_events
(PositionPipelineInstanceId,PositionStageInstanceId,EventType,EventTitle,EventDetails,ActorUserId)
SELECT batch.HiringCaseId,hiringCase.CurrentStageInstanceId,'ShortlistBatchApproved','Candidate shortlist batch approved',batch.BatchNumber,@UserId
FROM recruitment_profile_submission_batches batch JOIN recruitment_position_pipeline_instances hiringCase ON hiringCase.Id=batch.HiringCaseId WHERE batch.Id=@Id",
            new { Id = id, UserId = user.Id }, transaction);
        await transaction.CommitAsync();
        return (await GetProfileBatchAsync(db, id), "");
    }

    public async Task<(RecruitmentProfileSubmissionBatch? Row, string Error)> ForwardProfileBatchAsync(long id, AuthUser user, NotificationRepository notifications)
    {
        await using var db = Db();
        await db.OpenAsync();
        var source = await db.QueryFirstOrDefaultAsync<ProfileBatchForwardSource>(@"SELECT batch.Id,batch.ClientId,batch.HiringCaseId,batch.BatchNumber,batch.Status,
hiringCase.CurrentStageInstanceId,stageInstance.PipelineStageId,stage.StageName,workOrder.WorkOrderNumber,line.PositionName,
workOrder.CreatedByUserId HiringRequesterUserId,hiringCase.PositionId
FROM recruitment_profile_submission_batches batch
JOIN recruitment_position_pipeline_instances hiringCase ON hiringCase.Id=batch.HiringCaseId
JOIN recruitment_work_orders workOrder ON workOrder.Id=hiringCase.WorkOrderId
JOIN recruitment_work_order_lines line ON line.Id=hiringCase.WorkOrderLineId
LEFT JOIN recruitment_position_stage_instances stageInstance ON stageInstance.Id=hiringCase.CurrentStageInstanceId
LEFT JOIN recruitment_pipeline_stages stage ON stage.Id=stageInstance.PipelineStageId
WHERE batch.Id=@Id AND (@ClientId IS NULL OR batch.ClientId=@ClientId)", new { Id = id, user.ClientId });
        if (source is null) return (null, "Candidate shortlist batch was not found.");
        if (!source.Status.Equals("Approved", StringComparison.OrdinalIgnoreCase) && !source.Status.Equals("Forwarded", StringComparison.OrdinalIgnoreCase))
            return (null, "Approve the shortlist batch before forwarding it.");
        if (source.Status.Equals("Forwarded", StringComparison.OrdinalIgnoreCase)) return (await GetProfileBatchAsync(db, id), "");
        var actions = (await db.QueryAsync<ProfileBatchAction>(@"SELECT * FROM recruitment_pipeline_stage_actions
WHERE PipelineStageId=@PipelineStageId AND TriggerEvent='OnProfileBatchForward' AND ActionCode='SEND_NOTIFICATION' AND IsActive=TRUE
ORDER BY ExecutionOrder,Id", new { source.PipelineStageId })).ToList();
        if (actions.Count == 0) return (null, $"Configure an On Profile Batch Forward notification on {source.StageName} before forwarding.");
        var batch = await GetProfileBatchAsync(db, id);
        if (batch is null) return (null, "Candidate shortlist batch was not found.");
        var candidateNames = string.Join(", ", batch.Items.Select(item => item.CandidateName));
        foreach (var action in actions)
        {
            if (action.TemplateId is null or <= 0) return (null, $"Choose a notification template for the shortlist-forward action on {source.StageName}.");
            var recipients = await ResolveProfileBatchRecipientsAsync(db, action.Id, source);
            if (recipients.Count == 0) return (null, $"No active recipient could be resolved for the shortlist-forward action on {source.StageName}.");
            foreach (var recipient in recipients)
            {
                var delivered = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_profile_batch_notification_deliveries
WHERE BatchId=@BatchId AND StageActionId=@StageActionId AND RecipientEmail=@RecipientEmail", new { BatchId = id, StageActionId = action.Id, RecipientEmail = recipient.Email }) > 0;
                if (delivered) continue;
                var queueId = await notifications.QueueTemplateAsync(action.TemplateId.Value, recipient.Email, new NotificationEvent
                {
                    EventCode = "RECRUITMENT_PROFILE_BATCH_FORWARDED",
                    ResourceType = "RecruitmentProfileSubmissionBatch",
                    ResourceId = id.ToString(),
                    ClientId = source.ClientId,
                    ActorUserId = user.Id,
                    ActorName = user.DisplayName,
                    ActorEmail = user.Email,
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        batchId = id,
                        batchNumber = source.BatchNumber,
                        workOrderNumber = source.WorkOrderNumber,
                        positionName = source.PositionName,
                        stageName = source.StageName,
                        candidateCount = batch.Items.Count,
                        candidateNames
                    })
                });
                if (!queueId.HasValue) return (null, $"The shortlist email could not be queued for {recipient.Email}. Check the configured template and address.");
                await db.ExecuteAsync(@"INSERT INTO recruitment_profile_batch_notification_deliveries
(BatchId,StageActionId,RecipientType,RecipientEmail,NotificationQueueId)
VALUES (@BatchId,@StageActionId,@RecipientType,@RecipientEmail,@NotificationQueueId)
ON DUPLICATE KEY UPDATE NotificationQueueId=VALUES(NotificationQueueId)", new { BatchId = id, StageActionId = action.Id, recipient.RecipientType, RecipientEmail = recipient.Email, NotificationQueueId = queueId.Value });
            }
        }
        await db.ExecuteAsync(@"UPDATE recruitment_profile_submission_batches SET Status='Forwarded',ForwardedByUserId=@UserId,
ForwardedAtUtc=COALESCE(ForwardedAtUtc,UTC_TIMESTAMP(6)) WHERE Id=@Id", new { Id = id, UserId = user.Id });
        await db.ExecuteAsync(@"INSERT INTO recruitment_position_stage_events
(PositionPipelineInstanceId,PositionStageInstanceId,EventType,EventTitle,EventDetails,ActorUserId)
VALUES (@HiringCaseId,@StageInstanceId,'ShortlistBatchForwarded','Candidate shortlist batch forwarded',@BatchNumber,@UserId)",
            new { source.HiringCaseId, StageInstanceId = source.CurrentStageInstanceId, source.BatchNumber, UserId = user.Id });
        return (await GetProfileBatchAsync(db, id), "");
    }

    private static async Task<RecruitmentProfileSubmissionBatch?> GetProfileBatchAsync(MySqlConnection db, long id)
    {
        var row = await db.QueryFirstOrDefaultAsync<RecruitmentProfileSubmissionBatch>("SELECT * FROM recruitment_profile_submission_batches WHERE Id=@Id", new { Id = id });
        if (row is null) return null;
        row.Items = (await db.QueryAsync<RecruitmentProfileSubmissionBatchItem>(@"SELECT item.*,CONCAT(candidate.FirstName,' ',candidate.LastName) CandidateName,
score.TotalScore AtsScore,TRIM(BOTH ', ' FROM CONCAT_WS(', ',
CASE WHEN TRIM(candidate.FirstName)='' THEN 'name' END,CASE WHEN TRIM(candidate.Email)='' THEN 'email' END,
CASE WHEN TRIM(candidate.Phone)='' THEN 'contact' END,CASE WHEN candidate.TotalExperienceMonths<=0 THEN 'experience' END,
CASE WHEN TRIM(candidate.HighestQualification)='' THEN 'education' END,CASE WHEN TRIM(candidate.CurrentCompany)='' THEN 'current company' END,
CASE WHEN TRIM(candidate.CurrentTitle)='' THEN 'current designation' END,CASE WHEN candidate.CurrentCtc IS NULL THEN 'current CTC' END,
CASE WHEN candidate.NoticePeriodDays IS NULL THEN 'notice period' END,CASE WHEN candidate.ExpectedCtc IS NULL THEN 'expected CTC' END,
CASE WHEN NOT EXISTS(SELECT 1 FROM recruitment_candidate_certifications certification WHERE certification.CandidateId=candidate.Id) THEN 'certification' END,
CASE WHEN NOT EXISTS(SELECT 1 FROM recruitment_candidate_resumes resume WHERE resume.CandidateId=candidate.Id AND resume.IsPrimary=TRUE) THEN 'resume' END)) MissingFields
FROM recruitment_profile_submission_batch_items item
JOIN recruitment_candidates candidate ON candidate.Id=item.CandidateId
LEFT JOIN recruitment_application_scores score ON score.Id=item.ApplicationScoreId
WHERE item.BatchId=@Id ORDER BY item.Id", new { Id = id })).ToList();
        row.Deliveries = (await db.QueryAsync<RecruitmentProfileBatchNotificationDelivery>(@"SELECT * FROM recruitment_profile_batch_notification_deliveries
WHERE BatchId=@Id ORDER BY CreatedAtUtc,Id", new { Id = id })).ToList();
        return row;
    }

    private static Task RefreshProfileBatchReadinessAsync(MySqlConnection db, long batchId, System.Data.IDbTransaction? transaction = null) =>
        db.ExecuteAsync(@"UPDATE recruitment_profile_submission_batch_items item
JOIN recruitment_candidates candidate ON candidate.Id=item.CandidateId
SET item.ReadinessStatus=CASE WHEN TRIM(candidate.FirstName)<>'' AND TRIM(candidate.Email)<>'' AND TRIM(candidate.Phone)<>''
AND candidate.TotalExperienceMonths>0 AND TRIM(candidate.HighestQualification)<>'' AND TRIM(candidate.CurrentCompany)<>''
AND TRIM(candidate.CurrentTitle)<>'' AND candidate.CurrentCtc IS NOT NULL AND candidate.NoticePeriodDays IS NOT NULL
AND candidate.ExpectedCtc IS NOT NULL
AND EXISTS(SELECT 1 FROM recruitment_candidate_certifications certification WHERE certification.CandidateId=candidate.Id)
AND EXISTS(SELECT 1 FROM recruitment_candidate_resumes resume WHERE resume.CandidateId=candidate.Id AND resume.IsPrimary=TRUE)
THEN 'Ready' ELSE 'Incomplete' END WHERE item.BatchId=@BatchId", new { BatchId = batchId }, transaction);

    private static async Task<IReadOnlyList<ProfileBatchRecipient>> ResolveProfileBatchRecipientsAsync(MySqlConnection db, long stageActionId, ProfileBatchForwardSource context)
    {
        var configured = (await db.QueryAsync<ProfileBatchRecipientConfiguration>(@"SELECT * FROM recruitment_stage_action_recipients
WHERE StageActionId=@Id AND IsActive=TRUE ORDER BY DisplayOrder,Id", new { Id = stageActionId })).ToList();
        var recipients = new List<ProfileBatchRecipient>();
        foreach (var configuration in configured)
        {
            var type = (configuration.RecipientType ?? "").Trim();
            if (type.Equals("SpecificUser", StringComparison.OrdinalIgnoreCase) && configuration.UserId.HasValue)
                AddRange(type, await db.QueryAsync<string>("SELECT Email FROM authusers WHERE Id=@Id AND IsActive=TRUE AND (ClientId IS NULL OR ClientId=@ClientId)", new { Id = configuration.UserId.Value, context.ClientId }));
            else if (type.Equals("UserRole", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(configuration.RoleCode))
                AddRange(type, await db.QueryAsync<string>(@"SELECT DISTINCT userRow.Email FROM authusers userRow
JOIN authuserroles userRole ON userRole.UserId=userRow.Id JOIN authroles roleRow ON roleRow.Id=userRole.RoleId
WHERE roleRow.Code=@RoleCode AND userRow.IsActive=TRUE AND (userRow.ClientId IS NULL OR userRow.ClientId=@ClientId)", new { RoleCode = configuration.RoleCode.Trim(), context.ClientId }));
            else if (type.Equals("StaticEmail", StringComparison.OrdinalIgnoreCase)) Add(type, configuration.EmailAddress);
            else if (type.Equals("StageDefaultPanelMembers", StringComparison.OrdinalIgnoreCase))
                AddRange(type, await db.QueryAsync<string>(@"SELECT DISTINCT userRow.Email FROM recruitment_stage_default_panel_members panel
JOIN authusers userRow ON userRow.Id=panel.PanelUserId AND userRow.IsActive=TRUE
WHERE panel.PipelineStageId=@PipelineStageId AND (userRow.ClientId IS NULL OR userRow.ClientId=@ClientId)", new { context.PipelineStageId, context.ClientId }));
            else if (type.Equals("InterviewPanelMembers", StringComparison.OrdinalIgnoreCase))
                AddRange(type, await db.QueryAsync<string>(@"SELECT DISTINCT userRow.Email FROM recruitment_profile_submission_batch_items item
JOIN recruitment_interviews interviewRow ON interviewRow.ApplicationId=item.ApplicationId
JOIN recruitment_interview_panel_members panel ON panel.InterviewId=interviewRow.Id
JOIN authusers userRow ON userRow.Id=panel.PanelUserId AND userRow.IsActive=TRUE WHERE item.BatchId=@BatchId", new { BatchId = context.Id }));
            else if (type.Equals("HiringRequester", StringComparison.OrdinalIgnoreCase))
                AddRange(type, await db.QueryAsync<string>("SELECT Email FROM authusers WHERE Id=@Id AND IsActive=TRUE", new { Id = context.HiringRequesterUserId }));
            else if (type.Equals("PositionRecruiter", StringComparison.OrdinalIgnoreCase) && context.PositionId.HasValue)
                AddRange(type, await db.QueryAsync<string>(@"SELECT userRow.Email FROM recruitment_open_positions positionRow
JOIN authusers userRow ON userRow.Id=positionRow.RecruiterUserId AND userRow.IsActive=TRUE WHERE positionRow.Id=@PositionId", new { context.PositionId }));
        }
        return recipients.GroupBy(row => row.Email, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();

        void Add(string recipientType, string? email)
        {
            var value = (email ?? "").Trim();
            if (value.Length > 0 && System.Net.Mail.MailAddress.TryCreate(value, out var address)) recipients.Add(new ProfileBatchRecipient(recipientType, address.Address));
        }
        void AddRange(string recipientType, IEnumerable<string> emails) { foreach (var email in emails) Add(recipientType, email); }
    }

    private static Task<IEnumerable<RecruitmentHiringCase>> HiringCaseRowsAsync(MySqlConnection db, int? clientId, long? id) =>
        db.QueryAsync<RecruitmentHiringCase>(@"SELECT hiringCase.*,client.Name ClientName,workOrder.WorkOrderNumber,line.PositionName,line.PayBandLevelCode,line.Division,
definition.PipelineName,stage.StageName CurrentStageName,stage.StakeholderCode CurrentStakeholderCode
FROM recruitment_position_pipeline_instances hiringCase
JOIN clients client ON client.Id=hiringCase.ClientId
JOIN recruitment_work_orders workOrder ON workOrder.Id=hiringCase.WorkOrderId
JOIN recruitment_work_order_lines line ON line.Id=hiringCase.WorkOrderLineId
JOIN recruitment_pipeline_versions version ON version.Id=hiringCase.PipelineVersionId
JOIN recruitment_pipeline_definitions definition ON definition.Id=version.PipelineDefinitionId
LEFT JOIN recruitment_position_stage_instances currentStage ON currentStage.Id=hiringCase.CurrentStageInstanceId
LEFT JOIN recruitment_pipeline_stages stage ON stage.Id=currentStage.PipelineStageId
WHERE (@ClientId IS NULL OR hiringCase.ClientId=@ClientId) AND (@Id IS NULL OR hiringCase.Id=@Id)
ORDER BY hiringCase.UpdatedAtUtc DESC,hiringCase.Id DESC", new { ClientId = clientId, Id = id });

    private static Task<ActiveStageSource?> ActiveCaseStageAsync(MySqlConnection db, long id, int? clientId, System.Data.IDbTransaction? transaction = null) =>
        db.QueryFirstOrDefaultAsync<ActiveStageSource>(@"SELECT hiringCase.Id HiringCaseId,currentStage.Id StageInstanceId,stage.AllowPause,stage.PauseBehavior
FROM recruitment_position_pipeline_instances hiringCase
JOIN recruitment_position_stage_instances currentStage ON currentStage.Id=hiringCase.CurrentStageInstanceId AND currentStage.Status='Active'
JOIN recruitment_pipeline_stages stage ON stage.Id=currentStage.PipelineStageId
WHERE hiringCase.Id=@Id AND hiringCase.Status='Active' AND (@ClientId IS NULL OR hiringCase.ClientId=@ClientId)", new { Id = id, ClientId = clientId }, transaction);

    private static async Task EnsureColumnAsync(MySqlConnection db, string table, string column, string definition)
    {
        var exists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@TableName AND COLUMN_NAME=@ColumnName", new { TableName = table, ColumnName = column });
        if (exists == 0) await db.ExecuteAsync($"ALTER TABLE `{table}` ADD COLUMN `{column}` {definition}");
    }

    private static Task<bool> TableExistsAsync(MySqlConnection db, string table) =>
        db.ExecuteScalarAsync<bool>(@"SELECT COUNT(*)>0 FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@TableName", new { TableName = table });

    private static async Task AssignPositionPipelineAsync(MySqlConnection db, long positionId, long pipelineVersionId, int userId, System.Data.IDbTransaction transaction)
    {
        await db.ExecuteAsync(@"UPDATE recruitment_position_pipeline_assignments SET IsActive=FALSE
WHERE PositionId=@PositionId AND IsActive=TRUE AND PipelineVersionId<>@PipelineVersionId", new { PositionId = positionId, PipelineVersionId = pipelineVersionId }, transaction);
        await db.ExecuteAsync(@"INSERT INTO recruitment_position_pipeline_assignments
(PositionId,JobPostingId,PipelineVersionId,IsActive,AssignedByUserId)
SELECT @PositionId,NULL,@PipelineVersionId,TRUE,@UserId FROM DUAL
WHERE NOT EXISTS (SELECT 1 FROM recruitment_position_pipeline_assignments
WHERE PositionId=@PositionId AND PipelineVersionId=@PipelineVersionId AND IsActive=TRUE)", new { PositionId = positionId, PipelineVersionId = pipelineVersionId, UserId = userId }, transaction);
    }

    private static string Canonical(HashSet<string> values, string? input, string fallback) =>
        values.FirstOrDefault(value => value.Equals((input ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) ?? fallback;

    private sealed class StartCaseSource
    {
        public long WorkOrderLineId { get; set; }
        public long WorkOrderId { get; set; }
        public long? RequisitionId { get; set; }
        public long? PositionId { get; set; }
        public int ClientId { get; set; }
        public DateTime ReceivedAtUtc { get; set; }
        public int OverallSlaMinutes { get; set; }
        public long PipelineVersionId { get; set; }
        public string PipelineStatus { get; set; } = "";
        public string ScopeType { get; set; } = "Application";
        public string SlaMode { get; set; } = "StageEntry";
        public int PipelineOverallSlaMinutes { get; set; }
    }

    private sealed class StageDefinition
    {
        public long Id { get; set; }
        public string StageCode { get; set; } = "";
        public string StageName { get; set; } = "";
        public int DisplayOrder { get; set; }
        public int? TargetOffsetMinutes { get; set; }
        public bool IsInitial { get; set; }
        public bool IsTerminal { get; set; }
    }

    private class CurrentStageSource
    {
        public long HiringCaseId { get; set; }
        public int ClientId { get; set; }
        public long PipelineVersionId { get; set; }
        public long CurrentStageInstanceId { get; set; }
        public long PipelineStageId { get; set; }
        public int DisplayOrder { get; set; }
        public string StageName { get; set; } = "";
        public bool IsTerminal { get; set; }
        public bool RequiresApproval { get; set; }
        public long? ApprovalWorkflowId { get; set; }
    }

    private sealed class HiringCaseAdvanceRequestSource : CurrentStageSource
    {
        public long Id { get; set; }
        public long PositionStageInstanceId { get; set; }
        public string OutcomeCode { get; set; } = "ADVANCE";
        public string Reason { get; set; } = "";
        public string Status { get; set; } = "Pending Approval";
    }

    private sealed class HiringCaseAdvanceState
    {
        public long Id { get; set; }
        public string Status { get; set; } = "";
        public long? WorkflowInstanceId { get; set; }
    }

    private sealed class NextStageSource
    {
        public long StageInstanceId { get; set; }
        public long PipelineStageId { get; set; }
        public string StageName { get; set; } = "";
        public bool IsTerminal { get; set; }
    }

    private sealed class ActiveStageSource
    {
        public long HiringCaseId { get; set; }
        public long StageInstanceId { get; set; }
        public bool AllowPause { get; set; }
        public string PauseBehavior { get; set; } = "ShiftStageAndOverall";
    }

    private sealed class PauseSource
    {
        public long Id { get; set; }
        public DateTime PausedAtUtc { get; set; }
    }

    private sealed class ProcessDocumentGenerationContext : RecruitmentProcessDocument
    {
        public int TemplateClientId { get; set; }
        public string TemplateType { get; set; } = "";
        public string SubjectTemplate { get; set; } = "";
        public string BodyTemplate { get; set; } = "";
        public bool TemplateIsActive { get; set; }
        public string ClientName { get; set; } = "";
        public string WorkOrderNumber { get; set; } = "";
        public DateTime? WorkOrderReceivedAt { get; set; }
        public long? PositionId { get; set; }
        public string PositionName { get; set; } = "";
        public string PayBandLevelCode { get; set; } = "";
        public string Location { get; set; } = "";
        public string Division { get; set; } = "";
        public string StageName { get; set; } = "";
        public string CandidateFirstName { get; set; } = "";
        public string CandidateLastName { get; set; } = "";
        public string CandidateEmail { get; set; } = "";
        public DateTime? InterviewDate { get; set; }
    }

    private sealed class SelectionCommitteeCandidate
    {
        public long ApplicationId { get; set; }
        public string CandidateName { get; set; } = "";
        public long? InterviewId { get; set; }
        public DateTime? ScheduledStart { get; set; }
        public string InterviewStatus { get; set; } = "";
        public string Result { get; set; } = "";
        public decimal OverallScore { get; set; }
        public long? RoundConfigurationId { get; set; }
    }

    private sealed class SelectionCommitteePanelMember
    {
        public int PanelUserId { get; set; }
        public string PanelName { get; set; } = "";
        public string PanelRole { get; set; } = "Panelist";
        public string Designation { get; set; } = "";
        public string OrganisationName { get; set; } = "";
    }

    private sealed class SelectionCommitteeCompetency
    {
        public long StageCompetencyId { get; set; }
        public string CompetencyName { get; set; } = "";
        public decimal MaximumScore { get; set; }
        public int DisplayOrder { get; set; }
    }

    private sealed class SelectionCommitteeScore
    {
        public long InterviewId { get; set; }
        public long InterviewStageCompetencyId { get; set; }
        public decimal AwardedScore { get; set; }
    }

    private sealed class BatchApplicationSource
    {
        public long ApplicationId { get; set; }
        public long CandidateId { get; set; }
        public long PositionId { get; set; }
        public string CurrentStage { get; set; } = "";
        public long? ApplicationScoreId { get; set; }
    }

    private class ProfileBatchSource
    {
        public long Id { get; set; }
        public int ClientId { get; set; }
        public long HiringCaseId { get; set; }
        public string Status { get; set; } = "";
    }

    private sealed class ProfileBatchForwardSource : ProfileBatchSource
    {
        public string BatchNumber { get; set; } = "";
        public long? CurrentStageInstanceId { get; set; }
        public long PipelineStageId { get; set; }
        public string StageName { get; set; } = "";
        public string WorkOrderNumber { get; set; } = "";
        public string PositionName { get; set; } = "";
        public int HiringRequesterUserId { get; set; }
        public long? PositionId { get; set; }
    }

    private sealed class ProfileBatchAction
    {
        public long Id { get; set; }
        public long? TemplateId { get; set; }
    }

    private sealed class ProfileBatchRecipientConfiguration
    {
        public string RecipientType { get; set; } = "";
        public int? UserId { get; set; }
        public string RoleCode { get; set; } = "";
        public string EmailAddress { get; set; } = "";
    }

    private sealed record ProfileBatchRecipient(string RecipientType, string Email);
}
