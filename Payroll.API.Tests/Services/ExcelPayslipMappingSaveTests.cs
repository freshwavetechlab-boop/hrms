using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipMappingSaveTests
{
    private static ExcelPayslipBatch Batch()
    {
        var columns = new List<ExcelPayslipCalculationColumn>
        {
            new() { ColumnIndex = 0, SourceHeader = "Employee Name", Label = "Name", Kind = "employeeName" },
            new() { ColumnIndex = 1, SourceHeader = "Branch", Label = "Branch", Kind = "ignore" },
            new() { ColumnIndex = 2, SourceHeader = "CTC", Label = "CTC", Kind = "info" },
            new() { ColumnIndex = 3, SourceHeader = "Net Pay", Label = "In Hand Salary", Kind = "netPay" }
        };
        return new()
        {
            SalaryTemplateId = "template-a",
            CalculationSource = new() { Columns = columns },
            MappingProfile = JsonSerializer.SerializeToElement(new { headerSignature = new string('a', 64), columns, salaryTemplateId = "template-a" }, ExcelPayslipRepository.JsonOptions)
        };
    }

    [Fact]
    public void OneSaveAcceptsTheExactReviewedMappingWithoutChangingIt()
    {
        var batch = Batch();
        var before = JsonSerializer.Serialize(batch, ExcelPayslipRepository.JsonOptions);
        Assert.Null(ExcelPayslipRepository.ValidateBatchMappingProfile(batch));
        Assert.Equal(before, JsonSerializer.Serialize(batch, ExcelPayslipRepository.JsonOptions));
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("label")]
    [InlineData("header")]
    [InlineData("component")]
    [InlineData("blankPolicy")]
    [InlineData("missingColumn")]
    public void AStalePresetCannotBeSavedBesideDifferentBatchColumns(string difference)
    {
        var batch = Batch();
        var column = batch.CalculationSource!.Columns[3];
        switch (difference)
        {
            case "kind": column.Kind = "info"; break;
            case "label": column.Label = "Old label"; break;
            case "header": column.SourceHeader = "Another heading"; break;
            case "component": column.ComponentId = "another-component"; break;
            case "blankPolicy": column.BlankAsZero = true; break;
            case "missingColumn": batch.CalculationSource.Columns.RemoveAt(1); break;
        }
        Assert.Contains("mapping columns must match", ExcelPayslipRepository.ValidateBatchMappingProfile(batch));
    }

    [Fact]
    public void PresetAndBatchMustUseTheSameTemplateAndSource()
    {
        var batch = Batch();
        batch.SalaryTemplateId = "another-template";
        Assert.Contains("template must match", ExcelPayslipRepository.ValidateBatchMappingProfile(batch));
        batch.CalculationSource = null;
        Assert.Contains("calculation source", ExcelPayslipRepository.ValidateBatchMappingProfile(batch));
    }

    [Fact]
    public void LegacyCallersWithoutAPresetRetainTheirExistingSaveContract()
    {
        var batch = new ExcelPayslipBatch();
        Assert.Null(ExcelPayslipRepository.ValidateBatchMappingProfile(batch));
        Assert.DoesNotContain("mappingProfile", JsonSerializer.Serialize(batch, ExcelPayslipRepository.JsonOptions));
    }

    [Fact]
    public void MalformedPresetIsRejectedBeforeAnySave()
    {
        var batch = Batch();
        batch.MappingProfile = JsonSerializer.SerializeToElement(new { headerSignature = "not-a-signature", columns = new object[0] });
        Assert.Contains("headerSignature", ExcelPayslipRepository.ValidateBatchMappingProfile(batch));
    }

    [Fact]
    public void MalformedSourceIsReportedAsValidationRatherThanAnException()
    {
        var batch = Batch();
        batch.CalculationSource!.Columns = null!;
        Assert.Contains("calculation source", ExcelPayslipRepository.ValidateBatchMappingProfile(batch));
        batch.CalculationSource.Columns = [null!];
        Assert.Contains("calculation source", ExcelPayslipRepository.ValidateBatchMappingProfile(batch));
    }

    [Fact]
    public void LegacyNullComponentAndExplicitFalseBlankPolicyAreCompatible()
    {
        var batch = Batch();
        var json = batch.MappingProfile!.Value.GetRawText().Replace("\"kind\":\"netPay\"", "\"kind\":\"netPay\",\"componentId\":null,\"blankAsZero\":false");
        batch.MappingProfile = JsonDocument.Parse(json).RootElement.Clone();
        Assert.Null(ExcelPayslipRepository.ValidateBatchMappingProfile(batch));
    }
}
