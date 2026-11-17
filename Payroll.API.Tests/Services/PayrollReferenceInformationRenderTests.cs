using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class PayrollReferenceInformationRenderTests
{
    private static (PayslipDesignProfile Profile,PayslipDesignTemplate Template,NativePayslipDesignData Data) Inputs()=>
        (new(){ClientId=97790,TemplateId=97790,AmountDecimalPlaces=2,Fields=[
            new(){SourceColumn=3,Label="Current CTC (source)",Kind="info",InfoToken="SAVED_MONTHLY_GROSS"},
            new(){SourceColumn=6,Label="Saved earned gross",Kind="info",InfoToken="SAVED_MONTHLY_GROSS"},
            new(){SourceColumn=4,Label="Salary",Kind="earning",NativeCode="WAGE"}]},
        new(){Id=97790,ClientId=97790},
        new(){ClientId=97790,EmployeeId=9779002,NativeRowId=977901,Month="2026-09",Status="Approved",Gross=22500,Deductions=0,NetPay=22500,
            Information=new(){{"SAVED_MONTHLY_GROSS","22500"}},Lines=[new("WAGE","Earning",22500,22500)]});
    [Theory][InlineData(25000,"25000")][InlineData(33.335,"33.335")][InlineData(0,"0")]
    public void Reference_overlay_changes_only_exact_source_column_and_preserves_full_info_precision_and_all_financial_data(decimal value,string display)
    {
        var (profile,template,data)=Inputs();var nativeBefore=JsonSerializer.Serialize(data);var profileBefore=JsonSerializer.Serialize(profile);
        var row=NativePayslipDesignService.ProjectWithFrozenCurrency(profile,template,data,null);
        var financialBefore=JsonSerializer.Serialize(new{row.Earnings,row.Deductions,row.EmployerContributions,row.NetPay,row.DeclaredGross,row.DeclaredDeductions});
        PayslipDesignRepository.ApplyFrozenReferenceInformation(profile,template,data,row,new(PayrollReferenceStatus.Resolved,3,"Current CTC (source)",PayrollReferenceUnit.Amount,value,new string('a',64)));
        Assert.Equal(display,row.Information[0].Value);Assert.Equal("22500",row.Information[1].Value);
        Assert.Equal(display,ExcelPayslipPdfService.InformationValue(row.Information[0],row,2));
        Assert.Equal(financialBefore,JsonSerializer.Serialize(new{row.Earnings,row.Deductions,row.EmployerContributions,row.NetPay,row.DeclaredGross,row.DeclaredDeductions}));
        Assert.Equal(nativeBefore,JsonSerializer.Serialize(data));Assert.Equal(profileBefore,JsonSerializer.Serialize(profile));
    }
    [Fact]
    public void Other_period_absence_preserves_projected_row_byte_for_byte_and_missing_never_defaults_to_zero()
    {
        var (profile,template,data)=Inputs();var row=NativePayslipDesignService.Project(profile,template,data);var before=JsonSerializer.Serialize(row);
        PayslipDesignRepository.ApplyFrozenReferenceInformation(profile,template,data,row,new(PayrollReferenceStatus.NotApplicable,null,null,null,null,""));Assert.Equal(before,JsonSerializer.Serialize(row));
        Assert.Throws<InvalidOperationException>(()=>PayslipDesignRepository.ApplyFrozenReferenceInformation(profile,template,data,row,new(PayrollReferenceStatus.Missing,3,"Current CTC (source)",PayrollReferenceUnit.Amount,null,new string('a',64))));
        Assert.Equal(before,JsonSerializer.Serialize(row));
    }
    [Fact]
    public void Existing_hidden_reference_field_stays_hidden_and_never_replaces_another_same_token_field()
    {
        var (profile,template,data)=Inputs();profile.Fields[0].Visible=false;var row=NativePayslipDesignService.Project(profile,template,data);
        PayslipDesignRepository.ApplyFrozenReferenceInformation(profile,template,data,row,new(PayrollReferenceStatus.Resolved,3,"Current CTC (source)",PayrollReferenceUnit.Amount,25000,new string('a',64)));
        Assert.Equal("Saved earned gross",Assert.Single(row.Information).Label);Assert.Equal("22500",row.Information[0].Value);
    }
    [Theory][InlineData("settings")][InlineData("reports")][InlineData("foreign")][InlineData("singleForeign")]
    public async Task Real_budget_preview_requires_both_financial_read_and_configuration_rights_in_exact_client_scope(string mode)
    {
        var cfg=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:Default"]="not-a-database-connection",["IntegrationCredentialEncryption:MasterKey"]="synthetic-reference-auth-no-connection",["PayrollReferenceInformation:Enabled"]="true"}).Build();
        var protector=new PortableIntegrationCredentialProtector(cfg,new EphemeralDataProtectionProvider(),NullLogger<PortableIntegrationCredentialProtector>.Instance);
        var actor=new AuthUser{Id=97790,IsActive=true,Permissions=mode=="settings"?["settings.manage"]:mode=="reports"?["reports.payroll.view"]:["settings.manage","reports.payroll.view"],
            ClientAccessMode=mode=="singleForeign"?"Single":"Selected",ClientId=mode=="singleForeign"?97791:null,AssignedClientIds=mode=="foreign"?[97791]:[97790]};
        Assert.False(PayslipDesignRepository.CanManageReferenceInformation(actor,97790));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>new PayslipDesignRepository(cfg,protector,new()).PreviewReferenceInformationAsync(new(97790,1,"2026-09",1,new string('1',32),new string('2',32),3),actor));
    }
}
