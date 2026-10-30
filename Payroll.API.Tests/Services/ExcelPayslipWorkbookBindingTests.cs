using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;
public sealed class ExcelPayslipWorkbookBindingTests
{
    private static readonly string Hash=new('a',64), OtherHash=new('b',64), Id=new('c',32), OtherId=new('d',32);
    private static ExcelPayslipCalculationSource Source()=>new() {
        CaptureVersion=1,DependencyReviewStatus="Reviewed",WorkbookKey="wb_"+Hash,
        WorkbookFiles=[new() { WorkbookKey="wb_"+Hash,FileName="synthetic.xlsx",Sha256=Hash,OriginalId=Id }]
    };
    private static ExcelPayslipOriginal Original()=>new(Id,"synthetic.xlsx",100,Hash,DateTime.UnixEpoch);
    [Fact] public void ExactRetainedVersionIsValidWithoutReadingOrChangingOriginalBytes()=>Assert.Null(ExcelPayslipRepository.ValidateCapturedWorkbookBindings(Source(),[Original()]));
    [Fact] public void MissingOrOtherClientOriginalCannotBindByMatchingFilenameOrHash()=>Assert.Contains("client",ExcelPayslipRepository.ValidateCapturedWorkbookBindings(Source(),[Original() with { Id=OtherId }])!);
    [Fact] public void ChangedStoredHashRejectsBinding()=>Assert.NotNull(ExcelPayslipRepository.ValidateCapturedWorkbookBindings(Source(),[Original() with { Sha256=OtherHash }]));
    [Fact] public void DependencyMustMatchItsRetainedWorkbookVersion() {
        var s=Source();s.DependencySheets=[new() { WorkbookKey=s.WorkbookKey,SourceHash=OtherHash,SheetName="Attendance" }];
        Assert.Contains("version",ExcelPayslipRepository.ValidateCapturedWorkbookBindings(s,[Original()])!);
    }
    [Fact] public void SharedOriginalAndReviewedAliasCanBeReusedByDerivedSources() {
        var s=Source();s.WorkbookFiles.Add(new() { WorkbookKey="wb_"+OtherHash,FileName="dependency.xlsx",Sha256=OtherHash,OriginalId=OtherId });
        s.DependencySheets=[new() { WorkbookKey="wb_"+OtherHash,SourceHash=OtherHash,SheetName="Attendance" }];
        s.ExternalMappings=[new() { FromWorkbookKey=s.WorkbookKey,Reference="[1]",ToWorkbookKey="wb_"+OtherHash,Reviewed=true }];
        var originals=new[] {Original(),new ExcelPayslipOriginal(OtherId,"dependency.xlsx",100,OtherHash,DateTime.UnixEpoch)};
        Assert.Null(ExcelPayslipRepository.ValidateCapturedWorkbookBindings(s,originals));
        s.ExternalMappings[0].Reviewed=false;Assert.NotNull(ExcelPayslipRepository.ValidateCapturedWorkbookBindings(s,originals));
        s.DependencyReviewStatus="ReferenceOnly";Assert.Null(ExcelPayslipRepository.ValidateCapturedWorkbookBindings(s,originals));
    }
    [Theory] [InlineData("../unsafe.xlsx")] [InlineData("C:\\unsafe.xlsx")] [InlineData("")]
    public void UnsafeFilenameIsRejected(string name) {var s=Source();s.WorkbookFiles[0].FileName=name;Assert.NotNull(ExcelPayslipRepository.ValidateCapturedWorkbookBindings(s,[Original()]));}
    [Fact] public void DuplicatedOrNullBindingsFailClosed() {
        var s=Source();s.WorkbookFiles.Add(s.WorkbookFiles[0]);Assert.NotNull(ExcelPayslipRepository.ValidateCapturedWorkbookBindings(s,[Original()]));
        s.WorkbookFiles=[null!];Assert.NotNull(ExcelPayslipRepository.ValidateCapturedWorkbookBindings(s,[Original()]));
    }
    [Fact] public void LegacyLocalSourceRemainsAvailableButUnboundDependenciesNeedRecapture() {
        var s=new ExcelPayslipCalculationSource();Assert.Null(ExcelPayslipRepository.ValidateCapturedWorkbookBindings(s,[]));
        s.DependencySheets.Add(new());Assert.Contains("Re-capture",ExcelPayslipRepository.ValidateCapturedWorkbookBindings(s,[])!);
    }
    [Theory] [InlineData(0)] [InlineData(1)]
    public void ReferenceOnlyBlocksBothLegacyAndTransferReviewApply(int reviewVersion) {
        var plan=new ExcelPayrollSetupRepository.Plan { Source=Source() };
        plan.Source.DependencyReviewStatus="ReferenceOnly";
        ExcelPayrollSetupRepository.FinalizeTransferReview(plan,new() { TransferReviewVersion=reviewVersion },42,[]);
        Assert.False(plan.Preview.CanApply);Assert.Contains(plan.Preview.Issues,i=>i.Message.Contains("reference only"));
    }
}
