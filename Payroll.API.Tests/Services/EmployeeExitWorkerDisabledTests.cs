using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Services;
using Xunit;

namespace Payroll.API.Tests.Services;

public class EmployeeExitWorkerDisabledTests
{
    [Fact]
    public async Task Disabled_business_workers_do_not_apply_due_exits_on_startup()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["BackgroundWorkers:Enabled"] = "false" }).Build();
        using var worker = new EmployeeExitWorker(null!, NullLogger<EmployeeExitWorker>.Instance, configuration);
        await worker.StartAsync(CancellationToken.None);
        Assert.NotNull(worker.ExecuteTask);
        Assert.True(worker.ExecuteTask.IsCompletedSuccessfully);
        await worker.StopAsync(CancellationToken.None);
    }
}
