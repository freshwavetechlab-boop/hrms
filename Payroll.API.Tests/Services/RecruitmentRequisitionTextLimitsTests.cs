using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class RecruitmentRequisitionTextLimitsTests
{
    public static IEnumerable<object[]> Fields => RecruitmentRequisitionTextLimits.Fields.Select(rule => new object[] { rule.Field });
    private static PropertyInfo Property(string field) => typeof(SaveRecruitmentRequisition).GetProperties()
        .Single(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name) == field);

    [Theory]
    [MemberData(nameof(Fields))]
    public void EveryFieldAcceptsBoundaryAndRejectsOneExtraWithoutChangingText(string field)
    {
        var rule = RecruitmentRequisitionTextLimits.Fields.Single(row => row.Field == field);
        var draft = new SaveRecruitmentRequisition();
        var property = Property(field);
        property.SetValue(draft, new string('x', rule.Maximum));
        Assert.Empty(RecruitmentRequisitionTextLimits.Validate(draft));
        var original = new string('x', rule.Maximum + 1);
        property.SetValue(draft, original);
        var error = Assert.Single(RecruitmentRequisitionTextLimits.Validate(draft));
        Assert.Equal(field, error.Field);
        Assert.Contains($"maximum {rule.Maximum} {rule.Unit}", error.Message);
        Assert.DoesNotContain(original, error.Message);
        Assert.Equal(original, property.GetValue(draft));
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public void UnicodeMatchesMysqlCharacterAndUtf8ByteSemantics(string field)
    {
        var rule = RecruitmentRequisitionTextLimits.Fields.Single(row => row.Field == field);
        var draft = new SaveRecruitmentRequisition();
        var value = rule.Unit == "characters"
            ? string.Concat(Enumerable.Repeat("😀", rule.Maximum))
            : new string('ह', rule.Maximum / 3);
        Property(field).SetValue(draft, value);
        Assert.Empty(RecruitmentRequisitionTextLimits.Validate(draft));
        Property(field).SetValue(draft, value + "ह");
        Assert.Single(RecruitmentRequisitionTextLimits.Validate(draft));
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public async Task CreateAndUpdateRejectOverflowBeforeOpeningAnyDatabase(string field)
    {
        var rule = RecruitmentRequisitionTextLimits.Fields.Single(row => row.Field == field);
        var repository = new RecruitmentRepository(new ConfigurationBuilder().Build()); // No DB configured.
        foreach (var id in new long[] { 0, 123 })
        {
            var draft = new SaveRecruitmentRequisition { Id = id };
            Property(field).SetValue(draft, new string('x', rule.Maximum + 1));
            var (row, error) = await repository.SaveDraftAsync(draft, new AuthUser());
            Assert.Null(row);
            Assert.Contains(rule.Label, error);
            Assert.Contains("maximum", error);
        }
    }

    [Fact]
    public void ContractCoversEveryStoredStringExceptUnboundedJsonEvidence()
    {
        var properties = typeof(SaveRecruitmentRequisition).GetProperties()
            .Where(p => p.PropertyType == typeof(string) && p.Name != nameof(SaveRecruitmentRequisition.SourceParsedJson))
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).Order().ToArray();
        Assert.Equal(properties, RecruitmentRequisitionTextLimits.Fields.Select(rule => rule.Field).Order());
    }

    [Theory]
    [InlineData("BuiltIn")]
    [InlineData("LocalRAG + Gemini")]
    [InlineData("LocalRAG + LocalOpenAICompatible")]
    public void AllParserResultsBecomeReviewableWithoutLosingEvidence(string parser)
    {
        var result = new RecruitmentRequestDocumentParseResult { Status = "Parsed", ParserName = parser,
            Draft = new SaveRecruitmentRequisition { Qualification = new string('x', 251), Languages = new string('y', 251), SourceParsedJson = "{\"kept\":true}" } };
        var reviewed = RecruitmentRequisitionTextLimits.Review(result);
        Assert.Same(result, reviewed);
        Assert.Equal("NeedsReview", result.Status);
        Assert.Equal(251, result.Draft.Qualification.Length);
        Assert.Equal("{\"kept\":true}", result.Draft.SourceParsedJson);
        Assert.Contains("Qualification", result.ReviewFields);
        Assert.Contains("Languages", result.ReviewFields);
        Assert.Equal(2, result.Warnings.Count);
        RecruitmentRequisitionTextLimits.Review(result);
        Assert.Equal(2, result.Warnings.Count);
    }

    [Fact]
    public void DeterministicMultipleQualificationsAreKeptButFlaggedBeforeSave()
    {
        var source = "Qualifications\n" + string.Join("\n", Enumerable.Range(1, 8)
            .Select(n => $"Bachelor of Engineering in Computer Science specialization {n} from a recognized university."));
        var qualification = (string)typeof(RecruitmentRequestDocumentParsingService)
            .GetMethod("Qualifications", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [source])!;
        Assert.True(qualification.Length > 250);
        var result = RecruitmentRequisitionTextLimits.Review(new RecruitmentRequestDocumentParseResult
        { Status = "Parsed", Draft = new SaveRecruitmentRequisition { Qualification = qualification } });
        Assert.Equal(qualification, result.Draft.Qualification);
        Assert.Equal("NeedsReview", result.Status);
    }

    [Fact]
    public void ValidDraftRetainsItsStatusAndNullOptionalTextDoesNotCrashValidation()
    {
        var result = new RecruitmentRequestDocumentParseResult { Status = "Parsed", Draft = new() { Qualification = null! } };
        Assert.Equal("Parsed", RecruitmentRequisitionTextLimits.Review(result).Status);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void CorruptedOcrProseIsHeldForReviewInsteadOfBeingTreatedAsAJobFact()
    {
        var normal = "The manager will coordinate the operations team, maintain service-level agreements, and work with all stakeholders.";
        var corrupted = string.Join(' ', Enumerable.Repeat("Wrill be respon.sible./or rnancrging the tecrm o./'operators", 8));
        Assert.False(RecruitmentRequestDocumentParsingService.HasDamagedProse(normal));
        Assert.True(RecruitmentRequestDocumentParsingService.HasDamagedProse(corrupted));
    }
}
