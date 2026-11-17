using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;

namespace Payroll.API.Services;

// Authenticated small provenance links retain the original future assignment.
internal static class ReviewedLocationCorrectionOrigins
{
    // Existing assignment storage is VARCHAR(20); descriptive receipt/witness
    // kinds remain separate from this bounded persistence token.
    internal const string Status = "ReviewedPeriod";
    private const string Prefix = "excel_location_origin:";
    internal sealed record Origin(int ClientId, int EmployeeId, string Month, string AssignmentId,
        string PreviousAssignmentId, string PartKey, string ReceiptId, string SourceHash);
    private sealed record Envelope(int Version, string Payload);
    private sealed class Stored { public string Code { get; set; } = ""; public string Json { get; set; } = ""; }
    private static PortableIntegrationCredentialProtector Protector(IConfiguration configuration) => new(configuration,
        new EphemeralDataProtectionProvider(), NullLogger<PortableIntegrationCredentialProtector>.Instance);
    internal static async Task SaveAsync(MySqlConnection db, MySqlTransaction tx, IConfiguration configuration, Origin origin)
    {
        var json = JsonSerializer.Serialize(new Envelope(1, Protector(configuration).ProtectLocationCorrection(JsonSerializer.Serialize(origin))));
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@ClientId,@Code,TRUE,CAST(@Json AS JSON))",
            new { origin.ClientId, Code = Prefix + origin.AssignmentId, Json = json }, tx);
    }
    internal static async Task<IReadOnlyDictionary<string, Origin>> ReadAsync(MySqlConnection db, MySqlTransaction tx,
        IConfiguration configuration, int clientId, IReadOnlyList<string> assignmentIds)
    {
        if (assignmentIds.Count == 0) return new Dictionary<string, Origin>();
        var protector = Protector(configuration); var output = new Dictionary<string, Origin>(StringComparer.Ordinal);
        var stored = await db.QueryAsync<Stored>("SELECT ModuleCode Code,CAST(SettingsJson AS CHAR) Json FROM modulesettings WHERE client_id=@ClientId AND ModuleCode IN @Codes AND IsEnabled=TRUE",
            new { ClientId = clientId, Codes = assignmentIds.Distinct().Select(id => Prefix + id).ToArray() }, tx);
        foreach (var row in stored)
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(row.Json);
            var plaintext = envelope is { Version: 1 } ? protector.UnprotectLocationCorrection(envelope.Payload) : null;
            var origin = plaintext is null ? null : JsonSerializer.Deserialize<Origin>(plaintext);
            if (origin is null || origin.ClientId != clientId || row.Code != Prefix + origin.AssignmentId || !assignmentIds.Contains(origin.AssignmentId, StringComparer.Ordinal)
                || !Guid.TryParseExact(origin.PreviousAssignmentId, "N", out _) || !Guid.TryParseExact(origin.ReceiptId, "N", out _)
                || origin.EmployeeId <= 0 || string.IsNullOrWhiteSpace(origin.PartKey) || origin.PartKey.Length > 100
                || !DateTime.TryParseExact(origin.Month+"-01","yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out _)
                || !Regex.IsMatch(origin.SourceHash??"","\\A[a-f0-9]{64}\\z"))
                throw new InvalidOperationException("Period-only location correction provenance cannot be authenticated; future salary is not inferred.");
            output.Add(origin.AssignmentId, origin);
        }
        if (output.Count != assignmentIds.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidOperationException("A period-only correction is missing its original assignment proof; future salary defaults were not changed.");
        foreach(var origin in output.Values)
        {
            var saved=await db.ExecuteScalarAsync<string?>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Code AND IsEnabled=TRUE",
                new{ClientId=clientId,Code=Repositories.ExcelPayrollSetupRepository.LocationCorrectionReceiptPrefix+origin.ReceiptId},tx);
            JsonNode? receipt=null;
            try
            {
                var envelope=saved is null?null:JsonNode.Parse(saved);
                var plaintext=envelope?["version"]?.GetValue<int>()==1?protector.UnprotectLocationCorrection(envelope["payload"]!.GetValue<string>()):null;
                receipt=plaintext is null?null:JsonNode.Parse(plaintext);
            }
            catch(Exception failure)when(failure is JsonException or InvalidOperationException or FormatException){ }
            var proposed=receipt?["changes"]?["allocations"]?.AsArray();
            var previous=receipt?["previous"]?["storage"]?["allocations"]?.AsArray();
            if(receipt?["clientId"]?.GetValue<int>()!=clientId||receipt?["kind"]?.ToString() is not("AppliedLocationCorrection" or "OrphanedLocationRecovery")
                ||receipt?["result"]?["receiptId"]?.ToString()!=origin.ReceiptId
                ||proposed is null||!proposed.Any(part=>part?["employeeId"]?.GetValue<int>()==origin.EmployeeId&&part?["payPeriod"]?.ToString()==origin.Month
                    &&part?["sourcePartKey"]?.ToString()==origin.PartKey&&part?["assignmentId"]?.ToString()==origin.AssignmentId&&part?["sourceHash"]?.ToString()==origin.SourceHash)
                ||previous is null||!previous.Any(part=>part?["employeeId"]?.GetValue<int>()==origin.EmployeeId&&part?["payPeriod"]?.ToString()==origin.Month
                    &&part?["sourcePartKey"]?.ToString()==origin.PartKey&&part?["assignmentId"]?.ToString()==origin.PreviousAssignmentId))
                throw new InvalidOperationException("The new period correction receipt is missing, corrupted or differently scoped; no payroll fallback is accepted.");
        }
        return output;
    }
}
