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
CREATE TABLE IF NOT EXISTS salarycomponents (
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
CREATE TABLE IF NOT EXISTS salarystructures (
    Id BIGINT PRIMARY KEY,
    ClientId INT NOT NULL DEFAULT 0,
    ClientRef VARCHAR(200) NOT NULL DEFAULT '',
    Name VARCHAR(200) NOT NULL DEFAULT '',
    AnnualCtc DECIMAL(18,2) NOT NULL DEFAULT 0,
    Active BOOLEAN NOT NULL DEFAULT TRUE,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);
CREATE TABLE IF NOT EXISTS salarystructurelines (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    StructureId BIGINT NOT NULL,
    ComponentId VARCHAR(80) NOT NULL,
    ValueText VARCHAR(1000) NOT NULL DEFAULT '',
    SortOrder INT NOT NULL DEFAULT 0,
    UNIQUE KEY UX_SalaryStructureLines_Structure_Component (StructureId, ComponentId)
);
CREATE TABLE IF NOT EXISTS paysliptemplates (
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
CREATE TABLE IF NOT EXISTS ProfessionalTaxSlabs (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    State VARCHAR(100) NOT NULL,
    SalaryFrom DECIMAL(18,2) NOT NULL DEFAULT 0,
    SalaryTo DECIMAL(18,2) NULL,
    DeductionAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
    EffectiveFrom DATE NULL,
    EffectiveTo DATE NULL,
    Gender VARCHAR(30) NOT NULL DEFAULT 'All',
    Notes VARCHAR(500) NOT NULL DEFAULT '',
    Active BOOLEAN NOT NULL DEFAULT TRUE,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    INDEX IX_ProfessionalTaxSlabs_State_Active (State, Active)
);
CREATE TABLE IF NOT EXISTS clientpayschedules (
    ClientId INT PRIMARY KEY,
    WorkWeek VARCHAR(80) NOT NULL DEFAULT 'Monday - Friday',
    SalaryDays VARCHAR(80) NOT NULL DEFAULT 'Actual days',
    FixedDays VARCHAR(10) NOT NULL DEFAULT '30',
    PayDay VARCHAR(80) NOT NULL DEFAULT 'Last working day',
    FirstPayPeriod VARCHAR(7) NOT NULL DEFAULT '',
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);
CREATE TABLE IF NOT EXISTS employeesalarycomponents (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    EmployeeId INT NOT NULL,
    ComponentId VARCHAR(80) NOT NULL,
    ComponentCode VARCHAR(80) NOT NULL DEFAULT '',
    Amount DECIMAL(18,4) NOT NULL DEFAULT 0,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_EmployeeSalaryComponents_Employee_Component (EmployeeId, ComponentId)
);
CREATE TABLE IF NOT EXISTS employeepersonaldetails (
    EmployeeId INT PRIMARY KEY,
    DateOfBirth VARCHAR(30) NOT NULL DEFAULT '',
    Mobile VARCHAR(50) NOT NULL DEFAULT '',
    PanNumber VARCHAR(50) NOT NULL DEFAULT '',
    AadhaarNumber VARCHAR(50) NOT NULL DEFAULT '',
    UanNumber VARCHAR(50) NOT NULL DEFAULT '',
    EsicNumber VARCHAR(50) NOT NULL DEFAULT '',
    Address VARCHAR(800) NOT NULL DEFAULT '',
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
CREATE TABLE IF NOT EXISTS employeepaymentdetails (
    EmployeeId INT PRIMARY KEY,
    BankName VARCHAR(160) NOT NULL DEFAULT '',
    BankAccountNo VARCHAR(100) NOT NULL DEFAULT '',
    IfscCode VARCHAR(40) NOT NULL DEFAULT '',
    PaymentMode VARCHAR(60) NOT NULL DEFAULT '',
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);
CREATE TABLE IF NOT EXISTS payrunemployeelines (
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

        await EnsureEmployeeDetailColumnsAsync(connection);
    }

    public static async Task<string> GetSetupJsonAsync(MySqlConnection connection, MySqlTransaction? transaction = null)
    {
        var fallback = await connection.ExecuteScalarAsync<string?>("SELECT SetupJson FROM payrollsetups ORDER BY Id LIMIT 1", transaction: transaction) ?? "{}";
        var root = ParseRoot(fallback);
        root.Remove("salaryStructures");
        root.Remove("payslipTemplates");

        var components = (await connection.QueryAsync<ComponentRow>("SELECT * FROM salarycomponents ORDER BY Priority, Id", transaction: transaction)).ToList();
        if (components.Count > 0) root["salaryComponents"] = JsonSerializer.SerializeToNode(components.Select(ToDto), JsonOptions);

        var structures = (await connection.QueryAsync<StructureRow>("SELECT * FROM salarystructures ORDER BY Id", transaction: transaction)).ToList();
        if (structures.Count > 0)
        {
            var lines = (await connection.QueryAsync<StructureLineRow>("SELECT * FROM salarystructurelines ORDER BY StructureId, SortOrder, Id", transaction: transaction)).GroupBy(x => x.StructureId).ToDictionary(g => g.Key, g => g.ToList());
            root["salaryStructures"] = JsonSerializer.SerializeToNode(structures.Select(row => ToDto(row, lines.GetValueOrDefault(row.Id) ?? [])), JsonOptions);
        }

        var templates = (await connection.QueryAsync<PayslipTemplateRow>("SELECT * FROM paysliptemplates ORDER BY Name", transaction: transaction)).ToList();
        if (templates.Count > 0) root["payslipTemplates"] = JsonSerializer.SerializeToNode(templates.Select(ToDto), JsonOptions);

        var ptSlabs = (await connection.QueryAsync<ProfessionalTaxSlabDto>(@"SELECT Id id, State state, CAST(SalaryFrom AS CHAR) salaryFrom, CAST(SalaryTo AS CHAR) salaryTo, CAST(DeductionAmount AS CHAR) deductionAmount, DATE_FORMAT(EffectiveFrom,'%Y-%m-%d') effectiveFrom, DATE_FORMAT(EffectiveTo,'%Y-%m-%d') effectiveTo, Gender gender, Notes notes, Active active FROM ProfessionalTaxSlabs ORDER BY State, SalaryFrom, Id", transaction: transaction)).ToList();
        if (ptSlabs.Count > 0)
        {
            var statutory = root["statutory"] as JsonObject ?? [];
            statutory["ptStateSlabs"] = JsonSerializer.SerializeToNode(ptSlabs, JsonOptions);
            root["statutory"] = statutory;
        }

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
        if (root["statutory"] is JsonObject statutory && TryRead(statutory["ptStateSlabs"], out List<ProfessionalTaxSlabDto>? ptSlabs))
        {
            await SaveProfessionalTaxSlabsAsync(connection, ptSlabs ?? []);
            statutory.Remove("ptStateSlabs");
            if (statutory["ptSlabs"] is not null)
                statutory.Remove("ptSlabs");
        }

        var retained = new JsonObject();
        if (root["tax"] is not null) retained["tax"] = root["tax"]!.DeepClone();
        if (root["schedule"] is not null) retained["schedule"] = root["schedule"]!.DeepClone();
        if (root["statutory"] is not null) retained["statutory"] = root["statutory"]!.DeepClone();
        var setupJson = retained.ToJsonString(JsonOptions);
        var id = await connection.ExecuteScalarAsync<int?>("SELECT Id FROM payrollsetups ORDER BY Id LIMIT 1");
        if (id is null)
            await connection.ExecuteAsync("INSERT INTO payrollsetups (SetupJson) VALUES (@setupJson)", new { setupJson });
        else
            await connection.ExecuteAsync("UPDATE payrollsetups SET SetupJson=@setupJson WHERE Id=@id", new { setupJson, id });
    }

    public static async Task SyncClientPayScheduleAsync(MySqlConnection connection, int clientId, string payScheduleJson)
    {
        if (clientId <= 0) return;
        var schedule = Parse<ClientScheduleDto>(payScheduleJson);
        if (schedule is null || string.IsNullOrWhiteSpace(payScheduleJson) || payScheduleJson.Trim() == "{}")
        {
            await connection.ExecuteAsync("DELETE FROM clientpayschedules WHERE ClientId=@clientId", new { clientId });
            return;
        }
        await connection.ExecuteAsync(@"INSERT INTO clientpayschedules (ClientId,WorkWeek,SalaryDays,FixedDays,PayDay,FirstPayPeriod)
VALUES (@ClientId,@WorkWeek,@SalaryDays,@FixedDays,@PayDay,@FirstPayPeriod)
ON DUPLICATE KEY UPDATE WorkWeek=@WorkWeek,SalaryDays=@SalaryDays,FixedDays=@FixedDays,PayDay=@PayDay,FirstPayPeriod=@FirstPayPeriod", new { ClientId = clientId, schedule.WorkWeek, schedule.SalaryDays, schedule.FixedDays, schedule.PayDay, schedule.FirstPayPeriod });
    }

    public static async Task ApplyClientPaySchedulesAsync(MySqlConnection connection, IEnumerable<Client> clients)
    {
        var list = clients.ToList();
        if (list.Count == 0) return;
        var schedules = (await connection.QueryAsync<ClientScheduleRow>("SELECT * FROM clientpayschedules WHERE ClientId IN @Ids", new { Ids = list.Select(x => x.Id).ToArray() })).ToDictionary(x => x.ClientId);
        foreach (var client in list)
            if (schedules.TryGetValue(client.Id, out var schedule))
                client.PayScheduleJson = JsonSerializer.Serialize(ToDto(schedule), JsonOptions);
    }

    public static async Task ApplyEmployeeTablesAsync(MySqlConnection connection, IEnumerable<Employee> employees)
    {
        var list = employees.ToList();
        if (list.Count == 0) return;
        var ids = list.Select(x => x.Id).ToArray();
        var salaryRows = (await connection.QueryAsync<EmployeeSalaryComponentRow>("SELECT EmployeeId,ComponentId,Amount FROM employeesalarycomponents WHERE EmployeeId IN @ids", new { ids })).GroupBy(x => x.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());
        var personalRows = (await connection.QueryAsync<EmployeePersonalRow>("SELECT * FROM employeepersonaldetails WHERE EmployeeId IN @ids", new { ids })).ToDictionary(x => x.EmployeeId);
        var paymentRows = (await connection.QueryAsync<EmployeePaymentRow>("SELECT * FROM employeepaymentdetails WHERE EmployeeId IN @ids", new { ids })).ToDictionary(x => x.EmployeeId);

        foreach (var employee in list)
        {
            if (salaryRows.TryGetValue(employee.Id, out var salaries) && salaries.Count > 0)
            {
                var salary = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in salaries) salary[row.ComponentId] = row.Amount;
                employee.SalaryComponents = salary;
                employee.SalaryJson = JsonSerializer.Serialize(salary, JsonOptions);
            }
            if (personalRows.TryGetValue(employee.Id, out var personal))
            {
                var root = ParseRoot(employee.PersonalJson);
                Set(root, "dob", personal.DateOfBirth); Set(root, "mobile", personal.Mobile); Set(root, "pan", personal.PanNumber); Set(root, "aadhaar", personal.AadhaarNumber);
                Set(root, "uan", personal.UanNumber); Set(root, "esic", personal.EsicNumber); Set(root, "address", personal.Address); Set(root, "source", personal.Source); Set(root, "sourceLocation", personal.SourceLocation);
                Set(root, "city", personal.City); Set(root, "district", personal.District); Set(root, "state", personal.State); Set(root, "rawDesignation", personal.RawDesignation);
                Set(root, "originalEmployeeCode", personal.OriginalEmployeeCode); Set(root, "duplicateResolution", personal.DuplicateResolution);
                root["excelRow"] = personal.ExcelRow; root["esicEmployee"] = personal.EsicEmployee; root["ptLwfWorkmenComp"] = personal.PtLwfWorkmenComp; root["tds"] = personal.Tds; root["recovery"] = personal.Recovery;
                employee.PersonalDetails = ToPersonalDetails(personal);
                employee.PersonalJson = root.ToJsonString(JsonOptions);
            }
            if (paymentRows.TryGetValue(employee.Id, out var payment))
            {
                var root = ParseRoot(employee.PaymentJson);
                Set(root, "bank", payment.BankName); Set(root, "bankName", payment.BankName); Set(root, "account", payment.BankAccountNo); Set(root, "bankAccountNo", payment.BankAccountNo); Set(root, "ifsc", payment.IfscCode); Set(root, "ifscCode", payment.IfscCode); Set(root, "mode", payment.PaymentMode); Set(root, "paymentMode", payment.PaymentMode);
                employee.PaymentDetails = ToPaymentDetails(payment);
                employee.PaymentJson = root.ToJsonString(JsonOptions);
            }
        }
    }

    public static async Task SyncEmployeeTablesAsync(MySqlConnection connection, Employee employee)
    {
        if (employee.Id <= 0) return;
        var salaryRows = employee.SalaryComponents.Count > 0 ? employee.SalaryComponents : ParseDecimalMap(employee.SalaryJson);
        var personal = HasPersonalDetails(employee.PersonalDetails) ? employee.PersonalDetails : ToPersonalDetails(ParseRoot(employee.PersonalJson));
        var payment = HasPaymentDetails(employee.PaymentDetails) ? employee.PaymentDetails : ToPaymentDetails(ParseRoot(employee.PaymentJson));
        await SaveEmployeeSalaryAsync(connection, employee.Id, salaryRows);
        await SaveEmployeePersonalAsync(connection, employee.Id, personal);
        await SaveEmployeePaymentAsync(connection, employee.Id, payment);
        employee.SalaryComponents = new Dictionary<string, decimal>(salaryRows, StringComparer.OrdinalIgnoreCase);
        employee.SalaryJson = JsonSerializer.Serialize(employee.SalaryComponents, JsonOptions);
        employee.PersonalDetails = personal;
        employee.PersonalJson = ToPersonalJson(personal).ToJsonString(JsonOptions);
        employee.PaymentDetails = payment;
        employee.PaymentJson = ToPaymentJson(payment).ToJsonString(JsonOptions);
    }

    public static async Task SyncPayRunEmployeeLinesAsync(MySqlConnection connection, MySqlTransaction? transaction, PayRunEmployee row)
    {
        if (row.Id <= 0) return;
        await connection.ExecuteAsync("DELETE FROM payrunemployeelines WHERE PayRunEmployeeId=@Id", new { row.Id }, transaction);
        var lines = Parse<List<PayRunLineDto>>(row.DetailsJson) ?? [];
        var order = 0;
        foreach (var line in lines.Where(x => !string.IsNullOrWhiteSpace(x.Id)))
        {
            await connection.ExecuteAsync(@"INSERT INTO payrunemployeelines (PayRunEmployeeId,PayRunId,EmployeeId,ComponentCode,Name,Category,MonthlyAmount,Amount,ProRata,SortOrder)
VALUES (@PayRunEmployeeId,@PayRunId,@EmployeeId,@ComponentCode,@Name,@Category,@MonthlyAmount,@Amount,@ProRata,@SortOrder)", new { PayRunEmployeeId = row.Id, row.PayRunId, row.EmployeeId, ComponentCode = line.Id, line.Name, line.Category, line.MonthlyAmount, line.Amount, line.ProRata, SortOrder = order++ }, transaction);
        }
    }

    private static async Task EnsureEmployeeDetailColumnsAsync(MySqlConnection connection)
    {
        await EnsureColumnAsync(connection, "employeepersonaldetails", "Address", "VARCHAR(800) NOT NULL DEFAULT '' AFTER EsicNumber");
        await EnsureColumnAsync(connection, "employeepaymentdetails", "BankName", "VARCHAR(160) NOT NULL DEFAULT '' AFTER EmployeeId");
        await EnsureColumnAsync(connection, "employeepaymentdetails", "PaymentMode", "VARCHAR(60) NOT NULL DEFAULT '' AFTER IfscCode");
    }

    private static async Task EnsureColumnAsync(MySqlConnection connection, string tableName, string columnName, string definition)
    {
        var exists = await connection.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM information_schema.columns
WHERE table_schema = DATABASE() AND table_name = @TableName AND column_name = @ColumnName", new { TableName = tableName, ColumnName = columnName });
        if (exists == 0) await connection.ExecuteAsync($"ALTER TABLE `{tableName}` ADD COLUMN `{columnName}` {definition}");
    }

    private static async Task SaveComponentsAsync(MySqlConnection connection, List<ComponentDto> rows)
    {
        var ids = rows.Where(x => x.Id > 0).Select(x => x.Id).Distinct().ToArray();
        if (ids.Length > 0) await connection.ExecuteAsync("DELETE FROM salarycomponents WHERE Id NOT IN @ids", new { ids }); else await connection.ExecuteAsync("DELETE FROM salarycomponents");
        foreach (var row in rows.Where(x => x.Id > 0))
            await connection.ExecuteAsync(@"INSERT INTO salarycomponents (Id,Code,ComponentType,Category,Name,PayType,CalculationType,ValueText,Formula,BaseComponent,Taxable,Ctc,ProRata,Fbp,RestrictFbp,Epf,Esi,Recurring,Scheduled,InvestmentType,CorrectionOf,Active,Priority)
VALUES (@Id,@Code,@ComponentType,@Category,@Name,@PayType,@CalculationType,@Value,@Formula,@BaseComponent,@Taxable,@Ctc,@ProRata,@Fbp,@RestrictFbp,@Epf,@Esi,@Recurring,@Scheduled,@InvestmentType,@CorrectionOf,@Active,@PriorityNumber)
ON DUPLICATE KEY UPDATE Code=@Code,ComponentType=@ComponentType,Category=@Category,Name=@Name,PayType=@PayType,CalculationType=@CalculationType,ValueText=@Value,Formula=@Formula,BaseComponent=@BaseComponent,Taxable=@Taxable,Ctc=@Ctc,ProRata=@ProRata,Fbp=@Fbp,RestrictFbp=@RestrictFbp,Epf=@Epf,Esi=@Esi,Recurring=@Recurring,Scheduled=@Scheduled,InvestmentType=@InvestmentType,CorrectionOf=@CorrectionOf,Active=@Active,Priority=@PriorityNumber", new { row.Id, Code = Clean(row.Code).ToUpperInvariant(), row.ComponentType, row.Category, row.Name, row.PayType, row.CalculationType, row.Value, row.Formula, row.BaseComponent, row.Taxable, row.Ctc, row.ProRata, row.Fbp, row.RestrictFbp, row.Epf, row.Esi, row.Recurring, row.Scheduled, row.InvestmentType, row.CorrectionOf, row.Active, PriorityNumber = ToInt(row.Priority, 999) });
    }

    private static async Task SaveStructuresAsync(MySqlConnection connection, List<StructureDto> rows)
    {
        var ids = rows.Where(x => x.Id > 0).Select(x => x.Id).Distinct().ToArray();
        if (ids.Length > 0) await connection.ExecuteAsync("DELETE FROM salarystructures WHERE Id NOT IN @ids", new { ids }); else await connection.ExecuteAsync("DELETE FROM salarystructures");
        await connection.ExecuteAsync("DELETE FROM salarystructurelines WHERE StructureId NOT IN (SELECT Id FROM salarystructures)");
        foreach (var row in rows.Where(x => x.Id > 0))
        {
            await connection.ExecuteAsync(@"INSERT INTO salarystructures (Id,ClientId,ClientRef,Name,AnnualCtc,Active)
VALUES (@Id,@ClientId,@ClientRef,@Name,@AnnualCtc,@Active)
ON DUPLICATE KEY UPDATE ClientId=@ClientId,ClientRef=@ClientRef,Name=@Name,AnnualCtc=@AnnualCtc,Active=@Active", new { row.Id, ClientId = RefId(row.ClientId), ClientRef = row.ClientId ?? "", row.Name, AnnualCtc = ToDecimal(row.AnnualCtc), row.Active });
            await connection.ExecuteAsync("DELETE FROM salarystructurelines WHERE StructureId=@Id", new { row.Id });
            var index = 0;
            foreach (var line in row.Lines)
                await connection.ExecuteAsync(@"INSERT INTO salarystructurelines (StructureId,ComponentId,ValueText,SortOrder) VALUES (@StructureId,@ComponentId,@ValueText,@SortOrder)", new { StructureId = row.Id, line.ComponentId, ValueText = line.Value ?? "", SortOrder = index++ });
        }
    }

    private static async Task SavePayslipTemplatesAsync(MySqlConnection connection, List<PayslipTemplateDto> rows)
    {
        var ids = rows.Where(x => x.Id > 0).Select(x => x.Id).Distinct().ToArray();
        if (ids.Length > 0) await connection.ExecuteAsync("DELETE FROM paysliptemplates WHERE Id NOT IN @ids", new { ids }); else await connection.ExecuteAsync("DELETE FROM paysliptemplates");
        foreach (var row in rows.Where(x => x.Id > 0))
            await connection.ExecuteAsync(@"INSERT INTO paysliptemplates (Id,ClientId,ClientRef,Name,Theme,ShowLogo,ShowClient,ShowYtd,ShowBank,Note,Active)
VALUES (@Id,@ClientId,@ClientRef,@Name,@Theme,@ShowLogo,@ShowClient,@ShowYtd,@ShowBank,@Note,@Active)
ON DUPLICATE KEY UPDATE ClientId=@ClientId,ClientRef=@ClientRef,Name=@Name,Theme=@Theme,ShowLogo=@ShowLogo,ShowClient=@ShowClient,ShowYtd=@ShowYtd,ShowBank=@ShowBank,Note=@Note,Active=@Active", new { row.Id, ClientId = RefId(row.ClientId), ClientRef = row.ClientId ?? "", row.Name, row.Theme, row.ShowLogo, row.ShowClient, row.ShowYtd, row.ShowBank, row.Note, row.Active });
    }

    private static async Task SaveProfessionalTaxSlabsAsync(MySqlConnection connection, List<ProfessionalTaxSlabDto> rows)
    {
        var ids = rows.Where(x => x.Id > 0).Select(x => x.Id).Distinct().ToArray();
        if (ids.Length > 0) await connection.ExecuteAsync("DELETE FROM ProfessionalTaxSlabs WHERE Id NOT IN @ids", new { ids }); else await connection.ExecuteAsync("DELETE FROM ProfessionalTaxSlabs");
        foreach (var row in rows.Where(x => !string.IsNullOrWhiteSpace(x.State)))
            await connection.ExecuteAsync(@"INSERT INTO ProfessionalTaxSlabs (Id,State,SalaryFrom,SalaryTo,DeductionAmount,EffectiveFrom,EffectiveTo,Gender,Notes,Active)
VALUES (NULLIF(@Id,0),@State,@SalaryFrom,@SalaryTo,@DeductionAmount,@EffectiveFrom,@EffectiveTo,@Gender,@Notes,@Active)
ON DUPLICATE KEY UPDATE State=@State,SalaryFrom=@SalaryFrom,SalaryTo=@SalaryTo,DeductionAmount=@DeductionAmount,EffectiveFrom=@EffectiveFrom,EffectiveTo=@EffectiveTo,Gender=@Gender,Notes=@Notes,Active=@Active", new
            {
                row.Id,
                State = row.State.Trim(),
                SalaryFrom = ToDecimal(row.SalaryFrom),
                SalaryTo = string.IsNullOrWhiteSpace(row.SalaryTo) ? (decimal?)null : ToDecimal(row.SalaryTo),
                DeductionAmount = ToDecimal(row.DeductionAmount),
                EffectiveFrom = DateOrNull(row.EffectiveFrom),
                EffectiveTo = DateOrNull(row.EffectiveTo),
                Gender = string.IsNullOrWhiteSpace(row.Gender) ? "All" : row.Gender.Trim(),
                Notes = row.Notes ?? "",
                row.Active
            });
    }

    private static async Task SaveEmployeeSalaryAsync(MySqlConnection connection, int employeeId, Dictionary<string, decimal> rows)
    {
        await connection.ExecuteAsync("DELETE FROM employeesalarycomponents WHERE EmployeeId=@employeeId", new { employeeId });
        foreach (var row in rows)
        {
            var code = await connection.ExecuteScalarAsync<string?>("SELECT Code FROM salarycomponents WHERE Id=@Id OR Code=@Id LIMIT 1", new { Id = row.Key });
            await connection.ExecuteAsync(@"INSERT INTO employeesalarycomponents (EmployeeId,ComponentId,ComponentCode,Amount)
VALUES (@employeeId,@ComponentId,@ComponentCode,@Amount)
ON DUPLICATE KEY UPDATE ComponentCode=@ComponentCode,Amount=@Amount", new { employeeId, ComponentId = row.Key, ComponentCode = code ?? row.Key, Amount = row.Value });
        }
    }

    private static Task SaveEmployeePersonalAsync(MySqlConnection connection, int employeeId, EmployeePersonalDetails personal) =>
        connection.ExecuteAsync(@"INSERT INTO employeepersonaldetails (EmployeeId,DateOfBirth,Mobile,PanNumber,AadhaarNumber,UanNumber,EsicNumber,Address,Source,SourceLocation,City,District,State,RawDesignation,OriginalEmployeeCode,DuplicateResolution,ExcelRow,EsicEmployee,PtLwfWorkmenComp,Tds,Recovery)
VALUES (@EmployeeId,@DateOfBirth,@Mobile,@PanNumber,@AadhaarNumber,@UanNumber,@EsicNumber,@Address,@Source,@SourceLocation,@City,@District,@State,@RawDesignation,@OriginalEmployeeCode,@DuplicateResolution,@ExcelRow,@EsicEmployee,@PtLwfWorkmenComp,@Tds,@Recovery)
ON DUPLICATE KEY UPDATE DateOfBirth=@DateOfBirth,Mobile=@Mobile,PanNumber=@PanNumber,AadhaarNumber=@AadhaarNumber,UanNumber=@UanNumber,EsicNumber=@EsicNumber,Address=@Address,Source=@Source,SourceLocation=@SourceLocation,City=@City,District=@District,State=@State,RawDesignation=@RawDesignation,OriginalEmployeeCode=@OriginalEmployeeCode,DuplicateResolution=@DuplicateResolution,ExcelRow=@ExcelRow,EsicEmployee=@EsicEmployee,PtLwfWorkmenComp=@PtLwfWorkmenComp,Tds=@Tds,Recovery=@Recovery", new
        {
            EmployeeId = employeeId,
            personal.DateOfBirth,
            personal.Mobile,
            personal.PanNumber,
            personal.AadhaarNumber,
            personal.UanNumber,
            personal.EsicNumber,
            personal.Address,
            personal.Source,
            personal.SourceLocation,
            personal.City,
            personal.District,
            personal.State,
            personal.RawDesignation,
            personal.OriginalEmployeeCode,
            personal.DuplicateResolution,
            personal.ExcelRow,
            personal.EsicEmployee,
            personal.PtLwfWorkmenComp,
            personal.Tds,
            personal.Recovery
        });

    private static Task SaveEmployeePaymentAsync(MySqlConnection connection, int employeeId, EmployeePaymentDetails payment) =>
        connection.ExecuteAsync(@"INSERT INTO employeepaymentdetails (EmployeeId,BankName,BankAccountNo,IfscCode,PaymentMode)
VALUES (@EmployeeId,@BankName,@BankAccountNo,@IfscCode,@PaymentMode)
ON DUPLICATE KEY UPDATE BankName=@BankName,BankAccountNo=@BankAccountNo,IfscCode=@IfscCode,PaymentMode=@PaymentMode", new { EmployeeId = employeeId, payment.BankName, payment.BankAccountNo, payment.IfscCode, payment.PaymentMode });

    private static object ToDto(ComponentRow row) => new { row.Id, row.Code, row.ComponentType, row.Category, row.Name, row.PayType, row.CalculationType, value = row.ValueText, row.Formula, row.BaseComponent, row.Taxable, row.Ctc, row.ProRata, row.Fbp, row.RestrictFbp, row.Epf, row.Esi, row.Recurring, row.Scheduled, row.InvestmentType, row.CorrectionOf, row.Active, priority = row.Priority.ToString(CultureInfo.InvariantCulture) };
    private static object ToDto(StructureRow row, List<StructureLineRow> lines) => new { row.Id, clientId = string.IsNullOrWhiteSpace(row.ClientRef) ? row.ClientId.ToString(CultureInfo.InvariantCulture) : row.ClientRef, row.Name, annualCtc = row.AnnualCtc.ToString(CultureInfo.InvariantCulture), lines = lines.Select(x => new { x.ComponentId, value = x.ValueText }), row.Active };
    private static object ToDto(PayslipTemplateRow row) => new { row.Id, clientId = string.IsNullOrWhiteSpace(row.ClientRef) ? row.ClientId.ToString(CultureInfo.InvariantCulture) : row.ClientRef, row.Name, row.Theme, row.ShowLogo, row.ShowClient, row.ShowYtd, row.ShowBank, row.Note, row.Active };
    private static object ToDto(ClientScheduleRow row) => new { row.WorkWeek, row.SalaryDays, row.FixedDays, row.PayDay, row.FirstPayPeriod };

    private static EmployeePersonalDetails ToPersonalDetails(EmployeePersonalRow row) => new()
    {
        DateOfBirth = row.DateOfBirth,
        Mobile = row.Mobile,
        PanNumber = row.PanNumber,
        AadhaarNumber = row.AadhaarNumber,
        UanNumber = row.UanNumber,
        EsicNumber = row.EsicNumber,
        Address = row.Address,
        Source = row.Source,
        SourceLocation = row.SourceLocation,
        City = row.City,
        District = row.District,
        State = row.State,
        RawDesignation = row.RawDesignation,
        OriginalEmployeeCode = row.OriginalEmployeeCode,
        DuplicateResolution = row.DuplicateResolution,
        ExcelRow = row.ExcelRow,
        EsicEmployee = row.EsicEmployee,
        PtLwfWorkmenComp = row.PtLwfWorkmenComp,
        Tds = row.Tds,
        Recovery = row.Recovery
    };

    private static EmployeePersonalDetails ToPersonalDetails(JsonObject root) => new()
    {
        DateOfBirth = Text(root, "dob", Text(root, "dateOfBirth")),
        Mobile = Text(root, "mobile"),
        PanNumber = Text(root, "pan", Text(root, "panNumber")),
        AadhaarNumber = Text(root, "aadhaar", Text(root, "aadhaarNumber")),
        UanNumber = Text(root, "uan", Text(root, "uanNumber")),
        EsicNumber = Text(root, "esic", Text(root, "esicNumber")),
        Address = Text(root, "address"),
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
    };

    private static EmployeePaymentDetails ToPaymentDetails(EmployeePaymentRow row) => new() { BankName = row.BankName, BankAccountNo = row.BankAccountNo, IfscCode = row.IfscCode, PaymentMode = row.PaymentMode };
    private static EmployeePaymentDetails ToPaymentDetails(JsonObject root) => new() { BankName = Text(root, "bankName", Text(root, "bank")), BankAccountNo = Text(root, "bankAccountNo", Text(root, "account", Text(root, "accountNumber"))), IfscCode = Text(root, "ifscCode", Text(root, "ifsc")), PaymentMode = Text(root, "paymentMode", Text(root, "mode")) };

    private static JsonObject ToPersonalJson(EmployeePersonalDetails personal) => new()
    {
        ["dob"] = personal.DateOfBirth,
        ["dateOfBirth"] = personal.DateOfBirth,
        ["mobile"] = personal.Mobile,
        ["pan"] = personal.PanNumber,
        ["panNumber"] = personal.PanNumber,
        ["aadhaar"] = personal.AadhaarNumber,
        ["aadhaarNumber"] = personal.AadhaarNumber,
        ["uan"] = personal.UanNumber,
        ["uanNumber"] = personal.UanNumber,
        ["esic"] = personal.EsicNumber,
        ["esicNumber"] = personal.EsicNumber,
        ["address"] = personal.Address,
        ["source"] = personal.Source,
        ["sourceLocation"] = personal.SourceLocation,
        ["city"] = personal.City,
        ["district"] = personal.District,
        ["state"] = personal.State,
        ["rawDesignation"] = personal.RawDesignation,
        ["originalEmployeeCode"] = personal.OriginalEmployeeCode,
        ["duplicateResolution"] = personal.DuplicateResolution,
        ["excelRow"] = personal.ExcelRow,
        ["esicEmployee"] = personal.EsicEmployee,
        ["ptLwfWorkmenComp"] = personal.PtLwfWorkmenComp,
        ["tds"] = personal.Tds,
        ["recovery"] = personal.Recovery
    };

    private static JsonObject ToPaymentJson(EmployeePaymentDetails payment) => new()
    {
        ["bank"] = payment.BankName,
        ["bankName"] = payment.BankName,
        ["account"] = payment.BankAccountNo,
        ["bankAccountNo"] = payment.BankAccountNo,
        ["ifsc"] = payment.IfscCode,
        ["ifscCode"] = payment.IfscCode,
        ["mode"] = payment.PaymentMode,
        ["paymentMode"] = payment.PaymentMode
    };

    private static bool HasPersonalDetails(EmployeePersonalDetails personal) =>
        !string.IsNullOrWhiteSpace(personal.DateOfBirth) || !string.IsNullOrWhiteSpace(personal.Mobile) || !string.IsNullOrWhiteSpace(personal.PanNumber) || !string.IsNullOrWhiteSpace(personal.AadhaarNumber) || !string.IsNullOrWhiteSpace(personal.UanNumber) || !string.IsNullOrWhiteSpace(personal.EsicNumber) || !string.IsNullOrWhiteSpace(personal.Address) || !string.IsNullOrWhiteSpace(personal.Source) || !string.IsNullOrWhiteSpace(personal.SourceLocation) || !string.IsNullOrWhiteSpace(personal.City) || !string.IsNullOrWhiteSpace(personal.District) || !string.IsNullOrWhiteSpace(personal.State) || !string.IsNullOrWhiteSpace(personal.RawDesignation) || !string.IsNullOrWhiteSpace(personal.OriginalEmployeeCode) || !string.IsNullOrWhiteSpace(personal.DuplicateResolution) || personal.ExcelRow != 0 || personal.EsicEmployee != 0 || personal.PtLwfWorkmenComp != 0 || personal.Tds != 0 || personal.Recovery != 0;

    private static bool HasPaymentDetails(EmployeePaymentDetails payment) =>
        !string.IsNullOrWhiteSpace(payment.BankName) || !string.IsNullOrWhiteSpace(payment.BankAccountNo) || !string.IsNullOrWhiteSpace(payment.IfscCode) || !string.IsNullOrWhiteSpace(payment.PaymentMode);

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
    private static DateTime? DateOrNull(string? value) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.Date : null;
    private static async Task<bool> TableExistsAsync(MySqlConnection connection, string tableName) =>
        await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA='payroll' AND TABLE_NAME=@tableName", new { tableName }) > 0;

    private class ComponentDto { public long Id { get; set; } public string Code { get; set; } = ""; public string ComponentType { get; set; } = ""; public string Category { get; set; } = "Earning"; public string Name { get; set; } = ""; public string PayType { get; set; } = "Fixed Pay"; public string CalculationType { get; set; } = "Fixed Amount"; public string Value { get; set; } = ""; public string Formula { get; set; } = ""; public string BaseComponent { get; set; } = ""; public bool Taxable { get; set; } = true; public bool Ctc { get; set; } = true; public bool ProRata { get; set; } = true; public bool Fbp { get; set; } public bool RestrictFbp { get; set; } public string Epf { get; set; } = "Never"; public bool Esi { get; set; } public bool Recurring { get; set; } = true; public bool Scheduled { get; set; } public string InvestmentType { get; set; } = ""; public string CorrectionOf { get; set; } = ""; public bool Active { get; set; } = true; public string Priority { get; set; } = "999"; }
    private sealed class StructureDto { public long Id { get; set; } public string ClientId { get; set; } = ""; public string Name { get; set; } = ""; public string AnnualCtc { get; set; } = "0"; public List<StructureLineDto> Lines { get; set; } = []; public bool Active { get; set; } = true; }
    private sealed class StructureLineDto { public string ComponentId { get; set; } = ""; public string Value { get; set; } = ""; }
    private sealed class PayslipTemplateDto { public long Id { get; set; } public string ClientId { get; set; } = ""; public string Name { get; set; } = ""; public string Theme { get; set; } = "Classic"; public bool ShowLogo { get; set; } = true; public bool ShowClient { get; set; } = true; public bool ShowYtd { get; set; } = true; public bool ShowBank { get; set; } = true; public string Note { get; set; } = ""; public bool Active { get; set; } = true; }
    private class ScheduleDto { public string WorkWeek { get; set; } = "Monday - Friday"; public string SalaryDays { get; set; } = "Actual days"; public string FixedDays { get; set; } = "30"; public string PayDay { get; set; } = "Last working day"; public string FirstPayPeriod { get; set; } = ""; }
    private sealed class ProfessionalTaxSlabDto { public long Id { get; set; } public string State { get; set; } = ""; public string SalaryFrom { get; set; } = "0"; public string SalaryTo { get; set; } = ""; public string DeductionAmount { get; set; } = ""; public string EffectiveFrom { get; set; } = ""; public string EffectiveTo { get; set; } = ""; public string Gender { get; set; } = "All"; public string Notes { get; set; } = ""; public bool Active { get; set; } = true; }
    private class ClientScheduleDto : ScheduleDto { }
    private sealed class PayRunLineDto { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string Category { get; set; } = ""; public decimal MonthlyAmount { get; set; } public decimal Amount { get; set; } public bool ProRata { get; set; } }
    private sealed class ComponentRow { public long Id { get; set; } public string Code { get; set; } = ""; public string ComponentType { get; set; } = ""; public string Category { get; set; } = "Earning"; public string Name { get; set; } = ""; public string PayType { get; set; } = "Fixed Pay"; public string CalculationType { get; set; } = "Fixed Amount"; public string ValueText { get; set; } = ""; public string Formula { get; set; } = ""; public string BaseComponent { get; set; } = ""; public bool Taxable { get; set; } = true; public bool Ctc { get; set; } = true; public bool ProRata { get; set; } = true; public bool Fbp { get; set; } public bool RestrictFbp { get; set; } public string Epf { get; set; } = "Never"; public bool Esi { get; set; } public bool Recurring { get; set; } = true; public bool Scheduled { get; set; } public string InvestmentType { get; set; } = ""; public string CorrectionOf { get; set; } = ""; public bool Active { get; set; } = true; public int Priority { get; set; } = 999; }
    private sealed class StructureRow { public long Id { get; set; } public int ClientId { get; set; } public string ClientRef { get; set; } = ""; public string Name { get; set; } = ""; public decimal AnnualCtc { get; set; } public bool Active { get; set; } }
    private sealed class StructureLineRow { public long StructureId { get; set; } public string ComponentId { get; set; } = ""; public string ValueText { get; set; } = ""; }
    private sealed class PayslipTemplateRow { public long Id { get; set; } public int ClientId { get; set; } public string ClientRef { get; set; } = ""; public string Name { get; set; } = ""; public string Theme { get; set; } = "Classic"; public bool ShowLogo { get; set; } = true; public bool ShowClient { get; set; } = true; public bool ShowYtd { get; set; } = true; public bool ShowBank { get; set; } = true; public string Note { get; set; } = ""; public bool Active { get; set; } = true; }
    private sealed class ClientScheduleRow : ClientScheduleDto { public int ClientId { get; set; } }
    private sealed class EmployeeSalaryComponentRow { public int EmployeeId { get; set; } public string ComponentId { get; set; } = ""; public decimal Amount { get; set; } }
    private sealed class EmployeePersonalRow { public int EmployeeId { get; set; } public string DateOfBirth { get; set; } = ""; public string Mobile { get; set; } = ""; public string PanNumber { get; set; } = ""; public string AadhaarNumber { get; set; } = ""; public string UanNumber { get; set; } = ""; public string EsicNumber { get; set; } = ""; public string Address { get; set; } = ""; public string Source { get; set; } = ""; public string SourceLocation { get; set; } = ""; public string City { get; set; } = ""; public string District { get; set; } = ""; public string State { get; set; } = ""; public string RawDesignation { get; set; } = ""; public string OriginalEmployeeCode { get; set; } = ""; public string DuplicateResolution { get; set; } = ""; public int ExcelRow { get; set; } public decimal EsicEmployee { get; set; } public decimal PtLwfWorkmenComp { get; set; } public decimal Tds { get; set; } public decimal Recovery { get; set; } }
    private sealed class EmployeePaymentRow { public int EmployeeId { get; set; } public string BankName { get; set; } = ""; public string BankAccountNo { get; set; } = ""; public string IfscCode { get; set; } = ""; public string PaymentMode { get; set; } = ""; }
}
