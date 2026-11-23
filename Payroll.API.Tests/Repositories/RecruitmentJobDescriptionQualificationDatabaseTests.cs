using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class RecruitmentJobDescriptionQualificationDatabaseTests
{
    private sealed class QualificationDatabaseFactAttribute : FactAttribute
    {
        public QualificationDatabaseFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("FREVO_JD_QUALIFICATION_TEST_DB") is null)
                Skip = "An explicit task-owned localhost qualification test database is required.";
        }
    }

    [QualificationDatabaseFact]
    public async Task Existing_legacy_column_expands_idempotently_and_real_JD_save_keeps_the_entire_qualification()
    {
        await using var f = await Fixture.CreateAsync();
        const string comment = "Institute's eligibility \\ source wording";
        await f.Db.ExecuteAsync("ALTER TABLE recruitment_jd_qualification_requirements MODIFY QualificationName VARCHAR(180) NOT NULL COMMENT @Comment", new { Comment = comment });
        await f.Db.ExecuteAsync("INSERT INTO recruitment_jd_qualification_requirements(JobDescriptionVersionId,QualificationName,Specialization,IsMandatory,DisplayOrder) VALUES(999,'Existing qualification','Existing specialization',TRUE,1)");
        await RecruitmentPipelineRepository.EnsureJobDescriptionQualificationCapacityAsync(f.Db);
        await RecruitmentPipelineRepository.EnsureJobDescriptionQualificationCapacityAsync(f.Db);
        Assert.Equal("text", await f.Db.ExecuteScalarAsync<string>("SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='recruitment_jd_qualification_requirements' AND COLUMN_NAME='QualificationName'"));
        Assert.Equal("Existing qualification", await f.Db.ExecuteScalarAsync<string>("SELECT QualificationName FROM recruitment_jd_qualification_requirements WHERE JobDescriptionVersionId=999"));

        var qualification = new string('q', 230);
        var request = Request(qualification);
        var (saved, error) = await f.Repository.SaveJobDescriptionVersionAsync(request, f.User);
        Assert.Equal("", error); Assert.NotNull(saved);
        Assert.Equal(qualification, Assert.Single(saved.Qualifications).QualificationName);
        Assert.Equal(qualification, await f.Db.ExecuteScalarAsync<string>("SELECT QualificationName FROM recruitment_jd_qualification_requirements WHERE JobDescriptionVersionId=@Id", new { saved.Id }));
        Assert.Equal("utf8mb4_0900_ai_ci", await f.Db.ExecuteScalarAsync<string>("SELECT COLLATION_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='recruitment_jd_qualification_requirements' AND COLUMN_NAME='QualificationName'"));
        Assert.Equal(comment, await f.Db.ExecuteScalarAsync<string>("SELECT COLUMN_COMMENT FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='recruitment_jd_qualification_requirements' AND COLUMN_NAME='QualificationName'"));
    }

    [QualificationDatabaseFact]
    public async Task Save_defensively_upgrades_a_legacy_column_and_invalid_unicode_size_does_not_replace_an_existing_draft()
    {
        await using var f = await Fixture.CreateAsync();
        var original = "Original full eligibility wording: " + new string('x', 230);
        var (saved, error) = await f.Repository.SaveJobDescriptionVersionAsync(Request(original), f.User);
        Assert.Equal("", error); Assert.NotNull(saved);
        var invalid = Request(string.Concat(Enumerable.Repeat("अ", 21846)));
        invalid.Id = saved.Id;
        invalid.Title = "A changed title that must not be committed";
        var (rejected, failure) = await f.Repository.SaveJobDescriptionVersionAsync(invalid, f.User);
        Assert.Null(rejected); Assert.Contains("too long for one entry", failure);
        Assert.Equal(original, await f.Db.ExecuteScalarAsync<string>("SELECT QualificationName FROM recruitment_jd_qualification_requirements WHERE JobDescriptionVersionId=@Id", new { saved.Id }));
        Assert.Equal("Synthetic research role", await f.Db.ExecuteScalarAsync<string>("SELECT Title FROM recruitment_job_description_versions WHERE Id=@Id", new { saved.Id }));
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_job_description_versions"));
    }

    [QualificationDatabaseFact]
    public async Task Wider_existing_column_and_existing_text_are_not_shrunk_or_rewritten()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.ExecuteAsync("ALTER TABLE recruitment_jd_qualification_requirements MODIFY QualificationName MEDIUMTEXT NOT NULL; INSERT INTO recruitment_jd_qualification_requirements(JobDescriptionVersionId,QualificationName,Specialization,IsMandatory,DisplayOrder) VALUES(999,REPEAT('q',70000),'',TRUE,1)");
        await RecruitmentPipelineRepository.EnsureJobDescriptionQualificationCapacityAsync(f.Db);
        Assert.Equal("mediumtext", await f.Db.ExecuteScalarAsync<string>("SELECT DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='recruitment_jd_qualification_requirements' AND COLUMN_NAME='QualificationName'"));
        Assert.Equal(70000, await f.Db.ExecuteScalarAsync<int>("SELECT CHAR_LENGTH(QualificationName) FROM recruitment_jd_qualification_requirements WHERE JobDescriptionVersionId=999"));
    }

    private static SaveRecruitmentJobDescriptionVersion Request(string qualification) => new()
    {
        RequisitionId = 123, Title = "Synthetic research role", Summary = "Synthetic role summary", RolePurpose = "Synthetic research",
        Responsibilities = [new() { ResponsibilityText = "Conduct assigned research" }],
        Skills = [new() { SkillName = "Research", IsRequired = true, WeightPercent = 100 }],
        Qualifications = [new() { QualificationName = qualification, Specialization = "Life sciences", IsMandatory = true }]
    };

    private sealed class Fixture(MySqlConnection root, MySqlConnection db, string database, RecruitmentPipelineRepository repository) : IAsyncDisposable
    {
        public MySqlConnection Db => db;
        public RecruitmentPipelineRepository Repository => repository;
        public AuthUser User { get; } = new() { Id = 1, IsActive = true, ClientId = 55, RecruitmentScopeMode = "Client", Permissions = ["recruitment.requisition.create"] };

        public static async Task<Fixture> CreateAsync()
        {
            var connection = Environment.GetEnvironmentVariable("FREVO_JD_QUALIFICATION_TEST_DB")!;
            var settings = new MySqlConnectionStringBuilder(connection);
            if (settings.Server != "127.0.0.1" || settings.Port != 33332 || settings.Database != "jd_qualification_fixture")
                throw new InvalidOperationException("This test requires the explicitly named task-owned localhost database.");
            var database = "jd_qualification_" + Guid.NewGuid().ToString("N");
            settings.Database = "";
            var root = new MySqlConnection(settings.ConnectionString); await root.OpenAsync();
            await root.ExecuteAsync($"CREATE DATABASE `{database}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci");
            settings.Database = database;
            var db = new MySqlConnection(settings.ConnectionString); await db.OpenAsync();
            await db.ExecuteAsync("""
CREATE TABLE recruitment_requisitions(Id BIGINT PRIMARY KEY,ClientId INT,JobLocation VARCHAR(160));
INSERT INTO recruitment_requisitions VALUES(123,55,'Synthetic location');
CREATE TABLE recruitment_job_description_versions(Id BIGINT PRIMARY KEY AUTO_INCREMENT,RequisitionId BIGINT NOT NULL,ClientId INT NOT NULL,VersionNumber INT NOT NULL,Title VARCHAR(180) NOT NULL,Summary TEXT NOT NULL,RolePurpose TEXT NOT NULL,Status VARCHAR(40) NOT NULL DEFAULT 'Draft',WorkflowInstanceId BIGINT NULL,CreatedByUserId INT NOT NULL,ApprovedByUserId INT NULL,ApprovedAtUtc DATETIME NULL,CreatedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,UpdatedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,UNIQUE KEY UX_JdVersion(RequisitionId,VersionNumber));
CREATE TABLE recruitment_jd_responsibilities(Id BIGINT PRIMARY KEY AUTO_INCREMENT,JobDescriptionVersionId BIGINT,ResponsibilityText TEXT,DisplayOrder INT);
CREATE TABLE recruitment_jd_skill_requirements(Id BIGINT PRIMARY KEY AUTO_INCREMENT,JobDescriptionVersionId BIGINT,SkillId BIGINT NULL,SkillName VARCHAR(180),IsRequired BOOLEAN,MinimumYears DECIMAL(5,2),MinimumProficiency VARCHAR(80),WeightPercent DECIMAL(5,2),DisplayOrder INT);
CREATE TABLE recruitment_jd_qualification_requirements(Id BIGINT PRIMARY KEY AUTO_INCREMENT,JobDescriptionVersionId BIGINT,QualificationName VARCHAR(180) NOT NULL,Specialization VARCHAR(180) NOT NULL DEFAULT '',IsMandatory BOOLEAN,DisplayOrder INT,INDEX IX_JdQualification(JobDescriptionVersionId,DisplayOrder));
CREATE TABLE recruitment_jd_certification_requirements(Id BIGINT PRIMARY KEY AUTO_INCREMENT,JobDescriptionVersionId BIGINT,CertificationName TEXT,IsMandatory BOOLEAN,CandidateProofAttachmentFieldConfigurationId BIGINT NULL,DisplayOrder INT);
CREATE TABLE recruitment_jd_language_requirements(Id BIGINT PRIMARY KEY AUTO_INCREMENT,JobDescriptionVersionId BIGINT,LanguageName VARCHAR(120),Proficiency VARCHAR(80),IsMandatory BOOLEAN,DisplayOrder INT);
CREATE TABLE recruitment_jd_benefits(Id BIGINT PRIMARY KEY AUTO_INCREMENT,JobDescriptionVersionId BIGINT,BenefitName VARCHAR(180),Description TEXT,DisplayOrder INT);
""");
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = settings.ConnectionString }).Build();
            return new(root, db, database, new(config, null!));
        }

        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            if (!System.Text.RegularExpressions.Regex.IsMatch(database, "^jd_qualification_[a-f0-9]{32}$")) throw new InvalidOperationException("Unexpected test database.");
            await root.ExecuteAsync($"DROP DATABASE `{database}`"); await root.DisposeAsync();
        }
    }
}
