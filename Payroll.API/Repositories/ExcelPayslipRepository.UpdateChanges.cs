using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    private async Task<UpdatePlan> PrepareUpdatePlanAsync(MySqlConnection db, MySqlTransaction tx, UpdateContext context, ExcelPayslipUpdatePreviewRequest request)
    {
        if (request.Month != context.Raw.Month || !TextValid(request.SourceFileName, 240) || request.Rows is null || request.Rows.Count is < 1 or > 1000
            || request.Rows.Any(row => row is null || row.Values is null || row.Values.Count > 250 || !TextValid(row.EmployeeCode, 100)))
            throw new InvalidOperationException("Upload between 1 and 1000 employee-code rows for this saved salary month.");
        var plan = new UpdatePlan { BatchJson = context.BatchJson, SourceJson = context.SourceJson, Blockers = context.Blockers.ToList() };
        var inputCodes = request.Rows.GroupBy(row => row.EmployeeCode.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var savedCodes = context.Raw.Rows.GroupBy(row => UpdateCode(context, row).Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var inputRow in request.Rows)
        {
            var code = inputRow.EmployeeCode.Trim();
            savedCodes.TryGetValue(code, out var matches);
            if (inputCodes[code] != 1 || matches is null || matches.Length != 1)
            {
                var reason = inputCodes[code] != 1 ? "The uploaded employee code is duplicated." : matches is { Length: > 1 }
                    ? "This saved code has multiple source parts; use the existing individual review to avoid updating the wrong salary entry."
                    : "No unique employee code matches this saved batch. No employee will be created.";
                plan.Blockers.Add(code + ": " + reason); continue;
            }
            var row = matches[0];
            foreach (var (fieldId, rawValue) in inputRow.Values)
            {
                if (string.IsNullOrWhiteSpace(rawValue)) continue;
                if (!context.Fields.TryGetValue(fieldId, out var binding)) { plan.Blockers.Add(code + ": an uploaded field is not in this saved batch's update catalog."); continue; }
                var value = rawValue.Trim();
                var oldValue = UpdateFieldValue(context, row, binding);
                if (value == oldValue || binding.Field.Type == "number" && UpdateEquivalentNumber(value, oldValue)) continue;
                if (value.Length > 10000 || value.Any(ch => char.IsControl(ch) && ch is not ('\r' or '\n')))
                    throw new InvalidOperationException("An uploaded value exceeds its supported size or contains control characters.");
                var reason = !binding.Field.Editable ? binding.Field.Reason : "";
                var employeeId = context.MasterByRow.GetValueOrDefault(row.Id);
                if (binding.MasterKey is not null && employeeId <= 0)
                    reason = "This row has no verified existing Master link. Review its identity before changing Master details; no employee will be created.";
                if (binding.Field.Type == "number")
                {
                    if (!decimal.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var amount) || Math.Abs(amount) > MaximumAmount)
                        reason = "Enter an exact supported number. Blank cells are kept and identifiers must remain text.";
                    else value = amount.ToString(CultureInfo.InvariantCulture);
                    if (binding.Field.SourceColumnIndex is int column && !string.IsNullOrWhiteSpace(context.Source?.Rows.Single(r => r.SourceRow == row.SourceRow).Cells.ElementAtOrDefault(column)?.FormulaText))
                        reason = "This is a saved formula output. Change its plain input instead; the formula cannot be overwritten.";
                }
                var change = NewUpdateChange(context, row, binding.Field.Id, binding.Field.Label, binding.Field.Type, oldValue, value,
                    binding.Field.Destination, reason.Length == 0, false, reason.Length > 0 ? reason : binding.Field.Reason);
                plan.Changes.Add(change);
                if (!change.CanApply) continue;
                plan.Inputs.Add(new() { RowId = row.Id, FieldId = fieldId, Value = value, EmployeeId = employeeId, MasterKey = binding.MasterKey,
                    WriteSource = binding.Kind != "master" && !context.ImmutableSource && !(context.AppliedSalaryLinks && binding.MasterKey is not null) });
            }
        }
        foreach (var group in plan.Inputs.Where(input => input.MasterKey is not null).GroupBy(input => input.EmployeeId))
        {
            var changes = new Dictionary<string, string>(StringComparer.Ordinal);
            var conflict = false;
            foreach (var input in group)
                if (changes.TryGetValue(input.MasterKey!, out var previous) && previous != input.Value) conflict = true;
                else changes[input.MasterKey!] = input.Value;
            try
            {
                if (conflict) throw new InvalidOperationException("Two uploaded fields propose different values for the same Master field.");
                var normalized = await EmployeeRepository.ValidateReviewedExcelMasterFieldsAsync(db, tx, group.Key, context.Raw.ClientId, changes);
                plan.MasterChanges[group.Key] = normalized;
                foreach (var input in group)
                {
                    if (!normalized.TryGetValue(input.MasterKey!, out var value)) value = context.Masters[group.Key].GetValueOrDefault(input.MasterKey!, input.Value);
                    input.Value = value;
                    var publicChange = plan.Changes.Single(change => change.FieldId == input.FieldId && change.EmployeeCode == UpdateCode(context, context.Raw.Rows.Single(r => r.Id == input.RowId)));
                    publicChange.NewValue = value;
                    publicChange.Id = UpdateChangeId(input.RowId, publicChange);
                }
            }
            catch (InvalidOperationException error)
            {
                foreach (var input in group)
                {
                    var code = UpdateCode(context, context.Raw.Rows.Single(r => r.Id == input.RowId));
                    foreach (var change in plan.Changes.Where(change => change.EmployeeCode == code && change.FieldId == input.FieldId))
                    { change.CanApply = false; change.Reason = error.Message; }
                }
            }
        }
        foreach (var key in new[] { "email", "mobile", "pan", "aadhaar", "uan", "esic", "bankAccount" })
            foreach (var duplicate in plan.Inputs.Where(input => input.MasterKey == key)
                .GroupBy(input => UpdateIdentifierValue(key, input.Value), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Key.Length > 0 && group.Select(input => input.EmployeeId).Distinct().Count() > 1))
                foreach (var input in duplicate)
                {
                    var code = UpdateCode(context, context.Raw.Rows.Single(row => row.Id == input.RowId));
                    foreach (var change in plan.Changes.Where(change => change.EmployeeCode == code && change.FieldId == input.FieldId))
                    { change.CanApply = false; change.Reason = $"The same {key} value is proposed for more than one employee. Review their identities before applying it."; }
                }
        plan.Changes = plan.Changes.Where(change => !change.CanApply || change.OldValue != change.NewValue
            && (change.Type != "number" || !UpdateEquivalentNumber(change.OldValue, change.NewValue))).ToList();
        plan.Inputs = plan.Inputs.Where(input => plan.Changes.Any(change => change.CanApply && change.FieldId == input.FieldId
            && change.EmployeeCode == UpdateCode(context, context.Raw.Rows.Single(row => row.Id == input.RowId)))).ToList();
        plan.MasterChanges = plan.Inputs.Where(input => input.MasterKey is not null).GroupBy(input => input.EmployeeId)
            .ToDictionary(group => group.Key, group => group.GroupBy(input => input.MasterKey!).ToDictionary(fields => fields.Key, fields => fields.First().Value, StringComparer.Ordinal));
        if (plan.Blockers.Count == 0 && plan.Inputs.Count > 0)
            PatchReviewedUpdate(context, plan);
        return plan;
    }

    private static ExcelPayslipUpdateChange NewUpdateChange(UpdateContext context, ExcelPayslipRow row, string fieldId, string label, string type,
        string oldValue, string value, string destination, bool canApply, bool computed, string reason)
    {
        var result = new ExcelPayslipUpdateChange { EmployeeCode = UpdateCode(context, row), EmployeeName = UpdateName(context, row), FieldId = fieldId,
            Label = label, Type = type, OldValue = oldValue, NewValue = value, Destination = destination, CanApply = canApply, Computed = computed, Reason = reason };
        result.Id = UpdateChangeId(row.Id, result); return result;
    }
    private static string UpdateChangeId(string rowId, ExcelPayslipUpdateChange change) => CorrectionHash(new { rowId, change.FieldId, change.OldValue,
        change.NewValue, change.Destination, change.Computed });

    private static void PatchReviewedUpdate(UpdateContext context, UpdatePlan plan)
    {
        var batchDocument = JsonNode.Parse(context.BatchJson)!.AsObject();
        var sourceDocument = string.IsNullOrWhiteSpace(context.SourceJson) ? null : JsonNode.Parse(context.SourceJson)!.AsObject();
        var batchRows = batchDocument["rows"]!.AsArray().ToDictionary(node => node!["id"]!.GetValue<string>(), node => node!.AsObject(), StringComparer.Ordinal);
        var sourceRows = sourceDocument?["rows"]?.AsArray().ToDictionary(node => node!["sourceRow"]!.GetValue<int>(), node => node!.AsObject());
        var numeric = new List<ExcelPayslipCellCorrection>();
        foreach (var input in plan.Inputs.Where(input => input.WriteSource))
        {
            var field = context.Fields[input.FieldId]; var row = context.Raw.Rows.Single(r => r.Id == input.RowId);
            if (field.Field.Type == "number" && field.Field.SourceColumnIndex is int numericColumn && context.Source is not null)
            { numeric.Add(new() { SourceRow = row.SourceRow, ColumnIndex = numericColumn, Amount = decimal.Parse(input.Value, CultureInfo.InvariantCulture) }); continue; }
            SetUpdateRowValue(batchRows[input.RowId], field, input.Value);
            if (field.Field.SourceColumnIndex is int column && sourceRows is not null)
                SetUpdateSourceValue(sourceRows[row.SourceRow], column, input.Value, field.Field.Type != "number");
        }
        if (numeric.Count > 0)
        {
            var beforeNumericBatch = JsonSerializer.Deserialize<ExcelPayslipBatch>(batchDocument.ToJsonString(), JsonOptions)!;
            var beforeNumericSource = JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(sourceDocument!.ToJsonString(), JsonOptions)!;
            var calculated = ExcelPayslipCalculationService.RecalculateReviewedInputs(beforeNumericSource, beforeNumericBatch, numeric);
            var explicitCells = numeric.Select(edit => (edit.SourceRow, edit.ColumnIndex)).ToHashSet();
            foreach (var sourceRow in calculated.Rows)
            {
                if (sourceRows is null || !sourceRows.TryGetValue(sourceRow.SourceRow, out var sourceNode)) continue;
                var originalRow = beforeNumericSource.Rows.Single(r => r.SourceRow == sourceRow.SourceRow);
                for (var column = 0; column < sourceRow.Cells.Count; column++)
                {
                    var cell = sourceRow.Cells[column]; var original = originalRow.Cells.ElementAtOrDefault(column);
                    var explicitInput = explicitCells.Contains((sourceRow.SourceRow, column));
                    if (cell is null || original is null || cell.Value == original.Value && cell.Error == original.Error && cell.CalculationError == original.CalculationError
                        && (!explicitInput || cell.DataType == original.DataType)) continue;
                    SetUpdateSourceValue(sourceNode, column, cell.Value, false, cell, explicitInput);
                    var mapped = context.Fields.Values.SingleOrDefault(field => field.Field.SourceColumnIndex == column);
                    var row = context.Raw.Rows.SingleOrDefault(r => r.SourceRow == sourceRow.SourceRow);
                    if (mapped is not null && row is not null)
                    {
                        SetUpdateRowValue(batchRows[row.Id], mapped, cell.Value);
                        if (!explicitCells.Contains((sourceRow.SourceRow, column)))
                            plan.Changes.Add(NewUpdateChange(context, row, mapped.Field.Id, mapped.Field.Label, mapped.Field.Type,
                                original.Value, cell.Value, "Batch only", false, true, "Calculated from the selected plain inputs using the original saved formula. Included when you acknowledge this review."));
                    }
                    else if (!explicitCells.Contains((sourceRow.SourceRow, column)))
                        plan.Changes.Add(new() { Id = CorrectionHash(new { sourceRow.SourceRow, column, Before = original.Value, After = cell.Value }),
                            EmployeeName = "Worksheet totals", FieldId = $"source:{column}:row:{sourceRow.SourceRow}", Label = calculated.Columns.SingleOrDefault(c => c.ColumnIndex == column)?.Label ?? "Worksheet formula",
                            Type = "number", OldValue = original.Value, NewValue = cell.Value, Destination = "Batch only", Computed = true,
                            Reason = "Saved worksheet formula cache recalculated from the selected inputs." });
                }
            }
        }
        var changedRows = plan.Inputs.Select(input => input.RowId).ToHashSet(StringComparer.Ordinal);
        var typed = JsonSerializer.Deserialize<ExcelPayslipBatch>(batchDocument.ToJsonString(), JsonOptions)!;
        var typedSource = sourceDocument is null ? null : JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(sourceDocument.ToJsonString(), JsonOptions);
        var links = batchDocument["profileLinks"] as JsonArray;
        foreach (var rowId in changedRows)
        {
            if (!context.MasterByRow.TryGetValue(rowId, out var employeeId)) continue;
            var row = typed.Rows.Single(r => r.Id == rowId);
            links ??= new JsonArray();
            var existing = links.SingleOrDefault(node => node?["rowId"]?.GetValue<string>() == rowId)?.AsObject();
            if (existing is null) { existing = new JsonObject { ["rowId"] = rowId, ["sourceRow"] = row.SourceRow, ["employeeId"] = employeeId }; links.Add(existing); }
            if (existing["employeeId"]!.GetValue<int>() != employeeId) throw new InvalidOperationException("A reviewed employee binding changed. Refresh the preview.");
            existing["sourceIdentityHash"] = ExcelPayslipMasterSynchronization.ProfileIdentityHash(typed, row, typedSource);
        }
        if (links is not null && batchDocument["profileLinks"] is null) batchDocument["profileLinks"] = links;
        foreach (var row in typed.Rows.Where(row => changedRows.Contains(row.Id)))
            batchRows[row.Id]["warnings"] = JsonSerializer.SerializeToNode(RowWarnings(row), JsonOptions);
        if (typedSource is not null && (context.Source is null || ExcelPayslipCalculationService.ValidateSource(context.Source, context.Raw) is null)
            && ExcelPayslipCalculationService.ValidateSource(typedSource, typed) is { } validationError)
            throw new InvalidOperationException(validationError);
        plan.BatchJson = batchDocument.ToJsonString(); plan.SourceJson = sourceDocument?.ToJsonString() ?? "";
        if (System.Text.Encoding.UTF8.GetByteCount(plan.BatchJson) > MaximumBatchBytes || System.Text.Encoding.UTF8.GetByteCount(plan.SourceJson) > 16 * 1024 * 1024)
            throw new InvalidOperationException("The reviewed update exceeds this saved batch's supported storage size.");
    }

    private static void SetUpdateRowValue(JsonObject row, UpdateFieldBinding field, string value)
    {
        switch (field.Kind)
        {
            case "employeeName": row["employeeName"] = value; break;
            case "email": row["email"] = value; break;
            case "info": row["information"]!.AsArray()[field.Position]!["value"] = value; break;
            case "earning": row["earnings"]!.AsArray()[field.Position]!["amount"] = decimal.Parse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture); break;
            case "deduction": row["deductions"]!.AsArray()[field.Position]!["amount"] = decimal.Parse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture); break;
            case "employer": row["employerContributions"]!.AsArray()[field.Position]!["amount"] = decimal.Parse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture); break;
            case "netPay": row["netPay"] = decimal.Parse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture); break;
            case "grossTotal": row["declaredGross"] = decimal.Parse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture); break;
            case "deductionTotal": row["declaredDeductions"] = decimal.Parse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture); break;
        }
    }
    private static void SetUpdateSourceValue(JsonObject row, int column, string value, bool text, ExcelPayslipCalculationCell? calculated = null, bool numericInput = false)
    {
        var cells = row["cells"]!.AsArray(); while (cells.Count <= column) cells.Add(null);
        if (cells[column] is not JsonObject cell) { cell = new JsonObject(); cells[column] = cell; }
        cell["value"] = value; cell["error"] = calculated?.Error; cell["calculationError"] = calculated?.CalculationError;
        cell["missingCachedValue"] = false;
        if (numericInput) cell["dataType"] = "Number";
        else if (text) cell["dataType"] = "Text";
        // FormulaText, format/source metadata and unknown fields remain untouched.
    }
    private static bool UpdateEquivalentNumber(string first, string second) => decimal.TryParse(first, NumberStyles.Float | NumberStyles.AllowThousands,
        CultureInfo.InvariantCulture, out var left) && decimal.TryParse(second, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var right) && left == right;
    private static string UpdateIdentifierValue(string key, string value) => key == "mobile" ? AuthRepository.NormalizeEmployeeLoginPhone(value)
        : key == "email" ? value.Trim().ToLowerInvariant() : new string(value.Where(ch => !char.IsWhiteSpace(ch) && ch != '-').ToArray()).ToUpperInvariant();
}
