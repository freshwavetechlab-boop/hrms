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

public sealed class SalaryTemplateCreateTests
{
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
    public async Task Existing_global_daily_rate_validation_remains_strict()
    {
        await using var f = await Fixture.CreateAsync();
        var before = await f.SnapshotAsync();
        var setup = await PayrollDataTableStore.GetSetupJsonAsync(f.Db);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => PayrollDataTableStore.SaveSetupJsonAsync(f.Db, setup));
        Assert.Equal("A Daily Rate component must be an earning with a positive numeric daily rate.", error.Message);
        Assert.Equal(before, await f.SnapshotAsync());
    }

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
CREATE TABLE excel_payslip_batches(Id VARCHAR(80) PRIMARY KEY,SourceJson JSON); INSERT INTO excel_payslip_batches VALUES('september-fixture',JSON_OBJECT('unchanged','source salary'));
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
        internal async Task<string> SnapshotAsync(long newId = 0)
        {
            var data = new Dictionary<string, object>();
            foreach (var table in new[] { "salarycomponents", "employeesalarycomponents", "payrunemployees", "excel_payslip_batches", "paysliptemplates", "payrollsetups", "professionaltaxslabs", "clientpayschedules" })
                data[table] = (await Db.QueryAsync($"SELECT * FROM {table} ORDER BY 1")).ToArray();
            data["modulesettings"] = (await Db.QueryAsync("SELECT * FROM modulesettings ORDER BY client_id,ModuleCode")).ToArray();
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
