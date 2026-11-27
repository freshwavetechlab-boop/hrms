using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class EmployeeRepository
{
    public async Task<EmployeeEmailReminderPage> EmployeeEmailRemindersAsync(AuthUser user, int offset = 0, int limit = 100, int? clientId = null)
    {
        if (EmployeeEmailReminderPagingError(offset, limit) is { } invalid) throw new ArgumentOutOfRangeException(nameof(offset), invalid);
        if (!CanSeeEmployeeEmailReminders(user) || clientId.HasValue && (!SelectedClientAccess.Allows(user, clientId.Value)
                || user.ClientId.HasValue && user.ClientId != clientId)
            || SelectedClientAccess.IsSelected(user) && user.AssignedClientIds.Count == 0)
            return new([], 0, offset, limit);

        // Read current contact/identity columns only. Nothing is inserted into a
        // mail queue or notification table, so a completed correction disappears
        // on the next read and a skipped import field keeps its reminder.
        var selected = SelectedClientAccess.IsSelected(user);
        var sql = $"""
            SELECT e.Id EmployeeId,e.ClientId,c.Name ClientName,e.EmployeeCode,
                TRIM(CONCAT_WS(' ',e.FirstName,e.LastName)) EmployeeName,e.WorkEmail,
                {EmployeeEmploymentRules.EligibleOn("e", "@Today")} IsEligible
            FROM employees e JOIN clients c ON c.Id=e.ClientId
            WHERE e.IsActive=TRUE AND (@client IS NULL OR e.ClientId=@client)
            """ + (selected ? " AND e.ClientId IN @assigned" : "");
        await using var db = Connection();
        var rows = await db.QueryAsync<EmployeeEmailReminderSource>(sql,
            new { client = user.ClientId ?? clientId, assigned = user.AssignedClientIds, Today = EmployeeEmploymentRules.Today });
        return BuildEmployeeEmailReminderPage(rows, user, offset, limit);
    }

    internal static bool CanSeeEmployeeEmailReminders(AuthUser user) => user.Id > 0 && user.IsActive && !user.MustChangePassword
        && (user.ClientAccessMode.Equals("Legacy", StringComparison.OrdinalIgnoreCase) || SelectedClientAccess.IsSelected(user))
        && user.Permissions.Contains("employees.manage", StringComparer.OrdinalIgnoreCase);

    internal static string? EmployeeEmailReminderPagingError(int offset, int limit) => offset < 0
        ? "Offset must be zero or greater." : limit is < 1 or > 100 ? "Limit must be between 1 and 100." : null;

    internal static EmployeeEmailReminderPage BuildEmployeeEmailReminderPage(IEnumerable<EmployeeEmailReminderSource> rows, AuthUser user, int offset, int limit)
    {
        if (EmployeeEmailReminderPagingError(offset, limit) is { } invalid) throw new ArgumentOutOfRangeException(nameof(offset), invalid);
        if (!CanSeeEmployeeEmailReminders(user)) return new([], 0, offset, limit);
        var reminders = rows.Where(row => row.EmployeeId > 0 && row.IsEligible
                && (!user.ClientId.HasValue || user.ClientId == row.ClientId) && SelectedClientAccess.Allows(user, row.ClientId)
                && !EmailOk(row.WorkEmail ?? ""))
            .OrderBy(row => row.ClientName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.EmployeeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.EmployeeCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.EmployeeId)
            .Select(row => new EmployeeEmailReminder(row.EmployeeId, row.EmployeeCode, row.EmployeeName, row.ClientId, row.ClientName,
                string.IsNullOrWhiteSpace(row.WorkEmail) ? "Missing" : "Invalid"))
            .ToList();
        return new(reminders.Skip(offset).Take(limit).ToList(), reminders.Count, offset, limit);
    }

    internal sealed class EmployeeEmailReminderSource
    {
        public int EmployeeId { get; set; }
        public int ClientId { get; set; }
        public string ClientName { get; set; } = "";
        public string EmployeeCode { get; set; } = "";
        public string EmployeeName { get; set; } = "";
        public string WorkEmail { get; set; } = "";
        public bool IsEligible { get; set; }
    }
}

public sealed record EmployeeEmailReminder(int EmployeeId, string EmployeeCode, string EmployeeName, int ClientId, string ClientName, string EmailIssue);
public sealed record EmployeeEmailReminderPage(IReadOnlyList<EmployeeEmailReminder> Items, int Total, int Offset, int Limit);
