using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal static void SeparateKeptTransferRows(Plan plan,ExcelPayrollSetupResult result)
    {
        result.KeptRows=plan.Preview.Transfer?.Templates.SelectMany(t=>t.KeptAssignments).ToList() ?? [];
        plan.Rows.RemoveAll(r=>r.KeepExisting);
    }
    internal static string TransferDraftMetadata(string existingJson, ExcelPayrollSetupPreview preview)
    {
        var metadata = JsonNode.Parse(existingJson)?.AsObject() ?? new JsonObject();
        metadata["transfer"] = JsonSerializer.SerializeToNode(preview.Transfer, Json);
        metadata["locationAllocations"] = JsonSerializer.SerializeToNode(preview.LocationAllocations, Json);
        metadata["previewPlanHash"] = preview.PlanHash;
        metadata["previewCanApply"] = preview.CanApply;
        // A saved draft never authorizes Apply: Apply still rebuilds under locks and compares its fresh reviewed hash.
        return metadata.ToJsonString();
    }

    internal static void RequireMatchingSetupPlanHash(string current, string reviewed)
    {
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(current), System.Text.Encoding.ASCII.GetBytes(reviewed.ToLowerInvariant())))
            throw new InvalidOperationException("Source or payroll configuration changed after preview. Refresh the preview before applying.");
    }

    internal static void ValidateTransferRequest(ExcelPayrollSetupRequest request)
    {
        ValidateAttendanceReview(request.AttendanceReview);
        if (request.RoundingReviews is null || request.RoundingReviews.Count > 100
            || request.RoundingReviews.Any(r => r.Key.Length > 100 || request.RowIds?.Contains(r.Key) != true || !Regex.IsMatch(r.Value ?? "", "^[a-f0-9]{64}$")))
            throw new InvalidOperationException("Rounding review must refer to selected saved rows and their current bounded proof fingerprints.");
        if(request.TemplateDecisions?.Any(d=>d is not null && (d.ScopeAction is not("" or "AttestLegacyScope" or "ExtendReviewedScope" or "DifferentScope") || d.ScopeReason is null || d.ScopeReason.Length>500))==true)throw new InvalidOperationException("Review bounded template scope decisions and reasons.");
        if (request.TransferReviewVersion is not (0 or 1) || request.TemplateDecisions is null || request.TemplateDecisions.Count > 100
            || request.TemplateDecisions.Any(d => d is null || !Regex.IsMatch(d.VariantKey ?? "", "^[0-9]{1,20}$")
                || d.ExistingTemplateId is null || d.ExistingTemplateId.Length > 20 || d.ExistingTemplateId.Length > 0 && !Regex.IsMatch(d.ExistingTemplateId, "^[0-9]{1,20}$")
                || d.CreateVariant && d.ExistingTemplateId.Length > 0 || d.KeepExisting && (d.CreateVariant || d.ExistingTemplateId.Length > 0))
            || request.TemplateDecisions.Select(d => d.VariantKey).Distinct().Count() != request.TemplateDecisions.Count)
            throw new InvalidOperationException("Review distinct bounded transfer template decisions; choose reuse or create, never both.");
    }

    internal static bool TransferAmountsMatch(ExcelPayrollSetupPreviewRow row, ExcelPayslipRow source, int componentCount) =>
        row.Net.HasValue && Math.Abs(row.Net.Value - source.NetPay) <= 0.000001m
        && row.Gross.HasValue && Math.Abs(row.Gross.Value - (source.DeclaredGross ?? source.Earnings.Sum(a => a.Amount))) <= 0.000001m
        && row.Deductions.HasValue && Math.Abs(row.Deductions.Value - (source.DeclaredDeductions ?? source.Deductions.Sum(a => a.Amount))) <= 0.000001m
        && row.Benefits.HasValue && Math.Abs(row.Benefits.Value - source.EmployerContributions.Sum(a => a.Amount)) <= 0.000001m
        && row.Lines.Count == componentCount
        && row.Lines.All(l => l.SourceAmount.HasValue && l.Variance.HasValue && Math.Abs(l.Variance.Value) <= 0.000001m);

    internal static ExcelPayrollTransferFormula InspectTransferFormula(ExcelPayrollSetupRequest request, ExcelPayslipCalculationSource source,
        ExcelPayslipRow row, ExcelPayrollSetupComponent component)
    {
        var column = component.ExpectedColumnIndex;
        var cell = column is int index ? source.Rows.SingleOrDefault(r => r.SourceRow == row.SourceRow)?.Cells.ElementAtOrDefault(index) : null;
        var sourceHash = Hash(JsonSerializer.Serialize(new { row.SourceRow, column, cell, source.WorkbookKey, source.WorkbookFiles, source.ExternalMappings }, Json));
        ExcelPayrollTransferFormula Review(string status, string reason) => new(row.Id, component.Code, row.SourceRow, column, sourceHash, status, reason);
        if (cell is null || cell.Error is not null || cell.CalculationError is not null || cell.MissingCachedValue)
            return Review("Blocked", "A captured output cell and valid comparison value are required; choose the actual earned/deduction/employer output column.");
        // Reviewing a literal output cannot turn a lookup/formula cache in the
        // selected salary basis into a permanent recurring employee input.
        if(component.SourceColumnIndex is int recurringColumn && !string.IsNullOrWhiteSpace(source.Rows.SingleOrDefault(r=>r.SourceRow==row.SourceRow)?.Cells.ElementAtOrDefault(recurringColumn)?.FormulaText))
            return Review("Blocked","The recurring monthly/rate input is itself a captured formula. Translate its dependencies into native components or explicitly resolve a reviewed effective salary input; its lookup/formula cache cannot become a recurring constant.");
        if (string.IsNullOrWhiteSpace(cell.FormulaText))
            return request.ReviewedFixedInputs
                ? Review("Reviewed fixed source", "No source formula is captured. The reviewer accepted fixed/input semantics for this component; future-period rules are not inferred.")
                : Review("Blocked", "This output has no captured formula. Review fixed/input semantics explicitly; cached amounts alone do not prove recurring rules.");
        if (component.IsPeriodAdjustment)
            return Review("Blocked", "A formula output cannot be frozen into a period adjustment by Transfer Batch. Review an explicit recurring rule or a separately approved source correction.");
        try
        {
            var translation = TranslateTransferFormulaWithEvidence(cell.FormulaText, request, source, row);
            var translated = translation.Formula;
            if (translation.Dependencies.Count > 0)
                sourceHash = Hash(JsonSerializer.Serialize(new { Output = sourceHash, RecurringInputs = translation.Dependencies }, Json));
            string configured;
            if (component.SourceColumnIndex.HasValue && component.CalculationType == "Fixed Amount" && component.ProRata)
                configured = component.Code + "_MONTHLY/PAYROLL_DAYS*PAYABLE_DAYS";
            else if (component.SourceColumnIndex.HasValue && component.CalculationType == "Daily Rate")
                configured = component.Code + "_MONTHLY*PAYABLE_DAYS";
            else if (component.CalculationType == "Formula" && !component.ProRata)
                configured = component.RowFormulas.GetValueOrDefault(row.Id, component.Formula);
            else return Review("Blocked", "The captured formula needs an explicit native Formula without additional proration, or a proven monthly/daily input mapping.");
            if(request.PresentDaysColumnIndex is null && Regex.IsMatch(configured,@"\bPRESENT_DAYS\b",RegexOptions.IgnoreCase))return Review("Blocked","Map actual present/worked days explicitly; payable days cannot silently stand in for PRESENT_DAYS.");
            PayrollFormulaEvaluator.Validate(translated); PayrollFormulaEvaluator.Validate(configured);
            if (CanonicalTransferFormula(translated, request) != CanonicalTransferFormula(configured, request))
                return Review("Blocked", "Captured formula translation differs from the configured rule. Equivalent-looking rewrites require supported translation; cached-value equality is insufficient.");
            return Review("Exact supported translation", "Captured numeric arithmetic/conditional AST maps exactly to the selected native component/day inputs. Statutory correctness and saved Employee parity remain separate checks.");
        }
        catch (InvalidOperationException e) { return Review("Blocked", e.Message); }
    }

    private static string CanonicalTransferFormula(string formula, ExcelPayrollSetupRequest request)
    {
        var value = Regex.Replace(formula.TrimStart('=').ToUpperInvariant(), @"\s+", "");
        value = Regex.Replace(value, @"ROUND\(([^()]*(?:\([^()]*\)[^()]*)*),0\)", "ROUND($1)");
        return request.FixedPayrollDays.HasValue ? value.Replace("PAYROLL_DAYS", request.FixedPayrollDays.Value.ToString(CultureInfo.InvariantCulture)) : value;
    }

    // Deliberately bounded proof. Lookups/external inputs and arbitrary shared cells still require explicit native input mapping. Numeric conditionals use the main payroll AST.
    internal static string TranslateTransferFormula(string raw, ExcelPayrollSetupRequest request, ExcelPayslipCalculationSource source, ExcelPayslipRow row)
        => TranslateTransferFormulaWithEvidence(raw, request, source, row).Formula;
    private static int TransferColumn(string value)
    {
        if (value.Length > 3) throw new InvalidOperationException("Source dependency exceeds supported worksheet columns.");
        var index = value.Aggregate(0, (v,c) => v * 26 + c - 'A' + 1) - 1;
        if (index > 1499) throw new InvalidOperationException("Source dependency exceeds supported worksheet columns.");
        return index;
    }
    private static string TransferLetter(int index) { var result = ""; for (var n = index + 1; n > 0; n = (n-1)/26) result = (char)('A'+(n-1)%26) + result; return result; }

    internal static string TransferRulesSignature(IReadOnlyList<ResolvedComponent> components)
    {
        var rules = JsonSerializer.Serialize(components.Select(c=>new {c.Id,c.Definition.Category,c.Definition.ComponentRole,c.Definition.StatutoryType,c.Definition.Taxable,c.Definition.Priority,c.Definition.CalculationType,Value=CanonicalTransferValue(c.Definition.Value),Formula=CanonicalNativeFormula(c.Definition.Formula),c.Definition.BaseComponent,c.Definition.ProRata,c.Definition.IsPeriodAdjustment}),Json);
        // Preserve signatures for legacy inferred pay types and their explicit
        // equivalents. A recurring Variable Pay rule has different aggregate
        // semantics and must receive a distinct signature.
        return components.Any(c => ResolvedPayType(c.Definition) != (c.Definition.IsPeriodAdjustment ? "Variable Pay" : "Fixed Pay"))
            ? Hash(JsonSerializer.Serialize(new { Version = "explicit-pay-type-v1", Rules = rules, PayTypes = components.Select(c => new { c.Id, PayType = ResolvedPayType(c.Definition) }) }, Json))
            : Hash(rules);
    }
    private static string CanonicalTransferValue(string value) => decimal.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out var number)?number.ToString("G29",CultureInfo.InvariantCulture):value.Trim();
    private static string CanonicalNativeFormula(string formula) => string.IsNullOrWhiteSpace(formula)?"":PayrollFormulaEvaluator.Canonicalize(formula);
    private static bool TransferFieldEqual(string field,string existing,string proposed) {
        try {return field=="formula"?CanonicalNativeFormula(existing)==CanonicalNativeFormula(proposed):field=="value"?CanonicalTransferValue(existing)==CanonicalTransferValue(proposed):string.Equals(existing.Trim(),proposed.Trim(),StringComparison.Ordinal);}
        catch(InvalidOperationException){return false;}catch(FormatException){return false;}catch(OverflowException){return false;}
    }
    internal static IReadOnlyList<string> TransferTemplateDifferences(JsonObject candidate, IReadOnlyList<ResolvedComponent> proposed)
    {
        var lines = (candidate["lines"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var differences = new List<string>();
        if (lines.Count != proposed.Count) differences.Add("Component count differs");
        for (var i = 0; i < Math.Min(lines.Count, proposed.Count); i++)
        {
            var line = lines[i]; var c = proposed[i]; var d = c.Definition;
            var expected = new Dictionary<string,string> { ["componentId"] = c.Id, ["value"] = d.Value,
                ["calculationType"] = d.IsPeriodAdjustment ? "Manual / Variable" : d.CalculationType, ["formula"] = d.Formula,
                ["baseComponent"] = d.BaseComponent, ["proRataOverride"] = (!d.IsPeriodAdjustment && d.ProRata).ToString().ToLowerInvariant(), ["roundingMode"] = "" };
            foreach (var pair in expected)
                if (!TransferFieldEqual(pair.Key, Text(line,pair.Key), pair.Value))
                    differences.Add($"{d.Code}: {pair.Key} differs");
        }
        return differences;
    }

    internal static IReadOnlyList<ExcelPayrollTransferDifference> TransferTemplateDetails(JsonObject candidate,IReadOnlyList<ResolvedComponent> proposed)
    {
        var lines=(candidate["lines"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var details=new List<ExcelPayrollTransferDifference>();
        foreach(var c in proposed) {
            var d=c.Definition; var line=lines.FirstOrDefault(l=>Text(l,"componentId")==c.Id);
            foreach(var field in new Dictionary<string,string> { ["calculationType"]=d.CalculationType,["formula"]=d.Formula,["value"]=d.Value,["baseComponent"]=d.BaseComponent,["proRataOverride"]=(!d.IsPeriodAdjustment && d.ProRata).ToString().ToLowerInvariant(),["roundingMode"]="" })
                details.Add(new(d.Code,field.Key,line is null ? "Not present" : Text(line,field.Key),field.Value));
        }
        foreach(var line in lines.Where(l=>!proposed.Any(c=>c.Id==Text(l,"componentId")) )) details.Add(new(Text(line,"componentId"),"component","Existing only","Not present in Excel-derived variant"));
        return details;
    }

    private static ExcelPayrollTransferExistingResult ExistingTransferResult(PlannedRow row,ExcelPayslipRow sourceRow,JsonObject template,IReadOnlyList<JsonObject>? masters,int clientId)
    {
        const string coverage="Existing template draft with saved salary inputs and source attendance; excludes effective Employee tax/PF/policy and approved adjustments. Not payroll parity.";
        if(masters is null) return new(row.RowId,Text(template,"id"),false,null,sourceRow.NetPay,null,"Existing draft simulation unavailable; configuration comparison only.",[]);
        try {
            var lines=(template["lines"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var components=lines.Select(l=> {
                var c=masters.Single(m=>Text(m,"id")==Text(l,"componentId"));
                return new PayrollReviewComponent(Text(c,"id"),Text(c,"code"),Text(c,"category"),Text(c,"componentRole"),Text(c,"statutoryType","None"),Text(c,"calculationType"),Text(c,"value"),Text(c,"formula"),Text(c,"baseComponent"),Text(c,"proRata")=="true",Text(c,"taxable")=="true",Text(c,"active")!="false",int.TryParse(Text(c,"priority"),out var priority)?priority:100,Text(c,"payType","Fixed Pay"));
            }).ToList();
            var result=PayRunRepository.SimulateTemplate(new PayrollSimulationRequest {
                Mode="Template",ClientId=clientId,TemplateId=Text(template,"id"),AnnualCtc=row.ExistingAnnualCtc,
                PayrollDays=row.Preview.PayrollDays,PresentDays=row.Preview.PresentDays,PayableDays=row.Preview.PayableDays,AmountOverrides=new(row.ExistingSalaryInputs),
                Snapshot=new("Template",clientId,"",row.ExistingAnnualCtc,components,lines.Select(l=>new PayrollReviewLine(Text(l,"componentId"),Text(l,"value"),Text(l,"formula"),Text(l,"calculationType"),Text(l,"baseComponent"),Text(l,"proRataOverride"))).ToList())
            });
            return new(row.RowId,Text(template,"id"),result.Complete,result.Net,sourceRow.NetPay,result.Net-sourceRow.NetPay,coverage,result.Lines.Select(l=>new ExcelPayrollSetupLine(l.Code,row.Preview.Lines.SingleOrDefault(s=>s.Code.Equals(l.Code,StringComparison.OrdinalIgnoreCase))?.SourceAmount,l.Earned,null)).ToList());
        } catch(Exception e) when(e is InvalidOperationException or FormatException) { return new(row.RowId,Text(template,"id"),false,null,sourceRow.NetPay,null,"Existing draft cannot be compared with these inputs. Review the current template/amount mappings; no existing configuration was changed.",[]); }
    }

    internal static void FinalizeTransferReview(Plan plan, ExcelPayrollSetupRequest request, int clientId, IReadOnlyList<JsonObject> templates,IReadOnlyList<JsonObject>? masters=null)
    {
        if(plan.Source.CaptureVersion>0 && plan.Source.DependencyReviewStatus!="Reviewed" && request.TransferReviewVersion!=1)
            plan.Preview.Issues.Add(new("Error","This source is reference only. Supply/map and review its exact original workbook dependencies, save a reviewed version, then refresh Transfer Batch."));
        if(plan.Source.CaptureVersion>0 && request.TransferReviewVersion!=1)plan.Preview.Issues.Add(new("Error","Captured workbooks must use the saved-batch Transfer review; legacy apply cannot bypass dependency/attendance proof."));
        if (request.TransferReviewVersion != 1) return;
        var transfer = new ExcelPayrollTransferReview { SourceMonth=plan.Batch.Month, SourceBatchId = plan.Batch.Id, SourceHash = Hash(JsonSerializer.Serialize(new { plan.Batch, plan.Source }, Json)), EffectiveFrom = request.EffectiveFrom };
        plan.Preview.Transfer = transfer;
        void Error(string message, string? row = null, string? code = null) => plan.Preview.Issues.Add(new("Error", message, row, code));

        transfer.Components = plan.Components.Select(c => new ExcelPayrollTransferComponent(c.Definition.Code,c.Definition.Category,c.IsNew ? "Create" : "Reuse unchanged",c.Id)).ToList();
        var variants = plan.Rows.Where(r => r.TemplateId.Length > 0).GroupBy(r => r.TemplateId).ToList();
        if(request.TemplateScopeReviewVersion==1 && !request.TemplateDecisions.All(d=>d.KeepExisting))foreach(var issue in TemplateScopeIssues(request,plan.Rows))Error(issue);
        if (request.TemplateDecisions.Any(d => !variants.Any(v => v.Key == d.VariantKey))) Error("A template decision belongs to an old variant. Clear it and preview the current rules.");
        foreach (var variant in variants)
        {
            var review = new ExcelPayrollTransferTemplate { VariantKey = variant.Key, RowIds = variant.Select(r => r.RowId).ToList() };
            transfer.Templates.Add(review);
            var decision = request.TemplateDecisions.SingleOrDefault(d => d.VariantKey == variant.Key);
            var rules = TransferRulesSignature(variant.First().Components);
            review.Candidates = templates.Where(t => RefId(Text(t,"clientId")) == clientId && Text(t,"active") != "false").Select(t => {
                var differences = TransferTemplateDifferences(t, variant.First().Components); var compatibility=CompareTemplateScope(Text(t,"id"),rules,request,clientId,plan.TemplateScopes,decision); return new ExcelPayrollTransferTemplateCandidate(Text(t,"id"),Text(t,"name"), differences.Count == 0 && compatibility.Compatible,differences,TransferTemplateDetails(t,variant.First().Components)) { RecordedScopes=plan.TemplateScopes.Where(record=>record.ClientId==clientId&&record.TemplateId==Text(t,"id")).ToList(), ConfigurationExact=differences.Count==0,ScopeStatus=compatibility.Status,ScopeReason=compatibility.Reason };
            }).ToList();
            foreach(var row in variant) {
                var current=templates.SingleOrDefault(t=>RefId(Text(t,"clientId"))==clientId && Text(t,"id")==row.ExistingTemplateId && Text(t,"active")!="false");
                if(current is not null) review.ExistingResults.Add(ExistingTransferResult(row,plan.Batch.Rows.Single(r=>r.Id==row.RowId),current,masters,clientId));
            }
            if (decision is null) { Error("Review reuse/create for each template variant; no existing client template will be duplicated automatically."); continue; }
            if (decision.KeepExisting)
            {
                if(variant.Any(row=>row.Employee.Id<=0 || string.IsNullOrEmpty(row.ExistingTemplateId) || !review.Candidates.Any(c=>c.Id==row.ExistingTemplateId)))
                { Error("Keep existing requires a verified matched employee and their active current client template. Resolve identity/current assignment first; nothing can be guessed for a new employee."); continue; }
                review.Action="Keep current assignments unchanged — Excel parity unresolved";
                foreach(var row in variant) {
                    row.KeepExisting=true; row.TemplateId=row.ExistingTemplateId; row.Employee.SalaryStructureId=row.ExistingTemplateId;
                    row.Preview.TemplateId=row.ExistingTemplateId; row.Preview.Action="Keep existing; no source transfer";
                    review.KeptAssignments.Add(new(row.RowId,row.Employee.Id,row.ExistingTemplateId,"Explicit keep choice; salary, attendance, adjustments and existing templates are not changed. Source mismatch is not approved as parity."));
                }
                continue;
            }
            if (decision.ExistingTemplateId.Length > 0)
            {
                if (!review.Candidates.Any(c => c.Id == decision.ExistingTemplateId && c.Exact)) { Error("The chosen template is unavailable, another client's, or differs from the reviewed component rules. Refresh comparison; shared configuration is not overwritten."); continue; }
                review.Action = "Reuse unchanged"; review.TemplateId = decision.ExistingTemplateId; plan.ReusedTemplateIds.Add(review.TemplateId);
                foreach (var row in variant) { row.TemplateId = review.TemplateId; row.Employee.SalaryStructureId = review.TemplateId; row.Preview.TemplateId = review.TemplateId; }
            }
            else if (decision.CreateVariant)
            {
                if (review.Candidates.Any(c => c.Exact)) { Error("An exact existing template is available. Reuse it explicitly instead of creating a duplicate."); continue; }
                if(request.TemplateScopeReviewVersion==1 && review.Candidates.Any(c=>c.ConfigurationExact && c.ScopeStatus is "Unknown legacy scope" or "Extension review required") && (decision.ScopeAction!="DifferentScope" || decision.ScopeReason.Trim().Length<10)) { Error("An existing template has the same rules but unresolved applicability. Review explicit reuse/extension, or explain the distinct scope before creating another variant.");continue; }
                var name = request.TemplateName.Trim() + (variants.Count > 1 ? $" / {transfer.Templates.Count}" : "");
                if (templates.Any(t => Text(t,"id") == variant.Key || RefId(Text(t,"clientId")) == clientId && string.Equals(Text(t,"name"),name,StringComparison.OrdinalIgnoreCase)))
                    Error("The proposed template name or identifier is already in use. Review another name or an exact reuse candidate.");
                review.Action = "Create reviewed variant"; review.TemplateId = variant.Key; review.Name = name;
            }
            else Error("Choose explicit template reuse or reviewed variant creation.");
            if(review.TemplateId.Length>0 && request.TemplateScopeReviewVersion==1 && request.TemplateScope is {} reviewedScope)review.Applicability=new(clientId,review.TemplateId,rules,request.EffectiveFrom,ScopePolicyKey(reviewedScope,rules),JsonSerializer.Deserialize<ExcelPayrollTemplateScope>(JsonSerializer.Serialize(reviewedScope,Json),Json)!,plan.Batch.Id,plan.Batch.Month);
        }
        plan.Preview.TemplateIds=plan.Rows.Where(r=>r.TemplateId.Length>0).Select(r=>r.TemplateId).Distinct().ToList();
        plan.Preview.TemplateId=plan.Preview.TemplateIds.FirstOrDefault()??plan.Preview.TemplateId;
        if(plan.Source.CaptureVersion>0 && plan.Source.DependencyReviewStatus!="Reviewed" && (plan.Rows.Count==0 || plan.Rows.Any(r=>!r.KeepExisting)))Error("This source is reference only. Resolve dependencies in a reviewed saved batch before source transfer; verified keep-only decisions preserve existing assignments.");
        FinalizeAttendanceProof(plan,request,clientId,transfer.SourceHash);
        if (plan.Rows.Any(r=>!r.KeepExisting) && !request.ReviewedPolicyScope) Error("Review effective-period policy assumptions. Transfer prepares templates; it does not certify or activate statutory policies.");
        foreach (var row in plan.Rows)
        {
            var sourceRow = plan.Batch.Rows.Single(r => r.Id == row.RowId);
            var rowProofs = new List<ExcelPayrollTransferFormula>();
            foreach (var component in row.Components)
            {
                var proof = InspectTransferFormula(request, plan.Source, sourceRow, component.Definition); transfer.Formulas.Add(proof); rowProofs.Add(proof);
                if (proof.Status == "Blocked") plan.Preview.Issues.Add(new(row.KeepExisting ? "Review" : "Error",proof.Reason + (row.KeepExisting ? " Existing configuration is kept; this source rule was not transferred." : ""),row.RowId,component.Definition.Code));
            }
            var amountReview = ClassifyTransferAmounts(plan, row, request, transfer.SourceHash, rowProofs);
            transfer.AmountComparisons.Add(amountReview);
            if (amountReview.Status == "Explained rounding")
            {
                amountReview.Acknowledged = request.RoundingReviews.GetValueOrDefault(row.RowId) == amountReview.ProofHash;
                plan.Preview.Issues.Add(new(row.KeepExisting || amountReview.Acknowledged ? "Review" : "Error",
                    amountReview.Acknowledged ? "Reviewed deterministic source/core rounding is recorded. Raw/display differences remain visible; Employee payroll parity is not certified."
                        : "Review the deterministic rounding proof for this saved row, then repreview. No blanket currency tolerance or financial-result override is used.", row.RowId, "Rounding review"));
            }
            else if (amountReview.Status != "Exact at raw precision")
                plan.Preview.Issues.Add(new(row.KeepExisting ? "Review" : "Error", "Excel comparison is unresolved. " + amountReview.Reason + (row.KeepExisting ? " Existing assignments are kept unchanged; no source transfer was approved." : ""), row.RowId, "Amount comparison"));
        }
        foreach (var column in plan.Source.Columns.Where(c => request.SourceColumnRoles.GetValueOrDefault(c.ColumnIndex,c.Kind) is "earning" or "deduction" or "employer"))
            if (plan.Rows.Any(r=>!r.KeepExisting) && !request.Components.Any(c => c.ExpectedColumnIndex == column.ColumnIndex)) Error("A classified source financial output has no component comparison mapping. Review every output before transfer.");
        plan.Preview.TemplateIds = plan.Rows.Where(r => r.TemplateId.Length > 0).Select(r => r.TemplateId).Distinct().ToList();
        plan.Preview.TemplateId = plan.Preview.TemplateIds.FirstOrDefault() ?? "";
        transfer.PreparationStatus = plan.Preview.CanApply ? plan.Rows.All(r=>r.KeepExisting) ? "Ready to record keep decisions — no source transfer; parity unresolved" : "Ready for reviewed configuration Apply — payroll parity not verified" : "Blocked";
    }
}
