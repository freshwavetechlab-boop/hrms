using System.Text.Json;
using System.Text.Json.Nodes;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed partial class PayrollReferenceInformationTests
{
    private static (PayrollReferenceScope Scope,PayrollReferenceHistory History,PayrollReferenceNativeRow Native,JsonObject Receipt) EmptyDecisionEight(string family)
    {
        var c=ClosedRow();var template="source-template-"+family;
        c.History=c.History with{TemplateId=template};
        var rows=new JsonArray();var assignments=new JsonArray();var targets=new JsonArray();var selected=new JsonArray();
        for(var index=0;index<8;index++)
        {
            var employee=c.Scope.EmployeeId+index;var row=index==0?c.Scope.SourceRowId:"synthetic-"+family+"-"+index;
            rows.Add(JsonSerializer.SerializeToNode(new{employeeId=employee,rowId=row,historyId=42L+index,templateId=template}));
            assignments.Add(JsonSerializer.SerializeToNode(new{employeeId=employee,rowId=row,templateId=template}));
            targets.Add(JsonSerializer.SerializeToNode(new{employeeId=employee,monthlyCtcColumnIndex=3,sourceMonthlyBudget=54000,annualSalaryTarget=648000,sourceHash=c.Scope.SourceHash,meaning="MonthlyPayrollEarningsBudget",actualJoiningDateNotEstablished=true}));
            selected.Add(row);
        }
        c.Receipt["receipt"]!["rows"]=rows;c.Receipt["receipt"]!["initialSourceTargets"]=targets;
        c.Receipt["reviewedRequest"]!["assignments"]=assignments;c.Receipt["reviewedRequest"]!["setup"]!["rowIds"]=selected;
        c.Receipt["reviewedRequest"]!["setup"]!["employeeDecisions"]=new JsonArray();
        return c;
    }

    [Theory][InlineData("cap8")][InlineData("rate8")]
    public void Empty_optional_decisions_require_an_independent_verified_identity_and_complete_signed_eight_row_receipt(string family)
    {
        var c=EmptyDecisionEight(family);var protector=Protector();var envelope=Envelope(c.Receipt,protector);
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.AuthenticateClosedReceiptRowIdentity(c.Scope,envelope,protector));
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.AuthenticateClosedReceiptRowIdentity(c.Scope,envelope,protector,"not-verified"));
        var proof=PayrollReferenceInformation.Hash(new{Kind="Synthetic SELECT-verified existing identity",family,c.Scope.EmployeeId});
        var scope=c.Scope with{SourceIdentityHash=PayrollReferenceInformation.AuthenticateClosedReceiptRowIdentity(c.Scope,envelope,protector,proof)};
        var input=PayrollReferenceInformation.FromClosedInitialReceiptRow("ANNUAL_TARGET",scope,c.Native,[c.History],envelope,protector,proof);
        var field=Field(scope);var declaration=TargetDeclaration();
        Assert.Equal(54000,PayrollReferenceInformation.Resolve(declaration,Bind(declaration,scope,field,[input]),Profile(field),field,[input]).Value);
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.FromClosedInitialReceiptRow("ANNUAL_TARGET",scope,c.Native,[c.History],envelope,protector));
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.FromClosedInitialReceiptRow("ANNUAL_TARGET",scope,c.Native,[c.History],envelope,protector,H('f')));
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.FromClosedInitialReceipt("ANNUAL_TARGET",scope,c.Native,[c.History],envelope,protector));
    }

    [Theory][InlineData("partialDecision")][InlineData("wrongDecision")][InlineData("duplicateDecision")][InlineData("createDecision")]
    [InlineData("missingAssignment")][InlineData("duplicateTarget")][InlineData("missingSelected")][InlineData("signedBankIdentity")]
    public void Verified_empty_decision_mode_does_not_waive_nonempty_decisions_rosters_or_explicit_signed_bank_identity(string mutation)
    {
        var c=EmptyDecisionEight("cap8");var request=c.Receipt["reviewedRequest"]!;var receipt=c.Receipt["receipt"]!;
        if(mutation.EndsWith("Decision",StringComparison.Ordinal))
        {
            var decisions=request["assignments"]!.AsArray().Select(row=>JsonSerializer.SerializeToNode(new{rowId=row!["rowId"]!.ToString(),matchedEmployeeId=row["employeeId"]!.GetValue<int>(),allowCreate=false})!).ToArray();
            request["setup"]!["employeeDecisions"]=new JsonArray(decisions);
            var values=request["setup"]!["employeeDecisions"]!.AsArray();
            if(mutation=="partialDecision")values.RemoveAt(7);
            if(mutation=="wrongDecision")values[0]!["matchedEmployeeId"]=99;
            if(mutation=="duplicateDecision")values[7]=values[0]!.DeepClone();
            if(mutation=="createDecision")values[0]!["allowCreate"]=true;
        }
        if(mutation=="missingAssignment")request["assignments"]!.AsArray().RemoveAt(7);
        if(mutation=="duplicateTarget")receipt["initialSourceTargets"]!.AsArray().Add(receipt["initialSourceTargets"]![0]!.DeepClone());
        if(mutation=="missingSelected")request["setup"]!["rowIds"]!.AsArray().RemoveAt(7);
        if(mutation=="signedBankIdentity")receipt["sourceIdentities"]!.AsArray().Add(JsonSerializer.SerializeToNode(new{employeeId=c.Scope.EmployeeId,rowId=c.Scope.SourceRowId}));
        var protector=Protector();var envelope=Envelope(c.Receipt,protector);
        Assert.Throws<InvalidOperationException>(()=>PayrollReferenceInformation.AuthenticateClosedReceiptRowIdentity(c.Scope,envelope,protector,H('e')));
    }

    [Fact]
    public void Existing_nonempty_decision_identity_hash_remains_identical_even_when_a_new_proof_is_supplied()
    {
        var c=ClosedRow();var protector=Protector();var envelope=Envelope(c.Receipt,protector);
        Assert.Equal(PayrollReferenceInformation.AuthenticateClosedReceiptRowIdentity(c.Scope,envelope,protector),
            PayrollReferenceInformation.AuthenticateClosedReceiptRowIdentity(c.Scope,envelope,protector,H('e')));
    }
}
