using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class SelectedClientImportJobTests
{
    private static readonly IConfiguration EmptyConfiguration = new ConfigurationBuilder().Build();

    [Fact]
    public async Task EmployeeFailureStatusKeepsClientOwnershipWithoutDatabaseAccess()
    {
        var repository = new EmployeeRepository(EmptyConfiguration, new AuthRepository(EmptyConfiguration), null!);
        var failed = await repository.StartImportCsvJobAsync(20, null, "unsupported-mode", null, null);
        Assert.Equal("Failed", failed.State);
        Assert.Equal(20, failed.ClientId);
        Assert.Equal(20, (failed with { State = "Processing" }).ClientId);
        var services = new ServiceCollection().AddSingleton(repository).BuildServiceProvider();
        var store = new SelectedClientOwnershipStore(EmptyConfiguration, services);
        Assert.Equal(20, await store.ClientIdAsync(new("employee-import-job", failed.JobId.ToString())));
        Assert.Null(await store.ClientIdAsync(new("employee-import-job", Guid.NewGuid().ToString())));
        await AssertPollAllowedOnlyInOwnerClient("/api/employees/import-jobs/" + failed.JobId, store);
    }

    [Fact]
    public async Task EmployeeReviewedFailureAlsoCarriesOriginalClient()
    {
        var repository = new EmployeeRepository(EmptyConfiguration, new AuthRepository(EmptyConfiguration), null!);
        var failed = await repository.StartImportCsvJobAsync(21, null, "upsert", Guid.NewGuid(), null);
        Assert.Equal("Failed", failed.State);
        Assert.Equal(21, failed.ClientId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LeaveAndHolidayJobsRetainOwnerAcrossBackgroundFailure(bool leaveTypes)
    {
        // The intentionally absent connection string makes the background worker
        // fail before any connection or write; this exercises real job creation.
        var repository = new LeaveAttendanceRepository(EmptyConfiguration);
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("Code,Name\nTEST,Test\n"));
        var file = new FormFile(source, 0, source.Length, "file", "import.csv");
        var job = leaveTypes ? await repository.StartLeaveTypeImportJobAsync(20, file) : await repository.StartHolidayImportJobAsync(20, file);
        Assert.Equal(20, job.ClientId);
        var resource = leaveTypes ? "leave-type-import-job" : "holiday-import-job";
        var path = leaveTypes ? "/api/leave-attendance/leave-types/import-jobs/" : "/api/leave-attendance/holidays/import-jobs/";
        var services = new ServiceCollection().AddSingleton(repository).BuildServiceProvider();
        var store = new SelectedClientOwnershipStore(EmptyConfiguration, services);
        Assert.Equal(20, await store.ClientIdAsync(new(resource, job.JobId.ToString())));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ClientImportJobStatus? latest;
        while ((latest = leaveTypes ? repository.GetLeaveTypeImportJob(job.JobId) : repository.GetHolidayImportJob(job.JobId))?.State != "Failed")
            await Task.Delay(10, timeout.Token);
        Assert.Equal(20, latest.ClientId);
        await AssertPollAllowedOnlyInOwnerClient(path + job.JobId, store);
    }

    private static async Task AssertPollAllowedOnlyInOwnerClient(string path, ISelectedClientOwnershipLookup lookup)
    {
        foreach (var activeClient in new[] { 20, 21 })
        {
            var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream();
            context.Items["User"] = new AuthUser { Id = 8, IsActive = true, ClientAccessMode = "Selected", AssignedClientIds = [20, 21] };
            context.Request.Method = "GET"; context.Request.Path = path;
            context.Request.Headers[SelectedClientScopeMiddleware.Header] = activeClient.ToString();
            var called = false;
            await new SelectedClientScopeMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context, lookup);
            Assert.Equal(activeClient == 20, called);
            Assert.Equal(activeClient == 20 ? 200 : 403, context.Response.StatusCode);
        }
    }
}
