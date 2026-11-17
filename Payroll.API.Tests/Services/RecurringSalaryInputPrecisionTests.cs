using System.Globalization;using System.Text.Json;using Payroll.API.Models;using Payroll.API.Services;using Payroll.API.Repositories;using Xunit;
namespace Payroll.API.Tests.Services;
public sealed class RecurringSalaryInputPrecisionTests
{
 [Fact]public void RealCalculateRowUsesOnlyTheReviewedLiteralStorageBoundaryAndRetainsSource()
 {
  var f=Fixture("0.10000000000000001");f.Request.FixedPayrollDays=30;f.Row.Cells.Add(new(){DataType="Number",Value="30"});f.Row.Cells[1]!.Value="0.10000000000000001";
  var original=JsonSerializer.Serialize(f.Source);var row=new ExcelPayslipRow{Id="synthetic-literal-storage",SourceRow=8,NetPay=.1m};
  var resolved=new[]{new ExcelPayrollSetupRepository.ResolvedComponent(f.Component,"106",false)};
  var actual=ExcelPayrollSetupRepository.CalculateRow(7,f.Request,resolved,f.Row,row,calculationSource:f.Source);
  Assert.Equal(.1m,actual.Inputs["106"]);Assert.Equal(1.2m,actual.Preview.AnnualCtc);Assert.Equal(.1m,actual.Preview.Net);
  Assert.Contains(actual.Issues,i=>i.Severity=="Info"&&i.Code=="OTHER_ALLOW"&&i.Message.Contains("identical finite IEEE-754 bits"));Assert.Equal(original,JsonSerializer.Serialize(f.Source));
 }
 [Theory][InlineData("12.34567",false)][InlineData("0.10000000000000001",true)]
 public void RealCalculateRowStillRejectsRealPrecisionAndFormulaCacheInputs(string literal,bool formula)
 {
  var f=Fixture(literal);f.Request.FixedPayrollDays=30;f.Row.Cells.Add(new(){DataType="Number",Value="30"});if(formula)f.Row.Cells[0]!.FormulaText="1/10";
  var original=JsonSerializer.Serialize(f.Source);var row=new ExcelPayslipRow{Id="synthetic-unrepresentable-storage",SourceRow=8};
  var resolved=new[]{new ExcelPayrollSetupRepository.ResolvedComponent(f.Component,"106",false)};
  var error=Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.CalculateRow(7,f.Request,resolved,f.Row,row,calculationSource:f.Source));
  Assert.Contains("four-decimal precision",error.Message);Assert.Equal(original,JsonSerializer.Serialize(f.Source));
 }
 private static (ExcelPayrollSetupRequest Request,ExcelPayrollSetupComponent Component,ExcelPayslipCalculationSource Source,ExcelPayslipCalculationSourceRow Row) Fixture(string value="7696.4750000000004")
 {
  var c=new ExcelPayrollSetupComponent{Code="OTHER_ALLOW",Category="Earning",CalculationType="Fixed Amount",SourceColumnIndex=0,ExpectedColumnIndex=1,Formula="",ProRata=true};
  var row=new ExcelPayslipCalculationSourceRow{SourceRow=8,Cells=[new(){DataType="Number",Value=value},new(){DataType="Number",Value="10"}]};
  var source=new ExcelPayslipCalculationSource{Columns=[new(){ColumnIndex=0,SourceHeader="Other allowance",Label="Other allowance",Kind="ignore"},new(){ColumnIndex=1,SourceHeader="Earned Other Allowance",Label="Earned Other Allowance",Kind="earning"}],Rows=[row]};
  return(new(){ReviewedFixedInputs=true,PayableDaysColumnIndex=2,Components=[c],SourceColumnRoles=new(){{0,"info"}}},c,source,row);
 }
 [Theory][InlineData("7696.4750000000004","7696.475")][InlineData("0.10000000000000001","0.1")][InlineData("100000000.000000005","100000000")]
 public void OnlyIdenticalFiniteNumberRepresentationsNormalize(string original,string expected)
 {var f=Fixture(original);var before=JsonSerializer.Serialize(f.Source);Assert.True(RecurringSalaryInputPrecision.TryNormalize(f.Request,f.Component,f.Source,f.Row,decimal.Parse(original,CultureInfo.InvariantCulture),out var stored,out var evidence));Assert.Equal(decimal.Parse(expected,CultureInfo.InvariantCulture),stored);Assert.Contains("identical finite IEEE-754 bits",evidence);Assert.Equal(before,JsonSerializer.Serialize(f.Source));}
 [Fact]public void SameBoundaryAppliesToLiteralDailyRates(){var f=Fixture();f.Component.CalculationType="Daily Rate";Assert.True(RecurringSalaryInputPrecision.TryNormalize(f.Request,f.Component,f.Source,f.Row,7696.4750000000004m,out _,out _));}
 [Theory][InlineData("7696.4750001")][InlineData("12.34567")][InlineData("0.10001")][InlineData("-0.10000000000000001")][InlineData("1000000000.000000005")][InlineData("10000000.000000011")]
 public void RealPrecisionDifferentBitsOutOfBoundsAndNonTinyDeltasReject(string original)
 {var f=Fixture(original);Assert.False(RecurringSalaryInputPrecision.TryNormalize(f.Request,f.Component,f.Source,f.Row,decimal.Parse(original,CultureInfo.InvariantCulture),out var stored,out var evidence));Assert.Equal(decimal.Parse(original,CultureInfo.InvariantCulture),stored);Assert.Null(evidence);}
 [Theory][InlineData("Text")][InlineData("Formula")][InlineData("Missing")][InlineData("Error")][InlineData("WrongParsed")][InlineData("Period")][InlineData("FormulaType")][InlineData("Unreviewed")][InlineData("NoCapture")][InlineData("FinancialRoleOverride")][InlineData("ExpectedCashAlias")][InlineData("Payable")][InlineData("Present")][InlineData("PayrollColumn")][InlineData("PayrollAddress")][InlineData("CapturedPayrollAddress")][InlineData("CTC")][InlineData("LWP")][InlineData("RoleUnreviewed")][InlineData("ConfiguredFormula")][InlineData("DuplicateMetadata")]
 public void LiteralInputBoundaryCannotWaiveOtherAuthorities(string mutation)
 {
  var f=Fixture();var cell=f.Row.Cells[0]!;var amount=7696.4750000000004m;ExcelPayslipCalculationSource? capture=f.Source;
  switch(mutation){case"Text":cell.DataType="Text";break;case"Formula":cell.FormulaText="A8/3";break;case"Missing":cell.MissingCachedValue=true;break;case"Error":cell.Error="#VALUE!";break;case"WrongParsed":amount+=1;break;case"Period":f.Component.IsPeriodAdjustment=true;break;case"FormulaType":f.Component.CalculationType="Formula";break;case"Unreviewed":f.Request.ReviewedFixedInputs=false;break;case"NoCapture":capture=null;break;case"FinancialRoleOverride":f.Source.Columns[0].Kind="earning";break;case"ExpectedCashAlias":f.Component.ExpectedColumnIndex=0;break;case"Payable":f.Request.PayableDaysColumnIndex=0;break;case"Present":f.Request.PresentDaysColumnIndex=0;break;case"PayrollColumn":f.Request.PayrollDaysColumnIndex=0;break;case"PayrollAddress":f.Request.PayrollDaysCell="$A$5";break;case"CapturedPayrollAddress":f.Source.MonthDaysCell="A5";break;case"CTC":f.Request.MonthlyCtcColumnIndex=0;break;case"LWP":f.Request.AttendanceReview=new(){LwpColumnIndex=0};break;case"RoleUnreviewed":f.Request.SourceColumnRoles.Clear();break;case"ConfiguredFormula":f.Component.Formula="PAYABLE_DAYS";break;case"DuplicateMetadata":f.Source.Columns.Add(f.Source.Columns[0]);break;}
  Assert.False(RecurringSalaryInputPrecision.TryNormalize(f.Request,f.Component,capture,f.Row,amount,out var stored,out var evidence));Assert.Equal(amount,stored);Assert.Null(evidence);
 }
 [Theory][InlineData("UAN Number")][InlineData("Account Number")][InlineData("Mobile No")][InlineData("Payable working days")][InlineData("Monthly CTC")][InlineData("Total Earnings")][InlineData("Employee Allowance ID")][InlineData("Column A")][InlineData("Gross Salary")][InlineData("Final Gross")][InlineData("Net Take Home")]
 public void NumericIdentifiersDaysTotalsAndUnknownHeadersNeverNormalize(string header)
 {var f=Fixture();f.Source.Columns[0].SourceHeader=f.Source.Columns[0].Label=header;Assert.False(RecurringSalaryInputPrecision.TryNormalize(f.Request,f.Component,f.Source,f.Row,7696.4750000000004m,out _,out _));}
 [Fact]public void AlreadyRepresentableValuesProduceNoNewEvidenceOrBehavior(){var f=Fixture("7696.475");Assert.False(RecurringSalaryInputPrecision.TryNormalize(f.Request,f.Component,f.Source,f.Row,7696.475m,out var stored,out var evidence));Assert.Equal(7696.475m,stored);Assert.Null(evidence);}
}
