using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class LocalLlmProtocolTests
{
    private const string TestKey = "synthetic-test-key-never-a-real-credential";
    private static RecruitmentAiScoringSecretRow Settings(string provider = "LocalOpenAICompatible") => new()
    {
        ProviderCode = provider, ModelName = "hrms-local", EndpointUrl = "https://example.invalid/llm-api.php", RequestTimeoutSeconds = 210
    };

    private static string Envelope(string content, string? reason = "stop") => JsonSerializer.Serialize(new
    {
        choices = new[] { new { finish_reason = reason, message = new { content } } }
    });

    [Fact]
    public async Task LocalRequest_UsesExactEndpointBearerAndBoundedJsonObjectContract()
    {
        using var request = LocalLlmProtocol.CreateRequest(Settings(), TestKey, "Synthetic resume", "Extract facts.");
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://example.invalid/llm-api.php", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(TestKey, request.Headers.Authorization.Parameter);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        var bytes = await request.Content.ReadAsByteArrayAsync();
        using var payload = JsonDocument.Parse(bytes);
        var root = payload.RootElement;
        Assert.Equal(256, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("hrms-local", root.GetProperty("model").GetString());
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(0, root.GetProperty("temperature").GetInt32());
        Assert.Equal(2, root.GetProperty("messages").GetArrayLength());
        Assert.Equal(6, root.EnumerateObject().Count());
        Assert.All(root.GetProperty("messages").EnumerateArray(), message => Assert.Equal(2, message.EnumerateObject().Count()));
        Assert.DoesNotContain(TestKey, Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain(TestKey, request.RequestUri.ToString());
        Assert.True(bytes.Length <= LocalLlmProtocol.BodyBytes);
    }

    [Theory]
    [InlineData("http://example.invalid/llm-api.php")]
    [InlineData("https://user:private@example.invalid/llm-api.php")]
    [InlineData("https://example.invalid/llm-api.php#private")]
    [InlineData("/llm-api.php")]
    public void InvalidEndpoint_FailsWithoutEchoingCredentials(string endpoint)
    {
        var settings = Settings(); settings.EndpointUrl = endpoint;
        var error = Assert.Throws<LocalLlmException>(() => LocalLlmProtocol.CreateRequest(settings, TestKey, "sensitive document", null));
        Assert.Equal("ConfigurationError", error.Status);
        Assert.DoesNotContain(TestKey, error.Message);
        Assert.DoesNotContain(endpoint, error.Message);
        Assert.DoesNotContain("sensitive document", error.Message);
    }

    [Fact]
    public async Task AsciiContent_ExactCombinedByteLimitPassesOneExtraByteFails()
    {
        var reservedBytes = Encoding.UTF8.GetByteCount(LocalLlmProtocol.SystemInstruction + "\n");
        var prompt = new string('x', LocalLlmProtocol.ContentBytes - reservedBytes);
        using var message = LocalLlmProtocol.CreateRequest(Settings(), TestKey, prompt, null);
        Assert.True((await message.Content!.ReadAsByteArrayAsync()).Length < LocalLlmProtocol.BodyBytes);
        var error = Assert.Throws<LocalLlmException>(() => LocalLlmProtocol.CreateRequest(Settings(), TestKey, prompt + "x", null));
        Assert.Equal("InputTooLarge", error.Status);
    }

    [Fact]
    public void UnicodeContent_CountsUtf8BytesRatherThanCharacters()
    {
        var prompt = new string('\u0905', 4_000);
        Assert.True(prompt.Length < LocalLlmProtocol.ContentBytes);
        Assert.Equal(12_000, Encoding.UTF8.GetByteCount(prompt));
        var error = Assert.Throws<LocalLlmException>(() => LocalLlmProtocol.CreateRequest(Settings(), TestKey, prompt, null));
        Assert.Equal("InputTooLarge", error.Status);
    }

    [Fact]
    public void JsonEscaping_EnforcesFullBodyCapSeparatelyFromContentCap()
    {
        var prompt = new string('"', 3_000);
        Assert.True(Encoding.UTF8.GetByteCount(prompt + LocalLlmProtocol.SystemInstruction) < LocalLlmProtocol.ContentBytes);
        var error = Assert.Throws<LocalLlmException>(() => LocalLlmProtocol.CreateRequest(Settings(), TestKey, prompt, null));
        Assert.Equal("InputTooLarge", error.Status);
        Assert.Contains("16 KiB", error.Message);
    }

    [Fact]
    public void CompleteObject_RequiresStopAndReturnsUnmodifiedJson()
    {
        const string json = "{\"confidence\":0.8,\"fullName\":\"Asha Example\"}";
        Assert.Equal(json, LocalLlmProtocol.ReadResponse(Envelope(json)));
    }

    [Fact]
    public void LengthFinish_IsRejectedEvenIfJsonLooksComplete()
    {
        var error = Assert.Throws<LocalLlmException>(() => LocalLlmProtocol.ReadResponse(Envelope("{}", "length")));
        Assert.Equal("OutputTruncated", error.Status);
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"not an object\"")]
    [InlineData("```json\n{}\n```")]
    [InlineData("I cannot answer that")]
    public void InvalidModelContent_IsNotApplied(string content)
    {
        var error = Assert.Throws<LocalLlmException>(() => LocalLlmProtocol.ReadResponse(Envelope(content)));
        Assert.Equal("InvalidResponse", error.Status);
        Assert.DoesNotContain(content, error.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":[]}}]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"{}\"}}]}")]
    [InlineData("null")]
    public void InvalidEnvelope_ProducesSanitizedStructuredFailure(string response)
    {
        Assert.Equal("InvalidResponse", Assert.Throws<LocalLlmException>(() => LocalLlmProtocol.ReadResponse(response)).Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("tool_calls")]
    [InlineData("content_filter")]
    public void UnexpectedFinishReason_IsRejected(string? reason)
    {
        Assert.Equal("InvalidResponse", Assert.Throws<LocalLlmException>(() => LocalLlmProtocol.ReadResponse(Envelope("{}", reason))).Status);
    }

    private static Dictionary<string, object?> Score() => new()
    {
        ["overallFit"] = .8m, ["confidence"] = .9m,
        ["criteria"] = new Dictionary<string, object?>
        {
            ["requiredSkills"] = .9m, ["preferredSkills"] = 0m, ["experience"] = 1m,
            ["qualification"] = .8m, ["certifications"] = 0m, ["roleSimilarity"] = .8m
        }
    };

    [Fact]
    public void CompleteScoringRatios_AreAccepted() => LocalLlmProtocol.ValidateScore(JsonSerializer.Serialize(Score()));

    [Theory]
    [InlineData("requiredSkills")]
    [InlineData("preferredSkills")]
    [InlineData("experience")]
    [InlineData("qualification")]
    [InlineData("certifications")]
    [InlineData("roleSimilarity")]
    public void MissingCriterion_IsNeverSilentlyZeroFilled(string criterion)
    {
        var score = Score(); ((Dictionary<string, object?>)score["criteria"]!).Remove(criterion);
        Assert.Equal("InvalidResponse", Assert.Throws<LocalLlmException>(() => LocalLlmProtocol.ValidateScore(JsonSerializer.Serialize(score))).Status);
    }

    [Theory]
    [InlineData(-.01)]
    [InlineData(1.01)]
    [InlineData(95)]
    public void InvalidRatio_IsNotNormalizedIntoAnAcceptedScore(double value)
    {
        var score = Score(); score["confidence"] = value;
        Assert.Throws<LocalLlmException>(() => LocalLlmProtocol.ValidateScore(JsonSerializer.Serialize(score)));
    }

    [Theory]
    [InlineData("{\"confidence\":\"0.9\",\"fullName\":\"Asha\"}", false)]
    [InlineData("{\"confidence\":0.9}", false)]
    [InlineData("{\"confidence\":0.9,\"sections\":[]}", false)]
    [InlineData("{\"confidence\":0.9,\"requiredSkills\":[]}", true)]
    public void MissingOrInvalidFacts_AreRejected(string json, bool hiring) =>
        Assert.Equal("InvalidResponse", Assert.Throws<LocalLlmException>(() => LocalLlmProtocol.ValidateFacts(json, hiring)).Status);

    [Fact]
    public void ResumeGrounding_RemovesInventedFieldsAndModelSuppliedMetadata()
    {
        const string source = "Asha Example\nasha@example.invalid\n+91 90000 00001\nSkills: Terraform and Linux";
        var result = new RecruitmentAiResumeDocumentSuggestion
        {
            Confidence = .8m, FullName = "Asha Example", Email = "invented@example.invalid", Phone = "+91 90000 00001",
            ResidentialAddress = "Invented City", SummaryText = "Invented summary", TotalExperienceMonths = 120,
            FieldMetadata = new() { ["email"] = new() { SourceType = "exact", Confidence = 1m } },
            Sections = [new() { SectionCode = "SKILLS", Content = "Terraform and Linux", Confidence = 1m }, new() { Content = "Invented certification" }]
        };
        LocalLlmProtocol.GroundResume(result, source);
        Assert.Equal("Asha Example", result.FullName);
        Assert.Empty(result.Email); Assert.Empty(result.ResidentialAddress); Assert.Empty(result.SummaryText);
        Assert.Null(result.TotalExperienceMonths); Assert.False(result.FieldMetadata.ContainsKey("email"));
        Assert.Equal("exact", result.FieldMetadata["fullName"].SourceType);
        var section = Assert.Single(result.Sections);
        Assert.Equal("Terraform and Linux", section.Content); Assert.Equal(.8m, section.Confidence);
    }

    [Fact]
    public void ResumeGrounding_NormalizesWhitespaceButRequiresSourceEvidence()
    {
        var result = new RecruitmentAiResumeDocumentSuggestion { Confidence = .9m, FullName = "Asha Example" };
        LocalLlmProtocol.GroundResume(result, "ASHA\t  EXAMPLE");
        Assert.Equal("Asha Example", result.FullName);
        Assert.Throws<LocalLlmException>(() => LocalLlmProtocol.GroundResume(new() { FullName = "Invented Person", Confidence = .9m }, "Asha Example"));
    }

    [Theory]
    [InlineData("Asha", "Asha Example")]
    [InlineData("Example", "Asha Example")]
    [InlineData("Asha Example", "Asha Example Kumar")]
    public void ResumeGrounding_PartialNameIsNotMarkedAsExact(string name, string source)
    {
        var result = new RecruitmentAiResumeDocumentSuggestion { FullName = name, Confidence = .9m };
        Assert.Throws<LocalLlmException>(() => LocalLlmProtocol.GroundResume(result, source));
        Assert.Empty(result.FullName); Assert.Empty(result.FieldMetadata);
    }

    [Theory]
    [InlineData("Name: Asha Example | asha@example.invalid")]
    [InlineData("Full Name - Asha Example\nasha@example.invalid")]
    public void ResumeGrounding_CompleteLabelledNameIsPreserved(string source)
    {
        var result = new RecruitmentAiResumeDocumentSuggestion { FullName = "Asha Example", Confidence = .9m };
        LocalLlmProtocol.GroundResume(result, source);
        Assert.Equal("Asha Example", result.FullName); Assert.Equal("exact", result.FieldMetadata["fullName"].SourceType);
    }

    [Theory]
    [InlineData("asha@example.invalid", "notasha@example.invalid")]
    [InlineData("asha@example.invalid", "asha@example.invalid.uk")]
    [InlineData("asha@example.invalid", "not.asha@example.invalid")]
    public void ResumeGrounding_PartialEmailIsRejected(string email, string source)
    {
        var result = new RecruitmentAiResumeDocumentSuggestion { Email = email, Confidence = .9m };
        Assert.Throws<LocalLlmException>(() => LocalLlmProtocol.GroundResume(result, source));
        Assert.Empty(result.Email);
    }

    [Theory]
    [InlineData("12345", "Phone: 12345")]
    [InlineData("1234567890123456", "Phone: 1234567890123456")]
    [InlineData("9000000001", "Phone: 90000000012")]
    public void ResumeGrounding_InvalidOrPartialPhoneIsRejected(string phone, string source)
    {
        var result = new RecruitmentAiResumeDocumentSuggestion { Phone = phone, Confidence = .9m };
        Assert.Throws<LocalLlmException>(() => LocalLlmProtocol.GroundResume(result, source));
        Assert.Empty(result.Phone);
    }

    [Theory]
    [InlineData("Go", "Golang")]
    [InlineData("Java", "JavaScript")]
    [InlineData("Terraform", "NotTerraform")]
    [InlineData("example", "person@example.invalid")]
    public void HiringGrounding_PartialTokenDoesNotBecomeASkill(string skill, string source)
    {
        var result = new RecruitmentAiHiringDocumentSuggestion { RequiredSkills = [skill], Confidence = .9m };
        Assert.Throws<LocalLlmException>(() => LocalLlmProtocol.GroundHiring(result, source));
        Assert.Empty(result.RequiredSkills);
    }

    [Fact]
    public void ResumeGrounding_RejectsLongUnknownOrInvalidConfidenceSections()
    {
        var longExcerpt = new string('x', 71);
        var result = new RecruitmentAiResumeDocumentSuggestion
        {
            FullName = "Asha Example", Confidence = .9m,
            Sections = [new() { SectionCode = "SKILLS", Content = longExcerpt, Confidence = .9m },
                new() { SectionCode = "ADMINISTRATOR", Content = "Linux", Confidence = .9m },
                new() { SectionCode = "SKILLS", Content = "Linux", Confidence = -.01m },
                new() { SectionCode = "SKILLS", Content = "Linux", Confidence = 1.01m }]
        };
        LocalLlmProtocol.GroundResume(result, "Asha Example\nLinux\n" + longExcerpt);
        Assert.Equal("Asha Example", result.FullName); Assert.Empty(result.Sections);
    }

    [Theory]
    [InlineData("synthetic-secret\r\nInjected: value")]
    [InlineData("synthetic-secret\nvalue")]
    [InlineData("synthetic-secret\rvalue")]
    public void MalformedKey_IsSanitizedConfigurationFailure(string key)
    {
        var error = Assert.Throws<LocalLlmException>(() => LocalLlmProtocol.CreateRequest(Settings(), key, "Synthetic input", null));
        Assert.Equal("ConfigurationError", error.Status); Assert.DoesNotContain("synthetic-secret", error.Message);
    }

    [Theory]
    [InlineData(20, "super_admin", true)]
    [InlineData(20, "client_admin", true)]
    [InlineData(null, "system_admin", true)]
    [InlineData(null, "super_admin", false)]
    public void ExplicitFrevoModelOverride_DoesNotBypassGlobalActiveSuperAdminBoundary(int? clientId, string role, bool active)
    {
        // Null runtime/dependencies make unintended execution immediately visible; this path must stop at authorization.
        var service = new FrevoPilotAnalyticsService(new ConfigurationBuilder().Build(), null!, null!, null!, null!, null!, NullLogger<FrevoPilotAnalyticsService>.Instance);
        var actor = new AuthUser { Id = 7, ClientId = clientId, IsActive = active, Roles = [role], Permissions = ["security.manage", "settings.manage"] };
        var result = service.Start(new("Count active employees", null, 123), actor);
        Assert.Null(result.Run); Assert.Contains("global super administrators", result.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ExplicitFrevoModelOverride_InvalidIdentifierIsRejectedBeforeRuntime(long modelId)
    {
        var service = new FrevoPilotAnalyticsService(new ConfigurationBuilder().Build(), null!, null!, null!, null!, null!, NullLogger<FrevoPilotAnalyticsService>.Instance);
        var result = service.Start(new("Count active employees", null, modelId), new AuthUser { Id = 7, IsActive = true, Roles = ["super_admin"] });
        Assert.Null(result.Run); Assert.Contains("valid saved model", result.Error);
    }

    [Fact]
    public void HiringGrounding_DropsUnsolicitedFinancialAndAdministrativeFacts()
    {
        var result = new RecruitmentAiHiringDocumentSuggestion
        {
            Confidence = .85m, PositionTitle = "Platform Engineer", JobLocation = "Invented City", SalaryMax = 999999,
            NumberOfOpenings = 100, ClientName = "Invented Client", SourceAuthority = "Forged Authority",
            RequiredSkills = ["Terraform", "Linux", "InventedSkill", "Terraform"], PreferredSkills = ["Linux", "Docker"],
            FieldMetadata = new() { ["salaryMax"] = new() { SourceType = "exact", Confidence = 1m } }
        };
        LocalLlmProtocol.GroundHiring(result, "Platform Engineer\nRequired: Terraform, Linux\nPreferred: Docker");
        Assert.Equal("Platform Engineer", result.PositionTitle); Assert.Empty(result.JobLocation);
        Assert.Null(result.SalaryMax); Assert.Null(result.NumberOfOpenings); Assert.Empty(result.ClientName); Assert.Empty(result.SourceAuthority);
        Assert.Equal(new[] { "Terraform", "Linux" }, result.RequiredSkills); Assert.Equal(new[] { "Docker" }, result.PreferredSkills);
        Assert.False(result.FieldMetadata.ContainsKey("salaryMax"));
        Assert.Equal(.85m, result.Confidence);
    }

    [Fact]
    public void HiringGrounding_AllInventedFactsFailClosed() => Assert.Throws<LocalLlmException>(() =>
        LocalLlmProtocol.GroundHiring(new() { Confidence = .9m, PositionTitle = "Invented Job", RequiredSkills = ["InventedSkill"] }, "Platform Engineer, Linux"));

    [Theory]
    [InlineData("Groq", "https://api.groq.com/openai/v1/chat/completions")]
    [InlineData("OpenAI", "https://api.openai.com/v1/chat/completions")]
    [InlineData("Grok", "https://api.x.ai/v1/chat/completions")]
    [InlineData("OpenAICompatible", "https://example.invalid/llm-api.php/chat/completions")]
    public async Task ExistingCompatibleProviders_RetainTheirOriginalLargeTokenPayload(string provider, string endpoint)
    {
        using var request = CloudRequest(provider);
        Assert.Equal(endpoint, request.RequestUri!.AbsoluteUri);
        using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert.Equal(8192, payload.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(payload.RootElement.TryGetProperty("response_format", out _));
        Assert.False(payload.RootElement.TryGetProperty("stream", out _));
        Assert.Equal("Synthetic cloud instruction", payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ExistingGemini_RetainsItsOwnAuthJsonAndOutputCap()
    {
        using var request = CloudRequest("Gemini");
        Assert.Equal(TestKey, Assert.Single(request.Headers.GetValues("x-goog-api-key")));
        Assert.Null(request.Headers.Authorization);
        using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert.Equal(8192, payload.RootElement.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal("application/json", payload.RootElement.GetProperty("generationConfig").GetProperty("responseMimeType").GetString());
        Assert.False(payload.RootElement.TryGetProperty("max_tokens", out _));
    }

    private static HttpRequestMessage CloudRequest(string provider) => (HttpRequestMessage)typeof(RecruitmentAiScoringService)
        .GetMethod("CreateProviderRequest", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, [Settings(provider), TestKey, "Synthetic cloud prompt", "Synthetic cloud instruction", 8192, null, ""])!;

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(504)]
    [InlineData(500)]
    public async Task LocalTransport_ProviderErrorsDoNotEchoSecretsOrSensitiveResponseBody(int status)
    {
        var handler = new StubHandler(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(TestKey + " sensitive resume") });
        var result = await SendLocal(handler);
        Assert.False((bool)Property(result, "Success"));
        Assert.DoesNotContain(TestKey, (string)Property(result, "Error"));
        Assert.DoesNotContain("sensitive resume", (string)Property(result, "Error"));
        Assert.Equal("", Property(result, "Body")); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task LocalTransport_OversizedResponseIsRejectedWithoutEchoingBody()
    {
        var handler = new StubHandler(new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 131_073)) });
        var result = await SendLocal(handler);
        Assert.Equal("InvalidResponse", Property(result, "Status")); Assert.Equal("", Property(result, "Body"));
    }

    [Fact]
    public async Task LocalTransport_TruncatedResponseIsNotSuccessful()
    {
        var handler = new StubHandler(new(HttpStatusCode.OK) { Content = new StringContent(Envelope("{}", "length")) });
        var result = await SendLocal(handler);
        Assert.False((bool)Property(result, "Success")); Assert.Equal("OutputTruncated", Property(result, "Status"));
    }

    private static object Property(object result, string name) => result.GetType().GetProperty(name)!.GetValue(result)!;

    [Theory]
    [InlineData("LocalOpenAICompatible", false, 0)]
    [InlineData("LocalOpenAICompatible", true, 1)]
    [InlineData("Gemini", false, 1)]
    public async Task LocalUnsentWork_DoesNotAttemptProviderHealthOrUsageWrites(string provider, bool wasSent, int expectedWarnings)
    {
        // Deliberately no database configuration. A write attempt is caught/logged;
        // a local admission rejection must return before reaching that boundary.
        var logger = new ObservationLogger();
        var service = new RecruitmentAiScoringService(null!, null!, null!, logger);
        var method = typeof(RecruitmentAiScoringService).GetMethod("RecordProviderUsageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(service, [Settings(provider), "", false, wasSent, "Local admission busy", CancellationToken.None, false])!;
        Assert.Equal(expectedWarnings, logger.Warnings);
    }

    private sealed class ObservationLogger : Microsoft.Extensions.Logging.ILogger<RecruitmentAiScoringService>
    {
        public int Warnings;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (level == Microsoft.Extensions.Logging.LogLevel.Warning) Warnings++; }
    }

    private static async Task<object> SendLocal(StubHandler handler)
    {
        var service = new RecruitmentAiScoringService(null!, null!, new StubFactory(handler), NullLogger<RecruitmentAiScoringService>.Instance);
        var task = (Task)typeof(RecruitmentAiScoringService).GetMethod("SendLocalProviderAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [Settings(), TestKey, "Synthetic prompt", null, null, CancellationToken.None])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private sealed class StubFactory(StubHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(response); }
    }
}
