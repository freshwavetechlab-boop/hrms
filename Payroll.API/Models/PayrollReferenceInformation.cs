using System.Text.Json.Serialization;

namespace Payroll.API.Models;

// Numeric presentation data only. These types are not request DTOs, salary
// components or a new global payroll-token catalogue.
internal enum PayrollReferenceUnit { Scalar, Amount, DayUnits, PercentPoints }
internal enum PayrollReferenceInputMeaning { AnnualSalaryTarget, NativeMonthlySalaryTarget, TrueMonthlyBase, PayrollDivisor, RegularDutyUnits, ExtraDutyUnits, DeclaredOperand }
internal enum PayrollReferenceOutputMeaning { MonthlySalaryBudget, MonthlySalaryTarget, FullMonthReferenceAmount, PeriodInformationAmount, AggregatePeriodInformationAmount, DutyUnits }
internal enum PayrollReferenceStatus { Resolved, NotApplicable, Missing }
internal enum PayrollReferenceOperator { Add, Subtract, Multiply, Divide, Percent, Abs, Min, Max, Round, RoundUp, RoundDown, Equal, Less, Greater, If }

internal sealed record PayrollReferenceScope(int ClientId,int EmployeeId,int PayRunId,string PayPeriod,long NativeRowId,
    string NativeFinancialHash,string SourceBatchId,string SourceRowId,string SourceHash,string SourceIdentityHash);
internal sealed record PayrollReferenceField(int ClientId,int EmployeeId,int PayRunId,string PayPeriod,long TemplateId,
    string ProfileHash,int SourceColumn,string Label,string Kind,string NativeFinancialHash);
internal sealed record PayrollReferenceProfileColumn(int SourceColumn,string Label,string Kind);
internal sealed record PayrollReferenceProfile(int ClientId,long TemplateId,string ProfileHash,IReadOnlyList<PayrollReferenceProfileColumn> Columns);
internal sealed record PayrollReferenceInputDefinition(string Key,PayrollReferenceUnit Unit,PayrollReferenceInputMeaning Meaning,
    decimal Minimum=0,decimal Maximum=1_000_000_000m,int Precision=8);
internal sealed record PayrollReferenceOutputDefinition(string Key,PayrollReferenceUnit Unit,PayrollReferenceOutputMeaning Meaning,
    PayrollReferenceExpression Expression);
internal sealed record PayrollReferenceDeclaration(int Version,string Purpose,IReadOnlyList<PayrollReferenceInputDefinition> Inputs,
    IReadOnlyList<PayrollReferenceOutputDefinition> Outputs);
internal sealed record PayrollReferencePartEvidence(string PartKey,string AssignmentId,string SourceRowId,string SourceIdentityHash,string NativeInputHash,string TemplateHash,string ProofHash);
internal sealed record PayrollReferenceSelection(IReadOnlyList<string> CompletePartKeys,string ReferencePartKey,string ReferenceAssignmentId,string ProofHash,
    IReadOnlyList<PayrollReferencePartEvidence>? Parts=null);
internal sealed record PayrollReferenceBinding(int Version,PayrollReferenceScope Scope,PayrollReferenceField Field,string OutputKey,
    string DeclarationHash,IReadOnlyDictionary<string,string> InputProofs,PayrollReferenceSelection? ReferenceSelection=null);

[JsonPolymorphic(TypeDiscriminatorPropertyName="node")]
[JsonDerivedType(typeof(PayrollReferenceLiteral),"literal")]
[JsonDerivedType(typeof(PayrollReferenceInputExpression),"input")]
[JsonDerivedType(typeof(PayrollReferenceOutputExpression),"output")]
[JsonDerivedType(typeof(PayrollReferenceOperation),"operation")]
internal abstract record PayrollReferenceExpression;
internal sealed record PayrollReferenceLiteral(decimal Value,PayrollReferenceUnit Unit=PayrollReferenceUnit.Scalar):PayrollReferenceExpression;
internal sealed record PayrollReferenceInputExpression(string Key):PayrollReferenceExpression;
internal sealed record PayrollReferenceOutputExpression(string Key):PayrollReferenceExpression;
internal sealed record PayrollReferenceOperation(PayrollReferenceOperator Operator,IReadOnlyList<PayrollReferenceExpression> Arguments,int? Digits=null):PayrollReferenceExpression;

// The processing adapter must obtain these from the actual inserted row/trace
// epoch, not a simulation request, current Master read or source amount cache.
internal sealed record PayrollReferenceProcessingEvidence(string ProcessingTransactionId,string ProcessingEpochHash,string FinancialProfileHash,
    string InputSnapshotHash,string EffectiveTemplateHash,string OrdinaryEvidenceHash,string IndependentSourceProofHash);
internal sealed record PayrollReferenceHistory(long Id,int ClientId,int EmployeeId,string TemplateId,string ActionType,string Status,
    DateOnly EffectiveFrom,DateOnly EffectiveTo,decimal AnnualSalaryTarget,string RowHash);
internal sealed record PayrollReferenceNativeRow(long Id,int ClientId,int EmployeeId,int PayRunId,string PayPeriod,string FinancialHash);

internal sealed class PayrollReferenceInput
{
    internal PayrollReferenceInput(string key,PayrollReferenceScope scope,PayrollReferenceUnit unit,PayrollReferenceInputMeaning meaning,
        decimal? value,string proofHash,PayrollReferenceProcessingEvidence? processing,string? partKey=null,PayrollReferenceSelection? selection=null)
    {Key=key;Scope=scope;Unit=unit;Meaning=meaning;Value=value;ProofHash=proofHash;Processing=processing;PartKey=partKey;Selection=selection;}
    public string Key{get;}
    public PayrollReferenceScope Scope{get;}
    public PayrollReferenceUnit Unit{get;}
    public PayrollReferenceInputMeaning Meaning{get;}
    public decimal? Value{get;}
    public string ProofHash{get;}
    public PayrollReferenceProcessingEvidence? Processing{get;}
    public string? PartKey{get;}
    public PayrollReferenceSelection? Selection{get;}
}

internal sealed record PayrollReferenceResult(PayrollReferenceStatus Status,string Key,PayrollReferenceUnit? Unit,decimal? Value,
    string Reason,string DeclarationHash,string BindingHash,IReadOnlyDictionary<string,string> InputProofs);
internal sealed record PayrollReferenceSnapshot(int Version,string Purpose,PayrollReferenceScope Scope,PayrollReferenceField Field,
    string OutputKey,PayrollReferenceUnit Unit,decimal Value,string DeclarationHash,string BindingHash,
    IReadOnlyDictionary<string,string> InputProofs,string SnapshotHash);
