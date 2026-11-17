using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public class PayrollSimulationLocationTests
{
    [Fact]
    public void Single_effective_location_keeps_its_native_identity()
    {
        var result = new PayrollSimulationResult();
        PayRunRepository.SetSimulationLocations(result, [new(12, "Mumbai", "period-template")]);
        Assert.Equal(12, result.WorkLocationId);
        Assert.Equal("Mumbai", result.WorkLocationName);
        Assert.Equal("period-template", Assert.Single(result.Locations).TemplateId);
    }

    [Fact]
    public void Reviewed_parts_do_not_become_an_invented_single_location()
    {
        var result = new PayrollSimulationResult();
        PayRunRepository.SetSimulationLocations(result, [new(12, "Mumbai", "template-m"), new(19, "Delhi", "template-d")]);
        Assert.Equal(0, result.WorkLocationId);
        Assert.Equal("", result.WorkLocationName);
        Assert.Equal(2, result.Locations.Count);
    }

    [Fact]
    public void Unavailable_name_is_not_filled_from_a_different_master_record()
    {
        var result = new PayrollSimulationResult();
        PayRunRepository.SetSimulationLocations(result, [new(12, "", "period-template")]);
        Assert.Equal(12, result.WorkLocationId);
        Assert.Equal("", result.WorkLocationName);
    }
}
