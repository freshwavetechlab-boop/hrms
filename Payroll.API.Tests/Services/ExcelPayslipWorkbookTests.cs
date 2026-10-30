using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipWorkbookTests
{
    private static ExcelPayslipCalculationCell N(string value)=>new() { Value=value,DataType="Number" };
    private static ExcelPayslipCalculationCell T(string value)=>new() { Value=value,DataType="Text" };
    private static ExcelPayslipCalculationCell F(string formula)=>new() { Value="0",FormulaText=formula,DataType="Number" };
    private static ExcelPayslipCalculationSource Source(string formula="VLOOKUP(A3,Attendance!A2:D4,4,FALSE)")=>new() {
        SheetName="Salary",WorkbookKey="current",ColumnCount=3,HeaderRow=2,CompleteSheetCapture=true,
        Rows=[new() { SourceRow=3,Cells=[T("GAD-1"),N("30"),F(formula)] }],
        DependencySheets=[new() { WorkbookKey="current",SheetName="Attendance",SourceHash=new string('a',64),ColumnCount=4,CompleteSheetCapture=true,Rows=[
            new() { SourceRow=2,Cells=[T("GAD-1"),new() { DataType="Blank" },new() { DataType="Blank" },N("28")] },
            new() { SourceRow=3,Cells=[T("GAD-2"),new() { DataType="Blank" },new() { DataType="Blank" },N("29")] },
            new() { SourceRow=4,Cells=[T("GAD-3"),new() { DataType="Blank" },new() { DataType="Blank" },N("30")] }
        ] }]
    };
    private static decimal Value(ExcelPayslipCalculationSource source)=>ExcelPayslipCalculationService.EvaluateCapturedCell(source,"C3").Value;

    [Fact] public void NhdcSameWorkbookLookupIferrorAndBonusUseCapturedInputsWithoutFlattening()
    {
        var s=Source("IFERROR(VLOOKUP(A3,Attendance!A:D,4,FALSE),0)");var before=JsonSerializer.Serialize(s);
        var result=ExcelPayslipCalculationService.EvaluateCapturedCell(s,"C3");Assert.Equal(28,result.Value);Assert.Single(result.Traces);Assert.Equal(2,result.Traces[0].SelectedRow);
        s.Rows[0].Cells[2]=F("IF(C3<=21000,ROUNDUP(C3*8.33%,0),0)");
        // Separate captured basic cell drives the conditional; no arbitrary source constant is substituted.
        s.Rows[0].Cells[0]=N("20000");s.Rows[0].Cells[2]=F("IF(A3<=21000,ROUNDUP(A3/30*B3*8.33%,0),0)");Assert.Equal(1666,Value(s));
        Assert.Contains("VLOOKUP",before);Assert.Contains("Attendance",before);
    }
    [Fact] public void IitExternalLwpChainRequiresReviewedSuppliedWorkbookAndKeepsOriginalFormula()
    {
        var s=Source("B3-VLOOKUP(A3,'[1]Aug 26 to Sept 25, 2026'!A2:D4,4,0)");
        s.DependencySheets[0].WorkbookKey="supplied";s.DependencySheets[0].SheetName="Aug 26 to Sept 25, 2026";s.DependencySheets[0].Rows[0].Cells[3]=N("0.5");
        Assert.Contains("reviewed/mapped",Assert.ThrowsAny<InvalidOperationException>(()=>Value(s)).Message);
        s.ExternalMappings=[new() { FromWorkbookKey="current",Reference="[1]",ToWorkbookKey="supplied",Reviewed=true }];
        var before=JsonSerializer.Serialize(s);Assert.Equal(29.5m,Value(s));Assert.Equal(before,JsonSerializer.Serialize(s));
    }
    [Fact] public void MissingKeyUsesOnlyOriginalExplicitIferrorFallbackAndRecordsIt()
    {
        var s=Source("IFERROR(VLOOKUP(A3,Attendance!A2:D4,4,FALSE),0)");s.Rows[0].Cells[0]=T("NOT-FOUND");
        var r=ExcelPayslipCalculationService.EvaluateCapturedCell(s,"C3");Assert.Equal(0,r.Value);Assert.Contains(r.Traces,t=>t.Status.Contains("Handled #N/A"));
        s.Rows[0].Cells[0]=T("GAD-1");s.DependencySheets[0].Rows[0].Cells[3]=new() {DataType="Blank"};
        r=ExcelPayslipCalculationService.EvaluateCapturedCell(s,"C3");Assert.Equal(0,r.Value);Assert.Contains(r.Traces,t=>t.Status.Contains("matched blank result"));
    }
    [Theory] [InlineData("IFERROR(VLOOKUP(A3,Absent!A2:D4,4,FALSE),0)")] [InlineData("IFERROR(WEBSERVICE(\"https://example.invalid\"),0)")]
    public void IferrorCannotHideMissingDependenciesOrUnsupportedRules(string formula)
    {Assert.ThrowsAny<InvalidOperationException>(()=>Value(Source(formula)));}
    [Fact] public void ExactDuplicateSelectsFirstAndKeepsAmbiguityVisible()
    {
        var s=Source();s.DependencySheets[0].Rows[1].Cells[0]=T("GAD-1");var r=ExcelPayslipCalculationService.EvaluateCapturedCell(s,"C3");
        Assert.Equal(28,r.Value);Assert.Equal(2,r.Traces[0].MatchingRows);Assert.Contains("Duplicate",r.Traces[0].Status);
    }
    [Theory] [InlineData("VLOOKUP(A3,Attendance!A2:D4,4)")] [InlineData("VLOOKUP(A3,Attendance!A2:D4,4,TRUE)")]
    public void ApproximateDefaultUsesSortedTypedNumericKeysAndLastEqualDuplicate(string formula)
    {
        var s=Source(formula);s.Rows[0].Cells[0]=N("20");s.DependencySheets[0].Rows[0].Cells[0]=N("10");s.DependencySheets[0].Rows[1].Cells[0]=N("20");s.DependencySheets[0].Rows[2].Cells[0]=N("20");
        var r=ExcelPayslipCalculationService.EvaluateCapturedCell(s,"C3");Assert.Equal(30,r.Value);Assert.Equal(4,r.Traces[0].SelectedRow);
        s.Rows[0].Cells[0]=N("15");Assert.Equal(28,Value(s));
    }
    [Fact] public void ApproximateUnsortedOrTextKeysRemainUnresolvedEvenInsideIferror()
    {
        var s=Source("IFERROR(VLOOKUP(A3,Attendance!A2:D4,4),0)");Assert.ThrowsAny<InvalidOperationException>(()=>Value(s));
        s.Rows[0].Cells[0]=N("20");s.DependencySheets[0].Rows[0].Cells[0]=N("20");s.DependencySheets[0].Rows[1].Cells[0]=N("10");s.DependencySheets[0].Rows[2].Cells[0]=N("30");Assert.ThrowsAny<InvalidOperationException>(()=>Value(s));
    }
    [Fact] public void ExactLookupDistinguishesNumericAndTextKeysAndRejectsUnknownLegacyKeyTypes()
    {
        var s=Source();s.Rows[0].Cells[0]=N("10");s.DependencySheets[0].Rows[0].Cells[0]=T("10");s.DependencySheets[0].Rows[1].Cells[0]=N("10");Assert.Equal(29,Value(s));
        s.DependencySheets[0].Rows[1].Cells[0]!.DataType="";Assert.Contains("type",Assert.ThrowsAny<InvalidOperationException>(()=>Value(s)).Message);
    }
    [Fact] public void ExactWildcardAndEscapedLiteralAreBoundedAndCaseInsensitive()
    {
        var s=Source();s.Rows[0].Cells[0]=T("gad-?");Assert.Equal(28,Value(s));
        s.Rows[0].Cells[0]=T("GAD~*");s.DependencySheets[0].Rows[0].Cells[0]=T("GAD*");Assert.Equal(28,Value(s));
    }
    [Theory] [InlineData("IF(TRUE,10,1/0)",10)] [InlineData("IF(FALSE,1/0,11)",11)]
    [InlineData("IF(TRUE,12,VLOOKUP(A3,Missing!A1:D2,4,FALSE))",12)]
    [InlineData("IFERROR(1/0,13)",13)] [InlineData("IFERROR(AND(FALSE,1/0),14)",14)]
    [InlineData("ROUNDUP(-1.21,1)",-1.3)] [InlineData("ROUNDDOWN(-1.29,1)",-1.2)] [InlineData("ROUND(-1.25,1)",-1.3)]
    public void LazyBranchesSpreadsheetErrorsAndNegativeRoundingHaveExplicitSemantics(string formula,double expected)
    {Assert.Equal((decimal)expected,Value(Source(formula)));}
    [Fact] public void LookupTextResultCanDriveConditionalWithoutBecomingNumericConstant()
    {
        var s=Source("IF(VLOOKUP(A3,Attendance!A2:D4,4,FALSE)=\"paid\",100,0)");s.DependencySheets[0].Rows[0].Cells[3]=T("PAID");Assert.Equal(100,Value(s));
    }
    [Fact] public void CrossWorkbookCyclesAndOversizedRangesCannotBeSuppressed()
    {
        var s=Source("IFERROR(VLOOKUP(A3,Attendance!A2:D4,4,FALSE),0)");s.DependencySheets[0].Rows[0].Cells[3]=F("Salary!C3");Assert.Contains("Circular",Assert.ThrowsAny<InvalidOperationException>(()=>Value(s)).Message);
        Assert.Contains("10,000",Assert.ThrowsAny<InvalidOperationException>(()=>Value(Source("IFERROR(VLOOKUP(A3,Attendance!A1:Z1000,4,FALSE),0)"))).Message);
    }
    [Fact] public void UnsafeExternalPathsAndUnreviewedMappingsAreNeverFetched()
    {
        var s=Source();s.ExternalMappings=[new() { Reference="[https://bad.invalid/a.xlsx]",ToWorkbookKey="supplied",Reviewed=true }];Assert.ThrowsAny<InvalidOperationException>(()=>Value(s));
        s=Source("IFERROR(VLOOKUP(A3,'[1]Attendance'!A2:D4,4,FALSE),0)");s.ExternalMappings=[new() { Reference="[1]",ToWorkbookKey="supplied",Reviewed=false }];Assert.ThrowsAny<InvalidOperationException>(()=>Value(s));
    }
    [Fact] public void ColumnBoundsAndMissingKeyAreGenuineExcelErrorsWithExplicitFallback()
    {
        Assert.Equal(77,Value(Source("IFERROR(VLOOKUP(A3,Attendance!A2:D4,5,FALSE),77)")));
        Assert.Equal(78,Value(Source("IFERROR(VLOOKUP(A3,Attendance!A2:D4,0,FALSE),78)")));
    }
    [Fact] public void PublicPayslipCalculationTraversesLookupChainAndRetainsDerivedProvenance()
    {
        var source=Source();source.ColumnCount=9;
        source.Rows[0].Cells=[T("Synthetic Person"),T("GAD-1"),N("30"),F("VLOOKUP(B3,Attendance!A2:D4,4,FALSE)"),N("30000"),F("E3/30*(C3-D3)"),F("F3*12%"),F("F3-G3"),F("F3*13%")];
        foreach(var (column,value) in new[] {(3,"1"),(5,"29000"),(6,"3480"),(7,"25520"),(8,"3770")})source.Rows[0].Cells[column]!.Value=value;
        source.DependencySheets[0].Rows[0].Cells[3]=N("1");
        var labels=new[] {"Employee","Code","Cycle days","LWP","Rate","Wages","PF","Net","Employer PF"};
        var kinds=new[] {"employeeName","employeeCode","info","info","info","earning","deduction","netPay","employer"};
        source.Columns=labels.Select((l,i)=>new ExcelPayslipCalculationColumn {ColumnIndex=i,Label=l,SourceHeader=l,Kind=kinds[i]}).ToList();
        var batch=new ExcelPayslipBatch {Id=new string('a',32),ClientId=42,Month="2026-09",SheetName="Salary",HeaderRow=2,Rows=[new() {
            Id="one",SourceRow=3,EmployeeName="Synthetic Person",EmployeeCode="GAD-1",NetPay=25520,
            Information=[new() {Label="Cycle days",Value="30"},new() {Label="LWP",Value="1"},new() {Label="Rate",Value="30000"}],
            Earnings=[new() {Label="Wages",Amount=29000}],Deductions=[new() {Label="PF",Amount=3480}],EmployerContributions=[new() {Label="Employer PF",Amount=3770}]
        }]};
        var before=JsonSerializer.Serialize(source);
        var result=ExcelPayslipCalculationService.Calculate(source,batch,new() {Month="2026-10",AttendanceColumnIndex=2,Attendance=[new() {RowId="one",Days=29}]});
        Assert.Equal(28000,result.Rows[0].Earnings[0].Amount);Assert.Equal(24640,result.Rows[0].NetPay);Assert.Equal(3640,result.Rows[0].EmployerContributions[0].Amount);
        Assert.NotEmpty(result.CalculationSource!.LookupTraces);Assert.Contains("VLOOKUP",result.CalculationSource.Rows[0].Cells[3]!.FormulaText);Assert.Equal(before,JsonSerializer.Serialize(source));
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(result.CalculationSource,result));
    }
}
