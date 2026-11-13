using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class EmployeePhoneLoginTests
{
    [Theory]
    [InlineData("9876543210")]
    [InlineData(" 98765 43210 ")]
    [InlineData("+91 98765-43210")]
    [InlineData("0091 (98765) 43210")]
    [InlineData("91-98765-43210")]
    public void Indian_mobile_formats_normalize_to_the_same_alias(string value) =>
        Assert.Equal("9876543210", AuthRepository.NormalizeEmployeeLoginPhone(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1234567890")]
    [InlineData("09876543210")]
    [InlineData("+1 9876543210")]
    [InlineData("+44 9876543210")]
    [InlineData("+9876543210")]
    [InlineData("009198765432100")]
    [InlineData("91987654321099")]
    [InlineData("9876543210 ext 2")]
    [InlineData("9876543210/8765432109")]
    [InlineData("9.876543210E9")]
    [InlineData("９８７６５４３２１０")]
    [InlineData("98765XX210")]
    [InlineData("91+9876543210")]
    [InlineData("++919876543210")]
    public void Incomplete_ambiguous_or_non_mobile_values_are_not_phone_aliases(string? value) =>
        Assert.Empty(AuthRepository.NormalizeEmployeeLoginPhone(value));

    [Theory]
    [InlineData("ESS", true)]
    [InlineData("ess", true)]
    [InlineData("Employee", true)]
    [InlineData("Admin", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Phone_aliases_are_limited_to_explicit_employee_portals(string? portal, bool expected) =>
        Assert.Equal(expected, AuthRepository.IsEmployeePhoneLoginPortal(portal));
}
