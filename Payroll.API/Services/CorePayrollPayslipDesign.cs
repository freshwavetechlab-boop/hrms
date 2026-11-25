using System.Security.Cryptography;
using System.Text.Json;
using Payroll.API.Models;

namespace Payroll.API.Services;

// A portable design is presentation metadata only. Its hash binds the target
// client and schema; it never authenticates Excel salary or employee values.
internal static class CorePayrollPayslipDesign
{
    internal const string SourceKind = "CorePayroll";
    internal static string SchemaHash(int clientId, IReadOnlyList<PayslipDesignField> fields,
        bool simpleLayout, bool keepMappedZeroRows)
    {
        if (clientId <= 0 || fields is null || fields.Count is < 1 or > 100
            || fields.Any(field => field is null || string.IsNullOrWhiteSpace(field.Label) || field.Label.Length > 150
                || field.Kind is not ("info" or "earning" or "deduction" or "employer"))
            || fields.Select(field => field.SourceColumn).Distinct().Count() != fields.Count)
            throw new InvalidOperationException("Choose a client and a bounded set of unique presentation fields.");
        var schema = new { Version = 1, SourceKind, ClientId = clientId, SimpleLayout = simpleLayout,
            KeepMappedZeroRows = keepMappedZeroRows,
            Fields = fields.OrderBy(field => field.SourceColumn).Select(field => new { field.SourceColumn, field.Label, field.Kind }) };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(schema,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))).ToLowerInvariant();
    }
}
