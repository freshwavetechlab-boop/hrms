using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class PayRunRepository
{
    internal static PayrollSimulationResult SimulateLocationParts(PayrollLocationPartPeriod period, string setupJson,
        int payrollDays, decimal presentDays, decimal payableDays)
    {
        var result=new PayrollSimulationResult {Mode="Location parts template",ClientId=period.ClientId,EmployeeId=period.EmployeeId,PayPeriod=period.PayPeriod,LocationAllocations=period,
            Coverage="Actual Settings component/template formulas, reviewed location allocation inputs and one monthly post-processing pass. Existing personal deductions, approved adjustments and projected TDS still require Employee simulation after configuration."};
        if(payrollDays is <1 or >31||presentDays<0||payableDays<0||presentDays>payrollDays||payableDays>payrollDays)
            throw new InvalidOperationException("Review the native monthly payroll/present/payable totals for location allocations.");
        try
        {
            var first=period.Parts.FirstOrDefault()??throw new InvalidOperationException("Select at least one reviewed location allocation.");
            var primary=period.Parts.FirstOrDefault(part=>part.AssignmentId==period.ReferenceAssignmentId)??first;
            var employee=new PayRunSourceEmployee {Id=period.EmployeeId,ClientId=period.ClientId,EmployeeCode="Reviewed source employee",FirstName="Reviewed source employee",
                SalaryStructureId=primary.SalaryStructureId,AnnualCtc=primary.AnnualCtc,WorkLocationId=primary.WorkLocationId,WorkState=primary.WorkState,
                SalaryComponents=new(primary.SalaryInputs),SalaryJson=JsonSerializer.Serialize(primary.SalaryInputs),LocationParts=period};
            var row=BuildEmployee(0,employee,setupJson,period.PayPeriod,payrollDays,presentDays,payableDays,[],0,0,-1,false);
            CalculateConfiguredSalary(employee,ReadPayrollSetup(setupJson),payrollDays,presentDays,payableDays,result.Lines);
            result.Gross=row.GrossPay+row.OneTimeEarnings;result.Deductions=row.StatutoryDeductions+row.OneTimeDeductions;result.Net=row.NetPay;
            result.Benefits=result.Lines.Where(line=>line.Category=="Benefit").Sum(line=>line.Earned);
        }
        catch(Exception error) when(error is InvalidOperationException or FormatException or OverflowException)
        {result.Lines.Clear();result.Issues.Add(new("Error","Location allocation",error.Message,"Review the saved Settings templates, monthly/source-part scopes and native location attendance inputs."));}
        return result;
    }

    private sealed record LocationPartCalculation(PayrollLocationPart Part, List<CalculatedPayrollComponent> Salary,
        List<CalculatedPayrollComponent> Projection, List<PayrollSimulationLine> Trace);

    private static PayRunSourceEmployee LocationPartEmployee(PayRunSourceEmployee employee, PayrollLocationPart part) => new()
    {
        Id=employee.Id,ClientId=employee.ClientId,EmployeeCode=employee.EmployeeCode,FirstName=employee.FirstName,LastName=employee.LastName,
        SalaryStructureId=part.SalaryStructureId,AnnualCtc=part.AnnualCtc,WorkLocationId=part.WorkLocationId,WorkState=part.WorkState,
        SalaryComponents=new(part.SalaryInputs,StringComparer.OrdinalIgnoreCase),SalaryJson=JsonSerializer.Serialize(part.SalaryInputs)
    };

    private static string LocationAssignmentSignature(PayrollLocationPart part) => JsonSerializer.Serialize(new
    {
        part.WorkLocationId,part.WorkState,part.SalaryStructureId,part.AnnualCtc,part.PayrollDays,
        inputs=part.SalaryInputs.OrderBy(pair=>pair.Key,StringComparer.Ordinal),scopes=part.ComponentScopes.OrderBy(pair=>pair.Key,StringComparer.Ordinal)
    });

    private static decimal LocationEarned(CalculatedPayrollComponent line, PayrollLocationPart part) =>
        line.EarnedOverride ?? (line.DailyRate is decimal rate ? decimal.Round(rate*part.PayableDays,2)
            : line.Component.ProRata ? decimal.Round(line.Monthly*part.PayableDays/part.PayrollDays,2) : line.Monthly);

    private static List<CalculatedPayrollComponent> CalculateLocationPartSalary(PayRunSourceEmployee employee, PayrollSetupData setup,
        int payrollDays, decimal presentDays, decimal payableDays, List<PayrollSimulationLine>? trace)
    {
        var period=employee.LocationParts!;
        if(payrollDays is <1 or >31||presentDays<0||payableDays<0||presentDays>payrollDays||payableDays>payrollDays)
            throw new InvalidOperationException("Review the native monthly payroll/present/payable totals for location allocations.");
        if(period.Version!=1||period.ClientId!=employee.ClientId||period.EmployeeId!=employee.Id
            ||!Regex.IsMatch(period.PayPeriod??"",@"^\d{4}-(0[1-9]|1[0-2])$")||period.Parts is null||period.Parts.Count is <1 or >1000)
            throw new InvalidOperationException("Review the exact employee/client/period location allocation scope.");
        if(employee.SimulationAnnualCtc.HasValue)
            throw new InvalidOperationException("Review salary inputs per location assignment; an aggregate annual-target override cannot redistribute location allocations.");
        if(employee.PfPolicyPeriod is not null)
            throw new InvalidOperationException("Published PF policy and location allocations need an explicit combined monthly policy review. Source-part formulas cannot bypass or multiply an existing published policy.");
        if(period.Parts.Any(part=>part is null||string.IsNullOrWhiteSpace(part.PartKey)||string.IsNullOrWhiteSpace(part.AssignmentId)
            ||part.WorkLocationId<=0||string.IsNullOrWhiteSpace(part.SalaryStructureId)||part.PayrollDays is <1 or >31
            ||part.PresentDays<0||part.PayableDays<0||part.PresentDays>part.PayrollDays||part.PayableDays>part.PayrollDays
            ||part.SalaryInputs is null||part.ComponentScopes is null||part.AnnualCtc<0)
            ||period.Parts.Select(part=>part.PartKey).Distinct(StringComparer.Ordinal).Count()!=period.Parts.Count)
            throw new InvalidOperationException("Each location allocation needs a distinct part key, reviewed assignment, salary inputs and valid payroll/present/payable days.");
        if(period.Parts.Sum(part=>part.PresentDays)!=presentDays||period.Parts.Sum(part=>part.PayableDays)!=payableDays)
            throw new InvalidOperationException("Location allocations do not equal the native monthly present/payable totals. Reconcile the saved attendance inputs; no difference is distributed automatically.");
        if(period.Parts.GroupBy(part=>part.AssignmentId,StringComparer.Ordinal).Any(group=>group.Select(LocationAssignmentSignature).Distinct(StringComparer.Ordinal).Count()!=1))
            throw new InvalidOperationException("The same location assignment ID has conflicting configuration. Review immutable assignment versions.");

        var calculated=new List<LocationPartCalculation>();
        foreach(var part in period.Parts)
        {
            var structure=setup.Structures.SingleOrDefault(item=>item.Id==part.SalaryStructureId&&item.Active
                &&item.ClientId.Split([';',',','|']).Any(value=>value.Split(':')[0].Trim()==employee.ClientId.ToString(CultureInfo.InvariantCulture)))
                ??throw new InvalidOperationException("A reviewed location salary template is missing, inactive or belongs to another client.");
            var components=structure.Lines.Select(line=>setup.Components.SingleOrDefault(component=>component.Id==line.ComponentId&&component.Active)
                ??throw new InvalidOperationException("A location salary component is unavailable.")).ToList();
            if(part.SalaryInputs.Keys.Any(id=>!components.Any(component=>component.Id==id&&NormalizeCalculationType(FirstText(structure.Lines.Single(line=>line.ComponentId==id).CalculationType,component.CalculationType)) is "Fixed Amount" or "Daily Rate")))
                throw new InvalidOperationException("Location salary inputs must be explicit recurring monthly amounts or daily rates, not copied formula outputs.");
            if(part.ComponentScopes.Keys.Any(id=>!components.Any(component=>component.Id==id)))
                throw new InvalidOperationException("A location component-scope binding no longer belongs to its salary template.");
            foreach(var component in components)
                if(!part.ComponentScopes.TryGetValue(component.Id,out var scope)||scope is not ("SourceFormulaPerPart" or "Monthly"))
                    throw new InvalidOperationException($"{component.Code}: explicitly review SourceFormulaPerPart or Monthly calculation scope.");
            var partTrace=new List<PayrollSimulationLine>();
            var salary=CalculateConfiguredSalary(LocationPartEmployee(employee,part),setup,part.PayrollDays,part.PresentDays,part.PayableDays,partTrace);
            var projection=CalculateConfiguredSalary(LocationPartEmployee(employee,part),setup,part.PayrollDays,part.PayrollDays,part.PayrollDays);
            foreach(var line in salary)
            {
                var formula=line.Component.Formula;
                if(line.Component.ProRata&&Regex.IsMatch(formula,@"\b(PAYABLE_DAYS|PRESENT_DAYS|LOP_DAYS|GROSS_EARNED)\b|_EARNED\b",RegexOptions.IgnoreCase))
                    throw new InvalidOperationException($"{line.Component.Code}: the formula already uses earned/attendance inputs. Review Pro-rata so location attendance is applied once.");
                if(part.ComponentScopes[line.Component.Id]=="SourceFormulaPerPart")
                    foreach(var token in FormulaReferences(formula))
                    {
                        var dependency=components.FirstOrDefault(component=>component.Id==BaseComponentToken(token)||component.Code.Equals(BaseComponentToken(token),StringComparison.OrdinalIgnoreCase));
                        if(dependency is not null&&part.ComponentScopes[dependency.Id]=="Monthly")
                            throw new InvalidOperationException($"{line.Component.Code}: a source-part formula depends on a Monthly component. Review its calculation stages before payroll.");
                    }
            }
            calculated.Add(new(part,salary,projection,partTrace));
        }

        LocationPartCalculation reference;
        if(!string.IsNullOrWhiteSpace(period.ReferenceAssignmentId))
            reference=calculated.FirstOrDefault(item=>item.Part.AssignmentId==period.ReferenceAssignmentId)
                ??throw new InvalidOperationException("The reviewed current reference assignment is absent from this payroll allocation.");
        else
        {
            string Signature(LocationPartCalculation item)=>JsonSerializer.Serialize(new
            {
                item.Part.WorkState,item.Part.AnnualCtc,item.Part.PayrollDays,
                inputs=item.Part.SalaryInputs.OrderBy(pair=>pair.Key,StringComparer.Ordinal),
                projection=item.Projection.Select(line=>new{line.Component,line.Monthly}).OrderBy(line=>line.Component.Id,StringComparer.Ordinal)
            });
            if(calculated.Select(Signature).Distinct(StringComparer.Ordinal).Count()!=1)
                throw new InvalidOperationException("Location rates or monthly projection bases differ. Explicitly review the current reference assignment; payroll will not choose the last source row or add full-month rates.");
            reference=calculated[0];
        }
        var grouped=calculated.SelectMany(item=>item.Salary.Select(line=>(Item:item,Line:line))).GroupBy(item=>item.Line.Component.Id,StringComparer.Ordinal).ToList();
        var results=new Dictionary<string,CalculatedPayrollComponent>(StringComparer.Ordinal);
        var monthly=new Dictionary<string,List<(LocationPartCalculation Item,CalculatedPayrollComponent Line)>>(StringComparer.Ordinal);
        foreach(var group in grouped)
        {
            var first=group.First().Line.Component;
            if(group.Any(item=>item.Line.Component.Code!=first.Code||item.Line.Component.Category!=first.Category||item.Line.Component.StatutoryType!=first.StatutoryType
                ||item.Line.Component.ComponentRole!=first.ComponentRole||item.Line.Component.Taxable!=first.Taxable))
                throw new InvalidOperationException("Location salary components have incompatible classifications; review their Settings templates.");
            var scopes=group.Select(item=>item.Item.Part.ComponentScopes[group.Key]).Distinct(StringComparer.Ordinal).ToArray();
            if(scopes.Length!=1)throw new InvalidOperationException($"{first.Code}: location assignments disagree on monthly versus source-part scope.");
            if(scopes[0]=="Monthly")
            {
                if(group.Any(item=>item.Line.Component!=first))throw new InvalidOperationException($"{first.Code}: Monthly formulas/settings differ across location templates. Review one explicit monthly rule.");
                monthly[group.Key]=group.ToList();continue;
            }
            var referenceLine=reference.Projection.FirstOrDefault(line=>line.Component.Id==group.Key);
            var parts=group.Select(item=>new PayrollLocationComponentPart(item.Item.Part.PartKey,item.Item.Part.AssignmentId,item.Item.Part.WorkLocationId,
                item.Item.Part.SalaryStructureId,item.Item.Part.PayrollDays,item.Item.Part.PresentDays,item.Item.Part.PayableDays,"SourceFormulaPerPart",
                item.Line.Component.Formula,item.Line.Monthly,LocationEarned(item.Line,item.Item.Part),
                item.Item.Trace.SingleOrDefault(line=>line.Code==item.Line.Component.Code)?.UnroundedEarned)).ToArray();
            results[group.Key]=new(first with{ProRata=false},referenceLine?.Monthly??0,EarnedOverride:parts.Sum(part=>part.Earned),LocationParts:parts);
        }
        var values=new Dictionary<string,decimal>(StringComparer.OrdinalIgnoreCase);
        foreach(var line in reference.Projection)
        {values[line.Component.Id]=values[line.Component.Code]=line.Monthly;values[line.Component.Id+"_MONTHLY"]=values[line.Component.Code+"_MONTHLY"]=line.Monthly;}
        void SetEarned(CalculatedPayrollComponent line)
        {values[line.Component.Id+"_EARNED"]=values[line.Component.Code+"_EARNED"]=line.EarnedOverride!.Value;}
        foreach(var line in results.Values)SetEarned(line);
        var visiting=new HashSet<string>(StringComparer.Ordinal);var visited=new HashSet<string>(StringComparer.Ordinal);
        void EvaluateMonthly(string id)
        {
            if(visited.Contains(id))return;if(!visiting.Add(id))throw new InvalidOperationException("Monthly location salary components contain a circular dependency.");
            var entry=monthly[id];var component=entry[0].Line.Component;
            foreach(var token in FormulaReferences(component.Formula))
            {
                var dependency=setup.Components.FirstOrDefault(item=>item.Id==BaseComponentToken(token)||item.Code.Equals(BaseComponentToken(token),StringComparison.OrdinalIgnoreCase));
                if(dependency is not null&&monthly.ContainsKey(dependency.Id))EvaluateMonthly(dependency.Id);
                if(dependency is not null&&!token.EndsWith("_EARNED",StringComparison.OrdinalIgnoreCase)&&!reference.Projection.Any(line=>line.Component.Id==dependency.Id))
                    throw new InvalidOperationException($"{component.Code}: raw monthly base {token} is unavailable in the reviewed reference assignment. Review an aggregate earned-base formula instead.");
                if(dependency is not null&&!token.EndsWith("_EARNED",StringComparison.OrdinalIgnoreCase))
                {
                    var bases=calculated.Select(item=>item.Projection.FirstOrDefault(line=>line.Component.Id==dependency.Id)?.Monthly).ToArray();
                    if(bases.Any(value=>!value.HasValue)||bases.Distinct().Count()!=1)
                        throw new InvalidOperationException($"{component.Code}: raw monthly base {token} differs across location assignments. Use an explicitly reviewed aggregate earned-base formula; a projection reference does not choose a statutory base.");
                }
                if(new[]{"CTC","ANNUAL_CTC","MONTHLY_CTC","GROSS","MONTHLY_GROSS","EARNINGS_BEFORE_THIS","FIXED_EARNINGS_BEFORE_THIS","FIXED_EARNINGS"}.Contains(token,StringComparer.OrdinalIgnoreCase)
                    &&calculated.Select(item=>JsonSerializer.Serialize(new{item.Part.AnnualCtc,Monthly=item.Projection.Where(line=>IsPayableEarningCategory(line.Component.Category)).Sum(line=>line.Monthly)})).Distinct().Count()!=1)
                    throw new InvalidOperationException($"{component.Code}: monthly target/sum inputs differ across location assignments. Review an aggregate earned-base rule.");
            }
            var earnings=results.Values.Where(line=>IsPayableEarningCategory(line.Component.Category)).ToArray();
            values["GROSS_EARNED"]=earnings.Sum(line=>line.EarnedOverride!.Value);
            values["EARNINGS_BEFORE_THIS"]=earnings.Sum(line=>line.Monthly);
            values["FIXED_EARNINGS_BEFORE_THIS"]=earnings.Where(line=>line.Component.PayType=="Fixed Pay").Sum(line=>line.Monthly);
            values["FIXED_EARNINGS"]=values["FIXED_EARNINGS_BEFORE_THIS"];
            var target=reference.Part.AnnualCtc/12m;
            var type=NormalizeCalculationType(component.CalculationType);
            if(type=="Fixed Amount"&&entry.Select(item=>item.Line.Monthly).Distinct().Count()!=1)
                throw new InvalidOperationException($"{component.Code}: monthly fixed inputs differ across location assignments. Review one explicit monthly amount.");
            var raw=type switch
            {
                "Formula"=>EvaluateComponentFormula(component,component.Formula,target,payrollDays,presentDays,payableDays,values),
                "Fixed Amount"=>reference.Part.SalaryInputs.TryGetValue(id,out var fixedAmount)?fixedAmount:decimal.TryParse(component.Formula,NumberStyles.Number,CultureInfo.InvariantCulture,out var literal)?literal:throw new InvalidOperationException($"{component.Code}: reviewed monthly fixed input is missing."),
                "Manual / Variable"=>0,
                _=>throw new InvalidOperationException($"{component.Code}: this calculation type needs an explicit reviewed monthly location-allocation rule.")
            };
            var monthlyAmount=Math.Max(0,decimal.Round(raw,2));
            var earned=component.ProRata?decimal.Round(monthlyAmount*payableDays/payrollDays,2):monthlyAmount;
            var parts=entry.Select(item=>new PayrollLocationComponentPart(item.Item.Part.PartKey,item.Item.Part.AssignmentId,item.Item.Part.WorkLocationId,
                item.Item.Part.SalaryStructureId,item.Item.Part.PayrollDays,item.Item.Part.PresentDays,item.Item.Part.PayableDays,"Monthly",component.Formula,item.Line.Monthly,0)).ToArray();
            results[id]=new(component with{ProRata=false},reference.Projection.FirstOrDefault(line=>line.Component.Id==id)?.Monthly??monthlyAmount,
                EarnedOverride:earned,LocationParts:parts);
            values[id]=values[component.Code]=monthlyAmount;values[id+"_MONTHLY"]=values[component.Code+"_MONTHLY"]=monthlyAmount;SetEarned(results[id]);
            visiting.Remove(id);visited.Add(id);
        }
        foreach(var id in monthly.Keys)EvaluateMonthly(id);
        var output=results.Values.OrderBy(line=>line.Component.Priority).ThenBy(line=>line.Component.Id,StringComparer.Ordinal).ToList();
        if(trace is not null)foreach(var line in output)
            trace.Add(new(line.Component.Code,line.Component.Name,line.Component.Category,line.Component.Formula,
                monthly.ContainsKey(line.Component.Id)?"Settings monthly rule evaluated once over reviewed location inputs":"Settings formulas evaluated per location part; earned amounts summed once",
                line.EarnedOverride!.Value,line.Monthly,line.EarnedOverride.Value,false,"Reviewed saved location attendance allocations",
                "Actual engine component rounding retained; no second attendance proration.",[]) {UnroundedEarned=line.LocationParts?.All(part=>part.UnroundedEarned.HasValue)==true ? line.LocationParts.Sum(part=>part.UnroundedEarned!.Value) : null});
        return output;
    }
}
