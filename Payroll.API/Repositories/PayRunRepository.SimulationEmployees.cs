using System.Data;
using System.Globalization;
using Dapper;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    internal static void ValidateSimulationEmployeeScope(int clientId, string? templateId, string? payPeriod)
    {
        if (clientId <= 0 || string.IsNullOrWhiteSpace(templateId) || templateId.Length > 40
            || payPeriod?.Length != 7 || !DateOnly.TryParseExact(payPeriod + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new InvalidOperationException("Select a salary template and a valid pay month before choosing employees.");
    }

    internal static bool MatchesSimulationTemplate(string? effectiveTemplateId, IEnumerable<string>? locationTemplateIds, string selectedTemplateId) =>
        locationTemplateIds is null ? effectiveTemplateId == selectedTemplateId : locationTemplateIds.Contains(selectedTemplateId, StringComparer.Ordinal);

    public async Task<IReadOnlyList<object>> SimulationEmployeesAsync(int clientId, string? search, string? templateId, string? payPeriod, CancellationToken token)
    {
        // Existing callers retain the original active-employee lookup.
        if (templateId is null && payPeriod is null) return await SimulationEmployeesAsync(clientId, search, token);
        ValidateSimulationEmployeeScope(clientId, templateId, payPeriod);
        await using var db = CreateConnection();
        await db.OpenAsync(token);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, isReadOnly: true, token);
        var setup = ReadPayrollSetup(await PayrollDataTableStore.GetSetupJsonAsync(db, tx));
        var clientReference = clientId.ToString(CultureInfo.InvariantCulture);
        if (!setup.Structures.Any(s => s.Active && s.Id == templateId && s.ClientId.Split([';', ',', '|'])
                .Any(v => v.Split(':')[0].Trim() == clientReference)))
            throw new InvalidOperationException("This salary template is not available for the selected client.");
        var output = new List<object>();
        var searchText = search?.Trim() ?? "";
        const int pageSize = 200;
        // Resolve assignments before the result limit, including employees beyond
        // the first search page. The same native loader resolves dated IT0008 and
        // reviewed location-part assignments used by employee simulation.
        for (var offset = 0; output.Count < 50; offset += pageSize)
        {
            token.ThrowIfCancellationRequested();
            var employees = (await db.QueryAsync<PayRunSourceEmployee>(new CommandDefinition(@"
SELECT e.*, c.Name ClientName, COALESCE(w.State,'') WorkState
FROM employees e JOIN clients c ON c.Id=e.ClientId
LEFT JOIN worklocations w ON w.Id=e.WorkLocationId
WHERE e.ClientId=@ClientId AND " + EmployeeEmploymentRules.EligibleInMonth("e", "@PayPeriod") + @"
 AND (@Search='' OR e.EmployeeCode LIKE @Pattern OR CONCAT(e.FirstName,' ',e.LastName) LIKE @Pattern)
ORDER BY e.EmployeeCode,e.Id LIMIT @PageSize OFFSET @Offset", new { ClientId = clientId, PayPeriod = payPeriod, Search = searchText, Pattern = "%" + searchText + "%", PageSize = pageSize, Offset = offset }, tx, cancellationToken: token))).ToList();
            await LoadEmployeeTablesAsync(db, tx, employees, payPeriod!, loadReviewedProfessionalTax: false);
            foreach (var employee in employees)
            {
                if (!MatchesSimulationTemplate(employee.SalaryStructureId, employee.LocationParts?.Parts.Select(p => p.SalaryStructureId), templateId!)) continue;
                output.Add(new { id = employee.Id, code = employee.EmployeeCode, name = $"{employee.FirstName} {employee.LastName}".Trim() });
                if (output.Count == 50) break;
            }
            if (employees.Count < pageSize) break;
        }
        await tx.RollbackAsync(token);
        return output;
    }
}
