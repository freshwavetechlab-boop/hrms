using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipCodeOptionsAccessTests
{
    [Theory]
    [InlineData("GetExcelPayslipEmployeeCodeOptions", "employee-code-options", "GET")]
    [InlineData("PreviewExcelPayslipEmployeeCodes", "employee-code-preview", "POST")]
    public async Task SequenceMetadataRequiresReadPermissionAndPermittedClientBeforeOpeningDatabase(string endpointName, string path, string method)
    {
        var builder=WebApplication.CreateSlimBuilder(); builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection(); builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new ExcelPayslipRepository(builder.Configuration,null!,null!)); builder.Services.AddSingleton(new ExcelPayslipPdfService());
        var read=false; await using var app=builder.Build();
        ExcelPayslipEndpoints.Map(app,_=>read,_=>false,(_,client)=>client==33,_=>new AuthUser {Id=1,ClientId=33});
        var endpoint=((IEndpointRouteBuilder)app).DataSources.SelectMany(s=>s.Endpoints).OfType<RouteEndpoint>().Single(e=>e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName==endpointName);
        await Check("?clientId=33",403); read=true;
        await Check("?clientId=11",403); await Check("?clientId=0",400); await Check("",400);
        async Task Check(string query,int expected) {
            var context=new DefaultHttpContext {RequestServices=app.Services}; context.SetEndpoint(endpoint);
            context.Request.Path="/api/excel-payslips/"+path;context.Request.Method=method;context.Request.QueryString=new QueryString(query);context.Response.Body=new MemoryStream();
            if(method=="POST"){context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection());var bytes=System.Text.Encoding.UTF8.GetBytes("{\"codes\":[\"SYN001\"]}");context.Request.Body=new MemoryStream(bytes);context.Request.ContentLength=bytes.Length;context.Request.ContentType="application/json";}
            await endpoint.RequestDelegate!(context); Assert.Equal(expected,context.Response.StatusCode);Assert.Equal("no-store",context.Response.Headers.CacheControl.ToString());
        }
    }
    private sealed class BodyDetection : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
}
