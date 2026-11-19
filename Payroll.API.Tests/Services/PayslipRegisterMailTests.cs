using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class PayslipRegisterMailTests
{
    private static PayRunEmployee Employee() => new() { Id=18,PayRunId=98,ClientId=34,EmployeeId=11,EmployeeCode="TEST001",NetPay=900,GrossPay=1000,StatutoryDeductions=100 };
    private static NotificationRepository.RegisterMailQueue Queue(string state="Sent",long id=1) => new()
    { Id=id,EmployeeId=11,ClientId=34,EventCode=NotificationRepository.RegisterPdfEvent,ResourceType=NotificationRepository.RegisterPdfResource,
        ResourceId="98:18:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",Status=state,State=state,Recipient="employee@example.test",SentCopyStatus="Saved" };

    [Fact]
    public void SelectionRequiresOneValidCurrentEmployeeEmailAndNoPreviousAttempt()
    {
        var ready=NotificationRepository.RegisterEmployeeStatus(Employee(),"employee@example.test",[]);
        Assert.True(ready.CanSend);Assert.False(ready.CanResend);Assert.Equal("Not sent",ready.Status);
        foreach(var email in new[]{"","invalid","a@example.test;b@example.test"})
        { var missing=NotificationRepository.RegisterEmployeeStatus(Employee(),email,[]);Assert.False(missing.CanSend);Assert.False(missing.CanResend); }
    }

    [Theory]
    [InlineData("Sent")][InlineData("Delivered")][InlineData("Bounced")][InlineData("Failed")]
    public void CompletedAttemptsRequireTheSeparateResendPath(string status)
    {
        var result=NotificationRepository.RegisterEmployeeStatus(Employee(),"corrected@example.test",[Queue(status)]);
        Assert.False(result.CanSend);Assert.True(result.CanResend);Assert.Equal("corrected@example.test",result.Email);
        Assert.Equal("employee@example.test",result.LastRecipient);
    }

    [Theory]
    [InlineData("Queued")][InlineData("Deferred")][InlineData("Unknown")]
    public void AnyUnresolvedAttemptBlocksBothSendAndResendEvenIfANewerAttemptLooksComplete(string state)
    {
        var result=NotificationRepository.RegisterEmployeeStatus(Employee(),"employee@example.test",[Queue(state),Queue("Sent",2)]);
        Assert.False(result.CanSend);Assert.False(result.CanResend);
    }

    [Fact]
    public void SentFolderFailureCannotBecomeAFailedOrUnsentEmail()
    {
        var queue=Queue();queue.SentCopyStatus="Failed";queue.SentAt=DateTime.UtcNow;queue.Status="Retry";
        Assert.Equal("Sent",NotificationRepository.RegisterQueueState(queue));
        Assert.False(NotificationRepository.RegisterEmployeeStatus(Employee(),"employee@example.test",[queue]).CanSend);
    }

    [Theory]
    [InlineData("Pending")][InlineData("Retry")][InlineData("Processing")]
    [InlineData(NotificationRepository.RegisterPdfPending)][InlineData(NotificationRepository.RegisterPdfRetry)]
    public void BothLegacyAndPdfWorkerStatesAreRecognizedAsQueued(string status)
    { var row=Queue();row.Status=status;Assert.Equal("Queued",NotificationRepository.RegisterQueueState(row)); }

    [Fact]
    public void RequestReplayIsBoundToActorClientRunSelectionAndAcknowledgedResend()
    {
        var request=new PayslipMailJobRequest{RequestId=Guid.NewGuid().ToString("N"),EmployeeIds=[1,2]};
        Assert.Null(NotificationRepository.ValidateRegisterMailRequest(request));
        var original=NotificationRepository.RegisterMailFingerprint(34,98,7,request);
        request.EmployeeIds=[2,1];Assert.Equal(original,NotificationRepository.RegisterMailFingerprint(34,98,7,request));
        Assert.NotEqual(original,NotificationRepository.RegisterMailFingerprint(35,98,7,request));
        Assert.NotEqual(original,NotificationRepository.RegisterMailFingerprint(34,99,7,request));
        Assert.NotEqual(original,NotificationRepository.RegisterMailFingerprint(34,98,8,request));
        request.Resend=true;Assert.NotNull(NotificationRepository.ValidateRegisterMailRequest(request));
        request.AcknowledgeResend=true;Assert.Null(NotificationRepository.ValidateRegisterMailRequest(request));
        Assert.NotEqual(original,NotificationRepository.RegisterMailFingerprint(34,98,7,request));
        request.EmployeeIds=[1,1];Assert.NotNull(NotificationRepository.ValidateRegisterMailRequest(request));
    }

    [Fact]
    public void ProgressSeparatesAcceptanceSentCopyAndRecipientDeliveryAndRejectsMismatchedQueueIdentity()
    {
        var row=Queue();row.SentAt=DateTime.UtcNow;row.SentCopyStatus="Failed";
        var record=new NotificationRepository.RegisterMailRecord{Id="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",ClientId=34,PayRunId=98,
            Items=[new(){EmployeeId=11,QueueId=1,Status="Queued"}],Documents=new(){[11]=new(){PayRunEmployeeId=18}}};
        var job=NotificationRepository.RegisterJobSummary(record,[row]);
        Assert.Equal(1,job.AcceptedEmails);Assert.Equal(0,job.SentCopySavedEmails);Assert.Equal(1,job.SentCopyUnverifiedEmails);
        Assert.Equal(0,job.DeliveredEmails);Assert.True(job.IsComplete);Assert.Equal("Needs attention",job.Status);
        row.ResourceId="99:18:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var wrong=NotificationRepository.RegisterJobSummary(record,[row]);Assert.Equal(1,wrong.Unknown);Assert.Equal(0,wrong.AcceptedEmails);
    }

    [Fact]
    public void PayrollFingerprintIncludesAttendanceComponentsAndIdentityButDoesNotMutateTheSavedResult()
    {
        var row=Employee();var json=JsonSerializer.Serialize(row);var hash=NotificationRepository.RegisterPayrollHash(row);
        Assert.Equal(json,JsonSerializer.Serialize(row));row.PayableDays=40;Assert.NotEqual(hash,NotificationRepository.RegisterPayrollHash(row));
        row=JsonSerializer.Deserialize<PayRunEmployee>(json)!;row.DetailsJson="[{\"amount\":1001}]";Assert.NotEqual(hash,NotificationRepository.RegisterPayrollHash(row));
    }
}
