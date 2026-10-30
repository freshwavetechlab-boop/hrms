using System.Globalization;
using System.Text.Json;

namespace Payroll.API.Services;

// The owner chooses a saved payroll source as salary-payable authority for one period.
// Physical presence and the dated ledger remain separate, and are never inferred here.
public sealed record SourcePayableOverrideEvidence(int Version, int ClientId, int EmployeeId, string Month,
    string BatchId, string RowId, string ImportId, string SourceHash, int PayrollDays, decimal PayableDays,
    decimal PresentDays, string CycleStart, string CycleEnd);

public static class SourcePayableOverride
{
    public const string SourceType = "ExcelPayableOverride";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Encode(SourcePayableOverrideEvidence evidence)
    {
        Validate(evidence, evidence.ClientId, evidence.EmployeeId, evidence.Month, evidence.PayrollDays, evidence.PayableDays, evidence.PresentDays);
        var text = JsonSerializer.Serialize(evidence, Json);
        if (text.Length > 600) throw new InvalidOperationException("Source payable override provenance exceeds its storage limit.");
        return text;
    }
    public static SourcePayableOverrideEvidence Read(string text, int clientId, int employeeId, string month,
        int payrollDays, decimal payable, decimal? present)
    {
        SourcePayableOverrideEvidence? evidence;
        try { evidence = JsonSerializer.Deserialize<SourcePayableOverrideEvidence>(text, Json); }
        catch (JsonException) { throw new InvalidOperationException("Saved Excel payable override provenance is invalid."); }
        if (evidence is null) throw new InvalidOperationException("Saved Excel payable override provenance is missing.");
        Validate(evidence, clientId, employeeId, month, payrollDays, payable, present);
        return evidence;
    }
    private static void Validate(SourcePayableOverrideEvidence e, int clientId, int employeeId, string month,
        int payrollDays, decimal payable, decimal? present)
    {
        if (e.Version != 1 || e.ClientId <= 0 || e.EmployeeId <= 0 || e.ClientId != clientId || e.EmployeeId != employeeId
            || e.Month != month || !DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || !Guid.TryParse(e.BatchId, out _) || !Guid.TryParse(e.ImportId, out _) || string.IsNullOrWhiteSpace(e.RowId)
            || e.SourceHash.Length != 64 || !e.SourceHash.All(Uri.IsHexDigit)
            || e.PayrollDays is < 1 or > 31 || e.PayrollDays != payrollDays || payable < 0 || payable > payrollDays || e.PayableDays != payable
            || !present.HasValue || e.PresentDays != present.Value || present < 0 || present > payrollDays
            || !DateOnly.TryParseExact(e.CycleStart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !DateOnly.TryParseExact(e.CycleEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
            || end.DayNumber - start.DayNumber is < 0 or > 30)
            throw new InvalidOperationException("Excel payable override does not match its reviewed employee, period or source inputs.");
    }
}
