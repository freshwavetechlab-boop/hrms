using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public static class ExcelPayrollLocationCorrectionEndpoints
{
    public static void MapExcelPayrollLocationCorrections(this WebApplication app)
    {
        var routes = app.MapGroup("/api/excel-payroll-setup/location-corrections")
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(50000));
        routes.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (InvalidOperationException error) { return Results.BadRequest(new { error = error.Message }); }
            catch (System.Text.Json.JsonException) { return Results.BadRequest(new { error = "Saved source correction evidence is invalid; refresh its review." }); }
            catch (MySqlConnector.MySqlException failure)
            {
                // Do not log exception messages, SQL, parameters or employee data.
                var methods=new System.Diagnostics.StackTrace(failure,false).GetFrames()
                    .Select(frame=>frame.GetMethod()).Where(method=>method?.DeclaringType?.Assembly==typeof(ExcelPayrollLocationCorrectionEndpoints).Assembly)
                    .Select(method=>method!.DeclaringType!.FullName+"."+method.Name).Distinct().Take(12).ToArray();
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ExcelPayrollLocationCorrection")
                    .LogWarning("Location correction database request failed. Method={Method}; MySqlNumber={MySqlNumber}; SqlState={SqlState}; ApplicationMethods={ApplicationMethods}",
                        context.HttpContext.Request.Method,failure.Number,failure.SqlState,string.Join(";",methods));
                return Results.Json(new { error = "Correction outcome could not be confirmed. Check the receipt using the same request reference before retrying." }, statusCode: 503);
            }
        });
        routes.MapPost("/preview", async (int clientId, ExcelPayrollLocationCorrectionRequest request,
            ExcelPayrollSetupRepository repository, HttpContext context) =>
            context.Items["User"] is AuthUser actor ? Results.Ok(await repository.PreviewLocationCorrectionAsync(clientId, request, actor)) : Results.StatusCode(403))
            .WithMetadata(new CalculationOnlyPayrollReview());
        routes.MapPost("/apply", async (int clientId, ExcelPayrollLocationCorrectionApplyRequest request,
            ExcelPayrollSetupRepository repository, HttpContext context) =>
            context.Items["User"] is AuthUser actor ? Results.Ok(await repository.ApplyLocationCorrectionAsync(clientId, request, actor)) : Results.StatusCode(403))
            .WithMetadata(new RequireIsolatedPayrollProcessing());
    }
}
