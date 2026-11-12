using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using PdfSharp.Pdf.IO;

namespace Payroll.API.Tests.Services;

public sealed class DefaultRecruitmentOfferTemplateTests
{
    [Fact]
    public void Default_is_a_valid_branded_single_page_offer_with_a_signature_slot_and_row_one_mapping()
    {
        var body = DefaultRecruitmentOfferTemplate.Body();
        var layout = LayoutDocumentPdfService.Parse(body);
        Assert.True(LayoutDocumentPdfService.HasSignatureSlot(layout));
        Assert.Equal(1, Assert.Single(layout.ImportPresets).HeaderRow);
        Assert.Equal(1, Assert.Single(layout.Sections).Pages);
        Assert.Contains(layout.Header, element => element.Kind == "image" && element.Asset == "organizationLogo");
        Assert.DoesNotContain("ICMR", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UIDAI", body, StringComparison.OrdinalIgnoreCase);
        var values = Values();
        var before = JsonSerializer.Serialize(values);
        var result = new LayoutDocumentPdfService().Create(body, values, RecruitmentDocumentSigningTests.SyntheticPng());
        Assert.Equal("", result.Error); Assert.NotNull(result.Bytes);
        using var pdf = PdfReader.Open(new MemoryStream(result.Bytes!), PdfDocumentOpenMode.Import);
        Assert.Single(pdf.Pages);
        Assert.Equal(before, JsonSerializer.Serialize(values));
    }

    [Fact]
    public void Default_requires_source_joining_role_location_and_pay_without_inventing_dates_or_salary()
    {
        var layout = LayoutDocumentPdfService.Parse(DefaultRecruitmentOfferTemplate.Body());
        LayoutDocumentPdfService.PrepareValues(layout, new Dictionary<string, string> { ["candidateName"] = "Synthetic Person" }, false, out var missing);
        Assert.Equal(new[] { "Designation", "Date of joining", "Work location", "Monthly gross pay" }, missing);
        Assert.All(layout.Inputs, field => Assert.Equal("", field.DefaultValue));
        Assert.Empty(layout.Calculations);
        Assert.Empty(layout.Checks);
        var ready = LayoutDocumentPdfService.PrepareValues(layout, Values(), true, out missing);
        Assert.Empty(missing); Assert.Equal("2026-11-13", ready["joiningDate"]);
        Assert.Equal("35000", ready["monthlyGross"]);
        Assert.Equal("", ready["annualCtc"]);
        Assert.DoesNotContain(layout.Inputs, field => field.Key is "organizationName" or "clientName" or "offerDate");
    }

    [Fact]
    public void Default_never_overwrites_a_reserved_code_that_has_been_disabled_or_customized()
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(DefaultRecruitmentOfferTemplate.Code, RecruitmentDocumentRepository.AvailableDefaultTemplateCode(codes));
        codes.Add(DefaultRecruitmentOfferTemplate.Code.ToLowerInvariant());
        codes.Add(DefaultRecruitmentOfferTemplate.Code + "_2");
        Assert.Equal(DefaultRecruitmentOfferTemplate.Code + "_3", RecruitmentDocumentRepository.AvailableDefaultTemplateCode(codes));
    }

    [Fact]
    public async Task Ensure_rejects_foreign_clients_and_unauthorized_users_before_opening_a_connection()
    {
        var repository = new RecruitmentDocumentRepository(new ConfigurationBuilder().Build(), null!, new());
        var permitted = new AuthUser { Id = 7, IsActive = true, ClientAccessMode = "Selected", AssignedClientIds = [55], Permissions = ["recruitment.candidate.manage"] };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.EnsureDefaultTemplateAsync(56, permitted));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.EnsureDefaultTemplateAsync(55, new() { Id = 7, IsActive = true, Permissions = ["recruitment.rfr.view"] }));
        permitted.MustChangePassword = true;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.EnsureDefaultTemplateAsync(55, permitted));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.EnsureDefaultTemplateAsync(55, new() { Id = 0, IsActive = true, Permissions = ["settings.manage"] }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.EnsureDefaultTemplateAsync(0, new() { Id = 7, IsActive = true, Permissions = ["recruitment.offer.manage"] }));
    }

    private sealed class IsolatedFactAttribute : FactAttribute
    {
        public IsolatedFactAttribute() { if (Environment.GetEnvironmentVariable("FREVO_DEFAULT_OFFER_TEST_DB") is null) Skip = "Explicit isolated localhost default-offer test database required."; }
    }

    [IsolatedFact]
    public async Task Ensure_is_idempotent_under_concurrency_and_preserves_client_and_global_custom_templates()
    {
        var connection = Environment.GetEnvironmentVariable("FREVO_DEFAULT_OFFER_TEST_DB")!;
        var parsed = new MySqlConnectionStringBuilder(connection);
        Assert.Equal("127.0.0.1", parsed.Server); Assert.Equal((uint)33329, parsed.Port); Assert.Equal("offer_defaults_test", parsed.Database);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connection }).Build();
        await using var db = new MySqlConnection(connection); await db.OpenAsync();
        await db.ExecuteAsync("""
CREATE TABLE IF NOT EXISTS clients(Id INT PRIMARY KEY AUTO_INCREMENT,Name VARCHAR(180) NOT NULL,IsActive BOOLEAN NOT NULL DEFAULT TRUE);
CREATE TABLE IF NOT EXISTS recruitment_templates(Id INT PRIMARY KEY AUTO_INCREMENT,ClientId INT NOT NULL,TemplateType VARCHAR(80) NOT NULL,TemplateCode VARCHAR(80) NOT NULL,TemplateName VARCHAR(180) NOT NULL,SubjectTemplate VARCHAR(300) NOT NULL DEFAULT '',BodyTemplate TEXT,IsHtml BOOLEAN NOT NULL DEFAULT TRUE,IsActive BOOLEAN NOT NULL DEFAULT TRUE,UNIQUE KEY UX_template(ClientId,TemplateCode));
CREATE TABLE IF NOT EXISTS recruitment_admin_audit(Id INT PRIMARY KEY AUTO_INCREMENT,EntityType VARCHAR(100),EntityId INT,Action VARCHAR(80),NewValueJson JSON,ChangedByUserId INT);
DELETE FROM recruitment_admin_audit;
DELETE FROM recruitment_templates;
DELETE FROM clients;
""");
        async Task<int> Client(string name, bool active = true) => await db.ExecuteScalarAsync<int>("INSERT INTO clients(Name,IsActive) VALUES(@Name,@Active);SELECT LAST_INSERT_ID();", new { Name = name, Active = active });
        var client = await Client("Synthetic first client");
        var customClient = await Client("Synthetic custom client");
        var globalClient = await Client("Synthetic global client");
        var inactiveClient = await Client("Synthetic inactive client", false);
        var operatorUser = new AuthUser { Id = 9, IsActive = true, ClientAccessMode = "Selected", AssignedClientIds = [client, customClient, globalClient, inactiveClient], Permissions = ["recruitment.candidate.manage"] };
        var repository = new RecruitmentDocumentRepository(config, null!, new());
        Assert.Empty(await repository.TemplatesAsync(client, operatorUser));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_templates"));
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => repository.EnsureDefaultTemplateAsync(client, operatorUser)));
        var ids = concurrent.Select(result => JsonSerializer.SerializeToElement(Assert.Single(result)).GetProperty("Id").GetInt32()).ToArray();
        Assert.Single(ids.Distinct());
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_templates WHERE ClientId=@Client", new { Client = client }));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_admin_audit WHERE EntityId=@Id", new { Id = ids[0] }));
        Assert.False(RecruitmentPermissions.CanConfigure(operatorUser)); // Fixed provisioning grants no custom authoring permission.

        var customBody = DefaultRecruitmentOfferTemplate.Body().Replace("OFFER LETTER", "CUSTOM OFFER LETTER", StringComparison.Ordinal);
        var customId = await db.ExecuteScalarAsync<int>("INSERT INTO recruitment_templates(ClientId,TemplateType,TemplateCode,TemplateName,BodyTemplate,IsActive) VALUES(@Client,'Offer Letter','CUSTOM','Client-specific offer',@Body,TRUE);SELECT LAST_INSERT_ID();", new { Client = customClient, Body = customBody });
        var existing = Assert.Single(await repository.EnsureDefaultTemplateAsync(customClient, operatorUser));
        Assert.Equal(customId, JsonSerializer.SerializeToElement(existing).GetProperty("Id").GetInt32());
        Assert.Equal(customBody, await db.ExecuteScalarAsync<string>("SELECT BodyTemplate FROM recruitment_templates WHERE Id=@Id", new { Id = customId }));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_templates WHERE ClientId=@Client", new { Client = customClient }));

        var globalId = await db.ExecuteScalarAsync<int>("INSERT INTO recruitment_templates(ClientId,TemplateType,TemplateCode,TemplateName,BodyTemplate,IsActive) VALUES(0,'Offer Letter','GLOBAL','Shared approved offer',@Body,TRUE);SELECT LAST_INSERT_ID();", new { Body = customBody });
        Assert.Equal(globalId, JsonSerializer.SerializeToElement(Assert.Single(await repository.EnsureDefaultTemplateAsync(globalClient, operatorUser))).GetProperty("Id").GetInt32());
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_templates WHERE ClientId=@Client", new { Client = globalClient }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.EnsureDefaultTemplateAsync(inactiveClient, operatorUser));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_templates WHERE ClientId=@Client", new { Client = inactiveClient }));
    }

    private static Dictionary<string, string> Values() => new()
    {
        ["candidateName"] = "Synthetic Person", ["positionTitle"] = "Project Assistant", ["joiningDate"] = "13 November 2026",
        ["jobLocation"] = "Synthetic work location", ["monthlyGross"] = "35,000", ["organizationName"] = "Synthetic Employer",
        ["clientName"] = "Synthetic Client", ["offerNumber"] = "DRAFT/TEST/001", ["offerDate"] = "13/11/2026"
    };
}
