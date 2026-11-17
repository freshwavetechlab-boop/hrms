using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class PayrollReferenceInformationMiddlewareTests
{
    private sealed class CaptureLogger:ILogger<WorkflowActionMiddleware>
    {
        internal int Writes{get;private set;}
        public IDisposable? BeginScope<TState>(TState state)where TState:notnull=>null;
        public bool IsEnabled(LogLevel logLevel)=>true;
        public void Log<TState>(LogLevel logLevel,EventId eventId,TState state,Exception? exception,Func<TState,Exception?,string> formatter)=>Writes++;
    }
    [Fact]
    public async Task Metadata_only_reference_apply_marker_runs_handler_once_without_generic_rule_lookup_or_AfterSuccess_side_effects()
    {
        var logger=new CaptureLogger();var called=0;var context=new DefaultHttpContext();context.Response.Body=new MemoryStream();
        context.Request.Method="POST";context.Request.Path="/api/payslip-designs/reference-information/apply";
        context.Items["User"]=new AuthUser{Id=97790,IsActive=true,Permissions=["settings.manage","reports.payroll.view"],ClientAccessMode="Selected",AssignedClientIds=[97790]};
        context.SetEndpoint(new Endpoint(_=>Task.CompletedTask,new EndpointMetadataCollection(new PayrollReferenceMetadataOnly()),"ReferenceInformationApply"));
        var middleware=new WorkflowActionMiddleware(_=>{called++;return Task.CompletedTask;},logger);
        // Null dependencies deliberately prove the branch cannot perform a
        // workflow lookup, transition, resource-state write or notification.
        await middleware.InvokeAsync(context,null!,null!);
        Assert.Equal(1,called);Assert.Equal(0,logger.Writes);Assert.Equal(200,context.Response.StatusCode);
        Assert.Null(context.GetEndpoint()!.Metadata.GetMetadata<CalculationOnlyPayrollReview>());
    }
}
