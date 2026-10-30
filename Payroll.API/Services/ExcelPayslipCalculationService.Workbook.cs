using System.Globalization;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

public static partial class ExcelPayslipCalculationService
{
    internal static (decimal Value,IReadOnlyList<ExcelPayslipLookupTrace> Traces) EvaluateCapturedCell(ExcelPayslipCalculationSource source,string address,bool requireExplicitCells=false)
        => EvaluateCapturedCellAtInputBoundary(source,address,null,requireExplicitCells);

    // Only the source-review caller may supply independently verified period
    // inputs. Ordinary workbook recalculation retains its complete closure.
    internal static (decimal Value,IReadOnlyList<ExcelPayslipLookupTrace> Traces) EvaluateCapturedCellAtInputBoundary(
        ExcelPayslipCalculationSource source,string address,IReadOnlyDictionary<string,decimal>? reviewedInputs,bool requireExplicitCells=true,
        bool useReviewedBlankColumns=false)
    {
        ValidateWorkbookDependencies(source);
        var cells=source.Rows.SelectMany(r=>r.Cells.Select((c,i)=>(Cell:c,Address:Address(i,r.SourceRow)))).Where(v=>v.Cell is not null).ToDictionary(v=>v.Address,v=>v.Cell!,StringComparer.OrdinalIgnoreCase);
        var blankColumns=source.Columns.Where(c=>c.BlankAsZero).Select(c=>c.ColumnIndex).ToHashSet();
        HashSet<string> reviewedBlanks=useReviewedBlankColumns?source.Rows.SelectMany(r=>r.Cells.Select((c,i)=>(Cell:c,Column:i,Address:Address(i,r.SourceRow))))
            .Where(v=>blankColumns.Contains(v.Column)&&v.Cell is {} c&&!c.MissingCachedValue&&string.IsNullOrWhiteSpace(c.Value)
                &&string.IsNullOrWhiteSpace(c.FormulaText)&&c.Error is null&&string.IsNullOrEmpty(c.CalculationError)).Select(v=>v.Address).ToHashSet(StringComparer.OrdinalIgnoreCase):[];
        var evaluator=new WorkbookEvaluator(cells,source.SheetName,[],reviewedBlanks,reviewedInputs,source,requireExplicitCells);
        var result=evaluator.Read(address);return(result,evaluator.Traces.ToArray());
    }
    // Excel calculation errors are catchable by IFERROR. Missing/unreviewed dependencies and resource limits are not.
    private sealed class WorkbookError(string code,string? detail=null) : InvalidOperationException(detail??code) { public string Code { get; } = code; }
    private enum ScalarKind { Number, Text, Boolean, Blank, LegacyNumber }
    private sealed record Scalar(ScalarKind Kind, decimal Numeric = 0, string Text = "")
    {
        public static Scalar N(decimal value) => new(ScalarKind.Number, EnsureBounded(value));
        public decimal Number() => Kind switch {
            ScalarKind.Number or ScalarKind.LegacyNumber or ScalarKind.Boolean => Numeric,
            ScalarKind.Blank => 0,
            _ => TryNumber(Text,out var value) ? value : throw new WorkbookError("#VALUE!","Required input is not a valid number (#VALUE!).")
        };
        public bool Truth() => Kind==ScalarKind.Text ? bool.TryParse(Text,out var b) ? b : throw new WorkbookError("#VALUE!") : Number()!=0;
    }
    private sealed record WorkbookSheet(string Workbook,string Name,Dictionary<string,ExcelPayslipCalculationCell> Cells,int Columns,int LastRow,bool Complete);
    private sealed record WorkbookRange(WorkbookSheet Sheet,int FirstColumn,int LastColumn,int FirstRow,int LastRow,bool WholeColumns)
    { public string Label => $"{Sheet.Workbook}/{Sheet.Name}!{Address(FirstColumn,FirstRow)}:{Address(LastColumn,LastRow)}"; }
    private sealed record WorkbookNode(Func<Scalar> Scalar,Func<WorkbookRange>? Range=null,bool Reference=false);

    private sealed class WorkbookEvaluator
    {
        private readonly WorkbookSheet root;
        private readonly Dictionary<string,WorkbookSheet> sheets=new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string,ExcelPayslipExternalMapping> mappings=new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string,Scalar> cache=new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string,HashSet<string>> dependencies=new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> active=new(StringComparer.OrdinalIgnoreCase),attendance,blanks;
        private readonly Stack<string> stack=new();
        private readonly IReadOnlyDictionary<string,decimal>? fixedInputs;
        private readonly bool requireExplicitCells;
        private int operations;
        public List<ExcelPayslipLookupTrace> Traces { get; }=[];
        public WorkbookEvaluator(Dictionary<string,ExcelPayslipCalculationCell> cells,string sheet,HashSet<string> attendanceInputs,HashSet<string> reviewedBlanks,IReadOnlyDictionary<string,decimal>? overrides,ExcelPayslipCalculationSource? source=null,bool requireExplicitCells=false)
        {
            this.requireExplicitCells=requireExplicitCells;
            var positions=cells.Keys.Select(ParseAddress).ToArray();
            root=new(source?.WorkbookKey??"current",sheet,cells,source?.ColumnCount??(positions.Length==0?1:positions.Max(p=>p.Column)+1),positions.Length==0?1:positions.Max(p=>p.Row),source?.CompleteSheetCapture??false);
            sheets.Add(SheetKey(root.Workbook,root.Name),root);
            attendance=attendanceInputs.Select(a=>Key(root,NormalizeAddress(a))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            blanks=reviewedBlanks.Select(a=>Key(root,NormalizeAddress(a))).ToHashSet(StringComparer.OrdinalIgnoreCase);fixedInputs=overrides;
            if(source is null)return;
            ValidateWorkbookDependencies(source);
            foreach(var supplied in source.DependencySheets) {
                var map=supplied.Rows.SelectMany(r=>r.Cells.Select((c,i)=>(Cell:c,Address:Address(i,r.SourceRow)))).Where(v=>v.Cell is not null).ToDictionary(v=>v.Address,v=>v.Cell!,StringComparer.OrdinalIgnoreCase);
                var view=new WorkbookSheet(supplied.WorkbookKey,supplied.SheetName,map,supplied.ColumnCount,supplied.Rows.Count==0?1:supplied.Rows.Max(r=>r.SourceRow),supplied.CompleteSheetCapture);
                if(!sheets.TryAdd(SheetKey(view.Workbook,view.Name),view))throw new InvalidOperationException("Dependency worksheet names are ambiguous. Review the supplied workbook mapping.");
            }
            foreach(var m in source.ExternalMappings)mappings.Add(m.FromWorkbookKey+"|"+m.Reference,m);
        }
        private static string SheetKey(string workbook,string name)=>workbook+"|"+name;
        private static string Key(WorkbookSheet sheet,string address)=>SheetKey(sheet.Workbook,sheet.Name)+"!"+address;
        private void Work(){if(++operations>150000)throw new InvalidOperationException("Workbook calculation work limit exceeded; narrow the dependency ranges.");}
        private void Link(string key){if(stack.TryPeek(out var parent))dependencies[parent].UnionWith(dependencies[key]);}
        public bool DependsOn(string address,string input)=>dependencies.TryGetValue(Key(root,NormalizeAddress(address)),out var values)&&values.Contains(Key(root,NormalizeAddress(input)));
        public decimal Read(string reference)=>ReadScalar(root,reference).Number();
        private (WorkbookSheet Sheet,string Address) Locate(WorkbookSheet current,string reference)
        {
            var bang=reference.LastIndexOf('!');if(bang<0)return(current,NormalizeAddress(reference));
            var prefix=reference[..bang].Trim('\'').Replace("''","'");var workbook=current.Workbook;var sheetName=prefix;
            if(prefix.StartsWith('[')) {
                var end=prefix.IndexOf(']');if(end<2)throw new InvalidOperationException("Malformed external workbook reference.");
                var alias=prefix[..(end+1)];sheetName=prefix[(end+1)..];
                if(!mappings.TryGetValue(workbook+"|"+alias,out var mapping)||!mapping.Reviewed)throw new InvalidOperationException("Unsupported/unresolved external workbook input is not reviewed/mapped. Supply the original workbook and map this reference; no file/network lookup or cached-value fallback is performed.");
                workbook=mapping.ToWorkbookKey;
            }
            if(!sheets.TryGetValue(SheetKey(workbook,sheetName),out var target))throw new InvalidOperationException("Cross-worksheet input is not captured. Supply/map its dependency snapshot before recalculating.");
            return(target,NormalizeAddress(reference[(bang+1)..]));
        }
        private Scalar ReadScalar(WorkbookSheet current,string reference,bool tableBlank=false)
        {
            Work();var (sheet,address)=Locate(current,reference);var key=Key(sheet,address);
            if(cache.TryGetValue(key,out var value)){Link(key);return value;}
            if(active.Count>=128||!active.Add(key))throw new InvalidOperationException("Circular or excessive workbook dependency. Review the referenced cells.");
            dependencies[key]=attendance.Contains(key)?[key]:[];stack.Push(key);
            try {
                if(sheet==root&&fixedInputs is not null&&fixedInputs.TryGetValue(address,out var fixedValue))return cache[key]=Scalar.N(fixedValue);
                if(blanks.Contains(key))return cache[key]=Scalar.N(0);
                if(!sheet.Cells.TryGetValue(address,out var cell)) {
                    var (column,row)=ParseAddress(address);
                    if(!requireExplicitCells&&sheet.Complete&&column<sheet.Columns&&row<=sheet.LastRow)return cache[key]=new(ScalarKind.Blank);
                    throw new InvalidOperationException($"Required input {address} is missing from the captured worksheet. Review its source range.");
                }
                if(requireExplicitCells&&cell.MissingCachedValue&&string.IsNullOrWhiteSpace(cell.FormulaText))throw new InvalidOperationException("Required saved worksheet input is missing its captured value; missing data never means zero.");
                if(!string.IsNullOrWhiteSpace(cell.CalculationError))throw new InvalidOperationException("Saved formula cannot be recalculated: "+cell.CalculationError);
                if(!string.IsNullOrWhiteSpace(cell.FormulaText))return cache[key]=new WorkbookParser(cell.FormulaText,this,sheet).Parse().Scalar();
                if(cell.Error is not null)throw new WorkbookError(KnownError(cell.Error));
                value=cell.DataType switch {
                    "Blank"=>new(ScalarKind.Blank),
                    "Text"=>new(ScalarKind.Text,Text:cell.Value),
                    "Boolean"=>new(ScalarKind.Boolean,cell.Value is "1" or "TRUE" or "true"?1:0),
                    "Number"=>TryNumber(cell.Value,out var n)?Scalar.N(n):throw new WorkbookError("#VALUE!"),
                    _=>TryNumber(cell.Value,out var number)?new(ScalarKind.LegacyNumber,number):string.IsNullOrEmpty(cell.Value)&&tableBlank&&sheet.Complete?new(ScalarKind.Blank):new(ScalarKind.Text,Text:cell.Value)
                };
                return cache[key]=value;
            } catch(DivideByZeroException){throw new WorkbookError("#DIV/0!");}
              catch(OverflowException){throw new WorkbookError("#NUM!");}
            finally{active.Remove(key);stack.Pop();Link(key);}
        }
        private static string KnownError(string raw)=>new[]{"#N/A","#VALUE!","#REF!","#DIV/0!","#NUM!","#NAME?","#NULL!"}.Contains(raw)?raw:"#VALUE!";
        private WorkbookRange Range(WorkbookSheet current,string first,string last)
        {
            var bang=first.LastIndexOf('!');var prefix=bang<0?"":first[..(bang+1)];var start=bang<0?first:first[(bang+1)..];
            if(last.Contains('!')){var split=last.LastIndexOf('!');if(!string.Equals(prefix,last[..(split+1)],StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Ranges cannot span different worksheets.");last=last[(split+1)..];}
            var whole=Regex.IsMatch(start,@"^\$?[A-Z]{1,3}$",RegexOptions.IgnoreCase)&&Regex.IsMatch(last,@"^\$?[A-Z]{1,3}$",RegexOptions.IgnoreCase);
            var target=Locate(current,prefix+(whole?start+"1":start)).Sheet;
            var (c1,r1)=ParseAddress(whole?start+"1":start);var (c2,r2)=ParseAddress(whole?last+target.LastRow:last);
            if(c1>c2||r1>r2||(long)(c2-c1+1)*(r2-r1+1)>10000)throw new InvalidOperationException("Dependency range is reversed or exceeds 10,000 cells. Supply a bounded reviewed range.");
            return new(target,c1,c2,r1,r2,whole);
        }
        private Scalar TableValue(WorkbookRange range,int column,int row)=>ReadScalar(range.Sheet,Address(column,row),true);
        private static bool MatchExact(Scalar needle,Scalar item)
        {
            if(needle.Kind==ScalarKind.LegacyNumber||item.Kind==ScalarKind.LegacyNumber)throw new InvalidOperationException("Lookup key number/text type is unavailable in this old snapshot. Reimport typed source cells; no coercion is guessed.");
            if(needle.Kind==ScalarKind.Blank)needle=Scalar.N(0);if(item.Kind==ScalarKind.Blank)item=Scalar.N(0);
            if(needle.Kind!=item.Kind)return false;
            if(needle.Kind!=ScalarKind.Text)return needle.Numeric==item.Numeric;
            if(needle.Text.Length>500)throw new InvalidOperationException("Lookup text exceeds the bounded matcher limit.");
            var pattern="^";for(var i=0;i<needle.Text.Length;i++) {
                var c=needle.Text[i];if(c=='~'&&i+1<needle.Text.Length)pattern+=Regex.Escape(needle.Text[++i].ToString());
                else pattern+=c=='*'?".*":c=='?'?".":Regex.Escape(c.ToString());
            }
            try { return Regex.IsMatch(item.Text,pattern+"$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50)); } catch(RegexMatchTimeoutException) { throw new InvalidOperationException("Lookup wildcard matcher work limit exceeded; simplify the reviewed key pattern."); }
        }
        private Scalar Lookup(List<WorkbookNode> args)
        {
            if(args.Count is <3 or >4)throw new WorkbookError("#VALUE!");
            var key=args[0].Scalar();var range=args[1].Range?.Invoke()??throw new InvalidOperationException("VLOOKUP needs a captured rectangular range; dynamic ranges are not supported.");
            if(key.Kind==ScalarKind.Blank||key.Kind==ScalarKind.Text&&key.Text.Length==0)throw new InvalidOperationException("Blank lookup keys require reviewed source resolution; attendance is not inferred as zero.");
            var column=decimal.Truncate(args[2].Scalar().Number());if(column<1)throw new WorkbookError("#VALUE!");if(column>range.LastColumn-range.FirstColumn+1)throw new WorkbookError("#REF!");
            var approximate=args.Count==3||args[3].Scalar().Truth();
            if(range.WholeColumns&&(approximate||key.Kind==ScalarKind.Blank||key.Kind==ScalarKind.Text&&key.Text.Length==0||key.Kind is ScalarKind.Number or ScalarKind.LegacyNumber&&key.Numeric==0))
                throw new InvalidOperationException("This whole-column lookup requires an explicit bounded range for approximate/blank/zero keys.");
            if(approximate&&key.Kind!=ScalarKind.Number)throw new InvalidOperationException("Approximate VLOOKUP currently requires typed numeric sorted keys. Text/collation matching needs an explicit reviewed mapping.");
            int? selected=null;var matching=0;decimal? previous=null;
            for(var row=range.FirstRow;row<=range.LastRow;row++) {
                var item=TableValue(range,range.FirstColumn,row);
                if(approximate) {
                    if(item.Kind!=ScalarKind.Number||previous.HasValue&&item.Numeric<previous.Value)throw new InvalidOperationException("Approximate lookup keys are not a captured ascending numeric sequence. Review the source range; no result is guessed.");
                    previous=item.Numeric;if(item.Numeric<=key.Numeric)selected=row;
                    if(item.Numeric==key.Numeric)matching++;
                } else if(MatchExact(key,item)){matching++;selected??=row;}
            }
            if(Traces.Count>=10000)throw new InvalidOperationException("Lookup provenance limit exceeded.");
            Traces.Add(new(stack.TryPeek(out var formula)?formula:"",range.Label,approximate?"Approximate (TRUE/default)":"Exact (FALSE)",selected,matching,selected.HasValue?matching>1?"Duplicate keys; Excel selection preserved — review required":"Matched":"#N/A"));
            if(!selected.HasValue)throw new WorkbookError("#N/A");
            var result=TableValue(range,range.FirstColumn+(int)column-1,selected.Value);
            if(result.Kind==ScalarKind.Blank){Traces[^1]=Traces[^1] with {Status=Traces[^1].Status+"; matched blank result becomes zero under Excel semantics — native attendance input requires review"};return Scalar.N(0);}
            return result;
        }
        public void UpdateCachedValues()
        {
            foreach(var pair in cache) {
                if(blanks.Contains(pair.Key)||pair.Value.Kind is not (ScalarKind.Number or ScalarKind.LegacyNumber or ScalarKind.Boolean))continue;
                var split=pair.Key.LastIndexOf('!');if(split<0||!sheets.TryGetValue(pair.Key[..split],out var sheet)||!sheet.Cells.TryGetValue(pair.Key[(split+1)..],out var cell))continue;
                if(string.IsNullOrWhiteSpace(cell.FormulaText))continue; // Never flatten lookup keys, original inputs or text-typed cells.
                cell.Value=pair.Value.Number().ToString("G29",CultureInfo.InvariantCulture);cell.Error=null;cell.MissingCachedValue=false;
            }
        }
        private sealed class WorkbookParser(string text,WorkbookEvaluator evaluator,WorkbookSheet sheet)
        {
            private int index,depth,nodes;private char Current=>index<text.Length?text[index]:'\0';
            public WorkbookNode Parse(){if(text.Length>4096)throw new InvalidOperationException("Formula length exceeds its supported limit.");Match('=');var n=Compare();Skip();if(index!=text.Length)throw new InvalidOperationException("Unsupported formula syntax; review its source expression.");return n;}
            private static Scalar Arithmetic(Func<decimal> value){try{return Scalar.N(value());}catch(DivideByZeroException){throw new WorkbookError("#DIV/0!");}catch(OverflowException){throw new WorkbookError("#NUM!");}}
            private WorkbookNode Compare(){var left=Sum();Skip();var start=index;while(Current is '<' or '>' or '=')index++;if(start==index)return left;var op=text[start..index];if(op is not("=" or "<>" or "<" or ">" or "<=" or ">="))throw new InvalidOperationException("Unsupported comparison.");var right=Sum();return new(()=>{var a=left.Scalar();var b=right.Scalar();if(a.Kind==ScalarKind.LegacyNumber)a=Scalar.N(a.Numeric);if(b.Kind==ScalarKind.LegacyNumber)b=Scalar.N(b.Numeric);if(a.Kind==ScalarKind.Blank)a=b.Kind==ScalarKind.Text?new(ScalarKind.Text):Scalar.N(0);if(b.Kind==ScalarKind.Blank)b=a.Kind==ScalarKind.Text?new(ScalarKind.Text):Scalar.N(0);static int Rank(ScalarKind k)=>k==ScalarKind.Number?0:k==ScalarKind.Text?1:2;var cmp=a.Kind!=b.Kind?Rank(a.Kind).CompareTo(Rank(b.Kind)):a.Kind==ScalarKind.Text?StringComparer.OrdinalIgnoreCase.Compare(a.Text,b.Text):a.Number().CompareTo(b.Number());return new(ScalarKind.Boolean,(op switch{"="=>cmp==0,"<>"=>cmp!=0,"<"=>cmp<0,">"=>cmp>0,"<="=>cmp<=0,_=>cmp>=0})?1:0);});}
            private WorkbookNode Sum(){var n=Product();while(true){Skip();var op=Current;if(op is not('+' or '-'))return n;index++;var l=n;var r=Product();n=new(()=>Arithmetic(()=>op=='+'?l.Scalar().Number()+r.Scalar().Number():l.Scalar().Number()-r.Scalar().Number()));}}
            private WorkbookNode Product(){var n=Atom();while(true){Skip();var op=Current;if(op is not('*' or '/'))return n;index++;var l=n;var r=Atom();n=new(()=>Arithmetic(()=>op=='*'?l.Scalar().Number()*r.Scalar().Number():l.Scalar().Number()/r.Scalar().Number()));}}
            private WorkbookNode Atom()
            {
                if(++depth>64||++nodes>512)throw new InvalidOperationException("Formula nesting/size limit exceeded.");
                try {
                    Skip();WorkbookNode n;
                    if(Match('+'))n=Atom();else if(Match('-')){var v=Atom();n=new(()=>Arithmetic(()=>-v.Scalar().Number()));}
                    else if(Match('(')){n=Compare();Require(')');}
                    else if(Current=='"'){index++;var value="";var closed=false;while(Current!='\0'){var c=Current;index++;if(c=='"'){if(Current=='"'){index++;value+='"';continue;}closed=true;break;}value+=c;}if(!closed)throw new InvalidOperationException("Unterminated formula text.");n=new(()=>new(ScalarKind.Text,Text:value));}
                    else if(char.IsDigit(Current)||Current=='.'){var start=index;while(char.IsDigit(Current)||Current=='.')index++;if(Current is 'e' or 'E'){index++;if(Current is '+' or '-')index++;while(char.IsDigit(Current))index++;}if(!decimal.TryParse(text[start..index],NumberStyles.Float,CultureInfo.InvariantCulture,out var value))throw new InvalidOperationException("Invalid formula number.");n=new(()=>Scalar.N(value));}
                    else if(Current=='#'){var start=index;while(Current!='\0'&&Current is not(',' or ')' or ' '))index++;var error=KnownError(text[start..index]);n=new(()=>throw new WorkbookError(error));}
                    else {var name=Identifier();if(Match('(')){var args=new List<WorkbookNode>();if(!Match(')')){do{if(args.Count>=256)throw new InvalidOperationException("Too many formula arguments.");args.Add(Compare());}while(Match(','));Require(')');}n=Function(name.ToUpperInvariant(),args);}
                        else if(Match(':')){var end=Identifier();n=new(()=>throw new WorkbookError("#VALUE!"),()=>evaluator.Range(sheet,name,end));}
                        else if(name.Equals("TRUE",StringComparison.OrdinalIgnoreCase)||name.Equals("FALSE",StringComparison.OrdinalIgnoreCase))n=new(()=>new(ScalarKind.Boolean,name.Equals("TRUE",StringComparison.OrdinalIgnoreCase)?1:0));
                        else n=new(()=>evaluator.ReadScalar(sheet,name),Reference:true);}
                    while(Match('%')){var v=n;n=new(()=>Arithmetic(()=>v.Scalar().Number()/100));}return n;
                }finally{depth--;}
            }
            private WorkbookNode Function(string name,List<WorkbookNode> args)=>new(()=>{
                if(name=="IF"&&args.Count is 2 or 3)return args[0].Scalar().Truth()?args[1].Scalar():args.Count==3?args[2].Scalar():new(ScalarKind.Boolean);
                if(name=="IFERROR"&&args.Count==2){try{return args[0].Scalar();}catch(WorkbookError e){if(evaluator.Traces.Count>=10000)throw new InvalidOperationException("Provenance limit exceeded.");evaluator.Traces.Add(new(evaluator.stack.TryPeek(out var f)?f:"","","IFERROR",null,0,"Handled "+e.Code+" using the original explicit fallback"));return args[1].Scalar();}}
                if(name=="VLOOKUP")return evaluator.Lookup(args);
                IEnumerable<Scalar> Values(){foreach(var arg in args){if(arg.Range is not null){var range=arg.Range();for(var row=range.FirstRow;row<=range.LastRow;row++)for(var col=range.FirstColumn;col<=range.LastColumn;col++){if(!range.Sheet.Cells.ContainsKey(Address(col,row))){evaluator.Work();continue;}var v=evaluator.TableValue(range,col,row);if(v.Kind is ScalarKind.Number or ScalarKind.LegacyNumber)yield return v;}}else{var v=arg.Scalar();if(!arg.Reference||v.Kind is ScalarKind.Number or ScalarKind.LegacyNumber)yield return v;}}}
                if(name is "SUM" or "MIN" or "MAX"){var values=Values().Select(v=>v.Number()).ToArray();return Arithmetic(()=>name=="SUM"?values.Sum():values.Length==0?0:name=="MIN"?values.Min():values.Max());}
                if(name is "AND" or "OR"&&args.Count>0){var values=args.Select(a=>a.Scalar().Truth()).ToArray();return new(ScalarKind.Boolean,(name=="AND"?values.All(v=>v):values.Any(v=>v))?1:0);}
                if(name=="ABS"&&args.Count==1)return Arithmetic(()=>Math.Abs(args[0].Scalar().Number()));
                if(name is "ROUND" or "ROUNDUP" or "ROUNDDOWN"&&args.Count==2){var places=args[1].Scalar().Number();if(decimal.Truncate(places)!=places||places is < -6 or >8)throw new WorkbookError("#NUM!","Rounding precision must be between -6 and 8 (#NUM!).");var scale=(decimal)Math.Pow(10,(int)places);return Arithmetic(()=>{var v=args[0].Scalar().Number()*scale;return(name=="ROUND"?Math.Round(v,0,MidpointRounding.AwayFromZero):name=="ROUNDDOWN"?decimal.Truncate(v):Math.Sign(v)*decimal.Ceiling(Math.Abs(v)))/scale;});}
                throw new InvalidOperationException("Unsupported formula function or argument count: "+name+". Review a supported rule or dependency mapping.");
            });
            private string Identifier(){Skip();var start=index;if(Current=='\''){index++;var closed=false;while(Current!='\0'){if(Current=='\''){index++;if(Current!='\''){closed=true;break;}}index++;}if(!closed||!Match('!'))throw new InvalidOperationException("Invalid quoted worksheet reference.");}else if(Current=='['){index++;while(Current!='\0'&&Current!=']')index++;Require(']');}while(char.IsLetterOrDigit(Current)||Current is '$' or '_' or '.' or '!')index++;if(start==index)throw new InvalidOperationException("Unsupported formula token.");return text[start..index];}
            private void Skip(){while(char.IsWhiteSpace(Current))index++;}
            private bool Match(char c){Skip();if(Current!=c)return false;index++;return true;}
            private void Require(char c){if(!Match(c))throw new InvalidOperationException("Expected formula delimiter: "+c);}
        }
    }
    private static void ValidateWorkbookDependencies(ExcelPayslipCalculationSource source)
    {
        static bool KeyValid(string value)=>Regex.IsMatch(value??"",@"^[A-Za-z0-9_-]{1,80}$");
        if(source is null||string.IsNullOrWhiteSpace(source.SheetName)||source.SheetName.Length>100||source.ColumnCount is <1 or >512||source.Rows is null||source.Rows.Count>100000
            ||source.Rows.Any(r=>r is null||r.SourceRow is <1 or >100000||r.Cells is null||r.Cells.Count>source.ColumnCount)||source.Rows.Select(r=>r.SourceRow).Distinct().Count()!=source.Rows.Count)
            throw new InvalidOperationException("Captured root worksheet scope is invalid.");
        foreach(var cell in source.Rows.SelectMany(r=>r.Cells).Where(c=>c is not null))if(cell!.Value is null||cell.Value.Length>10000||cell.FormulaText?.Length>4096||cell.DataType is not("" or "Number" or "Text" or "Boolean" or "Blank"))throw new InvalidOperationException("Captured root cell type/size is invalid.");
        if(!KeyValid(source.WorkbookKey)||source.DependencySheets is null||source.ExternalMappings is null||source.DependencySheets.Count>20||source.ExternalMappings.Count>20)throw new InvalidOperationException("Review bounded supplied workbook/worksheet mappings.");
        if(source.CaptureVersion is <0 or >1 || source.CaptureVersion==1 && (source.DependencyReviewStatus is not ("Reviewed" or "ReferenceOnly") || source.WorkbookFiles is null || source.WorkbookFiles.Count is <1 or >20)) throw new InvalidOperationException("Captured workbook review/version is invalid. Re-supply and review the original dependencies.");
        var keys=new HashSet<string>(StringComparer.OrdinalIgnoreCase){source.WorkbookKey+"|"+source.SheetName};long total=source.Rows.Sum(r=>(long)r.Cells.Count);
        foreach(var sheet in source.DependencySheets){if(sheet is null||!KeyValid(sheet.WorkbookKey)||string.IsNullOrWhiteSpace(sheet.SheetName)||sheet.SheetName.Length>100||sheet.SheetName.IndexOfAny(['!','[',']','\\','/'])>=0||sheet.ColumnCount is <1 or >512||sheet.Rows is null||sheet.Rows.Count>100000||!keys.Add(sheet.WorkbookKey+"|"+sheet.SheetName)||!Regex.IsMatch(sheet.SourceHash??"",@"^[a-fA-F0-9]{64}$"))throw new InvalidOperationException("Dependency worksheet scope/hash is invalid or duplicated.");
            if(sheet.Rows.Any(r=>r is null||r.SourceRow is <1 or >100000||r.Cells is null||r.Cells.Count>sheet.ColumnCount)||sheet.Rows.Select(r=>r.SourceRow).Distinct().Count()!=sheet.Rows.Count)throw new InvalidOperationException("Dependency rows exceed limits or contain duplicate addresses.");total+=sheet.Rows.Sum(r=>(long)r.Cells.Count);
            foreach(var cell in sheet.Rows.SelectMany(r=>r.Cells).Where(c=>c is not null))if(cell!.Value is null||cell.Value.Length>10000||cell.FormulaText?.Length>4096||cell.Error?.Length>100||cell.CalculationError?.Length>500||cell.DataType is not("" or "Number" or "Text" or "Boolean" or "Blank"))throw new InvalidOperationException("Dependency cell metadata is invalid.");
        }
        if(total>250000)throw new InvalidOperationException("Supplied dependency snapshots exceed 250,000 cells. Review a smaller bounded source scope.");
        var aliases=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var mapping in source.ExternalMappings)if(mapping is null||!KeyValid(mapping.FromWorkbookKey)||!KeyValid(mapping.ToWorkbookKey)||!Regex.IsMatch(mapping.Reference??"",@"^\[[A-Za-z0-9_ .()-]{1,80}\]$")||!aliases.Add(mapping.FromWorkbookKey+"|"+mapping.Reference))throw new InvalidOperationException("External references must be explicit unique workbook slot/name mappings, never paths or URLs.");
    }
}
