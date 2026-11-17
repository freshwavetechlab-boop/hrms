using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

// Synthetic transaction/trace witnesses only. This suite does not create a
// persisted receipt, certify a legacy run, or render/approve/send a payslip.
public sealed class NativePayslipCurrencyStageTests
{
    private static string H(char c) => new(c, 64);
    private sealed record Fixture(NativeCurrencyProfile Profile, NativeCurrencyRunState Saved,
        NativeCurrencyObservation Observation);

    private static Fixture Build()
    {
        var result = new PayrollSimulationResult
        {
            Mode = "Employee", ClientId = 7, EmployeeId = 11, PayPeriod = "2026-09", TemplateId = "500",
            Inputs = [new("PAYROLL_DAYS", 30, "Attendance / policy", "Days"), new("PRESENT_DAYS", 16, "Attendance result", "Days"),
                new("PAYABLE_DAYS", 16, "Attendance result", "Days"), new("ANNUAL_CTC", 184968, "Saved salary", "Amount")],
            Lines =
            [
                Line("WAGES", "Earning", 15414m, 15414m, 8220.80m, 8220.80m, true),
                Line("PF", "Deduction", 986.496m, 986.50m, 986.50m, 986.496m),
                Line("ESI", "Deduction", 61.656m, 61.66m, 61.66m, 61.656m),
                Line("PF_ER", "Benefit", 1068.704m, 1068.70m, 1068.70m, 1068.704m),
                Line("ESI_ER", "Benefit", 267.176m, 267.18m, 267.18m, 267.176m)
            ],
            Gross = 8220.80m, Deductions = 1048.16m, Benefits = 1335.88m, Net = 7172.64m
        };
        var details = JsonSerializer.Serialize(result.Lines.Select(l => new
            { Id = l.Code, Category = l.Category, amount = l.Earned, monthlyAmount = l.Monthly, StatutoryType = "Synthetic" }));
        var row = new NativeCurrencySavedRow(21, 11, "11111111-1111-4111-8111-111111111111", 9,
            H('c'), H('d'), H('e'), "500", 184968, 30, 16, 16, details, 15414,
            new(result.Gross.Value, result.Deductions.Value, result.Benefits.Value, result.Net.Value));
        var state = new NativeCurrencyRunState(7, "2026-09", 31, new string('b', 32), [row], []);
        var traces = result.Lines.Select((line, index) => new NativeCurrencyTrace(index + 1, state.RunId, row.EmployeeId,
            line.Code, line.Earned, H('1'), H('2'), NativePayslipCurrencyStage.NativeRawStageHash(state, row, line))).ToImmutableArray();
        state = state with { Traces = traces };
        var observation = new NativeCurrencyObservation(row.NativeRowId, NativeCurrencyCaptureOrigin.NativeProcessingTransaction,
            state.ProcessingTransactionId, H('f'), H('3'), row.InputSnapshotHash, row.EffectiveTemplateHash, row.SourceIdentityHash,
            new() { Mode = "Employee", ClientId = state.ClientId, EmployeeId = row.EmployeeId, PayPeriod = state.PayPeriod }, result);
        return new(new(true, 1, 7, "2026-09", new string('a', 32), H('4'), H('5'), 0){SourceDataHash=H('6')}, state, observation);
    }

    private static PayrollSimulationLine Line(string code, string category, decimal raw, decimal monthly,
        decimal earned, decimal unrounded, bool proRata = false) =>
        new(code, code, category, raw.ToString(CultureInfo.InvariantCulture), "", raw, monthly, earned,
            proRata, "Component / template default", "Actual native currency path", []) { UnroundedEarned = unrounded };

    private static NativeCurrencyStageReceipt Capture(Fixture f) => NativePayslipCurrencyStage.Capture(f.Profile, f.Saved, [f.Observation]);

    private static Fixture RefreshNative(Fixture f)
    {
        var result = f.Observation.Result;
        var row = f.Saved.Rows[0] with
        {
            DetailsJson = JsonSerializer.Serialize(result.Lines.Select(l => new
                { Id = l.Code, Category = l.Category, amount = l.Earned, monthlyAmount = l.Monthly })),
            Totals = new(result.Gross!.Value, result.Deductions!.Value, result.Benefits!.Value, result.Net!.Value)
        };
        var state = f.Saved with { Rows = [row] };
        state = state with { Traces = result.Lines.Select((line, index) => new NativeCurrencyTrace(index + 1,
            state.RunId, row.EmployeeId, line.Code, line.Earned, H('1'), H('2'),
            NativePayslipCurrencyStage.NativeRawStageHash(state, row, line))).ToImmutableArray() };
        return f with { Saved = state };
    }

    [Fact]
    public void Same_processing_capture_preserves_every_accounted_amount_and_freezes_original_PF_stage()
    {
        var f = Build(); var before = JsonSerializer.Serialize(f);
        var receipt = Capture(f);
        Assert.Equal(receipt, NativePayslipCurrencyStage.Verify(f.Profile, f.Saved, receipt, receipt.PayloadHash));
        var pf = receipt.Rows.Single().Lines.Single(l => l.Code == "PF");
        Assert.Equal(986.496m, pf.UnroundedEarned); Assert.Equal(986.50m, pf.AccountedEarned);
        Assert.Equal(986m, decimal.Round(ExcelPayrollSetupRepository.SourceDisplayPrecision(pf.UnroundedEarned), 0, MidpointRounding.AwayFromZero));
        Assert.Equal(987m, decimal.Round(pf.AccountedEarned, 0, MidpointRounding.AwayFromZero));
        Assert.Equal(before, JsonSerializer.Serialize(f));
    }

    [Fact]
    public void Disabled_profile_keeps_legacy_behavior_even_when_no_receipt_exists()
    {
        var f = Build();
        Assert.Null(NativePayslipCurrencyStage.Verify(f.Profile with { Enabled = false }, f.Saved, null, ""));
        Assert.Throws<InvalidOperationException>(() => NativePayslipCurrencyStage.Verify(f.Profile, f.Saved, null, ""));
    }

    [Fact]
    public void Receipt_is_a_deep_immutable_copy_and_round_trips_without_source_financial_values()
    {
        var f = Build(); var receipt = Capture(f);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(receipt, options);
        f.Observation.Result.Lines[1] = f.Observation.Result.Lines[1] with { UnroundedEarned = 999 };
        f.Observation.Request.AmountOverrides["PF"] = 999;
        Assert.Equal(json, JsonSerializer.Serialize(receipt, options));
        Assert.DoesNotContain("sourceRaw", json); Assert.DoesNotContain("sourceAmount", json);
        var restored = JsonSerializer.Deserialize<NativeCurrencyStageReceipt>(json, options)!;
        Assert.Equal(json, JsonSerializer.Serialize(restored, options));
        Assert.Same(restored, NativePayslipCurrencyStage.Verify(f.Profile, f.Saved, restored, receipt.PayloadHash));
    }

    [Fact]
    public void Zero_salary_employee_and_all_mapped_zero_lines_remain_in_complete_receipt()
    {
        var f = Build(); var result = f.Observation.Result;
        result.Lines = result.Lines.Select(l => l.Code == "WAGES" ? l with { Earned = 0, UnroundedEarned = 0 }
            : l with { Raw = 0, Monthly = 0, Earned = 0, UnroundedEarned = 0 }).ToList();
        result.Inputs[1] = result.Inputs[1] with { Value = 0 }; result.Inputs[2] = result.Inputs[2] with { Value = 0 };
        result.Gross = result.Deductions = result.Benefits = result.Net = 0;
        f = f with { Saved = f.Saved with { Rows = [f.Saved.Rows[0] with { PresentDays = 0, PayableDays = 0 }] } };
        f = RefreshNative(f);
        var receipt = Capture(f);
        Assert.Single(receipt.Rows); Assert.Equal(5, receipt.Rows[0].Lines.Length);
        Assert.All(receipt.Rows[0].Lines, line => Assert.Equal(0m, line.AccountedEarned));
        Assert.Equal(15414m, receipt.Rows[0].Lines.Single(l => l.Code == "WAGES").AccountedMonthly);
        Assert.NotNull(NativePayslipCurrencyStage.Verify(f.Profile, f.Saved, receipt, receipt.PayloadHash));
    }

    [Fact]
    public void Same_run_partial_roster_and_more_than_5000_rows_are_rejected()
    {
        var f = Build(); var row = f.Saved.Rows[0];
        var another = row with { NativeRowId = 22, EmployeeId = 12, SourceRow = 10, SourceRowId = "22222222-2222-4222-8222-222222222222" };
        Assert.Throws<InvalidOperationException>(() => NativePayslipCurrencyStage.Capture(f.Profile, f.Saved with { Rows = [row, another] }, [f.Observation]));
        var tooMany = Enumerable.Range(1, 5001).Select(id => row with { NativeRowId = id, EmployeeId = id }).ToImmutableArray();
        Assert.Throws<InvalidOperationException>(() => NativePayslipCurrencyStage.Capture(f.Profile, f.Saved with { Rows = tooMany }, [f.Observation]));
    }

    [Fact]
    public void Full_641_employee_run_is_representable_while_partial_and_foreign_evidence_are_rejected()
    {
        var f = Build(); var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var rows = Enumerable.Range(1, 641).Select(id => f.Saved.Rows[0] with
        {
            NativeRowId = id, EmployeeId = id, SourceRow = id + 5,
            SourceRowId = new Guid(id, 0, 0, new byte[8]).ToString("D")
        }).ToImmutableArray();
        var state = f.Saved with { Rows = rows };
        var observations = rows.Select(row =>
        {
            var result = JsonSerializer.Deserialize<PayrollSimulationResult>(JsonSerializer.Serialize(f.Observation.Result, options), options)!;
            result.EmployeeId = row.EmployeeId;
            return f.Observation with { NativeRowId = row.NativeRowId, Result = result,
                Request = new() { Mode = "Employee", ClientId = state.ClientId, EmployeeId = row.EmployeeId, PayPeriod = state.PayPeriod } };
        }).ToArray();
        state = state with { Traces = observations.SelectMany((o, index) => o.Result.Lines.Select((line, lineIndex) => new NativeCurrencyTrace(
            index * 5 + lineIndex + 1, state.RunId, rows[index].EmployeeId, line.Code, line.Earned, H('1'), H('2'),
            NativePayslipCurrencyStage.NativeRawStageHash(state, rows[index], line)))).ToImmutableArray() };
        var receipt = NativePayslipCurrencyStage.Capture(f.Profile, state, observations);
        Assert.Equal(641, receipt.Rows.Length);
        Assert.NotNull(NativePayslipCurrencyStage.Verify(f.Profile, state, receipt, receipt.PayloadHash));
        Assert.Throws<InvalidOperationException>(() => NativePayslipCurrencyStage.Capture(f.Profile, state, observations[..640]));
        var foreign = observations.ToArray(); foreign[^1].Result.ClientId = 8;
        Assert.Throws<InvalidOperationException>(() => NativePayslipCurrencyStage.Capture(f.Profile, state, foreign));
    }

    [Theory]
    [InlineData("roster")][InlineData("details-amount")][InlineData("details-category")][InlineData("monthly-gross")]
    public void Saved_native_state_invariants_and_bound_monthly_gross_cannot_be_omitted(string mutation)
    {
        var f = Build(); var receipt = Capture(f); var row = f.Saved.Rows[0];
        switch (mutation)
        {
            case "roster": Assert.Throws<InvalidOperationException>(() => NativePayslipCurrencyStage.Verify(f.Profile,
                f.Saved with { Rows = [] }, receipt, receipt.PayloadHash)); return;
            case "details-amount": row = row with { DetailsJson = row.DetailsJson.Replace("986.50", "986.49", StringComparison.Ordinal) }; break;
            case "details-category": row = row with { DetailsJson = row.DetailsJson.Replace("Deduction", "Benefit", StringComparison.Ordinal) }; break;
            case "monthly-gross": row = row with { MonthlyGross = row.MonthlyGross + 0.01m }; break;
        }
        Assert.Throws<InvalidOperationException>(() => NativePayslipCurrencyStage.Verify(f.Profile,
            f.Saved with { Rows = [row] }, receipt, receipt.PayloadHash));
    }

    [Theory]
    [InlineData("{}")][InlineData("[")][InlineData("[{}]")]
    [InlineData("[{\"Id\":\"PF\",\"Category\":\"Deduction\",\"amount\":\"wrong\",\"monthlyAmount\":0}]")]
    public void Malformed_saved_financial_details_are_an_actionable_closed_review_failure(string details)
    {
        var f = Build();
        var state = f.Saved with { Rows = [f.Saved.Rows[0] with { DetailsJson = details }] };
        Assert.StartsWith("Review native payslip currency evidence:", Assert.Throws<InvalidOperationException>(() =>
            NativePayslipCurrencyStage.Capture(f.Profile, state, [f.Observation])).Message);
    }

    [Theory]
    [InlineData("source")][InlineData("client")][InlineData("month")][InlineData("profile")]
    [InlineData("two-decimals")][InlineData("one-decimal")][InlineData("unreviewed")][InlineData("version")]
    public void Capture_requires_explicit_exact_source_bound_whole_rupee_profile(string mutation)
    {
        var f = Build(); var p = f.Profile;
        p = mutation switch
        {
            "source" => p with { SourceHash = "" }, "client" => p with { ClientId = 8 },
            "month" => p with { PayPeriod = "2026-10" }, "profile" => p with { ProfileHash = "" },
            "two-decimals" => p with { AmountDecimalPlaces = 2 }, "one-decimal" => p with { AmountDecimalPlaces = 1 },
            "unreviewed" => p with { Enabled = false }, _ => p with { Version = 2 }
        };
        Assert.Throws<InvalidOperationException>(() => NativePayslipCurrencyStage.Capture(p, f.Saved, [f.Observation]));
    }

    [Theory]
    [InlineData("reconstructed")][InlineData("transaction")][InlineData("missing-raw-trace")][InlineData("raw-stage-hash")]
    [InlineData("missing-trace")][InlineData("duplicate-trace")][InlineData("foreign-trace")][InlineData("raw-stage")]
    [InlineData("accounted-monthly")][InlineData("accounted-earned")][InlineData("ordinary-proof")][InlineData("source-proof")]
    [InlineData("identity")][InlineData("inputs")][InlineData("template")][InlineData("incomplete-roster")]
    public void Reconstructed_stale_missing_or_foreign_native_evidence_cannot_create_receipt(string mutation)
    {
        var f = Build(); var state = f.Saved; var observed = f.Observation;
        switch (mutation)
        {
            case "reconstructed": observed = observed with { Origin = NativeCurrencyCaptureOrigin.ReconstructedSimulation }; break;
            case "transaction": observed = observed with { ProcessingTransactionId = new string('c', 32) }; break;
            case "missing-raw-trace": state = state with { Traces = state.Traces.Select(t => t with { NativeRawStageHash = "" }).ToImmutableArray() }; break;
            case "raw-stage-hash": state = state with { Traces = state.Traces.SetItem(1, state.Traces[1] with { NativeRawStageHash = H('9') }) }; break;
            case "missing-trace": state = state with { Traces = state.Traces.RemoveAt(1) }; break;
            case "duplicate-trace": state = state with { Traces = state.Traces.Add(state.Traces[0]) }; break;
            case "foreign-trace": state = state with { Traces = state.Traces.SetItem(1, state.Traces[1] with { EmployeeId = 99 }) }; break;
            case "raw-stage": observed.Result.Lines[1] = observed.Result.Lines[1] with { UnroundedEarned = 986.499m }; break;
            case "accounted-monthly": observed.Result.Lines[1] = observed.Result.Lines[1] with { Monthly = 986.49m }; break;
            case "accounted-earned": observed.Result.Lines[1] = observed.Result.Lines[1] with { Earned = 986.49m }; break;
            case "ordinary-proof": observed = observed with { OrdinaryEvidenceHash = "" }; break;
            case "source-proof": observed = observed with { IndependentSourceProofHash = "" }; break;
            case "identity": observed = observed with { SourceIdentityHash = H('8') }; break;
            case "inputs": observed = observed with { InputSnapshotHash = H('8') }; break;
            case "template": observed = observed with { EffectiveTemplateHash = H('8') }; break;
            case "incomplete-roster": Assert.Throws<InvalidOperationException>(() => NativePayslipCurrencyStage.Capture(f.Profile, state, [])); return;
        }
        Assert.Throws<InvalidOperationException>(() => NativePayslipCurrencyStage.Capture(f.Profile, state, [observed]));
    }

    [Theory]
    [InlineData("days")][InlineData("annual")][InlineData("amount")][InlineData("snapshot")][InlineData("named")]
    [InlineData("input-source")][InlineData("payable")][InlineData("result-scope")][InlineData("result-incomplete")]
    [InlineData("missing-line")][InlineData("duplicate-line")][InlineData("missing-raw")][InlineData("totals")]
    public void What_if_or_incomplete_ordinary_results_are_not_processing_evidence(string mutation)
    {
        var f = Build(); var o = f.Observation;
        switch (mutation)
        {
            case "days": o.Request.PayableDays = 16; break;
            case "annual": o.Request.AnnualCtc = 184968; break;
            case "amount": o.Request.AmountOverrides["PF"] = 986.50m; break;
            case "snapshot": o.Request.Snapshot = new("Template", 7, "", 184968, [], []); break;
            case "named": o.Request.NamedInputs = new(); break;
            case "input-source": o.Result.Inputs[0] = o.Result.Inputs[0] with { Source = "What-if" }; break;
            case "payable": o.Result.Inputs[2] = o.Result.Inputs[2] with { Value = 15 }; break;
            case "result-scope": o.Result.ClientId = 8; break;
            case "result-incomplete": o.Result.Issues.Add(new("Error", "Proof", "Missing", "Review")); break;
            case "missing-line": o.Result.Lines.RemoveAt(1); break;
            case "duplicate-line": o.Result.Lines.Add(o.Result.Lines[1]); break;
            case "missing-raw": o.Result.Lines[1] = o.Result.Lines[1] with { UnroundedEarned = null }; break;
            case "totals": o.Result.Net += 0.01m; break;
        }
        Assert.Throws<InvalidOperationException>(() => Capture(f));
    }

    [Theory]
    [InlineData("source")][InlineData("profile")][InlineData("details")][InlineData("input")][InlineData("identity")]
    [InlineData("template")][InlineData("totals")][InlineData("rerun-same-money")][InlineData("trace-payload")]
    [InlineData("processing-marker")][InlineData("run")][InlineData("foreign-client")][InlineData("forged-digest")]
    public void Verification_fails_closed_after_any_protected_binding_or_processing_epoch_change(string mutation)
    {
        var f = Build(); var receipt = Capture(f); var p = f.Profile; var state = f.Saved; var trusted = receipt.PayloadHash;
        var row = state.Rows[0];
        switch (mutation)
        {
            case "source": p = p with { SourceHash = H('8') }; break;
            case "profile": p = p with { ProfileHash = H('8') }; break;
            case "details": row = row with { DetailsJson = row.DetailsJson + " " }; break;
            case "input": row = row with { InputSnapshotHash = H('8') }; break;
            case "identity": row = row with { SourceIdentityHash = H('8') }; break;
            case "template": row = row with { EffectiveTemplateHash = H('8') }; break;
            case "totals": row = row with { Totals = row.Totals with { Net = row.Totals.Net + 0.01m } }; break;
            case "rerun-same-money": state = state with { Traces = state.Traces.Select(t => t with { Id = t.Id + 100 }).ToImmutableArray() }; break;
            case "trace-payload": state = state with { Traces = state.Traces.SetItem(1, state.Traces[1] with { OutputHash = H('8') }) }; break;
            case "processing-marker": state = state with { ProcessingTransactionId = new string('c', 32) }; break;
            case "run": state = state with { RunId = 32 }; break;
            case "foreign-client": state = state with { ClientId = 8 }; break;
            case "forged-digest": trusted = H('8'); break;
        }
        state = state with { Rows = state.Rows.SetItem(0, row) };
        Assert.Throws<InvalidOperationException>(() => NativePayslipCurrencyStage.Verify(p, state, receipt, trusted));
    }

    [Theory]
    [InlineData("1.005")][InlineData("1.015")][InlineData("-1.005")][InlineData("-1.015")]
    public void Monthly_midpoints_match_actual_existing_payroll_calculator_including_negative_clamp(string rawText)
    {
        var raw = decimal.Parse(rawText, CultureInfo.InvariantCulture);
        var component = new PayrollReviewComponent("1", "VALUE", "Earning", "Regular Earning", "None", "Formula",
            "0", rawText, "", false, true, true, 10, "Fixed Pay");
        var snapshot = new PayrollReviewSnapshot("Template", 7, "", 12, [component],
            [new("1", "0", rawText, "Formula", "", "false")]);
        var actual = Assert.Single(PayRunRepository.PreviewAdvisorTemplate(snapshot, 30, 30));
        Assert.Equal(actual.FullPeriod, NativePayslipCurrencyStage.AccountedMonthly(raw));
        Assert.Equal(actual.Earned, NativePayslipCurrencyStage.AccountedEarned(raw, "Monthly"));
    }

    [Theory]
    [InlineData("1.01", "Prorated", 15)]
    [InlineData("1.03", "Prorated", 15)]
    [InlineData("1.005", "DailyRate", 1)]
    [InlineData("1.015", "DailyRate", 1)]
    public void Earned_midpoints_follow_actual_daily_and_prorated_paths(string value, string path, int payable)
    {
        var daily = path == "DailyRate"; var prorated = path == "Prorated";
        var type = daily ? "Daily Rate" : "Formula";
        var component = new PayrollReviewComponent("1", "VALUE", "Earning", "Regular Earning", "None", type,
            value, value, "", prorated, true, true, 10, "Fixed Pay");
        var snapshot = new PayrollReviewSnapshot("Template", 7, "", 12, [component],
            [new("1", value, value, type, "", prorated ? "true" : "false")]);
        var actual = Assert.Single(PayRunRepository.PreviewAdvisorTemplate(snapshot, 30, payable));
        var raw = daily ? decimal.Parse(value, CultureInfo.InvariantCulture) * payable : actual.FullPeriod * payable / 30;
        Assert.Equal(actual.Earned, NativePayslipCurrencyStage.AccountedEarned(raw, path));
        var f = Build();
        var line = Line("VALUE", "Earning", daily ? decimal.Parse(value, CultureInfo.InvariantCulture) * 30 : decimal.Parse(value, CultureInfo.InvariantCulture),
            actual.FullPeriod, actual.Earned, raw, prorated) with { DailyRate = daily ? decimal.Parse(value, CultureInfo.InvariantCulture) : null };
        f.Observation.Result.Lines = [line];
        f.Observation.Result.Gross = f.Observation.Result.Net = actual.Earned;
        f.Observation.Result.Deductions = f.Observation.Result.Benefits = 0;
        f.Observation.Result.Inputs[1] = f.Observation.Result.Inputs[1] with { Value = payable };
        f.Observation.Result.Inputs[2] = f.Observation.Result.Inputs[2] with { Value = payable };
        f = f with { Saved = f.Saved with { Rows = [f.Saved.Rows[0] with { PayableDays = payable, PresentDays = payable, MonthlyGross = actual.FullPeriod }] } };
        Assert.Single(Capture(RefreshNative(f)).Rows[0].Lines);
    }

    [Fact]
    public void Actual_daily_rate_basis_can_have_zero_annual_CTC_without_becoming_missing_or_what_if()
    {
        var f = Build(); var result = f.Observation.Result;
        result.Lines = [Line("DAILY", "Earning", 9000, 9000, 4800, 4800) with { DailyRate = 300 }];
        result.Gross = result.Net = 4800; result.Deductions = result.Benefits = 0;
        result.Inputs[3] = result.Inputs[3] with { Value = 0 };
        f = f with { Saved = f.Saved with { Rows = [f.Saved.Rows[0] with { AnnualCtc = 0, MonthlyGross = 9000 }] } };
        var receipt = Capture(RefreshNative(f));
        Assert.Equal("DailyRate", Assert.Single(receipt.Rows[0].Lines).AccountingPath);
        Assert.NotNull(NativePayslipCurrencyStage.Verify(f.Profile, RefreshNative(f).Saved, receipt, receipt.PayloadHash));
    }
}
