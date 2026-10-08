using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipMasterIdentityTests
{
    private static ExcelPayslipRepository.MasterEmployeeIdentity Employee(int id = 1, int client = 11, string code = "PLRS040", string location = "Mohali") => new()
    {
        Id = id, ClientId = client, EmployeeCode = code, EmployeeName = "Rahul Kumar", WorkLocation = location,
        Pan = "ABCDE1234F", Uan = "100000000001", Aadhaar = "234567890123"
    };
    private static ExcelPayslipBatch Batch(string location = "Mohali", string code = "", params ExcelPayslipText[] information) => new()
    {
        ClientId = 11, Rows = [new() { Id = "row-3", SourceRow = 3, EmployeeName = "Rahul Kumar", EmployeeCode = code,
            Information = new List<ExcelPayslipText> { new() { Label = "Location Name (Tehsil/Sub Tehsil)", Value = location } }.Concat(information).ToList() }]
    };

    [Fact]
    public void SameClientNameAndLocationReuseMasterCodeAndKeepSameNamePeopleSeparate()
    {
        var batch = Batch("  mohali  ");
        ExcelPayslipRepository.AssignEmployeeCodes(batch, null, "PLRS", [],
            [Employee(), Employee(2, code: "PLRS041", location: "Ludhiana"), Employee(3, 12, "OTHER001")]);
        Assert.Equal("PLRS040", batch.Rows[0].EmployeeCode);
    }

    [Fact]
    public void AnotherClientsIdentityNeverMatchesOrAffectsTheNewCodeSequence()
    {
        var batch = Batch();
        ExcelPayslipRepository.AssignEmployeeCodes(batch, null, "PLRS", [], [Employee(client: 12, code: "PLRS999")]);
        Assert.Equal("PLRS00001", batch.Rows[0].EmployeeCode);
    }

    [Fact]
    public void NameAloneNeverMatchesAndAllMasterCodesAreReservedForAllocation()
    {
        var batch = Batch("");
        ExcelPayslipRepository.AssignEmployeeCodes(batch, null, "PLRS", ["PLRS041"], [Employee(code: "PLRS099")]);
        Assert.Equal("PLRS100", batch.Rows[0].EmployeeCode);
    }

    [Fact]
    public void SuppliedCodeIsPreservedEvenWhenMasterDataWouldOtherwiseMatch()
    {
        var batch = Batch(code: "original-001");
        ExcelPayslipRepository.AssignEmployeeCodes(batch, null, "PLRS", [], [Employee(), Employee(2)]);
        Assert.Equal("original-001", batch.Rows[0].EmployeeCode);
    }

    [Fact]
    public void StableIdentifierCanResolveDuplicateNameLocationAndAChangedLocation()
    {
        var first = Employee(); var second = Employee(2, code: "PLRS041");
        second.Pan = "FGHIJ5678K"; second.Uan = "100000000002";
        var batch = Batch(information: [new() { Label = "PAN No.", Value = "abcde1234f" }]);
        ExcelPayslipRepository.AssignEmployeeCodes(batch, null, "PLRS", [], [first, second]);
        Assert.Equal("PLRS040", batch.Rows[0].EmployeeCode);
        var moved = Batch("Chandigarh", information: [new() { Label = "UAN", Value = "1000 0000 0001" }]);
        ExcelPayslipRepository.AssignEmployeeCodes(moved, null, "PLRS", [], [first]);
        Assert.Equal("PLRS040", moved.Rows[0].EmployeeCode);
    }

    [Theory]
    [InlineData("PLRS010", "PLRS024", "PLRS025")]
    [InlineData("PLRS030", "PLRS031", "PLRS032")]
    public void DuplicateNameLocationAllocatesAfterHighestClientCodeAndPersistsCodesInSource(string historicalCode, string firstCode, string secondCode)
    {
        var batch = Batch();
        batch.Rows[0].Id = "row-94"; batch.Rows[0].SourceRow = 94;
        var second = Batch().Rows[0]; second.Id = "row-95"; second.SourceRow = 95; batch.Rows.Add(second);
        var source = new ExcelPayslipCalculationSource
        {
            HeaderRow = 2, ColumnCount = 2,
            Columns = [new() { ColumnIndex = 0, Kind = "employeeCode" }],
            Rows = batch.Rows.Select(row => new ExcelPayslipCalculationSourceRow
            {
                SourceRow = row.SourceRow, Cells = [new() { Value = "" }, new() { Value = "1000", FormulaText = "10*100" }]
            }).ToList()
        };
        ExcelPayslipRepository.AssignEmployeeCodes(batch, source, "PLRS", [historicalCode],
            [Employee(code: "PLRS022"), Employee(2, code: "PLRS023")]);
        Assert.Equal(new[] { firstCode, secondCode }, batch.Rows.Select(row => row.EmployeeCode));
        Assert.Equal(new[] { firstCode, secondCode }, source.Rows.Select(row => row.Cells[0]!.Value));
        Assert.All(source.Rows, row => { Assert.Equal("10*100", row.Cells[1]!.FormulaText); Assert.Equal("1000", row.Cells[1]!.Value); });
    }

    [Fact]
    public void DuplicateStrongIdentifierRequiresReviewAndDoesNotExposeIdentifier()
    {
        var batch = Batch(information: [new() { Label = "PAN", Value = "ABCDE1234F" }]);
        var error = Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.AssignEmployeeCodes(batch, null, "PLRS", [],
            [Employee(), Employee(2, code: "PLRS041", location: "Ludhiana")]));
        Assert.Contains("multiple Employee Master", error.Message);
        Assert.DoesNotContain("ABCDE1234F", error.Message);
    }

    [Fact]
    public void DifferentIdentifiersCannotSelectDifferentMasterEmployees()
    {
        var first = Employee(); var second = Employee(2, code: "PLRS041", location: "Ludhiana");
        second.Pan = "FGHIJ5678K"; second.Uan = "100000000002";
        var batch = Batch(information: [new() { Label = "PAN", Value = first.Pan }, new() { Label = "UAN", Value = second.Uan }]);
        Assert.Contains("different employees", Assert.Throws<InvalidOperationException>(() =>
            ExcelPayslipRepository.AssignEmployeeCodes(batch, null, "PLRS", [], [first, second])).Message);
    }

    [Fact]
    public void ConflictingUnmatchedIdentifierBlocksNameLocationFallback()
    {
        var batch = Batch(information: [new() { Label = "UAN", Value = "100000000009" }]);
        Assert.Contains("conflicts", Assert.Throws<InvalidOperationException>(() =>
            ExcelPayslipRepository.AssignEmployeeCodes(batch, null, "PLRS", [], [Employee()])).Message);
    }

    [Fact]
    public void InvalidOrMaskedIdentityDoesNotClaimAMasterRecord()
    {
        var batch = Batch("", information: [new() { Label = "Aadhaar", Value = "XXXXXXXX0123" }, new() { Label = "PAN", Value = "N/A" }]);
        ExcelPayslipRepository.AssignEmployeeCodes(batch, null, "PLRS", [], [Employee()]);
        Assert.Equal("PLRS041", batch.Rows[0].EmployeeCode);
    }

    [Theory]
    [InlineData("Adhaar")]
    [InlineData("Adhaar No.")]
    [InlineData("Adhaar Number")]
    public void PlrsAdhaarHeaderSpellingReusesExistingMasterCode(string label)
    {
        var batch = Batch("", information: [new() { Label = label, Value = "2345 6789 0123" }]);
        ExcelPayslipRepository.AssignEmployeeCodes(batch, null, "PLRS", [], [Employee()]);
        Assert.Equal("PLRS040", batch.Rows[0].EmployeeCode);
    }

    [Fact]
    public void TwoRowsCannotAcquireTheSameMasterCode()
    {
        var batch = Batch(); batch.Rows.Add(Batch().Rows[0]); batch.Rows[1].Id = "row-4"; batch.Rows[1].SourceRow = 4;
        Assert.Contains("more than once", Assert.Throws<InvalidOperationException>(() =>
            ExcelPayslipRepository.AssignEmployeeCodes(batch, null, "PLRS", [], [Employee()])).Message);
    }

    [Fact]
    public void ReusedMasterCodeFillsExistingBlankSourceCellWithoutChangingFormulas()
    {
        var batch = Batch();
        var source = new ExcelPayslipCalculationSource
        {
            HeaderRow = 2, ColumnCount = 2,
            Columns = [new() { ColumnIndex = 0, Kind = "employeeCode" }],
            Rows = [new() { SourceRow = 3, Cells = [new() { Value = "" }, new() { Value = "1000", FormulaText = "10*100" }] }]
        };
        ExcelPayslipRepository.AssignEmployeeCodes(batch, source, "PLRS", [], [Employee()]);
        Assert.Equal("PLRS040", source.Rows[0].Cells[0]!.Value);
        Assert.Equal("10*100", source.Rows[0].Cells[1]!.FormulaText);
    }
}
