using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using System.Text.Json;

namespace Payroll.API.Repositories;

public class RecruitmentRepository(IConfiguration configuration)
{
    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));
    private static readonly HashSet<string> CentralDropdownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Recruitment Status",
        "Position Status",
        "Publishing Channel",
        "Assignment Priority",
        "Recruitment Source",
        "Hiring Type",
        "Position Category",
        "Experience Range",
        "Budget Amount",
        "Interview Type",
        "Interview Result",
        "Interview Round",
        "Candidate Status",
        "Offer Status"
    };

    public async Task InitializeAsync()
    {
        await using var db = Db();
        await db.OpenAsync();
        await EnsureTablesAsync(db);
        await EnsureCatalogAsync(db);
    }

    public async Task<RecruitmentOptions> GetOptionsAsync(int employeeId, int? clientId, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        await EnsureTablesAsync(db);
        var employee = await db.QueryFirstOrDefaultAsync<RequesterRow>("SELECT Id,ClientId,Department FROM employees WHERE Id=@EmployeeId", new { EmployeeId = employeeId });
        var scopeClientId = clientId ?? user.ClientId ?? employee?.ClientId ?? 0;
        var setting = await GetSettingAsync(db, scopeClientId);
        var moduleEnabled = setting is not null && setting.RecruitmentEnabled && HasRecruitmentAccess(user);
        var enabled = moduleEnabled && setting is not null && setting.AllowEmployeeRfrCreation && HasRecruitmentCreateAccess(user);
        var options = new RecruitmentOptions
        {
            ModuleEnabled = moduleEnabled,
            Enabled = enabled,
            AllowReplacementHiring = setting?.AllowReplacementHiring ?? false,
            EnableInternalHiring = setting?.EnableInternalHiring ?? false,
            EnableReferralHiring = setting?.EnableReferralHiring ?? false,
            ClientName = await db.ExecuteScalarAsync<string>("SELECT COALESCE(Name,'') FROM clients WHERE Id=@ClientId", new { ClientId = scopeClientId }) ?? ""
        };
        options.PositionCategories = (await MasterValuesAsync(db, scopeClientId, "Position Category")).ToList();
        options.ExperienceRanges = (await MasterValuesAsync(db, scopeClientId, "Experience Range")).ToList();
        options.BudgetAmounts = (await MasterValuesAsync(db, scopeClientId, "Budget Amount")).ToList();
        options.HiringTypes = (await MasterValuesAsync(db, scopeClientId, "Hiring Type")).ToList();
        options.EmploymentTypes = await DropdownValuesAsync(db, scopeClientId, "Employment Type");
        options.Departments = await DropdownOrEmployeeValuesAsync(db, scopeClientId, "Department", "Department");
        options.Designations = await DropdownOrEmployeeValuesAsync(db, scopeClientId, "Designation", "Designation");
        options.Grades = await DropdownOrEmployeeValuesAsync(db, scopeClientId, "Employee Grade", "Grade");
        options.BusinessUnits = await DropdownValuesAsync(db, scopeClientId, "Business Unit");
        options.CostCenters = await DropdownValuesAsync(db, scopeClientId, "Cost Center");
        options.WorkLocations = (await db.QueryAsync<string>("SELECT Name FROM worklocations WHERE ClientId=@ClientId AND IsActive=TRUE ORDER BY Name", new { ClientId = scopeClientId })).ToList();
        options.Employees = (await db.QueryAsync<EmployeeLookup>(@"SELECT Id,EmployeeCode,CONCAT(FirstName,' ',COALESCE(LastName,'')) EmployeeName,Department,Designation FROM employees WHERE ClientId=@ClientId AND IsActive=TRUE ORDER BY FirstName,LastName", new { ClientId = scopeClientId })).ToList();
        if (setting is null || !setting.RecruitmentEnabled) options.ValidationMessages.Add("Recruitment is not enabled for this client.");
        else if (!HasRecruitmentAccess(user)) options.ValidationMessages.Add("Your user does not have recruitment access permission.");
        else if (!enabled) options.ValidationMessages.Add("Recruitment requisition creation requires Employee RFR creation and recruitment.rfr.create permission.");
        if (options.PositionCategories.Count == 0) options.ValidationMessages.Add("No active Position Category is configured in Dropdown Masters.");
        if (options.ExperienceRanges.Count == 0) options.ValidationMessages.Add("No active Experience Range is configured in Dropdown Masters.");
        if (options.BudgetAmounts.Count == 0) options.ValidationMessages.Add("No active Budget Amount is configured in Dropdown Masters.");
        if (options.HiringTypes.Count == 0) options.ValidationMessages.Add("No active Hiring Type is configured in Dropdown Masters.");
        return options;
    }

    public async Task<IEnumerable<RecruitmentRequisition>> GetMineAsync(int employeeId, int? clientId)
    {
        await using var db = Db();
        await db.OpenAsync();
        return await db.QueryAsync<RecruitmentRequisition>(ListSql("WHERE r.RequestedByEmployeeId=@EmployeeId AND (@ClientId IS NULL OR r.ClientId=@ClientId) ORDER BY r.UpdatedAt DESC"), new { EmployeeId = employeeId, ClientId = clientId });
    }

    public async Task<IEnumerable<RecruitmentRequisition>> SearchAsync(RecruitmentSearchRequest request, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var clientId = user.ClientId ?? request.ClientId;
        var where = @"WHERE (@ClientId IS NULL OR r.ClientId=@ClientId)
AND (@Status='' OR r.Status=@Status)
AND (@Department='' OR r.Department=@Department)
AND (@HiringType='' OR r.HiringType=@HiringType)
AND (@EmploymentType='' OR r.EmploymentType=@EmploymentType)
AND (@Priority='' OR r.HiringPriority=@Priority)
AND (@BusinessUnit='' OR r.BusinessUnit=@BusinessUnit)
AND (@PositionCategory='' OR r.PositionCategory=@PositionCategory)
AND (@Experience='' OR r.ExperienceRange=@Experience)
AND (@Location='' OR r.JobLocation=@Location)
AND (@Project='' OR r.Project=@Project)
AND (@ReplacementHiring IS NULL OR r.IsReplacement=@ReplacementHiring)
AND (@BudgetMin IS NULL OR r.BudgetAmount>=@BudgetMin)
AND (@BudgetMax IS NULL OR r.BudgetAmount<=@BudgetMax)
AND (@DateFrom IS NULL OR r.RequestDate>=@DateFrom)
AND (@DateTo IS NULL OR r.RequestDate<=@DateTo)
AND (@Query='' OR CONCAT(r.RfrNumber,' ',r.PositionTitle,' ',r.Department,' ',r.Project,' ',COALESCE(c.Name,''),' ',COALESCE(requester.FirstName,''),' ',COALESCE(requester.LastName,'')) LIKE CONCAT('%',@Query,'%'))
ORDER BY r.UpdatedAt DESC LIMIT 500";
        return await db.QueryAsync<RecruitmentRequisition>(ListSql(where), new { ClientId = clientId, request.Status, request.Department, request.HiringType, request.EmploymentType, request.Priority, request.BusinessUnit, request.PositionCategory, request.Experience, request.Location, request.Project, request.ReplacementHiring, request.BudgetMin, request.BudgetMax, request.DateFrom, request.DateTo, request.Query });
    }

    public async Task<RecruitmentRequisition?> GetAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var row = await db.QueryFirstOrDefaultAsync<RecruitmentRequisition>(ListSql("WHERE r.Id=@Id"), new { Id = id });
        if (row is null) return null;
        if (CanView(row, user)) return row;
        return null;
    }

    public async Task<(RecruitmentRequisition? Row, string Error)> SaveDraftAsync(SaveRecruitmentRequisition request, AuthUser user)
    {
        if (user.EmployeeId is null) return (null, "Employee link is required to create a recruitment requisition.");
        await using var db = Db();
        await db.OpenAsync();
        await EnsureTablesAsync(db);
        var employee = await db.QueryFirstOrDefaultAsync<RequesterRow>("SELECT Id,ClientId,Department FROM employees WHERE Id=@EmployeeId AND IsActive=TRUE", new { EmployeeId = user.EmployeeId.Value });
        if (employee is null) return (null, "Requester employee profile was not found.");
        var clientId = user.ClientId ?? employee.ClientId;
        var enabled = await IsRequesterAllowedAsync(db, clientId, user);
        if (!enabled) return (null, "Recruitment requisition creation is not enabled for your client.");
        var setting = await GetSettingAsync(db, clientId);
        var validation = await ValidateAsync(db, request, clientId, setting);
        if (!string.IsNullOrWhiteSpace(validation)) return (null, validation);
        if (request.Id > 0)
        {
            var existing = await db.QueryFirstOrDefaultAsync<RecruitmentRequisition>("SELECT * FROM recruitment_requisitions WHERE Id=@Id", new { request.Id });
            if (existing is null || existing.RequestedByEmployeeId != user.EmployeeId.Value) return (null, "Draft not found.");
            if (existing.Status is not ("Draft" or "Sent Back")) return (null, "Only draft or sent back requisitions can be edited.");
            await db.ExecuteAsync(UpdateSql, Payload(request, user, employee, clientId, existing.RfrNumber, existing.Id));
            await AuditAsync(db, request.Id, "Edit", user.Id, request);
            return (await GetAsync(request.Id, user), "");
        }
        var number = await NextRfrNumberAsync(db, clientId);
        var id = await db.ExecuteScalarAsync<long>(InsertSql + " SELECT LAST_INSERT_ID();", Payload(request, user, employee, clientId, number, 0));
        await AuditAsync(db, id, "Create Draft", user.Id, request);
        return (await GetAsync(id, user), "");
    }

    public async Task<(RecruitmentRequisition? Row, string Error)> SubmitAsync(long id, AuthUser user, WorkflowRepository workflows)
    {
        await using var db = Db();
        await db.OpenAsync();
        var row = await db.QueryFirstOrDefaultAsync<RecruitmentRequisition>("SELECT * FROM recruitment_requisitions WHERE Id=@Id", new { Id = id });
        if (row is null || row.RequestedByUserId != user.Id) return (null, "Requisition not found.");
        if (row.Status is not ("Draft" or "Sent Back")) return (null, "Only draft or sent back requisitions can be submitted.");
        await db.ExecuteAsync("UPDATE recruitment_requisitions SET Status='Pending Approval',SubmittedAt=UTC_TIMESTAMP(),UpdatedAt=UTC_TIMESTAMP() WHERE Id=@Id", new { Id = id });
        await AuditAsync(db, id, "Submit", user.Id, row);
        var workflowId = await GetApprovalWorkflowIdAsync(db, row.ClientId, workflows);
        if (workflowId is not null)
        {
            var instance = await workflows.StartAsync(new StartWorkflowRequest { WorkflowId = workflowId.Value, ResourceType = "RecruitmentRequisition", ResourceId = id.ToString(), PayloadJson = JsonSerializer.Serialize(row) }, user.Id);
            if (instance is not null) await db.ExecuteAsync("UPDATE recruitment_requisitions SET WorkflowInstanceId=@InstanceId WHERE Id=@Id", new { InstanceId = instance.Id, Id = id });
        }
        else
        {
            await db.ExecuteAsync("UPDATE recruitment_requisitions SET Status='Approved',UpdatedAt=UTC_TIMESTAMP() WHERE Id=@Id", new { Id = id });
            await CreateOpenPositionAsync(id, user.Id);
        }
        return (await GetAsync(id, user), "");
    }

    public async Task<(bool Ok, string Error)> WithdrawAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var affected = await db.ExecuteAsync("UPDATE recruitment_requisitions SET Status='Withdrawn',UpdatedAt=UTC_TIMESTAMP() WHERE Id=@Id AND RequestedByUserId=@UserId AND Status='Pending Approval'", new { Id = id, UserId = user.Id });
        if (affected == 0) return (false, "Only pending approval requisitions can be withdrawn.");
        await AuditAsync(db, id, "Withdraw", user.Id, new { id });
        return (true, "");
    }

    public async Task<(bool Ok, string Error)> DeleteDraftAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var affected = await db.ExecuteAsync("DELETE FROM recruitment_requisitions WHERE Id=@Id AND RequestedByUserId=@UserId AND Status='Draft'", new { Id = id, UserId = user.Id });
        if (affected == 0) return (false, "Only your own draft requisitions can be deleted.");
        await AuditAsync(db, id, "Delete Draft", user.Id, new { id });
        return (true, "");
    }

    public async Task SyncWorkflowStatusAsync(string resourceId, string status, int actorUserId)
    {
        if (!long.TryParse(resourceId, out var id)) return;
        await using var db = Db();
        await db.OpenAsync();
        var next = status == "Approved" ? "Approved" : status == "Rejected" ? "Rejected" : status == "Sent Back" ? "Sent Back" : "Pending Approval";
        await db.ExecuteAsync("UPDATE recruitment_requisitions SET Status=@Status,UpdatedAt=UTC_TIMESTAMP() WHERE Id=@Id", new { Status = next, Id = id });
        await AuditAsync(db, id, $"Workflow {next}", actorUserId, new { status });
        if (next == "Approved") await CreateOpenPositionAsync(id, actorUserId);
    }

    public async Task<RecruitmentDashboard> DashboardAsync(AuthUser user, bool own)
    {
        await using var db = Db();
        await db.OpenAsync();
        var filter = own && user.EmployeeId is not null ? "RequestedByEmployeeId=@EmployeeId" : "(@ClientId IS NULL OR ClientId=@ClientId)";
        var rows = (await db.QueryAsync<StatusCount>($"SELECT Status,COUNT(*) Count FROM recruitment_requisitions WHERE {filter} GROUP BY Status", new { EmployeeId = user.EmployeeId, ClientId = user.ClientId })).ToDictionary(x => x.Status, x => x.Count);
        var positionFilter = own && user.EmployeeId is not null
            ? "p.RequisitionId IN (SELECT Id FROM recruitment_requisitions WHERE RequestedByEmployeeId=@EmployeeId)"
            : "(@ClientId IS NULL OR p.ClientId=@ClientId)";
        return new RecruitmentDashboard
        {
            Drafts = rows.GetValueOrDefault("Draft"),
            PendingApproval = rows.GetValueOrDefault("Pending Approval"),
            Approved = rows.GetValueOrDefault("Approved"),
            Rejected = rows.GetValueOrDefault("Rejected"),
            Returned = rows.GetValueOrDefault("Sent Back"),
            Withdrawn = rows.GetValueOrDefault("Withdrawn"),
            OpenPositions = await db.ExecuteScalarAsync<int>($"SELECT COALESCE(SUM(RemainingPositions),0) FROM recruitment_open_positions p WHERE {positionFilter} AND p.Status NOT IN ('Closed','Cancelled','Filled')", new { EmployeeId = user.EmployeeId, ClientId = user.ClientId }),
            FilledPositions = await db.ExecuteScalarAsync<int>($"SELECT COALESCE(SUM(FilledPositions),0) FROM recruitment_open_positions p WHERE {positionFilter}", new { EmployeeId = user.EmployeeId, ClientId = user.ClientId }),
            CancelledPositions = await db.ExecuteScalarAsync<int>($"SELECT COALESCE(SUM(CancelledPositions),0) FROM recruitment_open_positions p WHERE {positionFilter}", new { EmployeeId = user.EmployeeId, ClientId = user.ClientId }),
            OnHoldPositions = await db.ExecuteScalarAsync<int>($"SELECT COALESCE(SUM(OnHoldPositions),0) FROM recruitment_open_positions p WHERE {positionFilter}", new { EmployeeId = user.EmployeeId, ClientId = user.ClientId }),
            RemainingPositions = await db.ExecuteScalarAsync<int>($"SELECT COALESCE(SUM(RemainingPositions),0) FROM recruitment_open_positions p WHERE {positionFilter}", new { EmployeeId = user.EmployeeId, ClientId = user.ClientId }),
            AverageApprovalHours = await db.ExecuteScalarAsync<decimal>($"SELECT COALESCE(AVG(TIMESTAMPDIFF(MINUTE,SubmittedAt,UpdatedAt))/60,0) FROM recruitment_requisitions WHERE {filter} AND Status='Approved' AND SubmittedAt IS NOT NULL", new { EmployeeId = user.EmployeeId, ClientId = user.ClientId }),
            DepartmentWiseHiring = await db.QueryAsync<RecruitmentMetric>($"SELECT COALESCE(NULLIF(Department,''),'Not specified') Label, COALESCE(SUM(RemainingPositions),0) Value FROM recruitment_open_positions p WHERE {positionFilter} GROUP BY Department ORDER BY Value DESC LIMIT 8", new { EmployeeId = user.EmployeeId, ClientId = user.ClientId }),
            CompanyWiseHiring = await db.QueryAsync<RecruitmentMetric>($"SELECT COALESCE(c.Name,'Not specified') Label, COALESCE(SUM(p.RemainingPositions),0) Value FROM recruitment_open_positions p LEFT JOIN clients c ON c.Id=p.ClientId WHERE {positionFilter} GROUP BY c.Name ORDER BY Value DESC LIMIT 8", new { EmployeeId = user.EmployeeId, ClientId = user.ClientId }),
            PriorityWiseHiring = await db.QueryAsync<RecruitmentMetric>($"SELECT COALESCE(NULLIF(HiringPriority,''),'Normal') Label, COALESCE(SUM(RemainingPositions),0) Value FROM recruitment_open_positions p WHERE {positionFilter} GROUP BY HiringPriority ORDER BY Value DESC", new { EmployeeId = user.EmployeeId, ClientId = user.ClientId }),
            UpcomingJoiningTargets = await db.QueryAsync<RecruitmentMetric>($"SELECT DATE_FORMAT(TargetJoiningDate,'%d-%m-%Y') Label, COALESCE(SUM(RemainingPositions),0) Value FROM recruitment_open_positions p WHERE {positionFilter} AND TargetJoiningDate IS NOT NULL AND TargetJoiningDate>=CURRENT_DATE GROUP BY TargetJoiningDate ORDER BY TargetJoiningDate LIMIT 8", new { EmployeeId = user.EmployeeId, ClientId = user.ClientId })
        };
    }

    public async Task<IEnumerable<RecruitmentOpenPosition>> OpenPositionsAsync(AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        return await db.QueryAsync<RecruitmentOpenPosition>(OpenPositionSql("WHERE (@ClientId IS NULL OR p.ClientId=@ClientId) ORDER BY p.CreatedAt DESC"), new { ClientId = user.ClientId });
    }

    public async Task<IEnumerable<string>> MasterOptionsAsync(string masterType, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        return await MasterValuesAsync(db, user.ClientId ?? 0, masterType);
    }

    public async Task<RecruitmentOperationsOptions> OperationsOptionsAsync(AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var clientId = user.ClientId ?? 0;
        var setting = await GetSettingAsync(db, clientId);
        return new RecruitmentOperationsOptions
        {
            AllowMultipleRecruiters = setting?.AllowMultipleRecruiters ?? false,
            EnableVendorHiring = setting?.EnableVendorHiring ?? false,
            EnableConsultantHiring = setting?.EnableConsultantHiring ?? false,
            EnableInternalHiring = setting?.EnableInternalHiring ?? false,
            EnableReferralHiring = setting?.EnableReferralHiring ?? false,
            EnableDocumentVerification = setting?.EnableDocumentVerification ?? false,
            Recruiters = await db.QueryAsync<AuthUser>(@"SELECT DISTINCT u.Id,u.Email,u.DisplayName,u.ClientId,u.EmployeeId,u.IsActive,u.MustChangePassword
FROM authusers u
LEFT JOIN authuserroles ur ON ur.UserId=u.Id
LEFT JOIN authroles r ON r.Id=ur.RoleId
LEFT JOIN authrolepermissions rp ON rp.RoleId=r.Id
LEFT JOIN authpermissions p ON p.Id=rp.PermissionId
WHERE u.IsActive=TRUE AND (@ClientId=0 OR u.ClientId IS NULL OR u.ClientId=@ClientId) AND (p.Code IN ('recruitment.manage','recruitment.position.manage') OR r.Code IN ('admin','hr_manager'))
ORDER BY u.DisplayName,u.Email", new { ClientId = clientId }),
            Vendors = await db.QueryAsync<RecruitmentPartner>("SELECT *,'' ClientName FROM recruitment_partners WHERE PartnerType='Vendor' AND IsActive=TRUE AND (@ClientId=0 OR ClientId=@ClientId) ORDER BY Name", new { ClientId = clientId }),
            Consultants = await db.QueryAsync<RecruitmentPartner>("SELECT *,'' ClientName FROM recruitment_partners WHERE PartnerType='Consultant' AND IsActive=TRUE AND (@ClientId=0 OR ClientId=@ClientId) ORDER BY Name", new { ClientId = clientId }),
            PositionStatuses = await MasterValuesAsync(db, clientId, "Position Status"),
            PublishingChannels = await MasterValuesAsync(db, clientId, "Publishing Channel"),
            AssignmentPriorities = await MasterValuesAsync(db, clientId, "Assignment Priority")
        };
    }

    public async Task<RecruitmentPositionDetail?> OpenPositionDetailAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var position = await db.QueryFirstOrDefaultAsync<RecruitmentOpenPosition>(OpenPositionSql("WHERE p.Id=@Id AND (@ClientId IS NULL OR p.ClientId=@ClientId)"), new { Id = id, ClientId = user.ClientId });
        if (position is null) return null;
        var setting = await GetSettingAsync(db, position.ClientId);
        return new RecruitmentPositionDetail
        {
            Position = position,
            AllowMultipleRecruiters = setting?.AllowMultipleRecruiters ?? false,
            EnableVendorHiring = setting?.EnableVendorHiring ?? false,
            EnableConsultantHiring = setting?.EnableConsultantHiring ?? false,
            EnableInternalHiring = setting?.EnableInternalHiring ?? false,
            EnableReferralHiring = setting?.EnableReferralHiring ?? false,
            EnableDocumentVerification = setting?.EnableDocumentVerification ?? false,
            Timeline = await db.QueryAsync<RecruitmentPositionTimeline>(@"SELECT t.*,COALESCE(u.DisplayName,u.Email,'System') ActorName FROM recruitment_position_timeline t LEFT JOIN authusers u ON u.Id=t.ActorUserId WHERE t.PositionId=@Id ORDER BY t.CreatedAt,t.Id", new { Id = id }),
            Notes = await db.QueryAsync<RecruitmentPositionNote>(@"SELECT n.*,COALESCE(u.DisplayName,u.Email,'') CreatedByName FROM recruitment_position_notes n LEFT JOIN authusers u ON u.Id=n.CreatedByUserId WHERE n.PositionId=@Id ORDER BY n.CreatedAt DESC,n.Id DESC", new { Id = id }),
            Checklist = setting?.EnableDocumentVerification == true ? await db.QueryAsync<RecruitmentPositionChecklistItem>("SELECT * FROM recruitment_position_checklist WHERE PositionId=@Id ORDER BY Mandatory DESC,Stage,ChecklistName", new { Id = id }) : [],
            RecruiterAssignments = await db.QueryAsync<RecruitmentRecruiterAssignment>(@"SELECT a.*,COALESCE(pr.DisplayName,pr.Email,'') PrimaryRecruiterName,COALESCE(sr.DisplayName,sr.Email,'') SecondaryRecruiterName,COALESCE(byu.DisplayName,byu.Email,'') AssignedByName FROM recruitment_recruiter_assignments a LEFT JOIN authusers pr ON pr.Id=a.PrimaryRecruiterUserId LEFT JOIN authusers sr ON sr.Id=a.SecondaryRecruiterUserId LEFT JOIN authusers byu ON byu.Id=a.AssignedByUserId WHERE a.PositionId=@Id ORDER BY a.CreatedAt DESC,a.Id DESC", new { Id = id }),
            VendorAssignments = await PartnerAssignmentsAsync(db, id, "Vendor"),
            ConsultantAssignments = await PartnerAssignmentsAsync(db, id, "Consultant"),
            Publications = await db.QueryAsync<RecruitmentJobPublication>(@"SELECT p.*,COALESCE(u.DisplayName,u.Email,'') PublishedByName FROM recruitment_job_publications p LEFT JOIN authusers u ON u.Id=p.PublishedByUserId WHERE p.PositionId=@Id ORDER BY p.PublishingDate DESC,p.Id DESC", new { Id = id }),
            ReferralCampaigns = await db.QueryAsync<RecruitmentReferralCampaign>(@"SELECT c.*,COALESCE(u.DisplayName,u.Email,'') CreatedByName FROM recruitment_referral_campaigns c LEFT JOIN authusers u ON u.Id=c.CreatedByUserId WHERE c.PositionId=@Id ORDER BY c.CreatedAt DESC,c.Id DESC", new { Id = id })
        };
    }

    public async Task<(RecruitmentPositionDetail? Detail, string Error)> AssignRecruiterAsync(long id, SaveRecruiterAssignment request, AuthUser user, NotificationRepository notifications)
    {
        if (request.PrimaryRecruiterUserId <= 0) return (null, "Primary recruiter is required.");
        await using var db = Db();
        await db.OpenAsync();
        var position = await PositionForActionAsync(db, id, user);
        if (position is null) return (null, "Open position not found.");
        var setting = await GetSettingAsync(db, position.ClientId);
        if (setting?.AllowMultipleRecruiters != true) request.SecondaryRecruiterUserId = 0;
        await db.ExecuteAsync("UPDATE recruitment_recruiter_assignments SET AssignmentStatus='Reassigned' WHERE PositionId=@Id AND AssignmentStatus='Active'", new { Id = id });
        await db.ExecuteAsync(@"INSERT INTO recruitment_recruiter_assignments (PositionId,PrimaryRecruiterUserId,SecondaryRecruiterUserId,AssignmentDate,AssignmentReason,AssignmentStatus,AssignedByUserId)
VALUES (@Id,@PrimaryRecruiterUserId,@SecondaryRecruiterUserId,UTC_TIMESTAMP(),@AssignmentReason,'Active',@UserId)", new { Id = id, request.PrimaryRecruiterUserId, request.SecondaryRecruiterUserId, request.AssignmentReason, UserId = user.Id });
        await db.ExecuteAsync("UPDATE recruitment_open_positions SET RecruiterUserId=@RecruiterUserId,Status='Recruiter Assigned',UpdatedAt=UTC_TIMESTAMP() WHERE Id=@Id", new { Id = id, RecruiterUserId = request.PrimaryRecruiterUserId });
        await AddTimelineAsync(db, id, "Recruiter Assigned", "Recruiter assigned", request.AssignmentReason, user.Id);
        await AuditPositionAsync(db, id, "Recruiter Assignment", user.Id, request);
        await NotifyAsync(notifications, "RECRUITMENT.RECRUITER_ASSIGNED", position, user, request);
        return (await OpenPositionDetailAsync(id, user), "");
    }

    public async Task<(RecruitmentPositionDetail? Detail, string Error)> AssignPartnerAsync(long id, string partnerType, SavePartnerAssignment request, AuthUser user, NotificationRepository notifications)
    {
        if (request.PartnerId <= 0) return (null, $"{partnerType} is required.");
        await using var db = Db();
        await db.OpenAsync();
        var position = await PositionForActionAsync(db, id, user);
        if (position is null) return (null, "Open position not found.");
        var setting = await GetSettingAsync(db, position.ClientId);
        if (partnerType.Equals("Vendor", StringComparison.OrdinalIgnoreCase) && setting?.EnableVendorHiring != true) return (null, "Vendor hiring is disabled for this client.");
        if (partnerType.Equals("Consultant", StringComparison.OrdinalIgnoreCase) && setting?.EnableConsultantHiring != true) return (null, "Consultant hiring is disabled for this client.");
        var exists = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_partners WHERE Id=@PartnerId AND PartnerType=@PartnerType AND IsActive=TRUE", new { request.PartnerId, PartnerType = partnerType });
        if (exists == 0) return (null, $"{partnerType} is not active in Recruitment Administration.");
        await db.ExecuteAsync(@"INSERT INTO recruitment_partner_assignments (PositionId,PartnerType,PartnerId,AssignmentDate,Priority,DueDate,ExpectedProfiles,Status,Remarks,AssignedByUserId)
VALUES (@Id,@PartnerType,@PartnerId,UTC_TIMESTAMP(),@Priority,@DueDate,@ExpectedProfiles,'Assigned',@Remarks,@UserId)", new { Id = id, PartnerType = partnerType, request.PartnerId, request.Priority, request.DueDate, request.ExpectedProfiles, request.Remarks, UserId = user.Id });
        await AddTimelineAsync(db, id, $"{partnerType} Assigned", $"{partnerType} assigned", request.Remarks, user.Id);
        await AuditPositionAsync(db, id, $"{partnerType} Assignment", user.Id, request);
        await NotifyAsync(notifications, partnerType.Equals("Vendor", StringComparison.OrdinalIgnoreCase) ? "RECRUITMENT.VENDOR_ASSIGNED" : "RECRUITMENT.CONSULTANT_ASSIGNED", position, user, request);
        return (await OpenPositionDetailAsync(id, user), "");
    }

    public async Task<(RecruitmentPositionDetail? Detail, string Error)> PublishPositionAsync(long id, SaveJobPublication request, AuthUser user, NotificationRepository notifications)
    {
        if (string.IsNullOrWhiteSpace(request.Channel)) return (null, "Publishing channel is required.");
        await using var db = Db();
        await db.OpenAsync();
        var position = await PositionForActionAsync(db, id, user);
        if (position is null) return (null, "Open position not found.");
        var setting = await GetSettingAsync(db, position.ClientId);
        if (request.Channel.Contains("Internal", StringComparison.OrdinalIgnoreCase) && setting?.EnableInternalHiring != true) return (null, "Internal hiring is disabled for this client.");
        if (request.Channel.Contains("Referral", StringComparison.OrdinalIgnoreCase) && setting?.EnableReferralHiring != true) return (null, "Referral hiring is disabled for this client.");
        await db.ExecuteAsync(@"INSERT INTO recruitment_job_publications (PositionId,Channel,PublishingDate,ExpiryDate,Status,Remarks,PublishedByUserId)
VALUES (@Id,@Channel,@PublishingDate,@ExpiryDate,@Status,@Remarks,@UserId)", new { Id = id, request.Channel, PublishingDate = request.PublishingDate ?? DateTime.Today, request.ExpiryDate, request.Status, request.Remarks, UserId = user.Id });
        await db.ExecuteAsync("UPDATE recruitment_open_positions SET Status=CASE WHEN Status='Open' THEN 'Published' ELSE Status END,PublishedAt=COALESCE(PublishedAt,UTC_TIMESTAMP()),UpdatedAt=UTC_TIMESTAMP() WHERE Id=@Id", new { Id = id });
        await AddTimelineAsync(db, id, "Published", $"Published on {request.Channel}", request.Remarks, user.Id);
        await AuditPositionAsync(db, id, "Job Publishing", user.Id, request);
        await NotifyAsync(notifications, "RECRUITMENT.POSITION_PUBLISHED", position, user, request);
        return (await OpenPositionDetailAsync(id, user), "");
    }

    public async Task<(RecruitmentPositionDetail? Detail, string Error)> CreateReferralCampaignAsync(long id, SaveReferralCampaign request, AuthUser user, NotificationRepository notifications)
    {
        if (string.IsNullOrWhiteSpace(request.CampaignName)) return (null, "Campaign name is required.");
        if (request.EndDate.Date < request.StartDate.Date) return (null, "Campaign end date cannot be before start date.");
        await using var db = Db();
        await db.OpenAsync();
        var position = await PositionForActionAsync(db, id, user);
        if (position is null) return (null, "Open position not found.");
        var setting = await GetSettingAsync(db, position.ClientId);
        if (setting?.EnableReferralHiring != true) return (null, "Referral hiring is disabled for this client.");
        await db.ExecuteAsync(@"INSERT INTO recruitment_referral_campaigns (PositionId,CampaignName,StartDate,EndDate,ReferralReward,VisibilityCompany,VisibilityDepartment,VisibilityBusinessUnit,VisibilityLocation,VisibilityEmploymentType,Status,CreatedByUserId)
VALUES (@Id,@CampaignName,@StartDate,@EndDate,@ReferralReward,'',@VisibilityDepartment,@VisibilityBusinessUnit,@VisibilityLocation,@VisibilityEmploymentType,@Status,@UserId)", new { Id = id, request.CampaignName, request.StartDate, request.EndDate, request.ReferralReward, request.VisibilityDepartment, request.VisibilityBusinessUnit, request.VisibilityLocation, request.VisibilityEmploymentType, request.Status, UserId = user.Id });
        await AddTimelineAsync(db, id, "Referral Opened", "Referral campaign started", request.CampaignName, user.Id);
        await AuditPositionAsync(db, id, "Referral Campaign", user.Id, request);
        await NotifyAsync(notifications, "RECRUITMENT.REFERRAL_STARTED", position, user, request);
        return (await OpenPositionDetailAsync(id, user), "");
    }

    public async Task<IEnumerable<RecruitmentInternalOpening>> InternalOpeningsAsync(AuthUser user)
    {
        if (user.EmployeeId is null) return [];
        await using var db = Db();
        await db.OpenAsync();
        var employee = await db.QueryFirstOrDefaultAsync<EmployeeScope>("SELECT ClientId,Department,BusinessUnit,WorkLocation,EmploymentType FROM employees WHERE Id=@Id", new { Id = user.EmployeeId });
        if (employee is null) return [];
        var setting = await GetSettingAsync(db, employee.ClientId);
        if (setting?.EnableInternalHiring != true || setting?.EnableReferralHiring != true) return [];
        return await db.QueryAsync<RecruitmentInternalOpening>(@"SELECT p.Id PositionId,p.PositionCode,p.PositionTitle,p.Department,p.JobLocation,p.EmploymentType,p.HiringType,p.ExperienceRange,p.RequiredSkills,p.TargetJoiningDate,c.CampaignName,c.ReferralReward,c.EndDate
FROM recruitment_open_positions p
JOIN recruitment_referral_campaigns c ON c.PositionId=p.Id AND c.Status='Open' AND CURRENT_DATE BETWEEN c.StartDate AND c.EndDate
WHERE p.ClientId=@ClientId AND p.Status NOT IN ('Closed','Cancelled','Filled')
  AND EXISTS (SELECT 1 FROM recruitment_job_publications pub WHERE pub.PositionId=p.Id AND pub.Status='Published' AND pub.Channel IN ('Internal Job Portal','Employee Referral'))
  AND (c.VisibilityDepartment='' OR c.VisibilityDepartment=@Department)
  AND (c.VisibilityBusinessUnit='' OR c.VisibilityBusinessUnit=@BusinessUnit)
  AND (c.VisibilityLocation='' OR c.VisibilityLocation=@WorkLocation)
  AND (c.VisibilityEmploymentType='' OR c.VisibilityEmploymentType=@EmploymentType)
ORDER BY c.EndDate,p.HiringPriority DESC,p.PositionTitle", employee);
    }

    public async Task<IEnumerable<RecruitmentEmployeeReferral>> MyReferralsAsync(AuthUser user)
    {
        if (user.EmployeeId is null) return [];
        await using var db = Db();
        await db.OpenAsync();
        return await db.QueryAsync<RecruitmentEmployeeReferral>(ReferralSql("WHERE r.ReferrerEmployeeId=@EmployeeId ORDER BY r.CreatedAt DESC"), new { EmployeeId = user.EmployeeId });
    }

    public async Task<(RecruitmentEmployeeReferral? Row, string Error)> SubmitReferralAsync(SaveEmployeeReferral request, AuthUser user, NotificationRepository notifications)
    {
        if (user.EmployeeId is null) return (null, "Employee profile is required.");
        if (string.IsNullOrWhiteSpace(request.CandidateName)) return (null, "Candidate name is required.");
        await using var db = Db();
        await db.OpenAsync();
        var visible = (await InternalOpeningsAsync(user)).Any(x => x.PositionId == request.PositionId);
        if (!visible) return (null, "This opening is not available for referral.");
        var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_employee_referrals (PositionId,ReferrerEmployeeId,CandidateName,CandidateEmail,CandidatePhone,Relationship,Remarks,Status)
VALUES (@PositionId,@EmployeeId,@CandidateName,@CandidateEmail,@CandidatePhone,@Relationship,@Remarks,'Submitted');SELECT LAST_INSERT_ID();", new { request.PositionId, EmployeeId = user.EmployeeId.Value, request.CandidateName, request.CandidateEmail, request.CandidatePhone, request.Relationship, request.Remarks });
        await db.ExecuteAsync("UPDATE recruitment_open_positions SET CandidateCount=CandidateCount+1,UpdatedAt=UTC_TIMESTAMP() WHERE Id=@PositionId", request);
        await AddTimelineAsync(db, request.PositionId, "Referral Submitted", $"Referral submitted: {request.CandidateName}", "", user.Id);
        var position = await db.QueryFirstAsync<RecruitmentOpenPosition>("SELECT * FROM recruitment_open_positions WHERE Id=@PositionId", request);
        await NotifyAsync(notifications, "RECRUITMENT.REFERRAL_SUBMITTED", position, user, request);
        return (await db.QueryFirstOrDefaultAsync<RecruitmentEmployeeReferral>(ReferralSql("WHERE r.Id=@Id"), new { Id = id }), "");
    }

    public async Task<(RecruitmentPositionDetail? Detail, string Error)> AddPositionNoteAsync(long id, SaveRecruitmentPositionNote request, AuthUser user)
    {
        if (string.IsNullOrWhiteSpace(request.NoteText)) return (null, "Note text is required.");
        await using var db = Db();
        await db.OpenAsync();
        var clientId = await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM recruitment_open_positions WHERE Id=@Id", new { Id = id });
        if (clientId is null || (user.ClientId is not null && user.ClientId != clientId)) return (null, "Open position not found.");
        await db.ExecuteAsync("INSERT INTO recruitment_position_notes (PositionId,NoteType,NoteText,CreatedByUserId) VALUES (@Id,@NoteType,@NoteText,@UserId)", new { Id = id, request.NoteType, request.NoteText, UserId = user.Id });
        await AddTimelineAsync(db, id, "Internal Note", "Internal note added", request.NoteType, user.Id);
        await AuditPositionAsync(db, id, "Internal Note", user.Id, request);
        return (await OpenPositionDetailAsync(id, user), "");
    }

    public async Task<(RecruitmentPositionDetail? Detail, string Error)> UpdatePositionStatusAsync(long id, UpdateRecruitmentPositionStatus request, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var position = await db.QueryFirstOrDefaultAsync<RecruitmentOpenPosition>("SELECT * FROM recruitment_open_positions WHERE Id=@Id", new { Id = id });
        if (position is null || (user.ClientId is not null && user.ClientId != position.ClientId)) return (null, "Open position not found.");
        if (!await ExistsMasterAsync(db, position.ClientId, "Position Status", request.Status)) return (null, "Selected position status is not configured in Dropdown Masters.");
        var remaining = Math.Max(0, position.ApprovedPositions - position.FilledPositions - position.CancelledPositions - position.OnHoldPositions);
        await db.ExecuteAsync("UPDATE recruitment_open_positions SET Status=@Status,RemainingPositions=@Remaining,UpdatedAt=UTC_TIMESTAMP() WHERE Id=@Id", new { Id = id, request.Status, Remaining = remaining });
        await AddTimelineAsync(db, id, "Status Change", $"Status changed to {request.Status}", request.Comment, user.Id);
        await AuditPositionAsync(db, id, "Status Change", user.Id, request);
        return (await OpenPositionDetailAsync(id, user), "");
    }

    public async Task<object> TrailAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var row = await db.QueryFirstOrDefaultAsync<RecruitmentRequisition>("SELECT * FROM recruitment_requisitions WHERE Id=@Id", new { Id = id });
        if (row is null || !CanView(row, user)) return new { events = Array.Empty<object>() };
        var instance = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT * FROM workflowinstances WHERE ResourceType='RecruitmentRequisition' AND ResourceId=@Id ORDER BY Id DESC LIMIT 1", new { Id = id.ToString() });
        if (instance is null) return new { resourceType = "RecruitmentRequisition", status = row.Status, events = Array.Empty<object>() };
        var events = await db.QueryAsync(@"SELECT h.Action,h.Comment,h.CreatedAt,COALESCE(u.DisplayName,'System') Actor,COALESCE(s.Name,'') StageName,FALSE IsPending FROM workflowhistory h LEFT JOIN authusers u ON u.Id=h.ActorUserId LEFT JOIN workflowtasks t ON t.Id=h.TaskId LEFT JOIN workflowstages s ON s.Id=t.StageId WHERE h.InstanceId=@Id ORDER BY h.CreatedAt", new { Id = (long)instance.Id });
        return new { instanceId = (long)instance.Id, workflowCode = "", workflowName = "", resourceType = "RecruitmentRequisition", matchScope = "", status = row.Status, createdAt = (DateTime)instance.CreatedAt, completedAt = instance.CompletedAt, events };
    }

    private async Task<long> CreateOpenPositionAsync(long requisitionId, int actorUserId)
    {
        await using var db = Db();
        await db.OpenAsync();
        var r = await db.QueryFirstAsync<RecruitmentRequisition>("SELECT * FROM recruitment_requisitions WHERE Id=@Id", new { Id = requisitionId });
        var existingId = await db.ExecuteScalarAsync<long?>("SELECT Id FROM recruitment_open_positions WHERE RequisitionId=@Id", new { Id = requisitionId });
        if (existingId is not null) return existingId.Value;
        var code = await NextPositionNumberAsync(db, r.ClientId);
        var snapshot = JsonSerializer.Serialize(r);
        var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO recruitment_open_positions (RequisitionId,PositionCode,ClientId,BranchId,BusinessUnit,Department,CostCenter,PositionTitle,PositionCategory,EmploymentType,HiringType,NumberOfPositions,ApprovedPositions,FilledPositions,CancelledPositions,OnHoldPositions,RemainingPositions,TargetJoiningDate,JobLocation,Project,BudgetAvailable,BudgetAmount,SalaryMin,SalaryMax,Currency,HiringPriority,RequiredSkills,PreferredSkills,ExperienceRange,Status,SnapshotJson)
VALUES (@Id,@Code,@ClientId,@BranchId,@BusinessUnit,@Department,@CostCenter,@PositionTitle,@PositionCategory,@EmploymentType,@HiringType,@NumberOfOpenings,@NumberOfOpenings,0,0,0,@NumberOfOpenings,@TargetJoiningDate,@JobLocation,@Project,@BudgetAvailable,@BudgetAmount,@SalaryMin,@SalaryMax,@Currency,@HiringPriority,@RequiredSkills,@PreferredSkills,@ExperienceRange,'Open',@Snapshot);
SELECT LAST_INSERT_ID();", new { r.Id, Code = code, r.ClientId, r.BranchId, r.BusinessUnit, r.Department, r.CostCenter, r.PositionTitle, r.PositionCategory, r.EmploymentType, r.HiringType, r.NumberOfOpenings, r.TargetJoiningDate, r.JobLocation, r.Project, r.BudgetAvailable, r.BudgetAmount, r.SalaryMin, r.SalaryMax, r.Currency, r.HiringPriority, r.RequiredSkills, r.PreferredSkills, r.ExperienceRange, Snapshot = snapshot });
        await db.ExecuteAsync("UPDATE recruitment_requisitions SET OpenPositionId=@OpenPositionId WHERE Id=@Id", new { OpenPositionId = id, Id = requisitionId });
        await AddTimelineAsync(db, id, "Created", "Open position created", $"Created from {r.RfrNumber}", actorUserId);
        await AddTimelineAsync(db, id, "Approved", "Requisition approved", r.RfrNumber, actorUserId);
        await ApplyRecruiterAssignmentRuleAsync(db, id, r, actorUserId);
        var setting = await GetSettingAsync(db, r.ClientId);
        if (setting?.EnableDocumentVerification == true) await CreateChecklistSnapshotAsync(db, id, r);
        await AuditAsync(db, requisitionId, "Open Position Creation", actorUserId, new { openPositionId = id });
        await AuditPositionAsync(db, id, "Create", actorUserId, new { requisitionId, positionCode = code });
        return id;
    }

    private static bool CanView(RecruitmentRequisition row, AuthUser user) => user.Permissions.Contains("recruitment.manage", StringComparer.OrdinalIgnoreCase) || user.Permissions.Contains("recruitment.rfr.view", StringComparer.OrdinalIgnoreCase) || row.RequestedByUserId == user.Id || (user.ClientId is not null && row.ClientId == user.ClientId);
    private static async Task<bool> IsRequesterAllowedAsync(MySqlConnection db, int clientId, AuthUser user)
    {
        if (!HasRecruitmentCreateAccess(user)) return false;
        return await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_settings WHERE ClientId=@ClientId AND RecruitmentEnabled=TRUE AND AllowEmployeeRfrCreation=TRUE AND IsActive=TRUE", new { ClientId = clientId }) > 0;
    }

    public static bool HasRecruitmentAccess(AuthUser user) =>
        user.Permissions.Contains("recruitment.manage", StringComparer.OrdinalIgnoreCase) ||
        user.Permissions.Contains("recruitment.rfr.view", StringComparer.OrdinalIgnoreCase) ||
        user.Permissions.Contains("recruitment.rfr.create", StringComparer.OrdinalIgnoreCase);

    public static bool HasRecruitmentCreateAccess(AuthUser user) =>
        user.Permissions.Contains("recruitment.manage", StringComparer.OrdinalIgnoreCase) ||
        user.Permissions.Contains("recruitment.rfr.create", StringComparer.OrdinalIgnoreCase);

    private static Task<RecruitmentSettingRow?> GetSettingAsync(MySqlConnection db, int clientId) =>
        db.QueryFirstOrDefaultAsync<RecruitmentSettingRow>("SELECT * FROM recruitment_settings WHERE ClientId=@ClientId AND IsActive=TRUE ORDER BY Id DESC LIMIT 1", new { ClientId = clientId });

    private static async Task<int?> GetApprovalWorkflowIdAsync(MySqlConnection db, int clientId, WorkflowRepository workflows)
    {
        var mapped = await db.ExecuteScalarAsync<int?>("SELECT WorkflowId FROM recruitment_approval_mappings WHERE ClientId=@ClientId AND ProcessCode='RFR_APPROVAL' AND IsActive=TRUE AND WorkflowId>0 LIMIT 1", new { ClientId = clientId });
        return mapped ?? await workflows.GetDefaultIdForActivityAsync("RFR.SUBMIT", clientId) ?? await workflows.GetDefaultIdAsync("RecruitmentRequisition", clientId);
    }

    private static async Task<string> ValidateAsync(MySqlConnection db, SaveRecruitmentRequisition request, int clientId, RecruitmentSettingRow? setting)
    {
        if (string.IsNullOrWhiteSpace(request.PositionTitle)) return "Position title is required.";
        if (string.IsNullOrWhiteSpace(request.Department)) return "Department is required.";
        if (request.NumberOfOpenings <= 0) return "Number of openings must be greater than zero.";
        if (request.IsReplacement && setting?.AllowReplacementHiring != true) return "Replacement hiring is disabled for this client.";
        if (request.IsReplacement && (request.ReplacementEmployeeId ?? 0) <= 0) return "Replacement employee is required for replacement hiring.";
        if (request.TargetJoiningDate is not null && request.TargetJoiningDate.Value.Date < DateTime.Today.AddDays(-1)) return "Target joining date cannot be in the past.";
        if (!string.IsNullOrWhiteSpace(request.HiringType) && !await ExistsMasterAsync(db, clientId, "Hiring Type", request.HiringType)) return "Selected hiring type is not active in Dropdown Masters.";
        if (!string.IsNullOrWhiteSpace(request.PositionCategory) && !await ExistsMasterAsync(db, clientId, "Position Category", request.PositionCategory)) return "Selected position category is not active in Dropdown Masters.";
        if (!string.IsNullOrWhiteSpace(request.ExperienceRange) && !await ExistsMasterAsync(db, clientId, "Experience Range", request.ExperienceRange)) return "Selected experience range is not active in Dropdown Masters.";
        if (request.BudgetAmount > 0 && !await ExistsBudgetMasterAsync(db, clientId, request.BudgetAmount)) return "Selected budget amount is not active in Dropdown Masters.";
        return "";
    }

    private static async Task<bool> ExistsMasterAsync(MySqlConnection db, int clientId, string type, string value)
    {
        if (CentralDropdownTypes.Contains(type))
            return await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dropdownmasters WHERE Type=@Type AND Value=@Value AND IsActive=TRUE AND (ClientId=0 OR ClientId=@ClientId OR ClientId IS NULL)", new { Type = type, Value = value, ClientId = clientId }) > 0
                || await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_master_values WHERE MasterType=@Type AND Name=@Value AND IsActive=TRUE AND ClientId IN (0,@ClientId)", new { Type = type, Value = value, ClientId = clientId }) > 0;
        return await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_master_values WHERE MasterType=@Type AND Name=@Value AND IsActive=TRUE AND ClientId IN (0,@ClientId)", new { Type = type, Value = value, ClientId = clientId }) > 0;
    }
    private static async Task<bool> ExistsBudgetMasterAsync(MySqlConnection db, int clientId, decimal value)
    {
        var rows = await MasterValuesAsync(db, clientId, "Budget Amount");
        var expected = value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return rows.Any(row => new string(row.Where(ch => char.IsDigit(ch) || ch == '.').ToArray()) == expected);
    }
    private static async Task<IEnumerable<string>> MasterValuesAsync(MySqlConnection db, int clientId, string type)
    {
        if (CentralDropdownTypes.Contains(type))
        {
            var values = await DropdownValuesAsync(db, clientId, type);
            if (values.Count > 0) return values;
        }
        return await db.QueryAsync<string>("SELECT Name FROM recruitment_master_values WHERE MasterType=@Type AND IsActive=TRUE AND ClientId IN (0,@ClientId) ORDER BY ClientId DESC,SortOrder,Name", new { Type = type, ClientId = clientId });
    }
    private static async Task<List<string>> DropdownValuesAsync(MySqlConnection db, int clientId, string type) => (await db.QueryAsync<string>("SELECT Value FROM dropdownmasters WHERE Type=@Type AND IsActive=TRUE AND (ClientId=0 OR ClientId=@ClientId OR ClientId IS NULL) ORDER BY Value", new { Type = type, ClientId = clientId })).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    private static async Task<List<string>> DropdownOrEmployeeValuesAsync(MySqlConnection db, int clientId, string type, string employeeColumn)
    {
        var values = await DropdownValuesAsync(db, clientId, type);
        if (values.Count > 0) return values;
        var allowedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Department", "Designation", "Grade" };
        if (!allowedColumns.Contains(employeeColumn)) return values;
        return (await db.QueryAsync<string>($"SELECT DISTINCT {employeeColumn} FROM employees WHERE ClientId=@ClientId AND IsActive=TRUE AND {employeeColumn}<>'' ORDER BY {employeeColumn}", new { ClientId = clientId })).ToList();
    }
    private static async Task<string> NextRfrNumberAsync(MySqlConnection db, int clientId)
    {
        var next = await db.ExecuteScalarAsync<int>("SELECT COUNT(*)+1 FROM recruitment_requisitions WHERE ClientId=@ClientId AND YEAR(CreatedAt)=YEAR(UTC_TIMESTAMP())", new { ClientId = clientId });
        var code = await db.ExecuteScalarAsync<string>("SELECT COALESCE(Code,CONCAT('C',Id)) FROM clients WHERE Id=@ClientId", new { ClientId = clientId }) ?? $"C{clientId}";
        return $"RFR-{code}-{DateTime.UtcNow:yyyy}-{next:D5}";
    }

    private static async Task<string> NextPositionNumberAsync(MySqlConnection db, int clientId)
    {
        var next = await db.ExecuteScalarAsync<int>(@"INSERT INTO recruitment_number_sequences (ClientId,SeriesCode,LastNumber) VALUES (@ClientId,'POS',0)
ON DUPLICATE KEY UPDATE LastNumber=LastNumber;
UPDATE recruitment_number_sequences SET LastNumber=LAST_INSERT_ID(LastNumber+1) WHERE ClientId=@ClientId AND SeriesCode='POS';
SELECT LAST_INSERT_ID();", new { ClientId = clientId });
        var code = await db.ExecuteScalarAsync<string>("SELECT COALESCE(Code,CONCAT('C',Id)) FROM clients WHERE Id=@ClientId", new { ClientId = clientId }) ?? $"C{clientId}";
        return $"POS-{code}-{next:D6}";
    }

    private static async Task CreateChecklistSnapshotAsync(MySqlConnection db, long positionId, RecruitmentRequisition r)
    {
        var rows = await db.QueryAsync<RecruitmentDocumentChecklist>(@"SELECT * FROM recruitment_document_checklist WHERE IsActive=TRUE AND (ClientId=@ClientId OR ClientId=0) AND (HiringType='' OR HiringType=@HiringType) ORDER BY ClientId DESC,Mandatory DESC,Stage,DocumentName", new { r.ClientId, r.HiringType });
        foreach (var row in rows.GroupBy(x => $"{x.Stage}|{x.DocumentName}", StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            await db.ExecuteAsync(@"INSERT INTO recruitment_position_checklist (PositionId,ChecklistName,Stage,Mandatory,IsCompleted) VALUES (@PositionId,@ChecklistName,@Stage,@Mandatory,FALSE)
ON DUPLICATE KEY UPDATE Mandatory=VALUES(Mandatory),Stage=VALUES(Stage)", new { PositionId = positionId, ChecklistName = row.DocumentName, row.Stage, row.Mandatory });
    }

    private static async Task ApplyRecruiterAssignmentRuleAsync(MySqlConnection db, long positionId, RecruitmentRequisition r, int actorUserId)
    {
        var rules = (await db.QueryAsync<RecruitmentAssignmentRule>(@"SELECT *
FROM recruitment_assignment_rules
WHERE ClientId=@ClientId
  AND IsActive=TRUE
  AND RecruiterUserId>0
  AND (BusinessUnit='' OR BusinessUnit=@BusinessUnit)
  AND (Department='' OR Department=@Department)
  AND (PositionCategory='' OR PositionCategory=@PositionCategory)
  AND (Project='' OR Project=@Project)
  AND (Location='' OR Location=@JobLocation)
  AND (ExperienceRange='' OR ExperienceRange=@ExperienceRange)
  AND (Priority='' OR Priority=@HiringPriority)
ORDER BY SortOrder, Id", r)).ToList();

        foreach (var rule in rules)
        {
            if (rule.WorkloadBased && rule.MaximumOpenPositions > 0)
            {
                var activeCount = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*)
FROM recruitment_open_positions
WHERE RecruiterUserId=@RecruiterUserId
  AND Status NOT IN ('Filled','Cancelled','Closed')", new { rule.RecruiterUserId });
                if (activeCount >= rule.MaximumOpenPositions) continue;
            }

            await db.ExecuteAsync(@"INSERT INTO recruitment_recruiter_assignments (PositionId,PrimaryRecruiterUserId,SecondaryRecruiterUserId,AssignmentDate,AssignmentReason,AssignmentStatus,AssignedByUserId)
VALUES (@PositionId,@RecruiterUserId,0,UTC_TIMESTAMP(),@Reason,'Active',@ActorUserId)", new { PositionId = positionId, rule.RecruiterUserId, Reason = $"Auto-assigned by rule: {rule.RuleName}", ActorUserId = actorUserId });
            await db.ExecuteAsync("UPDATE recruitment_open_positions SET RecruiterUserId=@RecruiterUserId,Status='Recruiter Assigned',UpdatedAt=UTC_TIMESTAMP() WHERE Id=@PositionId", new { PositionId = positionId, rule.RecruiterUserId });
            await AddTimelineAsync(db, positionId, "Recruiter Assigned", "Recruiter auto-assigned", rule.RuleName, actorUserId);
            await AuditPositionAsync(db, positionId, "Auto Recruiter Assignment", actorUserId, rule);
            return;
        }
    }

    private static Task AddTimelineAsync(MySqlConnection db, long positionId, string eventType, string title, string details, int? actorUserId) =>
        db.ExecuteAsync("INSERT INTO recruitment_position_timeline (PositionId,EventType,EventTitle,EventDetails,ActorUserId) VALUES (@PositionId,@EventType,@Title,@Details,@ActorUserId)", new { PositionId = positionId, EventType = eventType, Title = title, Details = details, ActorUserId = actorUserId });

    private static Task AuditPositionAsync(MySqlConnection db, long id, string action, int userId, object payload) =>
        db.ExecuteAsync("INSERT INTO recruitment_audit (EntityType,EntityId,Action,NewValueJson,ChangedByUserId) VALUES ('RecruitmentOpenPosition',@Id,@Action,@Json,@UserId)", new { Id = id, Action = action, Json = JsonSerializer.Serialize(payload), UserId = userId });

    private static async Task<RecruitmentOpenPosition?> PositionForActionAsync(MySqlConnection db, long id, AuthUser user) =>
        await db.QueryFirstOrDefaultAsync<RecruitmentOpenPosition>("SELECT * FROM recruitment_open_positions WHERE Id=@Id AND (@ClientId IS NULL OR ClientId=@ClientId)", new { Id = id, ClientId = user.ClientId });

    private static async Task<IEnumerable<RecruitmentPartnerAssignment>> PartnerAssignmentsAsync(MySqlConnection db, long positionId, string partnerType) =>
        await db.QueryAsync<RecruitmentPartnerAssignment>(@"SELECT a.*,COALESCE(p.Name,'') PartnerName,COALESCE(u.DisplayName,u.Email,'') AssignedByName
FROM recruitment_partner_assignments a
LEFT JOIN recruitment_partners p ON p.Id=a.PartnerId
LEFT JOIN authusers u ON u.Id=a.AssignedByUserId
WHERE a.PositionId=@PositionId AND a.PartnerType=@PartnerType
ORDER BY a.CreatedAt DESC,a.Id DESC", new { PositionId = positionId, PartnerType = partnerType });

    private static Task NotifyAsync(NotificationRepository notifications, string eventCode, RecruitmentOpenPosition position, AuthUser user, object payload) =>
        notifications.PublishEventAsync(new NotificationEvent
        {
            EventCode = eventCode,
            ResourceType = "RecruitmentOpenPosition",
            ResourceId = position.Id.ToString(),
            ClientId = position.ClientId,
            ActorUserId = user.Id,
            ActorName = user.DisplayName,
            ActorEmail = user.Email,
            PayloadJson = JsonSerializer.Serialize(new { position, payload })
        });

    private static object Payload(SaveRecruitmentRequisition request, AuthUser user, RequesterRow employee, int clientId, string number, long id) => new
    {
        Id = id,
        RfrNumber = number,
        RequestedByEmployeeId = user.EmployeeId ?? 0,
        RequestedByUserId = user.Id,
        ClientId = clientId,
        request.BranchId,
        request.BusinessUnit,
        Department = string.IsNullOrWhiteSpace(request.Department) ? employee.Department : request.Department,
        request.CostCenter,
        request.PositionTitle,
        request.PositionCategory,
        request.EmploymentType,
        request.HiringType,
        request.NumberOfOpenings,
        request.IsReplacement,
        request.ReplacementEmployeeId,
        request.TargetJoiningDate,
        request.JobLocation,
        request.WorkMode,
        ClientProjectId = request.ClientId,
        request.Project,
        request.BudgetAvailable,
        request.BudgetAmount,
        request.HiringPriority,
        request.BusinessJustification,
        request.ReasonForHiring,
        request.ExperienceRange,
        request.Qualification,
        request.RequiredSkills,
        request.PreferredSkills,
        request.Certifications,
        request.Languages,
        request.SalaryMin,
        request.SalaryMax,
        request.Currency,
        request.Benefits
    };

    private const string InsertSql = @"INSERT INTO recruitment_requisitions (RfrNumber,RequestedByEmployeeId,RequestedByUserId,ClientId,BranchId,BusinessUnit,Department,CostCenter,PositionTitle,PositionCategory,EmploymentType,HiringType,NumberOfOpenings,IsReplacement,ReplacementEmployeeId,TargetJoiningDate,JobLocation,WorkMode,ClientProjectId,Project,BudgetAvailable,BudgetAmount,HiringPriority,BusinessJustification,ReasonForHiring,ExperienceRange,Qualification,RequiredSkills,PreferredSkills,Certifications,Languages,SalaryMin,SalaryMax,Currency,Benefits)
VALUES (@RfrNumber,@RequestedByEmployeeId,@RequestedByUserId,@ClientId,@BranchId,@BusinessUnit,@Department,@CostCenter,@PositionTitle,@PositionCategory,@EmploymentType,@HiringType,@NumberOfOpenings,@IsReplacement,@ReplacementEmployeeId,@TargetJoiningDate,@JobLocation,@WorkMode,@ClientProjectId,@Project,@BudgetAvailable,@BudgetAmount,@HiringPriority,@BusinessJustification,@ReasonForHiring,@ExperienceRange,@Qualification,@RequiredSkills,@PreferredSkills,@Certifications,@Languages,@SalaryMin,@SalaryMax,@Currency,@Benefits);";
    private const string UpdateSql = @"UPDATE recruitment_requisitions SET BranchId=@BranchId,BusinessUnit=@BusinessUnit,Department=@Department,CostCenter=@CostCenter,PositionTitle=@PositionTitle,PositionCategory=@PositionCategory,EmploymentType=@EmploymentType,HiringType=@HiringType,NumberOfOpenings=@NumberOfOpenings,IsReplacement=@IsReplacement,ReplacementEmployeeId=@ReplacementEmployeeId,TargetJoiningDate=@TargetJoiningDate,JobLocation=@JobLocation,WorkMode=@WorkMode,ClientProjectId=@ClientProjectId,Project=@Project,BudgetAvailable=@BudgetAvailable,BudgetAmount=@BudgetAmount,HiringPriority=@HiringPriority,BusinessJustification=@BusinessJustification,ReasonForHiring=@ReasonForHiring,ExperienceRange=@ExperienceRange,Qualification=@Qualification,RequiredSkills=@RequiredSkills,PreferredSkills=@PreferredSkills,Certifications=@Certifications,Languages=@Languages,SalaryMin=@SalaryMin,SalaryMax=@SalaryMax,Currency=@Currency,Benefits=@Benefits,UpdatedAt=UTC_TIMESTAMP() WHERE Id=@Id";
    private static string ListSql(string where) => $@"SELECT r.*,c.Name ClientName,COALESCE(w.Name,'') BranchName,CONCAT(requester.FirstName,' ',COALESCE(requester.LastName,'')) RequestedByName,CONCAT(repl.FirstName,' ',COALESCE(repl.LastName,'')) ReplacementEmployeeName
FROM recruitment_requisitions r
LEFT JOIN clients c ON c.Id=r.ClientId
LEFT JOIN worklocations w ON w.Id=r.BranchId
LEFT JOIN employees requester ON requester.Id=r.RequestedByEmployeeId
LEFT JOIN employees repl ON repl.Id=r.ReplacementEmployeeId
{where}";

    private static string OpenPositionSql(string where) => $@"SELECT p.*,r.RfrNumber,c.Name ClientName,COALESCE(w.Name,'') BranchName,COALESCE(u.DisplayName,u.Email,'') RecruiterName
FROM recruitment_open_positions p
JOIN recruitment_requisitions r ON r.Id=p.RequisitionId
LEFT JOIN clients c ON c.Id=p.ClientId
LEFT JOIN worklocations w ON w.Id=p.BranchId
LEFT JOIN authusers u ON u.Id=p.RecruiterUserId
{where}";

    private static string ReferralSql(string where) => $@"SELECT r.*,p.PositionCode,p.PositionTitle,CONCAT(e.FirstName,' ',COALESCE(e.LastName,'')) ReferrerName
FROM recruitment_employee_referrals r
JOIN recruitment_open_positions p ON p.Id=r.PositionId
LEFT JOIN employees e ON e.Id=r.ReferrerEmployeeId
{where}";

    private static Task AuditAsync(MySqlConnection db, long id, string action, int userId, object payload) => db.ExecuteAsync("INSERT INTO recruitment_audit (EntityType,EntityId,Action,NewValueJson,ChangedByUserId) VALUES ('RecruitmentRequisition',@Id,@Action,@Json,@UserId)", new { Id = id, Action = action, Json = JsonSerializer.Serialize(payload), UserId = userId });

    private static Task EnsureCatalogAsync(MySqlConnection db) => db.ExecuteAsync(@"INSERT INTO workflowactivities (ActivityCode,DisplayName,ModuleCode,ResourceType,Description,IsActive) VALUES
('RFR.SUBMIT','Submit recruitment requisition','Talent Acquisition','RecruitmentRequisition','Recruitment requisition approval before open position creation.',TRUE)
ON DUPLICATE KEY UPDATE DisplayName=VALUES(DisplayName),ModuleCode=VALUES(ModuleCode),ResourceType=VALUES(ResourceType),Description=VALUES(Description),IsActive=VALUES(IsActive);
INSERT INTO workflow_action_rules (ActivityCode,HttpMethod,PathPattern,ResourceType,ResourceIdSource,ResourceIdRouteKey,ClientIdSource,ClientIdSql,ClientLookupTable,ClientLookupKeyColumn,ClientLookupClientColumn,TriggerMode,IsActive) VALUES
('RFR.SUBMIT','POST','/api/ess/recruitment/requisitions/{id}/submit','RecruitmentRequisition','route.id','id','','','recruitment_requisitions','Id','ClientId','AfterSuccess',TRUE)
ON DUPLICATE KEY UPDATE ResourceType=VALUES(ResourceType),ResourceIdSource=VALUES(ResourceIdSource),ResourceIdRouteKey=VALUES(ResourceIdRouteKey),ClientLookupTable=VALUES(ClientLookupTable),ClientLookupKeyColumn=VALUES(ClientLookupKeyColumn),ClientLookupClientColumn=VALUES(ClientLookupClientColumn),TriggerMode=VALUES(TriggerMode),IsActive=VALUES(IsActive);");

    private static async Task EnsureTablesAsync(MySqlConnection db)
    {
        await db.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS recruitment_requisitions (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,RfrNumber VARCHAR(80) NOT NULL,RequestDate DATE NOT NULL DEFAULT (CURRENT_DATE),RequestedByEmployeeId INT NOT NULL,RequestedByUserId INT NOT NULL,ClientId INT NOT NULL,BranchId INT NOT NULL DEFAULT 0,BusinessUnit VARCHAR(120) NOT NULL DEFAULT '',Department VARCHAR(120) NOT NULL DEFAULT '',CostCenter VARCHAR(120) NOT NULL DEFAULT '',PositionTitle VARCHAR(180) NOT NULL,PositionCategory VARCHAR(120) NOT NULL DEFAULT '',EmploymentType VARCHAR(80) NOT NULL DEFAULT '',HiringType VARCHAR(80) NOT NULL DEFAULT '',NumberOfOpenings INT NOT NULL DEFAULT 1,IsReplacement BOOLEAN NOT NULL DEFAULT FALSE,ReplacementEmployeeId INT NULL,TargetJoiningDate DATE NULL,JobLocation VARCHAR(160) NOT NULL DEFAULT '',WorkMode VARCHAR(40) NOT NULL DEFAULT 'Office',ClientProjectId INT NULL,Project VARCHAR(160) NOT NULL DEFAULT '',BudgetAvailable BOOLEAN NOT NULL DEFAULT FALSE,BudgetAmount DECIMAL(18,2) NOT NULL DEFAULT 0,HiringPriority VARCHAR(40) NOT NULL DEFAULT 'Normal',BusinessJustification TEXT NULL,ReasonForHiring VARCHAR(500) NOT NULL DEFAULT '',ExperienceRange VARCHAR(80) NOT NULL DEFAULT '',Qualification VARCHAR(250) NOT NULL DEFAULT '',RequiredSkills TEXT NULL,PreferredSkills TEXT NULL,Certifications VARCHAR(500) NOT NULL DEFAULT '',Languages VARCHAR(250) NOT NULL DEFAULT '',SalaryMin DECIMAL(18,2) NOT NULL DEFAULT 0,SalaryMax DECIMAL(18,2) NOT NULL DEFAULT 0,Currency VARCHAR(10) NOT NULL DEFAULT 'INR',Benefits TEXT NULL,Status VARCHAR(40) NOT NULL DEFAULT 'Draft',WorkflowInstanceId BIGINT NULL,OpenPositionId BIGINT NULL,SubmittedAt DATETIME NULL,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,UNIQUE KEY UX_recruitment_rfr_number (RfrNumber),INDEX IX_recruitment_rfr_client_status (ClientId,Status),INDEX IX_recruitment_rfr_requester (RequestedByEmployeeId,Status));
CREATE TABLE IF NOT EXISTS recruitment_open_positions (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,RequisitionId BIGINT NOT NULL,PositionCode VARCHAR(80) NOT NULL,ClientId INT NOT NULL,BranchId INT NOT NULL DEFAULT 0,BusinessUnit VARCHAR(120) NOT NULL DEFAULT '',Department VARCHAR(120) NOT NULL DEFAULT '',CostCenter VARCHAR(120) NOT NULL DEFAULT '',PositionTitle VARCHAR(180) NOT NULL,PositionCategory VARCHAR(120) NOT NULL DEFAULT '',EmploymentType VARCHAR(80) NOT NULL DEFAULT '',HiringType VARCHAR(80) NOT NULL DEFAULT '',NumberOfPositions INT NOT NULL DEFAULT 1,ApprovedPositions INT NOT NULL DEFAULT 1,FilledPositions INT NOT NULL DEFAULT 0,CancelledPositions INT NOT NULL DEFAULT 0,OnHoldPositions INT NOT NULL DEFAULT 0,RemainingPositions INT NOT NULL DEFAULT 1,TargetJoiningDate DATE NULL,JobLocation VARCHAR(160) NOT NULL DEFAULT '',Project VARCHAR(160) NOT NULL DEFAULT '',BudgetAvailable BOOLEAN NOT NULL DEFAULT FALSE,BudgetAmount DECIMAL(18,2) NOT NULL DEFAULT 0,SalaryMin DECIMAL(18,2) NOT NULL DEFAULT 0,SalaryMax DECIMAL(18,2) NOT NULL DEFAULT 0,Currency VARCHAR(10) NOT NULL DEFAULT 'INR',HiringPriority VARCHAR(40) NOT NULL DEFAULT '',RequiredSkills TEXT NULL,PreferredSkills TEXT NULL,ExperienceRange VARCHAR(80) NOT NULL DEFAULT '',Status VARCHAR(40) NOT NULL DEFAULT 'Open',RecruiterUserId INT NOT NULL DEFAULT 0,CandidateCount INT NOT NULL DEFAULT 0,InterviewCount INT NOT NULL DEFAULT 0,OfferCount INT NOT NULL DEFAULT 0,JoinedCount INT NOT NULL DEFAULT 0,SnapshotJson JSON NULL,PublishedAt DATETIME NULL,ClosedAt DATETIME NULL,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,UNIQUE KEY UX_recruitment_open_position_rfr (RequisitionId),UNIQUE KEY UX_recruitment_position_code (PositionCode),INDEX IX_recruitment_position_client_status (ClientId,Status));
CREATE TABLE IF NOT EXISTS recruitment_number_sequences (
Id INT PRIMARY KEY AUTO_INCREMENT,ClientId INT NOT NULL DEFAULT 0,SeriesCode VARCHAR(30) NOT NULL,LastNumber INT NOT NULL DEFAULT 0,UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,UNIQUE KEY UX_recruitment_number_sequence (ClientId,SeriesCode));
CREATE TABLE IF NOT EXISTS recruitment_position_timeline (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,PositionId BIGINT NOT NULL,EventType VARCHAR(80) NOT NULL,EventTitle VARCHAR(180) NOT NULL,EventDetails VARCHAR(1000) NOT NULL DEFAULT '',ActorUserId INT NULL,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,INDEX IX_recruitment_position_timeline (PositionId,CreatedAt));
CREATE TABLE IF NOT EXISTS recruitment_position_notes (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,PositionId BIGINT NOT NULL,NoteType VARCHAR(80) NOT NULL DEFAULT 'General',NoteText TEXT NOT NULL,CreatedByUserId INT NOT NULL,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,INDEX IX_recruitment_position_notes (PositionId,CreatedAt));
CREATE TABLE IF NOT EXISTS recruitment_position_checklist (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,PositionId BIGINT NOT NULL,ChecklistName VARCHAR(180) NOT NULL,Stage VARCHAR(120) NOT NULL DEFAULT '',Mandatory BOOLEAN NOT NULL DEFAULT TRUE,IsCompleted BOOLEAN NOT NULL DEFAULT FALSE,CompletedByUserId INT NULL,CompletedAt DATETIME NULL,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UNIQUE KEY UX_recruitment_position_checklist (PositionId,ChecklistName));
CREATE TABLE IF NOT EXISTS recruitment_recruiter_assignments (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,PositionId BIGINT NOT NULL,PrimaryRecruiterUserId INT NOT NULL,SecondaryRecruiterUserId INT NOT NULL DEFAULT 0,AssignmentDate DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,AssignmentReason VARCHAR(1000) NOT NULL DEFAULT '',AssignmentStatus VARCHAR(40) NOT NULL DEFAULT 'Active',AssignedByUserId INT NOT NULL,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,INDEX IX_recruitment_recruiter_assignment (PositionId,AssignmentStatus));
CREATE TABLE IF NOT EXISTS recruitment_partner_assignments (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,PositionId BIGINT NOT NULL,PartnerType VARCHAR(40) NOT NULL,PartnerId INT NOT NULL,AssignmentDate DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,Priority VARCHAR(40) NOT NULL DEFAULT 'Normal',DueDate DATE NULL,ExpectedProfiles INT NOT NULL DEFAULT 0,Status VARCHAR(40) NOT NULL DEFAULT 'Assigned',Remarks VARCHAR(1000) NOT NULL DEFAULT '',AssignedByUserId INT NOT NULL,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,INDEX IX_recruitment_partner_assignment (PositionId,PartnerType,Status));
CREATE TABLE IF NOT EXISTS recruitment_job_publications (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,PositionId BIGINT NOT NULL,Channel VARCHAR(120) NOT NULL,PublishingDate DATE NOT NULL,ExpiryDate DATE NULL,Status VARCHAR(40) NOT NULL DEFAULT 'Published',Remarks VARCHAR(1000) NOT NULL DEFAULT '',PublishedByUserId INT NOT NULL,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,INDEX IX_recruitment_publication (PositionId,Channel,Status));
CREATE TABLE IF NOT EXISTS recruitment_referral_campaigns (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,PositionId BIGINT NOT NULL,CampaignName VARCHAR(180) NOT NULL,StartDate DATE NOT NULL,EndDate DATE NOT NULL,ReferralReward DECIMAL(18,2) NOT NULL DEFAULT 0,VisibilityCompany VARCHAR(120) NOT NULL DEFAULT '',VisibilityDepartment VARCHAR(120) NOT NULL DEFAULT '',VisibilityBusinessUnit VARCHAR(120) NOT NULL DEFAULT '',VisibilityLocation VARCHAR(120) NOT NULL DEFAULT '',VisibilityEmploymentType VARCHAR(120) NOT NULL DEFAULT '',Status VARCHAR(40) NOT NULL DEFAULT 'Open',CreatedByUserId INT NOT NULL,CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,INDEX IX_recruitment_referral_campaign (PositionId,Status,EndDate));
CREATE TABLE IF NOT EXISTS recruitment_employee_referrals (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,PositionId BIGINT NOT NULL,ReferrerEmployeeId INT NOT NULL,CandidateName VARCHAR(180) NOT NULL,CandidateEmail VARCHAR(180) NOT NULL DEFAULT '',CandidatePhone VARCHAR(50) NOT NULL DEFAULT '',Relationship VARCHAR(120) NOT NULL DEFAULT '',Remarks VARCHAR(1000) NOT NULL DEFAULT '',Status VARCHAR(40) NOT NULL DEFAULT 'Submitted',CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,INDEX IX_recruitment_employee_referrals (ReferrerEmployeeId,Status),INDEX IX_recruitment_position_referrals (PositionId,Status));
CREATE TABLE IF NOT EXISTS recruitment_requisition_documents (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,RequisitionId BIGINT NOT NULL,DocumentCategory VARCHAR(120) NOT NULL DEFAULT 'Other',FileName VARCHAR(260) NOT NULL DEFAULT '',ContentType VARCHAR(120) NOT NULL DEFAULT '',StoragePath VARCHAR(500) NOT NULL DEFAULT '',UploadedBy INT NULL,UploadedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,INDEX IX_recruitment_doc_rfr (RequisitionId));
CREATE TABLE IF NOT EXISTS recruitment_audit (
Id BIGINT PRIMARY KEY AUTO_INCREMENT,EntityType VARCHAR(80) NOT NULL,EntityId BIGINT NOT NULL,Action VARCHAR(80) NOT NULL,OldValueJson JSON NULL,NewValueJson JSON NULL,ChangedByUserId INT NULL,ChangedOn DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,INDEX IX_recruitment_audit (EntityType,EntityId,ChangedOn));");

        await EnsureColumnAsync(db, "recruitment_open_positions", "businessunit", "VARCHAR(120) NOT NULL DEFAULT ''");
        await EnsureColumnAsync(db, "recruitment_open_positions", "costcenter", "VARCHAR(120) NOT NULL DEFAULT ''");
        await EnsureColumnAsync(db, "recruitment_open_positions", "approvedpositions", "INT NOT NULL DEFAULT 1");
        await EnsureColumnAsync(db, "recruitment_open_positions", "filledpositions", "INT NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "recruitment_open_positions", "cancelledpositions", "INT NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "recruitment_open_positions", "onholdpositions", "INT NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "recruitment_open_positions", "remainingpositions", "INT NOT NULL DEFAULT 1");
        await EnsureColumnAsync(db, "recruitment_open_positions", "budgetavailable", "BOOLEAN NOT NULL DEFAULT FALSE");
        await EnsureColumnAsync(db, "recruitment_open_positions", "budgetamount", "DECIMAL(18,2) NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "recruitment_open_positions", "preferredskills", "TEXT NULL");
        await EnsureColumnAsync(db, "recruitment_open_positions", "recruiteruserid", "INT NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "recruitment_open_positions", "candidatecount", "INT NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "recruitment_open_positions", "interviewcount", "INT NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "recruitment_open_positions", "offercount", "INT NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "recruitment_open_positions", "joinedcount", "INT NOT NULL DEFAULT 0");
        await EnsureColumnAsync(db, "recruitment_open_positions", "snapshotjson", "JSON NULL");
        await EnsureColumnAsync(db, "recruitment_open_positions", "publishedat", "DATETIME NULL");
        await EnsureColumnAsync(db, "recruitment_open_positions", "closedat", "DATETIME NULL");
        await EnsureColumnAsync(db, "recruitment_requisition_documents", "documentcategory", "VARCHAR(120) NOT NULL DEFAULT 'Other'");

        await db.ExecuteAsync(@"UPDATE recruitment_open_positions SET approvedpositions=NumberOfPositions WHERE approvedpositions=1 AND NumberOfPositions<>1;
UPDATE recruitment_open_positions SET remainingpositions=GREATEST(0,approvedpositions-filledpositions-cancelledpositions-onholdpositions) WHERE remainingpositions=1 OR remainingpositions IS NULL;");
    }

    private static async Task EnsureColumnAsync(MySqlConnection db, string tableName, string columnName, string definition)
    {
        var exists = await db.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*)
FROM information_schema.columns
WHERE table_schema = DATABASE()
  AND table_name = @TableName
  AND LOWER(column_name) = @ColumnName;",
            new { TableName = tableName, ColumnName = columnName.ToLowerInvariant() });

        if (exists == 0)
        {
            await db.ExecuteAsync($"ALTER TABLE `{tableName}` ADD COLUMN `{columnName.ToLowerInvariant()}` {definition}");
        }
    }

    private sealed class RequesterRow
    {
        public int Id { get; set; }
        public int ClientId { get; set; }
        public string Department { get; set; } = "";
    }
    private sealed class StatusCount
    {
        public string Status { get; set; } = "";
        public int Count { get; set; }
    }
    private sealed class RecruitmentSettingRow
    {
        public bool RecruitmentEnabled { get; set; }
        public bool AllowEmployeeRfrCreation { get; set; }
        public bool AllowReplacementHiring { get; set; }
        public bool AllowMultipleRecruiters { get; set; }
        public bool EnableVendorHiring { get; set; }
        public bool EnableConsultantHiring { get; set; }
        public bool EnableInternalHiring { get; set; }
        public bool EnableReferralHiring { get; set; }
        public bool EnableDocumentVerification { get; set; }
    }
    private sealed class EmployeeScope
    {
        public int ClientId { get; set; }
        public string Department { get; set; } = "";
        public string BusinessUnit { get; set; } = "";
        public string WorkLocation { get; set; } = "";
        public string EmploymentType { get; set; } = "";
    }
}
