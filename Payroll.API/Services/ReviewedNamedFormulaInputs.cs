using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

public static class ReviewedNamedFormulaInputs
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value,Json)))).ToLowerInvariant();
    public static string ProfileKey(string template) => "named-input-t:"+template;
    public static string AssignmentKey(string assignment) => "named-input-a:"+assignment;
    public static string RulesHash(string native,string declaration) => Hash(new{native,declaration});
    public static string DeclarationHash(int client,string template,string rules,List<NamedFormulaInputDefinition> definitions)
        => Hash(new{client,template,rules,definitions});

    public static void ValidateDefinitions(IReadOnlyList<NamedFormulaInputDefinition> definitions,IEnumerable<string> componentTokens)
    {
        if(definitions is null||definitions.Count is <1 or >16||definitions.Any(d=>d is null))throw new InvalidOperationException("Review 1–16 explicit named formula operands.");
        var forbidden=PayrollValueCatalogue.Names.Concat(new[]{"SUM","MIN","MAX","ROUND","ROUNDUP","ROUNDDOWN","ABS","IF","IFERROR","AND","OR","TRUE","FALSE"})
            .Concat(componentTokens.SelectMany(token=>new[]{token,token+"_MONTHLY",token+"_EARNED"})).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(definitions.Select(d=>d.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=definitions.Count)
            throw new InvalidOperationException("Named operands must have unique keys without case aliases.");
        foreach(var d in definitions)
            if(d is null||!Regex.IsMatch(d.Key??"","\\A[A-Z][A-Z0-9_]{1,63}\\z")||forbidden.Contains(d.Key!)||d.Key!.EndsWith("_MONTHLY",StringComparison.Ordinal)||d.Key.EndsWith("_EARNED",StringComparison.Ordinal)
                ||string.IsNullOrWhiteSpace(d.Label)||d.Label.Length>120||d.Unit is not("Amount" or "PercentPoints" or "Hours")||d.Purpose!="FormulaOperandOnly"||!d.Required
                ||d.Precision is <0 or >8||d.Minimum<0||d.Maximum<d.Minimum||d.Maximum>1_000_000_000m||d.Unit=="PercentPoints"&&d.Maximum>100)
                throw new InvalidOperationException("A named operand has an invalid/reserved key, unit, range or FormulaOperandOnly declaration.");
    }
    public static void ValidateContext(NamedFormulaInputContext context,IEnumerable<string> componentTokens)
    {
        if(context is null)throw new InvalidOperationException("Named operand context is missing.");
        ValidateDefinitions(context.Definitions,componentTokens);
        if(context.Values is null||context.Values.Count!=context.Definitions.Count||context.Values.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=context.Values.Count)
            throw new InvalidOperationException("Every declared named operand needs one explicit value; missing is not zero.");
        foreach(var d in context.Definitions)
            if(!context.Values.TryGetValue(d.Key,out var value)||value<d.Minimum||value>d.Maximum||decimal.Round(value,d.Precision)!=value)
                throw new InvalidOperationException(d.Key+": supply the reviewed operand with its exact unit, bounds and precision; values are never rounded or defaulted.");
        if(context.Values.Keys.Any(key=>!context.Definitions.Any(d=>d.Key==key)))throw new InvalidOperationException("Undeclared named operand values are not allowed.");
    }
    public static (NamedFormulaInputContext Context,List<NamedFormulaInputCell> Cells) Bind(NamedFormulaInputProfile profile,
        IReadOnlyList<NamedFormulaInputBinding> bindings,ExcelPayslipCalculationSource source,ExcelPayslipRow row,IEnumerable<string> componentTokens)
    {
        ValidateDefinitions(profile.Definitions,componentTokens);
        if(bindings is null||bindings.Any(b=>b is null)||bindings.Count!=profile.Definitions.Count||bindings.Select(b=>b.Key).Distinct(StringComparer.Ordinal).Count()!=bindings.Count
            ||bindings.Select(b=>b.ColumnIndex).Distinct().Count()!=bindings.Count)throw new InvalidOperationException("Bind every named operand to one distinct reviewed source INFO literal.");
        var captured=source.Rows.Single(r=>r.SourceRow==row.SourceRow);var values=new Dictionary<string,decimal>(StringComparer.Ordinal);var cells=new List<NamedFormulaInputCell>();
        foreach(var definition in profile.Definitions)
        {
            var binding=bindings.SingleOrDefault(b=>b.Key==definition.Key)??throw new InvalidOperationException("A declared source operand binding is missing.");
            var column=source.Columns.SingleOrDefault(c=>c.ColumnIndex==binding.ColumnIndex);
            var cell=binding.ColumnIndex>=0?captured.Cells.ElementAtOrDefault(binding.ColumnIndex):null;
            var header=source.Rows.SingleOrDefault(r=>r.SourceRow==binding.HeaderRow)?.Cells.ElementAtOrDefault(binding.ColumnIndex);
            if(column is null||column.Kind is not("info" or "ignore")||cell is null||cell.DataType is not("" or "Text" or "Number")||!string.IsNullOrWhiteSpace(cell.FormulaText)||cell.Error is not null||cell.CalculationError is not null||cell.MissingCachedValue
                ||binding.HeaderRow<=0||binding.HeaderRow>=row.SourceRow||header is null||header.DataType is not("" or "Text")||!Regex.IsMatch(header.Value,@"\p{L}")||!string.IsNullOrWhiteSpace(header.FormulaText)||header.Error is not null||header.CalculationError is not null||header.MissingCachedValue
                ||string.IsNullOrWhiteSpace(binding.SourceHeader)||binding.SourceHeader.Length>200||header.Value!=binding.SourceHeader||Regex.IsMatch(binding.SourceHeader,"^Original [A-Z]+$",RegexOptions.IgnoreCase)
                ||Regex.IsMatch(column.SourceHeader+" "+column.Label+" "+binding.SourceHeader,"employee|\\bemp\\b|\\buan\\b|\\bpan\\b|aadh?aar|bank|account|ifsc|identifier|date|birth|joining|e-?mail|earned|net.?pay|payable|present|attendance",RegexOptions.IgnoreCase)
                ||RecurringInputHeaderGuards.HasContactIdentifier(column.SourceHeader+" "+column.Label+" "+binding.SourceHeader)
                ||!decimal.TryParse(cell.Value,NumberStyles.Number|NumberStyles.AllowExponent,CultureInfo.InvariantCulture,out var value))
                throw new InvalidOperationException(definition.Key+": use an independent error-free INFO literal, never an identity, days, financial output or formula cache.");
            values.Add(definition.Key,value);cells.Add(new(definition.Key,row.SourceRow,binding.ColumnIndex,column.SourceHeader,column.Kind,cell.Value,Hash(cell),binding.HeaderRow,binding.SourceHeader));
        }
        var context=new NamedFormulaInputContext{Definitions=profile.Definitions,Values=values,DeclarationHash=profile.DeclarationHash};ValidateContext(context,componentTokens);return(context,cells);
    }
    public static string Signature(string legacy,NamedFormulaInputContext? context) => context is null?legacy:Hash(new{legacy,context.DeclarationHash,values=context.Values.OrderBy(p=>p.Key,StringComparer.Ordinal)});
}
