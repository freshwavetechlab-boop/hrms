using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal const string BoundedConfigurationDifferenceStatus="Reviewed bounded configuration difference";

    internal static void ValidateConfigurationDifferenceReview(ExcelPayrollSetupRequest request)
    {
        if(request.ConfigurationDifferenceReview is not{} d)return;
        if(d.Version!=1||!d.Reviewed||!Regex.IsMatch(d.SourceHash??"",@"\A[a-f0-9]{64}\z")
            ||d.MaxAbsoluteDifference is <=0 or >1||string.IsNullOrWhiteSpace(d.Reason)||d.Reason.Trim().Length<10||d.Reason.Length>500
            ||request.TransferReviewVersion!=1||!request.ReviewedFixedInputs||!request.ReviewedPolicyScope
            ||request.NamedInputReview is not null||request.ReportedPeriodInputs is not null||request.LocationAllocations is not null)
            throw new InvalidOperationException("Configuration monetary differences need an explicit version-one reviewed source hash, reason and limit no greater than INR 1, complete fixed-input/policy review and the normal flat saved-source workflow. No payroll is certified.");
    }

    internal static void RequireConfigurationDifferenceReviewEnabled(IConfiguration configuration,ExcelPayrollSetupRequest request)
    {
        if(request.ConfigurationDifferenceReview is not null&&!configuration.GetValue<bool>("Payroll:ConfigurationAmountDifferenceReviewEnabled"))
            throw new InvalidOperationException("Explicit configuration monetary-difference review is disabled. The existing exact configuration workflow remains unchanged.");
    }

    internal static ExcelPayrollTransferAmountReview ReviewConfigurationAmountDifferences(Plan plan,PlannedRow planned,
        ExcelPayrollSetupRequest request,string sourceHash,IReadOnlyList<ExcelPayrollTransferFormula> formulas,
        ExcelPayrollTransferAmountReview ordinary)
    {
        if(request.ConfigurationDifferenceReview is not{} declaration||ordinary.Status is "Exact at raw precision" or "Explained rounding")return ordinary;
        ValidateConfigurationDifferenceReview(request);
        if(sourceHash!=declaration.SourceHash||sourceHash!=Hash(JsonSerializer.Serialize(new{plan.Batch,plan.Source},Json)))
            throw new InvalidOperationException("The configuration monetary declaration belongs to a different saved source. Refresh its review.");
        ExcelPayrollTransferAmountReview Stop(string reason){ordinary.Reason="Bounded configuration review is not available: "+reason;return ordinary;}
        var sourceRow=plan.Batch.Rows.SingleOrDefault(r=>r.Id==planned.RowId);
        var captured=plan.Source.Rows.SingleOrDefault(r=>r.SourceRow==planned.SourceRow);
        var definitions=planned.Components.Select(c=>c.Definition).ToArray();
        var outputs=plan.Source.Columns.Where(c=>c.Kind is "earning" or "deduction" or "employer").ToArray();
        var owner=request.EmployeeDecisions.SingleOrDefault(e=>e.RowId==planned.RowId);
        if(sourceRow is null||captured is null||sourceRow.SourceRow!=planned.SourceRow||request.BatchId!=plan.Batch.Id||!request.RowIds.Contains(planned.RowId)
            ||planned.Employee.Id<=0||planned.Employee.ClientId!=plan.Batch.ClientId||string.IsNullOrWhiteSpace(planned.TemplateId)
            ||owner?.MatchedEmployeeId!=planned.Employee.Id||owner.AllowCreate
            ||planned.KeepExisting||planned.Components.Any(c=>c.IsNew)||definitions.Length==0
            ||definitions.Length!=outputs.Length||definitions.Length!=formulas.Count
            ||definitions.Select(c=>c.Code.ToUpperInvariant()).Distinct().Count()!=definitions.Length
            ||definitions.Any(c=>c.ExpectedColumnIndex is null||c.CalculationType=="Residual / Balancing")
            ||definitions.Select(c=>c.ExpectedColumnIndex).Distinct().Count()!=definitions.Length
            ||!definitions.Select(c=>c.ExpectedColumnIndex!.Value).Order().SequenceEqual(outputs.Select(c=>c.ColumnIndex).Order())
            ||formulas.Any(f=>f.RowId!=planned.RowId||f.SourceRow!=planned.SourceRow
                ||!Regex.IsMatch(f.SourceHash??"",@"\A[a-f0-9]{64}\z")
                ||!definitions.Any(c=>c.Code==f.Code&&c.ExpectedColumnIndex==f.SourceColumn)
                ||f.Status!="Exact supported translation"&&!(request.ReviewedFixedInputs&&f.Status=="Reviewed fixed source"))
            ||formulas.Select(f=>f.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=definitions.Length)
            return Stop("complete existing uniquely mapped components, source ownership and supported formulas/reviewed literals are mandatory.");
        RowArithmetic fresh;
        try{fresh=CalculateRow(plan.Batch.ClientId,request,planned.Components,captured,sourceRow,
            plan.Source.Columns.Where(c=>c.BlankAsZero).Select(c=>c.ColumnIndex).ToHashSet(),plan.Source);}
        catch(InvalidOperationException){return Stop("the normal source calculator could not be independently rebuilt.");}
        var preview=planned.Preview;
        if(fresh.Preview.WhatIfSimulation is not{} scenario||scenario.Mode!="Template"
            ||fresh.Preview.Gross!=preview.Gross||fresh.Preview.Deductions!=preview.Deductions||fresh.Preview.Benefits!=preview.Benefits||fresh.Preview.Net!=preview.Net
            ||fresh.Preview.PayrollDays!=preview.PayrollDays||fresh.Preview.PresentDays!=preview.PresentDays||fresh.Preview.PayableDays!=preview.PayableDays
            ||fresh.Preview.AnnualCtc!=preview.AnnualCtc||fresh.Preview.Lines.Count!=preview.Lines.Count
            ||fresh.Preview.Lines.Any(l=>preview.Lines.SingleOrDefault(p=>p.Code==l.Code)is not{} old
                ||old.SourceAmount!=l.SourceAmount||old.CalculatedAmount!=l.CalculatedAmount||old.Variance!=l.Variance||old.UnroundedEarned!=l.UnroundedEarned))
            return Stop("the fresh normal calculation differs from this preview.");
        var native=PayRunRepository.SimulateTemplate(scenario);
        // The existing stateless Template simulator leaves result.ClientId at
        // zero. Scope comes from its freshly rebuilt request/snapshot; this is
        // deliberately not an authenticated Employee calculation claim.
        if(!native.Complete||native.Mode!="Template"||scenario.ClientId!=plan.Batch.ClientId||scenario.Snapshot?.ClientId!=plan.Batch.ClientId
            ||native.ClientId!=0&&native.ClientId!=plan.Batch.ClientId||native.Lines.Count!=definitions.Length)
            return Stop("complete independent Template arithmetic is required; a saved Employee trace is not inferred.");
        var ledger=new List<ExcelPayrollTransferAmountLine>();var limit=declaration.MaxAbsoluteDifference;
        bool Bounded(decimal delta)=>Math.Abs(delta)<=limit;
        foreach(var definition in definitions)
        {
            var line=preview.Lines.SingleOrDefault(l=>l.Code==definition.Code);
            var actual=native.Lines.SingleOrDefault(l=>l.Code==definition.Code);
            var output=outputs.Single(c=>c.ColumnIndex==definition.ExpectedColumnIndex);
            var role=definition.Category switch{"Earning" or "Reimbursement"=>"earning","Deduction"=>"deduction","Benefit"=>"employer",_=>""};
            var stored=(definition.Category switch{"Earning" or "Reimbursement"=>sourceRow.Earnings,"Deduction"=>sourceRow.Deductions,_=>sourceRow.EmployerContributions}).Where(v=>v.Label==output.Label).ToArray();
            if(line?.SourceAmount is not decimal amount||line.Variance is null||actual?.UnroundedEarned is not decimal before
                ||actual.Category!=definition.Category||line.Category!=definition.Category||output.Kind!=role
                ||request.SourceColumnRoles.GetValueOrDefault(output.ColumnIndex,output.Kind)!=role
                ||stored.Length!=1||(SourceDisplayPrecision(stored[0].Amount)!=SourceDisplayPrecision(amount)
                    // Only the saved-output/captured-output identity uses the
                    // importer's existing binary cache contract. Native stages,
                    // source hashes and every monetary limit remain independent.
                    &&!ExcelPayslipCalculationService.SameAdjacentCachedAmount(stored[0].Amount,amount))
                ||SourceDisplayPrecision(ReadNumber(captured,output.ColumnIndex,output.BlankAsZero))!=SourceDisplayPrecision(amount)
                ||actual.Earned!=line.CalculatedAmount||line.Variance!=line.CalculatedAmount-amount
                ||decimal.Round(before,2,MidpointRounding.ToEven)!=line.CalculatedAmount
                ||amount<0||before<0||line.CalculatedAmount<0)
                return Stop("each unchanged source output, category and actual native currency line must remain complete and valid.");
            if(definition.IsPeriodAdjustment)
            {
                var cell=definition.SourceColumnIndex is int index?captured.Cells.ElementAtOrDefault(index):null;
                var reviewedOptionalZero=cell is{DataType:"Blank"}&&string.IsNullOrWhiteSpace(cell.Value)
                    &&definition.BlankInputAsZero&&output.BlankAsZero&&amount==0;
                if(definition.SourceColumnIndex!=definition.ExpectedColumnIndex||cell is null||cell.DataType!="Number"&&!reviewedOptionalZero
                    ||!string.IsNullOrWhiteSpace(cell.FormulaText)||cell.Error is not null||cell.CalculationError is not null||cell.MissingCachedValue
                    ||decimal.Round(amount,2,MidpointRounding.ToEven)!=line.CalculatedAmount)
                    return Stop("a period adjustment must remain its explicit same-cell reviewed literal and ordinary currency stage; formula caches cannot be frozen.");
            }
            else if(line.UnroundedEarned!=before||actual.AmountOverride&&definition.SourceColumnIndex is null
                ||formulas.Single(f=>f.Code==definition.Code).Status=="Reviewed fixed source"&&definition.CalculationType=="Formula"
                    &&!decimal.TryParse(definition.RowFormulas.GetValueOrDefault(planned.RowId,definition.Formula),NumberStyles.Number,CultureInfo.InvariantCulture,out _))
                return Stop("the normal recurring input/rule and its unrounded native trace must be retained; arbitrary output overrides cannot be reviewed.");
            var display=SourceCurrencyDisplay(amount);
            if(!Bounded(before-amount)||!Bounded(line.CalculatedAmount-amount)||!Bounded(line.CalculatedAmount-display))
                return Stop(definition.Code+" exceeds the explicitly reviewed per-line monetary limit.");
            ledger.Add(new(definition.Code,definition.Category,amount,display,before,line.CalculatedAmount,line.CalculatedAmount-amount,line.CalculatedAmount-display));
        }
        decimal Sum(params string[] categories)=>ledger.Where(l=>categories.Contains(l.Category)).Sum(l=>l.Calculated);
        var gross=Sum("Earning","Reimbursement");var deductions=Sum("Deduction");var benefits=Sum("Benefit");var net=Math.Max(0,gross-deductions);
        if(preview.Gross!=gross||preview.Deductions!=deductions||preview.Benefits!=benefits||preview.Net!=net
            ||native.Gross!=gross||native.Deductions!=deductions||native.Benefits!=benefits||native.Net!=net
            ||sourceRow.Earnings.Count!=ledger.Count(l=>l.Category is "Earning" or "Reimbursement")
            ||sourceRow.Deductions.Count!=ledger.Count(l=>l.Category=="Deduction")||sourceRow.EmployerContributions.Count!=ledger.Count(l=>l.Category=="Benefit")
            ||sourceRow.NetPay<0||sourceRow.DeclaredGross is <0||sourceRow.DeclaredDeductions is <0)
            return Stop("native totals must sum their unchanged currency lines and complete nonnegative source categories.");
        var sg=sourceRow.Earnings.Sum(v=>v.Amount);var sd=sourceRow.Deductions.Sum(v=>v.Amount);var sb=sourceRow.EmployerContributions.Sum(v=>v.Amount);
        var totals=new List<HistoricalAmountDifferenceTotal>();
        bool Total(string kind,decimal raw,decimal accounted){var display=SourceCurrencyDisplay(raw);if(!Bounded(accounted-raw)||!Bounded(accounted-display))return false;totals.Add(new(kind,raw,display,accounted,accounted-raw,accounted-display));return true;}
        if(!Total("Gross",sourceRow.DeclaredGross??sg,gross)||!Total("Deductions",sourceRow.DeclaredDeductions??sd,deductions)||!Total("Employer",sb,benefits)||!Total("Net",sourceRow.NetPay,net))
            return Stop("a source/native total exceeds the independently reviewed monetary limit.");
        var aggregates=new List<HistoricalSourceAggregateDifference>();
        bool Aggregate(string kind,decimal stored,decimal sum){var raw=stored-sum;var display=SourceCurrencyDisplay(stored)-SourceCurrencyDisplay(sum);if(!Bounded(raw)||!Bounded(display))return false;aggregates.Add(new(kind,stored,sum,raw,display));return true;}
        if(!Aggregate("Gross",sourceRow.DeclaredGross??sg,sg)||!Aggregate("Deductions",sourceRow.DeclaredDeductions??sd,sd)||!Aggregate("Net",sourceRow.NetPay,Math.Max(0,sg-sd)))
            return Stop("a cached source aggregate exceeds the independent monetary limit.");
        var evidence=new ExcelPayrollConfigurationDifferenceEvidence(1,plan.Batch.ClientId,planned.Employee.Id,planned.RowId,planned.TemplateId,sourceHash,limit,declaration.Reason.Trim(),
            Hash(JsonSerializer.Serialize(scenario,Json)),Hash(JsonSerializer.Serialize(native,Json)),Hash(JsonSerializer.Serialize(formulas,Json)),totals,aggregates);
        var result=new ExcelPayrollTransferAmountReview{Version="setup-bounded-monetary-difference-v1",RowId=planned.RowId,SourceRow=planned.SourceRow,Status=BoundedConfigurationDifferenceStatus,
            Reason="Explicit configuration arithmetic review only; all source and native amounts remain distinct and unchanged. "+declaration.Reason.Trim(),SourceNetRaw=sourceRow.NetPay,SourceNetDisplay=SourceCurrencyDisplay(sourceRow.NetPay),CoreNet=net,RawNetDelta=net-sourceRow.NetPay,DisplayNetDelta=net-SourceCurrencyDisplay(sourceRow.NetPay),Lines=ledger,ConfigurationDifference=evidence};
        var bound=JsonSerializer.SerializeToNode(request,Json)!.AsObject();bound.Remove("roundingReviews");
        result.ProofHash=Hash(JsonSerializer.Serialize(new{result.Version,Request=bound,SourceHash=sourceHash,SourceRow=captured,SavedSourceRow=sourceRow,preview.PayrollDays,preview.PresentDays,preview.PayableDays,result.Lines,result.ConfigurationDifference},Json));
        return result;
    }
}
