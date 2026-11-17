using Payroll.API.Models;

namespace Payroll.API.Services;

// Kept outside request JSON. The exact context is assembled only after the
// source binder or an authenticated persisted-manifest reader succeeds.
internal sealed record ReportedPeriodCalculationProof(ReportedPeriodInputProfileV2 Profile,
    ReportedPeriodBoundRowV2 Bound, string ContextHash)
{
    internal NativePayrollAttendanceInput? NativeAttendance { get; init; }
}

internal static class ReportedPeriodCalculationContext
{
    internal static NamedFormulaInputContext FromBound(ReportedPeriodInputProfileV2 profile,
        ReportedPeriodBoundRowV2 bound, string manifestHash = "", NativePayrollAttendanceInput? nativeAttendance = null)
    {
        ReportedPeriodInputProof.ValidateProfile(profile);
        ReportedPeriodInputProof.ValidateBound(bound);
        if (profile.ClientId != bound.ClientId || profile.DeclarationHash != bound.DeclarationHash
            || profile.Definitions.Count != bound.Values.Count
            || !profile.TotalPayableKeys.ToHashSet(StringComparer.Ordinal).SetEquals(bound.Cells.Where(c => c.SourceMode == "ReportedDays").Select(c => c.Key)))
            throw new InvalidOperationException("Reported calculation context requires its exact source declaration and summed day boundaries.");
        var context = new NamedFormulaInputContext
        {
            Definitions = profile.Definitions.ToList(), Values = new(bound.Values, StringComparer.Ordinal),
            DeclarationHash = profile.DeclarationHash, ManifestHash = manifestHash
        };
        context.ReportedPeriodProof = new(profile, bound, ContextHash(context)) { NativeAttendance=nativeAttendance };
        Validate(context, [], bound.PayrollDivisor, bound.PayableDays);
        return context;
    }

    internal static bool IsReported(NamedFormulaInputContext? context) => context?.ReportedPeriodProof is not null;

    internal static void Validate(NamedFormulaInputContext context, IEnumerable<string> componentTokens,
        int payrollDays, decimal payableDays)
    {
        var proof = context.ReportedPeriodProof ?? throw new InvalidOperationException("A trusted reported-period context is missing; uploaded Days operands are not accepted.");
        ReportedPeriodInputProof.ValidateProfile(proof.Profile, componentTokens);
        ReportedPeriodInputProof.ValidateBound(proof.Bound);
        if (proof.ContextHash != ContextHash(context) || context.DeclarationHash != proof.Profile.DeclarationHash
            || proof.Bound.DeclarationHash != context.DeclarationHash
            || context.Definitions.Count != proof.Profile.Definitions.Count
            || ReviewedNamedFormulaInputs.Hash(context.Definitions) != ReviewedNamedFormulaInputs.Hash(proof.Profile.Definitions)
            || context.Values.Count != proof.Bound.Values.Count
            || !context.Values.OrderBy(v => v.Key, StringComparer.Ordinal).SequenceEqual(proof.Bound.Values.OrderBy(v => v.Key, StringComparer.Ordinal))
            || payrollDays != proof.Bound.PayrollDivisor || payableDays != proof.Bound.PayableDays)
            throw new InvalidOperationException("Reported-period calculation values, declaration, divisor or whole-period payable changed; no fallback or what-if override is accepted.");
        foreach (var d in context.Definitions)
            if (!context.Values.TryGetValue(d.Key, out var value) || value < d.Minimum || value > d.Maximum || decimal.Round(value, d.Precision) != value)
                throw new InvalidOperationException("Reported-period context unit, bounds or exact precision changed.");
        if(proof.NativeAttendance is { } native && (native.ClientId!=proof.Bound.ClientId || native.Month!=proof.Bound.PayPeriod
            || native.EmployeeId<=0 || native.PayrollDays!=payrollDays || native.PayableDays!=payableDays || native.PresentDays.HasValue
            || !System.Text.RegularExpressions.Regex.IsMatch(native.SnapshotHash??"",@"\A[a-f0-9]{64}\z")
            || native.Error.Length!=0 || native.ProofKind is not("ReviewedClosedSourcePayableSeedV2.PendingNative" or "ReviewedClosedSourcePayableSeedV2.SavedNative")))
            throw new InvalidOperationException("Reported native attendance differs from its exact source/month/unknown-presence boundary.");
    }

    internal static NamedFormulaInputContext Clone(NamedFormulaInputContext context) => new()
    {
        Definitions = context.Definitions.ToList(), Values = new(context.Values, StringComparer.Ordinal),
        DeclarationHash = context.DeclarationHash, ManifestHash = context.ManifestHash,
        ReportedPeriodProof = context.ReportedPeriodProof
    };

    private static string ContextHash(NamedFormulaInputContext context) => ReviewedNamedFormulaInputs.Hash(new
    { context.Definitions, values = context.Values.OrderBy(v => v.Key, StringComparer.Ordinal), context.DeclarationHash, context.ManifestHash });
}
