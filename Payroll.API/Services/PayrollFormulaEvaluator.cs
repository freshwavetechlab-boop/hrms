using System.Globalization;

namespace Payroll.API.Services;

/// <summary>Bounded numeric/boolean AST used by the main payroll calculator. No workbook, storage or scripting access.</summary>
public static class PayrollFormulaEvaluator
{
    public const int MaximumExpressionLength = 4096;
    private sealed class ValueError(string message) : InvalidOperationException(message);
    private readonly record struct Value(decimal Number,bool Boolean=false);
    private sealed record Node(Func<Value> Run,string Structure="");
    public static void Validate(string expression)=>new Parser(expression,_=>0).Parse();
    // Bounded equivalence only: parse first, preserve every operator/parenthesis/argument order.
    // No evaluation, algebraic reassociation, component reordering or expected-value substitution.
    public static string Canonicalize(string expression) {
        Validate(expression);var text=expression.Trim();var index=text.StartsWith('=')?1:0;var result=new System.Text.StringBuilder();
        while(index<text.Length) {
            if(char.IsWhiteSpace(text[index])){index++;continue;}
            if(char.IsDigit(text[index])||text[index]=='.') {
                var start=index;while(index<text.Length&&(char.IsDigit(text[index])||text[index]=='.'))index++;
                if(index<text.Length&&text[index] is 'E' or 'e'){index++;if(index<text.Length&&text[index] is '+' or '-')index++;while(index<text.Length&&char.IsDigit(text[index]))index++;}
                var value=decimal.Parse(text[start..index],NumberStyles.Float,CultureInfo.InvariantCulture);result.Append('#').Append(value.ToString("G29",CultureInfo.InvariantCulture)).Append(';');
            }else if(char.IsLetter(text[index])||text[index]=='_') {
                var start=index;while(index<text.Length&&(char.IsLetterOrDigit(text[index])||text[index]=='_'))index++;result.Append('@').Append(text[start..index].ToUpperInvariant()).Append(';');
            }else result.Append(text[index++]);
        }
        return result.ToString();
    }
    // The evaluator's own parsed tree, never evaluated. Parentheses do not create
    // operators. Preserve associativity, unary operators, percent, comparisons,
    // function argument order and variable identities. The only default expanded
    // here is the engine's existing ROUND(value) == ROUND(value, literal zero).
    public static string CanonicalStructure(string expression)=>new Parser(expression,
        _=>throw new InvalidOperationException("Structural comparison cannot resolve payroll inputs."),true).Parse().Structure;
    // Restricted to explicit reviewed existing-template reuse. Unary plus is
    // exactly inner.Run in this evaluator, including its Boolean/error type.
    // Keep the ordinary structure/signature API unchanged.
    internal static string ReviewedTemplateStructure(string expression)=>new Parser(expression,
        _=>throw new InvalidOperationException("Reviewed template comparison cannot resolve payroll inputs."),true,true).Parse().Structure;
    public static decimal Evaluate(string expression,Func<string,decimal> resolve) {
        var value=new Parser(expression,resolve).Parse().Run();
        if(value.Boolean)throw new InvalidOperationException("A salary output must be numeric; select numeric IF branches.");
        return value.Number;
    }
    private static Node Arithmetic(Func<decimal> run,string structure="")=>new(()=>{
        try{return new(run());}catch(DivideByZeroException){throw new ValueError("A formula denominator is zero (#DIV/0!).");}
        catch(OverflowException){throw new ValueError("Formula arithmetic exceeds decimal limits (#NUM!).");}
    },structure);
    private static string Shape(string operation,params string[] children)=>operation+"("+string.Join(',',children)+")";
    private sealed class Parser(string text,Func<string,decimal> resolve,bool buildStructure=false,bool ignoreIdentityUnaryPlus=false)
    {
        private int index,depth,nodes;
        private char Current=>index<text.Length?text[index]:'\0';
        public Node Parse() {
            if(string.IsNullOrWhiteSpace(text)||text.Length>MaximumExpressionLength)throw new InvalidOperationException("Payroll formula is empty or exceeds 4096 characters.");
            Match('=');var node=Compare();Skip();if(index!=text.Length)throw new InvalidOperationException("Unsupported payroll formula syntax.");return node;
        }
        private Node Compare() {
            var left=Sum();Skip();var start=index;while(Current is '<' or '>' or '=')index++;
            if(start==index)return left;
            var op=text[start..index];if(op is not("=" or "<>" or "<" or ">" or "<=" or ">="))throw new InvalidOperationException("Unsupported formula comparison.");
            var right=Sum();return new(()=>{
                var a=left.Run();var b=right.Run();var cmp=a.Boolean!=b.Boolean?a.Boolean.CompareTo(b.Boolean):a.Number.CompareTo(b.Number);
                return new((op switch{"="=>cmp==0,"<>"=>cmp!=0,"<"=>cmp<0,">"=>cmp>0,"<="=>cmp<=0,_=>cmp>=0})?1:0,true);
            },buildStructure?Shape("COMPARE:"+op,left.Structure,right.Structure):"");
        }
        private Node Sum() {
            var n=Product();while(true){Skip();var op=Current;if(op is not('+' or '-'))return n;index++;var left=n;var right=Product();n=Arithmetic(()=>op=='+'?left.Run().Number+right.Run().Number:left.Run().Number-right.Run().Number,buildStructure?Shape("BINARY:"+op,left.Structure,right.Structure):"");}
        }
        private Node Product() {
            var n=Atom();while(true){Skip();var op=Current;if(op is not('*' or '/'))return n;index++;var left=n;var right=Atom();n=Arithmetic(()=>op=='*'?left.Run().Number*right.Run().Number:left.Run().Number/right.Run().Number,buildStructure?Shape("BINARY:"+op,left.Structure,right.Structure):"");}
        }
        private Node Atom() {
            if(++depth>64||++nodes>512)throw new InvalidOperationException("Payroll formula depth/node limit exceeded.");
            try {
                Skip();Node n;
                if(Match('+')){var inner=Atom();n=buildStructure&&!ignoreIdentityUnaryPlus?new(inner.Run,Shape("UNARY:+",inner.Structure)):inner;}
                else if(Match('-')){var inner=Atom();n=Arithmetic(()=>-inner.Run().Number,buildStructure?Shape("UNARY:-",inner.Structure):"");}
                else if(Match('(')){n=Compare();Require(')');}
                else if(char.IsDigit(Current)||Current=='.'){
                    var start=index;while(char.IsDigit(Current)||Current=='.')index++;
                    if(Current is 'E' or 'e'){index++;if(Current is '+' or '-')index++;while(char.IsDigit(Current))index++;}
                    if(!decimal.TryParse(text[start..index],NumberStyles.Float,CultureInfo.InvariantCulture,out var number))throw new InvalidOperationException("Invalid payroll formula number.");
                    n=new(()=>new(number),buildStructure?"NUMBER:"+number.ToString("G29",CultureInfo.InvariantCulture):"");
                }else {
                    var start=index;while(char.IsLetterOrDigit(Current)||Current=='_')index++;
                    if(start==index)throw new InvalidOperationException("Unsupported payroll formula token; workbook/text dependencies need reviewed native mapping.");
                    var name=text[start..index].ToUpperInvariant();
                    if(Match('(')){
                        var args=new List<Node>();if(!Match(')')){do{if(args.Count>=256)throw new InvalidOperationException("Too many formula arguments.");args.Add(Compare());}while(Match(','));Require(')');}
                        n=Function(name,args,buildStructure);
                    }else n=name is "TRUE" or "FALSE"?new(()=>new(name=="TRUE"?1:0,true),buildStructure?"BOOLEAN:"+name:""):new(()=>new(resolve(name)),buildStructure?"VARIABLE:"+name:"");
                }
                while(Match('%')){var inner=n;n=Arithmetic(()=>inner.Run().Number/100,buildStructure?Shape("PERCENT",inner.Structure):"");}
                return n;
            }finally{depth--;}
        }
        private static Node Function(string name,List<Node> args,bool buildStructure) {
            var shape="";
            if(buildStructure)
            {
                var children=args.Select(arg=>arg.Structure).ToList();
                if(name=="ROUND" && args.Count==1)children.Add("NUMBER:0");
                shape=Shape("FUNCTION:"+name,children.ToArray());
            }
            if(name=="IF"&&args.Count is 2 or 3)return new(()=>args[0].Run().Number!=0?args[1].Run():args.Count==3?args[2].Run():new(0,true),shape);
            if(name=="IFERROR"&&args.Count==2)return new(()=>{try{return args[0].Run();}catch(ValueError){return args[1].Run();}},shape);
            if(name is "AND" or "OR"&&args.Count>0)return new(()=>{var values=args.Select(a=>a.Run().Number!=0).ToArray();return new((name=="AND"?values.All(v=>v):values.Any(v=>v))?1:0,true);},shape);
            if(name is "SUM" or "MIN" or "MAX"&&args.Count>0)return Arithmetic(()=>{var values=args.Select(a=>a.Run().Number).ToArray();return name=="SUM"?values.Sum():name=="MIN"?values.Min():values.Max();},shape);
            if(name=="ABS"&&args.Count==1)return Arithmetic(()=>Math.Abs(args[0].Run().Number),shape);
            if(name is "ROUND" or "ROUNDUP" or "ROUNDDOWN"&&args.Count is 1 or 2)return Arithmetic(()=>{
                var digits=args.Count==2?args[1].Run().Number:0;
                if(decimal.Truncate(digits)!=digits||digits is <-6 or >8)throw new ValueError("Rounding precision must be an integer between -6 and 8 (#NUM!).");
                var scale=(decimal)Math.Pow(10,(int)digits);var value=args[0].Run().Number*scale;
                return(name=="ROUND"?Math.Round(value,0,MidpointRounding.AwayFromZero):name=="ROUNDDOWN"?decimal.Truncate(value):Math.Sign(value)*decimal.Ceiling(Math.Abs(value)))/scale;
            },shape);
            throw new InvalidOperationException("Unsupported native payroll function/argument count: "+name+". Review a supported conditional rule or dependency mapping.");
        }
        private void Skip(){while(char.IsWhiteSpace(Current))index++;}
        private bool Match(char c){Skip();if(Current!=c)return false;index++;return true;}
        private void Require(char c){if(!Match(c))throw new InvalidOperationException("Missing payroll formula delimiter: "+c);}
    }
}
