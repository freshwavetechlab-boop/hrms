using System.Text;
using PdfSharp.Pdf.IO;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class PayslipDesignSamplePreviewTests
{
    private static PayslipDesignSaveRequest Draft()=>new(){Template=new(){ClientId=71,ShowYtd=false},SourceHash=new string('a',64),Fields=[
        new(){SourceColumn=1,Label="Designation",Kind="info"},new(){SourceColumn=2,Label="Account No",Kind="info"},
        new(){SourceColumn=3,Label="Salary",Kind="earning"},new(){SourceColumn=4,Label="Bonus",Kind="earning"},
        new(){SourceColumn=5,Label="Employee PF",Kind="deduction"},new(){SourceColumn=6,Label="Employee ESI",Kind="deduction"},
        new(){SourceColumn=7,Label="Employer PF",Kind="employer"}]};
    private static List<PayslipDesignField> Source()=>Draft().Fields.Select(f=>new PayslipDesignField{SourceColumn=f.SourceColumn,Label=f.Label,Kind=f.Kind,InfoToken=f.SourceColumn==1?"DESIGNATION":f.SourceColumn==2?"BANK_ACCOUNT":""}).ToList();

    [Fact]
    public void Incomplete_unreviewed_mapping_can_preview_without_fabricating_real_identity_or_payroll()
    {
        var request=Draft();Assert.False(request.Reviewed);Assert.Equal(0,request.Template.Id);
        PayslipDesignSamplePreview.Validate(request,Source(),[]);
        var row=PayslipDesignSamplePreview.CreateRow(request,Source());
        Assert.Equal("Sample Employee",row.EmployeeName);Assert.Equal("SAMPLE-001",row.EmployeeCode);
        Assert.Equal("-",row.Information.Single(f=>f.Label=="Account No").Value);
        Assert.Equal("Sample designation",row.Information.Single(f=>f.Label=="Designation").Value);
        Assert.Equal(26200,row.DeclaredGross);Assert.Equal(1950,row.DeclaredDeductions);Assert.Equal(24250,row.NetPay);
        Assert.Single(row.EmployerContributions);Assert.Null(row.MasterDetails);
    }

    [Theory]
    [InlineData("label")][InlineData("kind")][InlineData("column")][InlineData("duplicate")]
    public void Preview_rejects_source_metadata_changes(string change)
    {
        var request=Draft();
        switch(change){case"label":request.Fields[0].Label="Changed";break;case"kind":request.Fields[0].Kind="earning";break;case"column":request.Fields[0].SourceColumn=99;break;default:request.Fields[1].SourceColumn=request.Fields[0].SourceColumn;break;}
        Assert.Throws<InvalidOperationException>(()=>PayslipDesignSamplePreview.Validate(request,Source(),[]));
    }

    [Fact]
    public void Display_controls_hide_bank_and_money_rows_without_changing_sample_totals()
    {
        var request=Draft();request.Template.ShowBank=false;request.Fields.Single(f=>f.Label=="Bonus").Visible=false;
        PayslipDesignSamplePreview.Validate(request,Source(),[]);
        var row=PayslipDesignSamplePreview.CreateRow(request,Source());
        Assert.DoesNotContain(row.Information,f=>f.Label=="Account No");Assert.Single(row.Earnings);
        Assert.Equal(26200,row.DeclaredGross);Assert.Equal(24250,row.NetPay);
    }

    [Fact]
    public void Sample_uses_the_exact_shared_renderer_and_keeps_the_employer_section()
    {
        var request=Draft();var row=PayslipDesignSamplePreview.CreateRow(request,Source());
        var service=new ExcelPayslipPdfService();var batch=new ExcelPayslipBatch{ClientId=71,ClientName="Example client",Month="2026-10"};
        var bytes=service.Create(batch,[row],amountDecimalPlaces:0,presentation:new(){ShowLogo=true,ShowClient=true,KeepMappedZeroRows=true,ExplicitSimpleInformation=true,Footer=request.Template.Note});
        var reference=service.Create(batch,[row],amountDecimalPlaces:0);
        static string Content(byte[] value){using var pdf=PdfReader.Open(new MemoryStream(value),PdfDocumentOpenMode.Import);Assert.Single(pdf.Pages.Cast<object>());return Encoding.Latin1.GetString(pdf.Pages[0].Contents.CreateSingleContent().Stream.UnfilteredValue);}
        Assert.Equal(Content(reference),Content(bytes));
    }
    [Fact]
    public void Reordering_display_fields_keeps_sample_amounts_and_information_budget_consistent()
    {
        var request=Draft();request.Fields[0].InfoToken="SAVED_MONTHLY_GROSS";
        var before=PayslipDesignSamplePreview.CreateRow(request,Source());request.Fields.Reverse();
        var after=PayslipDesignSamplePreview.CreateRow(request,Source());
        Assert.Equal(before.Earnings.OrderBy(x=>x.Label).Select(x=>(x.Label,x.Amount)),after.Earnings.OrderBy(x=>x.Label).Select(x=>(x.Label,x.Amount)));
        Assert.Equal(before.NetPay,after.NetPay);Assert.Equal("26200",after.Information.Single(x=>x.Label=="Designation").Value);
    }
}
