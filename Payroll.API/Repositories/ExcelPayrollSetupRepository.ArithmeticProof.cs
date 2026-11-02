using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    // Bounded same-row arithmetic proof. Literal cells remain distinct symbols:
    // equal cached numbers cannot make an unrelated fee/base an equivalent rule.
    // IEEE evaluation checks the captured caches; exact rational polynomials
    // prove distribution/cancellation in source totals without a money epsilon.
    internal static bool HasSourceArithmeticIdentity(Plan plan, PlannedRow row, ExcelPayrollSetupRequest request)
        => TrySourceArithmeticIdentity(plan,row,request,out _);

    private static bool TrySourceArithmeticIdentity(Plan plan, PlannedRow row, ExcelPayrollSetupRequest request,out string failure)
    {
        failure="Source arithmetic or its captured cache is not verified.";
        try
        {
            var saved = plan.Batch.Rows.Single(r => r.Id == row.RowId);
            if (saved.NetPay < 0) return false; // a negative balance is not a rounding exception to the cash floor
            var cells = plan.Source.Rows.Single(r => r.SourceRow == row.SourceRow);
            var parser = new SourceArithmetic(cells, plan.Source, request);
            var net = plan.Source.Columns.Single(c => request.SourceColumnRoles.GetValueOrDefault(c.ColumnIndex, c.Kind) == "netPay");
            var expected = Polynomial.Zero;
            foreach (var component in row.Components)
            {
                var index = component.Definition.ExpectedColumnIndex ?? throw new InvalidOperationException();
                var amount = row.Preview.Lines.Single(l => l.Code == component.Definition.Code).SourceAmount ?? throw new InvalidOperationException();
                var value = parser.Cell(index);
                if (!parser.CacheMatches(index, amount, value)) { failure="Captured cache proof failed for "+component.Definition.Code+".";return false; }
                if (component.Definition.Category is "Earning" or "Reimbursement") expected += value.Rule;
                else if (component.Definition.Category == "Deduction") expected -= value.Rule;
            }
            var actual = parser.Cell(net.ColumnIndex);
            if(!expected.Same(actual.Rule)){failure="The final net formula is not algebraically identical to the mapped earning/deduction outputs.";return false;}
            if(!parser.CacheMatches(net.ColumnIndex,saved.NetPay,actual)){failure="The final net cache is outside the captured arithmetic proof.";return false;}
            failure="";return true;
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException or DivideByZeroException)
        { failure="Unsupported same-row arithmetic or an unavailable/unreviewed source input.";return false; }
    }

    private readonly record struct Rational(BigInteger N, BigInteger D)
    {
        internal static Rational Make(BigInteger n, BigInteger d)
        {
            if (d.IsZero || n.GetBitLength() > 8192 || d.GetBitLength() > 8192) throw new InvalidOperationException();
            if (d.Sign < 0) { n = -n; d = -d; }
            var gcd = BigInteger.GreatestCommonDivisor(n, d); return new(n / gcd, d / gcd);
        }
        internal static Rational Number(string text)
        {
            var bits = decimal.GetBits(decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));
            var n = (BigInteger)(uint)bits[0] | (BigInteger)(uint)bits[1] << 32 | (BigInteger)(uint)bits[2] << 64;
            return Make(bits[3] < 0 ? -n : n, BigInteger.Pow(10, (bits[3] >> 16) & 255));
        }
        public static Rational operator +(Rational a, Rational b) => Make(a.N*b.D+b.N*a.D,a.D*b.D);
        public static Rational operator -(Rational a) => new(-a.N,a.D);
        public static Rational operator *(Rational a, Rational b) => Make(a.N*b.N,a.D*b.D);
        public static Rational operator /(Rational a, Rational b) => Make(a.N*b.D,a.D*b.N);
    }

    private sealed class Polynomial
    {
        private readonly Dictionary<string,Rational> terms;
        private Polynomial(Dictionary<string,Rational> terms) { this.terms=terms; }
        internal static Polynomial Zero => new([]);
        internal static Polynomial Constant(string value) => new(new(){[""]=Rational.Number(value)});
        internal static Polynomial Variable(int index) => new(new(){[index+":1"]=new(1,1)});
        private static string Product(string left, string right, int sign)
        {
            var powers = new SortedDictionary<int,int>();
            void Add(string key,int factor) { foreach(var item in key.Split(';',StringSplitOptions.RemoveEmptyEntries)) {
                var pair=item.Split(':');var index=int.Parse(pair[0],CultureInfo.InvariantCulture);
                var power=powers.GetValueOrDefault(index)+factor*int.Parse(pair[1],CultureInfo.InvariantCulture);
                if(Math.Abs(power)>32)throw new InvalidOperationException();powers[index]=power;
            }}
            Add(left,1);Add(right,sign);return string.Join(';',powers.Where(p=>p.Value!=0).Select(p=>p.Key+":"+p.Value));
        }
        private static Polynomial Clean(Dictionary<string,Rational> values)
        {
            foreach(var key in values.Where(p=>p.Value.N.IsZero).Select(p=>p.Key).ToArray())values.Remove(key);
            if(values.Count>256)throw new InvalidOperationException();return new(values);
        }
        public static Polynomial operator +(Polynomial a,Polynomial b) {
            var values=new Dictionary<string,Rational>(a.terms);foreach(var (key,value) in b.terms)values[key]=values.TryGetValue(key,out var old)?old+value:value;return Clean(values);
        }
        public static Polynomial operator -(Polynomial a,Polynomial b) => a+new Polynomial(b.terms.ToDictionary(p=>p.Key,p=>-p.Value));
        public static Polynomial operator *(Polynomial a,Polynomial b) {
            var values=new Dictionary<string,Rational>();foreach(var x in a.terms)foreach(var y in b.terms) {
                var key=Product(x.Key,y.Key,1);var value=x.Value*y.Value;values[key]=values.TryGetValue(key,out var old)?old+value:value;
                if(values.Count>256)throw new InvalidOperationException();
            }return Clean(values);
        }
        public static Polynomial operator /(Polynomial a,Polynomial b) {
            if(b.terms.Count!=1)throw new InvalidOperationException();var divisor=b.terms.Single();
            return Clean(a.terms.ToDictionary(p=>Product(p.Key,divisor.Key,-1),p=>p.Value/divisor.Value));
        }
        internal bool Same(Polynomial other) => (this-other).terms.Count==0;
    }

    private sealed record ArithmeticValue(Polynomial Rule,double Value,double Lower,double Upper)
    {
        internal static ArithmeticValue Literal(Polynomial rule,double value)=>new(rule,value,Math.BitDecrement(value),Math.BitIncrement(value));
        internal static ArithmeticValue Number(string text)=>Literal(Polynomial.Constant(text),double.Parse(text,NumberStyles.Float,CultureInfo.InvariantCulture));
        private static ArithmeticValue Enclose(Polynomial rule,double value,params double[] bounds)=>new(rule,value,Math.BitDecrement(bounds.Min()),Math.BitIncrement(bounds.Max()));
        public static ArithmeticValue operator +(ArithmeticValue a,ArithmeticValue b)=>Enclose(a.Rule+b.Rule,a.Value+b.Value,a.Lower+b.Lower,a.Upper+b.Upper);
        public static ArithmeticValue operator -(ArithmeticValue a,ArithmeticValue b)=>Enclose(a.Rule-b.Rule,a.Value-b.Value,a.Lower-b.Upper,a.Upper-b.Lower);
        public static ArithmeticValue operator *(ArithmeticValue a,ArithmeticValue b)=>Enclose(a.Rule*b.Rule,a.Value*b.Value,a.Lower*b.Lower,a.Lower*b.Upper,a.Upper*b.Lower,a.Upper*b.Upper);
        public static ArithmeticValue operator /(ArithmeticValue a,ArithmeticValue b) {
            if(b.Lower<=0&&b.Upper>=0)throw new InvalidOperationException();
            return Enclose(a.Rule/b.Rule,a.Value/b.Value,a.Lower/b.Lower,a.Lower/b.Upper,a.Upper/b.Lower,a.Upper/b.Upper);
        }
    }

    private sealed class SourceArithmetic(ExcelPayslipCalculationSourceRow row, ExcelPayslipCalculationSource source, ExcelPayrollSetupRequest request)
    {
        private readonly Dictionary<int,ArithmeticValue> cache=[];
        private readonly HashSet<int> visiting=[];
        internal ArithmeticValue Cell(int index)
        {
            if(cache.TryGetValue(index,out var previous))return previous;
            if(index<0||index>=source.ColumnCount||visiting.Count>=32||!visiting.Add(index))throw new InvalidOperationException();
            var cell=row.Cells.ElementAtOrDefault(index);
            if(cell?.Error is not null||cell?.CalculationError is not null||cell?.MissingCachedValue==true)throw new InvalidOperationException();
            var blank=cell is null||string.IsNullOrWhiteSpace(cell.Value)&&string.IsNullOrWhiteSpace(cell.FormulaText);
            ArithmeticValue value;
            if(blank) {
                if(!source.Columns.Any(c=>c.ColumnIndex==index&&c.BlankAsZero)&&!request.Components.Any(c=>c.ExpectedColumnIndex==index&&c.BlankInputAsZero))throw new InvalidOperationException();
                value=ArithmeticValue.Number("0");
            }
            else if(!string.IsNullOrWhiteSpace(cell!.FormulaText))value=new ArithmeticParser(cell.FormulaText,this,row.SourceRow).Parse();
            else value=ArithmeticValue.Literal(Polynomial.Variable(index),double.Parse(cell.Value.Replace(",",""),NumberStyles.Float,CultureInfo.InvariantCulture));
            if(!double.IsFinite(value.Value)||!double.IsFinite(value.Lower)||!double.IsFinite(value.Upper))throw new InvalidOperationException();
            visiting.Remove(index);return cache[index]=value;
        }
        internal bool CacheMatches(int index,decimal amount,ArithmeticValue evaluated)
        {
            var cell=row.Cells.ElementAtOrDefault(index);
            var captured=cell is null||string.IsNullOrWhiteSpace(cell.Value)?0:ReadNumber(row,index);
            var literal=double.Parse(captured.ToString(CultureInfo.InvariantCulture),CultureInfo.InvariantCulture);
            // Match the importer's numeric representation contract: XLSX G17
            // cache text and browser shortest-roundtrip JSON can name the same
            // IEEE value while landing on opposite decimal G15 midpoints.
            return (literal==double.Parse(amount.ToString(CultureInfo.InvariantCulture),CultureInfo.InvariantCulture)
                    || SourceDisplayPrecision(captured)==SourceDisplayPrecision(amount))
                && literal>=evaluated.Lower && literal<=evaluated.Upper;
        }
    }

    private sealed class ArithmeticParser
    {
        private readonly string text;private readonly SourceArithmetic cells;private readonly int row;private int at;
        internal ArithmeticParser(string formula,SourceArithmetic cells,int row) {
            text=Regex.Replace(formula.TrimStart('=').ToUpperInvariant(),@"[\s$]","");this.cells=cells;this.row=row;
            if(text.Length>1000)throw new InvalidOperationException();
        }
        private bool Take(char ch){if(at>=text.Length||text[at]!=ch)return false;at++;return true;}
        internal ArithmeticValue Parse(){var result=Add();if(at!=text.Length)throw new InvalidOperationException();return result;}
        private ArithmeticValue Add(){var v=Multiply();while(at<text.Length){if(Take('+'))v+=Multiply();else if(Take('-'))v-=Multiply();else break;}return v;}
        private ArithmeticValue Multiply(){var v=Primary();while(at<text.Length){if(Take('*'))v*=Primary();else if(Take('/'))v/=Primary();else break;}return v;}
        private ArithmeticValue Primary(){
            ArithmeticValue v;
            if(Take('+'))return Primary();if(Take('-'))return ArithmeticValue.Number("0")-Primary();
            if(Take('(')){v=Add();if(!Take(')'))throw new InvalidOperationException();}
            else if(text.AsSpan(at).StartsWith("SUM(")) {
                at+=4;v=ArithmeticValue.Number("0");
                do {var range=Regex.Match(text[at..],@"^([A-Z]+)(\d+):([A-Z]+)(\d+)");
                    if(range.Success){if(int.Parse(range.Groups[2].Value)!=row||int.Parse(range.Groups[4].Value)!=row)throw new InvalidOperationException();
                        var first=TransferColumn(range.Groups[1].Value);var last=TransferColumn(range.Groups[3].Value);
                        if(last<first||last-first>100)throw new InvalidOperationException();for(var index=first;index<=last;index++)v+=cells.Cell(index);at+=range.Length;
                    }else v+=Add();
                }while(Take(','));if(!Take(')'))throw new InvalidOperationException();
            } else {
                var reference=Regex.Match(text[at..],@"^([A-Z]+)(\d+)");
                if(reference.Success){if(int.Parse(reference.Groups[2].Value)!=row)throw new InvalidOperationException();v=cells.Cell(TransferColumn(reference.Groups[1].Value));at+=reference.Length;}
                else {var number=Regex.Match(text[at..],@"^(?:\d+(?:\.\d*)?|\.\d+)(?:E[+-]?\d+)?");if(!number.Success)throw new InvalidOperationException();v=ArithmeticValue.Number(number.Value);at+=number.Length;}
            }
            while(Take('%'))v/=ArithmeticValue.Number("100");return v;
        }
    }
}
