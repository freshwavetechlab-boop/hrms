using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class RecruitmentWorkOrderReviewTests
{
    private static RecruitmentWorkOrderReview Review() => new()
    {
        ClientId = 20, WorkOrderNumber = "WO-REVIEW", ReceivedAtUtc = new DateTime(2026, 9, 22, 10, 15, 0, DateTimeKind.Utc),
        Status = "Active", Remarks = "Reviewed note",
    };
    private static AuthUser Manager() => new() { ClientId = 20, Permissions = ["recruitment.manage"] };

    [Fact]
    public void RequestCreationPermissionDoesNotGrantWorkOrderEditing()
    {
        var user = Manager();
        user.Permissions = ["recruitment.view"];
        Assert.NotEmpty(RecruitmentRepository.ValidateWorkOrderReview(Review(), 20, user, true));
        user.Permissions = ["settings.manage"];
        Assert.Empty(RecruitmentRepository.ValidateWorkOrderReview(Review(), 20, user, true));
    }

    [Fact]
    public void BothRequestClientAndUserScopeMustMatch()
    {
        Assert.NotEmpty(RecruitmentRepository.ValidateWorkOrderReview(Review(), 21, Manager(), true));
        var user = Manager();
        user.ClientId = 21;
        Assert.NotEmpty(RecruitmentRepository.ValidateWorkOrderReview(Review(), 20, user, true));
        user.ClientId = null;
        Assert.Empty(RecruitmentRepository.ValidateWorkOrderReview(Review(), 20, user, true));
    }

    [Fact]
    public void BlankNumberIsOnlyAllowedForAutomaticCreation()
    {
        var review = Review();
        review.WorkOrderNumber = "  ";
        Assert.Empty(RecruitmentRepository.ValidateWorkOrderReview(review, 20, Manager(), false));
        Assert.NotEmpty(RecruitmentRepository.ValidateWorkOrderReview(review, 20, Manager(), true));
    }

    [Fact]
    public void StorageBoundsRejectWithoutTruncating()
    {
        var review = Review();
        review.WorkOrderNumber = new string('x', 120);
        Assert.Empty(RecruitmentRepository.ValidateWorkOrderReview(review, 20, Manager(), true));
        review.WorkOrderNumber += "x";
        Assert.NotEmpty(RecruitmentRepository.ValidateWorkOrderReview(review, 20, Manager(), true));
        Assert.Equal(121, review.WorkOrderNumber.Length);
        review.WorkOrderNumber = "WO-REVIEW";
        review.Remarks = new string('ह', 21845);
        Assert.Empty(RecruitmentRepository.ValidateWorkOrderReview(review, 20, Manager(), true));
        review.Remarks += "ह";
        Assert.NotEmpty(RecruitmentRepository.ValidateWorkOrderReview(review, 20, Manager(), true));
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Active")]
    [InlineData("On Hold")]
    [InlineData("Completed")]
    [InlineData("Cancelled")]
    public void ExistingWorkOrderStatusesRemainSupported(string status)
    {
        var review = Review();
        review.Status = status;
        Assert.Empty(RecruitmentRepository.ValidateWorkOrderReview(review, 20, Manager(), true));
    }

    [Fact]
    public void MissingDateAndInvalidStatusAreRejected()
    {
        var review = Review();
        review.ReceivedAtUtc = default;
        Assert.NotEmpty(RecruitmentRepository.ValidateWorkOrderReview(review, 20, Manager(), true));
        review = Review();
        review.Status = "Published";
        Assert.NotEmpty(RecruitmentRepository.ValidateWorkOrderReview(review, 20, Manager(), true));
    }
}
