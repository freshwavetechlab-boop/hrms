using System.Security.Cryptography;
using System.Text.Json;
using Payroll.API.Models;

namespace Payroll.API.Services;

// Only meanings that constrain the native financial display belong here.
// Cosmetic flags, Information, source-month selection and save audit are not
// native earnings or a new processing epoch. Original financial source identity
// remains separately bound by NativeCurrencyProfile.SourceBatchId/SourceHash.
internal static class NativeCurrencyPresentation
{
    internal static string Hash(PayslipDesignTemplate template,PayslipDesignProfile profile,string period)
    {
        NativePayslipDesignService.ValidateProfile(profile,template);
        var payload=new{Version=1,profile.ClientId,profile.TemplateId,template.Theme,profile.AmountDecimalPlaces,
            Applies=NativePayslipDesignScope.Applies(profile,period),
            Financial=profile.Fields.Where(f=>f.Kind!="info").OrderBy(f=>f.SourceColumn)
                .ThenBy(f=>f.Kind,StringComparer.Ordinal).ThenBy(f=>f.Label,StringComparer.Ordinal).ThenBy(f=>f.NativeCode,StringComparer.Ordinal)
                .Select(f=>new{f.SourceColumn,f.Label,f.Kind,f.NativeCode}).ToArray()};
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload,new JsonSerializerOptions(JsonSerializerDefaults.Web)))).ToLowerInvariant();
    }
}
