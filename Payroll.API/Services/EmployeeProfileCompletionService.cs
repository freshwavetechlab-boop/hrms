using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public class EmployeeProfileCompletionService(IConfiguration configuration, EmployeeRepository employees, AttachmentRepository attachments, CommunicationRepository communications)
{
    public const string TemplateCode = "EMPLOYEE_MISSING_INFORMATION";
    public const string EssUrl = "https://gad-ess.frevo.co.in/";

    public async Task<List<EmployeeCompletionStatus>> StatusAsync(int? clientId, AuthUser user, IReadOnlyCollection<int>? ids = null)
    {
        if (user.ClientId.HasValue && clientId.HasValue && user.ClientId != clientId) return [];
        var rows = (await employees.GetAsync(user.ClientId ?? clientId)).Where(row => ids is null || ids.Contains(row.Id)).ToList();
        if (rows.Count == 0) return [];
        await using var db = new MySqlConnection(configuration.GetConnectionString("Default")); await db.OpenAsync();
        var files = (await db.QueryAsync<EntityAttachment>(@"SELECT entity_id EntityId,attachment_attribute_id AttachmentAttributeId,expiry_date ExpiryDate,verification_status VerificationStatus
FROM entity_attachments WHERE entity_type='EMPLOYEE' AND entity_id IN @Ids AND is_current=TRUE AND is_deleted=FALSE", new { Ids = rows.Select(row => row.Id).ToArray() })).ToLookup(row => row.EntityId);
        var result = new List<EmployeeCompletionStatus>();
        foreach (var group in rows.GroupBy(row => row.ClientId))
        {
            var configs = (await attachments.GetEffectiveConfigurationsAsync(group.Key, "EMPLOYEE", "EMPLOYEE_CREATE_EDIT"))
                .Concat(await attachments.GetEffectiveConfigurationsAsync(group.Key, "EMPLOYEE", "EMPLOYEE_PROFILE")).ToList();
            foreach (var employee in group)
            {
                var missing = MissingFields(employee, configs, files[employee.Id]);
                var reason = !employee.IsActive ? "Employee is inactive." : !employee.PortalAccess ? "Enable ESS portal access first." :
                    !System.Net.Mail.MailAddress.TryCreate(employee.WorkEmail, out _) ? "Add a valid work email first." : missing.Count == 0 ? "All required information is complete." : "";
                result.Add(new EmployeeCompletionStatus(employee.Id, employee.ClientId, missing, reason.Length == 0, reason));
            }
        }
        return result;
    }

    internal static List<string> MissingFields(Employee employee, IEnumerable<AttachmentFieldConfiguration> configurations, IEnumerable<EntityAttachment> files)
    {
        var fields = new List<string>(); var personal = employee.PersonalDetails; var payment = employee.PaymentDetails;
        void Required(string label, string? value) { if (string.IsNullOrWhiteSpace(value)) fields.Add(label); }
        Required("Employee code", employee.EmployeeCode); Required("Employee type", personal.EmploymentType); Required("Category", personal.SkillCategory);
        Required("Department", employee.Department); Required("Designation", employee.Designation);
        if (employee.WorkLocationId <= 0) fields.Add("Work location");
        if (employee.PortalAccess) Required("Work email", employee.WorkEmail);
        Required("Name", employee.FirstName); Required("Joining date", employee.DateOfJoining); Required("Date of birth", personal.DateOfBirth); Required("Mobile", personal.Mobile);
        if (new[] { personal.Address, personal.CorrespondenceAddress, personal.PermanentAddress }.All(string.IsNullOrWhiteSpace)) fields.Add("Address");
        Required("Salary template", employee.SalaryStructureId); if (employee.AnnualCtc <= 0) fields.Add("Annual CTC");
        Required("Payment mode", payment.PaymentMode);
        if (!new[] { "cash", "cheque" }.Contains(payment.PaymentMode.Trim().ToLowerInvariant()))
        { Required("Bank name", payment.BankName); Required("Account number", payment.BankAccountNo); Required("IFSC", payment.IfscCode); }
        foreach (var group in configurations.Where(row => row.IsActive && row.IsRequired).GroupBy(row => row.AttachmentAttributeId))
        {
            var config = group.OrderByDescending(row => row.MinimumFileCount).First();
            var uploaded = files.Count(file => file.AttachmentAttributeId == group.Key && !file.VerificationStatus.Equals("Rejected", StringComparison.OrdinalIgnoreCase) && (!file.ExpiryDate.HasValue || file.ExpiryDate.Value.Date >= DateTime.UtcNow.Date));
            if (uploaded < Math.Max(1, config.MinimumFileCount)) fields.Add(config.FieldLabel);
        }
        return fields;
    }

    public async Task<CommunicationTemplate> EnsureTemplateAsync(int clientId, AuthUser user)
    {
        var existing = (await communications.GetTemplatesAsync(clientId, CommunicationChannels.Email, user)).FirstOrDefault(row => row.ClientId == clientId && row.Code == TemplateCode);
        if (existing is not null) return existing;
        var (item, error) = await communications.SaveTemplateAsync(new CommunicationTemplate
        {
            ClientId = clientId, Code = TemplateCode, Name = "Complete missing employee information", IsHtml = true,
            SubjectTemplate = "Complete your employee profile - {{employeeCode}}",
            BodyTemplate = "<p>Hello {{employeeName}},</p><p>Your employee profile for <strong>{{clientName}}</strong> ({{employeeCode}}) has missing information.</p><p>Please log in to <a href=\"{{essUrl}}\">Employee Self Service</a> using your existing ESS credentials, open <strong>My profile</strong>, complete your personal, contact, address and bank details, and upload the required documents.</p><p>Upload your documents before selecting <strong>Save profile</strong>. You can save once while edit access is open; the profile locks after a successful save. If it is locked, use <strong>Request edit access</strong>. After HR approves it, you can save once again.</p><p>For employment or salary details maintained by HR, please contact your HR team.</p><p>ESS portal: <a href=\"{{essUrl}}\">{{essUrl}}</a></p><p>Regards,<br>{{senderName}}</p>",
            Variables = [
                new() { VariableKey = "employeeName", Label = "Employee name", SourceCode = "Employee.FullName", IsRequired = true },
                new() { VariableKey = "employeeCode", Label = "Employee code", SourceCode = "Employee.EmployeeCode", IsRequired = true },
                new() { VariableKey = "clientName", Label = "Client name", SourceCode = "Client.Name", IsRequired = true },
                new() { VariableKey = "senderName", Label = "Sender", SourceCode = "CurrentUser.DisplayName", FallbackValue = "HR Team" },
                new() { VariableKey = "essUrl", Label = "ESS portal URL", FallbackValue = EssUrl, IsRequired = true },
            ]
        }, user);
        if (item is not null) return item;
        // A simultaneous setup may already have created the unique client template.
        return (await communications.GetTemplatesAsync(clientId, CommunicationChannels.Email, user)).FirstOrDefault(row => row.ClientId == clientId && row.Code == TemplateCode)
            ?? throw new InvalidOperationException(error);
    }
}

public record EmployeeCompletionStatus(int EmployeeId, int ClientId, List<string> MissingFields, bool CanSendEmail, string DisabledReason);
public record EmployeeCompletionSettingsRequest(int ClientId, bool FirstEditEnabled);
public record EmployeeCompletionMailRequest(int ClientId, List<int> EmployeeIds, string IdempotencyKey = "");
