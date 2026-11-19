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
        routes.MapGet("/source-period-capabilities", (int clientId, HttpContext context, IConfiguration configuration) =>
            context.Items["User"] is AuthUser actor && PayRunRepository.CanManageReviewedProfessionalTax(actor, clientId)
                ? Results.Ok(new { version = 2, sourceZeroStatutorySupported = true, sourceAttendanceOnlySupported = true,
                    payableOnlyAttendanceEnabled = configuration.GetValue("Payroll:PayableOnlyAttendanceEnabled",false), closedMonthOnly = true, maximumRows = 100 }) : Results.StatusCode(403));
        routes.MapPost("/source-period/preview", async (ReviewedProfessionalTaxRequest request, PayRunRepository repository, HttpContext context) =>
        {
            if(context.Items["User"] is not AuthUser actor || !PayRunRepository.CanManageReviewedProfessionalTax(actor, request.ClientId))return Results.StatusCode(403);
            ReviewedSourcePeriodStatutory.RequireClosedZeroReview(request, EmployeeEmploymentRules.Today);
            return Results.Ok(await repository.PreviewReviewedProfessionalTaxAsync(request, actor));
        }).WithMetadata(new CalculationOnlyPayrollReview()).WithName("PreviewSourcePeriodStatutoryZeros");
        routes.MapPost("/source-period/save", async (SaveReviewedProfessionalTaxRequest request, PayRunRepository repository, HttpContext context) =>
            request.Request is not null && context.Items["User"] is AuthUser actor && PayRunRepository.CanManageReviewedProfessionalTax(actor, request.Request.ClientId)
                ? Results.Ok(await repository.SaveReviewedProfessionalTaxAsync(request, actor, sourcePeriod: true)) : Results.StatusCode(403))
            .WithName("SaveSourcePeriodStatutoryZeros");
        routes.MapPost("/source-attendance/preview", async (SourceMonthAttendanceRequest request, PayRunRepository repository, HttpContext context) =>
            context.Items["User"] is AuthUser actor && PayRunRepository.CanManageReviewedProfessionalTax(actor,request.ClientId)
                ? Results.Ok(await repository.PreviewSourceMonthAttendanceAsync(request,actor)) : Results.StatusCode(403))
            .WithMetadata(new CalculationOnlyPayrollReview()).WithName("PreviewSourceMonthAttendance");
        routes.MapPost("/source-attendance/save", async (SaveSourceMonthAttendanceRequest request, PayRunRepository repository, HttpContext context) =>
            request.Request is not null && context.Items["User"] is AuthUser actor && PayRunRepository.CanManageReviewedProfessionalTax(actor,request.Request.ClientId)
                ? Results.Ok(await repository.SaveSourceMonthAttendanceAsync(request,actor)) : Results.StatusCode(403))
            .WithName("SaveSourceMonthAttendance");
    }
}
