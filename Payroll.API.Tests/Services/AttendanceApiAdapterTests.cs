using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class AttendanceApiAdapterTests
{
    private static AttendanceApiAdapter Config() => new()
    {
        ClientId = 11, Name = "Client machine", DeviceId = "gate-01", SourceDeviceId = "TW123",
        Url = "https://vendor.example/attendance", InValues = ["0"], OutValues = ["1"],
        BodyTemplate = """{"FromDate":"{{fromDate}}","ToDate":"{{toDate}}","DeviceID":"{{deviceId}}"}"""
    };
    private static JsonElement Row(string user = "0012", string mode = "0", string device = "TW123", string time = "2026-11-01T09:00:00") =>
        JsonSerializer.SerializeToElement(new { UserID = user, InOutMode = mode, DeviceID = device, PunchTime = time });
    private static readonly DateTime From = new(2026, 11, 1);
    private static readonly DateTime To = new(2026, 11, 2);

    [Fact]
    public void PausedDraftCanBeSavedWithoutInventingMachineOrDirectionValues()
    {
        var c = Config(); c.DeviceId = ""; c.SourceDeviceId = ""; c.InValues = []; c.OutValues = [];
        AttendanceApiProtocol.Validate(c, allowIncompleteDraft: true);
        Assert.Throws<InvalidOperationException>(() => AttendanceApiProtocol.Validate(c));
        c.DeviceId = "gate-01"; c.SourceDeviceId = "TW123";
        Assert.Throws<InvalidOperationException>(() => AttendanceApiProtocol.Validate(c));
        c.IsActive = true;
        Assert.Throws<InvalidOperationException>(() => AttendanceApiProtocol.Validate(c, allowIncompleteDraft: true));
    }

    [Fact]
    public void DraftStillRejectsConflictingDirectionsAndUnsafeTemplates()
    {
        var c = Config(); c.InValues = ["0"]; c.OutValues = ["0"];
        Assert.Throws<InvalidOperationException>(() => AttendanceApiProtocol.Validate(c, allowIncompleteDraft: true));
        c.InValues = []; c.OutValues = []; c.BodyTemplate = """{"value":"{{eval(code)}}"}""";
        Assert.Throws<InvalidOperationException>(() => AttendanceApiProtocol.Validate(c, allowIncompleteDraft: true));
    }

    [Fact]
    public void IdmsMapsLeadingZeroUserIdTimezoneAndVerifiedDirections()
    {
        var config = Config(); config.EmployeeMap["0012"] = "EMP001";
        var punch = AttendanceApiProtocol.Map(config, Row(), From, To)!;
        Assert.Equal("EMP001", punch.EmployeeCode);
        Assert.Equal("IN", punch.Action);
        Assert.EndsWith("+05:30", punch.CapturedAt);
        Assert.Equal("OUT", AttendanceApiProtocol.Map(config, Row(mode: "1"), From, To)!.Action);
    }

    [Fact]
    public void OtherMachineAndOutOfRangeRecordsAreNeverMapped()
    {
        Assert.Null(AttendanceApiProtocol.Map(Config(), Row(device: "TW999"), From, To));
        Assert.Null(AttendanceApiProtocol.Map(Config(), Row(time: "2026-10-31T23:59:59"), From, To));
        Assert.Null(AttendanceApiProtocol.Map(Config(), Row(time: "2026-11-03T00:00:00"), From, To));
    }

    [Fact]
    public void NumericActionsAreNeverGuessed()
    {
        var config = Config(); config.InValues = ["IN"]; config.OutValues = ["OUT"];
        Assert.Throws<InvalidOperationException>(() => AttendanceApiProtocol.Map(config, Row(), From, To));
        config.InValues = ["0"]; config.OutValues = ["0"];
        Assert.Throws<InvalidOperationException>(() => AttendanceApiProtocol.Validate(config));
    }

    [Fact]
    public void RetryIdentitySurvivesConfigurationRenameAndUsesActualPunchTime()
    {
        var c = Config(); var first = AttendanceApiProtocol.Map(c, Row(), From, To)!;
        c.Name = "Renamed"; c.Id = "different-config";
        Assert.Equal(first.PunchId, AttendanceApiProtocol.Map(c, Row(), From, To)!.PunchId);
        Assert.Equal(first.PunchId, AttendanceApiProtocol.Map(c, Row(time: "2026-11-01T03:30:00Z"), From, To)!.PunchId);
        Assert.NotEqual(first.PunchId, AttendanceApiProtocol.Map(c, Row(time: "2026-11-01T09:00:01"), From, To)!.PunchId);
        Assert.NotEqual(first.PunchId, AttendanceApiProtocol.Map(c, Row(mode: "1"), From, To)!.PunchId);
        Assert.InRange(first.PunchId.Length, 1, 100);
    }

    [Fact]
    public void ConfiguredPunchIdMustExistAndDoesNotIncludeMutableFields()
    {
        var c = Config(); c.PunchIdPath = "id";
        Assert.Throws<InvalidOperationException>(() => AttendanceApiProtocol.Map(c, Row(), From, To));
        var a = JsonSerializer.SerializeToElement(new { id = "log-123", UserID = "0012", InOutMode = "0", DeviceID = "TW123", PunchTime = "2026-11-01T09:00:00" });
        var first = AttendanceApiProtocol.Map(c, a, From, To)!;
        c.EmployeeMap["0012"] = "EMP001";
        Assert.Equal(first.PunchId, AttendanceApiProtocol.Map(c, a, From, To)!.PunchId);
    }

    [Fact]
    public void NestedJsonRootArraysAndProviderFailureAreHandled()
    {
        var c = Config(); c.RecordsPath = "result.logs";
        using var doc = JsonDocument.Parse("""{"Success":true,"result":{"logs":[{"user":{"id":"0012"}}]}}""");
        var rows = AttendanceApiProtocol.Records(c, doc.RootElement);
        Assert.Equal("0012", AttendanceApiProtocol.Scalar(rows, "0.user.id"));
        c.SuccessValue = "false";
        Assert.Throws<InvalidOperationException>(() => AttendanceApiProtocol.Records(c, doc.RootElement));
        c.SuccessPath = ""; c.RecordsPath = "$";
        Assert.Equal(1, AttendanceApiProtocol.Records(c, rows).GetArrayLength());
        c.RecordsPath = "missing";
        Assert.Throws<InvalidOperationException>(() => AttendanceApiProtocol.Records(c, doc.RootElement));
    }

    [Fact]
    public async Task TemplatesEscapeSecretsAndKeepPagingValuesNumeric()
    {
        var c = Config(); c.AuthType = "None"; c.Pagination = "Page";
        c.BodyTemplate = """{"key":"{{secret}}","page":"{{page}}","nested":{"limit":"{{pageSize}}"},"devices":["{{deviceId}}"]}""";
        c.Query["from"] = "{{fromDate}}"; c.Query["token"] = "{{secret}}";
        AttendanceApiProtocol.Validate(c);
        const string secret = "key\"with\\quotes&=?";
        using var request = AttendanceApiProtocol.BuildRequest(c, From, To, 2, secret);
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert.Equal(secret, body.RootElement.GetProperty("key").GetString());
        Assert.Equal(3, body.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(500, body.RootElement.GetProperty("nested").GetProperty("limit").GetInt32());
        Assert.Equal("TW123", body.RootElement.GetProperty("devices")[0].GetString());
        Assert.Contains("token=" + Uri.EscapeDataString(secret), request.RequestUri!.OriginalString);
    }

    [Fact]
    public void GetUsesQueryAndAuthHeaderWithoutABody()
    {
        var c = Config(); c.Method = "GET"; c.Query["device"] = "{{deviceId}}";
        using var request = AttendanceApiProtocol.BuildRequest(c, From, To, 0, "test-key");
        Assert.Null(request.Content);
        Assert.Equal("test-key", request.Headers.GetValues("X-Api-Key").Single());
        Assert.Contains("device=TW123", request.RequestUri!.Query);
        Assert.Throws<InvalidOperationException>(() => AttendanceApiProtocol.BuildRequest(c, From, To, 0, ""));
    }

    [Fact]
    public async Task DateTimeRequestsCoverTheWholeEndDateAndSupportExclusiveBounds()
    {
        var c = Config(); c.RequestDateFormat = "yyyy-MM-dd HH:mm:ss";
        c.BodyTemplate = """{"from":"{{fromDate}}","to":"{{toDate}}","exclusive":"{{toDateExclusive}}"}""";
        using var request = AttendanceApiProtocol.BuildRequest(c, From, To, 0, "key");
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert.Equal("2026-11-01 00:00:00", body.RootElement.GetProperty("from").GetString());
        Assert.Equal("2026-11-02 23:59:59", body.RootElement.GetProperty("to").GetString());
        Assert.Equal("2026-11-03 00:00:00", body.RootElement.GetProperty("exclusive").GetString());
    }

    [Fact]
    public void AdapterCredentialsArePortablePurposeSeparatedAndAuthenticated()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["IntegrationCredentialEncryption:MasterKey"] = "synthetic-test-master-key" }).Build();
        PortableIntegrationCredentialProtector Create() => new(config, new EphemeralDataProtectionProvider(), NullLogger<PortableIntegrationCredentialProtector>.Instance);
        var first = Create(); var restarted = Create();
        const string json = """{"Secret":"vendor-test-key","DeviceToken":"machine-test-key"}""";
        var ciphertext = first.ProtectAttendanceAdapter(json);
        Assert.DoesNotContain("vendor-test-key", ciphertext);
        Assert.Equal(json, restarted.UnprotectAttendanceAdapter(ciphertext));
        Assert.False(first.TryUnprotectStorage(ciphertext, out _));
        Assert.Null(first.UnprotectAttendanceAdapter(first.ProtectAi(json)));
        Assert.Null(first.UnprotectAttendanceAdapter(ciphertext[..^6] + "AAAAAA"));
    }

    [Theory]
    [InlineData("127.0.0.1", true, false)]
    [InlineData("::1", true, false)]
    [InlineData("::ffff:127.0.0.1", true, false)]
    [InlineData("169.254.169.254", true, false)]
    [InlineData("fd00:ec2::254", true, false)]
    [InlineData("168.63.129.16", true, false)]
    [InlineData("192.168.1.66", false, false)]
    [InlineData("192.168.1.66", true, true)]
    [InlineData("10.20.0.1", true, true)]
    [InlineData("100.64.0.1", false, false)]
    [InlineData("100.64.0.1", true, true)]
    [InlineData("8.8.8.8", false, true)]
    [InlineData("::", true, false)]
    [InlineData("ff02::1", true, false)]
    public void NetworkPolicyRequiresPrivateOptInAndAlwaysBlocksLocalMetadata(string ip, bool allowPrivate, bool expected) =>
        Assert.Equal(expected, AttendanceApiProtocol.AllowedAddress(IPAddress.Parse(ip), allowPrivate));

    [Fact]
    public void AuthorizationMatchesGlobalIntegrationSettingsPermission()
    {
        Assert.False(AttendanceApiAdapterEndpoints.Allowed(null));
        Assert.False(AttendanceApiAdapterEndpoints.Allowed(new AuthUser { Permissions = ["attendance.manage"] }));
        Assert.False(AttendanceApiAdapterEndpoints.Allowed(new AuthUser { ClientId = 11, Permissions = ["settings.manage"] }));
        Assert.True(AttendanceApiAdapterEndpoints.Allowed(new AuthUser { Permissions = ["settings.manage"] }));
    }

    [Fact]
    public void ValidationRejectsInvalidRangePaginationAndExecutableTemplates()
    {
        Assert.Throws<InvalidOperationException>(() => AttendanceApiProtocol.DateRange(new("2026-11-02", "2026-11-01")));
        Assert.Throws<InvalidOperationException>(() => AttendanceApiProtocol.DateRange(new("2026-01-01", "2026-11-01")));
        var c = Config(); c.Pagination = "Page";
        Assert.Throws<InvalidOperationException>(() => AttendanceApiProtocol.Validate(c));
        c.Pagination = "None"; c.BodyTemplate = """{"value":"{{eval(code)}}"}""";
        Assert.Throws<InvalidOperationException>(() => AttendanceApiProtocol.Validate(c));
        c.BodyTemplate = "{}"; c.Headers["Host"] = "internal.example";
        Assert.Throws<InvalidOperationException>(() => AttendanceApiProtocol.Validate(c));
    }
}
