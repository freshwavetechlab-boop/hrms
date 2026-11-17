using System.Globalization;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

// Presentation only. The source is a saved payroll component's monthly stage,
// never its earned amount, current Master salary, source cache or gross total.
internal static class NativePayslipMonthlyComponentInformation
{
    internal const string Prefix="COMPONENT_MONTHLY:";
    internal const string WriterFlag="Payroll:ComponentMonthlyPayslipInformationWritesEnabled";

    internal static bool TryCode(string? token,out string code)
    {
        code="";
        if(token is null||!token.StartsWith(Prefix,StringComparison.Ordinal))return false;
        var candidate=token[Prefix.Length..];
        if(!Regex.IsMatch(candidate,@"\A[A-Za-z][A-Za-z0-9_]{0,79}\z"))return false;
        code=candidate;return true;
    }

    internal static bool SupportedToken(string token)=>NativePayslipDesignService.InfoTokens.Contains(token)
        ||TryCode(token,out _);

    internal static IReadOnlyList<string> CatalogueTokens(IReadOnlyList<PayslipDesignCode> codes,bool writerEnabled)
    {
        if(!writerEnabled)return NativePayslipDesignService.InfoTokens;
        var monthly=codes.GroupBy(c=>c.Code,StringComparer.Ordinal)
            .Where(g=>g.Count()==1&&NativePayslipDesignService.LineKind(g.Single().Category)!=""&&TryCode(Prefix+g.Key,out _))
            .Select(g=>Prefix+g.Key).Order(StringComparer.Ordinal);
        return NativePayslipDesignService.InfoTokens.Concat(monthly).ToArray();
    }

    internal static void RequireWriter(IEnumerable<PayslipDesignField> fields,bool enabled)
    {
        if(!enabled&&fields.Any(f=>f.Kind=="info"&&f.InfoToken?.StartsWith(Prefix,StringComparison.Ordinal)==true))
            throw new InvalidOperationException("Saving component monthly information is disabled until compatible API, ESS and payroll-worker readers are deployed and verified. No presentation configuration was saved.");
    }

    internal static void ValidateCatalogue(IEnumerable<PayslipDesignField> fields,IReadOnlyList<PayslipDesignCode> codes)
    {
        foreach(var field in fields.Where(f=>f.Kind=="info"&&TryCode(f.InfoToken,out _)))
        {
            TryCode(field.InfoToken,out var code);var matches=codes.Where(c=>c.Code==code).ToArray();
            if(matches.Length!=1||NativePayslipDesignService.LineKind(matches[0].Category)=="")
                throw new InvalidOperationException("Select one existing active salary component for monthly information. Unknown or ambiguous component codes cannot be saved.");
        }
    }

    internal static string ReadSavedMonthly(string token,IReadOnlyList<NativePayslipDesignLine> savedLines)
    {
        if(!TryCode(token,out var code))throw new InvalidOperationException("The monthly information token is invalid.");
        var matches=savedLines.Where(l=>l.Code==code).ToArray();
        if(matches.Length==0)throw new InvalidOperationException($"Saved monthly component {code} is missing. No earned amount, current Master or source value was substituted.");
        var kinds=matches.Select(l=>NativePayslipDesignService.LineKind(l.Category)).Distinct(StringComparer.Ordinal).ToArray();
        if(kinds.Length!=1||kinds[0]=="")throw new InvalidOperationException($"Saved monthly component {code} has ambiguous financial categories.");
        // Period adjustment lines carry zero monthly amounts. They do not add
        // to, replace or duplicate the one saved nonzero monthly base.
        var bases=matches.Where(l=>l.MonthlyAmount!=0).ToArray();
        if(bases.Length>1)throw new InvalidOperationException($"Saved monthly component {code} has multiple nonzero monthly bases. Review the presentation before downloading.");
        return (bases.Length==1?bases[0].MonthlyAmount:0m).ToString("G29",CultureInfo.InvariantCulture);
    }
}
