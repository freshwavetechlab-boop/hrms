using System.Text.Json;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class RecruitmentSimpleJobDescriptionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Office\nAssistant")]
    [InlineData("Office\tAssistant")]
    [InlineData("Office\0Assistant")]
    [InlineData("Office\u202eAssistant")]
    [InlineData("Office\u2028Assistant")]
    public void Invalid_titles_fail_without_including_untrusted_input_in_the_error(string? title)
    {
        var error = Assert.Throws<ArgumentException>(() => RecruitmentSimpleJobDescription.Generate(title!));
        Assert.Equal("positionTitle", error.ParamName);
        Assert.Contains("Enter a position title", error.Message);
    }

    [Fact]
    public void Invalid_utf16_is_rejected_before_unicode_normalization()
    {
        var invalidTitle = "Office" + new string((char)0xd800, 1) + "Assistant";
        Assert.Throws<ArgumentException>(() => RecruitmentSimpleJobDescription.Generate(invalidTitle));
    }

    [Fact]
    public void Length_limit_counts_unicode_characters_after_trimming()
    {
        var boundary = string.Concat(Enumerable.Repeat("😀", RecruitmentSimpleJobDescription.MaximumTitleLength));
        Assert.Equal(boundary, RecruitmentSimpleJobDescription.Generate("  " + boundary + "  ").Title);
        Assert.Throws<ArgumentException>(() => RecruitmentSimpleJobDescription.Generate(boundary + "x"));
    }

    [Fact]
    public void Identity_key_normalizes_unicode_case_and_spaces_without_discarding_role_punctuation()
    {
        Assert.Equal("HR EXECUTIVE", RecruitmentSimpleJobDescription.NormalizeTitleKey("  ＨＲ\u00a0\u00a0Executive  "));
        Assert.Equal("HR EXECUTIVE", RecruitmentSimpleJobDescription.NormalizeTitleKey("hr executive"));
        Assert.NotEqual(RecruitmentSimpleJobDescription.NormalizeTitleKey("C# Developer"), RecruitmentSimpleJobDescription.NormalizeTitleKey("C++ Developer"));
        Assert.NotEqual(RecruitmentSimpleJobDescription.NormalizeTitleKey("Lab/Research"), RecruitmentSimpleJobDescription.NormalizeTitleKey("Lab Research"));
        Assert.Equal("कार्यालय सहायक", RecruitmentSimpleJobDescription.Generate(" कार्यालय   सहायक ").Title);
    }

    [Theory]
    [InlineData("Talent Acquisition Executive", "people and recruitment", "recruitment")]
    [InlineData("Accounts Assistant", "financial records", "reconcile")]
    [InlineData("IT Support Engineer", "technology support", "troubleshooting")]
    [InlineData("Database Administrator", "technology support", "troubleshooting")]
    [InlineData("Software Developer", "software delivery", "software changes")]
    [InlineData("Staff Nurse", "patient-care", "authorised scope")]
    [InlineData("Lab Technician", "laboratory", "samples")]
    [InlineData("Research Assistant", "research", "data quality")]
    [InlineData("Data Entry Operator", "information and document", "duplicates")]
    [InlineData("Office Administrator", "office operations", "office activities")]
    [InlineData("Account Manager", "customer relationships", "customer")]
    public void Familiar_titles_receive_relevant_responsibilities(string title, string summaryTerm, string responsibilityTerm)
    {
        var content = RecruitmentSimpleJobDescription.Generate(title);
        Assert.Equal(title, content.Title);
        Assert.Contains(summaryTerm, content.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(content.Responsibilities, duty => duty.Contains(responsibilityTerm, StringComparison.OrdinalIgnoreCase));
        Assert.InRange(content.Responsibilities.Count, 4, 6);
        Assert.InRange(content.Skills.Count, 3, 5);
        Assert.All(content.Responsibilities.Concat(content.Skills), value => Assert.False(string.IsNullOrWhiteSpace(value)));
    }

    [Fact]
    public void Unknown_title_gets_useful_generic_content_without_a_guessed_specialism()
    {
        var content = RecruitmentSimpleJobDescription.Generate("Community Liaison");
        Assert.Contains("Community Liaison", content.Summary);
        Assert.Contains("assigned operational activities", content.Summary);
        Assert.Contains(content.Responsibilities, value => value.Contains("accuracy"));
        Assert.DoesNotContain(content.Responsibilities, value => value.Contains("patient", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(content.Skills, value => value.Contains("accounting", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("HR Manager")]
    [InlineData("Accounts Executive")]
    [InlineData("Senior Software Developer")]
    [InlineData("Staff Nurse")]
    [InlineData("Research Scientist")]
    [InlineData("Community Liaison")]
    public void Title_only_content_does_not_invent_employment_or_credential_requirements(string title)
    {
        var content = RecruitmentSimpleJobDescription.Generate(title);
        var narrative = string.Join(" ", new[] { content.Summary, content.RolePurpose }.Concat(content.Responsibilities).Concat(content.Skills));
        foreach (var unsupported in new[] { "bachelor", "master's", "phd", "degree", "years of experience", "certification", "licensed", "salary", "ctc", "INR", "must-have", "minimum qualification" })
            Assert.DoesNotContain(unsupported, narrative, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Repeated_generation_is_deterministic_and_does_not_share_mutable_list_state()
    {
        var first = RecruitmentSimpleJobDescription.Generate("Research Assistant");
        var serialized = JsonSerializer.Serialize(first);
        Assert.Equal(serialized, JsonSerializer.Serialize(RecruitmentSimpleJobDescription.Generate("Research Assistant")));
        first.Responsibilities.Clear();
        first.Skills.Add("Caller-only change");
        Assert.Equal(serialized, JsonSerializer.Serialize(RecruitmentSimpleJobDescription.Generate("Research Assistant")));
    }
}
