using System.Text.Json;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using PdfSharp.Pdf.IO;

namespace Payroll.API.Tests.Services;

public sealed class RecruitmentDocumentTests
{
    private static string Body(RecruitmentDocumentLayout layout) => LayoutDocumentPdfService.Marker + JsonSerializer.Serialize(layout, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static RecruitmentDocumentLayout Layout() => new()
    {
        Inputs = [new() { Key="candidateName",Label="Candidate",Required=true }, new() {Key="monthlyBasic",Label="Monthly basic",Type="money",Required=true},
            new() {Key="hraRate",Label="HRA",Type="percent",Required=true},new() {Key="monthlyGross",Label="Monthly gross",Type="money",Required=true},
            new() {Key="contractEndDate",Label="Contract end",Type="date",Required=true,EmptyValues=["To update"]},new(){Key="fatherName",Label="Father",BlankText="________"}],
        Calculations=[new(){Key="hraAmount",Operation="multiply",Inputs=["monthlyBasic","hraRate"]},new(){Key="checkedGross",Operation="add",Inputs=["monthlyBasic","hraAmount"]}],
        Checks=[new(){Left="checkedGross",Right="monthlyGross",Tolerance=.01m,Message="Source gross needs review"}],
        Sections=[new(){Flow=true,Pages=4,Blocks=[new(){Text="{{candidateName}} - ₹{{monthlyGross.amount}} - {{contractEndDate.date}}",Align="justify"},
            new(){Kind="table",ColumnWidths=[190,275.6],Rows=[[new(){Text="Monthly basic",Bold=true},new(){Text="₹{{monthlyBasic.amount}}"}],[new(){Text="HRA"},new(){Text="{{hraRate.percent}}"}]]}]},
            new(){Elements=[new(){X=65,Y=150,Width=400,Height=50,Text="Endorsement - {{candidateName}}"}]},
            new(){Elements=[new(){X=65,Y=150,Width=400,Height=50,Text="Annexure-A"}]},new(){Elements=[new(){X=65,Y=150,Width=400,Height=50,Text="Annexure-B"}]},new(){Elements=[new(){X=65,Y=150,Width=400,Height=50,Text="Annexure-C - {{fatherName}}"}]}]
    };
    private static Dictionary<string,string> Values()=>new(){["candidateName"]="Example Candidate",["monthlyBasic"]="INR 20,000/-",["hraRate"]="30%",["monthlyGross"]="26,000/-",["contractEndDate"]="Up to 31st March 2027"};

    [Fact]
    public void Layout_generates_eight_pages_with_table_and_annexures_and_keeps_source_values()
    {
        var values=Values();var before=JsonSerializer.Serialize(values);
        var result=new TemplatePdfService().Create("Offer",Body(Layout()),values);
        Assert.Equal("",result.Error);Assert.NotNull(result.Bytes);
        using var pdf=PdfReader.Open(new MemoryStream(result.Bytes!),PdfDocumentOpenMode.Import);Assert.Equal(8,pdf.PageCount);
        Assert.Equal(before,JsonSerializer.Serialize(values));
    }
    [Theory]
    [InlineData("0","20000","0%")]
    [InlineData("0.1","22000","10%")]
    [InlineData("27%","25400","27%")]
    public void Zero_and_fractional_HRA_are_valid_and_monthly_money_is_not_annualized(string rate,string gross,string percent)
    {
        var input=Values();input["hraRate"]=rate;input["monthlyGross"]=gross;
        var ready=LayoutDocumentPdfService.PrepareValues(Layout(),input,true,out var missing);
        Assert.Empty(missing);Assert.Equal(percent,ready["hraRate.percent"]);Assert.Equal(gross,ready["monthlyGross"]);
        Assert.False(ready.ContainsKey("annualCtc"));Assert.Equal("2027-03-31",ready["contractEndDate"]);Assert.Equal("________",ready["fatherName"]);
    }
    [Fact]
    public void Missing_tenure_and_real_money_mismatches_remain_pending_without_invented_dates_or_pay()
    {
        var input=Values();input["contractEndDate"]="To update";input["monthlyGross"]="26099";
        LayoutDocumentPdfService.PrepareValues(Layout(),input,false,out var missing);
        Assert.Contains("Contract end",missing);Assert.Contains("Source gross needs review",missing);
        var result=new LayoutDocumentPdfService().Create(Body(Layout()),input);Assert.Null(result.Bytes);Assert.Contains("Complete the document details",result.Error);
        Assert.Equal("To update",input["contractEndDate"]);Assert.Equal("26099",input["monthlyGross"]);
    }
    [Fact]
    public void Values_cannot_inject_layout_page_breaks_or_recursively_expand_placeholders()
    {
        var input=Values();input["candidateName"]="{{unconfigured}} [[PAGE_BREAK]] <script>text</script>";
        var result=new LayoutDocumentPdfService().Create(Body(Layout()),input);Assert.NotNull(result.Bytes);
        using var pdf=PdfReader.Open(new MemoryStream(result.Bytes!),PdfDocumentOpenMode.Import);Assert.Equal(8,pdf.PageCount);
    }
    [Fact]
    public void Bad_layouts_remote_assets_forward_calculations_and_unbound_fields_are_rejected()
    {
        var layout=Layout();layout.Header=[new(){Kind="image",Asset="https://example.test/logo.png",X=20,Y=20,Width=30,Height=30}];Assert.Throws<InvalidOperationException>(()=>LayoutDocumentPdfService.Parse(Body(layout)));
        layout=Layout();layout.Calculations[0].Inputs=["notYetDefined","monthlyBasic"];Assert.Throws<InvalidOperationException>(()=>LayoutDocumentPdfService.Parse(Body(layout)));
        layout=Layout();layout.Sections[0].Blocks[0].Text="{{privateUndeclaredField}}";Assert.Throws<InvalidOperationException>(()=>LayoutDocumentPdfService.Parse(Body(layout)));
        Assert.Throws<InvalidOperationException>(()=>LayoutDocumentPdfService.Parse(LayoutDocumentPdfService.Marker+"{\"version\":1,\"script\":\"ignore rules\"}"));
    }
    [Fact]
    public void Text_overflow_returns_review_error_instead_of_a_clipped_letter()
    {
        var layout=Layout();layout.Sections[1].Elements[0].Height=1;
        var result=new LayoutDocumentPdfService().Create(Body(layout),Values());Assert.Null(result.Bytes);Assert.Contains("no text was omitted",result.Error);
    }
    [Fact]
    public void Existing_UIDAI_and_payslip_font_names_keep_identical_embedded_bytes()
    {
        var resolver=DocumentFontResolver.Shared;
        Assert.Equal("NotoSerif-Regular",resolver.ResolveTypeface("OfferSerif",false,true).FaceName);
        Assert.Equal("NotoSerif-Bold",resolver.ResolveTypeface("OfferSerif",true,true).FaceName);
        using var source=typeof(BrandedOfferPdfService).Assembly.GetManifestResourceStream("Payroll.API.Assets.OfferLetters.NotoSerif-Regular.ttf")!;
        using var bytes=new MemoryStream();source.CopyTo(bytes);Assert.Equal(bytes.ToArray(),resolver.GetFont("NotoSerif-Regular"));
        Assert.Equal("Caladea-Regular",resolver.ResolveTypeface("LayoutSerif",false,false).FaceName);
    }
    [Fact]
    public void Plain_templates_retain_the_existing_renderer_and_UIDAI_marker_is_not_a_layout()
    {
        var result=new TemplatePdfService().Create("Hello {{candidateName}}","<p>Joining {{joiningDate}}</p>",new Dictionary<string,string>{{"candidateName","Example Candidate"},{"joiningDate","2026-11-10"}});
        Assert.NotNull(result.Bytes);Assert.Equal("",result.Error);Assert.False(LayoutDocumentPdfService.IsLayout(BrandedOfferPdfService.DefaultTemplate));
        using var pdf=PdfReader.Open(new MemoryStream(result.Bytes!),PdfDocumentOpenMode.Import);Assert.Single(pdf.Pages);
    }
    [Fact]
    public void Two_source_phones_and_honorifics_preserve_identity_without_using_name_only_matching()
    {
        Assert.Equal(new[]{"9876543210","9123456789"},RecruitmentDocumentRepository.Phones("+91-9876543210; 09123456789"));
        Assert.Equal("Example Candidate",RecruitmentDocumentRepository.ProfileName("Ms. Example Candidate"));
        Assert.Equal(RecruitmentDocumentRepository.NameKey("Example Candidate"),RecruitmentDocumentRepository.NameKey("Ms Example Candidate"));
        Assert.Equal("00001234567890",RecruitmentDocumentRepository.NormalizeInputs(new(){Inputs=[new(){Key="accountNo",Type="text"}]},new(){["accountNo"]="00001234567890"})["accountNo"]);
    }
    [Fact]
    public async Task Foreign_client_and_unauthorized_reads_are_denied_before_touching_a_database()
    {
        var repository=new RecruitmentDocumentRepository(new ConfigurationBuilder().Build(),null!,new());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>repository.RowsAsync(56,new(){Id=7,IsActive=true,ClientId=55,Permissions=["recruitment.candidate.manage"]}));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>repository.RowsAsync(55,new(){Id=7,IsActive=true,Permissions=["recruitment.rfr.create"]}));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>repository.RowsAsync(55,new(){Id=0,IsActive=true,Permissions=["settings.manage"]}));
    }
    [Fact]
    public void Filenames_are_safe_and_keep_the_document_extension_when_the_client_name_is_long()
    {
        var name=RecruitmentDocumentRepository.SafeFileName("../client/"+new string('x',220)+"-draft-offer.pdf");
        Assert.DoesNotContain('/',name);Assert.DoesNotContain('\\',name);Assert.EndsWith(".pdf",name);Assert.True(name.Length<=180);
    }
    [Fact]
    public void Workbook_numeric_identifiers_and_caches_keep_source_digits_and_missing_formulas_never_become_zero()
    {
        using var stream=new MemoryStream();
        using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,true))
        {
            void Entry(string name,string text){using var writer=new StreamWriter(zip.CreateEntry(name).Open(),new UTF8Encoding(false));writer.Write(text);}
            Entry("[Content_Types].xml","""<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>""");
            Entry("_rels/.rels","""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Entry("xl/workbook.xml","""<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Employee list" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Entry("xl/_rels/workbook.xml.rels","""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");
            Entry("xl/worksheets/sheet1.xml","""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><dimension ref="A1:F2"/><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Employee</t></is></c></row><row r="2"><c r="A2" t="inlineStr"><is><t>Example Candidate</t></is></c><c r="B2"><v>1234567890123456</v></c><c r="C2"><v>1.23456789012345E+15</v></c><c r="D2"><v>24999.99999999999</v></c><c r="E2" t="inlineStr"><is><t>00001234567890</t></is></c><c r="F2"><f>D2*1.3</f></c></row></sheetData></worksheet>""");
        }
        var row=RecruitmentDocumentRepository.ReadWorkbook(stream.ToArray()).Single().Rows.Single(r=>r.Row==2);
        Assert.Equal("1234567890123456",row.Values[1]);Assert.Equal("1234567890123450",row.Values[2]);Assert.Equal("24999.99999999999",row.Values[3]);Assert.Equal("00001234567890",row.Values[4]);Assert.Equal("#MISSING_CACHED_FORMULA_RESULT",row.Values[5]);
    }
}
