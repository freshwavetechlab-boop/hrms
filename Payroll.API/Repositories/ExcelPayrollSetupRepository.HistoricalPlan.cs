using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    private sealed record HistoricalWrite(HistoricalPeriodSalaryAssignment Assignment, string EmployeeCode,
        decimal AnnualTarget, Dictionary<string, decimal> Inputs,HistoricalInitialSourceTargetEvidence? InitialTarget=null,
        IReadOnlyList<HistoricalLiteralSelfInputEvidence>? LiteralSelfInputs=null,HistoricalSourceIdentityEvidence? SourceIdentity=null,
        HistoricalProfessionalTaxEvidence? ProfessionalTax=null,HistoricalNamedInputPlan? NamedInputs=null)
    {
        internal HistoricalReportedInputPlan? ReportedInputs { get; init; }
    }
    private sealed record HistoricalPlan(HistoricalPeriodSalaryPreview Preview, EmployeeRepository.ClosedSalaryHistoryState State,
        DateTime From, DateTime To, string SourceHash, List<HistoricalWrite> Writes);
    private PayRunRepository HistoricalRuns => historicalPayRuns ?? new PayRunRepository(configuration, new TaxEngineRepository(configuration));

    private async Task<HistoricalPlan> BuildHistoricalPlanAsync(MySqlConnection db, MySqlTransaction tx, int clientId,
        HistoricalPeriodSalaryRequest request, bool lockState, AuthUser actor)
    {
        if(request.Setup?.ReportedPeriodInputs is not null)
            return await BuildReportedHistoricalPlanAsync(db,tx,clientId,request,lockState,actor);
        RequireHistoricalAmountDifferenceEnabled(request);
        RequireHistoricalNamedInputsEnabled(request);
        if(configuration.GetValue<bool>("Payroll:HistoricalBatchProofEnabled"))
            return await BuildHistoricalBatchPlanAsync(db,tx,clientId,request,lockState,actor);
        ValidateHistoricalSalaryRequest(request);
        if(request.PeriodProfessionalTaxReview is not null&&!PayRunRepository.CanManageReviewedProfessionalTax(actor,clientId))
            throw new UnauthorizedAccessException("Composed closed salary and period PT review additionally requires settings management for this client.");
        var setupRequest = request.Setup ?? throw new InvalidOperationException("Historical setup review is missing.");
        var ids = request.Assignments.Select(a => a.EmployeeId).Order().ToArray();
        if (lockState) await LockHistoricalInputsAsync(db, tx, clientId, request);
        var state = await EmployeeRepository.ReadClosedSalaryHistoryStateAsync(db, tx, clientId, ids, lockState);
        await ExcelPayslipRepository.RequireCurrentConsolidationSourceAsync(db, tx, clientId, setupRequest.BatchId);
        var (batch, source) = await ReadSourceAsync(db, tx, clientId, setupRequest.BatchId);
        var (from, to) = ClosedHistoricalPeriod(batch.Month, EmployeeEmploymentRules.Today);
        var preview = new HistoricalPeriodSalaryPreview { ClientId = clientId, BatchId = batch.Id, PayPeriod = batch.Month,
            EffectiveFrom = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), EffectiveTo = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        var sourceHash = Hash(JsonSerializer.Serialize(new { Batch = batch, Source = source }, Json));
        // Preserve unknown legacy JSON fields too: a typed round-trip alone is
        // insufficient as a stale-source witness for this append-only lane.
        var rawSourceWitness = await db.ExecuteScalarAsync<string>(@"SELECT CONCAT(SHA2(CAST(b.batch_json AS CHAR),256),':',SHA2(CAST(s.source_json AS CHAR),256))
FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id
WHERE b.client_id=@ClientId AND b.id=@BatchId", new { ClientId = clientId, BatchId = batch.Id }, tx);
        preview.SourceHash = sourceHash;
        var plan = new HistoricalPlan(preview, state, from, to, sourceHash, []);
        void Block(string message) { if (!preview.Blockers.Contains(message, StringComparer.Ordinal)) preview.Blockers.Add(message); }
        if (setupRequest.EffectiveFrom != preview.EffectiveFrom) Block("Historical effective date must equal the first day of this saved source month.");
        if (ExcelPayslipCalculationService.ValidateSource(source, batch) is string sourceError) Block("Saved source validation: " + sourceError);
        if (batch.Rows.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != batch.Rows.Count
            || batch.Rows.Any(r => string.IsNullOrWhiteSpace(r.Id))) Block("The saved source row identity is invalid.");
        var selected = batch.Rows.Where(r => setupRequest.RowIds.Contains(r.Id)).ToArray();
        if (selected.Length != request.Assignments.Count) Block("A selected row no longer belongs to this exact saved batch.");
        try { ValidateSharedPayrollDaysBinding(setupRequest,source,selected,batch.Rows); }
        catch(InvalidOperationException error) { Block(error.Message); }
        IReadOnlyList<HistoricalLiteralSelfInputEvidence> literalSelfInputs=[];
        try { literalSelfInputs=ReviewHistoricalLiteralSelfInputs(request,source,selected,sourceHash); }
        catch(InvalidOperationException error) { Block(error.Message); }
        foreach (var row in selected)
            if (batch.Rows.Count(r => string.Equals(r.EmployeeCode.Trim(), row.EmployeeCode.Trim(), StringComparison.OrdinalIgnoreCase)) != 1)
                Block("Repeated source employee rows require the existing location/source-part review; this lane cannot combine them.");
        var originals = source.CaptureVersion > 0 ? (await ExcelPayslipRepository.ReadLinkedOriginalsAsync(db, tx, clientId, batch.Id)).ToList() : [];
        if (source.CaptureVersion > 0 && (source.DependencyReviewStatus != "Reviewed"
            || ExcelPayslipRepository.ValidateCapturedWorkbookBindings(source, originals) is not null))
            Block("The exact captured original/dependency ownership must be reviewed before historical salary proof.");
        var identities = await ReadIdentitiesAsync(db, tx, clientId, batch.Month);
        IReadOnlyDictionary<string,HistoricalVerifiedSourceIdentity> reviewedIdentities=new Dictionary<string,HistoricalVerifiedSourceIdentity>();
        try { reviewedIdentities=await ReviewHistoricalSourceIdentitiesAsync(db,tx,request,batch,source,identities,sourceHash,lockState); }
        catch(InvalidOperationException error) { Block(error.Message); }
        var setupJson = await PayrollDataTableStore.GetSetupJsonAsync(db, tx);
        var setup = JsonNode.Parse(setupJson)!.AsObject();
        var masters = (setup["salaryComponents"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var templates = (setup["salaryStructures"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var templateScopes = await ReadTemplateScopesAsync(db, tx, clientId);
        var namedProfile=await ReadHistoricalNamedProfileAsync(db,tx,clientId,request,source,lockState);
        foreach (var error in TemplateScopeIssues(setupRequest, [])) Block(error);
        if (setupRequest.TemplateScope is { } scope && (scope.OpenEnded || scope.EffectiveTo != preview.EffectiveTo))
            Block("Historical template applicability must be explicitly limited to this closed source month.");
        var resolved = new List<ResolvedComponent>();
        foreach (var component in setupRequest.Components)
        {
            var found = masters.Where(m => Text(m, "id") == component.ExistingComponentId).ToArray();
            if (found.Length != 1) { Block(component.Code + ": selected component Master is unavailable."); continue; }
            if (ReusedComponentDifferences(found[0], component).Count > 0) Block(component.Code + ": existing Master classification, pay basis or priority differs; shared Masters will not change.");
            resolved.Add(new(component, Text(found[0], "id"), false));
        }
        foreach (var error in HistoricalSourceCoverage(source, setupRequest, resolved,literalSelfInputs.Select(input=>input.ComponentKey).ToHashSet(StringComparer.Ordinal))) Block(error);
        // This is an explicit closed-period salary correction, with native values
        // retained. The existing bounded monthly/cycle proof is valid too; no
        // old Applied receipt or dated attendance is fabricated.
        var native = await PayRunRepository.ReadNativeAttendanceProofAsync(db, tx, clientId, batch.Month, ids, allowRecordedMonthlyForSalaryCorrection: true);
        await PopulateHistoricalUnknownPresenceProofsAsync(db, tx, clientId, request, batch, source, native, lockState);
        var evidence = new Plan { Batch = batch, Source = source, EffectiveFrom = from, LinkedOriginals = originals, NativeAttendanceInputs = native };
        evidence.Components.AddRange(resolved);
        var coverage = (await db.QueryAsync<HistoricalCoverage>(@"SELECT p.EmployeeId,r.Id RunId,r.Status,p.IsSkipped
FROM payrunemployees p JOIN payruns r ON r.Id=p.PayRunId WHERE r.ClientId=@ClientId AND r.PayPeriod=@Period AND r.RunType='Regular' AND p.EmployeeId IN @Ids",
            new { ClientId = clientId, Period = batch.Month, Ids = ids }, tx)).ToArray();
        var runRows = (await db.QueryAsync<HistoricalRunState>("SELECT Id,Status,CAST(RequestJson AS CHAR) RequestJson FROM payruns WHERE ClientId=@ClientId AND PayPeriod=@Period AND RunType='Regular'",
            new { ClientId = clientId, Period = batch.Month }, tx)).OrderBy(r => r.Id).ToArray();
        var simulations = new List<object>();
        foreach (var assignment in request.Assignments)
        {
            var rowPreview = new HistoricalPeriodSalaryPreviewRow { RowId = assignment.RowId, EmployeeId = assignment.EmployeeId, TemplateId = assignment.TemplateId };
            var rowLiteralSelfInputs=literalSelfInputs.Where(input=>input.RowId==assignment.RowId).ToArray();
            if(rowLiteralSelfInputs.Length>0)rowPreview.ReviewedLiteralSelfInputs=rowLiteralSelfInputs.ToList();
            if(request.SalaryTargetMode==InitialSourceTargetMode)rowPreview.SalaryTargetMeaning=InitialSourceTargetExplanation;
            preview.Rows.Add(rowPreview);
            void RowBlock(string message) { if (!rowPreview.Blockers.Contains(message, StringComparer.Ordinal)) rowPreview.Blockers.Add(message); }
            var row = selected.SingleOrDefault(r => r.Id == assignment.RowId);
            var identity = identities.SingleOrDefault(e => e.Id == assignment.EmployeeId);
            if (row is null || identity is null) { RowBlock("The exact source row and existing client employee are required."); continue; }
            rowPreview.SourceRow = row.SourceRow; rowPreview.EmployeeCode = identity.EmployeeCode;
            var reviewedIdentity=reviewedIdentities.GetValueOrDefault(row.Id);
            rowPreview.ReviewedSourceIdentity=reviewedIdentity?.Evidence;
            foreach (var error in HistoricalIdentityIssues(batch, source, row, identity, identities,reviewedIdentity)) RowBlock(error);
            if (coverage.Any(c => c.EmployeeId == assignment.EmployeeId && !c.IsSkipped)) RowBlock("This employee already has a regular result in this month; retain that run and review its existing draft.");
            if (runRows.Any(r => HistoricalRequestIncludes(r.RequestJson, assignment.EmployeeId))) RowBlock("A saved regular request already includes this employee/month. Review that exact run before inserting another salary basis.");
            if (runRows.Any(r => r.Status is "AwaitingProcessing" or "Queued" or "Processing")) RowBlock("Another regular run is pending/processing for this client/month. Wait and review its final coverage first.");
            var template = templates.SingleOrDefault(t => Text(t, "id") == assignment.TemplateId && RefId(Text(t, "clientId")) == clientId && Text(t, "active") != "false");
            var components = ComponentsForRow(resolved, row.Id);
            if (template is null || TransferTemplateDifferences(template, components, employeeInputOverridesFallback: true).Count > 0)
                RowBlock("The existing client template does not exactly preserve the reviewed source formulas, component order, inputs and proration. Configure an exact existing template first.");
            if (template is not null)
            {
                var decisions = setupRequest.TemplateDecisions.Where(d => d.ExistingTemplateId == assignment.TemplateId).ToArray();
                if (decisions.Length > 1) RowBlock("Review one unambiguous applicability decision for this existing template.");
                var applicable = CompareTemplateScope(assignment.TemplateId, ReusedTemplateRulesSignature(template, components), setupRequest,
                    clientId, templateScopes, decisions.FirstOrDefault());
                if (!applicable.Compatible) RowBlock("Template applicability: " + applicable.Reason);
            }
            if (!native.TryGetValue(assignment.EmployeeId, out var attendance) || attendance.Error.Length > 0 || !attendance.PresentDays.HasValue && attendance.ProofKind != HistoricalUnknownPresenceKind)
                RowBlock("Verified existing native actual presence, payable units and dated cycle are required; this lane never invents attendance.");
            EmployeeRepository.ClosedSalaryHistoryRow? basis = null;
            HistoricalInitialSourceTargetEvidence? initial=null;
            decimal? target=null;
            try {
                if(request.SalaryTargetMode==InitialSourceTargetMode)
                {
                    var currentAnnual=await db.ExecuteScalarAsync<decimal>("SELECT AnnualCtc FROM employees WHERE Id=@Id AND ClientId=@ClientId",new{Id=assignment.EmployeeId,ClientId=clientId},tx);
                    initial=ReadInitialSourceTarget(request,source,row,assignment.EmployeeId,currentAnnual,sourceHash);
                    EmployeeRepository.RequireClosedInitialSalaryWindow(state,assignment.EmployeeId,from,to,initial.AnnualSalaryTarget,currentAnnual);
                    target=initial.AnnualSalaryTarget;rowPreview.InitialSourceAnnualSalaryTarget=target;rowPreview.SalaryTargetMeaning=InitialSourceTargetExplanation;
                    if(target==0)RowBlock("The reviewed monthly CTC is an explicit zero matching current Master, not missing data. This bounded initial lane cannot yet prove a zero salary basis through ordinary persisted Employee payroll; no fallback or amount was substituted.");
                }
                else {basis=EmployeeRepository.RequireClosedSalaryBasis(state,assignment.EmployeeId,from,to);target=basis.AnnualCtc;rowPreview.RetainedAnnualSalaryTarget=basis.AnnualCtc;}
            }
            catch (InvalidOperationException error) { RowBlock(error.Message); }
            if (rowPreview.Blockers.Count > 0 || preview.Blockers.Count > 0 || target is null || template is null || attendance is null) continue;
            try
            {
                var named=BindHistoricalNamedInputs(request,namedProfile,source,row);
                rowPreview.NamedInputs=named?.Context;
                var cells = source.Rows.Single(r => r.SourceRow == row.SourceRow);
                var arithmetic = CalculateRow(clientId, setupRequest, components, cells, row,
                    source.Columns.Where(c => c.BlankAsZero).Select(c => c.ColumnIndex).ToHashSet(), source, target.Value,named?.Context);
                if(rowLiteralSelfInputs.Any(input=>!arithmetic.Inputs.TryGetValue(input.InputKey,out var amount)||amount!=input.LiteralValue))
                    RowBlock("The historical salary input differs from its exact reviewed literal self-input cell; no current Master amount or alias is substituted.");
                foreach (var issue in arithmetic.Issues.Where(i => i.Severity == "Error")) RowBlock(issue.Message);
                if (arithmetic.Inputs.Count == 0 && named is null) RowBlock("An independent source monthly/rate input is required for this history lane. An empty historical input map would fall back to current Master inputs; it cannot be silently reused.");
                if (arithmetic.Inputs.Any(input => decimal.Round(input.Value, 4) != input.Value)) RowBlock("An independent source input exceeds the history store's four-decimal precision; it cannot be silently rounded.");
                var master = await EmployeeRepository.LoadRecruitmentEmployeeAsync(db, assignment.EmployeeId, tx)
                    ?? throw new InvalidOperationException("The existing Master employee is unavailable.");
                var planned = new PlannedRow { RowId = row.Id, SourceRow = row.SourceRow, Employee = master, Components = components,
                    TemplateId = assignment.TemplateId, SalaryInputs = arithmetic.Inputs, Preview = arithmetic.Preview };
                foreach (var error in TemplateScopeIssues(setupRequest, [planned])) RowBlock(error);
                // Arithmetic fallback is not presence evidence. Use only the retained native actual value for review.
                planned.Preview.PresentDays = attendance.PresentDays ?? arithmetic.Preview.PresentDays; // Arithmetic only; authenticated proof retains NULL.
                evidence.Rows.Add(planned);
                var attendanceProof = ProveHistoricalAttendanceInput(evidence, planned, setupRequest, clientId, sourceHash);
                if (!HistoricalAttendanceMatched(attendanceProof)) RowBlock(attendanceProof.Reason);
                var inspectedSourceBoundary=HistoricalInitialSourceBoundary(request,initial);
                var namedLeaves=named is null?null:request.NamedInputReview!.Bindings.ToDictionary(b=>b.ColumnIndex,b=>new NamedRecurringSourceBoundary(b.Key,named.Context.Values[b.Key]));
                rowPreview.Formulas = components.Select(component => namedLeaves is null
                    ?InspectTransferFormula(setupRequest, source, row, component.Definition,inspectedSourceBoundary)
                    :InspectNamedTransferFormula(setupRequest,source,row,component.Definition,namedLeaves,inspectedSourceBoundary)).ToList();
                foreach (var formula in rowPreview.Formulas.Where(f => f.Status is not ("Exact supported translation" or "Reviewed fixed source"))) RowBlock(formula.Code + ": " + formula.Reason);
                if (rowPreview.Blockers.Count > 0) continue;
                HistoricalConditionalProfessionalTax? conditionalPt=null;
                if(request.PeriodProfessionalTaxReview is { } ptReview)
                {
                    RequireHistoricalPtSourceClosure(request,source,row,sourceHash);
                    conditionalPt=new(row.Id,sourceHash,ptReview.Reason.Trim(),actor.Id,actor.Email,request);
                }
                var calculation = await HistoricalRuns.SimulateHistoricalSalaryInTransactionAsync(db, tx,
                    new() { Mode = "Employee", ClientId = clientId, EmployeeId = assignment.EmployeeId, PayPeriod = batch.Month },
                    new(clientId, assignment.EmployeeId, batch.Month, assignment.TemplateId, target.Value, arithmetic.Inputs,conditionalPt,named?.Context));
                rowPreview.ProfessionalTax=calculation.ProfessionalTax;
                foreach (var issue in calculation.Simulation.Issues.Where(i => i.Severity == "Error")) RowBlock(issue.Message);
                if (!calculation.Simulation.Complete || calculation.InputFingerprint.Length == 0) RowBlock("Complete regular Employee-mode proof is required; no hypothetical template-only result can authorize a historical insert.");
                var knownCodes = components.Select(c => c.Definition.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (calculation.Simulation.Lines.Any(line => line.Earned != 0 && !knownCodes.Contains(line.Code))) RowBlock("Native payroll adds an unmapped nonzero component or period adjustment; full source coverage is required.");
                foreach (var error in CompareHistoricalNative(planned, row, calculation.Simulation)) RowBlock(error);
                if(conditionalPt is not null)
                {
                    if(calculation.ProfessionalTax is null)RowBlock("The proposed-basis PT proof is missing; no historical insert is authorized.");
                    if(request.AmountDifferenceReview is null)RequireHistoricalPtExactDisplays(planned.Preview,row);
                }
                rowPreview.Gross = planned.Preview.Gross; rowPreview.TotalDeductions = planned.Preview.Deductions;
                rowPreview.EmployerCost = planned.Preview.Benefits; rowPreview.NetPay = planned.Preview.Net;
                rowPreview.Lines = planned.Preview.Lines;
                rowPreview.AmountReview = ClassifyTransferAmounts(evidence, planned, setupRequest, sourceHash, rowPreview.Formulas);
                if(request.AmountDifferenceReview is not null)
                {
                    rowPreview.AmountReview=ReviewHistoricalAmountDifferences(request,batch,source,row,planned.Preview,rowPreview.Formulas,
                        calculation.Simulation,calculation.InputFingerprint,rowPreview.AmountReview);
                    if(conditionalPt is not null&&!IsAcknowledgedHistoricalAmountDifference(request,rowPreview.AmountReview))RequireHistoricalPtExactDisplays(planned.Preview,row);
                }
                if (rowPreview.AmountReview.Status is not ("Exact at raw precision" or "Explained rounding" or BoundedHistoricalDifferenceStatus)) RowBlock(rowPreview.AmountReview.Reason);
                if (rowPreview.AmountReview.Status is "Explained rounding" or BoundedHistoricalDifferenceStatus)
                {
                    rowPreview.AmountReview.Acknowledged = setupRequest.RoundingReviews.GetValueOrDefault(row.Id) == rowPreview.AmountReview.ProofHash;
                    if (!rowPreview.AmountReview.Acknowledged) RowBlock(rowPreview.AmountReview.Status==BoundedHistoricalDifferenceStatus
                        ?"Review and acknowledge this exact bounded historical difference ledger before saving; every source/native amount remains unchanged."
                        :"Review and acknowledge this exact deterministic source/core rounding proof before saving the historical basis; displayed source and raw values remain unchanged.");
                }
                rowPreview.SourceMonthlyInputs = new(arithmetic.Inputs);
                simulations.Add(new { assignment.EmployeeId, calculation.InputFingerprint, Comparison = rowPreview.AmountReview });
                if (rowPreview.Blockers.Count == 0) plan.Writes.Add(new(assignment, identity.EmployeeCode, target.Value, new(arithmetic.Inputs),initial,rowLiteralSelfInputs.Length>0?rowLiteralSelfInputs:null,reviewedIdentity?.Evidence,calculation.ProfessionalTax,named));
            }
            catch (InvalidOperationException error) { RowBlock(error.Message); }
        }
        foreach (var row in preview.Rows.Where(r => r.Blockers.Count > 0)) Block("Source row " + row.SourceRow + ": " + string.Join(" ", row.Blockers));
        if (plan.Writes.Count != request.Assignments.Count && preview.Blockers.Count == 0) Block("Every selected historical salary row needs complete source/native proof.");
        preview.Fingerprint = Hash(JsonSerializer.Serialize(new { Version = 1, clientId, Request = request, SourceHash = sourceHash, RawSourceWitness = rawSourceWitness,
            CurrentMaster = state.CurrentMasterHash, History = state.History, Setup = setupJson, TemplateScopes = templateScopes, Native = native.OrderBy(p => p.Key), Coverage = coverage.OrderBy(c => c.RunId).ThenBy(c => c.EmployeeId),
            Runs = runRows, Simulations = simulations, Rows = preview.Rows }, Json));
        return plan;
    }

    private static IReadOnlyList<string> CompareHistoricalNative(PlannedRow planned, ExcelPayslipRow source, PayrollSimulationResult simulation)
    {
        var errors = new List<string>(); var actual = simulation.Lines.GroupBy(l => l.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var lines = new List<ExcelPayrollSetupLine>();
        foreach (var component in planned.Components)
        {
            var original = planned.Preview.Lines.Single(l => l.Code.Equals(component.Definition.Code, StringComparison.OrdinalIgnoreCase));
            if (!actual.TryGetValue(component.Definition.Code, out var values) || values.Length != 1)
            {
                if (original.SourceAmount != 0) errors.Add(component.Definition.Code + ": a uniquely mapped native financial line is missing.");
                lines.Add(original with { CalculatedAmount = 0, Variance = -original.SourceAmount, UnroundedEarned = 0 }); continue;
            }
            var line = values[0];
            if (line.Category != component.Definition.Category) errors.Add(component.Definition.Code + ": native financial classification differs from the saved source.");
            lines.Add(original with { CalculatedAmount = line.Earned, Variance = original.SourceAmount.HasValue ? line.Earned - original.SourceAmount.Value : null,
                UnroundedEarned = line.UnroundedEarned, Category = line.Category });
        }
        planned.Preview.Lines = lines; planned.Preview.Gross = simulation.Gross; planned.Preview.Deductions = simulation.Deductions;
        planned.Preview.Benefits = simulation.Benefits; planned.Preview.Net = simulation.Net;
        return errors;
    }

    private static bool HistoricalRequestIncludes(string? text, int employeeId)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var node = JsonNode.Parse(text)!;
        var selected = (node["IncludedEmployeeIds"] ?? node["includedEmployeeIds"]) as JsonArray;
        // An older unbounded regular request is not proof that this employee was intentionally excluded.
        return selected is null || selected.Count == 0 || selected.Any(value => value!.GetValue<int>() == employeeId);
    }
    private sealed class HistoricalCoverage { public int EmployeeId { get; set; } public int RunId { get; set; } public string Status { get; set; } = ""; public bool IsSkipped { get; set; } }
    private sealed class HistoricalRunState { public int Id { get; set; } public string Status { get; set; } = ""; public string? RequestJson { get; set; } }
}
