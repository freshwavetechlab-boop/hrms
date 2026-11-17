using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    private sealed record ReboundLocationRows(IReadOnlyList<LocationAllocationStorageRow> Allocations,
        IReadOnlyList<LocationAssignmentStorageRow> Assignments,PayrollLocationPartPeriod OriginalPeriod,
        IReadOnlyList<ExcelPayrollLocationRowBinding> Bindings,object NumericRowWitness);

    private static void ValidateLocationRowBindingReview(ExcelPayrollLocationCorrectionRequest request)
    {
        if(request.RowRebindReview is not { } review)return;
        if(!request.RecoverOrphanedLineage || review.Version!=1 || !review.Reviewed || review.Rows is null || review.Rows.Count is <1 or >100
            || review.Rows.Any(binding=>binding is null || !request.EmployeeIds.Contains(binding.EmployeeId)
                || !Guid.TryParseExact(binding.AssignmentId,"N",out _) || string.IsNullOrWhiteSpace(binding.SourcePartKey) || binding.SourcePartKey.Length>100
                || string.IsNullOrWhiteSpace(binding.StoredSourceRowId) || binding.StoredSourceRowId.Length>100
                || string.IsNullOrWhiteSpace(binding.CurrentRowId) || binding.CurrentRowId.Length>100 || binding.StoredSourceRowId==binding.CurrentRowId
                || binding.SourceRow is <1 or >1048576 || !Regex.IsMatch(binding.SourceIdentityHash??"",@"\A[a-f0-9]{64}\z"))
            || review.Rows.Select(binding=>(binding.EmployeeId,binding.SourcePartKey)).Distinct().Count()!=review.Rows.Count
            || review.Rows.Select(binding=>binding.CurrentRowId).Distinct(StringComparer.Ordinal).Count()!=review.Rows.Count
            || !review.Rows.Select(binding=>binding.EmployeeId).ToHashSet().SetEquals(request.EmployeeIds))
            throw new InvalidOperationException("Source-row rebinding requires an explicit reviewed version-one orphan recovery binding for every selected employee and unique source part; no same-ID or guessed binding is accepted.");
    }

    private static async Task<ReboundLocationRows> ReviewLocationRowBindingsAsync(MySqlConnection db,MySqlTransaction tx,
        ExcelPayrollLocationCorrectionRequest request,ExcelPayslipBatch batch,ExcelPayslipCalculationSource source,
        PayRunEmployee saved,IReadOnlyList<LocationAllocationStorageRow> allocations,IReadOnlyList<LocationAssignmentStorageRow> assignments,
        IReadOnlyList<Identity> identities)
    {
        var bindings=request.RowRebindReview!.Rows.Where(binding=>binding.EmployeeId==saved.EmployeeId).ToArray();
        if(bindings.Length!=allocations.Count || !bindings.Select(binding=>binding.SourcePartKey).ToHashSet(StringComparer.Ordinal).SetEquals(allocations.Select(part=>part.SourcePartKey)))
            throw new InvalidOperationException("Review every stored location part together; partial source-row rebinding is unavailable.");
        var target=identities.SingleOrDefault(identity=>identity.Id==saved.EmployeeId && identity.ClientId==batch.ClientId)
            ??throw new InvalidOperationException("The exact existing source-row employee is unavailable.");
        var normalizedAllocations=new List<LocationAllocationStorageRow>();var normalizedAssignments=assignments.ToList();
        var baseline=new PayrollLocationPartPeriod{ClientId=batch.ClientId,EmployeeId=saved.EmployeeId,PayPeriod=batch.Month,ReferenceAssignmentId=allocations.Select(part=>part.ReferenceAssignmentId).Distinct(StringComparer.Ordinal).Single()};
        var numericWitness=new List<object>();
        foreach(var allocation in allocations)
        {
            var binding=bindings.Single(candidate=>candidate.SourcePartKey==allocation.SourcePartKey);
            var assignment=assignments.SingleOrDefault(candidate=>candidate.Id==allocation.AssignmentId && candidate.ClientId==batch.ClientId && candidate.EmployeeId==saved.EmployeeId)
                ??throw new InvalidOperationException("The stored allocation's exact immutable assignment is missing.");
            if(binding.AssignmentId!=allocation.AssignmentId || binding.StoredSourceRowId!=allocation.SourceRowId || assignment.SourceRowId!=allocation.SourceRowId
                || allocation.ClientId!=batch.ClientId || allocation.EmployeeId!=saved.EmployeeId || allocation.SourceBatchId!=batch.Id || allocation.PayPeriod!=batch.Month
                || assignment.SourceBatchId!=batch.Id || assignment.SetupImportId!=allocation.SetupImportId || assignment.SourceHash!=allocation.SourceHash || assignment.Status!="Active")
                throw new InvalidOperationException("The explicit binding does not match the complete stored allocation/assignment provenance.");
            var oldRow=Regex.Match(binding.StoredSourceRowId,@"\Acaptured-row-([1-9][0-9]*)\z",RegexOptions.CultureInvariant);
            var rows=batch.Rows.Where(row=>row.Id==binding.CurrentRowId && row.SourceRow==binding.SourceRow).ToArray();
            if(!oldRow.Success || !int.TryParse(oldRow.Groups[1].Value,NumberStyles.None,CultureInfo.InvariantCulture,out var oldNumber)
                || oldNumber!=binding.SourceRow || rows.Length!=1 || source.Rows.Count(row=>row.SourceRow==binding.SourceRow)!=1)
                throw new InvalidOperationException("The stored legacy numeric source row and exact current captured row must agree; an ID-derived number alone never proves employee identity.");
            var numeric=await ReadStoredLocationSourceRowsAsync(db,tx,allocation,assignment);
            if(numeric.Any(pair=>pair.Value.HasValue && pair.Value!=binding.SourceRow))throw new InvalidOperationException("A stored numeric source row disagrees with the reviewed current row.");
            numericWitness.Add(new{binding.EmployeeId,binding.SourcePartKey,Values=numeric});
            var row=rows[0];var rebound=allocation with{SourceRowId=row.Id};
            RequireDurableLocationRecoveryLink(batch,source,rebound);
            var link=batch.ProfileLinks!.Single(candidate=>candidate.RowId==row.Id);
            if(link.SourceIdentityHash!=binding.SourceIdentityHash)throw new InvalidOperationException("The reviewed durable source identity changed; reload its exact current witness.");
            RequireIndependentRebindingIdentity(batch,source,row,target,identities,saved);
            normalizedAllocations.Add(rebound);
            normalizedAssignments[normalizedAssignments.FindIndex(candidate=>candidate.Id==assignment.Id)]=assignment with{SourceRowId=row.Id};
            baseline.Parts.Add(new(){PartKey=allocation.SourcePartKey,AssignmentId=assignment.Id,WorkLocationId=assignment.WorkLocationId,WorkState=assignment.WorkState,
                SalaryStructureId=assignment.SalaryStructureId,AnnualCtc=assignment.AnnualCtc,
                SalaryInputs=JsonSerializer.Deserialize<Dictionary<string,decimal>>(assignment.SalaryInputsJson,Json)??throw new InvalidOperationException("Stored salary inputs are invalid."),
                ComponentScopes=JsonSerializer.Deserialize<Dictionary<string,string>>(assignment.ComponentScopesJson,Json)??throw new InvalidOperationException("Stored component scopes are invalid."),
                PayrollDays=allocation.PayrollDays,PresentDays=allocation.PresentDays,PayableDays=allocation.PayableDays});
        }
        if(baseline.ReferenceAssignmentId is null || !baseline.Parts.Any(part=>part.AssignmentId==baseline.ReferenceAssignmentId))throw new InvalidOperationException("The complete original period needs its existing reference assignment.");
        return new(normalizedAllocations,normalizedAssignments,baseline,bindings,numericWitness);
    }

    private static async Task<Dictionary<string,int?>> ReadStoredLocationSourceRowsAsync(MySqlConnection db,MySqlTransaction tx,
        LocationAllocationStorageRow allocation,LocationAssignmentStorageRow assignment)
    {
        var values=new Dictionary<string,int?>(StringComparer.Ordinal);
        foreach(var table in new[]{"employee_payroll_location_assignments","employee_payroll_location_allocations"})
        {
            var columns=await db.QueryAsync<string>(@"SELECT column_name FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@Table
AND LOWER(column_name) IN ('source_row','sourcerow','source_row_number') ORDER BY ordinal_position",new{Table=table},tx);
            foreach(var column in columns)
            {
                if(!Regex.IsMatch(column,@"\A[A-Za-z_]+\z"))throw new InvalidOperationException("Stored numeric source-row metadata is unsupported.");
                var where=table.EndsWith("assignments",StringComparison.Ordinal)?"id=@Id AND client_id=@ClientId AND employee_id=@EmployeeId":"client_id=@ClientId AND employee_id=@EmployeeId AND pay_period=@PayPeriod AND source_part_key=@SourcePartKey";
                var number=await db.ExecuteScalarAsync<decimal?>($"SELECT `{column}` FROM `{table}` WHERE {where}",new{Id=assignment.Id,allocation.ClientId,allocation.EmployeeId,allocation.PayPeriod,allocation.SourcePartKey},tx);
                if(number.HasValue && (number<1 || number>1048576 || decimal.Truncate(number.Value)!=number))throw new InvalidOperationException("The stored numeric source-row value is invalid.");
                values[table+"."+column]=number.HasValue?(int)number.Value:null;
            }
        }
        return values;
    }

    private static void RequireIndependentRebindingIdentity(ExcelPayslipBatch batch,ExcelPayslipCalculationSource source,ExcelPayslipRow row,
        Identity target,IReadOnlyList<Identity> identities,PayRunEmployee saved)
    {
        var codes=identities.Where(identity=>identity.EmployeeCode.Trim().Equals(row.EmployeeCode.Trim(),StringComparison.OrdinalIgnoreCase)).ToArray();
        string Name(string value)=>Regex.Replace(value.Normalize(NormalizationForm.FormKC).Trim(),@"\s+"," ").ToUpperInvariant();
        if(string.IsNullOrWhiteSpace(row.EmployeeCode) || codes.Length!=1 || codes[0].Id!=target.Id
            || Name(row.EmployeeName)!=Name(target.Name) && Name(row.EmployeeName)!=Name(saved.EmployeeName))
            throw new InvalidOperationException("Rebinding needs one exact existing employee code and a consistent current or saved employee name; name/row-number matching alone is insufficient.");
        var cells=source.Rows.Single(captured=>captured.SourceRow==row.SourceRow);var anchored=false;
        foreach(var column in source.Columns)
        {
            var kind=ExcelPayslipMasterSynchronization.ProfileIdentityKey(column.Label);
            if(kind=="")kind=ExcelPayslipMasterSynchronization.ProfileIdentityKey(column.SourceHeader);
            if(kind is not("pan" or "aadhaar" or "uan"))continue;
            var cell=cells.Cells.ElementAtOrDefault(column.ColumnIndex);
            if(cell is null || cell.DataType is not("" or "Text" or "Number") || !string.IsNullOrWhiteSpace(cell.FormulaText) || cell.MissingCachedValue
                || !string.IsNullOrWhiteSpace(cell.Error) || !string.IsNullOrWhiteSpace(cell.CalculationError))continue;
            var value=cell.Value.Trim().ToUpperInvariant();
            var valid=kind=="pan"?Regex.IsMatch(value,@"\A[A-Z]{5}[0-9]{4}[A-Z]\z")
                :Regex.IsMatch(value,kind=="aadhaar"?@"\A[2-9][0-9]{11}\z":@"\A[0-9]{12}\z") && value.Any(character=>character!=value[0]);
            if(!valid)continue;
            string Read(Identity identity)=>(kind=="pan"?identity.Pan:kind=="aadhaar"?identity.Aadhaar:identity.Uan).Trim().ToUpperInvariant();
            var owners=identities.Where(identity=>Read(identity)==value).ToArray();
            if(owners.Length>1 || owners.Any(identity=>identity.Id!=target.Id))throw new InvalidOperationException("A captured strong identifier belongs to another or multiple employees; rebinding cannot resolve that conflict.");
            if(owners.Length==1)anchored=true;
        }
        if(!anchored)throw new InvalidOperationException("A canonical captured strong identifier must independently belong to this exact employee before rebinding.");
    }

    internal static string RequireOldLocationFinancialReplay(PayRunEmployee saved,PayRunEmployee replay)
    {
        // Native aggregate columns persist at DECIMAL(18,2). The JSON lines below
        // retain and compare their entire decimal precision without tolerance.
        static decimal StoredMoney(decimal amount)=>decimal.Round(amount,2,MidpointRounding.AwayFromZero);
        static Dictionary<string,(string Category,decimal Monthly,decimal Amount)> Lines(string json)
        {
            var output=new Dictionary<string,(string,decimal,decimal)>(StringComparer.Ordinal);
            foreach(var node in JsonNode.Parse(json)?.AsArray()??throw new InvalidOperationException("The original saved financial detail is unavailable."))
            {
                if(node is null)throw new InvalidOperationException("The original financial detail contains an empty line.");
                string Text(string key)=>node[key]?.ToString()??node[char.ToUpperInvariant(key[0])+key[1..]]?.ToString()??"";
                var id=Text("id");var category=Text("category");
                if(string.IsNullOrWhiteSpace(id) || category is not("Earning" or "Reimbursement" or "Deduction" or "Benefit" or "Summary")
                    || !decimal.TryParse(Text("monthlyAmount"),NumberStyles.Float,CultureInfo.InvariantCulture,out var monthly)
                    || !decimal.TryParse(Text("amount"),NumberStyles.Float,CultureInfo.InvariantCulture,out var amount) || !output.TryAdd(id,(category,monthly,amount)))
                    throw new InvalidOperationException("Every saved earning, deduction, employer and summary line needs complete unique numeric replay evidence.");
            }
            if(output.Count==0)throw new InvalidOperationException("The original saved financial lines are missing; no amount is inferred for rebinding.");
            return output;
        }
        var before=Lines(saved.DetailsJson);var calculated=Lines(replay.DetailsJson);
        if(saved.EmployeeId!=replay.EmployeeId || saved.ClientId!=replay.ClientId || saved.PresentDays!=replay.PresentDays || saved.PayableDays!=replay.PayableDays
            || saved.MonthlyGross!=StoredMoney(replay.MonthlyGross) || saved.GrossPay!=StoredMoney(replay.GrossPay) || saved.StatutoryDeductions!=StoredMoney(replay.StatutoryDeductions)
            || saved.OneTimeEarnings!=StoredMoney(replay.OneTimeEarnings) || saved.OneTimeDeductions!=StoredMoney(replay.OneTimeDeductions) || saved.ManualTds!=StoredMoney(replay.ManualTds)
            || saved.NetPay!=StoredMoney(replay.NetPay) || before.Count!=calculated.Count || before.Any(pair=>!calculated.TryGetValue(pair.Key,out var next)||next!=pair.Value))
            throw new InvalidOperationException("The original frozen allocation does not replay every saved Draft financial line, total, employer amount and known attendance input; no row is rebound.");
        return Hash(JsonSerializer.Serialize(new{saved.EmployeeId,saved.ClientId,saved.PresentDays,saved.PayableDays,saved.MonthlyGross,saved.GrossPay,saved.StatutoryDeductions,saved.OneTimeEarnings,saved.OneTimeDeductions,saved.ManualTds,saved.NetPay,
            Lines=before.OrderBy(pair=>pair.Key,StringComparer.Ordinal).Select(pair=>new{Code=pair.Key,pair.Value.Category,pair.Value.Monthly,pair.Value.Amount})},Json));
    }
}
