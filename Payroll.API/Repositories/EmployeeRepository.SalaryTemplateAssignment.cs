using System.Globalization;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class EmployeeRepository
{
    // Master-only employees may remain unbound until payroll is configured.
    // Unrelated edits retain existing legacy references; this guard applies when
    // a caller actually assigns a new template or changes the employee's client.
    internal static async Task ValidateSalaryTemplateAssignmentAsync(MySqlConnection db, Employee employee, Employee? before, MySqlTransaction? tx = null)
    {
        var selected = employee.SalaryStructureId ?? "";
        if (string.IsNullOrWhiteSpace(selected)) return;
        if (before is not null && before.ClientId == employee.ClientId
            && string.Equals(before.SalaryStructureId ?? "", selected, StringComparison.Ordinal)) return;
        if (!long.TryParse(selected, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0
            || selected != id.ToString(CultureInfo.InvariantCulture))
            throw new InvalidOperationException("Select a valid salary template available for this employee's client, or leave Salary template unassigned.");

        var template = await db.QuerySingleOrDefaultAsync<SalaryAssignmentTemplate>(
            "SELECT ClientId,ClientRef,Active FROM salarystructures WHERE Id=@Id" + (tx is null ? "" : " LOCK IN SHARE MODE"),
            new { Id = id }, tx);
        if (template is null)
            throw new InvalidOperationException("The selected salary template no longer exists. Refresh Salary Templates and select an available template for this employee's client.");
        if (!template.Active)
            throw new InvalidOperationException("The selected salary template is inactive. Select an active template for this employee's client, or leave Salary template unassigned.");
        var owner = SelectedClientOwnershipStore.ResolveSalaryStructureOwner(template.ClientId, template.ClientRef, employee.ClientId);
        if (employee.ClientId <= 0 || owner is null || owner != 0 && owner != employee.ClientId)
            throw new InvalidOperationException("The selected salary template is not available for this employee's client. Select a template configured for this client, or leave Salary template unassigned.");
    }

    private sealed class SalaryAssignmentTemplate
    {
        public int ClientId { get; set; }
        public string ClientRef { get; set; } = "";
        public bool Active { get; set; }
    }
}
