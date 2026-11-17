using System.Text.Json;
using System.Text.Json.Nodes;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class LocationRowRebindingTests
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static ExcelPayrollLocationCorrectionRequest Request()
    {
        var fixture=ExcelPayrollLocationCorrectionTests.Create();fixture.Request.CreateMissingDropdownValues=false;
        return new(){PayRunId=9,BatchId=fixture.Batch.Id,EmployeeIds=[7],RecoverOrphanedLineage=true,SourceReview=fixture.Request,
            RecoverySourceHash=new string('a',64),RowRebindReview=new(){Version=1,Reviewed=true,Rows=[new(7,new string('b',32),"part","captured-row-3","source-one",3,new string('a',64))]}};
    }
    [Fact]
    public void Binding_is_omitted_from_legacy_requests_and_requires_explicit_recovery_review()
    {
        var request=Request();request.RowRebindReview=null;Assert.DoesNotContain("rowRebindReview",JsonSerializer.Serialize(request,Json));
        var same=JsonSerializer.Serialize(request,Json);request.RowRebindReview=null;Assert.Equal(same,JsonSerializer.Serialize(request,Json));
        request=Request();ExcelPayrollSetupRepository.ValidateLocationCorrectionScope(request);
        request.RowRebindReview!.Reviewed=false;Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateLocationCorrectionScope(request));
        request=Request();request.RecoverOrphanedLineage=false;Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateLocationCorrectionScope(request));
        request=Request();request.RowRebindReview!.Version=2;Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateLocationCorrectionScope(request));
        request=Request();request.RowRebindReview!.Rows[0]=request.RowRebindReview.Rows[0] with{CurrentRowId="captured-row-3"};Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateLocationCorrectionScope(request));
        request=Request();request.EmployeeIds.Add(8);Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateLocationCorrectionScope(request));
    }
    private static PayRunEmployee Financial()
    {
        var details=new JsonArray(new JsonObject{["Id"]="BASIC",["Category"]="Earning",["monthlyAmount"]=200,["amount"]=100.1234567m},
            new JsonObject{["Id"]="PF",["Category"]="Deduction",["monthlyAmount"]=10,["amount"]=10},
            new JsonObject{["Id"]="ER",["Category"]="Benefit",["monthlyAmount"]=20,["amount"]=20});
        return new(){EmployeeId=7,ClientId=42,PresentDays=30,PayableDays=30,MonthlyGross=200,GrossPay=100.12m,StatutoryDeductions=10,NetPay=90.12m,DetailsJson=details.ToJsonString()};
    }
    [Fact]
    public void Old_replay_proves_full_precision_lines_and_existing_paise_storage_without_evaluating_new_source_amounts()
    {
        var saved=Financial();var replay=Financial();replay.GrossPay=100.1234567m;replay.NetPay=90.1234567m;
        Assert.Equal(64,ExcelPayrollSetupRepository.RequireOldLocationFinancialReplay(saved,replay).Length);
        var details=JsonNode.Parse(replay.DetailsJson)!;details[0]!["amount"]=100.1234568m;replay.DetailsJson=details.ToJsonString();
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.RequireOldLocationFinancialReplay(saved,replay));
    }
    [Fact]
    public void Replay_witness_hash_binds_full_precision_line_values_as_well_as_aggregate_storage()
    {
        var saved=Financial();var replay=Financial();var original=ExcelPayrollSetupRepository.RequireOldLocationFinancialReplay(saved,replay);
        var details=JsonNode.Parse(saved.DetailsJson)!;details[0]!["amount"]=100.1234568m;saved.DetailsJson=details.ToJsonString();replay.DetailsJson=saved.DetailsJson;
        Assert.NotEqual(original,ExcelPayrollSetupRepository.RequireOldLocationFinancialReplay(saved,replay));
    }
    [Theory]
    [InlineData("employee")][InlineData("client")][InlineData("present")][InlineData("payable")][InlineData("net")][InlineData("employer")][InlineData("missing")]
    public void Rebinding_rejects_incomplete_or_different_old_financial_and_attendance_evidence(string change)
    {
        var saved=Financial();var replay=Financial();
        switch(change)
        {
            case "employee":replay.EmployeeId++;break;case "client":replay.ClientId++;break;case "present":replay.PresentDays=31;break;
            case "payable":replay.PayableDays=31;break;case "net":replay.NetPay++;break;case "missing":replay.DetailsJson="[]";break;
            case "employer":var lines=JsonNode.Parse(replay.DetailsJson)!;lines[2]!["amount"]=21;replay.DetailsJson=lines.ToJsonString();break;
        }
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.RequireOldLocationFinancialReplay(saved,replay));
    }
}
