using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

// A new current-source/current-core witness may reference an unchanged AppliedV0
// receipt. It never creates a TransferV1 receipt or waives missing evidence.
internal static class SelectedPayRunAppliedSourceReview
{
    internal const string Kind = "ReviewedAppliedSourceV1";
    private static readonly Regex Fingerprint = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);

    internal static string Hash(object? value)
    {
        var element = JsonSerializer.SerializeToElement(value);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, element);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = element.EnumerateObject().ToArray();
                if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                    throw Conflict("A review fingerprint contains duplicate object keys.");
                foreach (var property in properties.OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                if (!element.TryGetDecimal(out var number))
                    throw Conflict("A review fingerprint contains an unsupported numeric value.");
                writer.WriteRawValue(number.ToString("G29", CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.String: writer.WriteStringValue(element.GetString()); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw Conflict("A review fingerprint contains an undefined value.");
        }
    }

    internal static string ScopeFingerprint(CreatePayRunRequest request)
    {
        RequireBoundedScope(request);
        var groups = SelectedPayRunDraft.Groups(request).Order().ToArray();
        return Hash(new
        {
            request.ClientId, request.PayPeriod, request.PayDate, request.TotalWorkingDays, request.RunType,
            RunName = string.IsNullOrWhiteSpace(request.RunName) ? "Selected regular payroll" : request.RunName.Trim(),
            Reason = request.Reason?.Trim() ?? "", AttendancePolicyBatchId = request.AttendancePolicyBatchId.Trim(),
            AttendanceGroupIds = groups, IncludedEmployeeIds = request.IncludedEmployeeIds.Order().ToArray(),
            AdjustmentIds = request.AdjustmentIds.Order().ToArray(), ExcludedEmployeeIds = Array.Empty<int>()
        });
    }

    internal static SelectedAppliedSourceWitness BuildWitness(CreatePayRunRequest request,
        string originalReceiptFingerprint, string sourceHash, string mappingFingerprint,
        string identityFingerprint, string sourceFormulaFingerprint, string nativeInputFingerprint,
        string salaryPolicyFingerprint, string coreCalculationFingerprint, string comparisonFingerprint,
        string businessFingerprint)
    {
        var hashes = new[] { originalReceiptFingerprint, sourceHash, mappingFingerprint, identityFingerprint,
            sourceFormulaFingerprint, nativeInputFingerprint, salaryPolicyFingerprint,
            coreCalculationFingerprint, comparisonFingerprint, businessFingerprint };
        if (hashes.Any(hash => !ValidHash(hash)))
            throw Conflict("A complete current source, identity, native, salary/policy, calculation and business proof is required.");
        var witnessHash = Hash(new
        {
            Kind, HistoricalTransferVersion = 0, ScopeFingerprint = ScopeFingerprint(request),
            originalReceiptFingerprint, sourceHash, mappingFingerprint, identityFingerprint,
            sourceFormulaFingerprint, nativeInputFingerprint, salaryPolicyFingerprint,
            coreCalculationFingerprint, comparisonFingerprint, businessFingerprint
        });
        return new(Kind, 0, originalReceiptFingerprint, sourceHash, mappingFingerprint, identityFingerprint,
            sourceFormulaFingerprint, nativeInputFingerprint, salaryPolicyFingerprint,
            coreCalculationFingerprint, comparisonFingerprint, witnessHash);
    }

    internal static void RequireAcknowledged(CreatePayRunRequest request, SelectedPayRunAdjustmentReview review, int actorId)
    {
        var witness = RequireCurrentWitness(request, review);
        if (actorId <= 0 || request.SourceReviewAttestation is not null
            || request.AdjustmentSourceReviewAcknowledgement is not { Confirmed: true } acknowledgement
            || !SameHash(acknowledgement.ExpectedFingerprint, witness.WitnessFingerprint))
            throw Conflict("Explicitly confirm this exact current source review before creating the held draft. Server attestations cannot be supplied by the caller.");
    }

    internal static void RequireStoredAttestation(CreatePayRunRequest request, SelectedPayRunAdjustmentReview review)
    {
        var witness = RequireCurrentWitness(request, review);
        if (request.SourceReviewAttestation is not { ConfirmedByUserId: > 0 } attestation
            || attestation.ConfirmedAtUtc.Kind != DateTimeKind.Utc || attestation.ConfirmedAtUtc <= DateTime.UnixEpoch
            || !SameHash(attestation.WitnessFingerprint, witness.WitnessFingerprint))
            throw Conflict("The held draft has no matching server-owned source-review attestation. Existing provenance and adjustment history remain unchanged.");
    }

    private static SelectedAppliedSourceWitness RequireCurrentWitness(CreatePayRunRequest request, SelectedPayRunAdjustmentReview review)
    {
        RequireBoundedScope(request);
        if (review is null || review.AdjustmentId != request.AdjustmentIds[0]
            || review.EmployeeId != request.IncludedEmployeeIds[0] || review.Amount <= 0
            || review.Category is not ("Earning" or "Reimbursement" or "Deduction")
            || !Regex.IsMatch(review.ComponentCode ?? "", "^[A-Za-z][A-Za-z0-9_]{0,79}$", RegexOptions.CultureInvariant)
            || !Guid.TryParseExact(review.SourceBatchId, "N", out _) || !Guid.TryParseExact(review.SourceImportId, "N", out _)
            || !Guid.TryParse(review.SourceRowId, out _) || review.SourceRow is < 1 or > 100000
            || !ValidHash(review.BusinessFingerprint) || review.SourceWitness is not { } witness
            || witness.Kind != Kind || witness.HistoricalTransferVersion != 0
            || !SameHash(review.ReceiptFingerprint, witness.OriginalReceiptFingerprint)
            || !SameHash(review.SourceHash, witness.SourceHash))
            throw Conflict("The distinct AppliedV0 source witness is missing, malformed or outside the exact employee/adjustment scope.");
        var current = BuildWitness(request, witness.OriginalReceiptFingerprint, witness.SourceHash,
            witness.MappingFingerprint, witness.IdentityFingerprint, witness.SourceFormulaFingerprint,
            witness.NativeInputFingerprint, witness.SalaryPolicyFingerprint, witness.CoreCalculationFingerprint,
            witness.ComparisonFingerprint, review.BusinessFingerprint);
        if (!SameHash(current.WitnessFingerprint, witness.WitnessFingerprint))
            throw Conflict("Source review evidence changed or is not bound to this exact business/scope snapshot. Review it again before processing.");
        return witness;
    }

    private static void RequireBoundedScope(CreatePayRunRequest request)
    {
        if (request is null || request.ClientId <= 0 || request.RunType != "Regular"
            || !Regex.IsMatch(request.PayPeriod ?? "", "^\\d{4}-(0[1-9]|1[0-2])$", RegexOptions.CultureInvariant)
            || request.TotalWorkingDays is < 1 or > 31 || request.PayDate.ToString("yyyy-MM", CultureInfo.InvariantCulture) != request.PayPeriod
            || request.IncludedEmployeeIds is not { Count: 1 } || request.IncludedEmployeeIds[0] <= 0
            || request.AdjustmentIds is not { Count: 1 } || request.AdjustmentIds[0] <= 0
            || request.ExcludedEmployeeIds is not { Count: 0 } || request.AttendanceGroupIds is null
            || string.IsNullOrWhiteSpace(request.AttendancePolicyBatchId) || request.AttendancePolicyBatchId.Length > 64
            || request.RunName?.Length > 120 || request.Reason?.Length > 500)
            throw Conflict("This source review requires one explicit existing employee, one existing adjustment and the exact Regular client/month/day/policy scope.");
        var groups = SelectedPayRunDraft.Groups(request);
        if (groups.Length == 0 || groups.Any(group => group <= 0) || groups.Distinct().Count() != groups.Length
            || request.AttendanceGroupId > 0 && !groups.Contains(request.AttendanceGroupId))
            throw Conflict("Review the exact active policy groups; implicit or duplicated group scope cannot be acknowledged.");
    }

    private static bool ValidHash(string? value) => value is not null && Fingerprint.IsMatch(value);
    private static bool SameHash(string? left, string? right) => ValidHash(left) && ValidHash(right)
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left!), Encoding.ASCII.GetBytes(right!));
    private static SelectedPayRunConflictException Conflict(string message) => new(message);
}
