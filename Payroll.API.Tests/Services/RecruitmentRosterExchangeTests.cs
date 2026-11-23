using System.IO.Compression;
using System.Security;
using System.Text;
using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class RecruitmentRosterExchangeTests
{
    private static readonly Dictionary<string, string> Components = new() { ["SALARY:BASIC"] = "12", ["SALARY:HRA"] = "13" };
    private static readonly Dictionary<string, string> MasterInputs = new() { ["SALARY:BASIC"] = "12" };
    private static List<RosterExchangeColumn> Columns() => [.. RecruitmentDocumentRepository.StandardRosterColumns(),
        new("SALARY:BASIC", "Basic Salary INR/month [SALARY:BASIC]", "0008", "money")];
    private static Employee Current() => new() { Id = 71, ClientId = 4, EmployeeCode = "EMP-OLD", FirstName = "Synthetic", LastName = "Person",
        WorkEmail = "old@example.invalid", DateOfJoining = "2026-08-21", IsActive = true, PortalAccess = false,
        AnnualCtc = 456789, SalaryStructureId = "client-template", SalaryComponents = new() { ["12"] = 21000, ["13"] = 6300 },
        PersonalDetails = new() { Mobile = "9876500001", City = "Personal city", PermanentAddress = "Retain address", DateOfBirth = "1987-03-01" },
        PaymentDetails = new() { BankName = "Synthetic bank", BankAccountNo = "000123456789012345", IfscCode = "SYNB0001234" } };

    [Fact]
    public void ManagerEmailUsesOneActiveClientScopedUserAndNoNumericWorksheetField()
    {
        var columns = Columns();
        Assert.Contains(columns, c => c.Key == "reportingManagerEmail" && c.Header.StartsWith("Reporting Manager Email"));
        Assert.DoesNotContain(columns, c => c.Key == "reportingManagerUserId");
        var managers = new List<WorkflowApprover> { new() { Id = 10, ClientId = 4, Email = "manager@example.invalid" }, new() { Id = 11, ClientId = 5, Email = "outside@example.invalid" }, new() { Id = 12, Email = "global@example.invalid" } };
        Assert.Equal(10, RecruitmentDocumentRepository.StandardReportingManagerId(" MANAGER@example.invalid ", 4, managers));
        Assert.Equal(12, RecruitmentDocumentRepository.StandardReportingManagerId("global@example.invalid", 4, managers));
        Assert.Throws<InvalidOperationException>(() => RecruitmentDocumentRepository.StandardReportingManagerId("outside@example.invalid", 4, managers));
        Assert.Throws<InvalidOperationException>(() => RecruitmentDocumentRepository.StandardReportingManagerId("unknown@example.invalid", 4, managers));
        managers.Add(new() { Id = 13, Email = "manager@example.invalid" });
        Assert.Throws<InvalidOperationException>(() => RecruitmentDocumentRepository.StandardReportingManagerId("manager@example.invalid", 4, managers));
        Assert.Throws<InvalidOperationException>(() => RecruitmentDocumentRepository.NormalizeStandardRosterValues(new Dictionary<string, string> { ["reportingManagerEmail"] = "not an email" }, columns));
    }

    [Fact]
    public void BlankValuesKeepCurrentDataWhileZeroAndLeadingZeroIdentifiersSurvive()
    {
        var current = Current();
        var values = RecruitmentDocumentRepository.NormalizeStandardRosterValues(new Dictionary<string, string>
            { ["firstName"] = "", ["SALARY:BASIC"] = "0", ["bankAccountNo"] = "000000123456789012", ["dateOfBirth"] = "01/03/1987", ["salaryStructureId"] = "another-template", ["active"] = "FALSE" }, Columns());
        var result = RecruitmentDocumentRepository.StandardRosterEmployee(current, 4, values, MasterInputs);
        Assert.Equal("Synthetic", result.FirstName); Assert.Equal(456789, result.AnnualCtc); Assert.Equal(6300, result.SalaryComponents["13"]);
        Assert.Equal(0, result.SalaryComponents["12"]); Assert.Equal("000000123456789012", result.PaymentDetails.BankAccountNo);
        Assert.Equal("1987-03-01", result.PersonalDetails.DateOfBirth); Assert.False(result.PortalAccess); Assert.True(result.IsActive);
        Assert.Equal("client-template", result.SalaryStructureId); Assert.False(values.ContainsKey("salaryStructureId")); Assert.False(values.ContainsKey("active"));
        Assert.Equal(6300, current.SalaryComponents["13"]); Assert.Equal(456789, current.AnnualCtc); // Input is not mutated.
    }

    [Fact]
    public void PendingProfileCorrectionsKeepOtherDataWithoutAMasterOrGuessedCtc()
    {
        var baseline = new Dictionary<string, string> { ["firstName"] = "Pending", ["candidateEmail"] = "pending@example.invalid", ["bankAccountNo"] = "0000123", ["SALARY:BASIC"] = "12345" };
        var changed = new Dictionary<string, string> { ["lastName"] = "Corrected", ["monthlyGross"] = "20000" };
        var result = RecruitmentDocumentRepository.StandardRosterCorrectionEmployee(null, 4, baseline, changed, Components);
        Assert.Equal("Pending", result.FirstName); Assert.Equal("Corrected", result.LastName); Assert.Equal("pending@example.invalid", result.WorkEmail);
        Assert.Equal("0000123", result.PaymentDetails.BankAccountNo); Assert.Equal(12345, result.SalaryComponents["12"]);
        Assert.Equal(0, result.AnnualCtc); Assert.Empty(result.SalaryStructureId); Assert.False(result.PortalAccess);
    }

    [Theory]
    [InlineData("21/08/26", "2026-08-21")]
    [InlineData("21-08-2026", "2026-08-21")]
    [InlineData("15.06.1970", "1970-06-15")]
    [InlineData("17/2/1982", "1982-02-17")]
    public void StandardTemplateDatesAcceptCommonEmployeeWorkbookFormats(string raw, string expected)
        => Assert.Equal(expected, RecruitmentDocumentRepository.NormalizeStandardRosterValues(new Dictionary<string, string> { ["joiningDate"] = raw }, Columns())["joiningDate"]);

    [Fact]
    public void PayrollOutputsAndOtherClientCatalogEntriesAreNotMasterInputs()
    {
        var values = RecruitmentDocumentRepository.NormalizeStandardRosterValues(new Dictionary<string, string> { ["firstName"] = "User supplied", ["monthlyGross"] = "35,000", ["annualCtc"] = "420000", ["SALARY:HRA"] = "8000", ["SALARY:C52_DAILY_WAGES"] = "900" }, Columns());
        var employee = RecruitmentDocumentRepository.StandardRosterEmployee(null, 4, values, MasterInputs);
        Assert.Empty(employee.SalaryComponents); Assert.Equal(0, employee.AnnualCtc); Assert.Empty(employee.SalaryStructureId);
        Assert.Single(values); Assert.Equal("User supplied", values["firstName"]);
        Assert.Equal(new[] { "SALARY:BASIC" }, Columns().Where(c => c.Key.StartsWith("SALARY:")).Select(c => c.Key));
        foreach (var code in new[] { "HRA", "C52_DAILY_WAGES", "C62_DAILY_WAGES", "FIXED_GROSS", "INCREMENT_5", "PF_EMPLOYER", "NET_PAY" })
            Assert.False(RecruitmentDocumentRepository.StandardRosterSalaryInput(code, true));
        Assert.True(RecruitmentDocumentRepository.StandardRosterSalaryInput("basic", true));
        Assert.False(RecruitmentDocumentRepository.StandardRosterSalaryInput("BASIC", false));
    }

    [Fact]
    public void PendingBasicCorrectionRetainsFullStoredPayWithoutInferringGrossOrCtc()
    {
        var baseline = new Dictionary<string, string> { ["firstName"] = "Pending", ["joiningDate"] = "2026-08-21", ["salaryComponentsJson"] = "{\"12\":21000,\"13\":6300,\"99\":123}", ["annualCtc"] = "456789", ["monthlyGross"] = "27300", ["monthlyHra"] = "6300", ["hraRate"] = "30%" };
        var supplied = new Dictionary<string, string> { ["SALARY:BASIC"] = "22000" };
        var result = RecruitmentDocumentRepository.StandardRosterCorrectionEmployee(null, 4, baseline, supplied, MasterInputs, Components);
        Assert.Equal(22000, result.SalaryComponents["12"]); Assert.Equal(6300, result.SalaryComponents["13"]); Assert.Equal(123, result.SalaryComponents["99"]);
        Assert.Equal(456789, result.AnnualCtc); Assert.Equal("27300", baseline["monthlyGross"]);
        Assert.False(supplied.ContainsKey("annualCtc")); Assert.False(supplied.ContainsKey("monthlyGross"));
        var stored = RecruitmentDocumentRepository.StandardStoredMasterInputs(baseline, new() { Values = supplied }, result, "12");
        Assert.Equal("22000", stored["monthlyBasic"]); Assert.Equal("6300", stored["monthlyHra"]); Assert.Equal("30%", stored["hraRate"]); Assert.Equal("27300", stored["monthlyGross"]);
        var joined = RecruitmentDocumentRepository.BuildRosterEmployee(new() { ClientId = 4, CandidateCode = "SYN-1", CandidateName = "Pending", WorkLocationId = 7 }, stored,
            new() { Id = 7, ClientId = 4, Name = "Synthetic location" });
        Assert.Equal(22000, joined.SalaryComponents["12"]); Assert.Equal(6300, joined.SalaryComponents["13"]); Assert.Equal(123, joined.SalaryComponents["99"]); Assert.Equal(456789, joined.AnnualCtc);
        var legacy = RecruitmentDocumentRepository.StandardRosterCorrectionEmployee(null, 4, new Dictionary<string, string> { ["firstName"] = "Legacy", ["SALARY:BASIC"] = "21000", ["SALARY:HRA"] = "6300" }, supplied, MasterInputs, Components);
        Assert.Equal(6300, legacy.SalaryComponents["13"]); Assert.Equal(0, legacy.AnnualCtc);
    }

    [Fact]
    public void ProfileCodeIsTheOnlyAnchorEvenWhenNameAndContactsChange()
    {
        var rows = new List<RecruitmentDocumentRosterRow> { new() { Id = 19, ClientId = 4, CandidateCode = "CAN-SYN-001", CandidateName = "Old name", Email = "old@example.invalid" } };
        Assert.Equal(19, RecruitmentDocumentRepository.MatchStandardRosterProfile(" can-syn-001 ", rows)!.Id);
        Assert.Null(RecruitmentDocumentRepository.MatchStandardRosterProfile("Old name", rows));
        Assert.Null(RecruitmentDocumentRepository.MatchStandardRosterProfile("old@example.invalid", rows));
        Assert.Null(RecruitmentDocumentRepository.MatchStandardRosterProfile("CAN-OTHER-001", rows));
        var proposed = Current(); proposed.FirstName = "New name"; proposed.WorkEmail = "new@example.invalid"; proposed.EmployeeCode = "NEW-CODE";
        var identity = Assert.Single(EmployeeRepository.PreviewRosterMasterCorrections([proposed], [Current()]));
        Assert.Equal(71, identity.MatchedEmployeeId); Assert.Empty(identity.BlockingReasons);
        var other = new Employee { Id = 72, ClientId = 4, EmployeeCode = "NEW-CODE", FirstName = "Different", WorkEmail = "other@example.invalid" };
        Assert.NotEmpty(Assert.Single(EmployeeRepository.PreviewRosterMasterCorrections([proposed], [Current(), other])).BlockingReasons);
    }

    [Fact]
    public void UnknownBlankDuplicateAndExplicitSkipRowsDoNotBlockAKnownProfile()
    {
        var current = new List<RecruitmentDocumentRosterRow> { new() { CandidateCode = "CAN-SYN-001" }, new() { CandidateCode = "CAN-SYN-002" } };
        var duplicate = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CAN-SYN-002" };
        foreach (var code in new[] { "", "CAN-UNKNOWN", "CAN-SYN-002" })
            Assert.NotEmpty(RecruitmentDocumentRepository.StandardRosterMatchIssue("Update", new() { ProfileCode = code }, current, duplicate));
        Assert.Empty(RecruitmentDocumentRepository.StandardRosterMatchIssue("Update", new() { ProfileCode = "CAN-SYN-001" }, current, duplicate));
        Assert.Contains("Marked Skip", RecruitmentDocumentRepository.StandardRosterMatchIssue("Update", new() { ProfileCode = "CAN-SYN-001", Values = new() { ["rowAction"] = "Skip" } }, current, duplicate));
        Assert.Empty(RecruitmentDocumentRepository.StandardRosterMatchIssue("Insert", new(), current, duplicate));
        Assert.NotEmpty(RecruitmentDocumentRepository.StandardRosterMatchIssue("Insert", new() { ProfileCode = "CAN-SYN-001" }, current, duplicate));
    }

    [Fact]
    public void SameStandardWorkbookCarriesAllFieldsAndKeepsAccountDigits()
    {
        var columns = Columns();
        var values = new Dictionary<string, string> { ["profileCode"] = "CAN-SYN-001", ["revision"] = new('a', 64), ["firstName"] = "Synthetic", ["bankAccountNo"] = "000123456789012345", ["SALARY:BASIC"] = "12345.67" };
        var bytes = Workbook(columns, values);
        var read = Assert.Single(RecruitmentDocumentRepository.ReadStandardRosterWorkbook(bytes, new() { ClientId = 4, TemplateId = 6, SourceGroup = "Synthetic", Mode = "Update" }, columns));
        Assert.Equal("CAN-SYN-001", read.ProfileCode); Assert.Equal("000123456789012345", read.Values["bankAccountNo"]); Assert.Equal("12345.67", read.Values["SALARY:BASIC"]);
        Assert.All(new[] { "0000", "0001", "0002", "0006", "0008", "0009" }, code => Assert.Contains(columns, c => c.Infotype == code));
        Assert.Equal(columns.Count, read.Values.Count);
        Assert.DoesNotContain(columns, c => c.Key is "rowAction" or "changeReason" or "salaryStructureId" or "active");
    }

    [Fact]
    public void WorkbookClientTemplateBatchVersionAndHeadersAreCheckedBeforeSaving()
    {
        var columns = Columns(); var row = new Dictionary<string, string> { ["profileCode"] = "CAN-SYN-1" };
        foreach (var settings in new[] {
            new RosterExchangeSettings { ClientId = 99, TemplateId = 6, SourceGroup = "Synthetic" },
            new RosterExchangeSettings { ClientId = 4, TemplateId = 99, SourceGroup = "Synthetic" },
            new RosterExchangeSettings { ClientId = 4, TemplateId = 6, SourceGroup = "Wrong batch" } })
            Assert.Throws<InvalidOperationException>(() => RecruitmentDocumentRepository.ReadStandardRosterWorkbook(Workbook(columns, row), settings, columns));
        Assert.Throws<InvalidOperationException>(() => RecruitmentDocumentRepository.ReadStandardRosterWorkbook(Workbook(columns.Where(c => c.Key != "revision").ToList(), row),
            new() { ClientId = 4, TemplateId = 6, SourceGroup = "Synthetic" }, columns));
    }

    [Fact]
    public async Task CorrectionsRequireMasterPermissionAndTheSelectedClientBeforeDatabaseAccess()
    {
        var repository = new RecruitmentDocumentRepository(new ConfigurationBuilder().Build(), null!, new());
        var settings = new RosterExchangeSettings { ClientId = 4, TemplateId = 6, Mode = "Update", SourceGroup = "Synthetic" };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.StandardTemplateAsync(settings, new() { Id = 2, IsActive = true, Permissions = ["recruitment.offer.manage"] }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.StandardTemplateAsync(settings, new() { Id = 2, IsActive = true, ClientId = 9, Permissions = ["settings.manage"] }));
        Assert.False(RecruitmentDocumentRepository.CanCorrectRosterMaster(new() { Permissions = ["recruitment.legacy.manage"] }));
    }

    internal static byte[] Workbook(IReadOnlyList<RosterExchangeColumn> columns, params Dictionary<string, string>[] rows)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(text); }
            Add("xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Employees\" sheetId=\"1\" r:id=\"r1\"/></sheets></workbook>");
            Add("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"r1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
            static string Column(int i) { var s = ""; for (i++; i > 0; i /= 26) { i--; s = (char)('A' + i % 26) + s; } return s; }
            string Sheet(IEnumerable<IEnumerable<string>> data) => "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" + string.Concat(data.Select((r, i) => $"<row r=\"{i + 1}\">" + string.Concat(r.Select((v, c) => "<c r=\"" + Column(c) + (i + 1) + "\" t=\"inlineStr\"><is><t>" + SecurityElement.Escape(v) + "</t></is></c>")) + "</row>")) + "</sheetData></worksheet>";
            const string reference = "__FREVO_TEMPLATE__:{\"version\":\"FREVO-EMPLOYEE-1\",\"clientId\":4,\"templateId\":6,\"importBatch\":\"Synthetic\"}";
            Add("xl/worksheets/sheet1.xml", Sheet(new[] { columns.Select(c => c.Header).Append(reference) }.Concat(rows.Select(row => columns.Select(c => row.GetValueOrDefault(c.Key, ""))))));
        }
        return output.ToArray();
    }
}
