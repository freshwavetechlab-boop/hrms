using System.Text.Json;
using System.Diagnostics;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Repositories;

public sealed class DocumentLayoutMigrationDatabaseTests
{
    private sealed class LocalFactAttribute : FactAttribute
    {
        public LocalFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("FREVO_DOCUMENT_LAYOUT_TEST_DB") is null)
                Skip = "Explicit task-owned localhost migration database required.";
        }
    }

    [LocalFact]
    public async Task Any_client_templates_are_discovered_and_only_never_issued_profiles_move()
    {
        await using var f = await Fixture.CreateAsync();
        var preserved = await f.PreservedAsync();
        var values = await f.ValueStateAsync();
        var result = await RecruitmentDocumentRepository.MigrateDocumentLayoutsAsync(f.Db);
        Assert.Equal(2, result.CreatedTemplates); Assert.Equal(2, result.ReboundRows);
        var targets = (await f.Db.QueryAsync<(long Id, int TemplateId)>("SELECT Id,TemplateId FROM recruitment_document_roster WHERE Id IN(801,901) ORDER BY Id")).ToArray();
        Assert.NotEqual(73, targets[0].TemplateId); Assert.NotEqual(92, targets[1].TemplateId);
        Assert.Equal(17, await f.Db.ExecuteScalarAsync<int>("SELECT ClientId FROM recruitment_templates WHERE Id=@Id", new { Id = targets[0].TemplateId }));
        Assert.Equal(208, await f.Db.ExecuteScalarAsync<int>("SELECT ClientId FROM recruitment_templates WHERE Id=@Id", new { Id = targets[1].TemplateId }));
        Assert.Equal(preserved, await f.PreservedAsync()); Assert.Equal(values, await f.ValueStateAsync());
        Assert.Equal(2, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_admin_audit"));
        Assert.Equal(2, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_document_roster_audit"));
        foreach (var id in targets.Select(target => target.TemplateId))
        {
            var clone = await f.Db.QuerySingleAsync<RecruitmentTemplate>("SELECT * FROM recruitment_templates WHERE Id=@Id", new { Id = id });
            Assert.StartsWith("DOC_LAYOUT_FLOW_V2_", clone.TemplateCode);
            Assert.DoesNotContain("Alpha", clone.TemplateName); Assert.DoesNotContain("Beta", clone.TemplateName);
            var layout = LayoutDocumentPdfService.Parse(clone.BodyTemplate);
            Assert.True(layout.Sections[1].Flow); Assert.Equal(1, layout.Sections[1].Pages);
            Assert.Contains("{{institutionShortName}}", layout.Sections[1].Blocks[2].Text);
            Assert.Contains("engagement is purely contractual", layout.Sections[1].Blocks[2].Text);
            Assert.Equal(10.5, layout.Sections[1].Blocks[2].FontSize);
            Assert.Contains(layout.Sections[1].Blocks, block => block.Kind == "signature");
            var original = LayoutDocumentPdfService.Parse(f.Body);
            static string Words(IEnumerable<string> fragments) => System.Text.RegularExpressions.Regex.Replace(string.Join(" ", fragments), @"\s+", " ").Trim();
            Assert.Equal(Words(original.Sections[1].Elements.Select(element => element.Text)), Words(layout.Sections[1].Blocks.Select(block => block.Text)));
            layout.Sections[1] = original.Sections[1];
            Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(layout));
        }
    }

    [LocalFact]
    public async Task Repeated_runs_reuse_versions_and_later_unissued_rows_without_duplicate_audits()
    {
        await using var f = await Fixture.CreateAsync();
        Assert.Equal(0, await f.RunCliAsync("--migrate-recruitment-document-layouts"));
        var unchanged = await f.AllAsync();
        Assert.Equal(0, await f.RunCliAsync("--migrate-recruitment-document-layouts"));
        Assert.Equal(unchanged, await f.AllAsync());
        Assert.Equal("text", await f.Db.ExecuteScalarAsync<string>("SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='recruitment_requisitions' AND COLUMN_NAME='Qualification'"));
        Assert.Equal("text", await f.Db.ExecuteScalarAsync<string>("SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='recruitment_jd_qualification_requirements' AND COLUMN_NAME='QualificationName'"));
        var schema = await f.Db.ExecuteScalarAsync<string>("SELECT GROUP_CONCAT(TABLE_NAME ORDER BY TABLE_NAME) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE()");
        Assert.NotEqual(0, await f.RunCliAsync("--migrate-unsupported-synthetic-option"));
        Assert.Equal(unchanged, await f.AllAsync());
        Assert.Equal(schema, await f.Db.ExecuteScalarAsync<string>("SELECT GROUP_CONCAT(TABLE_NAME ORDER BY TABLE_NAME) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE()"));
        await f.AddRowAsync(807, 17, 1001, 73);
        var next = await RecruitmentDocumentRepository.MigrateDocumentLayoutsAsync(f.Db);
        Assert.Equal(0, next.CreatedTemplates); Assert.Equal(1, next.ReboundRows);
        Assert.Equal(await f.Db.ExecuteScalarAsync<int>("SELECT TemplateId FROM recruitment_document_roster WHERE Id=801"),
            await f.Db.ExecuteScalarAsync<int>("SELECT TemplateId FROM recruitment_document_roster WHERE Id=807"));
        Assert.Equal(2, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_admin_audit"));
        Assert.Equal(3, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_document_roster_audit"));
    }

    [LocalFact]
    public async Task Global_template_retains_global_ownership_and_never_crosses_candidate_client_boundaries()
    {
        await using var f = await Fixture.CreateAsync();
        await f.AddTemplateAsync(146, 0, f.Body);
        await f.AddRowAsync(1201, 17, 1001, 146);
        await f.AddRowAsync(1202, 208, 2001, 146);
        await f.AddRowAsync(1203, 17, 2001, 146); // invalid cross-client candidate reference
        await f.AddRowAsync(1204, 17, 1001, 146, receipt: true);
        var invalidAndIssued = JsonSerializer.Serialize(await f.Db.QueryAsync("SELECT * FROM recruitment_document_roster WHERE Id IN(1203,1204) ORDER BY Id"));
        var result = await RecruitmentDocumentRepository.MigrateDocumentLayoutsAsync(f.Db);
        Assert.Equal(3, result.CreatedTemplates); Assert.Equal(4, result.ReboundRows);
        var first = await f.Db.ExecuteScalarAsync<int>("SELECT TemplateId FROM recruitment_document_roster WHERE Id=1201");
        Assert.Equal(first, await f.Db.ExecuteScalarAsync<int>("SELECT TemplateId FROM recruitment_document_roster WHERE Id=1202"));
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT ClientId FROM recruitment_templates WHERE Id=@Id", new { Id = first }));
        Assert.Equal(invalidAndIssued, JsonSerializer.Serialize(await f.Db.QueryAsync("SELECT * FROM recruitment_document_roster WHERE Id IN(1203,1204) ORDER BY Id")));
    }

    [LocalFact]
    public async Task Custom_incompatible_layouts_and_stale_row_hashes_are_not_guessed_or_overwritten()
    {
        await using var f = await Fixture.CreateAsync();
        var custom = LayoutDocumentPdfService.Parse(f.Body);
        custom.Sections[1].Elements[4].Runs = [new() { Text = "Custom rich text" }];
        var body = LayoutDocumentPdfService.Marker + JsonSerializer.Serialize(custom, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await f.AddTemplateAsync(147, 17, body); await f.AddRowAsync(1301, 17, 1001, 147);
        await f.Db.ExecuteAsync("UPDATE recruitment_document_roster SET TemplateHash='custom-stale-hash' WHERE Id=801");
        var stale = JsonSerializer.Serialize(await f.Db.QueryAsync("SELECT * FROM recruitment_document_roster WHERE Id IN(801,1301) ORDER BY Id"));
        var result = await RecruitmentDocumentRepository.MigrateDocumentLayoutsAsync(f.Db);
        Assert.Equal(2, result.CreatedTemplates); Assert.Equal(1, result.ReboundRows); Assert.True(result.SkippedTemplates > 0);
        Assert.Equal(stale, JsonSerializer.Serialize(await f.Db.QueryAsync("SELECT * FROM recruitment_document_roster WHERE Id IN(801,1301) ORDER BY Id")));
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_templates WHERE TemplateCode LIKE 'DOC_LAYOUT_FLOW_V2_147_%'"));
    }

    [LocalFact]
    public async Task Audit_failure_rolls_back_all_clones_and_all_client_profile_bindings()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.ExecuteAsync("ALTER TABLE recruitment_document_roster_audit ADD CONSTRAINT reject_synthetic_audit CHECK (ActorUserId<>0)");
        var before = await f.AllAsync();
        await Assert.ThrowsAsync<MySqlException>(() => RecruitmentDocumentRepository.MigrateDocumentLayoutsAsync(f.Db));
        Assert.Equal(before, await f.AllAsync());
    }

    private sealed class Fixture(MySqlConnection root, MySqlConnection db, string database, string body) : IAsyncDisposable
    {
        public MySqlConnection Db => db;
        public string Body => body;
        public async Task<int> RunCliAsync(string option)
        {
            var api = Environment.GetEnvironmentVariable("FREVO_DOCUMENT_LAYOUT_API_DLL") ?? typeof(RecruitmentDocumentRepository).Assembly.Location;
            Assert.True(File.Exists(api) && File.Exists(Path.ChangeExtension(api, ".runtimeconfig.json")), "Explicit built API DLL/runtimeconfig required for isolated CLI verification.");
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.GetDirectoryName(api)!, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add(api); start.ArgumentList.Add(option);
            var cliConnection = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FREVO_DOCUMENT_LAYOUT_TEST_DB")!) { Database = database };
            start.Environment["ConnectionStrings__Default"] = cliConnection.ConnectionString;
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            start.Environment["DOTNET_ENVIRONMENT"] = "Production";
            start.Environment["Database__AutoMigrate"] = "false";
            start.Environment["BackgroundWorkers__Enabled"] = "false";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw new InvalidOperationException("The migration command did not exit before starting a persistent host."); }
            var logs = await output + await error;
            Assert.DoesNotContain("Now listening on:", logs);
            if (option == "--migrate-recruitment-document-layouts") Assert.True(process.ExitCode == 0, logs);
            else Assert.Contains("Unknown migration option", logs);
            return process.ExitCode;
        }
        public async Task<string> PreservedAsync() => JsonSerializer.Serialize(new
        {
            original = await db.QueryAsync("SELECT * FROM recruitment_templates WHERE Id IN(73,92) ORDER BY Id"),
            rows = await db.QueryAsync("SELECT * FROM recruitment_document_roster WHERE Id NOT IN(801,901) ORDER BY Id"),
            releases = await db.QueryAsync("SELECT * FROM recruitment_document_roster_releases ORDER BY Id")
        });
        public async Task<string> ValueStateAsync() => JsonSerializer.Serialize(await db.QueryAsync("SELECT Id,CandidateId,ClientId,WorkLocationId,OfferNumber,CAST(ValuesJson AS CHAR) ValuesJson,SourceGroup,SourceFileHash FROM recruitment_document_roster ORDER BY Id"));
        public async Task<string> AllAsync() => JsonSerializer.Serialize(new
        {
            templates = await db.QueryAsync("SELECT * FROM recruitment_templates ORDER BY Id"),
            rows = await db.QueryAsync("SELECT * FROM recruitment_document_roster ORDER BY Id"),
            releases = await db.QueryAsync("SELECT * FROM recruitment_document_roster_releases ORDER BY Id"),
            templateAudit = await db.QueryAsync("SELECT * FROM recruitment_admin_audit ORDER BY Id"),
            rowAudit = await db.QueryAsync("SELECT * FROM recruitment_document_roster_audit ORDER BY Id")
        });
        public Task AddTemplateAsync(int id, int client, string templateBody) => db.ExecuteAsync("INSERT INTO recruitment_templates(Id,ClientId,TemplateCode,TemplateName,TemplateType,SubjectTemplate,BodyTemplate,IsHtml,IsActive) VALUES(@Id,@Client,@Code,@Name,'Offer Letter','Synthetic subject',@Body,FALSE,TRUE)", new { Id = id, Client = client, Code = "SYNTHETIC_" + id, Name = client == 17 ? "Alpha configured offer" : "Beta configured offer", Body = templateBody });
        public async Task AddRowAsync(long id, int client, long candidate, int template, bool receipt = false, string offerNumber = "")
        {
            var hash = await db.ExecuteScalarAsync<string>("SELECT SHA2(BodyTemplate,256) FROM recruitment_templates WHERE Id=@Id", new { Id = template });
            await db.ExecuteAsync("INSERT INTO recruitment_document_roster(Id,ClientId,CandidateId,TemplateId,TemplateHash,WorkLocationId,OfferNumber,ValuesJson,SourceGroup,SourceFileHash) VALUES(@Id,@Client,@Candidate,@Template,@Hash,456,@Offer,@Json,@Batch,'source')", new { Id = id, Client = client, Candidate = candidate, Template = template, Hash = hash, Offer = offerNumber, Json = "{\"monthlyBasic\":\"45678\",\"candidateAddress\":\"Current data remains\"}", Batch = "Batch " + client });
            if (receipt) await db.ExecuteAsync("INSERT INTO recruitment_document_roster_releases(RosterId,ClientId,PdfHash,PdfBytes,TemplateHash) VALUES(@Id,@Client,'previous',X'010203040506',@Hash)", new { Id = id, Client = client, Hash = hash });
        }
        public static async Task<Fixture> CreateAsync()
        {
            var settings = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FREVO_DOCUMENT_LAYOUT_TEST_DB")!);
            if (settings.Server != "127.0.0.1" || settings.Port != 33319 || settings.Database != "document_layout_test")
                throw new InvalidOperationException("Only the task-owned localhost migration database is permitted.");
            settings.Database = "";
            var root = new MySqlConnection(settings.ConnectionString); await root.OpenAsync();
            var database = "document_layout_" + Guid.NewGuid().ToString("N");
            await root.ExecuteAsync($"CREATE DATABASE `{database}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci");
            settings.Database = database;
            var db = new MySqlConnection(settings.ConnectionString); await db.OpenAsync();
            await db.ExecuteAsync("""
CREATE TABLE clients(Id INT PRIMARY KEY,Code VARCHAR(80),IsActive BOOL);
INSERT INTO clients VALUES(17,'ALPHA',TRUE),(208,'BETA',TRUE),(209,'DORMANT',FALSE);
CREATE TABLE recruitment_templates(Id INT PRIMARY KEY AUTO_INCREMENT,ClientId INT,TemplateCode VARCHAR(80),TemplateName VARCHAR(180),TemplateType VARCHAR(80),SubjectTemplate VARCHAR(300),BodyTemplate TEXT,IsHtml BOOL,IsActive BOOL,UNIQUE KEY UX_Code(ClientId,TemplateCode));
CREATE TABLE recruitment_candidates(Id BIGINT PRIMARY KEY,ClientId INT,CandidateCode VARCHAR(80),ProfileStatus VARCHAR(40));
INSERT INTO recruitment_candidates VALUES(1001,17,'SYN-A-001','Active'),(1002,17,'SYN-A-002','Active'),(1003,17,'SYN-A-003','Archived'),(2001,208,'SYN-B-001','Active'),(2002,208,'SYN-B-002','Active');
CREATE TABLE recruitment_document_roster(Id BIGINT PRIMARY KEY,ClientId INT,CandidateId BIGINT,TemplateId INT,TemplateHash CHAR(64),WorkLocationId INT,OfferNumber VARCHAR(160),ValuesJson JSON,SourceGroup VARCHAR(180),SourceFileHash CHAR(64),UpdatedAt DATETIME(6) DEFAULT CURRENT_TIMESTAMP(6));
CREATE TABLE recruitment_document_roster_releases(Id BIGINT PRIMARY KEY AUTO_INCREMENT,RosterId BIGINT,ClientId INT,PdfHash VARCHAR(64),PdfBytes BLOB,TemplateHash CHAR(64));
CREATE TABLE recruitment_admin_audit(Id BIGINT PRIMARY KEY AUTO_INCREMENT,EntityType VARCHAR(80),EntityId INT,Action VARCHAR(80),OldValueJson JSON,NewValueJson JSON,ChangedByUserId INT NULL);
CREATE TABLE recruitment_document_roster_audit(Id BIGINT PRIMARY KEY AUTO_INCREMENT,RosterId BIGINT,ClientId INT,Action VARCHAR(40),OldValuesJson JSON,NewValuesJson JSON,ActorUserId INT);
CREATE TABLE recruitment_requisitions(Id BIGINT PRIMARY KEY,Qualification VARCHAR(250) NOT NULL DEFAULT '');
CREATE TABLE recruitment_jd_qualification_requirements(Id BIGINT PRIMARY KEY,QualificationName VARCHAR(180) NOT NULL);
""");
            var texts = new[] { "ENDORSEMENT", "Acceptance wording", "I understand {{institutionShortName}}, {{institutionLocation}} and that my", "engagement is purely contractual", "Certificate first", "Certificate second", "Certificate third", "Company", "[Signature & Stamp of Signatory]", "Signatory", "HR", "Company", "Accepted and Agreed", "Signature and Date", "{{candidateName}}" };
            double[] positions = [114.476,131.676,148.876,162.076,179.276,192.476,205.676,234.976,252.176,292.476,304.576,317.776,347.076,375.276,388.476];
            var layout = new RecruitmentDocumentLayout { Sections = [new(), new() { Elements = texts.Select((text, i) => new DocumentElement { X = i == 0 ? 261.5 : 65.1, Y = positions[i], Width = i == 0 ? 269.3 : 465.7, Height = 14.7, Text = text, FontSize = 10.5, LineHeight = 12.6, MinFontSize = 9, Bold = i is 0 or 14 }).ToList() }] };
            var body = LayoutDocumentPdfService.Marker + JsonSerializer.Serialize(layout, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var fixture = new Fixture(root, db, database, body);
            await fixture.AddTemplateAsync(73, 17, body); await fixture.AddTemplateAsync(92, 208, body);
            await fixture.AddRowAsync(801,17,1001,73); await fixture.AddRowAsync(802,17,1002,73,receipt:true);
            await fixture.AddRowAsync(803,17,1001,73,offerNumber:"historical-number");
            await fixture.AddRowAsync(804,17,1003,73); await fixture.AddRowAsync(805,208,2001,73);
            await fixture.AddRowAsync(901,208,2001,92); await fixture.AddRowAsync(902,208,2002,92,receipt:true);
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            if (!System.Text.RegularExpressions.Regex.IsMatch(database, "^document_layout_[a-f0-9]{32}$")) throw new InvalidOperationException("Unexpected test database.");
            await root.ExecuteAsync($"DROP DATABASE `{database}`"); await root.DisposeAsync();
        }
    }
}
