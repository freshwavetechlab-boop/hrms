using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class NativePayslipDesignScopeTests
{
    [Theory]
    [InlineData("2026-09")][InlineData("0001-01")][InlineData("9999-12")]
    public void Exact_bounded_payroll_months_are_supported(string month) => NativePayslipDesignScope.ValidatePeriods([month]);

    [Theory]
    [InlineData("")][InlineData("2026-9")][InlineData("2026-00")][InlineData("2026-13")][InlineData("0000-01")]
    [InlineData("2026-09 ")][InlineData(" 2026-09")][InlineData("2026/09")][InlineData("2026-09-01")]
    public void Malformed_or_ambiguous_months_are_rejected(string month) => Assert.Throws<InvalidOperationException>(() => NativePayslipDesignScope.ValidatePeriods([month]));

    [Fact]
    public void Empty_duplicate_and_over_bound_lists_reject()
    {
        Assert.Throws<InvalidOperationException>(() => NativePayslipDesignScope.ValidatePeriods([]));
        Assert.Throws<InvalidOperationException>(() => NativePayslipDesignScope.ValidatePeriods(["2026-09", "2026-09"]));
        Assert.Throws<InvalidOperationException>(() => NativePayslipDesignScope.ValidatePeriods(Enumerable.Range(0, 121).Select(i => new DateOnly(2000, 1, 1).AddMonths(i).ToString("yyyy-MM")).ToArray()));
    }

    [Fact]
    public void Legacy_absent_null_scope_remains_all_months_without_changing_legacy_serialized_profile()
    {
        var profile = JsonSerializer.Deserialize<PayslipDesignProfile>("{\"version\":1}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(profile.PayPeriods); Assert.True(NativePayslipDesignScope.Applies(profile, "2026-06"));
        Assert.True(NativePayslipDesignScope.Applies(profile, null));
        var serialized = JsonSerializer.Serialize(profile, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("payPeriods", serialized); Assert.DoesNotContain("previousClassicTemplate", serialized);
    }

    [Fact]
    public void Omitted_scope_preserves_selected_months_and_only_explicit_all_widens()
    {
        var old = new PayslipDesignProfile { PayPeriods = ["2026-09"] };
        Assert.Equal(old.PayPeriods, NativePayslipDesignScope.Resolve(new(), old));
        Assert.Equal(old.PayPeriods, NativePayslipDesignScope.Resolve(new() { PayPeriods = null }, old));
        Assert.Null(NativePayslipDesignScope.Resolve(new() { AllPayPeriods = true }, old));
        Assert.Equal(new[] { "2026-09", "2026-10" }, NativePayslipDesignScope.Resolve(new() { PayPeriods = ["2026-10", "2026-09"] }, old));
        Assert.Throws<InvalidOperationException>(() => NativePayslipDesignScope.Resolve(new() { PayPeriods = ["2026-09"], AllPayPeriods = true }, old));
    }

    [Fact]
    public void Scoped_profile_requires_same_client_same_template_authenticatable_Classic_settings()
    {
        var profile = new PayslipDesignProfile { ClientId = 11, TemplateId = 55, PayPeriods = ["2026-09"] };
        Assert.Throws<InvalidOperationException>(() => NativePayslipDesignScope.ValidateFallback(profile));
        profile.PreviousClassicTemplate = new() { ClientId = 11, Id = 55, Theme = "Classic" };
        NativePayslipDesignScope.ValidateFallback(profile);
        Assert.True(NativePayslipDesignScope.Applies(profile, "2026-09")); Assert.False(NativePayslipDesignScope.Applies(profile, "2026-06"));
        Assert.Throws<InvalidOperationException>(() => NativePayslipDesignScope.Applies(profile, null));
        profile.PreviousClassicTemplate.ClientId = 12;
        Assert.Throws<InvalidOperationException>(() => NativePayslipDesignScope.ValidateFallback(profile));
    }

    [Fact]
    public void Save_DTO_never_accepts_a_client_fallback_and_legacy_body_omits_new_defaults()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = JsonSerializer.Deserialize<PayslipDesignSaveRequest>("{\"previousClassicTemplate\":{\"theme\":\"Forged\",\"note\":\"forged\"}}", json)!;
        var serialized = JsonSerializer.Serialize(request, json);
        Assert.DoesNotContain("forged", serialized); Assert.DoesNotContain("previousClassicTemplate", serialized);
        Assert.DoesNotContain("payPeriods", serialized); Assert.DoesNotContain("allPayPeriods", serialized);
    }

    [Fact]
    public void New_catalog_advertises_scope_support_without_advertising_or_enabling_the_write_gate()
    {
        var preview=new PayslipDesignPreview(11,"source","hash",false,[],[],[]);
        var value=JsonSerializer.SerializeToNode(preview,new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(value["supportsPayrollMonthScope"]!.GetValue<bool>());
        Assert.Null(value["scopedWritesEnabled"]);Assert.Null(value["allPayPeriods"]);
    }

    [Fact]
    public void Normal_DI_supplies_the_existing_protector_and_one_argument_constructor_remains_available()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["IntegrationCredentialEncryption:MasterKey"] = "synthetic-scope-test" }).Build();
        var services = new ServiceCollection(); services.AddSingleton<IConfiguration>(config);
        var protector=new PortableIntegrationCredentialProtector(config, new EphemeralDataProtectionProvider(), NullLogger<PortableIntegrationCredentialProtector>.Instance);
        services.AddSingleton(protector);
        services.AddSingleton<EssMssRepository>(); using var provider = services.BuildServiceProvider();
        var resolved=provider.GetRequiredService<EssMssRepository>();Assert.NotNull(resolved);
        Assert.Same(protector,typeof(EssMssRepository).GetField("payslipDesignProtector",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(resolved));
        Assert.NotNull(typeof(EssMssRepository).GetConstructor([typeof(IConfiguration)]));
        Assert.NotNull(new EssMssRepository(config));
    }
}
