using System.Text.Json.Nodes;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed partial class SalaryTemplateCreateTests
{
    [LocalWorkflowScopeDatabaseFact]
    public async Task Salary_template_unlink_preserves_salary_and_draft_rules_and_allows_separate_final_delete()
    {
        await using var f = await Fixture.CreateAsync();
        await PrepareDeletionFixture(f);
        var templateId = (await f.Repository.CreateClientSalaryTemplateAsync(20, Request()))["id"]!.GetValue<long>();
        var actor = new AuthUser { Id = 8, Email = "fixture@example.test", IsActive = true, ClientId = 20,
            Permissions = ["client.settings.manage", "employees.manage", "tax.statutory.manage"] };
        await f.Db.ExecuteAsync("""
            ALTER TABLE employees ADD AnnualCtc DECIMAL(18,2),ADD SalaryJson JSON;
            ALTER TABLE employee_it0008_basic_pay ADD Id BIGINT PRIMARY KEY AUTO_INCREMENT,ADD EffectiveTo DATE,
              ADD ActionType VARCHAR(60),ADD ChangeReason VARCHAR(500),ADD AnnualCtc DECIMAL(18,2),ADD SalaryJson JSON;
            ALTER TABLE payruns ADD Status VARCHAR(30),ADD PayPeriod VARCHAR(7);
            ALTER TABLE payrunemployees ADD EmployeeId INT;
            CREATE TABLE employee_audit_trail(EmployeeId INT,EmployeeCode VARCHAR(80),ActionType VARCHAR(60),InfotypeCode VARCHAR(20),
              FieldName VARCHAR(120),OldValue TEXT,NewValue TEXT,EffectiveFrom DATE,ChangedBy VARCHAR(190));
            INSERT INTO employees VALUES(42,20,'T42','Sample','Employee',@key,120000,'{"basic":10000}');
            INSERT INTO employee_it0008_basic_pay(EmployeeId,ClientId,EffectiveFrom,Status,SalaryStructureId,ActionType,ChangeReason,AnnualCtc,SalaryJson)
              VALUES(42,20,'2026-10-01','Active',@key,'Hire','Ordinary onboarding',120000,'{"basic":10000}');
            INSERT INTO modulesettings VALUES(20,'payroll',TRUE,JSON_OBJECT('keep','untouched','pfPolicyVersions',JSON_ARRAY(
              JSON_OBJECT('id','draft-a','status','Draft','name','Draft A','salaryStructureId',@key,'unknownSavedField',77),
              JSON_OBJECT('id','pub-b','status','Published','name','Published B','salaryStructureId','different'))));
            """, new { key = templateId.ToString() });
        async Task<SalaryTemplateMappingPage> Page(int offset = 0, int limit = 25) =>
            (await f.Repository.GetEditableSalaryTemplateMappingsAsync(20, templateId, offset, limit, actor))!;
        Task<bool> Apply(EditableSalaryTemplateMapping m) =>
            f.Repository.UnlinkSalaryTemplateMappingAsync(20, templateId, new(m.Kind, m.Key, m.Version), actor);
        Assert.Equal(3, (await Page(1, 1)).Total);
        Assert.Single((await Page(1, 1)).Items);
        var links = (await Page()).Items.ToDictionary(m => m.Kind);
        await f.Db.ExecuteAsync("UPDATE employees SET AnnualCtc=132000 WHERE Id=42");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(links["employee"]));
        actor.Permissions.Remove("employees.manage");
        Assert.DoesNotContain((await Page()).Items, m => m.Kind is "employee" or "basic-pay");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Apply(links["basic-pay"]));
        actor.Permissions.Add("employees.manage");
        // A saved period relying on this dated record removes its unlink action even after a preview.
        await f.Db.ExecuteAsync("INSERT INTO payruns VALUES(99,20,'Approved','2026-10'); INSERT INTO payrunemployees(Id,ClientId,PayRunId,EmployeeId) VALUES(99,20,99,42)");
        Assert.DoesNotContain((await Page()).Items, m => m.Kind == "basic-pay");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(links["basic-pay"]));
        await f.Db.ExecuteAsync("DELETE FROM payrunemployees WHERE Id=99; DELETE FROM payruns WHERE Id=99");
        foreach (var m in (await Page()).Items) Assert.True(await Apply(m));
        Assert.Empty((await Page()).Items);
        Assert.Equal(132000m, await f.Db.ExecuteScalarAsync<decimal>("SELECT AnnualCtc FROM employees WHERE Id=42 AND SalaryStructureId='' AND SalaryJson->>'$.basic'='10000'"));
        Assert.Equal(120000m, await f.Db.ExecuteScalarAsync<decimal>("SELECT AnnualCtc FROM employee_it0008_basic_pay WHERE EmployeeId=42 AND SalaryStructureId='' AND SalaryJson->>'$.basic'='10000'"));
        Assert.Equal(2, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_audit_trail WHERE EmployeeId=42 AND ChangedBy=@Email", actor));
        var payroll = JsonNode.Parse(await f.Db.ExecuteScalarAsync<string>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=20 AND ModuleCode='payroll'"))!;
        Assert.Equal("", payroll["pfPolicyVersions"]![0]!["salaryStructureId"]!.ToString());
        Assert.Equal("77", payroll["pfPolicyVersions"]![0]!["unknownSavedField"]!.ToString());
        Assert.Equal("different", payroll["pfPolicyVersions"]![1]!["salaryStructureId"]!.ToString());
        Assert.Equal("untouched", payroll["keep"]!.ToString());
        var ready = (await f.ReviewDeletionAsync(20, templateId)).Preview!;
        Assert.Empty(ready.Mappings);
        Assert.True((await f.ReviewDeletionAsync(20, templateId, ready.Version)).Deleted);
    }
}
