using System.Text.Json;
using System.Text.Json.Nodes;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Xunit;

namespace Payroll.API.Tests.Services;

public sealed class AppliedSourceMetadataCompatibilityTests
{
    private static ExcelPayrollSetupComponent Definition(string code, string category, string type, string formula,
        int priority, bool prorata = false) => new()
    {
        Code = code, Name = code, Category = category, ComponentRole = category == "Earning" ? "Regular Earning" : "Statutory Deduction",
        StatutoryType = "None", CalculationType = type, Formula = formula, Value = "0", Priority = priority,
        ProRata = prorata, Taxable = category == "Earning", PayType = "Fixed Pay"
    };
    private static JsonObject Master(string id, ExcelPayrollSetupComponent d, int priority, string? payType = null) =>
        JsonSerializer.SerializeToNode(new { id, code = d.Code, category = d.Category, componentRole = d.ComponentRole,
            statutoryType = d.StatutoryType, taxable = d.Taxable, priority, payType = payType ?? d.PayType,
            calculationType = d.CalculationType, active = true })!.AsObject();
    private static Dictionary<string,string> Review(ExcelPayrollSetupComponent[] definitions, JsonObject[] masters,
        string? native = "[]") => ExcelPayrollSetupRepository.ReviewAppliedComponentMetadata(masters,
            definitions.Select((d, i) => new ExcelPayrollSetupRepository.ResolvedComponent(d, (i + 1).ToString(), false)).ToArray(), native);

    [Fact]
    public void Explicit_dependencies_can_commute_but_source_and_current_metadata_are_immutable()
    {
        var wages = Definition("WAGES", "Earning", "Fixed Amount", "", 10, true);
        var pf = Definition("PF", "Deduction", "Formula", "WAGES_EARNED*12%", 20);
        var zero = Definition("ESIC", "Deduction", "Formula", "0", 30);
        var definitions = new[] { wages, pf, zero };
        var masters = new[] { Master("1", wages, 99), Master("2", pf, 1), Master("3", zero, 2, "Variable Pay") };
        var before = JsonSerializer.Serialize(new { definitions, masters });
        var result = Review(definitions, masters);
        Assert.Equal(3, result.Count);
        Assert.Contains("no running-total dependency", result["PF"]);
        Assert.Contains("non-earning pay type", result["ESIC"]);
        Assert.Equal(before, JsonSerializer.Serialize(new { definitions, masters }));
    }

    [Theory]
    [InlineData("EARNINGS_BEFORE_THIS")]
    [InlineData("FIXED_EARNINGS_BEFORE_THIS")]
    [InlineData("FIXED_EARNINGS")]
    [InlineData("GROSS_EARNED")]
    [InlineData("EARNED_EARNINGS_BEFORE_THIS")]
    [InlineData("MONTHLY_EARNINGS_BEFORE_THIS")]
    [InlineData("SUM(WAGES,OTHER)")]
    public void A_running_total_or_sum_still_requires_exact_metadata(string formula)
    {
        var d = Definition("PF", "Deduction", "Formula", formula, 10);
        Assert.Throws<SelectedPayRunConflictException>(() => Review([d], [Master("1", d, 20)]));
    }

    [Theory]
    [InlineData("Residual / Balancing")]
    [InlineData("Slab Based")]
    [InlineData("Daily Rate")]
    public void Unproven_calculation_types_are_not_allowed_to_change_order(string type)
    {
        var d = Definition("WAGES", "Earning", type, "100", 10);
        Assert.Throws<SelectedPayRunConflictException>(() => Review([d], [Master("1", d, 20)]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("[{\"pfPolicy\":{\"contribution\":120}}]")]
    public void Priority_change_requires_actual_native_evidence_without_implicit_dated_pf_dependencies(string? details)
    {
        var d = Definition("WAGES", "Earning", "Fixed Amount", "", 10);
        Assert.Throws<SelectedPayRunConflictException>(() => Review([d], [Master("1", d, 20)], details));
    }

    [Theory]
    [InlineData("Earning")]
    [InlineData("Reimbursement")]
    public void Relevant_earning_pay_type_changes_still_block(string category)
    {
        var d = Definition("ALLOW", category, "Fixed Amount", "", 10);
        Assert.Throws<SelectedPayRunConflictException>(() => Review([d], [Master("1", d, 10, "Variable Pay")]));
    }

    [Theory]
    [InlineData("category", "Benefit")]
    [InlineData("componentRole", "Employer Contribution")]
    [InlineData("statutoryType", "ESI Employer")]
    [InlineData("taxable", "true")]
    [InlineData("priority", "unknown")]
    [InlineData("payType", "unknown")]
    public void Classification_tax_and_malformed_metadata_remain_blocked(string field, string value)
    {
        var d = Definition("PF", "Deduction", "Formula", "0", 10);
        var master = Master("1", d, 20); master[field] = value;
        Assert.Throws<SelectedPayRunConflictException>(() => Review([d], [master]));
    }

    [Theory]
    [InlineData(30, 0)]
    [InlineData(30, 27)]
    [InlineData(30, 30)]
    [InlineData(31, 27)]
    [InlineData(31, 31)]
    public void Actual_engine_preserves_amounts_when_explicit_dependencies_are_reordered(int days, int payable)
    {
        PayrollSimulationResult Calculate(int wagesPriority, int pfPriority, string deductionPayType) => PayRunRepository.SimulateTemplate(new()
        {
            Mode = "Template", ClientId = 7, TemplateId = "500", AnnualCtc = 12000,
            PayrollDays = days, PresentDays = payable, PayableDays = payable,
            Snapshot = new("Template", 7, "", 12000,
            [
                new("1", "WAGES", "Earning", "Regular Earning", "None", "Formula", "0", "999", "", false, true, true, wagesPriority, "Fixed Pay"),
                new("2", "PF", "Deduction", "Statutory Deduction", "None", "Formula", "0", "WAGES_EARNED*12%", "", true, false, true, pfPriority, deductionPayType)
            ],
            [new("1", "1000", "", "Fixed Amount", "", "true"), new("2", "0", "WAGES_EARNED*12%", "Formula", "", "false")])
        });
        var source = Calculate(10, 20, "Fixed Pay"); var current = Calculate(99, 1, "Variable Pay");
        Assert.True(source.Complete); Assert.True(current.Complete);
        Assert.Equal(source.Gross, current.Gross); Assert.Equal(source.Deductions, current.Deductions); Assert.Equal(source.Net, current.Net);
        foreach (var line in source.Lines) Assert.Equal(line.Earned, Assert.Single(current.Lines, x => x.Code == line.Code).Earned);
        Assert.Equal(decimal.Round(1000m * payable / days, 2), current.Gross);
        Assert.Equal(decimal.Round(current.Gross!.Value * .12m, 2), current.Deductions);
    }
}
