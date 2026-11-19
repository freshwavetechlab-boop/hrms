using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollHelperMappingTests
{
    [Fact]
    public void Helper_proration_lesson_is_retrieved_from_structure_without_private_client_or_employee_data()
    {
        var request=new ExcelPayslipMappingRequest {Columns=[
            new(){ColumnIndex=0,SourceHeader="Monthly Rate",Kind="info",HasNumbers=true},
            new(){ColumnIndex=1,SourceHeader="Total Working Days",Kind="info",HasNumbers=true},
            new(){ColumnIndex=2,SourceHeader="Actual Working Days",Kind="info",HasNumbers=true},
            new(){ColumnIndex=3,SourceHeader="Per Days Rate",Kind="info",HasNumbers=true,HasFormula=true,FormulaDependencies=[0,1]},
            new(){ColumnIndex=4,SourceHeader="Basic Salary",Kind="earning",HasNumbers=true,HasFormula=true,FormulaDependencies=[3,2]},
            new(){ColumnIndex=5,SourceHeader="Employees EPF",Kind="deduction",HasNumbers=true,HasFormula=true,FormulaDependencies=[4]},
            new(){ColumnIndex=6,SourceHeader="Net Paid",Kind="netPay",HasNumbers=true,HasFormula=true,FormulaDependencies=[4,5]},
        ]};
        var before=JsonSerializer.Serialize(request);
        var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));
        var references=ExcelPayslipMappingService.Retrieve(request.Columns,knowledge,[]);
        var lesson=Assert.Single(references,reference=>reference.Title.TrimEnd('\r').EndsWith(": helper proration"));
        Assert.True(lesson.Content.Length<2600);Assert.Contains("parsed operator tree",lesson.Content);
        Assert.Contains("not a permanent exemption",lesson.Content);Assert.Contains("blank code or equal name alone",lesson.Content);
        using var prompt=JsonDocument.Parse(ExcelPayslipMappingService.BuildPrompt(request,null,references));
        Assert.Equal("excel-payroll-mapping-v74",prompt.RootElement.GetProperty("knowledgeVersion").GetString());
        Assert.Equal(before,JsonSerializer.Serialize(request));Assert.All(request.Columns.Take(4),column=>Assert.Equal("info",column.Kind));
    }
    [Fact]
    public void Generic_allowance_and_day_headers_do_not_displace_their_existing_lesson_with_a_helper_chain()
    {
        var columns=new List<ExcelPayslipMappingColumn> {
            new(){ColumnIndex=0,SourceHeader="Telephone Allowance",Kind="info"},
            new(){ColumnIndex=1,SourceHeader="Payroll Days",Kind="info"},
            new(){ColumnIndex=2,SourceHeader="Payable Days",Kind="info"},
            new(){ColumnIndex=3,SourceHeader="Telephone Allowance",Kind="earning",HasFormula=true,FormulaDependencies=[0,1,2]}
        };
        var knowledge=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));
        var references=ExcelPayslipMappingService.Retrieve(columns,knowledge,[]);
        Assert.DoesNotContain(references,reference=>reference.Title.TrimEnd('\r').EndsWith(": helper proration"));
        var withoutHelper=System.Text.RegularExpressions.Regex.Replace(knowledge,
            @"(?ms)^## Monthly Rate Total Working Days Actual Working Days Per Days Rate Basic Salary: helper proration\r?\n.*?(?=^## |\z)","");
        Assert.Equal(ExcelPayslipMappingService.Retrieve(columns,withoutHelper,[]).Select(reference=>reference.Title),
            references.Select(reference=>reference.Title));
    }
}
