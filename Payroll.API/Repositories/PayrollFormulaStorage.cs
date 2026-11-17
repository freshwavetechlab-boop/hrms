using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

// Existing formula strings, with the evaluator's unchanged AST/depth/node limits.
// This checks capacity before any write; it never runs schema DDL.
internal static class PayrollFormulaStorage
{
    internal const int LegacyLength = 1000;
    internal static IEnumerable<string> SetupRequestFormulas(ExcelPayrollSetupRequest request) =>
        request.Components.SelectMany(c => new[] { c.Formula }.Concat(c.RowFormulas.Values));

    internal static int RequiredLength(IEnumerable<string?> formulas)
    {
        var length = formulas.Select(f => f?.Length ?? 0).DefaultIfEmpty(0).Max();
        if (length > PayrollFormulaEvaluator.MaximumExpressionLength)
            throw new InvalidOperationException("Payroll formulas must not exceed 4096 characters.");
        return length;
    }

    internal static string[] MissingCapacity(IReadOnlyDictionary<string,long> lengths) =>
        new[] { "salarycomponents", "salarystructurelines" }
            .Where(table => !lengths.TryGetValue(table, out var length) || length < PayrollFormulaEvaluator.MaximumExpressionLength).ToArray();

    internal static async Task RequireAsync(MySqlConnection connection, MySqlTransaction? transaction, IEnumerable<string?> formulas)
    {
        if (RequiredLength(formulas) <= LegacyLength) return;
        var columns = await connection.QueryAsync<Capacity>(@"SELECT TABLE_NAME TableName,CHARACTER_MAXIMUM_LENGTH MaximumLength
FROM information_schema.columns WHERE table_schema=DATABASE() AND COLUMN_NAME='Formula'
AND TABLE_NAME IN ('salarycomponents','salarystructurelines')
AND DATA_TYPE IN ('varchar','text','mediumtext','longtext')", transaction: transaction);
        if (MissingCapacity(columns.ToDictionary(c => c.TableName, c => c.MaximumLength, StringComparer.OrdinalIgnoreCase)).Length > 0)
            throw new InvalidOperationException("Expanded payroll formulas require the reviewed 4096-character Formula storage migration. No formula may be truncated; no setup or salary changes were saved.");
    }

    internal static IEnumerable<string> SetupJsonFormulas(JsonObject root)
    {
        static JsonNode? Field(JsonObject row, string name) => row.FirstOrDefault(p => p.Key.Equals(name,StringComparison.OrdinalIgnoreCase)).Value;
        foreach (var component in (root["salaryComponents"] as JsonArray ?? []).OfType<JsonObject>())
            yield return Field(component,"formula")?.ToString() ?? "";
        foreach (var structure in (root["salaryStructures"] as JsonArray ?? []).OfType<JsonObject>())
            foreach (var line in (Field(structure,"lines") as JsonArray ?? []).OfType<JsonObject>())
                yield return Field(line,"formula")?.ToString() ?? "";
    }
    private sealed class Capacity { public string TableName { get; set; } = ""; public long MaximumLength { get; set; } }
}
