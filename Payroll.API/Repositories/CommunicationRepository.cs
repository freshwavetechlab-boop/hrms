using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed class CommunicationRepository
{
    private const int MaxRecipients = 5000;
    private static readonly Regex CodePattern = new("^[A-Za-z0-9._-]{2,120}$", RegexOptions.Compiled);
    private static readonly Regex TokenPattern = new("{{\\s*([A-Za-z][A-Za-z0-9]*)\\s*}}", RegexOptions.Compiled);
    private static readonly HashSet<string> AllowedSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "Employee.FullName", "Employee.FirstName", "Employee.EmployeeCode", "Employee.WorkEmail", "Employee.Mobile",
        "Employee.Department", "Employee.Designation", "Employee.WorkLocation", "Client.Name", "CurrentUser.DisplayName"
    };

    private readonly IConfiguration configuration;
    private readonly IDataProtector credentialProtector;
    private readonly IReadOnlyList<ICommunicationChannelSender> senders;
    private readonly ILogger<CommunicationRepository> logger;

    public CommunicationRepository(
        IConfiguration configuration,
        IDataProtectionProvider dataProtectionProvider,
        IEnumerable<ICommunicationChannelSender> senders,
        ILogger<CommunicationRepository> logger)
    {
        this.configuration = configuration;
        credentialProtector = dataProtectionProvider.CreateProtector("Payroll.API.CommunicationProviderCredentials.v1");
        this.senders = senders.ToList();
        this.logger = logger;
    }

    private MySqlConnection Db() => new(configuration.GetConnectionString("Default"));

    public async Task InitializeAsync()
    {
        await using var db = Db();
        await db.OpenAsync();
        await db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS communication_provider_accounts (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NULL,
    Channel VARCHAR(20) NOT NULL,
    ProviderCode VARCHAR(120) NOT NULL,
    AccountName VARCHAR(220) NOT NULL,
    BaseUrl VARCHAR(500) NOT NULL DEFAULT '',
    ApiVersion VARCHAR(40) NOT NULL DEFAULT '',
    SenderId VARCHAR(160) NOT NULL DEFAULT '',
    PhoneNumberId VARCHAR(160) NOT NULL DEFAULT '',
    BusinessAccountId VARCHAR(160) NOT NULL DEFAULT '',
    DefaultCountryCode VARCHAR(12) NOT NULL DEFAULT '+91',
    DefaultLanguageCode VARCHAR(20) NOT NULL DEFAULT 'en',
    RequestTimeoutSeconds INT NOT NULL DEFAULT 30,
    MaximumMessagesPerMinute INT NOT NULL DEFAULT 60,
    ApiKeyCipherText MEDIUMTEXT NULL,
    AccessTokenCipherText MEDIUMTEXT NULL,
    WebhookSecretCipherText MEDIUMTEXT NULL,
    IsEnabled BOOLEAN NOT NULL DEFAULT FALSE,
    DeliveryPaused BOOLEAN NOT NULL DEFAULT FALSE,
    HealthStatus VARCHAR(30) NOT NULL DEFAULT 'NotConfigured',
    LastHealthMessage VARCHAR(1000) NOT NULL DEFAULT '',
    LastTestedAtUtc DATETIME NULL,
    CreatedByUserId INT NOT NULL,
    UpdatedByUserId INT NOT NULL,
    CreatedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    INDEX IX_CommunicationProvider_ClientChannel (ClientId, Channel, IsEnabled),
    UNIQUE KEY UX_CommunicationProvider_ClientChannelCode (ClientId, Channel, ProviderCode)
);
CREATE TABLE IF NOT EXISTS communication_templates (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NULL,
    Channel VARCHAR(20) NOT NULL,
    Code VARCHAR(120) NOT NULL,
    Name VARCHAR(220) NOT NULL,
    SubjectTemplate VARCHAR(500) NOT NULL DEFAULT '',
    BodyTemplate MEDIUMTEXT NOT NULL,
    ProviderTemplateCode VARCHAR(220) NOT NULL DEFAULT '',
    LanguageCode VARCHAR(20) NOT NULL DEFAULT 'en',
    IsHtml BOOLEAN NOT NULL DEFAULT FALSE,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedByUserId INT NOT NULL DEFAULT 0,
    UpdatedByUserId INT NOT NULL DEFAULT 0,
    CreatedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    INDEX IX_CommunicationTemplate_ClientChannel (ClientId, Channel, IsActive),
    UNIQUE KEY UX_CommunicationTemplate_ClientCode (ClientId, Code)
);
CREATE TABLE IF NOT EXISTS communication_template_variables (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    TemplateId BIGINT NOT NULL,
    Position INT NOT NULL,
    VariableKey VARCHAR(120) NOT NULL,
    Label VARCHAR(180) NOT NULL,
    SourceCode VARCHAR(120) NOT NULL,
    IsRequired BOOLEAN NOT NULL DEFAULT FALSE,
    FallbackValue VARCHAR(1000) NOT NULL DEFAULT '',
    UNIQUE KEY UX_CommunicationTemplateVariable_Key (TemplateId, VariableKey),
    UNIQUE KEY UX_CommunicationTemplateVariable_Position (TemplateId, Position),
    INDEX IX_CommunicationTemplateVariable_Template (TemplateId),
    CONSTRAINT FK_CommunicationTemplateVariable_Template FOREIGN KEY (TemplateId) REFERENCES communication_templates(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS employee_communication_campaigns (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL,
    Channel VARCHAR(20) NOT NULL,
    TemplateId BIGINT NULL,
    SelectionMode VARCHAR(30) NOT NULL,
    SubjectSnapshot VARCHAR(500) NOT NULL DEFAULT '',
    BodySnapshot MEDIUMTEXT NOT NULL,
    TotalSelected INT NOT NULL DEFAULT 0,
    TotalEligible INT NOT NULL DEFAULT 0,
    TotalExcluded INT NOT NULL DEFAULT 0,
    TotalQueued INT NOT NULL DEFAULT 0,
    TotalSent INT NOT NULL DEFAULT 0,
    TotalDelivered INT NOT NULL DEFAULT 0,
    TotalRead INT NOT NULL DEFAULT 0,
    TotalFailed INT NOT NULL DEFAULT 0,
    Status VARCHAR(30) NOT NULL DEFAULT 'Draft',
    IdempotencyKey VARCHAR(120) NOT NULL,
    CreatedByUserId INT NOT NULL,
    CreatedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    StartedAtUtc DATETIME NULL,
    CompletedAtUtc DATETIME NULL,
    UNIQUE KEY UX_CommunicationCampaign_Idempotency (CreatedByUserId, IdempotencyKey),
    INDEX IX_CommunicationCampaign_ClientCreated (ClientId, CreatedAtUtc),
    INDEX IX_CommunicationCampaign_Status (Status, CreatedAtUtc)
);
CREATE TABLE IF NOT EXISTS employee_communication_campaign_filters (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    CampaignId BIGINT NOT NULL,
    FilterType VARCHAR(50) NOT NULL,
    IntegerValue INT NULL,
    TextValue VARCHAR(500) NOT NULL DEFAULT '',
    INDEX IX_CommunicationCampaignFilter_Campaign (CampaignId),
    CONSTRAINT FK_CommunicationCampaignFilter_Campaign FOREIGN KEY (CampaignId) REFERENCES employee_communication_campaigns(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS employee_communication_drafts (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL,
    Channel VARCHAR(20) NOT NULL,
    Status VARCHAR(20) NOT NULL DEFAULT 'Draft',
    CreatedByUserId INT NOT NULL,
    CreatedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    ConsumedAtUtc DATETIME NULL,
    INDEX IX_CommunicationDraft_OwnerStatus (CreatedByUserId, Status, UpdatedAtUtc),
    INDEX IX_CommunicationDraft_Client (ClientId, UpdatedAtUtc)
);
CREATE TABLE IF NOT EXISTS employee_communication_recipients (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    CampaignId BIGINT NOT NULL,
    EmployeeId INT NOT NULL,
    RecipientType VARCHAR(10) NOT NULL DEFAULT 'To',
    Destination VARCHAR(320) NOT NULL DEFAULT '',
    RenderedSubject VARCHAR(500) NOT NULL DEFAULT '',
    RenderedBody MEDIUMTEXT NOT NULL,
    Status VARCHAR(30) NOT NULL DEFAULT 'Pending',
    ExclusionReason VARCHAR(500) NOT NULL DEFAULT '',
    RetryCount INT NOT NULL DEFAULT 0,
    ProviderMessageId VARCHAR(300) NOT NULL DEFAULT '',
    ErrorCode VARCHAR(120) NOT NULL DEFAULT '',
    ErrorMessage TEXT NULL,
    EmailQueueId BIGINT NULL,
    MessageId BIGINT NULL,
    QueuedAtUtc DATETIME NULL,
    SentAtUtc DATETIME NULL,
    DeliveredAtUtc DATETIME NULL,
    ReadAtUtc DATETIME NULL,
    CreatedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY UX_CommunicationRecipient_CampaignEmployee (CampaignId, EmployeeId),
    INDEX IX_CommunicationRecipient_Status (Status, CreatedAtUtc),
    INDEX IX_CommunicationRecipient_EmailQueue (EmailQueueId),
    CONSTRAINT FK_CommunicationRecipient_Campaign FOREIGN KEY (CampaignId) REFERENCES employee_communication_campaigns(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS employee_communication_conversations (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL,
    EmployeeId INT NOT NULL,
    Channel VARCHAR(20) NOT NULL,
    Destination VARCHAR(320) NOT NULL,
    Status VARCHAR(20) NOT NULL DEFAULT 'Open',
    AssignedUserId INT NULL,
    LastMessagePreview VARCHAR(500) NOT NULL DEFAULT '',
    LastMessageAtUtc DATETIME NULL,
    UnreadCount INT NOT NULL DEFAULT 0,
    CreatedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_CommunicationConversation_EmployeeChannel (ClientId, EmployeeId, Channel),
    INDEX IX_CommunicationConversation_ClientActivity (ClientId, LastMessageAtUtc),
    INDEX IX_CommunicationConversation_Unread (ClientId, UnreadCount)
);
CREATE TABLE IF NOT EXISTS employee_communication_messages (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ConversationId BIGINT NOT NULL,
    CampaignRecipientId BIGINT NULL,
    ProviderAccountId BIGINT NULL,
    Direction VARCHAR(20) NOT NULL,
    MessageType VARCHAR(30) NOT NULL DEFAULT 'Text',
    Subject VARCHAR(500) NOT NULL DEFAULT '',
    Body MEDIUMTEXT NOT NULL,
    Status VARCHAR(30) NOT NULL DEFAULT 'Pending',
    ProviderMessageId VARCHAR(300) NOT NULL DEFAULT '',
    EmailQueueId BIGINT NULL,
    RetryCount INT NOT NULL DEFAULT 0,
    ErrorCode VARCHAR(120) NOT NULL DEFAULT '',
    ErrorMessage TEXT NULL,
    IdempotencyKey VARCHAR(120) NULL,
    CreatedByUserId INT NOT NULL DEFAULT 0,
    SentAtUtc DATETIME NULL,
    DeliveredAtUtc DATETIME NULL,
    ReadAtUtc DATETIME NULL,
    ReceivedAtUtc DATETIME NULL,
    CreatedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY UX_CommunicationMessage_Idempotency (CreatedByUserId, IdempotencyKey),
    INDEX IX_CommunicationMessage_Conversation (ConversationId, CreatedAtUtc),
    INDEX IX_CommunicationMessage_ProviderMessage (ProviderMessageId),
    INDEX IX_CommunicationMessage_EmailQueue (EmailQueueId),
    INDEX IX_CommunicationMessage_Status (Status, CreatedAtUtc),
    CONSTRAINT FK_CommunicationMessage_Conversation FOREIGN KEY (ConversationId) REFERENCES employee_communication_conversations(Id) ON DELETE CASCADE,
    CONSTRAINT FK_CommunicationMessage_Provider FOREIGN KEY (ProviderAccountId) REFERENCES communication_provider_accounts(Id) ON DELETE SET NULL
);
CREATE TABLE IF NOT EXISTS communication_delivery_attempts (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    MessageId BIGINT NOT NULL,
    CampaignRecipientId BIGINT NULL,
    AttemptNumber INT NOT NULL,
    ProviderAccountId BIGINT NULL,
    ProviderRequestId VARCHAR(300) NOT NULL DEFAULT '',
    HttpStatusCode INT NULL,
    IsSuccess BOOLEAN NOT NULL DEFAULT FALSE,
    ErrorCode VARCHAR(120) NOT NULL DEFAULT '',
    ErrorMessage TEXT NULL,
    SegmentCount INT NOT NULL DEFAULT 0,
    Cost DECIMAL(18,4) NOT NULL DEFAULT 0,
    Currency VARCHAR(10) NOT NULL DEFAULT '',
    AttemptedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CompletedAtUtc DATETIME NULL,
    INDEX IX_CommunicationAttempt_Message (MessageId, AttemptNumber),
    INDEX IX_CommunicationAttempt_Recipient (CampaignRecipientId),
    CONSTRAINT FK_CommunicationAttempt_Message FOREIGN KEY (MessageId) REFERENCES employee_communication_messages(Id) ON DELETE CASCADE,
    CONSTRAINT FK_CommunicationAttempt_Provider FOREIGN KEY (ProviderAccountId) REFERENCES communication_provider_accounts(Id) ON DELETE SET NULL
);
CREATE TABLE IF NOT EXISTS employee_communication_message_attachments (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    MessageId BIGINT NOT NULL,
    EntityAttachmentId BIGINT NOT NULL,
    CreatedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY UX_CommunicationMessageAttachment (MessageId, EntityAttachmentId),
    INDEX IX_CommunicationMessageAttachment_Entity (EntityAttachmentId),
    CONSTRAINT FK_CommunicationMessageAttachment_Message FOREIGN KEY (MessageId) REFERENCES employee_communication_messages(Id) ON DELETE CASCADE,
    CONSTRAINT FK_CommunicationMessageAttachment_Entity FOREIGN KEY (EntityAttachmentId) REFERENCES entity_attachments(id)
);
CREATE TABLE IF NOT EXISTS communication_delivery_events (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    MessageId BIGINT NOT NULL,
    CampaignRecipientId BIGINT NULL,
    ProviderAccountId BIGINT NULL,
    ProviderEventId VARCHAR(300) NOT NULL,
    EventStatus VARCHAR(40) NOT NULL,
    OccurredAtUtc DATETIME NOT NULL,
    ReceivedAtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY UX_CommunicationEvent_ProviderEvent (ProviderAccountId, ProviderEventId),
    INDEX IX_CommunicationEvent_Message (MessageId, OccurredAtUtc),
    INDEX IX_CommunicationEvent_Recipient (CampaignRecipientId),
    CONSTRAINT FK_CommunicationEvent_Message FOREIGN KEY (MessageId) REFERENCES employee_communication_messages(Id) ON DELETE CASCADE,
    CONSTRAINT FK_CommunicationEvent_Provider FOREIGN KEY (ProviderAccountId) REFERENCES communication_provider_accounts(Id) ON DELETE SET NULL
);");
        await EnsureColumnAsync(db, "employee_communication_recipients", "RecipientType", "VARCHAR(10) NOT NULL DEFAULT 'To' AFTER EmployeeId");
        await EnsureForeignKeyAsync(db, "communication_template_variables", "FK_CommunicationTemplateVariable_Template", "FOREIGN KEY (TemplateId) REFERENCES communication_templates(Id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(db, "employee_communication_campaign_filters", "FK_CommunicationCampaignFilter_Campaign", "FOREIGN KEY (CampaignId) REFERENCES employee_communication_campaigns(Id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(db, "employee_communication_recipients", "FK_CommunicationRecipient_Campaign", "FOREIGN KEY (CampaignId) REFERENCES employee_communication_campaigns(Id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(db, "employee_communication_messages", "FK_CommunicationMessage_Conversation", "FOREIGN KEY (ConversationId) REFERENCES employee_communication_conversations(Id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(db, "employee_communication_messages", "FK_CommunicationMessage_Provider", "FOREIGN KEY (ProviderAccountId) REFERENCES communication_provider_accounts(Id) ON DELETE SET NULL");
        await EnsureForeignKeyAsync(db, "communication_delivery_attempts", "FK_CommunicationAttempt_Message", "FOREIGN KEY (MessageId) REFERENCES employee_communication_messages(Id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(db, "communication_delivery_attempts", "FK_CommunicationAttempt_Provider", "FOREIGN KEY (ProviderAccountId) REFERENCES communication_provider_accounts(Id) ON DELETE SET NULL");
        await EnsureForeignKeyAsync(db, "employee_communication_message_attachments", "FK_CommunicationMessageAttachment_Message", "FOREIGN KEY (MessageId) REFERENCES employee_communication_messages(Id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(db, "employee_communication_message_attachments", "FK_CommunicationMessageAttachment_Entity", "FOREIGN KEY (EntityAttachmentId) REFERENCES entity_attachments(id)");
        await EnsureForeignKeyAsync(db, "communication_delivery_events", "FK_CommunicationEvent_Message", "FOREIGN KEY (MessageId) REFERENCES employee_communication_messages(Id) ON DELETE CASCADE");
        await EnsureForeignKeyAsync(db, "communication_delivery_events", "FK_CommunicationEvent_Provider", "FOREIGN KEY (ProviderAccountId) REFERENCES communication_provider_accounts(Id) ON DELETE SET NULL");
        await SeedDefaultTemplateAsync(db);
        await db.ExecuteAsync(@"INSERT IGNORE INTO schema_migrations (MigrationKey)
VALUES ('20260728.employee-communication.normalized-storage.v3')");
    }

    private static async Task SeedDefaultTemplateAsync(MySqlConnection db)
    {
        await db.ExecuteAsync(@"
INSERT INTO communication_templates
(ClientId,Channel,Code,Name,SubjectTemplate,BodyTemplate,ProviderTemplateCode,LanguageCode,IsHtml,IsActive,CreatedByUserId,UpdatedByUserId)
SELECT NULL,'Email','EMPLOYEE_GENERAL_ANNOUNCEMENT','General employee announcement','{{employeeName}} - an update from HR',
'<p>Hello {{employeeName}},</p><p>Please review this important update from HR.</p><p>Regards,<br>{{senderName}}</p>','','en',TRUE,TRUE,0,0
WHERE NOT EXISTS (SELECT 1 FROM communication_templates WHERE ClientId IS NULL AND Code='EMPLOYEE_GENERAL_ANNOUNCEMENT');");
        var templateId = await db.ExecuteScalarAsync<long>("SELECT Id FROM communication_templates WHERE ClientId IS NULL AND Code='EMPLOYEE_GENERAL_ANNOUNCEMENT' ORDER BY Id LIMIT 1");
        await db.ExecuteAsync(@"
INSERT IGNORE INTO communication_template_variables (TemplateId,Position,VariableKey,Label,SourceCode,IsRequired,FallbackValue) VALUES
(@TemplateId,1,'employeeName','Employee name','Employee.FullName',TRUE,'Employee'),
(@TemplateId,2,'senderName','Sender name','CurrentUser.DisplayName',TRUE,'HR Team');", new { TemplateId = templateId });
    }

    public async Task<(EmployeeCommunicationDraft? Item, string Error)> CreateDraftAsync(CreateEmployeeCommunicationDraftRequest request, AuthUser user)
    {
        try { EnsureClient(user, request.ClientId); }
        catch (InvalidOperationException ex) { return (null, ex.Message); }
        var channel = CommunicationChannels.Normalize(request.Channel);
        if (string.IsNullOrWhiteSpace(channel)) return (null, "A valid communication channel is required.");
        await using var db = Db();
        await db.OpenAsync();
        var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO employee_communication_drafts
(ClientId,Channel,Status,CreatedByUserId) VALUES (@ClientId,@Channel,'Draft',@UserId); SELECT LAST_INSERT_ID();",
            new { request.ClientId, Channel = channel, UserId = user.Id });
        return (await db.QueryFirstOrDefaultAsync<EmployeeCommunicationDraft>("SELECT * FROM employee_communication_drafts WHERE Id=@id", new { id }), string.Empty);
    }

    public async Task<List<CommunicationProviderAccount>> GetProvidersAsync(int? clientId, AuthUser user)
    {
        var scopedClientId = ResolveOptionalClient(user, clientId);
        await using var db = Db();
        await db.OpenAsync();
        return (await db.QueryAsync<CommunicationProviderAccount>(@"
SELECT p.Id,p.ClientId,COALESCE(c.Name,'All clients') ClientName,p.Channel,p.ProviderCode,p.AccountName,p.BaseUrl,p.ApiVersion,
p.SenderId,p.PhoneNumberId,p.BusinessAccountId,p.DefaultCountryCode,p.DefaultLanguageCode,p.RequestTimeoutSeconds,
p.MaximumMessagesPerMinute,p.IsEnabled,p.DeliveryPaused,p.HealthStatus,p.LastHealthMessage,p.LastTestedAtUtc,
(COALESCE(p.ApiKeyCipherText,'')<>'') HasApiKey,(COALESCE(p.AccessTokenCipherText,'')<>'') HasAccessToken,
(COALESCE(p.WebhookSecretCipherText,'')<>'') HasWebhookSecret,p.CreatedAtUtc,p.UpdatedAtUtc
FROM communication_provider_accounts p LEFT JOIN clients c ON c.Id=p.ClientId
WHERE (@ClientId IS NULL OR p.ClientId IS NULL OR p.ClientId=@ClientId)
ORDER BY p.Channel,p.ClientId DESC,p.AccountName", new { ClientId = scopedClientId })).ToList();
    }

    public async Task<(CommunicationProviderAccount? Item, string Error)> SaveProviderAsync(SaveCommunicationProviderAccountRequest request, AuthUser user)
    {
        var channel = CommunicationChannels.Normalize(request.Channel);
        if (channel is not (CommunicationChannels.Sms or CommunicationChannels.WhatsApp)) return (null, "Only Sms and WhatsApp provider accounts are supported here.");
        var providerCode = request.ProviderCode.Trim();
        if (!CodePattern.IsMatch(providerCode)) return (null, "Provider code must contain only letters, numbers, dot, dash or underscore.");
        if (string.IsNullOrWhiteSpace(request.AccountName)) return (null, "Account name is required.");
        if (!string.IsNullOrWhiteSpace(request.BaseUrl) && !IsSafeProviderUrl(request.BaseUrl)) return (null, "Provider base URL must be an HTTPS URL (HTTP is allowed only for localhost development).");
        int? clientId;
        try { clientId = ResolveWriteClient(user, request.ClientId); }
        catch (InvalidOperationException ex) { return (null, ex.Message); }

        await using var db = Db();
        await db.OpenAsync();
        var existing = request.Id > 0 ? await db.QueryFirstOrDefaultAsync<ProviderSecretRow>("SELECT * FROM communication_provider_accounts WHERE Id=@Id", request) : null;
        if (request.Id > 0 && existing is null) return (null, "Provider account not found.");
        if (existing is not null && !CanWriteClient(user, existing.ClientId)) return (null, "Provider account is outside your writable client scope.");
        var duplicate = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM communication_provider_accounts
WHERE Id<>@Id AND Channel=@Channel AND ProviderCode=@ProviderCode AND ((ClientId IS NULL AND @ClientId IS NULL) OR ClientId=@ClientId)",
            new { request.Id, Channel = channel, ProviderCode = providerCode, ClientId = clientId });
        if (duplicate > 0) return (null, "This provider is already configured for the selected scope and channel.");

        var secret = new ProviderSecretRow
        {
            ApiKeyCipherText = ProtectIfSupplied(request.ApiKey, existing?.ApiKeyCipherText),
            AccessTokenCipherText = ProtectIfSupplied(request.AccessToken, existing?.AccessTokenCipherText),
            WebhookSecretCipherText = ProtectIfSupplied(request.WebhookSecret, existing?.WebhookSecretCipherText)
        };
        var args = new
        {
            request.Id, ClientId = clientId, Channel = channel, ProviderCode = providerCode, AccountName = request.AccountName.Trim(),
            BaseUrl = request.BaseUrl.Trim().TrimEnd('/'), ApiVersion = request.ApiVersion.Trim(), SenderId = request.SenderId.Trim(),
            PhoneNumberId = request.PhoneNumberId.Trim(), BusinessAccountId = request.BusinessAccountId.Trim(),
            DefaultCountryCode = CleanCountryCode(request.DefaultCountryCode), DefaultLanguageCode = string.IsNullOrWhiteSpace(request.DefaultLanguageCode) ? "en" : request.DefaultLanguageCode.Trim(),
            RequestTimeoutSeconds = Math.Clamp(request.RequestTimeoutSeconds, 5, 120), MaximumMessagesPerMinute = Math.Clamp(request.MaximumMessagesPerMinute, 1, 10000),
            secret.ApiKeyCipherText, secret.AccessTokenCipherText, secret.WebhookSecretCipherText, request.IsEnabled, request.DeliveryPaused, UserId = user.Id
        };
        long id;
        if (request.Id == 0)
            id = await db.ExecuteScalarAsync<long>(@"INSERT INTO communication_provider_accounts
(ClientId,Channel,ProviderCode,AccountName,BaseUrl,ApiVersion,SenderId,PhoneNumberId,BusinessAccountId,DefaultCountryCode,DefaultLanguageCode,RequestTimeoutSeconds,MaximumMessagesPerMinute,ApiKeyCipherText,AccessTokenCipherText,WebhookSecretCipherText,IsEnabled,DeliveryPaused,HealthStatus,LastHealthMessage,CreatedByUserId,UpdatedByUserId)
VALUES (@ClientId,@Channel,@ProviderCode,@AccountName,@BaseUrl,@ApiVersion,@SenderId,@PhoneNumberId,@BusinessAccountId,@DefaultCountryCode,@DefaultLanguageCode,@RequestTimeoutSeconds,@MaximumMessagesPerMinute,@ApiKeyCipherText,@AccessTokenCipherText,@WebhookSecretCipherText,@IsEnabled,@DeliveryPaused,IF(@IsEnabled,'NotTested','Disabled'),'',@UserId,@UserId); SELECT LAST_INSERT_ID();", args);
        else
        {
            id = request.Id;
            await db.ExecuteAsync(@"UPDATE communication_provider_accounts SET ClientId=@ClientId,Channel=@Channel,ProviderCode=@ProviderCode,AccountName=@AccountName,BaseUrl=@BaseUrl,ApiVersion=@ApiVersion,SenderId=@SenderId,PhoneNumberId=@PhoneNumberId,BusinessAccountId=@BusinessAccountId,DefaultCountryCode=@DefaultCountryCode,DefaultLanguageCode=@DefaultLanguageCode,RequestTimeoutSeconds=@RequestTimeoutSeconds,MaximumMessagesPerMinute=@MaximumMessagesPerMinute,ApiKeyCipherText=@ApiKeyCipherText,AccessTokenCipherText=@AccessTokenCipherText,WebhookSecretCipherText=@WebhookSecretCipherText,IsEnabled=@IsEnabled,DeliveryPaused=@DeliveryPaused,HealthStatus=IF(@IsEnabled,'NotTested','Disabled'),LastHealthMessage='',UpdatedByUserId=@UserId WHERE Id=@Id", args);
        }
        return ((await GetProvidersAsync(clientId, user)).FirstOrDefault(x => x.Id == id), string.Empty);
    }

    public async Task<CommunicationProviderTestResult> TestProviderAsync(long id, AuthUser user, CancellationToken cancellationToken)
    {
        await using var db = Db();
        await db.OpenAsync(cancellationToken);
        var row = await db.QueryFirstOrDefaultAsync<ProviderSecretRow>("SELECT * FROM communication_provider_accounts WHERE Id=@id", new { id });
        if (row is null || !CanAccessClient(user, row.ClientId)) return new() { Message = "Provider account not found." };
        CommunicationProviderTestResult result;
        if (!row.IsEnabled) result = new() { Status = "Disabled", Message = "Provider account is disabled." };
        else if (row.DeliveryPaused) result = new() { Status = "Paused", Message = "Provider delivery is paused." };
        else if (FindSender(row.Channel, row.ProviderCode) is not { } sender)
            result = new() { Status = "Unavailable", Message = $"No installed adapter can test {row.ProviderCode}. Configuration was not reported as healthy." };
        else
        {
            try { result = await sender.TestAsync(ToProviderContext(row), cancellationToken); }
            catch (Exception ex) { result = new() { Status = "Unhealthy", Message = ex.Message }; }
        }
        result.TestedAtUtc = DateTime.UtcNow;
        await db.ExecuteAsync("UPDATE communication_provider_accounts SET HealthStatus=@Status,LastHealthMessage=@Message,LastTestedAtUtc=@TestedAtUtc WHERE Id=@id", new { result.Status, result.Message, result.TestedAtUtc, id });
        return result;
    }

    public async Task<List<CommunicationTemplate>> GetTemplatesAsync(int? clientId, string? channel, AuthUser user, bool includeInactive = true)
    {
        var scopedClientId = ResolveOptionalClient(user, clientId);
        var normalizedChannel = string.IsNullOrWhiteSpace(channel) ? string.Empty : CommunicationChannels.Normalize(channel);
        await using var db = Db();
        await db.OpenAsync();
        var templates = (await db.QueryAsync<CommunicationTemplate>(@"
SELECT t.*,COALESCE(c.Name,'All clients') ClientName FROM communication_templates t LEFT JOIN clients c ON c.Id=t.ClientId
WHERE (@ClientId IS NULL OR t.ClientId IS NULL OR t.ClientId=@ClientId) AND (@Channel='' OR t.Channel=@Channel)
  AND (@IncludeInactive=TRUE OR t.IsActive=TRUE)
ORDER BY t.IsActive DESC,t.Channel,t.Name", new { ClientId = scopedClientId, Channel = normalizedChannel, IncludeInactive = includeInactive })).ToList();
        if (templates.Count == 0) return templates;
        var variables = (await db.QueryAsync<CommunicationTemplateVariable>("SELECT * FROM communication_template_variables WHERE TemplateId IN @Ids ORDER BY Position,Id", new { Ids = templates.Select(x => x.Id).ToArray() })).ToList();
        foreach (var template in templates) template.Variables = variables.Where(x => x.TemplateId == template.Id).ToList();
        return templates;
    }

    public async Task<(CommunicationTemplate? Item, string Error)> SaveTemplateAsync(CommunicationTemplate request, AuthUser user)
    {
        var channel = CommunicationChannels.Normalize(request.Channel);
        if (string.IsNullOrWhiteSpace(channel)) return (null, "A valid channel is required.");
        if (!CodePattern.IsMatch(request.Code.Trim())) return (null, "Template code must contain only letters, numbers, dot, dash or underscore.");
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.BodyTemplate)) return (null, "Template name and body are required.");
        if (channel == CommunicationChannels.Email && string.IsNullOrWhiteSpace(request.SubjectTemplate)) return (null, "Email subject is required.");
        if (request.Variables.GroupBy(x => x.VariableKey, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) return (null, "Template variable keys must be unique.");
        if (request.Variables.Any(x => string.IsNullOrWhiteSpace(x.VariableKey) || (!string.IsNullOrWhiteSpace(x.SourceCode) && !AllowedSources.Contains(x.SourceCode)))) return (null, "One or more template variables use an unsupported data source.");
        int? clientId;
        try { clientId = ResolveWriteClient(user, request.ClientId); }
        catch (InvalidOperationException ex) { return (null, ex.Message); }
        await using var db = Db();
        await db.OpenAsync();
        if (request.Id > 0)
        {
            var existingClient = await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM communication_templates WHERE Id=@Id", request);
            var exists = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM communication_templates WHERE Id=@Id", request) > 0;
            if (!exists) return (null, "Template not found.");
            if (!CanWriteClient(user, existingClient)) return (null, "Template is outside your writable client scope.");
        }
        var duplicate = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM communication_templates WHERE Id<>@Id AND Code=@Code AND ((ClientId IS NULL AND @ClientId IS NULL) OR ClientId=@ClientId)", new { request.Id, Code = request.Code.Trim(), ClientId = clientId });
        if (duplicate > 0) return (null, "Template code already exists for this scope.");
        await using var tx = await db.BeginTransactionAsync();
        var args = new { request.Id, ClientId = clientId, Channel = channel, Code = request.Code.Trim(), Name = request.Name.Trim(), SubjectTemplate = request.SubjectTemplate.Trim(), BodyTemplate = channel == CommunicationChannels.Email && request.IsHtml ? SanitizeHtml(request.BodyTemplate) : request.BodyTemplate.Trim(), request.ProviderTemplateCode, LanguageCode = string.IsNullOrWhiteSpace(request.LanguageCode) ? "en" : request.LanguageCode.Trim(), IsHtml = channel == CommunicationChannels.Email && request.IsHtml, request.IsActive, UserId = user.Id };
        long id;
        if (request.Id == 0)
            id = await db.ExecuteScalarAsync<long>(@"INSERT INTO communication_templates (ClientId,Channel,Code,Name,SubjectTemplate,BodyTemplate,ProviderTemplateCode,LanguageCode,IsHtml,IsActive,CreatedByUserId,UpdatedByUserId) VALUES (@ClientId,@Channel,@Code,@Name,@SubjectTemplate,@BodyTemplate,@ProviderTemplateCode,@LanguageCode,@IsHtml,@IsActive,@UserId,@UserId); SELECT LAST_INSERT_ID();", args, tx);
        else
        {
            id = request.Id;
            await db.ExecuteAsync(@"UPDATE communication_templates SET ClientId=@ClientId,Channel=@Channel,Code=@Code,Name=@Name,SubjectTemplate=@SubjectTemplate,BodyTemplate=@BodyTemplate,ProviderTemplateCode=@ProviderTemplateCode,LanguageCode=@LanguageCode,IsHtml=@IsHtml,IsActive=@IsActive,UpdatedByUserId=@UserId WHERE Id=@Id", args, tx);
            await db.ExecuteAsync("DELETE FROM communication_template_variables WHERE TemplateId=@id", new { id }, tx);
        }
        var position = 0;
        foreach (var variable in request.Variables)
            await db.ExecuteAsync(@"INSERT INTO communication_template_variables (TemplateId,Position,VariableKey,Label,SourceCode,IsRequired,FallbackValue) VALUES (@TemplateId,@Position,@VariableKey,@Label,@SourceCode,@IsRequired,@FallbackValue)", new { TemplateId = id, Position = variable.Position > 0 ? variable.Position : ++position, VariableKey = variable.VariableKey.Trim(), Label = string.IsNullOrWhiteSpace(variable.Label) ? variable.VariableKey.Trim() : variable.Label.Trim(), SourceCode = variable.SourceCode.Trim(), variable.IsRequired, FallbackValue = variable.FallbackValue ?? string.Empty }, tx);
        await tx.CommitAsync();
        return ((await GetTemplatesAsync(clientId, channel, user)).FirstOrDefault(x => x.Id == id), string.Empty);
    }

    public async Task<CommunicationRecipientSearchResult> SearchRecipientsAsync(AuthUser user, int clientId, string? search, int? workLocationId, string? department, string? designation, int limit = 250)
    {
        EnsureClient(user, clientId);
        await using var db = Db();
        await db.OpenAsync();
        var filters = new RecipientFilters(clientId, search ?? string.Empty,
            workLocationId.HasValue ? [workLocationId.Value] : [],
            string.IsNullOrWhiteSpace(department) ? [] : [department],
            string.IsNullOrWhiteSpace(designation) ? [] : [designation], [], []);
        var rows = await QueryRecipientsAsync(db, filters, Math.Clamp(limit, 1, 1000));
        return new()
        {
            Items = rows,
            Total = rows.Count,
            EmailReadyCount = rows.Count(x => IsValidEmail(x.WorkEmail)),
            MobileReadyCount = rows.Count(x => !string.IsNullOrWhiteSpace(NormalizeMobile(x.Mobile, "+91")))
        };
    }

    public async Task<CommunicationPreviewResult> PreviewAsync(CommunicationSelectionRequest request, AuthUser user)
    {
        EnsureClient(user, request.ClientId);
        await using var db = Db();
        await db.OpenAsync();
        return await BuildPreviewAsync(db, request, user);
    }

    public async Task<(EmployeeCommunicationCampaign? Item, string Error)> SendAsync(SendEmployeeCommunicationRequest request, AuthUser user)
    {
        try { EnsureClient(user, request.ClientId); }
        catch (InvalidOperationException ex) { return (null, ex.Message); }
        request.IdempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? Guid.NewGuid().ToString("N") : request.IdempotencyKey.Trim();
        if (request.IdempotencyKey.Length > 120) return (null, "Idempotency key is too long.");
        await using var db = Db();
        await db.OpenAsync();
        var existingId = await db.ExecuteScalarAsync<long?>("SELECT Id FROM employee_communication_campaigns WHERE CreatedByUserId=@UserId AND IdempotencyKey=@Key", new { UserId = user.Id, Key = request.IdempotencyKey });
        if (existingId.HasValue) return (await GetCampaignAsync(existingId.Value, user), string.Empty);
        var preview = await BuildPreviewAsync(db, request, user);
        if (!preview.CanSend) return (null, preview.Warnings.FirstOrDefault() ?? "Communication cannot be sent.");
        var template = request.TemplateId.HasValue ? await LoadTemplateAsync(db, request.TemplateId.Value, request.ClientId, request.Channel) : null;
        var subject = !string.IsNullOrWhiteSpace(request.Subject) ? request.Subject.Trim() : template?.SubjectTemplate ?? string.Empty;
        var body = !string.IsNullOrWhiteSpace(request.Body) ? request.Body.Trim() : template?.BodyTemplate ?? string.Empty;
        var channel = CommunicationChannels.Normalize(request.Channel);
        if (channel == CommunicationChannels.Email) body = SanitizeHtml(body);
        var selected = await ResolveRecipientsAsync(db, request);
        var prepared = PrepareRecipients(selected, channel, subject, body, template, user);
        var (attachmentIds, attachmentError) = await ResolveDraftAttachmentsAsync(db, request.DraftId, request.ClientId, user.Id);
        if (!string.IsNullOrWhiteSpace(attachmentError)) return (null, attachmentError);
        if (channel == CommunicationChannels.Sms && attachmentIds.Count > 0) return (null, "SMS does not support file attachments. Remove the files or switch channel.");
        await using var tx = await db.BeginTransactionAsync();
        var campaignId = await db.ExecuteScalarAsync<long>(@"INSERT INTO employee_communication_campaigns
(ClientId,Channel,TemplateId,SelectionMode,SubjectSnapshot,BodySnapshot,TotalSelected,TotalEligible,TotalExcluded,Status,IdempotencyKey,CreatedByUserId,StartedAtUtc)
VALUES (@ClientId,@Channel,@TemplateId,@SelectionMode,@Subject,@Body,@Selected,@Eligible,@Excluded,'Queued',@Key,@UserId,UTC_TIMESTAMP()); SELECT LAST_INSERT_ID();",
            new { request.ClientId, Channel = channel, request.TemplateId, SelectionMode = NormalizeSelectionMode(request.SelectionMode), Subject = subject, Body = body, Selected = prepared.Count, Eligible = prepared.Count(x => x.IsEligible), Excluded = prepared.Count(x => !x.IsEligible), Key = request.IdempotencyKey, UserId = user.Id }, tx);
        await SaveCampaignFiltersAsync(db, tx, campaignId, request);
        foreach (var item in prepared)
        {
            var recipientId = await db.ExecuteScalarAsync<long>(@"INSERT INTO employee_communication_recipients
(CampaignId,EmployeeId,RecipientType,Destination,RenderedSubject,RenderedBody,Status,ExclusionReason)
VALUES (@CampaignId,@EmployeeId,@RecipientType,@Destination,@Subject,@Body,@Status,@Reason); SELECT LAST_INSERT_ID();",
                new { CampaignId = campaignId, item.Employee.EmployeeId, RecipientType = RecipientTypeFor(request, item.Employee.EmployeeId), item.Destination, Subject = item.Subject, Body = item.Body, Status = item.IsEligible ? "Pending" : "Excluded", Reason = item.ExclusionReason }, tx);
            if (!item.IsEligible) continue;
            var conversationId = await UpsertConversationAsync(db, tx, request.ClientId, item.Employee.EmployeeId, channel, item.Destination, item.Body);
            var provider = channel == CommunicationChannels.Email ? null : await FindEffectiveProviderAsync(db, request.ClientId, channel, tx);
            var messageId = await db.ExecuteScalarAsync<long>(@"INSERT INTO employee_communication_messages
(ConversationId,CampaignRecipientId,ProviderAccountId,Direction,MessageType,Subject,Body,Status,CreatedByUserId)
VALUES (@ConversationId,@RecipientId,@ProviderAccountId,'Outbound',@MessageType,@Subject,@Body,'Pending',@UserId); SELECT LAST_INSERT_ID();",
                new { ConversationId = conversationId, RecipientId = recipientId, ProviderAccountId = provider?.Id, MessageType = channel == CommunicationChannels.Email ? "Html" : "Text", Subject = item.Subject, Body = item.Body, UserId = user.Id }, tx);
            foreach (var attachmentId in attachmentIds)
                await db.ExecuteAsync(@"INSERT IGNORE INTO employee_communication_message_attachments (MessageId,EntityAttachmentId)
VALUES (@MessageId,@AttachmentId)", new { MessageId = messageId, AttachmentId = attachmentId }, tx);
            if (channel == CommunicationChannels.Email)
            {
                var queueId = await QueueEmailAsync(db, tx, request.ClientId, recipientId, item.Destination, item.Subject, item.Body);
                foreach (var attachmentId in attachmentIds)
                    await db.ExecuteAsync(@"INSERT IGNORE INTO notification_queue_attachments (QueueId,EntityAttachmentId)
VALUES (@QueueId,@AttachmentId)", new { QueueId = queueId, AttachmentId = attachmentId }, tx);
                await db.ExecuteAsync("UPDATE employee_communication_messages SET Status='Queued',EmailQueueId=@QueueId WHERE Id=@MessageId; UPDATE employee_communication_recipients SET Status='Queued',EmailQueueId=@QueueId,MessageId=@MessageId,QueuedAtUtc=UTC_TIMESTAMP() WHERE Id=@RecipientId", new { QueueId = queueId, MessageId = messageId, RecipientId = recipientId }, tx);
            }
            else
                await db.ExecuteAsync("UPDATE employee_communication_recipients SET Status='Queued',MessageId=@MessageId,QueuedAtUtc=UTC_TIMESTAMP() WHERE Id=@RecipientId", new { MessageId = messageId, RecipientId = recipientId }, tx);
        }
        if (request.DraftId.HasValue)
            await db.ExecuteAsync(@"UPDATE employee_communication_drafts SET Status='Consumed',ConsumedAtUtc=UTC_TIMESTAMP(),UpdatedAtUtc=UTC_TIMESTAMP()
WHERE Id=@DraftId AND CreatedByUserId=@UserId AND Status='Draft'", new { DraftId = request.DraftId.Value, UserId = user.Id }, tx);
        await tx.CommitAsync();
        await RefreshCampaignAsync(db, campaignId);
        return (await GetCampaignAsync(campaignId, user), string.Empty);
    }

    public async Task<EmployeeCommunicationCampaignPage> GetCampaignsAsync(AuthUser user, int? clientId, string? channel, string? status, string? search, int page, int pageSize)
    {
        var scopedClientId = ResolveOptionalClient(user, clientId);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var normalizedChannel = string.IsNullOrWhiteSpace(channel) ? string.Empty : CommunicationChannels.Normalize(channel);
        await using var db = Db();
        await db.OpenAsync();
        const string where = @"WHERE (@ClientId IS NULL OR ca.ClientId=@ClientId) AND (@Channel='' OR ca.Channel=@Channel) AND (@Status='' OR ca.Status=@Status)
AND (@Search='' OR ca.SubjectSnapshot LIKE CONCAT('%',@Search,'%') OR u.DisplayName LIKE CONCAT('%',@Search,'%') OR CAST(ca.Id AS CHAR)=@Search)";
        var args = new { ClientId = scopedClientId, Channel = normalizedChannel, Status = status?.Trim() ?? string.Empty, Search = search?.Trim() ?? string.Empty, Offset = (page - 1) * pageSize, PageSize = pageSize };
        var total = await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM employee_communication_campaigns ca LEFT JOIN authusers u ON u.Id=ca.CreatedByUserId {where}", args);
        var items = (await db.QueryAsync<EmployeeCommunicationCampaign>($@"SELECT ca.*,COALESCE(c.Name,'') ClientName,COALESCE(t.Name,'Custom message') TemplateName,COALESCE(u.DisplayName,'System') CreatedByName
FROM employee_communication_campaigns ca LEFT JOIN clients c ON c.Id=ca.ClientId LEFT JOIN communication_templates t ON t.Id=ca.TemplateId LEFT JOIN authusers u ON u.Id=ca.CreatedByUserId {where}
ORDER BY ca.CreatedAtUtc DESC LIMIT @PageSize OFFSET @Offset", args)).ToList();
        return new() { Items = items, Total = total, Page = page, PageSize = pageSize };
    }

    public async Task<EmployeeCommunicationCampaign?> GetCampaignAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var item = await db.QueryFirstOrDefaultAsync<EmployeeCommunicationCampaign>(@"SELECT ca.*,COALESCE(c.Name,'') ClientName,COALESCE(t.Name,'Custom message') TemplateName,COALESCE(u.DisplayName,'System') CreatedByName
FROM employee_communication_campaigns ca LEFT JOIN clients c ON c.Id=ca.ClientId LEFT JOIN communication_templates t ON t.Id=ca.TemplateId LEFT JOIN authusers u ON u.Id=ca.CreatedByUserId WHERE ca.Id=@id", new { id });
        if (item is null || !CanAccessClient(user, item.ClientId)) return null;
        item.Filters = (await db.QueryAsync<EmployeeCommunicationCampaignFilter>("SELECT * FROM employee_communication_campaign_filters WHERE CampaignId=@id ORDER BY Id", new { id })).ToList();
        item.Recipients = (await db.QueryAsync<EmployeeCommunicationRecipient>(@"SELECT r.*,e.EmployeeCode,TRIM(CONCAT(e.FirstName,' ',e.LastName)) EmployeeName
FROM employee_communication_recipients r JOIN employees e ON e.Id=r.EmployeeId WHERE r.CampaignId=@id ORDER BY e.FirstName,e.LastName", new { id })).ToList();
        if (item.Recipients.Count > 0)
        {
            var recipientIds = item.Recipients.Select(x => x.Id).ToArray();
            var attempts = (await db.QueryAsync<CommunicationDeliveryAttempt>("SELECT * FROM communication_delivery_attempts WHERE CampaignRecipientId IN @recipientIds ORDER BY AttemptNumber", new { recipientIds })).ToList();
            var events = (await db.QueryAsync<CommunicationDeliveryEvent>("SELECT * FROM communication_delivery_events WHERE CampaignRecipientId IN @recipientIds ORDER BY OccurredAtUtc", new { recipientIds })).ToList();
            foreach (var recipient in item.Recipients)
            {
                recipient.Attempts = attempts.Where(x => x.CampaignRecipientId == recipient.Id).ToList();
                recipient.Events = events.Where(x => x.CampaignRecipientId == recipient.Id).ToList();
            }
        }
        return item;
    }

    public async Task<(EmployeeCommunicationCampaign? Item, string Error)> RetryFailedAsync(long id, AuthUser user)
    {
        var campaign = await GetCampaignAsync(id, user);
        if (campaign is null) return (null, "Campaign not found.");
        await using var db = Db();
        await db.OpenAsync();
        var failed = (await db.QueryAsync<(long Id, long? MessageId, long? EmailQueueId)>("SELECT Id,MessageId,EmailQueueId FROM employee_communication_recipients WHERE CampaignId=@id AND Status='Failed' AND ExclusionReason=''", new { id })).ToList();
        foreach (var row in failed)
        {
            if (row.EmailQueueId.HasValue)
            {
                await db.ExecuteAsync("UPDATE notification_queue SET Status='Pending',RetryCount=0,ErrorMessage='',SentAt=NULL WHERE Id=@QueueId", new { QueueId = row.EmailQueueId });
                await db.ExecuteAsync("UPDATE employee_communication_messages SET Status='Queued',RetryCount=0,ErrorCode='',ErrorMessage='' WHERE Id=@MessageId", new { row.MessageId });
            }
            else if (row.MessageId.HasValue)
                await db.ExecuteAsync("UPDATE employee_communication_messages SET Status='Pending',RetryCount=0,ErrorCode='',ErrorMessage='' WHERE Id=@MessageId", new { row.MessageId });
            await db.ExecuteAsync("UPDATE employee_communication_recipients SET Status='Queued',RetryCount=0,ErrorCode='',ErrorMessage='',QueuedAtUtc=UTC_TIMESTAMP() WHERE Id=@Id", new { row.Id });
        }
        await RefreshCampaignAsync(db, id);
        return (await GetCampaignAsync(id, user), string.Empty);
    }

    public async Task<List<EmployeeCommunicationConversation>> GetConversationsAsync(AuthUser user, int? clientId, string? channel, string? status, string? search)
    {
        var scopedClientId = ResolveOptionalClient(user, clientId);
        var normalizedChannel = string.IsNullOrWhiteSpace(channel) ? string.Empty : CommunicationChannels.Normalize(channel);
        await using var db = Db();
        await db.OpenAsync();
        return (await db.QueryAsync<EmployeeCommunicationConversation>(@"
SELECT cv.*,e.EmployeeCode,TRIM(CONCAT(e.FirstName,' ',e.LastName)) EmployeeName,COALESCE(u.DisplayName,'') AssignedUserName
FROM employee_communication_conversations cv JOIN employees e ON e.Id=cv.EmployeeId LEFT JOIN authusers u ON u.Id=cv.AssignedUserId
WHERE (@ClientId IS NULL OR cv.ClientId=@ClientId) AND (@Channel='' OR cv.Channel=@Channel) AND (@Status='' OR cv.Status=@Status)
AND (@Search='' OR e.EmployeeCode LIKE CONCAT('%',@Search,'%') OR e.FirstName LIKE CONCAT('%',@Search,'%') OR e.LastName LIKE CONCAT('%',@Search,'%') OR cv.Destination LIKE CONCAT('%',@Search,'%'))
ORDER BY COALESCE(cv.LastMessageAtUtc,cv.CreatedAtUtc) DESC LIMIT 300",
            new { ClientId = scopedClientId, Channel = normalizedChannel, Status = status?.Trim() ?? string.Empty, Search = search?.Trim() ?? string.Empty })).ToList();
    }

    public async Task<EmployeeCommunicationConversation?> GetConversationAsync(long id, AuthUser user)
    {
        await using var db = Db();
        await db.OpenAsync();
        var item = await db.QueryFirstOrDefaultAsync<EmployeeCommunicationConversation>(@"SELECT cv.*,e.EmployeeCode,TRIM(CONCAT(e.FirstName,' ',e.LastName)) EmployeeName,COALESCE(u.DisplayName,'') AssignedUserName
FROM employee_communication_conversations cv JOIN employees e ON e.Id=cv.EmployeeId LEFT JOIN authusers u ON u.Id=cv.AssignedUserId WHERE cv.Id=@id", new { id });
        if (item is null || !CanAccessClient(user, item.ClientId)) return null;
        await db.ExecuteAsync("UPDATE employee_communication_conversations SET UnreadCount=0 WHERE Id=@id AND UnreadCount<>0", new { id });
        item.UnreadCount = 0;
        item.Messages = (await db.QueryAsync<EmployeeCommunicationMessage>("SELECT * FROM employee_communication_messages WHERE ConversationId=@id ORDER BY CreatedAtUtc,Id", new { id })).ToList();
        if (item.Messages.Count > 0)
        {
            var messageIds = item.Messages.Select(message => message.Id).ToArray();
            var attachments = (await db.QueryAsync<CommunicationMessageAttachment>(@"SELECT ma.Id,ma.MessageId,ma.EntityAttachmentId,
a.public_id PublicId,a.original_file_name FileName,a.detected_mime_type ContentType,a.file_size_bytes FileSizeBytes
FROM employee_communication_message_attachments ma
JOIN entity_attachments a ON a.id=ma.EntityAttachmentId
WHERE ma.MessageId IN @MessageIds AND a.is_current=TRUE AND a.is_deleted=FALSE
ORDER BY ma.Id", new { MessageIds = messageIds })).ToList();
            foreach (var message in item.Messages)
                message.Attachments = attachments.Where(attachment => attachment.MessageId == message.Id).ToList();
        }
        return item;
    }

    public async Task<(EmployeeCommunicationConversation? Item, string Error)> ReplyAsync(long id, CommunicationConversationReplyRequest request, AuthUser user)
    {
        if (string.IsNullOrWhiteSpace(request.Body) && !request.TemplateId.HasValue) return (null, "Message or template is required.");
        var conversation = await GetConversationAsync(id, user);
        if (conversation is null) return (null, "Conversation not found.");
        request.IdempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? Guid.NewGuid().ToString("N") : request.IdempotencyKey.Trim();
        await using var db = Db();
        await db.OpenAsync();
        var existing = await db.ExecuteScalarAsync<long?>("SELECT ConversationId FROM employee_communication_messages WHERE CreatedByUserId=@UserId AND IdempotencyKey=@Key", new { UserId = user.Id, Key = request.IdempotencyKey });
        if (existing.HasValue) return (await GetConversationAsync(existing.Value, user), string.Empty);
        var template = request.TemplateId.HasValue ? await LoadTemplateAsync(db, request.TemplateId.Value, conversation.ClientId, conversation.Channel) : null;
        if (request.TemplateId.HasValue && template is null) return (null, "Template is inactive, unavailable or belongs to another channel.");
        var employee = (await QueryRecipientsAsync(db, new(conversation.ClientId, string.Empty, [], [], [], [conversation.EmployeeId], []), 1)).FirstOrDefault();
        if (employee is null) return (null, "Employee is no longer active.");
        var subjectTemplate = !string.IsNullOrWhiteSpace(request.Subject) ? request.Subject : template?.SubjectTemplate ?? string.Empty;
        var bodyTemplate = !string.IsNullOrWhiteSpace(request.Body) ? request.Body : template?.BodyTemplate ?? string.Empty;
        if (conversation.Channel == CommunicationChannels.Email) bodyTemplate = SanitizeHtml(bodyTemplate);
        var prepared = PrepareRecipient(employee, conversation.Channel, subjectTemplate, bodyTemplate, template, user, conversation.Destination);
        if (!prepared.IsEligible) return (null, prepared.ExclusionReason);
        ProviderSecretRow? provider = null;
        if (conversation.Channel != CommunicationChannels.Email)
        {
            provider = await FindEffectiveProviderAsync(db, conversation.ClientId, conversation.Channel);
            if (provider is null) return (null, $"{conversation.Channel} is not configured or delivery is paused.");
            if (FindSender(conversation.Channel, provider.ProviderCode) is null) return (null, $"No installed adapter can send through {provider.ProviderCode}.");
        }
        var (attachmentIds, attachmentError) = await ResolveDraftAttachmentsAsync(db, request.DraftId, conversation.ClientId, user.Id);
        if (!string.IsNullOrWhiteSpace(attachmentError)) return (null, attachmentError);
        if (conversation.Channel == CommunicationChannels.Sms && attachmentIds.Count > 0) return (null, "SMS does not support file attachments.");
        await using var tx = await db.BeginTransactionAsync();
        var messageId = await db.ExecuteScalarAsync<long>(@"INSERT INTO employee_communication_messages
(ConversationId,ProviderAccountId,Direction,MessageType,Subject,Body,Status,IdempotencyKey,CreatedByUserId)
VALUES (@ConversationId,@ProviderAccountId,'Outbound',@MessageType,@Subject,@Body,'Pending',@Key,@UserId); SELECT LAST_INSERT_ID();",
            new { ConversationId = id, ProviderAccountId = provider?.Id, MessageType = conversation.Channel == CommunicationChannels.Email ? "Html" : "Text", Subject = prepared.Subject, Body = prepared.Body, Key = request.IdempotencyKey, UserId = user.Id }, tx);
        foreach (var attachmentId in attachmentIds)
            await db.ExecuteAsync(@"INSERT IGNORE INTO employee_communication_message_attachments (MessageId,EntityAttachmentId)
VALUES (@MessageId,@AttachmentId)", new { MessageId = messageId, AttachmentId = attachmentId }, tx);
        if (conversation.Channel == CommunicationChannels.Email)
        {
            var queueId = await QueueEmailAsync(db, tx, conversation.ClientId, null, conversation.Destination, prepared.Subject, prepared.Body, messageId);
            foreach (var attachmentId in attachmentIds)
                await db.ExecuteAsync(@"INSERT IGNORE INTO notification_queue_attachments (QueueId,EntityAttachmentId)
VALUES (@QueueId,@AttachmentId)", new { QueueId = queueId, AttachmentId = attachmentId }, tx);
            await db.ExecuteAsync("UPDATE employee_communication_messages SET Status='Queued',EmailQueueId=@queueId WHERE Id=@messageId", new { queueId, messageId }, tx);
        }
        await db.ExecuteAsync("UPDATE employee_communication_conversations SET LastMessagePreview=@Preview,LastMessageAtUtc=UTC_TIMESTAMP(),UpdatedAtUtc=UTC_TIMESTAMP() WHERE Id=@id", new { Preview = PreviewText(prepared.Body), id }, tx);
        if (request.DraftId.HasValue)
            await db.ExecuteAsync(@"UPDATE employee_communication_drafts SET Status='Consumed',ConsumedAtUtc=UTC_TIMESTAMP(),UpdatedAtUtc=UTC_TIMESTAMP()
WHERE Id=@DraftId AND CreatedByUserId=@UserId AND Status='Draft'", new { DraftId = request.DraftId.Value, UserId = user.Id }, tx);
        await tx.CommitAsync();
        return (await GetConversationAsync(id, user), string.Empty);
    }

    public async Task<CommunicationWebhookResult> HandleWebhookAsync(long accountId, string providerCode, string suppliedSecret, CommunicationWebhookRequest request)
    {
        await using var db = Db();
        await db.OpenAsync();
        var provider = await db.QueryFirstOrDefaultAsync<ProviderSecretRow>("SELECT * FROM communication_provider_accounts WHERE Id=@accountId AND ProviderCode=@providerCode AND IsEnabled=TRUE", new { accountId, providerCode });
        if (provider is null) return new() { Message = "Provider account not found or disabled." };
        if (string.IsNullOrWhiteSpace(provider.WebhookSecretCipherText) || !SecretsEqual(Unprotect(provider.WebhookSecretCipherText), suppliedSecret))
            return new() { Message = "Webhook authentication failed." };
        if (string.IsNullOrWhiteSpace(request.ProviderEventId)) return new() { Message = "Provider event id is required." };
        var duplicateMessageId = await db.ExecuteScalarAsync<long?>("SELECT MessageId FROM communication_delivery_events WHERE ProviderAccountId=@accountId AND ProviderEventId=@ProviderEventId", new { accountId, request.ProviderEventId });
        if (duplicateMessageId.HasValue) return new() { Accepted = true, Duplicate = true, MessageId = duplicateMessageId, Message = "Event already processed." };
        var direction = request.Direction.Trim();
        if (direction.Equals("Inbound", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(request.Body)) return new() { Message = "Inbound message body is required." };
            if (!provider.ClientId.HasValue) return new() { Message = "Inbound messages require a client-scoped provider account." };
            var sender = request.Sender.Trim();
            var candidates = await db.QueryAsync<(int Id, string Mobile)>(@"SELECT e.Id,COALESCE(p.Mobile,'') Mobile FROM employees e LEFT JOIN employeepersonaldetails p ON p.EmployeeId=e.Id WHERE e.ClientId=@ClientId AND e.IsActive=TRUE", new { ClientId = provider.ClientId });
            var senderDigits = Digits(sender);
            var employeeId = candidates.FirstOrDefault(x => SameMobile(x.Mobile, senderDigits)).Id;
            if (employeeId <= 0) return new() { Message = "Inbound sender does not match an active employee in this client." };
            await using var tx = await db.BeginTransactionAsync();
            var conversationId = await UpsertConversationAsync(db, tx, provider.ClientId.Value, employeeId, provider.Channel, sender, request.Body, true);
            var occurred = request.OccurredAtUtc ?? DateTime.UtcNow;
            var messageId = await db.ExecuteScalarAsync<long>(@"INSERT INTO employee_communication_messages
(ConversationId,ProviderAccountId,Direction,MessageType,Subject,Body,Status,ProviderMessageId,ReceivedAtUtc,CreatedAtUtc)
VALUES (@ConversationId,@ProviderAccountId,'Inbound','Text',@Subject,@Body,'Received',@ProviderMessageId,@Occurred,@Occurred); SELECT LAST_INSERT_ID();",
                new { ConversationId = conversationId, ProviderAccountId = provider.Id, request.Subject, request.Body, request.ProviderMessageId, Occurred = occurred }, tx);
            await db.ExecuteAsync(@"INSERT INTO communication_delivery_events (MessageId,ProviderAccountId,ProviderEventId,EventStatus,OccurredAtUtc) VALUES (@MessageId,@ProviderAccountId,@ProviderEventId,'Received',@Occurred)", new { MessageId = messageId, ProviderAccountId = provider.Id, request.ProviderEventId, Occurred = occurred }, tx);
            await tx.CommitAsync();
            return new() { Accepted = true, ConversationId = conversationId, MessageId = messageId, Message = "Inbound message accepted." };
        }

        if (string.IsNullOrWhiteSpace(request.ProviderMessageId)) return new() { Message = "Provider message id is required for delivery events." };
        var message = await db.QueryFirstOrDefaultAsync<EmployeeCommunicationMessage>("SELECT * FROM employee_communication_messages WHERE ProviderAccountId=@accountId AND ProviderMessageId=@ProviderMessageId ORDER BY Id DESC LIMIT 1", new { accountId, request.ProviderMessageId });
        if (message is null) return new() { Message = "Outbound message was not found." };
        var status = NormalizeDeliveryStatus(request.EventType);
        if (string.IsNullOrWhiteSpace(status)) return new() { Message = "Unsupported delivery event type." };
        var occurredAt = request.OccurredAtUtc ?? DateTime.UtcNow;
        await using (var tx = await db.BeginTransactionAsync())
        {
            await db.ExecuteAsync(@"INSERT INTO communication_delivery_events (MessageId,CampaignRecipientId,ProviderAccountId,ProviderEventId,EventStatus,OccurredAtUtc) VALUES (@MessageId,@CampaignRecipientId,@ProviderAccountId,@ProviderEventId,@Status,@OccurredAt)", new { MessageId = message.Id, message.CampaignRecipientId, ProviderAccountId = provider.Id, request.ProviderEventId, Status = status, OccurredAt = occurredAt }, tx);
            await ApplyMessageStatusAsync(db, tx, message.Id, message.CampaignRecipientId, status, occurredAt, string.Empty, string.Empty, request.ProviderMessageId);
            await tx.CommitAsync();
        }
        if (message.CampaignRecipientId.HasValue) await RefreshCampaignByRecipientAsync(db, message.CampaignRecipientId.Value);
        return new() { Accepted = true, MessageId = message.Id, ConversationId = message.ConversationId, Message = $"{status} event accepted." };
    }

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        await using var db = Db();
        await db.OpenAsync(cancellationToken);
        await SyncEmailQueueAsync(db, cancellationToken);
        var pending = (await db.QueryAsync<PendingMessageRow>(@"SELECT m.*,cv.ClientId,cv.Channel,cv.Destination,t.ProviderTemplateCode,t.LanguageCode
FROM employee_communication_messages m JOIN employee_communication_conversations cv ON cv.Id=m.ConversationId
LEFT JOIN employee_communication_recipients r ON r.Id=m.CampaignRecipientId
LEFT JOIN employee_communication_campaigns ca ON ca.Id=r.CampaignId LEFT JOIN communication_templates t ON t.Id=ca.TemplateId
WHERE m.Direction='Outbound' AND m.Status IN ('Pending','Retry') AND m.EmailQueueId IS NULL AND m.RetryCount<5 ORDER BY m.CreatedAtUtc LIMIT 20")).ToList();
        var processed = 0;
        foreach (var message in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var claimed = await db.ExecuteAsync("UPDATE employee_communication_messages SET Status='Processing' WHERE Id=@Id AND Status IN ('Pending','Retry')", new { message.Id });
            if (claimed == 0) continue;
            var provider = message.ProviderAccountId.HasValue ? await db.QueryFirstOrDefaultAsync<ProviderSecretRow>("SELECT * FROM communication_provider_accounts WHERE Id=@Id", new { Id = message.ProviderAccountId }) : await FindEffectiveProviderAsync(db, message.ClientId, message.Channel);
            var sender = provider is null ? null : FindSender(message.Channel, provider.ProviderCode);
            if (provider is null || !provider.IsEnabled || provider.DeliveryPaused || sender is null)
            {
                var error = provider is null ? $"{message.Channel} provider is not configured." : sender is null ? $"No installed adapter supports {provider.ProviderCode}." : "Provider delivery is paused or disabled.";
                await FailMessageAsync(db, message, "NotConfigured", error, provider?.Id);
                continue;
            }
            var started = DateTime.UtcNow;
            try
            {
                var result = await sender.SendAsync(ToProviderContext(provider), new(message.Destination, message.Subject, message.Body, message.ProviderTemplateCode, message.LanguageCode), cancellationToken);
                await RecordAttemptAsync(db, message, provider.Id, result, started);
                if (result.Success)
                {
                    await using var tx = await db.BeginTransactionAsync(cancellationToken);
                    await db.ExecuteAsync("UPDATE employee_communication_messages SET Status='Sent',ProviderMessageId=@ProviderMessageId,ErrorCode='',ErrorMessage='',SentAtUtc=UTC_TIMESTAMP() WHERE Id=@Id", new { result.ProviderMessageId, message.Id }, tx);
                    if (message.CampaignRecipientId.HasValue) await db.ExecuteAsync("UPDATE employee_communication_recipients SET Status='Sent',ProviderMessageId=@ProviderMessageId,ErrorCode='',ErrorMessage='',SentAtUtc=UTC_TIMESTAMP() WHERE Id=@RecipientId", new { result.ProviderMessageId, RecipientId = message.CampaignRecipientId }, tx);
                    await db.ExecuteAsync(@"INSERT IGNORE INTO communication_delivery_events (MessageId,CampaignRecipientId,ProviderAccountId,ProviderEventId,EventStatus,OccurredAtUtc) VALUES (@MessageId,@RecipientId,@ProviderId,@EventId,'Sent',UTC_TIMESTAMP())", new { MessageId = message.Id, RecipientId = message.CampaignRecipientId, ProviderId = provider.Id, EventId = string.IsNullOrWhiteSpace(result.ProviderMessageId) ? $"send-{message.Id}-{message.RetryCount + 1}" : $"send-{result.ProviderMessageId}" }, tx);
                    await tx.CommitAsync(cancellationToken);
                    processed++;
                }
                else await FailMessageAsync(db, message, result.ErrorCode, result.ErrorMessage, provider.Id, result.HttpStatusCode is 429 or >= 500);
            }
            catch (Exception ex)
            {
                await RecordAttemptAsync(db, message, provider.Id, new(false, "", "", null, "ProviderException", ex.Message), started);
                await FailMessageAsync(db, message, "ProviderException", ex.Message, provider.Id, true);
                logger.LogWarning(ex, "Communication message {MessageId} failed.", message.Id);
            }
            if (message.CampaignRecipientId.HasValue) await RefreshCampaignByRecipientAsync(db, message.CampaignRecipientId.Value);
        }
        return processed;
    }

    private async Task<CommunicationPreviewResult> BuildPreviewAsync(MySqlConnection db, CommunicationSelectionRequest request, AuthUser user)
    {
        var result = new CommunicationPreviewResult();
        var channel = CommunicationChannels.Normalize(request.Channel);
        if (string.IsNullOrWhiteSpace(channel)) { result.Warnings.Add("A valid communication channel is required."); return result; }
        var template = request.TemplateId.HasValue ? await LoadTemplateAsync(db, request.TemplateId.Value, request.ClientId, channel) : null;
        if (request.TemplateId.HasValue && template is null) { result.Warnings.Add("Template is inactive, unavailable or belongs to another channel."); return result; }
        var subject = !string.IsNullOrWhiteSpace(request.Subject) ? request.Subject.Trim() : template?.SubjectTemplate ?? string.Empty;
        var body = !string.IsNullOrWhiteSpace(request.Body) ? request.Body.Trim() : template?.BodyTemplate ?? string.Empty;
        if (channel == CommunicationChannels.Email) body = SanitizeHtml(body);
        if (string.IsNullOrWhiteSpace(body)) result.Warnings.Add("Message body is required.");
        if (channel == CommunicationChannels.Email && string.IsNullOrWhiteSpace(subject)) result.Warnings.Add("Email subject is required.");
        var selected = await ResolveRecipientsAsync(db, request);
        var prepared = PrepareRecipients(selected, channel, subject, body, template, user);
        result.SelectedCount = prepared.Count;
        result.EligibleCount = prepared.Count(x => x.IsEligible);
        result.ExcludedCount = prepared.Count(x => !x.IsEligible);
        result.MissingDestinationCount = prepared.Count(x => x.ExclusionReason.StartsWith("Missing", StringComparison.OrdinalIgnoreCase));
        result.DuplicateDestinationCount = prepared.Count(x => x.ExclusionReason.StartsWith("Duplicate", StringComparison.OrdinalIgnoreCase));
        result.Recipients = prepared.Select(x => new CommunicationPreviewRecipient { EmployeeId = x.Employee.EmployeeId, RecipientType = RecipientTypeFor(request, x.Employee.EmployeeId), EmployeeCode = x.Employee.EmployeeCode, EmployeeName = x.Employee.EmployeeName, Destination = x.Destination, IsEligible = x.IsEligible, ExclusionReason = x.ExclusionReason, SubjectPreview = x.Subject, BodyPreview = x.Body }).ToList();
        result.SampleSubject = result.Recipients.FirstOrDefault(x => x.IsEligible)?.SubjectPreview ?? string.Empty;
        result.SampleBody = result.Recipients.FirstOrDefault(x => x.IsEligible)?.BodyPreview ?? string.Empty;
        if (prepared.Count == 0) result.Warnings.Add("No employees match the current selection.");
        if (prepared.Count > MaxRecipients) result.Warnings.Add($"A maximum of {MaxRecipients} employees can be sent in one campaign.");
        if (result.MissingDestinationCount > 0) result.Warnings.Add($"{result.MissingDestinationCount} employee(s) do not have a valid {channel.ToLowerInvariant()} destination and will be excluded.");
        if (result.DuplicateDestinationCount > 0) result.Warnings.Add($"{result.DuplicateDestinationCount} duplicate destination(s) will be excluded.");
        var channelReady = await IsChannelReadyAsync(db, request.ClientId, channel);
        if (!channelReady.Ok) result.Warnings.Insert(0, channelReady.Error);
        result.CanSend = channelReady.Ok && result.EligibleCount > 0 && result.SelectedCount <= MaxRecipients && !string.IsNullOrWhiteSpace(body) && (channel != CommunicationChannels.Email || !string.IsNullOrWhiteSpace(subject));
        return result;
    }

    private async Task<(bool Ok, string Error)> IsChannelReadyAsync(MySqlConnection db, int clientId, string channel)
    {
        if (channel == CommunicationChannels.Email)
        {
            var smtp = await db.QueryFirstOrDefaultAsync<(bool IsEnabled, bool DeliveryPaused, string Host, string FromEmail)>("SELECT IsEnabled,DeliveryPaused,Host,FromEmail FROM notification_smtp_settings WHERE Id=1");
            return smtp.IsEnabled && !smtp.DeliveryPaused && !string.IsNullOrWhiteSpace(smtp.Host) && IsValidEmail(smtp.FromEmail)
                ? (true, string.Empty) : (false, "Email delivery is not configured, is paused, or has an invalid sender address.");
        }
        var provider = await FindEffectiveProviderAsync(db, clientId, channel);
        if (provider is null) return (false, $"{channel} provider is not configured, enabled, or delivery is paused.");
        return FindSender(channel, provider.ProviderCode) is not null
            ? (true, string.Empty)
            : (false, $"No installed adapter can send through {provider.ProviderCode}; the campaign will not be queued.");
    }

    private async Task<List<CommunicationRecipientOption>> ResolveRecipientsAsync(MySqlConnection db, CommunicationSelectionRequest request)
    {
        var allFiltered = NormalizeSelectionMode(request.SelectionMode) == "AllFiltered";
        var explicitEmployeeIds = request.EmployeeIds
            .Concat(request.ToEmployeeIds)
            .Concat(request.CcEmployeeIds)
            .Concat(request.BccEmployeeIds)
            .Where(x => x > 0)
            .Distinct()
            .Take(MaxRecipients + 1)
            .ToList();
        if (!allFiltered && explicitEmployeeIds.Count == 0) return [];
        var filters = new RecipientFilters(request.ClientId, request.Search, request.WorkLocationIds.Distinct().ToList(), CleanList(request.Departments), CleanList(request.Designations),
            allFiltered ? [] : explicitEmployeeIds, request.ExcludedEmployeeIds.Where(x => x > 0).Distinct().ToList());
        return await QueryRecipientsAsync(db, filters, MaxRecipients + 1);
    }

    private static async Task<List<CommunicationRecipientOption>> QueryRecipientsAsync(MySqlConnection db, RecipientFilters filters, int limit)
    {
        var where = new StringBuilder("e.ClientId=@ClientId AND e.IsActive=TRUE");
        var args = new DynamicParameters(new { filters.ClientId, Search = filters.Search.Trim(), Limit = limit });
        if (!string.IsNullOrWhiteSpace(filters.Search)) where.Append(" AND (e.EmployeeCode LIKE CONCAT('%',@Search,'%') OR e.FirstName LIKE CONCAT('%',@Search,'%') OR e.LastName LIKE CONCAT('%',@Search,'%') OR e.WorkEmail LIKE CONCAT('%',@Search,'%') OR e.Department LIKE CONCAT('%',@Search,'%') OR e.Designation LIKE CONCAT('%',@Search,'%'))");
        if (filters.WorkLocationIds.Count > 0) { where.Append(" AND e.WorkLocationId IN @WorkLocationIds"); args.Add("WorkLocationIds", filters.WorkLocationIds); }
        if (filters.Departments.Count > 0) { where.Append(" AND e.Department IN @Departments"); args.Add("Departments", filters.Departments); }
        if (filters.Designations.Count > 0) { where.Append(" AND e.Designation IN @Designations"); args.Add("Designations", filters.Designations); }
        if (filters.EmployeeIds.Count > 0) { where.Append(" AND e.Id IN @EmployeeIds"); args.Add("EmployeeIds", filters.EmployeeIds); }
        if (filters.ExcludedEmployeeIds.Count > 0) { where.Append(" AND e.Id NOT IN @ExcludedEmployeeIds"); args.Add("ExcludedEmployeeIds", filters.ExcludedEmployeeIds); }
        return (await db.QueryAsync<CommunicationRecipientOption>($@"SELECT e.Id EmployeeId,e.ClientId,COALESCE(c.Name,'') ClientName,e.EmployeeCode,TRIM(CONCAT(e.FirstName,' ',e.LastName)) EmployeeName,e.FirstName,COALESCE(e.WorkEmail,'') WorkEmail,COALESCE(p.Mobile,'') Mobile,COALESCE(e.Department,'') Department,COALESCE(e.Designation,'') Designation,e.WorkLocationId,COALESCE(w.Name,'') WorkLocationName,e.IsActive
FROM employees e JOIN clients c ON c.Id=e.ClientId LEFT JOIN employeepersonaldetails p ON p.EmployeeId=e.Id LEFT JOIN worklocations w ON w.Id=e.WorkLocationId
WHERE {where} ORDER BY e.FirstName,e.LastName,e.EmployeeCode LIMIT @Limit", args)).ToList();
    }

    private static List<PreparedRecipient> PrepareRecipients(List<CommunicationRecipientOption> employees, string channel, string subject, string body, CommunicationTemplate? template, AuthUser user)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<PreparedRecipient>();
        foreach (var employee in employees)
        {
            var destination = channel == CommunicationChannels.Email ? employee.WorkEmail.Trim() : NormalizeMobile(employee.Mobile, "+91");
            var prepared = PrepareRecipient(employee, channel, subject, body, template, user, destination);
            if (prepared.IsEligible && !seen.Add(destination)) prepared = prepared with { IsEligible = false, ExclusionReason = "Duplicate destination in this campaign." };
            result.Add(prepared);
        }
        return result;
    }

    private static PreparedRecipient PrepareRecipient(CommunicationRecipientOption employee, string channel, string subject, string body, CommunicationTemplate? template, AuthUser user, string destination)
    {
        var invalidDestination = channel == CommunicationChannels.Email ? !IsValidEmail(destination) : string.IsNullOrWhiteSpace(destination);
        if (invalidDestination) return new(employee, destination, string.Empty, string.Empty, false, $"Missing or invalid {(channel == CommunicationChannels.Email ? "email address" : "mobile number")}.");
        var values = StandardValues(employee, user);
        if (template is not null)
        {
            foreach (var variable in template.Variables)
            {
                var value = ValueForSource(variable.SourceCode, employee, user);
                if (string.IsNullOrWhiteSpace(value)) value = variable.FallbackValue;
                if (variable.IsRequired && string.IsNullOrWhiteSpace(value)) return new(employee, destination, string.Empty, string.Empty, false, $"Required value '{variable.Label}' is missing.");
                values[variable.VariableKey] = value ?? string.Empty;
            }
        }
        var encodeValues = channel == CommunicationChannels.Email;
        return new(employee, destination, Render(subject, values, false), Render(body, values, encodeValues), true, string.Empty);
    }

    private static Dictionary<string, string> StandardValues(CommunicationRecipientOption employee, AuthUser user) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["employeeName"] = employee.EmployeeName,
        ["firstName"] = employee.FirstName,
        ["employeeCode"] = employee.EmployeeCode,
        ["workEmail"] = employee.WorkEmail,
        ["mobile"] = employee.Mobile,
        ["department"] = employee.Department,
        ["designation"] = employee.Designation,
        ["workLocation"] = employee.WorkLocationName,
        ["clientName"] = employee.ClientName,
        ["senderName"] = user.DisplayName
    };

    private static string ValueForSource(string source, CommunicationRecipientOption employee, AuthUser user) => source.Trim() switch
    {
        "Employee.FullName" => employee.EmployeeName, "Employee.FirstName" => employee.FirstName, "Employee.EmployeeCode" => employee.EmployeeCode,
        "Employee.WorkEmail" => employee.WorkEmail, "Employee.Mobile" => employee.Mobile, "Employee.Department" => employee.Department,
        "Employee.Designation" => employee.Designation, "Employee.WorkLocation" => employee.WorkLocationName, "Client.Name" => employee.ClientName,
        "CurrentUser.DisplayName" => user.DisplayName, _ => string.Empty
    };

    private static string Render(string template, IReadOnlyDictionary<string, string> values, bool htmlEncodeValues) => TokenPattern.Replace(template ?? string.Empty, match =>
    {
        var value = values.TryGetValue(match.Groups[1].Value, out var found) ? found : match.Value;
        return htmlEncodeValues ? WebUtility.HtmlEncode(value) : value;
    });

    private static async Task<CommunicationTemplate?> LoadTemplateAsync(MySqlConnection db, long id, int clientId, string channel) =>
        await db.QueryFirstOrDefaultAsync<CommunicationTemplate>(@"SELECT * FROM communication_templates WHERE Id=@id AND Channel=@channel AND IsActive=TRUE AND (ClientId IS NULL OR ClientId=@clientId)", new { id, clientId, channel = CommunicationChannels.Normalize(channel) }) is { } template
            ? await AddVariablesAsync(db, template) : null;

    private static async Task<CommunicationTemplate> AddVariablesAsync(MySqlConnection db, CommunicationTemplate template)
    {
        template.Variables = (await db.QueryAsync<CommunicationTemplateVariable>("SELECT * FROM communication_template_variables WHERE TemplateId=@Id ORDER BY Position", template)).ToList();
        return template;
    }

    private static async Task SaveCampaignFiltersAsync(MySqlConnection db, System.Data.IDbTransaction tx, long campaignId, CommunicationSelectionRequest request)
    {
        async Task Add(string type, int? number = null, string text = "") => await db.ExecuteAsync("INSERT INTO employee_communication_campaign_filters (CampaignId,FilterType,IntegerValue,TextValue) VALUES (@campaignId,@type,@number,@text)", new { campaignId, type, number, text }, tx);
        if (!string.IsNullOrWhiteSpace(request.Search)) await Add("Search", text: request.Search.Trim());
        foreach (var value in request.WorkLocationIds.Distinct()) await Add("WorkLocation", value);
        foreach (var value in CleanList(request.Departments)) await Add("Department", text: value);
        foreach (var value in CleanList(request.Designations)) await Add("Designation", text: value);
        foreach (var value in request.ExcludedEmployeeIds.Distinct()) await Add("ExcludedEmployee", value);
    }

    private static async Task<long> UpsertConversationAsync(MySqlConnection db, System.Data.IDbTransaction tx, int clientId, int employeeId, string channel, string destination, string preview, bool unread = false) =>
        await db.ExecuteScalarAsync<long>(@"INSERT INTO employee_communication_conversations (ClientId,EmployeeId,Channel,Destination,Status,LastMessagePreview,LastMessageAtUtc,UnreadCount)
VALUES (@clientId,@employeeId,@channel,@destination,'Open',@Preview,UTC_TIMESTAMP(),@Unread)
ON DUPLICATE KEY UPDATE Id=LAST_INSERT_ID(Id),Destination=VALUES(Destination),Status='Open',LastMessagePreview=VALUES(LastMessagePreview),LastMessageAtUtc=VALUES(LastMessageAtUtc),UnreadCount=UnreadCount+@Unread,UpdatedAtUtc=UTC_TIMESTAMP(); SELECT LAST_INSERT_ID();",
            new { clientId, employeeId, channel, destination, Preview = PreviewText(preview), Unread = unread ? 1 : 0 }, tx);

    private static async Task<long> QueueEmailAsync(MySqlConnection db, System.Data.IDbTransaction tx, int clientId, long? recipientId, string destination, string subject, string body, long? messageId = null) =>
        await db.ExecuteScalarAsync<long>(@"INSERT INTO notification_queue (RuleId,EventCode,ResourceType,ResourceId,ClientId,ToJson,CcJson,BccJson,Subject,BodyHtml,Status)
VALUES (NULL,'EMPLOYEE_COMMUNICATION',@ResourceType,@ResourceId,@clientId,@ToJson,'[]','[]',@subject,@body,'Pending'); SELECT LAST_INSERT_ID();",
            new { ResourceType = recipientId.HasValue ? "EmployeeCommunicationRecipient" : "EmployeeCommunicationMessage", ResourceId = (recipientId ?? messageId ?? 0).ToString(), clientId, ToJson = JsonSerializer.Serialize(new[] { destination }), subject, body }, tx);

    private async Task<ProviderSecretRow?> FindEffectiveProviderAsync(MySqlConnection db, int clientId, string channel, System.Data.IDbTransaction? tx = null) =>
        await db.QueryFirstOrDefaultAsync<ProviderSecretRow>(@"SELECT * FROM communication_provider_accounts WHERE Channel=@channel AND IsEnabled=TRUE AND DeliveryPaused=FALSE AND (ClientId=@clientId OR ClientId IS NULL) ORDER BY (ClientId=@clientId) DESC,Id DESC LIMIT 1", new { clientId, channel }, tx);

    private ICommunicationChannelSender? FindSender(string channel, string providerCode) => senders.FirstOrDefault(x => x.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase) && x.ProviderCode.Equals(providerCode, StringComparison.OrdinalIgnoreCase));

    private CommunicationProviderContext ToProviderContext(ProviderSecretRow row) => new()
    {
        Account = row,
        ApiKey = Unprotect(row.ApiKeyCipherText),
        AccessToken = Unprotect(row.AccessTokenCipherText),
        WebhookSecret = Unprotect(row.WebhookSecretCipherText)
    };

    private async Task SyncEmailQueueAsync(MySqlConnection db, CancellationToken cancellationToken)
    {
        var rows = (await db.QueryAsync<EmailQueueSyncRow>(@"SELECT m.Id MessageId,m.CampaignRecipientId,m.ConversationId,m.EmailQueueId,q.Status,q.RetryCount,q.ErrorMessage,q.SentAt
FROM employee_communication_messages m JOIN notification_queue q ON q.Id=m.EmailQueueId
WHERE m.Direction='Outbound' AND m.EmailQueueId IS NOT NULL AND m.Status IN ('Queued','Processing','Retry')", commandTimeout: 30)).ToList();
        var campaigns = new HashSet<long>();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = row.Status switch { "Sent" => "Sent", "Failed" => "Failed", "Processing" => "Processing", _ => "Queued" };
            await db.ExecuteAsync("UPDATE employee_communication_messages SET Status=@status,RetryCount=@RetryCount,ErrorMessage=@Error,SentAtUtc=IF(@status='Sent',COALESCE(@SentAt,UTC_TIMESTAMP()),SentAtUtc) WHERE Id=@MessageId", new { status, row.RetryCount, Error = row.ErrorMessage ?? string.Empty, row.SentAt, row.MessageId });
            if (row.CampaignRecipientId.HasValue)
            {
                await db.ExecuteAsync("UPDATE employee_communication_recipients SET Status=@status,RetryCount=@RetryCount,ErrorMessage=@Error,SentAtUtc=IF(@status='Sent',COALESCE(@SentAt,UTC_TIMESTAMP()),SentAtUtc) WHERE Id=@RecipientId", new { status, row.RetryCount, Error = row.ErrorMessage ?? string.Empty, row.SentAt, RecipientId = row.CampaignRecipientId });
                var campaignId = await db.ExecuteScalarAsync<long>("SELECT CampaignId FROM employee_communication_recipients WHERE Id=@Id", new { Id = row.CampaignRecipientId });
                campaigns.Add(campaignId);
                if (status == "Sent") await db.ExecuteAsync(@"INSERT INTO communication_delivery_events (MessageId,CampaignRecipientId,ProviderAccountId,ProviderEventId,EventStatus,OccurredAtUtc)
SELECT @MessageId,@RecipientId,NULL,@EventId,'Sent',COALESCE(@SentAt,UTC_TIMESTAMP())
WHERE NOT EXISTS (SELECT 1 FROM communication_delivery_events WHERE MessageId=@MessageId AND ProviderEventId=@EventId)", new { row.MessageId, RecipientId = row.CampaignRecipientId, EventId = $"email-queue-{row.EmailQueueId}-sent", row.SentAt });
            }
        }
        foreach (var id in campaigns) await RefreshCampaignAsync(db, id);
    }

    private static async Task RecordAttemptAsync(MySqlConnection db, PendingMessageRow message, long providerId, CommunicationSendResult result, DateTime started) =>
        await db.ExecuteAsync(@"INSERT INTO communication_delivery_attempts (MessageId,CampaignRecipientId,AttemptNumber,ProviderAccountId,ProviderRequestId,HttpStatusCode,IsSuccess,ErrorCode,ErrorMessage,SegmentCount,Cost,Currency,AttemptedAtUtc,CompletedAtUtc)
VALUES (@MessageId,@CampaignRecipientId,@AttemptNumber,@ProviderId,@ProviderRequestId,@HttpStatusCode,@Success,@ErrorCode,@ErrorMessage,@SegmentCount,@Cost,@Currency,@Started,UTC_TIMESTAMP())",
            new { MessageId = message.Id, message.CampaignRecipientId, AttemptNumber = message.RetryCount + 1, ProviderId = providerId, result.ProviderRequestId, result.HttpStatusCode, result.Success, result.ErrorCode, result.ErrorMessage, result.SegmentCount, result.Cost, result.Currency, Started = started });

    private static async Task FailMessageAsync(MySqlConnection db, PendingMessageRow message, string errorCode, string error, long? providerId, bool retry = false)
    {
        var status = retry && message.RetryCount + 1 < 5 ? "Retry" : "Failed";
        await db.ExecuteAsync("UPDATE employee_communication_messages SET Status=@status,RetryCount=RetryCount+1,ErrorCode=@errorCode,ErrorMessage=@error WHERE Id=@Id", new { status, errorCode, error, message.Id });
        if (message.CampaignRecipientId.HasValue)
            await db.ExecuteAsync("UPDATE employee_communication_recipients SET Status=@RecipientStatus,RetryCount=RetryCount+1,ErrorCode=@errorCode,ErrorMessage=@error WHERE Id=@RecipientId", new { RecipientStatus = status == "Retry" ? "Queued" : "Failed", errorCode, error, RecipientId = message.CampaignRecipientId });
    }

    private static async Task ApplyMessageStatusAsync(MySqlConnection db, System.Data.IDbTransaction tx, long messageId, long? recipientId, string status, DateTime occurredAt, string errorCode, string error, string providerMessageId)
    {
        var timeColumn = status switch { "Sent" => "SentAtUtc", "Delivered" => "DeliveredAtUtc", "Read" => "ReadAtUtc", _ => string.Empty };
        await db.ExecuteAsync($"UPDATE employee_communication_messages SET Status=@status,ProviderMessageId=@providerMessageId,ErrorCode=@errorCode,ErrorMessage=@error{(timeColumn.Length > 0 ? $",{timeColumn}=@occurredAt" : "")} WHERE Id=@messageId", new { status, providerMessageId, errorCode, error, occurredAt, messageId }, tx);
        if (recipientId.HasValue)
            await db.ExecuteAsync($"UPDATE employee_communication_recipients SET Status=@status,ProviderMessageId=@providerMessageId,ErrorCode=@errorCode,ErrorMessage=@error{(timeColumn.Length > 0 ? $",{timeColumn}=@occurredAt" : "")} WHERE Id=@recipientId", new { status, providerMessageId, errorCode, error, occurredAt, recipientId }, tx);
    }

    private static async Task RefreshCampaignByRecipientAsync(MySqlConnection db, long recipientId)
    {
        var id = await db.ExecuteScalarAsync<long?>("SELECT CampaignId FROM employee_communication_recipients WHERE Id=@recipientId", new { recipientId });
        if (id.HasValue) await RefreshCampaignAsync(db, id.Value);
    }

    private static async Task RefreshCampaignAsync(MySqlConnection db, long id)
    {
        await db.ExecuteAsync(@"UPDATE employee_communication_campaigns c SET
TotalQueued=(SELECT COUNT(*) FROM employee_communication_recipients r WHERE r.CampaignId=c.Id AND r.Status IN ('Pending','Queued','Processing')),
TotalSent=(SELECT COUNT(*) FROM employee_communication_recipients r WHERE r.CampaignId=c.Id AND r.Status IN ('Sent','Delivered','Read')),
TotalDelivered=(SELECT COUNT(*) FROM employee_communication_recipients r WHERE r.CampaignId=c.Id AND r.Status IN ('Delivered','Read')),
TotalRead=(SELECT COUNT(*) FROM employee_communication_recipients r WHERE r.CampaignId=c.Id AND r.Status='Read'),
TotalFailed=(SELECT COUNT(*) FROM employee_communication_recipients r WHERE r.CampaignId=c.Id AND r.Status='Failed'),
Status=CASE
 WHEN EXISTS(SELECT 1 FROM employee_communication_recipients r WHERE r.CampaignId=c.Id AND r.Status IN ('Pending','Queued','Processing')) THEN 'Processing'
 WHEN EXISTS(SELECT 1 FROM employee_communication_recipients r WHERE r.CampaignId=c.Id AND r.Status='Failed') AND EXISTS(SELECT 1 FROM employee_communication_recipients r WHERE r.CampaignId=c.Id AND r.Status IN ('Sent','Delivered','Read')) THEN 'PartiallySent'
 WHEN EXISTS(SELECT 1 FROM employee_communication_recipients r WHERE r.CampaignId=c.Id AND r.Status='Failed') THEN 'Failed'
 ELSE 'Sent' END,
CompletedAtUtc=CASE WHEN EXISTS(SELECT 1 FROM employee_communication_recipients r WHERE r.CampaignId=c.Id AND r.Status IN ('Pending','Queued','Processing')) THEN NULL ELSE UTC_TIMESTAMP() END
WHERE c.Id=@id", new { id });
    }

    private string ProtectIfSupplied(string supplied, string? existing) => string.IsNullOrWhiteSpace(supplied) ? existing ?? string.Empty : credentialProtector.Protect(supplied.Trim());
    private string Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try { return credentialProtector.Unprotect(value); }
        catch (Exception ex) { logger.LogWarning(ex, "A communication provider credential could not be decrypted."); return string.Empty; }
    }

    private static bool SecretsEqual(string expected, string supplied)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(supplied)) return false;
        var left = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static int? ResolveOptionalClient(AuthUser user, int? requested)
    {
        if (user.ClientId.HasValue && requested.HasValue && user.ClientId != requested) throw new InvalidOperationException("Requested client is outside your access scope.");
        return user.ClientId ?? requested;
    }
    private static int? ResolveWriteClient(AuthUser user, int? requested)
    {
        if (user.ClientId.HasValue) return user.ClientId.Value;
        return requested is > 0 ? requested : null;
    }
    private static void EnsureClient(AuthUser user, int clientId)
    {
        if (clientId <= 0) throw new InvalidOperationException("Client is required.");
        if (user.ClientId.HasValue && user.ClientId.Value != clientId) throw new InvalidOperationException("Requested client is outside your access scope.");
    }
    private static bool CanAccessClient(AuthUser user, int? clientId) => !user.ClientId.HasValue || clientId is null || user.ClientId == clientId;
    private static bool CanWriteClient(AuthUser user, int? clientId) => !user.ClientId.HasValue || user.ClientId == clientId;
    private static string RecipientTypeFor(CommunicationSelectionRequest request, int employeeId)
    {
        if (request.BccEmployeeIds.Contains(employeeId)) return "Bcc";
        if (request.CcEmployeeIds.Contains(employeeId)) return "Cc";
        return "To";
    }
    private static async Task<(List<long> AttachmentIds, string Error)> ResolveDraftAttachmentsAsync(MySqlConnection db, long? draftId, int clientId, int userId)
    {
        if (!draftId.HasValue || draftId.Value <= 0) return ([], string.Empty);
        var valid = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM employee_communication_drafts
WHERE Id=@DraftId AND ClientId=@ClientId AND CreatedByUserId=@UserId AND Status='Draft'",
            new { DraftId = draftId.Value, ClientId = clientId, UserId = userId }) > 0;
        if (!valid) return ([], "The attachment draft is unavailable or has already been sent.");
        var ids = (await db.QueryAsync<long>(@"SELECT id FROM entity_attachments
WHERE client_id=@ClientId AND entity_type='EMPLOYEE_COMMUNICATION_DRAFT' AND entity_id=@DraftId
  AND is_current=TRUE AND is_deleted=FALSE ORDER BY uploaded_at_utc,id",
            new { ClientId = clientId, DraftId = draftId.Value })).ToList();
        return (ids, string.Empty);
    }
    private static string NormalizeSelectionMode(string? value) => value?.Equals("AllFiltered", StringComparison.OrdinalIgnoreCase) == true ? "AllFiltered" : "SelectedEmployees";
    private static List<string> CleanList(IEnumerable<string>? values) => (values ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    private static bool IsValidEmail(string? value) => !string.IsNullOrWhiteSpace(value) && System.Net.Mail.MailAddress.TryCreate(value.Trim(), out _);
    private static string CleanCountryCode(string? value) { var digits = Digits(value); return string.IsNullOrWhiteSpace(digits) ? "+91" : $"+{digits}"; }
    private static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
    private static string NormalizeMobile(string? value, string countryCode)
    {
        var raw = value?.Trim() ?? string.Empty; var digits = Digits(raw); if (digits.Length < 8) return string.Empty;
        if (raw.StartsWith('+')) return $"+{digits}";
        digits = digits.TrimStart('0');
        if (digits.Length == 10) digits = Digits(countryCode) + digits;
        return $"+{digits}";
    }
    private static bool SameMobile(string candidate, string normalizedSenderDigits)
    {
        var digits = Digits(candidate).TrimStart('0');
        return digits.Length >= 8 && normalizedSenderDigits.Length >= 8 && (digits.EndsWith(normalizedSenderDigits, StringComparison.Ordinal) || normalizedSenderDigits.EndsWith(digits, StringComparison.Ordinal));
    }
    private static bool IsSafeProviderUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("127.0.0.1"))));
    private static async Task EnsureForeignKeyAsync(MySqlConnection db, string tableName, string constraintName, string definition)
    {
        var exists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS
WHERE CONSTRAINT_SCHEMA=DATABASE() AND TABLE_NAME=@tableName AND CONSTRAINT_NAME=@constraintName", new { tableName, constraintName });
        if (exists == 0) await db.ExecuteAsync($"ALTER TABLE `{tableName}` ADD CONSTRAINT `{constraintName}` {definition}");
    }
    private static async Task EnsureColumnAsync(MySqlConnection db, string tableName, string columnName, string definition)
    {
        var exists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@TableName AND COLUMN_NAME=@ColumnName", new { TableName = tableName, ColumnName = columnName });
        if (exists == 0) await db.ExecuteAsync($"ALTER TABLE `{tableName}` ADD COLUMN `{columnName}` {definition}");
    }
    private static string SanitizeHtml(string html) => Regex.Replace(Regex.Replace(html ?? string.Empty, @"<(script|iframe|object|embed|form)[^>]*>.*?</\1>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline), "\\son[a-z]+\\s*=\\s*(['\\\"]).*?\\1", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline).Trim();
    private static string PreviewText(string value) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(value ?? string.Empty, "<[^>]+>", " ")), "\\s+", " ").Trim() is var text && text.Length > 500 ? text[..500] : text;
    private static string NormalizeDeliveryStatus(string? value) => value?.Trim().ToLowerInvariant() switch { "sent" => "Sent", "delivered" => "Delivered", "read" => "Read", "failed" or "undelivered" => "Failed", _ => string.Empty };

    private sealed class ProviderSecretRow : CommunicationProviderAccount
    {
        public string ApiKeyCipherText { get; set; } = string.Empty;
        public string AccessTokenCipherText { get; set; } = string.Empty;
        public string WebhookSecretCipherText { get; set; } = string.Empty;
    }
    private sealed class PendingMessageRow : EmployeeCommunicationMessage
    {
        public int ClientId { get; set; }
        public string Channel { get; set; } = string.Empty;
        public string Destination { get; set; } = string.Empty;
        public string ProviderTemplateCode { get; set; } = string.Empty;
        public string LanguageCode { get; set; } = "en";
    }
    private sealed class EmailQueueSyncRow
    {
        public long MessageId { get; set; }
        public long? CampaignRecipientId { get; set; }
        public long ConversationId { get; set; }
        public long EmailQueueId { get; set; }
        public string Status { get; set; } = string.Empty;
        public int RetryCount { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public DateTime? SentAt { get; set; }
    }
    private sealed record RecipientFilters(int ClientId, string Search, List<int> WorkLocationIds, List<string> Departments, List<string> Designations, List<int> EmployeeIds, List<int> ExcludedEmployeeIds);
    private sealed record PreparedRecipient(CommunicationRecipientOption Employee, string Destination, string Subject, string Body, bool IsEligible, string ExclusionReason);
}
