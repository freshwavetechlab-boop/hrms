using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed class NotificationAutomationRepository(IConfiguration configuration, ILogger<NotificationAutomationRepository> logger)
{
    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));

    private static readonly NotificationStakeholderOption[] CommonStakeholders =
    [
        Definition("ACTOR", "Person taking the action", "The signed-in user who performs the action."),
        Definition("REQUESTOR", "Requestor", "The user who originally submitted the process for approval."),
        Definition("CURRENT_APPROVER", "Current approver", "The user currently holding the pending workflow task.")
    ];

    public async Task<NotificationAutomationCatalog> GetCatalogAsync()
    {
        await using var db = Db();
        await db.OpenAsync();
        var activities = (await db.QueryAsync<ActivityRow>(@"SELECT a.ActivityCode,a.DisplayName,a.ModuleCode,a.ResourceType,a.Description,
COALESCE(r.HttpMethod,'') HttpMethod,COALESCE(r.PathPattern,'') PathPattern
FROM workflowactivities a
LEFT JOIN workflow_action_rules r ON r.Id=(SELECT matched.Id FROM workflow_action_rules matched WHERE matched.ActivityCode=a.ActivityCode AND matched.IsActive=TRUE ORDER BY matched.Id LIMIT 1)
WHERE a.IsActive=TRUE
ORDER BY a.ModuleCode,a.DisplayName")).ToList();
        var existingEvents = (await db.QueryAsync<string>("SELECT DISTINCT EventCode FROM notification_rules WHERE EventCode<>'' ORDER BY EventCode")).ToList();
        var events = new Dictionary<string, NotificationAutomationEvent>(StringComparer.OrdinalIgnoreCase);
        foreach (var activity in activities)
        {
            AddEvent(events, activity.ActivityCode, activity.DisplayName, activity, "Action", activity.Description);
            var root = LifecycleRoot(activity.ActivityCode);
            AddEvent(events, $"{root}.APPROVAL_ASSIGNED", $"{activity.DisplayName} - assigned for approval", activity, "Approval assigned", "A workflow task is assigned to the next approver.");
            AddEvent(events, $"{root}.APPROVED", $"{activity.DisplayName} - approved", activity, "Approved", "The workflow reaches its final approved state.");
            AddEvent(events, $"{root}.REJECTED", $"{activity.DisplayName} - rejected", activity, "Rejected", "An approver rejects the workflow request.");
            AddEvent(events, $"{root}.SENT_BACK", $"{activity.DisplayName} - sent back", activity, "Sent back", "An approver sends the workflow request back.");
        }
        foreach (var eventCode in existingEvents.Where(code => !events.ContainsKey(code)))
            events[eventCode] = new NotificationAutomationEvent { EventCode = eventCode, DisplayName = Humanize(eventCode), ModuleCode = "Existing automation", Lifecycle = "Configured event" };
        return new NotificationAutomationCatalog { Events = events.Values.OrderBy(item => item.ModuleCode).ThenBy(item => item.DisplayName).ToList() };
    }

    public async Task<NotificationStakeholderPreview> PreviewAsync(NotificationEvent evt)
    {
        evt.ClientId ??= await ResolveClientIdAsync(evt.ResourceType, evt.ResourceId);
        var result = new NotificationStakeholderPreview
        {
            EventCode = evt.EventCode,
            ResourceType = evt.ResourceType,
            ResourceId = evt.ResourceId,
            ClientId = evt.ClientId
        };
        await using var db = Db();
        await db.OpenAsync();
        foreach (var definition in DefinitionsFor(evt.ResourceType))
        {
            var option = Clone(definition);
            try
            {
                option.People = await ResolvePeopleAsync(db, option.Code, evt);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Stakeholder {StakeholderCode} could not be previewed for {ResourceType} {ResourceId}.", option.Code, evt.ResourceType, evt.ResourceId);
            }
            result.Stakeholders.Add(option);
        }
        return result;
    }

    public async Task<List<string>> ResolveEmailsAsync(string stakeholderCode, NotificationEvent evt)
    {
        await using var db = Db();
        await db.OpenAsync();
        try
        {
            var people = await ResolvePeopleAsync(db, stakeholderCode, evt);
            return people.Select(item => item.Email).Where(IsEmailCandidate).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(item => item).ToList();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Stakeholder {StakeholderCode} could not be resolved for notification {EventCode}.", stakeholderCode, evt.EventCode);
            return [];
        }
    }

    public async Task<int?> ResolveClientIdAsync(string resourceType, string resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceType) || string.IsNullOrWhiteSpace(resourceId)) return null;
        await using var db = Db();
        await db.OpenAsync();
        try
        {
            return resourceType.ToUpperInvariant() switch
            {
                "RECRUITMENTREQUISITION" => await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM recruitment_requisitions WHERE Id=@ResourceId", new { ResourceId = resourceId }),
                "RECRUITMENTOPENPOSITION" => await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM recruitment_open_positions WHERE Id=@ResourceId", new { ResourceId = resourceId }),
                "RECRUITMENTJOBDESCRIPTION" => await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM recruitment_job_description_versions WHERE Id=@ResourceId", new { ResourceId = resourceId }),
                "RECRUITMENTCANDIDATE" => await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM recruitment_candidates WHERE Id=@ResourceId", new { ResourceId = resourceId }),
                "RECRUITMENTAPPLICATION" => await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM recruitment_candidate_applications WHERE Id=@ResourceId", new { ResourceId = resourceId }),
                "RECRUITMENTINTERVIEW" => await db.ExecuteScalarAsync<int?>("SELECT a.ClientId FROM recruitment_interviews i JOIN recruitment_candidate_applications a ON a.Id=i.ApplicationId WHERE i.Id=@ResourceId", new { ResourceId = resourceId }),
                "RECRUITMENTOFFER" => await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM recruitment_offers WHERE Id=@ResourceId", new { ResourceId = resourceId }),
                "PAYRUN" => await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM payruns WHERE Id=@ResourceId", new { ResourceId = resourceId }),
                "LEAVEREQUEST" => await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM essleaverequests WHERE Id=@ResourceId", new { ResourceId = resourceId }),
                "TRAVELREQUEST" => await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM ess_travel_requests WHERE Id=@ResourceId", new { ResourceId = resourceId }),
                "EXPENSECLAIM" => await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM ess_expense_claims WHERE Id=@ResourceId", new { ResourceId = resourceId }),
                "EMPLOYEE" or "EMPLOYEEACTION" => await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM employees WHERE Id=@ResourceId", new { ResourceId = resourceId }),
                _ => await db.ExecuteScalarAsync<int?>(@"SELECT m.ClientId FROM workflowinstances i JOIN workflowmasters m ON m.Id=i.WorkflowId
WHERE i.ResourceType=@ResourceType AND i.ResourceId=@ResourceId ORDER BY i.Id DESC LIMIT 1", new { ResourceType = resourceType, ResourceId = resourceId })
            };
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Client could not be resolved for {ResourceType} {ResourceId}.", resourceType, resourceId);
            return null;
        }
    }

    private async Task<List<NotificationStakeholderPerson>> ResolvePeopleAsync(MySqlConnection db, string stakeholderCode, NotificationEvent evt)
    {
        var code = (stakeholderCode ?? "").Trim().ToUpperInvariant();
        IEnumerable<NotificationStakeholderPerson> rows = code switch
        {
            "ACTOR" => await ActorAsync(db, evt),
            "REQUESTOR" => await RequestorAsync(db, evt),
            "CURRENT_APPROVER" => await WorkflowPeopleAsync(db, evt, currentApprover: true),
            "HIRING_REQUESTER" => await HiringRequesterAsync(db, evt),
            "POSITION_RECRUITER" => await PositionRecruiterAsync(db, evt),
            "CANDIDATE" => await CandidateAsync(db, evt),
            "INTERVIEW_PANEL" => await InterviewPanelAsync(db, evt),
            "ASSIGNED_PARTNER" => await AssignedPartnersAsync(db, evt),
            "AFFECTED_EMPLOYEE" => await AffectedEmployeeAsync(db, evt),
            "REPORTING_MANAGER" => await ReportingManagerAsync(db, evt),
            "DEPARTMENT_HEAD" => await DepartmentHeadAsync(db, evt),
            _ => []
        };
        return rows.Where(item => IsEmailCandidate(item.Email))
            .GroupBy(item => item.Email.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.DisplayName)
            .ToList();
    }

    private static async Task<IEnumerable<NotificationStakeholderPerson>> ActorAsync(MySqlConnection db, NotificationEvent evt)
    {
        if (IsEmailCandidate(evt.ActorEmail)) return [new NotificationStakeholderPerson { UserId = evt.ActorUserId > 0 ? evt.ActorUserId : null, DisplayName = string.IsNullOrWhiteSpace(evt.ActorName) ? evt.ActorEmail : evt.ActorName, Email = evt.ActorEmail }];
        return await db.QueryAsync<NotificationStakeholderPerson>(UserSelect + " WHERE u.Id=@UserId AND u.IsActive=TRUE AND u.Email<>''", new { UserId = evt.ActorUserId });
    }

    private static async Task<IEnumerable<NotificationStakeholderPerson>> RequestorAsync(MySqlConnection db, NotificationEvent evt)
    {
        var rows = (await WorkflowPeopleAsync(db, evt, currentApprover: false)).ToList();
        if (rows.Count > 0) return rows;
        return await HiringRequesterAsync(db, evt);
    }

    private static Task<IEnumerable<NotificationStakeholderPerson>> WorkflowPeopleAsync(MySqlConnection db, NotificationEvent evt, bool currentApprover)
    {
        var userColumn = currentApprover ? "t.ApproverUserId" : "i.RequestorUserId";
        var pendingJoin = currentApprover ? "JOIN workflowtasks t ON t.InstanceId=i.Id AND t.Status='Pending'" : "LEFT JOIN workflowtasks t ON 1=0";
        return db.QueryAsync<NotificationStakeholderPerson>($@"SELECT u.Id UserId,COALESCE(NULLIF(u.DisplayName,''),u.Email) DisplayName,u.Email
FROM workflowinstances i
{pendingJoin}
JOIN authusers u ON u.Id={userColumn} AND u.IsActive=TRUE
WHERE i.ResourceType=@ResourceType AND i.ResourceId=@ResourceId AND u.Email<>''
ORDER BY i.Id DESC,t.Id DESC LIMIT 1", new { evt.ResourceType, evt.ResourceId });
    }

    private static Task<IEnumerable<NotificationStakeholderPerson>> HiringRequesterAsync(MySqlConnection db, NotificationEvent evt)
    {
        var source = RecruitmentSource(evt.ResourceType);
        if (source is null) return Task.FromResult<IEnumerable<NotificationStakeholderPerson>>([]);
        return db.QueryAsync<NotificationStakeholderPerson>($@"SELECT u.Id UserId,COALESCE(NULLIF(u.DisplayName,''),u.Email) DisplayName,u.Email
{source.Value.FromSql}
JOIN recruitment_requisitions r ON r.Id={source.Value.RequisitionIdSql}
JOIN authusers u ON u.Id=r.RequestedByUserId AND u.IsActive=TRUE
WHERE {source.Value.WhereSql} AND u.Email<>'' LIMIT 1", new { evt.ResourceId });
    }

    private static Task<IEnumerable<NotificationStakeholderPerson>> PositionRecruiterAsync(MySqlConnection db, NotificationEvent evt)
    {
        var source = RecruitmentSource(evt.ResourceType);
        if (source is null) return Task.FromResult<IEnumerable<NotificationStakeholderPerson>>([]);
        return db.QueryAsync<NotificationStakeholderPerson>($@"SELECT u.Id UserId,COALESCE(NULLIF(u.DisplayName,''),u.Email) DisplayName,u.Email
{source.Value.FromSql}
JOIN recruitment_requisitions r ON r.Id={source.Value.RequisitionIdSql}
JOIN recruitment_open_positions p ON p.RequisitionId=r.Id
JOIN authusers u ON u.Id=p.RecruiterUserId AND u.IsActive=TRUE
WHERE {source.Value.WhereSql} AND u.Email<>'' LIMIT 1", new { evt.ResourceId });
    }

    private static Task<IEnumerable<NotificationStakeholderPerson>> CandidateAsync(MySqlConnection db, NotificationEvent evt)
    {
        var (from, candidateId, where) = evt.ResourceType.ToUpperInvariant() switch
        {
            "RECRUITMENTCANDIDATE" => ("FROM recruitment_candidates c", "c.Id", "c.Id=@ResourceId"),
            "RECRUITMENTAPPLICATION" => ("FROM recruitment_candidate_applications a JOIN recruitment_candidates c ON c.Id=a.CandidateId", "c.Id", "a.Id=@ResourceId"),
            "RECRUITMENTINTERVIEW" => ("FROM recruitment_interviews i JOIN recruitment_candidate_applications a ON a.Id=i.ApplicationId JOIN recruitment_candidates c ON c.Id=a.CandidateId", "c.Id", "i.Id=@ResourceId"),
            "RECRUITMENTOFFER" => ("FROM recruitment_offers o JOIN recruitment_candidate_applications a ON a.Id=o.ApplicationId JOIN recruitment_candidates c ON c.Id=a.CandidateId", "c.Id", "o.Id=@ResourceId"),
            _ => ("", "", "")
        };
        if (from.Length == 0) return Task.FromResult<IEnumerable<NotificationStakeholderPerson>>([]);
        return db.QueryAsync<NotificationStakeholderPerson>($"SELECT NULL UserId,TRIM(CONCAT(c.FirstName,' ',c.LastName)) DisplayName,c.Email {from} WHERE {where} AND c.Email<>''", new { evt.ResourceId });
    }

    private static Task<IEnumerable<NotificationStakeholderPerson>> InterviewPanelAsync(MySqlConnection db, NotificationEvent evt)
    {
        var where = evt.ResourceType.Equals("RecruitmentInterview", StringComparison.OrdinalIgnoreCase) ? "i.Id=@ResourceId" : evt.ResourceType.Equals("RecruitmentApplication", StringComparison.OrdinalIgnoreCase) ? "i.ApplicationId=@ResourceId" : "1=0";
        return db.QueryAsync<NotificationStakeholderPerson>($@"SELECT u.Id UserId,COALESCE(NULLIF(u.DisplayName,''),u.Email) DisplayName,u.Email
FROM recruitment_interviews i JOIN recruitment_interview_panel_members panel ON panel.InterviewId=i.Id
JOIN authusers u ON u.Id=panel.PanelUserId AND u.IsActive=TRUE
WHERE {where} AND u.Email<>''", new { evt.ResourceId });
    }

    private static Task<IEnumerable<NotificationStakeholderPerson>> AssignedPartnersAsync(MySqlConnection db, NotificationEvent evt)
    {
        var source = RecruitmentSource(evt.ResourceType);
        if (source is null) return Task.FromResult<IEnumerable<NotificationStakeholderPerson>>([]);
        return db.QueryAsync<NotificationStakeholderPerson>($@"SELECT NULL UserId,COALESCE(NULLIF(partner.ContactPerson,''),partner.Name) DisplayName,partner.Email
{source.Value.FromSql}
JOIN recruitment_requisitions r ON r.Id={source.Value.RequisitionIdSql}
JOIN recruitment_open_positions p ON p.RequisitionId=r.Id
JOIN recruitment_partner_assignments assignment ON assignment.PositionId=p.Id AND assignment.Status NOT IN ('Cancelled','Inactive')
JOIN recruitment_partners partner ON partner.Id=assignment.PartnerId AND partner.IsActive=TRUE
WHERE {source.Value.WhereSql} AND partner.Email<>''", new { evt.ResourceId });
    }

    private static async Task<IEnumerable<NotificationStakeholderPerson>> AffectedEmployeeAsync(MySqlConnection db, NotificationEvent evt)
    {
        var employeeId = await ResolveEmployeeIdAsync(db, evt);
        if (employeeId is null) return [];
        return await db.QueryAsync<NotificationStakeholderPerson>(@"SELECT u.Id UserId,COALESCE(NULLIF(u.DisplayName,''),u.Email) DisplayName,u.Email
FROM authusers u WHERE u.EmployeeId=@EmployeeId AND u.IsActive=TRUE AND u.Email<>''", new { EmployeeId = employeeId });
    }

    private static async Task<IEnumerable<NotificationStakeholderPerson>> ReportingManagerAsync(MySqlConnection db, NotificationEvent evt)
    {
        var employeeId = await ResolveEmployeeIdAsync(db, evt);
        if (employeeId is null)
        {
            var requestor = (await RequestorAsync(db, evt)).FirstOrDefault();
            if (requestor?.UserId is not null)
                employeeId = await db.ExecuteScalarAsync<int?>("SELECT EmployeeId FROM authusers WHERE Id=@Id", new { Id = requestor.UserId });
        }
        if (employeeId is null) return [];
        return await db.QueryAsync<NotificationStakeholderPerson>(@"SELECT managerUser.Id UserId,COALESCE(NULLIF(managerUser.DisplayName,''),managerUser.Email) DisplayName,managerUser.Email
FROM employees employee
JOIN authusers managerUser ON managerUser.IsActive=TRUE
LEFT JOIN employees manager ON manager.Id=employee.ReportingManagerId
WHERE employee.Id=@EmployeeId
  AND (managerUser.Id=employee.ReportingManagerUserId OR (COALESCE(employee.ReportingManagerUserId,0)=0 AND managerUser.EmployeeId=manager.Id))
  AND managerUser.Email<>'' ORDER BY managerUser.Id LIMIT 1", new { EmployeeId = employeeId });
    }

    private static async Task<IEnumerable<NotificationStakeholderPerson>> DepartmentHeadAsync(MySqlConnection db, NotificationEvent evt)
    {
        var context = await ResolveDepartmentContextAsync(db, evt);
        if (context is null || context.ClientId <= 0 || string.IsNullOrWhiteSpace(context.Department)) return [];
        return await db.QueryAsync<NotificationStakeholderPerson>(@"SELECT u.Id UserId,COALESCE(NULLIF(u.DisplayName,''),u.Email) DisplayName,u.Email
FROM departmentheadassignments head JOIN authusers u ON u.Id=head.UserId AND u.IsActive=TRUE
WHERE head.ClientId=@ClientId AND head.Department=@Department AND u.Email<>''", context);
    }

    private static async Task<int?> ResolveEmployeeIdAsync(MySqlConnection db, NotificationEvent evt) => evt.ResourceType.ToUpperInvariant() switch
    {
        "LEAVEREQUEST" => await db.ExecuteScalarAsync<int?>("SELECT EmployeeId FROM essleaverequests WHERE Id=@ResourceId", new { evt.ResourceId }),
        "TRAVELREQUEST" => await db.ExecuteScalarAsync<int?>("SELECT EmployeeId FROM ess_travel_requests WHERE Id=@ResourceId", new { evt.ResourceId }),
        "EXPENSECLAIM" => await db.ExecuteScalarAsync<int?>("SELECT EmployeeId FROM ess_expense_claims WHERE Id=@ResourceId", new { evt.ResourceId }),
        "EMPLOYEE" or "EMPLOYEEACTION" => int.TryParse(evt.ResourceId, out var employeeId) ? employeeId : null,
        "RECRUITMENTREQUISITION" => await db.ExecuteScalarAsync<int?>("SELECT RequestedByEmployeeId FROM recruitment_requisitions WHERE Id=@ResourceId", new { evt.ResourceId }),
        _ => null
    };

    private static async Task<DepartmentContext?> ResolveDepartmentContextAsync(MySqlConnection db, NotificationEvent evt)
    {
        var source = RecruitmentSource(evt.ResourceType);
        if (source is not null)
            return await db.QueryFirstOrDefaultAsync<DepartmentContext>($@"SELECT r.ClientId,r.Department {source.Value.FromSql}
JOIN recruitment_requisitions r ON r.Id={source.Value.RequisitionIdSql} WHERE {source.Value.WhereSql} LIMIT 1", new { evt.ResourceId });
        var employeeId = await ResolveEmployeeIdAsync(db, evt);
        return employeeId is null ? null : await db.QueryFirstOrDefaultAsync<DepartmentContext>("SELECT ClientId,Department FROM employees WHERE Id=@EmployeeId", new { EmployeeId = employeeId });
    }

    private static (string FromSql, string RequisitionIdSql, string WhereSql)? RecruitmentSource(string resourceType) => resourceType.ToUpperInvariant() switch
    {
        "RECRUITMENTREQUISITION" => ("FROM recruitment_requisitions source", "source.Id", "source.Id=@ResourceId"),
        "RECRUITMENTOPENPOSITION" => ("FROM recruitment_open_positions source", "source.RequisitionId", "source.Id=@ResourceId"),
        "RECRUITMENTJOBDESCRIPTION" => ("FROM recruitment_job_description_versions source", "source.RequisitionId", "source.Id=@ResourceId"),
        "RECRUITMENTAPPLICATION" => ("FROM recruitment_candidate_applications application JOIN recruitment_open_positions source ON source.Id=application.PositionId", "source.RequisitionId", "application.Id=@ResourceId"),
        "RECRUITMENTINTERVIEW" => ("FROM recruitment_interviews interviewRow JOIN recruitment_candidate_applications application ON application.Id=interviewRow.ApplicationId JOIN recruitment_open_positions source ON source.Id=application.PositionId", "source.RequisitionId", "interviewRow.Id=@ResourceId"),
        "RECRUITMENTOFFER" => ("FROM recruitment_offers offerRow JOIN recruitment_candidate_applications application ON application.Id=offerRow.ApplicationId JOIN recruitment_open_positions source ON source.Id=application.PositionId", "source.RequisitionId", "offerRow.Id=@ResourceId"),
        _ => null
    };

    private static IEnumerable<NotificationStakeholderOption> DefinitionsFor(string resourceType)
    {
        foreach (var option in CommonStakeholders) yield return option;
        var normalized = resourceType.ToUpperInvariant();
        if (normalized.StartsWith("RECRUITMENT", StringComparison.Ordinal))
        {
            yield return Definition("HIRING_REQUESTER", "Hiring requestor", "The employee/user who raised the hiring request.");
            yield return Definition("POSITION_RECRUITER", "Assigned recruiter", "The recruiter assigned to the linked open position.");
            if (normalized is "RECRUITMENTCANDIDATE" or "RECRUITMENTAPPLICATION" or "RECRUITMENTINTERVIEW" or "RECRUITMENTOFFER")
                yield return Definition("CANDIDATE", "Candidate", "The candidate linked to this recruitment process.");
            if (normalized is "RECRUITMENTINTERVIEW" or "RECRUITMENTAPPLICATION")
                yield return Definition("INTERVIEW_PANEL", "Interview panel", "Active panel members for the linked interview.");
            yield return Definition("ASSIGNED_PARTNER", "Assigned partners", "Active consultants/vendors assigned to the linked position.");
            yield return Definition("DEPARTMENT_HEAD", "Department head", "The department head for the hiring request's client and department.");
        }
        if (normalized is "LEAVEREQUEST" or "TRAVELREQUEST" or "EXPENSECLAIM" or "ATTENDANCEREGULARIZATION" or "EMPLOYEE" or "EMPLOYEEACTION")
        {
            yield return Definition("AFFECTED_EMPLOYEE", "Affected employee", "The employee whose request or record is being processed.");
            yield return Definition("REPORTING_MANAGER", "Reporting manager", "The affected employee's current reporting manager.");
            yield return Definition("DEPARTMENT_HEAD", "Department head", "The configured head of the affected employee's department.");
        }
    }

    private static NotificationStakeholderOption Definition(string code, string label, string description) => new() { Code = code, Label = label, Description = description };
    private static NotificationStakeholderOption Clone(NotificationStakeholderOption source) => new() { Code = source.Code, Label = source.Label, Description = source.Description, IsRelevant = source.IsRelevant };
    private static string LifecycleRoot(string eventCode) => eventCode.EndsWith(".SUBMIT", StringComparison.OrdinalIgnoreCase) ? eventCode[..^7] : eventCode;
    private static string Humanize(string value) => string.Join(" ", value.Split(['.', '_'], StringSplitOptions.RemoveEmptyEntries).Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    private static bool IsEmailCandidate(string? email) => !string.IsNullOrWhiteSpace(email) && email.Contains('@');

    private static void AddEvent(Dictionary<string, NotificationAutomationEvent> target, string code, string name, ActivityRow activity, string lifecycle, string description)
    {
        if (target.ContainsKey(code)) return;
        target[code] = new NotificationAutomationEvent { EventCode = code, DisplayName = name, ModuleCode = activity.ModuleCode, ResourceType = activity.ResourceType, Description = description, HttpMethod = activity.HttpMethod, PathPattern = activity.PathPattern, Lifecycle = lifecycle };
    }

    private const string UserSelect = "SELECT u.Id UserId,COALESCE(NULLIF(u.DisplayName,''),u.Email) DisplayName,u.Email FROM authusers u";
    private sealed class ActivityRow { public string ActivityCode { get; set; } = ""; public string DisplayName { get; set; } = ""; public string ModuleCode { get; set; } = ""; public string ResourceType { get; set; } = ""; public string Description { get; set; } = ""; public string HttpMethod { get; set; } = ""; public string PathPattern { get; set; } = ""; }
    private sealed class DepartmentContext { public int ClientId { get; set; } public string Department { get; set; } = ""; }
}
