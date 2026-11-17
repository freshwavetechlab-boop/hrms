using System.Globalization;
using Payroll.API.Models;

namespace Payroll.API.Services;

internal static class NativePayslipDesignScope
{
    internal static void ValidatePeriods(IReadOnlyList<string>? periods)
    {
        if (periods is null) return;
        if (periods.Count is < 1 or > 120 || periods.Any(p => p is null || p.Length != 7
            || !DateOnly.TryParseExact(p + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            || periods.Distinct(StringComparer.Ordinal).Count() != periods.Count)
            throw new InvalidOperationException("Select 1–120 distinct payroll months in YYYY-MM format.");
    }

    internal static bool Applies(PayslipDesignProfile profile, string? period)
    {
        ValidatePeriods(profile.PayPeriods);
        if (profile.PayPeriods is null) return true;
        if (period is null) throw new InvalidOperationException("An explicit payroll period is required for this scoped source design.");
        ValidatePeriods([period]); return profile.PayPeriods.Contains(period, StringComparer.Ordinal);
    }

    internal static void ValidateFallback(PayslipDesignProfile profile)
    {
        var old = profile.PreviousClassicTemplate;
        if (profile.PayPeriods is not null && old is null)
            throw new InvalidOperationException("A payroll-month scoped design requires its authenticated original Classic presentation.");
        if (old is not null && (old.Id != profile.TemplateId || old.ClientId != profile.ClientId || old.Theme != "Classic"
            || !old.Active || string.IsNullOrWhiteSpace(old.Name) || old.Name.Length > 200 || old.Note is null || old.Note.Length > 120))
            throw new InvalidOperationException("The retained Classic presentation does not belong to this client/template.");
    }

    internal static List<string>? Resolve(PayslipDesignSaveRequest request, PayslipDesignProfile? existing)
    {
        if (request.AllPayPeriods && request.PayPeriods is not null)
            throw new InvalidOperationException("Choose All payroll months or selected payroll months, not both.");
        ValidatePeriods(request.PayPeriods);
        return request.AllPayPeriods ? null : request.PayPeriods is not null ? request.PayPeriods.Order(StringComparer.Ordinal).ToList()
            : existing?.PayPeriods?.ToList();
    }
}
