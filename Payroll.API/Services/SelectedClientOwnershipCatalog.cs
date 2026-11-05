using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public sealed record ScopeOwnershipReference(string Resource, string Value, bool AllowGlobal = false);

public interface ISelectedClientOwnershipLookup
{
    Task<int?> ClientIdAsync(ScopeOwnershipReference reference, CancellationToken cancellationToken = default);
}

// The catalog is code, never configuration supplied by an API caller. Unknown
// resources have no ownership authority. The middleware remains deny-by-default
// for routes outside the explicitly supported commercial workspaces.
public static class SelectedClientOwnershipCatalog
{
    private sealed record Route(string Prefix, string Resource, string Key = "Id");
    private static readonly Route[] Routes = [
        new("/api/employees", "employee"), new("/api/pay-runs", "payrun"),
        new("/api/payroll-adjustments", "adjustment"), new("/api/work-locations", "location"),
        new("/api/employee-form-bindings", "employee-form-binding"),
        new("/api/employees/field-configuration/forms", "form"),
        new("/api/employees/field-configuration/form-versions", "form-version"),
        new("/api/clients", "client"),
        new("/api/leave-attendance/shifts", "shift"), new("/api/leave-attendance/shift-assignments", "shift-assignment"),
        new("/api/leave-attendance/groups", "attendance-group"), new("/api/leave-attendance/geo-fences", "geo-fence"),
        new("/api/leave-attendance/leave-types", "leave-type"), new("/api/leave-attendance/holidays", "holiday"),
        new("/api/leave-attendance/attendance-settings", "attendance-settings"),
        new("/api/leave-attendance/attendance/daily/batch-jobs", "attendance-job"),
        new("/api/employees/import-jobs", "employee-import-job"),
        new("/api/leave-attendance/leave-types/import-jobs", "leave-type-import-job"),
        new("/api/leave-attendance/holidays/import-jobs", "holiday-import-job"),
        new("/api/tax-engine/employee-profiles", "employee"),
        new("/api/tax-engine/client-settings", "tax-client-setting"),
        new("/api/client-billing/configurations", "billing-configuration"),
        new("/api/client-billing/advanced/headers", "billing-header"), new("/api/client-billing/advanced/lines", "billing-line"),
        new("/api/travel-expense/policies", "travel-policy"), new("/api/travel-expense/policy", "travel-policy"),
        new("/api/travel-expense/assignments", "travel-assignment"), new("/api/travel-expense/assignment", "travel-assignment"),
        new("/api/travel-expense/rules", "travel-rule"), new("/api/travel-expense/rule", "travel-rule"),
        new("/api/travel-expense/client-settings", "travel-client-setting"), new("/api/travel-expense/client-setting", "travel-client-setting"),
        new("/api/travel-advances", "travel-advance"),
        new("/api/ess/travel/requests", "travel-request"), new("/api/ess/expenses/claims", "expense-claim"),
        new("/api/ess/leave/requests", "leave-request"), new("/api/ess/pay/payslips", "payrun"),
        new("/api/ess/mss/attendance/daily/batch-jobs", "attendance-job"),
        new("/api/ess/recruitment/requisitions", "requisition"), new("/api/ess/recruitment/referrals", "referral"),
        new("/api/ess/admin/travel/requests", "travel-request"), new("/api/ess/admin/expenses/claims", "expense-claim"),
        new("/api/employee-communications/campaigns", "communication-campaign"),
        new("/api/employee-communications/conversations", "communication-conversation"),
        new("/api/attachment-attributes", "attachment-attribute"), new("/api/attachment-configurations", "attachment-configuration"),
        new("/api/attachments", "attachment"),
        new("/api/recruitment/requisitions", "requisition"), new("/api/recruitment/open-positions", "position"),
        new("/api/recruitment/clients", "client"), new("/api/recruitment-admin/ai-scoring", "client"),
        new("/api/recruitment/candidates", "candidate"), new("/api/recruitment/applications", "application"),
        new("/api/recruitment/application-scores", "application-score"),
        new("/api/recruitment/interviews/rounds", "application"),
        new("/api/recruitment/interviews/scheduling-context", "application"),
        new("/api/recruitment/interviews", "interview"), new("/api/recruitment/internal-interviews", "interview"),
        new("/api/recruitment/offers", "offer"), new("/api/recruitment/work-orders", "work-order"),
        new("/api/recruitment/hiring-cases", "hiring-case"),
        new("/api/recruitment/process-documents", "process-document"),
        new("/api/recruitment/profile-submission-batches", "profile-batch"),
        new("/api/recruitment/document-roster/rows", "document-roster"),
        new("/api/recruitment-admin/settings", "recruitment-setting"), new("/api/recruitment-admin/masters", "recruitment-master"),
        new("/api/recruitment-admin/partners", "recruitment-partner"),
        new("/api/recruitment-admin/assignment-rules", "recruitment-assignment"),
        new("/api/recruitment-admin/sla-rules", "recruitment-sla"),
        new("/api/recruitment-admin/document-checklist", "recruitment-checklist"),
        new("/api/recruitment-admin/approval-mappings", "recruitment-approval"),
        new("/api/recruitment-admin/templates", "recruitment-template"),
        new("/api/recruitment-admin/skills", "recruitment-skill"),
        new("/api/recruitment-admin/ats-profiles", "scoring-profile"),
        new("/api/recruitment/forms", "form"), new("/api/recruitment/form-versions", "form-version"),
        new("/api/recruitment/job-descriptions", "job-description"), new("/api/recruitment/job-postings", "job-posting"),
        new("/api/recruitment/pipelines", "pipeline"), new("/api/recruitment/pipeline-versions", "pipeline-version"),
        new("/api/recruitment/pipeline-stages", "pipeline-stage"),
        new("/api/recruitment-orchestration/forms", "form"), new("/api/recruitment-orchestration/form-versions", "form-version"),
        new("/api/recruitment-orchestration/job-descriptions", "job-description"), new("/api/recruitment-orchestration/job-postings", "job-posting"),
        new("/api/recruitment-orchestration/pipelines", "pipeline"), new("/api/recruitment-orchestration/pipeline-versions", "pipeline-version"),
        new("/api/recruitment-orchestration/pipeline-stages", "pipeline-stage"),
        new("/api/recruitment-orchestration/interview-competencies", "interview-competency"),
        new("/api/recruitment-orchestration/pipeline-assignments", "position"),
        new("/api/recruitment-orchestration/applications", "application"), new("/api/recruitment-orchestration/candidate-actions", "candidate-action"),
        new("/api/recruitment/internal-interviews/questions", "interview-question"),
        new("/api/workflows/tasks", "workflow-task"), new("/api/workflows", "workflow-instance"),
        new("/api/notifications/recruitment-triggers", "recruitment-notification-rule"),
        new("/api/payroll-advisor/reviews", "payroll-review")
    ];

    private static string Path(string path) => path.Split('?', 2)[0].TrimEnd('/').ToLowerInvariant();
    private static bool Under(string path, string prefix) => path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal);
    private static bool Identity(string value) => long.TryParse(value, out var number) && number > 0 || Guid.TryParse(value, out _);

    public static IReadOnlyList<ScopeOwnershipReference> RouteReferences(string path)
    {
        path = Path(path); var result = new List<ScopeOwnershipReference>();
        foreach (var route in Routes)
        {
            if (!path.StartsWith(route.Prefix + "/", StringComparison.Ordinal)) continue;
            var id = path[(route.Prefix.Length + 1)..].Split('/')[0];
            if (Identity(id)) result.Add(new(route.Resource, id));
        }
        // A nested child must be checked too: the caller cannot combine a permitted
        // parent with an employee/checklist/score-job belonging to another client.
        if (Under(path, "/api/pay-runs")) { AddNested("employees", "employee"); AddNested("payslips", "employee"); }
        if (Under(path, "/api/recruitment/applications")) { AddNested("checklist", "candidate-checklist"); AddNested("score-jobs", "scoring-job"); }
        if (Under(path, "/api/recruitment-orchestration/applications")) AddNested("transitions", "pipeline-transition");
        if (Under(path, "/api/employees")) AddNested("dynamic-fields", "form-field");
        if (Under(path, "/api/clients")) AddNested("pf-policy-versions", "pf-policy-version");
        return result.Distinct().ToArray();
        void AddNested(string token, string resource)
        {
            var segments = path.Split('/');
            for (var i = 0; i + 1 < segments.Length; i++)
                if (segments[i] == token && Identity(segments[i + 1])) result.Add(new(resource, segments[i + 1]));
        }
    }

    public static bool IsMappedResourceRoute(string path) => RouteReferences(path).Count > 0;
    public static bool HasUnmappedRouteIdentity(string path)
    {
        var normalized = Path(path);
        var mapped = RouteReferences(normalized).GroupBy(row => row.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var value in normalized.Split('/').Where(Identity))
        {
            if (!mapped.TryGetValue(value, out var count) || count <= 0) return true;
            mapped[value] = count - 1;
        }
        return false;
    }
    public static bool IsSharedReadReference(ScopeOwnershipReference reference) => reference.Resource is
        "workflow" or "form" or "form-version" or "form-field" or "recruitment-template" or "pipeline" or "pipeline-version" or "pipeline-stage" or "attachment-attribute" or "attachment-configuration";
    public static bool TryRootReference(string path, string value, out ScopeOwnershipReference reference)
    {
        var normalized = Path(path); var route = Routes.FirstOrDefault(r => normalized == r.Prefix);
        var resource = route?.Resource;
        if (normalized == "/api/recruitment-admin/ai-scoring") resource = "ai-setting";
        if (Regex.IsMatch(normalized, @"^/api/clients/\d+/pf-policy-versions$")) resource = "pf-policy-version";
        if (Regex.IsMatch(normalized, @"^/api/(?:recruitment-orchestration|employees/field-configuration)/forms/\d+/versions$")) resource = "form-version";
        if (Regex.IsMatch(normalized, @"^/api/recruitment-orchestration/pipelines/\d+/versions$")) resource = "pipeline-version";
        if (Regex.IsMatch(normalized, @"^/api/recruitment-orchestration/pipeline-stages/\d+/actions$")) resource = "pipeline-stage-action";
        reference = new(resource ?? "unknown", value);
        return resource is not null && Identity(value);
    }

    // Ambiguous names such as templateId and policyId have meaning only in the
    // owning workspace. Never infer recruitment ownership for a payroll template.
    public static bool TryReference(string path, string fieldName, string value, out ScopeOwnershipReference reference)
    {
        var normalized = Path(path); var name = Regex.Replace(fieldName, "[^A-Za-z0-9]", "").ToLowerInvariant();
        var resource = name switch
        {
            "ids" or "rowids" when normalized is "/api/recruitment/document-roster/releases" or "/api/recruitment/document-roster/drafts" or "/api/recruitment/document-roster/rows/work-location" => "document-roster",
            "templateid" or "structureid" or "salarystructureid" when Under(normalized, "/api/payroll") || Under(normalized, "/api/pay-runs") || Under(normalized, "/api/setup") => "salary-structure",
            "templateid" when Under(normalized, "/api/recruitment") || Under(normalized, "/api/recruitment-admin") || Under(normalized, "/api/recruitment-orchestration") => "recruitment-template",
            "pipelinestageinstanceid" when Under(normalized, "/api/recruitment/hiring-cases") => "position-stage-instance",
            "pipelinestageinstanceid" => "application-stage-instance",
            "transitionid" or "targettransitionid" => "pipeline-transition",
            "fromstageid" or "tostageid" or "targetstageid" => "pipeline-stage",
            "policyid" when Under(normalized, "/api/travel-expense") => "travel-policy",
            "headerid" when Under(normalized, "/api/client-billing") => "billing-header",
            "competencyid" => "interview-competency",
            "referralid" => "referral",
            _ => ""
        };
        if (resource.Length == 0) return TryReference(fieldName, value, out reference);
        reference = new(resource, value, resource is "salary-structure" or "recruitment-template" or "pipeline-stage" or "pipeline-transition");
        return Identity(value);
    }

    public static bool TryReference(string fieldName, string value, out ScopeOwnershipReference reference)
    {
        var name = Regex.Replace(fieldName, "[^A-Za-z0-9]", "").ToLowerInvariant();
        var resource = name switch
        {
            "clientid" or "companyid" or "directclientid" or "scopeclientid" => "client",
            "employeeid" or "employeeids" or "includedemployeeids" or "excludedemployeeids" or "reportingemployeeid" or "replacementemployeeid" or "existingemployeeid" or "reportingmanagerid" or "referreremployeeid" or "requestedbyemployeeid" => "employee",
            "payrunid" or "payrunids" or "sourcepayrunid" => "payrun",
            "adjustmentid" or "adjustmentids" => "adjustment", "worklocationid" or "worklocationids" or "locationid" or "locationids" or "branchid" => "location",
            "shiftid" or "shiftids" or "allowedshiftids" => "shift", "groupid" or "attendancegroupid" or "attendancegroupids" => "attendance-group",
            "leavetypeid" or "leavetypeids" => "leave-type", "holidayid" => "holiday",
            "candidateid" or "candidateids" => "candidate", "applicationid" or "applicationids" => "application",
            "positionid" or "positionids" => "position", "requisitionid" or "requisitionids" => "requisition",
            "jobpostingid" or "jobpostingids" => "job-posting", "interviewid" or "interviewids" => "interview",
            "offerid" or "offerids" => "offer", "workorderid" => "work-order", "workorderlineid" => "work-order-line",
            "hiringcaseid" => "hiring-case", "processdocumentid" or "documentid" => "process-document",
            "resumeid" => "candidate-resume", "applicationscoreid" => "application-score",
            "formdefinitionid" or "definitionid" or "formid" => "form",
            "formversionid" or "applicationformversionid" => "form-version", "fieldid" => "form-field",
            "pipelineid" or "pipelinedefinitionid" => "pipeline", "pipelineversionid" => "pipeline-version",
            "pipelinestageid" => "pipeline-stage", "jobdescriptionversionid" => "job-description",
            "offertemplateid" or "jobdescriptiontemplateid" => "recruitment-template", "workflowid" or "approvalworkflowid" or "appliedapprovalworkflowid" or "varianceapprovalworkflowid" => "workflow",
            "scoringprofileid" or "defaultatsscoringprofileid" => "scoring-profile",
            "fieldconfigurationid" or "attachmentfieldconfigurationid" or "candidateproofattachmentfieldconfigurationid" => "attachment-configuration",
            "attachmentattributeid" => "attachment-attribute",
            "instanceid" or "workflowinstanceid" => "workflow-instance", "taskid" => "workflow-task",
            "attachmentpublicid" or "publicid" => "attachment", "attachmentid" => "attachment-row",
            "campaignid" => "communication-campaign", "draftid" => "communication-draft", "conversationid" => "communication-conversation",
            "travelrequestid" => "travel-request", "advanceid" => "travel-advance", "claimid" or "expenseclaimid" => "expense-claim",
            "policyid" => "travel-policy", "headerid" => "billing-header",
            _ => ""
        };
        var shared = resource is "workflow" or "form" or "form-version" or "form-field" or "recruitment-template" or "pipeline" or "pipeline-version" or "pipeline-stage" or "scoring-profile" or "attachment-configuration" or "attachment-attribute";
        reference = new(resource, value, shared);
        return resource.Length > 0 && Identity(value);
    }

    public static bool TryEntityReference(string entityType, string value, out ScopeOwnershipReference reference)
    {
        var normalized = Regex.Replace(entityType, "[^A-Za-z0-9]", "").ToLowerInvariant();
        if (normalized == "recruitmentpipelinetransition")
        {
            var kind = value.StartsWith("CASE:", StringComparison.OrdinalIgnoreCase) ? "hiring-case-transition"
                : value.StartsWith("MOM:", StringComparison.OrdinalIgnoreCase) ? "process-document" : "application-transition";
            var id = value.Contains(':') ? value.Split(':', 2)[1] : value;
            reference = new(kind, id); return Identity(id);
        }
        var resource = normalized switch
        {
            "employee" or "employeeprofileedit" => "employee", "employeeprofilefirstedit" => "client",
            "payrun" or "payrollrun" => "payrun", "recruitmentrequisition" => "requisition",
            "recruitmentopenposition" or "recruitmentbudget" => "position", "recruitmentcandidate" or "candidate" => "candidate",
            "candidateapplication" or "recruitmentapplication" => "application", "recruitmentinterview" => "interview",
            "recruitmentoffer" or "recruitmentfinaloffer" => "offer", "recruitmentjobdescription" => "job-description",
            "recruitmentprocessdocument" => "process-document", "recruitmentprofilesubmissionbatch" => "profile-batch",
            "travelrequest" or "esstravelrequest" => "travel-request", "expenseclaim" or "essexpenseclaim" => "expense-claim",
            "leaverequest" or "attendanceregularization" => "leave-request",
            _ => ""
        };
        reference = new(resource, value); return resource.Length > 0 && Identity(value);
    }

    public static bool IsAllowedScopedRequest(string method, string path)
    {
        path = Path(path); var get = HttpMethods.IsGet(method);
        // These workspaces expose shared infrastructure, global candidate pools,
        // credentials, arbitrary SQL/actions, unscoped imports or global audit.
        string[] denied = ["/api/admin", "/api/security", "/api/audit-logs", "/api/system", "/api/integrations/ai", "/api/scheduled-jobs",
            "/api/attachment-storage-servers", "/api/communication-settings", "/api/notifications/setup", "/api/notifications/incoming",
            "/api/notifications/imap", "/api/notifications/smtp", "/api/notifications/test", "/api/notifications/templates", "/api/notifications/rules",
            "/api/frevopilot/analytics", "/api/recruitment/talent-pool", "/api/recruitment/seed-pack", "/api/excel-payslips", "/api/excel-payroll-setup"];
        if (denied.Any(prefix => Under(path, prefix))) return false;
        if (path.Contains("/global-talent-pool", StringComparison.Ordinal)) return false;
        if (HasUnmappedRouteIdentity(path)) return false;
        // Workbooks in these legacy setup imports choose arbitrary tenants inside
        // their rows. They are not scoped by a ClientId request parameter.
        if (new[] { "/api/clients", "/api/work-locations", "/api/dropdowns", "/api/salary-components", "/api/salary-templates" }
            .Any(prefix => Under(path, prefix + "/import-jobs") || Under(path, prefix + "/import-template"))) return false;
        if (Under(path, "/api/organization")) return get;
        if (path == "/api/clients") return get;
        if (Under(path, "/api/dropdowns") && !get) return false;
        if (path == "/api/client-billing/module") return get;
        if (Under(path, "/api/client-billing/configurations/import-jobs") || Under(path, "/api/client-billing/configurations/import-template")) return false;
        if (Under(path, "/api/travel-expense/categories") || Under(path, "/api/travel-expense/global-category") || Under(path, "/api/travel-expense/global-header")) return false;
        if (Under(path, "/api/tax-engine") && !get && path != "/api/tax-engine/compute"
            && !Under(path, "/api/tax-engine/client-settings") && !Under(path, "/api/tax-engine/employee-profiles")) return false;
        if (Under(path, "/api/notifications")) return Under(path, "/api/notifications/recruitment-triggers");
        if (Under(path, "/api/workflows")) return get && (path == "/api/workflows" || Under(path, "/api/workflows/tasks") || path == "/api/workflows/requests/mine")
            || path == "/api/workflows/start" || Regex.IsMatch(path, @"^/api/workflows/tasks/\d+/[^/]+$") || Regex.IsMatch(path, @"^/api/workflows/\d+/history$");
        string[] allowed = ["/api/auth", "/api/organization", "/api/clients", "/api/setup", "/api/dashboard", "/api/work-locations", "/api/dropdowns",
            "/api/employees", "/api/employee-form-bindings", "/api/payroll", "/api/pay-runs", "/api/payroll-adjustments", "/api/reports",
            "/api/payroll-calculation", "/api/payroll-advisor", "/api/leave-attendance", "/api/integrations/attendance", "/api/recruitment", "/api/recruitment-admin", "/api/recruitment-orchestration",
            "/api/client-billing", "/api/travel-expense", "/api/travel-advances", "/api/tax-engine", "/api/employee-communications",
            "/api/attachments", "/api/attachment-targets", "/api/attachment-configurations", "/api/attachment-attributes", "/api/ess"];
        return allowed.Any(prefix => Under(path, prefix));
    }
}

// Register scoped. Cache lasts for this request only, so moved records and revoked
// client assignments are checked afresh on the following authenticated request.
public sealed class SelectedClientOwnershipStore(IConfiguration configuration, IServiceProvider? services = null) : ISelectedClientOwnershipLookup
{
    public int ScopeClientId { get; set; }
    private readonly Dictionary<ScopeOwnershipReference, int?> cache = [];
    private static readonly IReadOnlyDictionary<string, string> Sql = new Dictionary<string, string>
    {
        ["client"] = "SELECT Id FROM clients WHERE Id=@Id",
        ["employee"] = "SELECT ClientId FROM employees WHERE Id=@Id",
        ["payrun"] = "SELECT ClientId FROM payruns WHERE Id=@Id",
        ["adjustment"] = "SELECT ClientId FROM payrolladjustments WHERE Id=@Id",
        ["tax-client-setting"] = "SELECT client_id FROM tax_client_settings WHERE id=@Id",
        ["billing-configuration"] = "SELECT ClientId FROM client_billing_configurations WHERE Id=@Id",
        ["billing-header"] = "SELECT ClientId FROM client_billing_cost_rule_headers WHERE Id=@Id",
        ["billing-line"] = "SELECT h.ClientId FROM client_billing_cost_rule_lines l JOIN client_billing_cost_rule_headers h ON h.Id=l.HeaderId WHERE l.Id=@Id",
        ["travel-policy"] = "SELECT CompanyId FROM travel_policies WHERE Id=@Id",
        ["travel-assignment"] = "SELECT CompanyId FROM travel_policy_assignments WHERE Id=@Id",
        ["travel-rule"] = "SELECT p.CompanyId FROM travel_policy_rules r JOIN travel_policies p ON p.Id=r.PolicyId WHERE r.Id=@Id",
        ["travel-client-setting"] = "SELECT ClientId FROM travel_expense_client_settings WHERE Id=@Id",
        ["location"] = "SELECT ClientId FROM worklocations WHERE Id=@Id",
        ["shift"] = "SELECT client_id FROM attendance_shifts WHERE id=@Id",
        ["shift-assignment"] = "SELECT client_id FROM attendance_shift_assignments WHERE id=@Id",
        ["attendance-group"] = "SELECT client_id FROM attendance_groups WHERE id=@Id",
        ["attendance-settings"] = "SELECT client_id FROM attendance_settings WHERE id=@Id",
        ["geo-fence"] = "SELECT client_id FROM attendance_geo_fence_rules WHERE id=@Id",
        ["leave-type"] = "SELECT client_id FROM leave_types WHERE id=@Id",
        ["holiday"] = "SELECT client_id FROM holidays WHERE id=@Id",
        ["attendance-job"] = "SELECT client_id FROM attendance_batch_jobs WHERE job_id=@Id",
        ["employee-form-binding"] = "SELECT ClientId FROM employee_form_bindings WHERE Id=@Id",
        ["candidate"] = "SELECT ClientId FROM recruitment_candidates WHERE Id=@Id",
        ["referral"] = "SELECT p.ClientId FROM recruitment_employee_referrals r JOIN recruitment_open_positions p ON p.Id=r.PositionId WHERE r.Id=@Id",
        ["candidate-resume"] = "SELECT c.ClientId FROM recruitment_candidate_resumes r JOIN recruitment_candidates c ON c.Id=r.CandidateId WHERE r.Id=@Id",
        ["candidate-action"] = "SELECT ClientId FROM recruitment_candidate_action_sessions WHERE Id=@Id",
        ["application"] = "SELECT ClientId FROM recruitment_candidate_applications WHERE Id=@Id",
        ["application-score"] = "SELECT a.ClientId FROM recruitment_application_scores s JOIN recruitment_candidate_applications a ON a.Id=s.ApplicationId WHERE s.Id=@Id",
        ["scoring-job"] = "SELECT a.ClientId FROM recruitment_ats_scoring_jobs s JOIN recruitment_candidate_applications a ON a.Id=s.ApplicationId WHERE s.Id=@Id",
        ["candidate-checklist"] = "SELECT a.ClientId FROM recruitment_candidate_checklist_items s JOIN recruitment_candidate_applications a ON a.Id=s.ApplicationId WHERE s.Id=@Id",
        ["position"] = "SELECT ClientId FROM recruitment_open_positions WHERE Id=@Id",
        ["requisition"] = "SELECT ClientId FROM recruitment_requisitions WHERE Id=@Id",
        ["interview"] = "SELECT COALESCE(a.ClientId,i.DirectClientId) FROM recruitment_interviews i LEFT JOIN recruitment_candidate_applications a ON a.Id=i.ApplicationId WHERE i.Id=@Id",
        ["interview-question"] = "SELECT ClientId FROM recruitment_interview_questions WHERE Id=@Id",
        ["offer"] = "SELECT ClientId FROM recruitment_offers WHERE Id=@Id",
        ["work-order"] = "SELECT ClientId FROM recruitment_work_orders WHERE Id=@Id",
        ["work-order-line"] = "SELECT w.ClientId FROM recruitment_work_order_lines l JOIN recruitment_work_orders w ON w.Id=l.WorkOrderId WHERE l.Id=@Id",
        ["hiring-case"] = "SELECT ClientId FROM recruitment_position_pipeline_instances WHERE Id=@Id",
        ["hiring-case-transition"] = "SELECT h.ClientId FROM recruitment_hiring_case_advance_requests t JOIN recruitment_position_pipeline_instances h ON h.Id=t.HiringCaseId WHERE t.Id=@Id",
        ["application-transition"] = "SELECT a.ClientId FROM recruitment_pipeline_transition_requests t JOIN recruitment_candidate_applications a ON a.Id=t.ApplicationId WHERE t.Id=@Id",
        ["process-document"] = "SELECT ClientId FROM recruitment_process_documents WHERE Id=@Id",
        ["profile-batch"] = "SELECT ClientId FROM recruitment_profile_submission_batches WHERE Id=@Id",
        ["document-roster"] = "SELECT ClientId FROM recruitment_document_roster WHERE Id=@Id",
        ["job-posting"] = "SELECT ClientId FROM recruitment_job_postings WHERE Id=@Id",
        ["job-description"] = "SELECT ClientId FROM recruitment_job_description_versions WHERE Id=@Id",
        ["pipeline"] = "SELECT ClientId FROM recruitment_pipeline_definitions WHERE Id=@Id",
        ["pipeline-version"] = "SELECT p.ClientId FROM recruitment_pipeline_versions v JOIN recruitment_pipeline_definitions p ON p.Id=v.PipelineDefinitionId WHERE v.Id=@Id",
        ["pipeline-stage"] = "SELECT p.ClientId FROM recruitment_pipeline_stages s JOIN recruitment_pipeline_versions v ON v.Id=s.PipelineVersionId JOIN recruitment_pipeline_definitions p ON p.Id=v.PipelineDefinitionId WHERE s.Id=@Id",
        ["pipeline-transition"] = "SELECT p.ClientId FROM recruitment_pipeline_transitions t JOIN recruitment_pipeline_versions v ON v.Id=t.PipelineVersionId JOIN recruitment_pipeline_definitions p ON p.Id=v.PipelineDefinitionId WHERE t.Id=@Id",
        ["pipeline-stage-action"] = "SELECT p.ClientId FROM recruitment_pipeline_stage_actions a JOIN recruitment_pipeline_stages s ON s.Id=a.PipelineStageId JOIN recruitment_pipeline_versions v ON v.Id=s.PipelineVersionId JOIN recruitment_pipeline_definitions p ON p.Id=v.PipelineDefinitionId WHERE a.Id=@Id",
        ["interview-competency"] = "SELECT ClientId FROM recruitment_interview_competency_definitions WHERE Id=@Id",
        ["position-stage-instance"] = "SELECT p.ClientId FROM recruitment_position_stage_instances s JOIN recruitment_position_pipeline_instances p ON p.Id=s.PositionPipelineInstanceId WHERE s.Id=@Id",
        ["application-stage-instance"] = "SELECT a.ClientId FROM recruitment_application_stage_instances s JOIN recruitment_candidate_applications a ON a.Id=s.ApplicationId WHERE s.Id=@Id",
        ["form"] = "SELECT ClientId FROM form_definitions WHERE Id=@Id",
        ["form-version"] = "SELECT f.ClientId FROM form_versions v JOIN form_definitions f ON f.Id=v.FormDefinitionId WHERE v.Id=@Id",
        ["form-field"] = "SELECT f.ClientId FROM form_fields d JOIN form_versions v ON v.Id=d.FormVersionId JOIN form_definitions f ON f.Id=v.FormDefinitionId WHERE d.Id=@Id",
        ["recruitment-setting"] = "SELECT ClientId FROM recruitment_settings WHERE Id=@Id",
        ["recruitment-master"] = "SELECT ClientId FROM recruitment_master_values WHERE Id=@Id",
        ["recruitment-partner"] = "SELECT ClientId FROM recruitment_partners WHERE Id=@Id",
        ["recruitment-assignment"] = "SELECT ClientId FROM recruitment_assignment_rules WHERE Id=@Id",
        ["recruitment-sla"] = "SELECT ClientId FROM recruitment_sla_rules WHERE Id=@Id",
        ["recruitment-checklist"] = "SELECT ClientId FROM recruitment_document_checklist WHERE Id=@Id",
        ["recruitment-approval"] = "SELECT ClientId FROM recruitment_approval_mappings WHERE Id=@Id",
        ["recruitment-template"] = "SELECT ClientId FROM recruitment_templates WHERE Id=@Id",
        ["recruitment-notification-rule"] = "SELECT ClientId FROM recruitment_lifecycle_notification_rules WHERE Id=@Id",
        ["recruitment-skill"] = "SELECT ClientId FROM recruitment_skills WHERE Id=@Id",
        ["scoring-profile"] = "SELECT ClientId FROM recruitment_ats_scoring_profiles WHERE Id=@Id",
        ["ai-setting"] = "SELECT ClientId FROM recruitment_ai_scoring_settings WHERE Id=@Id",
        ["workflow"] = "SELECT COALESCE(ClientId,0) FROM workflowmasters WHERE Id=@Id",
        ["attachment"] = "SELECT client_id FROM entity_attachments WHERE public_id=@Id",
        ["attachment-row"] = "SELECT client_id FROM entity_attachments WHERE id=@Id",
        ["attachment-attribute"] = "SELECT client_id FROM attachment_attributes WHERE id=@Id",
        ["attachment-configuration"] = "SELECT client_id FROM attachment_field_configurations WHERE id=@Id",
        ["travel-request"] = "SELECT ClientId FROM ess_travel_requests WHERE Id=@Id",
        ["travel-advance"] = "SELECT ClientId FROM ess_travel_advances WHERE Id=@Id",
        ["expense-claim"] = "SELECT ClientId FROM ess_expense_claims WHERE Id=@Id",
        ["leave-request"] = "SELECT ClientId FROM essleaverequests WHERE Id=@Id",
        ["communication-campaign"] = "SELECT ClientId FROM employee_communication_campaigns WHERE Id=@Id",
        ["communication-draft"] = "SELECT ClientId FROM employee_communication_drafts WHERE Id=@Id",
        ["communication-conversation"] = "SELECT ClientId FROM employee_communication_conversations WHERE Id=@Id",
        ["payroll-review"] = "SELECT client_id FROM payroll_config_reviews WHERE id=@Id"
    };

    // Bulk employee/attendance/payroll requests share one read per resource type,
    // rather than one network round trip for each submitted employee. Dynamic
    // workflow and JSON-policy ownership retains its existing resolver below.
    public async Task PrimeAsync(IEnumerable<ScopeOwnershipReference> references, CancellationToken cancellationToken = default)
    {
        var groups = references.Distinct().Where(reference => !cache.ContainsKey(reference) && Sql.ContainsKey(reference.Resource))
            .GroupBy(reference => reference.Resource).ToArray();
        if (groups.Length == 0) return;
        await using var db = new MySqlConnection(configuration.GetConnectionString("Default"));
        await db.OpenAsync(cancellationToken);
        foreach (var group in groups)
        {
            if (!TryBuildBatchSql(group.Key, out var sql)) continue;
            var rows = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
            foreach (var ids in group.Select(reference => reference.Value).Distinct(StringComparer.OrdinalIgnoreCase).Chunk(1000))
                foreach (var row in await db.QueryAsync<BatchOwner>(new CommandDefinition(sql, new { Ids = ids }, cancellationToken: cancellationToken)))
                    rows[NormalizeBatchKey(group.Key, row.ReferenceId)] = row.ClientId;
            foreach (var reference in group)
                cache[reference] = rows.GetValueOrDefault(NormalizeBatchKey(group.Key, reference.Value));
        }
    }

    internal static string NormalizeBatchKey(string resource, string value) => resource is "attachment" or "attendance-job" or "payroll-review"
        ? value : long.TryParse(value, out var number) ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : value;

    internal static bool TryBuildBatchSql(string resource, out string sql)
    {
        sql = "";
        if (!Sql.TryGetValue(resource, out var source)) return false;
        var from = source.IndexOf(" FROM ", StringComparison.Ordinal);
        var where = source.LastIndexOf(" WHERE ", StringComparison.Ordinal);
        if (from < 0 || where <= from) return false;
        var predicate = source[(where + 7)..];
        if (!Regex.IsMatch(predicate, @"^[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)?=@Id$")) return false;
        var key = predicate[..^4]; var owner = source[7..from];
        sql = $"SELECT CAST({key} AS CHAR) ReferenceId,{owner} ClientId{source[from..where]} WHERE {key} IN @Ids";
        return true;
    }
    private sealed class BatchOwner
    {
        public string ReferenceId { get; set; } = "";
        public int? ClientId { get; set; }
    }

    public async Task<int?> ClientIdAsync(ScopeOwnershipReference reference, CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(reference, out var cached)) return cached;
        if (reference.Resource is "employee-import-job" or "leave-type-import-job" or "holiday-import-job")
        {
            int? owner = null;
            if (Guid.TryParse(reference.Value, out var jobId) && services is not null)
                owner = reference.Resource switch
                {
                    "employee-import-job" => services.GetService<EmployeeRepository>()?.GetImportJob(jobId)?.ClientId,
                    "leave-type-import-job" => services.GetService<LeaveAttendanceRepository>()?.GetLeaveTypeImportJob(jobId)?.ClientId,
                    "holiday-import-job" => services.GetService<LeaveAttendanceRepository>()?.GetHolidayImportJob(jobId)?.ClientId,
                    _ => null
                };
            cache[reference] = owner; return owner;
        }
        if (!Sql.ContainsKey(reference.Resource) && reference.Resource is not ("workflow-instance" or "workflow-task" or "salary-structure" or "pf-policy-version")) return null;
        if (reference.Resource == "pf-policy-version" && ScopeClientId <= 0) return null;
        await using var db = new MySqlConnection(configuration.GetConnectionString("Default"));
        await db.OpenAsync(cancellationToken);
        int? value;
        if (reference.Resource == "salary-structure")
        {
            var structure = await db.QuerySingleOrDefaultAsync<SalaryStructureScope>(new CommandDefinition(
                "SELECT ClientId,ClientRef FROM salarystructures WHERE Id=@Id", new { Id = reference.Value }, cancellationToken: cancellationToken));
            value = structure is null ? null : ResolveSalaryStructureOwner(structure.ClientId, structure.ClientRef, ScopeClientId);
        }
        else if (reference.Resource == "pf-policy-version")
        {
            // PF versions live inside the selected client's payroll module JSON,
            // so a matching GUID in a different module document has no authority.
            var versions = await PfPolicyRepository.ReadVersionsAsync(db, null, ScopeClientId);
            value = versions.SingleOrDefault(version => version.Id.Equals(reference.Value, StringComparison.OrdinalIgnoreCase))?.ClientId;
        }
        else value = await FindAsync(db, reference, cancellationToken);
        cache[reference] = value; return value;
    }
    public static int? ResolveSalaryStructureOwner(int storedClientId, string? clientReference, int scopeClientId)
    {
        var clients = new HashSet<int>();
        if (!string.IsNullOrWhiteSpace(clientReference))
        {
            foreach (var part in clientReference.Split([',', ';', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var token = part.Split(':', 2)[0].Trim();
                if (int.TryParse(token, out var id) && id > 0) clients.Add(id);
                else if (!token.Equals("all", StringComparison.OrdinalIgnoreCase) && token != "0") return null;
            }
        }
        if (clients.Count == 0 && storedClientId > 0) clients.Add(storedClientId);
        return clients.Contains(scopeClientId) ? scopeClientId : clients.Count > 0 ? clients.Min() : 0;
    }
    private static async Task<int?> FindAsync(MySqlConnection db, ScopeOwnershipReference reference, CancellationToken ct)
    {
        if (Sql.TryGetValue(reference.Resource, out var sql))
            return await db.ExecuteScalarAsync<int?>(new CommandDefinition(sql, new { Id = reference.Value }, cancellationToken: ct));
        if (reference.Resource is not ("workflow-instance" or "workflow-task")) return null;
        var select = reference.Resource == "workflow-task"
            ? "SELECT i.ResourceType,i.ResourceId FROM workflowtasks t JOIN workflowinstances i ON i.Id=t.InstanceId WHERE t.Id=@Id"
            : "SELECT ResourceType,ResourceId FROM workflowinstances WHERE Id=@Id";
        var owner = await db.QuerySingleOrDefaultAsync<WorkflowResource>(new CommandDefinition(select, new { Id = reference.Value }, cancellationToken: ct));
        return owner is not null && SelectedClientOwnershipCatalog.TryEntityReference(owner.ResourceType, owner.ResourceId, out var target)
            ? await FindAsync(db, target, ct) : null;
    }
    private sealed record WorkflowResource(string ResourceType, string ResourceId);
    private sealed record SalaryStructureScope(int ClientId, string ClientRef);
}
