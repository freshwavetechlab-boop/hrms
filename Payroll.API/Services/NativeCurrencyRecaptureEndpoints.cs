using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

// Explicit operator routes. Capture, recapture and scoped-design writes retain
// separate default-off compatibility gates in their owning repositories.
public static class NativeCurrencyRecaptureEndpoints
{
    internal static bool CanAccess(AuthUser? actor,int clientId) => actor is not null
        && PayRunRepository.CanManageReviewedProfessionalTax(actor,clientId);

    public static void MapNativeCurrencyRecapture(this WebApplication app)
    {
        static AuthUser? Actor(HttpContext context) => context.Items.TryGetValue("User",out var value)?value as AuthUser:null;
        var routes=app.MapGroup("/api/pay-runs")
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(25000));
        routes.AddEndpointFilter(async(context,next)=>
        {
            context.HttpContext.Response.Headers.CacheControl="no-store";
            try{return await next(context);}
            catch(UnauthorizedAccessException){return Results.StatusCode(403);}
            catch(InvalidOperationException error){return Results.Conflict(new{error=error.Message});}
            catch(System.Text.Json.JsonException){return Results.BadRequest(new{error="The native recapture review is invalid. No payroll changes were saved."});}
            catch(MySqlConnector.MySqlException){return Results.Json(new{error="Native recapture could not verify one complete snapshot. Retry the same reviewed request ID after checking its status."},statusCode:503);}
        });
        routes.MapGet("/{id:int}/currency-recapture-preview",async(int id,int clientId,string payPeriod,
            PayRunRepository repository,HttpContext context)=>
        {
            var actor=Actor(context);if(!CanAccess(actor,clientId))return Results.StatusCode(403);
            return Results.Ok(await repository.PreviewNativeCurrencyRecaptureAsync(id,clientId,payPeriod,actor!));
        }).WithMetadata(new RequireIsolatedPayrollProcessing()).WithName("PreviewNativeDraftCurrencyRecapture");
        routes.MapPost("/{id:int}/currency-recapture",async(int id,NativeCurrencyRecaptureRequest request,
            PayRunRepository repository,HttpContext context)=>
        {
            var actor=Actor(context);if(!CanAccess(actor,request.ClientId))return Results.StatusCode(403);
            return Results.Ok(await repository.ApplyNativeCurrencyRecaptureAsync(id,request,actor!));
        }).WithMetadata(new RequireIsolatedPayrollProcessing()).WithName("RecaptureNativeDraftCurrencyStage");
    }
}
