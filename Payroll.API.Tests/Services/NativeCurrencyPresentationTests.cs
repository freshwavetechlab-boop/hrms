using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class NativeCurrencyPresentationTests
{
    private static (PayslipDesignTemplate Template,PayslipDesignProfile Profile) Design()=>
        (new(){Id=55,ClientId=11,Theme=NativePayslipDesignService.Theme},new(){TemplateId=55,ClientId=11,AmountDecimalPlaces=0,
            SourceBatchId=new string('a',32),SourceHash=new string('b',64),Fields=[
                new(){SourceColumn=0,Kind="info",Label="Monthly reference",InfoToken="SAVED_MONTHLY_GROSS"},
                new(){SourceColumn=1,Kind="earning",Label="Wages",NativeCode="WAGES"},
                new(){SourceColumn=2,Kind="deduction",Label="PF",NativeCode="PF"},
                new(){SourceColumn=3,Kind="employer",Label="Employer PF",NativeCode="EPF_ER"}]});
    private static T Copy<T>(T value)=>JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    [Fact]
    public void Cosmetic_Information_audit_visibility_and_order_edits_do_not_change_financial_semantics()
    {
        var (template,profile)=Design();var expected=NativeCurrencyPresentation.Hash(template,profile,"2026-09");
        template.Name="Another title";template.Note="Another footer";template.ShowLogo=false;template.ShowClient=false;template.ShowYtd=true;template.ShowBank=false;
        profile.RequestId=Guid.NewGuid().ToString();profile.RequestHash=new string('c',64);profile.ReviewedBy=99;profile.KeepMappedZeroRows=false;
        profile.Fields.Reverse();foreach(var field in profile.Fields)field.Visible=false;
        profile.Fields.Single(f=>f.Kind=="info").InfoToken="PAN";
        Assert.Equal(expected,NativeCurrencyPresentation.Hash(template,profile,"2026-09"));
    }
    [Fact]
    public void Another_scope_month_or_same_schema_future_source_does_not_rebind_the_original_financial_source()
    {
        var (template,profile)=Design();var expected=NativeCurrencyPresentation.Hash(template,profile,"2026-09");
        profile.PayPeriods=["2026-09","2026-10"];profile.PreviousClassicTemplate=Copy(template);profile.PreviousClassicTemplate.Theme="Classic";
        profile.SourceBatchId=new string('d',32);profile.SourceHash=new string('e',64);
        Assert.Equal(expected,NativeCurrencyPresentation.Hash(template,profile,"2026-09"));
        profile.PayPeriods=["2026-10"];Assert.NotEqual(expected,NativeCurrencyPresentation.Hash(template,profile,"2026-09"));
    }
    [Theory]
    [InlineData("column")][InlineData("label")][InlineData("kind")][InlineData("code")][InlineData("precision")][InlineData("template")][InlineData("client")]
    public void Financial_mapping_precision_template_or_client_change_is_not_cosmetic(string change)
    {
        var (template,profile)=Design();var expected=NativeCurrencyPresentation.Hash(template,profile,"2026-09");
        switch(change)
        {
            case "column":profile.Fields[1].SourceColumn=4;break;
            case "label":profile.Fields[1].Label="Changed wages";break;
            case "kind":profile.Fields[1].Kind="employer";break;
            case "code":profile.Fields[1].NativeCode="OTHER_WAGES";break;
            case "precision":profile.AmountDecimalPlaces=2;break;
            case "template":profile.TemplateId=template.Id=56;break;
            case "client":profile.ClientId=template.ClientId=12;break;
        }
        Assert.NotEqual(expected,NativeCurrencyPresentation.Hash(template,profile,"2026-09"));
    }
}
