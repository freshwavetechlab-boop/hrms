using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class NativeCurrencyRecaptureEndpointTests
{
    private static AuthUser Operator()=>new(){Id=7,IsActive=true,Roles=["super_admin"],Permissions=["settings.manage","employees.manage","payroll.run"]};
    [Fact]
    public void Prepared_routes_reuse_the_existing_active_global_operator_and_client_guards()
    {
        Assert.True(NativeCurrencyRecaptureEndpoints.CanAccess(Operator(),11));
        Assert.False(NativeCurrencyRecaptureEndpoints.CanAccess(null,11));
        var inactive=Operator();inactive.IsActive=false;Assert.False(NativeCurrencyRecaptureEndpoints.CanAccess(inactive,11));
        var scoped=Operator();scoped.ClientId=11;Assert.False(NativeCurrencyRecaptureEndpoints.CanAccess(scoped,11));
        var selected=Operator();selected.ClientAccessMode="Selected";selected.AssignedClientIds=[12];
        Assert.False(NativeCurrencyRecaptureEndpoints.CanAccess(selected,11));Assert.True(NativeCurrencyRecaptureEndpoints.CanAccess(selected,12));
        var password=Operator();password.MustChangePassword=true;Assert.False(NativeCurrencyRecaptureEndpoints.CanAccess(password,11));
        foreach(var permission in new[]{"settings.manage","employees.manage","payroll.run"})
        {var actor=Operator();actor.Permissions.Remove(permission);Assert.False(NativeCurrencyRecaptureEndpoints.CanAccess(actor,11));}
    }
    [Fact]
    public void Apply_contract_has_no_client_supplied_salary_attendance_scope_subset_or_raw_native_stage()
    {
        Assert.Equal(new[]{"ClientId","PayPeriod","ReviewToken","RequestId","Confirmed"},typeof(NativeCurrencyRecaptureRequest).GetProperties().Select(p=>p.Name));
    }
}
