using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class RecruitmentClientConfigurationTests
{
    [Fact]
    public async Task GreenIndicatorsOnlyDescribePermittedClientsAndMissingModulesStayEmpty()
    {
        var target = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FREVO_ICMR_APPROVAL_TEST_DB") ?? throw new InvalidOperationException("Set an owned loopback MySQL fixture."));
        Assert.Contains(target.Server, new[] { "127.0.0.1", "localhost" }); Assert.Equal(33330u, target.Port);
        target.Database = ""; await using var root = new MySqlConnection(target.ConnectionString); await root.OpenAsync();
        var name = "recruitment_client_test_" + Guid.NewGuid().ToString("N");
        await root.ExecuteAsync($"CREATE DATABASE `{name}`"); target.Database = name;
        try
        {
            await using var db = new MySqlConnection(target.ConnectionString); await db.OpenAsync();
            await db.ExecuteAsync("""
CREATE TABLE clients(Id INT); INSERT INTO clients VALUES(55),(20),(70),(71),(72),(73),(74);
CREATE TABLE recruitment_pipeline_definitions(ClientId INT); INSERT INTO recruitment_pipeline_definitions VALUES(55);
CREATE TABLE recruitment_requisitions(ClientId INT); INSERT INTO recruitment_requisitions VALUES(20);
CREATE TABLE recruitment_document_roster(ClientId INT); INSERT INTO recruitment_document_roster VALUES(70);
CREATE TABLE recruitment_templates(ClientId INT); INSERT INTO recruitment_templates VALUES(72),(0);
CREATE TABLE form_definitions(ClientId INT,ModuleCode VARCHAR(80)); INSERT INTO form_definitions VALUES(73,'RECRUITMENT'),(74,'EMPLOYEE'),(0,'RECRUITMENT');
""");
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:Default"] = target.ConnectionString }).Build();
            var repo = new RecruitmentPipelineRepository(config, new WorkflowRepository(config));
            var user = new AuthUser { Id = 1, IsActive = true, ClientId = 55, Permissions = ["recruitment.rfr.view"], ClientAccessMode = "Selected", AssignedClientIds = [55,71,72,73,74] };
            var selected = await repo.ClientConfigurationSummaryAsync(user);
            Assert.Equal([55,71,72,73,74], selected.Select(r => r.ClientId).Order().ToArray());
            Assert.True(selected.Single(r => r.ClientId == 55).HasConfiguration); Assert.False(selected.Single(r => r.ClientId == 71).HasConfiguration);
            Assert.True(selected.Single(r => r.ClientId == 72).HasConfiguration); // Hiring template only.
            Assert.True(selected.Single(r => r.ClientId == 73).HasConfiguration); // Candidate form only.
            Assert.False(selected.Single(r => r.ClientId == 74).HasConfiguration); // Employee forms are a separate module.
            user.ClientAccessMode = "Legacy"; user.ClientId = 20;
            Assert.True(Assert.Single(await repo.ClientConfigurationSummaryAsync(user)).HasConfiguration);
            user.ClientId = null;
            Assert.Equal(7, (await repo.ClientConfigurationSummaryAsync(user)).Count);
            // Browsing all clients must not lazily start candidate journeys.
            // This fixture deliberately has no candidate/runtime tables.
            var ensure = typeof(RecruitmentPipelineRepository).GetMethod("EnsureMissingApplicationPipelinesAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await (Task)ensure.Invoke(repo, [null, null, null, user, null])!;
            user.IsActive = false; await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repo.ClientConfigurationSummaryAsync(user));
            Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_pipeline_definitions"));
        }
        finally
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^recruitment_client_test_[a-f0-9]{32}$")) throw new InvalidOperationException("Unexpected fixture name.");
            await root.ExecuteAsync($"DROP DATABASE `{name}`");
        }
    }
}
