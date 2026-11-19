using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using MySqlConnector;
using PdfSharp.Pdf.IO;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Tests.Repositories;

namespace Payroll.API.Tests.Services;

[CollectionDefinition("RegisterMail",DisableParallelization=true)]
public sealed class RegisterMailCollection { }

[Collection("RegisterMail")]
public sealed class PayslipRegisterMailDatabaseTests
{
    private sealed class IsolatedFactAttribute : FactAttribute
    { public IsolatedFactAttribute() { if(Environment.GetEnvironmentVariable("FREVO_REGISTER_MAIL_TEST_DB") is null) Skip="Explicit isolated register-mail database required."; } }

    [IsolatedFact]
    public async Task ExistingWorkerQueuesPdfOnceTracksSentAndAllowsOnlyAcknowledgedIdempotentResendsWithoutAnotherBatch()
    {
        var (db,notifications,run,emails,slips)=await Setup(3);
        await using var owned=db;
        var payrollBefore=await db.ExecuteScalarAsync<string>("SELECT CAST(JSON_ARRAYAGG(JSON_OBJECT('id',Id,'net',NetPay,'details',DetailsJson)) AS CHAR) FROM payrunemployees");
        var request=Request(run);var actor=new AuthUser{Id=7,Email="admin@example.invalid",DisplayName="QA"};
        var submitted=await notifications.QueueRegisterMailAsync(run,request,emails,slips,actor);
        Assert.Null(submitted.Error);Assert.Equal(2,submitted.Item!.Job.Queued);Assert.Equal(1,submitted.Item.Job.MissingEmail);
        Assert.Equal(2,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM notification_queue"));
        var retry=await notifications.QueueRegisterMailAsync(run,request,emails,slips,actor);
        Assert.Null(retry.Error);Assert.Equal(submitted.Item.Job.Id,retry.Item!.Job.Id);Assert.Equal(2,retry.Item.Job.Queued);
        var legacy=await notifications.QueuePayslipAsync(new NotificationEvent{ClientId=run.ClientId,ResourceId=run.Employees[0].Id.ToString(),ActorUserId=7,
            PayloadJson=JsonSerializer.Serialize(new{employeeEmail=emails[run.Employees[0].EmployeeId],employeeName="Synthetic",employeeCode="TEST001",payPeriod="2026-10"})},"<body>Legacy preview</body>");
        Assert.Equal("Already queued",legacy.Status); // An older UI cannot duplicate a new PDF queue item.
        var changed=Request(run);changed.RequestId=request.RequestId;changed.EmployeeIds=[run.Employees[0].EmployeeId];
        Assert.NotNull((await notifications.QueueRegisterMailAsync(run,changed,emails,slips,actor)).Error);
        var queue=await db.QueryFirstAsync<NotificationQueueItem>("SELECT * FROM notification_queue ORDER BY Id");
        var builder=new BodyBuilder();await notifications.AttachRegisterPayslipPdfAsync(db,queue,builder);
        var pdf=Assert.Single(builder.Attachments.OfType<MimePart>());
        Assert.EndsWith(".pdf",pdf.FileName);Assert.Equal("application/pdf",pdf.ContentType.MimeType);
        using var bytes=new MemoryStream();pdf.Content!.DecodeTo(bytes);bytes.Position=0;
        using var rendered=PdfReader.Open(bytes,PdfDocumentOpenMode.Import);Assert.Equal(2,rendered.PageCount); // Two locations, one employee email.
        await using var smtp=new LoopbackInterviewSmtp();
        await db.ExecuteAsync("UPDATE notification_smtp_settings SET Host='127.0.0.1',Port=@Port,UserName='',Password='',EnableSsl=FALSE,ImapEnabled=FALSE WHERE Id=1",new{smtp.Port});
        Assert.Equal(2,await notifications.ProcessPendingAsync(default));
        Assert.Equal(2,smtp.Deliveries.Count);Assert.Equal(0,await notifications.ProcessPendingAsync(default));
        var overview=await notifications.GetRegisterMailAsync(run,emails);
        Assert.Equal(2,overview.Items.Count(item=>item.Status=="Sent"));Assert.All(overview.Items,item=>Assert.False(item.CanSend));
        Assert.Equal(2,overview.Jobs.Single().AcceptedEmails);Assert.Equal(0,overview.Jobs.Single().SentCopySavedEmails);
        Assert.Equal(2,overview.Jobs.Single().SentCopyUnverifiedEmails); // IMAP disabled never pretends the mailbox copy exists.
        var ordinary=await notifications.QueueRegisterMailAsync(run,Request(run),emails,slips,actor);
        Assert.Null(ordinary.Error);Assert.Equal(0,ordinary.Item!.Job.TotalEmails);Assert.Equal(2,ordinary.Item.Job.Skipped);
        var resend=Request(run);resend.Resend=true;
        Assert.NotNull((await notifications.QueueRegisterMailAsync(run,resend,emails,slips,actor)).Error);
        resend.AcknowledgeResend=true;
        var again=await notifications.QueueRegisterMailAsync(run,resend,emails,slips,actor);
        Assert.Null(again.Error);Assert.Equal(2,again.Item!.Job.Queued);
        var sameAgain=await notifications.QueueRegisterMailAsync(run,resend,emails,slips,actor);
        Assert.Null(sameAgain.Error);Assert.Equal(4,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM notification_queue"));
        Assert.All((await notifications.GetRegisterMailAsync(run,emails)).Items,item=>Assert.False(item.CanResend));
        // No approved result or employee profile was rewritten, and no Excel storage was created or used.
        Assert.Equal(payrollBefore,await db.ExecuteScalarAsync<string>("SELECT CAST(JSON_ARRAYAGG(JSON_OBJECT('id',Id,'net',NetPay,'details',DetailsJson)) AS CHAR) FROM payrunemployees"));
        Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='excel_payslip_batches'"));
        Assert.Equal(3,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees"));
        Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payruns"));
    }

    [IsolatedFact]
    public async Task ApprovalRecipientAndMoneyChangesFailClosedAndAnotherClientCannotReuseThisRun()
    {
        var (db,notifications,run,emails,slips)=await Setup(2);await using var owned=db;
        var actor=new AuthUser{Id=7,Email="admin@example.invalid",DisplayName="QA"};
        var actualClient=run.ClientId;run.ClientId=908;
        Assert.NotNull((await notifications.QueueRegisterMailAsync(run,Request(run),emails,slips,actor)).Error);run.ClientId=actualClient;
        await db.ExecuteAsync("UPDATE payruns SET Status='Draft'");
        Assert.NotNull((await notifications.QueueRegisterMailAsync(run,Request(run),emails,slips,actor)).Error);
        await db.ExecuteAsync("UPDATE payruns SET Status='Approved';UPDATE employees SET WorkEmail='changed@example.invalid' WHERE Id=1");
        Assert.NotNull((await notifications.QueueRegisterMailAsync(run,Request(run),emails,slips,actor)).Error);
        await db.ExecuteAsync("UPDATE employees SET WorkEmail='employee1@example.invalid' WHERE Id=1;UPDATE payrunemployees SET NetPay=NetPay+1 WHERE EmployeeId=1");
        Assert.NotNull((await notifications.QueueRegisterMailAsync(run,Request(run),emails,slips,actor)).Error);
        Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM notification_queue"));
        await db.ExecuteAsync("UPDATE payrunemployees SET NetPay=NetPay-1 WHERE EmployeeId=1");
        var result=await notifications.QueueRegisterMailAsync(run,Request(run),emails,slips,actor);Assert.Null(result.Error);
        var queue=await db.QueryFirstAsync<NotificationQueueItem>("SELECT * FROM notification_queue ORDER BY Id");
        await db.ExecuteAsync("UPDATE payrunemployees SET NetPay=NetPay+1 WHERE EmployeeId=1");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>notifications.AttachRegisterPayslipPdfAsync(db,queue,new BodyBuilder()));
        await db.ExecuteAsync("UPDATE notification_queue SET Status='Sent',SentAt=UTC_TIMESTAMP() WHERE Id=@Id",new{queue.Id});
        await notifications.RetryAsync(queue.Id);Assert.Equal("Sent",await db.ExecuteScalarAsync<string>("SELECT Status FROM notification_queue WHERE Id=@Id",new{queue.Id}));
    }

    [IsolatedFact]
    public async Task OneRequestAcceptsAll561EmployeesAndConcurrentRequestsCannotQueueDuplicates()
    {
        var (db,notifications,run,emails,slips)=await Setup(561);await using var owned=db;
        var actor=new AuthUser{Id=7,Email="admin@example.invalid",DisplayName="QA"};
        var requests=await Task.WhenAll(notifications.QueueRegisterMailAsync(run,Request(run),emails,slips,actor),notifications.QueueRegisterMailAsync(run,Request(run),emails,slips,actor));
        Assert.All(requests,result=>Assert.Null(result.Error));
        Assert.Equal(560,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM notification_queue"));
        Assert.Equal(560,requests.Sum(result=>result.Item!.Job.TotalEmails));
        Assert.Equal(561,(await notifications.GetRegisterMailAsync(run,emails)).Items.Count);
    }

    private static PayslipMailJobRequest Request(PayRun run)=>new(){RequestId=Guid.NewGuid().ToString("N"),EmployeeIds=run.Employees.Select(e=>e.EmployeeId).ToList()};

    private static async Task<(MySqlConnection Db,NotificationRepository Notifications,PayRun Run,Dictionary<int,string> Emails,List<PayrollLocationPayslipDocument> Slips)> Setup(int count)
    {
        var connection=Environment.GetEnvironmentVariable("FREVO_REGISTER_MAIL_TEST_DB")!;
        var target=new MySqlConnectionStringBuilder(connection);
        Assert.Equal("127.0.0.1",target.Server);Assert.Equal((uint)33330,target.Port);Assert.Equal("payslip_mail_test",target.Database);
        var db=new MySqlConnection(connection);await db.OpenAsync();
        await db.ExecuteAsync("""
CREATE TABLE IF NOT EXISTS entity_attachments(id BIGINT PRIMARY KEY);
CREATE TABLE IF NOT EXISTS clients(Id INT PRIMARY KEY,Name VARCHAR(200),Code VARCHAR(40),IsActive BOOLEAN);
CREATE TABLE IF NOT EXISTS payruns(Id INT PRIMARY KEY,ClientId INT,PayPeriod VARCHAR(7),Status VARCHAR(30));
CREATE TABLE IF NOT EXISTS employees(Id INT PRIMARY KEY,ClientId INT,WorkEmail VARCHAR(254));
CREATE TABLE IF NOT EXISTS payrunemployees(Id INT PRIMARY KEY,PayRunId INT,ClientId INT,EmployeeId INT,EmployeeCode VARCHAR(80),EmployeeName VARCHAR(200),
 IsSkipped BOOLEAN,PresentDays DECIMAL(18,2) NULL,PayableDays DECIMAL(18,2),GrossPay DECIMAL(18,2),StatutoryDeductions DECIMAL(18,2),
 OneTimeEarnings DECIMAL(18,2),OneTimeDeductions DECIMAL(18,2),NetPay DECIMAL(18,2),DetailsJson JSON);
CREATE TABLE IF NOT EXISTS modulesettings(Id INT PRIMARY KEY AUTO_INCREMENT,client_id INT,ModuleCode VARCHAR(80),IsEnabled BOOLEAN,SettingsJson JSON,UNIQUE KEY uq(client_id,ModuleCode));
""");
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:Default"]=connection}).Build();
        var notifications=new NotificationRepository(config,null!,null!,NullLogger<NotificationRepository>.Instance);
        await notifications.InitializeAsync();
        await db.ExecuteAsync("""
DELETE FROM notification_queue_attachments;DELETE FROM notification_logs;DELETE FROM notification_queue;
DELETE FROM modulesettings;DELETE FROM payrunemployees;DELETE FROM payruns;DELETE FROM employees;DELETE FROM clients;
INSERT INTO clients VALUES(907,'Synthetic payroll','TEST',TRUE),(908,'Other client','OTHER',TRUE);
INSERT INTO payruns VALUES(98,907,'2026-10','Approved');
UPDATE notification_smtp_settings SET IsEnabled=TRUE,DeliveryPaused=FALSE,Host='127.0.0.1',Port=1,EnableSsl=FALSE,FromEmail='info@example.invalid',UserName='',Password='',ImapEnabled=FALSE WHERE Id=1;
""");
        var emails=new Dictionary<int,string>();var slips=new List<PayrollLocationPayslipDocument>();
        for(var index=1;index<=count;index++)
        {
            var email=index==count?"":$"employee{index}@example.invalid";emails[index]=email;
            await db.ExecuteAsync("INSERT INTO employees VALUES(@Id,907,@Email);INSERT INTO payrunemployees VALUES(@Id,98,907,@Id,@Code,@Name,FALSE,NULL,26,1000,100,0,0,900,JSON_ARRAY())",
                new{Id=index,Email=email,Code="TEST"+index.ToString("D3"),Name="Synthetic Employee "+index});
            foreach(var part in index==1?new[]{.4m,.6m}:new[]{1m})
            {
                var key=$"employee:{index}:{slips.Count}";var gross=1000*part;var deduction=100*part;var net=900*part;
                var row=new ExcelPayslipRow{Id=key,SourceRow=slips.Count+2,EmployeeCode="TEST"+index.ToString("D3"),EmployeeName="Synthetic Employee "+index,
                    Email=email,Earnings=[new(){Label="Basic",Amount=gross}],Deductions=[new(){Label="PF",Amount=deduction}],NetPay=net,
                    DeclaredGross=gross,DeclaredDeductions=deduction,Information=[new(){Label="Location",Value="Synthetic location"}]};
                slips.Add(new(index,row.EmployeeCode,row.EmployeeName,key,1,"Synthetic location",gross,deduction,net,null,26,"synthetic.html","<html><body>Approved synthetic payslip</body></html>"){PrintSnapshot=row});
            }
        }
        var run=new PayRun{Id=98,ClientId=907,ClientName="Synthetic payroll",PayPeriod="2026-10",Status="Approved",
            Employees=(await db.QueryAsync<PayRunEmployee>("SELECT * FROM payrunemployees ORDER BY Id")).ToList()};
        return(db,notifications,run,emails,slips);
    }
}
