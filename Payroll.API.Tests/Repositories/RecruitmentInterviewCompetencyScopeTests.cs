using Dapper;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class RecruitmentInterviewCompetencyScopeTests
{
    [LocalInterviewDatabaseFact]
    public async Task SelectedClientScoreEditorUsesItsAuthorizedBodyClientWithoutRedirectingOrTransferringRows()
    {
        await using var f = await RecruitmentPipelineCloneTests.Database.CreateAsync();
        await f.Db.ExecuteAsync("""
ALTER TABLE recruitment_interview_competency_definitions MODIFY COLUMN Id BIGINT NOT NULL AUTO_INCREMENT,ADD PRIMARY KEY(Id),
ADD COLUMN CompetencyCode VARCHAR(80) NOT NULL,ADD COLUMN Description VARCHAR(1000) NOT NULL DEFAULT '',ADD UNIQUE KEY UX_scope_competency(ClientId,CompetencyCode);
""");
        var repository = new RecruitmentPipelineRepository(f.Config, new WorkflowRepository(f.Config));
        // The shell remembers client 30; the editor explicitly targets assigned client 20.
        var selected = new AuthUser { Id = 2, IsActive = true, ClientId = 30, ClientAccessMode = "Selected", AssignedClientIds = [20, 30], Permissions = ["recruitment.configuration.manage"] };
        var created = await repository.SaveInterviewCompetencyAsync(new() { ClientId = 20, CompetencyCode = "BODY_SCORE", CompetencyName = "Fixture score" }, selected);
        Assert.Empty(created.Error); Assert.NotNull(created.Row); Assert.Equal(20, created.Row.ClientId);
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_interview_competency_definitions WHERE ClientId=30"));
        var updated = await repository.SaveInterviewCompetencyAsync(new() { Id = created.Row.Id, ClientId = 20, CompetencyCode = "BODY_SCORE", CompetencyName = "Reviewed fixture score" }, selected);
        Assert.Empty(updated.Error); Assert.NotNull(updated.Row); Assert.Equal(created.Row.Id, updated.Row.Id); Assert.Equal(20, updated.Row.ClientId);
        var transferred = await repository.SaveInterviewCompetencyAsync(new() { Id = created.Row.Id, ClientId = 30, CompetencyCode = "WRONG_CLIENT", CompetencyName = "Must not transfer" }, selected);
        Assert.Null(transferred.Row); Assert.Contains("not found", transferred.Error);
        Assert.Equal("Reviewed fixture score", await f.Db.ExecuteScalarAsync<string>("SELECT CompetencyName FROM recruitment_interview_competency_definitions WHERE Id=@Id AND ClientId=20", new { created.Row.Id }));
        var deniedSelected = new AuthUser { Id = 3, IsActive = true, ClientId = 30, ClientAccessMode = "Selected", AssignedClientIds = [30], Permissions = ["recruitment.configuration.manage"] };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.SaveInterviewCompetencyAsync(new() { ClientId = 20, CompetencyCode = "DENIED", CompetencyName = "Must not save" }, deniedSelected));
        var single = new AuthUser { Id = 4, IsActive = true, ClientId = 30, Permissions = ["recruitment.configuration.manage"] };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repository.SaveInterviewCompetencyAsync(new() { ClientId = 20, CompetencyCode = "DENIED", CompetencyName = "Must not save" }, single));
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_interview_competency_definitions"));
    }
}
