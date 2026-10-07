using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public class EmployeeFieldExchangeTests
{
    [Fact]
    public void RenamingAFieldKeepsItsExcelIdentity()
    {
        var form = new EmployeeAttributeForm { InfotypeCode = "0002", FormCode = "PERSONAL_EXTRA" };
        var field = new DynamicFormField { StableFieldCode = "CASTE", Label = "Caste" };
        var key = EmployeeAttributeRepository.FieldKey(form, field);
        field.Label = "Community";
        Assert.Equal(key, EmployeeAttributeRepository.FieldKey(form, field));
        Assert.Equal(key, EmployeeAttributeRepository.HeaderKey($"Old label [{key}]"));
        Assert.Equal("", EmployeeAttributeRepository.HeaderKey("Caste"));
    }

    [Theory]
    [InlineData("NUMBER", "25000.125", true)]
    [InlineData("NUMBER", "not a number", false)]
    [InlineData("DATE", "2026-09-01", true)]
    [InlineData("DATE", "not a date", false)]
    [InlineData("DATE", "09/01/2026", false)]
    [InlineData("DATETIME", "2026-09-01T09:30", true)]
    [InlineData("CHECKBOX", "FALSE", true)]
    [InlineData("CHECKBOX", "maybe", false)]
    public void TypedSpreadsheetValuesRejectInvalidData(string type, string cell, bool valid)
    {
        var field = new DynamicFormField { Id = 10, FieldTypeCode = type, Label = "Additional field" };
        if (!valid) { Assert.Throws<InvalidOperationException>(() => EmployeeAttributeRepository.ParseCell(field, cell)); return; }
        var value = EmployeeAttributeRepository.ParseCell(field, cell);
        Assert.Equal(10, value.FieldId);
        if (type == "NUMBER") Assert.Equal(25000.125m, value.DecimalValue);
        if (type == "CHECKBOX") Assert.False(value.BooleanValue);
        if (type == "DATE") Assert.Equal(new DateTime(2026, 9, 1), value.DateValue);
    }

    [Fact]
    public void DropdownUsesStableActiveOptionsAndRejectsUnknownValues()
    {
        var field = new DynamicFormField { Id = 12, FieldTypeCode = "SEARCH_SELECT", Label = "Community", Options = [
            new() { Id = 1, OptionCode = "GEN", OptionLabel = "Renamed general", IsActive = true },
            new() { Id = 2, OptionCode = "OLD", OptionLabel = "Old", IsActive = false } ] };
        Assert.Equal(new long[] { 1 }, EmployeeAttributeRepository.ParseCell(field, "GEN").SelectedOptionIds);
        Assert.Equal(new long[] { 1 }, EmployeeAttributeRepository.ParseCell(field, "Renamed general").SelectedOptionIds);
        Assert.Throws<InvalidOperationException>(() => EmployeeAttributeRepository.ParseCell(field, "OLD"));
        Assert.Throws<InvalidOperationException>(() => EmployeeAttributeRepository.ParseCell(field, "unknown"));
    }

    [Fact]
    public void CoreFieldsAndPublishedTypesCannotBeReplacedByCustomFields()
    {
        Assert.Contains("core", EmployeeAttributeRepository.ConfigurationError([new() { StableFieldCode = "FIRST_NAME", Label = "New name" }]));
        Assert.Contains("core", EmployeeAttributeRepository.ConfigurationError([new() { StableFieldCode = "OTHER", Label = "Annual CTC" }]));
        Assert.Contains("type", EmployeeAttributeRepository.ConfigurationError([new() { StableFieldCode = "CASTE", Label = "Caste", FieldTypeCode = "NUMBER" }], [new() { StableFieldCode = "CASTE", FieldTypeCode = "TEXT" }]));
        Assert.Empty(EmployeeAttributeRepository.ConfigurationError([new() { StableFieldCode = "CASTE", Label = "Community", FieldTypeCode = "TEXT" }], [new() { StableFieldCode = "CASTE", Label = "Caste", FieldTypeCode = "TEXT" }]));
        Assert.Contains("stable", EmployeeAttributeRepository.ConfigurationError([new() { Id = 15, StableFieldCode = "NEW_CODE", Label = "Community", FieldTypeCode = "TEXT" }], [new() { Id = 15, StableFieldCode = "CASTE", FieldTypeCode = "TEXT" }]));
        Assert.Contains("type", EmployeeAttributeRepository.ConfigurationError([new() { StableFieldCode = "CASTE", Label = "Community", FieldTypeCode = "NUMBER" }], [new() { Id = 15, StableFieldCode = "CASTE", FieldTypeCode = "TEXT" }, new() { Id = 16, StableFieldCode = "CASTE", FieldTypeCode = "TEXT" }]));
    }

    [Theory]
    [InlineData("TEXT", "EMAIL", true)]
    [InlineData("TEXT", "NUMBER", false)]
    [InlineData("DATE", "TEXT", false)]
    [InlineData("RADIO", "SEARCH_SELECT", true)]
    [InlineData("MULTI_SELECT", "SEARCH_SELECT", false)]
    public void CandidateTransferDoesNotGuessIncompatibleTypes(string source, string target, bool compatible) =>
        Assert.Equal(compatible, EmployeeAttributeRepository.CompatibleTransferTypes(source, target));

    [Fact]
    public void LinkingPreservesEmployeeIdentityPayAndFilledValues()
    {
        var existing = new Employee { Id = 15, ClientId = 20, EmployeeCode = "E015", FirstName = "Existing", WorkEmail = "old@example.test", AnnualCtc = 900000, PortalAccess = false, PersonalDetails = new() { Mobile = "9000000000", AadhaarNumber = "existing-id" }, SalaryComponents = new() { ["basic"] = 10000 } };
        var incoming = new Employee { EmployeeCode = "NEW", FirstName = "Candidate", LastName = "Surname", WorkEmail = "candidate@example.test", Department = "Technology", AnnualCtc = 1200000, PortalAccess = true, PersonalDetails = new() { Mobile = "9111111111" } };
        var merged = RecruitmentTalentRepository.FillEmployeeBlanks(existing, incoming);
        Assert.Equal(15, merged.Id); Assert.Equal("E015", merged.EmployeeCode); Assert.Equal(20, merged.ClientId);
        Assert.Equal("Existing", merged.FirstName); Assert.Equal("Surname", merged.LastName); Assert.Equal("Technology", merged.Department);
        Assert.Equal("old@example.test", merged.WorkEmail); Assert.Equal("9000000000", merged.PersonalDetails.Mobile);
        Assert.Equal("existing-id", merged.PersonalDetails.AadhaarNumber); Assert.Equal(900000, merged.AnnualCtc);
        Assert.False(merged.PortalAccess); Assert.Equal(10000, merged.SalaryComponents["basic"]);
    }

    [Fact]
    public void SalaryExportRoundTripKeepsAmountsAndRejectsBadJson()
    {
        Assert.True(EmployeeRepository.TrySalaryMap("{\"basic\":12345.67,\"hra\":4500}", out var salary));
        Assert.Equal(12345.67m, salary["basic"]);
        Assert.False(EmployeeRepository.TrySalaryMap("{\"basic\":\"invalid\"}", out _));
        Assert.False(EmployeeRepository.TrySalaryMap("[]", out _));
    }

    [Fact]
    public async Task ConfigurationReadRejectsWrongClientOrPermissionBeforeOpeningDatabase()
    {
        var repository = new EmployeeAttributeRepository(new ConfigurationBuilder().Build());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.ExchangeAsync(21, new() { ClientId = 20, Permissions = ["employees.view"] }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.ExchangeAsync(20, new() { ClientId = 20, EmployeeId = 15, Permissions = [] }));
    }
}
