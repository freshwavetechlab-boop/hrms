using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public class EssBulkProvisionPasswordTests
{
    [Theory]
    [InlineData(null, "ClientPolicy")]
    [InlineData("", "ClientPolicy")]
    [InlineData(" clientpolicy ", "ClientPolicy")]
    [InlineData(" shared ", "Shared")]
    [InlineData("EMPLOYEECODE", "EmployeeCode")]
    [InlineData("Email", "Email")]
    [InlineData("mobile", "Mobile")]
    public void Password_pattern_is_explicit_and_old_payload_keeps_client_policy(string? input, string expected)
        => Assert.Equal(expected, AuthRepository.NormalizeProvisionPasswordMode(input));

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Aadhaar")]
    [InlineData("employee-code-or-email")]
    public void Unsupported_pattern_is_never_silently_used_as_a_password(string input)
        => Assert.Throws<InvalidOperationException>(() => AuthRepository.NormalizeProvisionPasswordMode(input));

    [Theory]
    [InlineData("EmployeeCode", " SYN-23 ", null, null, "SYN-23")]
    [InlineData("Email", "SYN-23", " person@example.test ", null, "person@example.test")]
    [InlineData("Mobile", "SYN-23", "person@example.test", " +91 (90000) 00107 ", "+91 (90000) 00107")]
    [InlineData("Email", "SYN-23", " not-an-email ", null, "not-an-email")]
    [InlineData("Mobile", "SYN-23", "person@example.test", " office-phone ", "office-phone")]
    public void Password_pattern_uses_chosen_master_text_without_imposing_login_or_profile_validation(string mode, string code, string? email, string? mobile, string expected)
        => Assert.Equal(expected, AuthRepository.EmployeeProvisionPassword(mode, code, email, mobile));

    [Theory]
    [InlineData("EmployeeCode", " ", "person@example.test", "9000000107")]
    [InlineData("Email", "SYN-23", "", "9000000107")]
    [InlineData("Mobile", "SYN-23", "person@example.test", "")]
    public void Missing_chosen_field_is_returned_as_missing_instead_of_selecting_another_master_field(string mode, string code, string email, string mobile)
        => Assert.Empty(AuthRepository.EmployeeProvisionPassword(mode, code, email, mobile));
}
