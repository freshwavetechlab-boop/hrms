using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class EssProfileCoreFieldsTests
{
    [Theory]
    [InlineData("12345678901", "", "12-digit")]
    [InlineData("1234567890123", "", "12-digit")]
    [InlineData("1.23456789E11", "", "12-digit")]
    [InlineData("12345678901\n", "", "12-digit")]
    [InlineData("１２３４５６７８９０１２", "", "12-digit")]
    [InlineData("123456789012", "1.2345E9", "10-digit")]
    [InlineData("123456789012", "123456789", "10-digit")]
    public void Changed_statutory_identifiers_are_validated_as_digit_strings(string uan, string esic, string expected)
        => Assert.Contains(expected, EssMssRepository.NormalizeProfileCoreFields(new() { UanNumber = uan, EsicNumber = esic }, new(), new()));

    [Fact]
    public void Missing_and_unchanged_legacy_fields_do_not_block_other_profile_corrections()
    {
        var personal = new EmployeePersonalDetails { UanNumber = "Legacy pending", EsicNumber = "Not provided" };
        var employee = new Employee { Gender = "Legacy gender" };
        foreach (var request in new[] { new SaveEssProfileRequest(), new SaveEssProfileRequest { UanNumber = personal.UanNumber, EsicNumber = personal.EsicNumber, Gender = employee.Gender } })
        {
            Assert.Empty(EssMssRepository.NormalizeProfileCoreFields(request, employee, personal));
            Assert.Equal(personal.UanNumber, request.UanNumber); Assert.Equal(personal.EsicNumber, request.EsicNumber); Assert.Equal(employee.Gender, request.Gender);
        }
    }

    [Fact]
    public void Valid_identifiers_preserve_leading_zeros_and_optional_fields_can_remain_blank()
    {
        var request = new SaveEssProfileRequest { UanNumber = " 001234567890 ", EsicNumber = " 0012345678 ", Gender = " female " };
        Assert.Empty(EssMssRepository.NormalizeProfileCoreFields(request, new(), new()));
        Assert.Equal("001234567890", request.UanNumber); Assert.Equal("0012345678", request.EsicNumber); Assert.Equal("Female", request.Gender);
        Assert.Empty(EssMssRepository.NormalizeProfileCoreFields(new() { UanNumber = "", EsicNumber = "", Gender = "" }, new(), new()));
        Assert.Contains("Select", EssMssRepository.NormalizeProfileCoreFields(new() { Gender = "A new arbitrary value" }, new(), new()));
    }

    [Fact]
    public void Older_json_omits_new_fields_and_numeric_identifiers_are_rejected_by_the_contract()
    {
        var old = JsonSerializer.Deserialize<SaveEssProfileRequest>("{\"FirstName\":\"Synthetic\"}")!;
        Assert.Null(old.UanNumber); Assert.Null(old.EsicNumber); Assert.Null(old.Gender);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SaveEssProfileRequest>("{\"UanNumber\":123456789012}"));
    }

    [Fact]
    public void Omitted_or_unchanged_fields_preserve_exact_existing_values()
    {
        var personal = new EmployeePersonalDetails { UanNumber = " Legacy pending ", EsicNumber = " Not provided " };
        var employee = new Employee { Gender = "male" };
        foreach (var request in new[] { new SaveEssProfileRequest(), new SaveEssProfileRequest { UanNumber = personal.UanNumber, EsicNumber = personal.EsicNumber, Gender = employee.Gender } })
        {
            Assert.Empty(EssMssRepository.NormalizeProfileCoreFields(request, employee, personal));
            Assert.Equal(personal.UanNumber, request.UanNumber); Assert.Equal(personal.EsicNumber, request.EsicNumber); Assert.Equal(employee.Gender, request.Gender);
        }
    }
}
