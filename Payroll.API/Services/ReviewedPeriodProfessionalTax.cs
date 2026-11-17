using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

internal static class ReviewedPeriodProfessionalTax
{
    internal const string Prefix = "payroll.pt.reviewed:";
    internal const string ReceiptPrefix = "payroll.pt.receipt:";
    internal const string Reviewed = "ReviewedAmount", Automatic = "Automatic";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static string Key(int employeeId, string month) => Prefix + employeeId + ":" + month;
    internal static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json))));
    internal static bool ValidHash(string? value) => Regex.IsMatch(value ?? "", "^[a-fA-F0-9]{64}$");
    internal static bool ValidMonth(string? month) => DateOnly.TryParseExact((month ?? "") + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    internal static bool IsPt(string code, string statutoryType) => code.Equals("PT", StringComparison.OrdinalIgnoreCase)
        || statutoryType.Equals("Professional Tax", StringComparison.OrdinalIgnoreCase);
    internal static void Validate(ReviewedProfessionalTaxRequest request, bool saving = false)
    {
        if (request.ClientId <= 0 || !ValidMonth(request.PayPeriod) || !Guid.TryParseExact(request.SourceBatchId, "N", out _)
            || request.SourceHash is null || request.SourceHash.Length > 64 || request.SourceHash.Length > 0 && !ValidHash(request.SourceHash)
            || saving && !ValidHash(request.SourceHash) || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500
            || request.Rows is null || request.Rows.Count is < 1 or > 100)
            throw new InvalidOperationException("Review one exact client, source batch, payroll month, reason and up to 100 existing employees.");
        if (request.Rows.Any(row => row is null || row.EmployeeId <= 0 || string.IsNullOrWhiteSpace(row.SourceRowId) || row.SourceRowId.Length > 100
                || row.Mode is not (Reviewed or Automatic) || row.Mode == Reviewed && row.Amount is null
                || row.Mode == Automatic && row.Amount is not null || row.Amount is < 0 or > 10000000m
                || row.Amount.HasValue && decimal.Round(row.Amount.Value, 2) != row.Amount.Value)
            || request.Rows.Select(row => row.EmployeeId).Distinct().Count() != request.Rows.Count
            || request.Rows.Select(row => row.SourceRowId).Distinct(StringComparer.Ordinal).Count() != request.Rows.Count)
            throw new InvalidOperationException("Each unique source row must match one existing employee. Choose Automatic with no amount, or an explicit reviewed amount including zero, to paise.");
    }
    internal static void RequireControl(ReviewedProfessionalTaxControl control, int clientId, int employeeId, string month)
    {
        if (control.Version != 1 || control.Kind != "ReviewedPeriodProfessionalTax" || control.ClientId != clientId
            || control.EmployeeId != employeeId || control.PayPeriod != month || !ValidMonth(month)
            || control.Mode is not (Reviewed or Automatic) || control.Mode == Reviewed && control.Amount is null
            || control.Mode == Automatic && control.Amount is not null || control.Amount is < 0 or > 10000000m
            || control.Amount.HasValue && decimal.Round(control.Amount.Value, 2) != control.Amount.Value
            || !Guid.TryParseExact(control.SourceBatchId, "N", out _) || !ValidHash(control.SourceHash)
            || !ValidHash(control.NativeBasisHash) || string.IsNullOrWhiteSpace(control.SourceRowId)
            || string.IsNullOrWhiteSpace(control.Reason) || control.ReviewedById <= 0 || string.IsNullOrWhiteSpace(control.ReviewedBy)
            || control.ReviewedAtUtc == default || !Guid.TryParse(control.RequestId, out _)
            || control.HistoricalReceiptId is not null && (!Guid.TryParseExact(control.HistoricalReceiptId,"N",out _) || control.Mode!=Reviewed || control.Amount!=0))
            throw new InvalidOperationException("Reviewed period Professional Tax evidence is invalid or belongs to another scope. No automatic fallback is substituted.");
    }
    internal static decimal? Resolve(ReviewedProfessionalTaxControl? control, int clientId, int employeeId, string month,
        string sourceHash, string nativeBasisHash, bool locationParts, decimal seededPt, bool positiveConfiguredPt, bool mixedPt, int configuredPtLines)
    {
        if (control is null) return null;
        RequireControl(control, clientId, employeeId, month);
        if (control.Mode == Automatic) return null;
        if (!string.Equals(sourceHash, control.SourceHash, StringComparison.Ordinal) || nativeBasisHash != control.NativeBasisHash)
            throw new InvalidOperationException("Source or native employee basis changed after period Professional Tax review. Review this exact source and month again.");
        if (locationParts) throw new InvalidOperationException("Reviewed period Professional Tax cannot replace effective location allocations. Review the source-part statutory scope online.");
        if (seededPt > 0 || positiveConfiguredPt || mixedPt || configuredPtLines > 1)
            throw new InvalidOperationException("A positive, mixed or duplicate existing Professional Tax input conflicts with this reviewed period amount. Resolve it explicitly; existing deductions are retained.");
        return control.Amount;
    }
}
