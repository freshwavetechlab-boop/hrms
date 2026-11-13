using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipProfileSyncTests
{
    private static ExcelPayslipBatch Batch() => new() { Id=new string('a',32),ClientId=9,Month="2026-10",Rows=[new() {
        Id="profile-row",SourceRow=5,EmployeeCode="SYN-1",EmployeeName="Synthetic Employee",Email="source@example.test",
        Information=[new(){Label="PAN No. (AF)",Value="ABCDE1234F"},new(){Label="UAN",Value=""}],
        Earnings=[new(){Label="Basic",Amount=1234.567m}],Deductions=[new(){Label="PF",Amount=34.567m}],NetPay=1200m }] };
    private static ExcelPayslipRepository.EmploymentMaster Master(int id=7) => new() { Id=id,ClientId=9,EmployeeCode="SYN-1",FirstName="Synthetic",LastName="Employee",
        Pan="ABCDE1234F",IsActive=true,Eligible=true,Designation="Supervisor",AnnualCtc=480000,
        PersonalDetails=new(){PanNumber="ABCDE1234F",UanNumber="001234567890",Mobile="9000000001"} };
    private static ExcelPayslipCalculationSource Source() => new() { SheetName="Salary",HeaderRow=4,ColumnCount=2,
        Columns=[new(){ColumnIndex=0,SourceHeader="PAN No.",Kind="info"},new(){ColumnIndex=1,SourceHeader="Basic",Kind="earning"}],
        Rows=[new(){SourceRow=5,Cells=[new(){Value="ABCDE1234F"},new(){Value="1234.567",FormulaText="B4/26*25"}]}] };
    private static ExcelPayslipProfileLink Link(ExcelPayslipBatch batch,ExcelPayslipCalculationSource? source,int employee=7) =>
        new(batch.Rows[0].Id,batch.Rows[0].SourceRow,employee,ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,batch.Rows[0],source));

    [Fact]
    public void Unique_source_code_and_mapped_full_identity_project_profile_without_calculating_salary()
    {
        var batch=Batch();var source=Source();var master=Master();var original=JsonSerializer.Serialize(new{batch.Rows[0].Information,batch.Rows[0].Earnings,batch.Rows[0].Deductions,batch.Rows[0].NetPay,source});
        ExcelPayslipRepository.AnnotateEmployment(batch,source,[master],[]);
        ExcelPayslipRepository.AnnotateCurrentProfiles(batch,source,[master]);
        var details=batch.Rows[0].MasterDetails!;
        Assert.True(details.Linked);Assert.True(details.ProfileOnly);Assert.Equal(7,details.EmployeeId);
        Assert.Equal("001234567890",details.Fields["uan"]);Assert.Equal("9000000001",details.Fields["phone"]);
        Assert.Null(details.Salary);Assert.Empty(details.SalaryInputs);
        Assert.Equal(original,JsonSerializer.Serialize(new{batch.Rows[0].Information,batch.Rows[0].Earnings,batch.Rows[0].Deductions,batch.Rows[0].NetPay,source}));
    }

    [Fact]
    public void Reviewed_profile_link_survives_master_identifier_and_name_corrections()
    {
        var batch=Batch();var source=Source();batch.ProfileLinks=[Link(batch,source)];
        var master=Master();master.EmployeeCode="CURRENT-2";master.FirstName="Corrected";master.Uan="009876543210";
        master.PersonalDetails.UanNumber=master.Uan;master.PersonalDetails.PanNumber="FGHIJ5678K";master.Pan=master.PersonalDetails.PanNumber;
        ExcelPayslipRepository.AnnotateEmployment(batch,source,[master],[]);
        ExcelPayslipRepository.AnnotateCurrentProfiles(batch,source,[master]);
        Assert.Equal(7,batch.Rows[0].Employment!.EmployeeId);Assert.False(batch.Rows[0].Employment!.IdentityReviewRequired);
        Assert.Equal("Corrected Employee",batch.Rows[0].MasterDetails!.EmployeeName);
        Assert.Equal("009876543210",batch.Rows[0].MasterDetails!.Fields["uan"]);
        Assert.Equal("SYN-1",batch.Rows[0].EmployeeCode);Assert.Equal("Synthetic Employee",batch.Rows[0].EmployeeName);
        Assert.Equal("",batch.Rows[0].Information.Single(i=>i.Label=="UAN").Value);
    }

    [Theory]
    [InlineData("other-client")][InlineData("only-name")][InlineData("code-conflict")][InlineData("strong-conflict")]
    [InlineData("duplicate-pan")][InlineData("duplicate-source-code")][InlineData("conflicting-mapped-pan")]
    [InlineData("ignored-only-pan")]
    public void Auto_profile_projection_requires_unambiguous_mapped_identity(string change)
    {
        var batch=Batch();var source=Source();var master=Master();var masters=new List<ExcelPayslipRepository.EmploymentMaster>{master};
        if(change=="other-client")master.ClientId=10;
        if(change=="only-name"){batch.Rows[0].EmployeeCode="OTHER";batch.Rows[0].Information=[];}
        if(change=="code-conflict"){var other=Master(8);other.Pan="FGHIJ5678K";masters.Add(other);}
        if(change=="strong-conflict")master.Pan="FGHIJ5678K";
        if(change=="duplicate-pan"){var other=Master(8);other.EmployeeCode="SYN-2";masters.Add(other);}
        if(change=="duplicate-source-code")batch.Rows.Add(new(){Id="second",SourceRow=6,EmployeeCode="SYN-1",EmployeeName="Synthetic Employee",NetPay=1});
        if(change=="conflicting-mapped-pan")batch.Rows[0].Information.Add(new(){Label="PAN",Value="FGHIJ5678K"});
        if(change=="ignored-only-pan")batch.Rows[0].Information=[];
        Assert.Empty(ExcelPayslipRepository.ProfileBindings(batch,source,masters));
    }

    [Fact]
    public void Reviewed_split_source_parts_can_share_one_master_without_merging_pay()
    {
        var batch=Batch();var second=JsonSerializer.Deserialize<ExcelPayslipRow>(JsonSerializer.Serialize(batch.Rows[0]))!;
        second.Id="second-part";second.SourceRow=6;second.NetPay=400;batch.Rows.Add(second);
        batch.ProfileLinks=batch.Rows.Select(row=>new ExcelPayslipProfileLink(row.Id,row.SourceRow,7,ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,row,null))).ToList();
        ExcelPayslipRepository.AnnotateCurrentProfiles(batch,null,[Master()]);
        Assert.All(batch.Rows,row=>Assert.Equal(7,row.Employment!.EmployeeId));
        Assert.Equal(new[]{1200m,400m},batch.Rows.Select(row=>row.NetPay));
        Assert.All(batch.Rows,row=>Assert.True(row.MasterDetails!.ProfileOnly));
    }

    [Theory]
    [InlineData("row")][InlineData("source-row")][InlineData("month")][InlineData("source-identity")][InlineData("foreign-employee")][InlineData("duplicate-link")]
    public void Stale_or_foreign_reviewed_links_cannot_replace_independent_identity_proof(string change)
    {
        var batch=Batch();batch.Rows[0].Information=[];var source=Source();batch.ProfileLinks=[Link(batch,source)];var master=Master();
        if(change=="row")batch.Rows[0].Id="new-row";
        if(change=="source-row")batch.Rows[0].SourceRow=6;
        if(change=="month")batch.Month="2026-11";
        if(change=="source-identity")source.Rows[0].Cells[0]!.Value="FGHIJ5678K";
        if(change=="foreign-employee")master.ClientId=10;
        if(change=="duplicate-link")batch.ProfileLinks.Add(batch.ProfileLinks[0]);
        Assert.Empty(ExcelPayslipRepository.ProfileBindings(batch,source,[master]));
    }

    [Fact]
    public void Profile_witness_excludes_pay_cells_but_tracks_captured_identity_and_batch_scope()
    {
        var batch=Batch();var source=Source();var hash=ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,batch.Rows[0],source);
        source.Rows[0].Cells[1]!.Value="9999";source.Rows[0].Cells[1]!.FormulaText="B4/26*12";batch.Rows[0].NetPay=9999;
        Assert.Equal(hash,ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,batch.Rows[0],source));
        source.Rows[0].Cells[0]!.Value="FGHIJ5678K";
        Assert.NotEqual(hash,ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,batch.Rows[0],source));
    }

    [Fact]
    public void Profile_associations_do_not_invalidate_existing_payroll_setup_source_receipts()
    {
        var batch=Batch();var source=Source();var legacy=ExcelPayrollSetupRepository.Hash(JsonSerializer.Serialize(new{Batch=batch,Source=source},new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(legacy,ExcelPayslipRepository.EmploymentSourceHash(batch,source));
        batch.ProfileLinks=[Link(batch,source)];
        Assert.Equal(legacy,ExcelPayslipRepository.EmploymentSourceHash(batch,source));
    }

    [Fact]
    public void Public_save_cannot_nominate_a_master_without_independent_or_saved_proof()
    {
        var batch=Batch();batch.Rows[0].Information=[];var master=Master();
        batch.ProfileLinks=[Link(batch,null)];
        Assert.Empty(ExcelPayslipRepository.SaveProfileBindings(batch,null,[master],null));
        var saved=JsonSerializer.Deserialize<ExcelPayslipBatch>(JsonSerializer.Serialize(batch))!;
        Assert.Single(ExcelPayslipRepository.SaveProfileBindings(batch,null,[master],saved));
        saved.ClientId=10;
        Assert.Empty(ExcelPayslipRepository.SaveProfileBindings(batch,null,[master],saved));
    }

    [Fact]
    public void Repeated_placeholder_identifier_is_not_automatic_identity_evidence()
    {
        var batch=Batch();batch.Rows[0].Information=[new(){Label="UAN",Value="000000000000"}];
        var master=Master();master.Pan="";master.Uan="000000000000";
        Assert.Empty(ExcelPayslipRepository.ProfileBindings(batch,null,[master]));
    }

    [Fact]
    public void Profile_only_blank_master_email_keeps_existing_source_email_editing_available()
    {
        var row=Batch().Rows[0];row.Employment=new(7,"SYN-1","Active",null,false,false);
        row.MasterDetails=new(7,"Analyst"){Linked=true,ProfileOnly=true,Email=""};
        Assert.True(ExcelPayslipRepository.CanUpdateSourceEmail(row,"source-update@example.test"));
        row.MasterDetails=row.MasterDetails with{Email="current@example.test"};
        Assert.False(ExcelPayslipRepository.CanUpdateSourceEmail(row,"source-update@example.test"));
        Assert.True(ExcelPayslipRepository.CanUpdateSourceEmail(row,"current@example.test"));
        row.MasterDetails=row.MasterDetails with{ProfileOnly=false,Email=""};
        Assert.False(ExcelPayslipRepository.CanUpdateSourceEmail(row,"source-update@example.test"));
        row.MasterDetails=null;Assert.True(ExcelPayslipRepository.CanUpdateSourceEmail(row,"source-update@example.test"));
    }

    [Fact]
    public void Applied_UAN_fill_keeps_salary_when_receipt_inputs_and_rules_still_match()
    {
        var batch=Batch();var row=batch.Rows[0];var master=Master();master.AnnualCtc=120000;master.SalaryStructureId="t";master.SalaryComponents=new(){["basic"]=10000};
        var source=new ExcelPayslipCalculationSource{SheetName="Salary",ColumnCount=3,Rows=[new(){SourceRow=row.SourceRow,Cells=[new(){Value="10000"},new(){Value="10000"},new(){Value="10000"}]}]};
        var definition=new ExcelPayrollSetupComponent{Code="BASIC",Name="Basic",CalculationType="Fixed Amount",Value="0",SourceColumnIndex=1,ExpectedColumnIndex=2,PayType="Fixed Pay"};
        var request=new ExcelPayrollSetupRequest{BatchId=batch.Id,RowIds=[row.Id],MonthlyCtcColumnIndex=0,Components=[definition]};
        var result=new ExcelPayrollSetupResult{ImportId=new string('b',32),EmployeeIds=[master.Id],
            Transfer=new(){SourceHash="captured",Components=[new("BASIC","Earning","Reuse","basic")],Templates=[new(){TemplateId="t",RowIds=[row.Id]}]},
            TransferRows=[new(row.Id,master.Id,30,30,30,120000,[])]};
        const string setup="""{"salaryComponents":[{"id":"basic","code":"BASIC","name":"Basic","category":"Earning","componentRole":"Regular Earning","statutoryType":"None","calculationType":"Fixed Amount","value":"0","formula":"","baseComponent":"","proRata":true,"taxable":true,"active":true,"priority":"100","payType":"Fixed Pay"}],"salaryStructures":[{"id":"t","lines":[{"componentId":"basic","calculationType":"Fixed Amount","value":"0","formula":"","baseComponent":"","proRataOverride":true,"roundingMode":""}]}]}""";
        master.PersonalDetails.UanNumber="";
        var link=new ExcelPayslipMasterLink(row.Id,master.Id,row.SourceRow,"",ExcelPayslipMasterSynchronization.SalaryHash(master,setup),30,30,30,false){SourceHash="captured"};
        var binding=new ExcelPayslipRepository.MasterSyncBinding(row,link,request,result,batch,source);
        master.PersonalDetails.UanNumber="001234567890";master.FirstName="Corrected";
        Assert.NotEqual(link.SalaryHash,ExcelPayslipMasterSynchronization.SalaryHash(master,setup));
        Assert.True(ExcelPayslipRepository.MasterSalaryUnchanged(master,setup,binding));
        Assert.Equal(1200,row.NetPay);
        master.AnnualCtc=240000;Assert.False(ExcelPayslipRepository.MasterSalaryUnchanged(master,setup,binding));master.AnnualCtc=120000;
        master.SalaryStructureId="other";Assert.False(ExcelPayslipRepository.MasterSalaryUnchanged(master,setup,binding));master.SalaryStructureId="t";
        Assert.False(ExcelPayslipRepository.MasterSalaryUnchanged(master,setup.Replace("\"formula\":\"\"","\"formula\":\"BASIC * 2\""),binding));
        master.PersonalDetails.Tds=100;Assert.False(ExcelPayslipRepository.MasterSalaryUnchanged(master,setup,binding));
    }
}
