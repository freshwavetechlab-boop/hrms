using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    // A new observation of an already-Applied source, never an upgrade/rewrite of
    // its historical receipt. The caller must read the Applied receipt and raw
    // source under its own transaction and bind their immutable fingerprints.
    internal sealed record AppliedSourceRowReview(int PayrollDays, decimal PayableDays,
        decimal? SourcePresentDays, string SourceActualWorkedMeaning, string SourceHash,
        string MappingFingerprint, string IdentityFingerprint, string FormulaFingerprint,
        ExcelPayrollSetupPreviewRow Arithmetic, IReadOnlyList<SelectedSourceFinancialLine> Lines,
        IReadOnlyList<ResolvedComponent> Components);

    internal static AppliedSourceRowReview ReviewAppliedSourceRow(int clientId, int employeeId,
        ExcelPayslipBatch batch, ExcelPayslipCalculationSource source, ExcelPayrollSetupRequest original,
        ExcelPayrollSetupResult receipt, string setupJson, Employee currentEmployee,
        IReadOnlyList<Employee> identitySnapshot, string? nativeDetailsJson = null, NativePayrollAttendanceInput? nativeInputs = null)
    {
        try
        {
            return ReviewAppliedSourceRowCore(clientId, employeeId, batch, source, original, receipt,
                setupJson, currentEmployee, identitySnapshot, nativeDetailsJson, nativeInputs);
        }
        catch (SelectedPayRunConflictException) { throw; }
        catch (Exception error) when (error is InvalidOperationException or JsonException or FormatException
            or OverflowException or ArgumentException or NullReferenceException)
        {
            throw AppliedSourceConflict("Saved source, mapping or current configuration is invalid; refresh the source review.");
        }
    }

    private static SelectedPayRunConflictException AppliedSourceConflict(string reason) => new(reason);

    private static AppliedSourceRowReview ReviewAppliedSourceRowCore(int clientId, int employeeId,
        ExcelPayslipBatch batch, ExcelPayslipCalculationSource source, ExcelPayrollSetupRequest original,
        ExcelPayrollSetupResult receipt, string setupJson, Employee currentEmployee,
        IReadOnlyList<Employee> identitySnapshot, string? nativeDetailsJson, NativePayrollAttendanceInput? nativeInputs)
    {
        if (batch is null || source is null || original is null || receipt is null || currentEmployee is null
            || identitySnapshot is null || clientId <= 0 || employeeId <= 0 || batch.ClientId != clientId
            || currentEmployee.Id != employeeId || currentEmployee.ClientId != clientId
            || !Guid.TryParseExact(batch.Id, "N", out _) || original.BatchId != batch.Id
            || !Guid.TryParseExact(receipt.ImportId, "N", out _)
            || !DateTime.TryParseExact(batch.Month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw AppliedSourceConflict("Review one explicit existing employee and the same saved client/batch/period receipt.");
        if (batch.Rows is null || batch.Rows.Any(r => r is null) || source.Rows is null || source.Rows.Any(r => r is null)
            || source.Columns is null || source.Columns.Any(c => c is null || c.Label is null || c.SourceHeader is null || c.Kind is null)
            || source.ColumnCount is < 1 or > 512 || source.Columns.Select(c => c.ColumnIndex).Distinct().Count() != source.Columns.Count
            || source.Columns.Any(c => c.ColumnIndex < 0 || c.ColumnIndex >= source.ColumnCount)
            || receipt.EmployeeIds is null || receipt.KeptRows is null || receipt.KeptRows.Any(r => r is null)
            || receipt.TransferRows is null || receipt.TransferRows.Any(r => r is null)
            || original.EmployeeDecisions is null || original.EmployeeDecisions.Any(d => d is null)
            || currentEmployee.PersonalDetails is null || currentEmployee.PaymentDetails is null || currentEmployee.SalaryComponents is null
            || identitySnapshot.Count == 0 || identitySnapshot.Any(e => e is null || e.Id <= 0 || e.ClientId != clientId
                || e.PersonalDetails is null || e.PaymentDetails is null)
            || identitySnapshot.Select(e => e.Id).Distinct().Count() != identitySnapshot.Count)
            throw AppliedSourceConflict("Saved source, receipt or current identity metadata is missing or ambiguous.");
        ValidateRequest(original);
        if (original.LocationAllocations is not null || receipt.LocationAllocations is not null
            || original.TransferReviewVersion is not (0 or 1))
            throw AppliedSourceConflict("This existing-source review supports one employee/source row; location allocations require their complete reviewed pathway.");

        var decisions = original.EmployeeDecisions.Where(d => d.MatchedEmployeeId == employeeId).ToArray();
        if (decisions.Length != 1 || decisions[0].AllowCreate || original.RowIds.Count(id => id == decisions[0].RowId) != 1
            || receipt.EmployeeIds.Count(id => id == employeeId) != 1 || receipt.KeptRows.Any(r => r.RowId == decisions[0].RowId))
            throw AppliedSourceConflict("The original explicit employee decision and receipt membership do not prove one transferred source row.");
        var rows = batch.Rows.Where(r => r.Id == decisions[0].RowId).ToArray();
        if (rows.Length != 1 || !Guid.TryParse(rows[0].Id, out _) || rows[0].SourceRow <= 0
            || batch.Rows.Count(r => r.SourceRow == rows[0].SourceRow) != 1)
            throw AppliedSourceConflict("The selected saved source row is missing or duplicated.");
        var row = rows[0];
        var cellRows = source.Rows.Where(r => r.SourceRow == row.SourceRow).ToArray();
        if (cellRows.Length != 1 || cellRows[0].Cells is null || cellRows[0].Cells.Count > source.ColumnCount
            || cellRows[0].Cells.Any(c => c is not null && c.Value is null)
            || row.Information is null || row.Information.Any(i => i is null || i.Label is null || i.Value is null)
            || row.Earnings is null || row.Deductions is null || row.EmployerContributions is null
            || row.Earnings.Concat(row.Deductions).Concat(row.EmployerContributions).Any(a => a is null || string.IsNullOrWhiteSpace(a.Label) || a.Amount < 0)
            || string.IsNullOrWhiteSpace(row.EmployeeCode) || string.IsNullOrWhiteSpace(row.EmployeeName))
            throw AppliedSourceConflict("The preserved row identity, financial values or captured cells are missing or malformed.");

        // Review the saved worksheet's financial rules. Native period-input
        // mappings below are separately identified, not a claim that missing
        // original workbook bytes or lookup closure have been reconstructed.
        if (source.CaptureVersion != 0 || source.DependencyReviewStatus is not ("" or "Legacy" or "Reviewed")
            || source.WorkbookFiles is not { Count: 0 } || source.DependencySheets is not { Count: 0 }
            || source.ExternalMappings is not { Count: 0 })
            throw AppliedSourceConflict("This source requires retained workbook/dependency provenance review; it cannot be inferred from a legacy receipt.");
        var validationBatch = new ExcelPayslipBatch { Id = batch.Id, ClientId = clientId, Month = batch.Month,
            SheetName = batch.SheetName, HeaderRow = batch.HeaderRow, Rows = [row] };
        if (ExcelPayslipCalculationService.ValidateSource(source, validationBatch) is not null)
            throw AppliedSourceConflict("The selected preserved worksheet row does not match its saved identity, columns, values or source limits.");
        var sourceHash = ExcelPayslipRepository.EmploymentSourceHash(batch, source);
        if (original.TransferReviewVersion == 1)
        {
            var bindings = receipt.TransferRows.Where(r => r.RowId == row.Id).ToArray();
            if (receipt.Transfer is not { Version: 1 } transfer || transfer.SourceBatchId != batch.Id
                || transfer.SourceMonth != batch.Month || transfer.SourceHash != sourceHash
                || bindings.Length != 1 || bindings[0].EmployeeId != employeeId)
                throw AppliedSourceConflict("The original TransferV1 evidence changed or is incomplete; this review cannot waive it.");
        }
        else if (receipt.Transfer is not null || receipt.TransferRows.Count != 0)
            throw AppliedSourceConflict("The legacy receipt contains mixed transfer versions; resolve its provenance without rewriting history.");

        var identities = identitySnapshot.Select(e => new Identity { Id = e.Id, ClientId = e.ClientId,
            EmployeeCode = e.EmployeeCode, Name = (e.FirstName + " " + e.LastName).Trim(), IsActive = e.IsActive,
            Pan = e.PersonalDetails.PanNumber, Aadhaar = e.PersonalDetails.AadhaarNumber, Uan = e.PersonalDetails.UanNumber }).ToArray();
        var metadata = SourceInformation(row, source, original.SourceWorkLocationColumnIndex);
        var identityIssues = new List<ExcelPayrollSetupIssue>();
        var matched = MatchIdentity(row, metadata, identities, decisions[0], identityIssues);
        if (matched?.Id != employeeId || identityIssues.Any(i => i.Severity == "Error")
            || !SalaryOnlyBankReviewRows(batch, source, identities).Contains(row.Id))
            throw AppliedSourceConflict("Source identity is not uniquely anchored to the explicitly reviewed existing employee; no name-only match is accepted.");
        var candidate = NewEmployee(clientId, row, original, metadata);
        candidate.Id = employeeId;
        var checks = EmployeeRepository.ExcelPayrollIdentityRows([candidate], identitySnapshot);
        if (FullIdentityIssues([(row.Id, employeeId)], checks).Any(i => i.Severity == "Error"))
            throw AppliedSourceConflict("Current full employee identity preflight conflicts with this saved source row.");
        var snapshotEmployee = identitySnapshot.SingleOrDefault(e => e.Id == employeeId);
        if (snapshotEmployee is null || currentEmployee.EmployeeCode != snapshotEmployee.EmployeeCode
            || currentEmployee.FirstName != snapshotEmployee.FirstName || currentEmployee.LastName != snapshotEmployee.LastName
            || !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(currentEmployee.PersonalDetails, Json), JsonSerializer.SerializeToNode(snapshotEmployee.PersonalDetails, Json))
            || !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(currentEmployee.PaymentDetails, Json), JsonSerializer.SerializeToNode(snapshotEmployee.PaymentDetails, Json)))
            throw AppliedSourceConflict("The current employee and loaded identity snapshot differ; refresh them in one transaction.");

        var settings = JsonNode.Parse(setupJson) as JsonObject ?? throw AppliedSourceConflict("The current salary setup is unavailable.");
        if (settings["salaryComponents"] is not JsonArray masterNodes || masterNodes.Any(n => n is not JsonObject)
            || settings["salaryStructures"] is not JsonArray templateNodes || templateNodes.Any(n => n is not JsonObject))
            throw AppliedSourceConflict("Current salary component/template metadata is malformed.");
        var masters = masterNodes.OfType<JsonObject>().ToArray();
        var templates = templateNodes.OfType<JsonObject>().Where(t => Text(t, "id") == currentEmployee.SalaryStructureId).ToArray();
        if (string.IsNullOrWhiteSpace(currentEmployee.SalaryStructureId) || templates.Length != 1
            || Text(templates[0], "active") != "true"
            || !Text(templates[0], "clientId").Split([';', ',', '|']).Any(v => v.Split(':')[0].Trim() == clientId.ToString(CultureInfo.InvariantCulture)))
            throw AppliedSourceConflict("The employee has no explicit active salary template for this client.");
        var resolved = new List<ResolvedComponent>();
        foreach (var definition in original.Components)
        {
            var matches = !string.IsNullOrWhiteSpace(definition.ExistingComponentId)
                ? masters.Where(m => Text(m, "id") == definition.ExistingComponentId).ToArray()
                : masters.Where(m => string.Equals(Text(m, "code"), definition.Code, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
                throw AppliedSourceConflict($"{definition.Code}: the current component master is missing or ambiguous; no shared master is changed.");
            resolved.Add(new(definition, Text(matches[0], "id"), false));
        }
        if (resolved.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != resolved.Count)
            throw AppliedSourceConflict("Several source components resolve to one current component master.");
        var components = ComponentsForRow(resolved, row.Id);
        var currentSourceAcknowledgment = original.TransferReviewVersion==0 && nativeInputs is not null && nativeDetailsJson is not null;
        if(currentSourceAcknowledgment)components=AppliedCurrentTemplateRules(templates[0],components);
        else if (TransferTemplateDifferences(templates[0], components).Count != 0)
            throw AppliedSourceConflict("The current assigned template differs from this source row's rules, proration or component coverage.");
        // This is a new current-source review. The historical receipt remains
        // immutable; its original column/classification mapping is retained,
        // while corrected effective template rules must match source formulas.
        var reviewedRequest=JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(JsonSerializer.Serialize(original,Json),Json)!;
        if(currentSourceAcknowledgment)reviewedRequest.ReviewedFixedInputs=true;
        if(currentSourceAcknowledgment)
            foreach(var component in components.Where(c=>c.Definition.CalculationType=="Formula"&&c.Definition.SourceColumnIndex is null))
                if(!string.IsNullOrWhiteSpace(cellRows[0].Cells[component.Definition.ExpectedColumnIndex!.Value]?.FormulaText)
                    &&currentEmployee.SalaryComponents.Keys.Any(key=>key==component.Id||key.Equals(component.Definition.Code,StringComparison.OrdinalIgnoreCase)))
                    throw AppliedSourceConflict($"{component.Definition.Code}: an old stored amount overrides this recurring formula. Apply the reviewed source salary inputs without freezing its current zero/output before payroll.");
        var compatibleMetadata = ReviewAppliedComponentMetadata(masters, components, nativeDetailsJson);
        ValidateSharedPayrollDaysBinding(original, source, [row], batch.Rows);
        var arithmetic = CalculateRow(clientId, reviewedRequest, components, cellRows[0], row,
            source.Columns.Where(c => c.BlankAsZero).Select(c => c.ColumnIndex).ToHashSet());
        if (arithmetic.Issues.Any(i => i.Severity == "Error") || arithmetic.Preview.Net is null
            || currentEmployee.AnnualCtc != arithmetic.Preview.AnnualCtc
            || arithmetic.Inputs.Any(p => !currentEmployee.SalaryComponents.TryGetValue(p.Key, out var actual) || actual != p.Value))
            throw AppliedSourceConflict("Source arithmetic or current saved salary inputs differ; source expected amounts cannot replace native inputs.");

        var inputBoundary = ReviewAppliedNativeDayInputs(clientId, employeeId, batch, source, row,
            original, arithmetic.Preview, nativeInputs);
        var remappedLookup = inputBoundary.Keys.Any(address => source.Rows.Single(r => r.SourceRow == row.SourceRow)
            .Cells[TransferColumn(Regex.Match(address, @"^[A-Z]+").Value)]?.FormulaText is { Length: > 0 });

        var lines = new List<SelectedSourceFinancialLine>();
        var formulaProofs = new List<ExcelPayrollTransferFormula>();
        var recalculatedOutputs = new Dictionary<string,decimal>(StringComparer.OrdinalIgnoreCase);
        var mappedColumns = new HashSet<int>();
        foreach (var component in components)
        {
            var definition = component.Definition;
            if (definition.ExpectedColumnIndex is not int column || !mappedColumns.Add(column))
                throw AppliedSourceConflict("Every financial component requires one distinct preserved output column.");
            var columns = source.Columns.Where(c => c.ColumnIndex == column).ToArray();
            var role = definition.Category switch { "Earning" or "Reimbursement" => "earning", "Deduction" => "deduction", "Benefit" => "employer", _ => "" };
            if (columns.Length != 1 || role.Length == 0 || original.SourceColumnRoles.GetValueOrDefault(column, columns[0].Kind) != role)
                throw AppliedSourceConflict("A financial output role differs from its explicit earning/deduction/employer classification.");
            var financial = definition.Category switch { "Earning" or "Reimbursement" => row.Earnings, "Deduction" => row.Deductions, _ => row.EmployerContributions };
            var amounts = financial.Where(a => Normalize(a.Label) == Normalize(columns[0].Label)).ToArray();
            var raw = ReadAppliedCapturedNumber(source, cellRows[0], column);
            if (amounts.Length != 1 || amounts[0].Amount != raw)
                throw AppliedSourceConflict("The saved financial line and preserved source output do not agree exactly.");
            var recalculated = RequireAppliedCapturedValue(source, row.SourceRow, column, raw, inputBoundary);
            recalculatedOutputs[TransferLetter(column)+row.SourceRow] = recalculated;
            var formula = InspectTransferFormula(reviewedRequest, source, row, definition);
            if (definition.IsPeriodAdjustment)
            {
                var cell = cellRows[0].Cells[column]!;
                if (definition.SourceColumnIndex != column || definition.Category == "Benefit"
                    || !string.IsNullOrWhiteSpace(cell.FormulaText) || !string.IsNullOrWhiteSpace(definition.Formula)
                    || definition.RowFormulas.Count != 0 || definition.ProRata || !string.IsNullOrWhiteSpace(definition.BaseComponent)
                    || !decimal.TryParse(definition.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var placeholder) || placeholder != 0)
                    throw AppliedSourceConflict("An existing period component must be an explicit literal amount; formulas cannot become frozen adjustments.");
                formula = formula with { Status = "Reviewed existing period literal", Reason = "Original one-period classification retained; no recurring rule or new adjustment is inferred." };
            }
            else if (formula.Status is not ("Exact supported translation" or "Reviewed fixed source"))
                throw AppliedSourceConflict($"{definition.Code}: {formula.Reason} Review this component's source-to-native rule before creating payroll.");
            formulaProofs.Add(formula);
            var calculated = arithmetic.Preview.Lines.Single(l => l.Code.Equals(definition.Code, StringComparison.OrdinalIgnoreCase));
            var formulaStatus = compatibleMetadata.TryGetValue(definition.Code, out var metadataNote)
                ? formula.Status + "; " + metadataNote : formula.Status;
            if(currentSourceAcknowledgment && !original.ReviewedFixedInputs && formula.Status=="Reviewed fixed source")
                formulaStatus += "; current fixed-input semantics require source-review acknowledgment; historical receipt unchanged";
            if (remappedLookup && !definition.IsPeriodAdjustment)
                formulaStatus += "; verified native day-input mapping; original lookup closure not certified";
            if (recalculated != raw) formulaStatus += "; source arithmetic agrees at existing 15-significant-digit precision";
            lines.Add(new(definition.Code, definition.Category, raw, SourceCurrencyDisplay(raw), calculated.CalculatedAmount,
                calculated.UnroundedEarned, formulaStatus){ComponentRole=definition.ComponentRole,StatutoryType=definition.StatutoryType,
                Taxable=definition.Taxable,ProRata=definition.ProRata,SourceFormula=cellRows[0].Cells[column]?.FormulaText??"",
                NativeFormula=definition.RowFormulas.GetValueOrDefault(row.Id,definition.Formula),
                SourceColumnIndex=column,SourceLabel=columns[0].Label,SourceHeader=columns[0].SourceHeader});
        }
        var financialColumns = source.Columns.Where(c => original.SourceColumnRoles.GetValueOrDefault(c.ColumnIndex, c.Kind) is "earning" or "deduction" or "employer").Select(c => c.ColumnIndex).ToHashSet();
        if (!financialColumns.SetEquals(mappedColumns) || row.Earnings.Count != lines.Count(l => l.Category is "Earning" or "Reimbursement")
            || row.Deductions.Count != lines.Count(l => l.Category == "Deduction") || row.EmployerContributions.Count != lines.Count(l => l.Category == "Benefit"))
            throw AppliedSourceConflict("The complete saved financial row is not covered by unique source component mappings.");
        var gross = row.Earnings.Sum(a => a.Amount); var deductions = row.Deductions.Sum(a => a.Amount);
        if (row.DeclaredGross is decimal declaredGross && declaredGross != gross
            || row.DeclaredDeductions is decimal declaredDeductions && declaredDeductions != deductions
            || row.NetPay != Math.Max(0, gross - deductions))
            throw AppliedSourceConflict("Saved gross/deduction/net totals do not prove the exact raw financial aggregate.");
        foreach (var total in new[] { (Role: "netPay", Amount: row.NetPay), (Role: "grossTotal", Amount: gross), (Role: "deductionTotal", Amount: deductions) })
        {
            var columns = source.Columns.Where(c => original.SourceColumnRoles.GetValueOrDefault(c.ColumnIndex, c.Kind) == total.Role).ToArray();
            if (columns.Length > 1 || total.Role == "netPay" && columns.Length != 1)
                throw AppliedSourceConflict("A unique preserved source net output and unambiguous total columns are required.");
            foreach (var column in columns) RequireAppliedCapturedValue(source, row.SourceRow, column.ColumnIndex, total.Amount, inputBoundary);
        }
        foreach (var column in new[] { original.FixedPayrollDays.HasValue ? (int?)null : original.PayrollDaysColumnIndex, original.PayableDaysColumnIndex, original.PresentDaysColumnIndex }.Where(c => c.HasValue).Select(c => c!.Value).Distinct())
            RequireAppliedCapturedValue(source, row.SourceRow, column, ReadNumber(cellRows[0], column), inputBoundary);
        decimal? sourcePresent = null;
        if (original.PresentDaysColumnIndex is int presentColumn)
        {
            if (original.AttendanceReview is not { Reviewed: true, PresentMeaning: "ActualWorked" })
                throw AppliedSourceConflict("A source present column needs explicit original actual-worked meaning; payable fallback is not proof.");
            sourcePresent = ReadNumber(cellRows[0], presentColumn);
        }
        else if (original.Components.Any(c => new[] { c.Formula }.Concat(c.RowFormulas.Values)
            .Any(f => System.Text.RegularExpressions.Regex.IsMatch(f, @"\bPRESENT_DAYS\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))))
            throw AppliedSourceConflict("A presence-dependent formula has no reviewed actual-worked source column.");
        var mappingHash = Hash(JsonSerializer.Serialize(new { Version = "applied-source-mapping-v1", clientId, employeeId, original, receipt.ImportId,
            RowId = row.Id, row.SourceRow, currentEmployee.SalaryStructureId, currentEmployee.AnnualCtc, currentEmployee.SalaryComponents,
            Template = templates[0], Masters = components.Select(c => masters.Single(m => Text(m, "id") == c.Id)), Rules = TransferRulesSignature(components) }, Json));
        var identityHash = Hash(JsonSerializer.Serialize(new { Version = "applied-source-identity-v1", clientId, employeeId, row.Id, row.EmployeeCode,
            metadata, Decision = decisions[0], Current = identitySnapshot.OrderBy(e => e.Id).Select(e => new { e.Id, e.ClientId, e.EmployeeCode,
                e.FirstName, e.LastName, e.IsActive, e.WorkEmail, e.PersonalDetails, e.PaymentDetails }), Checks = checks }, Json));
        var formulaHash = Hash(JsonSerializer.Serialize(new { Version = "applied-source-formula-v1", sourceHash, mappingHash,
            Formulas = formulaProofs, Lines = lines, Gross = gross, Deductions = deductions, row.NetPay,
            arithmetic.Preview.PayrollDays, arithmetic.Preview.PayableDays, SourcePresent = sourcePresent,
            InputBoundaryVersion = "native-period-inputs-v1", InputBoundary = inputBoundary, NativeInputs = nativeInputs,
            CurrentSourceAcknowledgmentVersion = currentSourceAcknowledgment?1:0,
            RecalculatedOutputs = recalculatedOutputs }, Json));
        return new(arithmetic.Preview.PayrollDays, arithmetic.Preview.PayableDays, sourcePresent,
            sourcePresent.HasValue ? "ActualWorked" : "NotMapped", sourceHash, mappingHash, identityHash, formulaHash,
            arithmetic.Preview, lines.AsReadOnly(), components);
    }

    internal static IReadOnlyList<ResolvedComponent> AppliedCurrentTemplateRules(JsonObject template,IReadOnlyList<ResolvedComponent> source)
    {
        var lines=(template["lines"] as JsonArray)?.OfType<JsonObject>().ToArray()??[];
        if(lines.Length!=source.Count||lines.Select(l=>Text(l,"componentId")).Distinct(StringComparer.Ordinal).Count()!=lines.Length
            ||!lines.Select(l=>Text(l,"componentId")).ToHashSet(StringComparer.Ordinal).SetEquals(source.Select(c=>c.Id)))
            throw AppliedSourceConflict("The current assigned template must cover each mapped source component exactly once.");
        return source.Select(component=>
        {
            var line=lines.Single(l=>Text(l,"componentId")==component.Id);
            var definition=JsonSerializer.Deserialize<ExcelPayrollSetupComponent>(JsonSerializer.Serialize(component.Definition,Json),Json)!;
            var type=Text(line,"calculationType");
            if(type!=(definition.IsPeriodAdjustment?"Manual / Variable":definition.CalculationType)
                ||!bool.TryParse(Text(line,"proRataOverride"),out var proRata)||Text(line,"roundingMode")!="")
                throw AppliedSourceConflict("Current calculation kind, explicit proration or rounding needs a separately reviewed source mapping.");
            definition.Value=Text(line,"value");definition.Formula=Text(line,"formula");definition.BaseComponent=Text(line,"baseComponent");
            definition.ProRata=proRata;definition.RowFormulas.Clear();
            definition.ExemptWhenSourceZero=false;
            if(definition.IsPeriodAdjustment&&(definition.ProRata||definition.Formula.Length!=0||definition.Value!="0"||definition.BaseComponent.Length!=0))
                throw AppliedSourceConflict("An existing period adjustment must retain its zero nonrecurring template placeholder.");
            return component with{Definition=definition};
        }).ToArray();
    }

    // Apply must still insist that shared metadata can actually be persisted.
    // This separate read-only lane instead proves the already assigned template.
    // Explicit component dependencies are topologically sorted by the regular
    // calculator; running-total/slab/residual expressions cannot commute.
    internal static Dictionary<string,string> ReviewAppliedComponentMetadata(IReadOnlyList<JsonObject> masters,
        IReadOnlyList<ResolvedComponent> components, string? nativeDetailsJson)
    {
        var notes = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<(ResolvedComponent Component, JsonObject Master, IReadOnlyList<string> Differences)>();
        foreach (var component in components)
        {
            var master = masters.Single(m => Text(m, "id") == component.Id);
            var differences = ReusedComponentDifferences(master, component.Definition);
            if (differences.Any(d => d is not ("Priority" or "PayType")))
                throw AppliedSourceConflict($"{component.Definition.Code}: current {string.Join(", ", differences)} differs from the source mapping; no shared master is changed.");
            if (differences.Count > 0) pending.Add((component, master, differences));
        }
        if (pending.Count == 0) return notes;

        // A full real Employee calculation is required, not a caller flag or a
        // template-only amount match. Dated PF can introduce implicit wage
        // dependencies, so its priority changes are deliberately not waived.
        var independentRules = components.All(c => AppliedRuleHasIndependentOrder(c.Definition));
        var realNativeProof = false;
        if (nativeDetailsJson is not null)
        {
            using var native = JsonDocument.Parse(nativeDetailsJson);
            realNativeProof = native.RootElement.ValueKind == JsonValueKind.Array
                && !PayRunRepository.HasPfPolicySnapshot(nativeDetailsJson);
        }
        foreach (var (component, master, differences) in pending)
        {
            var definition = component.Definition;
            if (!int.TryParse(Text(master, "priority"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var priority)
                || Text(master, "payType") is not ("Fixed Pay" or "Variable Pay"))
                throw AppliedSourceConflict($"{definition.Code}: current priority/pay type is malformed.");
            if (!realNativeProof || !independentRules
                || differences.Contains("PayType") && definition.Category is not ("Deduction" or "Benefit"))
                throw AppliedSourceConflict($"{definition.Code}: current {string.Join(", ", differences)} needs effective-rule review. Running totals, relevant earning pay types or dated PF cannot be approved by matching amounts.");
            notes[definition.Code] = "Effective template rules verified; "
                + (differences.Contains("Priority") ? $"priority {definition.Priority} to {priority} has no running-total dependency" : "")
                + (differences.Contains("PayType") ? "; non-earning pay type does not enter fixed-earnings aggregates" : "");
        }
        return notes;
    }

    private static bool AppliedRuleHasIndependentOrder(ExcelPayrollSetupComponent definition)
    {
        if (definition.CalculationType is not ("Fixed Amount" or "Formula" or "Manual / Variable")) return false;
        var formulas = definition.RowFormulas.Values.Append(definition.Formula).Append(definition.BaseComponent);
        var orderTokens = PayrollValueCatalogue.SystemValues.Where(v => v.Source == "Earlier components").Select(v => v.Token);
        return formulas.All(formula => !Regex.IsMatch(formula, @"\bSUM\s*\(", RegexOptions.IgnoreCase)
            && orderTokens.All(token => !Regex.IsMatch(formula, @"\b" + Regex.Escape(token) + @"\b", RegexOptions.IgnoreCase)));
    }

    // An already transferred day column is a period input, not a recurring
    // lookup constant. Re-evaluate financial formulas relative to independently
    // recorded, same-employee/month native days; never bind financial outputs.
    internal static IReadOnlyDictionary<string,decimal> ReviewAppliedNativeDayInputs(int clientId, int employeeId,
        ExcelPayslipBatch batch, ExcelPayslipCalculationSource source, ExcelPayslipRow row,
        ExcelPayrollSetupRequest original, ExcelPayrollSetupPreviewRow arithmetic, NativePayrollAttendanceInput? native)
    {
        var bindings = new Dictionary<string,decimal>(StringComparer.OrdinalIgnoreCase);
        if (native is null) return bindings;
        var cycle = PayRunRepository.NativeAttendanceCycle(batch.Month, original.AttendanceCycleStartDay, original.AttendanceCycleEndDay);
        if (batch.ClientId != clientId || native.EmployeeId != employeeId || native.ClientId != clientId || native.Month != batch.Month
            || native.CycleStart != cycle.Start || native.CycleEnd != cycle.End || !string.IsNullOrEmpty(native.Error)
            || !Regex.IsMatch(native.SnapshotHash ?? "", @"^[a-fA-F0-9]{64}$")
            || native.PayrollDays != arithmetic.PayrollDays || native.PayableDays != arithmetic.PayableDays)
            throw AppliedSourceConflict("Mapped day inputs need an unchanged same-employee/client/month/cycle native attendance snapshot.");
        var columns = new List<(int Column,decimal Value)> { (original.PayableDaysColumnIndex,native.PayableDays) };
        if (!original.FixedPayrollDays.HasValue) columns.Add((original.PayrollDaysColumnIndex,native.PayrollDays!.Value));
        if (original.PresentDaysColumnIndex is int present)
        {
            if (original.AttendanceReview is not { Reviewed: true, PresentMeaning: "ActualWorked" } || !native.PresentDays.HasValue)
                throw AppliedSourceConflict("Only explicitly mapped actual presence can bind PRESENT_DAYS; payable is not presence.");
            columns.Add((present,native.PresentDays.Value));
        }
        if (original.AttendanceReview is { Reviewed: true, LwpColumnIndex: int lwp } review)
        {
            if (review.LwpBasis != "PayrollDivisorMinusPayable" || review.SourceMonth != batch.Month
                || review.SourceHash != ExcelPayslipRepository.EmploymentSourceHash(batch,source)
                || review.BoundSourceInputs != AttendanceBinding(original))
                throw AppliedSourceConflict("LWP requires its explicit reviewed divisor-minus-payable basis; it cannot be inferred from a label.");
            columns.Add((lwp,Math.Max(0,native.PayrollDays!.Value-native.PayableDays)));
        }
        if (columns.Select(c => c.Column).Distinct().Count() != columns.Count)
            throw AppliedSourceConflict("Native day roles must use distinct source columns.");
        var captured = source.Rows.Single(r => r.SourceRow == row.SourceRow);
        foreach (var (column,value) in columns)
        {
            var metadata = source.Columns.Where(c => c.ColumnIndex == column).ToArray();
            var cell = captured.Cells.ElementAtOrDefault(column);
            if (column < 0 || metadata.Length != 1 || original.SourceColumnRoles.GetValueOrDefault(column,metadata[0].Kind) is not ("info" or "ignore")
                || original.Components.Any(c => c.ExpectedColumnIndex == column || c.SourceColumnIndex == column)
                || cell is null || cell.MissingCachedValue || cell.Error is not null || !string.IsNullOrEmpty(cell.CalculationError)
                || !decimal.TryParse(cell.Value,NumberStyles.Number,CultureInfo.InvariantCulture,out var cached) || cached != value)
                throw AppliedSourceConflict("A mapped native day input is missing, erroneous, financial, or different from its preserved source value.");
            if (!string.IsNullOrWhiteSpace(cell.FormulaText))
            {
                // Closed arithmetic still has to reproduce its cache. A native
                // mapping only terminates an exact cross-sheet lookup input;
                // it must not hide stale arithmetic, approximate matching, or
                // an arbitrary fallback that could equal a plausible day count.
                try
                {
                    var evaluated=ExcelPayslipCalculationService.EvaluateCapturedCellAtInputBoundary(source,TransferLetter(column)+row.SourceRow,bindings,true);
                    if(evaluated.Value!=value || evaluated.Traces.Any(t=>t.Status!="Matched" || t.MatchingRows!=1
                        || t.SelectedRow is null || t.MatchMode.StartsWith("Approximate",StringComparison.Ordinal) || t.MatchMode=="IFERROR"))
                        throw AppliedSourceConflict("Mapped day arithmetic or lookup results differ from native inputs.");
                }
                catch(SelectedPayRunConflictException){throw;}
                catch(InvalidOperationException)
                {
                    var formula=Regex.Replace(cell.FormulaText.TrimStart('='),@"\s","").ToUpperInvariant();
                    const string lookup=@"VLOOKUP\(\$?[A-Z]+\$?[1-9][0-9]*,(?:'[^']+'|[A-Z_][A-Z0-9_.-]*)!\$?[A-Z]+(?:\$?[1-9][0-9]*)?:\$?[A-Z]+(?:\$?[1-9][0-9]*)?,[1-9][0-9]*,(?:FALSE|0)\)";
                    var direct=Regex.IsMatch(formula,"^"+lookup+"$");
                    var zeroFallback=Regex.IsMatch(formula,"^IFERROR\\("+lookup+",0\\)$");
                    if(!direct && !(zeroFallback && value!=0))
                        throw AppliedSourceConflict("Unresolved day input needs exact lookup mapping; approximate, unknown or zero error-fallback inputs remain under review.");
                }
            }
            bindings.Add(TransferLetter(column)+row.SourceRow,value);
        }
        return bindings;
    }

    private static decimal ReadAppliedCapturedNumber(ExcelPayslipCalculationSource source, ExcelPayslipCalculationSourceRow row, int column)
    {
        var cell=row.Cells.ElementAtOrDefault(column);
        var blank=source.Columns.Single(c=>c.ColumnIndex==column).BlankAsZero&&cell is not null&&!cell.MissingCachedValue
            &&string.IsNullOrWhiteSpace(cell.Value)&&string.IsNullOrWhiteSpace(cell.FormulaText)
            &&cell.Error is null&&string.IsNullOrEmpty(cell.CalculationError);
        return ReadNumber(row,column,blank);
    }

    private static decimal RequireAppliedCapturedValue(ExcelPayslipCalculationSource source, int sourceRow, int column, decimal expected,
        IReadOnlyDictionary<string,decimal>? inputBoundary = null)
    {
        var rows = source.Rows.Where(r => r.SourceRow == sourceRow).ToArray();
        if (rows.Length != 1 || ReadAppliedCapturedNumber(source, rows[0], column) != expected)
            throw AppliedSourceConflict("The exact preserved output/day cache differs from its saved comparison value.");
        var evaluated = ExcelPayslipCalculationService.EvaluateCapturedCellAtInputBoundary(source, TransferLetter(column) + sourceRow, inputBoundary,
            requireExplicitCells: true, useReviewedBlankColumns: true);
        if (SourceDisplayPrecision(evaluated.Value) != SourceDisplayPrecision(expected) || evaluated.Traces.Any(t => t.MatchMode.StartsWith("Approximate", StringComparison.Ordinal)
            || t.MatchMode == "IFERROR" || t.SelectedRow is null || t.MatchingRows != 1 || t.Status != "Matched"))
            throw AppliedSourceConflict("Captured formulas, financial/day caches or exact lookup closure differ; no expected output is substituted.");
        return evaluated.Value;
    }
}
