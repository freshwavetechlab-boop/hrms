using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal static void ValidateSourceSetupNamedInputReview(ExcelPayrollSetupRequest request)
    {
        ValidateSourceNamedBankReview(request);
        if (request.NamedInputReview is not { } review) return;
        if (request.LocationAllocations is null || request.LocationAllocations.Groups is null
            || request.LocationAllocations.Groups.Any(g => g is null || g.Parts is null || g.Parts.Any(p => p is null))
            || !Regex.IsMatch(review.TemplateId ?? "", "\\A[0-9]{1,20}\\z")
            || !Regex.IsMatch(review.DeclarationHash ?? "", "\\A[a-f0-9]{64}\\z") || review.Bindings is null
            || review.Bindings.Count is < 1 or > 16 || review.Bindings.Any(b => b is null || b.ColumnIndex is < 0 or > 1499
                || b.HeaderRow <= 0 || string.IsNullOrWhiteSpace(b.SourceHeader))
            || review.Bindings.Select(b => b.Key).Distinct(StringComparer.Ordinal).Count() != review.Bindings.Count
            || request.LocationAllocations.Groups.SelectMany(g => g.Parts).Any(p => p.TemplateId != review.TemplateId))
            throw new InvalidOperationException("Review one exact managed named-input template and complete source bindings for the existing employee location allocations.");
    }

    private async Task<NamedFormulaInputProfile?> ReadSourceSetupNamedProfileAsync(MySqlConnection db, MySqlTransaction tx,
        int clientId, ExcelPayrollSetupRequest request, ExcelPayslipCalculationSource source, string setupJson)
    {
        ValidateSourceSetupNamedInputReview(request);
        var templates = request.LocationAllocations!.Groups.SelectMany(g => g.Parts).Select(p => p.TemplateId).Distinct(StringComparer.Ordinal).ToArray();
        var profiles = await NamedFormulaInputStore.ReadProfilesAsync(db, tx, configuration, clientId, templates);
        if (profiles.Count == 0)
        {
            if (request.NamedInputReview is not null) throw new InvalidOperationException("The exact protected source named-input declaration is unavailable.");
            return null;
        }
        // Inspect the actual managed template as well as the request: omission
        // of the optional review cannot bypass compatible-reader rollout.
        if (!configuration.GetValue<bool>(HistoricalNamedInputsFlag))
            throw new InvalidOperationException("First-source named-input assignment writes require compatible API and payroll workers before enabling Payroll:HistoricalNamedInputsEnabled.");
        if (request.NamedInputReview is not { } review || templates.Length != 1 || profiles.Count != 1
            || !profiles.TryGetValue(review.TemplateId, out var profile) || profile.DeclarationHash != review.DeclarationHash)
            throw new InvalidOperationException("Every managed source template needs its exact declaration and explicit operand bindings; no zero or legacy fallback is used.");
        RequireReviewedNamedSourceRoles(request, source, review.Bindings);
        if (review.Bindings.Any(b => b.ColumnIndex == request.MonthlyCtcColumnIndex || b.ColumnIndex == request.PayrollDaysColumnIndex
            || b.ColumnIndex == request.PayableDaysColumnIndex || b.ColumnIndex == request.PresentDaysColumnIndex
            || request.Components.Any(c => c.SourceColumnIndex == b.ColumnIndex || c.ExpectedColumnIndex == b.ColumnIndex)))
            throw new InvalidOperationException("An independent operand cannot replace monthly salary inputs, financial outputs or attendance.");
        var setup = JsonNode.Parse(setupJson)!;
        var masters = setup["salaryComponents"]!.AsArray().OfType<JsonObject>().ToArray();
        var template = setup["salaryStructures"]!.AsArray().OfType<JsonObject>().SingleOrDefault(t => Text(t, "id") == profile.TemplateId
            && Text(t, "active") == "true" && Text(t, "clientId").Split([';', ',', '|']).Any(v => v.Split(':')[0].Trim() == clientId.ToString(CultureInfo.InvariantCulture)))
            ?? throw new InvalidOperationException("The exact managed source template is inactive or outside this client.");
        ReviewedNamedFormulaInputs.ValidateDefinitions(profile.Definitions, masters.SelectMany(m => new[] { Text(m, "id"), Text(m, "code") }));
        if (profile.RulesHash != LocationSettingsRulesHash(template, masters))
            throw new InvalidOperationException("The managed source template rules changed; review the declaration again.");
        return profile;
    }

    internal static void RequireNamedSourceSetupOwner(ExcelPayslipBatch batch, ExcelPayslipCalculationSource source,
        ExcelPayslipRow row, int employeeId, IReadOnlyList<Identity> identities, int? locationColumn,
        HistoricalVerifiedSourceIdentity? reviewedIdentity = null)
    {
        var info = SourceInformation(row, source, locationColumn);
        var issues = new List<ExcelPayrollSetupIssue>();
        var match = MatchIdentity(row, info, identities.Where(i => i.ClientId == batch.ClientId).ToArray(), new() { RowId = row.Id, MatchedEmployeeId = employeeId }, issues);
        if (match?.Id != employeeId || match.ClientId != batch.ClientId || match.EligibleForMonth != true || issues.Any(i => i.Severity == "Error"))
            throw new InvalidOperationException("The first-source named source row no longer has its original existing client employee.");
        if (reviewedIdentity is not null && (reviewedIdentity.Evidence.ClientId != batch.ClientId || reviewedIdentity.Evidence.EmployeeId != employeeId
            || reviewedIdentity.Evidence.RowId != row.Id || reviewedIdentity.Evidence.SourceRow != row.SourceRow
            || reviewedIdentity.Evidence.SourceHash != ExcelPayslipRepository.EmploymentSourceHash(batch, source) || reviewedIdentity.ReviewedNameChange))
            throw new InvalidOperationException("The first-source bank proof does not belong to this unchanged exact source employee.");
        var strong = reviewedIdentity is not null || HistoricalGovernmentAnchorExists(source, row, match, identities);
        var identityIssues = HistoricalIdentityIssues(batch, source, row, match, identities, reviewedIdentity);
        if (batch.ClientId <= 0 || match?.Id != employeeId || match.ClientId != batch.ClientId || match.EligibleForMonth != true
            || !strong || identityIssues.Count > 0 || issues.Any(i => i.Severity == "Error"))
            throw new InvalidOperationException("A first-source named assignment requires the same unique strong existing client employee and source-month employment proof.");
        var links = (batch.ProfileLinks ?? []).Where(l => l.RowId == row.Id).ToArray();
        if (links.Length > 0 && (links.Length != 1 || links[0].EmployeeId != employeeId || links[0].SourceRow != row.SourceRow
            || links[0].SourceIdentityHash != ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch, row, source)))
            throw new InvalidOperationException("An existing durable source link conflicts with the reviewed first-source employee.");
    }

    internal static async Task<List<Identity>> ReadNamedSourceSetupIdentitiesAsync(MySqlConnection db, MySqlTransaction tx, int client, string month)
    {
        var identities = await ReadIdentitiesAsync(db, tx, client, month);
        // Government ownership is global even when the salary operation is
        // client scoped. Other-client facts never become selectable employees.
        identities.AddRange(await db.QueryAsync<Identity>(@"SELECT e.Id,e.ClientId,COALESCE(p.PanNumber,'') Pan,COALESCE(p.AadhaarNumber,'') Aadhaar,COALESCE(p.UanNumber,'') Uan
FROM employees e LEFT JOIN employeepersonaldetails p ON p.EmployeeId=e.Id WHERE e.ClientId<>@Client", new { Client = client }, tx));
        return identities;
    }

    private static decimal NamedSourceFixedMonthlyAnnualBasis(ExcelPayrollSetupRequest request, IReadOnlyList<ResolvedComponent> components,
        decimal existingAnnual, NamedFormulaInputContext context)
    {
        if (request.NamedInputReview is not null && request.ReviewedFixedInputs && existingAnnual > 0 && existingAnnual <= 1_000_000_000m
            && components.Where(c => !c.Definition.IsPeriodAdjustment && c.Definition.Category is "Earning" or "Reimbursement")
                .All(c => c.Definition.CalculationType != "Formula" || ResolvedPayType(c.Definition) == "Variable Pay"
                    && context.Values.Keys.Any(key => Regex.IsMatch(c.Definition.Formula, @"\b" + Regex.Escape(key) + @"\b", RegexOptions.IgnoreCase))))
            return existingAnnual;
        throw new InvalidOperationException("Named source earnings require an independent positive monthly salary budget; variable quantities and earned formula outputs are not annualized. Review monthly inputs or an explicit monthly CTC target.");
    }

    private static List<NamedFormulaInputManifest> PrepareSourceSetupNamedManifests(LocationAllocationPlan plan,
        ExcelPayrollSetupRequest request, string rawBatchHash, string rawSourceHash)
    {
        if (plan.NamedProfile is not { } profile) return [];
        var manifests = new List<NamedFormulaInputManifest>();
        foreach (var period in plan.Periods)
        foreach (var part in period.Parts)
        {
            var review = plan.Evidence.Preview.LocationAllocations!.Groups.Single(g => g.EmployeeId == period.EmployeeId)
                .Parts.Single(p => p.AssignmentId == part.AssignmentId && p.PartKey == part.PartKey);
            var row = plan.Evidence.Batch.Rows.Single(r => r.Id == review.RowId);
            var bound = ReviewedNamedFormulaInputs.Bind(profile, request.NamedInputReview!.Bindings, plan.Evidence.Source, row, []);
            if (part.NamedInputs is null || !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(bound.Context.Values, Json), JsonSerializer.SerializeToNode(part.NamedInputs.Values, Json)))
                throw new InvalidOperationException("A first-source part lost its exact row's named operands.");
            var manifest = new NamedFormulaInputManifest(1, period.ClientId, period.EmployeeId, period.PayPeriod,
                part.AssignmentId, part.SalaryStructureId, part.WorkLocationId, part.PartKey, plan.Evidence.Batch.Id, row.Id, row.SourceRow,
                ExcelPayslipRepository.EmploymentSourceHash(plan.Evidence.Batch, plan.Evidence.Source), rawBatchHash, rawSourceHash,
                ExcelPayslipMasterSynchronization.ProfileIdentityHash(plan.Evidence.Batch, row, plan.Evidence.Source), profile.DeclarationHash,
                profile.Definitions, bound.Context.Values, bound.Cells, "", 0, DateTime.MinValue, "")
            {
                CompleteSourcePartKeys = period.Parts.Select(p => p.PartKey).Order(StringComparer.Ordinal).ToList(),
                ReferenceAssignmentId = period.ReferenceAssignmentId, NativeRulesHash = profile.RulesHash,
                ComponentScopesHash = ReviewedNamedFormulaInputs.Hash(part.ComponentScopes.OrderBy(p => p.Key, StringComparer.Ordinal))
            };
            manifest = manifest with { ManifestHash = NamedFormulaInputStore.ManifestHash(manifest) };
            part.NamedInputs.ManifestHash = manifest.ManifestHash;
            manifests.Add(manifest);
        }
        if (manifests.Select(m => m.AssignmentId).Distinct(StringComparer.Ordinal).Count() != manifests.Count)
            throw new InvalidOperationException("Every named first-source part needs its own exact assignment binding.");
        return manifests;
    }
}
