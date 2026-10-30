using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollIdentityColumnReviewTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed class Fixture
    {
        public ExcelPayslipBatch Batch { get; } = new() { Id=new string('a',32), ClientId=11, Month="2026-09",
            Rows=[new() { Id="one", SourceRow=9, EmployeeCode="SRC001", EmployeeName="Example Person" },
                new() { Id="two", SourceRow=10, EmployeeCode="SRC002", EmployeeName="Other Person" }] };
        public ExcelPayslipCalculationSource Source { get; } = new() { ColumnCount=2,
            Columns=[new() { ColumnIndex=0, SourceHeader="UAN-E" },new() { ColumnIndex=1, SourceHeader="UAN-AB" }],
            Rows=[new() { SourceRow=9, Cells=[new() { DataType="Text",Value="100000000002" },new() { DataType="Text",Value="100000000001" }] },
                new() { SourceRow=10, Cells=[new() { DataType="Text",Value="100000000004" },new() { DataType="Text",Value="100000000003" }] }] };
        public ExcelPayrollSetupRepository.Identity Master { get; } = new() { Id=7,ClientId=11,EmployeeCode="NATIVE007",Name="Example Person",Uan="100000000001",IsActive=true,EligibleForMonth=true };
        public ExcelPayrollSetupRequest Request { get; }
        public Fixture()
        {
            var candidate=ExcelPayrollSetupRepository.IdentityColumnCandidates(Batch,Source,["one"]).Single(c=>c.ColumnIndex==1);
            Request=new() { BatchId=Batch.Id,RowIds=["one"],TransferReviewVersion=1,ImportAttendance=false,
                EmployeeDecisions=[new() {RowId="one",MatchedEmployeeId=7}],
                IdentityColumnReview=new() {ColumnIndex=1,SourceHash=candidate.SourceHash,SelectionFingerprint=candidate.SelectionFingerprint,Reason="Original worksheet UAN header and captured column were independently correlated.",Reviewed=true} };
        }
        public ExcelPayrollSetupResult Receipt() => new() {EmployeeIds=[7],TransferRows=[new("one",7,30,26,30,0,[])],
            Transfer=new() {Version=1,SourceBatchId=Batch.Id,SourceMonth=Batch.Month,SourceHash=ExcelPayslipRepository.EmploymentSourceHash(Batch,Source)} };
    }

    [Fact]
    public void ExplicitScopedRoleFixesMappingWithoutChangingSourceOrMaster()
    {
        var f=new Fixture(); var before=JsonSerializer.Serialize(new {f.Batch,f.Source,f.Master});
        Assert.Empty(ExcelPayrollSetupRepository.SalaryOnlyBankReviewRows(f.Batch,f.Source,[f.Master]));
        var column=ExcelPayrollSetupRepository.ReviewedIdentityColumn(f.Batch,f.Source,f.Request);
        Assert.Equal(1,column);
        var map=new Dictionary<string,int> { ["one"]=column!.Value };
        Assert.Equal(new[]{"one"},ExcelPayrollSetupRepository.SalaryOnlyBankReviewRows(f.Batch,f.Source,[f.Master],map));
        Assert.Null(ExcelPayrollSetupRepository.ReviewedColumnForRow(map,"two"));
        Assert.Equal("100000000004",ExcelPayrollSetupRepository.SourceInformation(f.Batch.Rows[1],f.Source,reviewedUanColumn:ExcelPayrollSetupRepository.ReviewedColumnForRow(map,"two"))["UAN"]);
        Assert.Equal(before,JsonSerializer.Serialize(new {f.Batch,f.Source,f.Master}));
    }

    [Theory]
    [InlineData("source")][InlineData("rows")][InlineData("column")][InlineData("unreviewed")][InlineData("reason")]
    [InlineData("attendance")][InlineData("create")][InlineData("legacy")][InlineData("allocation")][InlineData("scope")]
    public void ChangedOrUnreviewedScopeIsRejected(string change)
    {
        var f=new Fixture();
        switch(change) {
            case "source": f.Source.Rows[1].Cells[0]!.Value="changed"; break;
            case "rows": f.Request.RowIds.Add("two"); break;
            case "column": f.Request.IdentityColumnReview!.ColumnIndex=0; break;
            case "unreviewed": f.Request.IdentityColumnReview!.Reviewed=false; break;
            case "reason": f.Request.IdentityColumnReview!.Reason=""; break;
            case "attendance": f.Request.ImportAttendance=true; break;
            case "create": f.Request.EmployeeDecisions[0].AllowCreate=true; break;
            case "legacy": f.Request.TransferReviewVersion=0; break;
            case "allocation": f.Request.LocationAllocations=new(); break;
            case "scope": f.Request.IdentityColumnReview!.SelectionFingerprint=new string('b',64); break;
        }
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ReviewedIdentityColumn(f.Batch,f.Source,f.Request));
    }

    [Theory]
    [InlineData("10000000000")][InlineData("1.00000000001E+11")][InlineData("0000-0000-0001")][InlineData("")]
    public void UnreliableValuesAreNotReconstructed(string value)
    {
        var f=new Fixture();f.Source.Rows[0].Cells[1]!.Value=value;
        Assert.Equal(1,ExcelPayrollSetupRepository.IdentityColumnCandidates(f.Batch,f.Source,["one"]).Single(c=>c.ColumnIndex==1).InvalidOrMissingRows);
    }

    [Theory]
    [InlineData("formula")][InlineData("error")][InlineData("calculation")][InlineData("cache")][InlineData("type")]
    public void FormulaOrUnreliableCellCannotBeAnIdentityLiteral(string change)
    {
        var f=new Fixture();var cell=f.Source.Rows[0].Cells[1]!;
        switch(change) {case "formula":cell.FormulaText="A1";break;case "error":cell.Error="#VALUE!";break;case "calculation":cell.CalculationError="error";break;case "cache":cell.MissingCachedValue=true;break;case "type":cell.DataType="DateTime";break;}
        Assert.Equal(1,ExcelPayrollSetupRepository.IdentityColumnCandidates(f.Batch,f.Source,["one"]).Single(c=>c.ColumnIndex==1).InvalidOrMissingRows);
    }

    [Theory]
    [InlineData("uan")][InlineData("name")][InlineData("duplicate")][InlineData("other-client")]
    public void ReviewedRoleNeverWaivesStrongIdentityOrNameContradictions(string change)
    {
        var f=new Fixture();var masters=new List<ExcelPayrollSetupRepository.Identity>{f.Master};
        if(change=="uan")f.Master.Uan="100000000005";
        if(change=="name")f.Master.Name="Different Person";
        if(change=="other-client")f.Master.ClientId=12;
        if(change=="duplicate")masters.Add(new() {Id=8,ClientId=11,EmployeeCode="NATIVE008",Name=f.Master.Name,Uan=f.Master.Uan});
        Assert.Empty(ExcelPayrollSetupRepository.SalaryOnlyBankReviewRows(f.Batch,f.Source,masters,new Dictionary<string,int>{{"one",1}}));
    }

    [Fact]
    public void AppliedRoleIsBoundToImmutableReceiptAndUsedByEmploymentResolver()
    {
        var f=new Fixture();var receipt=f.Receipt();
        Assert.Equal(1,ExcelPayrollSetupRepository.ReviewedIdentityColumns(f.Batch,f.Source,[(f.Request,receipt)])["one"]);
        var master=new ExcelPayslipRepository.EmploymentMaster {Id=7,ClientId=11,EmployeeCode="NATIVE007",FirstName="Example",LastName="Person",Uan=f.Master.Uan,IsActive=true,Eligible=true};
        ExcelPayslipRepository.AnnotateEmployment(f.Batch,f.Source,[master],[new(f.Request,receipt)]);
        Assert.Equal(7,f.Batch.Rows[0].Employment?.EmployeeId);
        Assert.Null(f.Batch.Rows[1].Employment?.EmployeeId);
        f.Batch.Rows[0].Employment=null;
        receipt.Transfer!.SourceMonth="2026-10";
        Assert.Empty(ExcelPayrollSetupRepository.ReviewedIdentityColumns(f.Batch,f.Source,[(f.Request,receipt)]));
    }

    [Fact]
    public void DefaultSerializationDoesNotChangeLegacyRequestHashes()
    {
        var request=new ExcelPayrollSetupRequest();
        Assert.DoesNotContain("identityColumnReview",JsonSerializer.Serialize(request,Json));
    }
}
