using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class PayslipDesignRepository(IConfiguration configuration, PortableIntegrationCredentialProtector protector, ExcelPayslipPdfService pdf)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private MySqlConnection Db()=>new(configuration.GetConnectionString("Default"));
    private static string Code(long id)=>$"payslip.design:{id}";
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private sealed record StoredSource(string BatchJson,string? SourceJson);
    private sealed record CompressedStoredSource(byte[] BatchJson,byte[]? SourceJson);
    private sealed record ProtectedProfile(string Value);
    private sealed record Receipt(string RequestHash,long TemplateId);
    public static bool CanManage(AuthUser user,int clientId)=>user.Id>0 && user.IsActive && !user.MustChangePassword && SelectedClientAccess.Allows(user,clientId)
        && (user.Permissions.Contains("settings.manage",StringComparer.OrdinalIgnoreCase) || user.Roles.Contains("super_admin",StringComparer.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<PayslipDesignSource>> SourcesAsync(int clientId)
    {
        await using var db=Db();await db.OpenAsync();
        return (await db.QueryAsync<PayslipDesignSource>(@"SELECT id Id,COALESCE(JSON_UNQUOTE(JSON_EXTRACT(batch_json,'$.month')),'') Month,
COALESCE(JSON_UNQUOTE(JSON_EXTRACT(batch_json,'$.sourceFileName')),'Saved Excel payslip') Name
FROM excel_payslip_batches WHERE client_id=@ClientId ORDER BY Month DESC,id LIMIT 300",new{ClientId=clientId})).ToArray();
    }
    private static async Task<StoredSource> SourceAsync(MySqlConnection db,int clientId,string id,MySqlTransaction? tx=null,bool locked=false)
    {
        if(clientId<=0 || string.IsNullOrWhiteSpace(id) || id.Length>100)throw new InvalidOperationException("Select a saved Excel payslip source for this client.");
        var stored=await db.QuerySingleOrDefaultAsync<CompressedStoredSource>(@"SELECT COMPRESS(CAST(b.batch_json AS CHAR)) BatchJson,COMPRESS(CAST(s.source_json AS CHAR)) SourceJson
FROM excel_payslip_batches b LEFT JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id
WHERE b.client_id=@ClientId AND b.id=@Id"+(locked?" FOR UPDATE":""),new{ClientId=clientId,Id=id},tx)
            ??throw new InvalidOperationException("The saved Excel source does not belong to this client.");
        // Transport only: retain the exact original JSON text used by source
        // hashes and profiles, including numbers, Unicode and optional sources.
        return new(ExcelPayslipConsolidationCatalog.DecodeSnapshot(stored.BatchJson,false)!,
            ExcelPayslipConsolidationCatalog.DecodeSnapshot(stored.SourceJson,true));
    }
    private static async Task<(ExcelPayslipBatch Batch,List<PayslipDesignField> Fields,string Hash)> MetadataAsync(MySqlConnection db,MySqlTransaction tx,StoredSource stored,int clientId)
    {
        var batch=JsonSerializer.Deserialize<ExcelPayslipBatch>(stored.BatchJson,Json)??throw new InvalidOperationException("Saved source metadata is invalid.");
        if(batch.ClientId!=clientId)throw new InvalidOperationException("Saved source client metadata does not match.");
        var source=string.IsNullOrWhiteSpace(stored.SourceJson)?null:JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(stored.SourceJson,Json);
        if(source is null)throw new InvalidOperationException("This saved source has no reviewed column mapping. Configure its Excel mapping first.");
        var fields=source.Columns.Where(c=>c.Kind is "info" or "earning" or "deduction" or "employer")
            .Select(c=>new PayslipDesignField { SourceColumn=c.ColumnIndex,Label=c.Label,Kind=c.Kind,InfoToken=c.Kind=="info"?SuggestToken(c.Label):"" }).ToList();
        await ExcelPayslipRepository.AnnotateDesignMetadataAsync(db,tx,batch,source);
        var labels=batch.Rows.SelectMany(row=>ExcelPayslipPresentation.Information(row)).Select(field=>field.Label).Distinct(StringComparer.Ordinal).ToHashSet();
        for(var index=0;index<NativePayslipDesignService.SupplementalFields.Length;index++)
        {
            var supplemental=NativePayslipDesignService.SupplementalFields[index];
            if(labels.Contains(supplemental.Label) && !fields.Any(field=>field.Kind=="info" && field.Label==supplemental.Label))
                fields.Add(new(){SourceColumn=1500+index,Kind="info",Label=supplemental.Label,InfoToken=supplemental.Token});
        }
        if(fields.Count is <1 or >100)throw new InvalidOperationException("The saved source does not have a supported presentation mapping.");
        return(batch,fields,Hash(JsonSerializer.Serialize(new{Source=ExcelPayslipLegacyReview.SnapshotHash(stored.BatchJson,stored.SourceJson),Fields=fields.Select(field=>new{field.SourceColumn,field.Label,field.Kind})},Json)));
    }
    private static string SuggestToken(string label)
    {
        var key=new string(label.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return key switch { "designation"=>"DESIGNATION","department"=>"DEPARTMENT","grade"=>"GRADE","worklocation" or "location"=>"WORK_LOCATION",
            "dateofjoining" or "joiningdate"=>"JOINING_DATE","email" or "emailid"=>"EMAIL","mobile" or "mobileno" or "mobilenumber" or "contactnumber"=>"MOBILE",
            "uan"=>"UAN","esic" or "esicno" or "esicnumber"=>"ESIC","pan" or "pannumber"=>"PAN","aadhaar" or "aadharnumber" or "aadhaarnumber"=>"AADHAAR",
            "bankaccountno" or "accountno" or "bankaccountnumber"=>"BANK_ACCOUNT","bankname"=>"BANK_NAME","ifsccode" or "ifsc"=>"IFSC","address"=>"ADDRESS",
            "monthlygross"=>"SAVED_MONTHLY_GROSS","daysinsalaryperiod" or "payrolldays"=>"PAYROLL_DAYS",
            "presentdays"=>"PRESENT_DAYS","payabledays"=>"PAYABLE_DAYS",_=>""};
    }
    private static async Task<IReadOnlyList<PayslipDesignCode>> CodesAsync(MySqlConnection db,MySqlTransaction? tx=null)=>
        (await db.QueryAsync<PayslipDesignCode>("SELECT Code,Name,Category FROM salarycomponents WHERE Active=TRUE ORDER BY Category,Name",transaction:tx)).ToArray();
    public async Task<PayslipDesignPreview> PreviewAsync(PayslipDesignPreviewRequest request)
    {
        await using var db=Db();await db.OpenAsync();await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,isReadOnly:true);
        var metadata=await MetadataAsync(db,tx,await SourceAsync(db,request.ClientId,request.SourceBatchId,tx),request.ClientId);
        var codes=await CodesAsync(db,tx);
        var tokens=NativePayslipMonthlyComponentInformation.CatalogueTokens(codes,configuration.GetValue<bool>(NativePayslipMonthlyComponentInformation.WriterFlag));
        var result=new PayslipDesignPreview(request.ClientId,request.SourceBatchId,metadata.Hash,metadata.Batch.SimpleLayout,metadata.Fields,codes,tokens)
            { KeepMappedZeroRows=NativePayslipDesignService.SourceKeepsMappedZeroRows(metadata.Batch.SimpleLayout) };await tx.CommitAsync();return result;
    }
    private async Task<PayslipDesignProfile?> ReadProfileAsync(MySqlConnection db,int clientId,long templateId,MySqlTransaction? tx=null)
    {
        var json=await db.ExecuteScalarAsync<string?>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Code AND IsEnabled=TRUE",new{ClientId=clientId,Code=Code(templateId)},tx);
        if(json is null)return null;
        var envelope=JsonSerializer.Deserialize<ProtectedProfile>(json,Json);
        var plain=envelope is null?null:protector.UnprotectPayslipDesign(envelope.Value);
        if(plain is null)throw new InvalidOperationException("The reviewed payslip design cannot be authenticated. Re-review the source presentation.");
        return JsonSerializer.Deserialize<PayslipDesignProfile>(plain,Json)??throw new InvalidOperationException("The saved payslip design is invalid.");
    }
    public async Task<object?> GetAsync(int clientId,long id)
    {
        await using var db=Db();await db.OpenAsync();var template=await db.QuerySingleOrDefaultAsync<PayslipDesignTemplate>("SELECT * FROM paysliptemplates WHERE Id=@Id AND ClientId=@ClientId",new{Id=id,ClientId=clientId});
        if(template is null)return null;var profile=await ReadProfileAsync(db,clientId,id);if(profile is null)return null;
        NativePayslipDesignService.ValidateProfile(profile,template);
        return new{Template=template,profile.SourceBatchId,profile.SourceHash,profile.SourceKind,profile.SimpleLayout,profile.KeepMappedZeroRows,ProfileHash=profile.RequestHash,profile.AmountDecimalPlaces,profile.Fields,profile.PayPeriods};
    }
    public async Task<PayslipDesignTemplate> SaveAsync(PayslipDesignSaveRequest request,AuthUser actor)
    {
        if(request.Template is null || request.Fields is null || request.Template.Note is null || request.SourceHash is null)
            throw new InvalidOperationException("Supply a complete reviewed payslip presentation request.");
        var template=JsonSerializer.Deserialize<PayslipDesignTemplate>(JsonSerializer.Serialize(request.Template,Json),Json)!;
        var designSourceKind=request.SourceKind??"ExcelSource";
        var isNew=template.Id==0;
        if(!CanManage(actor,template.ClientId))throw new UnauthorizedAccessException();
        if(!request.Reviewed || !Guid.TryParse(request.RequestId,out _) || template.Id<0 || template.Theme!=NativePayslipDesignService.Theme || string.IsNullOrWhiteSpace(template.Name) || template.Name.Length>200 || template.Note.Length>120)
            throw new InvalidOperationException("Review the source mapping, supported theme, template name and footer (up to 120 characters), then save with a request reference.");
        if(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request,Json))>64000)throw new InvalidOperationException("The presentation review is too large.");
        var requestHash=Hash(JsonSerializer.Serialize(request,Json));
        var receiptCode=$"payslip.design.request:{Guid.Parse(request.RequestId):N}";
        await using var db=Db();await db.OpenAsync();await using var tx=await db.BeginTransactionAsync();
        if(await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@Id AND IsActive=TRUE FOR UPDATE",new{Id=template.ClientId},tx) is null)throw new InvalidOperationException("The selected client is unavailable.");
        var priorJson=await db.ExecuteScalarAsync<string?>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Code",new{template.ClientId,Code=receiptCode},tx);
        if(priorJson is not null)
        {
            var env=JsonSerializer.Deserialize<ProtectedProfile>(priorJson,Json);var plain=env is null?null:protector.UnprotectPayslipDesign(env.Value);
            var prior=plain is null?null:JsonSerializer.Deserialize<Receipt>(plain,Json);
            if(prior is null || prior.RequestHash!=requestHash)throw new InvalidOperationException("This request reference was used for a different presentation review.");
            var saved=await db.QuerySingleAsync<PayslipDesignTemplate>("SELECT * FROM paysliptemplates WHERE Id=@Id AND ClientId=@ClientId",new{Id=prior.TemplateId,template.ClientId},tx);await tx.CommitAsync();return saved;
        }
        if(isNew || designSourceKind==CorePayrollPayslipDesign.SourceKind)await RequireClientSalaryTemplateAsync(db,tx,template.ClientId);
        NativePayslipMonthlyComponentInformation.RequireWriter(request.Fields,configuration.GetValue<bool>(NativePayslipMonthlyComponentInformation.WriterFlag));
        var metadata=await DesignMetadataAsync(db,tx,request,true);
        if(designSourceKind!="CorePayroll" && request.SourceHash!=metadata.Hash)throw new InvalidOperationException("The design schema or saved source changed. Reload and review its current presentation mapping.");
        if(request.Fields.Count!=metadata.Fields.Count || !request.Fields.OrderBy(f=>f.SourceColumn).Select(f=>(f.SourceColumn,f.Label,f.Kind)).SequenceEqual(metadata.Fields.OrderBy(f=>f.SourceColumn).Select(f=>(f.SourceColumn,f.Label,f.Kind))))
            throw new InvalidOperationException("The reviewed fields must retain every saved source column, label and kind; use visibility to hide a field.");
        PayslipDesignTemplate? originalTemplate=null;PayslipDesignProfile? existingProfile=null;
        if(template.Id>0)
        {
            originalTemplate=await db.QuerySingleOrDefaultAsync<PayslipDesignTemplate>("SELECT * FROM paysliptemplates WHERE Id=@Id AND ClientId=@ClientId FOR UPDATE",new{template.Id,template.ClientId},tx)
                ??throw new InvalidOperationException("This template belongs to another client or is global. Create a separate client template.");
            existingProfile=await ReadProfileAsync(db,template.ClientId,template.Id,tx);
            if((existingProfile?.RequestHash??"")!=request.ExistingProfileHash)
                throw new InvalidOperationException("This payslip design changed after you opened it. Reload and review the current design before saving.");
            if(existingProfile is not null)NativePayslipDesignService.ValidateProfile(existingProfile,originalTemplate);
            if(existingProfile is not null && existingProfile.SourceKind!=designSourceKind)
                throw new InvalidOperationException("Create a separate template to change its design source. The existing template and payroll presentation stay unchanged.");
        }
        var periods=NativePayslipDesignScope.Resolve(request,existingProfile);
        RequireScopedPresentationWrite(periods,existingProfile);
        var previousClassic=RetainClassicPresentation(existingProfile,originalTemplate,periods);
        var maxId=await db.ExecuteScalarAsync<long>("SELECT COALESCE(MAX(Id),0) FROM paysliptemplates",transaction:tx);
        var templateId=template.Id==0?Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),maxId+1):template.Id;
        if(template.Active && await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM paysliptemplates WHERE ClientId=@ClientId AND Active=TRUE AND Id>@Id)",new{template.ClientId,Id=templateId},tx))
            throw new InvalidOperationException("A newer active client template takes precedence. Deactivate it before activating this template.");
        var profile=new PayslipDesignProfile { ClientId=template.ClientId,TemplateId=templateId,SourceBatchId=request.SourceBatchId,SourceHash=metadata.Hash,SimpleLayout=metadata.Batch.SimpleLayout,
            SourceKind=designSourceKind,KeepMappedZeroRows=designSourceKind==CorePayrollPayslipDesign.SourceKind?request.KeepMappedZeroRows!.Value:NativePayslipDesignService.SourceKeepsMappedZeroRows(metadata.Batch.SimpleLayout),
            AmountDecimalPlaces=request.AmountDecimalPlaces,PayPeriods=periods,PreviousClassicTemplate=previousClassic,
            Fields=request.Fields,ReviewedBy=actor.Id,RequestId=request.RequestId,RequestHash=requestHash };
        template.Id=templateId;NativePayslipDesignService.ValidateProfile(profile,template);
        var nativeCodes=designSourceKind==CorePayrollPayslipDesign.SourceKind?await ClientSalaryCodesAsync(db,tx,template.ClientId):await CodesAsync(db,tx);
        NativePayslipMonthlyComponentInformation.ValidateCatalogue(request.Fields,nativeCodes);
        foreach(var field in request.Fields.Where(f=>f.Kind!="info"))
            if(!nativeCodes.Any(c=>c.Code==field.NativeCode && NativePayslipDesignService.LineKind(c.Category)==field.Kind))throw new InvalidOperationException($"Native component {field.NativeCode} does not match the reviewed {field.Kind} field.");
        await RequireApprovedFinancialCoverageAsync(db,tx,template,profile,originalTemplate,existingProfile);
        // A concurrent other-client allocation may collide. INSERT must fail and roll
        // back rather than updating that client's row through a global primary key.
        if(!isNew)
            await db.ExecuteAsync(@"UPDATE paysliptemplates SET Name=@Name,Theme=@Theme,ShowLogo=@ShowLogo,ShowClient=@ShowClient,ShowYtd=@ShowYtd,ShowBank=@ShowBank,Note=@Note,Active=@Active WHERE Id=@Id AND ClientId=@ClientId",template,tx);
        else await db.ExecuteAsync(@"INSERT INTO paysliptemplates(Id,ClientId,ClientRef,Name,Theme,ShowLogo,ShowClient,ShowYtd,ShowBank,Note,Active)
VALUES(@Id,@ClientId,CAST(@ClientId AS CHAR),@Name,@Theme,@ShowLogo,@ShowClient,@ShowYtd,@ShowBank,@Note,@Active)",template,tx);
        async Task Store(string code,object value)=>await db.ExecuteAsync(@"INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@ClientId,@Code,TRUE,CAST(@Json AS JSON))
ON DUPLICATE KEY UPDATE IsEnabled=TRUE,SettingsJson=VALUES(SettingsJson)",new{template.ClientId,Code=code,Json=JsonSerializer.Serialize(new ProtectedProfile(protector.ProtectPayslipDesign(JsonSerializer.Serialize(value,Json))),Json)},tx);
        await Store(Code(templateId),profile);await Store(receiptCode,new Receipt(requestHash,templateId));await tx.CommitAsync();return template;
    }

    public async Task<PayslipDesignPdf?> CreatePdfAsync(int clientId,int payRunId,int employeeId)
    {
        await using var db=Db();await db.OpenAsync();await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        var template=await db.QueryFirstOrDefaultAsync<PayslipDesignTemplate>(@"SELECT * FROM paysliptemplates WHERE Active=TRUE AND (ClientId=@ClientId OR ClientId=0)
ORDER BY CASE WHEN ClientId=@ClientId THEN 0 ELSE 1 END,Id DESC LIMIT 1",new{ClientId=clientId},tx);
        await RequireReferenceProfileBeforeFallbackAsync(db,tx,clientId,payRunId,template);
        if(template is null || template.Theme!=NativePayslipDesignService.Theme)return null;
        var profile=await ReadProfileAsync(db,clientId,template.Id,tx)??throw new InvalidOperationException("Review and save this client's Excel Payslip design in Settings first.");
        var saved=await db.QuerySingleOrDefaultAsync<NativeRow>(@"SELECT p.Id NativeRowId,p.EmployeeId,p.ClientId,e.EmployeeCode,TRIM(CONCAT(e.FirstName,' ',e.LastName)) EmployeeName,
COALESCE(c.Name,'') ClientName,r.PayPeriod Month,r.Status,p.GrossPay+p.OneTimeEarnings Gross,p.StatutoryDeductions+p.OneTimeDeductions Deductions,p.NetPay,p.MonthlyGross,p.PresentDays,p.PayableDays,r.TotalWorkingDays,p.DetailsJson,
COALESCE(e.Designation,'') Designation,COALESCE(e.Department,'') Department,COALESCE(e.Grade,'') Grade,COALESCE(e.AnnualCtc,0) CurrentAnnualCtc,COALESCE(e.WorkEmail,'') Email,COALESCE(e.DateOfJoining,'') JoiningDate,
COALESCE(w.Name,'') WorkLocation,COALESCE(pd.Mobile,'') Mobile,COALESCE(pd.UanNumber,'') Uan,COALESCE(pd.EsicNumber,'') Esic,COALESCE(pd.PanNumber,'') Pan,COALESCE(pd.AadhaarNumber,'') Aadhaar,COALESCE(pd.Address,'') Address,
COALESCE(pay.BankAccountNo,'') BankAccount,COALESCE(pay.BankName,'') BankName,COALESCE(pay.IfscCode,'') Ifsc
FROM payrunemployees p JOIN payruns r ON r.Id=p.PayRunId AND r.ClientId=p.ClientId
JOIN employees e ON e.Id=p.EmployeeId AND e.ClientId=p.ClientId JOIN clients c ON c.Id=p.ClientId
LEFT JOIN worklocations w ON w.Id=e.WorkLocationId AND w.ClientId=p.ClientId
LEFT JOIN employeepersonaldetails pd ON pd.EmployeeId=e.Id LEFT JOIN employeepaymentdetails pay ON pay.EmployeeId=e.Id
WHERE p.ClientId=@ClientId AND p.PayRunId=@PayRunId AND p.EmployeeId=@EmployeeId AND p.IsSkipped=FALSE AND r.Status IN ('Approved','Partially Paid','Paid')",new{ClientId=clientId,PayRunId=payRunId,EmployeeId=employeeId},tx);
        if(saved is null)return null;
        NativePayslipDesignService.ValidateProfile(profile,template);
        if(!NativePayslipDesignScope.Applies(profile,saved.Month))return null;
        string Number(decimal value)=>value.ToString("0.############################",CultureInfo.InvariantCulture);
        var data=new NativePayslipDesignData { NativeRowId=saved.NativeRowId,ClientId=clientId,EmployeeId=employeeId,EmployeeCode=saved.EmployeeCode,EmployeeName=saved.EmployeeName,ClientName=saved.ClientName,Month=saved.Month,Status=saved.Status,
            Gross=saved.Gross,Deductions=saved.Deductions,NetPay=saved.NetPay,Lines=NativePayslipDesignService.ReadLines(saved.DetailsJson),Information=new() {
                ["DESIGNATION"]=saved.Designation,["DEPARTMENT"]=saved.Department,["GRADE"]=saved.Grade,["CURRENT_ANNUAL_CTC"]=Number(saved.CurrentAnnualCtc),["WORK_LOCATION"]=saved.WorkLocation,["EMAIL"]=saved.Email,["JOINING_DATE"]=saved.JoiningDate,
                ["MOBILE"]=saved.Mobile,["UAN"]=saved.Uan,["ESIC"]=saved.Esic,["PAN"]=saved.Pan,["AADHAAR"]=saved.Aadhaar,["ADDRESS"]=saved.Address,["BANK_ACCOUNT"]=saved.BankAccount,["BANK_NAME"]=saved.BankName,["IFSC"]=saved.Ifsc,
                ["SAVED_MONTHLY_GROSS"]=Number(saved.MonthlyGross),["PAYROLL_DAYS"]=Number(saved.TotalWorkingDays),["PRESENT_DAYS"]=saved.PresentDays.HasValue?Number(saved.PresentDays.Value):"Not supplied",["PAYABLE_DAYS"]=Number(saved.PayableDays) } };
        var currency=await new PayRunRepository(configuration,new TaxEngineRepository(configuration),nativeCurrencyProtector:protector)
            .ReadNativeCurrencyStageAsync(db,tx,clientId,payRunId,employeeId);
        var row=await ProjectWithFrozenReferenceInformationAsync(db,tx,profile,template,data,currency,payRunId);
        if(template.ShowYtd)
        {
            var ytd=await db.QuerySingleAsync<Ytd>(@"SELECT COALESCE(SUM(p.GrossPay+p.OneTimeEarnings),0) Gross,COALESCE(SUM(p.StatutoryDeductions+p.OneTimeDeductions),0) Deductions,COALESCE(SUM(p.NetPay),0) NetPay
FROM payrunemployees p JOIN payruns r ON r.Id=p.PayRunId AND r.ClientId=p.ClientId WHERE p.EmployeeId=@EmployeeId AND p.ClientId=@ClientId AND p.IsSkipped=FALSE
AND r.PayPeriod<=@Month AND LEFT(r.PayPeriod,4)=LEFT(@Month,4) AND r.Status IN ('Approved','Partially Paid','Paid')",new{EmployeeId=employeeId,ClientId=clientId,saved.Month},tx);
            row.Information.AddRange(new[]{new ExcelPayslipText { Label="YTD earnings",Value=ExcelPayslipPdfService.Amount(ytd.Gross,profile.AmountDecimalPlaces) },new ExcelPayslipText { Label="YTD deductions",Value=ExcelPayslipPdfService.Amount(ytd.Deductions,profile.AmountDecimalPlaces) },new ExcelPayslipText { Label="YTD net pay",Value=ExcelPayslipPdfService.Amount(ytd.NetPay,profile.AmountDecimalPlaces) }});
        }
        var batch=new ExcelPayslipBatch { ClientId=clientId,ClientName=saved.ClientName,Month=saved.Month,SimpleLayout=profile.SimpleLayout };
        var bytes=pdf.Create(batch,[row],amountDecimalPlaces:profile.AmountDecimalPlaces,presentation:NativePayslipDesignService.Presentation(profile,template,currency));
        var code=new string(saved.EmployeeCode.Where(ch=>char.IsLetterOrDigit(ch)||ch is '-' or '_').ToArray());
        await tx.CommitAsync();return new(bytes,$"payslip-{code}-{saved.Month}.pdf");
    }
    private sealed class NativeRow
    {
        public int NativeRowId{get;set;}
        public int EmployeeId{get;set;}public int ClientId{get;set;}public string EmployeeCode{get;set;}="";public string EmployeeName{get;set;}="";public string ClientName{get;set;}="";public string Month{get;set;}="";public string Status{get;set;}="";
        public decimal Gross{get;set;}public decimal Deductions{get;set;}public decimal NetPay{get;set;}public decimal MonthlyGross{get;set;}public decimal? PresentDays{get;set;}public decimal PayableDays{get;set;}public int TotalWorkingDays{get;set;}public string DetailsJson{get;set;}="[]";
        public string Designation{get;set;}="";public string Department{get;set;}="";public string Grade{get;set;}="";public decimal CurrentAnnualCtc{get;set;}public string Email{get;set;}="";public string JoiningDate{get;set;}="";public string WorkLocation{get;set;}="";public string Mobile{get;set;}="";public string Uan{get;set;}="";public string Esic{get;set;}="";public string Pan{get;set;}="";public string Aadhaar{get;set;}="";public string Address{get;set;}="";public string BankAccount{get;set;}="";public string BankName{get;set;}="";public string Ifsc{get;set;}="";
    }
    private sealed class Ytd { public decimal Gross{get;set;}public decimal Deductions{get;set;}public decimal NetPay{get;set;} }
}
