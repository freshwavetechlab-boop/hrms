using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class HistoricalProfessionalTaxTests
{
    [Theory]
    [InlineData("G2-H2",true)][InlineData("SUM(G2:G2)-H2",true)]
    [InlineData("($G$2)-($H$2)",true)][InlineData("-H2+G2",true)]
    [InlineData("G2-H2-0",false)][InlineData("G2-H2-I2",false)]
    [InlineData("G3-H3",false)][InlineData("SUM(G2:H2)",false)]
    [InlineData("G2-H2+H2-H2",true)][InlineData("ROUND(G2-H2,2)",false)]
    [InlineData("[1]Salary!G2-H2",false)][InlineData("G2-H2*1",false)]
    public void Source_net_closure_uses_exact_symbolic_same_row_output_identity(string formula,bool valid)=>
        Assert.Equal(valid,ExcelPayrollSetupRepository.HistoricalNetClosure(formula,2,new Dictionary<int,int>{{6,1},{7,-1}}));

    [Fact]
    public void Formula_depth_and_size_are_bounded()
    {
        Assert.False(ExcelPayrollSetupRepository.HistoricalNetClosure(new string('(',100)+"G2-H2"+new string(')',100),2,new Dictionary<int,int>{{6,1},{7,-1}}));
        Assert.False(ExcelPayrollSetupRepository.HistoricalNetClosure(new string(' ',5000)+"G2-H2",2,new Dictionary<int,int>{{6,1},{7,-1}}));
    }

    [Fact]
    public void Optional_PT_contract_preserves_original_JSON_shape()
    {
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.DoesNotContain("professionalTax",JsonSerializer.Serialize(new HistoricalPeriodSalaryRequest(),options),StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("professionalTax",JsonSerializer.Serialize(new HistoricalPeriodSalaryPreviewRow(),options),StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("professionalTax",JsonSerializer.Serialize(new HistoricalPeriodSalaryReceipt(),options),StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("historicalReceiptId",JsonSerializer.Serialize(new ReviewedProfessionalTaxControl(),options));
    }

    [Theory]
    [InlineData("false")][InlineData("positive")][InlineData("missing")][InlineData("duplicate")][InlineData("wrongRow")][InlineData("hash")][InlineData("version")]
    public void Zero_authority_review_is_explicit_and_exact_for_every_selected_pair(string mode)
    {
        var request=new HistoricalPeriodSalaryRequest{Assignments=[new("source",12,"1")],PeriodProfessionalTaxReview=new(){Version=1,Reviewed=true,SourceHash=new string('a',64),Reason="Reviewed closed source only",Rows=[new(12,"source",0)]}};
        ExcelPayrollSetupRepository.ValidateHistoricalPeriodProfessionalTaxReview(request);
        var review=request.PeriodProfessionalTaxReview;
        switch(mode){case "false":review.Reviewed=false;break;case "positive":review.Rows=[new(12,"source",1)];break;case "missing":review.Rows=[];break;case "duplicate":review.Rows.Add(review.Rows[0]);break;case "wrongRow":review.Rows=[new(13,"foreign",0)];break;case "version":review.Version=2;break;default:review.SourceHash="stale";break;}
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateHistoricalPeriodProfessionalTaxReview(request));
    }
}
