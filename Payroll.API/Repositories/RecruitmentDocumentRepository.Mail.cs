using System.Net;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentDocumentRepository
{
    private static Task EnsureMailSchemaAsync(MySqlConnection db) => db.ExecuteAsync("""
CREATE TABLE IF NOT EXISTS recruitment_document_mail_jobs (
 Id CHAR(32) PRIMARY KEY,ClientId INT NOT NULL,ActorUserId INT NOT NULL,Fingerprint CHAR(64) NOT NULL,
 ClientName VARCHAR(255) NOT NULL,CreatedBy VARCHAR(190) NOT NULL,CreatedAtUtc DATETIME(6) NOT NULL,
 DismissedAtUtc DATETIME(6) NULL,ItemsJson JSON NOT NULL,INDEX IX_roster_mail_owner(ActorUserId,ClientId,CreatedAtUtc));
CREATE TABLE IF NOT EXISTS recruitment_document_mail_deliveries (
 Id BIGINT PRIMARY KEY AUTO_INCREMENT,ClientId INT NOT NULL,RosterId BIGINT NOT NULL,ReleaseId BIGINT NOT NULL,
 QueueId BIGINT NULL,Recipient VARCHAR(254) NOT NULL,DeliveryKey CHAR(64) NOT NULL,PdfHash CHAR(64) NOT NULL,
 FileName VARCHAR(180) NOT NULL,CreatedAt DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
 UNIQUE KEY UX_roster_mail_queue(QueueId),INDEX IX_roster_mail_delivery(ClientId,DeliveryKey,Id));
INSERT IGNORE INTO notification_templates(Code,Name,SubjectTemplate,BodyTemplate,IsHtml,IsActive)
VALUES('RECRUITMENT_OFFER_SEND_DEFAULT','Released offer letter','Offer letter - {{candidateName}} - {{clientName}}',
 '<p>Dear {{candidateName}},</p><p>Please find your released offer letter attached.</p><p>Offer reference: <strong>{{offerNumber}}</strong><br>Client / institute: {{workLocation}}<br>Date of joining: {{joiningDate}}</p><p>Regards,<br>{{organizationName}}</p>',TRUE,TRUE);
""");

    private static void MailAccess(AuthUser user,int? clientId=null)
    {
        if(user.Id<=0 || !user.IsActive || user.MustChangePassword || !RecruitmentPermissions.Has(user,"recruitment.offer.issue")) throw new UnauthorizedAccessException();
        if(clientId.HasValue) IssueAccess(user,clientId.Value);
    }

    public async Task<List<RecruitmentOfferMailItem>> PreviewOfferMailAsync(int clientId,RecruitmentOfferMailRequest request,AuthUser user)
    {
        MailAccess(user,clientId);ValidateIds(request.Ids);
        await using var db=Db();await db.OpenAsync();await EnsureAsync(db);
        await using var tx=await db.BeginTransactionAsync();
        await db.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=@Id AND IsActive=TRUE FOR UPDATE",new{Id=clientId},tx);
        return await ReviewOfferMailAsync(db,tx,clientId,request,user);
    }

    public async Task<RecruitmentOfferMailJob> SubmitOfferMailAsync(int clientId,RecruitmentOfferMailRequest request,AuthUser user)
    {
        MailAccess(user,clientId);ValidateIds(request.Ids);
        if(!Guid.TryParse(request.RequestId,out var requestId)) throw new InvalidOperationException("Supply a unique request ID for this send action.");
        if(request.ReleaseIds is null || request.ReleaseIds.Count!=request.Ids.Count || request.Ids.Any(id=>!request.ReleaseIds.ContainsKey(id.ToString())))
            throw new InvalidOperationException("Review the selected released letters before sending.");
        var id=requestId.ToString("N");
        var fingerprint=Hash(JsonSerializer.Serialize(new{ClientId=clientId,Ids=request.Ids.Order().ToArray(),request.RetryFailed,ReleaseIds=request.ReleaseIds.OrderBy(pair=>pair.Key).ToArray()},Json));
        await using var db=Db();await db.OpenAsync();await EnsureAsync(db);
        await using var tx=await db.BeginTransactionAsync();
        var clientName=await db.ExecuteScalarAsync<string>("SELECT Name FROM clients WHERE Id=@Id AND IsActive=TRUE FOR UPDATE",new{Id=clientId},tx)
            ?? throw new InvalidOperationException("Select an active client.");
        var previous=await db.QuerySingleOrDefaultAsync<StoredRosterMailJob>("SELECT * FROM recruitment_document_mail_jobs WHERE Id=@Id FOR UPDATE",new{Id=id},tx);
        if(previous is not null)
        {
            if(previous.ActorUserId!=user.Id || previous.ClientId!=clientId || previous.Fingerprint!=fingerprint) throw new InvalidOperationException("This send request was already used for another selection. Refresh before sending again.");
            var restored=JsonSerializer.Deserialize<List<RecruitmentOfferMailItem>>(previous.ItemsJson,Json) ?? [];
            await RequireMailLocationsAsync(db,clientId,restored,user,tx);
            return await BuildStoredMailJobAsync(db,previous,tx);
        }
        var items=await ReviewOfferMailAsync(db,tx,clientId,request,user);
        if(items.Any(item=>item.ReleaseId.GetValueOrDefault()!=request.ReleaseIds[item.RosterId.ToString()]))
            throw new InvalidOperationException("A released letter changed after review. Refresh the email preview before sending.");
        NotificationTemplate? template=null;
        if(items.Any(item=>item.Status=="Ready"))
        {
            if(configuration.GetValue<bool>("OutboundDelivery:Suppressed")) throw new InvalidOperationException("Outbound delivery is suppressed for this application instance.");
            if(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM notification_smtp_settings WHERE Id=1 AND IsEnabled=TRUE AND DeliveryPaused=FALSE AND Host<>'' AND FromEmail<>''",transaction:tx)!=1)
                throw new InvalidOperationException("Email delivery is not configured or is paused. Review Settings > Notifications.");
            template=await db.QuerySingleOrDefaultAsync<NotificationTemplate>("SELECT * FROM notification_templates WHERE Code='RECRUITMENT_OFFER_SEND_DEFAULT' AND IsActive=TRUE",transaction:tx);
            if(template is null || string.IsNullOrWhiteSpace(template.SubjectTemplate) || string.IsNullOrWhiteSpace(template.BodyTemplate)) throw new InvalidOperationException("Configure the active RECRUITMENT_OFFER_SEND_DEFAULT email template in Settings > Notifications.");
        }
        var organizationName=await db.ExecuteScalarAsync<string>("SELECT Name FROM organizations ORDER BY Id LIMIT 1",transaction:tx) ?? "";
        foreach(var item in items.Where(item=>item.Status=="Ready"))
        {
            var release=await db.QuerySingleAsync<RosterMailRelease>("SELECT Id ReleaseId,RosterId,PdfHash,FileName,OfferNumber,InputsHash,TemplateHash,LocationHash FROM recruitment_document_roster_releases WHERE Id=@Id AND ClientId=@ClientId",new{Id=item.ReleaseId,ClientId=clientId},tx);
            var row=await db.QuerySingleAsync<RecruitmentDocumentRosterRow>("SELECT CAST(ValuesJson AS CHAR) ValuesJson FROM recruitment_document_roster WHERE Id=@Id AND ClientId=@ClientId",new{Id=item.RosterId,ClientId=clientId},tx);
            var values=ReadValues(row.ValuesJson);
            var location=await ActiveLocationAsync(db,clientId,item.WorkLocationId!.Value,tx);
            var content=NotificationRepository.PrepareReleasedOfferEmail(template!,item,clientName,location.Name,organizationName,release.OfferNumber,values.GetValueOrDefault("joiningDate",""));
            var delivery=await db.ExecuteScalarAsync<long>("""
INSERT INTO recruitment_document_mail_deliveries(ClientId,RosterId,ReleaseId,Recipient,DeliveryKey,PdfHash,FileName)
VALUES(@ClientId,@RosterId,@ReleaseId,@Email,@Key,@PdfHash,@FileName);SELECT LAST_INSERT_ID();
""",new{ClientId=clientId,item.RosterId,item.ReleaseId,item.Email,Key=OfferDeliveryKey(item.ReleaseId!.Value,item.Email),release.PdfHash,release.FileName},tx);
            var queueId=await db.ExecuteScalarAsync<long>("""
INSERT INTO notification_queue(RuleId,EventCode,ResourceType,ResourceId,ClientId,ToJson,CcJson,BccJson,Subject,BodyHtml,Status)
VALUES(NULL,@EventCode,'RecruitmentRosterOffer',@ResourceId,@ClientId,@ToJson,'[]','[]',@Subject,@BodyHtml,@Status);SELECT LAST_INSERT_ID();
""",new{EventCode=NotificationRepository.RosterOfferEvent,ResourceId=delivery.ToString(),ClientId=clientId,ToJson=JsonSerializer.Serialize(new[]{item.Email}),content.Subject,content.BodyHtml,Status=NotificationRepository.RosterOfferPending},tx);
            await db.ExecuteAsync("UPDATE recruitment_document_mail_deliveries SET QueueId=@QueueId WHERE Id=@Id AND ClientId=@ClientId",new{QueueId=queueId,Id=delivery,ClientId=clientId},tx);
            item.QueueId=queueId;item.Status="Queued";item.Message="Released PDF queued to this recipient. Delivery continues in the background.";
        }
        var stored=new StoredRosterMailJob{Id=id,ClientId=clientId,ActorUserId=user.Id,Fingerprint=fingerprint,ClientName=clientName,CreatedBy=user.DisplayName,CreatedAtUtc=DateTime.UtcNow,ItemsJson=JsonSerializer.Serialize(items,Json)};
        await db.ExecuteAsync("INSERT INTO recruitment_document_mail_jobs(Id,ClientId,ActorUserId,Fingerprint,ClientName,CreatedBy,CreatedAtUtc,ItemsJson) VALUES(@Id,@ClientId,@ActorUserId,@Fingerprint,@ClientName,@CreatedBy,@CreatedAtUtc,@ItemsJson)",stored,tx);
        await tx.CommitAsync();return await BuildStoredMailJobAsync(db,stored);
    }

    private static string OfferDeliveryKey(long releaseId,string email)=>Hash(releaseId+"|"+email.Trim().ToLowerInvariant());

    private static async Task<List<RecruitmentOfferMailItem>> ReviewOfferMailAsync(MySqlConnection db,MySqlTransaction tx,int clientId,RecruitmentOfferMailRequest request,AuthUser user)
    {
        var rows=(await db.QueryAsync<RecruitmentDocumentRosterRow>("""
SELECT r.*,CAST(r.ValuesJson AS CHAR) ValuesJson,c.CandidateCode,TRIM(CONCAT(c.FirstName,' ',c.LastName)) CandidateName,c.Email,c.Phone,c.ProfileStatus
FROM recruitment_document_roster r JOIN recruitment_candidates c ON c.Id=r.CandidateId AND c.ClientId=r.ClientId
WHERE r.ClientId=@ClientId AND r.Id IN @Ids ORDER BY r.Id
""",new{ClientId=clientId,request.Ids},tx)).ToList();
        var locations=await RecruitmentAccessScope.WorkLocationsAsync(db,user,clientId,tx);
        if(rows.Count!=request.Ids.Count || rows.Any(row=>!CanSeeLocation(user,row.WorkLocationId,locations))) throw new UnauthorizedAccessException();
        var clientName=await db.ExecuteScalarAsync<string>("SELECT Name FROM clients WHERE Id=@Id",new{Id=clientId},tx) ?? "";
        var organizationName=await db.ExecuteScalarAsync<string>("SELECT Name FROM organizations ORDER BY Id LIMIT 1",transaction:tx) ?? "";
        var items=new List<RecruitmentOfferMailItem>();
        foreach(var row in rows)
        {
            var item=new RecruitmentOfferMailItem{RosterId=row.Id,WorkLocationId=row.WorkLocationId,CandidateCode=row.CandidateCode,CandidateName=row.CandidateName,Email=row.Email};items.Add(item);
            var release=await db.QueryFirstOrDefaultAsync<RosterMailRelease>("SELECT Id ReleaseId,RosterId,InputsHash,TemplateHash,LocationHash FROM recruitment_document_roster_releases WHERE RosterId=@Id AND ClientId=@ClientId ORDER BY ReleasedAt DESC,Id DESC LIMIT 1",new{row.Id,ClientId=clientId},tx);
            item.ReleaseId=release?.ReleaseId;
            var location=locations.FirstOrDefault(location=>location.Id==row.WorkLocationId);
            var template=await db.QuerySingleOrDefaultAsync<RecruitmentTemplate>("SELECT * FROM recruitment_templates WHERE Id=@Id AND ClientId IN (0,@ClientId) AND IsActive=TRUE",new{Id=row.TemplateId,ClientId=clientId},tx);
            var values=ReadValues(row.ValuesJson);if(location is not null)ApplyLocationValues(values,location);
            PrepareReleaseValues(row,values,clientName,organizationName,row.OfferNumber);
            if(release is null || location is null || row.ProfileStatus=="Archived" || template is null || Hash(SerializeValues(values))!=release.InputsHash || Hash(template.BodyTemplate)!=release.TemplateHash || Hash(JsonSerializer.Serialize(location,Json))!=release.LocationHash)
            {item.Status="Not released";item.Message="Release the current offer details before emailing. Draft or outdated PDFs are not sent.";continue;}
            if(!ExcelPayslipRepository.TryEmail(row.Email,out var recipient))
            {item.Status="Missing email";item.Message="Add one valid candidate email address, then release the updated letter before sending.";continue;}
            item.Email=recipient;
            var previous=await db.QueryFirstOrDefaultAsync<RosterMailQueue>("""
SELECT q.*,(SELECT MAX(l.CreatedAt) FROM notification_logs l WHERE l.QueueId=q.Id AND l.Status='Sent') LastSentAt
FROM recruitment_document_mail_deliveries d LEFT JOIN notification_queue q ON q.Id=d.QueueId AND q.ClientId=d.ClientId
WHERE d.ClientId=@ClientId AND d.DeliveryKey=@Key ORDER BY d.Id DESC LIMIT 1
""",new{ClientId=clientId,Key=OfferDeliveryKey(release.ReleaseId,recipient)},tx);
            if(previous is not null)
            {
                if(previous.SentAt.HasValue || previous.LastSentAt.HasValue || previous.Status=="Sent" || IsOfferPending(previous.Status))
                {item.Status="Skipped";item.Message="This released letter is already sent or queued to this recipient. No duplicate email will be sent.";continue;}
                if(previous.Status!="Failed" || !request.RetryFailed)
                {item.Status=previous.Status=="Failed"?"Failed":"Unknown";item.Message=previous.Status=="Failed"?"Previous delivery failed. Use Retry failed after reviewing the recipient.":"Previous delivery cannot be verified; review its notification log before retrying.";continue;}
            }
            item.Status="Ready";item.Message="Ready to email the saved released PDF.";
        }
        return items;
    }

    public async Task<List<RecruitmentOfferMailJob>> OwnOfferMailJobsAsync(AuthUser user,int? clientId=null)
    {
        MailAccess(user,clientId);await using var db=Db();await db.OpenAsync();await EnsureAsync(db);
        var jobs=await db.QueryAsync<StoredRosterMailJob>("SELECT * FROM recruitment_document_mail_jobs WHERE ActorUserId=@Actor AND (@ClientId IS NULL OR ClientId=@ClientId) ORDER BY CreatedAtUtc DESC LIMIT 100",new{Actor=user.Id,ClientId=clientId});
        var result=new List<RecruitmentOfferMailJob>();
        foreach(var job in jobs)
        {
            try{IssueAccess(user,job.ClientId);await RequireMailLocationsAsync(db,job.ClientId,JsonSerializer.Deserialize<List<RecruitmentOfferMailItem>>(job.ItemsJson,Json) ?? [],user);}
            catch(UnauthorizedAccessException){continue;}
            var summary=await BuildStoredMailJobAsync(db,job);if(!clientId.HasValue)summary.Items=[];result.Add(summary);
        }
        return result;
    }

    public async Task DismissOfferMailJobAsync(int clientId,string id,AuthUser user)
    {
        MailAccess(user,clientId);if(!Guid.TryParseExact(id,"N",out _))throw new InvalidOperationException("Email job not found.");
        await using var db=Db();await db.OpenAsync();await EnsureAsync(db);await using var tx=await db.BeginTransactionAsync();
        var job=await db.QuerySingleOrDefaultAsync<StoredRosterMailJob>("SELECT * FROM recruitment_document_mail_jobs WHERE Id=@Id AND ClientId=@ClientId AND ActorUserId=@Actor FOR UPDATE",new{Id=id,ClientId=clientId,Actor=user.Id},tx) ?? throw new UnauthorizedAccessException();
        await RequireMailLocationsAsync(db,clientId,JsonSerializer.Deserialize<List<RecruitmentOfferMailItem>>(job.ItemsJson,Json) ?? [],user,tx);
        if(!(await BuildStoredMailJobAsync(db,job,tx)).IsComplete)throw new InvalidOperationException("An ongoing email job cannot be dismissed.");
        await db.ExecuteAsync("UPDATE recruitment_document_mail_jobs SET DismissedAtUtc=UTC_TIMESTAMP(6) WHERE Id=@Id",new{Id=id},tx);await tx.CommitAsync();
    }

    private static async Task RequireMailLocationsAsync(MySqlConnection db,int clientId,List<RecruitmentOfferMailItem> items,AuthUser user,MySqlTransaction? tx=null)
    {
        var locations=await RecruitmentAccessScope.WorkLocationsAsync(db,user,clientId,tx);
        if(items.Any(item=>!CanSeeLocation(user,item.WorkLocationId,locations)))throw new UnauthorizedAccessException();
    }

    private static async Task<RecruitmentOfferMailJob> BuildStoredMailJobAsync(MySqlConnection db,StoredRosterMailJob stored,MySqlTransaction? tx=null)
    {
        var items=JsonSerializer.Deserialize<List<RecruitmentOfferMailItem>>(stored.ItemsJson,Json) ?? [];
        var ids=items.Where(item=>item.QueueId.HasValue).Select(item=>item.QueueId!.Value).ToArray();
        var queues=ids.Length==0 ? [] : (await db.QueryAsync<RosterMailQueue>("SELECT * FROM notification_queue WHERE ClientId=@ClientId AND Id IN @Ids AND EventCode=@Event AND ResourceType='RecruitmentRosterOffer'",new{stored.ClientId,Ids=ids,Event=NotificationRepository.RosterOfferEvent},tx)).ToArray();
        var logs=ids.Length==0 ? [] : (await db.QueryAsync<NotificationLog>("SELECT * FROM notification_logs WHERE QueueId IN @Ids AND EventCode=@Event AND ResourceType='RecruitmentRosterOffer' ORDER BY CreatedAt DESC,Id DESC",new{Ids=ids,Event=NotificationRepository.RosterOfferEvent},tx)).ToArray();
        return BuildOfferMailJob(stored.Id,stored.ClientId,stored.ClientName,stored.CreatedBy,stored.CreatedAtUtc,stored.DismissedAtUtc,items,queues,logs);
    }

    internal static bool IsOfferPending(string status)=>status is NotificationRepository.RosterOfferPending or NotificationRepository.RosterOfferRetry or "Processing" or "Sending";
    private static bool IsOfferRecipient(string json,string email)
    {
        try{var recipients=JsonSerializer.Deserialize<string[]>(json);return recipients is {Length:1} && recipients[0].Equals(email,StringComparison.OrdinalIgnoreCase);}
        catch(JsonException){return false;}
    }
    internal static RecruitmentOfferMailJob BuildOfferMailJob(string id,int clientId,string clientName,string actor,DateTime created,DateTime? dismissed,List<RecruitmentOfferMailItem> items,IEnumerable<RosterMailQueue> queues,IEnumerable<NotificationLog> logs)
    {
        var summary=new RecruitmentOfferMailJob{Id=id,ClientId=clientId,ClientName=clientName,CreatedBy=actor,CreatedAtUtc=DateTime.SpecifyKind(created,DateTimeKind.Utc),DismissedAtUtc=dismissed is null?null:DateTime.SpecifyKind(dismissed.Value,DateTimeKind.Utc),Total=items.Count,Items=items};
        var lookup=queues.ToDictionary(queue=>queue.Id);var processing=false;var oldProcessing=false;
        foreach(var item in items)
        {
            if(item.QueueId.HasValue)
            {
                summary.TotalEmails++;
                if(!lookup.TryGetValue(item.QueueId.Value,out var queue) || queue.ClientId!=clientId || queue.EventCode!=NotificationRepository.RosterOfferEvent || queue.ResourceType!="RecruitmentRosterOffer" || !IsOfferRecipient(queue.ToJson,item.Email)){item.Status="Unknown";item.Message="Queued delivery could not be verified.";}
                else
                {
                    var history=logs.Where(log=>log.QueueId==queue.Id && log.ResourceId==queue.ResourceId && log.Recipient.Equals(item.Email,StringComparison.OrdinalIgnoreCase)).OrderByDescending(log=>log.CreatedAt).ThenByDescending(log=>log.Id).ToArray();
                    var sent=history.FirstOrDefault(log=>log.Status=="Sent");
                    var accepted=queue.SentAt.HasValue || sent is not null || queue.Status=="Sent";
                    if(accepted)
                    {
                        summary.AcceptedEmails++;
                        var archive=history.FirstOrDefault(log=>log.Status.StartsWith("SentCopy",StringComparison.Ordinal) && sent is not null && log.ProviderMessageId==sent.ProviderMessageId && log.CreatedAt>=sent.CreatedAt);
                        item.SentCopyStatus=archive?.Status[8..] ?? "Unknown";
                        if(item.SentCopyStatus=="Saved")summary.SentCopySavedEmails++;else summary.SentCopyUnverifiedEmails++;
                        var report=history.FirstOrDefault(log=>log.Status is "Bounced" or "Deferred" or "Delivered" && sent is not null && log.ProviderMessageId==sent.ProviderMessageId && log.CreatedAt>=sent.CreatedAt);
                        item.DeliveryStatus=report?.Status ?? "";if(item.DeliveryStatus=="Delivered")summary.DeliveredEmails++;
                    }
                    item.Status=item.DeliveryStatus=="Bounced"?"Failed":item.DeliveryStatus=="Deferred"?"Unknown":accepted?"Sent":IsOfferPending(queue.Status)?"Queued":queue.Status=="Failed"?"Failed":"Unknown";
                    item.Message=accepted?item.DeliveryStatus=="Bounced"?"SMTP accepted the email, but a bounce was received. Review the address before retrying.":"SMTP accepted the email. Sent-folder copy and recipient delivery are tracked separately.":queue.ErrorMessage;
                    processing|=queue.Status is "Processing" or "Sending";
                    oldProcessing|=queue.Status is "Processing" or "Sending" && queue.CreatedAt<DateTime.UtcNow.AddMinutes(-30);
                }
            }
            switch(item.Status){case "Sent":summary.Sent++;break;case "Queued":summary.Queued++;break;case "Failed":summary.Failed++;break;case "Missing email":summary.MissingEmail++;break;case "Not released":summary.NotReleased++;break;case "Skipped":summary.Skipped++;break;default:summary.Unknown++;break;}
        }
        summary.Completed=summary.Total-summary.Queued;summary.PercentComplete=summary.Total==0?100:(int)(100m*summary.Completed/summary.Total);summary.IsComplete=summary.Queued==0;
        summary.Status=oldProcessing || summary.Failed+summary.MissingEmail+summary.NotReleased+summary.Unknown+summary.SentCopyUnverifiedEmails>0?"Needs attention":summary.IsComplete?"Completed":processing?"Processing":"Queued";
        summary.Message=oldProcessing?"An older email is still processing. Review its notification log before retrying; delivery may already have occurred.":summary.IsComplete?"Email processing finished. Review failed, missing-email or unreleased rows separately. SMTP acceptance is not proof of recipient delivery.":"Offer email delivery continues in the background. You may leave this page.";
        return summary;
    }

    private sealed class StoredRosterMailJob
    {
        public string Id{get;set;}="";public int ClientId{get;set;}public int ActorUserId{get;set;}public string Fingerprint{get;set;}="";public string ClientName{get;set;}="";public string CreatedBy{get;set;}="";public DateTime CreatedAtUtc{get;set;}public DateTime? DismissedAtUtc{get;set;}public string ItemsJson{get;set;}="[]";
    }
    private sealed class RosterMailRelease
    {
        public long ReleaseId{get;set;}public long RosterId{get;set;}public string InputsHash{get;set;}="";public string TemplateHash{get;set;}="";public string LocationHash{get;set;}="";public string PdfHash{get;set;}="";public string FileName{get;set;}="";public string OfferNumber{get;set;}="";
    }
    internal sealed class RosterMailQueue : NotificationQueueItem { public DateTime? LastSentAt{get;set;} }
}
