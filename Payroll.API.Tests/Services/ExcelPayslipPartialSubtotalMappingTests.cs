using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipPartialSubtotalMappingTests
{
    [Theory]
    [InlineData(30000, 1200, 0)]
    [InlineData(30000, 1200, 543)]
    [InlineData(0, 1200, 0)]
    [InlineData(30000, 0, 543)]
    public void ReloadedReviewedMappingDoesNotDeductRecoveryAlreadyInsideAllowanceOrTreatPartialSubtotalAsFullGross(int monthly, int recovery, int arrears)
    {
        const decimal basic=20215m,statutory=2425.8m;
        var allowance=monthly/30m*25m-recovery;var subtotal=basic+allowance;var net=subtotal-statutory+arrears;
        string N(decimal value)=>value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var row=new ExcelPayslipRow{Id="owned-embedded",SourceRow=2,EmployeeName="Synthetic Employee",
            Earnings=[new(){Label="Allowance",Amount=allowance},new(){Label="Basic",Amount=basic},new(){Label="Arrears",Amount=arrears}],
            Deductions=[new(){Label="Other deductions",Amount=recovery},new(){Label="Statutory",Amount=statutory}],DeclaredGross=subtotal,NetPay=net};
        var source=new ExcelPayslipCalculationSource{SheetName="Salary",HeaderRow=1,ColumnCount=11,
            Columns=[new(){ColumnIndex=0,SourceHeader="Name",Label="Name",Kind="employeeName"},
                new(){ColumnIndex=1,SourceHeader="Monthly allowance",Label="Monthly allowance",Kind="ignore"},
                new(){ColumnIndex=2,SourceHeader="Payable units",Label="Payable units",Kind="ignore"},
                new(){ColumnIndex=3,SourceHeader="Divisor",Label="Divisor",Kind="ignore"},
                new(){ColumnIndex=4,SourceHeader="Other deductions",Label="Other deductions",Kind="deduction"},
                new(){ColumnIndex=5,SourceHeader="Allowance",Label="Allowance",Kind="earning"},
                new(){ColumnIndex=6,SourceHeader="Basic",Label="Basic",Kind="earning"},
                new(){ColumnIndex=7,SourceHeader="Gross subtotal",Label="Gross subtotal",Kind="grossTotal"},
                new(){ColumnIndex=8,SourceHeader="Arrears",Label="Arrears",Kind="earning"},
                new(){ColumnIndex=9,SourceHeader="Statutory",Label="Statutory",Kind="deduction"},
                new(){ColumnIndex=10,SourceHeader="Net pay",Label="Net pay",Kind="netPay"}],
            Rows=[new(){SourceRow=2,Cells=[new(){Value=row.EmployeeName,DataType="Text"},new(){Value=N(monthly),DataType="Number"},
                new(){Value="25",DataType="Number"},new(){Value="30",DataType="Number"},new(){Value=N(recovery),DataType="Number"},
                new(){Value=N(allowance),FormulaText="B2/D2*C2-E2",DataType="Number"},new(){Value=N(basic),DataType="Number"},
                new(){Value=N(subtotal),FormulaText="F2+G2",DataType="Number"},new(){Value=N(arrears),DataType="Number"},
                new(){Value=N(statutory),DataType="Number"},new(){Value=N(net),FormulaText="H2-J2+I2",DataType="Number"}]}]};
        var batch=new ExcelPayslipBatch{Id=new('a',32),ClientId=42,Month="2026-10",SheetName="Salary",SourceFileName="synthetic.xlsx",HeaderRow=1,Rows=[row]};
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(source,batch));
        var originalCells=JsonSerializer.Serialize(source.Rows);var earnings=JsonSerializer.Serialize(row.Earnings);
        source.Columns[4].Kind="ignore";source.Columns[7].Kind="ignore";row.Deductions.RemoveAt(0);row.DeclaredGross=null;
        batch.CalculationSource=source;
        batch.MappingProfile=JsonSerializer.SerializeToElement(new{headerSignature=new string('b',64),columns=source.Columns.Select(c=>new{c.ColumnIndex,c.SourceHeader,c.Label,c.Kind})},new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var saved=JsonSerializer.Deserialize<ExcelPayslipBatch>(JsonSerializer.Serialize(batch))!;
        Assert.Null(ExcelPayslipRepository.ValidateBatchMappingProfile(saved));
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(saved.CalculationSource!,saved));
        Assert.Equal(originalCells,JsonSerializer.Serialize(saved.CalculationSource!.Rows));
        Assert.Equal(earnings,JsonSerializer.Serialize(saved.Rows[0].Earnings));
        var result=ExcelPayslipCalculationService.Calculate(saved.CalculationSource!,saved,new(){Month="2026-10",AttendanceColumnIndex=2,Attendance=[new(){RowId=row.Id,Days=24}]});
        var calculated=Assert.Single(result.Rows);var expectedAllowance=monthly/30m*24m-recovery;
        Assert.Single(calculated.Deductions);Assert.Equal(statutory,calculated.Deductions[0].Amount);Assert.Null(calculated.DeclaredGross);
        Assert.Equal(expectedAllowance,calculated.Earnings[0].Amount);Assert.Equal(basic+expectedAllowance-statutory+arrears,calculated.NetPay);
        Assert.DoesNotContain(ExcelPayslipRepository.RowWarnings(calculated),w=>w.Contains("do not match"));
        if(monthly==0)Assert.Contains(ExcelPayslipRepository.NegativeAmountWarning,ExcelPayslipRepository.RowWarnings(calculated));
        Assert.Equal(originalCells,JsonSerializer.Serialize(source.Rows));
    }

    [Fact]
    public void ReviewedPartialSubtotalInformationKeepsOriginalCellsAndAllCashWithoutAcknowledgement()
    {
        var row=new ExcelPayslipRow{Id="row-2",SourceRow=2,EmployeeCode="EXAMPLE1",EmployeeName="Example Employee",
            Earnings=[new(){Label="Basic",Amount=100m},new(){Label="Additional allowance",Amount=10m}],
            DeclaredGross=100m,NetPay=110m};
        var batch=new ExcelPayslipBatch{ClientId=1,Month="2026-10",SheetName="Salary",HeaderRow=1,
            SourceFileName="synthetic.xlsx",Rows=[row]};
        var source=new ExcelPayslipCalculationSource{SheetName="Salary",HeaderRow=1,ColumnCount=5,
            Columns=[new(){ColumnIndex=0,SourceHeader="Name",Label="Name",Kind="employeeName"},
                new(){ColumnIndex=1,SourceHeader="Basic",Label="Basic",Kind="earning"},
                new(){ColumnIndex=2,SourceHeader="Additional allowance",Label="Additional allowance",Kind="earning"},
                new(){ColumnIndex=3,SourceHeader="Gross subtotal",Label="Gross subtotal",Kind="grossTotal"},
                new(){ColumnIndex=4,SourceHeader="Net pay",Label="Net pay",Kind="netPay"}],
            Rows=[new(){SourceRow=2,Cells=[new(){Value="Example Employee",DataType="Text"},
                new(){Value="100",DataType="Number"},new(){Value="10",DataType="Number"},
                new(){Value="100",FormulaText="B2",DataType="Number"},
                new(){Value="110",FormulaText="D2+C2",DataType="Number"}]}]};
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(source,batch));
        Assert.Single(ExcelPayslipRepository.RowWarnings(row));
        var cells=JsonSerializer.Serialize(source.Rows);
        var cash=JsonSerializer.Serialize(new{row.Earnings,row.Deductions,row.EmployerContributions,row.NetPay});

        source.Columns[3].Kind="info";
        row.DeclaredGross=null;
        row.Information.Add(new(){Label="Gross subtotal",Value="100"});

        Assert.Null(ExcelPayslipCalculationService.ValidateSource(source,batch));
        Assert.Empty(ExcelPayslipRepository.RowWarnings(row));
        Assert.Null(row.WarningReview);
        Assert.Null(row.NegativeAmountReview);
        Assert.Equal(cells,JsonSerializer.Serialize(source.Rows));
        Assert.Equal(cash,JsonSerializer.Serialize(new{row.Earnings,row.Deductions,row.EmployerContributions,row.NetPay}));
        Assert.Equal(110m,ExcelPayslipPdfService.TotalsFor(row).Gross);
        Assert.Equal("100",row.Information.Single().Value);
    }

    private static (ExcelPayslipBatch Batch,ExcelPayslipCalculationSource Source) DensePartialSubtotal(int count)
    {
        var row=new ExcelPayslipRow{Id="owned-layout-row",SourceRow=2,EmployeeCode="SYN-01",EmployeeName="Synthetic Employee",
            Earnings=[new(){Label="Basic",Amount=100},new(){Label="Additional cash allowance",Amount=10},new(){Label="Signed adjustment",Amount=-2}],
            Deductions=[new(){Label="Employee deduction",Amount=5}],EmployerContributions=[new(){Label="Employer cost",Amount=7}],DeclaredGross=98,NetPay=103};
        var batch=new ExcelPayslipBatch{Id=new('a',32),ClientId=42,ClientName="Synthetic source",Month="2026-10",SheetName="Salary",HeaderRow=1,SourceFileName="synthetic.xlsx",Rows=[row]};
        var source=new ExcelPayslipCalculationSource{SheetName="Salary",HeaderRow=1,ColumnCount=8,
            Columns=[new(){ColumnIndex=0,SourceHeader="Name",Label="Name",Kind="employeeName"},new(){ColumnIndex=1,SourceHeader="Basic",Label="Basic",Kind="earning"},
                new(){ColumnIndex=2,SourceHeader="Additional cash allowance",Label="Additional cash allowance",Kind="earning"},new(){ColumnIndex=3,SourceHeader="Signed adjustment",Label="Signed adjustment",Kind="earning"},
                new(){ColumnIndex=4,SourceHeader="Employee deduction",Label="Employee deduction",Kind="deduction"},new(){ColumnIndex=5,SourceHeader="Employer cost",Label="Employer cost",Kind="employer"},
                new(){ColumnIndex=6,SourceHeader="Gross subtotal A",Label="Gross subtotal",Kind="grossTotal"},new(){ColumnIndex=7,SourceHeader="Net pay",Label="Net pay",Kind="netPay"}],
            Rows=[new(){SourceRow=2,Cells=[new(){Value=row.EmployeeName,DataType="Text"},new(){Value="100",DataType="Number"},new(){Value="10",DataType="Number"},new(){Value="-2",DataType="Number"},
                new(){Value="5",DataType="Number"},new(){Value="7",DataType="Number"},new(){Value="98",FormulaText="B2+D2",DataType="Number"},new(){Value="103",FormulaText="G2-E2+C2",DataType="Number"}]}]};
        for(var index=0;index<count;index++)
        {
            var label="Recorded detail "+index;var value="Captured reference detail retained exactly from the synthetic original source for review.";
            row.Information.Add(new(){Label=label,Value=value});source.Columns.Add(new(){ColumnIndex=source.ColumnCount++,SourceHeader=label,Label=label,Kind="info"});
            source.Rows[0].Cells.Add(new(){Value=value,DataType="Text"});
        }
        row.Warnings=ExcelPayslipRepository.RowWarnings(row);return(batch,source);
    }

    [Fact]
    public void IgnoredUnprintedPartialSubtotalPreservesFullSourceCashAndOriginalInformationAndPassesActualPresaveLayout()
    {
        var pdf=new ExcelPayslipPdfService();ExcelPayslipBatch? original=null;ExcelPayslipCalculationSource? fullSource=null;
        // Find a bounded synthetic page edge using the actual renderer, rather than assuming a platform font's width.
        for(var count=1;count<=64;count++)
        {
            var fixture=DensePartialSubtotal(count);
            try{pdf.ValidateLayout(fixture.Batch,fixture.Batch.Rows);}catch(InvalidOperationException error){Assert.Contains("too much text",error.Message);break;}
            var extra=JsonSerializer.Deserialize<ExcelPayslipBatch>(JsonSerializer.Serialize(fixture.Batch))!;
            extra.Rows[0].DeclaredGross=null;extra.Rows[0].Information.Add(new(){Label="Total earnings subtotal (before separate cash allowance)",Value="98"});
            try{pdf.ValidateLayout(extra,extra.Rows);}catch(InvalidOperationException error)
            {Assert.Contains("too much text",error.Message);original=fixture.Batch;fullSource=fixture.Source;break;}
        }
        Assert.NotNull(original);Assert.NotNull(fullSource);pdf.ValidateLayout(original,original.Rows);
        Assert.Null(ExcelPayslipRepository.ValidateBatch(original));Assert.Null(ExcelPayslipCalculationService.ValidateSource(fullSource,original));
        var old=original.Rows[0];var cells=JsonSerializer.Serialize(fullSource.Rows);var info=JsonSerializer.Serialize(old.Information);
        var cash=JsonSerializer.Serialize(new{old.Earnings,old.Deductions,old.EmployerContributions,old.NetPay});
        Assert.Equal(2,ExcelPayslipRepository.RowWarnings(old).Count);
        var reviewed=JsonSerializer.Deserialize<ExcelPayslipBatch>(JsonSerializer.Serialize(original))!;
        fullSource.Columns[6].Kind="ignore";reviewed.Rows[0].DeclaredGross=null;
        ExcelPayslipRepository.ReusePayslipIdentity(reviewed,original);
        var current=reviewed.Rows[0];Assert.Equal(old.Id,current.Id);Assert.Null(ExcelPayslipRepository.ValidateBatch(reviewed));
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(fullSource,reviewed));
        pdf.ValidateLayout(reviewed,reviewed.Rows); // The same normal pre-save legibility gate; no Save or DB is called.
        Assert.Equal(info,JsonSerializer.Serialize(current.Information));Assert.Equal(cells,JsonSerializer.Serialize(fullSource.Rows));
        Assert.Equal(cash,JsonSerializer.Serialize(new{current.Earnings,current.Deductions,current.EmployerContributions,current.NetPay}));
        Assert.Equal("B2+D2",fullSource.Rows[0].Cells[6]!.FormulaText);Assert.Equal("98",fullSource.Rows[0].Cells[6]!.Value);
        Assert.Equal("G2-E2+C2",fullSource.Rows[0].Cells[7]!.FormulaText);Assert.Equal("ignore",fullSource.Columns[6].Kind);
        Assert.Equal(108m,ExcelPayslipPdfService.TotalsFor(current).Gross);Assert.Equal(103m,current.NetPay);
        Assert.Equal(-2m,current.Earnings.Single(e=>e.Label=="Signed adjustment").Amount);
        Assert.Equal(ExcelPayslipRepository.NegativeAmountWarning,Assert.Single(ExcelPayslipRepository.RowWarnings(current)));
        Assert.Null(current.WarningReview);Assert.Null(current.NegativeAmountReview);Assert.Null(current.WarningReviewToken);
        Assert.NotEqual(NotificationRepository.ExcelPayslipBatchHash(original),NotificationRepository.ExcelPayslipBatchHash(reviewed));
    }
}
