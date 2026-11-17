using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipSaveWarningPreservationTests
{
    private static readonly DateTime ReviewedAt = new(2026, 10, 7, 12, 34, 56, DateTimeKind.Utc);
    private static ExcelPayslipRow Row(string id = "saved-row") => new()
    {
        Id = id, SourceRow = 2, EmployeeCode = "WARN-01", EmployeeName = "Synthetic person",
        Earnings = [new() { Label = "Basic", Amount = 1000.123456789012345678m }],
        Deductions = [new() { Label = "PF", Amount = 10m }],
        DeclaredGross = 980m, DeclaredDeductions = 10m, NetPay = 990.123456789012345678m,
        Information = [new() { Label = "Posting State", Value = "" }]
    };
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ExcelPayslipRepository.JsonOptions), ExcelPayslipRepository.JsonOptions)!;
    private static void Review(ExcelPayslipRow row) => row.WarningReview = new(ExcelPayslipRepository.NegativeAmountHash(row),
        "stored-reviewer@example.invalid", ReviewedAt, ExcelPayslipRepository.SourceRowWarnings(row));
    private static void Reuse(ExcelPayslipRow incoming, ExcelPayslipRow stored, int incomingClient = 42) =>
        ExcelPayslipRepository.ReusePayslipIdentity(new() { ClientId = incomingClient, Month = "2026-10", Rows = [incoming] },
            new() { ClientId = 42, Id = new('a', 32), Month = "2026-10", Rows = [stored] });

    [Fact]
    public void Info_only_save_retains_the_exact_trusted_review_and_discards_the_incoming_review_and_token()
    {
        var stored = Row(); Review(stored); var incoming = Clone(stored);
        incoming.Id = "browser-generated"; incoming.Information[0].Value = "Synthetic site";
        incoming.WarningReview = new("forged", "incoming-reviewer@example.invalid", ReviewedAt.AddDays(1), ["forged reason"]);
        incoming.WarningReviewToken = "incoming-response-token";
        Reuse(incoming, stored);
        Assert.Same(stored.WarningReview, incoming.WarningReview);
        Assert.Equal("stored-reviewer@example.invalid", incoming.WarningReview!.ReviewedBy);
        Assert.Equal(ReviewedAt, incoming.WarningReview.ReviewedAtUtc);
        Assert.Equal(stored.WarningReview.Warnings, incoming.WarningReview.Warnings);
        Assert.Empty(incoming.Warnings); Assert.Null(incoming.WarningReviewToken); Assert.Equal(stored.Id, incoming.Id);
    }

    [Theory]
    [InlineData("net")]
    [InlineData("earning")]
    [InlineData("deduction")]
    [InlineData("componentLabel")]
    public void Changed_money_or_component_identity_cannot_inherit_an_acknowledgement(string change)
    {
        var stored = Row(); Review(stored); var incoming = Clone(stored);
        switch (change)
        {
            case "net": incoming.NetPay += .01m; break;
            case "earning": incoming.Earnings[0].Amount += .01m; break;
            case "deduction": incoming.Deductions[0].Amount += .01m; break;
            default: incoming.Earnings[0].Label = "Other earning"; break;
        }
        Reuse(incoming, stored);
        Assert.Null(incoming.WarningReview); Assert.NotEqual(stored.Id, incoming.Id); Assert.NotEmpty(incoming.Warnings);
    }

    [Theory]
    [InlineData("row")]
    [InlineData("code")]
    [InlineData("name")]
    [InlineData("client")]
    public void Unmatched_or_foreign_identity_drops_even_a_well_formed_incoming_review(string change)
    {
        var stored = Row(); Review(stored); var incoming = Clone(stored);
        if (change == "row") incoming.SourceRow++;
        if (change == "code") incoming.EmployeeCode = "OTHER-01";
        if (change == "name") incoming.EmployeeName = "Another synthetic person";
        incoming.WarningReviewToken = "forged-token";
        Reuse(incoming, stored, change == "client" ? 43 : 42);
        Assert.Null(incoming.WarningReview); Assert.Null(incoming.WarningReviewToken);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("reason")]
    [InlineData("partialReasons")]
    [InlineData("reviewer")]
    public void Stale_or_reason_mismatched_stored_reviews_are_not_restored(string difference)
    {
        var stored = Row(); stored.NetPay = 950m; Review(stored);
        stored.WarningReview = difference switch
        {
            "hash" => stored.WarningReview! with { AmountsHash = "old-hash" },
            "reason" => stored.WarningReview! with { Warnings = ["Different review reason"] },
            "partialReasons" => stored.WarningReview! with { Warnings = stored.WarningReview!.Warnings.Take(1).ToList() },
            _ => stored.WarningReview! with { ReviewedBy = "" }
        };
        var incoming = Clone(stored); Reuse(incoming, stored);
        Assert.Null(incoming.WarningReview); Assert.NotEmpty(incoming.Warnings);
    }

    [Fact]
    public void A_new_row_never_supplies_its_own_acknowledgement()
    {
        var incoming = Row("new"); Review(incoming); incoming.WarningReviewToken = "forged";
        ExcelPayslipRepository.ReusePayslipIdentity(new() { ClientId = 42, Month = "2026-10", Rows = [incoming] },
            new() { ClientId = 42, Id = new('a', 32), Month = "2026-10" });
        Assert.Null(incoming.WarningReview); Assert.Null(incoming.WarningReviewToken);
    }

    [Fact]
    public void Existing_negative_review_and_decimal_precision_survive_unchanged_money()
    {
        var stored = Row(); stored.Deductions[0].Amount = -10m;
        stored.NegativeAmountReview = new(ExcelPayslipRepository.NegativeAmountHash(stored), "negative-reviewer@example.invalid", ReviewedAt);
        Review(stored); var incoming = Clone(stored); incoming.NegativeAmountReview = null;
        incoming.Earnings[0].Amount += .000000000000000002m;
        Reuse(incoming, stored);
        Assert.Equal(stored.Earnings[0].Amount, incoming.Earnings[0].Amount);
        Assert.Same(stored.NegativeAmountReview, incoming.NegativeAmountReview);
        Assert.Same(stored.WarningReview, incoming.WarningReview); Assert.Empty(incoming.Warnings);
    }
}
