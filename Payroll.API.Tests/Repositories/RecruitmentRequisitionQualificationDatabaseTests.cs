using System.Reflection;
using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class RecruitmentRequisitionQualificationDatabaseTests
{
    private sealed class QualificationDatabaseFactAttribute : FactAttribute
    {
        public QualificationDatabaseFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("FREVO_RFR_QUALIFICATION_TEST_DB") is null)
                Skip = "An explicit task-owned localhost qualification migration database is required.";
        }
    }

    [QualificationDatabaseFact]
    public async Task Legacy_upgrade_preserves_every_existing_value_column_policy_indexes_and_empty_default()
    {
        await using var f = await Fixture.CreateAsync();
        const string comment = "Institute's eligibility \\ original requirements";
        await f.Db.ExecuteAsync("CREATE TABLE recruitment_requisitions(Id BIGINT PRIMARY KEY,Qualification VARCHAR(250) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL DEFAULT '' COMMENT @Comment,Status VARCHAR(40),INDEX IX_status(Status))", new { Comment = comment });
        await f.Db.ExecuteAsync("INSERT INTO recruitment_requisitions VALUES(1,@Text,'Draft'),(2,'','Approved')", new { Text = "Bachelor’s degree · अनुभव आवश्यक 😀" });
        var before = (await f.Db.QueryAsync<(long Id, string Value, string Status)>("SELECT Id,HEX(Qualification) Value,Status FROM recruitment_requisitions ORDER BY Id")).ToArray();
        var indexes = await f.Db.QueryAsync<string>("SELECT CONCAT(INDEX_NAME,':',COLUMN_NAME,':',SEQ_IN_INDEX) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='recruitment_requisitions' ORDER BY INDEX_NAME,SEQ_IN_INDEX");

        await RecruitmentRepository.EnsureRequisitionQualificationCapacityAsync(f.Db);
        await RecruitmentRepository.EnsureRequisitionQualificationCapacityAsync(f.Db);

        Assert.Equal("text", await f.TypeAsync());
        Assert.Equal(before, (await f.Db.QueryAsync<(long Id, string Value, string Status)>("SELECT Id,HEX(Qualification) Value,Status FROM recruitment_requisitions ORDER BY Id")).ToArray());
        Assert.Equal(indexes, await f.Db.QueryAsync<string>("SELECT CONCAT(INDEX_NAME,':',COLUMN_NAME,':',SEQ_IN_INDEX) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='recruitment_requisitions' ORDER BY INDEX_NAME,SEQ_IN_INDEX"));
        Assert.Equal("utf8mb4_unicode_ci", await f.MetadataAsync("COLLATION_NAME"));
        Assert.Equal("NO", await f.MetadataAsync("IS_NULLABLE"));
        Assert.Equal(comment, await f.MetadataAsync("COLUMN_COMMENT"));
        await f.Db.ExecuteAsync("INSERT INTO recruitment_requisitions(Id,Status) VALUES(3,'Draft')");
        Assert.Equal("", await f.Db.ExecuteScalarAsync<string>("SELECT Qualification FROM recruitment_requisitions WHERE Id=3"));
    }

    [QualificationDatabaseFact]
    public async Task Nullable_and_nonempty_literal_defaults_are_preserved_without_sql_interpolation()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.ExecuteAsync("CREATE TABLE recruitment_requisitions(Id BIGINT PRIMARY KEY,Qualification VARCHAR(250) NULL DEFAULT NULL)");
        await f.Db.ExecuteAsync("INSERT INTO recruitment_requisitions VALUES(1,NULL)");
        await RecruitmentRepository.EnsureRequisitionQualificationCapacityAsync(f.Db);
        Assert.Equal("YES", await f.MetadataAsync("IS_NULLABLE"));
        Assert.Null(await f.Db.ExecuteScalarAsync<string?>("SELECT Qualification FROM recruitment_requisitions WHERE Id=1"));
        const string defaultValue = "Bachelor's degree \\ required";
        await f.Db.ExecuteAsync("DELETE FROM recruitment_requisitions;ALTER TABLE recruitment_requisitions MODIFY Qualification VARCHAR(250) NOT NULL DEFAULT @Default", new { Default = defaultValue });
        await RecruitmentRepository.EnsureRequisitionQualificationCapacityAsync(f.Db);
        await f.Db.ExecuteAsync("INSERT INTO recruitment_requisitions(Id) VALUES(2)");
        Assert.Equal(defaultValue, await f.Db.ExecuteScalarAsync<string>("SELECT Qualification FROM recruitment_requisitions WHERE Id=2"));
    }

    [QualificationDatabaseFact]
    public async Task Wider_text_is_not_shrunk_and_unexpected_indexed_legacy_columns_are_left_unchanged()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.ExecuteAsync("CREATE TABLE recruitment_requisitions(Id BIGINT PRIMARY KEY,Qualification MEDIUMTEXT NOT NULL);INSERT INTO recruitment_requisitions VALUES(1,REPEAT('q',70000))");
        await RecruitmentRepository.EnsureRequisitionQualificationCapacityAsync(f.Db);
        Assert.Equal("mediumtext", await f.TypeAsync());
        Assert.Equal(70000, await f.Db.ExecuteScalarAsync<int>("SELECT LENGTH(Qualification) FROM recruitment_requisitions WHERE Id=1"));
        await f.Db.ExecuteAsync("DELETE FROM recruitment_requisitions;ALTER TABLE recruitment_requisitions MODIFY Qualification VARCHAR(250) NOT NULL DEFAULT '';CREATE INDEX IX_custom_qualification ON recruitment_requisitions(Qualification);INSERT INTO recruitment_requisitions VALUES(2,'Existing custom indexed requirement')");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RecruitmentRepository.EnsureRequisitionQualificationCapacityAsync(f.Db));
        Assert.Contains("custom index", error.Message);
        Assert.Equal("varchar", await f.TypeAsync());
        Assert.Equal("Existing custom indexed requirement", await f.Db.ExecuteScalarAsync<string>("SELECT Qualification FROM recruitment_requisitions WHERE Id=2"));
    }

    [QualificationDatabaseFact]
    public async Task Real_requisition_save_upgrades_legacy_capacity_and_derived_JD_keeps_the_complete_requirement()
    {
        await using var f = await Fixture.CreateAsync();
        await f.PrepareRealSaveAsync();
        Assert.Equal("text", await f.TypeAsync()); // Fresh schema uses the same safe capacity.
        await f.Db.ExecuteAsync("ALTER TABLE recruitment_requisitions MODIFY Qualification VARCHAR(250) NOT NULL DEFAULT ''");
        var eligibility = "A postgraduate degree in life sciences plus documented field research experience " + new string('q', 3000) + " अंतिम योग्यता";
        var request = new SaveRecruitmentRequisition
        {
            ClientId = 55, RequesterUserId = 1, PositionTitle = "Synthetic research role", Department = "Research", NumberOfOpenings = 1,
            HiringType = "", EmploymentType = "", PositionCategory = "", ExperienceRange = "", Qualification = eligibility,
            RequiredSkills = "Research", BusinessJustification = "Conduct approved research", ReasonForHiring = "Support field research"
        };
        var repository = new RecruitmentRepository(f.Config);
        var (saved, error) = await repository.SaveDraftAsync(request, f.User);
        Assert.Equal("", error); Assert.NotNull(saved);
        Assert.Equal("text", await f.TypeAsync());
        Assert.Equal(eligibility, saved.Qualification);
        Assert.Equal(eligibility, await f.Db.ExecuteScalarAsync<string>("SELECT Qualification FROM recruitment_requisitions WHERE Id=@Id", new { saved.Id }));
        var derived = RecruitmentPipelineRepository.BuildJobDescriptionFromRequisition(saved);
        Assert.Equal(eligibility, Assert.Single(derived.Qualifications).QualificationName);
        var pipeline = new RecruitmentPipelineRepository(f.Config, null!);
        var (jd, jdError) = await pipeline.SaveJobDescriptionVersionAsync(derived, f.User);
        Assert.Equal("", jdError); Assert.NotNull(jd);
        Assert.Equal(eligibility, Assert.Single(jd.Qualifications).QualificationName);
        Assert.Equal(eligibility, await f.Db.ExecuteScalarAsync<string>("SELECT QualificationName FROM recruitment_jd_qualification_requirements WHERE JobDescriptionVersionId=@Id", new { jd.Id }));
        Assert.Equal("Draft", saved.Status); Assert.Equal("Draft", jd.Status);
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_open_positions"));
    }

    private sealed class Fixture(MySqlConnection owner, MySqlConnection db, string database, IConfiguration config) : IAsyncDisposable
    {
        public MySqlConnection Db => db;
        public IConfiguration Config => config;
        public AuthUser User { get; } = new() { Id = 1, IsActive = true, ClientId = 55, RecruitmentScopeMode = "Client", Permissions = ["settings.manage"] };
        public Task<string> TypeAsync() => MetadataAsync("DATA_TYPE");
        public Task<string> MetadataAsync(string column)
        {
            if (!new[] { "DATA_TYPE", "COLLATION_NAME", "IS_NULLABLE", "COLUMN_COMMENT" }.Contains(column)) throw new InvalidOperationException();
            return db.ExecuteScalarAsync<string>($"SELECT {column} FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='recruitment_requisitions' AND COLUMN_NAME='Qualification'")!;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var settings = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FREVO_RFR_QUALIFICATION_TEST_DB")!);
            if (settings.Server != "127.0.0.1" || settings.Port != 33319 || settings.Database != "roster_mail_test")
                throw new InvalidOperationException("This migration test requires the explicitly named task-owned localhost fixture.");
            settings.Database = "";
            var owner = new MySqlConnection(settings.ConnectionString); await owner.OpenAsync();
            var database = "rfr_qualification_" + Guid.NewGuid().ToString("N");
            await owner.ExecuteAsync($"CREATE DATABASE `{database}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci");
            settings.Database = database;
            var db = new MySqlConnection(settings.ConnectionString); await db.OpenAsync();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = settings.ConnectionString }).Build();
            return new(owner, db, database, config);
        }

        public async Task PrepareRealSaveAsync()
        {
            var ensure = typeof(RecruitmentRepository).GetMethod("EnsureTablesAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            await (Task)ensure.Invoke(null, [db])!;
            await db.ExecuteAsync("""
ALTER TABLE recruitment_requisitions ADD WorkOrderId BIGINT NULL,ADD WorkOrderLineNumber INT NULL;
CREATE TABLE clients(Id INT PRIMARY KEY,Name VARCHAR(100),Code VARCHAR(40),IsActive BOOLEAN);INSERT INTO clients VALUES(55,'Synthetic client','SYN',TRUE);
CREATE TABLE authusers(Id INT PRIMARY KEY,ClientId INT,Email VARCHAR(180),DisplayName VARCHAR(100),IsActive BOOLEAN,ClientAccessMode VARCHAR(40));INSERT INTO authusers VALUES(1,55,'synthetic@example.invalid','Synthetic hiring user',TRUE,'Legacy');
CREATE TABLE authroles(Id INT,ClientId INT);INSERT INTO authroles VALUES(1,55);
CREATE TABLE authuserroles(UserId INT,RoleId INT);INSERT INTO authuserroles VALUES(1,1);
CREATE TABLE authrolepermissions(RoleId INT,PermissionId INT);INSERT INTO authrolepermissions VALUES(1,1);
CREATE TABLE authpermissions(Id INT,Code VARCHAR(100));INSERT INTO authpermissions VALUES(1,'recruitment.rfr.create');
CREATE TABLE authuser_clients(UserId INT,ClientId INT);
CREATE TABLE employees(Id INT,ClientId INT,FirstName VARCHAR(100),LastName VARCHAR(100),Department VARCHAR(100));
CREATE TABLE worklocations(Id INT,ClientId INT,Name VARCHAR(100),IsActive BOOLEAN);
CREATE TABLE recruitment_work_orders(Id BIGINT,ClientId INT,Status VARCHAR(40),ReceivedAtUtc DATETIME);
CREATE TABLE recruitment_work_order_lines(Id BIGINT,WorkOrderId BIGINT,RequisitionId BIGINT,Status VARCHAR(40));
CREATE TABLE workflowtasks(Id BIGINT,StageId BIGINT,InstanceId BIGINT,Status VARCHAR(40),ApproverUserId INT);
CREATE TABLE workflowstages(Id BIGINT,Name VARCHAR(100));
CREATE TABLE recruitment_position_pipeline_instances(Id BIGINT,RequisitionId BIGINT,PipelineVersionId BIGINT,CurrentStageInstanceId BIGINT,Status VARCHAR(40));
CREATE TABLE recruitment_position_stage_instances(Id BIGINT,PipelineStageId BIGINT);
CREATE TABLE recruitment_pipeline_definitions(Id BIGINT,ClientId INT);
CREATE TABLE recruitment_pipeline_versions(Id BIGINT,PipelineDefinitionId BIGINT,Status VARCHAR(40),ScopeType VARCHAR(40));
CREATE TABLE recruitment_pipeline_stages(Id BIGINT,PipelineVersionId BIGINT,CardScope VARCHAR(40),IsInitial BOOLEAN,IsActive BOOLEAN,StageName VARCHAR(100),StageType VARCHAR(40));
CREATE TABLE recruitment_job_description_versions(Id BIGINT PRIMARY KEY AUTO_INCREMENT,RequisitionId BIGINT NOT NULL,ClientId INT NOT NULL,VersionNumber INT NOT NULL,Title VARCHAR(180) NOT NULL,Summary TEXT NOT NULL,RolePurpose TEXT NOT NULL,Status VARCHAR(40) NOT NULL DEFAULT 'Draft',WorkflowInstanceId BIGINT NULL,CreatedByUserId INT NOT NULL,ApprovedByUserId INT NULL,ApprovedAtUtc DATETIME NULL,CreatedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UpdatedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,UNIQUE KEY UX_JdVersion(RequisitionId,VersionNumber));
CREATE TABLE recruitment_jd_responsibilities(Id BIGINT PRIMARY KEY AUTO_INCREMENT,JobDescriptionVersionId BIGINT,ResponsibilityText TEXT,DisplayOrder INT);
CREATE TABLE recruitment_jd_skill_requirements(Id BIGINT PRIMARY KEY AUTO_INCREMENT,JobDescriptionVersionId BIGINT,SkillId BIGINT NULL,SkillName VARCHAR(180),IsRequired BOOLEAN,MinimumYears DECIMAL(5,2),MinimumProficiency VARCHAR(80),WeightPercent DECIMAL(5,2),DisplayOrder INT);
CREATE TABLE recruitment_jd_qualification_requirements(Id BIGINT PRIMARY KEY AUTO_INCREMENT,JobDescriptionVersionId BIGINT,QualificationName VARCHAR(180) NOT NULL,Specialization VARCHAR(180) NOT NULL DEFAULT '',IsMandatory BOOLEAN,DisplayOrder INT);
CREATE TABLE recruitment_jd_certification_requirements(Id BIGINT PRIMARY KEY AUTO_INCREMENT,JobDescriptionVersionId BIGINT,CertificationName TEXT,IsMandatory BOOLEAN,CandidateProofAttachmentFieldConfigurationId BIGINT NULL,DisplayOrder INT);
CREATE TABLE recruitment_jd_language_requirements(Id BIGINT PRIMARY KEY AUTO_INCREMENT,JobDescriptionVersionId BIGINT,LanguageName VARCHAR(120),Proficiency VARCHAR(80),IsMandatory BOOLEAN,DisplayOrder INT);
CREATE TABLE recruitment_jd_benefits(Id BIGINT PRIMARY KEY AUTO_INCREMENT,JobDescriptionVersionId BIGINT,BenefitName VARCHAR(180),Description TEXT,DisplayOrder INT);
""");
        }

        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            if (!System.Text.RegularExpressions.Regex.IsMatch(database, "^rfr_qualification_[a-f0-9]{32}$")) throw new InvalidOperationException("Unexpected fixture database.");
            await owner.ExecuteAsync($"DROP DATABASE `{database}`"); await owner.DisposeAsync();
        }
    }
}
