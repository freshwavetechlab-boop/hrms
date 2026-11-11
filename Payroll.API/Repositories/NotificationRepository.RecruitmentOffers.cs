using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MimeKit;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class NotificationRepository
{
    internal const string RosterOfferEvent="RECRUITMENT_ROSTER.OFFER_SEND";
    // An older worker must not send an offer while silently omitting its PDF.
    internal const string RosterOfferPending="RosterOfferPending";
    internal const string RosterOfferRetry="RosterOfferRetry";

    internal static (string Subject,string BodyHtml) PrepareReleasedOfferEmail(NotificationTemplate template,
        RecruitmentOfferMailItem item,string client,string location,string organization,string offerNumber,string joiningDate)
    {
        if(!template.IsActive || !ExcelPayslipRepository.TryEmail(item.Email,out _))throw new InvalidOperationException("An active offer email template and one valid recipient are required.");
        var values=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){["candidateName"]=item.CandidateName,["candidateCode"]=item.CandidateCode,["clientName"]=client,["workLocation"]=location,["organizationName"]=organization,["offerNumber"]=offerNumber,["joiningDate"]=joiningDate,["recipientEmail"]=item.Email};
        var subject=Regex.Replace(WebUtility.HtmlDecode(Render(template.SubjectTemplate,values)),@"[\r\n\t]+"," ").Trim();
        if(subject.Length==0)throw new InvalidOperationException("The offer email subject is empty.");
        var body=template.IsHtml?Render(template.BodyTemplate,values):"<p>"+Render(WebUtility.HtmlEncode(template.BodyTemplate),values).Replace("\r\n","\n").Replace("\n","<br />")+"</p>";
        return(subject,body);
    }

    private static async Task AttachReleasedOfferAsync(MySqlConnection db,NotificationQueueItem row,BodyBuilder builder)
    {
        if(!long.TryParse(row.ResourceId,out var id))throw new InvalidOperationException("The queued offer source is invalid.");
        var stored=await db.QuerySingleOrDefaultAsync<ReleasedOfferAttachment>("""
SELECT d.Id,d.ClientId,d.RosterId,d.ReleaseId,d.QueueId,d.Recipient,d.PdfHash,d.FileName,
 r.PdfHash ReleasePdfHash,r.PdfBytes,r.ClientId ReleaseClientId,r.RosterId ReleaseRosterId
FROM recruitment_document_mail_deliveries d JOIN recruitment_document_roster_releases r ON r.Id=d.ReleaseId
WHERE d.Id=@Id AND d.QueueId=@QueueId AND d.ClientId=@ClientId
""",new{Id=id,QueueId=row.Id,row.ClientId});
        if(stored is null)throw new InvalidOperationException("The queued released offer is unavailable.");
        AttachVerifiedReleasedOffer(builder,row,stored);
    }

    internal static void AttachVerifiedReleasedOffer(BodyBuilder builder,NotificationQueueItem row,ReleasedOfferAttachment stored)
    {
        var recipients=ReadEmailArray(row.ToJson);
        if(row.EventCode!=RosterOfferEvent || row.ResourceType!="RecruitmentRosterOffer" || row.ResourceId!=stored.Id.ToString()
            || stored.QueueId!=row.Id || stored.ClientId!=row.ClientId || stored.ReleaseClientId!=stored.ClientId || stored.ReleaseRosterId!=stored.RosterId
            || recipients.Count!=1 || !recipients[0].Equals(stored.Recipient,StringComparison.OrdinalIgnoreCase)
            || !ExcelPayslipRepository.TryEmail(stored.Recipient,out _) || ReadEmailArray(row.CcJson).Count!=0 || ReadEmailArray(row.BccJson).Count!=0
            || stored.PdfBytes.Length is <8 or >10*1024*1024 || !stored.PdfBytes.AsSpan(0,5).SequenceEqual("%PDF-"u8)
            || stored.PdfHash!=stored.ReleasePdfHash || Convert.ToHexString(SHA256.HashData(stored.PdfBytes)).ToLowerInvariant()!=stored.PdfHash)
            throw new InvalidOperationException("The queued offer recipient or released PDF failed its integrity check.");
        builder.Attachments.Add(RecruitmentDocumentRepository.SafeFileName(stored.FileName),stored.PdfBytes,new ContentType("application","pdf"));
        EmbedExcelPayslipImages(builder);
    }

    internal sealed class ReleasedOfferAttachment
    {
        public long Id{get;set;}public int ClientId{get;set;}public long RosterId{get;set;}public long ReleaseId{get;set;}public long QueueId{get;set;}
        public string Recipient{get;set;}="";public string PdfHash{get;set;}="";public string ReleasePdfHash{get;set;}="";public string FileName{get;set;}="";public byte[] PdfBytes{get;set;}=[];public int ReleaseClientId{get;set;}public long ReleaseRosterId{get;set;}
    }
}
