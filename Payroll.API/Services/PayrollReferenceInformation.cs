using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

// Pure, default-off presentation support. No database, Master lookup, native
// financial mutation, token registration, profile writer or endpoint lives here.
internal static class PayrollReferenceInformation
{
    internal const string Purpose="NonFinancialInformationOnly";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static bool Digest(string? value)=>value is not null&&Regex.IsMatch(value,"\\A[a-f0-9]{64}\\z");
    private static bool Key(string? value)=>value is not null&&Regex.IsMatch(value,"\\A[A-Z][A-Z0-9_]{1,63}\\z");
    private static bool Text(string? value,int max)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=max&&!value.Any(char.IsControl);
    internal static string Hash(object value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value,Json)))).ToLowerInvariant();
    private static DateOnly Start(string month)=>month?.Length==7&&DateOnly.TryParseExact(month+"-01","yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var value)
        ?value:throw new InvalidOperationException("Reference information needs an exact payroll month.");
    private static void Scope(PayrollReferenceScope value)
    {
        if(value is null||value.ClientId<=0||value.EmployeeId<=0||value.PayRunId<=0||value.NativeRowId<=0
            ||!Digest(value.NativeFinancialHash)||!Guid.TryParseExact(value.SourceBatchId,"N",out _)||!Text(value.SourceRowId,100)||!Digest(value.SourceHash)||!Digest(value.SourceIdentityHash))
            throw new InvalidOperationException("Reference information has incomplete native/source ownership evidence.");
        _=Start(value.PayPeriod);
    }
    private static void Field(PayrollReferenceField value)
    {
        if(value is null||value.ClientId<=0||value.EmployeeId<=0||value.PayRunId<=0||value.TemplateId<=0||!Digest(value.ProfileHash)
            ||value.SourceColumn is <0 or >=1500||value.Kind!="info"||string.IsNullOrWhiteSpace(value.Label)||value.Label.Length>150||!Digest(value.NativeFinancialHash))
            throw new InvalidOperationException("Bind an existing reviewed source Information column, label and presentation profile.");
        _=Start(value.PayPeriod);
    }
    private static PayrollReferenceSelection? Selection(PayrollReferenceSelection? value)
    {
        if(value is null)return null;
        if(value.CompletePartKeys is null||value.CompletePartKeys.Count is <1 or >1000||value.CompletePartKeys.Any(key=>!Text(key,100))
            ||value.CompletePartKeys.Distinct(StringComparer.Ordinal).Count()!=value.CompletePartKeys.Count||!value.CompletePartKeys.Contains(value.ReferencePartKey,StringComparer.Ordinal)
            ||!Text(value.ReferenceAssignmentId,100)||!Digest(value.ProofHash)||value.Parts is null||value.Parts.Count!=value.CompletePartKeys.Count
            ||value.Parts.Any(part=>part is null||!Text(part.PartKey,100)||!Text(part.AssignmentId,100)||!Text(part.SourceRowId,100)
                ||new[]{part.SourceIdentityHash,part.NativeInputHash,part.TemplateHash,part.ProofHash}.Any(hash=>!Digest(hash)))
            ||!value.Parts.Select(part=>part.PartKey).Order(StringComparer.Ordinal).SequenceEqual(value.CompletePartKeys.Order(StringComparer.Ordinal))
            ||value.Parts.Single(part=>part.PartKey==value.ReferencePartKey).AssignmentId!=value.ReferenceAssignmentId)
            throw new InvalidOperationException("An exact complete source-part set, row/identity/input/template proof and reviewed reference assignment are required; rates are not summed.");
        return value with{CompletePartKeys=Array.AsReadOnly(value.CompletePartKeys.ToArray()),Parts=Array.AsReadOnly(value.Parts.ToArray())};
    }
    private static void Processing(PayrollReferenceProcessingEvidence proof)
    {
        if(proof is null||!Guid.TryParseExact(proof.ProcessingTransactionId,"N",out _)||new[]{proof.ProcessingEpochHash,proof.FinancialProfileHash,proof.InputSnapshotHash,
                proof.EffectiveTemplateHash,proof.OrdinaryEvidenceHash,proof.IndependentSourceProofHash}.Any(value=>!Digest(value)))
            throw new InvalidOperationException("Use the actual same-processing epoch, native input/template and independent complete-source proof.");
    }
    private static PayrollReferenceInput Capture(string key,PayrollReferenceScope scope,PayrollReferenceUnit unit,PayrollReferenceInputMeaning meaning,
        decimal? value,PayrollReferenceProcessingEvidence proof,string? partKey=null,PayrollReferenceSelection? selection=null,object? basis=null)
    {
        Scope(scope);Processing(proof);if(!Key(key))throw new InvalidOperationException("Invalid reference input key.");selection=Selection(selection);
        if(partKey is not null&&(selection is null||!selection.CompletePartKeys.Contains(partKey,StringComparer.Ordinal)))throw new InvalidOperationException("Reference input part is outside the complete reviewed part set.");
        if(partKey is not null&&selection is not null)
        {
            var part=selection.Parts!.Single(value=>value.PartKey==partKey);
            if(part.NativeInputHash!=proof.InputSnapshotHash||part.TemplateHash!=proof.EffectiveTemplateHash)throw new InvalidOperationException("Reference provider input/template proof belongs to a different source part.");
        }
        var hash=Hash(new{Version=1,Purpose,scope,key,unit,meaning,value,proof,partKey,selection,basis});
        return new(key,scope,unit,meaning,value,hash,proof,partKey,selection);
    }
    internal static PayrollReferenceInput FromProcessingSalaryTarget(string key,PayrollReferenceScope scope,decimal? actualAnnualTarget,
        PayrollReferenceProcessingEvidence proof,PayrollReferenceSelection? selection=null)
    {
        if(actualAnnualTarget is <0 or >1_000_000_000m)throw new InvalidOperationException("Actual annual salary target is outside the native bound.");
        return Capture(key,scope,PayrollReferenceUnit.Amount,PayrollReferenceInputMeaning.AnnualSalaryTarget,actualAnnualTarget,proof,selection?.ReferencePartKey,selection);
    }
    internal static PayrollReferenceInput FromProcessingMonthlyTarget(string key,PayrollReferenceScope scope,decimal? actualNativeMonthlyTarget,
        PayrollReferenceProcessingEvidence proof,PayrollReferenceSelection? selection=null)
    {
        if(actualNativeMonthlyTarget is <0 or >1_000_000_000m)throw new InvalidOperationException("Actual native monthly target is outside its bound.");
        return Capture(key,scope,PayrollReferenceUnit.Amount,PayrollReferenceInputMeaning.NativeMonthlySalaryTarget,actualNativeMonthlyTarget,proof,selection?.ReferencePartKey,selection);
    }
    internal static PayrollReferenceInput FromProcessingDutyUnits(string key,PayrollReferenceScope scope,PayrollReferenceInputMeaning meaning,
        decimal? actualUnits,PayrollReferenceProcessingEvidence proof,string? partKey=null,PayrollReferenceSelection? selection=null)
    {
        if(meaning is not(PayrollReferenceInputMeaning.PayrollDivisor or PayrollReferenceInputMeaning.RegularDutyUnits or PayrollReferenceInputMeaning.ExtraDutyUnits)
            ||actualUnits is <0 or >999.99m||meaning==PayrollReferenceInputMeaning.PayrollDivisor&&actualUnits is {} divisor&&(divisor is <1 or >31||decimal.Truncate(divisor)!=divisor))
            throw new InvalidOperationException("Use explicit recorded payroll/regular/extra DayUnits; no earning amount is inverted into units.");
        return Capture(key,scope,PayrollReferenceUnit.DayUnits,meaning,actualUnits,proof,partKey,selection);
    }
    internal static PayrollReferenceInput FromProcessingMonthlyReference(string key,PayrollReferenceScope scope,decimal? actualFullMonthBase,
        string componentCode,string fullMonthCalculationHash,string calculationStage,PayrollReferenceProcessingEvidence proof,
        string? partKey=null,PayrollReferenceSelection? selection=null)
    {
        if(calculationStage!="FullMonthReferenceProjection"||!Text(componentCode,100)||!Digest(fullMonthCalculationHash)||actualFullMonthBase is <0 or >1_000_000_000m)
            throw new InvalidOperationException("A true full-month reference projection is required; raw monthlyAmount/earned/daily-rate values are not a monthly base.");
        return Capture(key,scope,PayrollReferenceUnit.Amount,PayrollReferenceInputMeaning.TrueMonthlyBase,actualFullMonthBase,proof,partKey,selection,
            new{componentCode,fullMonthCalculationHash,calculationStage});
    }
    // Authentication is performed here against the existing signed receipt. The
    // row/history facts are supplied by a future SELECT-only adapter, not a DTO.
    internal static PayrollReferenceInput FromClosedInitialReceipt(string key,PayrollReferenceScope scope,PayrollReferenceNativeRow native,
        IReadOnlyList<PayrollReferenceHistory> effectiveHistories,string protectedEnvelope,PortableIntegrationCredentialProtector protector)
        =>FromClosedInitialReceiptCore(key,scope,native,effectiveHistories,protectedEnvelope,protector,false);

    // This separate mode proves the prior accepted receipt's row ownership. It
    // makes no bank-ownership assertion and cannot replace explicit signed bank
    // review evidence when that evidence exists for the selected source row.
    internal static string AuthenticateClosedReceiptRowIdentity(PayrollReferenceScope scope,string protectedEnvelope,PortableIntegrationCredentialProtector protector,
        string? verifiedExistingIdentityHash=null)
    {Scope(scope);return ClosedReceiptRowIdentity(scope,ReadClosedReceipt(protectedEnvelope,protector),verifiedExistingIdentityHash);}
    internal static PayrollReferenceInput FromClosedInitialReceiptRow(string key,PayrollReferenceScope scope,PayrollReferenceNativeRow native,
        IReadOnlyList<PayrollReferenceHistory> effectiveHistories,string protectedEnvelope,PortableIntegrationCredentialProtector protector,
        string? verifiedExistingIdentityHash=null)
        =>FromClosedInitialReceiptCore(key,scope,native,effectiveHistories,protectedEnvelope,protector,true,verifiedExistingIdentityHash);

    private static PayrollReferenceInput FromClosedInitialReceiptCore(string key,PayrollReferenceScope scope,PayrollReferenceNativeRow native,
        IReadOnlyList<PayrollReferenceHistory> effectiveHistories,string protectedEnvelope,PortableIntegrationCredentialProtector protector,bool receiptRowIdentity,
        string? verifiedExistingIdentityHash=null)
    {
        Scope(scope);if(!Key(key)||native is null||effectiveHistories is null||effectiveHistories.Count!=1||effectiveHistories[0] is null||protector is null||protectedEnvelope is null||protectedEnvelope.Length>4*1024*1024)throw new InvalidOperationException("Missing, ambiguous or unbounded closed reference input evidence.");
        var history=effectiveHistories[0];
        if(native.Id!=scope.NativeRowId||native.ClientId!=scope.ClientId||native.EmployeeId!=scope.EmployeeId||native.PayRunId!=scope.PayRunId
            ||native.PayPeriod!=scope.PayPeriod||native.FinancialHash!=scope.NativeFinancialHash||!Digest(history.RowHash))throw new InvalidOperationException("Closed reference belongs to a different saved native row.");
        var stored=ReadClosedReceipt(protectedEnvelope,protector);
        var receipt=stored["receipt"]!;var request=stored["reviewedRequest"]!;var start=Start(scope.PayPeriod);var end=start.AddMonths(1).AddDays(-1);
        var rows=receipt["rows"]!.AsArray().Where(row=>row?["employeeId"]?.GetValue<int>()==scope.EmployeeId).ToArray();
        var targets=receipt["initialSourceTargets"]!.AsArray().Where(row=>row?["employeeId"]?.GetValue<int>()==scope.EmployeeId).ToArray();
        var identities=receipt["sourceIdentities"]?.AsArray().Where(row=>row?["employeeId"]?.GetValue<int>()==scope.EmployeeId&&row?["rowId"]?.ToString()==scope.SourceRowId).ToArray()??[];
        if(rows.Length!=1||targets.Length!=1||(!receiptRowIdentity&&identities.Length!=1))throw new InvalidOperationException("Closed reference employee/history, signed identity or target is missing or ambiguous.");
        var row=rows[0]!;var target=targets[0]!;var annual=target["annualSalaryTarget"]!.GetValue<decimal>();var review=request["initialSourceTargetReview"];
        var identity=identities.SingleOrDefault();
        var identityHash=receiptRowIdentity?ClosedReceiptRowIdentity(scope,stored,verifiedExistingIdentityHash):identity is null?"":Hash(identity);
        if(stored["actorId"]?.GetValue<int>() is not >0||stored["sourceHash"]?.ToString()!=scope.SourceHash
            ||receipt["clientId"]?.GetValue<int>()!=scope.ClientId||receipt["payPeriod"]?.ToString()!=scope.PayPeriod||receipt["batchId"]?.ToString()!=scope.SourceBatchId
            ||!Guid.TryParseExact(receipt["requestId"]?.ToString(),"N",out _)||!Digest(receipt["fingerprint"]?.ToString())
            ||request["salaryTargetMode"]?.ToString()!="ReviewedInitialSourceTarget"||request["setup"]?["batchId"]?.ToString()!=scope.SourceBatchId
            ||review?["version"]?.GetValue<int>()!=1||review["reviewed"]?.GetValue<bool>()!=true||review["sourceHash"]?.ToString()!=scope.SourceHash
            ||review["meaning"]?.ToString()!="MonthlyPayrollEarningsBudget"||review["actualJoiningDateNotEstablished"]?.GetValue<bool>()!=true
            ||review["monthlyCtcColumnIndex"]?.GetValue<int>() is not >=0 or >=1500||target["monthlyCtcColumnIndex"]?.GetValue<int>()!=review["monthlyCtcColumnIndex"]?.GetValue<int>()
            ||target["sourceHash"]?.ToString()!=scope.SourceHash||target["meaning"]?.ToString()!="MonthlyPayrollEarningsBudget"
            ||identityHash!=scope.SourceIdentityHash
            ||!receiptRowIdentity&&(identity?["clientId"]?.GetValue<int>()!=scope.ClientId||identity?["sourceHash"]?.ToString()!=scope.SourceHash
                ||new[]{identity?["sourceCellHash"]?.ToString(),identity?["bankOwnershipHash"]?.ToString(),identity?["reviewedNamesHash"]?.ToString()}.Any(value=>!Digest(value)))
            ||target["actualJoiningDateNotEstablished"]?.GetValue<bool>()!=true||annual is <=0 or >1_000_000_000m
            ||target["sourceMonthlyBudget"]!.GetValue<decimal>()*12m!=annual||row["rowId"]?.ToString()!=scope.SourceRowId||row["historyId"]?.GetValue<long>()!=history.Id
            ||row["templateId"]?.ToString()!=history.TemplateId||history.ClientId!=scope.ClientId||history.EmployeeId!=scope.EmployeeId||history.ActionType!="Closed Excel Salary"
            ||history.Status!="Historical"||history.EffectiveFrom!=start||history.EffectiveTo!=end||history.AnnualSalaryTarget!=annual)
            throw new InvalidOperationException("Authenticated closed target must match one exact whole-period IT0008 target; no Master/source amount fallback is accepted.");
        var proof=Hash(new{Version=1,Purpose,scope,native,history,ReceiptHash=Hash(stored),EnvelopeHash=Hash(protectedEnvelope),Mode="ReviewedInitialSourceTarget",
            IdentityKind=receiptRowIdentity?"ClosedReceiptRowIdentity":"ExplicitSignedSourceIdentity"});
        return new(key,scope,PayrollReferenceUnit.Amount,PayrollReferenceInputMeaning.AnnualSalaryTarget,history.AnnualSalaryTarget,proof,null);
    }
    private static JsonNode ReadClosedReceipt(string protectedEnvelope,PortableIntegrationCredentialProtector protector)
    {
        if(protector is null||protectedEnvelope is null||protectedEnvelope.Length>4*1024*1024)throw new InvalidOperationException("Missing or unbounded protected receipt.");
        using var document=JsonDocument.Parse(protectedEnvelope);NoDuplicates(document.RootElement);var envelope=JsonNode.Parse(protectedEnvelope)!;
        if(envelope["purpose"]?.ToString()!="ClosedHistoricalSalaryReceipt"||envelope["version"] is {} envelopeVersion&&envelopeVersion.GetValue<int>()!=1)
            throw new InvalidOperationException("Reference receipt purpose/version is invalid.");
        var plaintext=protector.UnprotectHistoricalSalary(envelope["payload"]?.ToString()??"")??throw new InvalidOperationException("Reference receipt authentication failed.");
        if(plaintext.Length>4*1024*1024)throw new InvalidOperationException("Reference receipt exceeds its bounded metadata size.");
        using var decoded=JsonDocument.Parse(plaintext);NoDuplicates(decoded.RootElement);var stored=JsonNode.Parse(plaintext)!;
        if(stored["version"] is {} version&&version.GetValue<int>()!=1)throw new InvalidOperationException("Unknown reference receipt version.");
        return stored;
    }
    private static string ClosedReceiptRowIdentity(PayrollReferenceScope scope,JsonNode stored,string? verifiedExistingIdentityHash=null)
    {
        var receipt=stored["receipt"]!;var request=stored["reviewedRequest"]!;
        var rows=receipt["rows"]?.AsArray().Select(row=>row!).ToArray()??[];
        var assignments=request["assignments"]?.AsArray().Select(row=>row!).ToArray()??[];
        var targets=receipt["initialSourceTargets"]?.AsArray().Select(row=>row!).ToArray()??[];
        var selected=request["setup"]?["rowIds"]?.AsArray().Select(row=>row!.ToString()).ToArray()??[];
        var hasDecisions=request["setup"]?["employeeDecisions"] is not null;
        var decisions=request["setup"]?["employeeDecisions"]?.AsArray().Select(row=>row!).ToArray()??[];
        // Historical salary also accepts an empty optional decision list after
        // its strict existing-Master identity checks. Reuse those receipts only
        // after the SELECT adapter repeats that ownership proof; a signed code
        // assignment alone must not silently become a new identity assertion.
        var verifiedEmptyDecisions=hasDecisions&&decisions.Length==0&&Digest(verifiedExistingIdentityHash);
        string Row(JsonNode row)=>row["rowId"]?.ToString()??"";int Employee(JsonNode row)=>row["employeeId"]?.GetValue<int>()??0;
        bool DistinctRows(JsonNode[] values)=>values.All(row=>row is not null&&Text(Row(row),100)&&Employee(row)>0)
            &&values.Select(Row).Distinct(StringComparer.Ordinal).Count()==values.Length&&values.Select(Employee).Distinct().Count()==values.Length;
        if(stored["actorId"]?.GetValue<int>() is not >0||stored["sourceHash"]?.ToString()!=scope.SourceHash||request["salaryTargetMode"]?.ToString()!="ReviewedInitialSourceTarget"
            ||receipt["clientId"]?.GetValue<int>()!=scope.ClientId||receipt["batchId"]?.ToString()!=scope.SourceBatchId||receipt["payPeriod"]?.ToString()!=scope.PayPeriod
            ||!Guid.TryParseExact(receipt["requestId"]?.ToString(),"N",out _)||!Digest(receipt["fingerprint"]?.ToString())||request["setup"]?["batchId"]?.ToString()!=scope.SourceBatchId
            ||rows.Length is <1 or >5000||!DistinctRows(rows)||rows.Any(row=>row["historyId"]?.GetValue<long>() is not >0||!Text(row["templateId"]?.ToString(),100))
            ||rows.Select(row=>row["historyId"]!.GetValue<long>()).Distinct().Count()!=rows.Length
            ||assignments.Length!=rows.Length||!DistinctRows(assignments)||assignments.Any(assignment=>!rows.Any(row=>Row(row)==Row(assignment)&&Employee(row)==Employee(assignment)&&row["templateId"]?.ToString()==assignment["templateId"]?.ToString()))
            ||targets.Length!=rows.Length||targets.Any(row=>row is null||Employee(row)<=0)||targets.Select(Employee).Distinct().Count()!=targets.Length||!targets.Select(Employee).Order().SequenceEqual(rows.Select(Employee).Order())
            ||selected.Length!=rows.Length||selected.Distinct(StringComparer.Ordinal).Count()!=selected.Length||!selected.Order(StringComparer.Ordinal).SequenceEqual(rows.Select(Row).Order(StringComparer.Ordinal))
            ||hasDecisions&&!verifiedEmptyDecisions&&(decisions.Length!=rows.Length||decisions.Any(decision=>decision is null||decision["allowCreate"]?.GetValue<bool>()!=false
                ||!rows.Any(row=>Row(row)==Row(decision)&&Employee(row)==decision["matchedEmployeeId"]?.GetValue<int>()))||decisions.Select(Row).Distinct(StringComparer.Ordinal).Count()!=decisions.Length))
            throw new InvalidOperationException("Closed receipt row identity needs the exact one-to-one complete signed receipt/assignment/selected-row roster and matching decision.");
        var matches=rows.Where(row=>Employee(row)==scope.EmployeeId&&Row(row)==scope.SourceRowId).ToArray();
        if(matches.Length!=1||receipt["sourceIdentities"]?.AsArray().Any(identity=>identity?["employeeId"]?.GetValue<int>()==scope.EmployeeId||identity?["rowId"]?.ToString()==scope.SourceRowId)==true)
            throw new InvalidOperationException("Use the explicit signed SourceIdentity when present; closed receipt row mode cannot bypass it or invent bank ownership.");
        var match=matches[0];var assignment=assignments.Single(row=>Row(row)==scope.SourceRowId);var decision=decisions.SingleOrDefault(row=>Row(row)==scope.SourceRowId);
        var identity=Hash(new{Version=1,Kind="ClosedReceiptRowIdentity",ReceiptVersion=stored["version"]?.GetValue<int>()??1,scope.ClientId,scope.PayPeriod,scope.SourceBatchId,scope.SourceHash,
            RequestId=receipt["requestId"]!.ToString(),Fingerprint=receipt["fingerprint"]!.ToString(),Row=match,Assignment=assignment,MatchingDecision=decision,
            CompleteRosterHash=Hash(rows.OrderBy(Row,StringComparer.Ordinal).ToArray()),CompleteAssignmentHash=Hash(assignments.OrderBy(Row,StringComparer.Ordinal).ToArray())});
        // Keep all previously authenticated nonempty/omitted-decision hashes
        // byte-identical. The new empty-list mode separately binds its proof.
        return verifiedEmptyDecisions?Hash(new{Version=1,Kind="VerifiedClosedReceiptRowIdentity",SignedRowIdentity=identity,VerifiedExistingIdentity=verifiedExistingIdentityHash}):identity;
    }
    private static void NoDuplicates(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object){var keys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var property in element.EnumerateObject())
            {if(!keys.Add(property.Name))throw new InvalidOperationException("Duplicate reference receipt properties are invalid.");NoDuplicates(property.Value);}}
        else if(element.ValueKind==JsonValueKind.Array)foreach(var child in element.EnumerateArray())NoDuplicates(child);
    }

    internal static string DeclarationHash(PayrollReferenceDeclaration declaration){Validate(declaration);return Hash(declaration);}
    private readonly record struct Dimension(int Amount,int Days,int Percent,bool Boolean=false)
    {
        internal static Dimension Of(PayrollReferenceUnit unit)=>unit switch{PayrollReferenceUnit.Amount=>new(1,0,0),PayrollReferenceUnit.DayUnits=>new(0,1,0),PayrollReferenceUnit.PercentPoints=>new(0,0,1),PayrollReferenceUnit.Scalar=>new(0,0,0),_=>throw new InvalidOperationException("Unknown reference unit.")};
    }
    private static HashSet<string> References(PayrollReferenceExpression expression,PayrollReferenceDeclaration declaration,HashSet<string>? visiting=null)
    {
        visiting??=[];var result=new HashSet<string>(StringComparer.Ordinal);
        if(expression is PayrollReferenceInputExpression input)result.Add(input.Key);
        else if(expression is PayrollReferenceOutputExpression output){if(!visiting.Add(output.Key))throw new InvalidOperationException("Reference information has a dependency cycle.");
            var definition=declaration.Outputs.SingleOrDefault(row=>row.Key==output.Key)??throw new InvalidOperationException("An information output dependency is undeclared.");result.UnionWith(References(definition.Expression,declaration,visiting));visiting.Remove(output.Key);}
        else if(expression is PayrollReferenceOperation operation)foreach(var child in operation.Arguments)result.UnionWith(References(child,declaration,visiting));
        return result;
    }
    internal static void Validate(PayrollReferenceDeclaration declaration)
    {
        if(declaration is null||declaration.Version!=1||declaration.Purpose!=Purpose||declaration.Inputs is null||declaration.Outputs is null
            ||declaration.Inputs.Count is <1 or >32||declaration.Outputs.Count is <1 or >32
            ||declaration.Inputs.Any(row=>row is null)||declaration.Outputs.Any(row=>row is null))throw new InvalidOperationException("Use a bounded version1 nonfinancial reference declaration.");
        if(declaration.Inputs.Select(row=>row.Key).Concat(declaration.Outputs.Select(row=>row.Key)).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=declaration.Inputs.Count+declaration.Outputs.Count)
            throw new InvalidOperationException("Reference inputs/outputs need distinct case-exact keys.");
        foreach(var input in declaration.Inputs)
            if(!Key(input.Key)||!Enum.IsDefined(input.Unit)||input.Unit==PayrollReferenceUnit.Scalar||!Enum.IsDefined(input.Meaning)||input.Precision is <0 or >8
                ||input.Minimum< -1_000_000_000m||input.Maximum>1_000_000_000m||input.Minimum>input.Maximum
                ||input.Meaning is PayrollReferenceInputMeaning.AnnualSalaryTarget or PayrollReferenceInputMeaning.NativeMonthlySalaryTarget or PayrollReferenceInputMeaning.TrueMonthlyBase&&input.Unit!=PayrollReferenceUnit.Amount
                ||input.Meaning is PayrollReferenceInputMeaning.PayrollDivisor or PayrollReferenceInputMeaning.RegularDutyUnits or PayrollReferenceInputMeaning.ExtraDutyUnits&&input.Unit!=PayrollReferenceUnit.DayUnits)
                throw new InvalidOperationException("A reference input has an invalid unit/meaning/range/precision.");
        foreach(var output in declaration.Outputs)
        {
            if(!Key(output.Key)||!Enum.IsDefined(output.Unit)||output.Unit==PayrollReferenceUnit.Scalar||!Enum.IsDefined(output.Meaning))throw new InvalidOperationException("A reference output declaration is invalid.");
            if(output.Unit!=(output.Meaning==PayrollReferenceOutputMeaning.DutyUnits?PayrollReferenceUnit.DayUnits:PayrollReferenceUnit.Amount))throw new InvalidOperationException("Reference output unit does not match its declared presentation meaning.");
            var nodes=0;var type=Type(output.Expression,declaration,[],0,ref nodes);
            if(type!=Dimension.Of(output.Unit))throw new InvalidOperationException("Reference expression units do not match the information output.");
            var refs=References(output.Expression,declaration);
            var annual=refs.Where(key=>declaration.Inputs.Single(row=>row.Key==key).Meaning==PayrollReferenceInputMeaning.AnnualSalaryTarget).ToArray();
            var nativeMonthly=refs.Where(key=>declaration.Inputs.Single(row=>row.Key==key).Meaning==PayrollReferenceInputMeaning.NativeMonthlySalaryTarget).ToArray();
            var budget=output.Meaning==PayrollReferenceOutputMeaning.MonthlySalaryBudget;
            var target=output.Meaning==PayrollReferenceOutputMeaning.MonthlySalaryTarget;
            if(budget?!(annual.Length==1&&nativeMonthly.Length==0&&MonthlyBudget(output.Expression,annual[0]))
                :target?!(annual.Length==0&&nativeMonthly.Length==1&&output.Expression is PayrollReferenceInputExpression direct&&direct.Key==nativeMonthly[0])
                :annual.Length+nativeMonthly.Length>0)
                throw new InvalidOperationException("Annual salary target can only produce an explicit annual-budget/12 value; actual native monthly target is separate. Neither can become Basic or a full monthly component base.");
            if(output.Meaning is PayrollReferenceOutputMeaning.MonthlySalaryBudget or PayrollReferenceOutputMeaning.MonthlySalaryTarget or PayrollReferenceOutputMeaning.FullMonthReferenceAmount
                &&refs.Any(key=>declaration.Inputs.Single(row=>row.Key==key).Meaning is PayrollReferenceInputMeaning.RegularDutyUnits or PayrollReferenceInputMeaning.ExtraDutyUnits))
                throw new InvalidOperationException("Full-month information cannot depend on earned/duty units.");
            if(output.Meaning==PayrollReferenceOutputMeaning.PeriodInformationAmount&&refs.Any(key=>declaration.Inputs.Single(row=>row.Key==key).Meaning==PayrollReferenceInputMeaning.TrueMonthlyBase)
                &&(!refs.Any(key=>declaration.Inputs.Single(row=>row.Key==key).Meaning is PayrollReferenceInputMeaning.RegularDutyUnits or PayrollReferenceInputMeaning.ExtraDutyUnits)
                    ||!refs.Any(key=>declaration.Inputs.Single(row=>row.Key==key).Meaning==PayrollReferenceInputMeaning.PayrollDivisor)))
                throw new InvalidOperationException("An earned information amount needs its recorded period units and payroll divisor, not a relabelled monthly rate.");
            if(output.Meaning==PayrollReferenceOutputMeaning.AggregatePeriodInformationAmount&&!Aggregate(output.Expression,declaration))
                throw new InvalidOperationException("Aggregate period information must explicitly add/subtract reviewed per-part period outputs, never monthly rates.");
        }
        if(JsonSerializer.SerializeToUtf8Bytes(declaration,Json).Length>64000)throw new InvalidOperationException("Reference declaration exceeds the bounded metadata size.");
    }
    private static bool MonthlyBudget(PayrollReferenceExpression expression,string key)=>expression is PayrollReferenceOperation{Operator:PayrollReferenceOperator.Divide,Arguments.Count:2} divide
        &&divide.Arguments[0] is PayrollReferenceInputExpression input&&input.Key==key&&divide.Arguments[1] is PayrollReferenceLiteral{Value:12,Unit:PayrollReferenceUnit.Scalar};
    private static bool Aggregate(PayrollReferenceExpression expression,PayrollReferenceDeclaration declaration)=>expression switch{
        PayrollReferenceOutputExpression output=>declaration.Outputs.Single(row=>row.Key==output.Key).Meaning==PayrollReferenceOutputMeaning.PeriodInformationAmount,
        PayrollReferenceOperation{Operator:PayrollReferenceOperator.Add or PayrollReferenceOperator.Subtract} operation=>operation.Arguments.All(argument=>Aggregate(argument,declaration)),_=>false};
    private static Dimension Type(PayrollReferenceExpression expression,PayrollReferenceDeclaration declaration,HashSet<string> visiting,int depth,ref int nodes)
    {
        if(expression is null||depth>32||++nodes>512)throw new InvalidOperationException("Reference expression depth/node bound exceeded.");
        if(expression is PayrollReferenceLiteral literal)
        {
            if(!Enum.IsDefined(literal.Unit)||literal.Value is <-1_000_000_000m or >1_000_000_000m
                ||literal.Unit is PayrollReferenceUnit.Amount or PayrollReferenceUnit.DayUnits&&literal.Value!=0)
                throw new InvalidOperationException("Only bounded scalar/percentage constants or typed zero may be declared; salary money and duty units are recorded inputs.");
            return Dimension.Of(literal.Unit);
        }
        if(expression is PayrollReferenceInputExpression input)return Dimension.Of(declaration.Inputs.SingleOrDefault(row=>row.Key==input.Key)?.Unit??throw new InvalidOperationException("Reference input is undeclared."));
        if(expression is PayrollReferenceOutputExpression output)
        {if(!visiting.Add(output.Key))throw new InvalidOperationException("Reference information has a dependency cycle.");var definition=declaration.Outputs.SingleOrDefault(row=>row.Key==output.Key)??throw new InvalidOperationException("Reference output is undeclared.");var type=Type(definition.Expression,declaration,visiting,depth+1,ref nodes);visiting.Remove(output.Key);return type;}
        if(expression is not PayrollReferenceOperation operation||!Enum.IsDefined(operation.Operator)||operation.Arguments is null||operation.Arguments.Count is <1 or >32)throw new InvalidOperationException("Unsupported reference expression node.");
        var types=new List<Dimension>();foreach(var argument in operation.Arguments)types.Add(Type(argument,declaration,visiting,depth+1,ref nodes));
        var op=operation.Operator;var round=op is PayrollReferenceOperator.Round or PayrollReferenceOperator.RoundUp or PayrollReferenceOperator.RoundDown;
        if(round&&(operation.Digits is null or <-6 or >8)||!round&&operation.Digits is not null)throw new InvalidOperationException("Reference rounding precision is invalid.");
        bool Same()=>types.All(type=>type==types[0]);
        if(op is PayrollReferenceOperator.Add or PayrollReferenceOperator.Subtract or PayrollReferenceOperator.Min or PayrollReferenceOperator.Max)
        {if(types.Count<2||!Same()||types[0].Boolean)throw new InvalidOperationException("Reference addition/comparison units differ.");return types[0];}
        if(op is PayrollReferenceOperator.Multiply or PayrollReferenceOperator.Divide)
        {if(types.Count!=2||types.Any(type=>type.Boolean))throw new InvalidOperationException("Reference arithmetic operands are invalid.");var sign=op==PayrollReferenceOperator.Multiply?1:-1;var type=new Dimension(types[0].Amount+sign*types[1].Amount,types[0].Days+sign*types[1].Days,types[0].Percent+sign*types[1].Percent);if(Math.Abs(type.Amount)>4||Math.Abs(type.Days)>4||Math.Abs(type.Percent)>4)throw new InvalidOperationException("Reference unit expression is too complex.");return type;}
        if(op==PayrollReferenceOperator.Percent){if(types.Count!=1||types[0]!=Dimension.Of(PayrollReferenceUnit.PercentPoints))throw new InvalidOperationException("Percent requires explicit percentage points.");return Dimension.Of(PayrollReferenceUnit.Scalar);}
        if(round||op==PayrollReferenceOperator.Abs){if(types.Count!=1||types[0].Boolean)throw new InvalidOperationException("Invalid numeric reference function.");return types[0];}
        if(op is PayrollReferenceOperator.Equal or PayrollReferenceOperator.Less or PayrollReferenceOperator.Greater){if(types.Count!=2||!Same()||types[0].Boolean)throw new InvalidOperationException("Reference comparison units differ.");return new(0,0,0,true);}
        if(op==PayrollReferenceOperator.If){if(types.Count!=3||!types[0].Boolean||types[1]!=types[2])throw new InvalidOperationException("Reference IF needs a condition and equal-unit branches.");return types[1];}
        throw new InvalidOperationException("Unsupported reference operator.");
    }

    internal static PayrollReferenceResult Resolve(PayrollReferenceDeclaration declaration,PayrollReferenceBinding binding,PayrollReferenceProfile profile,PayrollReferenceField requested,
        IReadOnlyList<PayrollReferenceInput>? providers)
    {
        Field(requested);Field(binding.Field);
        if(binding.Scope is null||declaration is null||declaration.Version!=1||declaration.Purpose!=Purpose)throw new InvalidOperationException("Unknown reference binding/declaration schema.");
        _=Start(binding.Scope.PayPeriod);
        if(binding.Field.ClientId!=binding.Scope.ClientId||binding.Field.EmployeeId!=binding.Scope.EmployeeId||binding.Field.PayRunId!=binding.Scope.PayRunId
            ||binding.Field.PayPeriod!=binding.Scope.PayPeriod||binding.Field.NativeFinancialHash!=binding.Scope.NativeFinancialHash)throw new InvalidOperationException("Reference field and source/native binding scopes disagree.");
        if(profile is null||profile.ClientId!=requested.ClientId||profile.TemplateId!=requested.TemplateId||profile.ProfileHash!=requested.ProfileHash||profile.Columns is null
            ||profile.Columns.Count is <1 or >100||profile.Columns.Any(column=>column is null)||profile.Columns.Select(column=>column.SourceColumn).Distinct().Count()!=profile.Columns.Count
            ||profile.Columns.Count(column=>column.SourceColumn==requested.SourceColumn&&column.Kind=="info"&&column.Label==requested.Label)!=1)
            throw new InvalidOperationException("The selected source Information field is not present in the actual reviewed profile.");
        if(binding.Version!=1||binding.Field.ClientId!=requested.ClientId||binding.Field.TemplateId!=requested.TemplateId||binding.Field.ProfileHash!=requested.ProfileHash
            ||binding.Field.SourceColumn!=requested.SourceColumn||binding.Field.Label!=requested.Label||binding.Field.Kind!=requested.Kind)
            throw new InvalidOperationException("Reference information presentation binding changed; review the existing Information column.");
        // Other-month legacy PDFs do not need this period's provider or source.
        if(binding.Scope.PayPeriod!=requested.PayPeriod)return new(PayrollReferenceStatus.NotApplicable,binding.OutputKey,null,null,"No reference override applies to this payroll period.",binding.DeclarationHash,"",new ReadOnlyDictionary<string,string>(new Dictionary<string,string>()));
        Scope(binding.Scope);if(binding.Field!=requested||binding.Scope.ClientId!=requested.ClientId||binding.Scope.EmployeeId!=requested.EmployeeId
            ||binding.Scope.PayRunId!=requested.PayRunId||binding.Scope.NativeFinancialHash!=requested.NativeFinancialHash)throw new InvalidOperationException("Reference information belongs to another native employee/run/financial snapshot.");
        var declarationHash=DeclarationHash(declaration);if(binding.DeclarationHash!=declarationHash||binding.InputProofs is null
            ||binding.InputProofs.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=binding.InputProofs.Count||binding.InputProofs.Any(pair=>!Digest(pair.Value)||!declaration.Inputs.Any(input=>input.Key==pair.Key)))throw new InvalidOperationException("Reference declaration/proof binding changed.");
        var output=declaration.Outputs.SingleOrDefault(row=>row.Key==binding.OutputKey)??throw new InvalidOperationException("Reference output is not declared.");
        providers??=[];if(providers.Any(row=>row is null)||providers.Select(row=>row.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=providers.Count
            ||providers.Any(row=>!declaration.Inputs.Any(input=>input.Key==row.Key)))throw new InvalidOperationException("Reference providers are duplicated or undeclared.");
        var references=References(output.Expression,declaration);var values=new Dictionary<string,decimal>(StringComparer.Ordinal);var proofs=new Dictionary<string,string>(StringComparer.Ordinal);
        var used=new List<PayrollReferenceInput>();
        foreach(var key in references)
        {
            var input=providers.SingleOrDefault(row=>row.Key==key);
            if(input is null)return new(PayrollReferenceStatus.Missing,output.Key,output.Unit,null,"Recorded reference input "+key+" is missing; it is not zero.",declarationHash,Hash(binding),new ReadOnlyDictionary<string,string>(proofs));
            var spec=declaration.Inputs.Single(row=>row.Key==key);
            if(input.Scope!=binding.Scope||input.Unit!=spec.Unit||input.Meaning!=spec.Meaning||!Digest(input.ProofHash)||!binding.InputProofs.TryGetValue(key,out var proof)||proof!=input.ProofHash
                ||input.Value is {} value&&(value<spec.Minimum||value>spec.Maximum||decimal.Round(value,spec.Precision)!=value))
                throw new InvalidOperationException("Reference input ownership/unit/meaning/precision or immutable proof changed.");
            if(!input.Value.HasValue)return new(PayrollReferenceStatus.Missing,output.Key,output.Unit,null,"Recorded reference input "+key+" is missing; it is not zero.",declarationHash,Hash(binding),new ReadOnlyDictionary<string,string>(proofs));
            if(input.Processing is not null)Processing(input.Processing);values[key]=input.Value.Value;proofs[key]=input.ProofHash;used.Add(input);
        }
        var epochs=used.Where(input=>input.Processing is not null).Select(input=>(input.Processing!.ProcessingTransactionId,input.Processing.ProcessingEpochHash)).Distinct().Count();
        if(epochs>1)throw new InvalidOperationException("Reference inputs come from different processing epochs.");
        var selection=Selection(binding.ReferenceSelection);
        if(used.Any(input=>Hash(input.Selection??(object)"")!=Hash(selection??(object)"")))throw new InvalidOperationException("Reference inputs lost their complete-part/reference selection proof.");
        if(output.Meaning is PayrollReferenceOutputMeaning.FullMonthReferenceAmount or PayrollReferenceOutputMeaning.MonthlySalaryBudget or PayrollReferenceOutputMeaning.MonthlySalaryTarget
            &&(used.Where(input=>input.Unit==PayrollReferenceUnit.Amount).Select(input=>input.PartKey).Distinct().Count()>1
                ||selection is not null&&used.Where(input=>input.Unit==PayrollReferenceUnit.Amount).Any(input=>input.PartKey!=selection.ReferencePartKey)))
            throw new InvalidOperationException("Full-month rates/targets from different parts cannot be added; use an explicit reference.");
        if(output.Meaning==PayrollReferenceOutputMeaning.PeriodInformationAmount&&used.Select(input=>input.PartKey).Distinct().Count()>1)
            throw new InvalidOperationException("A period reference amount mixes rates/divisor/duty units from different parts.");
        if(output.Meaning==PayrollReferenceOutputMeaning.AggregatePeriodInformationAmount)
        {
            if(selection is null||!used.Select(input=>input.PartKey).Distinct().Order(StringComparer.Ordinal).SequenceEqual(selection.CompletePartKeys.Order(StringComparer.Ordinal)))
                throw new InvalidOperationException("An aggregate period reference needs every proved part exactly; no part or rate is inferred.");
            foreach(var partOutput in declaration.Outputs.Where(row=>row.Meaning==PayrollReferenceOutputMeaning.PeriodInformationAmount))
            {
                var partRefs=References(partOutput.Expression,declaration);if(!partRefs.IsSubsetOf(references))continue;
                if(used.Where(input=>partRefs.Contains(input.Key)).Select(input=>input.PartKey).Distinct().Count()!=1)throw new InvalidOperationException("Each aggregated period amount needs its own part's base/divisor/units.");
            }
        }
        decimal Evaluate(PayrollReferenceExpression expression)=>expression switch{
            PayrollReferenceLiteral literal=>literal.Value,PayrollReferenceInputExpression input=>values[input.Key],PayrollReferenceOutputExpression other=>Evaluate(declaration.Outputs.Single(row=>row.Key==other.Key).Expression),
            PayrollReferenceOperation operation=>EvaluateOperation(operation,Evaluate),_=>throw new InvalidOperationException("Unsupported reference expression.")};
        decimal result;try{result=Evaluate(output.Expression);}catch(Exception error)when(error is OverflowException or DivideByZeroException){throw new InvalidOperationException("Reference expression cannot calculate; no zero fallback is used.",error);}
        return new(PayrollReferenceStatus.Resolved,output.Key,output.Unit,result,"Frozen nonfinancial payroll reference information.",declarationHash,Hash(binding),new ReadOnlyDictionary<string,string>(proofs));
    }
    private static decimal EvaluateOperation(PayrollReferenceOperation operation,Func<PayrollReferenceExpression,decimal> eval)
    {
        var a=operation.Arguments;decimal A()=>eval(a[0]);decimal B()=>eval(a[1]);
        return operation.Operator switch{
            PayrollReferenceOperator.Add=>a.Sum(eval),PayrollReferenceOperator.Subtract=>a.Skip(1).Aggregate(A(),(value,item)=>value-eval(item)),
            PayrollReferenceOperator.Multiply=>A()*B(),PayrollReferenceOperator.Divide=>A()/B(),PayrollReferenceOperator.Percent=>A()/100m,
            PayrollReferenceOperator.Abs=>Math.Abs(A()),PayrollReferenceOperator.Min=>a.Min(eval),PayrollReferenceOperator.Max=>a.Max(eval),
            PayrollReferenceOperator.Equal=>A()==B()?1:0,PayrollReferenceOperator.Less=>A()<B()?1:0,PayrollReferenceOperator.Greater=>A()>B()?1:0,
            PayrollReferenceOperator.If=>A()!=0?eval(a[1]):eval(a[2]),
            PayrollReferenceOperator.Round or PayrollReferenceOperator.RoundUp or PayrollReferenceOperator.RoundDown=>PayrollFormulaEvaluator.Evaluate(
                (operation.Operator==PayrollReferenceOperator.Round?"ROUND":operation.Operator==PayrollReferenceOperator.RoundUp?"ROUNDUP":"ROUNDDOWN")+"(REFERENCE_VALUE,"+operation.Digits!.Value.ToString(CultureInfo.InvariantCulture)+")",_=>A()),
            _=>throw new InvalidOperationException("Unsupported reference arithmetic.")};
    }
    internal static PayrollReferenceSnapshot Freeze(PayrollReferenceDeclaration declaration,PayrollReferenceBinding binding,PayrollReferenceProfile profile,
        PayrollReferenceField field,IReadOnlyList<PayrollReferenceInput>? providers)
    {
        var result=Resolve(declaration,binding,profile,field,providers);
        if(result.Status!=PayrollReferenceStatus.Resolved||result.Unit is null||result.Value is null)throw new InvalidOperationException("Only a resolved nonfinancial reference value can be frozen.");
        var proofs=new ReadOnlyDictionary<string,string>(new Dictionary<string,string>(result.InputProofs,StringComparer.Ordinal));
        var snapshot=new PayrollReferenceSnapshot(1,Purpose,binding.Scope,field,result.Key,result.Unit.Value,result.Value.Value,result.DeclarationHash,result.BindingHash,proofs,"");
        return snapshot with{SnapshotHash=Hash(snapshot)};
    }
    internal static void ValidateSnapshot(PayrollReferenceSnapshot snapshot,PayrollReferenceBinding binding,PayrollReferenceField field)
    {
        Field(field);Scope(binding.Scope);
        if(snapshot is null||snapshot.Version!=1||snapshot.Purpose!=Purpose||snapshot.Scope!=binding.Scope||snapshot.Field!=field||field!=binding.Field
            ||snapshot.OutputKey!=binding.OutputKey||snapshot.DeclarationHash!=binding.DeclarationHash||snapshot.BindingHash!=Hash(binding)||!Enum.IsDefined(snapshot.Unit)
            ||snapshot.Unit==PayrollReferenceUnit.Scalar||snapshot.InputProofs is null||snapshot.InputProofs.Any(pair=>!binding.InputProofs.TryGetValue(pair.Key,out var proof)||proof!=pair.Value)
            ||snapshot.SnapshotHash!=Hash(snapshot with{SnapshotHash=""}))throw new InvalidOperationException("Frozen reference snapshot ownership/schema/proof/value hash changed.");
        // This hash is a consistency check, not authentication. A future store
        // must retain it inside the existing protected server-only envelope.
    }
}
