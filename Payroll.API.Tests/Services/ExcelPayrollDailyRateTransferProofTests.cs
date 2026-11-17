using System.Globalization;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollDailyRateTransferProofTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(26)]
    [InlineData(31)]
    public void OnlyTheCompleteCapturedDailyRateNormalizationIsProven(int factor)
    {
        var (q, source, row, component) = Fixture(factor);
        var before = JsonSerializer.Serialize(new { q, source, row });
        var proof = ExcelPayrollSetupRepository.InspectTransferFormula(q, source, row, component);
        Assert.Equal("Exact supported translation", proof.Status);
        var translated = ExcelPayrollSetupRepository.TranslateTransferFormulaWithEvidence(source.Rows[0].Cells[3]!.FormulaText!, q, source, row);
        var dependency = Assert.Single(translated.Dependencies);
        Assert.Equal($"A2*{factor}", dependency.Formula); Assert.Null(dependency.Literal);
        Assert.Equal("Daily Rate", component.CalculationType); Assert.Equal(0, component.SourceColumnIndex);
        Assert.Empty(component.Formula); Assert.False(component.IsPeriodAdjustment);
        Assert.Equal(before, JsonSerializer.Serialize(new { q, source, row }));
        Assert.Equal(proof.SourceHash, ExcelPayrollSetupRepository.InspectTransferFormula(q, source, row, component).SourceHash);
    }

    [Theory]
    [InlineData("A2*26/26*B2")]
    [InlineData("((A2*26)/26)*B2")]
    [InlineData("(((C2))/26)*(B2)")]
    public void ParenthesesPreserveTheSameWholeAst(string formula)
    {
        var (q, source, row, component) = Fixture(26); source.Rows[0].Cells[3]!.FormulaText = formula;
        Assert.Equal("Exact supported translation", ExcelPayrollSetupRepository.InspectTransferFormula(q, source, row, component).Status);
    }

    [Theory]
    [InlineData("A2*0/0*B2")]
    [InlineData("A2*0.5/0.5*B2")]
    [InlineData("A2*-26/-26*B2")]
    [InlineData("A2*32/32*B2")]
    [InlineData("A2*26/25*B2")]
    [InlineData("A2*25/26*B2")]
    [InlineData("ROUND(A2*26,0)/26*B2")]
    [InlineData("ROUND(A2*26/26*B2,2)")]
    [InlineData("MIN(A2*26,15000)/26*B2")]
    [InlineData("C2*(B2/26)")]
    [InlineData("A2*(26/26)*B2")]
    [InlineData("26*A2/26*B2")]
    [InlineData("C2/26*B2+0")]
    [InlineData("C2/PAYROLL_DAYS*B2")]
    public void NearbyAlgebraAndRoundingAreNotApproved(string formula)
    {
        var (q, source, row, component) = Fixture(26); source.Rows[0].Cells[3]!.FormulaText = formula;
        Assert.Equal("Blocked", ExcelPayrollSetupRepository.InspectTransferFormula(q, source, row, component).Status);
    }

    [Theory]
    [InlineData("A2*E2", "C2/F2*B2")]
    [InlineData("A2*E2", "C2/E2*B2")]
    [InlineData("A2*26", "C2/E2*B2")]
    [InlineData("A2*E2", "C2/26*B2")]
    public void EqualInfoValuesNeverBecomeACancelableLiteral(string monthly, string earned)
    {
        var (q, source, row, component) = Fixture(26);
        source.Rows[0].Cells[2]!.FormulaText = monthly; source.Rows[0].Cells[3]!.FormulaText = earned;
        var translated = ExcelPayrollSetupRepository.TranslateTransferFormulaWithEvidence(earned, q, source, row);
        Assert.Contains(translated.Dependencies, d => d.Literal == "26");
        Assert.Equal("Blocked", ExcelPayrollSetupRepository.InspectTransferFormula(q, source, row, component).Status);
    }

    [Theory]
    [InlineData("C2*26")]
    [InlineData("D2*26")]
    [InlineData("A3*26")]
    [InlineData("[1]Other!A2*26")]
    public void CircularExternalAndCrossRowMonthlyInputsStayBlocked(string monthly)
    {
        var (q, source, row, component) = Fixture(26); source.Rows[0].Cells[2]!.FormulaText = monthly;
        Assert.Equal("Blocked", ExcelPayrollSetupRepository.InspectTransferFormula(q, source, row, component).Status);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("784.00001")]
    [InlineData("7.84E2")]
    [InlineData("text")]
    public void RateMustBeAnIndependentValidNativeInput(string value)
    {
        var (q, source, row, component) = Fixture(26); source.Rows[0].Cells[0]!.Value = value;
        Assert.Equal("Blocked", ExcelPayrollSetupRepository.InspectTransferFormula(q, source, row, component).Status);
    }

    [Theory]
    [InlineData("formula")]
    [InlineData("error")]
    [InlineData("calculation-error")]
    [InlineData("missing-cache")]
    [InlineData("ambiguous-input")]
    [InlineData("fixed-monthly")]
    [InlineData("reimbursement")]
    public void ProofDoesNotReplaceOtherInputOrComponentContracts(string problem)
    {
        var (q, source, row, component) = Fixture(26); var rate = source.Rows[0].Cells[0]!;
        switch (problem)
        {
            case "formula": rate.FormulaText = "700+84"; break;
            case "error": rate.Error = "#VALUE!"; break;
            case "calculation-error": rate.CalculationError = "#REF!"; break;
            case "missing-cache": rate.MissingCachedValue = true; break;
            case "ambiguous-input": q.Components.Add(new() { Key="other",Code="OTHER_RATE",Category="Earning",SourceColumnIndex=0,ExpectedColumnIndex=5,CalculationType="Daily Rate" }); break;
            case "fixed-monthly": component.CalculationType = "Fixed Amount"; component.ProRata = true; break;
            case "reimbursement": component.Category = "Reimbursement"; break;
        }
        Assert.Equal("Blocked", ExcelPayrollSetupRepository.InspectTransferFormula(q, source, row, component).Status);
    }

    [Fact]
    public void OriginalDailyMappingAndUnrelatedStrictFormulaRemainUnchanged()
    {
        var (q, source, row, component) = Fixture(26); source.Rows[0].Cells[3]!.FormulaText = "A2*B2";
        Assert.Equal("Exact supported translation", ExcelPayrollSetupRepository.InspectTransferFormula(q, source, row, component).Status);
        component.CalculationType = "Formula"; component.SourceColumnIndex = null; component.Formula = "42";
        Assert.Equal("Blocked", ExcelPayrollSetupRepository.InspectTransferFormula(q, source, row, component).Status);
    }

    [Fact]
    public void MonthlyFixedInputReferencesKeepTheirExistingUnits()
    {
        var (q, source, row, component) = Fixture(26); component.CalculationType = "Fixed Amount";
        Assert.Equal("DAILY_WAGE_MONTHLY", ExcelPayrollSetupRepository.TranslateTransferFormula("A2", q, source, row));
        component.CalculationType = "Daily Rate";
        Assert.Equal("(DAILY_WAGE_MONTHLY/PAYROLL_DAYS)", ExcelPayrollSetupRepository.TranslateTransferFormula("A2", q, source, row));
    }

    [Theory]
    [InlineData(784, 152.88, 662.48)]
    [InlineData(1000, 195, 845)]
    [InlineData(22000, 0, 0)]
    public void SourceDailyEligibilityUsesTheRateEvenWhenNativeMonthlyCrossesTheThreshold(decimal rate, decimal employeeEsi, decimal employerEsi)
    {
        var (q, source, row, wage) = Fixture(26); source.Rows[0].Cells[0]!.Value = rate.ToString(CultureInfo.InvariantCulture);
        var esi = new ExcelPayrollSetupComponent { Key="esi",Code="ESIC",Name="Employee ESI",Category="Deduction",ComponentRole="Statutory Deduction",StatutoryType="ESI Employee",CalculationType="Formula",ProRata=false,ExpectedColumnIndex=4,Priority=200 };
        var employer = new ExcelPayrollSetupComponent { Key="esi_er",Code="ESIC_ER",Name="Employer ESI",Category="Benefit",ComponentRole="Employer Contribution",StatutoryType="ESI Employer",CalculationType="Formula",ProRata=false,ExpectedColumnIndex=5,Priority=300 };
        var admin = new ExcelPayrollSetupComponent { Key="admin",Code="PF_ADMIN",Name="Employer admin",Category="Benefit",ComponentRole="Employer Benefit",StatutoryType="None",CalculationType="Formula",ProRata=false,ExpectedColumnIndex=6,Priority=400 };
        q.Components.AddRange([esi,employer,admin]); source.ColumnCount=7;
        source.Columns[4].Kind="deduction"; source.Columns[5].Kind="employer";
        q.SourceColumnRoles[4]="deduction"; q.SourceColumnRoles[5]="employer";
        source.Columns.Add(new(){ColumnIndex=6,SourceHeader="Employer admin",Kind="employer"});
        source.Rows[0].Cells[4]=new(){Value=employeeEsi.ToString(CultureInfo.InvariantCulture),FormulaText="IF(A2<21000,D2*0.75%,0)"};
        source.Rows[0].Cells[5]=new(){Value=employerEsi.ToString(CultureInfo.InvariantCulture),FormulaText="IF(A2<21000,D2*3.25%,0)"};
        source.Rows[0].Cells.Add(new(){Value="150",FormulaText="IF(C2<15000,C2*1%,15000*1%)"});
        foreach (var component in new[]{esi,employer,admin})
        {
            component.Formula=ExcelPayrollSetupRepository.TranslateTransferFormula(source.Rows[0].Cells[component.ExpectedColumnIndex!.Value]!.FormulaText!,q,source,row);
            Assert.Equal("Exact supported translation",ExcelPayrollSetupRepository.InspectTransferFormula(q,source,row,component).Status);
        }
        Assert.Contains("(DAILY_WAGE_MONTHLY/PAYROLL_DAYS)<21000",esi.Formula);
        var native=q.Components.Select((c,index)=>new PayrollReviewComponent((index+1).ToString(CultureInfo.InvariantCulture),c.Code,c.Category,c.ComponentRole,c.StatutoryType,c.CalculationType,c==wage?rate.ToString(CultureInfo.InvariantCulture):"0",c.Formula,"",false,c.Category=="Earning",true,c.Priority,"Fixed Pay")).ToList();
        var annual=rate*26*12;
        var result=PayRunRepository.SimulateTemplate(new(){Mode="Template",ClientId=1,TemplateId="offline",PayrollDays=26,PresentDays=0,PayableDays=26,AnnualCtc=annual,Snapshot=new("Template",1,"",annual,native,native.Select(c=>new PayrollReviewLine(c.Id)).ToList())});
        Assert.True(result.Complete,string.Join(";",result.Issues.Select(i=>i.Message)));
        Assert.Equal(rate*26,result.Lines.Single(l=>l.Code==wage.Code).Monthly);
        Assert.Equal(rate,result.Lines.Single(l=>l.Code==wage.Code).DailyRate);
        Assert.Equal(employeeEsi,result.Lines.Single(l=>l.Code==esi.Code).Earned);
        Assert.Equal(employerEsi,result.Lines.Single(l=>l.Code==employer.Code).Earned);
        Assert.Equal(150,result.Lines.Single(l=>l.Code==admin.Code).Earned);
        if(rate==1000){Assert.Equal(26000,result.Gross);Assert.Equal(26000,result.Lines.Single(l=>l.Code==esi.Code).Inputs["DAILY_WAGE_MONTHLY"]);Assert.Equal(195,result.Deductions);}
    }

    private static (ExcelPayrollSetupRequest, ExcelPayslipCalculationSource, ExcelPayslipRow, ExcelPayrollSetupComponent) Fixture(int factor)
    {
        var component = new ExcelPayrollSetupComponent { Key="wage",Code="DAILY_WAGE",Name="Wages",Category="Earning",ComponentRole="Regular Earning",CalculationType="Daily Rate",SourceColumnIndex=0,ExpectedColumnIndex=3,ProRata=false };
        var request = new ExcelPayrollSetupRequest { ReviewedFixedInputs=true,FixedPayrollDays=26,PayrollDaysColumnIndex=-1,PayableDaysColumnIndex=1,Components=[component],SourceColumnRoles=new(){[0]="info",[1]="info",[2]="info",[4]="info",[5]="info"} };
        var source = new ExcelPayslipCalculationSource { HeaderRow=1,ColumnCount=6,Columns=[new(){ColumnIndex=0,SourceHeader="Daily rate",Kind="info"},new(){ColumnIndex=1,SourceHeader="Payable units",Kind="ignore"},new(){ColumnIndex=2,SourceHeader="Monthly wages",Kind="ignore"},new(){ColumnIndex=3,SourceHeader="Earned wages",Kind="earning"},new(){ColumnIndex=4,SourceHeader="First factor",Kind="info"},new(){ColumnIndex=5,SourceHeader="Second factor",Kind="info"}],Rows=[new(){SourceRow=2,Cells=[new(){Value="784"},new(){Value="26"},new(){Value=(784*factor).ToString(CultureInfo.InvariantCulture),FormulaText=$"A2*{factor}"},new(){Value="20384",FormulaText=$"C2/{factor}*B2"},new(){Value="26"},new(){Value="26"}]}] };
        return (request,source,new(){Id="synthetic",SourceRow=2,EmployeeCode="SYN-ONLY",EmployeeName="Synthetic employee"},component);
    }
}
