using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class HiringParserRegressionTests
{
    private const string Source = """
Position Name: Platform Engineer
Location: UIDAI Tech Centre, Bangalore
Role Overview
Design reliable platforms using Kubernetes, service mesh (Istio/Linkerd), Jenkins and ArgoCD CI/CD.
Key Responsibilities
Monitor SLOs/SLIs using Prometheus, Grafana, Loki and OpenTelemetry.
Experience with Terraform, Ansible or Chef is desirable.
Experience Requirements
Essential: B.E./B.Tech in Computer Science with minimum 6 years of experience.
Desirable:
Certifications such as CKA/CKAD, Terraform Associate, AWS/GCP DevOps Engineer.
UIDAI Values
Citizen Centricity
Excellence
""";
    private static string Field(string method, string source = Source) =>
        (string)typeof(RecruitmentRequestDocumentParsingService).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [source])!;

    [Fact]
    public void MandatorySkillsDoNotLoseCoreToolsOrPromotePreferredTools()
    {
        var required = Field("RequiredSkills");
        Assert.Contains("Kubernetes", required); Assert.Contains("Istio", required);
        Assert.Contains("Jenkins", required); Assert.Contains("OpenTelemetry", required);
        Assert.DoesNotContain("Terraform", required); Assert.DoesNotContain("AWS", required);
        var preferred = Field("PreferredSkills");
        Assert.Contains("Terraform", preferred); Assert.Contains("Chef", preferred);
    }
    [Theory]
    [InlineData("")]
    [InlineData("Requirements ")]
    public void WrappedRequirementsRetainQualificationAndDoNotBecomeSkills(string label)
    {
        var text=Source.Replace("Experience Requirements\nEssential: B.E./B.Tech in Computer Science with minimum 6 years of experience.",
            $"Experience\nRequirements\nEssential: B.E./B.Tech in Computer Science, Information\n{label}Technology, or equivalent discipline from a reputed university with\nminimum 6 years of experience.");
        var qualification=Field("Qualifications",text);
        Assert.Contains("Information Technology",qualification);
        Assert.StartsWith("B.E.",qualification);
        var skills=Field("RequiredSkills",text);
        Assert.Contains("Kubernetes",skills);Assert.DoesNotContain("university",skills);
    }

    [Fact]
    public void StructuredLocalSkillRequirementsKeepAllSourceTools()
    {
        var fallback=new[]{"Kubernetes","Jenkins","Istio","Linkerd","Prometheus","Grafana"}.Select(name=>new RecruitmentAiHiringSkillSuggestion { SkillName=name,IsRequired=true,WeightPercent=2 }).ToList();
        var rows=(List<RecruitmentAiHiringSkillSuggestion>)typeof(RecruitmentRequestDocumentParsingService)
            .GetMethod("MergeAndNormalizeSkillRequirements",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[new List<RecruitmentAiHiringSkillSuggestion>(),fallback,true])!;
        Assert.Equal(6,rows.Count(r=>r.IsRequired));
        Assert.Equal(100m,rows.Sum(r=>r.WeightPercent));
    }

    [Fact]
    public void SourceVerifiedFastPathRequiresCoreFactsAndAnExplicitRoleLabel()
    {
        var draft = new SaveRecruitmentRequisition { PositionTitle="Platform Engineer", JobLocation=Field("JobLocation"),
            ExperienceRange=Field("Experience"), Qualification=Field("Qualifications"), RequiredSkills=Field("RequiredSkills") };
        Assert.True(RecruitmentRequestDocumentParsingService.HasStrongLocalHiringFacts(draft,Source));
        Assert.False(RecruitmentRequestDocumentParsingService.HasStrongLocalHiringFacts(draft,Source.Replace("Position Name:","Unknown heading:")));
        draft.Qualification="";
        Assert.False(RecruitmentRequestDocumentParsingService.HasStrongLocalHiringFacts(draft,Source));
    }

    [Fact]
    public void ExplicitPositionTitleWinsOverLongDocumentHeading()
    {
        var title=(string)typeof(RecruitmentRequestDocumentParsingService).GetMethod("PositionTitle", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null,["Job Description for the position of Platform Engineer, Band D\n"+Source,"jd.txt"])!;
        Assert.Equal("Platform Engineer", title);
    }

    [Theory]
    [InlineData("/Subtype /Image /Filter /DCTDecode", false)]
    [InlineData("/Length 120", true)]
    public void PdfBinaryStreamsCannotMasqueradeAsText(string header,bool textExpected)
    {
        var bytes=System.Text.Encoding.Latin1.GetBytes($"%PDF-1.4\n1 0 obj\n<< {header} >>\nstream\nBT\n(Readable words accidentally inside image bytes) Tj\nET\nendstream\nendobj");
        var value=(string)typeof(ResumeParsingService).GetMethod("ReadPdfOperators",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[bytes])!;
        Assert.Equal(textExpected,!string.IsNullOrEmpty(value));
    }

    private sealed class RealOcrFactAttribute : FactAttribute
    {
        public RealOcrFactAttribute() { if (Environment.GetEnvironmentVariable("HRMS_REAL_OCR_TEST") != "1") Skip="Explicit local fixture opt-in required."; }
    }
    [RealOcrFact]
    public async Task ActualScannedPdfHasReadableCoreEvidence()
    {
        var source=Environment.GetEnvironmentVariable("HRMS_OCR_FIXTURE") ?? throw new InvalidOperationException("Fixture path required.");
        var bytes=await File.ReadAllBytesAsync(source);
        var parser=new ResumeParsingService(null!,null!,NullLogger<ResumeParsingService>.Instance);
        var result=await parser.ParseAsync(new MemoryStream(bytes),Path.GetFileName(source),bytes.Length,CancellationToken.None);
        Assert.True(result.ParserName == "LocalOCR", $"Parser={result.ParserName}; status={result.Status}; textLength={result.Text.Length}");
        Assert.Contains("Platform Engineer",result.Text);
        Assert.Contains("Kubernetes",result.Text);
        var draft=new SaveRecruitmentRequisition { PositionTitle="Platform Engineer", JobLocation=Field("JobLocation",result.Text),
            ExperienceRange=Field("Experience",result.Text), Qualification=Field("Qualifications",result.Text), RequiredSkills=Field("RequiredSkills",result.Text) };
        Assert.True(RecruitmentRequestDocumentParsingService.HasStrongLocalHiringFacts(draft,result.Text),
            $"Missing source core: location={draft.JobLocation}; experience={draft.ExperienceRange}; qualification={draft.Qualification}; skills={draft.RequiredSkills}");
    }
}
