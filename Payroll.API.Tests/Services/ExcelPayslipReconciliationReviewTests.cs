using System.Text.Json;
using System.Text.Json.Nodes;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipReconciliationReviewTests
{
    private const string Id = "1234567890abcdef1234567890abcdef";
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static ExcelPayslipBatch Read(string json) => JsonSerializer.Deserialize<ExcelPayslipBatch>(json, ExcelPayslipRepository.JsonOptions)!;
    private static string Snapshot()
    {
        var batch = new ExcelPayslipBatch { Id = Id, ClientId = 42, Month = "2026-10", Rows = [
            new() { Id = "subtotal", SourceRow = 2, EmployeeCode = "TEST-A", EmployeeName = "Synthetic A",
                Earnings = [new() { Label = "Basic", Amount = 1200.123456789012345678m }, new() { Label = "Additional allowance (C)", Amount = 60m }],
                Deductions = [new() { Label = "Deduction (B)", Amount = 100m }],
                NetPay = 1160.123456789012345678m, DeclaredGross = 1200.123456789012345678m, DeclaredDeductions = 100m },
            new() { Id = "mismatch", SourceRow = 3, EmployeeCode = "TEST-B", EmployeeName = "Synthetic B",
                Earnings = [new() { Label = "Basic", Amount = 1000m }], Deductions = [new() { Label = "Adjustment", Amount = -20m }],
                NetPay = 900m, DeclaredGross = 980m, DeclaredDeductions = 10m }
        ] };
        foreach (var row in batch.Rows) row.Warnings = ExcelPayslipRepository.RowWarnings(row);
        var node = JsonSerializer.SerializeToNode(batch, ExcelPayslipRepository.JsonOptions)!;
        node["futureMetadata"] = new JsonObject { ["preserve"] = "unchanged" };
        return node.ToJsonString();
    }
    private static ExcelPayslipWarningReviewRequest Request(string json, string action = "review", params string[] ids)
    {
        var batch = Read(json);
        return new() { Action = action, Rows = batch.Rows.Where(r => ids.Length == 0 || ids.Contains(r.Id))
            .Select(r => new ExcelPayslipWarningReviewSelection(r.Id, ExcelPayslipRepository.WarningReviewToken(batch, r))).ToList() };
    }
    private static string Patch(string json, ExcelPayslipWarningReviewRequest? request = null, string actor = "reviewer@example.test") =>
        ExcelPayslipRepository.PatchWarningReviews(json, 42, Id, request ?? Request(json), actor, Now);

    [Fact]
    public void ClearAllIsMetadataOnlyAndPreservesExactAmountsUnknownFieldsAndMailHash()
    {
        var original = Snapshot(); var reviewed = Patch(original); var batch = Read(reviewed);
        Assert.All(batch.Rows, row => { Assert.Empty(row.Warnings); Assert.True(ExcelPayslipRepository.HasCurrentWarningReview(row));
            Assert.Equal("reviewer@example.test", row.WarningReview!.ReviewedBy); Assert.Equal(Now, row.WarningReview.ReviewedAtUtc); });
        Assert.Contains("1200.123456789012345678", reviewed);
        Assert.Equal(NotificationRepository.ExcelPayslipBatchHash(Read(original)), NotificationRepository.ExcelPayslipBatchHash(batch));
        var before = JsonNode.Parse(original)!; var after = JsonNode.Parse(reviewed)!;
        for (var index = 0; index < batch.Rows.Count; index++)
        {
            after["rows"]![index]!.AsObject().Remove("warningReview");
            after["rows"]![index]!["warnings"] = before["rows"]![index]!["warnings"]!.DeepClone();
        }
        Assert.True(JsonNode.DeepEquals(before, after));
        Assert.Null(ExcelPayslipRepository.SelectRows(batch, new() { RowIds = ["subtotal", "mismatch"] }).Error);
    }

    [Fact]
    public void SingleReviewIsIdempotentAuditedAndReversibleWithoutNewBatch()
    {
        var original = Snapshot(); var request = Request(original, "review", "subtotal"); var reviewed = Patch(original, request);
        Assert.Equal(reviewed, Patch(reviewed, request, "second@example.test"));
        Assert.Empty(Read(reviewed).Rows[0].Warnings); Assert.NotEmpty(Read(reviewed).Rows[1].Warnings);
        Assert.NotNull(ExcelPayslipRepository.SelectRows(Read(reviewed), new() { RowIds = ["mismatch"] }).Error);
        var restored = Patch(reviewed, Request(reviewed, "restore", "subtotal"));
        Assert.Null(Read(restored).Rows[0].WarningReview);
        Assert.Equal(Read(original).Rows[0].Warnings, Read(restored).Rows[0].Warnings);
        Assert.Equal(NotificationRepository.ExcelPayslipBatchHash(Read(original)), NotificationRepository.ExcelPayslipBatchHash(Read(restored)));
        Assert.Equal(restored, Patch(restored, Request(restored, "restore", "subtotal")));
        Assert.Equal(Id, Read(restored).Id);
    }

    [Fact]
    public void StaleRowsRejectTheWholeBulkRequestAndFinancialEditsInvalidateReview()
    {
        var json = Snapshot(); var request = Request(json); request.Rows[1] = request.Rows[1] with { Token = new string('0', 64) };
        Assert.Throws<InvalidOperationException>(() => Patch(json, request));
        Assert.Null(Read(json).Rows[0].WarningReview);
        foreach (var change in new Action<ExcelPayslipRow>[] {
            row => row.NetPay++, row => row.DeclaredGross++, row => row.DeclaredDeductions++,
            row => row.Earnings[0].Amount++, row => row.Earnings[0].Label = "Other component",
            row => row.Deductions[0].Amount++, row => row.EmployerContributions.Add(new() { Label="Employer cost", Amount=5 }),
            row => row.EmployeeCode="TEST-C", row => row.EmployeeName="Other synthetic", row => row.SourceRow++, row => row.Id="other"
        })
        {
            var batch = Read(Patch(json)); var row = batch.Rows[0]; change(row);
            Assert.False(ExcelPayslipRepository.HasCurrentWarningReview(row));
            Assert.NotEmpty(ExcelPayslipRepository.RowWarnings(row));
        }
        var changed = Read(json); changed.Rows[1].NetPay++;
        var changedJson = JsonSerializer.Serialize(changed, ExcelPayslipRepository.JsonOptions);
        Assert.Throws<InvalidOperationException>(() => Patch(changedJson, Request(json)));
    }

    [Fact]
    public void OwnershipDistinctRowsAndReviewerAreRequired()
    {
        var json = Snapshot(); var request = Request(json);
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.PatchWarningReviews(json, 43, Id, request, "reviewer", Now));
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.PatchWarningReviews(json, 42, new string('a',32), request, "reviewer", Now));
        Assert.Throws<InvalidOperationException>(() => Patch(json, request, ""));
        request.Rows.Add(request.Rows[0]); Assert.Throws<InvalidOperationException>(() => Patch(json, request));
        request = Request(json); request.Rows[1] = request.Rows[1] with { RowId="foreign" };
        Assert.Throws<InvalidOperationException>(() => Patch(json, request));
        Assert.Throws<InvalidOperationException>(() => Patch(json, new() { Action="ignore" }));
        Assert.Throws<InvalidOperationException>(() => Patch(json, new() { Rows=[] }));
    }

    [Fact]
    public void ReviewDoesNotBypassEmploymentOrMasterSalaryGuards()
    {
        var batch = Read(Patch(Snapshot())); var row = batch.Rows[0]; row.Email="new@example.test";
        Assert.True(ExcelPayslipRepository.HasCurrentWarningReview(row));
        ExcelPayslipRepository.AnnotateWarningReviews(batch);
        var originalHash = NotificationRepository.ExcelPayslipBatchHash(batch);
        row.WarningReviewToken = "response-only";
        Assert.Equal(originalHash, NotificationRepository.ExcelPayslipBatchHash(batch));
        row.Employment = new(null, row.EmployeeCode, "Inactive", null, true, false);
        Assert.NotNull(ExcelPayslipRepository.SelectRows(batch, new() { RowIds=["subtotal"] }).Error);
    }

    [Fact]
    public void ExistingNegativeReviewAndGeneralReviewPreserveEmailHashAndRestoreSeparately()
    {
        var original = Snapshot();
        var negative = ExcelPayslipRepository.PatchNegativeAmountReview(original,42,Id,"mismatch","negative-reviewer",Now);
        var all = Patch(negative);
        Assert.Equal(NotificationRepository.ExcelPayslipBatchHash(Read(original)), NotificationRepository.ExcelPayslipBatchHash(Read(all)));
        var restored = Read(Patch(all, Request(all,"restore")));
        Assert.DoesNotContain(ExcelPayslipRepository.NegativeAmountWarning, restored.Rows[1].Warnings);
        Assert.NotEmpty(restored.Rows[1].Warnings);
        Assert.NotNull(restored.Rows[1].NegativeAmountReview);
    }
}
