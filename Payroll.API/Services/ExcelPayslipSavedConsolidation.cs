using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

internal static class ExcelPayslipSavedConsolidation
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    internal static string SnapshotHash(ExcelPayslipBatch batch, ExcelPayslipCalculationSource? source) => Hash(JsonSerializer.Serialize(new { batch, source }, Json));
    internal static string PreviewToken(ExcelPayslipConsolidationPreview preview) => Hash(JsonSerializer.Serialize(new {
        preview.CanonicalBatchId,preview.CanonicalSnapshotHash, sourceBatchIds=preview.SourceBatchIds.Order(StringComparer.Ordinal), preview.CanonicalRows,
        sources=preview.Sources.OrderBy(s=>s.BatchId,StringComparer.Ordinal),preview.Blockers,preview.Differences },Json));
    private static string Code(string? value) => (value ?? "").Trim().ToUpperInvariant();
    private static string Name(string? value) => Regex.Replace((value ?? "").Trim(), @"\s+", " ").ToUpperInvariant();
    private static bool ValidRows(ExcelPayslipBatch batch) => batch.Rows.Count is > 0 and <= 2000
        && batch.Rows.All(row=>row.SourceRow>0 && !string.IsNullOrWhiteSpace(row.Id) && Code(row.EmployeeCode).Length>0 && Name(row.EmployeeName).Length>0)
        && batch.Rows.Select(row=>row.SourceRow).Distinct().Count()==batch.Rows.Count
        && batch.Rows.Select(row=>row.Id).Distinct(StringComparer.Ordinal).Count()==batch.Rows.Count
        && batch.Rows.Select(row=>(Code(row.EmployeeCode),Name(row.EmployeeName))).Distinct().Count()==batch.Rows.Count;
    private static bool SameIdentity(ExcelPayslipRow left,ExcelPayslipRow right) => left.SourceRow==right.SourceRow && Code(left.EmployeeCode)==Code(right.EmployeeCode) && Name(left.EmployeeName)==Name(right.EmployeeName);
    private static bool SameAmounts(IEnumerable<ExcelPayslipAmount> left,IEnumerable<ExcelPayslipAmount> right)
    {
        var l=left.ToArray(); var r=right.ToArray();
        if(l.Any(v=>string.IsNullOrWhiteSpace(v.Label)) || r.Any(v=>string.IsNullOrWhiteSpace(v.Label))
            || l.Select(v=>Name(v.Label)).Distinct().Count()!=l.Length || r.Select(v=>Name(v.Label)).Distinct().Count()!=r.Length)return false;
        return l.Select(v=>(Label:Name(v.Label),v.Amount)).OrderBy(v=>v.Label,StringComparer.Ordinal)
            .SequenceEqual(r.Select(v=>(Label:Name(v.Label),v.Amount)).OrderBy(v=>v.Label,StringComparer.Ordinal));
    }
    private static bool SameFinancial(ExcelPayslipRow left,ExcelPayslipRow right) =>
        SameAmounts(left.Earnings,right.Earnings) && SameAmounts(left.Deductions,right.Deductions)
        && SameAmounts(left.EmployerContributions,right.EmployerContributions) && left.NetPay==right.NetPay
        && left.DeclaredGross==right.DeclaredGross && left.DeclaredDeductions==right.DeclaredDeductions;
    internal static ExcelPayslipConsolidationPreview Compare(ExcelPayslipBatch canonical,ExcelPayslipCalculationSource? canonicalSource,
        IReadOnlyList<(ExcelPayslipBatch Batch,ExcelPayslipCalculationSource? Source)> sources)
    {
        var result=new ExcelPayslipConsolidationPreview {CanonicalBatchId=canonical.Id,CanonicalRows=canonical.Rows.Count,CanonicalSnapshotHash=SnapshotHash(canonical,canonicalSource),
            SourceBatchIds=sources.Select(s=>s.Batch.Id).Order(StringComparer.Ordinal).ToList(),
            Evidence="One working batch retains the complete canonical rows and formulas unchanged. An earlier subset is linked only when every retained-source row matches its original source row, employee code/name and every financial amount in the full batch. Original snapshots, delivery evidence and payroll receipts remain under their original IDs. Formula differences are disclosed, never blended; prior-version delivery is not a Sent flag on changed content."};
        var validCanonical=ValidRows(canonical);
        if(canonical.ClientId<=0 || !Guid.TryParseExact(canonical.Id,"N",out _) || !Regex.IsMatch(canonical.Month??"",@"^\d{4}-(0[1-9]|1[0-2])$") || !validCanonical)result.Blockers.Add("Canonical rows need a valid saved client/month, unique row IDs/source-row numbers and complete employee codes/names (maximum 2000 rows).");
        var canonicalRows=validCanonical?canonical.Rows.ToDictionary(row=>row.SourceRow):new Dictionary<int,ExcelPayslipRow>();
        if(canonicalSource is null)result.Blockers.Add("Retain the canonical saved calculation source before consolidation; formulas cannot be reconstructed or guessed.");
        if(sources.Count is <1 or >8 || sources.Select(s=>s.Batch.Id).Distinct(StringComparer.Ordinal).Count()!=sources.Count)
            result.Blockers.Add("Choose between 1 and 8 distinct retained source batches.");
        foreach(var (batch,source) in sources)
        {
            var item=new ExcelPayslipConsolidationSource {BatchId=batch.Id,Rows=batch.Rows.Count,FormulaSourcePresent=source is not null,
                FormulaSourceUnchanged=JsonSerializer.Serialize(source,Json)==JsonSerializer.Serialize(canonicalSource,Json),
                BatchHash=Hash(JsonSerializer.Serialize(batch,Json)),SourceHash=source is null?"":Hash(JsonSerializer.Serialize(source,Json))};
            result.Sources.Add(item);
            if(!Guid.TryParseExact(batch.Id,"N",out _) || batch.Id==canonical.Id || batch.ClientId!=canonical.ClientId || batch.Month!=canonical.Month || !ValidRows(batch) || batch.Rows.Count>canonical.Rows.Count)
            { result.Blockers.Add($"Source {batch.Id}: require a different saved batch in the exact client/month; every retained-source row must have complete unambiguous coverage in the full working batch."); continue; }
            item.IdentityMatch=true; item.FinancialMatch=true;
            foreach(var old in batch.Rows)
            {
                if(!canonicalRows.TryGetValue(old.SourceRow,out var row) || !SameIdentity(row,old))
                { item.IdentityMatch=false; if(result.Differences.Count<50)result.Differences.Add(new(batch.Id,old.SourceRow,"identity","Source row, employee code and name must agree; no name-only or positional match.")); continue; }
                if(!SameFinancial(row,old))
                { item.FinancialMatch=false; if(result.Differences.Count<50)result.Differences.Add(new(batch.Id,row.SourceRow,"financial","Earning/deduction/employer component labels and exact values, declared totals and net must all match.")); continue; }
                item.MatchedRows++;
            }
            if(!item.IdentityMatch || !item.FinancialMatch)result.Blockers.Add($"Source {batch.Id}: identity or complete financial comparison differs. This version cannot be grouped as an equivalent working batch.");
        }
        result.MatchedRows=result.Sources.Count>0?result.Sources.Min(s=>s.MatchedRows):0;
        result.Token=PreviewToken(result); return result;
    }
    internal static bool SourceRowBindingsValid(int version,int canonicalRows,
        IReadOnlyList<(int SourceRow,string CanonicalRowId,string SourceRowId)> rows) =>
        version is 1 or 2 && canonicalRows is >0 and <=2000 && rows.Count>0
        && (version==1?rows.Count==canonicalRows:rows.Count<=canonicalRows)
        && rows.All(row=>row.SourceRow>0&&!string.IsNullOrWhiteSpace(row.CanonicalRowId)&&!string.IsNullOrWhiteSpace(row.SourceRowId))
        && rows.Select(row=>row.SourceRow).Distinct().Count()==rows.Count
        && rows.Select(row=>row.CanonicalRowId).Distinct(StringComparer.Ordinal).Count()==rows.Count
        && rows.Select(row=>row.SourceRowId).Distinct(StringComparer.Ordinal).Count()==rows.Count;
    internal static List<ExcelPayslipHistoricalDelivery> BindHistoricalDeliveries(ExcelPayslipBatch canonical,ExcelPayslipBatch source,IEnumerable<ExcelPayslipRowDeliveryStatus> statuses)
    {
        if(canonical.Id==source.Id || canonical.ClientId!=source.ClientId || canonical.Month!=source.Month || !ValidRows(canonical) || !ValidRows(source))return [];
        var kept=canonical.Rows.ToDictionary(row=>row.SourceRow); var originals=source.Rows.ToDictionary(row=>row.Id,StringComparer.Ordinal);
        var result=new List<ExcelPayslipHistoricalDelivery>();
        foreach(var status in statuses)
        {
            if(!originals.TryGetValue(status.RowId,out var old) || !kept.TryGetValue(old.SourceRow,out var row) || !SameIdentity(row,old) || !SameFinancial(row,old))continue;
            foreach(var history in status.RecipientDeliveries)
                result.Add(new(row.Id,old.Id,source.Id,true,history.Status,history.Mode,history.QueuedAtUtc,history.SentAtUtc,
                    "Delivery history from the retained source version; this is not a send of the canonical version. "+history.Message));
        }
        return result;
    }
    internal static void ValidateReplay(string action,string storedRequestHash,string expectedAction,string requestHash)
    {
        if(action!=expectedAction || storedRequestHash!=requestHash)throw new InvalidOperationException("This request ID already belongs to another consolidation action or payload.");
    }
    internal static bool GroupIsActive(string groupId,IEnumerable<(string Action,string ObjectId)> actions) =>
        !actions.Any(action=>action.Action=="batch-group-undo" && action.ObjectId==groupId);
    internal static bool SnapshotBindingsMatch(string? canonicalHash,string expectedCanonicalHash,
        IReadOnlyDictionary<string,string?> sourceHashes,IReadOnlyDictionary<string,string> expectedSourceHashes) =>
        canonicalHash==expectedCanonicalHash && expectedCanonicalHash.Length==64 && sourceHashes.Count==expectedSourceHashes.Count
        && expectedSourceHashes.All(source=>sourceHashes.TryGetValue(source.Key,out var hash) && hash==source.Value && source.Value.Length==64);
    internal static bool HasOverlap(IEnumerable<string> selected,IEnumerable<IEnumerable<string>> activeGroups)
    {var ids=selected.ToHashSet(StringComparer.OrdinalIgnoreCase);return activeGroups.Any(group=>group.Any(ids.Contains));}
    internal static bool ShouldHideSource(bool active,bool snapshotBindingsMatch,bool includeConsolidated) => active && snapshotBindingsMatch && !includeConsolidated;
}
