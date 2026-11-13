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
    // Applied receipts are the durable employee link. Source identity validation
    // remains separate: an intentional Master rename must not detach this link.
    internal sealed record MasterSyncBinding(ExcelPayslipRow Row,ExcelPayslipMasterLink Link,
        ExcelPayrollSetupRequest Request,ExcelPayrollSetupResult Result,ExcelPayslipBatch Owner,ExcelPayslipCalculationSource? Source);

    private static async Task AnnotateCurrentMasterAsync(MySqlConnection db,MySqlTransaction tx,ExcelPayslipBatch batch,
        ExcelPayslipCalculationSource? source,IConfiguration? configuration)
    {
        if(!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='excel_payroll_setup_imports')",transaction:tx))return;
        var saved=(await db.QueryAsync<MasterSyncReceipt>(@"SELECT i.batch_id BatchId,i.updated_at UpdatedAt,CAST(i.request_json AS CHAR) RequestJson,CAST(i.result_json AS CHAR) ResultJson
FROM excel_payroll_setup_imports i JOIN excel_payslip_batches b ON b.id=i.batch_id AND b.client_id=i.client_id
WHERE i.client_id=@ClientId AND i.status='Applied' AND JSON_UNQUOTE(JSON_EXTRACT(b.batch_json,'$.month'))=@Month",new{batch.ClientId,batch.Month},tx)).OrderByDescending(i=>i.UpdatedAt).ToArray();
        var bindings=new Dictionary<string,List<MasterSyncBinding>>();
        foreach(var group in saved.GroupBy(item=>item.BatchId))
        {
            var json=await db.ExecuteScalarAsync<string>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@Id",new{batch.ClientId,Id=group.Key},tx);
            var owner=json is null?null:JsonSerializer.Deserialize<ExcelPayslipBatch>(json,JsonOptions);
            if(owner is null||owner.ClientId!=batch.ClientId||owner.Month!=batch.Month)continue;
            var ownedSource=group.Key==batch.Id?source:await ReadCalculationSourceAsync(db,batch.ClientId,group.Key,tx);
            foreach(var savedReview in group)
            {
                try
                {
                    var request=JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(savedReview.RequestJson,JsonOptions);
                    var result=JsonSerializer.Deserialize<ExcelPayrollSetupResult>(savedReview.ResultJson,JsonOptions);
                    if(request is null||result is null||request.BatchId!=owner.Id)continue;
                    foreach(var binding in MasterBindings(batch,source,owner,ownedSource,request,result))
                    {
                        if(!bindings.TryGetValue(binding.Row.Id,out var list))bindings[binding.Row.Id]=list=[];
                        list.Add(binding);
                    }
                }
                catch(JsonException){ /* A malformed receipt never establishes a link. */ }
            }
        }
        var selected=bindings.Values.Where(list=>list.Select(b=>b.Link.EmployeeId).Distinct().Count()==1).Select(list=>list[0]).ToArray();
        // Two source rows cannot silently become one current employee.
        selected=selected.Where(b=>selected.Count(other=>other.Link.EmployeeId==b.Link.EmployeeId)==1).ToArray();
        if(selected.Length==0)return;
        var ids=selected.Select(b=>b.Link.EmployeeId).ToArray();
        var employees=(await db.QueryAsync<EmploymentMaster>(@"SELECT e.*,COALESCE(w.Name,'') WorkLocation,COALESCE(w.City,'') LocationCity,
"+EmployeeEmploymentRules.EligibleInMonth("e","@Month")+@" Eligible FROM employees e LEFT JOIN worklocations w ON w.Id=e.WorkLocationId AND w.ClientId=e.ClientId
WHERE e.ClientId=@ClientId AND e.Id IN @Ids",new{batch.ClientId,batch.Month,Ids=ids},tx)).ToDictionary(e=>e.Id);
        await PayrollDataTableStore.ApplyEmployeeTablesAsync(db,employees.Values,tx);
        await EmployeeRepository.ApplyEmploymentStatusAsync(db,employees.Values.ToList(),tx);
        var setup=await PayrollDataTableStore.GetSetupJsonAsync(db,tx);
        var setupDocument=JsonNode.Parse(setup) as JsonObject??new();
        var changed=new List<(ExcelPayslipRow Row,ExcelPayslipMasterLink Link,Employee Employee)>();
        foreach(var binding in selected)
        {
            if(!employees.TryGetValue(binding.Link.EmployeeId,out var employee))continue;
            var row=binding.Row;
            row.MasterDetails=ExcelPayslipMasterSynchronization.Details(employee,employee.WorkLocation,true);
            row.MasterDetails.SalaryInputs=new(employee.SalaryComponents);
            if(binding.Source is not null)
            {
                void SourceInput(int column,decimal amount)
                {
                    foreach(var field in binding.Source.Columns.Where(c=>c.ColumnIndex==column))
                        foreach(var label in new[]{field.Label,field.SourceHeader})
                            row.MasterDetails.Fields["source:"+ExcelPayslipPresentation.SourceKey(label)]=amount.ToString(CultureInfo.InvariantCulture);
                }
                foreach(var definition in binding.Request.Components.Where(c=>c.SourceColumnIndex.HasValue))
                {
                    var component=binding.Result.Transfer?.Components.SingleOrDefault(c=>c.Code.Equals(definition.Code,StringComparison.OrdinalIgnoreCase));
                    if(component is not null&&employee.SalaryComponents.TryGetValue(component.MasterId,out var amount))SourceInput(definition.SourceColumnIndex!.Value,amount);
                }
                if(binding.Request.MonthlyCtcColumnIndex is int ctcColumn)SourceInput(ctcColumn,employee.AnnualCtc/12m);
            }
            // Raw source identity warnings do not invalidate a separately proven
            // Applied receipt. Eligibility always comes from the current master.
            row.Employment=new(employee.Id,employee.EmployeeCode,employee.EmploymentStatus,employee.LastWorkingDate,!employee.Eligible,false);
            var unchanged=MasterSalaryUnchanged(employee,setup,binding,setupDocument);
            if(!unchanged)changed.Add((row,binding.Link,employee));
        }
        if(changed.Count>0)
        {
            if(configuration is null)
                foreach(var (row,_,_) in changed)row.MasterDetails!.Salary=SyncReview("Current salary calculation is unavailable. Refresh using the current API before generating a payslip.");
            else await new PayRunRepository(configuration,new TaxEngineRepository(configuration)).RefreshExcelPayslipSalaryAsync(db,tx,batch,setup,changed);
        }
    }

    internal static ExcelPayslipCurrentSalary SyncReview(string message)=>new("Review required",message,[],[],[],null,null,null);

    internal static bool MasterSalaryUnchanged(Employee employee,string setup,MasterSyncBinding binding,JsonObject? setupDocument=null) =>
        binding.Link.SalaryHash.Length > 0 && binding.Link.SalaryHash == ExcelPayslipMasterSynchronization.SalaryHash(employee,setup,setupDocument)
        // A newly filled UAN or another profile-only correction can change the
        // broad historical fingerprint. Independently prove unchanged salary
        // configuration/inputs from its Applied receipt before recalculating.
        || ExcelPayrollSetupRepository.MasterSalaryMatchesReceipt(employee,setup,binding.Owner,binding.Source,binding.Request,binding.Result,binding.Link.RowId,binding.Link.SourceHash,setupDocument);

    internal static IReadOnlyList<MasterSyncBinding> MasterBindings(ExcelPayslipBatch batch,ExcelPayslipCalculationSource? source,
        ExcelPayslipBatch owner,ExcelPayslipCalculationSource? ownedSource,ExcelPayrollSetupRequest request,ExcelPayrollSetupResult result)
    {
        if(batch.ClientId!=owner.ClientId||batch.Month!=owner.Month||request.BatchId!=owner.Id)return [];
        var bindings=new List<MasterSyncBinding>();
        var sourceHash=EmploymentSourceHash(owner,ownedSource);
        var identities=batch.Rows.GroupBy(ExcelPayslipMasterSynchronization.IdentityHash).ToDictionary(g=>g.Key,g=>g.ToArray());
        foreach(var original in owner.Rows.Where(r=>request.RowIds.Contains(r.Id)&&!result.KeptRows.Any(k=>k.RowId==r.Id)))
        {
            var candidates=batch.Id==owner.Id?batch.Rows.Where(r=>r.Id==original.Id).ToArray()
                :(identities.GetValueOrDefault(ExcelPayslipMasterSynchronization.IdentityHash(original))??[])
                    .Where(r=>SameSourceWitness(source,r,ownedSource,original)).ToArray();
            if(candidates.Length!=1)continue;
            var links=result.MasterLinks.Where(l=>l.RowId==original.Id&&l.SourceRow==original.SourceRow&&l.IdentityHash==ExcelPayslipMasterSynchronization.IdentityHash(original)).ToArray();
            ExcelPayslipMasterLink? link=links.Length==1?links[0]:null;
            if(link is null&&result.MasterLinks.Count==0)
            {
                var rows=result.TransferRows.Where(r=>r.RowId==original.Id).ToArray();
                if(rows.Length!=1)continue;
                var proof=rows[0];
                link=new(original.Id,proof.EmployeeId,original.SourceRow,ExcelPayslipMasterSynchronization.IdentityHash(original),"",proof.PayrollDays,
                    request.PresentDaysColumnIndex.HasValue?proof.PresentDays:null,proof.PayableDays,request.ReviewedExtraDutyUnits);
            }
            if(link is null||link.EmployeeId<=0||result.EmployeeIds.Count(id=>id==link.EmployeeId)!=1)continue;
            var decision=request.EmployeeDecisions.Where(d=>d.RowId==original.Id).ToArray();
            if(decision.Length!=1||!(decision[0].MatchedEmployeeId==link.EmployeeId||decision[0].MatchedEmployeeId is null&&decision[0].AllowCreate))continue;
            link=link with {SourceBatchId=owner.Id,SourceHash=sourceHash,ImportId=result.ImportId};
            bindings.Add(new(candidates[0],link,request,result,owner,ownedSource));
        }
        return bindings;
    }

    private static bool SameSourceWitness(ExcelPayslipCalculationSource? left,ExcelPayslipRow row,ExcelPayslipCalculationSource? right,ExcelPayslipRow original)
    {
        if(left is null||right is null||left.SheetName!=right.SheetName||left.HeaderRow!=right.HeaderRow||left.ColumnCount!=right.ColumnCount)return false;
        var a=left.Rows.Where(r=>r.SourceRow==row.SourceRow).ToArray();var b=right.Rows.Where(r=>r.SourceRow==original.SourceRow).ToArray();
        if(a.Length!=1||b.Length!=1||a[0].Cells.Count!=b[0].Cells.Count)return false;
        return a[0].Cells.Zip(b[0].Cells).All(pair=>{
            var x=pair.First;var y=pair.Second;
            if(x is null||y is null)return x is null&&y is null;
            return x.FormulaText==y.FormulaText&&x.Error==y.Error&&x.CalculationError==y.CalculationError
                &&(x.Value==y.Value||decimal.TryParse(x.Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var v)
                    &&decimal.TryParse(y.Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var w)&&Math.Abs(v-w)<=0.00000001m);
        });
    }
    private sealed class MasterSyncReceipt {public string BatchId{get;set;}="";public DateTime UpdatedAt{get;set;}public string RequestJson{get;set;}="";public string ResultJson{get;set;}="";}
}
