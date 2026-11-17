using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class SalaryTemplateFrontendFormulaParityTests
{
    public static IEnumerable<object[]> SharedCases()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "payroll-ui", "tests", "fixtures", "salary-formula-parity.json"))) root = root.Parent;
        Assert.NotNull(root);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root!.FullName, "payroll-ui", "tests", "fixtures", "salary-formula-parity.json")));
        return json.RootElement.EnumerateArray().Select(item => new object[] { item.GetProperty("name").GetString()!, item.GetRawText() }).ToArray();
    }

    [Theory]
    [MemberData(nameof(SharedCases))]
    public void ActualNativeEvaluatorAndFrontendUseTheSameConditionalFixtures(string _, string raw)
    {
        using var item = JsonDocument.Parse(raw);
        var values = item.RootElement.GetProperty("values").EnumerateObject().ToDictionary(pair => pair.Name, pair => pair.Value.GetDecimal());
        var original = item.RootElement.GetProperty("formula").GetString()!;
        // These are the unchanged legacy normalization steps in PayRunRepository.
        var formula = Regex.Replace(original, @"SUM\s*\(\s*(FIXED\s+)?EARNINGS(_BEFORE_THIS)?\s*\)", m => values.GetValueOrDefault(m.Groups[1].Success ? "FIXED_EARNINGS_BEFORE_THIS" : "EARNINGS_BEFORE_THIS").ToString(CultureInfo.InvariantCulture), RegexOptions.IgnoreCase);
        formula = Regex.Replace(formula, @"(\d+(?:\.\d+)?)\s*%\s*OF\s*([A-Z0-9_]+)", "$2*$1/100", RegexOptions.IgnoreCase);
        var actual = PayrollFormulaEvaluator.Evaluate(formula, token => values.TryGetValue(token, out var value) ? value : throw new InvalidOperationException("Missing test operand"));
        Assert.Equal(item.RootElement.GetProperty("expected").GetDecimal(), actual);
        Assert.Equal(original, item.RootElement.GetProperty("formula").GetString());
    }
}
