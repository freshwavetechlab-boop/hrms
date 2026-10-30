using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    internal static string? ValidateCapturedWorkbookBindings(ExcelPayslipCalculationSource? source,IReadOnlyList<ExcelPayslipOriginal> originals)
    {
        if(source is null)return null;
        if(source.DependencySheets is null||source.ExternalMappings is null)return "Dependency metadata is incomplete.";
        if(source.CaptureVersion==0)return source.DependencySheets.Count+source.ExternalMappings.Count>0?"Re-capture dependency workbooks with retained original-file bindings before saving this source.":null;
        if(source.CaptureVersion!=1||source.DependencyReviewStatus is not ("Reviewed" or "ReferenceOnly")||source.WorkbookFiles is null||source.WorkbookFiles.Count is <1 or >20)
            return "Review the captured workbook version/status and retained original files.";
        if(source.WorkbookFiles.Any(f=>f is null)||source.WorkbookFiles.Select(f=>f.WorkbookKey).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=source.WorkbookFiles.Count)return "Workbook bindings contain ambiguous duplicate fingerprints.";
        foreach(var file in source.WorkbookFiles) {
            var sha=file.Sha256??"";
            if(!Regex.IsMatch(sha,@"^[a-fA-F0-9]{64}$")||file.WorkbookKey!="wb_"+sha.ToLowerInvariant()||!Guid.TryParseExact(file.OriginalId,"N",out _)
                ||string.IsNullOrWhiteSpace(file.FileName)||file.FileName.Length>255||file.FileName!=Path.GetFileName(file.FileName.Replace('\\','/'))||file.FileName.Any(char.IsControl))return "A workbook fingerprint/original reference is invalid. Retain the exact supplied file through attachment storage.";
            if(!originals.Any(o=>o.Id==file.OriginalId&&string.Equals(o.Sha256,file.Sha256,StringComparison.OrdinalIgnoreCase)))return "A captured workbook does not match an available original in this client's linked attachment set. Re-supply/re-retain the exact file; no cross-client or hash-only attachment is accepted.";
        }
        if(!source.WorkbookFiles.Any(f=>f.WorkbookKey==source.WorkbookKey))return "The calculation workbook is not bound to a retained original.";
        foreach(var sheet in source.DependencySheets)if(!source.WorkbookFiles.Any(f=>f.WorkbookKey==sheet.WorkbookKey&&string.Equals(f.Sha256,sheet.SourceHash,StringComparison.OrdinalIgnoreCase)))return "A dependency worksheet points to a different/unretained workbook version.";
        foreach(var mapping in source.ExternalMappings)if(!source.WorkbookFiles.Any(f=>f.WorkbookKey==mapping.FromWorkbookKey)||!source.WorkbookFiles.Any(f=>f.WorkbookKey==mapping.ToWorkbookKey)
            ||source.DependencyReviewStatus=="Reviewed"&&!mapping.Reviewed)return "An external dependency mapping is missing a retained source/target or explicit review.";
        return null;
    }
}
