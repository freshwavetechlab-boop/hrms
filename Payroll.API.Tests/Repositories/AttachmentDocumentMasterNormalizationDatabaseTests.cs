using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class AttachmentDocumentMasterNormalizationDatabaseTests
{
    private sealed class IsolatedFactAttribute : FactAttribute
    {
        public IsolatedFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("FREVO_DOCUMENT_MASTER_TEST_DB") is null)
                Skip = "Explicit isolated localhost document master test database required.";
        }
    }

    [IsolatedFact]
    public async Task One_generic_aadhaar_master_preserves_form_rules_files_and_numeric_bindings_with_a_rollback_receipt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var db = fixture.Db;
        var beforeFiles = await SnapshotAsync(db, "entity_attachments", "attachment_attribute_id");
        var beforeConfigurations = await SnapshotAsync(db, "attachment_field_configurations", "attachment_attribute_id", "updated_at_utc", "updated_by_user_id");
        var beforeForms = await SnapshotAsync(db, "form_fields");
        var beforePipeline = await SnapshotAsync(db, "recruitment_stage_attachment_requirements");
        var beforeCertification = await SnapshotAsync(db, "recruitment_jd_certification_requirements");

        await using (var tx = await db.BeginTransactionAsync())
        {
            var result = await AttachmentRepository.NormalizeJoiningDocumentMastersAsync(db, tx, 42);
            Assert.Equal(1, result.MergedMasters);
            Assert.Contains(result.Mappings, mapping => mapping.SourceAttributeId == 66 && mapping.CanonicalAttributeId == 1);
            await tx.CommitAsync();
        }

        var master = await db.QuerySingleAsync<Master>("SELECT id Id,client_id ClientId,attribute_code Code,attribute_name Name,data_classification Classification FROM attachment_attributes WHERE id=1");
        Assert.Equal(0, master.ClientId);
        Assert.Equal("AADHAAR_CARD", master.Code);
        Assert.Equal("Aadhaar Card", master.Name);
        Assert.Equal("Confidential", master.Classification);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_attributes WHERE id=66"));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_attributes WHERE attribute_code IN('AADHAR','AADHAAR_CARD','UIDAI_JOIN_AADHAAR')"));
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_field_configurations WHERE id IN(1,89) AND attachment_attribute_id=1"));
        Assert.Equal(1048576, await db.ExecuteScalarAsync<long>("SELECT maximum_file_size_bytes FROM attachment_field_configurations WHERE id=1"));
        Assert.Equal(10485760, await db.ExecuteScalarAsync<long>("SELECT maximum_file_size_bytes FROM attachment_field_configurations WHERE id=89"));
        Assert.Equal(beforeConfigurations, await SnapshotAsync(db, "attachment_field_configurations", "attachment_attribute_id", "updated_at_utc", "updated_by_user_id"));
        Assert.Equal(beforeFiles, await SnapshotAsync(db, "entity_attachments", "attachment_attribute_id"));
        Assert.Equal(beforeForms, await SnapshotAsync(db, "form_fields"));
        Assert.Equal(beforePipeline, await SnapshotAsync(db, "recruitment_stage_attachment_requirements"));
        Assert.Equal(beforeCertification, await SnapshotAsync(db, "recruitment_jd_certification_requirements"));
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM entity_attachments WHERE attachment_attribute_id=1"));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT AttachmentAttributeId FROM recruitment_document_checklist WHERE Id=2"));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT AttachmentAttributeId FROM recruitment_candidate_checklist_items WHERE Id=3"));

        var receipt = await db.QuerySingleAsync<(int? Actor, string Metadata)>("SELECT actor_user_id Actor,CAST(metadata_json AS CHAR) Metadata FROM attachment_audit_logs WHERE action='DOCUMENT_MASTER_MERGE'");
        Assert.Equal(42, receipt.Actor);
        using var audit = JsonDocument.Parse(receipt.Metadata);
        Assert.Equal(66, Property(audit.RootElement, "sourceAttributeId").GetInt64());
        Assert.Equal(1, Property(audit.RootElement, "canonicalAttributeId").GetInt64());
        var before = Property(audit.RootElement, "before");
        Assert.Equal("UIDAI_JOIN_AADHAAR", Property(Property(before, "source"), "attributeCode").GetString());
        Assert.Equal("AADHAR", Property(Property(before, "canonical"), "attributeCode").GetString());
        Assert.Contains("entity_attachments", receipt.Metadata);
        Assert.Contains("attachment_field_configurations", receipt.Metadata);

        await using var repeated = await db.BeginTransactionAsync();
        var noChanges = await AttachmentRepository.NormalizeJoiningDocumentMastersAsync(db, repeated, 42);
        Assert.Equal(0, noChanges.MergedMasters);
        Assert.Equal(0, noChanges.RenamedMasters);
        await repeated.CommitAsync();
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_audit_logs WHERE action='DOCUMENT_MASTER_MERGE'"));
    }

    [IsolatedFact]
    public async Task Generic_names_reuse_pan_and_academic_ids_without_changing_special_system_codes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var db = fixture.Db;
        await using (var tx = await db.BeginTransactionAsync())
        {
            await AttachmentRepository.NormalizeJoiningDocumentMastersAsync(db, tx);
            await tx.CommitAsync();
        }
        Assert.Equal("PAN_CARD", await db.ExecuteScalarAsync<string>("SELECT attribute_code FROM attachment_attributes WHERE id=65"));
        Assert.Equal("EDUCATIONAL_CERTIFICATES", await db.ExecuteScalarAsync<string>("SELECT attribute_code FROM attachment_attributes WHERE id=69"));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT client_id FROM attachment_attributes WHERE id=65"));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT client_id FROM attachment_attributes WHERE id=69"));
        Assert.Equal("RESUME", await db.ExecuteScalarAsync<string>("SELECT attribute_code FROM attachment_attributes WHERE id=2"));
        Assert.Equal("OFFER_LETTER", await db.ExecuteScalarAsync<string>("SELECT attribute_code FROM attachment_attributes WHERE id=3"));
        Assert.Equal("EXCEL_WORKBOOK", await db.ExecuteScalarAsync<string>("SELECT attribute_code FROM attachment_attributes WHERE id=90"));
        Assert.Equal("Resume", await db.ExecuteScalarAsync<string>("SELECT attribute_name FROM attachment_attributes WHERE id=2"));
        Assert.Equal("Offer letter", await db.ExecuteScalarAsync<string>("SELECT attribute_name FROM attachment_attributes WHERE id=3"));
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_attributes WHERE attribute_code='RESUME'"));
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_attributes WHERE attribute_code='OFFER_LETTER'"));
        Assert.Equal(4, await db.ExecuteScalarAsync<int>("SELECT attachment_attribute_id FROM attachment_field_configurations WHERE id=4"));
        Assert.Equal(30, await db.ExecuteScalarAsync<int>("SELECT attachment_attribute_id FROM attachment_field_configurations WHERE id=30"));
        Assert.Equal(88, await db.ExecuteScalarAsync<long>("SELECT AttachmentFieldConfigurationId FROM form_fields WHERE Id=97"));
        Assert.Equal(92, await db.ExecuteScalarAsync<long>("SELECT AttachmentFieldConfigurationId FROM form_fields WHERE Id=101"));
        Assert.Equal("UIDAI_JOIN_PAN", await db.ExecuteScalarAsync<string>("SELECT StableFieldCode FROM form_fields WHERE Id=97"));
    }

    [IsolatedFact]
    public async Task Incompatible_document_metadata_is_not_silently_merged_or_changed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var db = fixture.Db;
        await db.ExecuteAsync("UPDATE attachment_attributes SET requires_document_number=TRUE WHERE id=66");
        await using (var tx = await db.BeginTransactionAsync())
        {
            var result = await AttachmentRepository.NormalizeJoiningDocumentMastersAsync(db, tx);
            Assert.Equal(0, result.MergedMasters);
            Assert.NotEmpty(result.Skipped);
            await tx.CommitAsync();
        }
        Assert.Equal(66, await db.ExecuteScalarAsync<int>("SELECT attachment_attribute_id FROM attachment_field_configurations WHERE id=89"));
        Assert.Equal(66, await db.ExecuteScalarAsync<int>("SELECT attachment_attribute_id FROM entity_attachments WHERE id=2"));
        Assert.True(await db.ExecuteScalarAsync<bool>("SELECT requires_document_number FROM attachment_attributes WHERE id=66"));
        Assert.False(await db.ExecuteScalarAsync<bool>("SELECT requires_document_number FROM attachment_attributes WHERE id=1"));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_audit_logs WHERE action='DOCUMENT_MASTER_MERGE'"));
    }

    [IsolatedFact]
    public async Task Caller_rollback_restores_masters_references_rules_and_audit_together()
    {
        await using var fixture = await Fixture.CreateAsync();
        var db = fixture.Db;
        var masters = await SnapshotAsync(db, "attachment_attributes");
        var configs = await SnapshotAsync(db, "attachment_field_configurations");
        var files = await SnapshotAsync(db, "entity_attachments");
        await using (var tx = await db.BeginTransactionAsync())
        {
            var result = await AttachmentRepository.NormalizeJoiningDocumentMastersAsync(db, tx);
            Assert.Equal(1, result.MergedMasters);
            await tx.RollbackAsync();
        }
        Assert.Equal(masters, await SnapshotAsync(db, "attachment_attributes"));
        Assert.Equal(configs, await SnapshotAsync(db, "attachment_field_configurations"));
        Assert.Equal(files, await SnapshotAsync(db, "entity_attachments"));
        Assert.Equal(66, await db.ExecuteScalarAsync<int>("SELECT AttachmentAttributeId FROM recruitment_document_checklist WHERE Id=2"));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_audit_logs"));
    }

    [IsolatedFact]
    public async Task Unknown_populated_attribute_reference_prevents_destructive_master_merge()
    {
        await using var fixture = await Fixture.CreateAsync();
        var db = fixture.Db;
        await db.ExecuteAsync("CREATE TABLE custom_document_binding(Id BIGINT PRIMARY KEY,AttachmentAttributeId BIGINT);INSERT INTO custom_document_binding VALUES(1,66)");
        await using (var tx = await db.BeginTransactionAsync())
        {
            var result = await AttachmentRepository.NormalizeJoiningDocumentMastersAsync(db, tx);
            Assert.Equal(0, result.MergedMasters);
            Assert.NotEmpty(result.Skipped);
            await tx.CommitAsync();
        }
        Assert.Equal(66, await db.ExecuteScalarAsync<int>("SELECT attachment_attribute_id FROM attachment_field_configurations WHERE id=89"));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_attributes WHERE id=66"));
        Assert.Equal(66, await db.ExecuteScalarAsync<int>("SELECT AttachmentAttributeId FROM custom_document_binding WHERE Id=1"));
    }

    [IsolatedFact]
    public async Task Saving_another_aadhaar_alias_is_rejected_while_the_existing_generic_master_remains_editable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var db = fixture.Db;
        await using (var tx = await db.BeginTransactionAsync())
        {
            await AttachmentRepository.NormalizeJoiningDocumentMastersAsync(db, tx);
            await tx.CommitAsync();
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = db.ConnectionString }).Build();
        var repository = new AttachmentRepository(config, null!, null!, null!, null!, NullLogger<AttachmentRepository>.Instance);
        var user = new AuthUser { Id = 42, IsActive = true, Permissions = ["settings.manage"] };
        var count = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_attributes");
        var duplicate = await repository.SaveAttributeAsync(new AttachmentAttribute
        {
            ClientId = 20, AttributeCode = "ANOTHER_CLIENT_JOIN_AADHAAR", AttributeName = "Aadhaar Card", DataClassification = "Confidential"
        }, user);
        Assert.Null(duplicate.Item);
        Assert.Contains("existing", duplicate.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(count, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_attributes"));
        var saved = await repository.SaveAttributeAsync(new AttachmentAttribute
        {
            Id = 1, ClientId = 0, AttributeCode = "AADHAAR_CARD", AttributeName = "Aadhaar Card", DataClassification = "Confidential", Description = "Shared proof of identity"
        }, user);
        Assert.Null(saved.Error);
        Assert.NotNull(saved.Item);
        Assert.Equal(1, saved.Item.Id);
        Assert.Equal("Shared proof of identity", saved.Item.Description);
        Assert.Equal(count, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_attributes"));
    }

    private static JsonElement Property(JsonElement element, string name)
        => element.EnumerateObject().Single(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static async Task<string> SnapshotAsync(MySqlConnection db, string table, params string[] excluded)
    {
        var rows = await db.QueryAsync($"SELECT * FROM `{table}` ORDER BY id");
        return JsonSerializer.Serialize(rows.Select(row => ((IDictionary<string, object>)row)
            .Where(pair => !excluded.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value)));
    }

    private sealed class Master
    {
        public int ClientId { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string Classification { get; set; } = "";
    }

    private sealed class Fixture(MySqlConnection owner, MySqlConnection db, string name) : IAsyncDisposable
    {
        public MySqlConnection Db => db;
        public static async Task<Fixture> CreateAsync()
        {
            var parsed = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FREVO_DOCUMENT_MASTER_TEST_DB")!);
            Assert.Equal("127.0.0.1", parsed.Server);
            Assert.Equal(33332u, parsed.Port);
            Assert.Equal("master_fixture", parsed.Database);
            parsed.Database = "";
            var owner = new MySqlConnection(parsed.ConnectionString); await owner.OpenAsync();
            var name = "master_test_" + Guid.NewGuid().ToString("N");
            await owner.ExecuteAsync($"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");
            parsed.Database = name;
            var db = new MySqlConnection(parsed.ConnectionString); await db.OpenAsync();
            await db.ExecuteAsync(Schema);
            return new(owner, db, name);
        }
        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            await owner.ExecuteAsync($"DROP DATABASE `{name}`");
            await owner.DisposeAsync();
        }
    }

    private const string Schema = """
CREATE TABLE clients(Id INT PRIMARY KEY,Name VARCHAR(180)); INSERT INTO clients VALUES(20,'Synthetic UIDAI');
CREATE TABLE attachment_attributes (
 id BIGINT PRIMARY KEY,client_id INT NOT NULL,attribute_code VARCHAR(80) NOT NULL,attribute_name VARCHAR(180) NOT NULL,
 description VARCHAR(500) NOT NULL DEFAULT '',data_classification VARCHAR(30) NOT NULL DEFAULT 'Internal',
 requires_document_number BOOLEAN NOT NULL DEFAULT FALSE,requires_issue_date BOOLEAN NOT NULL DEFAULT FALSE,requires_expiry_date BOOLEAN NOT NULL DEFAULT FALSE,
 is_active BOOLEAN NOT NULL DEFAULT TRUE,created_by_user_id INT NULL,created_at_utc DATETIME(6) DEFAULT CURRENT_TIMESTAMP(6),
 updated_by_user_id INT NULL,updated_at_utc DATETIME(6) DEFAULT CURRENT_TIMESTAMP(6),UNIQUE KEY uq_attribute(client_id,attribute_code));
CREATE TABLE attachment_field_configurations (
 id BIGINT PRIMARY KEY,client_id INT NOT NULL,attachment_attribute_id BIGINT NOT NULL,module_code VARCHAR(60) NOT NULL,form_code VARCHAR(100) NOT NULL,
 section_code VARCHAR(100) NOT NULL DEFAULT 'DOCUMENTS',field_key VARCHAR(120) NOT NULL,field_label VARCHAR(180) NOT NULL,help_text VARCHAR(500) NOT NULL DEFAULT '',
 is_required BOOLEAN NOT NULL DEFAULT FALSE,allow_multiple BOOLEAN NOT NULL DEFAULT FALSE,minimum_file_count INT NOT NULL DEFAULT 0,maximum_file_count INT NOT NULL DEFAULT 1,
 allowed_extensions_json JSON NOT NULL,allowed_mime_types_json JSON NOT NULL,maximum_file_size_bytes BIGINT NOT NULL,maximum_total_size_bytes BIGINT NULL,
 owner_can_view BOOLEAN NOT NULL DEFAULT TRUE,owner_can_upload BOOLEAN NOT NULL DEFAULT TRUE,owner_can_replace BOOLEAN NOT NULL DEFAULT TRUE,owner_can_delete BOOLEAN NOT NULL DEFAULT FALSE,
 requires_verification BOOLEAN NOT NULL DEFAULT FALSE,versioning_enabled BOOLEAN NOT NULL DEFAULT TRUE,requirement_scope VARCHAR(30) DEFAULT 'NewEntitiesOnly',
 display_order INT NOT NULL DEFAULT 100,effective_from_utc DATETIME(6) NULL,effective_until_utc DATETIME(6) NULL,is_active BOOLEAN NOT NULL DEFAULT TRUE,
 created_by_user_id INT NULL,created_at_utc DATETIME(6) DEFAULT CURRENT_TIMESTAMP(6),updated_by_user_id INT NULL,updated_at_utc DATETIME(6) DEFAULT CURRENT_TIMESTAMP(6),
 UNIQUE KEY uq_configuration(client_id,module_code,form_code,field_key));
CREATE TABLE entity_attachments (
 id BIGINT PRIMARY KEY,public_id CHAR(36),client_id INT,attachment_attribute_id BIGINT,field_configuration_id BIGINT,entity_type VARCHAR(60),entity_id BIGINT,
 storage_server_id BIGINT,storage_key VARCHAR(700),original_file_name VARCHAR(180),sha256_hash CHAR(64),version_number INT,is_current BOOLEAN,uploaded_by_user_id INT);
CREATE TABLE recruitment_document_checklist(Id BIGINT PRIMARY KEY,AttachmentAttributeId BIGINT);
CREATE TABLE recruitment_candidate_checklist_items(Id BIGINT PRIMARY KEY,AttachmentAttributeId BIGINT);
CREATE TABLE attachment_audit_logs (
 id BIGINT AUTO_INCREMENT PRIMARY KEY,attachment_id BIGINT NULL,client_id INT,entity_type VARCHAR(60),entity_id BIGINT,action VARCHAR(40),actor_user_id INT NULL,
 success BOOLEAN,failure_reason VARCHAR(500),ip_address VARCHAR(80),user_agent VARCHAR(500),metadata_json JSON,created_at_utc DATETIME(6) DEFAULT CURRENT_TIMESTAMP(6));
CREATE TABLE form_definitions(Id BIGINT PRIMARY KEY,ClientId INT,ModuleCode VARCHAR(80),FormCode VARCHAR(120),FormName VARCHAR(180),PurposeCode VARCHAR(100),Status VARCHAR(30),CurrentPublishedVersionId BIGINT);
CREATE TABLE form_versions(Id BIGINT PRIMARY KEY,FormDefinitionId BIGINT,Status VARCHAR(30));
CREATE TABLE form_field_types(Id INT PRIMARY KEY,TypeCode VARCHAR(40));
CREATE TABLE form_fields(Id BIGINT PRIMARY KEY,FormVersionId BIGINT,FieldTypeId INT,AttachmentFieldConfigurationId BIGINT,IsActive BOOLEAN,StableFieldCode VARCHAR(120));
CREATE TABLE recruitment_stage_attachment_requirements(Id BIGINT PRIMARY KEY,AttachmentFieldConfigurationId BIGINT);
CREATE TABLE recruitment_jd_certification_requirements(Id BIGINT PRIMARY KEY,CandidateProofAttachmentFieldConfigurationId BIGINT);
INSERT INTO attachment_attributes(id,client_id,attribute_code,attribute_name,data_classification) VALUES
 (1,0,'AADHAR','Adhaar card','Internal'),(66,20,'UIDAI_JOIN_AADHAAR','Aadhaar Card','Confidential'),
 (65,20,'UIDAI_JOIN_PAN','PAN Card','Confidential'),(69,20,'UIDAI_JOIN_ACADEMIC','Academic certificates (Class 10 onwards)','Confidential'),
 (2,0,'RESUME','Resume','Internal'),(4,20,'RESUME','Resume / CV','Internal'),
 (3,0,'OFFER_LETTER','Offer letter','Internal'),(30,20,'OFFER_LETTER','Released offer letter','Internal'),(90,0,'EXCEL_WORKBOOK','Original workbook','Restricted');
INSERT INTO attachment_field_configurations(id,client_id,attachment_attribute_id,module_code,form_code,field_key,field_label,is_required,allowed_extensions_json,allowed_mime_types_json,maximum_file_size_bytes) VALUES
 (1,0,1,'EMPLOYEE','EMPLOYEE_CREATE_EDIT','ADHAAR_DOC','Adhaar Card',TRUE,'["pdf"]','["application/pdf"]',1048576),
 (89,20,66,'RECRUITMENT','UIDAI_JOINING_DOCUMENTS','UIDAI_JOIN_AADHAAR','Aadhaar Card',TRUE,'["pdf","png"]','["application/pdf","image/png"]',10485760),
 (88,20,65,'RECRUITMENT','UIDAI_JOINING_DOCUMENTS','UIDAI_JOIN_PAN','PAN Card',TRUE,'["pdf"]','["application/pdf"]',2097152),
 (92,20,69,'RECRUITMENT','UIDAI_JOINING_DOCUMENTS','UIDAI_JOIN_ACADEMIC','Academic certificates (Class 10 onwards)',TRUE,'["pdf"]','["application/pdf"]',3145728),
 (4,20,4,'RECRUITMENT','CANDIDATE_APPLICATION','RESUME','Resume / CV',TRUE,'["pdf"]','["application/pdf"]',10485760),
 (30,20,30,'RECRUITMENT','PRE_ONBOARDING','OFFER_LETTER','Released offer letter',TRUE,'["pdf"]','["application/pdf"]',10485760);
INSERT INTO entity_attachments VALUES
 (1,'00000000-0000-0000-0000-000000000001',55,1,1,'EMPLOYEE',55,9,'55/EMPLOYEE/55/AADHAR/file.pdf','file.pdf',REPEAT('a',64),2,TRUE,12),
 (2,'00000000-0000-0000-0000-000000000002',20,66,89,'CANDIDATE',20,9,'20/CANDIDATE/20/UIDAI_JOIN_AADHAAR/original.pdf','original.pdf',REPEAT('b',64),3,TRUE,13);
INSERT INTO recruitment_document_checklist VALUES(2,66);
INSERT INTO recruitment_candidate_checklist_items VALUES(3,66);
INSERT INTO form_definitions VALUES(7,20,'RECRUITMENT','UIDAI_JOINING_DOCUMENTS','UIDAI Joining Documents','DOCUMENT_REQUEST','Active',13);
INSERT INTO form_versions VALUES(13,7,'Published'); INSERT INTO form_field_types VALUES(1,'UPLOAD');
INSERT INTO form_fields VALUES(98,13,1,89,TRUE,'UIDAI_JOIN_AADHAAR'),(97,13,1,88,TRUE,'UIDAI_JOIN_PAN'),(101,13,1,92,TRUE,'UIDAI_JOIN_ACADEMIC');
INSERT INTO recruitment_stage_attachment_requirements VALUES(1,89);
INSERT INTO recruitment_jd_certification_requirements VALUES(1,89);
""";
}
