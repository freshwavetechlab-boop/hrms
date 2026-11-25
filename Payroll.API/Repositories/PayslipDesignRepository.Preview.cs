using System.Text.Json;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class PayslipDesignRepository
{
    public async Task<PayslipDesignPdf> PreviewPdfAsync(PayslipDesignSaveRequest request)
    {
        if(request.Template is null || request.Template.ClientId<=0 || request.SourceHash is null
            || JsonSerializer.SerializeToUtf8Bytes(request,Json).Length>64000)
            throw new InvalidOperationException("Choose a client and a bounded saved-source presentation review.");
        await using var db=Db();await db.OpenAsync();
        await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,isReadOnly:true);
        var metadata=await DesignMetadataAsync(db,tx,request);
        if(request.SourceKind!=CorePayrollPayslipDesign.SourceKind && request.SourceHash!=metadata.Hash)throw new InvalidOperationException("The saved source presentation changed. Reload its current labels before previewing.");
        if(request.Template.Id>0 && !await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM paysliptemplates WHERE Id=@Id AND ClientId=@ClientId)",new{request.Template.Id,request.Template.ClientId},tx))
            throw new InvalidOperationException("The selected template does not belong to this client.");
        PayslipDesignSamplePreview.Validate(request,metadata.Fields,request.SourceKind==CorePayrollPayslipDesign.SourceKind?await ClientSalaryCodesAsync(db,tx,request.Template.ClientId):await CodesAsync(db,tx));
        var row=PayslipDesignSamplePreview.CreateRow(request,metadata.Fields);
        var batch=new ExcelPayslipBatch { ClientId=request.Template.ClientId,ClientName=metadata.Batch.ClientName,Month=metadata.Batch.Month,SimpleLayout=metadata.Batch.SimpleLayout };
        var bytes=pdf.Create(batch,[row],amountDecimalPlaces:request.AmountDecimalPlaces,presentation:new ExcelPayslipPdfPresentationOptions {
            ShowLogo=request.Template.ShowLogo,ShowClient=request.Template.ShowClient,KeepMappedZeroRows=request.SourceKind==CorePayrollPayslipDesign.SourceKind?request.KeepMappedZeroRows!.Value:NativePayslipDesignService.SourceKeepsMappedZeroRows(metadata.Batch.SimpleLayout),ExplicitSimpleInformation=true,Footer=request.Template.Note });
        await tx.CommitAsync();
        return new(bytes,"sample-payslip-design.pdf");
    }
}
