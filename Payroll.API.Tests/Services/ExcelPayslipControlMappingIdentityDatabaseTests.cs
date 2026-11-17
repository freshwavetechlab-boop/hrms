using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MimeKit;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Payroll.API.Tests.Fixtures;

namespace Payroll.API.Tests.Services;

[Collection("ReviewedPayroll")]
public sealed class ExcelPayslipControlMappingIdentityDatabaseTests : IAsyncLifetime
{
    private const int Client=97964;
    private const string BatchId="97964000000000000000000000000001",OriginalId="97964000000000000000000000000002";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private readonly ReviewedPayrollFixture? fixture=Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is string c?new(c):null;
    private ReviewedPayrollFixture F=>fixture??throw new InvalidOperationException("Explicit sentinel-guarded loopback fixture required.");
    private ExcelPayslipRepository Repo=>new(F.Config,new(F.Config,null!,null!,NullLogger<NotificationRepository>.Instance),new());
    private sealed class IsolatedFactAttribute:FactAttribute{public IsolatedFactAttribute(){if(Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB")is null)Skip="Explicit owned sentinel-guarded loopback fixture required.";}}
    public async Task InitializeAsync(){if(fixture is not null)await CleanAsync();}
    public async Task DisposeAsync(){if(fixture is not null)await CleanAsync();}
    private async Task CleanAsync()
    {
        await using var db=await F.OpenAsync();await db.ExecuteAsync(@"
DELETE a FROM notification_queue_attachments a JOIN notification_queue q ON q.Id=a.QueueId WHERE q.ClientId=97964;
DELETE l FROM notification_logs l JOIN notification_queue q ON q.Id=l.QueueId WHERE q.ClientId=97964;
DELETE FROM notification_queue WHERE ClientId=97964;
DELETE FROM excel_payslip_batch_originals WHERE client_id=97964;DELETE FROM excel_payslip_originals WHERE client_id=97964;
DELETE FROM entity_attachments WHERE client_id=97964;DELETE FROM excel_payslip_calculation_sources WHERE client_id=97964;
DELETE FROM excel_payslip_batches WHERE client_id=97964;DELETE FROM employeepersonaldetails WHERE EmployeeId IN(9796401,9796402);
DELETE FROM employeepaymentdetails WHERE EmployeeId IN(9796401,9796402);DELETE FROM employeesalarycomponents WHERE EmployeeId IN(9796401,9796402);
DELETE FROM employees WHERE ClientId=97964;DELETE FROM clients WHERE Id=97964;");
    }
    private sealed record Snapshot(ExcelPayslipBatch Batch,ExcelPayslipCalculationSource Source,string State,string Mail,string Originals,string Masters);
    private async Task<Snapshot> ReadAsync()
    {
        await using var db=await F.OpenAsync();var saved=await db.QuerySingleAsync<(string Batch,string Source,string State)>(@"
SELECT CAST(b.batch_json AS CHAR),CAST(s.source_json AS CHAR),CAST(b.send_state_json AS CHAR)
FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id
WHERE b.client_id=97964 AND b.id=@BatchId",new{BatchId});
        async Task<string> Rows(string sql)=>JsonSerializer.Serialize((await db.QueryAsync(sql)).ToArray(),Json);
        var mail=await Rows("SELECT * FROM notification_queue WHERE ClientId=97964 ORDER BY Id")+await Rows("SELECT l.* FROM notification_logs l JOIN notification_queue q ON q.Id=l.QueueId WHERE q.ClientId=97964 ORDER BY l.Id");
        var originals=await Rows("SELECT * FROM excel_payslip_batch_originals WHERE client_id=97964 ORDER BY original_id")+await Rows("SELECT * FROM excel_payslip_originals WHERE client_id=97964 ORDER BY id")+await Rows("SELECT * FROM entity_attachments WHERE client_id=97964 ORDER BY id");
        var masters=await Rows("SELECT * FROM employees WHERE ClientId=97964 ORDER BY Id")+await Rows("SELECT * FROM employeepersonaldetails WHERE EmployeeId IN(9796401,9796402) ORDER BY EmployeeId")+await Rows("SELECT * FROM employeepaymentdetails WHERE EmployeeId IN(9796401,9796402) ORDER BY EmployeeId")+await Rows("SELECT * FROM employeesalarycomponents WHERE EmployeeId IN(9796401,9796402) ORDER BY EmployeeId,ComponentId");
        return new(JsonSerializer.Deserialize<ExcelPayslipBatch>(saved.Batch,Json)!,JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(saved.Source,Json)!,saved.State,mail,originals,masters);
    }
    private static string Cash(ExcelPayslipRow row)=>JsonSerializer.Serialize(new{row.Earnings,row.Deductions,row.EmployerContributions,row.NetPay},Json);
    private async Task SeedAsync()
    {
        var batch=new ExcelPayslipBatch{Id=BatchId,ClientId=Client,ClientName="Synthetic source controls",Month="2026-10",SheetName="Salary",HeaderRow=1,SourceFileName="Synthetic controls.xlsx",SimpleLayout=true,LifecycleVersion=1,CreatedBy="synthetic-owner",CreatedAtUtc=new(2026,10,31,0,0,0,DateTimeKind.Utc),OriginalWorkbooks=[new(OriginalId,"Synthetic controls.xlsx")]};
        var source=new ExcelPayslipCalculationSource{SheetName="Salary",HeaderRow=1,ColumnCount=10,DateSystem="1900"};
        foreach(var (label,kind) in new[]{("Employee Code","employeeCode"),("Employee Name","employeeName"),("PAN","info"),("Basic","earning"),("Allowance","earning"),("PF","deduction"),("Employer PF","employer"),("Gross subtotal A","grossTotal"),("Net","netPay"),("Deductions","deductionTotal")})source.Columns.Add(new(){ColumnIndex=source.Columns.Count,SourceHeader=label,Label=label,Kind=kind});
        for(var i=0;i<3;i++)
        {
            var row=new ExcelPayslipRow{Id="owned-"+i,SourceRow=i+2,EmployeeCode="CONTROL-"+i,EmployeeName="Synthetic person "+i,Email=$"person{i}@example.invalid",Information=[new(){Label="PAN",Value=i==0?"ABCDE1234F":i==1?"BCDEF2345G":""}],Earnings=[new(){Label="Basic",Amount=1000+i},new(){Label="Allowance",Amount=20}],Deductions=[new(){Label="PF",Amount=10}],EmployerContributions=[new(){Label="Employer PF",Amount=50}],DeclaredGross=1000+i,DeclaredDeductions=10,NetPay=1010+i};
            row.WarningReview=new(ExcelPayslipRepository.NegativeAmountHash(row),"stored-reviewer@example.invalid",new(2026,10,31,12,0,0,DateTimeKind.Utc),ExcelPayslipRepository.SourceRowWarnings(row));row.Warnings=ExcelPayslipRepository.RowWarnings(row);batch.Rows.Add(row);
            var values=new[]{row.EmployeeCode,row.EmployeeName,row.Information[0].Value,(1000+i).ToString(CultureInfo.InvariantCulture),"20","10","50",(1000+i).ToString(CultureInfo.InvariantCulture),(1010+i).ToString(CultureInfo.InvariantCulture),"10"};
            source.Rows.Add(new(){SourceRow=row.SourceRow,Cells=values.Select((v,c)=>(ExcelPayslipCalculationCell?)new(){Value=v,DataType=c<3?"Text":"Number"}).ToList()});
        }
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(source,batch));
        var owners=new[]{new ExcelPayslipRepository.EmploymentMaster{Id=9796401,ClientId=Client,EmployeeCode="CONTROL-0",Pan="ABCDE1234F"},new ExcelPayslipRepository.EmploymentMaster{Id=9796402,ClientId=Client,EmployeeCode="CONTROL-1",Pan="BCDEF2345G"}};
        batch.ProfileLinks=ExcelPayslipRepository.ProfileBindings(batch,source,owners).ToList();Assert.Equal(2,batch.ProfileLinks.Count);
        await using var db=await F.OpenAsync();await db.ExecuteAsync(@"
INSERT INTO clients(Id,Code,Name,IsActive)VALUES(97964,'SOURCE-CONTROL','Synthetic source controls',TRUE);
INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,DateOfJoining,SalaryStructureId,AnnualCtc,SalaryJson,PersonalJson,PaymentJson,IsActive)
VALUES(9796401,97964,'CONTROL-0','Synthetic person','0','2026-01-01','',0,JSON_OBJECT(),JSON_OBJECT(),JSON_OBJECT(),TRUE),(9796402,97964,'CONTROL-1','Synthetic person','1','2026-01-01','',0,JSON_OBJECT(),JSON_OBJECT(),JSON_OBJECT(),TRUE);
INSERT INTO employeepersonaldetails(EmployeeId,PanNumber)VALUES(9796401,'ABCDE1234F'),(9796402,'BCDEF2345G');
INSERT INTO employeepaymentdetails(EmployeeId)VALUES(9796401),(9796402);
");
        var attachment=await db.ExecuteScalarAsync<long>(@"INSERT INTO entity_attachments(public_id,client_id,attachment_attribute_id,field_configuration_id,storage_server_id,entity_type,entity_id,original_file_name,stored_file_name,storage_key,file_extension,detected_mime_type,file_size_bytes,sha256_hash,uploaded_by_user_id)
VALUES(UUID(),97964,97964,97964,97964,'SyntheticPayroll',97964,'Synthetic controls.xlsx','synthetic.xlsx','synthetic/controls.xlsx','.xlsx','application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',10,REPEAT('a',64),97964);SELECT LAST_INSERT_ID();");
        var state=new ExcelPayslipSendState();
        foreach(var group in new[]{batch.Rows.Take(2).ToArray(),batch.Rows.Skip(2).ToArray()})
        {
            var request=new SendExcelPayslipsRequest{RequestId=Guid.NewGuid().ToString("N"),RowIds=group.Select(r=>r.Id).ToList(),Mode="Individual",IncludeSeal=false,AmountDecimalPlaces=0};
            var review=ExcelPayslipRepository.ReviewDeliverySelection(group,request,ExcelPayslipRepository.BuildDeliveryStatuses(batch,state,[]));
            Assert.All(review.Items,item=>Assert.NotEqual("Error",item.Status));
            var documents=ExcelPayslipRepository.PrepareMailJobDocuments(batch,group,request,review,request.RequestId,"synthetic-operator",ExcelPayslipTests.MailTemplate);
            foreach(var document in documents)
            {
                document.QueueId=await db.ExecuteScalarAsync<long>(@"INSERT INTO notification_queue(EventCode,ResourceType,ResourceId,ClientId,ToJson,CcJson,BccJson,Subject,BodyHtml,Status,SentAt)
VALUES('EXCEL_PAYSLIP.SEND','ExcelPayslipBatch',@Resource,97964,@ToJson,'[]','[]','Stored synthetic mail','Retained historical mail body','Sent','2026-10-31');SELECT LAST_INSERT_ID();",new{Resource=BatchId+":"+document.Id,ToJson=JsonSerializer.Serialize(new[]{document.Email})});
                await db.ExecuteAsync(@"INSERT INTO notification_logs(QueueId,EventCode,ResourceType,ResourceId,Recipient,Status,ProviderMessageId)VALUES(@Id,'EXCEL_PAYSLIP.SEND','ExcelPayslipBatch',@Resource,@Email,'Sent','synthetic-old-message')",new{Id=document.QueueId,Resource=BatchId+":"+document.Id,document.Email});
                state.Deliveries[document.Id]=document;
            }
            var job=ExcelPayslipRepository.CreateMailJobRecord(batch,request.RequestId,97964,"synthetic-operator",group,review,documents);state.Jobs[job.Id]=job;
            state.Requests[request.RequestId]=JsonSerializer.SerializeToElement(new{fingerprint=ExcelPayslipRepository.SendFingerprint(BatchId,request),createdBy="synthetic-operator",result=review},Json);
        }
        Assert.Equal(2,state.Jobs.Count);Assert.Equal(3,state.Deliveries.Count);Assert.Equal(2,state.Requests.Count);
        await db.ExecuteAsync(@"INSERT INTO excel_payslip_originals(client_id,id,attachment_id,sha256_hash)VALUES(97964,@OriginalId,@Attachment,REPEAT('a',64));
INSERT INTO excel_payslip_batch_originals(client_id,batch_id,original_id,original_file_name)VALUES(97964,@BatchId,@OriginalId,'Synthetic controls.xlsx');
INSERT INTO excel_payslip_batches(client_id,id,batch_json,send_state_json,created_at)VALUES(97964,@BatchId,CAST(@Batch AS JSON),CAST(@State AS JSON),'2026-10-31');
INSERT INTO excel_payslip_calculation_sources(client_id,batch_id,source_json,created_at)VALUES(97964,@BatchId,CAST(@Source AS JSON),'2026-10-31');",new{OriginalId,Attachment=attachment,BatchId,Batch=JsonSerializer.Serialize(batch,Json),State=JsonSerializer.Serialize(state,Json),Source=JsonSerializer.Serialize(source,Json)});
    }
    [IsolatedFact]
    public async Task Normal_control_mapping_save_keeps_job_document_row_refs_originals_and_master_links_but_not_stale_pdf_or_ack()
    {
        await SeedAsync();var before=await ReadAsync();var beforeState=JsonSerializer.Deserialize<ExcelPayslipSendState>(before.State,Json)!;
        Assert.Equal(2,beforeState.Jobs.Count);Assert.Equal(3,beforeState.Deliveries.Count);Assert.Equal(2,before.Batch.ProfileLinks!.Count);
        var oldCash=before.Batch.Rows.ToDictionary(r=>r.Id,Cash);var oldLinks=JsonSerializer.Serialize(before.Batch.ProfileLinks,Json);var oldSourceRows=JsonSerializer.Serialize(before.Source.Rows,Json);
        var request=before.Batch;request.CalculationSource=before.Source;var control=before.Source.Columns.Single(c=>c.ColumnIndex==7);control.Kind="info";
        foreach(var row in request.Rows){row.Information.Add(new(){Label="Gross subtotal A",Value=row.DeclaredGross!.Value.ToString(CultureInfo.InvariantCulture)});row.DeclaredGross=null;row.WarningReview=new(ExcelPayslipRepository.NegativeAmountHash(row),"foreign-review@example.invalid",DateTime.UtcNow,["Caller-supplied review"]);row.WarningReviewToken="foreign-token";}
        var saved=await Repo.SaveAsync(Client,request,"synthetic-mapping-operator");Assert.Null(saved.Error);Assert.NotNull(saved.Item);
        var after=await ReadAsync();Assert.Equal(before.State,after.State);Assert.Equal(before.Mail,after.Mail);Assert.Equal(before.Originals,after.Originals);Assert.Equal(before.Masters,after.Masters);
        Assert.Equal(oldLinks,JsonSerializer.Serialize(after.Batch.ProfileLinks,Json));Assert.Equal(oldSourceRows,JsonSerializer.Serialize(after.Source.Rows,Json));
        Assert.Equal(oldCash.Keys.Order(),after.Batch.Rows.Select(r=>r.Id).Order());Assert.Equal("info",after.Source.Columns.Single(c=>c.ColumnIndex==7).Kind);
        Assert.All(after.Batch.Rows,row=>{Assert.Equal(oldCash[row.Id],Cash(row));Assert.Null(row.DeclaredGross);Assert.Null(row.WarningReview);Assert.Null(row.WarningReviewToken);Assert.Null(row.NegativeAmountReview);Assert.Empty(row.Warnings);});
        var state=JsonSerializer.Deserialize<ExcelPayslipSendState>(after.State,Json)!;Assert.Equal(2,state.Jobs.Count);Assert.Equal(3,state.Deliveries.Count);Assert.Equal(2,state.Requests.Count);
        var currentIds=after.Batch.Rows.Select(r=>r.Id).ToHashSet();Assert.All(state.Jobs.Values,job=>Assert.All(job.RowIds,id=>Assert.Contains(id,currentIds)));Assert.All(state.Deliveries.Values,doc=>{Assert.All(doc.RowIds,id=>Assert.Contains(id,currentIds));Assert.NotEqual(doc.BatchSha256,NotificationRepository.ExcelPayslipBatchHash(after.Batch));});
        var oldDocument=state.Deliveries.Values.First();var builder=new BodyBuilder();
        var oldQueue=new NotificationQueueItem{Id=oldDocument.QueueId,ClientId=Client,EventCode=NotificationRepository.ExcelPayslipEvent,ResourceType="ExcelPayslipBatch",ResourceId=BatchId+":"+oldDocument.Id,ToJson=JsonSerializer.Serialize(new[]{oldDocument.Email})};
        var outdated=Assert.Throws<InvalidOperationException>(()=>NotificationRepository.AttachExcelPayslipPdf(builder,oldQueue,oldDocument,after.Batch,after.Source));Assert.Contains("integrity check",outdated.Message);Assert.Empty(builder.Attachments);
        var delivery=await Repo.GetDeliveryStatusAsync(Client,BatchId);Assert.NotNull(delivery);Assert.Equal(3,delivery.Items.Count);Assert.All(delivery.Items,item=>Assert.Equal("Sent",item.Status));
        var changed=after.Batch.Rows[0];var oldId=changed.Id;changed.Earnings[0].Amount+=1;changed.NetPay+=1;var cells=after.Source.Rows.Single(r=>r.SourceRow==changed.SourceRow).Cells;cells[3]!.Value=changed.Earnings[0].Amount.ToString(CultureInfo.InvariantCulture);cells[8]!.Value=changed.NetPay.ToString(CultureInfo.InvariantCulture);after.Batch.CalculationSource=after.Source;
        var cashSave=await Repo.SaveAsync(Client,after.Batch,"synthetic-cash-review");Assert.Null(cashSave.Error);var final=await ReadAsync();Assert.NotEqual(oldId,final.Batch.Rows[0].Id);Assert.Equal(after.Batch.Rows.Skip(1).Select(r=>r.Id),final.Batch.Rows.Skip(1).Select(r=>r.Id));
        Assert.Equal(before.State,final.State);Assert.Equal(before.Mail,final.Mail);Assert.Equal(before.Originals,final.Originals);Assert.Equal(before.Masters,final.Masters);
        await using var db=await F.OpenAsync();Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM excel_payslip_batches WHERE client_id=97964"));Assert.Equal(2,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees WHERE ClientId=97964"));Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payruns WHERE ClientId=97964"));
    }
}
