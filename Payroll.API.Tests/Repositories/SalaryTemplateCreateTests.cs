using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Repositories;

public sealed partial class SalaryTemplateCreateTests
{
    private static Task PrepareDeletionFixture(Fixture f) => f.Db.ExecuteAsync("""
        CREATE TABLE employees(Id INT PRIMARY KEY,ClientId INT,EmployeeCode VARCHAR(80),FirstName VARCHAR(80),LastName VARCHAR(80),SalaryStructureId VARCHAR(80));
        ALTER TABLE excel_payslip_batches ADD client_id INT, ADD batch_json JSON;
        ALTER TABLE payrunemployees ADD ClientId INT, ADD PayRunId INT, ADD DetailsJson JSON;
        CREATE TABLE employee_it0008_basic_pay(EmployeeId INT,ClientId INT,EffectiveFrom DATE,Status VARCHAR(30),SalaryStructureId VARCHAR(80));
        CREATE TABLE employee_payroll_location_assignments(client_id INT,employee_id INT,work_location_id INT,status VARCHAR(30),salary_structure_id VARCHAR(80));
        CREATE TABLE excel_payroll_setup_imports(id VARCHAR(80),client_id INT,status VARCHAR(30),request_json JSON,result_json JSON);
        CREATE TABLE payruns(Id INT PRIMARY KEY,ClientId INT);
        CREATE TABLE payroll_calculation_traces(PayRunId INT,InputJson JSON);
        """);

    [LocalWorkflowScopeDatabaseFact]
    public async Task Salary_template_deletion_rechecks_mappings_and_content_before_removing_only_owned_header_and_lines()
    {
        await using var f = await Fixture.CreateAsync();
        await PrepareDeletionFixture(f);
        await f.Db.ExecuteAsync("INSERT INTO salarystructures(Id,ClientId,ClientRef,Name) VALUES(4000000000000,10,'10','Future imported ID')");
        var before = await f.SnapshotAsync();
        var id = (await f.Repository.CreateClientSalaryTemplateAsync(20, Request()))["id"]!.GetValue<long>();
        var staleGlobal = await f.Repository.GetAsync();
        var staleClient = await f.Repository.GetForSelectedClientAsync(20);
        var preview = (await f.ReviewDeletionAsync(20, id)).Preview!;
        Assert.Empty(preview.Mappings);
        Assert.Null((await f.ReviewDeletionAsync(10, id, preview.Version)).Preview);
        await f.Db.ExecuteAsync("INSERT INTO employees VALUES(42,20,'T42','Sample','Employee',@id)", new { id });
        var blocked = await f.ReviewDeletionAsync(20, id, preview.Version);
        Assert.False(blocked.Deleted);
        Assert.Contains("T42", Assert.Single(Assert.Single(blocked.Preview!.Mappings).Examples));
        await f.Db.ExecuteAsync("UPDATE employees SET SalaryStructureId='' WHERE Id=42; UPDATE salarystructurelines SET ValueText='42' WHERE StructureId=@id AND ComponentId='1'", new { id });
        var changed = await f.ReviewDeletionAsync(20, id, preview.Version);
        Assert.False(changed.Deleted);
        Assert.NotEqual(preview.Version, changed.Preview!.Version);
        Assert.True((await f.ReviewDeletionAsync(20, id, changed.Preview.Version)).Deleted);
        var marker = "deleted-salary-template:" + id;
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=20 AND ModuleCode=@marker AND JSON_LENGTH(SettingsJson)=0", new { marker }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Repository.SaveAsync(staleGlobal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Repository.SaveForSelectedClientAsync(20, staleClient));
        Assert.Equal(before, await f.SnapshotAsync(ignoredMarker: marker));
        Assert.Null((await f.ReviewDeletionAsync(20, id)).Preview);
        var recreated = (await f.Repository.CreateClientSalaryTemplateAsync(20, Request()))["id"]!.GetValue<long>();
        Assert.Equal(id + 1, recreated);
        await f.Db.ExecuteAsync("UPDATE salarystructures SET ClientRef='10,20' WHERE Id=31");
        var shared = Assert.Single((await f.ReviewDeletionAsync(20, 31)).Preview!.Mappings);
        Assert.Equal("Shared template", shared.Type);
        Assert.Equal(2, shared.Count);
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Salary_template_deletion_reports_historical_policy_location_design_and_import_dependencies()
    {
        await using var f = await Fixture.CreateAsync();
        await PrepareDeletionFixture(f);
        await f.Db.ExecuteAsync("""
            INSERT INTO employee_it0008_basic_pay VALUES(42,10,'2026-09-01','Historical','31');
            INSERT INTO employee_payroll_location_assignments VALUES(10,42,5,'Active','31');
            UPDATE excel_payslip_batches SET client_id=10,batch_json='{"month":"2026-09","salaryTemplateId":"31"}';
            UPDATE payrunemployees SET ClientId=10,PayRunId=98,DetailsJson='[{"locationParts":[{"SalaryStructureId":"31"}]}]';
            INSERT INTO excel_payroll_setup_imports VALUES('request-only',10,'Draft','{"templateDecisions":[{"existingTemplateId":"31"}]}','{}'),
                ('result-only',10,'Applied','{}','{"templateIds":["31"]}'),('other',10,'Applied','{}','{"templateId":"131"}');
            INSERT INTO payruns VALUES(98,10);
            INSERT INTO payroll_calculation_traces VALUES(98,'{"nativeCurrencyContext":{"templateId":"31"}}');
            INSERT INTO modulesettings VALUES(10,'payroll',TRUE,'{"pfPolicyVersions":[{"salaryStructureId":"31","name":"Example","versionNumber":1,"status":"Published"}]}'),
                (10,'excel_payslip_profile:10',TRUE,'{"abc":{"salaryTemplateId":"31"}}');
            """);
        var before = await f.SnapshotAsync();
        var preview = (await f.ReviewDeletionAsync(10, 31)).Preview!;
        var types = preview.Mappings.ToDictionary(m => m.Type, m => m.Count);
        Assert.Equal(2, types["Excel payroll setup records"]);
        var batchMapping = Assert.Single(preview.Mappings, m => m.Type == "Saved Excel payslip batches");
        Assert.Equal("2026-09 · september-fixture", Assert.Single(batchMapping.Examples));
        foreach (var type in new[] { "Employee salary history", "Location salary assignments", "Managed formula input declarations", "PF policy versions",
                     "Excel payslip header mappings", "Saved Excel payslip batches", "Saved payroll", "Saved payroll calculation traces", "Payslip templates requiring an active salary template" })
            Assert.Equal(1, types[type]);
        Assert.False((await f.ReviewDeletionAsync(10, 31, preview.Version)).Deleted);
        Assert.Equal(before, await f.SnapshotAsync());
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Create_saves_only_one_template_and_preserves_every_existing_record()
    {
        await using var f = await Fixture.CreateAsync();
        var before = await f.SnapshotAsync();
        var saved = await f.Repository.CreateClientSalaryTemplateAsync(20, Request());
        var id = saved["id"]!.GetValue<long>();
        Assert.Equal(before, await f.SnapshotAsync(id));
        Assert.Equal("20", saved["clientId"]!.GetValue<string>());
        Assert.Equal("New monthly template", saved["name"]!.GetValue<string>());
        Assert.Equal(6, saved["lines"]!.AsArray().Count);
        var basic = saved["lines"]!.AsArray()[0]!.AsObject();
        Assert.Equal("Fixed Amount", basic["calculationType"]!.GetValue<string>());
        Assert.Equal("0", basic["value"]!.GetValue<string>());
        var pf = saved["lines"]!.AsArray()[1]!.AsObject();
        Assert.Equal("MIN(BASIC_EARNED,25000)*12%", pf["formula"]!.GetValue<string>());
        Assert.Equal("false", pf["proRataOverride"]!.GetValue<string>());
        Assert.Equal(6, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM salarystructurelines WHERE StructureId=@id", new { id }));
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Duplicate_names_and_invalid_lines_cannot_overwrite_or_partially_save()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Repository.CreateClientSalaryTemplateAsync(20, Request());
        var before = await f.SnapshotAsync();
        var duplicate = Request(); duplicate.Name = " NEW MONTHLY TEMPLATE ";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Repository.CreateClientSalaryTemplateAsync(20, duplicate));
        Assert.Contains("has not been overwritten", error.Message);
        foreach (var kind in new[] { "missing", "duplicate", "inactive", "daily-conversion", "zero-daily", "invalid-formula" })
        {
            var request = Request(); request.Name = kind;
            if (kind == "missing") request.Lines[0].ComponentId = "999";
            if (kind == "duplicate") request.Lines.Add(request.Lines[0]);
            if (kind == "inactive") request.Lines[0].ComponentId = "9";
            if (kind == "daily-conversion") request.Lines[0].CalculationType = "Daily Rate";
            if (kind == "zero-daily") request.Lines[0] = new() { ComponentId = "2", Value = "0" };
            if (kind == "invalid-formula") request.Lines[1].Formula = "MIN(";
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Repository.CreateClientSalaryTemplateAsync(20, request));
            Assert.Equal(before, await f.SnapshotAsync());
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Repository.CreateClientSalaryTemplateAsync(30, Request()));
        Assert.Equal(before, await f.SnapshotAsync());
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task A_failure_after_header_and_first_line_rolls_back_the_whole_create()
    {
        await using var f = await Fixture.CreateAsync();
        var before = await f.SnapshotAsync();
        await f.Db.ExecuteAsync("CREATE TRIGGER fail_new_template_line BEFORE INSERT ON salarystructurelines FOR EACH ROW BEGIN IF NEW.ComponentId='4' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Synthetic second-line failure'; END IF; END");
        await Assert.ThrowsAsync<MySqlException>(() => f.Repository.CreateClientSalaryTemplateAsync(20, Request()));
        Assert.Equal(before, await f.SnapshotAsync());
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Concurrent_duplicate_create_inserts_once_and_first_template_can_be_created()
    {
        await using var f = await Fixture.CreateAsync();
        async Task<bool> Attempt()
        {
            try { await f.Repository.CreateClientSalaryTemplateAsync(20, Request()); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var results = await Task.WhenAll(Attempt(), Attempt());
        Assert.Single(results, saved => saved);
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM salarystructures WHERE ClientId=20"));
        await f.Db.ExecuteAsync("DELETE FROM salarystructurelines; DELETE FROM salarystructures; DELETE FROM modulesettings");
        var before = await f.SnapshotAsync();
        var first = await f.Repository.CreateClientSalaryTemplateAsync(20, Request());
        Assert.Equal(before, await f.SnapshotAsync(first["id"]!.GetValue<long>()));
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task A_template_daily_rate_override_does_not_edit_the_shared_zero_default()
    {
        await using var f = await Fixture.CreateAsync();
        var before = await f.SnapshotAsync();
        var request = new SalaryTemplateCreateRequest
        {
            Name = "Daily template", Lines = [new() { ComponentId = "2", Value = "700.50", CalculationType = "Daily Rate", ProRataOverride = "false" }]
        };
        var saved = await f.Repository.CreateClientSalaryTemplateAsync(10, request);
        Assert.Equal(before, await f.SnapshotAsync(saved["id"]!.GetValue<long>()));
        Assert.Equal("0", await f.Db.ExecuteScalarAsync<string>("SELECT ValueText FROM salarycomponents WHERE Id=2"));
        Assert.Equal("700.50", saved["lines"]!.AsArray()[0]!["value"]!.GetValue<string>());
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Monthly_component_add_and_edit_preserve_unchanged_legacy_daily_defaults_and_other_data()
    {
        await using var f = await Fixture.CreateAsync();
        var before = await f.SnapshotAsync();
        var setup = JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(f.Db))!;
        setup["salaryComponents"]!.AsArray().Add(new JsonObject
        {
            ["id"] = 50, ["code"] = "VDA", ["name"] = "VDA", ["category"] = "Earning",
            ["calculationType"] = "Fixed Amount", ["value"] = "1", ["active"] = true
        });

        await f.Repository.SaveAsync(setup.ToJsonString());
        Assert.Equal("1", await f.Db.ExecuteScalarAsync<string>("SELECT ValueText FROM salarycomponents WHERE Id=50"));
        Assert.Equal(before, await f.SnapshotAsync(newComponentId: 50));

        setup = JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(f.Db))!;
        setup["salaryComponents"]!.AsArray().Single(row => row!["id"]!.GetValue<long>() == 50)!["value"] = "1250";
        await f.Repository.SaveAsync(setup.ToJsonString());
        Assert.Equal("1250", await f.Db.ExecuteScalarAsync<string>("SELECT ValueText FROM salarycomponents WHERE Id=50"));
        Assert.Equal(before, await f.SnapshotAsync(newComponentId: 50));
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task New_daily_components_still_require_a_positive_numeric_rate_and_earning_category_before_any_write()
    {
        await using var f = await Fixture.CreateAsync();
        var before = await f.SnapshotAsync();
        foreach (var (rate, category) in new[] { ("0", "Earning"), ("", "Earning"), ("-1", "Earning"), ("BASIC*5%", "Earning"), ("700", "Deduction") })
        {
            var setup = JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(f.Db))!;
            var components = setup["salaryComponents"]!.AsArray();
            components.Insert(0, new JsonObject { ["id"] = 50, ["code"] = "VDA", ["name"] = "VDA", ["value"] = "1" });
            components.Add(new JsonObject
            {
                ["id"] = 51, ["code"] = "NEW_DAILY", ["name"] = "New daily", ["category"] = category,
                ["calculationType"] = "Daily Rate", ["value"] = rate
            });
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Repository.SaveAsync(setup.ToJsonString()));
            Assert.Equal("A Daily Rate component must be an earning with a positive numeric daily rate.", error.Message);
            Assert.Equal(before, await f.SnapshotAsync());
        }
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Legacy_invalid_daily_defaults_are_retained_only_when_the_whole_component_is_unchanged()
    {
        await using var f = await Fixture.CreateAsync();
        var before = await f.SnapshotAsync();
        foreach (var (id, field, value) in new[]
        {
            (2L, "value", "-1"), (2L, "value", ""), (2L, "formula", "BASIC*5%"),
            (2L, "category", "Deduction"), (2L, "name", "Renamed daily"),
            (2L, "code", "RENAMED_DAILY"), (2L, "priority", "10"), (3L, "name", "Renamed blank daily")
        })
        {
            var setup = JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(f.Db))!;
            setup["salaryComponents"]!.AsArray().Single(row => row!["id"]!.GetValue<long>() == id)![field] = value;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Repository.SaveAsync(setup.ToJsonString()));
            Assert.Equal("A Daily Rate component must be an earning with a positive numeric daily rate.", error.Message);
            Assert.Equal(before, await f.SnapshotAsync());
        }
        foreach (var (id, calculationType) in new[] { (1L, "Daily Rate"), (2L, "Fixed Amount") })
        {
            var setup = JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(f.Db))!;
            var component = setup["salaryComponents"]!.AsArray().Single(row => row!["id"]!.GetValue<long>() == id)!;
            component["calculationType"] = calculationType; component["value"] = "700";
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Repository.SaveAsync(setup.ToJsonString()));
            Assert.Contains("cannot be converted between monthly and daily rates", error.Message);
            Assert.Equal(before, await f.SnapshotAsync());
        }
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Positive_daily_rates_can_be_created_and_updated_but_cannot_be_replaced_by_zero()
    {
        await using var f = await Fixture.CreateAsync();
        var before = await f.SnapshotAsync();
        var setup = JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(f.Db))!;
        setup["salaryComponents"]!.AsArray().Add(new JsonObject
        {
            ["id"] = 50, ["code"] = "NEW_DAILY", ["name"] = "New daily", ["category"] = "Earning",
            ["calculationType"] = "Daily Rate", ["value"] = "700.50", ["proRata"] = false
        });
        await f.Repository.SaveAsync(setup.ToJsonString());
        Assert.Equal("700.50", await f.Db.ExecuteScalarAsync<string>("SELECT ValueText FROM salarycomponents WHERE Id=50"));
        Assert.Equal(before, await f.SnapshotAsync(newComponentId: 50));

        setup = JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(f.Db))!;
        setup["salaryComponents"]!.AsArray().Single(row => row!["id"]!.GetValue<long>() == 50)!["value"] = "800.25";
        await f.Repository.SaveAsync(setup.ToJsonString());
        Assert.Equal("800.25", await f.Db.ExecuteScalarAsync<string>("SELECT ValueText FROM salarycomponents WHERE Id=50"));
        Assert.Equal(before, await f.SnapshotAsync(newComponentId: 50));

        before = await f.SnapshotAsync();
        setup = JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(f.Db))!;
        setup["salaryComponents"]!.AsArray().Single(row => row!["id"]!.GetValue<long>() == 50)!["value"] = "0";
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Repository.SaveAsync(setup.ToJsonString()));
        Assert.Equal(before, await f.SnapshotAsync());
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Single_component_save_preserves_700_templates_other_components_salary_and_source_records()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.ExecuteAsync("INSERT INTO salarystructures(Id,ClientId,ClientRef,Name) VALUES " + string.Join(",", Enumerable.Range(1000, 700).Select(id => $"({id},10,'10','Unchanged template {id}')")));
        await f.Db.ExecuteAsync("INSERT INTO salarystructurelines(StructureId,ComponentId,Formula) SELECT Id,'1','GROSS*50%' FROM salarystructures WHERE Id>=1000");
        var before = await f.SnapshotAsync();
        var created = await f.Repository.SaveSalaryComponentAsync(ComponentRequest(50, "VDA", "Fixed Amount", "0"), null);
        Assert.Equal("VDA", created["code"]!.GetValue<string>());
        Assert.Equal(before, await f.SnapshotAsync(newComponentId: 50));

        var edited = created.DeepClone().AsObject(); edited["name"] = "Variable Dearness Allowance";
        var saved = await f.Repository.SaveSalaryComponentAsync(edited, created);
        Assert.Equal("Variable Dearness Allowance", saved["name"]!.GetValue<string>());
        Assert.Equal(before, await f.SnapshotAsync(newComponentId: 50));
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Single_component_save_rejects_duplicate_stale_and_changed_codes_without_other_writes()
    {
        await using var f = await Fixture.CreateAsync();
        var created = await f.Repository.SaveSalaryComponentAsync(ComponentRequest(50, "VDA", "Fixed Amount", "0"), null);
        var before = await f.SnapshotAsync();
        await Assert.ThrowsAsync<System.Data.DBConcurrencyException>(() => f.Repository.SaveSalaryComponentAsync(ComponentRequest(51, "VDA", "Fixed Amount", "0"), null));
        await Assert.ThrowsAsync<System.Data.DBConcurrencyException>(() => f.Repository.SaveSalaryComponentAsync(created, null));
        Assert.Equal(before, await f.SnapshotAsync());

        var edited = created.DeepClone().AsObject(); edited["value"] = "1250";
        var saved = await f.Repository.SaveSalaryComponentAsync(edited, created);
        before = await f.SnapshotAsync();
        edited["value"] = "2000";
        await Assert.ThrowsAsync<System.Data.DBConcurrencyException>(() => f.Repository.SaveSalaryComponentAsync(edited, created));
        edited = saved.DeepClone().AsObject(); edited["code"] = "RENAMED_VDA";
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Repository.SaveSalaryComponentAsync(edited, saved));
        Assert.Equal(before, await f.SnapshotAsync());
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Single_component_save_keeps_daily_validation_and_retains_only_unchanged_legacy_defaults()
    {
        await using var f = await Fixture.CreateAsync();
        var before = await f.SnapshotAsync();
        foreach (var value in new[] { "0", "", "-1", "BASIC*5%" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Repository.SaveSalaryComponentAsync(ComponentRequest(50, "NEW_DAILY", "Daily Rate", value), null));
        Assert.Equal(before, await f.SnapshotAsync());

        var setup = JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(f.Db))!;
        var legacy = setup["salaryComponents"]!.AsArray().Single(row => row!["id"]!.GetValue<long>() == 2)!.AsObject();
        await f.Repository.SaveSalaryComponentAsync(legacy, legacy.DeepClone().AsObject());
        var changed = legacy.DeepClone().AsObject(); changed["name"] = "Changed legacy default";
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Repository.SaveSalaryComponentAsync(changed, legacy));
        changed = legacy.DeepClone().AsObject(); changed["calculationType"] = "Fixed Amount";
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Repository.SaveSalaryComponentAsync(changed, legacy));
        Assert.Equal(before, await f.SnapshotAsync());

        var saved = await f.Repository.SaveSalaryComponentAsync(ComponentRequest(50, "NEW_DAILY", "Daily Rate", "700.50"), null);
        Assert.Equal("700.50", saved["value"]!.GetValue<string>());
        Assert.Equal(before, await f.SnapshotAsync(newComponentId: 50));
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Single_component_http_save_enforces_permission_and_reports_duplicate_and_stale_conflicts()
    {
        await using var f = await Fixture.CreateAsync();
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(f.Configuration); builder.Services.AddSingleton<SettingsRepository>();
        await using var app = builder.Build();
        var canManage = false;
        SalaryComponentEndpoints.Map(app, _ => canManage);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var request = new SalaryComponentEndpoints.SaveRequest(ComponentRequest(50, "VDA", "Fixed Amount", "0"));
        var before = await f.SnapshotAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("/api/salary-components", request)).StatusCode);
        Assert.Equal(before, await f.SnapshotAsync());
        canManage = true;
        var response = await http.PostAsJsonAsync("/api/salary-components", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(before, await f.SnapshotAsync(newComponentId: 50));
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsJsonAsync("/api/salary-components", request)).StatusCode);
        var edited = created.DeepClone().AsObject(); edited["value"] = "1250";
        var update = new SalaryComponentEndpoints.SaveRequest(edited, created);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("/api/salary-components", update)).StatusCode);
        before = await f.SnapshotAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsJsonAsync("/api/salary-components", update)).StatusCode);
        Assert.Equal(before, await f.SnapshotAsync());
    }

    private static JsonObject ComponentRequest(long id, string code, string calculationType, string value) => new()
    {
        ["id"] = id, ["code"] = code, ["name"] = code, ["category"] = "Earning",
        ["calculationType"] = calculationType, ["value"] = value, ["proRata"] = false
    };

    [LocalWorkflowScopeDatabaseFact]
    public async Task Http_endpoint_enforces_client_permissions_and_rejects_setup_payloads()
    {
        await using var f = await Fixture.CreateAsync();
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(f.Configuration); builder.Services.AddSingleton<SettingsRepository>();
        await using var app = builder.Build();
        var actor = new AuthUser { Id = 8, IsActive = true, ClientId = 20, Permissions = ["client.settings.manage"] };
        SalaryTemplateEndpoints.Map(app, _ => actor, (_, clientId) => actor.ClientId is null || actor.ClientId == clientId);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("/api/clients/10/salary-templates", Request())).StatusCode);
        actor.Permissions = [];
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("/api/clients/20/salary-templates", Request())).StatusCode);
        actor.Permissions = ["client.settings.manage"];
        var before = await f.SnapshotAsync();
        var forged = JsonSerializer.SerializeToNode(Request(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        forged["salaryComponents"] = new JsonArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/api/clients/20/salary-templates", new StringContent(forged.ToJsonString(), Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(before, await f.SnapshotAsync());
        var response = await http.PostAsJsonAsync("/api/clients/20/salary-templates", Request());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var saved = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(before, await f.SnapshotAsync(saved["id"]!.GetValue<long>()));
    }

    internal static SalaryTemplateCreateRequest Request() => new()
    {
        Name = "New monthly template", Lines = [
            new() { ComponentId="1",CalculationType="Fixed Amount",Value="0",ProRataOverride="true" },
            new() { ComponentId="4",CalculationType="Formula",Formula="MIN(BASIC_EARNED,25000)*12%",ProRataOverride="false" },
            new() { ComponentId="5",CalculationType="Formula",Formula="BASIC_EARNED*0.75%",ProRataOverride="false" },
            new() { ComponentId="8",ProRataOverride="false" },
            new() { ComponentId="6",CalculationType="Formula",Formula="MIN(BASIC_EARNED,25000)*13%",ProRataOverride="false" },
            new() { ComponentId="7",CalculationType="Formula",Formula="BASIC_EARNED*3.25%",ProRataOverride="false" }
        ]
    };

    private sealed class Fixture(MySqlConnection root, MySqlConnection db, string name, IConfiguration configuration) : IAsyncDisposable
    {
        internal MySqlConnection Db { get; } = db;
        internal IConfiguration Configuration { get; } = configuration;
        internal SettingsRepository Repository => new(Configuration);
        internal Task<(SalaryTemplateDeletionPreview? Preview, bool Deleted)> ReviewDeletionAsync(int clientId, long templateId, string? version = null) =>
            Repository.ReviewSalaryTemplateDeletionAsync(clientId, templateId, new PayslipDesignRepository(Configuration, NamedFormulaInputStore.Protector(Configuration), new()), version);
        internal static async Task<Fixture> CreateAsync()
        {
            var target = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FREVO_WORKFLOW_SCOPE_TEST_DB") ?? throw new InvalidOperationException("Owned loopback fixture required."));
            Assert.Contains(target.Server, new[] { "localhost", "127.0.0.1", "::1" }); Assert.Equal(33331u, target.Port); target.Database = "";
            var root = new MySqlConnection(target.ConnectionString); await root.OpenAsync();
            Assert.Equal("frevo-workflow-scope-test-20261114", await root.ExecuteScalarAsync<string>("SELECT @@hostname"));
            var name = "hrms_template_create_test_" + Guid.NewGuid().ToString("N");
            await root.ExecuteAsync($"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci"); target.Database = name;
            var db = new MySqlConnection(target.ConnectionString); await db.OpenAsync(); await PayrollDataTableStore.EnsureAsync(db);
            await db.ExecuteAsync("""
CREATE TABLE payrollsetups(Id INT PRIMARY KEY AUTO_INCREMENT,SetupJson JSON);
INSERT INTO payrollsetups(SetupJson) VALUES('{"tax":{"clientSettings":[]},"schedule":{"payDay":"Last working day"},"statutory":{"ptNumber":"synthetic"}}');
CREATE TABLE clients(Id INT PRIMARY KEY,IsActive BOOLEAN); INSERT INTO clients VALUES(10,TRUE),(20,TRUE),(30,FALSE);
CREATE TABLE modulesettings(client_id INT,ModuleCode VARCHAR(120),IsEnabled BOOLEAN,SettingsJson JSON,PRIMARY KEY(client_id,ModuleCode));
INSERT INTO modulesettings VALUES(10,'named-input-t:31',TRUE,JSON_OBJECT('fixture','protected declaration'));
CREATE TABLE excel_payslip_batches(Id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,SourceJson JSON); INSERT INTO excel_payslip_batches VALUES('september-fixture',JSON_OBJECT('unchanged','source salary'));
CREATE TABLE payrunemployees(Id INT PRIMARY KEY,NetPay DECIMAL(18,2)); INSERT INTO payrunemployees VALUES(1,12345.67);
INSERT INTO employeesalarycomponents(EmployeeId,ComponentId,ComponentCode,Amount) VALUES(1,'2','LEGACY_DAILY',700);
INSERT INTO salarycomponents(Id,Code,Name,Category,CalculationType,ValueText,Formula,ProRata) VALUES
(1,'BASIC','Basic','Earning','Formula','','GROSS * 50%',TRUE),
(2,'LEGACY_DAILY','Foreign zero default','Earning','Daily Rate','0','',FALSE),
(3,'LEGACY_BLANK_DAILY','Foreign blank default','Earning','Daily Rate','','',FALSE),
(4,'PF','PF','Deduction','Formula','','ROUND(MIN(BASIC,15000)*12%,0)',TRUE),
(5,'ESIC','ESIC','Deduction','Manual / Variable','','',FALSE),
(6,'EPF_ER','Employer PF','Benefit','Formula','','MIN(BASIC,15000)*13%',FALSE),
(7,'ESIC_ER','Employer ESIC','Benefit','Manual / Variable','','',FALSE),
(8,'RECOVERY','Recovery','Deduction','Manual / Variable','','',FALSE);
INSERT INTO salarycomponents(Id,Code,Name,Active) VALUES(9,'INACTIVE','Inactive',FALSE);
INSERT INTO salarystructures(Id,ClientId,ClientRef,Name) VALUES(31,10,'10','Existing monthly');
INSERT INTO salarystructurelines(StructureId,ComponentId,ValueText,CalculationType,ProRataOverride) VALUES(31,'1','GROSS * 50%','Formula','true');
INSERT INTO paysliptemplates(Id,ClientId,ClientRef,Name,Theme) VALUES(88,10,'10','Existing payslip','Classic');
""");
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = target.ConnectionString }).Build();
            return new(root, db, name, config);
        }
        internal async Task<string> SnapshotAsync(long newId = 0, long newComponentId = 0, string ignoredMarker = "")
        {
            var data = new Dictionary<string, object>();
            data["salarycomponents"] = (await Db.QueryAsync("SELECT * FROM salarycomponents WHERE Id<>@newComponentId ORDER BY Id", new { newComponentId })).ToArray();
            foreach (var table in new[] { "employeesalarycomponents", "payrunemployees", "excel_payslip_batches", "paysliptemplates", "payrollsetups", "professionaltaxslabs", "clientpayschedules" })
                data[table] = (await Db.QueryAsync($"SELECT * FROM {table} ORDER BY 1")).ToArray();
            data["modulesettings"] = (await Db.QueryAsync("SELECT * FROM modulesettings WHERE ModuleCode<>@ignoredMarker ORDER BY client_id,ModuleCode", new { ignoredMarker })).ToArray();
            data["salarystructures"] = (await Db.QueryAsync("SELECT * FROM salarystructures WHERE Id<>@newId ORDER BY Id", new { newId })).ToArray();
            data["salarystructurelines"] = (await Db.QueryAsync("SELECT * FROM salarystructurelines WHERE StructureId<>@newId ORDER BY Id", new { newId })).ToArray();
            return JsonSerializer.Serialize(data);
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync(); MySqlConnection.ClearAllPools(); Assert.Matches("^hrms_template_create_test_[a-f0-9]{32}$", name);
            await root.ExecuteAsync($"DROP DATABASE `{name}`"); await root.DisposeAsync();
        }
    }
}
