using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class SettingsRepository
{
    public async Task<JsonObject> CreateClientSalaryTemplateAsync(int clientId, SalaryTemplateCreateRequest request)
    {
        if (clientId <= 0 || request is null || string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200
            || request.Lines is null || request.Lines.Count is < 1 or > 100 || request.AnnualCtc is null
            || !decimal.TryParse(string.IsNullOrWhiteSpace(request.AnnualCtc) ? "0" : request.AnnualCtc,
                NumberStyles.Number, CultureInfo.InvariantCulture, out var annualCtc) || annualCtc is < 0 or > 1000000000m)
            throw new InvalidOperationException("Select a client, name the template and add up to 100 valid component lines.");

        await using var db = Connection(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.Serializable);
        if (await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@clientId AND IsActive=TRUE FOR UPDATE", new { clientId }, tx) is null)
            throw new InvalidOperationException("The selected client is unavailable.");
        // Reuse the existing scoped writer and its lock order. The command is
        // append-only; it never writes masters, existing templates or payroll.
        await db.QueryAsync<long>("SELECT Id FROM payrollsetups ORDER BY Id FOR UPDATE", transaction: tx);
        await db.QueryAsync<long>("SELECT Id FROM salarystructures ORDER BY Id FOR UPDATE", transaction: tx);
        await db.QueryAsync<long>("SELECT Id FROM salarystructurelines ORDER BY Id FOR UPDATE", transaction: tx);
        await db.QueryAsync<long>("SELECT Id FROM paysliptemplates ORDER BY Id FOR UPDATE", transaction: tx);
        var before = JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db, tx))!.AsObject();
        before["salaryStructures"] ??= new JsonArray();
        var oldRows = before["salaryStructures"]!.AsArray().OfType<JsonObject>().ToArray();
        var name = request.Name.Trim();
        if (oldRows.Any(row => ReadClientRefs(row) is { } ids && (ids.Count == 0 || ids.Contains(clientId))
                && string.Equals(ReadProperty(row, "name")?.ToString().Trim(), name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A salary template with this name already exists for this client. Choose another name; the saved template has not been overwritten.");
        ValidateNewTemplateLines(request.Lines, before["salaryComponents"] as JsonArray ?? []);
        var lastId = oldRows.Select(ReadRowId).DefaultIfEmpty(0).Max();
        var id = await PayrollDataTableStore.NextSalaryTemplateIdAsync(db, tx, lastId);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var template = JsonSerializer.SerializeToNode(new
        {
            id, clientId = clientId.ToString(CultureInfo.InvariantCulture), name,
            annualCtc = decimal.Round(annualCtc, 2).ToString("0.00", CultureInfo.InvariantCulture), request.Active, request.Lines
        }, json)!.AsObject();
        var after = before.DeepClone().AsObject();
        after["salaryStructures"]!.AsArray().Add(template.DeepClone());
        await PayrollDataTableStore.SaveSelectedClientSetupJsonAsync(db, tx, clientId, before, after, singleStructureAppendOnly: true);

        var stored = JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db, tx))!.AsObject();
        var saved = stored["salaryStructures"]!.AsArray().OfType<JsonObject>().Single(row => ReadRowId(row) == id);
        if (!JsonNode.DeepEquals(saved, template)) throw new InvalidOperationException("The template did not retain its exact configuration. No changes were saved.");
        var result = saved.DeepClone().AsObject();
        stored["salaryStructures"]!.AsArray().Remove(saved);
        if (!JsonNode.DeepEquals(before, stored)) throw new InvalidOperationException("Another setup record changed. The template save was rolled back.");
        await tx.CommitAsync();
        return result;
    }

    private static void ValidateNewTemplateLines(List<SalaryTemplateCreateLine> lines, JsonArray masters)
    {
        var components = masters.OfType<JsonObject>().ToDictionary(ReadRowId);
        var seen = new HashSet<long>();
        foreach (var line in lines)
        {
            if (line is null || !long.TryParse(line.ComponentId, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || id <= 0 || line.ComponentId != id.ToString(CultureInfo.InvariantCulture) || !seen.Add(id)
                || !components.TryGetValue(id, out var component) || component["active"]?.GetValue<bool>() != true)
                throw new InvalidOperationException("Use each active, saved salary component at most once. Create new component masters through their existing configuration flow.");
            if (line.Value is null || line.Value.Length > 1000 || line.Formula is null || line.Formula.Length > 4096
                || line.CalculationType is null || line.CalculationType.Length > 80 || line.BaseComponent is null || line.BaseComponent.Length > 80
                || line.ProRataOverride is null || line.ProRataOverride.Length > 20 || line.RoundingMode is null || line.RoundingMode.Length > 40)
                throw new InvalidOperationException("A salary template line contains invalid or oversized calculation fields.");
            var masterType = component["calculationType"]?.ToString() ?? "";
            var type = string.IsNullOrWhiteSpace(line.CalculationType) ? masterType : line.CalculationType;
            if ((masterType == "Daily Rate") != (type == "Daily Rate"))
                throw new InvalidOperationException("Existing salary components cannot be converted between monthly and daily rates. Create a separate component to preserve employee salary amounts.");
            var source = new[] { line.Formula, line.Value, component["formula"]?.ToString(), component["value"]?.ToString() }.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
            if (type == "Daily Rate" && (component["category"]?.ToString() != "Earning"
                || !decimal.TryParse(source, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate) || rate <= 0))
                throw new InvalidOperationException("A Daily Rate component must be an earning with a positive numeric daily rate.");
            if (type == "Formula") PayrollFormulaEvaluator.Validate(source);
        }
    }
}
