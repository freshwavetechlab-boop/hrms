using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Payroll.API.Models;
using Payroll.API.Services;
using Xunit;

namespace Payroll.API.Tests.Services;

public class CalculationOnlyWorkflowTests
{
    [Theory]
    [InlineData("/api/payroll-calculation/simulate")]
    [InlineData("/API/PAYROLL-CALCULATION/SIMULATE/")]
    [InlineData("/api/excel-payroll-setup/preview")]
    [InlineData("/api/excel-payslips/employee-code-preview")]
    [InlineData("/api/excel-payslips/employee-identity-preview")]
    public async Task Pure_calculations_never_resolve_workflows_or_publish_notifications(string path)
    {
        var logger = new ObservedLogger();
        var ran = false;
        var middleware = new WorkflowActionMiddleware(_ => { ran = true; return Task.CompletedTask; }, logger);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST"; context.Request.Path = path;
        context.Items["User"] = new AuthUser { Id = 7, IsActive = true };
        // Null repositories deliberately fail if the middleware tries to resolve
        // any workflow. Such failures are logged, so assert no swallowed lookup.
        await middleware.InvokeAsync(context, null!, null!);
        Assert.True(ran); Assert.Equal(0, logger.Errors);
    }

    [Theory]
    [InlineData("POST", "/api/excel-payroll-setup/apply")]
    [InlineData("POST", "/api/excel-payroll-setup/draft")]
    [InlineData("POST", "/api/payruns/submit")]
    [InlineData("POST", "/api/payroll-calculation/simulate-other")]
    [InlineData("DELETE", "/api/payroll-calculation/simulate")]
    [InlineData("POST", "/api/excel-payslips/batches")]
    [InlineData("POST", "/api/excel-payslips/employee-code-preview-other")]
    [InlineData("POST", "/api/excel-payslips/employee-identity-preview-other")]
    public void Real_business_actions_keep_existing_workflow_behavior(string method, string path) =>
        Assert.False(WorkflowActionMiddleware.IsCalculationOnlyRequest(method, path));

    private sealed class ObservedLogger : ILogger<WorkflowActionMiddleware>
    {
        public int Errors { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { if (level >= LogLevel.Error) Errors++; }
    }
}
