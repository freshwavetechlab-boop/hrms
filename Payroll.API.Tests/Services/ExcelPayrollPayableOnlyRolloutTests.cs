using Microsoft.Extensions.Configuration;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollPayableOnlyRolloutTests
{
    private static IConfiguration Configuration(string? configuredValue) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ExcelPayrollSetupRepository.PayableOnlyRolloutSetting] = configuredValue
        }).Build();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("not-reviewed")]
    public void MissingFalseOrInvalidRolloutRemainsDisabledEvenWithReadySchema(string? configuredValue)
    {
        var configuration = Configuration(configuredValue);
        Assert.False(ExcelPayrollSetupRepository.PayableOnlyRolloutEnabled(configuration));
        Assert.False(ExcelPayrollSetupRepository.PayableOnlyRolloutAvailable(configuration, true));
        Assert.Contains("readers and workers", ExcelPayrollSetupRepository.PayableOnlyRolloutError(configuration, true));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    public void ExplicitRolloutStillRequiresBothNullableColumns(string configuredValue)
    {
        var configuration = Configuration(configuredValue);
        Assert.True(ExcelPayrollSetupRepository.PayableOnlyRolloutEnabled(configuration));
        Assert.False(ExcelPayrollSetupRepository.PayableOnlyRolloutAvailable(configuration, false));
        Assert.Contains("nullable-presence schema", ExcelPayrollSetupRepository.PayableOnlyRolloutError(configuration, false));
        Assert.True(ExcelPayrollSetupRepository.PayableOnlyRolloutAvailable(configuration, true));
        Assert.Null(ExcelPayrollSetupRepository.PayableOnlyRolloutError(configuration, true));
    }

    [Fact]
    public void NormalKnownPresenceRequestsDoNotOptIntoTheNewMode()
    {
        var request = new Payroll.API.Models.ExcelPayrollSetupRequest();
        Assert.False(request.ImportPayableOnlyAttendance);
        Assert.Null(ExcelPayrollSetupRepository.PayableOnlyRequestError(request));
    }
}
