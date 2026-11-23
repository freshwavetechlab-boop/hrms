using Microsoft.Extensions.Configuration;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class RecruitmentDocumentCollectionMailTests
{
    private static IConfiguration Config(string? url) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["PublicPortal:BaseUrl"] = url }).Build();

    [Fact]
    public void UploadLinkUsesConfiguredPortalAndOpaqueTokenOnly()
    {
        Assert.Equal("https://careers.example.invalid/candidate-documents/opaque_token-19", RecruitmentDocumentCollectionMail.BuildUploadUrl(Config("https://careers.example.invalid/"), "opaque_token-19"));
        Assert.Equal("http://127.0.0.1:5207/candidate-documents/local", RecruitmentDocumentCollectionMail.BuildUploadUrl(Config("http://127.0.0.1:5207"), "local"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://external.example.invalid")]
    [InlineData("https://user:password@example.invalid")]
    [InlineData("https://example.invalid/?redirect=other")]
    [InlineData("https://example.invalid/#fragment")]
    public void UploadLinkRejectsMissingOrUnsafePortal(string? portal)
        => Assert.Throws<InvalidOperationException>(() => RecruitmentDocumentCollectionMail.BuildUploadUrl(Config(portal), "opaque-token"));

    [Theory]
    [InlineData("")]
    [InlineData("../../candidate")]
    [InlineData("token\" onclick=\"bad")]
    [InlineData("token?email=person@example.invalid")]
    public void UploadLinkRejectsNonOpaqueTokens(string token)
        => Assert.Throws<InvalidOperationException>(() => RecruitmentDocumentCollectionMail.BuildUploadUrl(Config("https://example.invalid"), token));

    [Fact]
    public void MailContainsDirectNoLoginLinkAndSingleSubmissionInstruction()
    {
        var body = RecruitmentDocumentCollectionMail.AppendUploadLink("<p>Offer attached.</p>", "https://example.invalid/candidate-documents/token");
        Assert.StartsWith("<p>Offer attached.</p>", body);
        Assert.Contains("href=\"https://example.invalid/candidate-documents/token\"", body);
        Assert.Contains("No login is required", body);
        Assert.Contains("Already submitted", body);
        Assert.Throws<InvalidOperationException>(() => RecruitmentDocumentCollectionMail.AppendUploadLink("", "javascript:alert(1)"));
    }
}
