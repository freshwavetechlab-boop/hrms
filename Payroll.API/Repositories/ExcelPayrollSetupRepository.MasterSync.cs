using System.Text.Json.Nodes;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    // Legacy Applied receipts predate a salary fingerprint. Reconstruct their
    // reviewed inputs, never baseline the current Master at the first read.
    internal static bool MasterSalaryMatchesReceipt(Employee employee,string setupJson,ExcelPayslipBatch batch,
        ExcelPayslipCalculationSource? source,ExcelPayrollSetupRequest request,ExcelPayrollSetupResult result,string rowId,string? capturedHash=null,JsonObject? setupDocument=null)
    {
        if(source is null||result.Transfer is null)return false;
        try
        {
            var row=batch.Rows.Single(r=>r.Id==rowId);var cells=source.Rows.Single(r=>r.SourceRow==row.SourceRow);
            if(capturedHash is not null&&result.Transfer.SourceHash!=capturedHash)return false;
            var templates=result.Transfer.Templates.Where(t=>t.RowIds.Contains(rowId)).ToArray();
            if(templates.Length!=1||templates[0].TemplateId!=employee.SalaryStructureId)return false;
            var resolved=request.Components.Select(c=>new ResolvedComponent(c,result.Transfer.Components.Single(m=>m.Code.Equals(c.Code,StringComparison.OrdinalIgnoreCase)).MasterId,false)).ToList();
            var components=ComponentsForRow(resolved,rowId);
            var setup=setupDocument??JsonNode.Parse(setupJson)!;
            var template=(setup["salaryStructures"] as JsonArray)?.OfType<JsonObject>().SingleOrDefault(t=>Text(t,"id")==employee.SalaryStructureId);
            if(template is null||TransferTemplateDifferences(template,components,employeeInputOverridesFallback:true).Count>0)return false;
            ReviewAppliedComponentMetadata((setup["salaryComponents"] as JsonArray??[]).OfType<JsonObject>().ToList(),components,null);
            Dictionary<string,decimal> inputs;decimal annual;
            if(request.MonthlyCtcColumnIndex is int ctcColumn)
            {
                // This exact captured source was already proved on Apply. Read
                // its reviewed monthly inputs instead of re-evaluating the
                // entire workbook once per employee on every page refresh.
                annual=decimal.Round(ReadNumber(cells,ctcColumn)*12m,2,MidpointRounding.AwayFromZero);
                inputs=components.Where(c=>!c.Definition.IsPeriodAdjustment&&c.Definition.SourceColumnIndex.HasValue)
                    .ToDictionary(c=>c.Id,c=>ReadNumber(cells,c.Definition.SourceColumnIndex!.Value,c.Definition.BlankInputAsZero||source.Columns.Any(col=>col.ColumnIndex==c.Definition.SourceColumnIndex&&col.BlankAsZero)));
                var proof=result.TransferRows.Single(r=>r.RowId==rowId);
                foreach(var c in components.Where(c=>c.Definition.ExemptWhenSourceZero&&c.Definition.ExpectedColumnIndex.HasValue))
                    if(proof.PayableDays>0&&employee.SalaryComponents.GetValueOrDefault(c.Id,-1)==0
                        &&ReadNumber(cells,c.Definition.ExpectedColumnIndex!.Value,true)==0
                        &&proof.Lines.Single(l=>l.Code.Equals(c.Definition.Code,StringComparison.OrdinalIgnoreCase)).CalculatedAmount==0)inputs[c.Id]=0;
            }
            else
            {
                var arithmetic=CalculateRow(batch.ClientId,request,resolved,cells,row,source.Columns.Where(c=>c.BlankAsZero).Select(c=>c.ColumnIndex).ToHashSet(),source);
                if(arithmetic.Issues.Any(i=>i.Severity=="Error"))return false;
                annual=decimal.Round(arithmetic.Preview.AnnualCtc,2,MidpointRounding.AwayFromZero);inputs=arithmetic.Inputs;
            }
            return employee.AnnualCtc==annual
                &&inputs.Count==employee.SalaryComponents.Count&&inputs.All(p=>employee.SalaryComponents.TryGetValue(p.Key,out var value)&&value==decimal.Round(p.Value,4,MidpointRounding.AwayFromZero))
                &&employee.PersonalDetails.Tds==0&&employee.PersonalDetails.Recovery==0&&employee.PersonalDetails.EsicEmployee==0&&employee.PersonalDetails.PtLwfWorkmenComp==0;
        }
        catch(Exception error)when(error is InvalidOperationException or FormatException or ArgumentException or KeyNotFoundException){return false;}
    }
}
