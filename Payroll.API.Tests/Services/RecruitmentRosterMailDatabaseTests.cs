using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class RecruitmentRosterMailDatabaseTests
{
    private sealed class IsolatedFactAttribute : FactAttribute
    {
        public IsolatedFactAttribute(){if(Environment.GetEnvironmentVariable("FREVO_ROSTER_TEST_DB") is null)Skip="Explicit isolated localhost roster test database required.";}
    }

    [IsolatedFact]
    public async Task Release_onboards_once_and_mail_is_durable_deduplicated_scoped_and_attaches_the_released_pdf()
    {
        var connection=Environment.GetEnvironmentVariable("FREVO_ROSTER_TEST_DB")!;
        var parsed=new MySqlConnectionStringBuilder(connection);
        Assert.Equal("127.0.0.1",parsed.Server);Assert.Equal((uint)33319,parsed.Port);Assert.Equal("roster_mail_test",parsed.Database);
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"ConnectionStrings:Default",connection},{"OutboundDelivery:Suppressed","false"}}).Build();
        await using var db=new MySqlConnection(connection);await db.OpenAsync();
        var token=Guid.NewGuid().ToString("N")[..10];
        var client=await db.ExecuteScalarAsync<int>("INSERT INTO clients(Name,Code,IsActive) VALUES(@Name,@Code,TRUE);SELECT LAST_INSERT_ID();",new{Name="Offer test "+token,Code="TEST"+token});
        var location=await db.ExecuteScalarAsync<int>("INSERT INTO worklocations(Name,ClientId,City,State,IsActive) VALUES('Synthetic institute',@Client,'Test city','Test state',TRUE);SELECT LAST_INSERT_ID();",new{Client=client});
        var user=new AuthUser{Id=1,IsActive=true,DisplayName="Synthetic sender",ClientAccessMode="Selected",AssignedClientIds=[client],RecruitmentScopeMode="Client",Permissions=["recruitment.offer.issue","recruitment.offer.manage"]};
        var repository=new RecruitmentDocumentRepository(config,null!,new());await repository.RowsAsync(client,user);
        var layout=new RecruitmentDocumentLayout { Inputs=[new(){Key="joiningDate",Label="Date of joining",Type="date",Required=true}],Sections=Enumerable.Range(0,8).Select(_=>new DocumentSection{Elements=[new(){X=65,Y=150,Width=460,Height=30,Text="{{candidateName}} {{joiningDate.date}} {{offerNumber}}"}]}).ToList() };
        layout.Sections[4].Elements.Add(new(){Kind="signature",X=65,Y=230,Width=220,Height=40,Text="[Signature & Stamp of Signatory]"});
        var body=LayoutDocumentPdfService.Marker+JsonSerializer.Serialize(layout,new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var template=await db.ExecuteScalarAsync<int>("INSERT INTO recruitment_templates(ClientId,TemplateCode,TemplateName,TemplateType,BodyTemplate,IsActive) VALUES(@ClientId,@Code,'Synthetic offer','Offer Letter',@Body,TRUE);SELECT LAST_INSERT_ID();",new{ClientId=client,Code=token,Body=body});
        async Task<long> AddRow(string name,string email,int rowNumber,string joining="2026-11-02")
        {
            var code="SYNTH-"+token+"-"+rowNumber;
            var candidate=await db.ExecuteScalarAsync<long>("INSERT INTO recruitment_candidates(CandidateCode,ClientId,FirstName,LastName,Email,Phone,PreferredLocationsJson,CreatedByUserId) VALUES(@Code,@ClientId,@Name,'',@Email,'','[]',1);SELECT LAST_INSERT_ID();",new{Code=code,ClientId=client,Name=name,Email=email});
            var values=JsonSerializer.Serialize(new Dictionary<string,string>{{"joiningDate",joining},{"monthlyGross","50000"},{"positionTitle","Researcher"}});
            return await db.ExecuteScalarAsync<long>("INSERT INTO recruitment_document_roster(ClientId,CandidateId,TemplateId,WorkLocationId,SourceGroup,SourceFileName,SourceFileHash,SourceSheetName,SourceRow,TemplateHash,ValuesJson,SourceValuesJson,CreatedByUserId,UpdatedByUserId) VALUES(@ClientId,@CandidateId,@TemplateId,@Location,'Synthetic','synthetic.xlsx',@Hash,'Employees',@Row,@Hash,@Json,@Json,1,1);SELECT LAST_INSERT_ID();",new{ClientId=client,CandidateId=candidate,TemplateId=template,Location=location,Hash=new string('a',64),Row=rowNumber,Json=values});
        }
        var rowId=await AddRow("Synthetic Person","one@example.invalid",1);
        await repository.SaveSigningAssetAsync(client,RecruitmentDocumentSigningTests.SyntheticPng(),"synthetic.png",user);
        var release=await repository.ReleaseAsync(client,rowId,true,user);
        var releasedRows=await repository.RowsAsync(client,user);var row=Assert.Single(releasedRows);Assert.True(row.EmployeeId>0);Assert.Equal(row.CandidateCode,row.EmployeeCode);
        Assert.Equal(0,await db.ExecuteScalarAsync<decimal>("SELECT AnnualCtc FROM employees WHERE Id=@Id",new{Id=row.EmployeeId}));
        Assert.Equal("2026-11-02",await db.ExecuteScalarAsync<string>("SELECT DateOfJoining FROM employees WHERE Id=@Id",new{Id=row.EmployeeId}));
        Assert.Equal(release.Bytes,(await repository.ReleaseAsync(client,rowId,true,user)).Bytes);
        Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees WHERE ClientId=@Client",new{Client=client}));
        // Same person on a second source row links the existing master.
        var duplicate=await AddRow("Synthetic Person","one@example.invalid",2);await repository.ReleaseAsync(client,duplicate,true,user);
        Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees WHERE ClientId=@Client",new{Client=client}));
        var missing=await AddRow("Missing Email","",3);await repository.ReleaseAsync(client,missing,true,user);
        var unreleased=await AddRow("Unreleased Person","unreleased@example.invalid",4);
        var selection=new RecruitmentOfferMailRequest{Ids=[rowId,missing,unreleased]};
        var preview=await repository.PreviewOfferMailAsync(client,selection,user);
        Assert.Equal("Ready",preview.Single(item=>item.RosterId==rowId).Status);Assert.Equal("Missing email",preview.Single(item=>item.RosterId==missing).Status);Assert.Equal("Not released",preview.Single(item=>item.RosterId==unreleased).Status);
        await using var smtp=new SyntheticSmtp();
        await db.ExecuteAsync("UPDATE notification_smtp_settings SET IsEnabled=TRUE,DeliveryPaused=FALSE,Host='127.0.0.1',Port=@Port,UserName='',Password='',EnableSsl=FALSE,FromEmail='test@example.invalid',FromName='Synthetic sender',ImapEnabled=FALSE WHERE Id=1",new{smtp.Port});
        selection.RequestId=Guid.NewGuid().ToString();selection.ReleaseIds=preview.ToDictionary(item=>item.RosterId.ToString(),item=>item.ReleaseId??0);
        var job=await repository.SubmitOfferMailAsync(client,selection,user);Assert.Equal(1,job.Queued);Assert.Equal(1,job.MissingEmail);Assert.Equal(1,job.NotReleased);
        var restored=await repository.SubmitOfferMailAsync(client,selection,user);Assert.Equal(job.Id,restored.Id);Assert.Equal(job.Items[0].QueueId,restored.Items[0].QueueId);
        Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_document_mail_deliveries WHERE ClientId=@Client",new{Client=client}));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>repository.DismissOfferMailJobAsync(client,job.Id,user));
        var worker=new NotificationRepository(config,null!,null!,NullLogger<NotificationRepository>.Instance);
        Assert.Equal(1,await worker.ProcessPendingAsync(CancellationToken.None,job.Items.Single(item=>item.QueueId.HasValue).QueueId));
        using var message=await smtp.Message;
        var attachment=Assert.IsType<MimePart>(Assert.Single(message.Attachments));using var decoded=new MemoryStream();attachment.Content.DecodeTo(decoded);Assert.Equal(release.Bytes,decoded.ToArray());Assert.Equal("one@example.invalid",Assert.Single(message.To.Mailboxes).Address);
        var completed=Assert.Single(await repository.OwnOfferMailJobsAsync(user,client));Assert.Equal(1,completed.Sent);Assert.Equal(1,completed.AcceptedEmails);Assert.True(completed.IsComplete);Assert.Equal(1,completed.SentCopyUnverifiedEmails);
        Assert.Equal("Skipped",(await repository.PreviewOfferMailAsync(client,new(){Ids=[rowId],RetryFailed=true},user)).Single().Status);
        var other=new AuthUser{Id=2,IsActive=true,ClientAccessMode="Selected",AssignedClientIds=[client],Permissions=["recruitment.offer.issue"]};Assert.Empty(await repository.OwnOfferMailJobsAsync(other,client));
        await repository.DismissOfferMailJobAsync(client,job.Id,user);Assert.NotNull(Assert.Single(await repository.OwnOfferMailJobsAsync(user,client)).DismissedAtUtc);
        var retryRow=await AddRow("Retry Person","retry@example.invalid",7);await repository.ReleaseAsync(client,retryRow,true,user);
        var retryPreview=await repository.PreviewOfferMailAsync(client,new(){Ids=[retryRow]},user);
        var retryRequest=new RecruitmentOfferMailRequest{Ids=[retryRow],RequestId=Guid.NewGuid().ToString(),ReleaseIds=retryPreview.ToDictionary(item=>item.RosterId.ToString(),item=>item.ReleaseId??0)};
        var failedJob=await repository.SubmitOfferMailAsync(client,retryRequest,user);
        await db.ExecuteAsync("UPDATE notification_queue SET Status='Failed',RetryCount=5,ErrorMessage='Synthetic provider failure' WHERE Id=@Id",new{Id=failedJob.Items[0].QueueId});
        Assert.Equal("Failed",(await repository.PreviewOfferMailAsync(client,new(){Ids=[retryRow]},user)).Single().Status);
        retryRequest.RequestId=Guid.NewGuid().ToString();retryRequest.RetryFailed=true;
        var retryJob=await repository.SubmitOfferMailAsync(client,retryRequest,user);Assert.NotEqual(failedJob.Items[0].QueueId,retryJob.Items[0].QueueId);Assert.Equal(1,retryJob.Queued);
        Assert.Equal("Skipped",(await repository.PreviewOfferMailAsync(client,new(){Ids=[retryRow],RetryFailed=true},user)).Single().Status);
        var locationRestricted=new AuthUser{Id=1,IsActive=true,ClientAccessMode="Selected",AssignedClientIds=[client],RecruitmentScopeMode="SelectedLocations",RecruitmentLocationIds=[],Permissions=["recruitment.offer.issue"]};
        Assert.Empty(await repository.OwnOfferMailJobsAsync(locationRestricted,client));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>repository.PreviewOfferMailAsync(client,new(){Ids=[rowId]},locationRestricted));
        // An invalid row rolls back BOTH release receipts and employee creation.
        var good=await AddRow("Atomic Good","good@example.invalid",5);var invalid=await AddRow("Atomic Invalid","bad@example.invalid",6,"");
        var employeesBefore=await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees WHERE ClientId=@Client",new{Client=client});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>repository.ReleaseZipAsync(client,new(){Ids=[good,invalid]},user));
        Assert.Equal(employeesBefore,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees WHERE ClientId=@Client",new{Client=client}));Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_document_roster_releases WHERE RosterId IN @Ids",new{Ids=new[]{good,invalid}}));
    }

    private sealed class SyntheticSmtp : IAsyncDisposable
    {
        private readonly TcpListener listener=new(IPAddress.Loopback,0);
        private readonly CancellationTokenSource stop=new(TimeSpan.FromSeconds(45));
        public int Port{get;}
        public Task<MimeMessage> Message{get;}
        public SyntheticSmtp(){listener.Start();Port=((IPEndPoint)listener.LocalEndpoint).Port;Message=CaptureAsync();}
        private async Task<MimeMessage> CaptureAsync()
        {
            using var client=await listener.AcceptTcpClientAsync(stop.Token);await using var stream=client.GetStream();using var reader=new StreamReader(stream,Encoding.UTF8);await using var writer=new StreamWriter(stream,new UTF8Encoding(false)){NewLine="\r\n",AutoFlush=true};
            await writer.WriteLineAsync("220 localhost synthetic SMTP");MimeMessage? result=null;
            while(await reader.ReadLineAsync(stop.Token) is {} line)
            {
                if(line.StartsWith("EHLO")){await writer.WriteLineAsync("250-localhost");await writer.WriteLineAsync("250 PIPELINING");}
                else if(line=="DATA")
                {
                    await writer.WriteLineAsync("354 Send message");var content=new StringBuilder();
                    while(await reader.ReadLineAsync(stop.Token) is {} part && part!=".")content.AppendLine(part.StartsWith("..")?part[1..]:part);
                    using var bytes=new MemoryStream(Encoding.UTF8.GetBytes(content.ToString()));result=await MimeMessage.LoadAsync(bytes,stop.Token);await writer.WriteLineAsync("250 Accepted");
                }
                else if(line=="QUIT"){await writer.WriteLineAsync("221 Bye");break;}
                else await writer.WriteLineAsync("250 OK");
            }
            return result??throw new InvalidOperationException("No synthetic email received.");
        }
        public ValueTask DisposeAsync(){stop.Cancel();listener.Stop();stop.Dispose();return ValueTask.CompletedTask;}
    }
}
