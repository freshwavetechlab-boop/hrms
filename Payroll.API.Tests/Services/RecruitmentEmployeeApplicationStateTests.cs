using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class RecruitmentEmployeeApplicationStateTests
{
    [Theory]
    [InlineData(null,"Offer Released","Offer Released",false)]
    [InlineData(null,"Offer Accepted","Offer Accepted",false)]
    [InlineData(0,"Screening","New",false)]
    [InlineData(71,"Offer Released","Offer Released",true)]
    [InlineData(null,"Joined","New",true)]
    [InlineData(null,"Offer Accepted","Hired",true)]
    [InlineData(null," hired ","Offer Released",true)]
    [InlineData(null,"JOINED","Rejected",true)]
    public void LateResponsesPreserveLinkedOrJoinedApplicationsWithoutChangingUnimportedFlows(int? employeeId,string stage,string status,bool expected) =>
        Assert.Equal(expected,RecruitmentEmployeeApplicationState.PreserveOfferResponse(employeeId,stage,status));
}
