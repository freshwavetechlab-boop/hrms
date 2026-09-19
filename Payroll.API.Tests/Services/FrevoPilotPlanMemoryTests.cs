using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class FrevoPilotPlanMemoryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "frevopilot-plan-memory-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(directory, "plans.enc");
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static JsonElement Contract => Json("""
        {"version":"simple-count-v1","recordTable":"employees","measure":"count_records",
         "grouping":{"table":"employees","column":"Gender"},"requestedFilters":[],
         "recordDefinitionFilters":[],"requiredRelationships":[],"allowedTables":["employees"],
         "noOtherFiltersRequested":true}
        """);
    private static JsonElement Payload(string sql = "SELECT Gender, COUNT(*) total FROM employees GROUP BY Gender") =>
        JsonSerializer.SerializeToElement(new { sql, parameters = Array.Empty<string>() });
    private static string Protect(string value) => "encrypted:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static string? Unprotect(string value) => value.StartsWith("encrypted:", StringComparison.Ordinal)
        ? Encoding.UTF8.GetString(Convert.FromBase64String(value[10..])) : null;
    private static FrevoPilotPlanSession Session(FrevoPilotPlanMemory memory, string scope = "actor1|clientRRU|schema1|policy1", string provider = "local|model1|endpoint1")
    {
        var session = memory.CreateSession(scope);
        session.Prepare(provider, Contract);
        return session;
    }
    private static void Save(FrevoPilotPlanSession session, JsonElement? payload = null) =>
        Assert.True(session.Promote(Assert.IsType<string>(session.RememberCandidate(payload ?? Payload()))));

    [Fact]
    public void CandidateIsNotVisibleUntilItsOwnSessionPromotesItsOneUseReceipt()
    {
        var memory = new FrevoPilotPlanMemory();
        var first = Session(memory);
        var other = Session(memory);
        var receipt = Assert.IsType<string>(first.RememberCandidate(Payload()));
        Assert.False(first.TryGet(out _));
        Assert.False(other.Promote(receipt));
        Assert.False(first.Promote(Guid.NewGuid().ToString("N")));
        Assert.True(first.Promote(receipt));
        Assert.False(first.Promote(receipt));
        Assert.True(other.TryGet(out var result));
        Assert.Equal(Payload().GetRawText(), result.GetRawText());
    }

    [Theory]
    [InlineData("actor2|clientRRU|schema1|policy1", "local|model1|endpoint1")]
    [InlineData("actor1|clientOther|schema1|policy1", "local|model1|endpoint1")]
    [InlineData("actor1|clientRRU|schema2|policy1", "local|model1|endpoint1")]
    [InlineData("actor1|clientRRU|schema1|policy2", "local|model1|endpoint1")]
    [InlineData("actor1|clientRRU|schema1|policy1", "local|model2|endpoint1")]
    [InlineData("actor1|clientRRU|schema1|policy1", "local|model1|endpoint2")]
    public void FingerprintsIsolateActorScopeSchemaPolicyAndProvider(string scope, string provider)
    {
        var memory = new FrevoPilotPlanMemory();
        Save(Session(memory));
        Assert.False(Session(memory, scope, provider).TryGet(out _));
    }

    [Fact]
    public void RecursiveObjectOrderingDoesNotChangeIdentityButGroupingDoes()
    {
        var memory = new FrevoPilotPlanMemory();
        var session = Session(memory);
        var receipt = Assert.IsType<string>(session.RememberCandidate(Payload()));
        var reversed = JsonNode.Parse(Contract.GetRawText())!.AsObject();
        reversed["grouping"] = new JsonObject { ["column"] = "Gender", ["table"] = "employees" };
        var reordered = new JsonObject(reversed.Reverse().Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value?.DeepClone())));
        session.Prepare("local|model1|endpoint1", Json(reordered.ToJsonString()));
        Assert.True(session.Promote(receipt));
        Assert.True(Session(memory).TryGet(out _));
        reversed["grouping"]!["column"] = "Department";
        session.Prepare("local|model1|endpoint1", Json(reversed.ToJsonString()));
        Assert.False(session.TryGet(out _));
    }

    [Fact]
    public void InvalidationRetainsContractForRepairButRevokesPendingReceipts()
    {
        var memory = new FrevoPilotPlanMemory();
        var session = Session(memory);
        Save(session);
        var failedReceipt = Assert.IsType<string>(session.RememberCandidate(Payload()));
        session.Invalidate();
        Assert.False(session.TryGet(out _));
        Assert.False(session.Promote(failedReceipt));
        Assert.Equal(Contract.GetRawText(), session.Contract!.Value.GetRawText());
        Assert.Equal("local|model1|endpoint1", session.ProviderFingerprint);
        Save(session, Payload("SELECT Gender, COUNT(Id) total FROM employees GROUP BY Gender"));
        Assert.True(Session(memory).TryGet(out var corrected));
        Assert.Contains("COUNT(Id)", corrected.GetProperty("sql").GetString());
    }

    [Fact]
    public void ChangingProviderOrUnsupportedContractRevokesCandidateState()
    {
        var memory = new FrevoPilotPlanMemory();
        var session = Session(memory);
        var receipt = Assert.IsType<string>(session.RememberCandidate(Payload()));
        session.Prepare("different-provider", Contract);
        Assert.False(session.Promote(receipt));
        session.Prepare("different-provider", Json("{\"version\":\"unknown\"}"));
        Assert.Null(session.Contract);
        Assert.Null(session.ProviderFingerprint);
        Assert.Null(session.RememberCandidate(Payload()));
        Assert.False(session.TryGet(out _));
    }

    [Fact]
    public void PendingCandidatesAreBoundedAndOnlySqlAndParametersSurvive()
    {
        var session = Session(new FrevoPilotPlanMemory());
        var payload = Json("""{"sql":"SELECT COUNT(*) FROM employees","parameters":[],"rows":[{"private":"never-save"}],"summary":"never-save","prompt":"never-save","apiKey":"never-save"}""");
        var receipts = Enumerable.Range(0, 4).Select(_ => Assert.IsType<string>(session.RememberCandidate(payload))).ToArray();
        Assert.Null(session.RememberCandidate(payload));
        Assert.True(session.Promote(receipts[0]));
        Assert.True(session.TryGet(out var stored));
        Assert.Equal(new[] { "sql", "parameters" }, stored.EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain("never-save", stored.GetRawText());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"sql\":\"DELETE FROM employees\",\"parameters\":[]}")]
    [InlineData("{\"sql\":\"SELECT COUNT(*) FROM employees\"}")]
    [InlineData("{\"sql\":123,\"parameters\":[]}")]
    [InlineData("{\"sql\":\"SELECT COUNT(*) FROM employees\",\"parameters\":[1]}")]
    [InlineData("{\"sql\":\"SELECT COUNT(*) FROM employees\",\"parameters\":[null]}")]
    [InlineData("{\"sql\":\"SELECT COUNT(*) FROM employees\",\"sql\":\"SELECT 1\",\"parameters\":[]}")]
    public void InvalidPayloadsNeverReceivePromotableReceipts(string payload)
    {
        var session = Session(new FrevoPilotPlanMemory());
        Assert.Null(session.RememberCandidate(Json(payload)));
        Assert.False(session.TryGet(out _));
    }

    [Fact]
    public void SqlParameterAndEntryCountsAreBounded()
    {
        var memory = new FrevoPilotPlanMemory();
        var session = Session(memory);
        Assert.Null(session.RememberCandidate(Payload("SELECT " + new string('x', 20_000))));
        Assert.Null(session.RememberCandidate(JsonSerializer.SerializeToElement(new { sql = "SELECT COUNT(*) FROM employees", parameters = new[] { new string('x', 2001) } })));
        Assert.Null(session.RememberCandidate(JsonSerializer.SerializeToElement(new { sql = "SELECT COUNT(*) FROM employees", parameters = Enumerable.Repeat("x", 21) })));
        for (var index = 0; index < 129; index++) Save(Session(memory, "scope-" + index));
        Assert.False(Session(memory, "scope-0").TryGet(out _));
        Assert.True(Session(memory, "scope-128").TryGet(out _));
    }

    [Fact]
    public void PersistenceRequiresBothProtectionDelegatesAndReloadsEncryptedPlans()
    {
        Save(Session(new FrevoPilotPlanMemory(FilePath)));
        Assert.False(File.Exists(FilePath));
        Save(Session(new FrevoPilotPlanMemory(FilePath, Protect)));
        Assert.False(File.Exists(FilePath));
        Save(Session(new FrevoPilotPlanMemory(FilePath, Protect, Unprotect)));
        Assert.DoesNotContain("SELECT", File.ReadAllText(FilePath));
        Assert.True(Session(new FrevoPilotPlanMemory(FilePath, Protect, Unprotect)).TryGet(out _));
        Session(new FrevoPilotPlanMemory(FilePath, Protect, Unprotect)).Invalidate();
        Assert.False(Session(new FrevoPilotPlanMemory(FilePath, Protect, Unprotect)).TryGet(out _));
    }

    [Fact]
    public void ExpiredAndFutureDatedEntriesAreNotLoaded()
    {
        Save(Session(new FrevoPilotPlanMemory(FilePath, Protect, Unprotect)));
        var saved = JsonNode.Parse(Unprotect(File.ReadAllText(FilePath))!)!;
        foreach (var created in new[] { DateTimeOffset.UtcNow.AddDays(-8), DateTimeOffset.UtcNow.AddDays(1) })
        {
            saved["entries"]![0]!["createdUtc"] = created;
            File.WriteAllText(FilePath, Protect(saved.ToJsonString()));
            Assert.False(Session(new FrevoPilotPlanMemory(FilePath, Protect, Unprotect)).TryGet(out _));
        }
    }

    [Fact]
    public void CorruptOversizedAndUnavailableStorageFailOpen()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, "corrupt");
        var corrupted = new FrevoPilotPlanMemory(FilePath, Protect, Unprotect);
        Assert.False(Session(corrupted).TryGet(out _));
        Save(Session(corrupted));
        File.WriteAllText(FilePath, new string('x', FrevoPilotPlanMemory.MaximumBytes + 1));
        Assert.False(Session(new FrevoPilotPlanMemory(FilePath, Protect, Unprotect)).TryGet(out _));
        var unavailable = Session(new FrevoPilotPlanMemory(directory, Protect, Unprotect));
        Save(unavailable);
        Assert.True(unavailable.TryGet(out _));
        var failedProtection = Session(new FrevoPilotPlanMemory(FilePath, _ => throw new InvalidOperationException(), Unprotect));
        Save(failedProtection);
        Assert.True(failedProtection.TryGet(out _));
    }

    [Fact]
    public void TotalMemoryAndEncryptedFileSizeStayBounded()
    {
        var memory = new FrevoPilotPlanMemory();
        var payload = JsonSerializer.SerializeToElement(new
        {
            sql = "SELECT " + new string('x', 19_000),
            parameters = Enumerable.Repeat(new string('y', 2_000), 20).ToArray()
        });
        for (var index = 0; index < 128; index++) Save(Session(memory, "large-" + index), payload);
        var retained = Enumerable.Range(0, 128).Count(index => Session(memory, "large-" + index).TryGet(out _));
        Assert.InRange(retained, 1, 18);
        var oversizedEncryption = Session(new FrevoPilotPlanMemory(FilePath, _ => new string('x', FrevoPilotPlanMemory.MaximumBytes + 1), Unprotect));
        Save(oversizedEncryption);
        Assert.False(File.Exists(FilePath));
        Assert.True(oversizedEncryption.TryGet(out _));
    }

    [Fact]
    public void IdentityProtectionCannotWriteOrLoadPlaintextPlans()
    {
        Save(Session(new FrevoPilotPlanMemory(FilePath, text => text, text => text)));
        Assert.False(File.Exists(FilePath));
        Save(Session(new FrevoPilotPlanMemory(FilePath, Protect, Unprotect)));
        File.WriteAllText(FilePath, Unprotect(File.ReadAllText(FilePath))!);
        Assert.False(Session(new FrevoPilotPlanMemory(FilePath, text => text, text => text)).TryGet(out _));
    }

    [Fact]
    public void RealProtectionNearCapacityPersistsInvalidationWithoutExceedingFileLimit()
    {
        var protector = new EphemeralDataProtectionProvider().CreateProtector("FrevoPilotPlanMemoryTests.v1");
        FrevoPilotPlanMemory Memory() => new(FilePath, protector.Protect, protector.Unprotect);
        var memory = Memory();
        var payload = JsonSerializer.SerializeToElement(new
        {
            sql = "SELECT " + new string('x', 19_000),
            parameters = Enumerable.Repeat(new string('y', 2_000), 20).ToArray()
        });
        for (var index = 0; index < 30; index++) Save(Session(memory, "encrypted-large-" + index), payload);
        Assert.InRange(new FileInfo(FilePath).Length, 1, FrevoPilotPlanMemory.MaximumBytes);
        var plaintextBytes = Encoding.UTF8.GetByteCount(protector.Unprotect(File.ReadAllText(FilePath)));
        Assert.InRange(plaintextBytes, 800 * 1024, FrevoPilotPlanMemory.MaximumMemoryBytes);
        var loaded = Session(Memory(), "encrypted-large-29");
        Assert.True(loaded.TryGet(out _));
        loaded.Invalidate();
        Assert.False(Session(Memory(), "encrypted-large-29").TryGet(out _));
        Assert.InRange(new FileInfo(FilePath).Length, 1, FrevoPilotPlanMemory.MaximumBytes);
    }

    [Fact]
    public void ConcurrentSessionsKeepReceiptsAndPlansIndependent()
    {
        var memory = new FrevoPilotPlanMemory();
        Parallel.For(0, 64, index => Save(Session(memory, "scope-" + index)));
        for (var index = 0; index < 64; index++) Assert.True(Session(memory, "scope-" + index).TryGet(out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
