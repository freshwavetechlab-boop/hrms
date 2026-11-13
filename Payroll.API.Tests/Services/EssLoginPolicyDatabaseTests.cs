using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

[CollectionDefinition("EssLoginPolicyDatabase", DisableParallelization = true)]
public sealed class EssLoginPolicyDatabaseCollection { }

[Collection("EssLoginPolicyDatabase")]
public sealed class EssLoginPolicyDatabaseTests
{
    private sealed class IsolatedFactAttribute : FactAttribute
    {
        public IsolatedFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("FREVO_ESS_LOGIN_POLICY_TEST_DB") is null)
                Skip = "Explicit isolated localhost ESS login policy database required.";
        }
    }

    [IsolatedFact]
    public async Task Aadhaar_initial_password_forces_change_even_when_bulk_request_opts_out()
    {
        await using var f = await Fixture.CreateAsync();
        await f.SetPolicyAsync(907, "AadhaarFirst8");
        await f.AddEmployeeAsync(107, 907, "POLICY-107", "2345 6789 0123");

        var result = await f.Auth.ProvisionEmployeeLoginsAsync(new()
        {
            EmployeeIds = [107], MustChangePassword = false
        });

        Assert.Equal(1, result.CreatedCount);
        Assert.Empty(result.TemporaryPassword);
        Assert.True(await f.Db.ExecuteScalarAsync<bool>("SELECT MustChangePassword FROM authusers WHERE EmployeeId=107"));
        var login = await f.Auth.LoginAsync(new() { Email = "POLICY-107", Password = "23456789", Portal = "ESS" }, "127.0.0.1", "synthetic-policy-test");
        Assert.NotNull(login);
        Assert.True(login.User.MustChangePassword);
        Assert.Equal(107, login.User.EmployeeId);
        Assert.Equal(907, login.User.ClientId);
        var reused = await f.Auth.ChangePasswordAsync(login.User.Id, "23456789", "23456789");
        Assert.Contains("different", reused.Error!);
        Assert.True(await f.Db.ExecuteScalarAsync<bool>("SELECT MustChangePassword FROM authusers WHERE EmployeeId=107"));
        var changed = await f.Auth.ChangePasswordAsync(login.User.Id, "23456789", "SyntheticOwn@2026");
        Assert.Null(changed.Error);
        Assert.False(changed.User!.MustChangePassword);
        Assert.Null(await f.Auth.LoginAsync(new() { Email = "POLICY-107", Password = "23456789", Portal = "ESS" }, "127.0.0.1", "synthetic-policy-test"));
        Assert.NotNull(await f.Auth.LoginAsync(new() { Email = "POLICY-107", Password = "SyntheticOwn@2026", Portal = "ESS" }, "127.0.0.1", "synthetic-policy-test"));
    }

    [IsolatedFact]
    public async Task Missing_or_masked_Aadhaar_skips_creation_without_exposing_source_or_resetting_existing_account()
    {
        await using var f = await Fixture.CreateAsync();
        await f.SetPolicyAsync(907, "Fixed", "SyntheticExisting@2026");
        await f.AddEmployeeAsync(107, 907, "POLICY-107", "");
        Assert.Equal("Created", (await f.Auth.EnsureEmployeeLoginAsync(107)).Status);
        await f.Db.ExecuteAsync("UPDATE authusers SET MustChangePassword=FALSE WHERE EmployeeId=107");
        var existingHash = await f.Db.ExecuteScalarAsync<string>("SELECT PasswordHash FROM authusers WHERE EmployeeId=107");
        await f.SetPolicyAsync(907, "AadhaarFirst8");
        await f.AddEmployeeAsync(108, 907, "POLICY-108", "");
        await f.AddEmployeeAsync(109, 907, "POLICY-109", "XXXXXXXX0123");

        var response = await f.Auth.ProvisionEmployeeLoginsAsync(new() { EmployeeIds = [107, 108, 109] });
        Assert.Equal(0, response.CreatedCount);
        Assert.Equal(3, response.SkippedCount);
        Assert.All(response.Results.Where(r => r.EmployeeId != 107), row =>
        {
            Assert.Contains("12-digit Aadhaar", row.Message);
            Assert.DoesNotContain("XXXXXXXX0123", row.Message);
        });
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM authusers"));

        var existing = await f.Auth.EnsureEmployeeLoginAsync(107);
        Assert.Equal("Existing", existing.Status);
        Assert.Empty(existing.TemporaryPassword);
        Assert.Equal(existingHash, await f.Db.ExecuteScalarAsync<string>("SELECT PasswordHash FROM authusers WHERE EmployeeId=107"));
        Assert.False(await f.Db.ExecuteScalarAsync<bool>("SELECT MustChangePassword FROM authusers WHERE EmployeeId=107"));
        Assert.NotNull(await f.Auth.LoginAsync(new() { Email = "POLICY-107", Password = "SyntheticExisting@2026", Portal = "ESS" }, "127.0.0.1", "synthetic-policy-test"));
    }

    [IsolatedFact]
    public async Task Client_initial_password_policy_does_not_change_another_clients_existing_or_new_credentials()
    {
        await using var f = await Fixture.CreateAsync();
        await f.SetPolicyAsync(907, "AadhaarFirst8");
        await f.SetPolicyAsync(908, "Fixed", "SyntheticOther@2026");
        await f.AddEmployeeAsync(107, 907, "POLICY-107", "234567890123");
        await f.AddEmployeeAsync(108, 908, "OTHER-108", "");
        await f.AddEmployeeAsync(109, 908, "OTHER-109", "");
        var existing = await f.Auth.EnsureEmployeeLoginAsync(108);
        Assert.Equal("Created", existing.Status);
        var existingHash = await f.Db.ExecuteScalarAsync<string>("SELECT PasswordHash FROM authusers WHERE EmployeeId=108");

        var response = await f.Auth.ProvisionEmployeeLoginsAsync(new() { EmployeeIds = [107, 108, 109], MustChangePassword = false });
        Assert.Equal(2, response.CreatedCount);
        Assert.Equal(1, response.SkippedCount);
        Assert.Equal(existingHash, await f.Db.ExecuteScalarAsync<string>("SELECT PasswordHash FROM authusers WHERE EmployeeId=108"));
        Assert.Equal("Fixed", await f.Db.ExecuteScalarAsync<string>("SELECT InitialPasswordMode FROM ess_client_settings WHERE ClientId=908"));
        var other = await f.Auth.LoginAsync(new() { Email = "OTHER-109", Password = "SyntheticOther@2026", Portal = "ESS" }, "127.0.0.1", "synthetic-policy-test");
        Assert.NotNull(other);
        Assert.False(other.User.MustChangePassword);
        Assert.Equal(908, other.User.ClientId);
        Assert.Null(await f.Auth.LoginAsync(new() { Email = "OTHER-109", Password = "23456789", Portal = "ESS" }, "127.0.0.1", "synthetic-policy-test"));
    }

    [IsolatedFact]
    public async Task Canonical_Aadhaar_overrides_stale_JSON_and_canonical_blank_cannot_use_stale_identifier()
    {
        await using var f = await Fixture.CreateAsync();
        await f.SetPolicyAsync(907, "AadhaarFirst8");
        await f.AddEmployeeAsync(107, 907, "POLICY-107", "234567890123");
        await f.AddEmployeeAsync(108, 907, "POLICY-108", "234567890123");
        await f.Db.ExecuteAsync(@"
INSERT INTO employeepersonaldetails(EmployeeId,AadhaarNumber) VALUES(107,'345678901234'),(108,'')");

        var result = await f.Auth.ProvisionEmployeeLoginsAsync(new() { EmployeeIds = [107, 108], MustChangePassword = false });

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal("Skipped", result.Results.Single(r => r.EmployeeId == 108).Status);
        Assert.Contains("12-digit Aadhaar", result.Results.Single(r => r.EmployeeId == 108).Message);
        Assert.NotNull(await f.Auth.LoginAsync(new() { Email = "POLICY-107", Password = "34567890", Portal = "ESS" }, "127.0.0.1", "synthetic-policy-test"));
        Assert.Null(await f.Auth.LoginAsync(new() { Email = "POLICY-107", Password = "23456789", Portal = "ESS" }, "127.0.0.1", "synthetic-policy-test"));
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM authusers WHERE EmployeeId=108"));
    }

    [IsolatedFact]
    public async Task Older_settings_payload_preserves_enabled_phone_login_and_explicit_disable_is_client_scoped()
    {
        await using var f = await Fixture.CreateAsync();
        var client = (await f.Ess.GetEssClientSettingsAsync(907)).Single();
        client.AllowPhoneLogin = true;
        client.InitialPasswordMode = "AadhaarFirst8";
        client = await f.Ess.SaveEssClientSettingAsync(client, 901);
        var other = (await f.Ess.GetEssClientSettingsAsync(908)).Single();
        other.AllowPhoneLogin = true;
        await f.Ess.SaveEssClientSettingAsync(other, 901);

        client.AllowPhoneLogin = null;
        client.AllowProfileEdit = false;
        var savedOldPayload = await f.Ess.SaveEssClientSettingAsync(client, 901);
        Assert.True(savedOldPayload.AllowPhoneLogin);
        Assert.True((await f.Ess.GetEssClientSettingsAsync(907)).Single().AllowPhoneLogin);
        Assert.False((await f.Ess.GetEssClientSettingsAsync(907)).Single().AllowProfileEdit);
        Assert.True((await f.Ess.GetEssClientSettingsAsync(908)).Single().AllowPhoneLogin);

        client.AllowPhoneLogin = false;
        Assert.False((await f.Ess.SaveEssClientSettingAsync(client, 901)).AllowPhoneLogin);
        Assert.False((await f.Ess.GetEssClientSettingsAsync(907)).Single().AllowPhoneLogin);
        Assert.True((await f.Ess.GetEssClientSettingsAsync(908)).Single().AllowPhoneLogin);
    }

    [IsolatedFact]
    public async Task Foreign_settings_id_is_rejected_before_either_clients_settings_or_phone_flags_change()
    {
        await using var f = await Fixture.CreateAsync();
        var own = (await f.Ess.GetEssClientSettingsAsync(907)).Single();
        own.AllowPhoneLogin = true;
        await f.Ess.SaveEssClientSettingAsync(own, 901);
        var foreign = (await f.Ess.GetEssClientSettingsAsync(908)).Single();
        var before = await f.PolicySnapshotAsync();

        own.Id = foreign.Id;
        own.InitialPasswordMode = "AadhaarFirst8";
        own.AllowPhoneLogin = false;
        own.AllowProfileEdit = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Ess.SaveEssClientSettingAsync(own, 901));

        Assert.Equal(before, await f.PolicySnapshotAsync());
    }

    [IsolatedFact]
    public async Task Enabled_phone_formats_sign_in_as_the_same_linked_employee_and_keep_first_change_required()
    {
        await using var f = await Fixture.CreateAsync();
        await f.SetPolicyAsync(907, "Fixed", "SyntheticPhone@2026");
        await f.EnablePhoneLoginAsync(907);
        await f.AddEmployeeAsync(107, 907, "POLICY-107", "");
        var created = await f.Auth.EnsureEmployeeLoginAsync(107);
        Assert.Equal("Created", created.Status);

        foreach (var loginId in new[] { "9000000107", "+91 90000-00107", "0091 (90000) 00107", "919000000107" })
        {
            var login = await f.LoginAsync(loginId, "SyntheticPhone@2026");
            Assert.NotNull(login);
            Assert.Equal(created.UserId, login.User.Id);
            Assert.Equal(107, login.User.EmployeeId);
            Assert.Equal(907, login.User.ClientId);
            Assert.True(login.User.MustChangePassword);
            Assert.Contains("ess.self", login.User.Permissions);
        }
        Assert.Null(await f.LoginAsync("9000000107", "WrongSyntheticPassword"));
        Assert.NotNull(await f.LoginAsync("policy-107", "SyntheticPhone@2026"));
    }

    [IsolatedFact]
    public async Task Missing_or_disabled_client_phone_policy_does_not_enable_phone_aliases()
    {
        await using var f = await Fixture.CreateAsync();
        await f.SetPolicyAsync(907, "Fixed", "SyntheticPhone@2026");
        await f.SetPolicyAsync(908, "Fixed", "SyntheticOther@2026");
        await f.AddEmployeeAsync(107, 907, "POLICY-107", "");
        await f.AddEmployeeAsync(108, 908, "OTHER-108", "", "9000000108");
        await f.Auth.EnsureEmployeeLoginAsync(107);
        await f.Auth.EnsureEmployeeLoginAsync(108);
        Assert.Null(await f.LoginAsync("9000000107", "SyntheticPhone@2026"));
        Assert.Null(await f.LoginAsync("9000000108", "SyntheticOther@2026"));

        await f.EnablePhoneLoginAsync(907);
        Assert.NotNull(await f.LoginAsync("9000000107", "SyntheticPhone@2026"));
        Assert.Null(await f.LoginAsync("9000000108", "SyntheticOther@2026"));
        var settings = (await f.Ess.GetEssClientSettingsAsync(907)).Single();
        settings.AllowPhoneLogin = false;
        await f.Ess.SaveEssClientSettingAsync(settings, 901);
        Assert.Null(await f.LoginAsync("9000000107", "SyntheticPhone@2026"));
        Assert.NotNull(await f.LoginAsync("POLICY-107", "SyntheticPhone@2026"));
        Assert.NotNull(await f.LoginAsync("OTHER-108", "SyntheticOther@2026"));

        await f.EnablePhoneLoginAsync(907);
        await f.Db.ExecuteAsync("UPDATE ess_client_settings SET IsActive=FALSE WHERE ClientId=907");
        Assert.Null(await f.LoginAsync("9000000107", "SyntheticPhone@2026"));
        Assert.NotNull(await f.LoginAsync("POLICY-107", "SyntheticPhone@2026"));
        await f.Db.ExecuteAsync("DELETE FROM ess_client_settings WHERE ClientId=907");
        Assert.Null(await f.LoginAsync("9000000107", "SyntheticPhone@2026"));
        Assert.NotNull(await f.LoginAsync("POLICY-107", "SyntheticPhone@2026"));
    }

    [IsolatedFact]
    public async Task Shared_phone_inside_a_client_is_rejected_even_when_only_one_password_matches()
    {
        await using var f = await Fixture.CreateAsync();
        await f.EnablePhoneLoginAsync(907);
        await f.SetPolicyAsync(907, "Fixed", "SyntheticFirst@2026");
        await f.AddEmployeeAsync(107, 907, "POLICY-107", "");
        await f.Auth.EnsureEmployeeLoginAsync(107);
        await f.SetPolicyAsync(907, "Fixed", "SyntheticSecond@2026");
        await f.AddEmployeeAsync(108, 907, "POLICY-108", "", "+91 90000 00107");
        await f.Auth.EnsureEmployeeLoginAsync(108);

        Assert.Null(await f.LoginAsync("9000000107", "SyntheticFirst@2026"));
        Assert.Null(await f.LoginAsync("+919000000107", "SyntheticSecond@2026"));
        Assert.NotNull(await f.LoginAsync("POLICY-107", "SyntheticFirst@2026"));
        Assert.NotNull(await f.LoginAsync("POLICY-108", "SyntheticSecond@2026"));
    }

    [IsolatedFact]
    public async Task Shared_phone_in_another_client_blocks_alias_even_when_other_client_did_not_enable_phone_login()
    {
        await using var f = await Fixture.CreateAsync();
        await f.EnablePhoneLoginAsync(907);
        await f.SetPolicyAsync(907, "Fixed", "SyntheticFirst@2026");
        await f.SetPolicyAsync(908, "Fixed", "SyntheticOther@2026");
        await f.AddEmployeeAsync(107, 907, "POLICY-107", "");
        await f.AddEmployeeAsync(108, 908, "OTHER-108", "", "0091 90000 00107");
        await f.Auth.EnsureEmployeeLoginAsync(107);
        await f.Auth.EnsureEmployeeLoginAsync(108);

        Assert.Null(await f.LoginAsync("9000000107", "SyntheticFirst@2026"));
        Assert.Null(await f.LoginAsync("+919000000107", "SyntheticOther@2026"));
        Assert.NotNull(await f.LoginAsync("POLICY-107", "SyntheticFirst@2026"));
        Assert.NotNull(await f.LoginAsync("OTHER-108", "SyntheticOther@2026"));
    }

    [IsolatedFact]
    public async Task Canonical_mobile_overrides_old_JSON_and_auth_mobile_and_blank_or_invalid_master_removes_alias()
    {
        await using var f = await Fixture.CreateAsync();
        await f.EnablePhoneLoginAsync(907);
        await f.SetPolicyAsync(907, "Fixed", "SyntheticPhone@2026");
        await f.AddEmployeeAsync(107, 907, "POLICY-107", "");
        await f.Auth.EnsureEmployeeLoginAsync(107);
        await f.Db.ExecuteAsync(@"
INSERT INTO employeepersonaldetails(EmployeeId,Mobile) VALUES(107,'+91 90000 00108');
UPDATE authusers SET Mobile='9000000109' WHERE EmployeeId=107");

        Assert.NotNull(await f.LoginAsync("9000000108", "SyntheticPhone@2026"));
        Assert.Null(await f.LoginAsync("9000000107", "SyntheticPhone@2026"));
        Assert.Null(await f.LoginAsync("9000000109", "SyntheticPhone@2026"));
        await f.Db.ExecuteAsync("UPDATE employeepersonaldetails SET Mobile='' WHERE EmployeeId=107");
        Assert.Null(await f.LoginAsync("9000000108", "SyntheticPhone@2026"));
        Assert.Null(await f.LoginAsync("9000000107", "SyntheticPhone@2026"));
        await f.Db.ExecuteAsync("UPDATE employeepersonaldetails SET Mobile='9.000000108E9' WHERE EmployeeId=107");
        Assert.Null(await f.LoginAsync("9000000108", "SyntheticPhone@2026"));
        foreach (var invalid in new[] { "900000010", "09000000107", "1234567890", "9.000000108E9", "+44 9000000108" })
            Assert.Null(await f.LoginAsync(invalid, "SyntheticPhone@2026"));
        Assert.NotNull(await f.LoginAsync("POLICY-107", "SyntheticPhone@2026"));
    }

    [IsolatedFact]
    public async Task Employee_code_email_and_Admin_logins_keep_their_existing_behavior()
    {
        await using var f = await Fixture.CreateAsync();
        await f.EnablePhoneLoginAsync(907);
        await f.SetPolicyAsync(907, "Fixed", "SyntheticPhone@2026");
        await f.AddEmployeeAsync(107, 907, "POLICY-107", "");
        await f.Auth.EnsureEmployeeLoginAsync(107);
        var admin = await f.Auth.SaveUserAsync(new()
        {
            Email = "synthetic-admin@example.test", DisplayName = "Synthetic admin", Password = "SyntheticAdmin@2026",
            Mobile = "9000000199", Roles = ["admin"], IsActive = true
        });
        Assert.NotNull(admin);
        var adminLogin = await f.LoginAsync("SYNTHETIC-ADMIN@EXAMPLE.TEST", "SyntheticAdmin@2026", "Admin");
        Assert.NotNull(adminLogin);
        Assert.Equal(admin.Id, adminLogin.User.Id);
        Assert.Null(await f.LoginAsync("9000000199", "SyntheticAdmin@2026"));
        Assert.NotNull(await f.LoginAsync("POLICY-107", "SyntheticPhone@2026"));
        Assert.Null(await f.LoginAsync("POLICY-107", "SyntheticPhone@2026", "Admin"));
        Assert.Null(await f.LoginAsync("9000000107", "SyntheticPhone@2026", "Admin"));
        Assert.Null(await f.LoginAsync("9000000107", "SyntheticPhone@2026", ""));
    }

    [IsolatedFact]
    public async Task Numeric_explicit_login_ID_keeps_priority_and_wrong_password_never_falls_back_to_another_employee_phone()
    {
        await using var f = await Fixture.CreateAsync();
        await f.EnablePhoneLoginAsync(907);
        await f.SetPolicyAsync(907, "Fixed", "SyntheticPhone@2026");
        await f.AddEmployeeAsync(107, 907, "POLICY-107", "");
        await f.Auth.EnsureEmployeeLoginAsync(107);
        await f.SetPolicyAsync(907, "Fixed", "SyntheticNumeric@2026");
        await f.AddEmployeeAsync(108, 907, "9000000107", "", "9000000108");
        await f.Auth.EnsureEmployeeLoginAsync(108);

        Assert.Null(await f.LoginAsync("9000000107", "SyntheticPhone@2026"));
        var numeric = await f.LoginAsync("9000000107", "SyntheticNumeric@2026");
        Assert.NotNull(numeric);
        Assert.Equal(108, numeric.User.EmployeeId);
        var alias = await f.LoginAsync("+919000000107", "SyntheticPhone@2026");
        Assert.NotNull(alias);
        Assert.Equal(107, alias.User.EmployeeId);
        Assert.NotNull(await f.LoginAsync("POLICY-107", "SyntheticPhone@2026"));
    }

    [IsolatedFact]
    public async Task Phone_alias_requires_active_consistent_employee_portal_and_ESS_permission_mapping()
    {
        await using var f = await Fixture.CreateAsync();
        await f.EnablePhoneLoginAsync(907);
        await f.SetPolicyAsync(907, "Fixed", "SyntheticPhone@2026");
        await f.AddEmployeeAsync(107, 907, "POLICY-107", "");
        await f.Auth.EnsureEmployeeLoginAsync(107);
        Assert.NotNull(await f.LoginAsync("9000000107", "SyntheticPhone@2026"));

        await f.Db.ExecuteAsync("UPDATE employees SET PortalAccess=FALSE WHERE Id=107");
        Assert.Null(await f.LoginAsync("9000000107", "SyntheticPhone@2026"));
        await f.Db.ExecuteAsync("UPDATE employees SET PortalAccess=TRUE,IsActive=FALSE WHERE Id=107");
        Assert.Null(await f.LoginAsync("9000000107", "SyntheticPhone@2026"));
        await f.Db.ExecuteAsync("UPDATE employees SET IsActive=TRUE WHERE Id=107;UPDATE authusers SET IsActive=FALSE WHERE EmployeeId=107");
        Assert.Null(await f.LoginAsync("9000000107", "SyntheticPhone@2026"));
        await f.Db.ExecuteAsync("UPDATE authusers SET IsActive=TRUE,ClientId=908 WHERE EmployeeId=107");
        Assert.Null(await f.LoginAsync("9000000107", "SyntheticPhone@2026"));
        await f.Db.ExecuteAsync("UPDATE authusers SET ClientId=907 WHERE EmployeeId=107;DELETE ur FROM authuserroles ur JOIN authusers u ON u.Id=ur.UserId WHERE u.EmployeeId=107");
        Assert.Null(await f.LoginAsync("9000000107", "SyntheticPhone@2026"));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public MySqlConnection Db { get; }
        public AuthRepository Auth { get; }
        public EssMssRepository Ess { get; }
        private Fixture(string connection)
        {
            Db = new(connection);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = connection,
                ["EmployeeLogin:InitialPasswordMode"] = "Random"
            }).Build();
            Auth = new(config); Ess = new(config);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = Environment.GetEnvironmentVariable("FREVO_ESS_LOGIN_POLICY_TEST_DB")
                ?? throw new InvalidOperationException("Explicit isolated test connection required.");
            var target = new MySqlConnectionStringBuilder(connection);
            if (target.Server != "127.0.0.1" || target.Port != 33330 || target.Database != "attendance_location_test")
                throw new InvalidOperationException("Only the named loopback ESS test database is permitted.");
            var f = new Fixture(connection);
            await f.Db.OpenAsync();
            await f.Db.ExecuteAsync(@"
SET FOREIGN_KEY_CHECKS=0;
DROP TABLE IF EXISTS authsessions,authuserroles,authrolepermissions,authuser_recruitment_locations,authuser_clients,authusers,authroles,authpermissions,auditlogs,schema_migrations,ess_client_settings,employeepersonaldetails,ResourceStates,employees,worklocations,clients;
SET FOREIGN_KEY_CHECKS=1;
CREATE TABLE clients(Id INT PRIMARY KEY,Name VARCHAR(180),IsActive BOOLEAN NOT NULL DEFAULT TRUE);
CREATE TABLE worklocations(Id INT PRIMARY KEY,ClientId INT,Name VARCHAR(180));
CREATE TABLE employees(Id INT PRIMARY KEY,ClientId INT NOT NULL,EmployeeCode VARCHAR(80),FirstName VARCHAR(100),LastName VARCHAR(100),WorkEmail VARCHAR(190) NOT NULL DEFAULT '',Department VARCHAR(100) NOT NULL DEFAULT '',Designation VARCHAR(100) NOT NULL DEFAULT '',PersonalJson JSON,IsActive BOOLEAN NOT NULL DEFAULT TRUE,PortalAccess BOOLEAN NOT NULL DEFAULT TRUE);
CREATE TABLE ResourceStates(Id BIGINT PRIMARY KEY AUTO_INCREMENT,ResourceType VARCHAR(100),ResourceId VARCHAR(120),CurrentState VARCHAR(60),WorkflowInstanceId BIGINT NULL,CreatedBy INT NOT NULL DEFAULT 0,ModifiedBy INT NOT NULL DEFAULT 0,CreatedOn DATETIME DEFAULT CURRENT_TIMESTAMP,ModifiedOn DATETIME DEFAULT CURRENT_TIMESTAMP,UNIQUE KEY UX_ResourceStates_Resource(ResourceType,ResourceId));
INSERT INTO clients(Id,Name) VALUES(907,'Synthetic login client'),(908,'Synthetic other client');
INSERT INTO worklocations(Id,ClientId,Name) VALUES(907,907,'Synthetic location'),(908,908,'Synthetic other location');
");
            await f.Auth.InitializeAsync();
            await f.Ess.GetEssClientSettingsAsync();
            return f;
        }

        public Task AddEmployeeAsync(int id, int clientId, string code, string aadhaar, string mobile = "9000000107") => Db.ExecuteAsync(@"
INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,PersonalJson)
VALUES(@Id,@ClientId,@Code,'Synthetic','Employee',JSON_OBJECT('aadhaarNumber',@Aadhaar,'mobile',@Mobile))", new { Id = id, ClientId = clientId, Code = code, Aadhaar = aadhaar, Mobile = mobile });

        public async Task EnablePhoneLoginAsync(int clientId)
        {
            var setting = (await Ess.GetEssClientSettingsAsync(clientId)).Single();
            setting.AllowPhoneLogin = true;
            await Ess.SaveEssClientSettingAsync(setting, 901);
        }

        public Task<LoginResponse?> LoginAsync(string loginId, string password, string portal = "ESS") =>
            Auth.LoginAsync(new() { Email = loginId, Password = password, Portal = portal }, "127.0.0.1", "synthetic-policy-test");

        public Task SetPolicyAsync(int clientId, string mode, string fixedPassword = "") => Db.ExecuteAsync(@"
UPDATE ess_client_settings SET InitialPasswordMode=@Mode,FixedPassword=@FixedPassword,IsActive=TRUE WHERE ClientId=@ClientId",
            new { ClientId = clientId, Mode = mode, FixedPassword = fixedPassword });

        public async Task<string[]> PolicySnapshotAsync() => (await Db.QueryAsync<string>(@"
SELECT CONCAT('setting:',ClientId,':',Id,':',AllowProfileEdit,':',InitialPasswordMode,':',FixedPassword,':',IsActive) FROM ess_client_settings
UNION ALL SELECT CONCAT('state:',ResourceType,':',ResourceId,':',CurrentState,':',CreatedBy,':',ModifiedBy) FROM ResourceStates ORDER BY 1")).ToArray();

        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }
}
