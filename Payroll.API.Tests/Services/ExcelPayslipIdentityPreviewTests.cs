using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipIdentityPreviewTests
{
    private static Employee Master(int id,string code,string mobile="",string bank="",string uan="",string email="")=>new(){Id=id,ClientId=33,EmployeeCode=code,FirstName="Synthetic "+id,IsActive=true,WorkEmail=email,PersonalDetails=new(){Mobile=mobile,UanNumber=uan},PaymentDetails=new(){BankAccountNo=bank}};
    private static ExcelPayslipIdentityInput Row(int row=43,string code="SOURCE-1",string mobile="",string bank="",string uan="",string email="")=>new(row,code,"Synthetic 1",email,mobile,uan,bank,"","");
    private static ExcelPayslipIdentityMatch Match(ExcelPayslipIdentityInput row,params Employee[] masters)=>ExcelPayslipRepository.BuildIdentityPreview(33,new([row]),masters).Rows.Single();

    [Theory]
    [InlineData("Phone")][InlineData("Bank Account")][InlineData("UAN")][InlineData("Email")]
    public void DifferentSourceCodeUsesUniqueExactIdentifierWithoutRenamingMaster(string kind)
    {
        var master=Master(1,"NHDC001",mobile:"9876543210",bank:"001234567890",uan:"123456789012",email:"source@example.invalid");
        var row=Row(mobile:kind=="Phone"?"+91 9876543210":"",bank:kind=="Bank Account"?"001234567890":"",uan:kind=="UAN"?"123456789012":"",email:kind=="Email"?"SOURCE@example.invalid":"");
        var before=JsonSerializer.Serialize(master);var result=Match(row,master);
        Assert.Equal("Existing",result.Status);Assert.Equal("NHDC001",result.MasterCode);Assert.Contains(kind,result.Basis);Assert.Empty(result.Reasons);
        Assert.Equal(before,JsonSerializer.Serialize(master));Assert.Equal("SOURCE-1",row.EmployeeCode);
        var json=JsonSerializer.Serialize(result);Assert.DoesNotContain("001234567890",json);Assert.DoesNotContain("9876543210",json);Assert.DoesNotContain("123456789012",json);
    }

    [Fact]
    public void DuplicateBankOrIdentifiersPointingToDifferentPeopleExplainReviewWithoutChoosingOldMaster()
    {
        var a=Master(1,"NHDC001",mobile:"9876543210",bank:"001234567890");var b=Master(2,"NHDC002",mobile:"9988776655",bank:"001234567890");
        var duplicate=Match(Row(bank:"001234567890"),a,b);
        Assert.Equal("Review",duplicate.Status);Assert.Empty(duplicate.MasterCode);Assert.Contains(duplicate.Reasons,x=>x.Contains("Bank Account matches 2"));
        b.PaymentDetails.BankAccountNo="009988776655";
        var conflict=Match(Row(mobile:"9988776655",bank:"001234567890"),a,b);
        Assert.Equal("Review",conflict.Status);Assert.Empty(conflict.MasterCode);Assert.Contains(conflict.Reasons,x=>x.Contains("different employees"));
    }

    [Fact]
    public void SharedOfficeContactsDoNotOverruleAnIndependentUniqueBankMatch()
    {
        var a=Master(1,"NHDC001",mobile:"9876543210",bank:"001234567890",email:"office@example.invalid");
        var b=Master(2,"NHDC002",mobile:"9876543210",bank:"009988776655",email:"office@example.invalid");
        var result=Match(Row(bank:"001234567890",mobile:"9876543210",email:"office@example.invalid"),a,b);
        Assert.Equal("Existing",result.Status);Assert.Equal("NHDC001",result.MasterCode);Assert.Equal(new[]{"Bank Account"},result.Basis);Assert.Empty(result.Reasons);
        Assert.Equal("Review",Match(Row(mobile:"9876543210",email:"office@example.invalid"),a,b).Status);
    }

    [Fact]
    public void ScientificNotationAndMissingLeadingZerosCannotBecomeExactBankMatches()
    {
        var master=Master(1,"NHDC001",bank:"001234567890");
        Assert.Equal("No match",Match(Row(bank:"1234567890") with {EmployeeName="Another person"},master).Status);
        var unreliable=Match(Row(bank:"1.2346E+11"),master);
        Assert.Equal("Review",unreliable.Status);Assert.Contains(unreliable.Reasons,x=>x.Contains("scientific notation"));
    }

    [Fact]
    public void NamesAndLocationOrDepartmentAreReviewEvidenceWithoutAutomaticPersonMatch()
    {
        var master=Master(1,"NHDC001");master.Department="Operations";
        var row=Row() with {Department="Operations"};
        var result=Match(row,master);Assert.Equal("Review",result.Status);Assert.Empty(result.MasterCode);Assert.Contains(result.Reasons,x=>x.Contains("location/department"));
        Assert.Equal("No match",Match(Row() with {EmployeeName="New employee"},master).Status);
    }

    [Fact]
    public void ClientBoundaryInactiveMatchesAndConflictingMappedColumnsRemainExplicit()
    {
        var a=Master(1,"NHDC001",bank:"001234567890");a.IsActive=false;
        var other=Master(2,"OTHER",bank:"001234567890");other.ClientId=11;
        var matched=Match(Row(bank:"001234567890"),a,other);Assert.Equal("Existing",matched.Status);Assert.False(matched.Active);
        var conflict=Match(Row(bank:"001234567890") with {ConflictingFields=["uan"]},a);
        Assert.Equal("Review",conflict.Status);Assert.Contains(conflict.Reasons,x=>x.Contains("Mapped source columns"));
    }

    [Fact]
    public void InvalidOrDuplicateSourceRowsFailBeforeAnyDatabaseRead()
    {
        foreach(var request in new[]{new ExcelPayslipIdentityPreviewRequest(null!),new([Row(),Row()]),new([Row() with {Email=new string('a',301)}]),new([Row() with {ConflictingFields=["unexpected"]}])})
            Assert.Throws<InvalidOperationException>(()=>ExcelPayslipRepository.BuildIdentityPreview(33,request,[]));
    }
}
