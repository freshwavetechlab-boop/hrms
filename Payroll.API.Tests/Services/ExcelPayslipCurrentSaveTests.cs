using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipCurrentSaveTests
{
    private static ExcelPayslipRow Row(string id,decimal net=100m)=>new(){Id=id,SourceRow=3,EmployeeCode="SYN-1",EmployeeName="Synthetic employee",NetPay=net};

    [Fact]
    public void NoOpSaveDoesNotRewriteStoredJsonButContactEditsStillPersist()
    {
        var saved=new ExcelPayslipBatch{Id="saved",Month="2026-09",Rows=[Row("stable")]};
        var next=new ExcelPayslipBatch{Id="saved",Month="2026-09",Rows=[Row("stable")]};
        next.Rows[0].Employment=new(1,"SYN-1","Active",null,false,false);
        next.Rows[0].MasterDetails=new(1,"Current designation");
        Assert.True(ExcelPayslipRepository.SameSavedContent(next,saved));
        next.Rows[0].Email="synthetic@example.test";
        Assert.False(ExcelPayslipRepository.SameSavedContent(next,saved));
    }

    [Fact]
    public void RepeatedSaveRetainsTheRecordAndUnchangedRowIdentity()
    {
        var old=new ExcelPayslipBatch{Id="saved",Month="2026-09",CreatedAtUtc=new(2026,9,1),CreatedBy="owner",Rows=[Row("stable")]};
        var next=new ExcelPayslipBatch{Month=old.Month,Rows=[Row("retry-generated")]};
        ExcelPayslipRepository.ReusePayslipIdentity(next,old);
        Assert.Equal(old.Id,next.Id); Assert.Equal("stable",next.Rows[0].Id);
        Assert.Equal(old.CreatedAtUtc,next.CreatedAtUtc); Assert.Equal(old.CreatedBy,next.CreatedBy);
    }

    [Fact]
    public void BrowserDecimalRoundTripKeepsAuthoritativeAmountsAndIdentity()
    {
        var original=Row("stable",19540.385m);original.Earnings=[new(){Label="Basic",Amount=10000.123456789m}];
        var incoming=Row("new",19540.385000000002m);incoming.Earnings=[new(){Label="Basic",Amount=10000.123456789002m}];
        ExcelPayslipRepository.ReusePayslipIdentity(new(){Month="2026-09",Rows=[incoming]},new(){Id="saved",Month="2026-09",Rows=[original]});
        Assert.Equal("stable",incoming.Id);Assert.Equal(original.NetPay,incoming.NetPay);Assert.Equal(original.Earnings[0].Amount,incoming.Earnings[0].Amount);
    }

    [Fact]
    public void SourceCachesRetainTheSamePrecisionWithoutHidingFormulaOrRealAmountChanges()
    {
        static ExcelPayslipCalculationCell Cell(string value,string formula="A3*0.05")=>new(){DataType="Number",Value=value,FormulaText=formula};
        var old=new ExcelPayslipCalculationSource{SheetName="Salary",HeaderRow=2,Rows=[new(){SourceRow=3,Cells=[Cell("965.30000000000007"),Cell("200"),Cell("10")]}]};
        var next=new ExcelPayslipCalculationSource{SheetName="Salary",HeaderRow=2,Rows=[new(){SourceRow=3,Cells=[Cell("965.3"),Cell("201"),Cell("10.000000001","B3*0.05")]}]};
        old.Columns=next.Columns=[new(){ColumnIndex=0,Kind="earning",Label="HRA"},new(){ColumnIndex=1,Kind="deduction",Label="PT"},new(){ColumnIndex=2,Kind="earning",Label="Other"}];
        ExcelPayslipRepository.RetainCalculationDecimalCaches(next,old);
        Assert.Equal("965.30000000000007",next.Rows[0].Cells[0]!.Value);
        Assert.Equal("201",next.Rows[0].Cells[1]!.Value);
        Assert.Equal("10.000000001",next.Rows[0].Cells[2]!.Value);
    }

    [Fact]
    public void SavedUpdateRestoresDecimalPrecisionBeforeTheFinalStrictSourceCheck()
    {
        var original=Row("stable",965.3m);original.Earnings=[new(){Label="Basic",Amount=965.3m}];
        var incoming=Row("new",965.3000000002m);incoming.Earnings=[new(){Label="Basic",Amount=965.3000000002m}];
        var source=new ExcelPayslipCalculationSource{SheetName="Salary",HeaderRow=2,ColumnCount=3,
            Columns=[new(){ColumnIndex=0,Kind="employeeName",Label="Name"},new(){ColumnIndex=1,Kind="earning",Label="Basic"},new(){ColumnIndex=2,Kind="netPay",Label="Net"}],
            Rows=[new(){SourceRow=3,Cells=[new(){DataType="Text",Value=original.EmployeeName},new(){DataType="Number",Value="965.3"},new(){DataType="Number",Value="965.3"}]}]};
        var next=new ExcelPayslipBatch{Month="2026-09",SheetName="Salary",HeaderRow=2,Rows=[incoming]};
        Assert.NotNull(ExcelPayslipCalculationService.ValidateSource(source,next));
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(source,next,pendingSavedDecimalRestore:true));
        ExcelPayslipRepository.ReusePayslipIdentity(next,new(){Id="saved",Month=next.Month,Rows=[original]});
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(source,next));Assert.Equal("stable",incoming.Id);
        incoming.NetPay+=0.01m;
        Assert.NotNull(ExcelPayslipCalculationService.ValidateSource(source,next,pendingSavedDecimalRestore:true));
        // MySQL JSON can return the adjacent double representation even when
        // the source cache text was stored literally and never changed.
        incoming.NetPay=incoming.Earnings[0].Amount=222.7615384615385m;
        source.Rows[0].Cells[1]!.Value=source.Rows[0].Cells[2]!.Value="222.76153846153846";
        Assert.NotNull(ExcelPayslipCalculationService.ValidateSource(source,next));
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(source,next,pendingSavedDecimalRestore:true));
    }

    [Fact]
    public void ChangedAmountsCannotInheritOldDeliveryOrReviewRowIdentity()
    {
        var old=new ExcelPayslipBatch{Id="saved",Month="2026-09",Rows=[Row("stable")]};
        var next=new ExcelPayslipBatch{Month=old.Month,Rows=[Row("stable",110m)]};
        ExcelPayslipRepository.ReusePayslipIdentity(next,old);
        Assert.Equal("saved",next.Id); Assert.NotEqual("stable",next.Rows[0].Id); Assert.Null(next.Rows[0].NegativeAmountReview);
        Assert.Equal(110m,next.Rows[0].NetPay);
    }

    [Fact]
    public void CurrentListKeepsOneRecordPerClientMonthAndOtherMonthsRemainAvailable()
    {
        var current=ExcelPayslipRepository.CurrentMonths([
            new(){ClientId=7,Month="2026-09",Id="last-updated"},new(){ClientId=7,Month="2026-09",Id="old-duplicate"},
            new(){ClientId=7,Month="2026-08",Id="other-month"},new(){ClientId=8,Month="2026-09",Id="other-client"}]);
        Assert.Equal(3,current.Count); Assert.Contains(current,row=>row.Id=="last-updated");
        Assert.DoesNotContain(current,row=>row.Id=="old-duplicate");
        Assert.Contains(current,row=>row.Id=="other-month"); Assert.Contains(current,row=>row.Id=="other-client");
    }

    [Fact]
    public void UpdateCannotMoveAnotherMonthsSavedRecord()=>Assert.Throws<InvalidOperationException>(()=>
        ExcelPayslipRepository.ReusePayslipIdentity(new(){Month="2026-10"},new(){Month="2026-09"}));
}
