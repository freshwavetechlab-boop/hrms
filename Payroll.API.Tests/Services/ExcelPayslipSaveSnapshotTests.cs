using System.Text.Json;using Payroll.API.Models;using Payroll.API.Repositories;using Payroll.API.Services;using Xunit;
namespace Payroll.API.Tests.Services;
public sealed class ExcelPayslipSaveSnapshotTests
{
 private static readonly string A=new('a',64),B=new('b',64),C=new('c',64);
 [Fact]public void OmittedConditionPreservesTheLegacyRequestShape(){var b=new ExcelPayslipBatch{Id=new string('a',32)};Assert.Null(ExcelPayslipRepository.ValidateExpectedSaveSnapshot(b));Assert.DoesNotContain("expectedSaveSnapshotHash",JsonSerializer.Serialize(b,new JsonSerializerOptions(JsonSerializerDefaults.Web)));}
 [Fact]public void ExactReviewedBatchSourceAndMailMaySave(){var hash=ExcelPayslipRepository.SaveSnapshotHash(A,B,C);Assert.True(ExcelPayslipRepository.ExpectedSaveSnapshotMatches(hash,A,B,C));Assert.True(ExcelPayslipRepository.ExpectedSaveSnapshotMatches(hash.ToUpperInvariant(),A.ToUpperInvariant(),B,C));}
 [Theory][InlineData(0)][InlineData(1)][InlineData(2)]public void AnyChangedAuthorityRejectsThePreviouslyReviewedSave(int slot){var expected=ExcelPayslipRepository.SaveSnapshotHash(A,B,C);var values=new[]{A,B,C};values[slot]=new string('d',64);Assert.False(ExcelPayslipRepository.ExpectedSaveSnapshotMatches(expected,values[0],values[1],values[2]));}
 [Theory][InlineData("")][InlineData("bad")][InlineData(" abc")][InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa ")]
 public void MalformedConditionsAreRejected(string value){var b=new ExcelPayslipBatch{Id=new string('a',32),ExpectedSaveSnapshotHash=value};Assert.NotNull(ExcelPayslipRepository.ValidateExpectedSaveSnapshot(b));Assert.False(ExcelPayslipRepository.ExpectedSaveSnapshotMatches(value,A,B,C));}
 [Theory][InlineData("",false)][InlineData("not-a-guid",false)][InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",true)]public void ConditionCannotTurnAnImportOrLocationRetryIntoAnOverwrite(string id,bool location){var b=new ExcelPayslipBatch{Id=id,CreateLocationBatch=location,ExpectedSaveSnapshotHash=A};Assert.NotNull(ExcelPayslipRepository.ValidateExpectedSaveSnapshot(b));}
 [Fact]public void ExistingMissingSourceHasItsOwnExactFingerprint(){var hash=ExcelPayslipRepository.SaveSnapshotHash(A,"",C);Assert.True(ExcelPayslipRepository.ExpectedSaveSnapshotMatches(hash,A,"",C));Assert.False(ExcelPayslipRepository.ExpectedSaveSnapshotMatches(hash,A,B,C));}
 [Fact]public void AReviewedPresetCannotOverwriteAChangedMappingProfile(){var hash=ExcelPayslipRepository.SaveSnapshotHash(A,B,C,A);Assert.True(ExcelPayslipRepository.ExpectedSaveSnapshotMatches(hash,A,B,C,A));Assert.False(ExcelPayslipRepository.ExpectedSaveSnapshotMatches(hash,A,B,C,B));}
 [Fact]public void IncludingOrOmittingPresetAuthorityIsAnExplicitDifferentCondition(){var without=ExcelPayslipRepository.SaveSnapshotHash(A,B,C);var with=ExcelPayslipRepository.SaveSnapshotHash(A,B,C,A);Assert.NotEqual(without,with);Assert.False(ExcelPayslipRepository.ExpectedSaveSnapshotMatches(without,A,B,C,A));}
 [Fact]public void AcceptedConditionIsNotSourcePayrollOrHistoryData(){var b=new ExcelPayslipBatch{Id=new string('a',32),ExpectedSaveSnapshotHash=A};Assert.Null(ExcelPayslipRepository.ValidateExpectedSaveSnapshot(b));var clone=JsonSerializer.Deserialize<ExcelPayslipBatch>(JsonSerializer.Serialize(b))!;clone.ExpectedSaveSnapshotHash=null;Assert.DoesNotContain("ExpectedSaveSnapshotHash",JsonSerializer.Serialize(clone));Assert.Equal(A,b.ExpectedSaveSnapshotHash);}
 [Theory][InlineData("amount")][InlineData("mapping")]
 public void AConditionallyReviewedCorrectionKeepsItsEmployeeRowButChangesThePdfAndReviewFingerprint(string correction)
 {
  var old=new ExcelPayslipBatch{Id=new string('a',32),ClientId=7,Month="2026-10",Rows=[new(){Id="existing-row",SourceRow=8,EmployeeCode="SYN1",EmployeeName="Synthetic Person",NetPay=100,Earnings=[new(){Label="Pay",Amount=100}]}]};
  var incoming=JsonSerializer.Deserialize<ExcelPayslipBatch>(JsonSerializer.Serialize(old))!;var row=incoming.Rows[0];row.Id="browser-new-id";
  old.Rows[0].WarningReview=new(ExcelPayslipRepository.NegativeAmountHash(old.Rows[0]),"reviewer@example.invalid",DateTime.UtcNow,["prior warning"]);row.WarningReview=old.Rows[0].WarningReview;
  if(correction=="amount"){row.Earnings[0].Amount=110;row.NetPay=110;}else row.Earnings.Add(new(){Label="Source arrears",Amount=10});
  var previousPdf=NotificationRepository.ExcelPayslipBatchHash(old);var previousReview=ExcelPayslipRepository.NegativeAmountHash(old.Rows[0]);ExcelPayslipRepository.ReusePayslipIdentity(incoming,old,preserveReviewedRowIdentity:true);
  Assert.Equal("existing-row",row.Id);Assert.Equal(old.Id,incoming.Id);Assert.Null(row.WarningReview);Assert.Null(row.WarningReviewToken);Assert.NotEqual(previousPdf,NotificationRepository.ExcelPayslipBatchHash(incoming));Assert.NotEqual(previousReview,ExcelPayslipRepository.NegativeAmountHash(row));
 }
 [Fact]public void ReviewedRowPreservationDoesNotPermitAForeignClientToReuseTheSameHistory()
 {
  var saved=new ExcelPayslipBatch{Id=new string('a',32),ClientId=7,Month="2026-10",Rows=[new(){Id="owned",SourceRow=8,EmployeeCode="SYN1",EmployeeName="Synthetic Person",Earnings=[new(){Label="Pay",Amount=100}],NetPay=100}]};
  var incoming=JsonSerializer.Deserialize<ExcelPayslipBatch>(JsonSerializer.Serialize(saved))!;incoming.ClientId=8;incoming.Rows[0].NetPay=90;ExcelPayslipRepository.ReusePayslipIdentity(incoming,saved,preserveReviewedRowIdentity:true);Assert.NotEqual("owned",incoming.Rows[0].Id);Assert.Null(incoming.Rows[0].WarningReview);
 }
}
