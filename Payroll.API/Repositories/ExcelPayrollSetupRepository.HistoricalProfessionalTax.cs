using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal static void ValidateHistoricalPeriodProfessionalTaxReview(HistoricalPeriodSalaryRequest request)
    {
        if(request.PeriodProfessionalTaxReview is not { } review)return;
        if(review.Version!=1 || !review.Reviewed || !Regex.IsMatch(review.SourceHash??"",@"\A[a-f0-9]{64}\z")
            || string.IsNullOrWhiteSpace(review.Reason) || review.Reason.Length>500 || review.Rows is null
            || review.Rows.Count!=request.Assignments.Count || review.Rows.Count is <1 or >100
            || review.Rows.Any(row=>row is null || row.Amount!=0 || !request.Assignments.Any(a=>a.EmployeeId==row.EmployeeId&&a.RowId==row.RowId))
            || review.Rows.Select(row=>row.EmployeeId).Distinct().Count()!=review.Rows.Count || review.Rows.Select(row=>row.RowId).Distinct(StringComparer.Ordinal).Count()!=review.Rows.Count)
            throw new InvalidOperationException("Closed historical PT needs an explicit version-one source-authority zero review, bounded reason, exact source hash and every selected employee/source row once. Automatic PT remains the default.");
    }

    private static void RequireHistoricalPtSourceClosure(HistoricalPeriodSalaryRequest request,ExcelPayslipCalculationSource source,ExcelPayslipRow row,string sourceHash)
    {
        ValidateHistoricalPeriodProfessionalTaxReview(request);
        if(request.PeriodProfessionalTaxReview is not { } review)return;
        if(review.SourceHash!=sourceHash || source.Columns.Any(c=>Regex.IsMatch(c.SourceHeader+" "+c.Label,@"\bPT\b|professional\s*tax|PT[_\s-]*LWF",RegexOptions.IgnoreCase))
            || request.Setup.Components.Any(c=>Services.ReviewedPeriodProfessionalTax.IsPt(c.Code,c.StatutoryType)||c.Code=="PT_LWF_WC"))
            throw new InvalidOperationException("This closed-period PT review needs the exact fresh source with no PT/mixed-PT column or configured PT component. Existing explicit PT rules require their own review.");
        var net=source.Columns.Where(c=>c.Kind=="netPay").ToArray();var captured=source.Rows.Single(r=>r.SourceRow==row.SourceRow);
        var formula=net.Length==1?captured.Cells.ElementAtOrDefault(net[0].ColumnIndex)?.FormulaText:null;
        var expected=request.Setup.Components.Where(c=>c.Category is "Earning" or "Reimbursement" or "Deduction")
            .ToDictionary(c=>c.ExpectedColumnIndex!.Value,c=>c.Category=="Deduction"?-1:1);
        if(string.IsNullOrWhiteSpace(formula)||!HistoricalNetClosure(formula,row.SourceRow,expected))
            throw new InvalidOperationException("Source net must prove the complete mapped earning-minus-deduction identity by exact same-row additive cell references. Cached equality cannot establish period PT zero.");
    }

    // Exact linear symbolic proof at already-verified financial output cells;
    // no input sampling, cached zero substitution, hidden fee or source alias.
    internal static bool HistoricalNetClosure(string formula,int sourceRow,IReadOnlyDictionary<int,int> expected)
    {
        try
        {
            if(formula.Length>4096||sourceRow<1||expected.Count is <1 or >100)return false;
            var text=Regex.Replace(formula.TrimStart('=').ToUpperInvariant(),@"[\s$]","");
            text=Regex.Replace(text,@"SUM\(([A-Z]+)([0-9]+):([A-Z]+)([0-9]+)\)",m=>{
                if(m.Groups[2].Value!=sourceRow.ToString(CultureInfo.InvariantCulture)||m.Groups[4].Value!=m.Groups[2].Value)throw new InvalidOperationException();
                var from=TransferColumn(m.Groups[1].Value);var to=TransferColumn(m.Groups[3].Value);if(to<from||to-from>100)throw new InvalidOperationException();
                return "("+string.Join("+",Enumerable.Range(from,to-from+1).Select(c=>TransferLetter(c)+sourceRow))+")";
            });
            if(text.Length>16000)return false;
            var index=0;var depth=0;Dictionary<int,int> Expression()
            {
                var result=Term();while(index<text.Length&&text[index] is '+' or '-') {var sign=text[index++]=='+'?1:-1;foreach(var(c,value)in Term())result[c]=result.GetValueOrDefault(c)+sign*value;}return result;
            }
            Dictionary<int,int> Term()
            {
                var sign=1;while(index<text.Length&&text[index] is '+' or '-')if(text[index++]=='-')sign=-sign;
                Dictionary<int,int> value;
                if(index<text.Length&&text[index]=='('){if(++depth>64)throw new InvalidOperationException();index++;value=Expression();if(index>=text.Length||text[index++]!=')')throw new InvalidOperationException();depth--;}
                else
                {
                    var match=Regex.Match(text[index..],@"\A([A-Z]+)([0-9]+)");
                    if(!match.Success||match.Groups[2].Value!=sourceRow.ToString(CultureInfo.InvariantCulture))throw new InvalidOperationException();
                    var column=TransferColumn(match.Groups[1].Value);if(!expected.ContainsKey(column))throw new InvalidOperationException();index+=match.Length;value=new(){{column,1}};
                }
                return value.ToDictionary(p=>p.Key,p=>sign*p.Value);
            }
            var actual=Expression().Where(p=>p.Value!=0).ToDictionary(p=>p.Key,p=>p.Value);
            return index==text.Length&&actual.Count==expected.Count&&expected.All(p=>actual.GetValueOrDefault(p.Key)==p.Value);
        }
        catch(Exception error)when(error is InvalidOperationException or ArgumentException or FormatException or OverflowException){return false;}
    }

    private static void RequireHistoricalPtExactDisplays(ExcelPayrollSetupPreviewRow calculated,ExcelPayslipRow source)
    {
        if(calculated.Lines.Any(line=>!line.SourceAmount.HasValue||line.CalculatedAmount!=SourceCurrencyDisplay(line.SourceAmount.Value))
            || calculated.Gross!=SourceCurrencyDisplay(source.DeclaredGross??source.Earnings.Sum(x=>x.Amount))
            || calculated.Deductions!=SourceCurrencyDisplay(source.DeclaredDeductions??source.Deductions.Sum(x=>x.Amount))
            || calculated.Benefits!=SourceCurrencyDisplay(source.EmployerContributions.Sum(x=>x.Amount))||calculated.Net!=SourceCurrencyDisplay(source.NetPay))
            throw new InvalidOperationException("The composed closed-period PT review requires every source component/employer amount and gross/deduction/employer/net total to match at currency precision. A one-paise difference cannot be waived.");
    }

    internal static async Task RequireHistoricalPtIdentityAsync(MySqlConnection db,MySqlTransaction tx,int clientId,
        HistoricalPeriodSalaryRequest request,string rowId,int employeeId,string expectedSourceHash,HistoricalSourceIdentityEvidence? expectedIdentity=null)
    {
        var(batch,source)=await ReadSourceAsync(db,tx,clientId,request.Setup.BatchId);
        var hash=Hash(JsonSerializer.Serialize(new{Batch=batch,Source=source},Json));
        if(hash!=expectedSourceHash)throw new InvalidOperationException("Historical PT source identity changed.");
        var row=batch.Rows.Single(r=>r.Id==rowId);var identities=await ReadIdentitiesAsync(db,tx,clientId,batch.Month);
        var target=identities.Single(e=>e.Id==employeeId);
        var clone=JsonSerializer.Deserialize<HistoricalPeriodSalaryRequest>(JsonSerializer.Serialize(request,Json),Json)!;
        clone.Assignments=clone.Assignments.Where(a=>a.EmployeeId==employeeId&&a.RowId==rowId).ToList();clone.Setup.RowIds=[rowId];
        if(clone.SourceIdentityReview is { } identityReview){identityReview.Bindings=identityReview.Bindings.Where(b=>b.RowId==rowId&&b.EmployeeId==employeeId).ToList();if(identityReview.Bindings.Count==0)clone.SourceIdentityReview=null;}
        var reviewed=await ReviewHistoricalSourceIdentitiesAsync(db,tx,clone,batch,source,identities,hash,false);
        var verified=reviewed.GetValueOrDefault(rowId);
        if(expectedIdentity is not null&&(verified is null||JsonSerializer.Serialize(verified.Evidence,Json)!=JsonSerializer.Serialize(expectedIdentity,Json)))
            throw new InvalidOperationException("The authenticated historical source identity/ownership evidence changed.");
        if(HistoricalIdentityIssues(batch,source,row,target,identities,verified).Count>0)throw new InvalidOperationException("Historical period PT needs the same verified source identity as its closed salary receipt.");
    }
}
