using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public class NotificationRepository(IConfiguration configuration, ILogger<NotificationRepository> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));

    public async Task InitializeAsync()
    {
        await using var db = Db();
        await db.OpenAsync();
        await db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS notification_smtp_settings (
    Id TINYINT PRIMARY KEY,
    IsEnabled BOOLEAN NOT NULL DEFAULT FALSE,
    Host VARCHAR(220) NOT NULL DEFAULT '',
    Port INT NOT NULL DEFAULT 587,
    UserName VARCHAR(220) NOT NULL DEFAULT '',
    Password VARCHAR(500) NOT NULL DEFAULT '',
    EnableSsl BOOLEAN NOT NULL DEFAULT TRUE,
    FromEmail VARCHAR(220) NOT NULL DEFAULT '',
    FromName VARCHAR(220) NOT NULL DEFAULT '',
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);
CREATE TABLE IF NOT EXISTS notification_templates (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    Code VARCHAR(120) NOT NULL,
    Name VARCHAR(220) NOT NULL,
    SubjectTemplate VARCHAR(500) NOT NULL,
    BodyTemplate MEDIUMTEXT NOT NULL,
    IsHtml BOOLEAN NOT NULL DEFAULT TRUE,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_NotificationTemplate_Code (Code),
    INDEX IX_NotificationTemplate_Active (IsActive)
);
CREATE TABLE IF NOT EXISTS notification_rules (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    Name VARCHAR(220) NOT NULL,
    EventCode VARCHAR(120) NOT NULL,
    ClientId INT NULL,
    TemplateId BIGINT NOT NULL,
    IsEnabled BOOLEAN NOT NULL DEFAULT TRUE,
    ConditionJson JSON NOT NULL,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    INDEX IX_NotificationRules_Event (EventCode, ClientId, IsEnabled)
);
CREATE TABLE IF NOT EXISTS notification_recipients (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    RuleId BIGINT NOT NULL,
    RecipientType VARCHAR(10) NOT NULL DEFAULT 'To',
    SourceType VARCHAR(40) NOT NULL DEFAULT 'StaticEmail',
    SourceValue VARCHAR(500) NOT NULL DEFAULT '',
    TableName VARCHAR(120) NOT NULL DEFAULT '',
    MatchColumn VARCHAR(120) NOT NULL DEFAULT '',
    MatchValueSource VARCHAR(120) NOT NULL DEFAULT 'resourceId',
    EmailColumn VARCHAR(120) NOT NULL DEFAULT '',
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    INDEX IX_NotificationRecipients_Rule (RuleId, IsActive)
);
CREATE TABLE IF NOT EXISTS notification_parameter_mappings (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    RuleId BIGINT NOT NULL,
    ParameterName VARCHAR(120) NOT NULL,
    SourceType VARCHAR(40) NOT NULL DEFAULT 'Payload',
    PayloadPath VARCHAR(200) NOT NULL DEFAULT '',
    TableName VARCHAR(120) NOT NULL DEFAULT '',
    MatchColumn VARCHAR(120) NOT NULL DEFAULT '',
    MatchValueSource VARCHAR(120) NOT NULL DEFAULT 'resourceId',
    ValueColumn VARCHAR(120) NOT NULL DEFAULT '',
    DefaultValue VARCHAR(1000) NOT NULL DEFAULT '',
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    INDEX IX_NotificationParameters_Rule (RuleId, IsActive)
);
CREATE TABLE IF NOT EXISTS notification_queue (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    RuleId BIGINT NULL,
    EventCode VARCHAR(120) NOT NULL,
    ResourceType VARCHAR(100) NOT NULL,
    ResourceId VARCHAR(120) NOT NULL,
    ClientId INT NULL,
    ToJson JSON NOT NULL,
    CcJson JSON NOT NULL,
    BccJson JSON NOT NULL,
    Subject VARCHAR(500) NOT NULL,
    BodyHtml MEDIUMTEXT NOT NULL,
    Status VARCHAR(30) NOT NULL DEFAULT 'Pending',
    RetryCount INT NOT NULL DEFAULT 0,
    ErrorMessage TEXT NULL,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    SentAt DATETIME NULL,
    INDEX IX_NotificationQueue_Status (Status, CreatedAt),
    INDEX IX_NotificationQueue_Resource (ResourceType, ResourceId)
);
CREATE TABLE IF NOT EXISTS notification_logs (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    QueueId BIGINT NOT NULL,
    EventCode VARCHAR(120) NOT NULL,
    ResourceType VARCHAR(100) NOT NULL,
    ResourceId VARCHAR(120) NOT NULL,
    Recipient VARCHAR(220) NOT NULL,
    Status VARCHAR(30) NOT NULL,
    ProviderMessageId VARCHAR(220) NOT NULL DEFAULT '',
    ErrorMessage TEXT NULL,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX IX_NotificationLogs_Queue (QueueId),
    INDEX IX_NotificationLogs_Resource (ResourceType, ResourceId)
);");
        await db.ExecuteAsync(@"INSERT INTO notification_smtp_settings (Id,IsEnabled,Host,Port,UserName,Password,EnableSsl,FromEmail,FromName)
VALUES (1,FALSE,'',587,'','',TRUE,'','')
ON DUPLICATE KEY UPDATE Id=Id;");
        await EnsureDefaultsAsync(db);
    }

    public async Task<NotificationSetup> GetSetupAsync()
    {
        await using var db = Db();
        await db.OpenAsync();
        var setup = new NotificationSetup
        {
            Smtp = await db.QueryFirstOrDefaultAsync<NotificationSmtpSetting>("SELECT * FROM notification_smtp_settings WHERE Id=1") ?? new NotificationSmtpSetting(),
            Templates = (await db.QueryAsync<NotificationTemplate>("SELECT * FROM notification_templates ORDER BY IsActive DESC, Name")).ToList(),
            Rules = (await db.QueryAsync<NotificationRule>(@"SELECT r.*,COALESCE(c.Name,'All clients') ClientName,COALESCE(t.Name,'') TemplateName
FROM notification_rules r LEFT JOIN clients c ON c.Id=r.ClientId LEFT JOIN notification_templates t ON t.Id=r.TemplateId ORDER BY r.IsEnabled DESC,r.EventCode,r.Name")).ToList(),
            Queue = (await db.QueryAsync<NotificationQueueItem>("SELECT * FROM notification_queue ORDER BY CreatedAt DESC LIMIT 200")).ToList(),
            Logs = (await db.QueryAsync<NotificationLog>("SELECT * FROM notification_logs ORDER BY CreatedAt DESC LIMIT 300")).ToList()
        };
        var recipients = (await db.QueryAsync<NotificationRecipient>("SELECT * FROM notification_recipients ORDER BY RecipientType,Id")).ToList();
        var parameters = (await db.QueryAsync<NotificationParameterMapping>("SELECT * FROM notification_parameter_mappings ORDER BY ParameterName,Id")).ToList();
        foreach (var rule in setup.Rules)
        {
            rule.Recipients = recipients.Where(item => item.RuleId == rule.Id).ToList();
            rule.Parameters = parameters.Where(item => item.RuleId == rule.Id).ToList();
        }
        return setup;
    }

    public async Task<NotificationSmtpSetting> SaveSmtpAsync(NotificationSmtpSetting request)
    {
        await using var db = Db();
        await db.OpenAsync();
        await db.ExecuteAsync(@"INSERT INTO notification_smtp_settings (Id,IsEnabled,Host,Port,UserName,Password,EnableSsl,FromEmail,FromName)
VALUES (1,@IsEnabled,@Host,@Port,@UserName,@Password,@EnableSsl,@FromEmail,@FromName)
ON DUPLICATE KEY UPDATE IsEnabled=@IsEnabled,Host=@Host,Port=@Port,UserName=@UserName,Password=@Password,EnableSsl=@EnableSsl,FromEmail=@FromEmail,FromName=@FromName", NormalizeSmtp(request));
        return await db.QuerySingleAsync<NotificationSmtpSetting>("SELECT * FROM notification_smtp_settings WHERE Id=1");
    }

    public async Task<NotificationTemplate> SaveTemplateAsync(NotificationTemplate request)
    {
        await using var db = Db();
        await db.OpenAsync();
        var data = new { request.Id, Code = CleanCode(request.Code), Name = request.Name.Trim(), request.SubjectTemplate, request.BodyTemplate, request.IsHtml, request.IsActive };
        if (string.IsNullOrWhiteSpace(data.Code) || string.IsNullOrWhiteSpace(data.Name) || string.IsNullOrWhiteSpace(data.SubjectTemplate))
            throw new InvalidOperationException("Template code, name, and subject are required.");
        if (request.Id == 0)
        {
            var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO notification_templates (Code,Name,SubjectTemplate,BodyTemplate,IsHtml,IsActive)
VALUES (@Code,@Name,@SubjectTemplate,@BodyTemplate,@IsHtml,@IsActive)
ON DUPLICATE KEY UPDATE Name=VALUES(Name),SubjectTemplate=VALUES(SubjectTemplate),BodyTemplate=VALUES(BodyTemplate),IsHtml=VALUES(IsHtml),IsActive=VALUES(IsActive);
SELECT Id FROM notification_templates WHERE Code=@Code;", data);
            request.Id = id;
        }
        else
        {
            await db.ExecuteAsync(@"UPDATE notification_templates SET Code=@Code,Name=@Name,SubjectTemplate=@SubjectTemplate,BodyTemplate=@BodyTemplate,IsHtml=@IsHtml,IsActive=@IsActive WHERE Id=@Id", data);
        }
        return await db.QuerySingleAsync<NotificationTemplate>("SELECT * FROM notification_templates WHERE Id=@Id", new { request.Id });
    }

    public async Task<NotificationRule> SaveRuleAsync(NotificationRule request)
    {
        await using var db = Db();
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        var data = new { request.Id, Name = request.Name.Trim(), EventCode = CleanCode(request.EventCode), request.ClientId, request.TemplateId, request.IsEnabled, ConditionJson = string.IsNullOrWhiteSpace(request.ConditionJson) ? "{}" : request.ConditionJson };
        if (string.IsNullOrWhiteSpace(data.Name) || string.IsNullOrWhiteSpace(data.EventCode) || data.TemplateId <= 0)
            throw new InvalidOperationException("Rule name, event, and template are required.");
        if (request.Id == 0)
            request.Id = await db.ExecuteScalarAsync<long>("INSERT INTO notification_rules (Name,EventCode,ClientId,TemplateId,IsEnabled,ConditionJson) VALUES (@Name,@EventCode,@ClientId,@TemplateId,@IsEnabled,@ConditionJson);SELECT LAST_INSERT_ID();", data, tx);
        else
            await db.ExecuteAsync("UPDATE notification_rules SET Name=@Name,EventCode=@EventCode,ClientId=@ClientId,TemplateId=@TemplateId,IsEnabled=@IsEnabled,ConditionJson=@ConditionJson WHERE Id=@Id", data, tx);

        await db.ExecuteAsync("DELETE FROM notification_recipients WHERE RuleId=@RuleId;DELETE FROM notification_parameter_mappings WHERE RuleId=@RuleId;", new { RuleId = request.Id }, tx);
        foreach (var recipient in request.Recipients.Where(item => item.IsActive))
            await db.ExecuteAsync(@"INSERT INTO notification_recipients (RuleId,RecipientType,SourceType,SourceValue,TableName,MatchColumn,MatchValueSource,EmailColumn,IsActive)
VALUES (@RuleId,@RecipientType,@SourceType,@SourceValue,@TableName,@MatchColumn,@MatchValueSource,@EmailColumn,@IsActive)", new { RuleId = request.Id, RecipientType = CleanRecipientType(recipient.RecipientType), recipient.SourceType, recipient.SourceValue, recipient.TableName, recipient.MatchColumn, recipient.MatchValueSource, recipient.EmailColumn, recipient.IsActive }, tx);
        foreach (var parameter in request.Parameters.Where(item => item.IsActive && !string.IsNullOrWhiteSpace(item.ParameterName)))
            await db.ExecuteAsync(@"INSERT INTO notification_parameter_mappings (RuleId,ParameterName,SourceType,PayloadPath,TableName,MatchColumn,MatchValueSource,ValueColumn,DefaultValue,IsActive)
VALUES (@RuleId,@ParameterName,@SourceType,@PayloadPath,@TableName,@MatchColumn,@MatchValueSource,@ValueColumn,@DefaultValue,@IsActive)", new { RuleId = request.Id, ParameterName = parameter.ParameterName.Trim(), parameter.SourceType, parameter.PayloadPath, parameter.TableName, parameter.MatchColumn, parameter.MatchValueSource, parameter.ValueColumn, parameter.DefaultValue, parameter.IsActive }, tx);
        await tx.CommitAsync();
        return (await GetSetupAsync()).Rules.First(item => item.Id == request.Id);
    }

    public async Task PublishEventAsync(NotificationEvent notificationEvent)
    {
        if (string.IsNullOrWhiteSpace(notificationEvent.EventCode) || string.IsNullOrWhiteSpace(notificationEvent.ResourceType) || string.IsNullOrWhiteSpace(notificationEvent.ResourceId))
            return;
        try
        {
            await using var db = Db();
            await db.OpenAsync();
            var rules = (await db.QueryAsync<NotificationRule>(@"SELECT r.*,COALESCE(c.Name,'All clients') ClientName,COALESCE(t.Name,'') TemplateName
FROM notification_rules r LEFT JOIN clients c ON c.Id=r.ClientId LEFT JOIN notification_templates t ON t.Id=r.TemplateId
WHERE r.IsEnabled=TRUE AND r.EventCode=@EventCode AND (r.ClientId IS NULL OR r.ClientId=@ClientId)
ORDER BY r.ClientId IS NULL, r.Id", new { EventCode = CleanCode(notificationEvent.EventCode), notificationEvent.ClientId })).ToList();
            if (rules.Count == 0) return;
            var recipients = (await db.QueryAsync<NotificationRecipient>("SELECT * FROM notification_recipients WHERE RuleId IN @Ids AND IsActive=TRUE", new { Ids = rules.Select(item => item.Id).ToArray() })).ToList();
            var parameters = (await db.QueryAsync<NotificationParameterMapping>("SELECT * FROM notification_parameter_mappings WHERE RuleId IN @Ids AND IsActive=TRUE", new { Ids = rules.Select(item => item.Id).ToArray() })).ToList();
            var templates = (await db.QueryAsync<NotificationTemplate>("SELECT * FROM notification_templates WHERE Id IN @Ids AND IsActive=TRUE", new { Ids = rules.Select(item => item.TemplateId).Distinct().ToArray() })).ToDictionary(item => item.Id);
            foreach (var rule in rules)
            {
                if (!templates.TryGetValue(rule.TemplateId, out var template)) continue;
                rule.Recipients = recipients.Where(item => item.RuleId == rule.Id).ToList();
                rule.Parameters = parameters.Where(item => item.RuleId == rule.Id).ToList();
                await QueueRuleAsync(db, notificationEvent, rule, template);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Notification event publish failed for {EventCode} {ResourceType} {ResourceId}.", notificationEvent.EventCode, notificationEvent.ResourceType, notificationEvent.ResourceId);
        }
    }

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        await using var db = Db();
        await db.OpenAsync(cancellationToken);
        var smtp = await db.QueryFirstOrDefaultAsync<NotificationSmtpSetting>("SELECT * FROM notification_smtp_settings WHERE Id=1") ?? new NotificationSmtpSetting();
        var rows = (await db.QueryAsync<NotificationQueueItem>("SELECT * FROM notification_queue WHERE Status IN ('Pending','Retry') AND RetryCount < 5 ORDER BY CreatedAt LIMIT 20")).ToList();
        var count = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!smtp.IsEnabled) throw new InvalidOperationException("SMTP is disabled. Configure Settings / Notifications / SMTP.");
                await SendAsync(smtp, row, cancellationToken);
                await db.ExecuteAsync("UPDATE notification_queue SET Status='Sent',SentAt=UTC_TIMESTAMP(),ErrorMessage='' WHERE Id=@Id", new { row.Id });
                await WriteLogsAsync(db, row, "Sent", "");
                count++;
            }
            catch (Exception exception)
            {
                var status = row.RetryCount + 1 >= 5 ? "Failed" : "Retry";
                await db.ExecuteAsync("UPDATE notification_queue SET Status=@Status,RetryCount=RetryCount+1,ErrorMessage=@Error WHERE Id=@Id", new { row.Id, Status = status, Error = exception.Message });
                await WriteLogsAsync(db, row, status, exception.Message);
                logger.LogWarning(exception, "Queued notification {QueueId} failed.", row.Id);
            }
        }
        return count;
    }

    public async Task RetryAsync(long id)
    {
        await using var db = Db();
        await db.OpenAsync();
        await db.ExecuteAsync("UPDATE notification_queue SET Status='Pending',RetryCount=0,ErrorMessage='' WHERE Id=@Id", new { Id = id });
    }

    public async Task QueueTestAsync(NotificationTestRequest request, int actorUserId)
    {
        await using var db = Db();
        await db.OpenAsync();
        var rule = await db.QueryFirstOrDefaultAsync<NotificationRule>("SELECT * FROM notification_rules WHERE Id=@RuleId", request);
        if (rule is null) throw new InvalidOperationException("Notification rule not found.");
        rule.Recipients = [new NotificationRecipient { RecipientType = "To", SourceType = "StaticEmail", SourceValue = request.ToEmail, IsActive = true }];
        rule.Parameters = (await db.QueryAsync<NotificationParameterMapping>("SELECT * FROM notification_parameter_mappings WHERE RuleId=@RuleId AND IsActive=TRUE", request)).ToList();
        var template = await db.QueryFirstAsync<NotificationTemplate>("SELECT * FROM notification_templates WHERE Id=@TemplateId", new { rule.TemplateId });
        await QueueRuleAsync(db, new NotificationEvent { EventCode = rule.EventCode, ResourceType = "Test", ResourceId = "TEST", ActorUserId = actorUserId, ActorName = "Test User", ActorEmail = request.ToEmail, PayloadJson = "{\"test\":\"true\"}" }, rule, template);
    }

    private async Task QueueRuleAsync(MySqlConnection db, NotificationEvent evt, NotificationRule rule, NotificationTemplate template)
    {
        var values = BuildBaseValues(evt);
        foreach (var mapping in rule.Parameters)
            values[mapping.ParameterName] = await ResolveParameterAsync(db, mapping, evt, values);
        var to = await ResolveRecipientsAsync(db, rule.Recipients.Where(item => item.RecipientType.Equals("To", StringComparison.OrdinalIgnoreCase)), evt, values);
        var cc = await ResolveRecipientsAsync(db, rule.Recipients.Where(item => item.RecipientType.Equals("Cc", StringComparison.OrdinalIgnoreCase)), evt, values);
        var bcc = await ResolveRecipientsAsync(db, rule.Recipients.Where(item => item.RecipientType.Equals("Bcc", StringComparison.OrdinalIgnoreCase)), evt, values);
        if (to.Count == 0 && cc.Count == 0 && bcc.Count == 0)
        {
            await db.ExecuteAsync(@"INSERT INTO notification_queue (RuleId,EventCode,ResourceType,ResourceId,ClientId,ToJson,CcJson,BccJson,Subject,BodyHtml,Status,ErrorMessage)
VALUES (@RuleId,@EventCode,@ResourceType,@ResourceId,@ClientId,'[]','[]','[]',@Subject,@BodyHtml,'Failed','No recipients resolved.')", new { RuleId = rule.Id, evt.EventCode, evt.ResourceType, evt.ResourceId, evt.ClientId, Subject = Render(template.SubjectTemplate, values), BodyHtml = Render(template.BodyTemplate, values) });
            return;
        }
        await db.ExecuteAsync(@"INSERT INTO notification_queue (RuleId,EventCode,ResourceType,ResourceId,ClientId,ToJson,CcJson,BccJson,Subject,BodyHtml,Status)
VALUES (@RuleId,@EventCode,@ResourceType,@ResourceId,@ClientId,@ToJson,@CcJson,@BccJson,@Subject,@BodyHtml,'Pending')", new { RuleId = rule.Id, evt.EventCode, evt.ResourceType, evt.ResourceId, evt.ClientId, ToJson = JsonSerializer.Serialize(to), CcJson = JsonSerializer.Serialize(cc), BccJson = JsonSerializer.Serialize(bcc), Subject = Render(template.SubjectTemplate, values), BodyHtml = Render(template.BodyTemplate, values) });
    }

    private Dictionary<string, string> BuildBaseValues(NotificationEvent evt)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["eventCode"] = evt.EventCode,
            ["resourceType"] = evt.ResourceType,
            ["resourceId"] = evt.ResourceId,
            ["clientId"] = evt.ClientId?.ToString() ?? "",
            ["actorName"] = evt.ActorName,
            ["actorEmail"] = evt.ActorEmail,
            ["requestedBy"] = evt.ActorName,
            ["requestedByEmail"] = evt.ActorEmail,
            ["now"] = DateTime.Now.ToString("dd-MM-yyyy HH:mm")
        };
        using var document = ParseJson(evt.PayloadJson);
        if (document is not null) FlattenJson(document.RootElement, "", values);
        return values;
    }

    private async Task<string> ResolveParameterAsync(MySqlConnection db, NotificationParameterMapping mapping, NotificationEvent evt, Dictionary<string, string> values)
    {
        if (mapping.SourceType.Equals("Payload", StringComparison.OrdinalIgnoreCase))
            return ValueFromDictionary(values, mapping.PayloadPath, mapping.DefaultValue);
        if (!mapping.SourceType.Equals("Lookup", StringComparison.OrdinalIgnoreCase)) return mapping.DefaultValue;
        if (!Safe(mapping.TableName) || !Safe(mapping.MatchColumn) || !Safe(mapping.ValueColumn)) return mapping.DefaultValue;
        var matchValue = ResolveMatchValue(mapping.MatchValueSource, evt, values);
        if (string.IsNullOrWhiteSpace(matchValue)) return mapping.DefaultValue;
        return await db.ExecuteScalarAsync<string?>($"SELECT `{mapping.ValueColumn}` FROM `{mapping.TableName}` WHERE `{mapping.MatchColumn}`=@MatchValue LIMIT 1", new { MatchValue = matchValue }) ?? mapping.DefaultValue;
    }

    private async Task<List<string>> ResolveRecipientsAsync(MySqlConnection db, IEnumerable<NotificationRecipient> rows, NotificationEvent evt, Dictionary<string, string> values)
    {
        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.SourceType.Equals("StaticEmail", StringComparison.OrdinalIgnoreCase)) AddEmails(emails, row.SourceValue);
            else if (row.SourceType.Equals("RequestorEmail", StringComparison.OrdinalIgnoreCase)) AddEmails(emails, evt.ActorEmail);
            else if (row.SourceType.Equals("PayloadEmail", StringComparison.OrdinalIgnoreCase)) AddEmails(emails, ValueFromDictionary(values, row.SourceValue, ""));
            else if (row.SourceType.Equals("UserRole", StringComparison.OrdinalIgnoreCase))
            {
                var roleEmails = await db.QueryAsync<string>(@"SELECT DISTINCT u.Email FROM authusers u JOIN authuserroles ur ON ur.UserId=u.Id JOIN authroles r ON r.Id=ur.RoleId
WHERE u.IsActive=TRUE AND (r.Code=@Role OR r.Name=@Role) AND (@ClientId IS NULL OR u.ClientId IS NULL OR u.ClientId=@ClientId)", new { Role = row.SourceValue, evt.ClientId });
                foreach (var email in roleEmails) AddEmails(emails, email);
            }
            else if (row.SourceType.Equals("ReportingManager", StringComparison.OrdinalIgnoreCase))
            {
                var email = await ResolveReportingManagerEmailAsync(db, evt);
                AddEmails(emails, email ?? "");
            }
            else if (row.SourceType.Equals("Lookup", StringComparison.OrdinalIgnoreCase) && Safe(row.TableName) && Safe(row.MatchColumn) && Safe(row.EmailColumn))
            {
                var matchValue = ResolveMatchValue(row.MatchValueSource, evt, values);
                if (!string.IsNullOrWhiteSpace(matchValue))
                {
                    var email = await db.ExecuteScalarAsync<string?>($"SELECT `{row.EmailColumn}` FROM `{row.TableName}` WHERE `{row.MatchColumn}`=@MatchValue LIMIT 1", new { MatchValue = matchValue });
                    AddEmails(emails, email ?? "");
                }
            }
        }
        return emails.OrderBy(item => item).ToList();
    }

    private static async Task<string?> ResolveReportingManagerEmailAsync(MySqlConnection db, NotificationEvent evt)
    {
        if (evt.ResourceType.Equals("LeaveRequest", StringComparison.OrdinalIgnoreCase))
            return await db.ExecuteScalarAsync<string?>(@"SELECT managerUser.Email
FROM essleaverequests request
JOIN employees employee ON employee.Id=request.EmployeeId
JOIN authusers managerUser ON managerUser.IsActive=TRUE
LEFT JOIN employees manager ON manager.Id=employee.ReportingManagerId
WHERE request.Id=@ResourceId
  AND (managerUser.Id=employee.ReportingManagerUserId OR (COALESCE(employee.ReportingManagerUserId,0)=0 AND managerUser.EmployeeId=manager.Id))
  AND managerUser.Email <> ''
ORDER BY managerUser.Id
LIMIT 1", new { evt.ResourceId });

        if (evt.ResourceType.Equals("ExpenseClaim", StringComparison.OrdinalIgnoreCase))
            return await db.ExecuteScalarAsync<string?>(@"SELECT managerUser.Email
FROM ess_expense_claims request
JOIN employees employee ON employee.Id=request.EmployeeId
JOIN authusers managerUser ON managerUser.IsActive=TRUE
LEFT JOIN employees manager ON manager.Id=employee.ReportingManagerId
WHERE request.Id=@ResourceId
  AND (managerUser.Id=employee.ReportingManagerUserId OR (COALESCE(employee.ReportingManagerUserId,0)=0 AND managerUser.EmployeeId=manager.Id))
  AND managerUser.Email <> ''
ORDER BY managerUser.Id
LIMIT 1", new { evt.ResourceId });

        if (evt.ResourceType.Equals("Employee", StringComparison.OrdinalIgnoreCase) || evt.ResourceType.Equals("EmployeeAction", StringComparison.OrdinalIgnoreCase))
            return await db.ExecuteScalarAsync<string?>(@"SELECT managerUser.Email
FROM employees employee
JOIN authusers managerUser ON managerUser.IsActive=TRUE
LEFT JOIN employees manager ON manager.Id=employee.ReportingManagerId
WHERE employee.Id=@ResourceId
  AND (managerUser.Id=employee.ReportingManagerUserId OR (COALESCE(employee.ReportingManagerUserId,0)=0 AND managerUser.EmployeeId=manager.Id))
  AND managerUser.Email <> ''
ORDER BY managerUser.Id
LIMIT 1", new { evt.ResourceId });

        return null;
    }

    private static string ResolveMatchValue(string source, NotificationEvent evt, Dictionary<string, string> values)
    {
        if (source.Equals("resourceId", StringComparison.OrdinalIgnoreCase)) return evt.ResourceId;
        if (source.Equals("clientId", StringComparison.OrdinalIgnoreCase)) return evt.ClientId?.ToString() ?? "";
        return ValueFromDictionary(values, source, "");
    }

    private static string Render(string template, Dictionary<string, string> values) =>
        Regex.Replace(template ?? "", @"\{\{\s*([A-Za-z0-9_.-]+)\s*\}\}", match => WebUtility.HtmlEncode(ValueFromDictionary(values, match.Groups[1].Value, "")));

    private static string ValueFromDictionary(Dictionary<string, string> values, string key, string fallback) =>
        string.IsNullOrWhiteSpace(key) ? fallback : values.TryGetValue(key, out var value) ? value : fallback;

    private static void FlattenJson(JsonElement element, string prefix, Dictionary<string, string> values)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        foreach (var property in element.EnumerateObject())
        {
            var key = string.IsNullOrWhiteSpace(prefix) ? property.Name : $"{prefix}.{property.Name}";
            if (property.Value.ValueKind == JsonValueKind.Object) FlattenJson(property.Value, key, values);
            else values[key] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.GetRawText();
        }
    }

    private static JsonDocument? ParseJson(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json); }
        catch { return null; }
    }

    private static async Task SendAsync(NotificationSmtpSetting smtp, NotificationQueueItem row, CancellationToken cancellationToken)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(string.IsNullOrWhiteSpace(smtp.FromName) ? smtp.FromEmail : smtp.FromName, smtp.FromEmail));
        foreach (var email in ReadEmailArray(row.ToJson)) message.To.Add(MailboxAddress.Parse(email));
        foreach (var email in ReadEmailArray(row.CcJson)) message.Cc.Add(MailboxAddress.Parse(email));
        foreach (var email in ReadEmailArray(row.BccJson)) message.Bcc.Add(MailboxAddress.Parse(email));
        if (message.To.Count == 0 && message.Cc.Count == 0 && message.Bcc.Count == 0) throw new InvalidOperationException("No recipients found.");
        message.Subject = row.Subject;
        message.Body = new BodyBuilder { HtmlBody = row.BodyHtml, TextBody = StripHtml(row.BodyHtml) }.ToMessageBody();

        using var client = new SmtpClient();
        var socketOptions = smtp.EnableSsl
            ? smtp.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls
            : SecureSocketOptions.None;
        await client.ConnectAsync(smtp.Host, smtp.Port, socketOptions, cancellationToken);
        if (!string.IsNullOrWhiteSpace(smtp.UserName))
            await client.AuthenticateAsync(smtp.UserName, smtp.Password, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    private static string StripHtml(string html) =>
        WebUtility.HtmlDecode(Regex.Replace(html ?? "", "<[^>]+>", " ")).Trim();

    private static async Task WriteLogsAsync(MySqlConnection db, NotificationQueueItem row, string status, string error)
    {
        foreach (var email in ReadEmailArray(row.ToJson).Concat(ReadEmailArray(row.CcJson)).Concat(ReadEmailArray(row.BccJson)).Distinct(StringComparer.OrdinalIgnoreCase))
            await db.ExecuteAsync(@"INSERT INTO notification_logs (QueueId,EventCode,ResourceType,ResourceId,Recipient,Status,ErrorMessage)
VALUES (@QueueId,@EventCode,@ResourceType,@ResourceId,@Recipient,@Status,@Error)", new { QueueId = row.Id, row.EventCode, row.ResourceType, row.ResourceId, Recipient = email, Status = status, Error = error });
    }

    private static List<string> ReadEmailArray(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? []; }
        catch { return []; }
    }

    private static void AddEmails(HashSet<string> emails, string value)
    {
        foreach (var email in (value ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Regex.IsMatch(email, @"^[^\s@]+@[^\s@]+\.[^\s@]+$")) emails.Add(email);
    }

    private static NotificationSmtpSetting NormalizeSmtp(NotificationSmtpSetting row) => new()
    {
        IsEnabled = row.IsEnabled,
        Host = row.Host.Trim(),
        Port = row.Port <= 0 ? 587 : row.Port,
        UserName = row.UserName.Trim(),
        Password = row.Password,
        EnableSsl = row.EnableSsl,
        FromEmail = row.FromEmail.Trim(),
        FromName = row.FromName.Trim()
    };

    private static string CleanCode(string value) => (value ?? "").Trim().ToUpperInvariant();
    private static string CleanRecipientType(string value) => value.Equals("Cc", StringComparison.OrdinalIgnoreCase) ? "Cc" : value.Equals("Bcc", StringComparison.OrdinalIgnoreCase) ? "Bcc" : "To";
    private static bool Safe(string value) => Regex.IsMatch(value ?? "", @"^[A-Za-z_][A-Za-z0-9_]*$");

    private static Task EnsureDefaultsAsync(MySqlConnection db) => db.ExecuteAsync(@"INSERT INTO notification_templates (Code,Name,SubjectTemplate,BodyTemplate,IsHtml,IsActive) VALUES
('PAYRUN_LOCKED_DEFAULT','Payroll locked notification','Payroll {{resourceId}} is locked','<p>Payroll request <b>{{resourceId}}</b> has been submitted by {{requestedBy}}.</p><p>Event: {{eventCode}}</p>',TRUE,TRUE),
('LEAVE_REQUEST_DEFAULT','Leave request notification','Leave request {{resourceId}} submitted','<p>Leave request <b>{{resourceId}}</b> has been submitted by {{requestedBy}}.</p>',TRUE,TRUE),
('EXPENSE_CLAIM_SUBMIT_DEFAULT','Expense claim submission','Expense claim {{resourceId}} submitted','<p>Expense claim <b>{{resourceId}}</b> has been submitted by {{requestedBy}}.</p><p>Use My Tasks to review the request.</p>',TRUE,TRUE),
('EXPENSE_CLAIM_ACTION_DEFAULT','Expense claim workflow action','Expense claim {{resourceId}} updated','<p>Expense claim <b>{{resourceId}}</b> has been updated.</p><p>Event: {{eventCode}}</p>',TRUE,TRUE)
ON DUPLICATE KEY UPDATE Name=VALUES(Name);

INSERT INTO notification_rules (Name,EventCode,ClientId,TemplateId,IsEnabled,ConditionJson)
SELECT 'Expense claim submission to manager','EXPENSE_CLAIM.SUBMIT',NULL,t.Id,TRUE,'{}'
FROM notification_templates t
WHERE t.Code='EXPENSE_CLAIM_SUBMIT_DEFAULT'
  AND NOT EXISTS (SELECT 1 FROM notification_rules r WHERE r.EventCode='EXPENSE_CLAIM.SUBMIT' AND r.Name='Expense claim submission to manager');

INSERT INTO notification_recipients (RuleId,RecipientType,SourceType,SourceValue,TableName,MatchColumn,MatchValueSource,EmailColumn,IsActive)
SELECT r.Id,'To','ReportingManager','','','','resourceId','',TRUE
FROM notification_rules r
WHERE r.EventCode='EXPENSE_CLAIM.SUBMIT' AND r.Name='Expense claim submission to manager'
  AND NOT EXISTS (SELECT 1 FROM notification_recipients x WHERE x.RuleId=r.Id AND x.RecipientType='To' AND x.SourceType='ReportingManager');

INSERT INTO notification_recipients (RuleId,RecipientType,SourceType,SourceValue,TableName,MatchColumn,MatchValueSource,EmailColumn,IsActive)
SELECT r.Id,'Cc','RequestorEmail','','','','resourceId','',TRUE
FROM notification_rules r
WHERE r.EventCode='EXPENSE_CLAIM.SUBMIT' AND r.Name='Expense claim submission to manager'
  AND NOT EXISTS (SELECT 1 FROM notification_recipients x WHERE x.RuleId=r.Id AND x.RecipientType='Cc' AND x.SourceType='RequestorEmail');");
}
