using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Services;
using Xunit;

namespace Payroll.API.Tests.Services;

public sealed class NativePayslipMonthlyComponentInformationTests
{
    private const string Token="COMPONENT_MONTHLY:STAT_BONUS";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static PayslipDesignTemplate Template()=>new(){Id=91,ClientId=61,Theme="Excel Payslip"};
    private static PayslipDesignField Info()=>new(){SourceColumn=5,Label="Bonus (monthly)",Kind="info",InfoToken=Token};
    private static PayslipDesignProfile Profile()=>new(){ClientId=61,TemplateId=91,Fields=[Info(),new(){SourceColumn=8,Label="Basic earned",Kind="earning",NativeCode="BASIC"},new(){SourceColumn=9,Label="Bonus earned",Kind="earning",NativeCode="STAT_BONUS"},new(){SourceColumn=18,Label="PF",Kind="deduction",NativeCode="PF"},new(){SourceColumn=11,Label="Employer PF",Kind="employer",NativeCode="EPF_ER"}]};
    private static NativePayslipDesignData Data()
    {
        var data=new NativePayslipDesignData{ClientId=61,EmployeeId=101,EmployeeCode="SAMPLE-001",EmployeeName="Sample Employee",Month="2026-10",Status="Approved",
            Lines=[new("BASIC","Earning",19760,9880),new("STAT_BONUS","Earning",1646,823),new("PF","Deduction",2371.2m,1185.6m),new("EPF_ER","Benefit",2568.8m,1284.4m)]};
        data.Information["SAVED_MONTHLY_GROSS"]="21406";data.Information["CURRENT_ANNUAL_CTC"]="999999";data.Information[Token]="123456";
        Totals(data);return data;
    }
    private static void Totals(NativePayslipDesignData data){data.Gross=data.Lines.Where(l=>NativePayslipDesignService.LineKind(l.Category)=="earning").Sum(l=>l.Amount);data.Deductions=data.Lines.Where(l=>NativePayslipDesignService.LineKind(l.Category)=="deduction").Sum(l=>l.Amount);data.NetPay=data.Gross-data.Deductions;}
    private static PayslipDesignCode[] Codes()=>[new("BASIC","Basic","Earning"),new("STAT_BONUS","Bonus","Earning"),new("PF","Employee PF","Deduction"),new("EPF_ER","Employer PF","Benefit")];

    [Fact]
    public void Partial_days_show_saved_full_month_component_and_preserve_every_cash_value()
    {
        var data=Data();var profile=Profile();var before=JsonSerializer.Serialize(data,Json);var fields=JsonSerializer.Serialize(profile.Fields,Json);
        var row=NativePayslipDesignService.Project(profile,Template(),data);
        Assert.Equal("1646",row.Information.Single().Value);Assert.Equal("Bonus (monthly)",row.Information.Single().Label);
        Assert.Equal(new[]{9880m,823m},row.Earnings.Select(a=>a.Amount));Assert.Equal(1185.6m,row.Deductions.Single().Amount);Assert.Equal(1284.4m,row.EmployerContributions.Single().Amount);
        Assert.Equal(data.Gross,row.DeclaredGross);Assert.Equal(data.Deductions,row.DeclaredDeductions);Assert.Equal(data.NetPay,row.NetPay);
        Assert.Equal(before,JsonSerializer.Serialize(data,Json));Assert.Equal(fields,JsonSerializer.Serialize(profile.Fields,Json));
    }
    [Fact]
    public void Master_information_gross_earned_and_forged_token_values_are_never_fallbacks()
    {var data=Data();Assert.Equal("1646",NativePayslipDesignService.Project(Profile(),Template(),data).Information.Single().Value);}
    [Fact]
    public void Extra_period_adjustment_with_zero_monthly_does_not_double_monthly_base()
    {
        var data=Data();data.Lines.Add(new("STAT_BONUS","Earning",0,100));Totals(data);
        var row=NativePayslipDesignService.Project(Profile(),Template(),data);Assert.Equal("1646",row.Information.Single().Value);Assert.Equal(923,row.Earnings.Single(a=>a.Label=="Bonus earned").Amount);Assert.Equal(data.NetPay,row.NetPay);
    }
    [Fact]
    public void Zero_payable_earned_amount_does_not_erase_saved_monthly_amount()
    {var data=Data();data.Lines[1]=data.Lines[1] with{Amount=0};Totals(data);Assert.Equal("1646",NativePayslipDesignService.Project(Profile(),Template(),data).Information.Single().Value);}
    [Fact]
    public void Zero_monthly_and_period_only_lines_display_the_saved_zero_without_earned_fallback()
    {var data=Data();data.Lines[1]=data.Lines[1] with{MonthlyAmount=0};data.Lines.Add(new("STAT_BONUS","Earning",0,100));Totals(data);Assert.Equal("0",NativePayslipDesignService.Project(Profile(),Template(),data).Information.Single().Value);}
    [Fact]
    public void Missing_saved_code_rejects_even_when_master_information_has_a_value()
    {var data=Data();data.Lines.RemoveAll(l=>l.Code=="STAT_BONUS");Totals(data);Assert.Contains("missing",Assert.Throws<InvalidOperationException>(()=>NativePayslipDesignService.Project(Profile(),Template(),data)).Message);}
    [Theory]
    [InlineData(500)]
    [InlineData(1646)]
    public void Multiple_nonzero_monthly_bases_reject_instead_of_sum_or_first_match(decimal other)
    {var data=Data();data.Lines.Add(new("STAT_BONUS","Earning",other,0));Assert.Contains("multiple",Assert.Throws<InvalidOperationException>(()=>NativePayslipDesignService.Project(Profile(),Template(),data)).Message);}
    [Fact]
    public void Same_code_in_conflicting_categories_is_ambiguous()
    {var data=Data();data.Lines.Add(new("STAT_BONUS","Deduction",0,0));Assert.Contains("categories",Assert.Throws<InvalidOperationException>(()=>NativePayslipDesignService.Project(Profile(),Template(),data)).Message);}
    [Fact]
    public void Saved_code_matching_is_exact_and_does_not_infer_an_alias()
    {var profile=Profile();profile.Fields[0].InfoToken="COMPONENT_MONTHLY:stat_bonus";Assert.Throws<InvalidOperationException>(()=>NativePayslipDesignService.Project(profile,Template(),Data()));}
    [Fact]
    public void Signed_saved_monthly_information_is_not_clamped()
    {var data=Data();data.Lines[1]=data.Lines[1] with{MonthlyAmount=-1646};Assert.Equal("-1646",NativePayslipDesignService.Project(Profile(),Template(),data).Information.Single().Value);}
    [Fact]
    public void Writer_disabled_rejects_new_token_without_restricting_saved_rendering()
    {Assert.Throws<InvalidOperationException>(()=>NativePayslipMonthlyComponentInformation.RequireWriter(Profile().Fields,false));Assert.Equal("1646",NativePayslipDesignService.Project(Profile(),Template(),Data()).Information.Single().Value);}
    [Fact]
    public void New_writer_is_disabled_when_configuration_has_no_explicit_capability()
    {var configuration=new ConfigurationBuilder().Build();Assert.False(configuration.GetValue<bool>(NativePayslipMonthlyComponentInformation.WriterFlag));Assert.Throws<InvalidOperationException>(()=>NativePayslipMonthlyComponentInformation.RequireWriter(Profile().Fields,configuration.GetValue<bool>(NativePayslipMonthlyComponentInformation.WriterFlag)));}
    [Fact]
    public void A_hidden_new_token_is_still_an_incompatible_write_until_enabled()
    {var fields=Profile().Fields;fields[0].Visible=false;Assert.Throws<InvalidOperationException>(()=>NativePayslipMonthlyComponentInformation.RequireWriter(fields,false));}
    [Fact]
    public void Existing_information_only_writes_are_unchanged_when_new_writer_is_off()
    {NativePayslipMonthlyComponentInformation.RequireWriter([new(){Kind="info",InfoToken="SAVED_MONTHLY_GROSS"}],false);}
    [Fact]
    public void Explicit_writer_enable_allows_the_new_presentation_choice()
    {NativePayslipMonthlyComponentInformation.RequireWriter(Profile().Fields,true);}
    [Fact]
    public void Disabled_catalogue_preserves_the_exact_legacy_information_list()
    {
        const string expected="[\"DESIGNATION\",\"DEPARTMENT\",\"GRADE\",\"WORK_LOCATION\",\"JOINING_DATE\",\"EMAIL\",\"MOBILE\",\"UAN\",\"ESIC\",\"PAN\",\"AADHAAR\",\"BANK_ACCOUNT\",\"BANK_NAME\",\"IFSC\",\"ADDRESS\",\"SAVED_MONTHLY_GROSS\",\"CURRENT_ANNUAL_CTC\",\"PAYROLL_DAYS\",\"PRESENT_DAYS\",\"PAYABLE_DAYS\"]";
        Assert.Equal(expected,JsonSerializer.Serialize(NativePayslipMonthlyComponentInformation.CatalogueTokens(Codes(),false),Json));
    }
    [Fact]
    public void Enabled_catalogue_extends_existing_options_deterministically()
    {var values=NativePayslipMonthlyComponentInformation.CatalogueTokens(Codes(),true);Assert.Equal(NativePayslipDesignService.InfoTokens,values.Take(20));Assert.Contains(Token,values);Assert.Equal(values.Skip(20).Order(StringComparer.Ordinal),values.Skip(20));}
    [Fact]
    public void Selected_code_must_exist_once_in_the_active_catalogue()
    {NativePayslipMonthlyComponentInformation.ValidateCatalogue(Profile().Fields,Codes());Assert.Throws<InvalidOperationException>(()=>NativePayslipMonthlyComponentInformation.ValidateCatalogue(Profile().Fields,Codes().Where(c=>c.Code!="STAT_BONUS").ToArray()));}
    [Fact]
    public void Duplicate_catalogue_code_is_rejected_and_not_offered()
    {
        var codes=Codes().Append(new("STAT_BONUS","Other bonus","Earning")).ToArray();Assert.Throws<InvalidOperationException>(()=>NativePayslipMonthlyComponentInformation.ValidateCatalogue(Profile().Fields,codes));Assert.DoesNotContain(Token,NativePayslipMonthlyComponentInformation.CatalogueTokens(codes,true));
    }
    [Fact]
    public void Unsupported_catalogue_category_is_rejected()
    {Assert.Throws<InvalidOperationException>(()=>NativePayslipMonthlyComponentInformation.ValidateCatalogue([Info()],[new("STAT_BONUS","Summary","Summary")]));}
    [Theory]
    [InlineData("COMPONENT_MONTHLY:")]
    [InlineData("COMPONENT_MONTHLY:A+B")]
    [InlineData("COMPONENT_MONTHLY:STAT_BONUS:OTHER")]
    [InlineData("component_monthly:STAT_BONUS")]
    public void Malformed_token_does_not_become_an_expression_or_master_lookup(string token)
    {var profile=Profile();profile.Fields[0].InfoToken=token;Assert.Throws<InvalidOperationException>(()=>NativePayslipDesignService.ValidateProfile(profile,Template()));}
    [Fact]
    public void Supplemental_master_field_cannot_be_repurposed_as_monthly_component()
    {var profile=Profile();profile.Fields[0].SourceColumn=1506;profile.Fields[0].Label="Annual CTC";Assert.Throws<InvalidOperationException>(()=>NativePayslipDesignService.ValidateProfile(profile,Template()));}
    [Fact]
    public void Sample_preview_accepts_real_catalogue_code_and_keeps_source_labels_and_kinds()
    {
        var fields=Profile().Fields;var original=JsonSerializer.Deserialize<List<PayslipDesignField>>(JsonSerializer.Serialize(fields,Json),Json)!;original[0].InfoToken="";
        var request=new PayslipDesignSaveRequest{Template=Template(),Fields=fields};PayslipDesignSamplePreview.Validate(request,original,Codes());
        var row=PayslipDesignSamplePreview.CreateRow(request,original);Assert.Equal("Bonus (monthly)",row.Information.Single().Label);Assert.Equal("Sample monthly amount",row.Information.Single().Value);
        request.Fields[0].Label="Different label";Assert.Throws<InvalidOperationException>(()=>PayslipDesignSamplePreview.Validate(request,original,Codes()));
    }
    [Fact]
    public void Legacy_profile_wire_payload_has_no_added_feature_fields()
    {
        var profile=new PayslipDesignProfile{ClientId=61,TemplateId=91,Fields=[new(){SourceColumn=4,Label="Designation",InfoToken="DESIGNATION"}]};
        const string expected="{\"version\":1,\"clientId\":61,\"templateId\":91,\"sourceBatchId\":\"\",\"sourceHash\":\"\",\"simpleLayout\":false,\"keepMappedZeroRows\":true,\"amountDecimalPlaces\":0,\"fields\":[{\"sourceColumn\":4,\"label\":\"Designation\",\"kind\":\"info\",\"nativeCode\":\"\",\"infoToken\":\"DESIGNATION\",\"visible\":true}],\"requestId\":\"\",\"requestHash\":\"\",\"reviewedBy\":0}";
        Assert.Equal(expected,JsonSerializer.Serialize(profile,Json));NativePayslipDesignService.ValidateProfile(JsonSerializer.Deserialize<PayslipDesignProfile>(expected,Json)!,Template());
    }
}
