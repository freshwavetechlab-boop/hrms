using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed partial class HistoricalPeriodSalaryDatabaseTests
{
    private const string BankOne = "00123456977120", BankTwo = "00123456977121";

    private async Task<HistoricalPeriodSalaryRequest> BankOwnershipRequestAsync(bool keepGovernment = false, bool rename = false)
    {
        const int bankColumn = 10;
        source.ColumnCount = 11;
        source.Columns.Add(new() { ColumnIndex = bankColumn, SourceHeader = "Account. No.", Label = "Bank account no", Kind = "info" });
        for (var index = 0; index < source.Rows.Count; index++)
        {
            source.Rows[index].Cells.Add(new() { DataType = "Text", Value = index == 0 ? BankOne : BankTwo });
            batch.Rows[index].Information.Add(new() { Label = "Bank account no", Value = index == 0 ? BankOne : BankTwo });
            if (!keepGovernment)
            {
                source.Rows[index].Cells[2]!.Value = "";
                batch.Rows[index].Information.Single(item => item.Label == "UAN").Value = "";
            }
        }
        await using (var db = await F.OpenAsync())
        {
            await db.ExecuteAsync("INSERT INTO employeepaymentdetails(EmployeeId,BankAccountNo) VALUES(@One,@BankOne),(@Two,@BankTwo)", new { One, Two, BankOne, BankTwo });
            if (!keepGovernment) await db.ExecuteAsync("UPDATE employeepersonaldetails SET UanNumber='' WHERE EmployeeId IN (@One,@Two)", new { One, Two });
            if (rename) await db.ExecuteAsync("UPDATE employees SET FirstName='Synthetic',LastName='Corrected' WHERE Id=@One", new { One });
            await db.ExecuteAsync("UPDATE excel_payslip_batches SET batch_json=CAST(@Batch AS JSON) WHERE client_id=@Client;UPDATE excel_payslip_calculation_sources SET source_json=CAST(@Source AS JSON) WHERE client_id=@Client",
                new { Client, Batch = JsonSerializer.Serialize(batch, Json), Source = JsonSerializer.Serialize(source, Json) });
        }
        var request = Request();
        request.SourceIdentityReview = new() { Version = 1, Reviewed = true,
            SourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Batch = batch, Source = source }, Json)))).ToLowerInvariant(),
            Reason = "Reviewed source account ownership and exact employee code/name for this closed month only.",
            Bindings = [new(batch.Rows[0].Id, One, bankColumn, batch.Rows[0].EmployeeName, rename ? "Synthetic Corrected" : "Synthetic One", rename)] };
        return request;
    }

    [IsolatedFact]
    public async Task ExplicitMissingGovernmentBankReviewSavesOnlyClosedHistoryAndProtectedEvidence()
    {
        var request = await BankOwnershipRequestAsync(); var before = await WitnessAsync();
        var defaultRequest = Request();
        var original = await Repository.PreviewHistoricalSalaryAsync(Client, defaultRequest, Actor);
        Assert.False(original.CanApply); Assert.Contains(original.Blockers, issue => issue.Contains("source-verified"));
        var preview = await Repository.PreviewHistoricalSalaryAsync(Client, request, Actor);
        Assert.True(preview.CanApply, string.Join(";", preview.Blockers)); Assert.NotNull(preview.Rows[0].ReviewedSourceIdentity);
        Assert.Equal(before, await WitnessAsync()); Assert.Equal(0, await WrittenAsync());
        var id = Guid.NewGuid().ToString("N"); var receipt = await ApplyAsync(preview, id);
        var evidence = Assert.Single(receipt.SourceIdentities!);
        Assert.Equal(One, evidence.EmployeeId); Assert.Equal(10, evidence.BankColumnIndex); Assert.False(evidence.ReviewedNameChange);
        Assert.Equal(before, await WitnessAsync()); Assert.Equal(1, await WrittenAsync());
        var replay = await ApplyAsync(preview, id); Assert.True(replay.Replayed); Assert.Equal(receipt.SourceIdentities, replay.SourceIdentities);
        Assert.Equal(1, await WrittenAsync());
        var actual = await F.Runs.SimulateEmployeeAsync(new() { Mode = "Employee", ClientId = Client, EmployeeId = One, PayPeriod = "2026-09" }, default);
        Assert.True(actual.Complete); Assert.Equal(8800m, actual.Net);
    }

    [IsolatedTheory]
    [InlineData("otherOwner")][InlineData("bankChanged")][InlineData("nameChanged")]
    public async Task BankOwnershipAndReviewedNamesAreRevalidatedAtAtomicApply(string mode)
    {
        var request = await BankOwnershipRequestAsync();
        var preview = await Repository.PreviewHistoricalSalaryAsync(Client, request, Actor);
        Assert.True(preview.CanApply, string.Join(";", preview.Blockers));
        await using (var db = await F.OpenAsync())
            await db.ExecuteAsync(mode switch { "otherOwner" => "UPDATE employeepaymentdetails SET BankAccountNo=@BankOne WHERE EmployeeId=@Two",
                "bankChanged" => "UPDATE employeepaymentdetails SET BankAccountNo='00123456977999' WHERE EmployeeId=@One",
                _ => "UPDATE employees SET LastName='Changed' WHERE Id=@One" }, new { One, Two, BankOne });
        await Assert.ThrowsAsync<SelectedPayRunConflictException>(() => ApplyAsync(preview));
        Assert.Equal(0, await WrittenAsync());
        await using var check = await F.OpenAsync();
        Assert.Equal(0, await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=@Client", new { Client }));
    }

    [IsolatedFact]
    public async Task ReviewedCorrectedNameNeedsBothGovernmentAndAccountOwnership()
    {
        var request = await BankOwnershipRequestAsync(keepGovernment: true, rename: true);
        var defaultPreview = await Repository.PreviewHistoricalSalaryAsync(Client, Request(), Actor);
        Assert.False(defaultPreview.CanApply); Assert.Contains(defaultPreview.Blockers, issue => issue.Contains("code/name differs"));
        var reviewed = await Repository.PreviewHistoricalSalaryAsync(Client, request, Actor);
        Assert.True(reviewed.CanApply, string.Join(";", reviewed.Blockers)); Assert.True(reviewed.Rows[0].ReviewedSourceIdentity!.ReviewedNameChange);
        var before = await WitnessAsync(); var receipt = await ApplyAsync(reviewed);
        Assert.True(Assert.Single(receipt.SourceIdentities!).ReviewedNameChange); Assert.Equal(before, await WitnessAsync());
    }
}
