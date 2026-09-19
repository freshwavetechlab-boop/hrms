using Payroll.API.Services;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Payroll.API.Tests.Services;

public class EngineAndOfferTests
{
    [Fact]
    public void Offer_PrivateAssetResolvesFromRuntimeDirectoryWithoutMachineSpecificPath()
    {
        var runtime = Path.Combine(Path.GetTempPath(), "offer-api-runtime");
        Assert.Equal(Path.Combine(runtime, "PrivateAssets", "OfferSigning", "GadAuthrizedSignAndSeal.png"), BrandedOfferPdfService.SigningAssetPath(null, runtime));
        Assert.Equal(Path.Combine(runtime, "private", "seal.png"), BrandedOfferPdfService.SigningAssetPath(Path.Combine("private", "seal.png"), runtime));
        var mounted = Path.Combine(Path.GetTempPath(), "private-mounted-seal.png");
        Assert.Equal(mounted, BrandedOfferPdfService.SigningAssetPath(mounted, runtime));
    }
    [Theory]
    [InlineData("Parsed", false)]
    [InlineData("NeedsReview", true)]
    [InlineData("Failed", true)]
    [InlineData("Pending", true)]
    [InlineData(null, true)]
    public void Ats_InvalidResumeFailsPreflightWithoutProviderCall(string? status, bool hasError) =>
        Assert.Equal(hasError, Payroll.API.Repositories.RecruitmentTalentRepository.AtsResumePreconditionError(status).Length > 0);
    [Fact]
    public async Task Monitor_SharedQueueShowsOtherReplicaWorkAndRecoversFromTelemetryFailure()
    {
        var monitor = new EngineRuntimeMonitor();
        var snapshot = await monitor.SnapshotWithJobsAsync(_ => Task.FromResult(new List<Payroll.API.Models.EngineJobObservation> {
            new() { Status = "Processing", StartedAt = DateTime.UtcNow.AddSeconds(-10), UpdatedAt = DateTime.UtcNow },
            new() { Status = "Queued", UpdatedAt = DateTime.UtcNow }
        }), CancellationToken.None);
        var metric = snapshot.Engines.Single(e => e.Code == "ats-scoring");
        Assert.Equal(1, metric.ActiveRequests);
        Assert.Equal(1, metric.QueuedRequests);
        Assert.InRange(metric.LoadPercent, 30, 40);
        var fallback = await new EngineRuntimeMonitor().SnapshotWithJobsAsync(_ => throw new InvalidOperationException("private DB error"), CancellationToken.None);
        Assert.DoesNotContain("private DB error", System.Text.Json.JsonSerializer.Serialize(fallback));
        Assert.Contains("shared queue unavailable", fallback.Engines.Single(e => e.Code == "ats-scoring").Coverage);
    }

    [Fact]
    public void Offer_ChangedOrLegacyApprovalNeverAuthorizesSigning()
    {
        var date = new DateTime(2026, 10, 1);
        var payload = System.Text.Json.JsonSerializer.Serialize(new { OfferLetterTemplateHash = BrandedOfferPdfService.TemplateHash("approved"), OfferedCtc = 750000, Currency = "INR", ProposedJoiningDate = date, OfferTemplateId = 26 });
        Assert.True(BrandedOfferPdfService.MatchesApprovedTerms(payload, "approved", 750000, "INR", date, 26));
        Assert.False(BrandedOfferPdfService.MatchesApprovedTerms(payload, "changed", 750000, "INR", date, 26));
        Assert.False(BrandedOfferPdfService.MatchesApprovedTerms(payload, "approved", 850000, "INR", date, 26));
        Assert.False(BrandedOfferPdfService.MatchesApprovedTerms(payload, "approved", 750000, "INR", date.AddDays(1), 26));
        Assert.False(BrandedOfferPdfService.MatchesApprovedTerms("{}", "approved", 750000, "INR", date, 26));
    }
    [Fact]
    public void Monitor_TracksActualWorkAndCompletesOnlyOnce()
    {
        var monitor = new EngineRuntimeMonitor();
        var work = monitor.Observe("ats-scoring");
        Assert.Equal(1, monitor.Snapshot().Engines.Single(e => e.Code == "ats-scoring").ActiveRequests);
        work.Succeeded = true;
        work.Dispose(); work.Dispose();
        var metric = monitor.Snapshot().Engines.Single(e => e.Code == "ats-scoring");
        Assert.Equal(0, metric.ActiveRequests);
        Assert.Equal(1, metric.RequestsLastFiveMinutes);
        Assert.Equal(100, metric.SuccessRate);
        Assert.Null(EngineRuntimeMonitor.Classify("POST", "/api/recruitment/applications/1/score"));
    }

    [Fact]
    public void Monitor_RecordsWorkerFailureWithoutSensitiveContent()
    {
        var monitor = new EngineRuntimeMonitor();
        using (monitor.Observe("ats-scoring")) { }
        var metric = monitor.Snapshot().Engines.Single(e => e.Code == "ats-scoring");
        Assert.Equal("Attention", metric.State);
        Assert.Equal(0, metric.SuccessRate);
        Assert.Equal(0, metric.ActiveRequests);
    }

    [Theory]
    [InlineData(750000, "Seven lakh fifty thousand only")]
    [InlineData(1234567, "Twelve lakh thirty four thousand five hundred sixty seven only")]
    [InlineData(10000000, "One crore only")]
    public void Offer_UsesIndianAmountWords(decimal amount, string expected) => Assert.Equal(expected, BrandedOfferPdfService.AmountInWords(amount));

    [Fact]
    public void Offer_UsesTwoBrandedPagesAndNoSignatureInDraft()
    {
        var values = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
            ["offerNumber"]="GAD/HR/UIDAI/2026", ["offerDate"]="17-09-2026", ["candidateName"]="Sample Candidate",
            ["candidateEmail"]="candidate@example.com", ["candidatePhone"]="9999999999", ["positionTitle"]="Centre Manager - Aadhaar Seva Kendra, Band - 'A'",
            ["jobLocation"]="ASK - Palghar (Mumbai), Regional Office, Mumbai", ["formattedCtc"]="7,50,000", ["ctcInWords"]="Seven lakh fifty thousand only", ["joiningDate"]="01 October 2026"
        };
        var rendered = new TemplatePdfService().RenderOfferText(BrandedOfferPdfService.DefaultTemplate, values);
        Assert.NotNull(rendered.Text);
        Assert.DoesNotContain("{{", rendered.Text);
        // Only test inputs; no real candidate file or signature is needed.
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
        var logo = File.ReadAllBytes(Path.Combine(root, "ess-mss/public/assets/organization-logo.png"));
        var bytes = new BrandedOfferPdfService().Create(rendered.Text!, logo, null);
        using var document = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        Assert.Equal(2, document.PageCount);
        foreach (var page in document.Pages)
        {
            Assert.NotNull(page.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/XObject"));
            Assert.NotNull(page.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/ExtGState")?.Elements["/OfferWatermark"]);
        }
        var output = Path.Combine(root, "playwright-e2e/artifacts/offer-pdf-review/generated-draft.pdf");
        if (Directory.Exists(Path.GetDirectoryName(output))) File.WriteAllBytes(output, bytes);
    }
}
