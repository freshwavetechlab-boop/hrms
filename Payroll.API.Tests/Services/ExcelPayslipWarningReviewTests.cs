using System.Text.Json;
using System.Text.Json.Nodes;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipWarningReviewTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";
    private static readonly DateTime ReviewedAt = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
    private static string Snapshot()
    {
        var json = JsonSerializer.SerializeToNode(new ExcelPayslipBatch { Id = Id, ClientId = 42, Month = "2026-09", Rows = [
            new() { Id = "one", SourceRow = 270, EmployeeCode = "001346", EmployeeName = "Reviewed employee",
                Information = [new() { Label = "Account No", Value = "000123456" }],
                Earnings = [new() { Label = "Basic", Amount = 853.923456789012345678m }],
                Deductions = [new() { Label = "Deduction", Amount = 994.673456789012345678m }], NetPay = -140.75m },
            new() { Id = "two", SourceRow = 271, EmployeeCode = "001347", EmployeeName = "Other employee", NetPay = -10m }
        ] }, ExcelPayslipRepository.JsonOptions)!;
        foreach (var row in json["rows"]!.AsArray())
        {
            var typed = row!.Deserialize<ExcelPayslipRow>(ExcelPayslipRepository.JsonOptions)!;
            row!["warnings"] = JsonSerializer.SerializeToNode(ExcelPayslipRepository.RowWarnings(typed));
        }
        json["futureMetadata"] = new JsonObject { ["keep"] = true };
        return json.ToJsonString();
    }
    private static string Patch(string json, string rowId = "one", int client = 42, string batchId = Id, string actor = "reviewer@example.test") =>
        ExcelPayslipRepository.PatchNegativeAmountReview(json, client, batchId, rowId, actor, ReviewedAt);
    private static ExcelPayslipBatch Read(string json) => JsonSerializer.Deserialize<ExcelPayslipBatch>(json, ExcelPayslipRepository.JsonOptions)!;

    [Fact]
    public void ReviewRemovesOnlySelectedNegativeWarningAndPreservesExactSalaryDocument()
    {
        var beforeJson = Snapshot(); var afterJson = Patch(beforeJson); var saved = Read(afterJson);
        Assert.Empty(saved.Rows[0].Warnings);
        Assert.Contains(ExcelPayslipRepository.NegativeAmountWarning, saved.Rows[1].Warnings);
        Assert.Equal(-140.75m, saved.Rows[0].NetPay);
        Assert.Equal("reviewer@example.test", saved.Rows[0].NegativeAmountReview!.ReviewedBy);
        Assert.Equal(ReviewedAt, saved.Rows[0].NegativeAmountReview!.ReviewedAtUtc);
        Assert.Contains("853.923456789012345678", afterJson);
        Assert.Contains("994.673456789012345678", afterJson);
        var before = JsonNode.Parse(beforeJson)!; var after = JsonNode.Parse(afterJson)!;
        after["rows"]![0]!.AsObject().Remove("negativeAmountReview");
        after["rows"]![0]!["warnings"] = before["rows"]![0]!["warnings"]!.DeepClone();
        Assert.True(JsonNode.DeepEquals(before, after));
        Assert.Equal(NotificationRepository.ExcelPayslipBatchHash(Read(beforeJson)), NotificationRepository.ExcelPayslipBatchHash(saved));
        saved.Rows[0].NetPay -= 1;
        Assert.NotEqual(NotificationRepository.ExcelPayslipBatchHash(Read(beforeJson)), NotificationRepository.ExcelPayslipBatchHash(saved));
        saved = Read(afterJson);
        var selection = new ExcelPayslipSelection { RowIds = ["one"] };
        Assert.Null(ExcelPayslipRepository.SelectRows(saved, selection).Error);
        selection.RowIds = ["two"];
        Assert.NotNull(ExcelPayslipRepository.SelectRows(saved, selection).Error);
    }

    [Fact]
    public void ReviewKeepsReconciliationWarningsAndCannotApproveOtherAmountsOrIdentities()
    {
        var json = JsonNode.Parse(Snapshot())!;
        json["rows"]![0]!["netPay"] = -150m;
        var saved = Read(Patch(json.ToJsonString()));
        Assert.DoesNotContain(ExcelPayslipRepository.NegativeAmountWarning, saved.Rows[0].Warnings);
        Assert.Single(saved.Rows[0].Warnings);
        Assert.Contains("do not match source net pay", saved.Rows[0].Warnings[0]);
        Assert.NotNull(ExcelPayslipRepository.SelectRows(saved, new() { RowIds = ["one"] }).Error);
        foreach (var change in new Action<ExcelPayslipRow>[] {
            row => row.NetPay -= 1, row => row.Earnings[0].Amount += 1, row => row.Deductions[0].Amount += 1,
            row => row.EmployerContributions.Add(new() { Label = "EPF", Amount = 1 }), row => row.DeclaredGross = 853,
            row => row.DeclaredDeductions = 994, row => row.EmployeeCode = "other", row => row.SourceRow++, row => row.Id = "other"
        })
        {
            var row = Read(Patch(Snapshot())).Rows[0]; change(row);
            Assert.Contains(ExcelPayslipRepository.NegativeAmountWarning, ExcelPayslipRepository.RowWarnings(row));
        }
    }

    [Fact]
    public void RetryPreservesFirstReviewerAndContactOnlyChangesDoNotInvalidateAmountsReview()
    {
        var saved = Patch(Snapshot());
        Assert.Equal(saved, Patch(saved, actor: "another@example.test"));
        var row = Read(saved).Rows[0]; row.Email = "updated@example.test";
        Assert.Empty(ExcelPayslipRepository.RowWarnings(row));
    }

    [Fact]
    public void UnknownRowsClientsBatchesAndRowsWithoutNegativeAmountsCannotBeReviewed()
    {
        var json = Snapshot();
        Assert.Throws<InvalidOperationException>(() => Patch(json, rowId: "foreign"));
        Assert.Throws<InvalidOperationException>(() => Patch(json, client: 43));
        Assert.Throws<InvalidOperationException>(() => Patch(json, batchId: new string('a', 32)));
        Assert.Throws<InvalidOperationException>(() => Patch(json, actor: ""));
        var positive = JsonNode.Parse(json)!; positive["rows"]![0]!["netPay"] = 0;
        Assert.Throws<InvalidOperationException>(() => Patch(positive.ToJsonString()));
    }
}
