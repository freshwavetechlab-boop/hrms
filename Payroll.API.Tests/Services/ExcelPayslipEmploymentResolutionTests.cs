using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipEmploymentResolutionTests
{
    private static ExcelPayslipBatch Batch() => new() {
        Id=new string('a',32), ClientId=11, Month="2026-09",
        Rows=[new() { Id="source-one", SourceRow=9, EmployeeCode="SOURCE009", EmployeeName="Example Employee" }]
    };
    private static ExcelPayslipCalculationSource Source() => new() {
        Columns=[new() { ColumnIndex=0, SourceHeader="PAN No." },new() { ColumnIndex=1, SourceHeader="Location Name (Tehsil/Sub Tehsil)" },
            new() { ColumnIndex=2, SourceHeader="District" },new() { ColumnIndex=3, SourceHeader="UAN-AB" }],
        Rows=[new() { SourceRow=9, Cells=[new() { Value="ABCDE1234F" },new() { Value="North" },new() { Value="Example District" },new() { Value="100000000099" }] }]
    };
    private static ExcelPayslipRepository.EmploymentMaster Master(int id=7, string code="NATIVE007") => new() {
        Id=id, ClientId=11, EmployeeCode=code, FirstName="Example", LastName="Employee", Pan="ABCDE1234F",
        WorkLocationId=4, WorkLocation="North Office", LocationCity="Example District", IsActive=true, Eligible=true
    };
    private static ExcelPayslipRepository.AppliedEmploymentReview Receipt(ExcelPayslipBatch batch, ExcelPayslipCalculationSource source) => new(
        new() { BatchId=batch.Id, RowIds=["source-one"], TransferReviewVersion=1,
            EmployeeDecisions=[new() { RowId="source-one", MatchedEmployeeId=7 }],
            TemplateScope=new() { Reviewed=true, LocationMode="Listed", WorkLocationIds=[4] } },
        new() { ImportId="receipt-one", EmployeeIds=[7], TransferRows=[new("source-one",7,30,26,30,0,[])],
            Transfer=new() { SourceBatchId=batch.Id, SourceMonth=batch.Month, SourceHash=ExcelPayslipRepository.EmploymentSourceHash(batch,source) } });

    [Fact]
    public void SavedReviewedGeneratedCodeUsesCanonicalStrongIdentityAndPreservesAllInputs()
    {
        var batch=Batch(); var source=Source(); var master=Master(); var receipt=Receipt(batch,source);
        var before=JsonSerializer.Serialize(new {source,master,receipt});
        ExcelPayslipRepository.AnnotateEmployment(batch,source,[master],[receipt]);
        Assert.Equal(7,batch.Rows[0].Employment?.EmployeeId);
        Assert.Equal("NATIVE007",batch.Rows[0].Employment?.EmployeeCode);
        Assert.Equal("SOURCE009",batch.Rows[0].EmployeeCode);
        Assert.False(batch.Rows[0].Employment?.IdentityReviewRequired);
        Assert.Equal(before,JsonSerializer.Serialize(new {source,master,receipt}));
    }

    [Theory]
    [InlineData("source")][InlineData("month")][InlineData("batch")][InlineData("employee")]
    [InlineData("decision")][InlineData("duplicate-decision")][InlineData("duplicate-receipt-row")]
    [InlineData("scope")][InlineData("unreviewed")][InlineData("kept")]
    public void AliasLocationNeedsAnExactCurrentReviewedReceipt(string change)
    {
        var batch=Batch(); var source=Source(); var receipt=Receipt(batch,source);
        switch(change) {
            case "source": source.Rows[0].Cells[0]!.FormulaText="=A1"; break;
            case "month": receipt.Result.Transfer!.SourceMonth="2026-10"; break;
            case "batch": receipt.Request.BatchId=new string('b',32); break;
            case "employee": receipt.Result.EmployeeIds=[8]; break;
            case "decision": receipt.Request.EmployeeDecisions[0].MatchedEmployeeId=8; break;
            case "duplicate-decision": receipt.Request.EmployeeDecisions.Add(receipt.Request.EmployeeDecisions[0]); break;
            case "duplicate-receipt-row": receipt.Result.TransferRows.Add(receipt.Result.TransferRows[0]); break;
            case "scope": receipt.Request.TemplateScope!.WorkLocationIds=[8]; break;
            case "unreviewed": receipt.Request.TemplateScope!.Reviewed=false; break;
            case "kept": receipt.Result.KeptRows.Add(new("source-one",7,"old","keep")); break;
        }
        ExcelPayslipRepository.AnnotateEmployment(batch,source,[Master()],[receipt]);
        Assert.Null(batch.Rows[0].Employment?.EmployeeId);
    }

    [Theory]
    [InlineData("pan")][InlineData("district")][InlineData("name")][InlineData("other-client")]
    public void CurrentMasterContradictionsNeverUseTheReceiptAsAuthority(string change)
    {
        var batch=Batch(); var source=Source(); var receipt=Receipt(batch,source); var master=Master();
        if(change=="pan") master.Pan="FGHIJ5678K";
        if(change=="district") master.LocationCity="Other District";
        if(change=="name") master.FirstName="Different";
        if(change=="other-client") master.ClientId=12;
        ExcelPayslipRepository.AnnotateEmployment(batch,source,[master],[receipt]);
        Assert.Null(batch.Rows[0].Employment?.EmployeeId);
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public void DuplicateStrongIdentifierOrAnotherOwnerOfTheSourceCodeBlocks(bool duplicatePan)
    {
        var batch=Batch(); var source=Source(); var receipt=Receipt(batch,source);
        var other=Master(8,duplicatePan ? "NATIVE008" : "SOURCE009");
        if(!duplicatePan) other.Pan="FGHIJ5678K";
        ExcelPayslipRepository.AnnotateEmployment(batch,source,[Master(),other],[receipt]);
        Assert.Null(batch.Rows[0].Employment?.EmployeeId);
    }

    [Fact]
    public void NameAndLocationAloneCannotSupplyPayrollIdentity()
    {
        var batch=Batch(); var source=Source(); source.Rows[0].Cells[0]!.Value=""; source.Rows[0].Cells[1]!.Value="North Office";
        ExcelPayslipRepository.AnnotateEmployment(batch,source,[Master()],[]);
        Assert.Null(batch.Rows[0].Employment?.EmployeeId);
    }

    [Fact]
    public void ExactLocationAndUniquePanNeedNoInventedSourceCodeAlias()
    {
        var batch=Batch(); var source=Source(); source.Rows[0].Cells[1]!.Value="North Office";
        ExcelPayslipRepository.AnnotateEmployment(batch,source,[Master()],[]);
        Assert.Equal(7,batch.Rows[0].Employment?.EmployeeId);
    }

    [Fact]
    public void CanonicalUanColumnIsUsedInsteadOfTheIgnoredAlternateHeader()
    {
        var batch=Batch(); var source=Source();
        source.Columns.Add(new() { ColumnIndex=4, SourceHeader="UAN-E" });
        source.Rows[0].Cells.Add(new() { Value="100000000001" });
        var receipt=Receipt(batch,source); var master=Master(); master.Uan="100000000001";
        ExcelPayslipRepository.AnnotateEmployment(batch,source,[master],[receipt]);
        Assert.Equal(7,batch.Rows[0].Employment?.EmployeeId);
    }

    [Fact]
    public void ExitedEmployeesRemainBlockedForTheSourcePeriod()
    {
        var batch=Batch(); var source=Source(); var receipt=Receipt(batch,source); var master=Master(); master.Eligible=false;
        ExcelPayslipRepository.AnnotateEmployment(batch,source,[master],[receipt]);
        Assert.True(batch.Rows[0].Employment?.BlockedForMonth);
    }

    [Fact]
    public void RepeatedSourceIdentityCannotCreateTwoCalculationTargets()
    {
        var batch=Batch(); var source=Source(); source.Rows[0].Cells[1]!.Value="North Office";
        batch.Rows.Add(new() { Id="source-two", SourceRow=10, EmployeeCode="SOURCE010", EmployeeName="Example Employee", Information=[new() { Label="PAN",Value="ABCDE1234F" }] });
        ExcelPayslipRepository.AnnotateEmployment(batch,source,[Master()],[]);
        Assert.All(batch.Rows,r => Assert.Null(r.Employment?.EmployeeId));
    }
}
