using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public static class PayrollDataTableStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public static async Task EnsureAsync(MySqlConnection connection)
    {
        await connection.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS SalaryComponents (
    Id BIGINT PRIMARY KEY,
    Code VARCHAR(80) NOT NULL,
    ComponentType VARCHAR(120) NOT NULL DEFAULT '',
    Category VARCHAR(60) NOT NULL DEFAULT 'Earning',
    Name VARCHAR(160) NOT NULL,
    PayType VARCHAR(60) NOT NULL DEFAULT 'Fixed Pay',
    CalculationType VARCHAR(80) NOT NULL DEFAULT 'Fixed Amount',
    ValueText VARCHAR(500) NOT NULL DEFAULT '',
    Formula VARCHAR(1000) NOT NULL DEFAULT '',
    BaseComponent VARCHAR(80) NOT NULL DEFAULT '',
    Taxable BOOLEAN NOT NULL DEFAULT TRUE,
    Ctc BOOLEAN NOT NULL DEFAULT TRUE,
    ProRata BOOLEAN NOT NULL DEFAULT TRUE,
    Fbp BOOLEAN NOT NULL DEFAULT FALSE,
    RestrictFbp BOOLEAN NOT NULL DEFAULT FALSE,
    Epf VARCHAR(40) NOT NULL DEFAULT 'Never',
    Esi BOOLEAN NOT NULL DEFAULT FALSE,
    Recurring BOOLEAN NOT NULL DEFAULT TRUE,
    Scheduled BOOLEAN NOT NULL DEFAULT FALSE,
    InvestmentType VARCHAR(120) NOT NULL DEFAULT '',
    CorrectionOf VARCHAR(120) NOT NULL DEFAULT '',
    Active BOOLEAN NOT NULL DEFAULT TRUE,
    Priority INT NOT NULL DEFAULT 999,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_SalaryComponents_Code (Code)
);
CREATE TABLE IF NOT EXISTS SalaryStructures (
    Id BIGINT PRIMARY KEY,
    ClientId INT NOT NULL DEFAULT 0,
    ClientRef VARCHAR(200) NOT NULL DEFAULT '',
    Name VARCHAR(200) NOT NULL DEFAULT '',
    AnnualCtc DECIMAL(18,2) NOT NULL DEFAULT 0,
    Active BOOLEAN NOT NULL DEFAULT TRUE,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);
CREATE TABLE IF NOT EXISTS SalaryStructureLines (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    StructureId BIGINT NOT NULL,
    ComponentId VARCHAR(80) NOT NULL,
    ValueText VARCHAR(1000) NOT NULL DEFAULT '',
    SortOrder INT NOT NULL DEFAULT 0,
    UNIQUE KEY UX_SalaryStructureLines_Structure_Component (StructureId, ComponentId)
);
CREATE TABLE IF NOT EXISTS PayslipTemplates (
    Id BIGINT PRIMARY KEY,
    ClientId INT NOT NULL DEFAULT 0,
    ClientRef VARCHAR(200) NOT NULL DEFAULT '',
    Name VARCHAR(200) NOT NULL DEFAULT '',
    Theme VARCHAR(80) NOT NULL DEFAULT 'Classic',
    ShowLogo BOOLEAN NOT NULL DEFAULT TRUE,
    ShowClient BOOLEAN NOT NULL DEFAULT TRUE,
    ShowYtd BOOLEAN NOT NULL DEFAULT TRUE,
    ShowBank BOOLEAN NOT NULL DEFAULT TRUE,
    Note VARCHAR(1000) NOT NULL DEFAULT '',
    Active BOOLEAN NOT NULL DEFAULT TRUE,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);
CREATE TABLE IF NOT EXISTS PayrollSchedules (
    Id INT PRIMARY KEY DEFAULT 1,
    WorkWeek VARCHAR(80) NOT NULL DEFAULT 'Monday - Friday',
    SalaryDays VARCHAR(80) NOT NULL DEFAULT 'Actual days',
    FixedDays VARCHAR(10) NOT NULL DEFAULT '30',
    PayDay VARCHAR(80) NOT NULL DEFAULT 'Last working day',
    FirstPayPeriod VARCHAR(7) NOT NULL DEFAULT '',
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);
CREATE TABLE IF NOT EXISTS ClientPaySchedules (
    ClientId INT PRIMARY KEY,
    WorkWeek VARCHAR(80) NOT NULL DEFAULT 'Monday - Friday',
    SalaryDays VARCHAR(80) NOT NULL DEFAULT 'Actual days',
    FixedDays VARCHAR(10) NOT NULL DEFAULT '30',
    PayDay VARCHAR(80) NOT NULL DEFAULT 'Last working day',
    FirstPayPeriod VARCHAR(7) NOT NULL DEFAULT '',
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);
CREATE TABLE IF NOT EXISTS EmployeeSalaryComponents (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    EmployeeId INT NOT NULL,
    ComponentId VARCHAR(80) NOT NULL,
    ComponentCode VARCHAR(80) NOT NULL DEFAULT '',
    Amount DECIMAL(18,4) NOT NULL DEFAULT 0,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_EmployeeSalaryComponents_Employee_Component (EmployeeId, ComponentId)
);
CREATE TABLE IF NOT EXISTS EmployeePersonalDetails (
    EmployeeId INT PRIMARY KEY,
    DateOfBirth VARCHAR(30) NOT NULL DEFAULT '',
    Mobile VARCHAR(50) NOT NULL DEFAULT '',
    PanNumber VARCHAR(50) NOT NULL DEFAULT '',
    AadhaarNumber VARCHAR(50) NOT NULL DEFAULT '',
    UanNumber VARCHAR(50) NOT NULL DEFAULT '',
    EsicNumber VARCHAR(50) NOT NULL DEFAULT '',
    Source VARCHAR(120) NOT NULL DEFAULT '',
    SourceLocation VARCHAR(200) NOT NULL DEFAULT '',
    City VARCHAR(100) NOT NULL DEFAULT '',
    District VARCHAR(100) NOT NULL DEFAULT '',
    State VARCHAR(100) NOT NULL DEFAULT '',
    RawDesignation VARCHAR(160) NOT NULL DEFAULT '',
    OriginalEmployeeCode VARCHAR(80) NOT NULL DEFAULT '',
    DuplicateResolution VARCHAR(500) NOT NULL DEFAULT '',
    ExcelRow INT NOT NULL DEFAULT 0,
    EsicEmployee DECIMAL(18,4) NOT NULL DEFAULT 0,
    PtLwfWorkmenComp DECIMAL(18,4) NOT NULL DEFAULT 0,
    Tds DECIMAL(18,4) NOT NULL DEFAULT 0,
    Recovery DECIMAL(18,4) NOT NULL DEFAULT 0,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);
CREATE TABLE IF NOT EXISTS EmployeePaymentDetails (
    EmployeeId INT PRIMARY KEY,
    BankAccountNo VARCHAR(100) NOT NULL DEFAULT '',
    IfscCode VARCHAR(40) NOT NULL DEFAULT '',
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);
CREATE TABLE IF NOT EXISTS PayRunEmployeeLines (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    PayRunEmployeeId INT NOT NULL,
    PayRunId INT NOT NULL,
    EmployeeId INT NOT NULL,
    ComponentCode VARCHAR(80) NOT NULL,
    Name VARCHAR(180) NOT NULL DEFAULT '',
    Category VARCHAR(80) NOT NULL DEFAULT '',
    MonthlyAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
    Amount DECIMAL(18,4) NOT NULL DEFAULT 0,
    ProRata BOOLEAN NOT NULL DEFAULT FALSE,
    SortOrder INT NOT NULL DEFAULT 0,
    UNIQUE KEY UX_PayRunEmployeeLines_Row_Component (PayRunEmployeeId, ComponentCode, SortOrder)
);");

        await BackfillSetupTablesAsync(connection);
        await SeedReclDeductionComponentsAsync(connection);
        await BackfillClientPaySchedulesAsync(connection);
        await BackfillEmployeeTablesAsync(connection);
        await BackfillPayRunLinesAsync(connection);
    }

    public static async Task<string> GetSetupJsonAsync(MySqlConnection connection, MySqlTransaction? transaction = null)
    {
        var fallback = await connection.ExecuteScalarAsync<string?>("SELECT SetupJson FROM PayrollSetups ORDER BY Id LIMIT 1", transaction: transaction) ?? "{}";
        var root = ParseRoot(fallback);

        var components = (await connection.QueryAsync<ComponentRow>("SELECT * FROM SalaryComponents ORDER BY Priority, Id", transaction: transaction)).ToList();
        if (components.Count > 0) root["salaryComponents"] = JsonSerializer.SerializeToNode(components.Select(ToDto), JsonOptions);

        var structures = (await connection.QueryAsync<StructureRow>("SELECT * FROM SalaryStructures ORDER BY Id", transaction: transaction)).ToList();
        if (structures.Count > 0)
        {
            var lines = (await connection.QueryAsync<StructureLineRow>("SELECT * FROM SalaryStructureLines ORDER BY StructureId, SortOrder, Id", transaction: transaction)).GroupBy(x => x.StructureId).ToDictionary(g => g.Key, g => g.ToList());
            root["salaryStructures"] = JsonSerializer.SerializeToNode(structures.Select(row => ToDto(row, lines.GetValueOrDefault(row.Id) ?? [])), JsonOptions);
        }

        var templates = (await connection.QueryAsync<PayslipTemplateRow>("SELECT * FROM PayslipTemplates ORDER BY Name", transaction: transaction)).ToList();
        if (templates.Count > 0) root["payslipTemplates"] = JsonSerializer.SerializeToNode(templates.Select(ToDto), JsonOptions);

        var schedule = await connection.QueryFirstOrDefaultAsync<ScheduleDto>("SELECT WorkWeek workWeek, SalaryDays salaryDays, FixedDays fixedDays, PayDay payDay, FirstPayPeriod firstPayPeriod FROM PayrollSchedules WHERE Id=1", transaction: transaction);
        if (schedule is not null) root["schedule"] = JsonSerializer.SerializeToNode(schedule, JsonOptions);

        return root.ToJsonString(JsonOptions);
    }

    public static async Task SaveSetupJsonAsync(MySqlConnection connection, string json)
    {
        var root = ParseRoot(json);

        if (TryRead(root["salaryComponents"], out List<ComponentDto>? components))
            await SaveComponentsAsync(connection, components ?? []);
        if (TryRead(root["salaryStructures"], out List<StructureDto>? structures))
            await SaveStructuresAsync(connection, structures ?? []);
        if (TryRead(root["payslipTemplates"], out List<PayslipTemplateDto>? templates))
            await SavePayslipTemplatesAsync(connection, templates ?? []);
        if (TryRead(root["schedule"], out ScheduleDto? schedule) && schedule is not null)
            await SaveScheduleAsync(connection, schedule);

        var retained = new JsonObject();
        if (root["tax"] is not null) retained["tax"] = root["tax"]!.DeepClone();
        if (root["statutory"] is not null) retained["statutory"] = root["statutory"]!.DeepClone();
        var setupJson = retained.ToJsonString(JsonOptions);
        var id = await connection.ExecuteScalarAsync<int?>("SELECT Id FROM PayrollSetups ORDER BY Id LIMIT 1");
        if (id is null)
            await connection.ExecuteAsync("INSERT INTO PayrollSetups (SetupJson) VALUES (@setupJson)", new { setupJson });
        else
            await connection.ExecuteAsync("UPDATE PayrollSetups SET SetupJson=@setupJson WHERE Id=@id", new { setupJson, id });
    }

    public static async Task SyncClientPayScheduleAsync(MySqlConnection connection, int clientId, string payScheduleJson)
    {
        if (clientId <= 0) return;
        var schedule = Parse<ClientScheduleDto>(payScheduleJson);
        if (schedule is null || string.IsNullOrWhiteSpace(payScheduleJson) || payScheduleJson.Trim() == "{}")
        {
            await connection.ExecuteAsync("DELETE FROM ClientPaySchedules WHERE ClientId=@clientId", new { clientId });
            return;
        }
        await connection.ExecuteAsync(@"INSERT INTO ClientPaySchedules (ClientId,WorkWeek,SalaryDays,FixedDays,PayDay,FirstPayPeriod)
VALUES (@ClientId,@WorkWeek,@SalaryDays,@FixedDays,@PayDay,@FirstPayPeriod)
ON DUPLICATE KEY UPDATE WorkWeek=@WorkWeek,SalaryDays=@SalaryDays,FixedDays=@FixedDays,PayDay=@PayDay,FirstPayPeriod=@FirstPayPeriod", new { ClientId = clientId, schedule.WorkWeek, schedule.SalaryDays, schedule.FixedDays, schedule.PayDay, schedule.FirstPayPeriod });
    }

    public static async Task ApplyClientPaySchedulesAsync(MySqlConnection connection, IEnumerable<Client> clients)
    {
        var list = clients.ToList();
        if (list.Count == 0) return;
        var schedules = (await connection.QueryAsync<ClientScheduleRow>("SELECT * FROM ClientPaySchedules WHERE ClientId IN @Ids", new { Ids = list.Select(x => x.Id).ToArray() })).ToDictionary(x => x.ClientId);
        foreach (var client in list)
            if (schedules.TryGetValue(client.Id, out var schedule))
                client.PayScheduleJson = JsonSerializer.Serialize(ToDto(schedule), JsonOptions);
    }

    public static async Task ApplyEmployeeTablesAsync(MySqlConnection connection, IEnumerable<Employee> employees)
    {
        var list = employees.ToList();
        if (list.Count == 0) return;
        var ids = list.Select(x => x.Id).ToArray();
        var salaryRows = (await connection.QueryAsync<EmployeeSalaryComponentRow>("SELECT EmployeeId,ComponentId,Amount FROM EmployeeSalaryComponents WHERE EmployeeId IN @ids", new { ids })).GroupBy(x => x.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());
        var personalRows = (await connection.QueryAsync<EmployeePersonalRow>("SELECT * FROM EmployeePersonalDetails WHERE EmployeeId IN @ids", new { ids })).ToDictionary(x => x.EmployeeId);
        var paymentRows = (await connection.QueryAsync<EmployeePaymentRow>("SELECT * FROM EmployeePaymentDetails WHERE EmployeeId IN @ids", new { ids })).ToDictionary(x => x.EmployeeId);

        foreach (var employee in list)
        {
            if (salaryRows.TryGetValue(employee.Id, out var salaries) && salaries.Count > 0)
            {
                var salary = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in salaries) salary[row.ComponentId] = row.Amount;
                employee.SalaryJson = JsonSerializer.Serialize(salary, JsonOptions);
            }
            if (personalRows.TryGetValue(employee.Id, out var personal))
            {
                var root = ParseRoot(employee.PersonalJson);
                Set(root, "dob", personal.DateOfBirth); Set(root, "mobile", personal.Mobile); Set(root, "pan", personal.PanNumber); Set(root, "aadhaar", personal.AadhaarNumber);
                Set(root, "uan", personal.UanNumber); Set(root, "esic", personal.EsicNumber); Set(root, "source", personal.Source); Set(root, "sourceLocation", personal.SourceLocation);
                Set(root, "city", personal.City); Set(root, "district", personal.District); Set(root, "state", personal.State); Set(root, "rawDesignation", personal.RawDesignation);
                Set(root, "originalEmployeeCode", personal.OriginalEmployeeCode); Set(root, "duplicateResolution", personal.DuplicateResolution);
                root["excelRow"] = personal.ExcelRow; root["esicEmployee"] = personal.EsicEmployee; root["ptLwfWorkmenComp"] = personal.PtLwfWorkmenComp; root["tds"] = personal.Tds; root["recovery"] = personal.Recovery;
                employee.PersonalJson = root.ToJsonString(JsonOptions);
            }
            if (paymentRows.TryGetValue(employee.Id, out var payment))
            {
                var root = ParseRoot(employee.PaymentJson);
                Set(root, "bankAccountNo", payment.BankAccountNo); Set(root, "ifscCode", payment.IfscCode);
                employee.PaymentJson = root.ToJsonString(JsonOptions);
            }
        }
    }

    public static async Task SyncEmployeeTablesAsync(MySqlConnection connection, Employee employee)
    {
        if (employee.Id <= 0) return;
        await SaveEmployeeSalaryAsync(connection, employee.Id, ParseDecimalMap(employee.SalaryJson));
        await SaveEmployeePersonalAsync(connection, employee.Id, ParseRoot(employee.PersonalJson));
        await SaveEmployeePaymentAsync(connection, employee.Id, ParseRoot(employee.PaymentJson));
    }

    public static async Task SyncPayRunEmployeeLinesAsync(MySqlConnection connection, MySqlTransaction? transaction, PayRunEmployee row)
    {
        if (row.Id <= 0) return;
        await connection.ExecuteAsync("DELETE FROM PayRunEmployeeLines WHERE PayRunEmployeeId=@Id", new { row.Id }, transaction);
        var lines = Parse<List<PayRunLineDto>>(row.DetailsJson) ?? [];
        var order = 0;
        foreach (var line in lines.Where(x => !string.IsNullOrWhiteSpace(x.Id)))
        {
            await connection.ExecuteAsync(@"INSERT INTO PayRunEmployeeLines (PayRunEmployeeId,PayRunId,EmployeeId,ComponentCode,Name,Category,MonthlyAmount,Amount,ProRata,SortOrder)
VALUES (@PayRunEmployeeId,@PayRunId,@EmployeeId,@ComponentCode,@Name,@Category,@MonthlyAmount,@Amount,@ProRata,@SortOrder)", new { PayRunEmployeeId = row.Id, row.PayRunId, row.EmployeeId, ComponentCode = line.Id, line.Name, line.Category, line.MonthlyAmount, line.Amount, line.ProRata, SortOrder = order++ }, transaction);
        }
    }

    private static async Task BackfillSetupTablesAsync(MySqlConnection connection)
    {
        var json = await connection.ExecuteScalarAsync<string?>("SELECT SetupJson FROM PayrollSetups ORDER BY Id LIMIT 1") ?? "{}";
        var root = ParseRoot(json);
        if (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM SalaryComponents") == 0 && TryRead(root["salaryComponents"], out List<ComponentDto>? components)) await SaveComponentsAsync(connection, components ?? []);
        if (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM SalaryStructures") == 0 && TryRead(root["salaryStructures"], out List<StructureDto>? structures)) await SaveStructuresAsync(connection, structures ?? []);
        if (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PayslipTemplates") == 0 && TryRead(root["payslipTemplates"], out List<PayslipTemplateDto>? templates)) await SavePayslipTemplatesAsync(connection, templates ?? []);
        if (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PayrollSchedules") == 0 && TryRead(root["schedule"], out ScheduleDto? schedule) && schedule is not null) await SaveScheduleAsync(connection, schedule);
    }

    private static async Task SeedReclDeductionComponentsAsync(MySqlConnection connection)
    {
        var rows = new[]
        {
            new { Id = 110L, Code = "ESIC", ComponentType = "Employee State Insurance", Category = "Deduction", Name = "Employee ESIC", PayType = "Fixed Pay", CalculationType = "Manual / Variable", ValueText = "", Formula = "", BaseComponent = "", Taxable = false, Ctc = false, ProRata = false, Fbp = false, RestrictFbp = false, Epf = "Never", Esi = false, Recurring = true, Scheduled = false, InvestmentType = "", CorrectionOf = "", Active = true, Priority = 120 },
            new { Id = 111L, Code = "PT_LWF_WC", ComponentType = "Professional Tax / LWF / Workmen Comp", Category = "Deduction", Name = "PT / LWF / Workmen Comp", PayType = "Fixed Pay", CalculationType = "Manual / Variable", ValueText = "", Formula = "", BaseComponent = "", Taxable = false, Ctc = false, ProRata = false, Fbp = false, RestrictFbp = false, Epf = "Never", Esi = false, Recurring = true, Scheduled = false, InvestmentType = "", CorrectionOf = "", Active = true, Priority = 130 },
            new { Id = 112L, Code = "TDS", ComponentType = "Tax Deducted at Source", Category = "Deduction", Name = "TDS", PayType = "Fixed Pay", CalculationType = "Manual / Variable", ValueText = "", Formula = "", BaseComponent = "", Taxable = false, Ctc = false, ProRata = false, Fbp = false, RestrictFbp = false, Epf = "Never", Esi = false, Recurring = true, Scheduled = false, InvestmentType = "", CorrectionOf = "", Active = true, Priority = 140 },
            new { Id = 113L, Code = "RECOVERY", ComponentType = "Recovery", Category = "Deduction", Name = "Recovery", PayType = "Variable Pay", CalculationType = "Manual / Variable", ValueText = "", Formula = "", BaseComponent = "", Taxable = false, Ctc = false, ProRata = false, Fbp = false, RestrictFbp = false, Epf = "Never", Esi = false, Recurring = false, Scheduled = true, InvestmentType = "", CorrectionOf = "", Active = true, Priority = 150 }
        };
        await connection.ExecuteAsync(@"INSERT INTO SalaryComponents (Id,Code,ComponentType,Category,Name,PayType,CalculationType,ValueText,Formula,BaseComponent,Taxable,Ctc,ProRata,Fbp,RestrictFbp,Epf,Esi,Recurring,Scheduled,InvestmentType,CorrectionOf,Active,Priority)
VALUES (@Id,@Code,@ComponentType,@Category,@Name,@PayType,@CalculationType,@ValueText,@Formula,@BaseComponent,@Taxable,@Ctc,@ProRata,@Fbp,@RestrictFbp,@Epf,@Esi,@Recurring,@Scheduled,@InvestmentType,@CorrectionOf,@Active,@Priority)
ON DUPLICATE KEY UPDATE Code=@Code,ComponentType=@ComponentType,Category=@Category,Name=@Name,PayType=@PayType,CalculationType=@CalculationType,ValueText=@ValueText,Formula=@Formula,BaseComponent=@BaseComponent,Taxable=@Taxable,Ctc=@Ctc,ProRata=@ProRata,Fbp=@Fbp,RestrictFbp=@RestrictFbp,Epf=@Epf,Esi=@Esi,Recurring=@Recurring,Scheduled=@Scheduled,InvestmentType=@InvestmentType,CorrectionOf=@CorrectionOf,Active=@Active,Priority=@Priority", rows);
        await connection.ExecuteAsync(@"INSERT INTO SalaryStructureLines (StructureId,ComponentId,ValueText,SortOrder)
SELECT s.Id, CAST(c.Id AS CHAR), '', c.Priority FROM SalaryStructures s JOIN SalaryComponents c ON c.Code IN ('ESIC','PT_LWF_WC','TDS','RECOVERY')
WHERE NOT EXISTS (SELECT 1 FROM SalaryStructureLines l WHERE l.StructureId=s.Id AND l.ComponentId=CAST(c.Id AS CHAR))");
    }

    private static async Task BackfillClientPaySchedulesAsync(MySqlConnection connection)
    {
        var rows = await connection.QueryAsync<Client>("SELECT * FROM Clients WHERE PayScheduleJson IS NOT NULL AND PayScheduleJson <> '{}'");
        foreach (var client in rows) await SyncClientPayScheduleAsync(connection, client.Id, client.PayScheduleJson);
    }

    private static async Task BackfillEmployeeTablesAsync(MySqlConnection connection)
    {
        if (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM EmployeeSalaryComponents") > 0) return;
        var rows = await connection.QueryAsync<Employee>("SELECT * FROM Employees");
        foreach (var employee in rows) await SyncEmployeeTablesAsync(connection, employee);
    }

    private static async Task BackfillPayRunLinesAsync(MySqlConnection connection)
    {
        if (!await TableExistsAsync(connection, "PayRunEmployees")) return;
        if (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PayRunEmployeeLines") > 0) return;
        var rows = await connection.QueryAsync<PayRunEmployee>("SELECT * FROM PayRunEmployees");
        foreach (var row in rows) await SyncPayRunEmployeeLinesAsync(connection, null, row);
    }

    private static async Task SaveComponentsAsync(MySqlConnection connection, List<ComponentDto> rows)
    {
        var ids = rows.Where(x => x.Id > 0).Select(x => x.Id).Distinct().ToArray();
        if (ids.Length > 0) await connection.ExecuteAsync("DELETE FROM SalaryComponents WHERE Id NOT IN @ids", new { ids }); else await connection.ExecuteAsync("DELETE FROM SalaryComponents");
        foreach (var row in rows.Where(x => x.Id > 0))
            await connection.ExecuteAsync(@"INSERT INTO SalaryComponents (Id,Code,ComponentType,Category,Name,PayType,CalculationType,ValueText,Formula,BaseComponent,Taxable,Ctc,ProRata,Fbp,RestrictFbp,Epf,Esi,Recurring,Scheduled,InvestmentType,CorrectionOf,Active,Priority)
VALUES (@Id,@Code,@ComponentType,@Category,@Name,@PayType,@CalculationType,@Value,@Formula,@BaseComponent,@Taxable,@Ctc,@ProRata,@Fbp,@RestrictFbp,@Epf,@Esi,@Recurring,@Scheduled,@InvestmentType,@CorrectionOf,@Active,@PriorityNumber)
ON DUPLICATE KEY UPDATE Code=@Code,ComponentType=@ComponentType,Category=@Category,Name=@Name,PayType=@PayType,CalculationType=@CalculationType,ValueText=@Value,Formula=@Formula,BaseComponent=@BaseComponent,Taxable=@Taxable,Ctc=@Ctc,ProRata=@ProRata,Fbp=@Fbp,RestrictFbp=@RestrictFbp,Epf=@Epf,Esi=@Esi,Recurring=@Recurring,Scheduled=@Scheduled,InvestmentType=@InvestmentType,CorrectionOf=@CorrectionOf,Active=@Active,Priority=@PriorityNumber", new { row.Id, Code = Clean(row.Code).ToUpperInvariant(), row.ComponentType, row.Category, row.Name, row.PayType, row.CalculationType, row.Value, row.Formula, row.BaseComponent, row.Taxable, row.Ctc, row.ProRata, row.Fbp, row.RestrictFbp, row.Epf, row.Esi, row.Recurring, row.Scheduled, row.InvestmentType, row.CorrectionOf, row.Active, PriorityNumber = ToInt(row.Priority, 999) });
    }

    private static async Task SaveStructuresAsync(MySqlConnection connection, List<StructureDto> rows)
    {
        var ids = rows.Where(x => x.Id > 0).Select(x => x.Id).Distinct().ToArray();
        if (ids.Length > 0) await connection.ExecuteAsync("DELETE FROM SalaryStructures WHERE Id NOT IN @ids", new { ids }); else await connection.ExecuteAsync("DELETE FROM SalaryStructures");
        await connection.ExecuteAsync("DELETE FROM SalaryStructureLines WHERE StructureId NOT IN (SELECT Id FROM SalaryStructures)");
        foreach (var row in rows.Where(x => x.Id > 0))
        {
            await connection.ExecuteAsync(@"INSERT INTO SalaryStructures (Id,ClientId,ClientRef,Name,AnnualCtc,Active)
VALUES (@Id,@ClientId,@ClientRef,@Name,@AnnualCtc,@Active)
ON DUPLICATE KEY UPDATE ClientId=@ClientId,ClientRef=@ClientRef,Name=@Name,AnnualCtc=@AnnualCtc,Active=@Active", new { row.Id, ClientId = RefId(row.ClientId), ClientRef = row.ClientId ?? "", row.Name, AnnualCtc = ToDecimal(row.AnnualCtc), row.Active });
            await connection.ExecuteAsync("DELETE FROM SalaryStructureLines WHERE StructureId=@Id", new { row.Id });
            var index = 0;
            foreach (var line in row.Lines)
                await connection.ExecuteAsync(@"INSERT INTO SalaryStructureLines (StructureId,ComponentId,ValueText,SortOrder) VALUES (@StructureId,@ComponentId,@ValueText,@SortOrder)", new { StructureId = row.Id, line.ComponentId, ValueText = line.Value ?? "", SortOrder = index++ });
        }
    }

    private static async Task SavePayslipTemplatesAsync(MySqlConnection connection, List<PayslipTemplateDto> rows)
    {
        var ids = rows.Where(x => x.Id > 0).Select(x => x.Id).Distinct().ToArray();
        if (ids.Length > 0) await connection.ExecuteAsync("DELETE FROM PayslipTemplates WHERE Id NOT IN @ids", new { ids }); else await connection.ExecuteAsync("DELETE FROM PayslipTemplates");
        foreach (var row in rows.Where(x => x.Id > 0))
            await connection.ExecuteAsync(@"INSERT INTO PayslipTemplates (Id,ClientId,ClientRef,Name,Theme,ShowLogo,ShowClient,ShowYtd,ShowBank,Note,Active)
VALUES (@Id,@ClientId,@ClientRef,@Name,@Theme,@ShowLogo,@ShowClient,@ShowYtd,@ShowBank,@Note,@Active)
ON DUPLICATE KEY UPDATE ClientId=@ClientId,ClientRef=@ClientRef,Name=@Name,Theme=@Theme,ShowLogo=@ShowLogo,ShowClient=@ShowClient,ShowYtd=@ShowYtd,ShowBank=@ShowBank,Note=@Note,Active=@Active", new { row.Id, ClientId = RefId(row.ClientId), ClientRef = row.ClientId ?? "", row.Name, row.Theme, row.ShowLogo, row.ShowClient, row.ShowYtd, row.ShowBank, row.Note, row.Active });
    }

    private static Task SaveScheduleAsync(MySqlConnection connection, ScheduleDto row) =>
        connection.ExecuteAsync(@"INSERT INTO PayrollSchedules (Id,WorkWeek,SalaryDays,FixedDays,PayDay,FirstPayPeriod)
VALUES (1,@WorkWeek,@SalaryDays,@FixedDays,@PayDay,@FirstPayPeriod)
ON DUPLICATE KEY UPDATE WorkWeek=@WorkWeek,SalaryDays=@SalaryDays,FixedDays=@FixedDays,PayDay=@PayDay,FirstPayPeriod=@FirstPayPeriod", row);

    private static async Task SaveEmployeeSalaryAsync(MySqlConnection connection, int employeeId, Dictionary<string, decimal> rows)
    {
        await connection.ExecuteAsync("DELETE FROM EmployeeSalaryComponents WHERE EmployeeId=@employeeId", new { employeeId });
        foreach (var row in rows)
        {
            var code = await connection.ExecuteScalarAsync<string?>("SELECT Code FROM SalaryComponents WHERE Id=@Id OR Code=@Id LIMIT 1", new { Id = row.Key });
            await connection.ExecuteAsync(@"INSERT INTO EmployeeSalaryComponents (EmployeeId,ComponentId,ComponentCode,Amount)
VALUES (@employeeId,@ComponentId,@ComponentCode,@Amount)
ON DUPLICATE KEY UPDATE ComponentCode=@ComponentCode,Amount=@Amount", new { employeeId, ComponentId = row.Key, ComponentCode = code ?? row.Key, Amount = row.Value });
        }
    }

    private static Task SaveEmployeePersonalAsync(MySqlConnection connection, int employeeId, JsonObject root) =>
        connection.ExecuteAsync(@"INSERT INTO EmployeePersonalDetails (EmployeeId,DateOfBirth,Mobile,PanNumber,AadhaarNumber,UanNumber,EsicNumber,Source,SourceLocation,City,District,State,RawDesignation,OriginalEmployeeCode,DuplicateResolution,ExcelRow,EsicEmployee,PtLwfWorkmenComp,Tds,Recovery)
VALUES (@EmployeeId,@DateOfBirth,@Mobile,@PanNumber,@AadhaarNumber,@UanNumber,@EsicNumber,@Source,@SourceLocation,@City,@District,@State,@RawDesignation,@OriginalEmployeeCode,@DuplicateResolution,@ExcelRow,@EsicEmployee,@PtLwfWorkmenComp,@Tds,@Recovery)
ON DUPLICATE KEY UPDATE DateOfBirth=@DateOfBirth,Mobile=@Mobile,PanNumber=@PanNumber,AadhaarNumber=@AadhaarNumber,UanNumber=@UanNumber,EsicNumber=@EsicNumber,Source=@Source,SourceLocation=@SourceLocation,City=@City,District=@District,State=@State,RawDesignation=@RawDesignation,OriginalEmployeeCode=@OriginalEmployeeCode,DuplicateResolution=@DuplicateResolution,ExcelRow=@ExcelRow,EsicEmployee=@EsicEmployee,PtLwfWorkmenComp=@PtLwfWorkmenComp,Tds=@Tds,Recovery=@Recovery", new
        {
            EmployeeId = employeeId,
            DateOfBirth = Text(root, "dob", Text(root, "dateOfBirth")),
            Mobile = Text(root, "mobile"),
            PanNumber = Text(root, "pan", Text(root, "panNumber")),
            AadhaarNumber = Text(root, "aadhaar", Text(root, "aadhaarNumber")),
            UanNumber = Text(root, "uan", Text(root, "uanNumber")),
            EsicNumber = Text(root, "esic", Text(root, "esicNumber")),
            Source = Text(root, "source"),
            SourceLocation = Text(root, "sourceLocation"),
            City = Text(root, "city"),
            District = Text(root, "district"),
            State = Text(root, "state"),
            RawDesignation = Text(root, "rawDesignation"),
            OriginalEmployeeCode = Text(root, "originalEmployeeCode"),
            DuplicateResolution = Text(root, "duplicateResolution"),
            ExcelRow = ToInt(Text(root, "excelRow"), 0),
            EsicEmployee = ToDecimal(Text(root, "esicEmployee")),
            PtLwfWorkmenComp = ToDecimal(Text(root, "ptLwfWorkmenComp")),
            Tds = ToDecimal(Text(root, "tds")),
            Recovery = ToDecimal(Text(root, "recovery"))
        });

    private static Task SaveEmployeePaymentAsync(MySqlConnection connection, int employeeId, JsonObject root) =>
        connection.ExecuteAsync(@"INSERT INTO EmployeePaymentDetails (EmployeeId,BankAccountNo,IfscCode)
VALUES (@EmployeeId,@BankAccountNo,@IfscCode)
ON DUPLICATE KEY UPDATE BankAccountNo=@BankAccountNo,IfscCode=@IfscCode", new { EmployeeId = employeeId, BankAccountNo = Text(root, "bankAccountNo", Text(root, "accountNumber")), IfscCode = Text(root, "ifscCode", Text(root, "ifsc")) });

    private static object ToDto(ComponentRow row) => new { row.Id, row.Code, row.ComponentType, row.Category, row.Name, row.PayType, row.CalculationType, value = row.ValueText, row.Formula, row.BaseComponent, row.Taxable, row.Ctc, row.ProRata, row.Fbp, row.RestrictFbp, row.Epf, row.Esi, row.Recurring, row.Scheduled, row.InvestmentType, row.CorrectionOf, row.Active, priority = row.Priority.ToString(CultureInfo.InvariantCulture) };
    private static object ToDto(StructureRow row, List<StructureLineRow> lines) => new { row.Id, clientId = string.IsNullOrWhiteSpace(row.ClientRef) ? row.ClientId.ToString(CultureInfo.InvariantCulture) : row.ClientRef, row.Name, annualCtc = row.AnnualCtc.ToString(CultureInfo.InvariantCulture), lines = lines.Select(x => new { x.ComponentId, value = x.ValueText }), row.Active };
    private static object ToDto(PayslipTemplateRow row) => new { row.Id, clientId = string.IsNullOrWhiteSpace(row.ClientRef) ? row.ClientId.ToString(CultureInfo.InvariantCulture) : row.ClientRef, row.Name, row.Theme, row.ShowLogo, row.ShowClient, row.ShowYtd, row.ShowBank, row.Note, row.Active };
    private static object ToDto(ClientScheduleRow row) => new { row.WorkWeek, row.SalaryDays, row.FixedDays, row.PayDay, row.FirstPayPeriod };

    private static JsonObject ParseRoot(string? json)
    {
        try { return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json)?.AsObject() ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }

    private static T? Parse<T>(string? json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch { return default; }
    }

    private static bool TryRead<T>(JsonNode? node, out T? value)
    {
        if (node is null) { value = default; return false; }
        try { value = node.Deserialize<T>(JsonOptions); return true; }
        catch { value = default; return false; }
    }

    private static Dictionary<string, decimal> ParseDecimalMap(string? json) => Parse<Dictionary<string, decimal>>(json) ?? [];
    private static string Text(JsonObject root, string key, string fallback = "") => root.TryGetPropertyValue(key, out var value) && value is not null ? value.ToString() : fallback;
    private static void Set(JsonObject root, string key, string value) { if (!string.IsNullOrWhiteSpace(value)) root[key] = value; }
    private static string Clean(string? value) => value?.Trim() ?? "";
    private static int RefId(string? value) => int.TryParse((value ?? "").Split(':')[0], out var id) ? id : 0;
    private static int ToInt(string? value, int fallback) => int.TryParse(Clean(value), NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : fallback;
    private static decimal ToDecimal(string? value) => decimal.TryParse(Clean(value), NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : 0;
    private static async Task<bool> TableExistsAsync(MySqlConnection connection, string tableName) =>
        await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA='payroll' AND TABLE_NAME=@tableName", new { tableName }) > 0;

    private class ComponentDto { public long Id { get; set; } public string Code { get; set; } = ""; public string ComponentType { get; set; } = ""; public string Category { get; set; } = "Earning"; public string Name { get; set; } = ""; public string PayType { get; set; } = "Fixed Pay"; public string CalculationType { get; set; } = "Fixed Amount"; public string Value { get; set; } = ""; public string Formula { get; set; } = ""; public string BaseComponent { get; set; } = ""; public bool Taxable { get; set; } = true; public bool Ctc { get; set; } = true; public bool ProRata { get; set; } = true; public bool Fbp { get; set; } public bool RestrictFbp { get; set; } public string Epf { get; set; } = "Never"; public bool Esi { get; set; } public bool Recurring { get; set; } = true; public bool Scheduled { get; set; } public string InvestmentType { get; set; } = ""; public string CorrectionOf { get; set; } = ""; public bool Active { get; set; } = true; public string Priority { get; set; } = "999"; }
    private sealed class StructureDto { public long Id { get; set; } public string ClientId { get; set; } = ""; public string Name { get; set; } = ""; public string AnnualCtc { get; set; } = "0"; public List<StructureLineDto> Lines { get; set; } = []; public bool Active { get; set; } = true; }
    private sealed class StructureLineDto { public string ComponentId { get; set; } = ""; public string Value { get; set; } = ""; }
    private sealed class PayslipTemplateDto { public long Id { get; set; } public string ClientId { get; set; } = ""; public string Name { get; set; } = ""; public string Theme { get; set; } = "Classic"; public bool ShowLogo { get; set; } = true; public bool ShowClient { get; set; } = true; public bool ShowYtd { get; set; } = true; public bool ShowBank { get; set; } = true; public string Note { get; set; } = ""; public bool Active { get; set; } = true; }
    private class ScheduleDto { public string WorkWeek { get; set; } = "Monday - Friday"; public string SalaryDays { get; set; } = "Actual days"; public string FixedDays { get; set; } = "30"; public string PayDay { get; set; } = "Last working day"; public string FirstPayPeriod { get; set; } = ""; }
    private class ClientScheduleDto : ScheduleDto { }
    private sealed class PayRunLineDto { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string Category { get; set; } = ""; public decimal MonthlyAmount { get; set; } public decimal Amount { get; set; } public bool ProRata { get; set; } }
    private sealed class ComponentRow { public long Id { get; set; } public string Code { get; set; } = ""; public string ComponentType { get; set; } = ""; public string Category { get; set; } = "Earning"; public string Name { get; set; } = ""; public string PayType { get; set; } = "Fixed Pay"; public string CalculationType { get; set; } = "Fixed Amount"; public string ValueText { get; set; } = ""; public string Formula { get; set; } = ""; public string BaseComponent { get; set; } = ""; public bool Taxable { get; set; } = true; public bool Ctc { get; set; } = true; public bool ProRata { get; set; } = true; public bool Fbp { get; set; } public bool RestrictFbp { get; set; } public string Epf { get; set; } = "Never"; public bool Esi { get; set; } public bool Recurring { get; set; } = true; public bool Scheduled { get; set; } public string InvestmentType { get; set; } = ""; public string CorrectionOf { get; set; } = ""; public bool Active { get; set; } = true; public int Priority { get; set; } = 999; }
    private sealed class StructureRow { public long Id { get; set; } public int ClientId { get; set; } public string ClientRef { get; set; } = ""; public string Name { get; set; } = ""; public decimal AnnualCtc { get; set; } public bool Active { get; set; } }
    private sealed class StructureLineRow { public long StructureId { get; set; } public string ComponentId { get; set; } = ""; public string ValueText { get; set; } = ""; }
    private sealed class PayslipTemplateRow { public long Id { get; set; } public int ClientId { get; set; } public string ClientRef { get; set; } = ""; public string Name { get; set; } = ""; public string Theme { get; set; } = "Classic"; public bool ShowLogo { get; set; } = true; public bool ShowClient { get; set; } = true; public bool ShowYtd { get; set; } = true; public bool ShowBank { get; set; } = true; public string Note { get; set; } = ""; public bool Active { get; set; } = true; }
    private sealed class ClientScheduleRow : ClientScheduleDto { public int ClientId { get; set; } }
    private sealed class EmployeeSalaryComponentRow { public int EmployeeId { get; set; } public string ComponentId { get; set; } = ""; public decimal Amount { get; set; } }
    private sealed class EmployeePersonalRow { public int EmployeeId { get; set; } public string DateOfBirth { get; set; } = ""; public string Mobile { get; set; } = ""; public string PanNumber { get; set; } = ""; public string AadhaarNumber { get; set; } = ""; public string UanNumber { get; set; } = ""; public string EsicNumber { get; set; } = ""; public string Source { get; set; } = ""; public string SourceLocation { get; set; } = ""; public string City { get; set; } = ""; public string District { get; set; } = ""; public string State { get; set; } = ""; public string RawDesignation { get; set; } = ""; public string OriginalEmployeeCode { get; set; } = ""; public string DuplicateResolution { get; set; } = ""; public int ExcelRow { get; set; } public decimal EsicEmployee { get; set; } public decimal PtLwfWorkmenComp { get; set; } public decimal Tds { get; set; } public decimal Recovery { get; set; } }
    private sealed class EmployeePaymentRow { public int EmployeeId { get; set; } public string BankAccountNo { get; set; } = ""; public string IfscCode { get; set; } = ""; }
}
