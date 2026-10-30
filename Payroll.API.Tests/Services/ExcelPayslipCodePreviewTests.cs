using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipCodePreviewTests
{
    [Fact]
    public void CodePreviewCountsExistingInactiveAndAmbiguousCodesWithoutExposingIdentity()
    {
        var request=new ExcelPayslipCodePreviewRequest([" nhdc00001 ","NHDC00001","NEW001","0007","DUP001"]);
        var before=JsonSerializer.Serialize(request);
        var result=ExcelPayslipRepository.BuildCodePreview(request,[new("NHDC00001",true),new("0007",false),new("DUP001",true),new("dup001",false),new("UNREQUESTED",true)]);
        Assert.Equal(4,result.CheckedCodes);Assert.Equal(3,result.Matches.Count);
        Assert.Equal(new ExcelPayslipCodeMatch("NHDC00001",1,1),result.Matches.Single(m=>m.Code=="NHDC00001"));
        Assert.Equal(new ExcelPayslipCodeMatch("0007",1,0),result.Matches.Single(m=>m.Code=="0007"));
        Assert.Equal(new ExcelPayslipCodeMatch("DUP001",2,1),result.Matches.Single(m=>m.Code=="DUP001"));
        Assert.DoesNotContain(result.Matches,m=>m.Code=="NEW001"||m.Code=="UNREQUESTED");
        Assert.Equal(before,JsonSerializer.Serialize(request));
        var properties=JsonDocument.Parse(JsonSerializer.Serialize(result.Matches[0])).RootElement.EnumerateObject().Select(p=>p.Name).ToArray();
        Assert.Equal(new[]{"Code","Records","ActiveRecords"},properties);
    }

    [Fact]
    public void LiteralCodesKeepLeadingZerosAndNormalizeTextWithoutNumericReconstruction()
    {
        var request=new ExcelPayslipCodePreviewRequest(["0007","7","ＮＨＤＣ００１","AB  001"]);
        var result=ExcelPayslipRepository.BuildCodePreview(request,[new("0007",true),new("NHDC001",true),new("AB 001",true)]);
        Assert.Equal(4,result.CheckedCodes);Assert.Equal(3,result.Matches.Count);
        Assert.DoesNotContain(result.Matches,m=>m.Code=="7");
    }

    [Fact]
    public void InvalidOrOversizedCodeScopeIsRejected()
    {
        foreach(var codes in new List<string>[] {null!,[new string('X',81)],["CODE\n1"],Enumerable.Repeat("X",1001).ToList(),[null!]})
            Assert.Throws<InvalidOperationException>(()=>ExcelPayslipRepository.BuildCodePreview(new(codes),[]));
        Assert.Empty(ExcelPayslipRepository.BuildCodePreview(new([]),[new("X",true)]).Matches);
    }
}
