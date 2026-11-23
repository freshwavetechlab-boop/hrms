using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Repositories;

public sealed class RecruitmentDocumentCollectionDatabaseTests
{
    private sealed class IsolatedFactAttribute : FactAttribute
    {
        public IsolatedFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("FREVO_DOCUMENT_COLLECTION_TEST_DB") is null) Skip = "Explicit isolated localhost document collection database required.";
        }
    }

    [IsolatedFact]
    public async Task Requests_reuse_global_catalog_rotate_recipient_and_complete_atomically_once_even_concurrently()
    {
        await using var f = await Fixture.CreateAsync();
        var fields = await f.Repository.GetCatalogAsync(55, f.User);
        Assert.Equal(11, fields.Count);
        var selected = new[] { new RecruitmentRequestedDocument { FieldConfigurationId = fields[0].FieldConfigurationId, Required = true }, new RecruitmentRequestedDocument { FieldConfigurationId = fields[1].FieldConfigurationId, Required = false } };
        var first = await f.LinkAsync(selected);
        Assert.Equal(first, await f.LinkAsync(selected));
        await Assert.ThrowsAsync<RecruitmentDocumentCollectionValidationException>(() => f.LinkAsync([
            new() { FieldConfigurationId = selected[0].FieldConfigurationId, Required = false }, selected[1]]));
        Assert.True((await f.Repository.GetPublicAsync(first))!.Documents[0].Required);
        var raw = await f.Db.QuerySingleAsync<(string TokenHash, string TokenCipherText, string RecipientHash)>("SELECT TokenHash,TokenCipherText,RecipientHash FROM recruitment_document_collection_requests");
        Assert.DoesNotContain(first, raw.TokenHash + raw.TokenCipherText + raw.RecipientHash);
        Assert.DoesNotContain("recipient@example.com", raw.RecipientHash);
        var second = await f.LinkAsync(selected, "corrected@example.com");
        Assert.NotEqual(first, second);
        Assert.Null(await f.Repository.GetPublicAsync(first));
        var open = await f.Repository.GetPublicAsync(second);
        Assert.Equal("Open", open!.Status); Assert.Equal(2, open.Documents.Count); Assert.Equal(0, open.Documents[1].MinimumFileCount);
        var publicJson = JsonSerializer.Serialize(open);
        Assert.DoesNotContain("ICMR", publicJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recipient", publicJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CandidateId", publicJson);
        await Assert.ThrowsAsync<RecruitmentDocumentCollectionValidationException>(() => f.Repository.SubmitAsync(second, "other@example.com", new(), f.Files(selected[0].FieldConfigurationId), "", "", default));
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM entity_attachments"));
        var submissions = await Task.WhenAll(
            f.Repository.SubmitAsync(second, "CORRECTED@example.com", new(), f.Files(selected[0].FieldConfigurationId), "", "", default),
            f.Repository.SubmitAsync(second, "corrected@example.com", new(), f.Files(selected[0].FieldConfigurationId), "", "", default));
        Assert.Single(submissions, r => r!.AlreadySubmitted);
        Assert.Single(submissions, r => !r!.AlreadySubmitted);
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM entity_attachments WHERE entity_type='CANDIDATE' AND entity_id=1 AND is_current=TRUE"));
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_document_collection_files"));
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM person_activity_events WHERE EventType='OFFER_DOCUMENTS_SUBMITTED'"));
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_audit_logs WHERE action='EXTERNAL_DOCUMENT_UPLOAD'"));
        await f.Db.ExecuteAsync("UPDATE recruitment_document_collection_requests SET ExpiresAtUtc=DATE_SUB(UTC_TIMESTAMP(),INTERVAL 1 DAY)");
        var completed = await f.Repository.GetPublicAsync(second);
        Assert.Equal("Completed", completed!.Status); Assert.Empty(completed.Documents);
        Assert.Equal(second, await f.LinkAsync(selected, "another@example.com"));
        Assert.True((await f.Repository.SubmitAsync(second, "", new(), new FormFileCollection(), "", "", default))!.AlreadySubmitted);
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_document_collection_requests"));
        var aadhaarFields = await AttachmentRepository.LoadJoiningDocumentConfigurationsAsync(f.Db, null, 55);
        Assert.Equal(900, Assert.Single(aadhaarFields, field => AttachmentRepository.JoiningDocumentKind(field) == "AADHAAR").AttachmentAttributeId);
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_field_configurations WHERE form_code='DOCUMENT_REQUEST'"));
        await f.Repository.InitializeAsync();
        Assert.Equal(11, (await f.Repository.GetCatalogAsync(55, f.User)).Count);
        await f.Db.ExecuteAsync("UPDATE attachment_attributes SET is_active=FALSE WHERE attribute_code='PAN_CARD' AND client_id=0");
        await f.Repository.InitializeAsync();
        Assert.Equal(10, (await f.Repository.GetCatalogAsync(55, f.User)).Count);
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT is_active FROM attachment_attributes WHERE attribute_code='PAN_CARD' AND client_id=0"));
    }

    [IsolatedFact]
    public async Task Existing_published_joining_fields_are_shared_with_their_ids_limits_bindings_and_files_preserved()
    {
        await using var f = await Fixture.CreateAsync(seedLegacyJoiningDocuments: true);
        var sourceFields = new long[] { 8701, 8702, 8703 };
        foreach (var client in new[] { 20, 55, 70 })
        {
            var user = new AuthUser { Id = 1, IsActive = true, ClientAccessMode = "Selected", AssignedClientIds = [client], Permissions = ["recruitment.offer.manage"] };
            var catalog = await f.Repository.GetCatalogAsync(client, user);
            Assert.All(sourceFields, id => Assert.Single(catalog, field => field.FieldConfigurationId == id));
            var configured = await AttachmentRepository.LoadJoiningDocumentConfigurationsAsync(f.Db, null, client);
            Assert.Equal(2097152, Assert.Single(configured, field => field.Id == 8701).MaximumFileSizeBytes);
            Assert.Equal(3, Assert.Single(configured, field => field.Id == 8702).MaximumFileCount);
            Assert.Equal("EMPLOYEE_CREATE_EDIT", Assert.Single(configured, field => field.AttachmentAttributeId == 900).FormCode);
        }
        Assert.Equal(sourceFields, (await f.Db.QueryAsync<long>("SELECT AttachmentFieldConfigurationId FROM form_fields ORDER BY Id")).ToArray());
        Assert.Equal(3, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_field_configurations WHERE id IN @Ids AND client_id=0", new { Ids = sourceFields }));
        Assert.Equal(3, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_attributes WHERE id IN(901,902,903) AND client_id=0"));
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_field_configurations WHERE form_code='DOCUMENT_REQUEST'"));
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_attributes WHERE attribute_code IN('UIDAI_JOIN_PAN','PAN_CARD')"));
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_attributes WHERE attribute_code IN('UIDAI_JOIN_ACADEMIC','EDUCATIONAL_CERTIFICATES')"));
        var stored = await f.Db.QuerySingleAsync<(int ClientId, long AttributeId, long ConfigurationId, string StorageKey)>("SELECT client_id ClientId,attachment_attribute_id AttributeId,field_configuration_id ConfigurationId,storage_key StorageKey FROM entity_attachments");
        Assert.Equal((20, 901L, 8701L, "unchanged/private-proof.pdf"), stored);
        var count = await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_field_configurations");
        await f.Repository.InitializeAsync();
        Assert.Equal(count, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_field_configurations"));
        await f.Db.ExecuteAsync("UPDATE attachment_field_configurations SET is_active=FALSE WHERE id=8701");
        await f.Repository.InitializeAsync();
        Assert.DoesNotContain(await f.Repository.GetCatalogAsync(55, f.User), field => field.FieldConfigurationId == 8701);
        Assert.DoesNotContain(await AttachmentRepository.LoadJoiningDocumentConfigurationsAsync(f.Db, null, 55), field => AttachmentRepository.JoiningDocumentKind(field) == "PAN");
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT is_active FROM attachment_field_configurations WHERE id=8701"));
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_field_configurations WHERE attachment_attribute_id=901"));
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_attributes WHERE attribute_code IN('UIDAI_JOIN_PAN','PAN_CARD')"));
    }

    [IsolatedFact]
    public async Task External_collection_uses_existing_employee_attachment_configuration_and_keeps_ordinary_candidate_files_visible_once()
    {
        await using var f = await Fixture.CreateAsync(seedLegacyJoiningDocuments: true);
        await f.Db.ExecuteAsync(@"
INSERT INTO recruitment_open_positions VALUES(20,55,9);
INSERT INTO recruitment_candidate_applications VALUES(10,1,55,20);
INSERT INTO worklocations VALUES(9,55,'Posting one',TRUE);
INSERT INTO entity_attachments(public_id,client_id,attachment_attribute_id,field_configuration_id,storage_server_id,entity_type,entity_id,original_file_name,stored_file_name,storage_key,file_extension,detected_mime_type,file_size_bytes,sha256_hash,uploaded_by_user_id)
SELECT '00000000-0000-0000-0000-000000000002',55,901,8701,id,'CANDIDATE',1,'legacy-pan.pdf','legacy-pan.pdf','unchanged/legacy-pan.pdf','pdf','application/pdf',10,REPEAT('b',64),1 FROM attachment_storage_servers WHERE server_code='API_LOCAL';");
        var viewer = new AuthUser { Id = 20, IsActive = true, ClientAccessMode = "Selected", AssignedClientIds = [55], Permissions = ["attachment.recruitment.view"] };
        var legacy = Assert.Single(await f.Attachments.GetAttachmentsAsync("CANDIDATE", 1, viewer));
        Assert.Equal(8701, legacy.FieldConfigurationId);
        var token = await f.LinkAsync([new() { FieldConfigurationId = 8700, Required = true }], applicationId: 10);
        await f.Repository.SubmitAsync(token, "recipient@example.com", new(), f.Files(8700), "", "", default);
        var files = (await f.Attachments.GetAttachmentsAsync("CANDIDATE", 1, viewer)).ToList();
        Assert.Equal(2, files.Count);
        Assert.Equal(2, files.Select(file => file.PublicId).Distinct().Count());
        Assert.Single(files, file => file.PublicId == legacy.PublicId);
        var collected = Assert.Single(files, file => file.FieldConfigurationId == 8700);
        Assert.Equal(900, collected.AttachmentAttributeId);
        Assert.Equal(55, collected.ClientId);
        Assert.NotNull((await f.Attachments.GetForContentAsync(legacy.PublicId, viewer, "PREVIEW", "", "")).Attachment);
        Assert.NotNull((await f.Attachments.GetForContentAsync(collected.PublicId, viewer, "PREVIEW", "", "")).Attachment);
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_field_configurations WHERE form_code='DOCUMENT_REQUEST'"));
    }

    [IsolatedFact]
    public async Task Existing_custom_joining_configuration_also_works_through_the_normal_private_attachment_upload()
    {
        await using var f = await Fixture.CreateAsync(seedLegacyJoiningDocuments: true);
        var editor = new AuthUser { Id = 30, IsActive = true, ClientAccessMode = "Selected", AssignedClientIds = [55], Permissions = ["attachment.recruitment.upload"] };
        var upload = await f.Attachments.UploadAsync(new AttachmentUploadMetadata { EntityType = "CANDIDATE", EntityId = 1, FieldConfigurationId = 8701 }, f.File(8701), editor, "", "", default);
        Assert.Null(upload.Error);
        Assert.NotNull(upload.Attachment);
        Assert.Equal(8701, upload.Attachment!.FieldConfigurationId);
        Assert.Equal(901, upload.Attachment.AttachmentAttributeId);
        Assert.Equal(55, upload.Attachment.ClientId);
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_field_configurations WHERE id=8701 AND form_code='UIDAI_JOINING_DOCUMENTS'"));
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_field_configurations WHERE form_code='DOCUMENT_REQUEST'"));
    }

    [IsolatedFact]
    public async Task Resending_an_open_request_keeps_its_exact_existing_configuration_after_a_new_client_override_becomes_preferred()
    {
        await using var f = await Fixture.CreateAsync(seedLegacyJoiningDocuments: true);
        var token = await f.LinkAsync([new() { FieldConfigurationId = 8701, Required = true }]);
        await f.Db.ExecuteAsync(@"
INSERT INTO attachment_field_configurations(id,client_id,attachment_attribute_id,module_code,form_code,field_key,field_label,allowed_extensions_json,allowed_mime_types_json,maximum_file_size_bytes)
VALUES(8801,55,901,'RECRUITMENT','UIDAI_JOINING_DOCUMENTS','UIDAI_JOIN_PAN','PAN Card','[""pdf""]','[""application/pdf""]',2097152);
INSERT INTO form_definitions VALUES(11,55,'RECRUITMENT','LOCAL_JOINING_DOCUMENTS','Local placement','DOCUMENT_REQUEST','Active',14);
INSERT INTO form_versions VALUES(14,11,'Published');
INSERT INTO form_fields VALUES(99,14,1,8801,TRUE,'PAN');");
        Assert.Contains(await f.Repository.GetCatalogAsync(55, f.User), field => field.FieldConfigurationId == 8801);
        Assert.DoesNotContain(await f.Repository.GetCatalogAsync(55, f.User), field => field.FieldConfigurationId == 8701);
        string resent;
        await using (var tx = await f.Db.BeginTransactionAsync())
        {
            resent = await f.Repository.EnsureLinkAsync(f.Db, tx, 55, 1, "recipient@example.com", 1);
            await tx.CommitAsync();
        }
        Assert.Equal(token, resent);
        Assert.Equal("8701", Assert.Single((await f.Repository.GetPublicAsync(token))!.Documents).Id);
        await f.Repository.SubmitAsync(token, "recipient@example.com", new(), f.Files(8701), "", "", default);
        Assert.Equal(8701, await f.Db.ExecuteScalarAsync<long>("SELECT field_configuration_id FROM entity_attachments WHERE entity_type='CANDIDATE' AND entity_id=1"));
    }

    [IsolatedFact]
    public async Task Storage_failure_rolls_back_all_attachment_rows_and_deletes_already_staged_bytes()
    {
        await using var f = await Fixture.CreateAsync();
        var catalog = await f.Repository.GetCatalogAsync(55, f.User);
        var selected = catalog.Take(2).Select(d => new RecruitmentRequestedDocument { FieldConfigurationId = d.FieldConfigurationId, Required = true }).ToArray();
        var token = await f.LinkAsync(selected);
        var files = f.Files(selected[0].FieldConfigurationId);
        files.Add(new FailingWriteFile("files-" + selected[1].FieldConfigurationId));
        var masterAuditCount = await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_audit_logs");
        await Assert.ThrowsAsync<RecruitmentDocumentCollectionValidationException>(() => f.Repository.SubmitAsync(token, "recipient@example.com", new(), files, "", "", default));
        Assert.Equal("Open", (await f.Repository.GetPublicAsync(token))!.Status);
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM entity_attachments"));
        Assert.Equal(masterAuditCount, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_audit_logs"));
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM attachment_audit_logs WHERE action='EXTERNAL_DOCUMENT_UPLOAD'"));
        Assert.Empty(Directory.GetFiles(f.UploadRoot, "*", SearchOption.AllDirectories));
        // A corrected retry succeeds after the failed submission; no partial state consumed the link.
        var retry = f.Files(selected[0].FieldConfigurationId); retry.Add(f.File(selected[1].FieldConfigurationId));
        Assert.False((await f.Repository.SubmitAsync(token, "recipient@example.com", new(), retry, "", "", default))!.AlreadySubmitted);
        Assert.Equal(2, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM entity_attachments"));
    }

    [IsolatedFact]
    public async Task Cross_client_candidate_documents_are_visible_only_in_the_collecting_client_and_location()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.ExecuteAsync(@"
UPDATE recruitment_candidates SET ClientId=0,EmployeeId=222 WHERE Id=1;
INSERT INTO clients VALUES(70,'Second client','SECOND');
INSERT INTO recruitment_open_positions VALUES(20,55,9),(21,70,10);
INSERT INTO recruitment_candidate_applications VALUES(10,1,55,20),(11,1,70,21);
INSERT INTO worklocations VALUES(9,55,'Posting one',TRUE),(10,70,'Posting two',TRUE);");
        var field = (await f.Repository.GetCatalogAsync(55, f.User))[0].FieldConfigurationId;
        var selected = new[] { new RecruitmentRequestedDocument { FieldConfigurationId = field, Required = true } };
        var first = await f.LinkAsync(selected, applicationId: 10);
        await f.Repository.SubmitAsync(first, "recipient@example.com", new(), f.Files(field), "", "", default);
        string second;
        await using (var tx = await f.Db.BeginTransactionAsync())
        {
            second = await f.Repository.EnsureLinkAsync(f.Db, tx, 70, 1, "second@example.com", 1, applicationId: 11, documentRequests: selected);
            await tx.CommitAsync();
        }
        await f.Repository.SubmitAsync(second, "second@example.com", new(), f.Files(field), "", "", default);
        static AuthUser Viewer(int client, string scope = "Client", params int[] locations) => new()
        { Id = 20, IsActive = true, EmployeeId = 222, ClientAccessMode = "Selected", AssignedClientIds = [client], RecruitmentScopeMode = scope,
            RecruitmentLocationIds = [.. locations], Permissions = ["attachment.recruitment.view"] };
        var firstDocs = await f.Attachments.GetAttachmentsAsync("CANDIDATE", 1, Viewer(55));
        var firstDoc = Assert.Single(firstDocs); Assert.Equal(55, firstDoc.ClientId);
        var secondDoc = Assert.Single(await f.Attachments.GetAttachmentsAsync("CANDIDATE", 1, Viewer(70))); Assert.Equal(70, secondDoc.ClientId);
        Assert.NotNull((await f.Attachments.GetForContentAsync(firstDoc.PublicId, Viewer(55), "PREVIEW", "", "")).Attachment);
        Assert.Null((await f.Attachments.GetForContentAsync(firstDoc.PublicId, Viewer(70), "PREVIEW", "", "")).Attachment);
        Assert.Null((await f.Attachments.GetForContentAsync(secondDoc.PublicId, Viewer(55), "PREVIEW", "", "")).Attachment);
        Assert.Empty(await f.Attachments.GetAttachmentsAsync("CANDIDATE", 1, Viewer(55, "SelectedLocations", 99)));
        Assert.Null((await f.Attachments.GetForContentAsync(firstDoc.PublicId, Viewer(55, "SelectedLocations", 99), "PREVIEW", "", "")).Attachment);
        Assert.Single(await f.Attachments.GetAttachmentsAsync("CANDIDATE", 1, Viewer(55, "SelectedLocations", 9)));
        Assert.Null((await f.Attachments.GetForContentAsync(firstDoc.PublicId, new AuthUser { Id = 0, IsActive = true }, "PREVIEW", "", "")).Attachment);
    }

    private sealed class Fixture(MySqlConnection owner, MySqlConnection db, string name, string folder, RecruitmentDocumentCollectionRepository repository, AttachmentRepository attachments) : IAsyncDisposable
    {
        public MySqlConnection Db => db;
        public RecruitmentDocumentCollectionRepository Repository => repository;
        public AttachmentRepository Attachments => attachments;
        public string UploadRoot => Path.Combine(folder, "uploads");
        public AuthUser User { get; } = new() { Id = 1, IsActive = true, ClientAccessMode = "Selected", AssignedClientIds = [55], Permissions = ["recruitment.offer.manage"] };
        internal static async Task<Fixture> CreateAsync(bool seedLegacyJoiningDocuments = false)
        {
            var parsed = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FREVO_DOCUMENT_COLLECTION_TEST_DB")!);
            Assert.Equal("127.0.0.1", parsed.Server); Assert.Equal(33332u, parsed.Port); Assert.Equal("collection_fixture", parsed.Database);
            parsed.Database = ""; var owner = new MySqlConnection(parsed.ConnectionString); await owner.OpenAsync();
            var name = "collection_test_" + Guid.NewGuid().ToString("N");
            await owner.ExecuteAsync($"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");
            parsed.Database = name; var db = new MySqlConnection(parsed.ConnectionString); await db.OpenAsync();
            var folder = Path.Combine(Path.GetTempPath(), "frevo-document-collection-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Default"] = parsed.ConnectionString, ["UPLOAD_DIR"] = Path.Combine(folder, "uploads") }).Build();
            var host = new Host { ContentRootPath = folder };
            var protector = new EphemeralDataProtectionProvider();
            var credentials = new PortableIntegrationCredentialProtector(config, protector, NullLogger<PortableIntegrationCredentialProtector>.Instance);
            var drive = new GoogleDriveOAuthService(null!, config, host, protector, NullLogger<GoogleDriveOAuthService>.Instance);
            var storage = new AttachmentStorageService(null!, host, config, drive);
            var attachments = new AttachmentRepository(config, host, credentials, storage, drive, NullLogger<AttachmentRepository>.Instance);
            await attachments.InitializeAsync();
            await db.ExecuteAsync(@"
CREATE TABLE clients(Id INT PRIMARY KEY,Name VARCHAR(180),Code VARCHAR(80)); INSERT INTO clients VALUES(55,'ICMR','ICMR');
CREATE TABLE recruitment_candidates(Id BIGINT PRIMARY KEY,ClientId INT NOT NULL,EmployeeId INT NULL); INSERT INTO recruitment_candidates VALUES(1,55,NULL);
CREATE TABLE recruitment_candidate_applications(Id BIGINT PRIMARY KEY,CandidateId BIGINT,ClientId INT,PositionId BIGINT);
CREATE TABLE recruitment_open_positions(Id BIGINT PRIMARY KEY,ClientId INT,BranchId INT);
CREATE TABLE worklocations(Id INT PRIMARY KEY,ClientId INT,Name VARCHAR(80),IsActive BOOLEAN);
CREATE TABLE authusers(Id INT PRIMARY KEY,DisplayName VARCHAR(80),Email VARCHAR(100));
CREATE TABLE recruitment_employee_referrals(CandidateId BIGINT,ReferrerEmployeeId INT);
CREATE TABLE person_activity_events(Id BIGINT AUTO_INCREMENT PRIMARY KEY,ClientId INT,CandidateId BIGINT,EmployeeId INT,ModuleCode VARCHAR(80),EventType VARCHAR(100),EventTitle VARCHAR(200),EventSummary VARCHAR(1000),ResourceType VARCHAR(80),ResourceId VARCHAR(100),ActorUserId INT NULL,Visibility VARCHAR(40),IsSensitive BOOLEAN,MetadataJson JSON,OccurredAt DATETIME);
CREATE TABLE recruitment_audit(Id BIGINT AUTO_INCREMENT PRIMARY KEY,EntityType VARCHAR(100),EntityId BIGINT,Action VARCHAR(100),NewValueJson JSON,ChangedByUserId INT);
INSERT INTO attachment_attributes(id,client_id,attribute_code,attribute_name,data_classification) VALUES(900,0,'ADHAAR_DOC','Adhaar Card','Confidential');");
            if (seedLegacyJoiningDocuments)
                await db.ExecuteAsync(@"
INSERT INTO clients VALUES(20,'Synthetic UIDAI','UIDAI'),(70,'Second client','SECOND');
CREATE TABLE form_definitions(Id BIGINT PRIMARY KEY,ClientId INT,ModuleCode VARCHAR(80),FormCode VARCHAR(120),FormName VARCHAR(180),PurposeCode VARCHAR(100),Status VARCHAR(30),CurrentPublishedVersionId BIGINT);
CREATE TABLE form_versions(Id BIGINT PRIMARY KEY,FormDefinitionId BIGINT,Status VARCHAR(30));
CREATE TABLE form_field_types(Id INT PRIMARY KEY,TypeCode VARCHAR(40));
CREATE TABLE form_fields(Id BIGINT PRIMARY KEY,FormVersionId BIGINT,FieldTypeId INT,AttachmentFieldConfigurationId BIGINT,IsActive BOOLEAN,StableFieldCode VARCHAR(120));
INSERT INTO form_definitions VALUES(10,20,'RECRUITMENT','UIDAI_JOINING_DOCUMENTS','Synthetic joining documents','DOCUMENT_REQUEST','Active',13);
INSERT INTO form_versions VALUES(13,10,'Published');
INSERT INTO form_field_types VALUES(1,'UPLOAD');
INSERT INTO attachment_attributes(id,client_id,attribute_code,attribute_name,data_classification) VALUES
(901,20,'UIDAI_JOIN_PAN','PAN Card','Confidential'),
(902,20,'UIDAI_JOIN_ACADEMIC','Academic certificates (Class 10 onwards)','Confidential'),
(903,20,'UIDAI_JOIN_ADDRESS','Address / ID Proof (Passport / Voter ID / Driving License)','Confidential');
INSERT INTO attachment_field_configurations(id,client_id,attachment_attribute_id,module_code,form_code,field_key,field_label,allowed_extensions_json,allowed_mime_types_json,maximum_file_size_bytes,allow_multiple,maximum_file_count) VALUES
(8700,0,900,'EMPLOYEE','EMPLOYEE_CREATE_EDIT','ADHAAR_DOC','Adhaar Card','[""pdf""]','[""application/pdf""]',1048576,FALSE,1),
(8701,20,901,'RECRUITMENT','UIDAI_JOINING_DOCUMENTS','UIDAI_JOIN_PAN','PAN Card','[""pdf""]','[""application/pdf""]',2097152,FALSE,1),
(8702,20,902,'RECRUITMENT','UIDAI_JOINING_DOCUMENTS','UIDAI_JOIN_ACADEMIC','Academic certificates (Class 10 onwards)','[""pdf""]','[""application/pdf""]',3145728,TRUE,3),
(8703,20,903,'RECRUITMENT','UIDAI_JOINING_DOCUMENTS','UIDAI_JOIN_ADDRESS','Address / ID Proof (Passport / Voter ID / Driving License)','[""pdf""]','[""application/pdf""]',4194304,FALSE,1);
INSERT INTO form_fields VALUES(96,13,1,8701,TRUE,'UIDAI_JOIN_PAN'),(97,13,1,8702,TRUE,'UIDAI_JOIN_ACADEMIC'),(98,13,1,8703,TRUE,'UIDAI_JOIN_ADDRESS');
INSERT INTO entity_attachments(public_id,client_id,attachment_attribute_id,field_configuration_id,storage_server_id,entity_type,entity_id,original_file_name,stored_file_name,storage_key,file_extension,detected_mime_type,file_size_bytes,sha256_hash,uploaded_by_user_id)
SELECT '00000000-0000-0000-0000-000000000001',20,901,8701,id,'EMPLOYEE',42,'proof.pdf','proof.pdf','unchanged/private-proof.pdf','pdf','application/pdf',10,REPEAT('a',64),1 FROM attachment_storage_servers WHERE server_code='API_LOCAL';");
            var repository = new RecruitmentDocumentCollectionRepository(config, attachments, protector); await repository.InitializeAsync();
            return new(owner, db, name, folder, repository, attachments);
        }
        public async Task<string> LinkAsync(IReadOnlyList<RecruitmentRequestedDocument> selected, string recipient = "recipient@example.com", long? applicationId = null)
        {
            await using var tx = await db.BeginTransactionAsync();
            var token = await repository.EnsureLinkAsync(db, tx, 55, 1, recipient, 1, applicationId: applicationId, documentRequests: selected);
            await tx.CommitAsync(); return token;
        }
        public FormFileCollection Files(long id) => [File(id)];
        public IFormFile File(long id)
        {
            var bytes = Encoding.ASCII.GetBytes("%PDF-1.4\nsynthetic joining document\n%%EOF");
            return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files-" + id, "joining-document.pdf") { Headers = new HeaderDictionary(), ContentType = "application/pdf" };
        }
        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync(); await owner.ExecuteAsync($"DROP DATABASE `{name}`"); await owner.DisposeAsync();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    private sealed class FailingWriteFile(string field) : IFormFile
    {
        private int opens;
        private readonly byte[] bytes = Encoding.ASCII.GetBytes("%PDF-1.4\nfailure fixture\n%%EOF");
        public string ContentType => "application/pdf";
        public string ContentDisposition => "";
        public IHeaderDictionary Headers { get; } = new HeaderDictionary();
        public long Length => bytes.Length;
        public string Name => field;
        public string FileName => "failure.pdf";
        public Stream OpenReadStream() => ++opens > 2 ? throw new IOException("Synthetic storage read failure") : new MemoryStream(bytes);
        public void CopyTo(Stream target) { using var source = OpenReadStream(); source.CopyTo(target); }
        public async Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) { await using var source = OpenReadStream(); await source.CopyToAsync(target, cancellationToken); }
    }

    private sealed class Host : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "DocumentCollectionTests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
