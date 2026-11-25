using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class PayslipDesignEndpoints
{
    public static void MapPayslipDesigns(this WebApplication app)
    {
        var routes=app.MapGroup("/api/payslip-designs").WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(70000));
        routes.AddEndpointFilter(async(context,next)=>
        {
            context.HttpContext.Response.Headers.CacheControl="no-store";
            try {return await next(context);}
            catch(UnauthorizedAccessException){return Results.StatusCode(403);}
            catch(InvalidOperationException error){return Results.BadRequest(new{error=error.Message});}
            catch(System.Text.Json.JsonException){return Results.BadRequest(new{error="Payslip presentation metadata is invalid. No configuration save was confirmed."});}
            catch(MySqlConnector.MySqlException){return Results.Json(new{error="Payslip design data could not be verified. Retry an uncertain save with the same request reference."},statusCode:503);}
        });
        static bool Manage(HttpContext context,int clientId)=>context.Items["User"] is AuthUser actor && PayslipDesignRepository.CanManage(actor,clientId);
        routes.MapGet("/sources",async(int clientId,PayslipDesignRepository repository,HttpContext context)=>Manage(context,clientId)?Results.Ok(await repository.SourcesAsync(clientId)):Results.StatusCode(403));
        routes.MapGet("/{templateId:long}",async(long templateId,int clientId,PayslipDesignRepository repository,HttpContext context)=>
        {
            if(!Manage(context,clientId))return Results.StatusCode(403);
            var profile=await repository.GetAsync(clientId,templateId);return profile is null?Results.NotFound():Results.Ok(profile);
        });
        routes.MapPost("/copy-preview",async(PayslipDesignCopyPreviewRequest request,PayslipDesignRepository repository,HttpContext context)=>
            Manage(context,request.ClientId) && Manage(context,request.SourceClientId)
                ? Results.Ok(await repository.CopyPreviewAsync(request,(AuthUser)context.Items["User"]!)) : Results.StatusCode(403))
            .WithMetadata(new CalculationOnlyPayrollReview());
        routes.MapPost("/core-preview",async(PayslipDesignPreviewRequest request,PayslipDesignRepository repository,HttpContext context)=>
            Manage(context,request.ClientId)?Results.Ok(await repository.CorePreviewAsync(request.ClientId)):Results.StatusCode(403))
            .WithMetadata(new CalculationOnlyPayrollReview());
        routes.MapPost("/preview",async(PayslipDesignPreviewRequest request,PayslipDesignRepository repository,HttpContext context)=>
            Manage(context,request.ClientId)?Results.Ok(await repository.PreviewAsync(request)):Results.StatusCode(403))
            .WithMetadata(new CalculationOnlyPayrollReview());
        routes.MapPost("/preview-pdf",async(PayslipDesignSaveRequest request,PayslipDesignRepository repository,HttpContext context)=>
        {
            if(request.Template is null || !Manage(context,request.Template.ClientId))return Results.StatusCode(403);
            var result=await repository.PreviewPdfAsync(request);
            return Results.File(result.Bytes,"application/pdf",result.FileName);
        }).WithMetadata(new CalculationOnlyPayrollReview());
        routes.MapPost("/save",async(PayslipDesignSaveRequest request,PayslipDesignRepository repository,HttpContext context)=>
            request.Template is not null && Manage(context,request.Template.ClientId)?Results.Ok(await repository.SaveAsync(request,(AuthUser)context.Items["User"]!)):Results.StatusCode(403));
        app.MapGet("/api/pay-runs/{id:int}/payslips/{employeeId:int}/pdf",async(int id,int employeeId,PayRunRepository runs,PayslipDesignRepository repository,HttpContext context)=>
        {
            context.Response.Headers.CacheControl="no-store";
            if(context.Items["User"] is not AuthUser actor || !actor.IsActive || actor.MustChangePassword || !WorkspacePermissions.CanReadPayslips(actor))return Results.StatusCode(403);
            var run=await runs.GetAsync(id);if(run is null)return Results.NotFound();
            if(!SelectedClientAccess.Allows(actor,run.ClientId))return Results.StatusCode(403);
            try {var result=await repository.CreatePdfAsync(run.ClientId,id,employeeId);return result is null?Results.NotFound():Results.File(result.Bytes,"application/pdf",result.FileName);}
            catch(InvalidOperationException error){return Results.BadRequest(new{error=error.Message});}
            catch(System.Text.Json.JsonException){return Results.BadRequest(new{error="Saved approved payroll or presentation metadata is invalid."});}
        });
    }
}
