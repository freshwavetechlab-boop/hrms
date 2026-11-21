using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class RecruitmentEmployeeMasterSyncTests
{
    private static Employee Master(int id = 4, int client = 55) => new() { Id = id, ClientId = client,
        EmployeeCode = "SYN-4", FirstName = "Synthetic", LastName = "Person", WorkEmail = "corrected@example.invalid",
        Designation = "Revised role", WorkLocationId = 17, AnnualCtc = 0,
        PersonalDetails = new() { Mobile = "9000000000", PanNumber = "PRIVATE" } };
    private static RecruitmentDocumentRosterRow Row(int? employeeId = 4) => new() { Id = 9, ClientId = 55,
        EmployeeId = employeeId, CandidateName = "Synthetic Person", Email = "old@example.invalid", WorkLocationId = 11,
        ValuesJson = "{\"positionTitle\":\"Old role\",\"monthlyBasic\":\"56000\",\"monthlyGross\":\"72800.00\",\"hraRate\":\"30%\",\"contractEndDate\":\"2026-12-31\"}" };

    [Fact]
    public void Stored_identity_wins_and_cannot_cross_clients_or_fall_back_to_email_after_a_broken_link()
    {
        var row = Row(); var master = Master();
        Assert.Same(master, RecruitmentEmployeeMasterSync.Resolve(row, [master]));
        Assert.Null(RecruitmentEmployeeMasterSync.Resolve(row, [Master(client: 91)]));
        row.EmployeeId = 99; row.Email = master.WorkEmail;
        Assert.Null(RecruitmentEmployeeMasterSync.Resolve(row, [master]));
    }

    [Fact]
    public void Legacy_matching_requires_a_unique_same_client_identity_and_refuses_shared_contacts()
    {
        var row = Row(null); var master = Master(); row.Email = master.WorkEmail.ToUpperInvariant();
        Assert.Same(master, RecruitmentEmployeeMasterSync.Resolve(row, [master, Master(7, 91)]));
        Assert.Null(RecruitmentEmployeeMasterSync.Resolve(row, [master, Master(7)]));
        row.CandidateName = "Different Person";
        Assert.Null(RecruitmentEmployeeMasterSync.Resolve(row, [master]));
        row.Email = "";
        Assert.Null(RecruitmentEmployeeMasterSync.Resolve(row, [master]));
    }

    [Fact]
    public void Current_master_updates_document_inputs_without_erasing_source_pay_or_exposing_unused_private_fields()
    {
        var row = Row(); var master = Master(); var original = row.ValuesJson;
        RecruitmentEmployeeMasterSync.Apply(row, master);
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(row.ValuesJson)!;
        Assert.Equal("Revised role", values["positionTitle"]); Assert.Equal(master.WorkEmail, row.Email);
        Assert.Equal(17, row.WorkLocationId); Assert.Equal(master.Id, row.SyncedEmployeeId);
        Assert.Equal("56000", values["monthlyBasic"]); Assert.Equal("72800.00", values["monthlyGross"]);
        Assert.Equal("2026-12-31", values["contractEndDate"]); Assert.False(values.ContainsKey("annualCtc"));
        Assert.False(values.ContainsKey("pan")); Assert.False(values.ContainsKey("bankAccountNo"));
        Assert.NotEqual(RecruitmentDocumentRepository.RowRevision(original, 11), RecruitmentDocumentRepository.RowRevision(row.ValuesJson, 17));
        var once = row.ValuesJson; RecruitmentEmployeeMasterSync.Apply(row, master); Assert.Equal(once, row.ValuesJson);
    }

    [Fact]
    public void Clearing_a_linked_master_contact_cannot_keep_an_old_email_recipient()
    {
        var row = Row(); var employee = Master(); employee.WorkEmail = ""; employee.PersonalDetails.Mobile = "";
        RecruitmentEmployeeMasterSync.Apply(row, employee);
        Assert.Empty(row.Email); Assert.Empty(row.Phone);
    }

    [Fact]
    public void Saving_exact_monthly_components_does_not_change_existing_letter_values_or_apply_a_global_hra_formula()
    {
        var row = Row(); var master = Master();
        master.SalaryComponents = new() { ["101"] = 56000, ["102"] = 16800 };
        RecruitmentEmployeeMasterSync.Apply(row, master, componentCodes: new Dictionary<string, string> { ["101"] = "BASIC", ["102"] = "HRA" });
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(row.ValuesJson)!;
        Assert.Equal("56000", values["monthlyBasic"]); Assert.Equal("72800.00", values["monthlyGross"]); Assert.Equal("30%", values["hraRate"]);
        master.SalaryComponents["102"] = 11200;
        RecruitmentEmployeeMasterSync.Apply(row, master, componentCodes: new Dictionary<string, string> { ["101"] = "BASIC", ["102"] = "HRA" });
        values = JsonSerializer.Deserialize<Dictionary<string, string>>(row.ValuesJson)!;
        Assert.Equal("67200", values["monthlyGross"]); Assert.Equal("0.2", values["hraRate"]);
        Assert.Equal(0, master.AnnualCtc);
    }
}

public sealed class RecruitmentRosterSalarySyncTests
{
    [Theory]
    [InlineData("30%")]
    [InlineData("30")]
    [InlineData("0.30")]
    public void Monthly_basic_and_explicit_hra_reconcile_to_the_exact_offer_gross(string rate)
    {
        var salary = RecruitmentDocumentRepository.RosterMonthlySalary(new Dictionary<string, string> { ["monthlyBasic"] = "56,000", ["monthlyGross"] = "72,800", ["hraRate"] = rate });
        Assert.Equal(56000, salary["BASIC"]); Assert.Equal(16800, salary["HRA"]); Assert.Equal(72800, salary.Values.Sum());
        Assert.False(salary.ContainsKey("CTC"));
    }

    [Theory]
    [InlineData("", "72800", "30%")]
    [InlineData("56000", "72800", "")]
    [InlineData("56000", "72800", "40%")]
    [InlineData("-56000", "72800", "30%")]
    [InlineData("56000", "50000", "30%")]
    public void Missing_or_conflicting_breakups_never_invent_a_payroll_rule(string basic, string gross, string hra)
        => Assert.Empty(RecruitmentDocumentRepository.RosterMonthlySalary(new Dictionary<string, string> { ["monthlyBasic"] = basic, ["monthlyGross"] = gross, ["hraRate"] = hra }));

    [Fact]
    public async Task Salary_sync_cannot_overwrite_an_existing_template_ctc_or_component()
    {
        using var db = new MySqlConnector.MySqlConnection();
        foreach (var employee in new[] { new Employee { AnnualCtc = 1 }, new Employee { SalaryStructureId = "7" }, new Employee { SalaryComponents = new() { ["101"] = 1 } } })
            Assert.False(await EmployeeRepository.InitializeRecruitmentSalaryAsync(db, null!, employee, new() { ["101"] = 56000 }));
    }
}
