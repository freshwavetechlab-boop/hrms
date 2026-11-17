using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal const string BoundedHistoricalDifferenceStatus="Reviewed bounded monetary difference";
    internal static void ValidateHistoricalAmountDifferenceReview(HistoricalPeriodSalaryRequest request)
    {
        if(request.AmountDifferenceReview is not{} review)return;
        if(review.Version!=1||!review.Reviewed||!Regex.IsMatch(review.SourceHash??"",@"\A[a-f0-9]{64}\z")
            ||review.MaxAbsoluteDifference is <=0 or >1||string.IsNullOrWhiteSpace(review.Reason)||review.Reason.Length>500)
            throw new InvalidOperationException("Historical monetary differences require an explicit version-one source hash, reason and positive limit no greater than INR 1. No financial amount is replaced.");
    }
    private void RequireHistoricalAmountDifferenceEnabled(HistoricalPeriodSalaryRequest request)
    {
        if(request.AmountDifferenceReview is not null&&!configuration.GetValue<bool>("Payroll:HistoricalAmountDifferenceReviewEnabled"))
            throw new InvalidOperationException("Explicit bounded historical monetary-difference review is disabled. Ordinary exact review remains unchanged.");
    }
    internal static bool IsAcknowledgedHistoricalAmountDifference(HistoricalPeriodSalaryRequest request,ExcelPayrollTransferAmountReview review)=>
        request.AmountDifferenceReview is not null&&review.Status==BoundedHistoricalDifferenceStatus&&review.BoundedDifference is not null
        &&review.ProofHash.Length==64&&request.Setup.RoundingReviews.GetValueOrDefault(review.RowId)==review.ProofHash;

    // Runs only after ordinary native Employee calculation and all existing
    // ownership, formula, attendance, policy, source-basis and coverage gates.
    internal static ExcelPayrollTransferAmountReview ReviewHistoricalAmountDifferences(HistoricalPeriodSalaryRequest request,
        ExcelPayslipBatch batch,ExcelPayslipCalculationSource source,ExcelPayslipRow row,
        ExcelPayrollSetupPreviewRow calculated,IReadOnlyList<ExcelPayrollTransferFormula> formulas,
        PayrollSimulationResult native,string inputFingerprint,ExcelPayrollTransferAmountReview ordinary)
    {
        if(request.AmountDifferenceReview is not{} declaration)return ordinary;
        ValidateHistoricalAmountDifferenceReview(request);
        var sourceHash=Hash(JsonSerializer.Serialize(new{Batch=batch,Source=source},Json));
        if(declaration.SourceHash!=sourceHash)throw new InvalidOperationException("The bounded difference declaration belongs to a different source version.");
        if(ordinary.Status=="Exact at raw precision")return ordinary;
        var assignment=request.Assignments.SingleOrDefault(a=>a.RowId==row.Id)
            ??throw new InvalidOperationException("The exact historical monetary-review owner is missing.");
        var reported=TrustedReportedHistoricalNative(request,batch,row,native);
        if(!native.Complete||native.Mode!="Employee"||native.ClientId!=batch.ClientId||native.EmployeeId!=assignment.EmployeeId
            ||native.TemplateId!=assignment.TemplateId||native.PayPeriod!=batch.Month||!Regex.IsMatch(inputFingerprint??"",@"\A[a-fA-F0-9]{64}\z")
            ||native.AttendanceEvidence is not{TemporaryDayOverrides:false,PolicyValidationError:""} attendance
            ||!attendance.ActualPresenceKnown&&!reported
            ||attendance.PayPeriod!=batch.Month||attendance.WorkingDays!=calculated.PayrollDays||request.Setup.PresentDaysColumnIndex is not null&&attendance.PresentDays!=calculated.PresentDays||attendance.PayableDays!=calculated.PayableDays)
            throw new InvalidOperationException("Complete ordinary Employee input/trace evidence is required for bounded historical review.");
        var definitions=request.Setup.Components;
        var outputColumns=source.Columns.Where(c=>c.Kind is "earning" or "deduction" or "employer").ToArray();
        if(definitions.Count==0||definitions.Count!=calculated.Lines.Count||definitions.Count!=formulas.Count
            ||definitions.Any(c=>string.IsNullOrWhiteSpace(c.ExistingComponentId)||c.ExpectedColumnIndex is null||c.IsPeriodAdjustment
                ||c.CalculationType=="Residual / Balancing"||c.CalculationType=="Manual / Variable"&&!ReportedZeroPeriodOutput(reported,source,row,c))
            ||definitions.Select(c=>c.Code.ToUpperInvariant()).Distinct().Count()!=definitions.Count
            ||definitions.Select(c=>c.ExpectedColumnIndex).Distinct().Count()!=definitions.Count
            ||!definitions.Select(c=>c.ExpectedColumnIndex!.Value).Order().SequenceEqual(outputColumns.Select(c=>c.ColumnIndex).Order())
            ||formulas.Any(f=>f.RowId!=row.Id||f.SourceRow!=row.SourceRow||!Regex.IsMatch(f.SourceHash??"",@"\A[a-f0-9]{64}\z")
                ||!definitions.Any(c=>c.Code.Equals(f.Code,StringComparison.OrdinalIgnoreCase)&&c.ExpectedColumnIndex==f.SourceColumn)
                ||f.Status!="Exact supported translation"&&!(request.Setup.ReviewedFixedInputs&&f.Status=="Reviewed fixed source"))
            ||formulas.Select(f=>f.Code.ToUpperInvariant()).Distinct().Count()!=definitions.Count)
            throw new InvalidOperationException("Complete uniquely mapped existing components and supported formulas/reviewed literal inputs are mandatory; a monetary limit cannot waive a mapping or formula gap.");
        var cells=source.Rows.Single(r=>r.SourceRow==row.SourceRow);var ledger=new List<ExcelPayrollTransferAmountLine>();
        void Bounded(decimal delta,string meaning){if(Math.Abs(delta)>declaration.MaxAbsoluteDifference)throw new InvalidOperationException(meaning+" exceeds the explicitly reviewed historical monetary limit. No amount was changed.");}
        foreach(var definition in definitions)
        {
            var line=calculated.Lines.SingleOrDefault(l=>l.Code.Equals(definition.Code,StringComparison.OrdinalIgnoreCase));
            var actual=native.Lines.Where(l=>l.Code.Equals(definition.Code,StringComparison.OrdinalIgnoreCase)).ToArray();
            var column=outputColumns.Single(c=>c.ColumnIndex==definition.ExpectedColumnIndex);
            var role=definition.Category switch{"Earning" or "Reimbursement"=>"earning","Deduction"=>"deduction","Benefit"=>"employer",_=>""};
            var storedAmounts=definition.Category switch{"Earning" or "Reimbursement"=>row.Earnings,"Deduction"=>row.Deductions,_=>row.EmployerContributions};
            var storedMatches=storedAmounts.Where(value=>string.Equals(value.Label,column.Label,StringComparison.OrdinalIgnoreCase)).ToArray();
            var formula=formulas.SingleOrDefault(value=>value.Code.Equals(definition.Code,StringComparison.OrdinalIgnoreCase));
            if(line?.SourceAmount is not decimal amount||line.UnroundedEarned is not decimal before||line.Variance is null
                ||actual.Length!=1||actual[0].Category!=definition.Category||line.Category!=definition.Category||column.Kind!=role
                ||actual.Length==1&&actual[0].AmountOverride&&!(definition.SourceColumnIndex.HasValue&&definition.CalculationType is "Fixed Amount" or "Daily Rate")
                ||request.Setup.SourceColumnRoles.GetValueOrDefault(column.ColumnIndex,column.Kind)!=role
                ||!formulas.Any(f=>f.Code.Equals(definition.Code,StringComparison.OrdinalIgnoreCase))
                ||formula?.Status=="Reviewed fixed source"&&definition.CalculationType=="Formula"&&!decimal.TryParse(definition.RowFormulas.GetValueOrDefault(row.Id,definition.Formula),NumberStyles.Number,CultureInfo.InvariantCulture,out _)
                ||actual[0].Earned!=line.CalculatedAmount||actual[0].UnroundedEarned!=before
                ||decimal.Round(before,2,MidpointRounding.ToEven)!=line.CalculatedAmount
                ||amount<0||before<0||line.CalculatedAmount<0
                ||storedMatches.Length!=1||SourceDisplayPrecision(storedMatches[0].Amount)!=SourceDisplayPrecision(amount)
                ||SourceDisplayPrecision(ReadNumber(cells,column.ColumnIndex,definition.BlankInputAsZero||column.BlankAsZero))!=SourceDisplayPrecision(amount)
                ||line.Variance!=line.CalculatedAmount-amount)
                throw new InvalidOperationException("Every mapped source/native component and its actual currency stage must remain complete and unchanged; missing, ambiguous or invalid data cannot be waived.");
            Bounded(before-amount,definition.Code+" pre-currency difference");
            var display=SourceCurrencyDisplay(amount);Bounded(line.CalculatedAmount-amount,definition.Code+" raw difference");Bounded(line.CalculatedAmount-display,definition.Code+" displayed difference");
            ledger.Add(new(definition.Code,definition.Category,amount,display,before,line.CalculatedAmount,line.CalculatedAmount-amount,line.CalculatedAmount-display));
        }
        var codes=definitions.Select(c=>c.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(native.Lines.Any(l=>l.Earned!=0&&!codes.Contains(l.Code)))throw new InvalidOperationException("An unmapped nonzero native component cannot be covered by a monetary review.");
        decimal Sum(params string[] kinds)=>ledger.Where(l=>kinds.Contains(l.Category)).Sum(l=>l.Calculated);
        var gross=Sum("Earning","Reimbursement");var deductions=Sum("Deduction");var benefits=Sum("Benefit");var net=Math.Max(0,gross-deductions);
        if(calculated.Gross!=gross||calculated.Deductions!=deductions||calculated.Benefits!=benefits||calculated.Net!=net
            ||native.Gross!=gross||native.Deductions!=deductions||native.Benefits!=benefits||native.Net!=net)
            throw new InvalidOperationException("Actual native totals must exactly sum its unchanged financial currency lines. A monetary review cannot replace a total.");
        if(row.Earnings.Count!=ledger.Count(l=>l.Category is "Earning" or "Reimbursement")||row.Deductions.Count!=ledger.Count(l=>l.Category=="Deduction")||row.EmployerContributions.Count!=ledger.Count(l=>l.Category=="Benefit"))
            throw new InvalidOperationException("Complete source earning/deduction/employer coverage is required.");
        var sourceGross=row.Earnings.Sum(v=>v.Amount);var sourceDeductions=row.Deductions.Sum(v=>v.Amount);var sourceBenefits=row.EmployerContributions.Sum(v=>v.Amount);
        if(sourceGross<0||sourceDeductions<0||sourceBenefits<0||row.NetPay<0||row.DeclaredGross is <0||row.DeclaredDeductions is <0)throw new InvalidOperationException("Negative source financial values require their existing dedicated review.");
        var totals=new List<HistoricalAmountDifferenceTotal>();
        void Total(string kind,decimal raw,decimal amount){var display=SourceCurrencyDisplay(raw);Bounded(amount-raw,kind+" raw total");Bounded(amount-display,kind+" displayed total");totals.Add(new(kind,raw,display,amount,amount-raw,amount-display));}
        Total("Gross",row.DeclaredGross??sourceGross,gross);Total("Deductions",row.DeclaredDeductions??sourceDeductions,deductions);Total("Employer",sourceBenefits,benefits);Total("Net",row.NetPay,net);
        var aggregates=new List<HistoricalSourceAggregateDifference>();
        void Aggregate(string kind,decimal stored,decimal sum){Bounded(stored-sum,kind+" cached source aggregate");var display=SourceCurrencyDisplay(stored)-SourceCurrencyDisplay(sum);Bounded(display,kind+" displayed source aggregate");aggregates.Add(new(kind,stored,sum,stored-sum,display));}
        Aggregate("Gross",row.DeclaredGross??sourceGross,sourceGross);Aggregate("Deductions",row.DeclaredDeductions??sourceDeductions,sourceDeductions);Aggregate("Net",row.NetPay,Math.Max(0,sourceGross-sourceDeductions));
        var traceHash=Hash(JsonSerializer.Serialize(new{native.ClientId,native.EmployeeId,native.PayPeriod,native.TemplateId,native.Mode,native.AttendanceEvidence,native.Inputs,native.Lines,native.Issues,native.Gross,native.Deductions,native.Benefits,native.Net},Json));
        var result=new ExcelPayrollTransferAmountReview{Version="historical-bounded-monetary-difference-v1",RowId=row.Id,SourceRow=row.SourceRow,Status=BoundedHistoricalDifferenceStatus,
            Reason="Explicit reviewed monetary difference; source and native amounts remain distinct and unchanged. "+declaration.Reason.Trim(),SourceNetRaw=row.NetPay,SourceNetDisplay=SourceCurrencyDisplay(row.NetPay),CoreNet=net,RawNetDelta=net-row.NetPay,DisplayNetDelta=net-SourceCurrencyDisplay(row.NetPay),Lines=ledger,
            BoundedDifference=new(1,batch.ClientId,assignment.EmployeeId,row.Id,assignment.TemplateId,sourceHash,declaration.MaxAbsoluteDifference,declaration.Reason.Trim(),inputFingerprint!,traceHash,Hash(JsonSerializer.Serialize(formulas,Json)),totals,aggregates)};
        var reviewedRequest=JsonSerializer.SerializeToNode(request,Json)!.AsObject();reviewedRequest["setup"]!.AsObject().Remove("roundingReviews");
        result.ProofHash=Hash(JsonSerializer.Serialize(new{Version=result.Version,Request=reviewedRequest,SourceHash=sourceHash,SourceRow=cells,SavedSourceRow=row,calculated.PayrollDays,calculated.PresentDays,calculated.PayableDays,result.Lines,result.BoundedDifference},Json));
        return result;
    }
}
