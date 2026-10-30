using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipMasterComparisonAccessTests
{
    [Theory]
    [InlineData("GetExcelPayslipMasterComparison","GET","employee-master-comparison")]
    [InlineData("PreviewExcelPayslipEmployeeIdentities","POST","employee-identity-preview")]
    public async Task PrivateMasterReadIsDeniedBeforeDatabaseForMissingPermissionOrWrongClient(string endpointName,string method,string path)
    {
        var builder=WebApplication.CreateSlimBuilder();builder.Configuration.Sources.Clear();builder.Configuration.AddInMemoryCollection();builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new ExcelPayslipRepository(builder.Configuration,null!,null!));builder.Services.AddSingleton(new ExcelPayslipPdfService());
        var user=new AuthUser {Id=1,IsActive=true,ClientId=33,Permissions=["payroll.run"]};var canRead=false;
        await using var app=builder.Build();ExcelPayslipEndpoints.Map(app,_=>canRead,_=>false,(_,client)=>client==33,_=>user);
        var endpoint=((IEndpointRouteBuilder)app).DataSources.SelectMany(s=>s.Endpoints).OfType<RouteEndpoint>().Single(e=>e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName==endpointName);
        await Check(33,403);canRead=true;await Check(33,403);user.Permissions=["employees.view"];await Check(11,403);
        user.IsActive=false;await Check(33,403);user.IsActive=true;user.MustChangePassword=true;await Check(33,403);
        async Task Check(int clientId,int expected){var context=new DefaultHttpContext {RequestServices=app.Services};context.SetEndpoint(endpoint);context.Request.Method=method;context.Request.Path="/api/excel-payslips/"+path;context.Request.QueryString=new QueryString($"?clientId={clientId}&employeeCode=SYN001");context.Response.Body=new MemoryStream();await endpoint.RequestDelegate!(context);Assert.Equal(expected,context.Response.StatusCode);Assert.Equal("no-store",context.Response.Headers.CacheControl.ToString());}
    }
}
