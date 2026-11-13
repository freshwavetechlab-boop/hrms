using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Fixtures;

// Test-only setup. Every DDL/write is preceded by the exact loopback DB guard.
public sealed class ExcelPayslipBatchUpdateFixture
{
    public const int ClientId = EssPayslipFlowFixture.ClientId;
    public const string BatchId = EssPayslipFlowFixture.BatchId;
    public string Connection { get; }
    public IConfiguration Config { get; }
    public ExcelPayslipRepository Repository { get; }
    public string OriginalSendStateJson { get; private set; } = "";
    public ExcelPayslipBatchUpdateFixture(string connection)
    {
        var target = new MySqlConnectionStringBuilder(connection);
        if (target.Server != "127.0.0.1" || target.Port != 33330 || target.Database != "attendance_location_test")
            throw new InvalidOperationException("Only the explicitly owned loopback Excel update test database is permitted.");
        Connection = connection;
        Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connection, ["OutboundDelivery:Suppressed"] = "true", ["IntegrationCredentialEncryption:MasterKey"] = "synthetic-batch-update-test-key" }).Build();
        var protector = new PortableIntegrationCredentialProtector(Config, new EphemeralDataProtectionProvider(), NullLogger<PortableIntegrationCredentialProtector>.Instance);
        Repository = new(Config, new NotificationRepository(Config, null!, null!, NullLogger<NotificationRepository>.Instance), new ExcelPayslipPdfService(), protector);
    }

    public async Task ResetAsync()
    {
        var profile = new EssPayslipFlowFixture(Connection);
        await profile.ResetAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "EssProfile"));
        await using var db = await OpenAsync();
        foreach (var (table, column, definition) in new[] {
            ("employees", "PersonalJson", "JSON NULL"), ("employees", "PaymentJson", "JSON NULL"),
            ("employees", "Grade", "VARCHAR(100) NOT NULL DEFAULT ''"), ("employees", "PortalAccess", "BOOLEAN NOT NULL DEFAULT TRUE"),
            ("worklocations", "State", "VARCHAR(100) NOT NULL DEFAULT ''"), ("worklocations", "IsActive", "BOOLEAN NOT NULL DEFAULT TRUE") })
            if (!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@Table AND column_name=@Column)", new { Table = table, Column = column }))
                await db.ExecuteAsync($"ALTER TABLE {table} ADD COLUMN {column} {definition}");
        // Production schema setup is called only against this guarded synthetic DB,
        // before any operation transaction. The feature itself must never do DDL.
        await EmployeeRepository.PrepareRecruitmentSaveAsync(db);
        await db.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS auditlogs(Id BIGINT PRIMARY KEY AUTO_INCREMENT,UserId INT NULL,UserEmail VARCHAR(190),Action VARCHAR(120) NOT NULL,Resource VARCHAR(190),Method VARCHAR(20),Path VARCHAR(500),StatusCode INT NOT NULL DEFAULT 0,IpAddress VARCHAR(80),UserAgent VARCHAR(500),DetailsJson JSON NOT NULL,CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP);
CREATE TABLE IF NOT EXISTS notification_queue(Id BIGINT PRIMARY KEY AUTO_INCREMENT,RuleId BIGINT NULL,EventCode VARCHAR(120),ResourceType VARCHAR(100),ResourceId VARCHAR(120),ClientId INT,ToJson JSON,CcJson JSON,BccJson JSON,Subject VARCHAR(500),BodyHtml MEDIUMTEXT,Status VARCHAR(40),RetryCount INT NOT NULL DEFAULT 0,ErrorMessage TEXT,CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,SentAt DATETIME NULL);
CREATE TABLE IF NOT EXISTS notification_logs(Id BIGINT PRIMARY KEY AUTO_INCREMENT,QueueId BIGINT,EventCode VARCHAR(120),ResourceType VARCHAR(100),ResourceId VARCHAR(120),Recipient VARCHAR(220),Status VARCHAR(40),ProviderMessageId VARCHAR(220) NOT NULL DEFAULT '',ErrorMessage TEXT,CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP);
CREATE TABLE IF NOT EXISTS payruns(Id INT PRIMARY KEY AUTO_INCREMENT,ClientId INT,PayPeriod VARCHAR(7),Status VARCHAR(40));
CREATE TABLE IF NOT EXISTS payrunemployees(Id INT PRIMARY KEY AUTO_INCREMENT,PayRunId INT,EmployeeId INT);
CREATE TABLE IF NOT EXISTS dropdownmasters(Id BIGINT PRIMARY KEY AUTO_INCREMENT,ClientId INT,Type VARCHAR(100),Value VARCHAR(190),IsActive BOOLEAN DEFAULT TRUE);
CREATE TABLE IF NOT EXISTS modulesettings(client_id INT NOT NULL,ModuleCode VARCHAR(190) NOT NULL,IsEnabled BOOLEAN NOT NULL DEFAULT TRUE,SettingsJson JSON,PRIMARY KEY(client_id,ModuleCode));
CREATE TABLE IF NOT EXISTS attendance_groups(id INT PRIMARY KEY,client_id INT,name VARCHAR(190),work_location_id INT NOT NULL DEFAULT 0,department VARCHAR(100) DEFAULT '',designation VARCHAR(100) DEFAULT '',is_active BOOLEAN DEFAULT TRUE);
CREATE TABLE IF NOT EXISTS attendance_group_employees(attendance_group_id INT,employee_id INT,UNIQUE KEY UX_Synthetic_Membership(attendance_group_id,employee_id));
DROP TRIGGER IF EXISTS synthetic_batch_update_late_failure;
DELETE e FROM payrunemployees e JOIN payruns r ON r.Id=e.PayRunId WHERE r.ClientId=907;
DELETE FROM payruns WHERE ClientId=907;
DELETE FROM attendance_group_employees WHERE employee_id IN(107,108);
DELETE FROM attendance_groups WHERE client_id=907;
DELETE FROM dropdownmasters WHERE ClientId=907;
DELETE FROM modulesettings WHERE client_id=907 AND ModuleCode LIKE 'xp_update_%';
DELETE FROM employee_audit_trail WHERE EmployeeId IN (107,108);
DELETE FROM employee_infotype_records WHERE EmployeeId IN (107,108);
DELETE FROM employee_it0001_org_assignment WHERE EmployeeId IN (107,108);
DELETE FROM employee_it0008_basic_pay WHERE EmployeeId IN (107,108);
DELETE FROM employee_it0002_personal_data WHERE EmployeeId=108;
DELETE FROM employee_it0006_addresses WHERE EmployeeId=108;
DELETE FROM employee_it0009_bank_details WHERE EmployeeId=108;
DELETE FROM employeepersonaldetails WHERE EmployeeId=108;
DELETE FROM employeepaymentdetails WHERE EmployeeId=108;
DELETE FROM employeesalarycomponents WHERE EmployeeId=108;
DELETE FROM employees WHERE Id=108;
DELETE FROM notification_logs;
DELETE FROM notification_queue;
UPDATE employees SET PersonalJson=JSON_OBJECT('keepCustomPersonal','unchanged'),PaymentJson=JSON_OBJECT('keepCustomPayment','unchanged'),Grade='Synthetic grade',PortalAccess=TRUE WHERE Id=107 AND ClientId=907;");
        var batchText = await db.ExecuteScalarAsync<string>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=907 AND id=@Id", new { Id = BatchId }) ?? throw new InvalidOperationException("Synthetic saved batch missing.");
        var sourceText = await db.ExecuteScalarAsync<string>("SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=907 AND batch_id=@Id", new { Id = BatchId }) ?? throw new InvalidOperationException("Synthetic saved calculation source missing.");
        var batchNode = JsonNode.Parse(batchText)!; batchNode["futureMetadata"] = new JsonObject { ["keep"] = "unrecognized-but-preserved" };
        var sourceNode = JsonNode.Parse(sourceText)!; sourceNode["futureMetadata"] = new JsonObject { ["keep"] = "unrecognized-but-preserved" };
        // The ESS display-only fixture intentionally has a compact source. This
        // update feature exercises the real validated source, so retain every
        // mapped information/component and the production mapping kind names.
        sourceNode["columnCount"] = 14;
        sourceNode["columns"]![0]!["kind"] = "employeeCode"; sourceNode["columns"]![1]!["kind"] = "employeeName"; sourceNode["columns"]![5]!["kind"] = "netPay";
        var columns = sourceNode["columns"]!.AsArray(); var cells = sourceNode["rows"]![0]!["cells"]!.AsArray();
        foreach (var (index, label, kind, value, formula) in new[] {
            (6,"Mobile No","info","9000000107",(string?)null), (7,"Account No","info","0012345678901",null),
            (8,"IFSC Code","info","ABCD0000123",null), (9,"Work Location","info","Synthetic period office",null),
            (10,"PF","deduction","10",null), (11,"Employer PF","employer","12.3456",null),
            (12,"Gross","grossTotal","1244.5678","E2"), (13,"Total deductions","deductionTotal","10","K2") })
        {
            columns.Add(new JsonObject { ["columnIndex"] = index, ["sourceHeader"] = label, ["label"] = label, ["kind"] = kind });
            var cell = new JsonObject { ["value"] = value }; if (formula is not null) cell["formulaText"] = formula; cells.Add(cell);
        }
        var linkedBatch = batchNode.Deserialize<ExcelPayslipBatch>(ExcelPayslipRepository.JsonOptions)!;
        var validSource = sourceNode.Deserialize<ExcelPayslipCalculationSource>(ExcelPayslipRepository.JsonOptions)!;
        linkedBatch.ProfileLinks = [new(linkedBatch.Rows[0].Id, linkedBatch.Rows[0].SourceRow, 107, ExcelPayslipMasterSynchronization.ProfileIdentityHash(linkedBatch, linkedBatch.Rows[0], validSource))];
        batchNode["profileLinks"] = JsonSerializer.SerializeToNode(linkedBatch.ProfileLinks, ExcelPayslipRepository.JsonOptions);
        await db.ExecuteAsync("UPDATE excel_payslip_batches SET batch_json=CAST(@Batch AS JSON) WHERE client_id=907 AND id=@Id;UPDATE excel_payslip_calculation_sources SET source_json=CAST(@Source AS JSON) WHERE client_id=907 AND batch_id=@Id", new { Id = BatchId, Batch = batchNode.ToJsonString(), Source = sourceNode.ToJsonString() });
        var raw = batchNode.Deserialize<ExcelPayslipBatch>(ExcelPayslipRepository.JsonOptions)!;
        var document = NotificationRepository.PrepareExcelPayslipMailMetadata(raw, raw.Rows, "source@example.test", "11111111111111111111111111111111", "synthetic sender", false, 2, ExcelPayslipTestsTemplate());
        document.QueueId = await db.ExecuteScalarAsync<long>("INSERT INTO notification_queue(EventCode,ResourceType,ResourceId,ClientId,ToJson,CcJson,BccJson,Subject,BodyHtml,Status,SentAt) VALUES('EXCEL_PAYSLIP.SEND','ExcelPayslipBatch',@Resource,907,'[\"source@example.test\"]','[]','[]','Historical synthetic payslip','Historical only','Sent',UTC_TIMESTAMP());SELECT LAST_INSERT_ID()", new { Resource = BatchId + ":" + document.Id });
        var state = new ExcelPayslipSendState(); state.Deliveries.Add(document.Id, document);
        state.Requests[document.RequestId] = JsonSerializer.SerializeToElement(new { fingerprint = "historical-fixture", result = new { items = new[] { new { rowId = "e2e-row", email = "source@example.test", status = "Queued" } } } });
        OriginalSendStateJson = JsonSerializer.Serialize(state, ExcelPayslipRepository.JsonOptions);
        await db.ExecuteAsync("UPDATE excel_payslip_batches SET send_state_json=CAST(@State AS JSON) WHERE client_id=907 AND id=@Id", new { Id = BatchId, State = OriginalSendStateJson });
        OriginalSendStateJson = (await db.ExecuteScalarAsync<string>("SELECT CAST(send_state_json AS CHAR) FROM excel_payslip_batches WHERE client_id=907 AND id=@Id", new { Id = BatchId }))!;
    }

    public async Task AddSecondEmployeeAsync()
    {
        await using var db = await OpenAsync();
        await db.ExecuteAsync("INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,AnnualCtc,SalaryStructureId,DateOfJoining,SalaryJson,PersonalJson,PaymentJson,WorkEmail,Designation,Department,Grade,WorkLocationId,PortalAccess) VALUES(108,907,'PROFILE-108','Second','Employee',480000,'Synthetic template','2026-01-02','{}','{}','{}','second@example.test','Analyst','Operations','Synthetic grade',907,TRUE);INSERT INTO employeepersonaldetails(EmployeeId,PanNumber,Mobile) VALUES(108,'BCDEF2345G','9000000108');INSERT INTO employeepaymentdetails(EmployeeId,BankName,BankAccountNo,IfscCode,PaymentMode) VALUES(108,'Synthetic Bank','0012345678902','ABCD0000123','Bank Transfer')");
        var docs = await DocumentsAsync(); var batch = docs.Batch.Deserialize<ExcelPayslipBatch>(ExcelPayslipRepository.JsonOptions)!; var source = docs.Source.Deserialize<ExcelPayslipCalculationSource>(ExcelPayslipRepository.JsonOptions)!;
        var second = JsonSerializer.Deserialize<ExcelPayslipRow>(JsonSerializer.Serialize(batch.Rows[0], ExcelPayslipRepository.JsonOptions), ExcelPayslipRepository.JsonOptions)!;
        second.Id = "e2e-row-2"; second.SourceRow = 3; second.EmployeeCode = "PROFILE-108"; second.EmployeeName = "Second Employee"; second.Email = "second@example.test";
        second.Information.Single(f => f.Label == "PAN").Value = "BCDEF2345G"; second.Information.Single(f => f.Label == "Mobile No").Value = "9000000108"; second.Information.Single(f => f.Label == "Account No").Value = "0012345678902";
        batch.Rows.Add(second);
        var secondSource = JsonSerializer.Deserialize<ExcelPayslipCalculationSourceRow>(JsonSerializer.Serialize(source.Rows[0]), ExcelPayslipRepository.JsonOptions)!;
        secondSource.SourceRow = 3; secondSource.Cells[0]!.Value = second.EmployeeCode; secondSource.Cells[1]!.Value = second.EmployeeName; secondSource.Cells[2]!.Value = "BCDEF2345G"; secondSource.Cells[5]!.FormulaText = "E3-10";
        secondSource.Cells[6]!.Value = "9000000108"; secondSource.Cells[7]!.Value = "0012345678902"; secondSource.Cells[12]!.FormulaText = "E3"; secondSource.Cells[13]!.FormulaText = "K3";
        source.Rows.Add(secondSource); batch.ProfileLinks!.Add(new(second.Id, second.SourceRow, 108, ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch, second, source)));
        var batchNode = JsonSerializer.SerializeToNode(batch, ExcelPayslipRepository.JsonOptions)!; batchNode["futureMetadata"] = docs.Batch["futureMetadata"]!.DeepClone();
        var sourceNode = JsonSerializer.SerializeToNode(source, ExcelPayslipRepository.JsonOptions)!; sourceNode["futureMetadata"] = docs.Source["futureMetadata"]!.DeepClone();
        await db.ExecuteAsync("UPDATE excel_payslip_batches SET batch_json=CAST(@Batch AS JSON) WHERE client_id=907 AND id=@Id;UPDATE excel_payslip_calculation_sources SET source_json=CAST(@Source AS JSON) WHERE client_id=907 AND batch_id=@Id", new { Id = BatchId, Batch = batchNode.ToJsonString(), Source = sourceNode.ToJsonString() });
    }

    public async Task AddAttendanceInputAsync()
    {
        var docs = await DocumentsAsync(); var source = docs.Source; var batch = docs.Batch;
        source["columnCount"] = 16; source["attendanceColumnIndex"] = 14;
        source["columns"]!.AsArray().Add(new JsonObject { ["columnIndex"] = 14, ["sourceHeader"] = "Paid days", ["label"] = "Paid days", ["kind"] = "info" });
        source["rows"]![0]!["cells"]!.AsArray().Add(new JsonObject { ["value"] = "30", ["dataType"] = "Number" });
        source["rows"]![0]!["cells"]![4]!["formulaText"] = "1244.5678*O2/30";
        source["rows"]![0]!["cells"]![5]!["formulaText"] = "E2-K2";
        batch["rows"]![0]!["information"]!.AsArray().Add(new JsonObject { ["label"] = "Paid days", ["value"] = "30" });
        source["columns"]!.AsArray().Add(new JsonObject { ["columnIndex"] = 15, ["sourceHeader"] = "Annual CTC", ["label"] = "Annual CTC", ["kind"] = "info" });
        source["rows"]![0]!["cells"]!.AsArray().Add(new JsonObject { ["value"] = "480000", ["dataType"] = "Number" });
        batch["rows"]![0]!["information"]!.AsArray().Add(new JsonObject { ["label"] = "Annual CTC", ["value"] = "480000" });
        var typedBatch = batch.Deserialize<ExcelPayslipBatch>(ExcelPayslipRepository.JsonOptions)!;
        var typedSource = source.Deserialize<ExcelPayslipCalculationSource>(ExcelPayslipRepository.JsonOptions)!;
        if (ExcelPayslipCalculationService.ValidateSource(typedSource, typedBatch) is { } error) throw new InvalidOperationException(error);
        await using var db = await OpenAsync();
        await db.ExecuteAsync("UPDATE excel_payslip_batches SET batch_json=CAST(@Batch AS JSON) WHERE client_id=907 AND id=@Id;UPDATE excel_payslip_calculation_sources SET source_json=CAST(@Source AS JSON) WHERE client_id=907 AND batch_id=@Id", new { Id = BatchId, Batch = batch.ToJsonString(), Source = source.ToJsonString() });
    }

    public async Task<MySqlConnection> OpenAsync() { var db = new MySqlConnection(Connection); await db.OpenAsync(); return db; }
    public async Task<(JsonNode Batch, JsonNode Source, string State)> DocumentsAsync()
    {
        await using var db = await OpenAsync(); var row = await db.QuerySingleAsync<(string Batch, string Source, string State)>("SELECT CAST(b.batch_json AS CHAR),CAST(s.source_json AS CHAR),CAST(b.send_state_json AS CHAR) FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=907 AND b.id=@Id", new { Id = BatchId });
        return (JsonNode.Parse(row.Batch)!, JsonNode.Parse(row.Source)!, row.State);
    }
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static NotificationTemplate ExcelPayslipTestsTemplate() => new() { Code = "EXCEL_PAYSLIP_SEND_DEFAULT", Name = "Synthetic historical", SubjectTemplate = "{{employeeName}}", BodyTemplate = "Historical fixture", IsActive = true };
}
