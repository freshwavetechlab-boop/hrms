using Microsoft.Extensions.Configuration;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class SelectedClientOwnershipBatchTests
{
    [Fact]
    public async Task EmptyAndNonSqlPrimingNeverOpensAConnection()
    {
        var store = new SelectedClientOwnershipStore(new ConfigurationBuilder().Build());
        await store.PrimeAsync([]);
        await store.PrimeAsync([new("unknown-resource", "1"), new("workflow-task", "1"), new("employee-import-job", Guid.NewGuid().ToString())]);
    }

    [Theory]
    [InlineData("employee", "Id", "ClientId", "employees")]
    [InlineData("interview", "i.Id", "COALESCE(a.ClientId,i.DirectClientId)", "recruitment_interviews")]
    [InlineData("attachment", "public_id", "client_id", "entity_attachments")]
    [InlineData("workflow", "Id", "COALESCE(ClientId,0)", "workflowmasters")]
    public void BatchSqlKeepsAuthoritativeOwnershipAndParameterizedIdentifiers(string resource, string key, string owner, string table)
    {
        Assert.True(SelectedClientOwnershipStore.TryBuildBatchSql(resource, out var sql));
        Assert.StartsWith($"SELECT CAST({key} AS CHAR) ReferenceId,{owner} ClientId FROM ", sql);
        Assert.Contains(table, sql);
        Assert.EndsWith($" WHERE {key} IN @Ids", sql);
        Assert.DoesNotContain("=@Id", sql);
    }

    [Theory]
    [InlineData("unregistered-table")]
    [InlineData("workflow-task")]
    [InlineData("workflow-instance")]
    [InlineData("salary-structure")]
    [InlineData("pf-policy-version")]
    [InlineData("employee-import-job")]
    public void DynamicAndUnknownResourcesDoNotBecomeGenericSql(string resource) =>
        Assert.False(SelectedClientOwnershipStore.TryBuildBatchSql(resource, out _));

    [Theory]
    [InlineData("employee", "001", "1")]
    [InlineData("employee", "1", "1")]
    [InlineData("attachment", "001", "001")]
    [InlineData("attendance-job", "001", "001")]
    [InlineData("payroll-review", "001", "001")]
    public void NumericAliasesAndTextKeysPreserveTheirDatabaseIdentity(string resource, string input, string expected) =>
        Assert.Equal(expected, SelectedClientOwnershipStore.NormalizeBatchKey(resource, input));
}
