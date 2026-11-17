using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using NamedRecurringSourceBoundary = Payroll.API.Repositories.ExcelPayrollSetupRepository.NamedRecurringSourceBoundary;

namespace Payroll.API.Tests.Services;

public sealed class SourceNamedInputTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static List<NamedFormulaInputDefinition> HourDefinitions =>
        [new("OT_HOURS", "Reviewed overtime hours", "Hours", 2, 0, 744)];

    private static NamedFormulaInputContext Hours(decimal value) => new()
    {
        Definitions = HourDefinitions,
        Values = new() { ["OT_HOURS"] = value },
        DeclarationHash = new string('a', 64)
    };

    [Fact]
    public void Omitted_and_explicit_null_review_preserve_the_frozen_v45_request_bytes_and_hash()
    {
        // Captured by read-only reflection from immutable v45 d85a9a2b…;
        // this test has no runtime dependency on a local application binary.
        const string legacyHash = "43fc1100dd9779199857a16fe845bbca7592fc3f341c631b7f9c475cddb6c30c";
        var request = new ExcelPayrollSetupRequest();
        var omitted = JsonSerializer.Serialize(request, Json);
        Assert.DoesNotContain("namedInputReview", omitted);
        Assert.Equal(legacyHash, Sha256(omitted));
        var explicitNull = JsonNode.Parse(omitted)!.AsObject();
        explicitNull["namedInputReview"] = null;
        var restored = JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(explicitNull.ToJsonString(), Json)!;
        Assert.Null(restored.NamedInputReview);
        Assert.Equal(omitted, JsonSerializer.Serialize(restored, Json));
        Assert.Equal(legacyHash, Sha256(JsonSerializer.Serialize(restored, Json)));
        ExcelPayrollSetupRepository.ValidateSourceSetupNamedInputReview(request);
    }

    [Fact]
    public void Existing_amount_and_percent_declarations_keep_their_frozen_v45_hash()
    {
        List<NamedFormulaInputDefinition> definitions =
        [
            new("SOURCE_BASE", "Independent source amount", "Amount", 4, 0, 1_000_000_000m),
            new("SOURCE_RATE_PCT", "Independent source percent", "PercentPoints", 4, 0, 100)
        ];
        ReviewedNamedFormulaInputs.ValidateDefinitions(definitions, []);
        Assert.Equal("a30f05e5be09b72993585e29d6e55e7f69d39b9276a909d2a2429b87758cc471",
            ReviewedNamedFormulaInputs.DeclarationHash(42, "700", new string('a', 64), definitions));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.5")]
    [InlineData("7.25")]
    [InlineData("744")]
    public void Explicit_hours_allow_zero_and_fractional_values_without_rounding_or_money_aliases(string text)
    {
        var value = decimal.Parse(text, CultureInfo.InvariantCulture);
        var fixture = Create(hours: value);
        var bound = ReviewedNamedFormulaInputs.Bind(fixture.Profile, fixture.Request.NamedInputReview!.Bindings,
            fixture.Plan.Source, fixture.Plan.Batch.Rows[0], ["BASIC", "HRA", "OT"]);
        ReviewedNamedFormulaInputs.ValidateContext(bound.Context, ["BASIC", "HRA", "OT"]);
        Assert.Equal(value, bound.Context.Values["OT_HOURS"]);
        Assert.Equal("Hours", Assert.Single(bound.Context.Definitions).Unit);
        Assert.Equal("FormulaOperandOnly", Assert.Single(bound.Context.Definitions).Purpose);
        Assert.Single(bound.Context.Values);
        Assert.False(bound.Context.Values.ContainsKey("MONTHLY_CTC"));
        Assert.False(bound.Context.Values.ContainsKey("OT_MONTHLY"));
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("744.01")]
    [InlineData("0.001")]
    public void Hours_outside_the_reviewed_range_or_precision_are_rejected_without_coercion(string text)
    {
        var value = decimal.Parse(text, CultureInfo.InvariantCulture);
        var context = Hours(value);
        Assert.Throws<InvalidOperationException>(() => ReviewedNamedFormulaInputs.ValidateContext(context, []));
        Assert.Equal(value, context.Values["OT_HOURS"]);
    }

    [Theory]
    [InlineData("no allocations")]
    [InlineData("another template")]
    [InlineData("bad declaration")]
    [InlineData("duplicate binding")]
    [InlineData("unreviewed header")]
    public void Source_review_requires_the_exact_location_template_declaration_and_complete_bindings(string mutation)
    {
        var request = Create().Request;
        if (mutation == "no allocations") request.LocationAllocations = null;
        if (mutation == "another template") request.LocationAllocations!.Groups[0].Parts[0].TemplateId = "701";
        if (mutation == "bad declaration") request.NamedInputReview!.DeclarationHash = "not-a-source-declaration";
        if (mutation == "duplicate binding") request.NamedInputReview!.Bindings.Add(request.NamedInputReview.Bindings[0]);
        if (mutation == "unreviewed header") request.NamedInputReview!.Bindings[0] = request.NamedInputReview.Bindings[0] with { HeaderRow = 0 };
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.ValidateSourceSetupNamedInputReview(request));
    }

    [Theory]
    [InlineData("formula cache")]
    [InlineData("missing value")]
    [InlineData("earned output")]
    [InlineData("different header")]
    public void Overtime_hours_remain_an_independent_verified_literal(string mutation)
    {
        var f = Create();
        if (mutation == "formula cache") f.Plan.Source.Rows[1].Cells[5]!.FormulaText = "=1/2";
        if (mutation == "missing value") f.Plan.Source.Rows[1].Cells[5]!.Value = "";
        if (mutation == "earned output") f.Plan.Source.Columns[5].Kind = "earning";
        if (mutation == "different header") f.Request.NamedInputReview!.Bindings[0] = new("OT_HOURS", 5, 1, "Unreviewed quantity");
        Assert.Throws<InvalidOperationException>(() => ReviewedNamedFormulaInputs.Bind(f.Profile,
            f.Request.NamedInputReview!.Bindings, f.Plan.Source, f.Plan.Batch.Rows[0], ["BASIC", "HRA", "OT"]));
    }

    [Theory]
    [InlineData("0", 15)]
    [InlineData("0.5", 15)]
    [InlineData("2", 12)]
    public void One_rule_uses_independent_monthly_bases_and_hours_without_freezing_earned_pay(string hours, int payable)
    {
        var f = Create(hours: decimal.Parse(hours, CultureInfo.InvariantCulture), payable: payable);
        var row = f.Plan.Rows[0];
        var source = f.Plan.Batch.Rows[0];
        var original = JsonSerializer.Serialize(new { f.Plan.Source, f.Plan.Batch, f.Request }, Json);
        var proofs = row.Components.Select(c => ExcelPayrollSetupRepository.InspectNamedTransferFormula(f.Request,
            f.Plan.Source, source, c.Definition, f.NamedLeaves)).ToList();
        Assert.All(proofs, proof => Assert.Equal("Exact supported translation", proof.Status));
        Assert.Equal(3000m * payable / 30, row.Preview.Lines.Single(l => l.Code == "BASIC").CalculatedAmount);
        Assert.Equal(decimal.Round(1000m * payable / 30, 2, MidpointRounding.ToEven), row.Preview.Lines.Single(l => l.Code == "HRA").CalculatedAmount);
        Assert.Equal(250 * f.Context.Values["OT_HOURS"], row.Preview.Lines.Single(l => l.Code == "OT").CalculatedAmount);
        Assert.Equal(48_000m, row.Preview.AnnualCtc);
        Assert.Equal(new[] { "1", "2" }, row.SalaryInputs.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(3000, row.SalaryInputs["1"]);
        Assert.Equal(1000, row.SalaryInputs["2"]);
        Assert.DoesNotContain(row.SalaryInputs.Keys, key => key is "3" or "OT_HOURS");
        Assert.Equal(original, JsonSerializer.Serialize(new { f.Plan.Source, f.Plan.Batch, f.Request }, Json));
    }

    [Fact]
    public void Rounding_rebuild_uses_the_same_named_context_as_the_source_preview()
    {
        // 1000 / 11 * 6 crosses the source formatter's G15 boundary between
        // IEEE source caches and decimal payroll. The proof must rebuild core
        // arithmetic, including the otherwise-unrelated OT operand.
        var f = Create(monthlyBasic: 1000m, monthlyHra: 2200m, days: 11, payable: 6);
        var row = f.Plan.Rows[0];
        var proofs = row.Components.Select(c => ExcelPayrollSetupRepository.InspectNamedTransferFormula(f.Request,
            f.Plan.Source, f.Plan.Batch.Rows[0], c.Definition, f.NamedLeaves)).ToList();
        Assert.NotEqual(ExcelPayrollSetupRepository.SourceDisplayPrecision(row.Preview.Lines[0].SourceAmount!.Value),
            ExcelPayrollSetupRepository.SourceDisplayPrecision(row.Preview.Lines[0].UnroundedEarned!.Value));
        var before = JsonSerializer.Serialize(new { f.Plan.Source, f.Plan.Batch, row.Preview }, Json);
        var approved = ExcelPayrollSetupRepository.ClassifyTransferAmounts(f.Plan, row, f.Request, new string('b', 64), proofs, f.Context);
        Assert.Equal("Explained rounding", approved.Status);
        Assert.Equal("source-core-arithmetic-v2", approved.Version);
        Assert.Equal("Mismatch", ExcelPayrollSetupRepository.ClassifyTransferAmounts(f.Plan, row, f.Request, new string('b', 64), proofs).Status);
        Assert.Equal(before, JsonSerializer.Serialize(new { f.Plan.Source, f.Plan.Batch, row.Preview }, Json));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(12)]
    public void Reviewed_monthly_budget_stays_independent_of_bound_variable_overtime_hours(int hours)
    {
        var f = Create(hours: hours);
        var arithmetic = f.Plan.Rows[0].Preview;
        Assert.Equal(12m * (3000m + 1000m), arithmetic.AnnualCtc);
        Assert.Equal(2000m + 250m * hours, arithmetic.Gross);
        Assert.Equal(arithmetic.Gross, arithmetic.Net);
        Assert.Equal(250m * hours, arithmetic.Lines.Single(l => l.Code == "OT").CalculatedAmount);
        Assert.Equal("Fixed Pay", ExcelPayrollSetupRepository.ResolvedPayType(f.Request.Components[0]));
        Assert.Equal("Fixed Pay", ExcelPayrollSetupRepository.ResolvedPayType(f.Request.Components[1]));
    }

    [Theory]
    [InlineData("unreviewed")]
    [InlineData("fixed-formula")]
    [InlineData("unbound-variable")]
    public void Named_period_formula_cannot_replace_an_independently_reviewed_monthly_budget(string mutation)
    {
        var f = Create();
        if (mutation == "unreviewed") f.Request.ReviewedFixedInputs = false;
        if (mutation == "fixed-formula") f.Request.Components[2].PayType = "Fixed Pay";
        if (mutation == "unbound-variable") f.Request.Components[2].Formula = "1";
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.CalculateRow(42, f.Request, f.Plan.Components,
            f.Plan.Source.Rows[1], f.Plan.Batch.Rows[0], calculationSource: f.Plan.Source, namedInputs: f.Context));
    }

    [Fact]
    public void Source_owner_accepts_only_the_original_unique_current_client_employee()
    {
        var f = Create();
        var identity = new ExcelPayrollSetupRepository.Identity
        { Id = 71, ClientId = 42, EmployeeCode = "SOURCE-71", Name = "Source Example", IsActive = true, EligibleForMonth = true, Aadhaar = "234567890123" };
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.RequireNamedSourceSetupOwner(f.Plan.Batch, f.Plan.Source, f.Plan.Batch.Rows[0], 71, [identity], null));
        f.Plan.Source.Columns.Add(new() { ColumnIndex = 10, SourceHeader = "Aadhaar", Label = "Aadhaar", Kind = "info" });
        f.Plan.Source.Rows[0].Cells.Add(new() { Value = "Aadhaar", DataType = "Text" });
        f.Plan.Source.Rows[1].Cells.Add(new() { Value = "2345  6789 0123", DataType = "Text" });
        f.Plan.Source.ColumnCount++;
        ExcelPayrollSetupRepository.RequireNamedSourceSetupOwner(f.Plan.Batch, f.Plan.Source, f.Plan.Batch.Rows[0], 71, [identity], null);
        var before = JsonSerializer.Serialize(new { f.Plan.Batch, f.Plan.Source }, Json);
        var originalLinks = f.Plan.Batch.ProfileLinks;
        identity.ClientId = 43;
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.RequireNamedSourceSetupOwner(f.Plan.Batch, f.Plan.Source, f.Plan.Batch.Rows[0], 71, [identity], null));
        identity.ClientId = 42;
        identity.EligibleForMonth = false;
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.RequireNamedSourceSetupOwner(f.Plan.Batch, f.Plan.Source, f.Plan.Batch.Rows[0], 71, [identity], null));
        identity.EligibleForMonth = true;
        foreach (var foreignClient in new[] { 42, 43 })
            Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.RequireNamedSourceSetupOwner(f.Plan.Batch, f.Plan.Source, f.Plan.Batch.Rows[0], 71,
                [identity, new() { Id = 72, ClientId = foreignClient, EmployeeCode = "OTHER", Name = "Other Person", Aadhaar = identity.Aadhaar, EligibleForMonth = true }], null));
        foreach (var mutation in new[] { "formula", "exponent", "error" })
        {
            var cell = f.Plan.Source.Rows[1].Cells[10]!;
            if (mutation == "formula") cell.FormulaText = "=234567890123";
            if (mutation == "exponent") cell.Value = "2.34567890123E11";
            if (mutation == "error") cell.Error = "#VALUE!";
            Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.RequireNamedSourceSetupOwner(f.Plan.Batch, f.Plan.Source, f.Plan.Batch.Rows[0], 71, [identity], null));
            cell.FormulaText = null; cell.Error = null; cell.Value = "2345  6789 0123";
        }
        f.Plan.Batch.ProfileLinks = [new(f.Plan.Batch.Rows[0].Id, 3, 72,
            ExcelPayslipMasterSynchronization.ProfileIdentityHash(f.Plan.Batch, f.Plan.Batch.Rows[0], f.Plan.Source))];
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.RequireNamedSourceSetupOwner(f.Plan.Batch, f.Plan.Source, f.Plan.Batch.Rows[0], 71, [identity], null));
        f.Plan.Batch.ProfileLinks = originalLinks;
        Assert.Equal(before, JsonSerializer.Serialize(new { f.Plan.Batch, f.Plan.Source }, Json));
    }

    private static Fixture Create(decimal hours = .5m, decimal monthlyBasic = 3000m, decimal monthlyHra = 1000m,
        int days = 30, int payable = 15)
    {
        static string N(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
        var basic = (double)monthlyBasic / days * payable;
        var hra = (double)monthlyHra / days * payable;
        var ot = 250d * (double)hours;
        string[] values = [days.ToString(CultureInfo.InvariantCulture), payable.ToString(CultureInfo.InvariantCulture),
            payable.ToString(CultureInfo.InvariantCulture), monthlyBasic.ToString(CultureInfo.InvariantCulture),
            monthlyHra.ToString(CultureInfo.InvariantCulture), hours.ToString(CultureInfo.InvariantCulture), N(basic), N(hra), N(ot), N(basic + hra + ot)];
        string[] headers = ["Salary divisor", "Payable units", "Worked units", "Monthly basic", "Monthly HRA", "Overtime hours", "Basic earned", "HRA earned", "Overtime earning", "Net pay"];
        string?[] expressions = [null, null, null, null, null, null, "D3/A3*B3", "E3/A3*B3", "250*F3", "SUM(G3:I3)"];
        var source = new ExcelPayslipCalculationSource
        {
            HeaderRow = 1, ColumnCount = 10, SheetName = "Synthetic first source",
            Columns = Enumerable.Range(0, 10).Select(i => new ExcelPayslipCalculationColumn
            { ColumnIndex = i, SourceHeader = headers[i], Label = headers[i], Kind = i is >= 6 and <= 8 ? "earning" : i == 9 ? "netPay" : i is 3 or 4 ? "ignore" : "info" }).ToList(),
            Rows = [new() { SourceRow = 1, Cells = headers.Select(v => (ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell { Value = v, DataType = "Text" }).ToList() },
                new() { SourceRow = 3, Cells = values.Select((v, i) => (ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell { Value = v, FormulaText = expressions[i], DataType = "Number" }).ToList() }]
        };
        var saved = new ExcelPayslipRow
        {
            Id = "source", SourceRow = 3, EmployeeCode = "SOURCE-71", EmployeeName = "Source Example",
            NetPay = decimal.Parse(values[9], CultureInfo.InvariantCulture),
            Earnings = [new() { Label = "Basic earned", Amount = decimal.Parse(values[6], CultureInfo.InvariantCulture) },
                new() { Label = "HRA earned", Amount = decimal.Parse(values[7], CultureInfo.InvariantCulture) },
                new() { Label = "Overtime earning", Amount = decimal.Parse(values[8], CultureInfo.InvariantCulture) }]
        };
        var profile = new NamedFormulaInputProfile(1, 42, "700", new string('c', 64), HourDefinitions, new string('a', 64));
        var request = new ExcelPayrollSetupRequest
        {
            BatchId = new string('d', 32), RowIds = [saved.Id], TemplateName = "One reusable source rule", EffectiveFrom = "2026-10-01",
            PayrollDaysColumnIndex = 0, PayableDaysColumnIndex = 1, PresentDaysColumnIndex = 2, ReviewedFixedInputs = true,
            NamedInputReview = new() { TemplateId = profile.TemplateId, DeclarationHash = profile.DeclarationHash, Bindings = [new("OT_HOURS", 5, 1, headers[5])] },
            LocationAllocations = new() { Groups = [new() { EmployeeId = 71, Parts = [new() { RowId = saved.Id, WorkLocationId = 10, TemplateId = profile.TemplateId }] }] },
            Components = [new() { Key = "basic", Code = "BASIC", ExistingComponentId = "1", SourceColumnIndex = 3, ExpectedColumnIndex = 6, Priority = 10 },
                new() { Key = "hra", Code = "HRA", ExistingComponentId = "2", SourceColumnIndex = 4, ExpectedColumnIndex = 7, Priority = 20 },
                new() { Key = "ot", Code = "OT", ExistingComponentId = "3", CalculationType = "Formula", Formula = "250*OT_HOURS", ExpectedColumnIndex = 8, ProRata = false, Priority = 30, PayType = "Variable Pay" }]
        };
        var bound = ReviewedNamedFormulaInputs.Bind(profile, request.NamedInputReview.Bindings, source, saved, ["BASIC", "HRA", "OT"]);
        var plan = new ExcelPayrollSetupRepository.Plan { Batch = new() { Id = request.BatchId, ClientId = 42, Month = "2026-10", Rows = [saved] }, Source = source };
        plan.Components.AddRange(request.Components.Select((c, i) => new ExcelPayrollSetupRepository.ResolvedComponent(c, (i + 1).ToString(CultureInfo.InvariantCulture), false)));
        var arithmetic = ExcelPayrollSetupRepository.CalculateRow(42, request, plan.Components, source.Rows[1], saved, calculationSource: source, namedInputs: bound.Context);
        plan.Rows.Add(new() { RowId = saved.Id, SourceRow = 3, Employee = new() { Id = 71, ClientId = 42 }, TemplateId = profile.TemplateId,
            Components = plan.Components, Preview = arithmetic.Preview, SalaryInputs = arithmetic.Inputs });
        return new(plan, request, profile, bound.Context, new() { [5] = new("OT_HOURS", hours) });
    }

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private sealed record Fixture(ExcelPayrollSetupRepository.Plan Plan, ExcelPayrollSetupRequest Request,
        NamedFormulaInputProfile Profile, NamedFormulaInputContext Context, Dictionary<int, NamedRecurringSourceBoundary> NamedLeaves);
}
