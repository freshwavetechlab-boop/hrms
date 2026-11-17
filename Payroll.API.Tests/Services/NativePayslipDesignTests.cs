using System.Text;
using System.Text.Json;
using PdfSharp.Pdf.IO;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class NativePayslipDesignTests
{
    private static PayslipDesignTemplate Template()=>new(){Id=7001,ClientId=71,ShowYtd=false};
    private static PayslipDesignProfile Profile()=>new(){ClientId=71,TemplateId=7001,AmountDecimalPlaces=2,Fields=[
        new(){SourceColumn=2,Label="Designation",Kind="info",InfoToken="DESIGNATION"},
        new(){SourceColumn=3,Label="Current CTC (source)",Kind="info",InfoToken="SAVED_MONTHLY_GROSS"},
        new(){SourceColumn=4,Label="Present Days",Kind="info",InfoToken="PRESENT_DAYS"},
        new(){SourceColumn=5,Label="Salary",Kind="earning",NativeCode="BASIC"},
        new(){SourceColumn=6,Label="Bonus",Kind="earning",NativeCode="BONUS"},
        new(){SourceColumn=7,Label="Employee PF",Kind="deduction",NativeCode="PF"},
        new(){SourceColumn=8,Label="Employee ESI",Kind="deduction",NativeCode="ESI"},
        new(){SourceColumn=9,Label="Employer PF",Kind="employer",NativeCode="EPF_ER"},
        new(){SourceColumn=10,Label="Employer ESI",Kind="employer",NativeCode="ESIC_ER"},
        new(){SourceColumn=11,Label="Account No",Kind="info",InfoToken="BANK_ACCOUNT"}]};
    private static NativePayslipDesignData Data()=>new(){ClientId=71,EmployeeId=7101,EmployeeCode="SYNTH-7101",EmployeeName="Synthetic employee",ClientName="Synthetic client",Month="2026-10",Status="Approved",
        Gross=16000.20m,Deductions=1800.25m,NetPay=14199.95m,Information=new(){["DESIGNATION"]="Current Master role",["SAVED_MONTHLY_GROSS"]="16000.2",["PRESENT_DAYS"]="Not supplied",["BANK_ACCOUNT"]="001234567890"},
        Lines=[new("BASIC","Earning",16000.2m,16000.2m),new("BONUS","Earning",0,0),new("PF","Deduction",1800.25m,1800.25m),new("ESI","Deduction",0,0),new("PT","Deduction",0,0),new("EPF_ER","Benefit",1950,1950),new("ESIC_ER","Benefit",0,0)]};
    private static string Content(byte[] bytes)
    {using var pdf=PdfReader.Open(new MemoryStream(bytes),PdfDocumentOpenMode.Import);Assert.Single(pdf.Pages.Cast<object>());return Encoding.Latin1.GetString(pdf.Pages[0].Contents.CreateSingleContent().Stream.UnfilteredValue);}

    [Fact]
    public void Actual_pdf_reuses_source_renderer_with_identical_content_plan_and_preserves_zero_and_employer_rows()
    {
        var template=Template();var profile=Profile();var data=Data();
        var native=NativePayslipDesignService.Project(profile,template,data);
        var source=new ExcelPayslipRow{EmployeeCode=data.EmployeeCode,EmployeeName=data.EmployeeName,Information=[
            new(){Label="Designation",Value="Current Master role"},new(){Label="Current CTC (source)",Value="16000.2"},new(){Label="Present Days",Value="Not supplied"},new(){Label="Account No",Value="001234567890"}],
            Earnings=[new(){Label="Salary",Amount=16000.2m},new(){Label="Bonus",Amount=0}],Deductions=[new(){Label="Employee PF",Amount=1800.25m},new(){Label="Employee ESI",Amount=0}],
            EmployerContributions=[new(){Label="Employer PF",Amount=1950},new(){Label="Employer ESI",Amount=0}],NetPay=14199.95m};
        var batch=new ExcelPayslipBatch{ClientId=71,ClientName=data.ClientName,Month="2026-10"};var renderer=new ExcelPayslipPdfService();
        var baseline=Content(renderer.Create(batch,[source],amountDecimalPlaces:2));
        var result=Content(renderer.Create(batch,[native],amountDecimalPlaces:2,presentation:NativePayslipDesignService.Presentation(profile,template)));
        Assert.Equal(baseline,result);
        Assert.Contains("Employer Contributions",result);Assert.Contains("Employer PF",result);Assert.Contains("Bonus",result);
        Assert.DoesNotContain("PT",native.Deductions.Select(x=>x.Label));Assert.Contains("Rupees",result);Assert.DoesNotContain("14199.95 only",result);
    }

    [Fact]
    public void Projection_has_no_source_money_input_and_current_identity_does_not_change_saved_salary()
    {
        var profile=Profile();var template=Template();var data=Data();var before=JsonSerializer.Serialize(data);
        var row=NativePayslipDesignService.Project(profile,template,data);
        Assert.Equal("Current Master role",row.Information[0].Value);Assert.Equal("Not supplied",row.Information[2].Value);
        Assert.Equal(14199.95m,row.NetPay);Assert.Equal(16000.2m,row.DeclaredGross);Assert.Equal(1800.25m,row.DeclaredDeductions);
        Assert.Equal(new[]{"Salary","Bonus"},row.Earnings.Select(x=>x.Label));Assert.Equal(new[]{"Employee PF","Employee ESI"},row.Deductions.Select(x=>x.Label));
        Assert.Equal(1950,row.EmployerContributions[0].Amount);Assert.Equal(before,JsonSerializer.Serialize(data));
    }

    [Theory]
    [InlineData("earning")][InlineData("deduction")][InlineData("employer")]
    public void Unmapped_nonzero_component_in_any_financial_category_blocks_instead_of_dropping(string kind)
    {
        var data=Data();data.Lines.Add(new("UNREVIEWED",kind=="earning"?"Earning":kind=="deduction"?"Deduction":"Benefit",25,25));
        Assert.Contains("UNREVIEWED",Assert.Throws<InvalidOperationException>(()=>NativePayslipDesignService.Project(Profile(),Template(),data)).Message);
    }

    [Theory]
    [InlineData("Draft")][InlineData("Pending Approval")][InlineData("Failed")]
    public void Final_pdf_requires_approved_saved_payroll(string status)
    {var data=Data();data.Status=status;Assert.Throws<InvalidOperationException>(()=>NativePayslipDesignService.Project(Profile(),Template(),data));}

    [Fact]
    public void Explicit_hide_affects_presentation_only_and_never_drops_component_from_totals()
    {
        var profile=Profile();profile.Fields.Single(f=>f.NativeCode=="PF").Visible=false;profile.Fields.Add(new(){SourceColumn=12,Label="Unknown source heading",Kind="info",Visible=false});
        var data=Data();var row=NativePayslipDesignService.Project(profile,Template(),data);
        Assert.DoesNotContain(row.Deductions,f=>f.Label=="Employee PF");Assert.Equal(1800.25m,row.DeclaredDeductions);Assert.Equal(data.NetPay,row.NetPay);
    }

    [Fact]
    public void Client_precision_mapping_duplicates_and_inconsistent_saved_totals_are_rejected()
    {
        var data=Data();data.ClientId=72;Assert.Throws<InvalidOperationException>(()=>NativePayslipDesignService.Project(Profile(),Template(),data));
        var profile=Profile();profile.AmountDecimalPlaces=1;Assert.Throws<InvalidOperationException>(()=>NativePayslipDesignService.Project(profile,Template(),Data()));
        profile=Profile();profile.Fields[6].NativeCode="PF";Assert.Throws<InvalidOperationException>(()=>NativePayslipDesignService.Project(profile,Template(),Data()));
        profile=Profile();profile.Fields[1].InfoToken="CURRENT_CTC";Assert.Throws<InvalidOperationException>(()=>NativePayslipDesignService.Project(profile,Template(),Data()));
        data=Data();data.Gross+=5;Assert.Throws<InvalidOperationException>(()=>NativePayslipDesignService.Project(Profile(),Template(),data));
    }

    [Theory]
    [InlineData(0,"14,200")][InlineData(2,"14,199.95")]
    public void Precision_and_presentation_controls_are_opt_in_and_do_not_change_salary(int precision,string expected)
    {
        var template=Template();template.ShowBank=false;var profile=Profile();profile.AmountDecimalPlaces=precision;var data=Data();var row=NativePayslipDesignService.Project(profile,template,data);
        Assert.DoesNotContain(row.Information,f=>f.Label=="Account No");var before=JsonSerializer.Serialize(row);
        var content=Content(new ExcelPayslipPdfService().Create(new(){ClientName="Unique hidden client",Month="2026-10"},[row],amountDecimalPlaces:precision,presentation:new(){ShowLogo=false,ShowClient=false,Footer="Reviewed custom footer"}));
        Assert.Contains(expected,content);Assert.DoesNotContain("Unique hidden client",content);Assert.Contains("Reviewed custom footer",content);
        Assert.Equal(before,JsonSerializer.Serialize(row));
    }

    [Fact]
    public void Benefit_lines_are_retained_and_unknown_financial_categories_fail_closed()
    {
        var lines=NativePayslipDesignService.ReadLines("[{\"id\":\"EPF_ER\",\"category\":\"Benefit\",\"monthlyAmount\":1950,\"amount\":1950},{\"id\":\"NET_PAY\",\"category\":\"Summary\",\"amount\":50}]");
        Assert.Single(lines);Assert.Equal(1950,lines[0].Amount);
        Assert.Throws<InvalidOperationException>(()=>NativePayslipDesignService.ReadLines("[{\"id\":\"X\",\"category\":\"UnrecognizedCost\",\"amount\":100}]"));
    }

    [Fact]
    public void Settings_save_authorization_retains_selected_and_single_client_scope()
    {
        var user=new AuthUser{Id=1,IsActive=true,Permissions=["settings.manage"],ClientAccessMode="Selected",AssignedClientIds=[71,72]};
        Assert.True(PayslipDesignRepository.CanManage(user,71));Assert.False(PayslipDesignRepository.CanManage(user,73));
        user.ClientAccessMode="Single";user.ClientId=71;Assert.True(PayslipDesignRepository.CanManage(user,71));Assert.False(PayslipDesignRepository.CanManage(user,72));
        user.MustChangePassword=true;Assert.False(PayslipDesignRepository.CanManage(user,71));
    }

    [Fact]
    public void Supplemental_master_fields_share_actual_source_projection_order_and_pdf_layout()
    {
        var profile=Profile();var data=Data();var template=Template();
        foreach(var (id,label,token,value) in new[]{(1501,"Department","DEPARTMENT","Current department"),(1502,"Grade","GRADE","Current grade"),(1503,"Work Location","WORK_LOCATION","Current office"),(1504,"Email","EMAIL","synthetic@example.test")})
        {profile.Fields.Add(new(){SourceColumn=id,Label=label,Kind="info",InfoToken=token});data.Information[token]=value;}
        var native=NativePayslipDesignService.Project(profile,template,data);
        var source=new ExcelPayslipRow { Id="source",EmployeeName=data.EmployeeName,EmployeeCode=data.EmployeeCode,NetPay=data.NetPay,
            Information=native.Information.Take(4).Select(f=>new ExcelPayslipText{Label=f.Label,Value=f.Value}).ToList(),
            Earnings=native.Earnings,Deductions=native.Deductions,EmployerContributions=native.EmployerContributions,
            Employment=new(data.EmployeeId,data.EmployeeCode,"Active",null,false,false),
            MasterDetails=new(data.EmployeeId,"Current Master role"){Linked=true,ProfileOnly=true,EmployeeCode=data.EmployeeCode,EmployeeName=data.EmployeeName,
                Fields=new(){["designation"]="Current Master role",["department"]="Current department",["grade"]="Current grade",["worklocation"]="Current office",["email"]="synthetic@example.test"}}};
        var batch=new ExcelPayslipBatch{ClientId=data.ClientId,ClientName=data.ClientName,Month=data.Month};var renderer=new ExcelPayslipPdfService();
        Assert.Equal(Content(renderer.Create(batch,[source],amountDecimalPlaces:2)),Content(renderer.Create(batch,[native],amountDecimalPlaces:2,presentation:new(){Footer=template.Note})));
        profile.Fields.Single(f=>f.SourceColumn==1502).InfoToken="EMAIL";
        Assert.Throws<InvalidOperationException>(()=>NativePayslipDesignService.Project(profile,template,data));
    }

    [Fact]
    public void Newly_reviewed_simple_source_omits_zero_financial_rows_without_changing_saved_money_or_employee()
    {
        var profile=Profile(); profile.SimpleLayout=true;
        profile.KeepMappedZeroRows=NativePayslipDesignService.SourceKeepsMappedZeroRows(profile.SimpleLayout);
        var template=Template(); var data=Data(); var before=JsonSerializer.Serialize(data);
        var row=NativePayslipDesignService.Project(profile,template,data);
        var projectedBefore=JsonSerializer.Serialize(row);
        var content=Content(new ExcelPayslipPdfService().Create(new(){SimpleLayout=true,ClientName=data.ClientName,Month=data.Month},[row],
            amountDecimalPlaces:2,presentation:NativePayslipDesignService.Presentation(profile,template)));
        Assert.DoesNotContain("Bonus",content); Assert.DoesNotContain("Employee ESI",content); Assert.DoesNotContain("Employer ESIC",content);
        Assert.Contains("14,199.95",content); Assert.Contains("1,800.25",content); Assert.Contains("1,950.00",content);
        Assert.Contains(row.Earnings,line=>line.Label=="Bonus"&&line.Amount==0);
        Assert.Equal(projectedBefore,JsonSerializer.Serialize(row)); Assert.Equal(before,JsonSerializer.Serialize(data));

        foreach(var line in data.Lines.ToArray()) data.Lines[data.Lines.IndexOf(line)]=line with {Amount=0,MonthlyAmount=0};
        data.Gross=data.Deductions=data.NetPay=0;
        var zero=NativePayslipDesignService.Project(profile,template,data);
        var zeroContent=Content(new ExcelPayslipPdfService().Create(new(){SimpleLayout=true,ClientName=data.ClientName,Month=data.Month},[zero],
            amountDecimalPlaces:2,presentation:NativePayslipDesignService.Presentation(profile,template)));
        Assert.Contains("Synthetic employee",zeroContent); Assert.Contains("0.00",zeroContent); Assert.DoesNotContain("Employer EPF",zeroContent);
    }

    [Fact]
    public void Standard_and_legacy_protected_profiles_preserve_their_zero_row_visibility()
    {
        Assert.True(NativePayslipDesignService.SourceKeepsMappedZeroRows(simpleLayout:false));
        var profile=Profile(); profile.SimpleLayout=true;
        var legacyJson=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(profile))!.AsObject();
        legacyJson.Remove(nameof(PayslipDesignProfile.KeepMappedZeroRows));
        var legacy=JsonSerializer.Deserialize<PayslipDesignProfile>(legacyJson.ToJsonString())!;
        Assert.True(legacy.KeepMappedZeroRows);
        var template=Template(); var row=NativePayslipDesignService.Project(legacy,template,Data());
        var content=Content(new ExcelPayslipPdfService().Create(new(){SimpleLayout=true,Month="2026-10"},[row],amountDecimalPlaces:2,
            presentation:NativePayslipDesignService.Presentation(legacy,template)));
        Assert.Contains("Bonus",content); Assert.Contains("Employee ESI",content);
    }
}
