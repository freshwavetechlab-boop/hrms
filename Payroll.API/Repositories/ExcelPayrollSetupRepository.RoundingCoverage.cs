using System.Globalization;
using System.Text.RegularExpressions;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    // A source can retain an empty ESIC identifier-like column beside its real
    // monetary ESI output. An explicit INFO role alone cannot drop a deduction:
    // prove the exact typed-blank zero and both financial dependency controls
    // for the complete selected source cohort. Amounts/collections stay intact.
    internal static int ProvenBlankAuxiliaryDeductionCount(Plan plan,ExcelPayrollSetupRequest request)
    {
        bool Esic(ExcelPayslipCalculationColumn column)=>Regex.IsMatch(column.SourceHeader+" "+column.Label,@"\bESI(?:C)?\b",RegexOptions.IgnoreCase);
        var columns=plan.Source.Columns;
        var candidates=columns.Where(c=>c.ColumnIndex>=0&&c.ColumnIndex<plan.Source.ColumnCount&&columns.Count(column=>column.ColumnIndex==c.ColumnIndex)==1
            &&c.Kind=="deduction"&&Esic(c)&&request.SourceColumnRoles.GetValueOrDefault(c.ColumnIndex,c.Kind)=="info"
            &&!request.Components.Any(component=>component.ExpectedColumnIndex==c.ColumnIndex||component.SourceColumnIndex==c.ColumnIndex)).ToArray();
        if(candidates.Length==0||request.RowIds.Count==0||request.RowIds.Count!=request.RowIds.Distinct().Count())return 0;
        var totals=columns.Where(c=>request.SourceColumnRoles.GetValueOrDefault(c.ColumnIndex,c.Kind)=="deductionTotal").ToArray();
        var nets=columns.Where(c=>request.SourceColumnRoles.GetValueOrDefault(c.ColumnIndex,c.Kind)=="netPay").ToArray();
        var actual=request.Components.Where(c=>c.Category=="Deduction"&&c.ExpectedColumnIndex is int index
            &&columns.Count(column=>column.ColumnIndex==index&&Esic(column)&&request.SourceColumnRoles.GetValueOrDefault(index,column.Kind)=="deduction")==1).ToArray();
        if(totals.Length!=1||nets.Length!=1||actual.Length!=1)return 0;
        var actualIndex=actual[0].ExpectedColumnIndex!.Value;
        var count=0;
        foreach(var candidate in candidates)
        {
            var valid=true;
            foreach(var rowId in request.RowIds)
            {
                var saved=plan.Batch.Rows.Where(row=>row.Id==rowId).ToArray();
                if(saved.Length!=1){valid=false;break;}
                var rows=plan.Source.Rows.Where(row=>row.SourceRow==saved[0].SourceRow).ToArray();
                var amounts=saved[0].Deductions.Where(amount=>Normalize(amount.Label)==Normalize(candidate.Label)).ToArray();
                var blank=rows.Length==1?rows[0].Cells.ElementAtOrDefault(candidate.ColumnIndex):null;
                if(blank is not{DataType:"Blank",Value:""}||!string.IsNullOrWhiteSpace(blank.FormulaText)||blank.Error is not null||blank.CalculationError is not null||blank.MissingCachedValue
                    ||amounts.Length!=1||amounts[0].Amount!=0){valid=false;break;}
                var sourceRow=rows[0];
                HashSet<int>? Graph(int index,HashSet<int>? visiting=null)
                {
                    visiting??=[];
                    if(index<0||index>=plan.Source.ColumnCount||visiting.Count>=20||!visiting.Add(index))return null;
                    var metadata=columns.Where(column=>column.ColumnIndex==index).ToArray();
                    var cell=sourceRow.Cells.ElementAtOrDefault(index);
                    if(metadata.Length!=1||cell is null||cell.Error is not null||cell.CalculationError is not null||cell.MissingCachedValue)return null;
                    var role=request.SourceColumnRoles.GetValueOrDefault(index,metadata[0].Kind);
                    if(role is "info" or "ignore"&&ProtectedRecurringInputHeader(metadata[0].SourceHeader+" "+metadata[0].Label))return null;
                    if(string.IsNullOrWhiteSpace(cell.FormulaText))
                    {
                        if(cell.DataType=="Number"&&decimal.TryParse(cell.Value,NumberStyles.Number|NumberStyles.AllowExponent,CultureInfo.InvariantCulture,out _))return [index];
                        if(cell is{DataType:"Blank",Value:""}&&(IsReviewedRecurringBlankOperand(request,saved[0].Id,index)||metadata[0].BlankAsZero
                            ||request.Components.Any(component=>component.ExpectedColumnIndex==index&&component.BlankInputAsZero)))return [index];
                        return null;
                    }
                    if(cell.DataType!="Number"||!decimal.TryParse(cell.Value,NumberStyles.Number|NumberStyles.AllowExponent,CultureInfo.InvariantCulture,out _))return null;
                    var formula=Regex.Replace(cell.FormulaText.TrimStart('=').ToUpperInvariant(),@"[\s$]","");
                    if(formula.Length>1000||Regex.IsMatch(formula,@"[!\[\]""\\]"))return null;
                    try
                    {
                        formula=Regex.Replace(formula,@"SUM\(([A-Z]+)(\d+):([A-Z]+)(\d+)\)",match=>{
                            if(int.Parse(match.Groups[2].Value,CultureInfo.InvariantCulture)!=sourceRow.SourceRow||match.Groups[2].Value!=match.Groups[4].Value)throw new InvalidOperationException();
                            var first=TransferColumn(match.Groups[1].Value);var last=TransferColumn(match.Groups[3].Value);
                            if(last<first||last-first>100)throw new InvalidOperationException();
                            return "("+string.Join("+",Enumerable.Range(first,last-first+1).Select(column=>TransferLetter(column)+sourceRow.SourceRow))+")";
                        });
                        var refs=Regex.Matches(formula,@"\b([A-Z]{1,3})([1-9]\d*)\b");
                        if(refs.Any(match=>int.Parse(match.Groups[2].Value,CultureInfo.InvariantCulture)!=sourceRow.SourceRow))return null;
                        PayrollFormulaEvaluator.Validate(Regex.Replace(formula,@"\b([A-Z]{1,3})([1-9]\d*)\b","1"));
                        var graph=new HashSet<int>{index};
                        foreach(Match reference in refs){var child=Graph(TransferColumn(reference.Groups[1].Value),new(visiting));if(child is null)return null;graph.UnionWith(child);}
                        return graph;
                    }
                    catch(Exception error)when(error is InvalidOperationException or FormatException or OverflowException){return null;}
                }
                var actualCell=sourceRow.Cells.ElementAtOrDefault(actualIndex);
                if(string.IsNullOrWhiteSpace(actualCell?.FormulaText)){valid=false;break;}
                var total=Graph(totals[0].ColumnIndex);var net=Graph(nets[0].ColumnIndex);var output=Graph(actualIndex);
                if(total is null||net is null||output is null||!total.Contains(actualIndex)||!net.Contains(actualIndex)
                    ||total.Contains(candidate.ColumnIndex)||net.Contains(candidate.ColumnIndex)||output.Contains(candidate.ColumnIndex)){valid=false;break;}
            }
            if(valid)count++;
        }
        return count;
    }
}
