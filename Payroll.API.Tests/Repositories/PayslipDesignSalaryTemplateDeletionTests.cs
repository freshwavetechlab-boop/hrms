using Payroll.API.Models;
using Payroll.API.Repositories;
using Xunit;

namespace Payroll.API.Tests.Repositories;

public sealed class PayslipDesignSalaryTemplateDeletionTests
{
    [Fact]
    public void Only_mapped_outputs_losing_their_last_source_block_removal()
    {
        PayslipDesignField[] mappings = [
            new() { Kind = "earning", NativeCode = "BASIC" },
            new() { Kind = "deduction", NativeCode = "RECOVERY", Visible = false },
            new() { Kind = "deduction", NativeCode = "STALE" }];
        PayslipDesignCode[] removed = [
            new("BASIC", "Basic salary", "Earning"), new("RECOVERY", "Recovery", "Deduction"),
            new("OTHER", "Other allowance", "Earning")];
        PayslipDesignCode[] retained = [
            new("BASIC", "Basic", "Earning"), new("RECOVERY", "Employer adjustment", "Benefit")];
        Assert.Equal("RECOVERY", Assert.Single(PayslipDesignRepository.LostMappedFields(mappings, removed, retained)).NativeCode);
        Assert.Empty(PayslipDesignRepository.LostMappedFields(mappings, removed, [..retained, new("RECOVERY", "Reviewed deduction", "Deduction")]));
    }

    [Fact]
    public void Monthly_information_tracks_the_stable_code_and_ignores_unrelated_info_tokens()
    {
        PayslipDesignField[] mappings = [
            new() { Kind = "info", InfoToken = "COMPONENT_MONTHLY:BONUS" },
            new() { Kind = "info", InfoToken = "DEPARTMENT" }];
        PayslipDesignCode[] removed = [new("BONUS", "Bonus", "Earning"), new("DEPARTMENT", "Department allowance", "Earning")];
        Assert.Equal("COMPONENT_MONTHLY:BONUS", Assert.Single(PayslipDesignRepository.LostMappedFields(mappings, removed, [])).InfoToken);
        Assert.Empty(PayslipDesignRepository.LostMappedFields(mappings, removed, [new("BONUS", "Bonus", "Earning")]));
    }
}
