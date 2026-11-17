using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class PayrollSourceDataProvenanceTests
{
    private static string Link(int employee)=>"{\"rowId\":\"source-row\",\"sourceRow\":6,\"employeeId\":"+employee+",\"sourceIdentityHash\":\""+new string('a',64)+"\"}";
    [Fact]
    public void Only_canonical_top_level_server_profile_links_are_mutable_without_changing_batch_provenance()
    {
        const string baseline="{\"amount\":1.00,\"label\":\"kept\"}";
        var expected=PayrollSourceDataProvenance.BatchHash(baseline);
        Assert.Equal(expected,PayrollSourceDataProvenance.BatchHash("{\"label\":\"kept\",\"profileLinks\":null,\"amount\":1.00}"));
        Assert.Equal(expected,PayrollSourceDataProvenance.BatchHash("{\"amount\":1.00,\"label\":\"kept\",\"profileLinks\":["+Link(11)+"]}"));
        Assert.Equal(expected,PayrollSourceDataProvenance.BatchHash("{\"amount\":1.00,\"label\":\"kept\",\"profileLinks\":["+Link(12)+"]}"));
        Assert.NotEqual(expected,PayrollSourceDataProvenance.BatchHash("{\"amount\":1.00,\"label\":\"kept\",\"other\":{\"profileLinks\":[]}}"));
        Assert.NotEqual(expected,PayrollSourceDataProvenance.BatchHash("{\"amount\":1.00,\"label\":\"kept\",\"ProfileLinks\":[]}"));
    }
    [Theory]
    [InlineData("{\"n\":1.00}","{\"n\":1.0}")]
    [InlineData("{\"n\":\"1.00\"}","{\"n\":\"1.0\"}")]
    [InlineData("{\"n\":1}","{\"n\":1,\"unknown\":true}")]
    [InlineData("{\"n\":0.12345678901234567890123456781}","{\"n\":0.12345678901234567890123456782}")]
    public void Every_other_unknown_field_string_or_numeric_lexeme_remains_bound(string before,string after)
    {
        Assert.NotEqual(PayrollSourceDataProvenance.BatchHash(before),PayrollSourceDataProvenance.BatchHash(after));
        Assert.NotEqual(PayrollSourceDataProvenance.SourceHash(before),PayrollSourceDataProvenance.SourceHash(after));
    }
    [Theory]
    [InlineData("{\"x\":1,\"x\":1}")]
    [InlineData("{\"nested\":{\"x\":1,\"x\":2}}")]
    [InlineData("{\"profileLinks\":{\"salary\":123}}")]
    [InlineData("{\"profileLinks\":[{\"rowId\":\"x\",\"sourceRow\":1,\"employeeId\":1,\"sourceIdentityHash\":\"not-a-hash\"}]}")]
    public void Duplicate_or_noncanonical_server_metadata_cannot_create_a_blanket_waiver(string json)
        =>Assert.Throws<InvalidOperationException>(()=>PayrollSourceDataProvenance.BatchHash(json));
    [Fact]
    public void Source_json_has_no_profile_link_exception_and_duplicate_or_depth_failures_are_explicit()
    {
        Assert.NotEqual(PayrollSourceDataProvenance.SourceHash("{}"),PayrollSourceDataProvenance.SourceHash("{\"profileLinks\":[]}"));
        Assert.Throws<InvalidOperationException>(()=>PayrollSourceDataProvenance.SourceHash("{\"x\":1,\"x\":2}"));
        Assert.Throws<InvalidOperationException>(()=>PayrollSourceDataProvenance.BatchHash("{\"n\":"+new string('[',65)+"0"+new string(']',65)+"}"));
    }
}
