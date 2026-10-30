using Payroll.API.Models;
using Payroll.API.Services;
using Xunit;

namespace Payroll.API.Tests.Services;

// Anonymous outcome-orchestration tests. These do not simulate MySQL locking,
// transaction rollback or a network failure during an actual database commit.
public class SelectedPayRunProcessingOutcomeTests
{
    [Theory]
    [InlineData("Draft")] [InlineData("Pending Approval")] [InlineData("Approved")]
    [InlineData("Partially Paid")] [InlineData("Paid")] [InlineData("Failed")]
    public async Task Fresh_committed_state_is_preserved_after_refresh_or_commit_acknowledgment_failure(string status)
    {
        var run = new PayRun { Id=91,Status=status,NetPay=22680m,
            Employees=[new(){EmployeeId=11,NetPay=22680m}] };
        var adjustment = new PayrollAdjustment { Id=101,Status="Applied",PayRunId=91,Amount=22680m };
        var failureWrites=0;
        var result=await PayRunProcessingOutcome.RecoverAsync(
            ()=>Task.FromResult(new PayRunProcessingOutcome.Snapshot(run,7)),
            current=> { failureWrites++; current.Status="Failed"; adjustment.Status="Approved"; return Task.FromResult(current); });
        Assert.Same(run,result); Assert.Equal(status,result.Status); Assert.Equal(22680m,result.NetPay);
        Assert.Single(result.Employees); Assert.Equal("Applied",adjustment.Status); Assert.Equal(91,adjustment.PayRunId);
        Assert.Equal(0,failureWrites);
    }

    [Fact]
    public async Task A_failed_current_outcome_read_does_not_record_failure_or_retry_calculation()
    {
        var reads=0;var writes=0;
        await Assert.ThrowsAsync<TimeoutException>(()=>PayRunProcessingOutcome.RecoverAsync(
            ()=>{reads++;throw new TimeoutException("Synthetic current-read failure");},
            run=>{writes++;return Task.FromResult(run);}));
        Assert.Equal(1,reads);Assert.Equal(0,writes);
    }

    [Theory]
    [InlineData(1,0,0)] [InlineData(0,1,0)] [InlineData(0,0,1)]
    public async Task Processing_with_any_result_evidence_or_totals_remains_uncertain_without_mutation(long evidence,decimal cost,decimal net)
    {
        var run=new PayRun { Id=91,Status="Processing",PayrollCost=cost,NetPay=net };
        var writes=0;
        var error=await Assert.ThrowsAsync<SelectedPayRunConflictException>(()=>PayRunProcessingOutcome.RecoverAsync(
            ()=>Task.FromResult(new PayRunProcessingOutcome.Snapshot(run,evidence)),
            current=>{writes++;current.Status="Failed";return Task.FromResult(current);}));
        Assert.Contains("before any retry",error.Message);Assert.Equal("Processing",run.Status);Assert.Equal(0,writes);
    }

    [Theory]
    [InlineData("Queued")] [InlineData("AwaitingProcessing")] [InlineData("Unknown")]
    public async Task An_unexpected_state_is_not_reclassified_as_a_calculation_failure(string status)
    {
        var writes=0;
        await Assert.ThrowsAsync<SelectedPayRunConflictException>(()=>PayRunProcessingOutcome.RecoverAsync(
            ()=>Task.FromResult(new PayRunProcessingOutcome.Snapshot(new(){Status=status},0)),
            run=>{writes++;return Task.FromResult(run);}));
        Assert.Equal(0,writes);
    }

    [Fact]
    public async Task Missing_run_is_an_uncertain_outcome_not_permission_to_recreate_or_fail_it()
    {
        var writes=0;
        await Assert.ThrowsAsync<SelectedPayRunConflictException>(()=>PayRunProcessingOutcome.RecoverAsync(
            ()=>Task.FromResult(new PayRunProcessingOutcome.Snapshot(null,0)),
            run=>{writes++;return Task.FromResult(run);}));
        Assert.Equal(0,writes);
    }

    [Fact]
    public async Task Only_verified_empty_processing_state_records_one_failure()
    {
        var run=new PayRun { Id=91,Status="Processing" };var writes=0;var reads=0;
        var result=await PayRunProcessingOutcome.RecoverAsync(
            ()=>{reads++;return Task.FromResult(new PayRunProcessingOutcome.Snapshot(run,0));},
            current=>{writes++;current.Status="Failed";current.ProcessingError="Synthetic rolled-back calculation";return Task.FromResult(current);});
        Assert.Same(run,result);Assert.Equal("Failed",result.Status);Assert.Empty(result.Employees);
        Assert.Equal(1,reads);Assert.Equal(1,writes);
    }

    [Fact]
    public async Task Uncertain_failure_recording_commit_is_not_automatically_retried()
    {
        var writes=0;
        await Assert.ThrowsAsync<TimeoutException>(()=>PayRunProcessingOutcome.RecoverAsync(
            ()=>Task.FromResult(new PayRunProcessingOutcome.Snapshot(new(){Status="Processing"},0)),
            run=>{writes++;throw new TimeoutException("Synthetic diagnostic commit acknowledgment failure");}));
        Assert.Equal(1,writes);
    }
}
