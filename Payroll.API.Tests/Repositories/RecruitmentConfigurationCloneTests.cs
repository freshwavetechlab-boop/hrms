using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class RecruitmentConfigurationCloneTests
{
    private static AuthUser Selected() => new() { Id = 1, IsActive = true, ClientId = 20, ClientAccessMode = "Selected", AssignedClientIds = [20, 30], Permissions = ["recruitment.configuration.manage"] };
    private static RecruitmentFormRepository Forms(IConfiguration config) => new(config, NullLogger<RecruitmentFormRepository>.Instance, null!, null!, null!);

    [Fact]
    public async Task CopiesRejectUnassignedTargetsBeforeOpeningADatabase()
    {
        var config = new ConfigurationBuilder().Build();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new RecruitmentAdminRepository(config).CreateSimilarTemplateAsync(1, new() { TargetClientId = 99, TemplateName = "Copy", TemplateCode = "COPY" }, Selected()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Forms(config).CreateSimilarFormAsync(1, new() { TargetClientId = 99, FormName = "Copy", FormCode = "COPY" }, Selected()));
    }

    [LocalInterviewDatabaseFact]
    public async Task TemplateCopyPreservesSourceLayoutAndCreatesIndependentTargetWithScopedSources()
    {
        await using var f = await RecruitmentPipelineCloneTests.Database.CreateAsync();
        await PrepareAsync(f.Db);
        var repository = new RecruitmentAdminRepository(f.Config);
        var before = JsonSerializer.Serialize(await f.Db.QuerySingleAsync<RecruitmentTemplate>("SELECT * FROM recruitment_templates WHERE Id=1"));
        var (copy, error) = await repository.CreateSimilarTemplateAsync(1, new() { TargetClientId = 30, TemplateCode = "NEW_OFFER", TemplateName = "Target offer" }, Selected());
        Assert.Empty(error); Assert.NotNull(copy); Assert.NotEqual(1, copy.Id); Assert.Equal(30, copy.ClientId);
        Assert.Equal("Offer Letter", copy.TemplateType); Assert.Equal("{\"type\":\"layout\",\"pages\":8}", copy.BodyTemplate);
        Assert.Equal("Hello {{candidate_name}}", copy.SubjectTemplate); Assert.False(copy.IsHtml);
        Assert.Equal(before, JsonSerializer.Serialize(await f.Db.QuerySingleAsync<RecruitmentTemplate>("SELECT * FROM recruitment_templates WHERE Id=1")));
        Assert.DoesNotContain(await repository.ListTemplatesAsync(Selected(), null), row => row.ClientId == 99);
        Assert.Contains(await repository.ListTemplatesAsync(Selected(), null), row => row.ClientId == 0);
        Assert.Equal(new[] { 0, 30 }, (await repository.ListTemplatesAsync(Selected(), 30)).Select(row => row.ClientId).Distinct().Order().ToArray());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.CreateSimilarTemplateAsync(3, new() { TargetClientId = 30, TemplateCode = "DENIED", TemplateName = "Denied" }, Selected()));
        await Assert.ThrowsAsync<MySqlException>(() => repository.CreateSimilarTemplateAsync(1, new() { TargetClientId = 30, TemplateCode = "NEW_OFFER", TemplateName = "Duplicate" }, Selected()));
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_admin_audit"));
    }

    [LocalInterviewDatabaseFact]
    public async Task CandidateFormCopyResetsNestedIdsRemapsRulesAndKeepsPublishedJobAndSubmissionsUntouched()
    {
        await using var f = await RecruitmentPipelineCloneTests.Database.CreateAsync(); await PrepareAsync(f.Db);
        var repository = Forms(f.Config);
        var before = JsonSerializer.Serialize(await repository.GetAsync(1, Selected()));
        var (copy, error) = await repository.CreateSimilarFormAsync(1, new() { TargetClientId = 30, FormCode = "COPY", FormName = "Target candidate form" }, Selected());
        Assert.Empty(error); Assert.NotNull(copy); Assert.Equal(30, copy.Definition.ClientId); Assert.Null(copy.Definition.CurrentPublishedVersionId);
        Assert.Equal("Draft", copy.Version.Status); Assert.Equal(1, copy.Version.VersionNumber); Assert.Null(copy.Version.PublishedByUserId); Assert.Null(copy.Version.PublishedAtUtc);
        var fields = Assert.Single(copy.Version.Sections).Fields; Assert.Equal(4, fields.Count);
        Assert.All(fields, field => Assert.DoesNotContain(field.Id, new long[] { 11, 12, 13, 14 }));
        var current = fields.Single(field => field.StableFieldCode == "CURRENT_CTC"); var expected = fields.Single(field => field.StableFieldCode == "EXPECTED_CTC");
        var rule = Assert.Single(expected.ValidationRules); Assert.Equal(current.Id, rule.CompareFieldId); Assert.Equal("CURRENT_CTC", rule.CompareFieldCode);
        Assert.Equal(2, fields.Single(field => field.StableFieldCode == "FIRST_NAME").Options.Count);
        var resume = fields.Single(field => field.StableFieldCode == "RESUME"); Assert.True(resume.IsRequired); Assert.Contains("RESUME", resume.SemanticCodes); Assert.Null(resume.AttachmentFieldConfigurationId); Assert.Single(copy.Warnings);
        Assert.Equal(before, JsonSerializer.Serialize(await repository.GetAsync(1, Selected())));
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT ApplicationFormVersionId FROM fixture_live_jobs"));
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM fixture_submissions WHERE FormVersionId=1"));
        var targetUser = Selected(); targetUser.ClientId = 30;
        var (published, publishError) = await repository.PublishAsync(copy.Version.Id, targetUser); Assert.Null(published); Assert.Contains("Resume", publishError);
        var (sameClient, sameError) = await repository.CreateSimilarFormAsync(1, new() { SourceVersionId = 1, TargetClientId = 20, FormCode = "SAME", FormName = "Same client" }, Selected());
        Assert.Empty(sameError); Assert.NotNull(sameClient); Assert.Empty(sameClient.Warnings); Assert.Equal(50, sameClient.Version.Sections[0].Fields.Single(field => field.StableFieldCode == "RESUME").AttachmentFieldConfigurationId);
    }

    [LocalInterviewDatabaseFact]
    public async Task CandidateFormCopyRejectsEmployeeSourcesForeignVersionsAndRollsBackInvalidRules()
    {
        await using var f = await RecruitmentPipelineCloneTests.Database.CreateAsync(); await PrepareAsync(f.Db);
        var repository = Forms(f.Config);
        var request = new CreateSimilarRecruitmentForm { TargetClientId = 30, FormCode = "COPY", FormName = "Copy" };
        var (employee, employeeError) = await repository.CreateSimilarFormAsync(2, request, Selected()); Assert.Null(employee); Assert.Contains("candidate", employeeError);
        request.SourceVersionId = 2;
        var (foreign, foreignError) = await repository.CreateSimilarFormAsync(1, request, Selected()); Assert.Null(foreign); Assert.Contains("does not belong", foreignError);
        request.SourceVersionId = 1;
        await f.Db.ExecuteAsync("UPDATE form_field_validation_rules SET CompareFieldId=999 WHERE Id=1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CreateSimilarFormAsync(1, request, Selected()));
        Assert.Equal(3, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM form_definitions"));
        Assert.Equal(2, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM form_versions"));
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_admin_audit"));
        Assert.Equal(new[] { 1L, 2L }, (await repository.ListAsync(Selected(), null)).Select(row => row.Id).Order().ToArray());
        Assert.Null(await repository.GetAsync(3, Selected()));
    }

    private static Task PrepareAsync(MySqlConnection db) => db.ExecuteAsync("""
DROP TABLE recruitment_templates,form_versions,form_definitions,attachment_field_configurations;
CREATE TABLE recruitment_admin_audit(Id BIGINT PRIMARY KEY AUTO_INCREMENT,EntityType VARCHAR(80),EntityId BIGINT,Action VARCHAR(80),NewValueJson JSON,ChangedByUserId INT);
CREATE TABLE recruitment_templates(Id INT PRIMARY KEY AUTO_INCREMENT,ClientId INT,TemplateType VARCHAR(80),TemplateCode VARCHAR(80),TemplateName VARCHAR(180),SubjectTemplate VARCHAR(300),BodyTemplate TEXT,IsHtml BOOLEAN,IsActive BOOLEAN,UNIQUE(ClientId,TemplateCode));
INSERT INTO recruitment_templates VALUES(1,20,'Offer Letter','SOURCE','Source','Hello {{candidate_name}}','{"type":"layout","pages":8}',FALSE,TRUE),(2,0,'Email','GLOBAL','Global','','Shared',TRUE,TRUE),(3,99,'Email','PRIVATE','Private','','Private',TRUE,TRUE);
CREATE TABLE form_definitions(Id BIGINT PRIMARY KEY AUTO_INCREMENT,ClientId INT,ModuleCode VARCHAR(80),FormCode VARCHAR(120),FormName VARCHAR(180),PurposeCode VARCHAR(100),EntityType VARCHAR(80),Status VARCHAR(30),RequiresEmailVerification BOOLEAN,CurrentPublishedVersionId BIGINT NULL,CreatedByUserId INT,CreatedAtUtc DATETIME(6) DEFAULT CURRENT_TIMESTAMP(6),UpdatedAtUtc DATETIME(6) DEFAULT CURRENT_TIMESTAMP(6),UNIQUE(ClientId,ModuleCode,FormCode));
INSERT INTO form_definitions(Id,ClientId,ModuleCode,FormCode,FormName,PurposeCode,EntityType,Status,RequiresEmailVerification,CurrentPublishedVersionId,CreatedByUserId) VALUES(1,20,'RECRUITMENT','SOURCE','Candidate source','CANDIDATE_APPLICATION','CANDIDATE','Active',TRUE,1,1),(2,20,'EMPLOYEE','EMPLOYEE','Employee source','EMPLOYEE_INFOTYPE_BASIC','EMPLOYEE','Active',FALSE,2,1),(3,99,'RECRUITMENT','PRIVATE','Private form','CANDIDATE_APPLICATION','CANDIDATE','Active',TRUE,NULL,1);
CREATE TABLE form_versions(Id BIGINT PRIMARY KEY AUTO_INCREMENT,FormDefinitionId BIGINT,VersionNumber INT,Status VARCHAR(30),CreatedByUserId INT,PublishedByUserId INT NULL,PublishedAtUtc DATETIME(6) NULL,CreatedAtUtc DATETIME(6) DEFAULT CURRENT_TIMESTAMP(6),UNIQUE(FormDefinitionId,VersionNumber));
INSERT INTO form_versions(Id,FormDefinitionId,VersionNumber,Status,CreatedByUserId,PublishedByUserId,PublishedAtUtc) VALUES(1,1,1,'Published',1,1,UTC_TIMESTAMP(6)),(2,2,1,'Published',1,1,UTC_TIMESTAMP(6));
CREATE TABLE form_sections(Id BIGINT PRIMARY KEY AUTO_INCREMENT,FormVersionId BIGINT,SectionCode VARCHAR(100),SectionLabel VARCHAR(180),Description VARCHAR(500),DisplayOrder INT);
INSERT INTO form_sections VALUES(1,1,'GENERAL','General','Candidate information',1);
CREATE TABLE form_field_types(Id INT PRIMARY KEY,TypeCode VARCHAR(40),IsActive BOOLEAN); INSERT INTO form_field_types VALUES(1,'TEXT',TRUE),(2,'NUMBER',TRUE),(3,'UPLOAD',TRUE);
CREATE TABLE form_lookup_sources(Id BIGINT PRIMARY KEY,SourceCode VARCHAR(100));
CREATE TABLE attachment_attributes(id BIGINT PRIMARY KEY,is_active BOOLEAN,requires_document_number BOOLEAN,requires_issue_date BOOLEAN,requires_expiry_date BOOLEAN);
CREATE TABLE attachment_field_configurations(id BIGINT PRIMARY KEY,client_id INT,is_active BOOLEAN,allow_multiple BOOLEAN,minimum_file_count INT,maximum_file_count INT,maximum_file_size_bytes BIGINT,maximum_total_size_bytes BIGINT NULL,allowed_extensions_json JSON,allowed_mime_types_json JSON,attachment_attribute_id BIGINT NULL,effective_from_utc DATETIME NULL,effective_until_utc DATETIME NULL);
INSERT INTO attachment_field_configurations VALUES(50,20,TRUE,FALSE,1,1,10485760,NULL,'[".pdf"]','["application/pdf"]',NULL,NULL,NULL);
CREATE TABLE form_fields(Id BIGINT PRIMARY KEY AUTO_INCREMENT,FormVersionId BIGINT,SectionId BIGINT,FieldTypeId INT,StableFieldCode VARCHAR(120),Label VARCHAR(180),Placeholder VARCHAR(250) DEFAULT '',HelpText VARCHAR(500) DEFAULT '',IsRequired BOOLEAN,DisplayOrder INT,WidthColumns INT DEFAULT 12,MinimumLength INT NULL,MaximumLength INT NULL,MinimumNumber DECIMAL(20,4) NULL,MaximumNumber DECIMAL(20,4) NULL,MinimumDate DATE NULL,MaximumDate DATE NULL,AttachmentFieldConfigurationId BIGINT NULL,LookupSourceId BIGINT NULL,IsActive BOOLEAN);
INSERT INTO form_fields(Id,FormVersionId,SectionId,FieldTypeId,StableFieldCode,Label,IsRequired,DisplayOrder,AttachmentFieldConfigurationId,IsActive) VALUES(11,1,1,1,'FIRST_NAME','First name',TRUE,1,NULL,TRUE),(12,1,1,2,'CURRENT_CTC','Current CTC',FALSE,2,NULL,TRUE),(13,1,1,2,'EXPECTED_CTC','Expected CTC',FALSE,3,NULL,TRUE),(14,1,1,3,'RESUME','Resume',TRUE,4,50,TRUE);
CREATE TABLE form_field_options(Id BIGINT PRIMARY KEY AUTO_INCREMENT,FieldId BIGINT,OptionCode VARCHAR(120),OptionLabel VARCHAR(180),DisplayOrder INT,IsActive BOOLEAN); INSERT INTO form_field_options VALUES(1,11,'A','A',1,TRUE),(2,11,'B','B',2,TRUE);
CREATE TABLE form_semantic_attributes(Id INT PRIMARY KEY,SemanticCode VARCHAR(100),IsActive BOOLEAN); INSERT INTO form_semantic_attributes VALUES(1,'FIRST_NAME',TRUE),(2,'RESUME',TRUE),(3,'CURRENT_CTC',TRUE),(4,'EXPECTED_CTC',TRUE);
CREATE TABLE form_field_semantic_mappings(FieldId BIGINT,SemanticAttributeId INT,PRIMARY KEY(FieldId,SemanticAttributeId)); INSERT INTO form_field_semantic_mappings VALUES(11,1),(12,3),(13,4),(14,2);
CREATE TABLE form_field_validation_rules(Id BIGINT PRIMARY KEY AUTO_INCREMENT,FieldId BIGINT,RuleType VARCHAR(60),ComparisonOperator VARCHAR(30),CompareFieldId BIGINT NULL,TextValue VARCHAR(500) NULL,IntegerValue BIGINT NULL,DecimalValue DECIMAL(20,4) NULL,DateValue DATE NULL,BooleanValue BOOLEAN NULL,ErrorMessage VARCHAR(500),DisplayOrder INT);
INSERT INTO form_field_validation_rules(Id,FieldId,RuleType,ComparisonOperator,CompareFieldId,ErrorMessage,DisplayOrder) VALUES(1,13,'COMPARE_FIELD','GTE',12,'Expected must exceed current',1);
CREATE TABLE fixture_live_jobs(Id INT PRIMARY KEY,ApplicationFormVersionId BIGINT); INSERT INTO fixture_live_jobs VALUES(1,1);
CREATE TABLE fixture_submissions(Id INT PRIMARY KEY,FormVersionId BIGINT); INSERT INTO fixture_submissions VALUES(1,1);
""");
}
