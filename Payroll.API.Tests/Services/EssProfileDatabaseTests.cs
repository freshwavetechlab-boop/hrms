using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class EssProfileDatabaseTests
{
    private sealed class IsolatedFactAttribute : FactAttribute
    {
        public IsolatedFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("FREVO_ESS_PROFILE_TEST_DB") is null)
                Skip = "Explicit isolated localhost ESS profile database required.";
        }
    }

    [IsolatedFact]
    public async Task Missing_core_fields_save_to_master_and_infotype_then_lock_without_changing_payroll()
    {
        await using var f = await Fixture.CreateAsync();
        var request = f.Request();
        var result = await f.Repository.SaveProfileAsync(107, 907, request, "synthetic-profile@example.test");
        Assert.Null(result.Error);
        Assert.NotNull(result.Profile);
        Assert.Equal("001234567890", result.Profile.UanNumber);
        Assert.Equal("0012345678", result.Profile.EsicNumber);
        Assert.Equal("Female", result.Profile.Gender);
        Assert.False(result.Profile.CanEdit);
        Assert.True(result.Profile.CanRequestEdit);
        Assert.Equal("Locked", result.Profile.EditStatus);
        var snapshot = await f.Db.QuerySingleAsync<EmployeePersonalDetails>("SELECT UanNumber,EsicNumber FROM employee_it0002_personal_data WHERE EmployeeId=107 AND Status='Active'");
        Assert.Equal(request.UanNumber, snapshot.UanNumber);
        Assert.Equal(request.EsicNumber, snapshot.EsicNumber);
        Assert.Equal("Female", await f.Db.ExecuteScalarAsync<string>("SELECT Gender FROM employee_it0002_personal_data WHERE EmployeeId=107 AND Status='Active'"));
        Assert.Equal(480000m, await f.Db.ExecuteScalarAsync<decimal>("SELECT AnnualCtc FROM employees WHERE Id=107"));
        Assert.Equal("Synthetic template", await f.Db.ExecuteScalarAsync<string>("SELECT SalaryStructureId FROM employees WHERE Id=107"));
        Assert.Equal("2026-01-02", await f.Db.ExecuteScalarAsync<string>("SELECT DateOfJoining FROM employees WHERE Id=107"));
        Assert.Equal("0", await f.Db.ExecuteScalarAsync<string>("SELECT JSON_EXTRACT(NewValueJson,'$.UanNumber') IS NULL FROM ess_profile_update_audit WHERE EmployeeId=107"));
        request.UanNumber = "111111111111";
        var denied = await f.Repository.SaveProfileAsync(107, 907, request, "synthetic-profile@example.test");
        Assert.Contains("locked", denied.Error!);
        Assert.Equal("001234567890", (await f.Repository.GetProfileAsync(107, 907))!.UanNumber);
        Assert.Equal(1, await f.AuditsAsync());
    }

    [IsolatedFact]
    public async Task Invalid_identifiers_and_wrong_client_do_not_consume_one_save_or_modify_records()
    {
        await using var f = await Fixture.CreateAsync();
        foreach (var invalid in new[] { "1.234E11", "123", "12345678901X" })
        {
            var request = f.Request(); request.UanNumber = invalid;
            Assert.Contains("12-digit", (await f.Repository.SaveProfileAsync(107, 907, request, "synthetic")).Error!);
        }
        var invalidEsic = f.Request(); invalidEsic.EsicNumber = "1234.56789";
        Assert.Contains("10-digit", (await f.Repository.SaveProfileAsync(107, 907, invalidEsic, "synthetic")).Error!);
        Assert.Null(await f.Repository.GetProfileAsync(107, 908));
        Assert.Contains("not found", (await f.Repository.SaveProfileAsync(107, 908, f.Request(), "synthetic")).Error!);
        Assert.True((await f.Repository.GetProfileAsync(107, 907))!.CanEdit);
        Assert.Equal(0, await f.AuditsAsync());
        Assert.Empty(await f.Db.QueryAsync<string>("SELECT UanNumber FROM employeepersonaldetails WHERE EmployeeId=107"));
    }

    [IsolatedFact]
    public async Task Older_payload_preserves_existing_identifiers_and_gender()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.ExecuteAsync("INSERT INTO employeepersonaldetails(EmployeeId,UanNumber,EsicNumber) VALUES(107,'legacy-pending','legacy-esic'); UPDATE employees SET Gender='Legacy gender' WHERE Id=107");
        var oldRequest = f.Request(); oldRequest.UanNumber = null; oldRequest.EsicNumber = null; oldRequest.Gender = null;
        var saved = await f.Repository.SaveProfileAsync(107, 907, oldRequest, "synthetic");
        Assert.Null(saved.Error);
        Assert.Equal("legacy-pending", saved.Profile!.UanNumber);
        Assert.Equal("legacy-esic", saved.Profile.EsicNumber);
        Assert.Equal("Legacy gender", saved.Profile.Gender);
    }

    [IsolatedFact]
    public async Task HR_approval_reopens_exactly_one_save_and_pending_edits_are_denied()
    {
        await using var f = await Fixture.CreateAsync();
        Assert.Null((await f.Repository.SaveProfileAsync(107, 907, f.Request(), "synthetic")).Error);
        var workflow = new WorkflowRepository(f.Config);
        var requested = await f.Repository.RequestProfileEditAsync(f.Actor(), "Correct my UAN after document review", workflow);
        Assert.Empty(requested.Error);
        Assert.NotNull(requested.Instance);
        var pending = (await f.Repository.GetProfileAsync(107, 907))!;
        Assert.Equal("Pending approval", pending.EditStatus);
        Assert.False(pending.CanEdit);
        Assert.False(pending.CanRequestEdit);
        Assert.Contains("locked", (await f.Repository.SaveProfileAsync(107, 907, f.Request(), "synthetic")).Error!);
        var taskId = await f.Db.ExecuteScalarAsync<long>("SELECT Id FROM workflowtasks WHERE InstanceId=@Id", new { Id = requested.Instance.Id });
        Assert.False(await workflow.ActionAsync(taskId, 9907, "Approved", "Wrong employee cannot approve"));
        Assert.True(await workflow.ActionAsync(taskId, 9908, "Approved", "Synthetic HR reviewed"));
        Assert.True((await f.Repository.GetProfileAsync(107, 907))!.CanEdit);
        var invalidCorrection = f.Request(); invalidCorrection.UanNumber = "not-a-uan";
        Assert.Contains("12-digit", (await f.Repository.SaveProfileAsync(107, 907, invalidCorrection, "synthetic")).Error!);
        Assert.True((await f.Repository.GetProfileAsync(107, 907))!.CanEdit);
        Assert.Equal("Approved", await f.Db.ExecuteScalarAsync<string>("SELECT CurrentState FROM ResourceStates WHERE ResourceType='EmployeeProfileEdit' AND ResourceId='107'"));
        var correction = f.Request(); correction.UanNumber = "009876543210";
        Assert.Null((await f.Repository.SaveProfileAsync(107, 907, correction, "synthetic")).Error);
        Assert.False((await f.Repository.GetProfileAsync(107, 907))!.CanEdit);
        Assert.Equal("Consumed", await f.Db.ExecuteScalarAsync<string>("SELECT CurrentState FROM ResourceStates WHERE ResourceType='EmployeeProfileEdit' AND ResourceId='107'"));
        Assert.Equal(2, await f.AuditsAsync());
    }

    [IsolatedFact]
    public async Task Concurrent_first_saves_cannot_spend_the_one_time_grant_twice()
    {
        await using var f = await Fixture.CreateAsync();
        // Profile table creation is completed by fixture setup before concurrency.
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => f.Repository.SaveProfileAsync(107, 907, f.Request(), "synthetic")));
        Assert.Single(results, result => result.Error is null);
        Assert.Single(results, result => result.Error?.Contains("locked") == true);
        Assert.Equal(1, await f.AuditsAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public MySqlConnection Db { get; }
        public IConfiguration Config { get; }
        public EssMssRepository Repository { get; }
        private Fixture(string connection)
        {
            Db = new(connection);
            Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connection }).Build();
            Repository = new(Config);
        }
        public static async Task<Fixture> CreateAsync()
        {
            var connection = Environment.GetEnvironmentVariable("FREVO_ESS_PROFILE_TEST_DB")!;
            var parsed = new MySqlConnectionStringBuilder(connection);
            Assert.Equal("127.0.0.1", parsed.Server); Assert.Equal((uint)33330, parsed.Port); Assert.Equal("attendance_location_test", parsed.Database);
            var f = new Fixture(connection); await f.Db.OpenAsync();
            await f.Db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "EssProfile", "profile-schema.sql")));
            // GetProfile creates only the profile tables in this explicitly isolated DB.
            await f.Repository.GetProfileAsync(-1, 907);
            await f.CleanupAsync();
            await f.Db.ExecuteAsync("INSERT INTO clients(Id,Name,IsActive) VALUES(907,'Synthetic ESS Profile',TRUE),(908,'Synthetic other profile client',TRUE); INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,AnnualCtc,SalaryStructureId,DateOfJoining,SalaryJson) VALUES(107,907,'PROFILE-107','Synthetic','Employee',480000,'Synthetic template','2026-01-02','{}'); INSERT INTO authusers(Id,ClientId,EmployeeId,IsActive) VALUES(9907,907,107,TRUE),(9908,907,NULL,TRUE); INSERT INTO workflowmasters(Id,ClientId,Code,Name,ResourceType,IsActive) VALUES(907,907,'ESS_PROFILE_EDIT','Synthetic HR profile approval','EmployeeProfileEdit',TRUE); INSERT INTO workflowstages(Id,WorkflowId,StageOrder,Name,ApproverType,ApproverUserId) VALUES(907,907,1,'Synthetic HR approval','Specific User',9908)");
            return f;
        }
        public AuthUser Actor() => new() { Id = 9907, ClientId = 907, EmployeeId = 107, Email = "synthetic-profile@example.test", IsActive = true, Permissions = ["ess.self"] };
        public SaveEssProfileRequest Request() => new() { FirstName = "Synthetic", LastName = "Employee", Gender = "Female", UanNumber = "001234567890", EsicNumber = "0012345678", WorkEmail = "synthetic-profile@example.test", Mobile = "9000000107", DateOfBirth = "1990-01-01", PanNumber = "ABCDE1234F", AadhaarNumber = "123456789012", Address = "Synthetic address", PermanentAddress = "Synthetic address", CorrespondenceAddress = "Synthetic address", City = "Synthetic city", State = "Synthetic state", BankName = "Synthetic bank", BankAccountNo = "0012345678901", IfscCode = "ABCD0000123", PaymentMode = "Bank Transfer" };
        public Task<int> AuditsAsync() => Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ess_profile_update_audit WHERE EmployeeId=107");
        private Task CleanupAsync() => Db.ExecuteAsync("DELETE FROM workflowhistory WHERE InstanceId IN(SELECT Id FROM workflowinstances WHERE ResourceType='EmployeeProfileEdit' AND ResourceId='107'); DELETE FROM workflowtasks WHERE InstanceId IN(SELECT Id FROM workflowinstances WHERE ResourceType='EmployeeProfileEdit' AND ResourceId='107'); DELETE FROM workflowinstances WHERE ResourceType='EmployeeProfileEdit' AND ResourceId='107'; DELETE FROM workflowstages WHERE WorkflowId=907; DELETE FROM workflowmasters WHERE Id=907; DELETE FROM ResourceStates WHERE (ResourceType='EmployeeProfileEdit' AND ResourceId='107') OR (ResourceType='EmployeeProfileFirstEdit' AND ResourceId='907'); DELETE FROM ess_profile_update_audit WHERE EmployeeId=107; DELETE FROM employee_it0002_personal_data WHERE EmployeeId=107; DELETE FROM employee_it0006_addresses WHERE EmployeeId=107; DELETE FROM employee_it0009_bank_details WHERE EmployeeId=107; DELETE FROM employeepersonaldetails WHERE EmployeeId=107; DELETE FROM employeepaymentdetails WHERE EmployeeId=107; DELETE FROM employees WHERE Id=107; DELETE FROM authusers WHERE Id IN(9907,9908); DELETE FROM clients WHERE Id IN(907,908)");
        public async ValueTask DisposeAsync() { try { await CleanupAsync(); } finally { await Db.DisposeAsync(); } }
    }
}
