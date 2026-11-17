using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Xunit;

namespace Payroll.API.Tests.Services;

public sealed partial class ReportedPeriodInputV2Tests
{
    [Fact]
    public void FractionsCloseEveryCashFormulaWithoutClaimingPresenceOrLookupVerification()
    {
        var f = Fixture.Create(); var bound = f.Bind(); var proof = f.Close(bound);
        Assert.Equal(28.5m, bound.PayableDays); Assert.Equal(30, bound.PayrollDivisor);
        Assert.Equal(2, proof.DeclaredDayBoundaries); Assert.Equal(14, proof.Cells.Count);
        Assert.Equal("AB5", proof.SharedDivisorCell);
        Assert.All(proof.Cells, c => Assert.InRange(Math.Abs(c.Difference), 0, 0.000001m));
        Assert.False(bound.ActualPresenceKnown); Assert.False(bound.ExternalLookupsVerified);
        Assert.False(bound.DependencyCompletenessClaimed); Assert.False(proof.NativePayrollParityCertified);
        Assert.Equal("ReferenceOnly", f.Source.DependencyReviewStatus);
        var wage = proof.Cells.Single(c => c.ColumnIndex == 28);
        Assert.Contains("PAYROLL_DAYS", wage.NativeExpression); Assert.Contains("DAYS_NEW_RATE", wage.NativeExpression);
        Assert.DoesNotContain("VLOOKUP", wage.NativeExpression);
        Assert.Equal("Z8-AA8", bound.Cells.Single(c => c.Key == "DAYS_NEW_RATE").OriginalFormula);
        Assert.Contains("VLOOKUP", f.Source.Rows.Single(r => r.SourceRow == 8).Cells[26]!.FormulaText!);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(5, 25)] [InlineData(1.5, 23.5)]
    public void ReusableRuleRecalculatesForIndependentPeriodQuantities(decimal oldDays, decimal newDays)
    {
        var f = Fixture.Create(oldDays, newDays); var b = f.Bind(); var p = f.Close(b);
        Assert.Equal(oldDays + newDays, b.PayableDays);
        Assert.Equal(23868m / 30m * oldDays, p.Cells.Single(c => c.ColumnIndex == 16).Calculated);
        Assert.Equal(24258m / 30m * newDays, p.Cells.Single(c => c.ColumnIndex == 28).Calculated);
    }

    [Fact]
    public void SameCombinedDaysDoNotEraseTheRateSplit()
    {
        var a = Fixture.Create(0, 24); var b = Fixture.Create(1, 23);
        var aw = a.Close(a.Bind()).Cells.Where(c => c.ColumnIndex is 16 or 28).Sum(c => c.Calculated);
        var bw = b.Close(b.Bind()).Cells.Where(c => c.ColumnIndex is 16 or 28).Sum(c => c.Calculated);
        Assert.Equal(13m, aw - bw); Assert.Equal(a.Bind().PayableDays, b.Bind().PayableDays);
    }

    [Fact]
    public void IndependentAmountRetainsThirteenDecimalPlacesInV2Only()
    {
        var f = Fixture.Create(); var b = f.Bind();
        Assert.Equal(10000.1234567890123m, b.Values["BASE_OLD_ALLOWANCE"]);
        Assert.Contains("918*26", b.Cells.Single(c => c.Key == "BASE_OLD_BASIC").IndependentExpression!);
        Assert.Throws<InvalidOperationException>(() => ReviewedNamedFormulaInputs.ValidateDefinitions(f.Profile.Definitions, []));
    }

    [Fact]
    public void OriginalWhitespaceHeaderIsExplicitlyBoundAndRetainedWithoutFuzzyMatching()
    {
        var f = Fixture.Create(); f.Source.Rows.Single(r => r.SourceRow == 6).Cells[11]!.Value = " Special Allowance";
        var index = f.Review.Bindings.FindIndex(b => b.ColumnIndex == 11);
        f.Review.Bindings[index] = f.Review.Bindings[index] with { OriginalHeaderText = " Special Allowance" };
        f.RefreshHashes(); var bound = f.Bind();
        Assert.Equal(" Special Allowance", bound.Cells.Single(c => c.ColumnIndex == 11).OriginalHeaderText);
        f.Source.Rows.Single(r => r.SourceRow == 6).Cells[11]!.Value = " Special Allowance X";
        Assert.Throws<InvalidOperationException>(() => f.Close(bound));
    }

    [Fact]
    public void OptionalReviewIsAbsentFromExistingV1Serialization()
    {
        var request = new ExcelPayrollSetupRequest { BatchId = "synthetic", NamedInputReview = new() { TemplateId = "12" } };
        var json = JsonSerializer.SerializeToNode(request, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        Assert.False(json.ContainsKey("reportedPeriodInputs"));
        request.ReportedPeriodInputs = Fixture.Create().Review;
        Assert.NotNull(JsonSerializer.SerializeToNode(request, new JsonSerializerOptions(JsonSerializerDefaults.Web))!["reportedPeriodInputs"]);
    }

    [Fact]
    public void ExistingValidV1RequestStillUsesTheNormalValidatorUnchanged()
    {
        var request = ValidLegacyRequest();
        ExcelPayrollSetupRepository.ValidateRequest(request);
        Assert.Null(request.ReportedPeriodInputs);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UnintegratedReportedPayloadCannotBeSilentlyIgnoredEvenWhenFlagsAreEnabled(bool flagsEnabled)
    {
        var config = Config(flagsEnabled);
        if (flagsEnabled) ReportedPeriodInputV2Codec.RequireWriterCompatibility(config);
        var request = ValidLegacyRequest(); request.ReportedPeriodInputs = Fixture.Create().Review;
        var error = Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.ValidateRequest(request));
        Assert.Contains("not supported by this normal workflow yet", error.Message);
    }

    [Fact]
    public void ExistingRequestBytesAndHashesMatchTheIndependentFrozenV48Assembly()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ReportedPeriodV1SerializationVectors.json");
        var vectors = JsonNode.Parse(File.ReadAllText(path))!["Vectors"]!.AsArray();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Equal(3, vectors.Count);
        foreach (var vector in vectors)
        {
            var expected = vector!["Json"]!.GetValue<string>();
            var request = JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(expected, json)!;
            var actual = JsonSerializer.Serialize(request, json);
            Assert.Equal(expected, actual);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(actual))).ToLowerInvariant();
            Assert.Equal(vector["Sha256"]!.GetValue<string>(), hash);
        }
    }

    [Theory]
    [InlineData("earning")] [InlineData("deduction")] [InlineData("employer")] [InlineData("grossTotal")] [InlineData("netPay")] [InlineData("employeeCode")]
    public void OriginalFinancialAndIdentityRolesCannotBeOverriddenIntoDays(string kind)
    {
        var f = Fixture.Create(); f.Source.Columns[15].Kind = kind; f.RefreshHashes();
        Assert.Throws<InvalidOperationException>(() => f.Bind());
    }

    [Theory]
    [InlineData("UAN")] [InlineData("Aadhaar Number")] [InlineData("Contact Number")] [InlineData("Bank Account")]
    public void IdentifierHeadersCannotBeDisguisedAsReportedInputs(string header)
    {
        var f = Fixture.Create(); f.Source.Columns[15].SourceHeader = header; f.Source.Columns[15].Label = header;
        f.Source.Rows.Single(r => r.SourceRow == 6).Cells[15]!.Value = header;
        f.Review.Bindings[0] = f.Review.Bindings[0] with { SourceHeader = header }; f.RefreshHashes();
        Assert.Throws<InvalidOperationException>(() => f.Bind());
    }

    [Fact]
    public void SameColumnDivisorIsNeverTheEmployeeQuantityCell()
    {
        var f = Fixture.Create(); f.Review.SharedPayrollDivisorCell = "AB8";
        Assert.Throws<InvalidOperationException>(() => f.Bind());
    }

    [Fact]
    public void AnotherEmployeesLiteralIsNotASharedHeaderDivisor()
    {
        var f = Fixture.Create(); f.Batch.Rows.Add(new() { Id = "synthetic-other", EmployeeCode = "SYNTHETIC002", SourceRow = 9 });
        var other = new ExcelPayslipCalculationSourceRow { SourceRow = 9, Cells = Enumerable.Range(0, 40).Select(_ => (ExcelPayslipCalculationCell?)null).ToList() };
        other.Cells[27] = new() { DataType = "Number", Value = "30" }; f.Source.Rows.Add(other);
        f.Review.SharedPayrollDivisorCell = "AB9"; f.RefreshHashes();
        Assert.Throws<InvalidOperationException>(() => f.Bind());
    }

    [Theory]
    [InlineData("Total Earnings")] [InlineData("Total Deductions")] [InlineData("Invoice")]
    public void LiteralFinancialControlsDoNotBecomeIndependentMonetaryInputs(string header)
    {
        var f = Fixture.Create(); f.Source.Columns[11].SourceHeader = header; f.Source.Columns[11].Label = header;
        f.Source.Rows.Single(r => r.SourceRow == 6).Cells[11]!.Value = header;
        var index = f.Review.Bindings.FindIndex(b => b.ColumnIndex == 11); f.Review.Bindings[index] = f.Review.Bindings[index] with { SourceHeader = header };
        f.RefreshHashes(); Assert.Throws<InvalidOperationException>(() => f.Bind());
    }

    [Fact]
    public void ExplicitIndependentMonthlyGrossLiteralRetainsItsExistingMeaning()
    {
        var f = Fixture.Create(); const string header = "Monthly Gross";
        f.Source.Columns[11].SourceHeader = header; f.Source.Columns[11].Label = header; f.Source.Rows.Single(r => r.SourceRow == 6).Cells[11]!.Value = header;
        var index = f.Review.Bindings.FindIndex(b => b.ColumnIndex == 11); f.Review.Bindings[index] = f.Review.Bindings[index] with { SourceHeader = header };
        f.RefreshHashes(); Assert.Equal(10000.1234567890123m, f.Bind().Values["BASE_OLD_ALLOWANCE"]);
    }

    [Theory]
    [InlineData(-0.5, 24)] [InlineData(5.5, 24)] [InlineData(5, 25.5)]
    public void NegativeOverCapacityAndOverAggregateDaysReject(decimal oldDays, decimal newDays)
    {
        var f = Fixture.Create(oldDays, newDays);
        Assert.Throws<InvalidOperationException>(() => f.Bind());
    }

    [Fact]
    public void ChangedFormulaWithSameSavedQuantityInvalidatesTheExactCellReview()
    {
        var f = Fixture.Create(); f.Source.Rows.Single(r => r.SourceRow == 8).Cells[15]!.FormulaText = "5-0.5";
        f.RefreshHashes(refreshCellHashes: false);
        Assert.Throws<InvalidOperationException>(() => f.Bind());
    }

    [Fact]
    public void DifferentSourceIdentityCannotReuseAReview()
    {
        var f = Fixture.Create(); f.Review.Rows[0].SourceIdentityHash = new string('f', 64);
        Assert.Throws<InvalidOperationException>(() => f.Bind());
    }

    [Theory]
    [InlineData("Client")] [InlineData("Month")] [InlineData("Batch")] [InlineData("RawSource")]
    public void ForeignScopeRejects(string field)
    {
        var f = Fixture.Create();
        switch (field) { case "Client": f.Review.ClientId++; break; case "Month": f.Review.PayPeriod = "2026-09"; break; case "Batch": f.Review.BatchId = "other"; break; default: f.Review.RawSourceHash = new string('e', 64); break; }
        Assert.Throws<InvalidOperationException>(() => f.Bind());
    }

    [Theory]
    [InlineData("'Other'!A1")] [InlineData("L9")]
    public void RemainingSheetOrCrossRowCashDependencyRejects(string leaf)
    {
        var f = Fixture.Create(); f.Source.Rows.Single(r => r.SourceRow == 8).Cells[29]!.FormulaText = leaf;
        f.RefreshHashes(); var bound = f.Bind();
        Assert.Throws<InvalidOperationException>(() => f.Close(bound));
    }

    [Fact]
    public void IndependentMoneyCannotBeAFormulaCacheFromADayOrEarning()
    {
        var f = Fixture.Create(); f.Source.Rows.Single(r => r.SourceRow == 8).Cells[11]!.FormulaText = "Q8";
        f.RefreshHashes(); Assert.Throws<InvalidOperationException>(() => f.Bind());
    }

    [Fact]
    public void MissingSubtotalFormulaCannotBecomeAnUnboundInfoCashCache()
    {
        var f = Fixture.Create(); f.Source.Rows.Single(r => r.SourceRow == 8).Cells[19]!.FormulaText = null;
        f.RefreshHashes(); var bound = f.Bind();
        Assert.Throws<InvalidOperationException>(() => f.Close(bound));
    }

    [Fact]
    public void MissingDayCacheDoesNotBecomeZero()
    {
        var f = Fixture.Create(); f.Source.Rows.Single(r => r.SourceRow == 8).Cells[15]!.MissingCachedValue = true;
        f.RefreshHashes(); Assert.Throws<InvalidOperationException>(() => f.Bind());
    }

    [Fact]
    public void PartialGrossAndLiteralArrearsRemainVisibleIntegrationGates()
    {
        var f = Fixture.Create(5, 25, arrears: 543); var p = f.Close(f.Bind());
        Assert.Equal(543, p.Cells.Single(c => c.ColumnIndex == 36).Calculated);
        Assert.Equal(543, p.Cells.Where(c => c.Kind == "earning").Sum(c => c.Calculated) - p.Cells.Single(c => c.Kind == "grossTotal").Calculated);
        Assert.Contains(p.RequiredIntegrationGates, x => x.Contains("Partial gross"));
        Assert.Contains(p.RequiredIntegrationGates, x => x.Contains("approved transaction"));
        Assert.False(p.NativePayrollParityCertified);
    }

    [Fact]
    public void SignedSourceEarningIsProvedAndHeldRatherThanClampedOrRecategorized()
    {
        var f = Fixture.Create(5, 25, newAllowance: 0, newOffset: 1200); var p = f.Close(f.Bind());
        Assert.Equal(-1200, p.Cells.Single(c => c.ColumnIndex == 29).Calculated);
        Assert.Contains(p.RequiredIntegrationGates, x => x.Contains("Signed negative"));
    }

    [Fact]
    public void WritersDefaultOffAndV2CryptoPurposeCannotBeReadAsV1()
    {
        var f = Fixture.Create(); var off = Config(false); var on = Config(true);
        Assert.Throws<InvalidOperationException>(() => ReportedPeriodInputV2Codec.ProtectProfile(f.Profile, off));
        var envelope = ReportedPeriodInputV2Codec.ProtectProfile(f.Profile, on);
        Assert.Equal(f.Profile.DeclarationHash, ReportedPeriodInputV2Codec.ReadProfile(envelope, off, 7, "12").DeclarationHash);
        Assert.Throws<InvalidOperationException>(() => NamedFormulaInputStore.Read<ReportedPeriodInputProfileV2>(envelope, on, "Template"));
        Assert.Throws<InvalidOperationException>(() => ReportedPeriodInputV2Codec.ReadProfile(envelope, on, 8, "12"));
    }

    [Fact]
    public void ManifestRejectsForeignOwnerAndMutatedBoundValuesWithoutFallback()
    {
        var f = Fixture.Create(); var bound = f.Bind(); var hash = new string('d', 64);
        var manifest = new ReportedPeriodInputManifestV2(2, 7, 101, "2026-10", 201, "12", hash, hash, new string('a', 32), 3, DateTime.UtcNow, bound, "");
        manifest = manifest with { ManifestHash = ReportedPeriodInputV2Codec.ManifestHash(manifest) };
        var config = Config(true); var envelope = ReportedPeriodInputV2Codec.ProtectManifest(manifest, f.Profile, config);
        var scope = new ReportedPeriodInputV2Codec.ManifestScope(7, 101, "2026-10", 201, "12", bound.SourceHash, bound.RowId, bound.SourceIdentityHash, hash, hash, bound.RawBatchHash, bound.RawSourceHash, bound.PrimaryWorkbookSha256, bound.DeclarationHash);
        Assert.Equal(101, ReportedPeriodInputV2Codec.ReadManifest(envelope, Config(false), scope, f.Profile).EmployeeId);
        Assert.Throws<InvalidOperationException>(() => ReportedPeriodInputV2Codec.ReadManifest(envelope, config, scope with { EmployeeId = 102 }, f.Profile));
        Assert.Throws<InvalidOperationException>(() => ReportedPeriodInputV2Codec.ReadManifest(envelope, config, scope with { RawSourceHash = new string('e',64) }, f.Profile));
        bound.Values["DAYS_OLD_RATE"] = 1;
        Assert.Throws<InvalidOperationException>(() => ReportedPeriodInputV2Codec.ProtectManifest(manifest with { ManifestHash = ReportedPeriodInputV2Codec.ManifestHash(manifest) }, f.Profile, config));
    }

    [Fact]
    public void EvenSelfHashedPayableOverrideCannotDisagreeWithTheDeclaredDaysSum()
    {
        var f = Fixture.Create(); var bound = f.Bind() with { PayableDays = 30, ProofHash = "" };
        bound = bound with { ProofHash = ReportedPeriodInputProof.BoundHash(bound) };
        Assert.Throws<InvalidOperationException>(() => ReportedPeriodInputProof.ValidateBound(bound));
    }

    private static IConfiguration Config(bool enabled) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["IntegrationCredentialEncryption:MasterKey"] = "synthetic-reported-period-test-key",
        ["Payroll:HistoricalNamedInputsEnabled"] = enabled.ToString(), [ReportedPeriodInputProof.WriterFlag] = enabled.ToString()
    }).Build();

    private static ExcelPayrollSetupRequest ValidLegacyRequest() => new()
    {
        BatchId = new string('b',32), RowIds = ["synthetic-row"], TemplateName = "Synthetic salary template", EffectiveFrom = "2026-10-01",
        FixedPayrollDays = 30, PayableDaysColumnIndex = 15,
        Components = [new() { Key = "synthetic-basic", Code = "BASIC", Name = "Basic", Value = "1000", SourceColumnIndex = 9, ExpectedColumnIndex = 16 }]
    };

    internal sealed class Fixture
    {
        internal ExcelPayslipBatch Batch = new(); internal ExcelPayslipCalculationSource Source = new(); internal ExcelPayslipRow Row = new();
        internal ReportedPeriodInputProfileV2 Profile = null!; internal ReportedPeriodInputReview Review = new(); internal ReportedPeriodInputProof.SourceScope Scope = null!;
        internal static Fixture Create(decimal oldDays = 4.5m, decimal newDays = 24m, decimal arrears = 0, decimal newAllowance = 10000, decimal newOffset = 0)
        {
            var f = new Fixture(); var sha = new string('a', 64);
            f.Row = new() { Id = "synthetic-row", SourceRow = 8, EmployeeCode = "SYNTHETIC001", EmployeeName = "Synthetic Person" };
            f.Batch = new() { Id = new string('b', 32), ClientId = 7, Month = "2026-10", Rows = [f.Row] };
            f.Source = new() { CaptureVersion = 1, DependencyReviewStatus = "ReferenceOnly", SourceBatchId = f.Batch.Id, WorkbookKey = "wb_" + sha,
                WorkbookFiles = [new() { WorkbookKey = "wb_" + sha, Sha256 = sha, OriginalId = new string('a', 32), FileName = "synthetic.xlsx" }], ColumnCount = 40 };
            for (var i = 0; i < 40; i++) f.Source.Columns.Add(new() { ColumnIndex = i, SourceHeader = "Independent Amount " + i, Label = "Independent Amount " + i, Kind = "info" });
            void Column(int index, string header, string kind = "info") { f.Source.Columns[index].SourceHeader = header; f.Source.Columns[index].Label = header; f.Source.Columns[index].Kind = kind; }
            Column(0, "Employee Code", "employeeCode"); Column(1, "Employee Name", "employeeName"); Column(9, "Old Basic"); Column(11, "Special Allowance"); Column(20, "New VDA"); Column(23, "Special Allowance");
            foreach (var index in new[] {13,25}) Column(index, "Total Working Days"); foreach (var index in new[] {14,26}) Column(index, "LWP"); foreach (var index in new[] {15,27}) Column(index, "Net Working Days");
            foreach (var index in new[] {16,17,18,28,29,30,36}) Column(index, "Cash earning " + index, "earning");
            foreach (var index in new[] {33,34,35}) Column(index, "Cash deduction " + index, "deduction"); foreach (var index in new[] {38,39}) Column(index, "Employer cost " + index, "employer");
            Column(19, "Subtotal", "ignore"); Column(31, "Subtotal", "ignore"); Column(32, "Total Earnings", "grossTotal"); Column(37, "Net Pay", "netPay"); f.Source.Columns[36].BlankAsZero = true;
            var head = new ExcelPayslipCalculationSourceRow { SourceRow = 6, Cells = f.Source.Columns.Select(c => (ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell { DataType = "Text", Value = c.SourceHeader }).ToList() };
            var data = new ExcelPayslipCalculationSourceRow { SourceRow = 8, Cells = Enumerable.Range(0, 40).Select(_ => (ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell { DataType = "Number", Value = "0" }).ToList() };
            var shared = new ExcelPayslipCalculationSourceRow { SourceRow = 5, Cells = Enumerable.Range(0, 40).Select(_ => (ExcelPayslipCalculationCell?)null).ToList() }; shared.Cells[27] = new() { DataType = "Number", Value = "30" };
            void Value(int index, decimal value, string? formula = null) { data.Cells[index] = new() { DataType = "Number", Value = value.ToString("G29", CultureInfo.InvariantCulture), FormulaText = formula }; }
            Value(9, 23868, "918*26"); Value(10, 0); Value(11, 10000.1234567890123m); Value(13, 5); Value(14, 5 - oldDays, "VLOOKUP(B8,'[1]Unprovided'!B7:N181,12,0)"); Value(15, oldDays, "N8-O8");
            Value(20, 933); Value(21, 24258, "U8*26"); Value(22, newOffset); Value(23, newAllowance); Value(24, 208); Value(25, 25); Value(26, 25 - newDays, "VLOOKUP(B8,'[1]Unprovided'!B7:AP181,40)"); Value(27, newDays, "Z8-AA8");
            var oldWage = 23868m / 30m * oldDays; var oldAllowance = 10000.1234567890123m / 30m * oldDays; var oldBonus = oldWage * 0.0833m;
            var newWage = 24258m / 30m * newDays; var newEarnedAllowance = newAllowance / 30m * newDays - newOffset; var newBonus = newWage * 0.0833m;
            Value(16, oldWage, "J8/$AB$5*P8"); Value(17, oldAllowance, "L8/$AB$5*P8-K8"); Value(18, oldBonus, "Q8*8.33%"); Value(19, oldWage + oldAllowance + oldBonus, "Q8+R8+S8");
            Value(28, newWage, "V8/$AB$5*AB8"); Value(29, newEarnedAllowance, "X8/$AB$5*AB8-W8"); Value(30, newBonus, "AC8*8.33%"); Value(31, newWage + newEarnedAllowance + newBonus, "AC8+AD8+AE8");
            var gross = oldWage + oldAllowance + oldBonus + newWage + newEarnedAllowance + newBonus; var pf = Math.Min(newWage, 25000) * 0.12m; var pt = newDays > 0 ? 208 : 0;
            Value(32, gross, "T8+AF8"); Value(33, pf, "IF(AC8<25000,AC8*12%,25000*12%)"); Value(34, 0, "IF((J8+X8)<21000,AC8*0.75%,0)"); Value(35, pt, "IF(AB8>0,Y8,0)"); Value(36, arrears); Value(37, gross - pf - pt + arrears, "AG8-AH8-AI8-AJ8+AK8"); Value(38, Math.Min(newWage, 25000) * 0.13m, "IF(AC8<25000,AC8*13%,25000*13%)"); Value(39, 0, "IF((J8+X8)<21000,AC8*3.25%,0)");
            f.Source.Rows = [shared, head, data];
            var specs = new[] { ("DAYS_OLD_RATE",15,"Days","ReportedDays",(int?)13), ("DAYS_NEW_RATE",27,"Days","ReportedDays",(int?)25),
                ("BASE_OLD_BASIC",9,"Amount","IndependentAmount",(int?)null), ("BASE_OLD_ALLOWANCE",11,"Amount","IndependentAmount",(int?)null), ("BASE_NEW_RATE",20,"Amount","IndependentAmount",(int?)null), ("BASE_NEW_ALLOWANCE",23,"Amount","IndependentAmount",(int?)null), ("OFFSET_OLD",10,"Amount","IndependentAmount",(int?)null), ("OFFSET_NEW",22,"Amount","IndependentAmount",(int?)null), ("PT_REFERENCE",24,"Amount","IndependentAmount",(int?)null) };
            var defs = specs.Select(x => new NamedFormulaInputDefinition(x.Item1, x.Item1, x.Item3, x.Item3 == "Days" ? 2 : 18, 0, x.Item3 == "Days" ? 31 : 1_000_000_000)).ToList();
            f.Profile = new(2, 7, "12", new string('c', 64), defs, ["DAYS_OLD_RATE", "DAYS_NEW_RATE"], ""); f.Profile = f.Profile with { DeclarationHash = ReportedPeriodInputProof.DeclarationHash(f.Profile) };
            f.Review = new() { Reviewed = true, ClientId = 7, BatchId = f.Batch.Id, PayPeriod = "2026-10", RawBatchHash = sha, RawSourceHash = sha, PrimaryWorkbookSha256 = sha, CycleStart = "2026-09-26", CycleEnd = "2026-10-25", SharedPayrollDivisorCell = "AB5", TotalPayableKeys = ["DAYS_OLD_RATE", "DAYS_NEW_RATE"], Bindings = specs.Select(x => new ReportedPeriodInputBinding(x.Item1, x.Item2, 6, f.Source.Columns[x.Item2].SourceHeader, x.Item4, x.Item5)).ToList(), Rows = [new() { RowId = f.Row.Id, SourceRow = 8, SourceIdentityHash = new string('d',64) }] };
            f.RefreshHashes(); return f;
        }
        internal void RefreshHashes(bool refreshCellHashes = true)
        {
            var hash = ReportedPeriodInputProof.SourceHash(Batch, Source);
            Review.SourceHash = hash; Scope = new(7, Batch.Id, "2026-10", hash, Review.RawBatchHash, Review.RawSourceHash, Review.PrimaryWorkbookSha256, Review.CycleStart, Review.CycleEnd, new Dictionary<string, string> { [Row.Id] = new string('d',64) });
            if (refreshCellHashes) foreach (var binding in Review.Bindings) { var cell = Source.Rows.Single(r => r.SourceRow == 8).Cells[binding.ColumnIndex]!; Review.Rows[0].ExpectedValues[binding.Key] = decimal.Parse(cell.Value, CultureInfo.InvariantCulture); Review.Rows[0].CellHashes[binding.Key] = ReportedPeriodInputProof.CellHash(cell); }
        }
        internal ReportedPeriodBoundRowV2 Bind() => ReportedPeriodInputProof.Bind(Profile, Review, Batch, Source, Row, Scope);
        internal ReportedPeriodCashClosureV2 Close(ReportedPeriodBoundRowV2 b) => ReportedPeriodInputProof.CloseFinancialGraph(Review, Batch, Source, Row, b);
    }
}
