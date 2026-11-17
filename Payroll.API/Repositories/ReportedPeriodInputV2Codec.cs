using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

// Separate purposes: existing named-input v1 envelopes/keys/readers are untouched.
// This codec performs no persistence and exposes no route. Repository/worker
// ownership, receipt, attendance and history integration remains a required gate.
internal static class ReportedPeriodInputV2Codec
{
    private const string ProfilePurpose = "ReportedSalaryPeriodProfileV2";
    private const string ManifestPurpose = "ReportedSalaryPeriodManifestV2";
    internal sealed record ManifestScope(int ClientId, int EmployeeId, string PayPeriod, long HistoryId,
        string TemplateId, string SourceHash, string RowId, string SourceIdentityHash,
        string NativeIdentityOwnershipHash, string NativeAttendanceSnapshotHash, string RawBatchHash,
        string RawSourceHash, string PrimaryWorkbookSha256, string DeclarationHash);

    internal static void RequireWriterCompatibility(IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("Payroll:HistoricalNamedInputsEnabled") || !configuration.GetValue<bool>(ReportedPeriodInputProof.WriterFlag))
            throw new InvalidOperationException("Reported period-input writers are disabled by default. Compatible API and payroll-worker readers, nullable attendance and the remaining normal-workflow proofs must be deployed/verified before enabling both capabilities.");
    }
    internal static string ProtectProfile(ReportedPeriodInputProfileV2 profile, IConfiguration configuration)
    {
        RequireWriterCompatibility(configuration);
        ReportedPeriodInputProof.ValidateProfile(profile);
        return NamedFormulaInputStore.Protect(profile, configuration, ProfilePurpose);
    }
    internal static ReportedPeriodInputProfileV2 ReadProfile(string envelope, IConfiguration configuration, int clientId, string templateId)
    {
        var profile = NamedFormulaInputStore.Read<ReportedPeriodInputProfileV2>(envelope, configuration, ProfilePurpose);
        ReportedPeriodInputProof.ValidateProfile(profile);
        if (profile.ClientId != clientId || profile.TemplateId != templateId) throw new InvalidOperationException("Foreign reported period profile.");
        return profile;
    }
    internal static string ManifestHash(ReportedPeriodInputManifestV2 manifest) => ReviewedNamedFormulaInputs.Hash(manifest with { ManifestHash = "" });
    internal static string ProtectManifest(ReportedPeriodInputManifestV2 manifest, ReportedPeriodInputProfileV2 profile, IConfiguration configuration)
    {
        RequireWriterCompatibility(configuration);
        ValidateManifestForProfile(manifest, profile);
        return NamedFormulaInputStore.Protect(manifest, configuration, ManifestPurpose);
    }
    internal static void ValidateManifestForProfile(ReportedPeriodInputManifestV2 manifest,ReportedPeriodInputProfileV2 profile)
    {
        ValidateManifest(manifest);RequireProfile(manifest,profile);
    }
    internal static ReportedPeriodInputManifestV2 ReadManifest(string envelope, IConfiguration configuration, ManifestScope expected, ReportedPeriodInputProfileV2 profile)
    {
        var manifest = NamedFormulaInputStore.Read<ReportedPeriodInputManifestV2>(envelope, configuration, ManifestPurpose);
        ValidateManifest(manifest);
        RequireProfile(manifest, profile);
        var bound = manifest.BoundInputs;
        if (manifest.ClientId != expected.ClientId || manifest.EmployeeId != expected.EmployeeId || manifest.PayPeriod != expected.PayPeriod
            || manifest.HistoryId != expected.HistoryId || manifest.TemplateId != expected.TemplateId || bound.SourceHash != expected.SourceHash
            || bound.RowId != expected.RowId || bound.SourceIdentityHash != expected.SourceIdentityHash
            || manifest.NativeIdentityOwnershipHash != expected.NativeIdentityOwnershipHash || manifest.NativeAttendanceSnapshotHash != expected.NativeAttendanceSnapshotHash
            || bound.RawBatchHash != expected.RawBatchHash || bound.RawSourceHash != expected.RawSourceHash || bound.PrimaryWorkbookSha256 != expected.PrimaryWorkbookSha256
            || bound.DeclarationHash != expected.DeclarationHash)
            throw new InvalidOperationException("Reported period operands lack their exact client/employee/month/history/source/identity/native-attendance scope. No fallback is accepted.");
        return manifest;
    }
    private static void RequireProfile(ReportedPeriodInputManifestV2 manifest, ReportedPeriodInputProfileV2 profile)
    {
        ReportedPeriodInputProof.ValidateProfile(profile);
        var bound = manifest.BoundInputs;
        if (profile.ClientId != manifest.ClientId || profile.TemplateId != manifest.TemplateId || profile.DeclarationHash != bound.DeclarationHash
            || profile.Definitions.Count != bound.Cells.Count || !profile.TotalPayableKeys.ToHashSet(StringComparer.Ordinal).SetEquals(bound.Cells.Where(c => c.SourceMode == "ReportedDays").Select(c => c.Key)))
            throw new InvalidOperationException("Reported manifest does not belong to the exact managed declaration.");
        foreach (var definition in profile.Definitions)
        {
            if (!bound.Values.TryGetValue(definition.Key, out var value) || value < definition.Minimum || value > definition.Maximum || decimal.Round(value, definition.Precision) != value
                || bound.Cells.Single(c => c.Key == definition.Key).SourceMode != (definition.Unit == "Days" ? "ReportedDays" : "IndependentAmount"))
                throw new InvalidOperationException("Reported manifest unit, precision or operand declaration differs.");
        }
    }
    private static void ValidateManifest(ReportedPeriodInputManifestV2 manifest)
    {
        bool Hash(string? value) => value is not null && System.Text.RegularExpressions.Regex.IsMatch(value, "\\A[a-f0-9]{64}\\z");
        if (manifest is null || manifest.Version != 2 || manifest.ClientId <= 0 || manifest.EmployeeId <= 0 || manifest.HistoryId <= 0
            || string.IsNullOrWhiteSpace(manifest.TemplateId) || !Guid.TryParseExact(manifest.RequestId, "N", out _)
            || manifest.ReviewerId <= 0 || manifest.ReviewedAtUtc.Kind != DateTimeKind.Utc
            || !Hash(manifest.NativeIdentityOwnershipHash) || !Hash(manifest.NativeAttendanceSnapshotHash)
            || manifest.ManifestHash != ManifestHash(manifest) || manifest.BoundInputs is null || manifest.BoundInputs.ClientId != manifest.ClientId || manifest.BoundInputs.PayPeriod != manifest.PayPeriod)
            throw new InvalidOperationException("A version-two reported manifest needs exact protected ownership/history/native-attendance/reviewer scope.");
        ReportedPeriodInputProof.ValidateBound(manifest.BoundInputs);
    }
}
