using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipMasterSyncTests
{
    private static ExcelPayslipBatch Batch()=>new(){Id=new string('a',32),ClientId=9,Month="2026-09",Rows=[new(){Id="row",SourceRow=3,EmployeeCode="SYN-1",EmployeeName="Synthetic Employee",
        Information=[new(){Label="Designation_Manpower Category",Value="Operator"}],Earnings=[new(){Label="Basic",Amount=1234.567m}],NetPay=1234.567m}]};
    private static ExcelPayslipRepository.EmploymentMaster Master()=>new(){Id=7,ClientId=9,EmployeeCode="SYN-1",FirstName="Synthetic",LastName="Employee",IsActive=true,Eligible=true,Designation="Supervisor"};

    [Fact]
    public void CurrentDesignationIsReadOnlyAndRebuildsAfterTheMasterChanges()
    {
        var batch=Batch();var row=batch.Rows[0];var master=Master();var source=JsonSerializer.Serialize(row.Information);var salary=JsonSerializer.Serialize(new{row.Earnings,row.NetPay});
        ExcelPayslipRepository.AnnotateEmployment(batch,null,[master],[]);
        Assert.Equal("Supervisor",ExcelPayslipPresentation.Information(row).Single().Value);
        Assert.Equal("Supervisor",ExcelPayslipPdfService.SimpleInformation(batch,row).Single(field=>field.Label=="Designation").Value);
        master.Designation="Senior Supervisor";
        ExcelPayslipRepository.AnnotateEmployment(batch,null,[master],[]);
        Assert.Equal("Senior Supervisor",row.MasterDetails?.Designation);
        Assert.Equal(source,JsonSerializer.Serialize(row.Information));Assert.Equal(salary,JsonSerializer.Serialize(new{row.Earnings,row.NetPay}));
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void BothPdfLayoutsGenerateFromTheCurrentDesignationWithoutMutatingSalary(bool simple)
    {
        var batch=Batch();batch.SimpleLayout=simple;batch.ClientName="Synthetic client";
        ExcelPayslipRepository.AnnotateEmployment(batch,null,[Master()],[]);
        var before=JsonSerializer.Serialize(batch);
        var pdf=new ExcelPayslipPdfService().Create(batch,batch.Rows);
        Assert.True(pdf.Length>1000);Assert.Equal(before,JsonSerializer.Serialize(batch));
        Assert.Equal("Supervisor",ExcelPayslipPresentation.Information(batch.Rows[0]).Single().Value);
    }

    [Theory]
    [InlineData("foreign")][InlineData("name-conflict")][InlineData("duplicate-code")]
    public void UnverifiedOrCrossClientIdentityDoesNotSupplyDesignation(string change)
    {
        var batch=Batch();var master=Master();var masters=new List<ExcelPayslipRepository.EmploymentMaster>{master};
        if(change=="foreign")master.ClientId=10;
        if(change=="name-conflict")master.FirstName="Different";
        if(change=="duplicate-code"){var other=Master();other.Id=8;masters.Add(other);}
        ExcelPayslipRepository.AnnotateEmployment(batch,null,masters,[]);
        Assert.Null(batch.Rows[0].MasterDetails);
        Assert.Equal("Operator",ExcelPayslipPresentation.Information(batch.Rows[0]).Single().Value);
    }

    [Theory]
    [InlineData("")][InlineData("Unassigned")]
    public void UnconfiguredMasterKeepsTheSourceDesignation(string designation)
    {
        var batch=Batch();var master=Master();master.Designation=designation;
        ExcelPayslipRepository.AnnotateEmployment(batch,null,[master],[]);
        Assert.Equal("Operator",ExcelPayslipPresentation.Information(batch.Rows[0]).Single().Value);
    }

    [Fact]
    public void MissingSourceDesignationCanBeShownButForgedReadAnnotationsCannotOverrideIdentity()
    {
        var batch=Batch();var row=batch.Rows[0];row.Information=[];
        row.MasterDetails=new(7,"Forged");
        Assert.Empty(ExcelPayslipPresentation.Information(row));
        ExcelPayslipRepository.AnnotateEmployment(batch,null,[Master()],[]);
        Assert.Equal("Designation",ExcelPayslipPresentation.Information(row).Single().Label);
        Assert.Empty(row.Information);
    }

    [Fact]
    public void ClientConfigurationCompatibilityDoesNotDependOnTheSalaryMonthOrEmployeeRows()
    {
        var source=new ExcelPayslipCalculationSource{ColumnCount=2,Columns=[new(){ColumnIndex=0,SourceHeader="Basic",Kind="earning"},new(){ColumnIndex=1,SourceHeader="Net paid",Kind="netPay"}]};
        var next=JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(JsonSerializer.Serialize(source))!;
        next.Rows=[new(){SourceRow=20,Cells=[new(){Value="5000",FormulaText="A20/26*B20"}]}];
        Assert.True(ExcelPayrollSetupRepository.CompatibleConfigurationColumns(next,source));
        next.Columns[0].Kind="info";Assert.False(ExcelPayrollSetupRepository.CompatibleConfigurationColumns(next,source));
        next.Columns[0].Kind="earning";next.Columns[0].SourceHeader="Daily Rate";Assert.False(ExcelPayrollSetupRepository.CompatibleConfigurationColumns(next,source));
    }

    [Fact]
    public void AppliedLinkSurvivesMasterRenameAndProjectsAllFieldsWithoutRewritingTheWorkbook()
    {
        var batch=Batch();var row=batch.Rows[0];var master=Master();
        row.Email="old@example.test";row.Information.AddRange([new(){Label="Mobile No",Value="old"},new(){Label="Account No",Value="old-bank"},new(){Label="CTC",Value="12"}]);
        var original=JsonSerializer.Serialize(row);
        var request=new ExcelPayrollSetupRequest{BatchId=batch.Id,RowIds=[row.Id],EmployeeDecisions=[new(){RowId=row.Id,MatchedEmployeeId=master.Id}]};
        var result=new ExcelPayrollSetupResult{ImportId=new string('b',32),EmployeeIds=[master.Id],MasterLinks=[new(row.Id,master.Id,row.SourceRow,ExcelPayslipMasterSynchronization.IdentityHash(row),"initial",26,null,17,false)]};
        var link=Assert.Single(ExcelPayslipRepository.MasterBindings(batch,null,batch,null,request,result));
        master.EmployeeCode="CHANGED-2";master.FirstName="Renamed";master.WorkEmail="current@example.test";master.AnnualCtc=420000m;
        master.PersonalDetails.Mobile="9000000001";master.PaymentDetails.BankAccountNo="new-bank";
        row.Employment=new(master.Id,master.EmployeeCode,"Active",null,false,false);
        row.MasterDetails=ExcelPayslipMasterSynchronization.Details(master,"Current location",true);
        var before=JsonSerializer.Serialize(row);var current=ExcelPayslipPresentation.Current(row);
        Assert.Equal(master.Id,link.Link.EmployeeId);Assert.Equal("CHANGED-2",current.EmployeeCode);Assert.Equal("Renamed Employee",current.EmployeeName);
        Assert.Equal("current@example.test",current.Email);Assert.Equal("9000000001",current.Information.Single(f=>f.Label=="Mobile No").Value);
        Assert.Equal("new-bank",current.Information.Single(f=>f.Label=="Account No").Value);Assert.Equal("420000",current.Information.Single(f=>f.Label=="CTC").Value);
        Assert.Equal(before,JsonSerializer.Serialize(row));Assert.Contains("old@example.test",original);Assert.Equal("SYN-1",row.EmployeeCode);
        master.WorkEmail="";row.MasterDetails=ExcelPayslipMasterSynchronization.Details(master,"",true);
        Assert.Equal("",ExcelPayslipPresentation.Current(row).Email);
        var foreign=Batch();foreign.ClientId=10;Assert.Empty(ExcelPayslipRepository.MasterBindings(foreign,null,batch,null,request,result));
        result.MasterLinks[0]=result.MasterLinks[0] with {IdentityHash="forged"};
        Assert.Empty(ExcelPayslipRepository.MasterBindings(batch,null,batch,null,request,result));
    }

    [Fact]
    public void CurrentSalaryProjectionIsUsedForPdfSelectionAndBlocksIncompleteCalculations()
    {
        var batch=Batch();var row=batch.Rows[0];row.Employment=new(7,"SYN-1","Active",null,false,false);
        row.MasterDetails=ExcelPayslipMasterSynchronization.Details(Master(),"",true);
        row.MasterDetails.Salary=new("Updated","Current engine calculation",[new(){Label="Basic",Amount=15000}],[],[],15000,0,15000);
        var selection=ExcelPayslipRepository.SelectRows(batch,new(){RowIds=[row.Id],AmountDecimalPlaces=0,AcknowledgeWarnings=true});
        Assert.Null(selection.Error);Assert.Equal(15000,Assert.Single(selection.Rows).NetPay);Assert.Equal(1234.567m,row.NetPay);
        Assert.True(new ExcelPayslipPdfService().Create(batch,batch.Rows).Length>1000);
        row.MasterDetails.Salary=ExcelPayslipRepository.SyncReview("Actual present days required by the changed formula.");
        Assert.Empty(ExcelPayslipRepository.SelectRows(batch,new(){RowIds=[row.Id],AmountDecimalPlaces=0,AcknowledgeWarnings=true}).Rows);
        Assert.Throws<InvalidOperationException>(()=>new ExcelPayslipPdfService().Create(batch,batch.Rows));
    }

    [Fact]
    public void ACloneCanReuseAnAppliedLinkOnlyWhenItsCapturedCellsAndFormulasMatch()
    {
        var owner=Batch();var row=owner.Rows[0];var clone=JsonSerializer.Deserialize<ExcelPayslipBatch>(JsonSerializer.Serialize(owner))!;
        clone.Id=new string('c',32);clone.Rows[0].Id="copied-row";
        var source=new ExcelPayslipCalculationSource{SheetName="Salary",HeaderRow=2,ColumnCount=1,Rows=[new(){SourceRow=3,Cells=[new(){Value="1200",FormulaText="A2*2"}]}]};
        var request=new ExcelPayrollSetupRequest{BatchId=owner.Id,RowIds=[row.Id],EmployeeDecisions=[new(){RowId=row.Id,MatchedEmployeeId=7}]};
        var result=new ExcelPayrollSetupResult{ImportId=new string('b',32),EmployeeIds=[7],MasterLinks=[new(row.Id,7,row.SourceRow,ExcelPayslipMasterSynchronization.IdentityHash(row),"salary",26,null,17,false)]};
        Assert.Equal("copied-row",Assert.Single(ExcelPayslipRepository.MasterBindings(clone,source,owner,source,request,result)).Row.Id);
        var changed=JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(JsonSerializer.Serialize(source))!;changed.Rows[0].Cells[0]!.FormulaText="A2*3";
        Assert.Empty(ExcelPayslipRepository.MasterBindings(clone,changed,owner,source,request,result));
        changed.Rows[0].Cells[0]!.FormulaText="A2*2";changed.Rows[0].Cells[0]!.Value="1800";
        Assert.Empty(ExcelPayslipRepository.MasterBindings(clone,changed,owner,source,request,result));
    }

    [Fact]
    public void SalaryFingerprintTracksCtcInputsAndRulesButIgnoresContactChanges()
    {
        var master=Master();master.AnnualCtc=120000;master.SalaryStructureId="s";master.SalaryComponents=new(){["basic"]=10000};
        const string setup="""{"salaryComponents":[{"id":"basic","code":"BASIC","formula":"MONTHLY_CTC"}],"salaryStructures":[{"id":"s","lines":[{"componentId":"basic"}]}]}""";
        var hash=ExcelPayslipMasterSynchronization.SalaryHash(master,setup);
        master.WorkEmail="new@example.test";master.EmployeeCode="RENAME";master.PersonalDetails.Mobile="9000000000";
        Assert.Equal(hash,ExcelPayslipMasterSynchronization.SalaryHash(master,setup));
        master.AnnualCtc=240000;Assert.NotEqual(hash,ExcelPayslipMasterSynchronization.SalaryHash(master,setup));master.AnnualCtc=120000;
        master.SalaryComponents["basic"]=11000;Assert.NotEqual(hash,ExcelPayslipMasterSynchronization.SalaryHash(master,setup));master.SalaryComponents["basic"]=10000;
        Assert.NotEqual(hash,ExcelPayslipMasterSynchronization.SalaryHash(master,setup.Replace("MONTHLY_CTC","MONTHLY_CTC * 1.1")));
    }

    [Fact]
    public void CurrentCtcAndSalaryInputsUseTheNativeFormulaWithUnknownPresenceAndExtraDutyUnits()
    {
        var batch=Batch();var master=Master();master.SalaryStructureId="s";master.AnnualCtc=312000;
        var source=batch.Rows[0];var untouched=JsonSerializer.Serialize(source);
        var link=new ExcelPayslipMasterLink(source.Id,master.Id,source.SourceRow,"","",26,null,28,true){SourceBatchId=batch.Id,SourceHash=new string('a',64),ImportId=new string('b',32)};
        const string setup="""{"salaryComponents":[{"id":"basic","code":"BASIC","name":"Basic","active":true,"category":"Earning","calculationType":"Formula","formula":"MONTHLY_CTC / PAYROLL_DAYS * PAYABLE_DAYS","proRata":false}],"salaryStructures":[{"id":"s","clientId":"9","active":true,"lines":[{"componentId":"basic"}]}]}""";
        var first=PayRunRepository.CalculateMasterSalaryArithmetic(master,setup,batch,link);Assert.Equal(28000,first.NetPay);
        master.AnnualCtc=624000;Assert.Equal(56000,PayRunRepository.CalculateMasterSalaryArithmetic(master,setup,batch,link).NetPay);
        master.SalaryComponents["basic"]=12345;Assert.Equal(12345,PayRunRepository.CalculateMasterSalaryArithmetic(master,setup,batch,link).NetPay);
        master.SalaryComponents.Clear();
        Assert.Throws<InvalidOperationException>(()=>PayRunRepository.CalculateMasterSalaryArithmetic(master,setup.Replace("PAYABLE_DAYS","PRESENT_DAYS"),batch,link));
        Assert.Equal(untouched,JsonSerializer.Serialize(source));
    }
}
