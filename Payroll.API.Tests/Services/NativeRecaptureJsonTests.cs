using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class NativeRecaptureJsonTests
{
    [Theory]
    [InlineData("8220.80", "8220.8")][InlineData("0", "-0.000")][InlineData("1e3", "1000.00")]
    [InlineData("-1000.00", "-1e3")][InlineData("1e-29", "0.00000000000000000000000000001")]
    [InlineData("{\"a\":1.00,\"b\":[false,null,\"same\"]}", "{\"b\":[false,null,\"same\"],\"a\":1}")]
    public void Exact_numeric_spellings_match_without_changing_the_documents(string left, string right) => Assert.True(NativeRecaptureJson.Equivalent(left, right));

    [Theory]
    [InlineData("986.496000000000000000000000001", "986.496000000000000000000000002")]
    [InlineData("1e-29", "9e-30")][InlineData("1e100001", "10e100000")]
    [InlineData("{\"a\":1,\"a\":1}", "{\"a\":1}")][InlineData("{\"a\":1}", "{\"a\":1,\"a\":1}")]
    [InlineData("[1,2]", "[2,1]")][InlineData("\"1.00\"", "\"1\"")][InlineData("null", "false")]
    [InlineData("{\"a\":null}", "{}")][InlineData("1", "\"1\"")]
    public void Any_value_or_structure_change_duplicate_key_or_unbounded_exponent_rejects(string left, string right) => Assert.False(NativeRecaptureJson.Equivalent(left, right));
}
