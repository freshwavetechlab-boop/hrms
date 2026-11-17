using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

// Server metadata only, attached to this one configuration apply route. The
// repository owns its Serializable fail-closed workflow isolation check.
internal sealed class PayrollReferenceMetadataOnly { }

// Explicit reviewed metadata routes. The writer is disabled by default and the
// repository rechecks client, source, workflow and frozen native evidence.
public static class PayrollReferenceInformationEndpoints
{
    public static void MapPayrollReferenceInformation(this WebApplication app)
    {
        var routes=app.MapGroup("/api/payslip-designs/reference-information")
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(70000));
        routes.AddEndpointFilter(async(context,next)=>
        {
            context.HttpContext.Response.Headers.CacheControl="no-store";
            try{return await next(context);}
            catch(UnauthorizedAccessException){return Results.StatusCode(403);}
            catch(InvalidOperationException error){return Results.BadRequest(new{error=error.Message});}
            catch(System.Text.Json.JsonException){return Results.BadRequest(new{error="Reference Information review metadata is invalid. No configuration outcome is confirmed."});}
            catch(MySqlConnector.MySqlException)
            {return Results.Json(new{error="Reference Information save could not be confirmed. Check or replay the same request GUID; do not assume rollback or create another request."},statusCode:503);}
        });
        routes.MapPost("/preview",async(PayrollReferenceInformationReviewRequest request,PayslipDesignRepository repository,HttpContext context)=>
            context.Items["User"] is AuthUser actor&&PayslipDesignRepository.CanManageReferenceInformation(actor,request.ClientId)
                ?Results.Ok(await repository.PreviewReferenceInformationAsync(request,actor)):Results.StatusCode(403))
            .WithMetadata(new CalculationOnlyPayrollReview()).WithName("PreviewPayrollReferenceInformation");
        routes.MapPost("/apply",async(int clientId,PayrollReferenceInformationApplyRequest request,PayslipDesignRepository repository,HttpContext context)=>
            context.Items["User"] is AuthUser actor&&PayslipDesignRepository.CanManageReferenceInformation(actor,clientId)
                ?Results.Ok(await repository.ApplyScopedReferenceInformationAsync(clientId,request,actor)):Results.StatusCode(403))
            .WithMetadata(new PayrollReferenceMetadataOnly()).WithName("ApplyPayrollReferenceInformation");
    }
}
