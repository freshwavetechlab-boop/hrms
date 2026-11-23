using System.Text;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class RecruitmentJobDescriptionQualificationTests
{
    [Fact]
    public void Parsed_qualification_longer_than_the_legacy_180_character_column_is_preserved_and_valid()
    {
        var text = "Graduate degree in life sciences with the required research experience and practical laboratory training. "
            + "Eligibility includes the equivalent postgraduate qualification and relevant project work as specified by the institute.";
        Assert.True(text.Length > 180);
        var request = Request(text);
        Assert.Equal("", RecruitmentPipelineRepository.ValidateJobDescriptionQualifications(request));
        Assert.Equal(text, request.Qualifications[0].QualificationName);
    }

    [Theory]
    [InlineData("a", 65535, 65536)]
    [InlineData("अ", 21845, 21846)]
    public void Text_capacity_is_measured_in_utf8_bytes_including_multibyte_qualification_wording(string character, int validCount, int invalidCount)
    {
        var exact = string.Concat(Enumerable.Repeat(character, validCount));
        Assert.Equal(65535, Encoding.UTF8.GetByteCount(exact));
        Assert.Equal("", RecruitmentPipelineRepository.ValidateJobDescriptionQualifications(Request(exact)));
        var excessive = string.Concat(Enumerable.Repeat(character, invalidCount));
        var error = RecruitmentPipelineRepository.ValidateJobDescriptionQualifications(Request(excessive));
        Assert.Contains("Qualification row 1", error);
        Assert.Contains("too long for one entry", error);
    }

    [Fact]
    public void Wider_existing_text_storage_is_never_narrowed()
    {
        foreach (var type in new[] { "text", "MEDIUMTEXT", "longtext" })
            Assert.True(RecruitmentPipelineRepository.QualificationColumnIsWideEnough(type));
        foreach (var type in new[] { "varchar", "tinytext" })
            Assert.False(RecruitmentPipelineRepository.QualificationColumnIsWideEnough(type));
    }

    [Fact]
    public void Oversized_specialization_has_a_field_specific_validation_message_without_truncation()
    {
        var request = Request("Life Sciences");
        request.Qualifications[0].Specialization = new string('x', 181);
        Assert.Contains("specialization must be 180 characters or fewer", RecruitmentPipelineRepository.ValidateJobDescriptionQualifications(request));
        Assert.Equal(181, request.Qualifications[0].Specialization.Length);
    }

    private static SaveRecruitmentJobDescriptionVersion Request(string text) => new()
    {
        Qualifications = [new() { QualificationName = text }]
    };
}
