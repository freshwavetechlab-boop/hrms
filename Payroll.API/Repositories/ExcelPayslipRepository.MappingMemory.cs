using System.Text.Json;
using Dapper;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    // Read only the selected client's explicit saved mappings, never salary batches or other clients.
    public async Task<IReadOnlyList<ExcelPayslipMappingExample>> GetMappingExamplesAsync(int clientId, CancellationToken ct)
    {
        await using var db = Db(); await db.OpenAsync(ct);
        var records = await db.QueryAsync<string>(new CommandDefinition(
            "SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@ClientId AND ModuleCode IN (@Code,@LegacyCode) ORDER BY ModuleCode=@Code DESC LIMIT 2",
            new { ClientId = clientId, Code = MappingProfileStorageCode(clientId), LegacyCode = ProfileCode },
            commandTimeout: 5, cancellationToken: ct));
        return ReadMappingExamples(records);
    }

    internal static IReadOnlyList<ExcelPayslipMappingExample> ReadMappingExamples(IEnumerable<string> records)
    {
        var found = new Dictionary<string, ExcelPayslipMappingExample>();
        foreach (var text in records)
        {
            if (text.Length > 4 * 1024 * 1024) continue;
            try
            {
                using var json = JsonDocument.Parse(text);
                if (json.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var profile in json.RootElement.EnumerateObject().Take(50))
                {
                    if (found.ContainsKey(profile.Name) || ValidateProfile(profile.Value) is not null) continue;
                    var columns = profile.Value.GetProperty("columns").Deserialize<List<ExcelPayslipMappingColumn>>(JsonOptions);
                    if (columns is { Count: > 0 and <= 128 }) found[profile.Name] = new(profile.Name, columns);
                }
            }
            catch (JsonException) { /* A legacy malformed preset is not mapping knowledge. */ }
        }
        return found.Values.Take(50).ToArray();
    }
}

public sealed record ExcelPayslipMappingExample(string Signature, IReadOnlyList<ExcelPayslipMappingColumn> Columns);
