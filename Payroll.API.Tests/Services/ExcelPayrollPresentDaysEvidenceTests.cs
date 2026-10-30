using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollPresentDaysEvidenceTests
{
    private static (ExcelPayrollSetupRepository.Plan Plan, ExcelPayrollSetupRequest Request) Fixture(bool mapped = true, decimal worked = 26)
    {
        var plan = new ExcelPayrollSetupRepository.Plan
        {
            Batch = new() { Id = new string('a', 32), ClientId = 11, Month = "2026-09" },
            Source = new() { Rows = [new() { SourceRow = 6, Cells = [new() { Value = "30" }, new() { Value = "30" }, new() { Value = worked.ToString(System.Globalization.CultureInfo.InvariantCulture) }] }] }
        };
        var row = new ExcelPayrollSetupRepository.PlannedRow { RowId = "source-6", SourceRow = 6,
            Preview = new() { RowId = "source-6", PayrollDays = 30, PayableDays = 30, PresentDays = mapped ? worked : 30 } };
        plan.Rows.Add(row); plan.Preview.Rows.Add(row.Preview);
        var request = new ExcelPayrollSetupRequest { PayrollDaysColumnIndex = 0, PayableDaysColumnIndex = 1,
            PresentDaysColumnIndex = mapped ? 2 : null, AttendanceCycleStartDay = 1, AttendanceCycleEndDay = 30,
            AttendanceReview = new() { Mode = "SourceSeed", Reviewed = true, SourceMonth = "2026-09", CycleStart = "2026-09-01", CycleEnd = "2026-09-30", PayableMeaning = "AlreadyPayable", PresentMeaning = mapped ? "ActualWorked" : "NotMapped" } };
        Review(plan, request);
        return (plan, request);
    }
    private static void Review(ExcelPayrollSetupRepository.Plan plan, ExcelPayrollSetupRequest request)
    {
        request.AttendanceReview!.SourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { plan.Batch, plan.Source }, new JsonSerializerOptions(JsonSerializerDefaults.Web))))).ToLowerInvariant();
        request.AttendanceReview.BoundSourceInputs = ExcelPayrollSetupRepository.AttendanceBinding(request);
    }
    [Fact] public void MissingWorkedColumnDoesNotBecomeSourceEvidenceOrWritableAttendance()
    {
        var (plan, request) = Fixture(false); var before = JsonSerializer.Serialize(plan.Source);
        ExcelPayrollSetupRepository.FinalizePresentDaysEvidence(plan, request, 11);
        var evidence = Assert.IsType<ExcelPayrollPresentDaysEvidence>(plan.Rows[0].Preview.PresentDaysEvidence);
        Assert.Null(evidence.SourceValue); Assert.Null(evidence.SourceCell); Assert.Equal(30, evidence.CalculationValue);
        Assert.False(evidence.ActualWorkedReviewed); Assert.Equal("NotMapped", evidence.Status); Assert.False(plan.Preview.CanApply);
        Assert.Equal(before, JsonSerializer.Serialize(plan.Source)); Assert.Equal(30, plan.Rows[0].Preview.PresentDays);
        ExcelPayrollSetupRepository.FinalizePresentDaysEvidence(plan, request, 11);
        Assert.Single(plan.Preview.Issues);
    }
    [Theory] [InlineData(26)] [InlineData(0)] [InlineData(26.5)]
    public void ReviewedNumericWorkedDaysRemainDistinctFromPayable(decimal worked)
    {
        var (plan, request) = Fixture(true, worked);
        ExcelPayrollSetupRepository.FinalizePresentDaysEvidence(plan, request, 11);
        var evidence = plan.Rows[0].Preview.PresentDaysEvidence!;
        Assert.True(evidence.ActualWorkedReviewed); Assert.Equal(worked, evidence.SourceValue); Assert.Equal("C6", evidence.SourceCell);
        Assert.Equal("ReviewedWorkedDays", evidence.Status); Assert.Equal(30, plan.Rows[0].Preview.PayableDays); Assert.True(plan.Preview.CanApply);
    }
    [Theory] [InlineData("meaning")] [InlineData("hash")] [InlineData("month")] [InlineData("client")] [InlineData("cycle")] [InlineData("binding")] [InlineData("not reviewed")]
    public void StaleOrUnreviewedMeaningsCannotAuthorizeAttendanceImport(string change)
    {
        var (plan, request) = Fixture();
        if (change == "meaning") request.AttendanceReview!.PresentMeaning = "NotMapped";
        if (change == "hash") request.AttendanceReview!.SourceHash = new string('b', 64);
        if (change == "month") request.AttendanceReview!.SourceMonth = "2026-08";
        if (change == "client") plan.Batch.ClientId = 12;
        if (change == "cycle") request.AttendanceReview!.CycleStart = "2026-08-31";
        if (change == "binding") request.FixedPayrollDays = 30;
        if (change == "not reviewed") request.AttendanceReview!.Reviewed = false;
        ExcelPayrollSetupRepository.FinalizePresentDaysEvidence(plan, request, 11);
        Assert.False(plan.Rows[0].Preview.PresentDaysEvidence!.ActualWorkedReviewed); Assert.False(plan.Preview.CanApply);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void KeepExistingAndSalaryOnlyChoicesNeverRequireInventedWorkedDays(bool keep)
    {
        var (plan, request) = Fixture(false); plan.Rows[0].KeepExisting = keep; request.ImportAttendance = keep;
        ExcelPayrollSetupRepository.FinalizePresentDaysEvidence(plan, request, 11);
        Assert.True(plan.Preview.CanApply); Assert.False(plan.Rows[0].Preview.PresentDaysEvidence!.ActualWorkedReviewed);
    }
    [Theory] [InlineData("missing")] [InlineData("blank")] [InlineData("changed")]
    public void MissingBlankOrChangedSourceNeverGetsWorkedCertification(string change)
    {
        var (plan, request) = Fixture();
        if (change == "missing") plan.Source.Rows.Clear();
        if (change == "blank") plan.Source.Rows[0].Cells[2]!.Value = "";
        if (change == "changed") plan.Source.Rows[0].Cells[2]!.Value = "25";
        Review(plan, request); ExcelPayrollSetupRepository.FinalizePresentDaysEvidence(plan, request, 11);
        Assert.False(plan.Rows[0].Preview.PresentDaysEvidence!.ActualWorkedReviewed); Assert.False(plan.Preview.CanApply);
    }
}
