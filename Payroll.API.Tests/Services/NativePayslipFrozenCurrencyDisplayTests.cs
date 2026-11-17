using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PdfSharp.Pdf.IO;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class NativePayslipFrozenCurrencyDisplayTests
{
    private static readonly string Hash = new('a', 64);
    private static PayslipDesignTemplate Template() => new() { Id = 71, ClientId = 7 };
    private static PayslipDesignProfile Profile() => new()
    {
        ClientId = 7, TemplateId = 71, SimpleLayout = true, KeepMappedZeroRows = false, AmountDecimalPlaces = 0,
        Fields = [
            new() { SourceColumn=0, Label="UAN", InfoToken="UAN" },
            new() { SourceColumn=1, Label="Esic No", InfoToken="ESIC" },
            new() { SourceColumn=2, Label="Designation", InfoToken="DESIGNATION" },
            new() { SourceColumn=3, Label="Number of Working Days Attended", InfoToken="PAYABLE_DAYS" },
            new() { SourceColumn=4, Label="Account No", InfoToken="BANK_ACCOUNT" },
            new() { SourceColumn=5, Label="IFSC Code", InfoToken="IFSC" },
            new() { SourceColumn=12, Label="Earn Salary", Kind="earning", NativeCode="WAGES" },
            new() { SourceColumn=13, Label="PF @12%", Kind="deduction", NativeCode="PF" },
            new() { SourceColumn=14, Label="ESIC @0.75%", Kind="deduction", NativeCode="ESI" },
            new() { SourceColumn=17, Label="P.F @13%", Kind="employer", NativeCode="PF_ER" },
            new() { SourceColumn=18, Label="ESIC @3.25%", Kind="employer", NativeCode="ESI_ER" }
        ]
    };
    private static NativePayslipDesignData Data() => new()
    {
        NativeRowId=91, ClientId=7, EmployeeId=11, EmployeeCode="SYN-11", EmployeeName="Synthetic employee",
        ClientName="Synthetic client", Month="2026-09", Status="Approved", Gross=8220.80m, Deductions=1048.16m, NetPay=7172.64m,
        Information=new() { ["UAN"]="100000000001", ["ESIC"]="1234567890", ["DESIGNATION"]="Assistant", ["PAYABLE_DAYS"]="16", ["BANK_ACCOUNT"]="0012345678", ["IFSC"]="SYN00000001" },
        Lines=[new("WAGES","Earning",15414,8220.80m), new("PF","Deduction",986.50m,986.50m), new("ESI","Deduction",61.66m,61.66m), new("PF_ER","Benefit",1068.70m,1068.70m), new("ESI_ER","Benefit",267.18m,267.18m)]
    };
    private static NativeCurrencyFrozenLine Line(string code, string category, decimal monthlyRaw, decimal raw,
        decimal monthly, decimal earned, string path="Monthly") => new(code, category, monthlyRaw, raw, monthly, earned, path, 12, Hash);
    private static NativeCurrencyFrozenRow Frozen() => new(91,11,"source-row",529,Hash,Hash,Hash,Hash,Hash,
        [Line("WAGES","Earning",15414,8220.80m,15414,8220.80m,"Prorated"),
         Line("PF","Deduction",986.496m,986.496m,986.50m,986.50m),
         Line("ESI","Deduction",61.656m,61.656m,61.66m,61.66m),
         Line("PF_ER","Benefit",1068.704m,1068.704m,1068.70m,1068.70m),
         Line("ESI_ER","Benefit",267.176m,267.176m,267.18m,267.18m)]);
    private static string Content(byte[] bytes)
    {
        using var document=PdfReader.Open(new MemoryStream(bytes),PdfDocumentOpenMode.Import);
        Assert.Single(document.Pages.Cast<object>());
        return Encoding.Latin1.GetString(document.Pages[0].Contents.CreateSingleContent().Stream.UnfilteredValue);
    }

    [Fact]
    public void Actual_simple_pdf_matches_independent_raw_source_and_fixes_double_round_without_changing_accounting()
    {
        var data=Data(); var profile=Profile(); var template=Template(); var frozen=Frozen(); var before=JsonSerializer.Serialize(data);
        var projected=NativePayslipDesignService.ProjectWithFrozenCurrency(profile,template,data,frozen);
        var source=new ExcelPayslipRow
        {
            EmployeeCode="SYN-11", EmployeeName="Synthetic employee", NetPay=7172.648m,
            Information=[new(){Label="UAN",Value="100000000001"},new(){Label="Esic No",Value="1234567890"},new(){Label="Designation",Value="Assistant"},new(){Label="Number of Working Days Attended",Value="16"},new(){Label="Account No",Value="0012345678"},new(){Label="IFSC Code",Value="SYN00000001"}],
            Earnings=[new(){Label="Earn Salary",Amount=8220.80m}],
            Deductions=[new(){Label="PF @12%",Amount=986.496m},new(){Label="ESIC @0.75%",Amount=61.656m}],
            EmployerContributions=[new(){Label="P.F @13%",Amount=1068.704m},new(){Label="ESIC @3.25%",Amount=267.176m}]
        };
        var batch=new ExcelPayslipBatch { ClientId=7, ClientName="Synthetic client", Month="2026-09", SimpleLayout=true };
        var renderer=new ExcelPayslipPdfService();
        var sourceContent=Content(renderer.Create(batch,[source],amountDecimalPlaces:0));
        var nativeContent=Content(renderer.Create(batch,[projected],amountDecimalPlaces:0,presentation:NativePayslipDesignService.Presentation(profile,template,frozen)));
        Assert.Equal(sourceContent,nativeContent);
        Assert.Equal("986",ExcelPayslipPdfService.Amount(projected.Deductions[0].Amount,0));
        Assert.Equal("987",ExcelPayslipPdfService.Amount(data.Lines.Single(l=>l.Code=="PF").Amount,0));
        Assert.Equal(8220.80m,projected.DeclaredGross); Assert.Equal(1048.16m,projected.DeclaredDeductions); Assert.Equal(7172.64m,projected.NetPay);
        Assert.Equal(1335.88m,NativePayslipDesignService.Presentation(profile,template,frozen).NativeEmployerTotal);
        Assert.Equal(before,JsonSerializer.Serialize(data));
    }

    [Fact]
    public void Disabled_currency_path_is_identical_to_existing_projection_and_two_decimal_design_is_unchanged()
    {
        var profile=Profile(); profile.AmountDecimalPlaces=2; var data=Data();
        Assert.Equal(JsonSerializer.Serialize(NativePayslipDesignService.Project(profile,Template(),data)),
            JsonSerializer.Serialize(NativePayslipDesignService.ProjectWithFrozenCurrency(profile,Template(),data,null)));
        Assert.Null(NativePayslipDesignService.Presentation(profile,Template()).NativeEmployerTotal);
        Assert.Throws<InvalidOperationException>(()=>NativePayslipDesignService.ProjectWithFrozenCurrency(profile,Template(),data,Frozen()));
    }

    [Theory]
    [InlineData("row")][InlineData("employee")][InlineData("missing")][InlineData("duplicate")]
    [InlineData("earned")][InlineData("monthly")][InlineData("category")][InlineData("path")][InlineData("raw")]
    public void Changed_or_incomplete_frozen_accounting_fails_instead_of_silently_falling_back(string change)
    {
        var frozen=Frozen(); var first=frozen.Lines[0];
        frozen=change switch
        {
            "row" => frozen with {NativeRowId=92}, "employee" => frozen with {EmployeeId=12},
            "missing" => frozen with {Lines=frozen.Lines.RemoveAt(1)},
            "duplicate" => frozen with {Lines=frozen.Lines.SetItem(1,first)},
            "earned" => frozen with {Lines=frozen.Lines.SetItem(0,first with {AccountedEarned=8221})},
            "monthly" => frozen with {Lines=frozen.Lines.SetItem(0,first with {AccountedMonthly=15415})},
            "category" => frozen with {Lines=frozen.Lines.SetItem(0,first with {Category="Deduction"})},
            "path" => frozen with {Lines=frozen.Lines.SetItem(0,first with {AccountingPath="Unknown"})},
            _ => frozen with {Lines=frozen.Lines.SetItem(0,first with {UnroundedEarned=8221})}
        };
        Assert.Throws<InvalidOperationException>(()=>NativePayslipDesignService.ProjectWithFrozenCurrency(Profile(),Template(),Data(),frozen));
    }

    [Fact]
    public void Genuine_zero_and_negative_raw_monthly_clamp_remain_zero_and_hidden_fields_keep_full_native_totals()
    {
        var data=Data(); data.Gross=data.Deductions=data.NetPay=0;
        data.Lines=data.Lines.Select(line=>line with {MonthlyAmount=0,Amount=0}).ToList();
        var frozen=Frozen() with {Lines=Frozen().Lines.Select(line=>line with {MonthlyRaw=-0.004m,UnroundedEarned=-0.004m,AccountedMonthly=0,AccountedEarned=0,AccountingPath="Monthly"}).ToImmutableArray()};
        var row=NativePayslipDesignService.ProjectWithFrozenCurrency(Profile(),Template(),data,frozen);
        Assert.All(row.Earnings.Concat(row.Deductions).Concat(row.EmployerContributions),amount=>Assert.Equal(0,amount.Amount));
        Assert.Equal(0,row.NetPay);
        var profile=Profile(); profile.Fields.Single(field=>field.NativeCode=="PF").Visible=false;
        row=NativePayslipDesignService.ProjectWithFrozenCurrency(profile,Template(),Data(),Frozen());
        Assert.Single(row.Deductions); Assert.Equal(1048.16m,row.DeclaredDeductions); Assert.Equal(7172.64m,row.NetPay);
    }

    [Fact]
    public void Employer_footer_uses_native_accounting_total_even_when_raw_values_or_visibility_would_change_its_sum()
    {
        var profile=Profile(); profile.Fields.Single(field=>field.NativeCode=="ESI_ER").Visible=false;
        var frozen=Frozen(); var data=Data(); var row=NativePayslipDesignService.ProjectWithFrozenCurrency(profile,Template(),data,frozen);
        Assert.Single(row.EmployerContributions);
        var presentation=NativePayslipDesignService.Presentation(profile,Template(),frozen);
        Assert.Equal(1335.88m,presentation.NativeEmployerTotal);
        var bytes=new ExcelPayslipPdfService().Create(new(){ClientName="Synthetic client",Month="2026-09",SimpleLayout=true},[row],amountDecimalPlaces:0,presentation:presentation);
        Assert.Contains("1,336",Content(bytes));
    }
}
