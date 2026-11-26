using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ReviewedSourceMonthIdentityTests
{
    [Fact]
    public void Captured_identity_boundary_is_retrieved_from_anonymous_source_headers()
    {
        var columns=new[]{"Bio Metric ID","Name","AADHAR NO.","PAN NO.","Total Working Days","Actual working days","Net Paid"}
            .Select((header,index)=>new ExcelPayslipMappingColumn{ColumnIndex=index,SourceHeader=header,Kind="info"}).ToList();
        var text=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FrevoPilot","docs","excel-payroll-mapping.md"));
        var lesson=Assert.Single(ExcelPayslipMappingService.Retrieve(columns,text,[]),reference=>reference.Title.StartsWith("Bio Metric ID AADHAR NO PAN Employee code:"));
        Assert.True(lesson.Content.Length<2600);Assert.Contains("generated display code",lesson.Content);
        Assert.Contains("physical presence stays unknown",lesson.Content);Assert.Equal("excel-payroll-mapping-v80",ExcelPayslipMappingService.KnowledgeVersion);
    }
    private static ExcelPayslipBatch Batch()=>new(){Id=new('a',32),ClientId=11,Month="2026-10",
        Rows=[new(){Id="row-7",SourceRow=7,EmployeeCode="DISPLAY008",EmployeeName="Example Employee"}]};
    private static ExcelPayslipCalculationSource Source()=>new(){ColumnCount=3,
        Columns=[new(){ColumnIndex=0,SourceHeader="Employee code",Kind="employeeCode"},new(){ColumnIndex=1,SourceHeader="PAN No."},new(){ColumnIndex=2,SourceHeader="AADHAR NO."}],
        Rows=[new(){SourceRow=7,Cells=[new(){Value="",DataType="Blank"},new(){Value="ABCDE1234F",DataType="Text"},new(){Value="234567890123",DataType="Text"}]}]};
    private static ExcelPayslipRepository.EmploymentMaster Master(int id=7,string code="NATIVE007")=>new(){Id=id,ClientId=11,EmployeeCode=code,
        FirstName="Example",LastName="Employee",Pan="ABCDE1234F",Aadhaar="234567890123",IsActive=true,Eligible=true};
    private static void Review(ExcelPayslipBatch batch,ExcelPayslipCalculationSource source,params ExcelPayslipRepository.EmploymentMaster[] masters)=>
        ExcelPayslipRepository.AnnotateEmployment(batch,source,masters,[],new HashSet<string>{"row-7"});

    [Fact]
    public void Captured_empty_code_uses_unique_government_identity_without_rewriting_display_code_or_other_inputs()
    {
        var batch=Batch();var source=Source();var master=Master();var other=Master(8,"DISPLAY008");other.FirstName="Other";other.Pan="FGHIJ5678K";other.Aadhaar="345678901234";
        var before=JsonSerializer.Serialize(new{source,master,other});
        ExcelPayslipRepository.AnnotateEmployment(batch,source,[master,other],[]);Assert.Null(batch.Rows[0].Employment?.EmployeeId);
        Review(batch,source,master,other);
        Assert.Equal(7,batch.Rows[0].Employment?.EmployeeId);Assert.Equal("NATIVE007",batch.Rows[0].Employment?.EmployeeCode);
        Assert.Equal("DISPLAY008",batch.Rows[0].EmployeeCode);Assert.Equal(before,JsonSerializer.Serialize(new{source,master,other}));
    }

    [Theory]
    [InlineData("valid")][InlineData("supplied-code")][InlineData("legacy")][InlineData("incomplete")]
    [InlineData("disabled-generation")][InlineData("missing-pan")][InlineData("calculated-pan")][InlineData("missing-aadhaar")]
    [InlineData("different-name")][InlineData("conflicting-government-owner")]
    public void Actual_generated_code_producer_keeps_typed_source_conflicts_and_requires_two_captured_identifiers(string change)
    {
        var batch=Batch();batch.Rows[0].EmployeeCode="";batch.EmployeeCodeGeneration=new(true,3);
        var source=Source();source.CaptureVersion=1;source.CompleteSheetCapture=true;
        ExcelPayslipRepository.AssignEmployeeCodes(batch,source,"DISPLAY",["DISPLAY007"]);
        Assert.Equal("DISPLAY008",batch.Rows[0].EmployeeCode);Assert.Equal("",source.Rows[0].Cells[0]!.DataType);
        var master=Master();var other=Master(8,"DISPLAY008");other.FirstName="Other";other.Pan="FGHIJ5678K";other.Aadhaar="345678901234";
        if(change=="supplied-code")source.Rows[0].Cells[0]!.DataType="Text";
        if(change=="legacy")source.CaptureVersion=0;
        if(change=="incomplete")source.CompleteSheetCapture=false;
        if(change=="disabled-generation")batch.EmployeeCodeGeneration=new(false,3);
        if(change=="missing-pan")source.Rows[0].Cells[1]!.Value="N/A";
        if(change=="calculated-pan")source.Rows[0].Cells[1]!.FormulaText="=A1";
        if(change=="missing-aadhaar")source.Rows[0].Cells[2]!.Value="N/A";
        if(change=="different-name")master.FirstName="Different";
        if(change=="conflicting-government-owner")other.Aadhaar=master.Aadhaar;
        var snapshot=JsonSerializer.Serialize(new{source,master,other});
        Review(batch,source,master,other);
        if(change=="valid")Assert.Equal(7,batch.Rows[0].Employment?.EmployeeId);else Assert.Null(batch.Rows[0].Employment?.EmployeeId);
        Assert.Equal("DISPLAY008",batch.Rows[0].EmployeeCode);Assert.Equal(snapshot,JsonSerializer.Serialize(new{source,master,other}));
    }
    [Theory]
    [InlineData("real-code")][InlineData("no-code-column")][InlineData("code-formula")][InlineData("duplicate-code-column")]
    [InlineData("different-name")][InlineData("different-client")][InlineData("duplicate-government-owner")][InlineData("aadhaar-conflict")]
    public void Captured_identity_review_keeps_real_conflicts_and_unproven_code_blanks_blocked(string change)
    {
        var batch=Batch();var source=Source();var master=Master();var other=Master(8,"DISPLAY008");other.FirstName="Other";other.Pan="FGHIJ5678K";other.Aadhaar="345678901234";
        if(change=="real-code")source.Rows[0].Cells[0]!.Value="DISPLAY008";
        if(change=="no-code-column")source.Columns[0].Kind="info";
        if(change=="code-formula")source.Rows[0].Cells[0]!.FormulaText="=A1";
        if(change=="duplicate-code-column")source.Columns.Add(new(){ColumnIndex=0,Kind="employeeCode"});
        if(change=="different-name")master.FirstName="Different";
        if(change=="different-client")master.ClientId=12;
        if(change=="duplicate-government-owner")other.Pan=master.Pan;
        if(change=="aadhaar-conflict")source.Rows[0].Cells[2]!.Value=other.Aadhaar;
        Review(batch,source,master,other);Assert.Null(batch.Rows[0].Employment?.EmployeeId);
    }
    [Theory]
    [InlineData("AADHAR NO.")][InlineData("Aadhaar No")][InlineData("Aadhar Number")]
    public void Captured_aadhaar_heading_is_an_identity_literal_and_never_an_earning(string heading)
    {
        var batch=Batch();batch.Rows[0].EmployeeCode="SOURCE007";var source=Source();source.Rows[0].Cells[0]!.Value="SOURCE007";
        source.Rows[0].Cells[1]!.Value="N/A";source.Columns[2].SourceHeader=heading;var master=Master();master.Pan="";
        ExcelPayslipRepository.AnnotateEmployment(batch,source,[master],[]);Assert.Null(batch.Rows[0].Employment?.EmployeeId);
        Review(batch,source,master);Assert.Equal(7,batch.Rows[0].Employment?.EmployeeId);
        source.Rows[0].Cells[2]!.FormulaText="=A1";Review(batch,source,master);Assert.Null(batch.Rows[0].Employment?.EmployeeId);
    }
}
