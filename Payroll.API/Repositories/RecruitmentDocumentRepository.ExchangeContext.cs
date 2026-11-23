using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentDocumentRepository
{
    private sealed class StandardComponent
    {
        public string Id { get; set; } = "";
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public string CalculationType { get; set; } = "";
        public bool Active { get; set; }
    }
    private sealed class StandardContext
    {
        public RosterExchangeTemplate Template { get; set; } = new();
        public List<RecruitmentDocumentRosterRow> Rows { get; set; } = [];
        public Dictionary<int, Employee> Employees { get; set; } = [];
        public List<WorkflowApprover> Managers { get; set; } = [];
        public List<StandardComponent> Components { get; set; } = [];
        public Dictionary<string, string> ComponentIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> BaselineComponentIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public EmployeeFieldExchange Custom { get; set; } = new([], [], []);
        public List<WorkLocation> Locations { get; set; } = [];
        public HashSet<int> AllowedLocations { get; set; } = [];
        public Dictionary<long, Dictionary<string, string>> Current { get; set; } = [];
        public Dictionary<long, string> Revisions { get; set; } = [];
    }

    internal static bool CanCorrectRosterMaster(AuthUser user) => user.Permissions.Any(p => p.Equals("employees.manage", StringComparison.OrdinalIgnoreCase)
        || p.Equals("settings.manage", StringComparison.OrdinalIgnoreCase));

    private static void StandardAccess(RosterExchangeSettings settings, AuthUser user)
    {
        Access(user, settings.ClientId);
        if (settings.ClientId <= 0 || settings.TemplateId <= 0 || settings.Mode is not ("Update" or "Insert") || settings.SourceGroup is null || settings.SourceGroup.Length > 180 || settings.SourceGroup.Any(char.IsControl))
            throw new InvalidOperationException("Choose Insert or Update and a valid import batch.");
        if (settings.Mode == "Update" && !CanCorrectRosterMaster(user)) throw new UnauthorizedAccessException("Employee Master management permission is required for corrected versions.");
        if (settings.Mode == "Insert") IntakeAccess(user);
    }

    private async Task<StandardContext> StandardContextAsync(MySqlConnection db, RosterExchangeSettings settings, AuthUser user)
    {
        StandardAccess(settings, user);
        var clientName = await db.QuerySingleOrDefaultAsync<string>("SELECT Name FROM clients WHERE Id=@ClientId AND IsActive=TRUE", settings)
            ?? throw new InvalidOperationException("Select an active client.");
        _ = await TemplateAsync(db, settings.ClientId, settings.TemplateId);
        var context = new StandardContext();
        context.AllowedLocations = (await RecruitmentAccessScope.WorkLocationsAsync(db, user, settings.ClientId)).Select(l => l.Id).ToHashSet();
        context.Locations = (await db.QueryAsync<WorkLocation>("SELECT * FROM worklocations WHERE ClientId=@ClientId", settings)).ToList();
        var allRows = (await db.QueryAsync<RecruitmentDocumentRosterRow>(RosterJoiningRowsSql.Replace("r.TemplateId,r.WorkLocationId", "r.TemplateId,r.SourceGroup,r.WorkLocationId")
            + " WHERE r.ClientId=@ClientId AND r.TemplateId=@TemplateId AND c.ProfileStatus<>'Archived' AND (r.EmployeeId IS NULL OR c.EmployeeId IS NULL OR r.EmployeeId=c.EmployeeId) AND (COALESCE(r.EmployeeId,c.EmployeeId) IS NULL OR e.Id IS NOT NULL) ORDER BY r.Id", settings)).ToList();
        allRows = allRows.Where(row => StandardCanSeeLocation(user, row.WorkLocationId, context.AllowedLocations)).ToList();
        var groups = allRows.Select(row => row.SourceGroup).Distinct().OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
        context.Rows = allRows.Where(row => settings.SourceGroup.Length == 0 || row.SourceGroup == settings.SourceGroup).ToList();
        context.Employees = (await EmployeeRepository.ReadExcelPayrollIdentitySnapshotAsync(db, null, settings.ClientId)).ToDictionary(e => e.Id);
        context.Managers = (await db.QueryAsync<WorkflowApprover>("SELECT Id,Email,ClientId FROM authusers WHERE IsActive=TRUE AND (ClientId IS NULL OR ClientId=@ClientId)", settings)).ToList();
        allRows = allRows.Where(row => row.EmployeeId is null || context.Employees.TryGetValue(row.EmployeeId.Value, out var master)
            && StandardCanSeeLocation(user, master.WorkLocationId, context.AllowedLocations)).ToList();
        groups = allRows.Select(row => row.SourceGroup).Distinct().OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
        context.Rows = context.Rows.Where(row => row.EmployeeId is null || context.Employees.TryGetValue(row.EmployeeId.Value, out var employee)
            && StandardCanSeeLocation(user, employee.WorkLocationId, context.AllowedLocations)).ToList();
        context.Components = (await db.QueryAsync<StandardComponent>("SELECT CAST(Id AS CHAR) Id,Code,Name,Category,CalculationType,Active FROM salarycomponents ORDER BY Priority,Id")).ToList();
        context.BaselineComponentIds = context.Components.ToDictionary(c => "SALARY:" + c.Code.ToUpperInvariant(), c => c.Id, StringComparer.OrdinalIgnoreCase);
        var forms = await EmployeeAttributeRepository.ExchangeFormsAsync(db, settings.ClientId);
        var customColumns = EmployeeAttributeRepository.Columns(forms);
        context.Custom = settings.Mode == "Update" && customColumns.Count > 0
            ? await new EmployeeAttributeRepository(configuration).ExchangeAsync(settings.ClientId, user)
            : new(forms, customColumns, []);
        var columns = StandardRosterColumns();
        var revision = columns.Single(c => c.Key == "revision"); columns.Remove(revision);
        columns.AddRange(customColumns.Select(c => new RosterExchangeColumn(c.Code, c.Header, c.InfotypeCode,
            c.Field.FieldTypeCode == "DATE" ? "date" : "text", RequiredOnInsert: c.Field.IsRequired,
            Description: c.Field.FieldTypeCode == "MULTI_SELECT" ? "Separate option codes with a semicolon." : string.Join(", ", c.Field.Options.Where(o => o.IsActive).Select(o => o.OptionCode)))));
        foreach (var component in context.Components.Where(c => StandardRosterSalaryInput(c.Code, c.Active)))
        {
            const string key = "SALARY:BASIC";
            columns.Add(new(key, "Basic Salary INR/month [SALARY:BASIC]", "0008", "money", Description: "Employee's monthly Basic input. Salary templates calculate allowances, increments and other pay components."));
            context.ComponentIds[key] = component.Id;
        }
        columns.Add(revision);
        context.Template = new() { ClientId = settings.ClientId, ClientName = clientName, TemplateId = settings.TemplateId, SourceGroup = settings.SourceGroup, Columns = columns, ImportBatches = groups };
        foreach (var row in context.Rows)
        {
            var employee = row.EmployeeId.HasValue ? context.Employees.GetValueOrDefault(row.EmployeeId.Value) : null;
            var custom = employee is null ? null : context.Custom.Values.GetValueOrDefault(employee.Id);
            var values = StandardCurrentValues(row, employee, custom, context);
            var rowRevision = StandardRosterRevision(row, employee, custom);
            values["revision"] = rowRevision; context.Revisions[row.Id] = rowRevision; context.Current[row.Id] = values;
            if (settings.Mode == "Update") context.Template.Rows.Add(columns.ToDictionary(c => c.Key, c => values.GetValueOrDefault(c.Key, ""), StringComparer.OrdinalIgnoreCase));
        }
        return context;
    }

    private static bool StandardCanSeeLocation(AuthUser user, int? locationId, IReadOnlySet<int> allowed) =>
        !RecruitmentAccessScope.IsRestricted(user) || locationId.HasValue && allowed.Contains(locationId.Value);

    private static Dictionary<string, string> StandardCurrentValues(RecruitmentDocumentRosterRow row, Employee? employee, IReadOnlyDictionary<string, string>? custom, StandardContext context)
    {
        var values = ReadValues(row.ValuesJson);
        var name = row.CandidateName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (!values.ContainsKey("firstName")) values["firstName"] = name.FirstOrDefault() ?? "";
        if (!values.ContainsKey("lastName")) values["lastName"] = name.ElementAtOrDefault(1) ?? "";
        values["candidateEmail"] = row.Email; values["candidatePhone"] = row.Phone;
        if (employee is null)
        {
            foreach (var (target, alias) in new[] { ("bankAccountNo", "accountNo"), ("ifscCode", "ifsc"), ("panNumber", "pan"), ("aadhaarNumber", "aadhaar"), ("uanNumber", "uan") })
                if (!values.ContainsKey(target)) values[target] = values.GetValueOrDefault(alias, "");
            foreach (var key in new[] { "city", "state" }) values[key] = values.GetValueOrDefault("employee" + char.ToUpperInvariant(key[0]) + key[1..], "");
            var pendingSalary = values.TryGetValue("salaryComponentsJson", out var json) ? JsonSerializer.Deserialize<Dictionary<string, decimal>>(json, Json) ?? [] : [];
            foreach (var component in context.Components)
                if (pendingSalary.TryGetValue(component.Id, out var amount)) values["SALARY:" + component.Code.ToUpperInvariant()] = amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var pair in RosterMonthlySalary(values))
                if (!values.ContainsKey("SALARY:" + pair.Key)) values["SALARY:" + pair.Key] = pair.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (employee is not null)
        {
            foreach (var pair in StandardEmployeeValues(employee)) values[pair.Key] = pair.Value;
            foreach (var component in context.Components) values["SALARY:" + component.Code.ToUpperInvariant()] = employee.SalaryComponents.TryGetValue(component.Id, out var amount) ? amount.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            var copy = JsonSerializer.Deserialize<RecruitmentDocumentRosterRow>(JsonSerializer.Serialize(row, Json), Json)!;
            RecruitmentEmployeeMasterSync.Apply(copy, employee, context.Template.Columns.Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase), context.Components.ToDictionary(c => c.Id, c => c.Code), context.Components.Where(c => c.Category.Equals("Earning", StringComparison.OrdinalIgnoreCase)).Select(c => c.Id).ToHashSet());
            var projected = ReadValues(copy.ValuesJson);
            if (projected.TryGetValue("monthlyGross", out var gross)) values["monthlyGross"] = gross;
        }
        if (custom is not null) foreach (var pair in custom) values[pair.Key] = pair.Value;
        var managerId = employee?.ReportingManagerUserId ?? (int.TryParse(values.GetValueOrDefault("reportingManagerUserId", ""), out var pendingManager) ? pendingManager : 0);
        values["reportingManagerEmail"] = managerId > 0 ? context.Managers.SingleOrDefault(m => m.Id == managerId)?.Email ?? "" : values.GetValueOrDefault("reportingManagerEmail", "");
        var location = context.Locations.SingleOrDefault(l => l.Id == (employee?.WorkLocationId is > 0 ? employee.WorkLocationId : row.WorkLocationId));
        if (location is not null)
        {
            values["workLocation"] = location.Name; values["workLocationAddress"] = location.Address; values["workLocationCity"] = location.City;
            values["workLocationState"] = location.State; values["workLocationPostalCode"] = location.PostalCode;
        }
        foreach (var column in context.Template.Columns.Where(c => c.Key.StartsWith("OFFER:", StringComparison.OrdinalIgnoreCase))) values[column.Key] = values.GetValueOrDefault(column.Key[6..], "");
        values["profileCode"] = row.CandidateCode; values["rowAction"] = "Apply"; values["changeReason"] = "";
        return values; // Full private baseline retains omitted pay; exports use only the explicit columns above.
    }

    public async Task<RosterExchangeTemplate> StandardTemplateAsync(RosterExchangeSettings settings, AuthUser user)
    {
        StandardAccess(settings, user);
        await using var db = Db(); await db.OpenAsync();
        var context = await StandardContextAsync(db, settings, user);
        if (settings.Mode == "Update" && settings.SourceGroup.Length == 0 && context.Template.ImportBatches.Count > 1)
            throw new InvalidOperationException("Select the import batch to download its corrected version.");
        if (settings.Mode == "Insert") context.Template.Rows = [];
        return context.Template;
    }
}
