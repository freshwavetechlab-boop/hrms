using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Payroll.API.Models;

namespace Payroll.API.Services;

// Only attendance inputs change. Salary assignments, formulas and source batches
// are immutable inputs to this plan, never inferred from an uploaded net amount.
internal static class PayrollLocationAttendancePlan
{
    internal sealed record Plan(List<PayrollLocationAttendanceRow> Changed,
        List<IGrouping<int, PayrollLocationAttendanceRow>> Publish, int PendingEmployees);

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static string InputVersion(string key, int payrollDays, decimal? presentDays, decimal? payableDays) =>
        Hash(JsonSerializer.Serialize(new { key, payrollDays, presentDays, payableDays }));
    internal static void ValidateScope(int clientId, string month, IReadOnlyList<int> groupIds)
    {
        if (clientId <= 0 || !DateTime.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || groupIds is null || groupIds.Count is < 1 or > 1000 || groupIds.Any(id => id <= 0) || groupIds.Distinct().Count() != groupIds.Count)
            throw new InvalidOperationException("Select a client, payroll month and attendance policy.");
    }

    internal static Plan Build(PayrollLocationAttendanceContext context, PayrollLocationAttendanceRequest request)
    {
        ValidateScope(request.ClientId, request.Month, request.GroupIds);
        if (!Guid.TryParseExact(request.RequestId, "N", out _) || request.Rows is null || request.Rows.Count is < 1 or > 1000 || request.Rows.Any(row => row is null))
            throw new InvalidOperationException("Use a new attendance import ID and 1–1000 reviewed location rows.");
        if (context.ClientId != request.ClientId || context.Month != request.Month || context.PolicyBatchId != request.PolicyBatchId
            || !context.GroupIds.Order().SequenceEqual(request.GroupIds.Order()) || context.Version != request.ContextVersion)
            throw new InvalidOperationException("Attendance, policy or salary inputs changed. Refresh and review the current location template.");
        if (!context.StorageAvailable) throw new InvalidOperationException("Apply the location attendance migration before saving this template.");
        if (request.WorkLocationId < 0 || request.Rows.Select(row => row.Key).Distinct(StringComparer.Ordinal).Count() != request.Rows.Count)
            throw new InvalidOperationException("Each location attendance part must occur once.");
        var rows = context.Rows.Select(row => JsonSerializer.Deserialize<PayrollLocationAttendanceRow>(JsonSerializer.Serialize(row))!).ToList();
        var changed = new List<PayrollLocationAttendanceRow>();
        foreach (var input in request.Rows)
        {
            var row = rows.SingleOrDefault(row => row.Key == input.Key);
            if (row is null || row.EmployeeId != input.EmployeeId || row.WorkLocationId != input.WorkLocationId
                || request.WorkLocationId > 0 && row.WorkLocationId != request.WorkLocationId)
                throw new InvalidOperationException("An attendance row belongs to another employee, location or policy.");
            if (row.LockedPayRunId.HasValue) throw new InvalidOperationException($"Payroll #{row.LockedPayRunId} already covers this employee/month. Saved payroll attendance was retained.");
            if (row.InputVersion != input.InputVersion) throw new InvalidOperationException("This location row changed after the template was downloaded. Refresh this location before replacing its attendance.");
            if (!string.IsNullOrEmpty(row.Issue)) throw new InvalidOperationException(row.EmployeeCode + ": " + row.Issue);
            if (input.PayrollDays is < 1 or > 31 || input.PresentDays < 0 || input.PayableDays < 0
                || input.PresentDays > input.PayrollDays || input.PayableDays > input.PayrollDays
                || decimal.Round(input.PresentDays, 4) != input.PresentDays || decimal.Round(input.PayableDays, 4) != input.PayableDays)
                throw new InvalidOperationException("Enter explicit payroll, present and payable days within the reviewed divisor; blank days are not zero.");
            row.PayrollDays = input.PayrollDays; row.PresentDays = input.PresentDays; row.PayableDays = input.PayableDays;
            if (!row.NativePart) row.MonthlyPayrollDays = input.PayrollDays;
            changed.Add(row);
        }
        var employeeIds = changed.Select(row => row.EmployeeId).ToHashSet();
        var affected = rows.Where(row => employeeIds.Contains(row.EmployeeId)).GroupBy(row => row.EmployeeId).ToList();
        foreach (var group in affected)
        {
            if (group.Select(row => row.NativePart).Distinct().Count() != 1
                || group.Select(row => row.MonthlyPayrollDays).Distinct().Count() != 1 || group.Any(row => !string.IsNullOrEmpty(row.Issue)))
                throw new InvalidOperationException("Review every salary/attendance part for this employee before publishing monthly totals.");
            var divisor = group.First().MonthlyPayrollDays;
            if (divisor is < 1 or > 31 || group.Sum(row => row.PresentDays ?? 0) > divisor || group.Sum(row => row.PayableDays ?? 0) > divisor)
                throw new InvalidOperationException("Location totals exceed the employee's monthly payroll days. No extra days were distributed automatically.");
        }
        var publish = affected.Where(group => group.All(row => row.Recorded)).ToList();
        return new(changed, publish, affected.Count - publish.Count);
    }
}
