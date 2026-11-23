using System.Text.Json;
using Microsoft.Extensions.Configuration;
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

    [Fact]
    public void Current_master_gross_updates_legacy_words_from_the_same_amount_and_excludes_deductions()
    {
        var row = Row(); row.ValuesJson = "{\"monthlyGross\":\"30000\",\"salaryInWords\":\"Rupees Thirty Thousand Only\"}";
        var master = Master(); master.SalaryComponents = new() { ["101"] = 25000, ["103"] = 2000.13m, ["PF"] = 3000 };
        var codes = new Dictionary<string, string> { ["101"] = "BASIC", ["103"] = "STAT_BONUS", ["PF"] = "PF" };
        RecruitmentEmployeeMasterSync.Apply(row, master, componentCodes: codes, earningComponentIds: new HashSet<string> { "101", "103" });
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(row.ValuesJson)!;
        Assert.Equal("27000.13", values["monthlyGross"]);
        Assert.Equal("Rupees Twenty seven thousand and thirteen paise only", values["salaryInWords"]);
        Assert.Contains("salaryInWords", row.EmployeeManagedFields);
        var once = row.ValuesJson;
        RecruitmentEmployeeMasterSync.Apply(row, master, componentCodes: codes, earningComponentIds: new HashSet<string> { "101", "103" });
        Assert.Equal(once, row.ValuesJson);
    }

    [Fact]
    public void An_unchanged_amount_preserves_existing_imported_words_and_release_formatting()
    {
        var row = Row(); row.ValuesJson = "{\"monthlyGross\":\"40,065.00\",\"salaryInWords\":\"Rupees Forty Thousand Sixty Five Only\"}";
        var master = Master(); master.SalaryComponents = new() { ["101"] = 40065 };
        RecruitmentEmployeeMasterSync.Apply(row, master, componentCodes: new Dictionary<string, string> { ["101"] = "BASIC" });
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(row.ValuesJson)!;
        Assert.Equal("40,065.00", values["monthlyGross"]);
        Assert.Equal("Rupees Forty Thousand Sixty Five Only", values["salaryInWords"]);
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
        {
            employee.Id = 4; employee.ClientId = 55;
            Assert.False(await EmployeeRepository.InitializeRecruitmentSalaryAsync(db, null!, employee, new() { ["101"] = 56000 }));
        }
    }

    [Fact]
    public void Annualization_is_explicitly_configured_per_client()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RecruitmentSalarySync:AnnualizeMonthlyRemunerationClientIds:0"] = "55"
        }).Build();
        Assert.True(RecruitmentDocumentRepository.AnnualizeRosterRemuneration(configuration, 55));
        Assert.False(RecruitmentDocumentRepository.AnnualizeRosterRemuneration(configuration, 70));
        Assert.False(RecruitmentDocumentRepository.AnnualizeRosterRemuneration(configuration, 71));
        Assert.False(RecruitmentDocumentRepository.AnnualizeRosterRemuneration(new ConfigurationBuilder().Build(), 55));
        configuration["RecruitmentSalarySync:AnnualizeMonthlyRemunerationClientIds:1"] = "70";
        Assert.True(RecruitmentDocumentRepository.AnnualizeRosterRemuneration(configuration, 70));
    }

    [Fact]
    public void Approved_remuneration_can_seed_an_annual_base_without_inventing_basic_or_contributions()
    {
        var values = new Dictionary<string, string> { ["monthlyGross"] = "40,065" };
        var plan = Assert.IsType<RecruitmentDocumentRepository.RosterSalarySeed>(RecruitmentDocumentRepository.RosterSalarySeedFor([values], true));
        Assert.Equal(480780, plan.AnnualCtc); Assert.Equal(40065, plan.MonthlyRemuneration); Assert.Empty(plan.Components);
        var employee = new Employee { Id = 4, ClientId = 70 };
        Assert.True(EmployeeRepository.ApplyRecruitmentSalarySeed(employee, [], plan.AnnualCtc));
        Assert.Equal(480780, employee.AnnualCtc); Assert.Empty(employee.SalaryComponents); Assert.Empty(employee.SalaryStructureId);
        Assert.False(EmployeeRepository.ApplyRecruitmentSalarySeed(employee, [], plan.AnnualCtc));
        Assert.Null(RecruitmentDocumentRepository.RosterSalarySeedFor([values], false));
    }

    [Fact]
    public void Existing_exact_breakup_and_annual_base_can_be_initialized_together()
    {
        var values = new Dictionary<string, string> { ["monthlyGross"] = "72,800", ["monthlyBasic"] = "56000", ["hraRate"] = "30%" };
        var plan = RecruitmentDocumentRepository.RosterSalarySeedFor([values], true)!;
        Assert.Equal(873600, plan.AnnualCtc); Assert.Equal(56000, plan.Components["BASIC"]); Assert.Equal(16800, plan.Components["HRA"]);
        var employee = new Employee { Id = 4, ClientId = 55 };
        Assert.True(EmployeeRepository.ApplyRecruitmentSalarySeed(employee, new() { ["101"] = plan.Components["BASIC"], ["102"] = plan.Components["HRA"] }, plan.AnnualCtc));
        Assert.Equal(873600, employee.AnnualCtc); Assert.Equal(72800, employee.SalaryComponents.Values.Sum());
        Assert.Equal(0, RecruitmentDocumentRepository.RosterSalarySeedFor([values], false)!.AnnualCtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-40065")]
    [InlineData("0")]
    [InlineData("40065.001")]
    [InlineData("1000000000000")]
    [InlineData("not a salary")]
    public void Missing_invalid_or_overprecise_remuneration_cannot_seed_a_salary(string gross)
        => Assert.Null(RecruitmentDocumentRepository.RosterSalarySeedFor([new Dictionary<string, string> { ["monthlyGross"] = gross }], true));

    [Fact]
    public void Multiple_assignments_must_agree_and_explicit_annual_terms_take_precedence()
    {
        var first = new Dictionary<string, string> { ["monthlyGross"] = "40000", ["annualCtc"] = "500000" };
        Assert.Equal(500000, RecruitmentDocumentRepository.RosterSalarySeedFor([first], true)!.AnnualCtc);
        Assert.Null(RecruitmentDocumentRepository.RosterSalarySeedFor([first, new Dictionary<string, string> { ["monthlyGross"] = "41000" }], true));
        Assert.Null(RecruitmentDocumentRepository.RosterSalarySeedFor([first, new Dictionary<string, string> { ["monthlyGross"] = "40000" }], true));
        first["annualCtc"] = "invalid";
        Assert.Null(RecruitmentDocumentRepository.RosterSalarySeedFor([first], true));
    }

    [Fact]
    public void A_missing_ctc_base_preserves_current_and_historical_component_decisions()
    {
        var employee = new Employee { Id = 4, ClientId = 55, SalaryComponents = new() { ["101"] = 56000, ["102"] = 16800, ["PF"] = 1800 } };
        employee.SalaryJson = JsonSerializer.Serialize(employee.SalaryComponents);
        var original = employee.SalaryJson;
        Assert.True(EmployeeRepository.ApplyRecruitmentSalarySeed(employee, new() { ["101"] = 40000 }, 873600, hasComponentHistory: true));
        Assert.Equal(original, employee.SalaryJson); Assert.Equal(56000, employee.SalaryComponents["101"]); Assert.Equal(1800, employee.SalaryComponents["PF"]);
        var historical = new Employee { Id = 5, ClientId = 55 };
        Assert.True(EmployeeRepository.ApplyRecruitmentSalarySeed(historical, new() { ["101"] = 40000 }, 480000, hasComponentHistory: true));
        Assert.Empty(historical.SalaryComponents);
        Assert.False(EmployeeRepository.ApplyRecruitmentSalarySeed(new Employee { Id = 6, ClientId = 55 }, new() { ["101"] = 40000 }, hasComponentHistory: true));
    }

    [Fact]
    public async Task Annual_base_cannot_replace_a_ctc_template_or_inactive_employee_before_database_access()
    {
        using var db = new MySqlConnector.MySqlConnection();
        foreach (var employee in new[] { new Employee { AnnualCtc = 1 }, new Employee { SalaryStructureId = "7" }, new Employee { IsActive = false } })
        {
            employee.Id = 4; employee.ClientId = 70;
            Assert.False(await EmployeeRepository.InitializeRecruitmentSalaryAsync(db, null!, employee, [], 480780));
        }
    }
}
