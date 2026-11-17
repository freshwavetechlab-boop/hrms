using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class PayrollReferenceInformationProtectionTests
{
    private static PortableIntegrationCredentialProtector Protector(string key)=>new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string,string?>{["IntegrationCredentialEncryption:MasterKey"]=key}).Build(),new EphemeralDataProtectionProvider(),NullLogger<PortableIntegrationCredentialProtector>.Instance);
    [Fact]
    public void Reference_metadata_has_separate_portable_purpose_and_never_accepts_historical_or_currency_envelopes()
    {
        var protector=Protector("synthetic-reference-repository-protector");const string value="synthetic nonfinancial metadata";
        var encrypted=protector.ProtectPayrollReferenceInformation(value);Assert.Equal(value,protector.UnprotectPayrollReferenceInformation(encrypted));
        Assert.Null(protector.UnprotectHistoricalSalary(encrypted));Assert.Null(protector.UnprotectNativeCurrencyStage(encrypted));
        Assert.Null(protector.UnprotectPayrollReferenceInformation(protector.ProtectHistoricalSalary(value)));
        Assert.Null(protector.UnprotectPayrollReferenceInformation(protector.ProtectNativeCurrencyStage(value)));
        Assert.Null(Protector("another-synthetic-key").UnprotectPayrollReferenceInformation(encrypted));
        Assert.Null(protector.UnprotectPayrollReferenceInformation(encrypted[..^4]+"bad!"));
    }
    [Fact]
    public async Task Reference_writer_is_absent_by_default_and_fails_before_opening_a_connection()
    {
        var cfg=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:Default"]="not-a-database-connection"}).Build();
        var repo=new PayslipDesignRepository(cfg,Protector("synthetic-default-gate"),new());
        var actor=new AuthUser{Id=97790,IsActive=true,Roles=["super_admin"],ClientAccessMode="All"};
        var request=new PayrollReferenceInformationReviewRequest(97790,1,"2026-09",1,new string('1',32),new string('2',32),3);
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.PreviewReferenceInformationAsync(request,actor));
        Assert.Contains("not enabled",error.Message);
    }
}
