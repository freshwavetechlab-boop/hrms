using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

[Collection("EssPayslipFlow")]
public sealed class ExcelPayslipConsolidationListDatabaseTests
{
    private sealed class IsolatedFactAttribute : FactAttribute
    {
        public IsolatedFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("FREVO_EXCEL_CATALOG_TEST_DB") is null)
                Skip = "Explicit isolated consolidation catalog database required.";
        }
    }

    [IsolatedFact]
    public async Task Client_list_and_dashboard_preserve_exact_fresh_bindings_and_retained_history_without_writes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = await fixture.DocumentsAsync();
        var working = await fixture.Repository.ListAsync(Fixture.Client);
        var current = Assert.Single(working);
        Assert.Equal(Fixture.Canonical, current.Id); Assert.False(current.ConsolidationStale);
        Assert.Equal(8, current.ConsolidatedSourceBatchIds.Count);
        var all = await fixture.Repository.ListAsync(Fixture.Client, includeConsolidated: true);
        Assert.Equal(9, all.Count); Assert.All(all, row => Assert.False(row.ConsolidationStale));
        Assert.All(all.Where(row => row.Id != Fixture.Canonical), row =>
        {
            Assert.True(row.IsConsolidatedSource); Assert.Equal(Fixture.Canonical, row.CanonicalBatchId);
            Assert.Equal(Fixture.Group, row.ConsolidationGroupId);
        });
        var dashboard = await fixture.Repository.GetDashboardAsync(Fixture.Client);
        Assert.NotNull(dashboard); Assert.Equal(Fixture.Canonical, dashboard.SelectedBatch!.Id);
        Assert.Equal(95.123456m, dashboard.Summary!.NetPay);
        var historical = await fixture.Repository.GetDashboardAsync(Fixture.Client, Fixture.Source(1));
        Assert.NotNull(historical); Assert.Equal(Fixture.Source(1), historical.SelectedBatch!.Id);
        Assert.True(historical.SelectedBatch.IsConsolidatedSource);
        Assert.Equal(before, await fixture.DocumentsAsync());
    }

    [IsolatedFact]
    public async Task A_changed_source_formula_reveals_retained_versions_and_is_rechecked_on_every_request()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Single(await fixture.Repository.ListAsync(Fixture.Client));
        await using (var db = await fixture.OpenAsync())
            await db.ExecuteAsync("UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.rows[0].cells[1].formulaText','A2-6') WHERE client_id=@Client AND batch_id=@Id",
                new { Fixture.Client, Id = Fixture.Source(3) });
        var before = await fixture.DocumentsAsync();
        var changed = await fixture.Repository.ListAsync(Fixture.Client);
        // CurrentMonths selects one working month after filtering. Retained
        // visibility is asserted through the exact include-history contract.
        Assert.True(Assert.Single(changed).ConsolidationStale);
        var all = await fixture.Repository.ListAsync(Fixture.Client, includeConsolidated: true);
        Assert.Equal(9, all.Count); Assert.All(all, row => Assert.True(row.ConsolidationStale));
        Assert.Equal(before, await fixture.DocumentsAsync());
    }

    [IsolatedFact]
    public async Task A_missing_source_stays_stale_instead_of_trusting_the_saved_group_proof()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var db = await fixture.OpenAsync())
            await db.ExecuteAsync("DELETE FROM excel_payslip_calculation_sources WHERE client_id=@Client AND batch_id=@Id", new { Fixture.Client, Id = Fixture.Source(5) });
        var before = await fixture.DocumentsAsync();
        var all = await fixture.Repository.ListAsync(Fixture.Client, includeConsolidated: true);
        Assert.Equal(9, all.Count); Assert.All(all, row => Assert.True(row.ConsolidationStale));
        Assert.Equal(before, await fixture.DocumentsAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal const int Client = 97707;
        internal const string Canonical = "ffffffffffffffffffffffffffff7707", Group = "cccccccccccccccccccccccccccc7707";
        internal static string Source(int number) => number.ToString("x32");
        private string Connection { get; }
        internal ExcelPayslipRepository Repository { get; }
        private Fixture(string connection)
        {
            var target = new MySqlConnectionStringBuilder(connection);
            if (target.Server != "127.0.0.1" || target.Port != 33330 || target.Database != "attendance_location_test")
                throw new InvalidOperationException("Only the explicitly owned loopback consolidation test database is permitted.");
            Connection = connection;
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Default"] = connection, ["OutboundDelivery:Suppressed"] = "true" }).Build();
            Repository = new(configuration, null!, new ExcelPayslipPdfService());
        }
        internal async Task<MySqlConnection> OpenAsync()
        {
            var db = new MySqlConnection(Connection); await db.OpenAsync(); return db;
        }
        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(Environment.GetEnvironmentVariable("FREVO_EXCEL_CATALOG_TEST_DB")!);
            await using var db = await fixture.OpenAsync();
            // This schema/setup runs only after the exact loopback guard above.
            // Production list/dashboard methods must perform no schema or data writes.
            await db.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS clients(Id INT PRIMARY KEY,Code VARCHAR(60) NOT NULL DEFAULT '',Name VARCHAR(180),IsActive BOOLEAN NOT NULL DEFAULT TRUE);
CREATE TABLE IF NOT EXISTS excel_payslip_batches(client_id INT,id CHAR(32),batch_json JSON,send_state_json JSON,created_at DATETIME,PRIMARY KEY(client_id,id));
CREATE TABLE IF NOT EXISTS excel_payslip_calculation_sources(client_id INT,batch_id CHAR(32),source_json JSON,created_at DATETIME,PRIMARY KEY(client_id,batch_id));
CREATE TABLE IF NOT EXISTS excel_payslip_cleanup_actions(client_id INT NOT NULL,batch_id CHAR(32) NOT NULL,request_id CHAR(32) NOT NULL,request_hash CHAR(64) NOT NULL,action_code VARCHAR(40) NOT NULL,object_id VARCHAR(80) NOT NULL DEFAULT '',batch_hash CHAR(64) NOT NULL,keeper_id VARCHAR(32) NOT NULL DEFAULT '',keeper_hash VARCHAR(64) NOT NULL DEFAULT '',proof_json JSON NOT NULL,actor VARCHAR(190) NOT NULL,created_at_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),PRIMARY KEY(client_id,request_id));");
            await ClearAsync(db);
            await db.ExecuteAsync("INSERT INTO clients(Id,Code,Name,IsActive) VALUES(@Client,'CATALOG_TEST','Synthetic consolidation catalog',TRUE)", new { Client });
            var documents = new Dictionary<string, string>();
            foreach (var id in Enumerable.Range(1, 8).Select(Source).Prepend(Canonical))
            {
                var rowId = id == Canonical ? "canonical-row" : "retained-" + id;
                var batch = new ExcelPayslipBatch { Id = id, ClientId = Client, ClientName = "Synthetic consolidation catalog", Month = "2026-10", SourceFileName = "Synthetic source.xlsx", SheetName = "Salary", HeaderRow = 1,
                    CreatedBy = "isolated-catalog-test", CreatedAtUtc = new(2026, 11, id == Canonical ? 2 : 1),
                    Rows = [new() { Id = rowId, SourceRow = 2, EmployeeCode = "SYN-001", EmployeeName = "Synthetic Employee", Earnings = [new() { Label = "Basic", Amount = 100.123456m }], Deductions = [new() { Label = "PF", Amount = 5 }], EmployerContributions = [new() { Label = "Employer PF", Amount = 13 }], NetPay = 95.123456m }] };
                var source = new ExcelPayslipCalculationSource { SheetName = "Salary", HeaderRow = 1, ColumnCount = 2,
                    Columns = [new() { ColumnIndex = 0, SourceHeader = "Basic", Label = "Basic", Kind = "earning" }, new() { ColumnIndex = 1, SourceHeader = "Net", Label = "Net", Kind = "netPay" }],
                    Rows = [new() { SourceRow = 2, Cells = [new() { Value = "100.123456" }, new() { Value = "95.123456", FormulaText = "A2-5" }] }] };
                var sourceNode = JsonSerializer.SerializeToNode(source, ExcelPayslipRepository.JsonOptions)!;
                sourceNode["unmappedEvidence"] = new string('x', 128 * 1024) + " हिंदी 😀 \\u003Ckept\\u003E";
                await db.ExecuteAsync("INSERT INTO excel_payslip_batches(client_id,id,batch_json,send_state_json,created_at) VALUES(@Client,@Id,CAST(@Batch AS JSON),CAST(@State AS JSON),@Created);INSERT INTO excel_payslip_calculation_sources(client_id,batch_id,source_json,created_at) VALUES(@Client,@Id,CAST(@Source AS JSON),@Created)",
                    new { Client, Id = id, Batch = JsonSerializer.Serialize(batch, ExcelPayslipRepository.JsonOptions), Source = sourceNode.ToJsonString(), State = JsonSerializer.Serialize(new { retained = "unchanged", evidence = new string('s', 32768) }), Created = batch.CreatedAtUtc });
                var stored = await db.QuerySingleAsync<Snapshot>("SELECT CAST(b.batch_json AS CHAR) Batch,CAST(s.source_json AS CHAR) Source FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=@Client AND b.id=@Id", new { Client, Id = id });
                documents[id] = ExcelPayslipLegacyReview.SnapshotHash(stored.Batch, stored.Source);
            }
            var proof = new { Version = 1, ClientId = Client, GroupId = Group, CanonicalBatchId = Canonical, CanonicalHash = documents[Canonical], CanonicalRows = 1,
                Sources = Enumerable.Range(1, 8).Select(number => new { BatchId = Source(number), SnapshotHash = documents[Source(number)], Rows = new[] { new { SourceRow = 2, CanonicalRowId = "canonical-row", SourceRowId = "retained-" + Source(number) } } }) };
            await db.ExecuteAsync("INSERT INTO excel_payslip_cleanup_actions(client_id,batch_id,request_id,request_hash,action_code,object_id,batch_hash,keeper_id,keeper_hash,proof_json,actor) VALUES(@Client,@Canonical,@Group,@Hash,'batch-group-create','',@Hash,@Canonical,@Hash,CAST(@Proof AS JSON),'isolated-catalog-test')",
                new { Client, Canonical, Group, Hash = documents[Canonical], Proof = JsonSerializer.Serialize(proof, ExcelPayslipRepository.JsonOptions) });
            return fixture;
        }
        private sealed class Snapshot { public string Batch { get; set; } = ""; public string Source { get; set; } = ""; }
        internal async Task<string> DocumentsAsync()
        {
            await using var db = await OpenAsync();
            var snapshots = await db.QueryAsync("SELECT b.id,SHA2(CAST(b.batch_json AS CHAR),256) batchHash,SHA2(CAST(b.send_state_json AS CHAR),256) stateHash,SHA2(CAST(s.source_json AS CHAR),256) sourceHash FROM excel_payslip_batches b LEFT JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=@Client ORDER BY b.id", new { Client });
            var audits = await db.QueryAsync("SELECT request_id,SHA2(CAST(proof_json AS CHAR),256) proofHash FROM excel_payslip_cleanup_actions WHERE client_id=@Client ORDER BY request_id", new { Client });
            return JsonSerializer.Serialize(new { snapshots, audits });
        }
        private static Task ClearAsync(MySqlConnection db) => db.ExecuteAsync("DELETE FROM excel_payslip_cleanup_actions WHERE client_id=@Client;DELETE FROM excel_payslip_calculation_sources WHERE client_id=@Client;DELETE FROM excel_payslip_batches WHERE client_id=@Client;DELETE FROM clients WHERE Id=@Client", new { Client });
        public async ValueTask DisposeAsync() { await using var db = await OpenAsync(); await ClearAsync(db); }
    }
}
