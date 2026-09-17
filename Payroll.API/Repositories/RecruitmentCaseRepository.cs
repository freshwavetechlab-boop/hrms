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
    INDEX IX_recruitment_position_stage_definition (PositionPipelineInstanceId,PipelineStageId,Id),
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
CREATE TABLE IF NOT EXISTS recruitment_process_document_signatures (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ProcessDocumentId BIGINT NOT NULL,
    ClientId INT NOT NULL,
    SignerUserId INT NOT NULL,
    SignerName VARCHAR(190) NOT NULL,
    SignerRole VARCHAR(100) NOT NULL DEFAULT '',
    SignatureMethod VARCHAR(20) NOT NULL,
    SignatureDataUrl MEDIUMTEXT NOT NULL,
    SignedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY UX_recruitment_process_document_signer (ProcessDocumentId,SignerUserId),
    INDEX IX_recruitment_process_document_signature_client (ClientId,ProcessDocumentId),
    CONSTRAINT FK_recruitment_process_document_signature_document FOREIGN KEY (ProcessDocumentId) REFERENCES recruitment_process_documents(Id) ON DELETE CASCADE
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
        await AllowPositionStageReentryAsync(db);

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
        var rows = (await db.QueryAsync<RecruitmentWorkOrder>(@"SELECT workOrder.*,client.Name ClientName,
(SELECT COUNT(*) FROM recruitment_work_order_lines line WHERE line.WorkOrderId=workOrder.Id AND line.RequisitionId IS NOT NULL) LineCount,
(SELECT COUNT(*) FROM recruitment_position_pipeline_instances hiringCase WHERE hiringCase.WorkOrderId=workOrder.Id AND hiringCase.Status IN ('Active','Candidate Flow')) OpenCaseCount
FROM recruitment_work_orders workOrder
JOIN clients client ON client.Id=workOrder.ClientId
WHERE (@ClientId IS NULL OR workOrder.ClientId=@ClientId)
AND (@Query='' OR workOrder.WorkOrderNumber LIKE CONCAT('%',@Query,'%') OR workOrder.Subject LIKE CONCAT('%',@Query,'%'))
ORDER BY workOrder.ReceivedAtUtc DESC,workOrder.Id DESC", new { ClientId = effectiveClientId, Query = (query ?? "").Trim() })).ToList();
        if (!RecruitmentAccessScope.IsRestricted(user)) return rows;
        var visible = new List<RecruitmentWorkOrder>();
        foreach (var row in rows)
        {
            var locations = await db.QueryAsync<string>("SELECT Location FROM recruitment_work_order_lines WHERE WorkOrderId=@Id", new { row.Id });
            if (!locations.Any())
            {
                if (user.ClientId == row.ClientId) visible.Add(row);
                continue;
            }
            if ((await RecruitmentAccessScope.FilterAsync(db, user, locations, _ => row.ClientId, location => location)).Count > 0) visible.Add(row);
        }
        return visible;
    }

    public async Task<RecruitmentWorkOrder?> GetWorkOrderAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var row = await db.QueryFirstOrDefaultAsync<RecruitmentWorkOrder>(@"SELECT workOrder.*,client.Name ClientName,
(SELECT COUNT(*) FROM recruitment_work_order_lines line WHERE line.WorkOrderId=workOrder.Id AND line.RequisitionId IS NOT NULL) LineCount,
(SELECT COUNT(*) FROM recruitment_position_pipeline_instances hiringCase WHERE hiringCase.WorkOrderId=workOrder.Id AND hiringCase.Status IN ('Active','Candidate Flow')) OpenCaseCount
FROM recruitment_work_orders workOrder JOIN clients client ON client.Id=workOrder.ClientId
WHERE workOrder.Id=@Id AND (@ClientId IS NULL OR workOrder.ClientId=@ClientId)", new { Id = id, user.ClientId });
        if (row is null) return null;
        row.Lines = (await db.QueryAsync<RecruitmentWorkOrderLine>("SELECT * FROM recruitment_work_order_lines WHERE WorkOrderId=@Id ORDER BY LineNumber,Id", new { Id = id })).ToList();
        row.Lines = await RecruitmentAccessScope.FilterAsync(db, user, row.Lines, _ => row.ClientId, line => line.Location);
        if (RecruitmentAccessScope.IsRestricted(user) && row.Lines.Count == 0 && user.ClientId != row.ClientId) return null;
        row.LineCount = row.Lines.Count(line => line.RequisitionId is > 0);
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
        // Work orders capture demand and the anchor date only. Published pipeline
        // versions are the single source of truth for every SLA target.
        request.OverallSlaMinutes = 0;
        var duplicateLine = request.Lines.GroupBy(row => row.LineNumber).FirstOrDefault(group => group.Key <= 0 || group.Count() > 1);
        if (duplicateLine is not null) return (null, "Every work order line needs a unique positive line number.");
        if (request.Lines.Any(row => string.IsNullOrWhiteSpace(row.PositionName) || row.NumberOfPositions <= 0))
            return (null, "Every work order line needs a position name and at least one position.");

        await using var db = Db();
        await db.OpenAsync();
        foreach (var line in request.Lines)
            if (!await RecruitmentAccessScope.CanAccessLocationAsync(db, user, request.ClientId, line.Location)) return (null, "A work-order line is outside your recruitment visibility scope.");
        if (request.Id > 0 && RecruitmentAccessScope.IsRestricted(user))
        {
            var existingLocations = (await db.QueryAsync<string>("SELECT Location FROM recruitment_work_order_lines WHERE WorkOrderId=@Id", new { request.Id })).ToList();
            var visibleExisting = await RecruitmentAccessScope.FilterAsync(db, user, existingLocations, _ => request.ClientId, location => location);
            if (visibleExisting.Count != existingLocations.Count) return (null, "This work order includes locations outside your scope and cannot be edited from a restricted account.");
        }
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
        DateTime? dueAt = null;
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

    public async Task<(IReadOnlyList<RecruitmentHiringCase> Rows, string Error)> EnsureHiringCasesForWorkOrderAsync(long workOrderId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var workOrder = await db.QueryFirstOrDefaultAsync<WorkOrderSlaSource>(@"SELECT Id,ClientId,WorkOrderNumber,Subject,Status
FROM recruitment_work_orders WHERE Id=@Id AND (@ClientId IS NULL OR ClientId=@ClientId)", new { Id = workOrderId, user.ClientId });
        if (workOrder is null) return ([], "Work order was not found in your permitted client scope.");
        if (workOrder.Status is "Completed" or "Cancelled" or "On Hold") return ([], "");

        var pipelineVersions = (await db.QueryAsync<long>(@"SELECT DISTINCT versionRow.Id
FROM recruitment_pipeline_definitions definition
JOIN recruitment_pipeline_versions versionRow ON versionRow.Id=definition.CurrentPublishedVersionId
WHERE definition.ClientId=@ClientId AND definition.IsActive=TRUE AND versionRow.Status='Published'
  AND versionRow.ScopeType IN ('Position','Hybrid')", new { workOrder.ClientId })).ToList();
        if (pipelineVersions.Count != 1) return ([], pipelineVersions.Count == 0
            ? "Publish one Position or Hybrid pipeline to start the work-order SLA automatically."
            : "Keep one current Position or Hybrid pipeline for this client so the work-order SLA can start automatically.");

        var lineIds = (await db.QueryAsync<long>(
            "SELECT Id FROM recruitment_work_order_lines WHERE WorkOrderId=@Id ORDER BY LineNumber,Id", new { Id = workOrderId })).ToList();
        if (lineIds.Count == 0)
        {
            var positionName = FirstValue(workOrder.Subject, workOrder.WorkOrderNumber, "Hiring request pending");
            var lineId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_work_order_lines
(WorkOrderId,LineNumber,PositionName,PayBandLevelCode,NumberOfPositions,Location,Division,RequisitionId,PositionId,Status)
VALUES (@WorkOrderId,1,@PositionName,'',1,'','',NULL,NULL,'Open');SELECT LAST_INSERT_ID();",
                new { WorkOrderId = workOrderId, PositionName = positionName });
            lineIds.Add(lineId);
        }

        var rows = new List<RecruitmentHiringCase>();
        foreach (var lineId in lineIds)
        {
            var (row, error) = await StartHiringCaseAsync(new StartRecruitmentHiringCaseRequest
            {
                WorkOrderLineId = lineId,
                PipelineVersionId = pipelineVersions[0]
            }, user);
            if (row is null && error.Length > 0) return (rows, error);
            if (row is not null) rows.Add(row);
        }
        return (rows, "");
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
        await db.ExecuteAsync(@"DELETE signatureRow FROM recruitment_process_document_signatures signatureRow
JOIN recruitment_process_documents documentRow ON documentRow.Id=signatureRow.ProcessDocumentId
WHERE documentRow.HiringCaseId=@Id", new { Id = id }, transaction);
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
        var existingCaseId = await db.ExecuteScalarAsync<long?>(@"SELECT Id FROM recruitment_position_pipeline_instances
WHERE WorkOrderLineId=@WorkOrderLineId AND (@ClientId IS NULL OR ClientId=@ClientId) LIMIT 1", new { request.WorkOrderLineId, user.ClientId });
        if (existingCaseId is > 0) return (await GetHiringCaseAsync(existingCaseId.Value, user), "");
        var source = await db.QueryFirstOrDefaultAsync<StartCaseSource>(@"SELECT line.Id WorkOrderLineId,line.WorkOrderId,line.RequisitionId,line.PositionId,
workOrder.ClientId,workOrder.ReceivedAtUtc,COALESCE(requisition.RequestDate,workOrder.ReceivedAtUtc) SlaAnchorAtUtc,
line.Location,version.Id PipelineVersionId,version.Status PipelineStatus,
version.ScopeType,version.SlaMode,version.OverallSlaMinutes PipelineOverallSlaMinutes
FROM recruitment_work_order_lines line
JOIN recruitment_work_orders workOrder ON workOrder.Id=line.WorkOrderId
LEFT JOIN recruitment_requisitions requisition ON requisition.Id=line.RequisitionId
CROSS JOIN recruitment_pipeline_versions version
JOIN recruitment_pipeline_definitions definition ON definition.Id=version.PipelineDefinitionId AND definition.ClientId=workOrder.ClientId
WHERE line.Id=@WorkOrderLineId AND version.Id=@PipelineVersionId AND (@ClientId IS NULL OR workOrder.ClientId=@ClientId)", new { request.WorkOrderLineId, request.PipelineVersionId, user.ClientId });
        if (source is null) return (null, "Work order line or pipeline version was not found for this client.");
        if (!await RecruitmentAccessScope.CanAccessLocationAsync(db, user, source.ClientId, source.Location)) return (null, "Work order line was not found in your permitted location scope.");
        if (!source.PipelineStatus.Equals("Published", StringComparison.OrdinalIgnoreCase)) return (null, "Publish the pipeline version before starting a hiring case.");
        if (source.ScopeType.Equals("Application", StringComparison.OrdinalIgnoreCase)) return (null, "Select a Position or Hybrid pipeline for the work-order hiring case.");
        var stages = (await db.QueryAsync<StageDefinition>(@"SELECT Id,StageCode,StageName,CardScope,DisplayOrder,TargetOffsetMinutes,IsInitial,IsTerminal
FROM recruitment_pipeline_stages WHERE PipelineVersionId=@Id AND CardScope='Position' AND IsActive=TRUE ORDER BY DisplayOrder,Id", new { Id = request.PipelineVersionId })).ToList();
        var initial = stages.SingleOrDefault(row => row.IsInitial);
        if (initial is null) return (null, "The pipeline needs exactly one active initial Position stage.");

        await using var transaction = await db.BeginTransactionAsync();
        var overallMinutes = source.PipelineOverallSlaMinutes;
        var overallDue = overallMinutes > 0 ? source.SlaAnchorAtUtc.AddMinutes(overallMinutes) : (DateTime?)null;
        var inserted = await db.ExecuteAsync(@"INSERT IGNORE INTO recruitment_position_pipeline_instances
(ClientId,WorkOrderId,WorkOrderLineId,RequisitionId,PositionId,PipelineVersionId,SlaAnchorAtUtc,OverallDueAtUtc,Status,StartedByUserId)
VALUES (@ClientId,@WorkOrderId,@WorkOrderLineId,@RequisitionId,@PositionId,@PipelineVersionId,@SlaAnchorAtUtc,@OverallDueAtUtc,'Active',@UserId)", new { source.ClientId, source.WorkOrderId, source.WorkOrderLineId, source.RequisitionId, source.PositionId, source.PipelineVersionId, source.SlaAnchorAtUtc, OverallDueAtUtc = overallDue, UserId = user.Id }, transaction);
        if (inserted == 0)
        {
            await transaction.RollbackAsync();
            existingCaseId = await db.ExecuteScalarAsync<long?>("SELECT Id FROM recruitment_position_pipeline_instances WHERE WorkOrderLineId=@WorkOrderLineId LIMIT 1", new { request.WorkOrderLineId });
            return existingCaseId is > 0
                ? (await GetHiringCaseAsync(existingCaseId.Value, user), "")
                : (null, "The hiring journey could not be initialized. Refresh and try again.");
        }
        var caseId = await db.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID()", transaction: transaction);
        long initialInstanceId = 0;
        foreach (var stage in stages)
        {
            var active = stage.Id == initial.Id;
            var dueAt = stage.TargetOffsetMinutes.HasValue ? source.SlaAnchorAtUtc.AddMinutes(stage.TargetOffsetMinutes.Value) : (DateTime?)null;
            var stageId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_position_stage_instances
(PositionPipelineInstanceId,PipelineStageId,Status,EnteredAtUtc,DueAtUtc)
VALUES (@CaseId,@StageId,@Status,@EnteredAtUtc,@DueAtUtc);SELECT LAST_INSERT_ID();", new { CaseId = caseId, StageId = stage.Id, Status = active ? "Active" : "Pending", EnteredAtUtc = active ? source.SlaAnchorAtUtc : (DateTime?)null, DueAtUtc = dueAt }, transaction);
            if (active) initialInstanceId = stageId;
        }
        await db.ExecuteAsync("UPDATE recruitment_position_pipeline_instances SET CurrentStageInstanceId=@StageId WHERE Id=@Id", new { Id = caseId, StageId = initialInstanceId }, transaction);
        await db.ExecuteAsync(@"UPDATE recruitment_work_order_lines SET Status=CASE WHEN Status='Open' THEN 'In Progress' ELSE Status END WHERE Id=@LineId;
UPDATE recruitment_work_orders SET Status=CASE WHEN Status='Draft' THEN 'Active' ELSE Status END WHERE Id=@WorkOrderId", new { LineId = source.WorkOrderLineId, source.WorkOrderId }, transaction);
        await db.ExecuteAsync(@"INSERT INTO recruitment_position_stage_events
(PositionPipelineInstanceId,PositionStageInstanceId,EventType,EventTitle,EventDetails,ActorUserId)
VALUES (@CaseId,@StageId,'CaseStarted','Hiring case started','Work-order SLA clock started from the received timestamp.',@UserId)", new { CaseId = caseId, StageId = initialInstanceId, UserId = user.Id }, transaction);
        if (source.PositionId is > 0) await AssignPositionPipelineAsync(db, source.PositionId.Value, source.PipelineVersionId, user.Id, transaction);
        await transaction.CommitAsync();
        return (await GetHiringCaseAsync(caseId, user), "");
    }

    public async Task<(RecruitmentHiringCase? Row, string Error)> EnsureHiringCaseForRequisitionAsync(long requisitionId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var workOrderError = await EnsureAutomaticWorkOrderLinkAsync(db, requisitionId, user);
        if (workOrderError.Length > 0) return (null, workOrderError);
        var link = await db.QueryFirstOrDefaultAsync<AutomaticCaseSource>(@"SELECT line.Id WorkOrderLineId,workOrder.ClientId,
hiringCase.Id HiringCaseId,
(SELECT assignment.PipelineVersionId FROM recruitment_position_pipeline_assignments assignment
 WHERE assignment.PositionId=COALESCE(line.PositionId,requisition.OpenPositionId) AND assignment.IsActive=TRUE
 ORDER BY assignment.AssignedAtUtc DESC,assignment.Id DESC LIMIT 1) AssignedPipelineVersionId
FROM recruitment_requisitions requisition
JOIN recruitment_work_order_lines line ON line.RequisitionId=requisition.Id
JOIN recruitment_work_orders workOrder ON workOrder.Id=line.WorkOrderId
LEFT JOIN recruitment_position_pipeline_instances hiringCase ON hiringCase.WorkOrderLineId=line.Id
WHERE requisition.Id=@RequisitionId AND (@ClientId IS NULL OR requisition.ClientId=@ClientId)
LIMIT 1", new { RequisitionId = requisitionId, user.ClientId });
        if (link is null) return (null, "");
        if (link.HiringCaseId is > 0) return (await GetHiringCaseAsync(link.HiringCaseId.Value, user), "");

        var pipelineVersionId = link.AssignedPipelineVersionId;
        if (pipelineVersionId is null or <= 0)
        {
            var published = (await db.QueryAsync<long>(@"SELECT DISTINCT versionRow.Id
FROM recruitment_pipeline_definitions definition
JOIN recruitment_pipeline_versions versionRow ON versionRow.Id=definition.CurrentPublishedVersionId
WHERE definition.ClientId=@ClientId AND definition.IsActive=TRUE AND versionRow.Status='Published'
  AND versionRow.ScopeType IN ('Position','Hybrid')", new { link.ClientId })).ToList();
            if (published.Count == 0)
            {
                published = (await db.QueryAsync<long>(@"SELECT DISTINCT versionRow.Id
FROM recruitment_pipeline_versions versionRow
JOIN recruitment_pipeline_definitions definition ON definition.Id=versionRow.PipelineDefinitionId
WHERE definition.ClientId=@ClientId AND definition.IsActive=TRUE AND versionRow.Status='Published'
  AND versionRow.ScopeType IN ('Position','Hybrid')", new { link.ClientId })).ToList();
            }
            if (published.Count != 1) return (null, published.Count == 0
                ? "Publish one Position or Hybrid pipeline to start the request SLA automatically."
                : "Keep one current Position or Hybrid pipeline for this client, or assign a pipeline to the position.");
            pipelineVersionId = published[0];
        }
        return await StartHiringCaseAsync(new StartRecruitmentHiringCaseRequest
        {
            WorkOrderLineId = link.WorkOrderLineId,
            PipelineVersionId = pipelineVersionId.Value
        }, user);
    }

    public async Task<(RecruitmentHiringCase? Row, string Error)> EnsureHiringCaseForJobPostingAsync(long jobPostingId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var requisitionId = await db.ExecuteScalarAsync<long?>(@"SELECT positionRow.RequisitionId
FROM recruitment_job_postings posting
JOIN recruitment_open_positions positionRow ON positionRow.Id=posting.PositionId
WHERE posting.Id=@JobPostingId AND (@ClientId IS NULL OR posting.ClientId=@ClientId)", new { JobPostingId = jobPostingId, user.ClientId });
        return requisitionId is > 0
            ? await EnsureHiringCaseForRequisitionAsync(requisitionId.Value, user)
            : (null, "The job posting's hiring request was not found.");
    }

    private static async Task<string> EnsureAutomaticWorkOrderLinkAsync(MySqlConnection db, long requisitionId, AuthUser user)
    {
        await using var transaction = await db.BeginTransactionAsync();
        var source = await db.QueryFirstOrDefaultAsync<AutomaticWorkOrderSource>(@"SELECT requisition.Id,requisition.RfrNumber,requisition.RequestDate,requisition.ClientId,
requisition.PositionTitle,requisition.NumberOfOpenings,requisition.JobLocation,requisition.BusinessUnit,requisition.Department,
requisition.OpenPositionId,requisition.WorkOrderId,requisition.WorkOrderLineNumber,client.Name ClientName
FROM recruitment_requisitions requisition
JOIN clients client ON client.Id=requisition.ClientId
WHERE requisition.Id=@RequisitionId AND (@ClientId IS NULL OR requisition.ClientId=@ClientId)
FOR UPDATE", new { RequisitionId = requisitionId, user.ClientId }, transaction);
        if (source is null)
        {
            await transaction.RollbackAsync();
            return "Hiring request was not found in your permitted client scope.";
        }

        var existingLine = await db.QueryFirstOrDefaultAsync<(long Id, long WorkOrderId, int LineNumber)>(@"SELECT Id,WorkOrderId,LineNumber
FROM recruitment_work_order_lines WHERE RequisitionId=@RequisitionId LIMIT 1 FOR UPDATE",
            new { RequisitionId = requisitionId }, transaction);
        if (existingLine.Id > 0)
        {
            await db.ExecuteAsync(@"UPDATE recruitment_requisitions SET WorkOrderId=@WorkOrderId,WorkOrderLineNumber=@LineNumber WHERE Id=@Id",
                new { existingLine.WorkOrderId, existingLine.LineNumber, Id = requisitionId }, transaction);
            await transaction.CommitAsync();
            return "";
        }

        long workOrderId;
        if (source.WorkOrderId is > 0)
        {
            workOrderId = source.WorkOrderId.Value;
            var valid = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_work_orders WHERE Id=@Id AND ClientId=@ClientId",
                new { Id = workOrderId, source.ClientId }, transaction);
            if (valid == 0)
            {
                await transaction.RollbackAsync();
                return "The hiring request's work order is outside its client scope.";
            }
        }
        else
        {
            var workOrderNumber = $"AUTO-WO-{source.RfrNumber}";
            workOrderId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_work_orders
(ClientId,WorkOrderNumber,ReceivedAtUtc,ReceivedFrom,Subject,Remarks,Status,OverallSlaMinutes,DueAtUtc,CreatedByUserId)
VALUES (@ClientId,@WorkOrderNumber,@ReceivedAtUtc,@ReceivedFrom,'',@Remarks,'Active',0,NULL,@UserId)
ON DUPLICATE KEY UPDATE Id=LAST_INSERT_ID(Id),ReceivedAtUtc=VALUES(ReceivedAtUtc),ReceivedFrom=VALUES(ReceivedFrom),
Status=CASE WHEN Status IN ('Completed','Cancelled') THEN Status ELSE 'Active' END;
SELECT LAST_INSERT_ID();", new
            {
                source.ClientId,
                WorkOrderNumber = workOrderNumber,
                ReceivedAtUtc = source.RequestDate.Date,
                ReceivedFrom = source.ClientName,
                Remarks = $"System-generated from direct hiring request {source.RfrNumber}.",
                UserId = user.Id
            }, transaction);
        }

        var reusableLine = source.WorkOrderId is > 0
            ? await db.QueryFirstOrDefaultAsync<(long Id, int LineNumber)>(@"SELECT Id,LineNumber
FROM recruitment_work_order_lines
WHERE WorkOrderId=@WorkOrderId AND RequisitionId IS NULL AND PositionId IS NULL
  AND (@RequestedLineNumber IS NULL OR LineNumber=@RequestedLineNumber)
ORDER BY LineNumber,Id LIMIT 1 FOR UPDATE", new
            {
                WorkOrderId = workOrderId,
                RequestedLineNumber = source.WorkOrderLineNumber is > 0 ? source.WorkOrderLineNumber : null
            }, transaction)
            : default;
        var lineNumber = reusableLine.Id > 0
            ? reusableLine.LineNumber
            : source.WorkOrderLineNumber is > 0
                ? source.WorkOrderLineNumber.Value
                : await db.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(LineNumber),0)+1 FROM recruitment_work_order_lines WHERE WorkOrderId=@WorkOrderId",
                    new { WorkOrderId = workOrderId }, transaction);
        var occupied = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_work_order_lines
WHERE WorkOrderId=@WorkOrderId AND LineNumber=@LineNumber AND RequisitionId IS NOT NULL AND RequisitionId<>@RequisitionId",
            new { WorkOrderId = workOrderId, LineNumber = lineNumber, RequisitionId = requisitionId }, transaction);
        if (occupied > 0)
            lineNumber = await db.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(LineNumber),0)+1 FROM recruitment_work_order_lines WHERE WorkOrderId=@WorkOrderId",
                new { WorkOrderId = workOrderId }, transaction);

        var lineId = reusableLine.Id;
        if (lineId > 0)
        {
            await db.ExecuteAsync(@"UPDATE recruitment_work_order_lines SET PositionName=@PositionName,
PayBandLevelCode='',NumberOfPositions=@NumberOfPositions,Location=@Location,Division=@Division,
RequisitionId=@RequisitionId,PositionId=@PositionId,Status='Open' WHERE Id=@LineId", new
            {
                PositionName = source.PositionTitle,
                NumberOfPositions = Math.Max(1, source.NumberOfOpenings),
                Location = source.JobLocation ?? "",
                Division = string.IsNullOrWhiteSpace(source.BusinessUnit) ? source.Department ?? "" : source.BusinessUnit,
                RequisitionId = requisitionId,
                PositionId = source.OpenPositionId,
                LineId = lineId
            }, transaction);
        }
        else
        {
            lineId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_work_order_lines
(WorkOrderId,LineNumber,PositionName,PayBandLevelCode,NumberOfPositions,Location,Division,RequisitionId,PositionId,Status)
VALUES (@WorkOrderId,@LineNumber,@PositionName,'',@NumberOfPositions,@Location,@Division,@RequisitionId,@PositionId,'Open');
SELECT LAST_INSERT_ID();", new
            {
                WorkOrderId = workOrderId,
                LineNumber = lineNumber,
                PositionName = source.PositionTitle,
                NumberOfPositions = Math.Max(1, source.NumberOfOpenings),
                Location = source.JobLocation ?? "",
                Division = string.IsNullOrWhiteSpace(source.BusinessUnit) ? source.Department ?? "" : source.BusinessUnit,
                RequisitionId = requisitionId,
                PositionId = source.OpenPositionId
            }, transaction);
        }
        await db.ExecuteAsync(@"UPDATE recruitment_requisitions SET WorkOrderId=@WorkOrderId,WorkOrderLineNumber=@LineNumber WHERE Id=@Id;
UPDATE recruitment_position_pipeline_instances SET WorkOrderId=@WorkOrderId,WorkOrderLineId=@LineId,RequisitionId=@Id,
PositionId=COALESCE(@PositionId,PositionId) WHERE WorkOrderLineId=@LineId OR RequisitionId=@Id;",
            new { WorkOrderId = workOrderId, LineId = lineId, Id = requisitionId, LineNumber = lineNumber, PositionId = source.OpenPositionId }, transaction);
        await transaction.CommitAsync();
        return "";
    }

    public async Task<IReadOnlyList<RecruitmentHiringCase>> ListHiringCasesAsync(AuthUser user, int? clientId = null)
    {
        await using var db = Db();
        await db.OpenAsync();
        var rows = await HiringCaseRowsAsync(db, user.ClientId ?? clientId, null);
        return await RecruitmentAccessScope.FilterAsync(db, user, rows, row => row.ClientId, row => row.Location);
    }

    public async Task<RecruitmentHiringCase?> GetHiringCaseAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var row = (await HiringCaseRowsAsync(db, user.ClientId, id)).FirstOrDefault();
        if (row is null || !await RecruitmentAccessScope.CanAccessLocationAsync(db, user, row.ClientId, row.Location)) return null;
        row.Stages = (await db.QueryAsync<RecruitmentHiringCaseStage>(@"SELECT instance.*,stage.StageCode,stage.StageName,stage.DisplayOrder,stage.StakeholderCode,stage.TargetOffsetMinutes,
stage.AllowPause,stage.PauseBehavior,stage.RequiresApproval,stage.ApprovalWorkflowId,stage.IsTerminal,
EXISTS(SELECT 1 FROM recruitment_position_stage_pause_periods pauseRow WHERE pauseRow.PositionStageInstanceId=instance.Id AND pauseRow.ResumedAtUtc IS NULL) IsPaused,
(instance.EnteredAtUtc IS NOT NULL AND instance.DueAtUtc IS NOT NULL
 AND COALESCE(instance.CompletedAtUtc,UTC_TIMESTAMP(6))>instance.DueAtUtc) IsSlaBreached,
CASE WHEN instance.EnteredAtUtc IS NULL THEN 0 ELSE GREATEST(0,TIMESTAMPDIFF(SECOND,instance.EnteredAtUtc,COALESCE(instance.CompletedAtUtc,UTC_TIMESTAMP(6)))-instance.PausedDurationSeconds-
COALESCE((SELECT SUM(TIMESTAMPDIFF(SECOND,pauseRow.PausedAtUtc,UTC_TIMESTAMP(6))) FROM recruitment_position_stage_pause_periods pauseRow WHERE pauseRow.PositionStageInstanceId=instance.Id AND pauseRow.ResumedAtUtc IS NULL),0)) END ActiveDurationSeconds
FROM recruitment_position_stage_instances instance
JOIN recruitment_pipeline_stages stage ON stage.Id=instance.PipelineStageId
WHERE instance.PositionPipelineInstanceId=@Id
ORDER BY CASE WHEN instance.EnteredAtUtc IS NULL THEN 1 ELSE 0 END,instance.EnteredAtUtc,instance.Id,stage.DisplayOrder", new { Id = id })).ToList();
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
        row.Events = (await db.QueryAsync<RecruitmentHiringCaseEvent>(@"SELECT eventRow.*,
COALESCE(actor.DisplayName,actor.Email,'System') ActorName
FROM recruitment_position_stage_events eventRow
LEFT JOIN authusers actor ON actor.Id=eventRow.ActorUserId
WHERE eventRow.PositionPipelineInstanceId=@Id
ORDER BY eventRow.CreatedAtUtc,eventRow.Id", new { Id = id })).ToList();
        if (row.CurrentStageInstanceId is > 0)
        {
            var pending = await db.QueryFirstOrDefaultAsync<HiringCaseAdvanceState>(@"SELECT Id,Status FROM recruitment_hiring_case_advance_requests
WHERE HiringCaseId=@CaseId AND PositionStageInstanceId=@StageInstanceId AND Status='Pending Approval' ORDER BY Id DESC LIMIT 1",
                new { CaseId = id, StageInstanceId = row.CurrentStageInstanceId });
            if (pending is not null) MarkAdvance(row, pending.Status, pending.Id, "Stage movement is pending in global My Tasks.");
        }
        return row;
    }

    public async Task<IReadOnlyList<RecruitmentPipelineTransition>> GetAvailableHiringCaseTransitionsAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var rows = (await db.QueryAsync<RecruitmentPipelineTransition>(@"SELECT transitionRow.*,fromStage.StageCode FromStageCode,toStage.StageCode ToStageCode
FROM recruitment_position_pipeline_instances hiringCase
JOIN recruitment_position_stage_instances currentStage ON currentStage.Id=hiringCase.CurrentStageInstanceId AND currentStage.Status IN ('Active','Paused')
JOIN recruitment_pipeline_transitions transitionRow ON transitionRow.PipelineVersionId=hiringCase.PipelineVersionId AND transitionRow.FromStageId=currentStage.PipelineStageId AND transitionRow.IsActive=TRUE
JOIN recruitment_pipeline_stages fromStage ON fromStage.Id=transitionRow.FromStageId AND fromStage.CardScope='Position'
JOIN recruitment_pipeline_stages toStage ON toStage.Id=transitionRow.ToStageId AND toStage.IsActive=TRUE
WHERE hiringCase.Id=@Id AND (@ClientId IS NULL OR hiringCase.ClientId=@ClientId)
ORDER BY transitionRow.DisplayOrder,transitionRow.Id", new { Id = id, user.ClientId })).ToList();
        foreach (var row in rows.Where(row => IsDivisionRejectionOutcome(row.OutcomeCode)))
            row.ActionLabel = "Rejected by Division";
        var previous = await db.QueryFirstOrDefaultAsync<PreviousStageSource>(@"SELECT
hiringCase.PipelineVersionId,currentStage.PipelineStageId FromStageId,currentDefinition.StageCode FromStageCode,
previousStage.Id ToStageId,previousStage.StageCode ToStageCode,previousStage.StageName
FROM recruitment_position_pipeline_instances hiringCase
JOIN recruitment_position_stage_instances currentStage ON currentStage.Id=hiringCase.CurrentStageInstanceId AND currentStage.Status IN ('Active','Paused')
JOIN recruitment_pipeline_stages currentDefinition ON currentDefinition.Id=currentStage.PipelineStageId
JOIN recruitment_pipeline_stages previousStage ON previousStage.PipelineVersionId=hiringCase.PipelineVersionId
 AND previousStage.CardScope='Position' AND previousStage.IsActive=TRUE AND previousStage.IsTerminal=FALSE
 AND previousStage.DisplayOrder<currentDefinition.DisplayOrder
WHERE hiringCase.Id=@Id AND hiringCase.Status='Active' AND (@ClientId IS NULL OR hiringCase.ClientId=@ClientId)
ORDER BY previousStage.DisplayOrder DESC,previousStage.Id DESC LIMIT 1", new { Id = id, user.ClientId });
        if (previous is not null)
            rows.Add(new RecruitmentPipelineTransition
            {
                Id = -previous.ToStageId,
                PipelineVersionId = previous.PipelineVersionId,
                FromStageId = previous.FromStageId,
                ToStageId = previous.ToStageId,
                FromStageCode = previous.FromStageCode,
                ToStageCode = previous.ToStageCode,
                OutcomeCode = BackwardOutcome(previous.ToStageId),
                ActionLabel = $"Move back to {previous.StageName}",
                RequiresReason = true,
                IsActive = true,
                DisplayOrder = rows.Count + 100
            });
        return rows;
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
        var outcome = string.IsNullOrWhiteSpace(request.OutcomeCode) ? "ADVANCE" : request.OutcomeCode.Trim().ToUpperInvariant();
        var reason = (request.Reason ?? "").Trim();
        if (TryParseBackwardOutcome(outcome, out var backwardStageId))
        {
            if (reason.Length < 3) return (null, "A clear reason is required when moving to a previous stage.");
            var backwardTarget = await db.QueryFirstOrDefaultAsync<TransitionTargetSource>(@"SELECT
stage.Id PipelineStageId,stage.StageName,stage.StageType,stage.CardScope,stage.IsTerminal,
stage.SlaDurationMinutes,stage.TargetOffsetMinutes,hiringCase.SlaAnchorAtUtc,version.SlaMode
FROM recruitment_position_pipeline_instances hiringCase
JOIN recruitment_pipeline_versions version ON version.Id=hiringCase.PipelineVersionId
JOIN recruitment_pipeline_stages stage ON stage.PipelineVersionId=hiringCase.PipelineVersionId
 AND stage.CardScope='Position' AND stage.IsActive=TRUE AND stage.IsTerminal=FALSE
WHERE hiringCase.Id=@CaseId AND stage.DisplayOrder<@DisplayOrder
ORDER BY stage.DisplayOrder DESC,stage.Id DESC LIMIT 1", new { CaseId = current.HiringCaseId, current.DisplayOrder }, transaction);
            if (backwardTarget is null || backwardTarget.PipelineStageId != backwardStageId)
                return (null, "The selected previous stage is no longer available. Refresh and try again.");
            await ApplyHiringCaseAdvanceAsync(db, transaction, current, outcome, reason, user.Id, backwardTarget, true);
            await transaction.CommitAsync();
            return (await GetHiringCaseAsync(id, user), "");
        }
        if (!IsDivisionRejectionOutcome(outcome))
        {
            var validationError = await ValidateHiringCaseStageExitAsync(db, transaction, current);
            if (validationError.Length > 0) return (null, validationError);
        }
        var selectedTransition = await db.QueryFirstOrDefaultAsync<RecruitmentPipelineTransition>(@"SELECT * FROM recruitment_pipeline_transitions
WHERE PipelineVersionId=@PipelineVersionId AND FromStageId=@StageId AND OutcomeCode=@Outcome AND IsActive=TRUE
ORDER BY DisplayOrder,Id LIMIT 1", new { current.PipelineVersionId, StageId = current.PipelineStageId, Outcome = outcome }, transaction);
        var outgoingCount = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_pipeline_transitions WHERE PipelineVersionId=@PipelineVersionId AND FromStageId=@StageId AND IsActive=TRUE", new { current.PipelineVersionId, StageId = current.PipelineStageId }, transaction);
        if (outgoingCount > 0 && selectedTransition is null) return (null, "Select a configured action for the current stage.");
        if (selectedTransition?.RequiresReason == true && reason.Length < 3) return (null, "A clear reason is required for this stage action.");
        if (selectedTransition?.ApprovalWorkflowId is > 0)
        {
            current.RequiresApproval = true;
            current.ApprovalWorkflowId = selectedTransition.ApprovalWorkflowId;
        }
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

    public async Task<string> AdvanceHiringCaseForCandidateMilestoneAsync(long applicationId, string milestone, AuthUser user)
    {
        var normalizedMilestone = (milestone ?? "").Trim();
        if (normalizedMilestone is not ("ProfilesSelected" or "InterviewScheduled"))
            return await AdvanceDownstreamHiringCaseForApplicationAsync(applicationId, user);
        await using var db = Db();
        await db.OpenAsync();
        var application = await db.QueryFirstOrDefaultAsync<CandidateMilestoneSource>(@"SELECT applicationRow.PositionId,applicationRow.ClientId
FROM recruitment_candidate_applications applicationRow
WHERE applicationRow.Id=@ApplicationId AND applicationRow.ApplicationType='Application'", new { ApplicationId = applicationId });
        if (application is null || (user.ClientId is not null && user.ClientId != application.ClientId)) return "Application was not found.";

        // Vacancy milestones are cohort milestones, not per-candidate milestones. A single
        // shortlist or interview must never advance the cumulative position SLA while another
        // linked application is still waiting for a decision.
        var candidates = (await db.QueryAsync<CandidateMilestoneSource>(@"SELECT applicationRow.PositionId,applicationRow.ClientId,
COALESCE(candidateStage.StageName,applicationRow.CurrentStage,'') CandidateStageName,
COALESCE(candidateStage.StageType,'') CandidateStageType,
EXISTS(SELECT 1 FROM recruitment_interviews interviewRow
  WHERE interviewRow.ApplicationId=applicationRow.Id
    AND interviewRow.Status IN ('Scheduled','Rescheduled','Completed')) HasInterview
FROM recruitment_candidate_applications applicationRow
LEFT JOIN recruitment_application_pipeline_instances candidatePipeline ON candidatePipeline.ApplicationId=applicationRow.Id
LEFT JOIN recruitment_application_stage_instances candidateInstance ON candidateInstance.Id=candidatePipeline.CurrentStageInstanceId
LEFT JOIN recruitment_pipeline_stages candidateStage ON candidateStage.Id=candidateInstance.PipelineStageId
WHERE applicationRow.PositionId=@PositionId AND applicationRow.ApplicationType='Application'
ORDER BY applicationRow.Id", new { application.PositionId })).ToList();
        if (candidates.Count == 0) return "";

        var continuingCandidates = candidates.Where(candidate => !CandidateIsRejectedOrWithdrawn(candidate)).ToList();
        if (continuingCandidates.Count == 0)
            return await ReturnHiringCaseToProfileSharingAsync(db, application.PositionId, candidates.Count, user.Id);

        var profilesResolved = continuingCandidates.Count > 0
            && continuingCandidates.All(CandidateIsReadyForProfileSharing);
        var interviewsResolved = profilesResolved
            && continuingCandidates.All(candidate => candidate.HasInterview);

        // Evaluate both gates on every relevant candidate change. This lets the last rejection
        // release an already interview-ready cohort without requiring another artificial edit.
        var targets = new[] { "ProfilesSelected", "InterviewScheduled" };
        foreach (var target in targets)
        {
            if (target == "ProfilesSelected" && !profilesResolved) continue;
            if (target == "InterviewScheduled" && !interviewsResolved) continue;
            var transition = await db.QueryFirstOrDefaultAsync<HiringMilestoneTransition>(@"SELECT hiringCase.Id HiringCaseId,transitionRow.OutcomeCode
FROM recruitment_position_pipeline_instances hiringCase
JOIN recruitment_position_stage_instances currentInstance ON currentInstance.Id=hiringCase.CurrentStageInstanceId AND currentInstance.Status='Active'
JOIN recruitment_pipeline_transitions transitionRow ON transitionRow.PipelineVersionId=hiringCase.PipelineVersionId
 AND transitionRow.FromStageId=currentInstance.PipelineStageId AND transitionRow.IsActive=TRUE
JOIN recruitment_pipeline_stages targetStage ON targetStage.Id=transitionRow.ToStageId AND targetStage.CardScope='Position' AND targetStage.IsActive=TRUE
WHERE hiringCase.PositionId=@PositionId AND hiringCase.Status='Active'
 AND transitionRow.OutcomeCode NOT LIKE '%REJECT%'
 AND ((@Target='ProfilesSelected' AND LOWER(targetStage.StageName) LIKE '%sharing%profile%')
   OR (@Target='InterviewScheduled' AND (targetStage.StageType='Interview' OR LOWER(targetStage.StageName) LIKE '%interview%panel%')))
ORDER BY hiringCase.Id DESC,transitionRow.DisplayOrder,transitionRow.Id LIMIT 1", new { application.PositionId, Target = target });
            if (transition is null) continue;
            var (_, error) = await AdvanceHiringCaseAsync(transition.HiringCaseId, new MoveRecruitmentHiringCaseRequest
            {
                OutcomeCode = transition.OutcomeCode,
                Reason = target == "ProfilesSelected"
                    ? $"Automatically advanced after all {candidates.Count} linked candidates were resolved for profile sharing ({continuingCandidates.Count} continuing)."
                    : $"Automatically advanced after interviews were scheduled or completed for all {continuingCandidates.Count} continuing candidates."
            }, user);
            if (!string.IsNullOrWhiteSpace(error)) return error;
        }
        return await AdvanceDownstreamHiringCaseAsync(db, application.PositionId, user);
    }

    public async Task<string> AdvanceHiringCaseForDocumentMilestoneAsync(long hiringCaseId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var positionId = await db.ExecuteScalarAsync<long?>(@"SELECT PositionId FROM recruitment_position_pipeline_instances
WHERE Id=@Id AND (@ClientId IS NULL OR ClientId=@ClientId)", new { Id = hiringCaseId, user.ClientId });
        return positionId is > 0 ? await AdvanceDownstreamHiringCaseAsync(db, positionId.Value, user) : "";
    }

    public async Task<string> AdvanceHiringCaseAutomationAsync(long hiringCaseId, AuthUser user) =>
        await AdvanceHiringCaseForDocumentMilestoneAsync(hiringCaseId, user);

    public async Task<string> AdvanceHiringCaseForOfferMilestoneAsync(long offerId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var applicationId = await db.ExecuteScalarAsync<long?>(@"SELECT applicationRow.Id
FROM recruitment_offers offerRow
JOIN recruitment_candidate_applications applicationRow ON applicationRow.Id=offerRow.ApplicationId
WHERE offerRow.Id=@Id AND (@ClientId IS NULL OR applicationRow.ClientId=@ClientId)", new { Id = offerId, user.ClientId });
        return applicationId is > 0 ? await AdvanceDownstreamHiringCaseForApplicationAsync(applicationId.Value, user) : "";
    }

    private async Task<string> AdvanceDownstreamHiringCaseForApplicationAsync(long applicationId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var application = await db.QueryFirstOrDefaultAsync<CandidateMilestoneSource>(@"SELECT PositionId,ClientId
FROM recruitment_candidate_applications WHERE Id=@Id AND ApplicationType='Application'", new { Id = applicationId });
        if (application is null || (user.ClientId is not null && user.ClientId != application.ClientId)) return "";
        return await AdvanceDownstreamHiringCaseAsync(db, application.PositionId, user);
    }

    private async Task<string> AdvanceDownstreamHiringCaseAsync(MySqlConnection db, long positionId, AuthUser user)
    {
        for (var step = 0; step < 6; step++)
        {
            var current = await db.QueryFirstOrDefaultAsync<HiringAutomationStage>(@"SELECT
hiringCase.Id HiringCaseId,hiringCase.ClientId,hiringCase.PositionId,hiringCase.CurrentStageInstanceId,
stage.Id PipelineStageId,stage.StageCode,stage.StageName,stage.StageType,stage.IsTerminal,stage.RequiresApproval
FROM recruitment_position_pipeline_instances hiringCase
JOIN recruitment_position_stage_instances stageInstance ON stageInstance.Id=hiringCase.CurrentStageInstanceId AND stageInstance.Status='Active'
JOIN recruitment_pipeline_stages stage ON stage.Id=stageInstance.PipelineStageId
WHERE hiringCase.PositionId=@PositionId AND hiringCase.Status='Active'
ORDER BY hiringCase.Id DESC LIMIT 1", new { PositionId = positionId });
            if (current is null || current.IsTerminal) return "";

            var candidates = (await db.QueryAsync<CandidateMilestoneSource>(@"SELECT applicationRow.Id ApplicationId,
applicationRow.PositionId,applicationRow.ClientId,
COALESCE(candidateStage.StageName,applicationRow.CurrentStage,'') CandidateStageName,
COALESCE(candidateStage.StageType,'') CandidateStageType,
EXISTS(SELECT 1 FROM recruitment_interviews interviewRow WHERE interviewRow.ApplicationId=applicationRow.Id
 AND interviewRow.Status IN ('Scheduled','Rescheduled','Completed')) HasInterview,
EXISTS(SELECT 1 FROM recruitment_interviews interviewRow WHERE interviewRow.ApplicationId=applicationRow.Id
 AND interviewRow.Status='Completed' AND interviewRow.Result IN ('Selected','Rejected')) HasCompletedInterviewDecision,
COALESCE((SELECT offerRow.Status FROM recruitment_offers offerRow WHERE offerRow.ApplicationId=applicationRow.Id
 ORDER BY offerRow.UpdatedAt DESC,offerRow.Id DESC LIMIT 1),'') LatestOfferStatus,
(applicationRow.JoinedEmployeeId IS NOT NULL OR applicationRow.CurrentStage='Joined') IsJoined
FROM recruitment_candidate_applications applicationRow
LEFT JOIN recruitment_application_pipeline_instances candidatePipeline ON candidatePipeline.ApplicationId=applicationRow.Id
LEFT JOIN recruitment_application_stage_instances candidateInstance ON candidateInstance.Id=candidatePipeline.CurrentStageInstanceId
LEFT JOIN recruitment_pipeline_stages candidateStage ON candidateStage.Id=candidateInstance.PipelineStageId
WHERE applicationRow.PositionId=@PositionId AND applicationRow.ApplicationType='Application'", new { PositionId = positionId })).ToList();
            if (candidates.Count == 0) return "";
            var continuing = candidates.Where(candidate => !CandidateIsRejectedOrWithdrawn(candidate)).ToList();
            if (continuing.Count == 0) return await ReturnHiringCaseToProfileSharingAsync(db, positionId, candidates.Count, user.Id);

            var key = $"{current.StageCode} {current.StageName} {current.StageType}".ToUpperInvariant();
            bool ready;
            string reason;
            if (key.Contains("INTERVIEW") && !key.Contains("SHARING"))
            {
                ready = continuing.All(row => row.HasCompletedInterviewDecision);
                reason = $"Automatically advanced after final interview decisions were recorded for all {continuing.Count} continuing candidates.";
            }
            else if (key.Contains("SIGNING") && key.Contains("MOM"))
            {
                var required = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_stage_process_document_requirements
WHERE PipelineStageId=@StageId AND IsRequired=TRUE AND DocumentType IN ('MOM','SIGNED_MOM')", new { StageId = current.PipelineStageId });
                var complete = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(DISTINCT document.DocumentType)
FROM recruitment_process_documents document
WHERE document.HiringCaseId=@CaseId AND document.PipelineStageId=@StageId AND document.Status='Signed'
 AND document.DocumentType IN ('MOM','SIGNED_MOM')", new { CaseId = current.HiringCaseId, StageId = current.PipelineStageId });
                ready = required > 0 && complete >= required;
                reason = "Automatically advanced after every required committee MoM was finalized as signed.";
            }
            else if (key.Contains("NEGOTIATION") || key.Contains("MOM_TO_HR"))
            {
                ready = continuing.All(row => row.LatestOfferStatus is "Pending Approval" or "Approved" or "Pending Candidate" or "Released" or "Accepted");
                reason = $"Automatically submitted negotiation outcomes for all {continuing.Count} continuing candidates to HR approval.";
            }
            else if (key.Contains("APPROVAL") && key.Contains("HR"))
            {
                ready = continuing.All(row => row.LatestOfferStatus is "Approved" or "Pending Candidate" or "Released" or "Accepted");
                reason = "Automatically requested or applied HR Division approval after all continuing candidate negotiations were submitted.";
            }
            else if (key.Contains("OFFER") && (key.Contains("ISSU") || current.StageType.Equals("Offer", StringComparison.OrdinalIgnoreCase)))
            {
                ready = continuing.All(row => row.LatestOfferStatus is "Pending Candidate" or "Released" or "Accepted");
                reason = $"Automatically completed offer issuance after all {continuing.Count} continuing candidates received their offers.";
            }
            else return "";
            if (!ready) return "";

            var transition = await db.QueryFirstOrDefaultAsync<HiringMilestoneTransition>(@"SELECT
@CaseId HiringCaseId,transitionRow.OutcomeCode
FROM recruitment_pipeline_transitions transitionRow
JOIN recruitment_pipeline_stages targetStage ON targetStage.Id=transitionRow.ToStageId
WHERE transitionRow.PipelineVersionId=(SELECT PipelineVersionId FROM recruitment_position_pipeline_instances WHERE Id=@CaseId)
 AND transitionRow.FromStageId=@StageId AND transitionRow.IsActive=TRUE
 AND transitionRow.OutcomeCode NOT LIKE '%REJECT%' AND targetStage.CardScope='Position' AND targetStage.IsActive=TRUE
ORDER BY transitionRow.DisplayOrder,transitionRow.Id LIMIT 1",
                new { CaseId = current.HiringCaseId, StageId = current.PipelineStageId });
            if (transition is null) return "";
            var (moved, error) = await AdvanceHiringCaseAsync(current.HiringCaseId, new MoveRecruitmentHiringCaseRequest
            {
                OutcomeCode = transition.OutcomeCode,
                Reason = reason
            }, user);
            if (!string.IsNullOrWhiteSpace(error)) return error;
            if (moved?.AdvanceStatus == "Pending Approval") return "";
        }
        return "";
    }

    private static async Task<string> ReturnHiringCaseToProfileSharingAsync(MySqlConnection db, long positionId, int candidateCount, int actorUserId)
    {
        await using var transaction = await db.BeginTransactionAsync();
        var hiringCase = await db.QueryFirstOrDefaultAsync<ProfileReworkCaseSource>(@"SELECT
hiringCase.Id HiringCaseId,hiringCase.PipelineVersionId,hiringCase.SlaAnchorAtUtc,
hiringCase.CurrentStageInstanceId,currentStage.PipelineStageId CurrentPipelineStageId,
currentStageRow.StageName CurrentStageName,version.SlaMode
FROM recruitment_position_pipeline_instances hiringCase
JOIN recruitment_position_stage_instances currentStage ON currentStage.Id=hiringCase.CurrentStageInstanceId AND currentStage.Status='Active'
JOIN recruitment_pipeline_stages currentStageRow ON currentStageRow.Id=currentStage.PipelineStageId
JOIN recruitment_pipeline_versions version ON version.Id=hiringCase.PipelineVersionId
WHERE hiringCase.PositionId=@PositionId AND hiringCase.Status='Active'
ORDER BY hiringCase.Id DESC LIMIT 1 FOR UPDATE", new { PositionId = positionId }, transaction);
        if (hiringCase is null)
        {
            await transaction.RollbackAsync();
            return "";
        }

        var target = await db.QueryFirstOrDefaultAsync<StageDefinition>(@"SELECT Id,StageCode,StageName,CardScope,DisplayOrder,
SlaDurationMinutes,TargetOffsetMinutes,IsInitial,IsTerminal
FROM recruitment_pipeline_stages
WHERE PipelineVersionId=@PipelineVersionId AND CardScope='Position' AND IsActive=TRUE
  AND (UPPER(StageCode) IN ('SHARING_PROFILES','PROFILE_SHARING')
    OR (LOWER(StageName) LIKE '%sharing%' AND LOWER(StageName) LIKE '%profile%'))
ORDER BY CASE WHEN UPPER(StageCode)='SHARING_PROFILES' THEN 0 ELSE 1 END,DisplayOrder,Id LIMIT 1",
            new { hiringCase.PipelineVersionId }, transaction);
        if (target is null)
        {
            await transaction.RollbackAsync();
            return "";
        }

        var openPauses = (await db.QueryAsync<PauseSource>(@"SELECT Id,PausedAtUtc
FROM recruitment_position_stage_pause_periods
WHERE PositionStageInstanceId=@StageInstanceId AND ResumedAtUtc IS NULL FOR UPDATE",
            new { StageInstanceId = hiringCase.CurrentStageInstanceId }, transaction)).ToList();
        var pausedSeconds = openPauses.Sum(pause => Math.Max(0, (long)(DateTime.UtcNow - pause.PausedAtUtc).TotalSeconds));
        foreach (var pause in openPauses)
            await db.ExecuteAsync(@"UPDATE recruitment_position_stage_pause_periods
SET ResumedByUserId=@UserId,ResumedAtUtc=UTC_TIMESTAMP(6),DurationSeconds=@Seconds WHERE Id=@Id",
                new { pause.Id, UserId = actorUserId, Seconds = Math.Max(0, (long)(DateTime.UtcNow - pause.PausedAtUtc).TotalSeconds) }, transaction);
        if (pausedSeconds > 0)
            await db.ExecuteAsync(@"UPDATE recruitment_position_stage_instances
SET PausedDurationSeconds=PausedDurationSeconds+@Seconds WHERE Id=@Id",
                new { Id = hiringCase.CurrentStageInstanceId, Seconds = pausedSeconds }, transaction);

        long targetInstanceId;
        if (hiringCase.CurrentPipelineStageId == target.Id)
        {
            targetInstanceId = hiringCase.CurrentStageInstanceId;
        }
        else
        {
            await db.ExecuteAsync(@"UPDATE recruitment_position_stage_instances
SET Status='Completed',OutcomeCode='ALL_PROFILES_REJECTED',CompletedAtUtc=UTC_TIMESTAMP(6)
WHERE Id=@Id", new { Id = hiringCase.CurrentStageInstanceId }, transaction);
            await db.ExecuteAsync(@"UPDATE recruitment_hiring_case_advance_requests
SET Status='Superseded',DecidedAtUtc=UTC_TIMESTAMP(6)
WHERE HiringCaseId=@CaseId AND PositionStageInstanceId=@StageInstanceId AND Status='Pending Approval'",
                new { CaseId = hiringCase.HiringCaseId, StageInstanceId = hiringCase.CurrentStageInstanceId }, transaction);

            targetInstanceId = await db.ExecuteScalarAsync<long?>(@"SELECT Id FROM recruitment_position_stage_instances
WHERE PositionPipelineInstanceId=@CaseId AND PipelineStageId=@StageId AND Status='Pending'
ORDER BY Id DESC LIMIT 1 FOR UPDATE", new { CaseId = hiringCase.HiringCaseId, StageId = target.Id }, transaction) ?? 0;
            if (targetInstanceId <= 0)
            {
                DateTime? dueAt = hiringCase.SlaMode.Equals("CumulativeFromAnchor", StringComparison.OrdinalIgnoreCase)
                    && target.TargetOffsetMinutes.HasValue
                    ? hiringCase.SlaAnchorAtUtc.AddMinutes(target.TargetOffsetMinutes.Value)
                    : target.SlaDurationMinutes > 0 ? DateTime.UtcNow.AddMinutes(target.SlaDurationMinutes) : null;
                targetInstanceId = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_position_stage_instances
(PositionPipelineInstanceId,PipelineStageId,Status,EnteredAtUtc,DueAtUtc)
VALUES (@CaseId,@StageId,'Active',UTC_TIMESTAMP(6),@DueAtUtc);SELECT LAST_INSERT_ID();",
                    new { CaseId = hiringCase.HiringCaseId, StageId = target.Id, DueAtUtc = dueAt }, transaction);
            }
            else
            {
                await db.ExecuteAsync(@"UPDATE recruitment_position_stage_instances
SET Status='Active',OutcomeCode='',EnteredAtUtc=UTC_TIMESTAMP(6),CompletedAtUtc=NULL
WHERE Id=@Id", new { Id = targetInstanceId }, transaction);
            }
            await db.ExecuteAsync(@"UPDATE recruitment_position_pipeline_instances
SET Status='Active',CurrentStageInstanceId=@StageInstanceId,CompletedAtUtc=NULL
WHERE Id=@CaseId", new { CaseId = hiringCase.HiringCaseId, StageInstanceId = targetInstanceId }, transaction);
        }

        var details = hiringCase.CurrentPipelineStageId == target.Id
            ? $"All {candidateCount} linked candidates were rejected or withdrawn. Profile sourcing continues in {target.StageName}; the original cumulative SLA anchor and due date remain unchanged."
            : $"All {candidateCount} linked candidates were rejected or withdrawn. Returned from {hiringCase.CurrentStageName} to {target.StageName}; the original cumulative SLA anchor and due date remain unchanged.";
        await db.ExecuteAsync(@"INSERT INTO recruitment_position_stage_events
(PositionPipelineInstanceId,PositionStageInstanceId,EventType,EventTitle,EventDetails,ActorUserId)
VALUES (@CaseId,@StageInstanceId,'ProfilesReworkStarted','Profiles returned for sourcing',@Details,@UserId)",
            new { CaseId = hiringCase.HiringCaseId, StageInstanceId = targetInstanceId, Details = details, UserId = actorUserId }, transaction);
        await transaction.CommitAsync();
        return "";
    }

    private static bool CandidateIsRejectedOrWithdrawn(CandidateMilestoneSource application)
    {
        if (application.CandidateStageType.Equals("Rejected", StringComparison.OrdinalIgnoreCase)
            || application.CandidateStageType.Equals("Withdrawn", StringComparison.OrdinalIgnoreCase)) return true;
        var stage = application.CandidateStageName.ToLowerInvariant();
        return stage.Contains("reject") || stage.Contains("withdraw");
    }

    private static bool CandidateIsReadyForProfileSharing(CandidateMilestoneSource application)
    {
        if (CandidateIsRejectedOrWithdrawn(application)) return false;
        if (new[] { "Interview", "HR", "Offer", "Documents", "PreOnboarding", "Joining", "Completed" }
            .Contains(application.CandidateStageType, StringComparer.OrdinalIgnoreCase)) return true;
        var stage = application.CandidateStageName.ToLowerInvariant();
        return stage.Contains("profile") || stage.Contains("shortlist") || stage.Contains("stakeholder");
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
        if (!IsDivisionRejectionOutcome(request.OutcomeCode))
        {
            var validationError = await ValidateHiringCaseStageExitAsync(db, transaction, current);
            if (validationError.Length > 0) return (null, validationError);
        }
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
    AND (requirement.RequiresSignature=FALSE OR (document.Status='Signed' AND (
      EXISTS (SELECT 1 FROM entity_attachments attachment WHERE attachment.entity_type='RECRUITMENT_PROCESS_DOCUMENT'
        AND attachment.entity_id=document.Id AND attachment.is_current=TRUE AND attachment.is_deleted=FALSE)
      OR (SELECT COUNT(*) FROM recruitment_process_document_signatures signatureRow WHERE signatureRow.ProcessDocumentId=document.Id)
        >= GREATEST(1,COALESCE(NULLIF((SELECT COUNT(*) FROM recruitment_stage_default_panel_members panel
          WHERE panel.PipelineStageId=document.PipelineStageId AND panel.IsRequired=TRUE),0),1))))) )
ORDER BY requirement.DisplayOrder,requirement.Id", new { StageId = current.PipelineStageId, CaseId = current.HiringCaseId }, transaction)).ToArray();
        return missingDocuments.Length > 0 ? $"Complete required process documents before moving this stage: {string.Join(", ", missingDocuments)}." : "";
    }

    private static async Task ApplyHiringCaseAdvanceAsync(MySqlConnection db, MySqlTransaction transaction, CurrentStageSource current, string outcome, string reason, int actorUserId, TransitionTargetSource? explicitTarget = null, bool movingBackward = false)
    {
        var rejectedByDivision = IsDivisionRejectionOutcome(outcome);
        await db.ExecuteAsync(@"UPDATE recruitment_position_stage_instances SET Status='Completed',OutcomeCode=@Outcome,
CompletedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id", new { Id = current.CurrentStageInstanceId, Outcome = outcome }, transaction);
        var configured = explicitTarget ?? await db.QueryFirstOrDefaultAsync<TransitionTargetSource>(@"SELECT transitionRow.Id TransitionId,toStage.Id PipelineStageId,
toStage.StageName,toStage.StageType,toStage.CardScope,toStage.IsTerminal,toStage.SlaDurationMinutes,toStage.TargetOffsetMinutes,
hiringCase.SlaAnchorAtUtc,version.SlaMode
FROM recruitment_pipeline_transitions transitionRow
JOIN recruitment_pipeline_stages toStage ON toStage.Id=transitionRow.ToStageId AND toStage.IsActive=TRUE
JOIN recruitment_position_pipeline_instances hiringCase ON hiringCase.Id=@CaseId
JOIN recruitment_pipeline_versions version ON version.Id=hiringCase.PipelineVersionId
WHERE transitionRow.PipelineVersionId=@PipelineVersionId AND transitionRow.FromStageId=@FromStageId
  AND transitionRow.OutcomeCode=@Outcome AND transitionRow.IsActive=TRUE
ORDER BY transitionRow.DisplayOrder,transitionRow.Id LIMIT 1", new { CaseId = current.HiringCaseId, current.PipelineVersionId, FromStageId = current.PipelineStageId, Outcome = outcome }, transaction);
        NextStageSource? next = null;
        var candidateHandoff = configured?.CardScope.Equals("Application", StringComparison.OrdinalIgnoreCase) == true;
        if (configured is null)
        {
            next = await db.QueryFirstOrDefaultAsync<NextStageSource>(@"SELECT instance.Id StageInstanceId,stage.Id PipelineStageId,stage.StageName,stage.IsTerminal
FROM recruitment_position_stage_instances instance
JOIN recruitment_pipeline_stages stage ON stage.Id=instance.PipelineStageId
WHERE instance.PositionPipelineInstanceId=@CaseId AND instance.Status='Pending' AND stage.DisplayOrder>@DisplayOrder
ORDER BY stage.DisplayOrder,stage.Id LIMIT 1", new { CaseId = current.HiringCaseId, current.DisplayOrder }, transaction);
            var hasApplicationFlow = await db.ExecuteScalarAsync<bool>(@"SELECT COUNT(*)>0 FROM recruitment_pipeline_stages
WHERE PipelineVersionId=@PipelineVersionId AND CardScope='Application' AND IsActive=TRUE", new { current.PipelineVersionId }, transaction);
            candidateHandoff = (next is null || current.IsTerminal) && hasApplicationFlow;
        }
        else if (!candidateHandoff)
        {
            var stageInstanceId = await ActivateOrCreatePositionStageAsync(db, transaction, current.HiringCaseId, configured);
            next = new NextStageSource { StageInstanceId = stageInstanceId, PipelineStageId = configured.PipelineStageId, StageName = configured.StageName, IsTerminal = configured.IsTerminal, StageType = configured.StageType };
        }

        if (candidateHandoff)
            await db.ExecuteAsync(@"UPDATE recruitment_position_pipeline_instances
SET Status='Candidate Flow',CurrentStageInstanceId=@StageInstanceId,CompletedAtUtc=NULL WHERE Id=@Id",
                new { Id = current.HiringCaseId, StageInstanceId = current.CurrentStageInstanceId }, transaction);
        else if (next?.IsTerminal == true)
        {
            var terminalStatus = next.StageType.Equals("Rejected", StringComparison.OrdinalIgnoreCase) ? "Rejected"
                : next.StageType.Equals("Withdrawn", StringComparison.OrdinalIgnoreCase) ? "Withdrawn" : "Completed";
            await db.ExecuteAsync(@"UPDATE recruitment_position_stage_instances SET Status='Completed',EnteredAtUtc=COALESCE(EnteredAtUtc,UTC_TIMESTAMP(6)),CompletedAtUtc=UTC_TIMESTAMP(6),OutcomeCode=@Outcome WHERE Id=@Id;
UPDATE recruitment_position_pipeline_instances SET Status=@Status,CurrentStageInstanceId=@Id,CompletedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@CaseId", new { Id = next.StageInstanceId, CaseId = current.HiringCaseId, Status = terminalStatus, Outcome = rejectedByDivision ? "" : outcome }, transaction);
        }
        else if (next is null || current.IsTerminal)
            await db.ExecuteAsync("UPDATE recruitment_position_pipeline_instances SET Status='Completed',CurrentStageInstanceId=@StageInstanceId,CompletedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id", new { Id = current.HiringCaseId, StageInstanceId = current.CurrentStageInstanceId }, transaction);
        else
            await db.ExecuteAsync(@"UPDATE recruitment_position_stage_instances
SET Status='Active',OutcomeCode='',EnteredAtUtc=COALESCE(EnteredAtUtc,UTC_TIMESTAMP(6)),CompletedAtUtc=NULL
WHERE Id=@Id;
UPDATE recruitment_position_pipeline_instances
SET Status='Active',CurrentStageInstanceId=@Id,CompletedAtUtc=NULL WHERE Id=@CaseId",
                new { Id = next.StageInstanceId, CaseId = current.HiringCaseId }, transaction);
        await db.ExecuteAsync(@"INSERT INTO recruitment_position_stage_events
(PositionPipelineInstanceId,PositionStageInstanceId,EventType,EventTitle,EventDetails,ActorUserId)
VALUES (@CaseId,@StageId,@EventType,@Title,@Details,@UserId)", new
        {
            CaseId = current.HiringCaseId,
            StageId = current.CurrentStageInstanceId,
            EventType = rejectedByDivision ? "RejectedByDivision" : movingBackward ? "StageMovedBack" : "StageMoved",
            Title = rejectedByDivision ? "Rejected by Division" : candidateHandoff ? "Candidate flow ready" : next is null ? "Hiring case completed" : $"Moved to {next.StageName}",
            Details = reason,
            UserId = actorUserId
        }, transaction);
    }

    private static async Task<long> ActivateOrCreatePositionStageAsync(MySqlConnection db, MySqlTransaction transaction, long hiringCaseId, TransitionTargetSource target)
    {
        var stageInstanceId = await db.ExecuteScalarAsync<long?>(@"SELECT Id FROM recruitment_position_stage_instances
WHERE PositionPipelineInstanceId=@CaseId AND PipelineStageId=@StageId AND Status='Pending'
ORDER BY Id DESC LIMIT 1 FOR UPDATE", new { CaseId = hiringCaseId, StageId = target.PipelineStageId }, transaction) ?? 0;
        if (stageInstanceId > 0)
        {
            await db.ExecuteAsync(@"UPDATE recruitment_position_stage_instances
SET Status='Active',OutcomeCode='',EnteredAtUtc=COALESCE(EnteredAtUtc,UTC_TIMESTAMP(6)),CompletedAtUtc=NULL
WHERE Id=@Id", new { Id = stageInstanceId }, transaction);
            return stageInstanceId;
        }

        DateTime? dueAt = target.SlaMode.Equals("CumulativeFromAnchor", StringComparison.OrdinalIgnoreCase)
            && target.TargetOffsetMinutes.HasValue
            ? target.SlaAnchorAtUtc.AddMinutes(target.TargetOffsetMinutes.Value)
            : target.SlaDurationMinutes > 0 ? DateTime.UtcNow.AddMinutes(target.SlaDurationMinutes) : null;
        return await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_position_stage_instances
(PositionPipelineInstanceId,PipelineStageId,Status,EnteredAtUtc,DueAtUtc)
VALUES (@CaseId,@StageId,'Active',UTC_TIMESTAMP(6),@DueAtUtc);SELECT LAST_INSERT_ID();",
            new { CaseId = hiringCaseId, StageId = target.PipelineStageId, DueAtUtc = dueAt }, transaction);
    }

    private static bool IsDivisionRejectionOutcome(string? outcome) =>
        (outcome ?? "").Trim().ToUpperInvariant() is "REJECT" or "REJECT_BY_DIVISION" or "REJECTED_BY_DIVISION";

    private static string BackwardOutcome(long stageId) => $"MOVE_BACK_TO_{stageId}";

    private static bool TryParseBackwardOutcome(string? outcome, out long stageId) =>
        long.TryParse((outcome ?? "").Trim().ToUpperInvariant().Replace("MOVE_BACK_TO_", "", StringComparison.Ordinal), out stageId)
        && (outcome ?? "").Trim().StartsWith("MOVE_BACK_TO_", StringComparison.OrdinalIgnoreCase);

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

    public async Task<IReadOnlyList<RecruitmentProcessDocumentSignature>> ListProcessDocumentSignaturesAsync(long documentId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var allowed = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_process_documents
WHERE Id=@Id AND (@ClientId IS NULL OR ClientId=@ClientId)", new { Id = documentId, user.ClientId });
        if (allowed == 0) return [];
        return (await db.QueryAsync<RecruitmentProcessDocumentSignature>(@"SELECT *
FROM recruitment_process_document_signatures WHERE ProcessDocumentId=@Id ORDER BY SignedAtUtc,Id", new { Id = documentId })).ToList();
    }

    public async Task<(RecruitmentProcessDocumentSignature? Row, string Error)> SaveProcessDocumentSignatureAsync(
        long documentId,
        SaveRecruitmentProcessDocumentSignature request,
        AuthUser user)
    {
        if (user.Id <= 0) return (null, "A signed-in user is required to sign this document.");
        var method = (request.SignatureMethod ?? "").Trim();
        method = method.Equals("typed", StringComparison.OrdinalIgnoreCase) ? "Typed"
            : method.Equals("drawn", StringComparison.OrdinalIgnoreCase) ? "Drawn"
            : method.Equals("image", StringComparison.OrdinalIgnoreCase) ? "Image" : "";
        if (method.Length == 0) return (null, "Choose Typed, Drawn or Image signature.");
        var signerName = Regex.Replace((request.SignerName ?? "").Trim(), @"\s+", " ");
        if (signerName.Length is < 2 or > 190) return (null, "Enter the signer's full name.");
        var signatureValue = (request.SignatureDataUrl ?? "").Trim();
        if (method == "Typed") signatureValue = signerName;
        else
        {
            var match = Regex.Match(signatureValue, @"^data:image/(?<type>png|jpeg);base64,(?<data>[A-Za-z0-9+/=]+)$", RegexOptions.IgnoreCase);
            if (!match.Success) return (null, "Use a PNG/JPG image or draw the signature in the provided pad.");
            try
            {
                if (Convert.FromBase64String(match.Groups["data"].Value).Length > 750 * 1024)
                    return (null, "Signature image must be 750 KB or smaller.");
            }
            catch (FormatException)
            {
                return (null, "The signature image is invalid.");
            }
        }

        await using var db = Db();
        await db.OpenAsync();
        var document = await db.QueryFirstOrDefaultAsync<RecruitmentProcessDocument>(@"SELECT * FROM recruitment_process_documents
WHERE Id=@Id AND (@ClientId IS NULL OR ClientId=@ClientId)", new { Id = documentId, user.ClientId });
        if (document is null) return (null, "Recruitment process document was not found.");
        if (document.Status.Equals("Signed", StringComparison.OrdinalIgnoreCase)) return (null, "This document is already final and signed.");
        var requiresSignature = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM recruitment_stage_process_document_requirements
WHERE PipelineStageId=@StageId AND DocumentType=@DocumentType AND RequiresSignature=TRUE",
            new { StageId = document.PipelineStageId, document.DocumentType });
        if (requiresSignature == 0) return (null, "This pipeline document does not require signatures.");
        var access = await db.QueryFirstAsync<PanelSignatureAccess>(@"SELECT
(SELECT COUNT(DISTINCT panel.PanelUserId) FROM recruitment_position_pipeline_instances hiringCase
 JOIN recruitment_candidate_applications applicationRow ON applicationRow.PositionId=hiringCase.PositionId AND applicationRow.ApplicationType='Application'
 JOIN recruitment_interviews interviewRow ON interviewRow.ApplicationId=applicationRow.Id
 JOIN recruitment_interview_panel_members panel ON panel.InterviewId=interviewRow.Id
 WHERE hiringCase.Id=@HiringCaseId) ActualMemberCount,
(SELECT COUNT(*) FROM recruitment_position_pipeline_instances hiringCase
 JOIN recruitment_candidate_applications applicationRow ON applicationRow.PositionId=hiringCase.PositionId AND applicationRow.ApplicationType='Application'
 JOIN recruitment_interviews interviewRow ON interviewRow.ApplicationId=applicationRow.Id
 JOIN recruitment_interview_panel_members panel ON panel.InterviewId=interviewRow.Id
 WHERE hiringCase.Id=@HiringCaseId AND panel.PanelUserId=@UserId) IsActualMember,
(SELECT COUNT(*) FROM recruitment_stage_default_panel_members panel
 WHERE panel.PipelineStageId=@StageId AND panel.IsRequired=TRUE) DefaultMemberCount,
(SELECT COUNT(*) FROM recruitment_stage_default_panel_members panel
 WHERE panel.PipelineStageId=@StageId AND panel.IsRequired=TRUE AND panel.PanelUserId=@UserId) IsDefaultMember",
            new { document.HiringCaseId, StageId = document.PipelineStageId, UserId = user.Id });
        if ((access.ActualMemberCount > 0 && access.IsActualMember == 0)
            || (access.ActualMemberCount == 0 && access.DefaultMemberCount > 0 && access.IsDefaultMember == 0))
            return (null, "Only a configured committee member can sign this MoM.");
        var signerRole = await db.ExecuteScalarAsync<string?>(@"SELECT roleName FROM (
SELECT panel.PanelRole roleName,1 sortOrder FROM recruitment_position_pipeline_instances hiringCase
JOIN recruitment_candidate_applications applicationRow ON applicationRow.PositionId=hiringCase.PositionId AND applicationRow.ApplicationType='Application'
JOIN recruitment_interviews interviewRow ON interviewRow.ApplicationId=applicationRow.Id
JOIN recruitment_interview_panel_members panel ON panel.InterviewId=interviewRow.Id
WHERE hiringCase.Id=@HiringCaseId AND panel.PanelUserId=@UserId
UNION ALL
SELECT panel.PanelRole,2 FROM recruitment_stage_default_panel_members panel
WHERE panel.PipelineStageId=@StageId AND panel.PanelUserId=@UserId
) roles ORDER BY sortOrder LIMIT 1", new { document.HiringCaseId, StageId = document.PipelineStageId, UserId = user.Id }) ?? "Authorised signatory";
        await db.ExecuteAsync(@"INSERT INTO recruitment_process_document_signatures
(ProcessDocumentId,ClientId,SignerUserId,SignerName,SignerRole,SignatureMethod,SignatureDataUrl,SignedAtUtc)
VALUES (@DocumentId,@ClientId,@UserId,@SignerName,@SignerRole,@Method,@SignatureValue,UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE SignerName=VALUES(SignerName),SignerRole=VALUES(SignerRole),SignatureMethod=VALUES(SignatureMethod),
SignatureDataUrl=VALUES(SignatureDataUrl),SignedAtUtc=VALUES(SignedAtUtc)", new
        {
            DocumentId = documentId, document.ClientId, UserId = user.Id, SignerName = signerName, SignerRole = signerRole,
            Method = method, SignatureValue = signatureValue
        });
        if (document.HiringCaseId is > 0)
            await db.ExecuteAsync(@"INSERT INTO recruitment_position_stage_events
(PositionPipelineInstanceId,PositionStageInstanceId,EventType,EventTitle,EventDetails,ActorUserId)
VALUES (@CaseId,(SELECT CurrentStageInstanceId FROM recruitment_position_pipeline_instances WHERE Id=@CaseId),
'DocumentSigned','MoM signature captured',@Details,@UserId)", new
            {
                CaseId = document.HiringCaseId.Value,
                Details = $"{signerName} signed {document.DocumentType} using {method} signature.",
                UserId = user.Id
            });
        return (await db.QueryFirstOrDefaultAsync<RecruitmentProcessDocumentSignature>(@"SELECT *
FROM recruitment_process_document_signatures WHERE ProcessDocumentId=@DocumentId AND SignerUserId=@UserId",
            new { DocumentId = documentId, UserId = user.Id }), "");
    }

    private static async Task<SignatureGate> SignatureGateAsync(MySqlConnection db, long documentId)
    {
        var gate = await db.QueryFirstOrDefaultAsync<SignatureGate>(@"SELECT
(SELECT COUNT(*) FROM recruitment_process_document_signatures signatureRow WHERE signatureRow.ProcessDocumentId=documentRow.Id) Captured,
GREATEST(1,COALESCE(
 NULLIF((SELECT COUNT(DISTINCT panel.PanelUserId) FROM recruitment_position_pipeline_instances hiringCase
   JOIN recruitment_candidate_applications applicationRow ON applicationRow.PositionId=hiringCase.PositionId AND applicationRow.ApplicationType='Application'
   JOIN recruitment_interviews interviewRow ON interviewRow.ApplicationId=applicationRow.Id
   JOIN recruitment_interview_panel_members panel ON panel.InterviewId=interviewRow.Id
   WHERE hiringCase.Id=documentRow.HiringCaseId),0),
 NULLIF((SELECT COUNT(*) FROM recruitment_stage_default_panel_members panel
   WHERE panel.PipelineStageId=documentRow.PipelineStageId AND panel.IsRequired=TRUE),0),1)) Required
FROM recruitment_process_documents documentRow WHERE documentRow.Id=@Id", new { Id = documentId });
        return gate ?? new SignatureGate { Required = 1 };
    }

    public async Task<IReadOnlyList<RecruitmentProcessDocument>> ListProcessDocumentsAsync(AuthUser user, long? hiringCaseId, long? applicationId)
    {
        await using var db = Db();
        await db.OpenAsync();
        return (await db.QueryAsync<RecruitmentProcessDocument>(@"SELECT documentRow.*,
EXISTS (SELECT 1 FROM entity_attachments attachment
    WHERE attachment.entity_type='RECRUITMENT_PROCESS_DOCUMENT' AND attachment.entity_id=documentRow.Id
    AND attachment.is_current=TRUE AND attachment.is_deleted=FALSE
    AND NOT (attachment.public_id <=> documentRow.AttachmentPublicId)) HasFinalSignedAttachment,
(SELECT COUNT(*) FROM recruitment_process_document_signatures signatureRow
    WHERE signatureRow.ProcessDocumentId=documentRow.Id) SignatureCount,
GREATEST(1,COALESCE(
    NULLIF((SELECT COUNT(DISTINCT panel.PanelUserId)
      FROM recruitment_position_pipeline_instances hiringCase
      JOIN recruitment_candidate_applications applicationRow ON applicationRow.PositionId=hiringCase.PositionId AND applicationRow.ApplicationType='Application'
      JOIN recruitment_interviews interviewRow ON interviewRow.ApplicationId=applicationRow.Id
      JOIN recruitment_interview_panel_members panel ON panel.InterviewId=interviewRow.Id
      WHERE hiringCase.Id=documentRow.HiringCaseId),0),
    NULLIF((SELECT COUNT(*) FROM recruitment_stage_default_panel_members panel
      WHERE panel.PipelineStageId=documentRow.PipelineStageId AND panel.IsRequired=TRUE),0),1)) RequiredSignatureCount,
((SELECT COUNT(*) FROM recruitment_process_document_signatures signatureRow
    WHERE signatureRow.ProcessDocumentId=documentRow.Id)>=GREATEST(1,COALESCE(
    NULLIF((SELECT COUNT(DISTINCT panel.PanelUserId)
      FROM recruitment_position_pipeline_instances hiringCase
      JOIN recruitment_candidate_applications applicationRow ON applicationRow.PositionId=hiringCase.PositionId AND applicationRow.ApplicationType='Application'
      JOIN recruitment_interviews interviewRow ON interviewRow.ApplicationId=applicationRow.Id
      JOIN recruitment_interview_panel_members panel ON panel.InterviewId=interviewRow.Id
      WHERE hiringCase.Id=documentRow.HiringCaseId),0),
    NULLIF((SELECT COUNT(*) FROM recruitment_stage_default_panel_members panel
      WHERE panel.PipelineStageId=documentRow.PipelineStageId AND panel.IsRequired=TRUE),0),1))) CapturedSignaturesComplete
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
                var signatureGate = await SignatureGateAsync(db, request.Id);
                if (finalAttachmentCount == 0 && !signatureGate.Complete)
                    return (null, $"Upload the final signed document or collect all required signatures ({signatureGate.Captured}/{signatureGate.Required}) before marking it signed.");
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
        db.QueryAsync<RecruitmentHiringCase>(@"SELECT hiringCase.*,client.Name ClientName,workOrder.WorkOrderNumber,line.PositionName,line.PayBandLevelCode,line.Division,line.Location,
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

    private static async Task AllowPositionStageReentryAsync(MySqlConnection db)
    {
        var legacyUniqueIndex = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='recruitment_position_stage_instances'
  AND INDEX_NAME='UX_recruitment_position_stage' AND NON_UNIQUE=0");
        if (legacyUniqueIndex > 0)
            await db.ExecuteAsync("ALTER TABLE recruitment_position_stage_instances DROP INDEX UX_recruitment_position_stage");

        var historyIndex = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='recruitment_position_stage_instances'
  AND INDEX_NAME='IX_recruitment_position_stage_definition'");
        if (historyIndex == 0)
            await db.ExecuteAsync(@"CREATE INDEX IX_recruitment_position_stage_definition
ON recruitment_position_stage_instances (PositionPipelineInstanceId,PipelineStageId,Id)");
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
        public DateTime SlaAnchorAtUtc { get; set; }
        public string Location { get; set; } = "";
        public long PipelineVersionId { get; set; }
        public string PipelineStatus { get; set; } = "";
        public string ScopeType { get; set; } = "Application";
        public string SlaMode { get; set; } = "StageEntry";
        public int PipelineOverallSlaMinutes { get; set; }
    }

    private sealed class WorkOrderSlaSource
    {
        public long Id { get; set; }
        public int ClientId { get; set; }
        public string WorkOrderNumber { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Status { get; set; } = "Draft";
    }

    private static string FirstValue(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private sealed class AutomaticCaseSource
    {
        public long WorkOrderLineId { get; set; }
        public int ClientId { get; set; }
        public long? HiringCaseId { get; set; }
        public long? AssignedPipelineVersionId { get; set; }
    }

    private sealed class StageDefinition
    {
        public long Id { get; set; }
        public string StageCode { get; set; } = "";
        public string StageName { get; set; } = "";
        public string CardScope { get; set; } = "Position";
        public int DisplayOrder { get; set; }
        public int SlaDurationMinutes { get; set; }
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

    private sealed class AutomaticWorkOrderSource
    {
        public long Id { get; set; }
        public string RfrNumber { get; set; } = "";
        public DateTime RequestDate { get; set; }
        public int ClientId { get; set; }
        public string ClientName { get; set; } = "";
        public string PositionTitle { get; set; } = "";
        public int NumberOfOpenings { get; set; }
        public string JobLocation { get; set; } = "";
        public string BusinessUnit { get; set; } = "";
        public string Department { get; set; } = "";
        public long? OpenPositionId { get; set; }
        public long? WorkOrderId { get; set; }
        public int? WorkOrderLineNumber { get; set; }
    }

    private sealed class NextStageSource
    {
        public long StageInstanceId { get; set; }
        public long PipelineStageId { get; set; }
        public string StageName { get; set; } = "";
        public bool IsTerminal { get; set; }
        public string StageType { get; set; } = "";
    }

    private sealed class PreviousStageSource
    {
        public long PipelineVersionId { get; set; }
        public long FromStageId { get; set; }
        public string FromStageCode { get; set; } = "";
        public long ToStageId { get; set; }
        public string ToStageCode { get; set; } = "";
        public string StageName { get; set; } = "";
    }

    private sealed class TransitionTargetSource
    {
        public long TransitionId { get; set; }
        public long PipelineStageId { get; set; }
        public string StageName { get; set; } = "";
        public string StageType { get; set; } = "";
        public string CardScope { get; set; } = "Position";
        public bool IsTerminal { get; set; }
        public int SlaDurationMinutes { get; set; }
        public int? TargetOffsetMinutes { get; set; }
        public DateTime SlaAnchorAtUtc { get; set; }
        public string SlaMode { get; set; } = "StageEntry";
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

    private sealed class CandidateMilestoneSource
    {
        public long ApplicationId { get; set; }
        public long PositionId { get; set; }
        public int ClientId { get; set; }
        public string CandidateStageName { get; set; } = "";
        public string CandidateStageType { get; set; } = "";
        public bool HasInterview { get; set; }
        public bool HasCompletedInterviewDecision { get; set; }
        public string LatestOfferStatus { get; set; } = "";
        public bool IsJoined { get; set; }
    }

    private sealed class HiringAutomationStage
    {
        public long HiringCaseId { get; set; }
        public int ClientId { get; set; }
        public long PositionId { get; set; }
        public long CurrentStageInstanceId { get; set; }
        public long PipelineStageId { get; set; }
        public string StageCode { get; set; } = "";
        public string StageName { get; set; } = "";
        public string StageType { get; set; } = "";
        public bool IsTerminal { get; set; }
        public bool RequiresApproval { get; set; }
    }

    private sealed class SignatureGate
    {
        public int Captured { get; set; }
        public int Required { get; set; }
        public bool Complete => Captured >= Math.Max(1, Required);
    }

    private sealed class PanelSignatureAccess
    {
        public int ActualMemberCount { get; set; }
        public int IsActualMember { get; set; }
        public int DefaultMemberCount { get; set; }
        public int IsDefaultMember { get; set; }
    }

    private sealed class HiringMilestoneTransition
    {
        public long HiringCaseId { get; set; }
        public string OutcomeCode { get; set; } = "";
    }

    private sealed class ProfileReworkCaseSource
    {
        public long HiringCaseId { get; set; }
        public long PipelineVersionId { get; set; }
        public DateTime SlaAnchorAtUtc { get; set; }
        public long CurrentStageInstanceId { get; set; }
        public long CurrentPipelineStageId { get; set; }
        public string CurrentStageName { get; set; } = "";
        public string SlaMode { get; set; } = "StageEntry";
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
