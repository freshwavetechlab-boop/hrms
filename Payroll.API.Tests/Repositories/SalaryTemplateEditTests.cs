using System.Data;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Tests.Repositories;

public sealed partial class SalaryTemplateCreateTests
{
    [LocalWorkflowScopeDatabaseFact]
    public async Task Salary_template_edit_saves_one_formula_while_unrelated_setup_rows_are_locked()
    {
        await using var f = await Fixture.CreateAsync();
        var original = await f.Repository.CreateClientSalaryTemplateAsync(20, Request());
        var id = original["id"]!.GetValue<long>();
        await f.Db.ExecuteAsync("""
            UPDATE salarystructurelines SET SortOrder=(SortOrder+1)*10 WHERE StructureId=@id;
            INSERT INTO salarystructures(Id, ClientId, ClientRef, Name) VALUES (99, 20, '20', 'Other template for this client');
            INSERT INTO salarystructurelines(StructureId, ComponentId, ValueText) VALUES (99, '1', '5000');
            """, new { id });
        var originalOrder = (await f.Db.QueryAsync<(string Code, int SortOrder)>(
            "SELECT ComponentId, SortOrder FROM salarystructurelines WHERE StructureId=@id ORDER BY SortOrder", new { id })).ToArray();
        var otherRecords = await f.SnapshotAsync(id);
        var edit = new SalaryTemplateUpdateRequest { Expected = Request(), Template = Request() };
        edit.Template.Lines[1].Formula = "MIN(BASIC_EARNED,15000)*12%";

        var saved = await SaveWithUnrelatedRowsLocked(f, id, edit);
        Assert.NotNull(saved);
        Assert.Equal(id, saved["id"]!.GetValue<long>());
        var wanted = original.DeepClone().AsObject();
        wanted["lines"]![1]!["formula"] = edit.Template.Lines[1].Formula;
        Assert.True(JsonNode.DeepEquals(wanted, saved));
        Assert.Equal(edit.Template.Lines[1].Formula, await f.Db.ExecuteScalarAsync<string>(
            "SELECT Formula FROM salarystructurelines WHERE StructureId=@id AND ComponentId='4'", new { id }));
        Assert.Equal(originalOrder, await f.Db.QueryAsync<(string Code, int SortOrder)>(
            "SELECT ComponentId, SortOrder FROM salarystructurelines WHERE StructureId=@id ORDER BY SortOrder", new { id }));
        Assert.Equal(otherRecords, await f.SnapshotAsync(id));
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Salary_template_edit_adds_removes_and_reorders_only_target_lines_under_contention()
    {
        await using var f = await Fixture.CreateAsync();
        var id = (await f.Repository.CreateClientSalaryTemplateAsync(20, Request()))["id"]!.GetValue<long>();
        await f.Db.ExecuteAsync("UPDATE salarystructurelines SET SortOrder=(SortOrder+1)*10 WHERE StructureId=@id", new { id });
        var otherRecords = await f.SnapshotAsync(id);
        var edit = new SalaryTemplateUpdateRequest { Expected = Request(), Template = Request() };
        edit.Template.Lines.RemoveAt(3);
        edit.Template.Lines.Reverse();
        edit.Template.Lines.Add(new() { ComponentId = "2", CalculationType = "Daily Rate", Value = "700" });
        var originalIds = (await f.Db.QueryAsync<(string Code, long Id)>(
            "SELECT ComponentId, Id FROM salarystructurelines WHERE StructureId=@id", new { id })).ToDictionary(line => line.Code, line => line.Id);
        Assert.NotNull(await SaveWithUnrelatedRowsLocked(f, id, edit));
        var stored = (await f.Db.QueryAsync<(string Code, long Id)>(
            "SELECT ComponentId, Id FROM salarystructurelines WHERE StructureId=@id ORDER BY SortOrder, Id", new { id })).ToArray();
        Assert.Equal(edit.Template.Lines.Select(line => line.ComponentId), stored.Select(row => row.Code));
        Assert.All(stored.Where(row => originalIds.ContainsKey(row.Code)), row => Assert.Equal(originalIds[row.Code], row.Id));
        Assert.Equal(otherRecords, await f.SnapshotAsync(id));
    }

    private static async Task<JsonObject?> SaveWithUnrelatedRowsLocked(Fixture f, long id, SalaryTemplateUpdateRequest edit)
    {
        await using var blocker = await f.Db.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await f.Db.QueryAsync("SELECT Id FROM payrollsetups FOR UPDATE", transaction: blocker);
        await f.Db.QueryAsync("SELECT Id FROM salarystructures WHERE Id IN (31,99) FOR UPDATE", transaction: blocker);
        var otherLineIds = await f.Db.QueryAsync<long>(
            "SELECT Id FROM salarystructurelines WHERE StructureId IN (31,99)", transaction: blocker);
        await f.Db.QueryAsync("SELECT Id FROM salarystructurelines FORCE INDEX (PRIMARY) WHERE Id IN @otherLineIds FOR UPDATE",
            new { otherLineIds }, blocker);
        await f.Db.QueryAsync("SELECT Id FROM paysliptemplates FOR UPDATE", transaction: blocker);
        var saving = f.Repository.UpdateClientSalaryTemplateAsync(20, id, edit);
        var completed = await Task.WhenAny(saving, Task.Delay(TimeSpan.FromSeconds(10)));
        await blocker.RollbackAsync();
        var saved = await saving;
        Assert.True(completed == saving, "Editing the target template waited for an unrelated row lock.");
        return saved;
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Salary_template_edit_preserves_newer_edits_and_allows_a_retry_after_a_lost_response()
    {
        await using var f = await Fixture.CreateAsync();
        var id = (await f.Repository.CreateClientSalaryTemplateAsync(20, Request()))["id"]!.GetValue<long>();
        var edit = new SalaryTemplateUpdateRequest { Expected = Request(), Template = Request() };
        edit.Template.Lines[0].Value = "22000";
        var saved = await f.Repository.UpdateClientSalaryTemplateAsync(20, id, edit);
        Assert.NotNull(saved);
        var afterSave = await f.SnapshotAsync();
        var retry = await f.Repository.UpdateClientSalaryTemplateAsync(20, id, edit);
        Assert.True(JsonNode.DeepEquals(saved, retry));
        edit.Template.Lines[0].Value = "24000";
        await Assert.ThrowsAsync<DBConcurrencyException>(() => f.Repository.UpdateClientSalaryTemplateAsync(20, id, edit));
        Assert.Equal(afterSave, await f.SnapshotAsync());
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Salary_template_edit_rejects_one_of_two_simultaneous_edits_to_the_same_version()
    {
        await using var f = await Fixture.CreateAsync();
        var id = (await f.Repository.CreateClientSalaryTemplateAsync(20, Request()))["id"]!.GetValue<long>();
        async Task<bool> SaveValue(string value)
        {
            var edit = new SalaryTemplateUpdateRequest { Expected = Request(), Template = Request() };
            edit.Template.Lines[0].Value = value;
            try { return await f.Repository.UpdateClientSalaryTemplateAsync(20, id, edit) is not null; }
            catch (DBConcurrencyException) { return false; }
        }
        var saved = await Task.WhenAll(SaveValue("21000"), SaveValue("24000"));
        Assert.Single(saved, succeeded => succeeded);
        Assert.Equal(saved[0] ? "21000" : "24000", await f.Db.ExecuteScalarAsync<string>(
            "SELECT ValueText FROM salarystructurelines WHERE StructureId=@id AND ComponentId='1'", new { id }));
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Salary_template_edit_rolls_back_its_header_and_lines_if_writing_a_new_line_fails()
    {
        await using var f = await Fixture.CreateAsync();
        var id = (await f.Repository.CreateClientSalaryTemplateAsync(20, Request()))["id"]!.GetValue<long>();
        var edit = new SalaryTemplateUpdateRequest { Expected = Request(), Template = Request() };
        edit.Template.Name = "Updated name";
        edit.Template.Lines[0].Value = "21000";
        edit.Template.Lines.Add(new() { ComponentId = "2", CalculationType = "Daily Rate", Value = "700" });
        await f.Db.ExecuteAsync("""
            CREATE TRIGGER reject_added_line BEFORE INSERT ON salarystructurelines FOR EACH ROW
            BEGIN IF NEW.ComponentId='2' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected line failure'; END IF; END
            """);
        var before = await f.SnapshotAsync();
        var failure = await Assert.ThrowsAsync<MySqlException>(() => f.Repository.UpdateClientSalaryTemplateAsync(20, id, edit));
        Assert.Contains("Injected line failure", failure.Message);
        Assert.Equal(before, await f.SnapshotAsync());
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Salary_template_edit_enforces_ownership_validates_edits_and_preserves_managed_templates()
    {
        await using var f = await Fixture.CreateAsync();
        var id = (await f.Repository.CreateClientSalaryTemplateAsync(20, Request()))["id"]!.GetValue<long>();
        var edit = new SalaryTemplateUpdateRequest { Expected = Request(), Template = Request() };
        edit.Template.Name = "Edited monthly template";
        var before = await f.SnapshotAsync();
        Assert.Null(await f.Repository.UpdateClientSalaryTemplateAsync(10, id, edit));
        Assert.Null(await f.Repository.UpdateClientSalaryTemplateAsync(20, 987654, edit));
        edit.Template.Lines.Add(edit.Template.Lines[0]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Repository.UpdateClientSalaryTemplateAsync(20, id, edit));
        Assert.Equal(before, await f.SnapshotAsync());
        edit.Template.Lines.RemoveAt(edit.Template.Lines.Count - 1);

        await f.Db.ExecuteAsync("UPDATE salarystructures SET ClientRef='20,10' WHERE Id=@id", new { id });
        before = await f.SnapshotAsync();
        Assert.Null(await f.Repository.UpdateClientSalaryTemplateAsync(20, id, edit));
        Assert.Equal(before, await f.SnapshotAsync());

        await f.Db.ExecuteAsync("""
            UPDATE salarystructures SET ClientRef='20' WHERE Id=@id;
            INSERT INTO modulesettings VALUES (20,CONCAT('named-input-t:',@id),TRUE,JSON_OBJECT('fixture','protected'));
            """, new { id });
        before = await f.SnapshotAsync();
        await Assert.ThrowsAsync<DBConcurrencyException>(() => f.Repository.UpdateClientSalaryTemplateAsync(20, id, edit));
        Assert.Equal(before, await f.SnapshotAsync());
    }
}
