using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

// Endpoint metadata only: an observational preview cannot start a generic POST
// workflow or emit a business notification. Its endpoint retains all auth checks.
public sealed class CalculationOnlyPayrollReview { }

public static class ReviewedProfessionalTaxEndpoints
{
    public static void MapReviewedProfessionalTax(this WebApplication app)
    {
        var routes = app.MapGroup("/api/payroll-calculation/period-professional-tax")
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(300000));
        routes.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (InvalidOperationException error) { return Results.BadRequest(new { error = error.Message }); }
            catch (System.Text.Json.JsonException) { return Results.BadRequest(new { error = "The reviewed period PT source or request is invalid; no automatic zero was assumed." }); }
            catch (MySqlConnector.MySqlException) { return Results.Json(new { error = "Period PT data could not be verified. No control save was confirmed; retry only with the same request reference." }, statusCode: 503); }
        });
        routes.MapPost("/preview", async (ReviewedProfessionalTaxRequest request, PayRunRepository repository, HttpContext context) =>
            context.Items["User"] is AuthUser actor && PayRunRepository.CanManageReviewedProfessionalTax(actor, request.ClientId)
                ? Results.Ok(await repository.PreviewReviewedProfessionalTaxAsync(request, actor)) : Results.StatusCode(403))
            .WithMetadata(new CalculationOnlyPayrollReview()).WithName("PreviewReviewedPeriodProfessionalTax");
        routes.MapPost("/save", async (SaveReviewedProfessionalTaxRequest request, PayRunRepository repository, HttpContext context) =>
            request.Request is not null && context.Items["User"] is AuthUser actor && PayRunRepository.CanManageReviewedProfessionalTax(actor, request.Request.ClientId)
                ? Results.Ok(await repository.SaveReviewedProfessionalTaxAsync(request, actor)) : Results.StatusCode(403))
            .WithMetadata(new RequireIsolatedPayrollProcessing()).WithName("SaveReviewedPeriodProfessionalTax");
    }
}
