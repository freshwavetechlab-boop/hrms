using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public class EssInitialPasswordPolicyTests
{
    [Theory]
    [InlineData("234567890123", "23456789")]
    [InlineData(" 2345 6789 0123 ", "23456789")]
    [InlineData("2345-6789-0123", "23456789")]
    public void Complete_source_identifier_produces_exactly_its_first_eight_digits(string source, string expected)
        => Assert.Equal(expected, AuthRepository.AadhaarFirstEightTemporaryPassword(source));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("23456789")]
    [InlineData("XXXXXXXX0123")]
    [InlineData("2.34567890123E11")]
    [InlineData("2345/6789/0123")]
    [InlineData("२३४५६७८९०१२३")]
    [InlineData("2345678901234")]
    public void Missing_masked_malformed_or_incomplete_identifier_never_becomes_a_password(string? source)
    {
        var error = Assert.Throws<InvalidOperationException>(() => AuthRepository.AadhaarFirstEightTemporaryPassword(source));
        Assert.Contains("12-digit Aadhaar", error.Message);
        if (!string.IsNullOrEmpty(source)) Assert.DoesNotContain(source, error.Message);
    }

    [Theory]
    [InlineData("AadhaarFirst8", true)]
    [InlineData(" aadhaarfirst8 ", true)]
    [InlineData("Aadhaar", false)]
    [InlineData("EmployeeCode", false)]
    [InlineData("Random", false)]
    [InlineData("Fixed", false)]
    [InlineData("App Default", false)]
    [InlineData(null, false)]
    public void Existing_password_modes_are_distinct_from_the_new_opt_in_mode(string? mode, bool expected)
        => Assert.Equal(expected, AuthRepository.IsAadhaarFirstEightPasswordMode(mode));
}
