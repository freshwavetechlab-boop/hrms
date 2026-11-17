using System.Globalization;
using System.Text.RegularExpressions;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    // Normalize only a proven IF cap/floor identity. No source values are read,
    // no conditional branch is frozen, and unrelated IF rules retain their AST.
    internal static string NormalizeTransferCapFormula(string formula)
    {
        var text = Regex.Replace(formula.TrimStart('=').ToUpperInvariant(), @"\s+", "");
        if (text.Length > PayrollFormulaEvaluator.MaximumExpressionLength) throw new InvalidOperationException("The native cap rule exceeds the supported formula length.");
        return Rewrite(text, 0);

        static string Rewrite(string value, int depth)
        {
            if (depth > 20) return value; // Leave deeper existing rules to the unchanged main parser.
            var call = Regex.Match(value, @"\b([A-Z][A-Z0-9_]*)\(");
            if (!call.Success) return value;
            var open = call.Index + call.Length - 1;
            if (!Arguments(value, open, out var args, out var end)) return value;
            var rewritten = args.Select(arg => Rewrite(arg, depth + 1)).ToArray();
            var expression = call.Groups[1].Value == "IF" && rewritten.Length == 3
                && Cap(rewritten, out var capped) ? capped : call.Groups[1].Value + "(" + string.Join(',', rewritten) + ")";
            return value[..call.Index] + expression + Rewrite(value[(end + 1)..], depth);
        }

        static bool Cap(string[] args, out string result)
        {
            result = "";
            var condition = Ungroup(args[0]);
            var level = 0; var comparison = -1; var comparisonLength = 0;
            for (var i = 0; i < condition.Length; i++)
            {
                if (condition[i] == '(') level++;
                else if (condition[i] == ')') level--;
                else if (level == 0 && condition[i] is '<' or '>')
                {
                    if (comparison >= 0) return false;
                    comparison = i; comparisonLength = i + 1 < condition.Length && condition[i + 1] == '=' ? 2 : 1;
                    i += comparisonLength - 1;
                }
                else if (level == 0 && condition[i] == '=') return false;
            }
            if (comparison <= 0) return false;
            var reference = Ungroup(condition[..comparison]);
            var operation = condition.Substring(comparison, comparisonLength);
            var limit = condition[(comparison + comparisonLength)..];
            if (!Regex.IsMatch(limit, @"^-?\d+(?:\.\d+)?$")
                || !decimal.TryParse(limit, NumberStyles.Number, CultureInfo.InvariantCulture, out _)
                || !Product(args[1], out var yes, out var yesRate) || !Product(args[2], out var no, out var noRate)
                || yesRate != noRate || !((yes == reference && no == limit) || (yes == limit && no == reference))) return false;
            var maximum = operation.StartsWith('>') == (yes == reference);
            result = (maximum ? "MAX" : "MIN") + "(" + reference + "," + limit + ")*" + yesRate;
            return true;
        }

        static bool Product(string value, out string basis, out string factor)
        {
            value = Ungroup(value); basis = factor = "";
            var level = 0; var product = -1;
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] == '(') level++;
                else if (value[i] == ')') level--;
                else if (level == 0 && value[i] == '*') { if (product >= 0) return false; product = i; }
                else if (level == 0 && (value[i] is '+' or '-') && (i == 0 || i == product + 1)) { }
                else if (level == 0 && value[i] is '+' or '-' or '/' or '<' or '>' or '=') return false;
            }
            if (product <= 0 || product >= value.Length - 1) return false;
            basis = Ungroup(value[..product]); factor = value[(product + 1)..];
            return basis.Length > 0 && factor.Length > 0;
        }

        static string Ungroup(string value)
        {
            while (value.StartsWith('(') && Arguments(value, 0, out var args, out var end) && end == value.Length - 1 && args.Count == 1)
                value = args[0];
            return value;
        }

        static bool Arguments(string value, int open, out List<string> args, out int end)
        {
            args = []; end = -1; var level = 0; var start = open + 1;
            for (var i = start; i < value.Length; i++)
            {
                if (value[i] == '(') level++;
                else if (value[i] == ')')
                {
                    if (level == 0) { args.Add(value[start..i]); end = i; return true; }
                    level--;
                }
                else if (value[i] == ',' && level == 0) { args.Add(value[start..i]); start = i + 1; }
            }
            return false;
        }
    }
}
