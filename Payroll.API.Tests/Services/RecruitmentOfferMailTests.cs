using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using MimeKit;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class RecruitmentOfferMailTests
{
    [Fact]
    public void Onboarding_copies_available_identity_posting_and_bank_but_does_not_invent_ctc_or_portal_access()
    {
        var row=new RecruitmentDocumentRosterRow{ClientId=7,WorkLocationId=9,CandidateCode="SYNTH-17",CandidateName="Test Person",Email="test@example.invalid",Phone="9000000017"};
        var values=new Dictionary<string,string>{["joiningDate"]="2026-11-02",["monthlyGross"]="70000",["monthlyBasic"]="50000",["accountNo"]="000000000017",["ifsc"]="TEST0000017",["positionTitle"]="Researcher"};
        var employee=RecruitmentDocumentRepository.BuildRosterEmployee(row,values,new(){Id=9,ClientId=7,Name="Test Institute",City="Test City"});
        Assert.Equal("SYNTH-17",employee.EmployeeCode);Assert.Equal("2026-11-02",employee.DateOfJoining);Assert.Equal("Researcher",employee.Designation);Assert.Equal("000000000017",employee.PaymentDetails.BankAccountNo);Assert.Equal("TEST0000017",employee.PaymentDetails.IfscCode);
        Assert.Equal(0,employee.AnnualCtc);Assert.False(employee.PortalAccess);Assert.Empty(employee.SalaryStructureId);Assert.Empty(employee.SalaryComponents);
        values["annualCtc"]="8,40,000";Assert.Equal(840000,RecruitmentDocumentRepository.BuildRosterEmployee(row,values,new(){Id=9,ClientId=7}).AnnualCtc);
        values["joiningDate"]="";Assert.Throws<InvalidOperationException>(()=>RecruitmentDocumentRepository.BuildRosterEmployee(row,values,new(){Id=9,ClientId=7}));
    }

    [Fact]
    public void Linking_preserves_existing_master_salary_and_bank_values()
    {
        var master=new Employee{EmployeeCode="EMP-1",FirstName="Test",LastName="Person",AnnualCtc=900000,DateOfJoining="2021-04-01",WorkLocationId=9,SalaryStructureId="existing",PaymentDetails=new(){BankAccountNo="001",IfscCode="OLD"}};
        var incoming=new Employee{EmployeeCode="CAND-1",FirstName="Test",LastName="Person",AnnualCtc=500000,DateOfJoining="2026-11-02",PaymentDetails=new(){BankAccountNo="002",BankName="Available bank",IfscCode="NEW"},PersonalDetails=new(){Mobile="9000000001"}};
        var merged=RecruitmentDocumentRepository.FillRosterEmployeeBlanks(master,incoming);
        Assert.Equal("EMP-1",merged.EmployeeCode);Assert.Equal(900000,merged.AnnualCtc);Assert.Equal("2021-04-01",merged.DateOfJoining);Assert.Equal("001",merged.PaymentDetails.BankAccountNo);Assert.Equal("OLD",merged.PaymentDetails.IfscCode);Assert.Equal("Available bank",merged.PaymentDetails.BankName);Assert.Equal("9000000001",merged.PersonalDetails.Mobile);
    }

    [Fact]
    public void Progress_separates_smtp_sent_copy_delivery_and_rows_needing_attention()
    {
        var now=DateTime.UtcNow;var items=new List<RecruitmentOfferMailItem>{new(){RosterId=1,QueueId=10,Email="a@example.invalid"},new(){RosterId=2,QueueId=11,Email="b@example.invalid"},new(){RosterId=3,Status="Missing email"},new(){RosterId=4,Status="Not released"},new(){RosterId=5,Status="Skipped"}};
        var sent=Queue(10,"Sent");sent.SentAt=now;var pending=Queue(11,NotificationRepository.RosterOfferPending);
        var logs=new[]{Log(10,"a@example.invalid","Sent",now),Log(10,"a@example.invalid","SentCopySaved",now.AddSeconds(1)),Log(10,"a@example.invalid","Delivered",now.AddSeconds(2))};
        var result=RecruitmentDocumentRepository.BuildOfferMailJob("job",7,"Client","Sender",now,null,items,[sent,pending],logs);
        Assert.Equal(5,result.Total);Assert.Equal(2,result.TotalEmails);Assert.Equal(1,result.AcceptedEmails);Assert.Equal(1,result.SentCopySavedEmails);Assert.Equal(1,result.DeliveredEmails);Assert.Equal(1,result.Sent);Assert.Equal(1,result.Queued);Assert.Equal(1,result.MissingEmail);Assert.Equal(1,result.NotReleased);Assert.Equal(1,result.Skipped);Assert.Equal(80,result.PercentComplete);Assert.False(result.IsComplete);
    }

    [Fact]
    public void Accepted_email_with_failed_sent_copy_remains_sent_and_bounce_is_separate()
    {
        var now=DateTime.UtcNow;var sent=Queue(10,"Sent");sent.SentAt=now;
        var logs=new[]{Log(10,"a@example.invalid","Sent",now),Log(10,"a@example.invalid","SentCopyFailed",now.AddSeconds(1))};
        var result=RecruitmentDocumentRepository.BuildOfferMailJob("job",7,"Client","Sender",now,null,[new(){RosterId=1,QueueId=10,Email="a@example.invalid"}],[sent],logs);
        Assert.Equal(1,result.Sent);Assert.Equal(0,result.Failed);Assert.Equal(1,result.SentCopyUnverifiedEmails);Assert.True(result.IsComplete);
        var bounced=RecruitmentDocumentRepository.BuildOfferMailJob("job",7,"Client","Sender",now,null,[new(){RosterId=1,QueueId=10,Email="a@example.invalid"}],[sent],logs.Append(Log(10,"a@example.invalid","Bounced",now.AddSeconds(2))));
        Assert.Equal(1,bounced.Failed);Assert.Equal(1,bounced.AcceptedEmails);Assert.Equal(0,bounced.Sent);
    }

    [Fact]
    public void Attachment_is_the_exact_released_pdf_and_tampering_never_sends_a_bare_email()
    {
        var bytes=Encoding.ASCII.GetBytes("%PDF-1.7\nsynthetic immutable attachment");var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var row=Queue(10,NotificationRepository.RosterOfferPending);row.ToJson="[\"a@example.invalid\"]";
        var source=new NotificationRepository.ReleasedOfferAttachment{Id=10,QueueId=10,ClientId=7,ReleaseClientId=7,RosterId=1,ReleaseRosterId=1,ReleaseId=2,Recipient="a@example.invalid",PdfHash=hash,ReleasePdfHash=hash,PdfBytes=bytes,FileName="offer.pdf"};
        var builder=new BodyBuilder();NotificationRepository.AttachVerifiedReleasedOffer(builder,row,source);Assert.Single(builder.Attachments);Assert.Equal("application/pdf",builder.Attachments[0].ContentType.MimeType);
        row.ToJson="[\"other@example.invalid\"]";Assert.Throws<InvalidOperationException>(()=>NotificationRepository.AttachVerifiedReleasedOffer(new(),row,source));
        row.ToJson="[\"a@example.invalid\"]";source.ReleaseClientId=8;Assert.Throws<InvalidOperationException>(()=>NotificationRepository.AttachVerifiedReleasedOffer(new(),row,source));
        source.ReleaseClientId=7;source.PdfBytes[8]^=1;Assert.Throws<InvalidOperationException>(()=>NotificationRepository.AttachVerifiedReleasedOffer(new(),row,source));
    }

    [Fact]
    public async Task Offer_send_and_job_access_require_issue_permission_and_authoritative_client_scope_before_io()
    {
        var repository=new RecruitmentDocumentRepository(new ConfigurationBuilder().Build(),null!,new());
        var user=new AuthUser{Id=1,IsActive=true,ClientAccessMode="Selected",AssignedClientIds=[7],Permissions=["recruitment.offer.issue"]};
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>repository.PreviewOfferMailAsync(8,new(){Ids=[1]},user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>repository.SubmitOfferMailAsync(8,new(){Ids=[1]},user));
        user.Permissions=["recruitment.candidate.manage"];
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>repository.OwnOfferMailJobsAsync(user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>repository.SubmitOfferMailAsync(7,new(){Ids=[1]},user));
    }

    private static RecruitmentDocumentRepository.RosterMailQueue Queue(long id,string status)=>new(){Id=id,ClientId=7,EventCode=NotificationRepository.RosterOfferEvent,ResourceType="RecruitmentRosterOffer",ResourceId=id.ToString(),Status=status,CreatedAt=DateTime.UtcNow,ToJson=id==10?"[\"a@example.invalid\"]":"[\"b@example.invalid\"]"};
    private static NotificationLog Log(long id,string recipient,string status,DateTime time)=>new(){QueueId=id,ResourceId=id.ToString(),ResourceType="RecruitmentRosterOffer",EventCode=NotificationRepository.RosterOfferEvent,Recipient=recipient,Status=status,ProviderMessageId="synthetic-"+id,CreatedAt=time};
}
