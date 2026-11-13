using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipUpdateFieldMappingTests
{
    [Theory]
    [InlineData("AADHAR NUMBER", "aadhaar")]
    [InlineData("Aadhaar number (V)", "aadhaar")]
    [InlineData("UAN Number", "uan")]
    [InlineData("Contact Number", "mobile")]
    [InlineData("Bank Account Number", "bankAccount")]
    [InlineData("IFSC Code", "ifsc")]
    [InlineData("PAN Number", "pan")]
    [InlineData("ESIC Number", "esic")]
    public void Identifier_information_headers_remain_explicit_Master_profile_fields(string label, string field) =>
        Assert.Equal(field, ExcelPayslipRepository.ReviewedUpdateMasterKey("info", label));

    [Theory]
    [InlineData("info", "Annual CTC", "annualCtc")]
    [InlineData("info", "Monthly CTC", null)]
    [InlineData("info", "CTC", null)]
    [InlineData("earning", "Annual CTC", null)]
    [InlineData("netPay", "Annual CTC", null)]
    [InlineData("deduction", "UAN Number", null)]
    public void Financial_roles_and_monthly_controls_cannot_become_an_inferred_Master_basis(string kind, string label, string? expected) =>
        Assert.Equal(expected, ExcelPayslipRepository.ReviewedUpdateMasterKey(kind, label));
}
