using System.Text.Json.Nodes;
using Dapper;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed partial class SalaryTemplateCreateTests
{
    [LocalWorkflowScopeDatabaseFact]
    public async Task Unlinking_excel_template_mappings_preserves_other_headers_clients_drafts_and_applied_receipts()
    {
        await using var f = await Fixture.CreateAsync();
        await PrepareDeletionFixture(f);
        await f.Db.ExecuteAsync("""
            ALTER TABLE excel_payroll_setup_imports ADD request_hash CHAR(64) DEFAULT '', ADD plan_hash VARCHAR(64) DEFAULT '';
            INSERT INTO modulesettings VALUES
                (10,'excel_payslip_profile',1,'{"match":{"salaryTemplateId":"31","columns":[{"sourceHeader":"Basic","kind":"earning"}]}}'),
                (10,'excel_payslip_profile:10',1,'{"match":{"salaryTemplateId":"31","columns":[{"sourceHeader":"Basic","kind":"earning"}]},"other":{"salaryTemplateId":"32"}}'),
                (20,'excel_payslip_profile:20',1,'{"match":{"salaryTemplateId":"31"}}');
            INSERT INTO excel_payroll_setup_imports (id,client_id,status,request_json,result_json) VALUES
                ('draft-form',10,'Draft','{"batchId":"B1","rowIds":["a"],"templateDecisions":[{"existingTemplateId":"31"},{"existingTemplateId":"32"}],"locationAllocations":{"reviewed":true,"groups":[{"parts":[{"templateId":"31","workLocationId":5,"componentScopes":{"BASIC":"Monthly"}},{"templateId":"32","workLocationId":9}]}]}}',
                    '{"editableDefaultsRevision":2,"transfer":{"templates":[{"templateId":"31"}]},"previewCanApply":true}'),
                ('archived',10,'Applied','{"templateDecisions":[{"existingTemplateId":"31"}]}','{"templateId":"31"}');
            """);
        await using var tx = await f.Db.BeginTransactionAsync();
        var mappings = await SettingsRepository.ReadEditableExcelMappingsAsync(f.Db, tx, 10, 31);
        Assert.Equal(2, mappings.Count);
        var header = Assert.Single(mappings, row => row.Kind == "excel-header");
        var draft = Assert.Single(mappings, row => row.Kind == "excel-draft");
        Assert.Equal("draft-form", draft.Key);
        await SettingsRepository.UnlinkExcelMappingAsync(f.Db, tx, 10, 31, header);
        await SettingsRepository.UnlinkExcelMappingAsync(f.Db, tx, 10, 31, draft);
        Assert.Empty(await SettingsRepository.ReadEditableExcelMappingsAsync(f.Db, tx, 10, 31));
        await tx.CommitAsync();
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=10 AND JSON_CONTAINS_PATH(SettingsJson,'one','$.match.salaryTemplateId')"));
        Assert.Equal("31", await f.Db.ExecuteScalarAsync<string>("SELECT JSON_UNQUOTE(JSON_EXTRACT(SettingsJson,'$.match.salaryTemplateId')) FROM modulesettings WHERE client_id=20 AND ModuleCode='excel_payslip_profile:20'"));
        var retained = JsonNode.Parse(await f.Db.ExecuteScalarAsync<string>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=10 AND ModuleCode='excel_payslip_profile:10'"))!;
        Assert.Equal("Basic", retained["match"]!["columns"]![0]!["sourceHeader"]!.GetValue<string>());
        Assert.Equal("32", retained["other"]!["salaryTemplateId"]!.GetValue<string>());
        var stored = await f.Db.QuerySingleAsync<(string Request, string Result, string Hash)>("SELECT CAST(request_json AS CHAR),CAST(result_json AS CHAR),request_hash FROM excel_payroll_setup_imports WHERE id='draft-form'");
        var request = JsonNode.Parse(stored.Request)!;
        var result = JsonNode.Parse(stored.Result)!;
        Assert.Equal(64, stored.Hash.Length);
        Assert.Equal("32", Assert.Single(request["templateDecisions"]!.AsArray())!["existingTemplateId"]!.GetValue<string>());
        var parts = request["locationAllocations"]!["groups"]![0]!["parts"]!.AsArray();
        Assert.Equal("", parts[0]!["templateId"]!.GetValue<string>());
        Assert.Equal(5, parts[0]!["workLocationId"]!.GetValue<int>());
        Assert.Equal("32", parts[1]!["templateId"]!.GetValue<string>());
        Assert.False(result["previewCanApply"]!.GetValue<bool>());
        Assert.Equal(2, result["editableDefaultsRevision"]!.GetValue<int>());
        Assert.Equal("31", await f.Db.ExecuteScalarAsync<string>("SELECT JSON_UNQUOTE(JSON_EXTRACT(result_json,'$.templateId')) FROM excel_payroll_setup_imports WHERE id='archived'"));
    }
}
