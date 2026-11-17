using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed partial class PayrollReferenceInformationDatabaseTests
{
    private async Task PrepareVerifiedEmptyDecisionSourceAsync(bool retainSignedBankIdentity=false)
    {
        // A valid applied historical receipt can omit caller matching decisions
        // after strict government ownership. Recreate that real contract with
        // an independent literal UAN, without changing its native money/roster.
        source.ColumnCount=6;source.Columns.Add(new(){ColumnIndex=5,SourceHeader="UAN",Label="UAN",Kind="info"});
        foreach(var (row,index)in source.Rows.Select((row,index)=>(row,index)))
        {while(row.Cells.Count<6)row.Cells.Add(null);row.Cells[5]=new(){DataType="Text",Value=index==0?"201234567801":"201234567802"};}
        sourceHash=PayrollReferenceInformation.Hash(new{Batch=batch,Source=source});
        await using var db=await F.OpenAsync();
        await db.ExecuteAsync("INSERT INTO employeepersonaldetails(EmployeeId,UanNumber) VALUES(@One,'201234567801'),(@Two,'201234567802')",new{One,Two});
        await db.ExecuteAsync("UPDATE excel_payslip_calculation_sources SET source_json=CAST(@Source AS JSON) WHERE client_id=@Client AND batch_id=@Batch",new{Client,Batch,Source=JsonSerializer.Serialize(source,Json)});
        var code=$"closed_salary_receipt:{Client}:{ReceiptId}";
        var envelope=JsonNode.Parse((await db.ExecuteScalarAsync<string>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@Client AND ModuleCode=@Code",new{Client,Code=code}))!)!;
        var plain=JsonNode.Parse(Protector.UnprotectHistoricalSalary(envelope["payload"]!.ToString())!)!;
        plain["sourceHash"]=sourceHash;plain["reviewedRequest"]!["initialSourceTargetReview"]!["sourceHash"]=sourceHash;
        plain["reviewedRequest"]!["setup"]!["employeeDecisions"]=new JsonArray();
        foreach(var target in plain["receipt"]!["initialSourceTargets"]!.AsArray())target!["sourceHash"]=sourceHash;
        if(retainSignedBankIdentity)foreach(var identity in plain["receipt"]!["sourceIdentities"]!.AsArray())identity!["sourceHash"]=sourceHash;
        else plain["receipt"]!["sourceIdentities"]=new JsonArray();
        envelope["payload"]=Protector.ProtectHistoricalSalary(plain.ToJsonString());
        await db.ExecuteAsync("UPDATE modulesettings SET SettingsJson=CAST(@Json AS JSON) WHERE client_id=@Client AND ModuleCode=@Code",new{Client,Code=code,Json=envelope.ToJsonString()});
    }

    [LocalTheory][InlineData(false)][InlineData(true)]
    public async Task Actual_empty_decision_receipt_rechecks_unique_government_identity_and_captures_full_budget_without_financial_changes(bool retainSignedBankIdentity)
    {
        await PrepareVerifiedEmptyDecisionSourceAsync(retainSignedBankIdentity);var repo=Repo();var before=await WitnessAsync();
        var preview=await repo.PreviewReferenceInformationAsync(Request,Actor);
        Assert.Equal(before,await WitnessAsync());Assert.Equal(2,preview.Rows.Count);Assert.Equal(25000,preview.Rows.Single(row=>row.EmployeeId==Two).MonthlyBudget);
        var id=Guid.NewGuid().ToString("N");var command=new PayrollReferenceInformationApplyRequest(preview.ReviewToken,id,true);
        var receipt=await repo.ApplyReferenceInformationAsync(command,Actor);
        Assert.Equal(before,await WitnessAsync($"prfi:run:{runId}",$"prfi:req:{id}"));Assert.Equal(2,receipt.EmployeeCount);
        var saved=await WitnessAsync();Assert.Equal(receipt,await repo.ApplyReferenceInformationAsync(command,Actor));Assert.Equal(saved,await WitnessAsync());
        Assert.Equal(25000,(await ReadAsync(false)).Value);Assert.Equal(saved,await WitnessAsync());
    }

    [LocalTheory][InlineData("missingGovernment")][InlineData("sharedGovernment")][InlineData("foreignOwner")][InlineData("changedName")]
    [InlineData("wrongDecision")][InlineData("partialDecision")][InlineData("duplicateDecision")][InlineData("createDecision")]
    public async Task Empty_decision_metadata_does_not_waive_ownership_name_or_nonempty_decision_errors(string change)
    {
        await PrepareVerifiedEmptyDecisionSourceAsync();await using(var db=await F.OpenAsync())
        {
            if(change=="missingGovernment")await db.ExecuteAsync("UPDATE employeepersonaldetails SET UanNumber='' WHERE EmployeeId=@Two",new{Two});
            if(change=="sharedGovernment")await db.ExecuteAsync("UPDATE employeepersonaldetails SET UanNumber='201234567802' WHERE EmployeeId=@One",new{One});
            if(change=="foreignOwner")await db.ExecuteAsync("UPDATE employeepersonaldetails SET UanNumber=CASE EmployeeId WHEN @One THEN '201234567802' ELSE '201234567801' END WHERE EmployeeId IN(@One,@Two)",new{One,Two});
            if(change=="changedName")await db.ExecuteAsync("UPDATE employees SET LastName='Changed' WHERE Id=@Two",new{Two});
            if(change.EndsWith("Decision",StringComparison.Ordinal))
            {
                var code=$"closed_salary_receipt:{Client}:{ReceiptId}";var envelope=JsonNode.Parse((await db.ExecuteScalarAsync<string>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@Client AND ModuleCode=@Code",new{Client,Code=code}))!)!;
                var plain=JsonNode.Parse(Protector.UnprotectHistoricalSalary(envelope["payload"]!.ToString())!)!;
                var decisions=new JsonArray(batch.Rows.Select((row,index)=>JsonSerializer.SerializeToNode(new{rowId=row.Id,matchedEmployeeId=index==0?One:Two,allowCreate=false})!).ToArray());
                if(change=="wrongDecision")decisions[0]!["matchedEmployeeId"]=Two;
                if(change=="partialDecision")decisions.RemoveAt(1);
                if(change=="duplicateDecision")decisions[1]=decisions[0]!.DeepClone();
                if(change=="createDecision")decisions[0]!["allowCreate"]=true;
                plain["reviewedRequest"]!["setup"]!["employeeDecisions"]=decisions;envelope["payload"]=Protector.ProtectHistoricalSalary(plain.ToJsonString());
                await db.ExecuteAsync("UPDATE modulesettings SET SettingsJson=CAST(@Json AS JSON) WHERE client_id=@Client AND ModuleCode=@Code",new{Client,Code=code,Json=envelope.ToJsonString()});
            }
        }
        var before=await WitnessAsync();await Assert.ThrowsAsync<InvalidOperationException>(()=>Repo().PreviewReferenceInformationAsync(Request,Actor));Assert.Equal(before,await WitnessAsync());
    }

    [LocalFact]
    public async Task Empty_decision_apply_rechecks_ownership_after_preview_and_does_not_write_partial_metadata()
    {
        await PrepareVerifiedEmptyDecisionSourceAsync();var repo=Repo();var preview=await repo.PreviewReferenceInformationAsync(Request,Actor);
        await using(var db=await F.OpenAsync())await db.ExecuteAsync("UPDATE employeepersonaldetails SET UanNumber='201234567802' WHERE EmployeeId=@One",new{One});
        var before=await WitnessAsync();await Assert.ThrowsAsync<InvalidOperationException>(()=>repo.ApplyReferenceInformationAsync(new(preview.ReviewToken,Guid.NewGuid().ToString(),true),Actor));Assert.Equal(before,await WitnessAsync());
    }
}
