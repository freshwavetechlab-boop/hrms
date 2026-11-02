using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ResumeTrainingOutboxTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "frevo-training-test-" + Guid.NewGuid().ToString("N"));
    private const string Source = "Asha Kumar\nasha@example.test\nPhone: +91 9876543210\nPROFESSIONAL EXPERIENCE\nApril 2021 - Current\nChief Technology Officer\nExample Research Labs\nBengaluru, India\nEDUCATION\nMBA\nExample University, 2010\nSKILLS\nSQL\nDocker\nDate of birth: 1 Jan 1980\n";
    private static ResumeParsedFacts Reviewed => ResumeParsedFacts.Empty with { FullName = "Asha Kumar", Email = "asha@example.test",
        Phone = "9876543210", CurrentCompany = "Example Research Labs", CurrentTitle = "Chief Technology Officer",
        HighestQualification = "MBA", Skills = ["SQL", "Docker"] };
    private sealed class Environment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Test";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class Transport : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Bodies = [];
        public HttpStatusCode Code = HttpStatusCode.OK;
        public string Response = "{\"code\":\"accepted\"}";
        public HttpClient CreateClient(string name) { Assert.Equal("LocalLlmLearning", name); return new(this, false); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("https://eeslindia.org/llm-learning.php", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer " + new string('a', 64), request.Headers.Authorization!.ToString());
            Bodies.Add(await request.Content!.ReadAsStringAsync(token));
            return new(Code) { Content = new StringContent(Response) };
        }
    }
    private (ResumeTrainingOutbox, PortableIntegrationCredentialProtector, Transport) Service(bool enabled = true)
    {
        Directory.CreateDirectory(root);
        var keyPath = Path.Combine(root, "synthetic-test-key"); File.WriteAllText(keyPath, new string('a', 64));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["LocalLlmLearning:Enabled"] = enabled.ToString(), ["LocalLlmLearning:ApiKeyFile"] = keyPath,
            ["IntegrationCredentialEncryption:MasterKey"] = "synthetic test purpose separation key" }).Build();
        var protector = new PortableIntegrationCredentialProtector(config, new EphemeralDataProtectionProvider(), NullLogger<PortableIntegrationCredentialProtector>.Instance);
        var transport = new Transport();
        return (new(config, new Environment(root), protector, transport, NullLogger<ResumeTrainingOutbox>.Instance), protector, transport);
    }
    private string[] Files => Directory.Exists(Path.Combine(root, "App_Data")) ? Directory.GetFiles(root, "*.enc", SearchOption.AllDirectories) : [];

    [Fact]
    public void EvidenceExcludesIdentityContactsAndOnlyChangedSupportedFieldsEnterLearning()
    {
        var result = ResumeTrainingOutbox.BuildExample(20, 42, Source, ResumeParsedFacts.Empty, Reviewed, 7);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("Asha", json); Assert.DoesNotContain("9876543210", json); Assert.DoesNotContain("example.test", json);
        Assert.DoesNotContain("Date of birth", json); Assert.Contains("Example Research Labs", json);
        Assert.DoesNotContain("candidateId", json); Assert.Contains("recruiter_review", json);
        Assert.Null(ResumeTrainingOutbox.BuildExample(20, 42, Source, Reviewed, Reviewed, 7));
        Assert.Null(ResumeTrainingOutbox.BuildExample(20, 42, Source, ResumeParsedFacts.Empty, Reviewed, 0));
        Assert.Null(ResumeTrainingOutbox.BuildExample(-1, 42, Source, ResumeParsedFacts.Empty, Reviewed, 7));
        var invented = ResumeParsedFacts.Empty with { CurrentCompany = "Imaginary Employer" };
        Assert.Null(ResumeTrainingOutbox.BuildExample(20, 42, Source, ResumeParsedFacts.Empty, invented, 7));
    }

    [Fact]
    public async Task QueueIsEncryptedPurposeSeparatedAndOnlyAcknowledgedAfterSuccessfulDelivery()
    {
        var (service, protector, transport) = Service();
        service.QueueReviewed(20, 42, Source, ResumeParsedFacts.Empty, Reviewed, 7);
        var path = Assert.Single(Files); var saved = File.ReadAllText(path);
        Assert.DoesNotContain("Research", saved); Assert.Null(protector.UnprotectResumeLearning(saved));
        Assert.Contains("Research", protector.UnprotectResumeTraining(saved));
        transport.Code = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<HttpRequestException>(() => service.FlushOneAsync(default)); Assert.Single(Files);
        transport.Code = HttpStatusCode.OK; transport.Response = "{\"code\":\"unknown\"}";
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.FlushOneAsync(default)); Assert.Single(Files);
        transport.Response = "{\"code\":\"accepted\"}";
        Assert.True(await service.FlushOneAsync(default)); Assert.Empty(Files);
        Assert.False(await service.FlushOneAsync(default));
    }

    [Fact]
    public async Task RevocationDropsPendingCandidateExamplesAndPrecedesOtherCandidates()
    {
        var (service, _, transport) = Service();
        service.QueueReviewed(20, 42, Source, ResumeParsedFacts.Empty, Reviewed, 7);
        service.QueueReviewed(21, 43, Source, ResumeParsedFacts.Empty, Reviewed, 7);
        service.QueuePurge(42);
        Assert.Equal(2, Files.Length);
        Assert.True(await service.FlushOneAsync(default));
        using var body = JsonDocument.Parse(Assert.Single(transport.Bodies));
        Assert.Equal("purge_subject", body.RootElement.GetProperty("action").GetString());
        Assert.Equal(-1, body.RootElement.GetProperty("clientId").GetInt32());
        Assert.Equal(ResumeTrainingOutbox.Subject(42), body.RootElement.GetProperty("subjectRef").GetString());
        Assert.True(await service.FlushOneAsync(default)); Assert.Empty(Files);
        Assert.NotEqual(ResumeTrainingOutbox.Subject(42), ResumeTrainingOutbox.Subject(43));
    }

    [Fact]
    public async Task DisabledLearningDoesNotCreateQueueOrCallNetwork()
    {
        var (service, _, transport) = Service(false);
        service.QueueReviewed(20, 42, Source, ResumeParsedFacts.Empty, Reviewed, 7); service.QueuePurge(42);
        Assert.Empty(Files); Assert.False(await service.FlushOneAsync(default)); Assert.Empty(transport.Bodies);
    }

    [Fact]
    public async Task PermanentSchemaRejectionCannotBlockLaterCorrectionsAndPurgeRemovesIt()
    {
        var (service, _, transport) = Service();
        service.QueueReviewed(20, 42, Source, ResumeParsedFacts.Empty, Reviewed, 7);
        transport.Code = HttpStatusCode.BadRequest;
        Assert.True(await service.FlushOneAsync(default));
        Assert.Contains("rejected", Assert.Single(Files));
        Assert.False(await service.FlushOneAsync(default));
        service.QueuePurge(42);
        Assert.DoesNotContain(Files, path => path.Contains("rejected"));
        transport.Code = HttpStatusCode.OK;
        Assert.True(await service.FlushOneAsync(default)); Assert.Empty(Files);
    }

    [Theory]
    [InlineData("recovering")]
    [InlineData("safety_wait")]
    [InlineData("identity_blocked")]
    public void SupervisorStatusRemainsRecognizedAndDoesNotOfferDuplicateStart(string code)
    {
        var status = LocalLlmRecoveryService.Describe(code);
        Assert.Equal(code, status.Code); Assert.False(status.CanStart);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
