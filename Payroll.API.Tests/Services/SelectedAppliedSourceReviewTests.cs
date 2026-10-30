using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;
using Xunit;

namespace Payroll.API.Tests.Services;

// Pure anonymous binding tests. No API, database, payroll or adjustment is written.
public class SelectedPayRunAppliedSourceReviewTests
{
    private static string H(char c) => new(c,64);
    private static CreatePayRunRequest Request() => new()
    {
        ClientId=7, PayPeriod="2026-09", PayDate=new(2026,9,28), TotalWorkingDays=30,
        RunType="Regular", IncludedEmployeeIds=[11], AdjustmentIds=[101],
        AttendanceGroupId=21, AttendanceGroupIds=[21], AttendancePolicyBatchId="synthetic-policy"
    };
    private static SelectedAppliedSourceWitness Witness(CreatePayRunRequest request, string? business=null) =>
        SelectedPayRunAppliedSourceReview.BuildWitness(request,H('a'),H('b'),H('c'),H('d'),H('e'),H('f'),H('0'),H('1'),H('2'),business??H('3'));
    private static SelectedPayRunAdjustmentReview Review(CreatePayRunRequest request) => new(101,11,"TA_DA","Reimbursement",22680m,false,
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","11111111-1111-4111-8111-111111111111",9,H('b'),H('a'),H('3'))
        {SourceWitness=Witness(request)};
    private static void Acknowledge(CreatePayRunRequest request,SelectedPayRunAdjustmentReview review) =>
        request.AdjustmentSourceReviewAcknowledgement=new(review.SourceWitness!.WitnessFingerprint,true);

    [Fact]
    public void Canonical_hash_sorts_nested_object_keys_and_normalizes_decimal_spelling()
    {
        using var left=JsonDocument.Parse("{\"z\":{\"b\":1.00,\"a\":null},\"a\":[1,2]}");
        using var right=JsonDocument.Parse("{\"a\":[1.0,2],\"z\":{\"a\":null,\"b\":1}}");
        Assert.Equal(SelectedPayRunAppliedSourceReview.Hash(left.RootElement),SelectedPayRunAppliedSourceReview.Hash(right.RootElement));
    }

    [Fact]
    public void Canonical_hash_preserves_array_order_null_and_actual_business_timestamps()
    {
        var first=SelectedPayRunAppliedSourceReview.Hash(new { values=new[]{1,2},UpdatedAt=new DateTime(2026,9,1) });
        Assert.NotEqual(first,SelectedPayRunAppliedSourceReview.Hash(new { values=new[]{2,1},UpdatedAt=new DateTime(2026,9,1) }));
        Assert.NotEqual(first,SelectedPayRunAppliedSourceReview.Hash(new { values=new[]{1,2},UpdatedAt=new DateTime(2026,9,2) }));
        Assert.NotEqual(SelectedPayRunAppliedSourceReview.Hash(new { a=(string?)null }),SelectedPayRunAppliedSourceReview.Hash(new Dictionary<string,object?>()));
    }

    [Theory]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("1e100")]
    public void Ambiguous_or_unsupported_canonical_values_fail_closed(string json)
    {
        using var document=JsonDocument.Parse(json);
        Assert.Throws<SelectedPayRunConflictException>(()=>SelectedPayRunAppliedSourceReview.Hash(document.RootElement));
    }

    [Fact]
    public void Effective_group_set_and_policy_are_normalized_but_server_metadata_is_excluded()
    {
        var first=Request(); first.AttendanceGroupIds=[22,21];
        var second=Request(); second.AttendanceGroupIds=[21,22]; second.AttendancePolicyBatchId=" synthetic-policy ";
        second.SelectedAdjustmentReviews=[Review(second)];
        second.AdjustmentSourceReviewAcknowledgement=new(H('a'),false);
        second.SourceReviewAttestation=new(77,new(2026,9,28,0,0,0,DateTimeKind.Utc),H('a'));
        Assert.Equal(SelectedPayRunAppliedSourceReview.ScopeFingerprint(first),SelectedPayRunAppliedSourceReview.ScopeFingerprint(second));
        second.AttendanceGroupId=0; second.AttendanceGroupIds=[21];
        var fallback=Request(); fallback.AttendanceGroupIds=[];
        Assert.Equal(SelectedPayRunAppliedSourceReview.ScopeFingerprint(fallback),SelectedPayRunAppliedSourceReview.ScopeFingerprint(second));
    }

    [Theory]
    [InlineData("client")] [InlineData("period")] [InlineData("days")] [InlineData("employee")]
    [InlineData("adjustment")] [InlineData("policy")] [InlineData("group")] [InlineData("paydate")]
    public void Each_relevant_scope_change_has_a_distinct_review_fingerprint(string change)
    {
        var before=Request(); var after=Request();
        switch(change)
        {
            case "client": after.ClientId=8;break;
            case "period": after.PayPeriod="2026-10";after.PayDate=new(2026,10,28);break;
            case "days": after.TotalWorkingDays=31;break;
            case "employee": after.IncludedEmployeeIds=[12];break;
            case "adjustment": after.AdjustmentIds=[102];break;
            case "policy": after.AttendancePolicyBatchId="other-policy";break;
            case "group": after.AttendanceGroupId=22;after.AttendanceGroupIds=[22];break;
            case "paydate": after.PayDate=new(2026,9,29);break;
        }
        Assert.NotEqual(Witness(before).WitnessFingerprint,Witness(after).WitnessFingerprint);
    }

    [Theory]
    [InlineData("two-employees")] [InlineData("two-adjustments")] [InlineData("duplicate-adjustments")]
    [InlineData("no-employee")] [InlineData("no-adjustment")] [InlineData("null-adjustments")]
    [InlineData("exclusion")] [InlineData("offcycle")] [InlineData("wrong-paydate")]
    [InlineData("no-policy")] [InlineData("duplicate-groups")] [InlineData("mixed-primary-group")]
    public void Source_witness_never_infers_or_broadens_the_initial_one_by_one_scope(string mutation)
    {
        var request=Request();
        switch(mutation)
        {
            case "two-employees":request.IncludedEmployeeIds=[11,12];break;
            case "two-adjustments":request.AdjustmentIds=[101,102];break;
            case "duplicate-adjustments":request.AdjustmentIds=[101,101];break;
            case "no-employee":request.IncludedEmployeeIds=[];break;
            case "no-adjustment":request.AdjustmentIds=[];break;
            case "null-adjustments":request.AdjustmentIds=null!;break;
            case "exclusion":request.ExcludedEmployeeIds=[12];break;
            case "offcycle":request.RunType="Off Cycle";break;
            case "wrong-paydate":request.PayDate=new(2026,10,1);break;
            case "no-policy":request.AttendancePolicyBatchId=" ";break;
            case "duplicate-groups":request.AttendanceGroupIds=[21,21];break;
            case "mixed-primary-group":request.AttendanceGroupId=22;break;
        }
        Assert.Throws<SelectedPayRunConflictException>(()=>Witness(request));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    public void Every_proof_and_business_fingerprint_is_mandatory(int absent)
    {
        var values=Enumerable.Range(0,10).Select(index=>H((char)('a'+index%6))).ToArray();values[absent]="";
        Assert.Throws<SelectedPayRunConflictException>(()=>SelectedPayRunAppliedSourceReview.BuildWitness(Request(),
            values[0],values[1],values[2],values[3],values[4],values[5],values[6],values[7],values[8],values[9]));
    }

    [Fact]
    public void Fresh_acknowledgement_is_bound_to_the_complete_distinct_historical_zero_witness()
    {
        var request=Request();var review=Review(request);Acknowledge(request,review);
        SelectedPayRunAppliedSourceReview.RequireAcknowledged(request,review,77);
        Assert.Equal(SelectedPayRunAppliedSourceReview.Kind,review.SourceWitness!.Kind);
        Assert.Equal(0,review.SourceWitness.HistoricalTransferVersion);
        Assert.Null(request.SourceReviewAttestation);
        Assert.Null(request.SelectedAdjustmentReviews);
    }

    [Theory]
    [InlineData("missing")] [InlineData("unconfirmed")] [InlineData("different")] [InlineData("uppercase")]
    [InlineData("invalid-actor")] [InlineData("caller-attestation")]
    public void No_boolean_or_server_metadata_can_replace_the_exact_current_confirmation(string change)
    {
        var request=Request();var review=Review(request);Acknowledge(request,review);var actor=77;
        switch(change)
        {
            case "missing":request.AdjustmentSourceReviewAcknowledgement=null;break;
            case "unconfirmed":request.AdjustmentSourceReviewAcknowledgement=request.AdjustmentSourceReviewAcknowledgement! with{Confirmed=false};break;
            case "different":request.AdjustmentSourceReviewAcknowledgement=new(H('f'),true);break;
            case "uppercase":request.AdjustmentSourceReviewAcknowledgement=new(review.SourceWitness!.WitnessFingerprint.ToUpperInvariant(),true);break;
            case "invalid-actor":actor=0;break;
            case "caller-attestation":request.SourceReviewAttestation=new(77,new(2026,9,28,0,0,0,DateTimeKind.Utc),review.SourceWitness!.WitnessFingerprint);break;
        }
        Assert.Throws<SelectedPayRunConflictException>(()=>SelectedPayRunAppliedSourceReview.RequireAcknowledged(request,review,actor));
    }

    [Theory]
    [InlineData("missing-witness")] [InlineData("kind")] [InlineData("historical-version")]
    [InlineData("business")] [InlineData("source")] [InlineData("receipt")]
    [InlineData("formula")] [InlineData("native")] [InlineData("salary-policy")]
    [InlineData("calculation")] [InlineData("comparison")] [InlineData("aggregate")]
    public void Forged_or_stale_proof_members_do_not_pass_a_previously_matching_acknowledgement(string change)
    {
        var request=Request();var review=Review(request);Acknowledge(request,review);var witness=review.SourceWitness!;
        review=change switch
        {
            "missing-witness"=>review with{SourceWitness=null},
            "kind"=>review with{SourceWitness=witness with{Kind="TransferV1"}},
            "historical-version"=>review with{SourceWitness=witness with{HistoricalTransferVersion=1}},
            "business"=>review with{BusinessFingerprint=H('e')},
            "source"=>review with{SourceHash=H('e')},
            "receipt"=>review with{ReceiptFingerprint=H('e')},
            "formula"=>review with{SourceWitness=witness with{SourceFormulaFingerprint=H('4')}},
            "native"=>review with{SourceWitness=witness with{NativeInputFingerprint=H('4')}},
            "salary-policy"=>review with{SourceWitness=witness with{SalaryPolicyFingerprint=H('4')}},
            "calculation"=>review with{SourceWitness=witness with{CoreCalculationFingerprint=H('4')}},
            "comparison"=>review with{SourceWitness=witness with{ComparisonFingerprint=H('4')}},
            _=>review with{SourceWitness=witness with{WitnessFingerprint=H('4')}}
        };
        Assert.Throws<SelectedPayRunConflictException>(()=>SelectedPayRunAppliedSourceReview.RequireAcknowledged(request,review,77));
    }

    [Theory]
    [InlineData("missing")] [InlineData("actor")] [InlineData("date")] [InlineData("local-date")] [InlineData("hash")]
    public void Held_processing_requires_a_persisted_server_attestation_for_the_same_stable_witness(string change)
    {
        var request=Request();var review=Review(request);
        request.SourceReviewAttestation=new(77,new(2026,9,28,0,0,0,DateTimeKind.Utc),review.SourceWitness!.WitnessFingerprint);
        request.SourceReviewAttestation=change switch
        {
            "missing"=>null,
            "actor"=>request.SourceReviewAttestation with{ConfirmedByUserId=0},
            "date"=>request.SourceReviewAttestation with{ConfirmedAtUtc=default},
            "local-date"=>request.SourceReviewAttestation with{ConfirmedAtUtc=new(2026,9,28)},
            _=>request.SourceReviewAttestation with{WitnessFingerprint=H('a')}
        };
        Assert.Throws<SelectedPayRunConflictException>(()=>SelectedPayRunAppliedSourceReview.RequireStoredAttestation(request,review));
    }

    [Fact]
    public void Stored_attestation_is_stable_without_regenerating_a_reviewer_or_clock_value()
    {
        var request=Request();var review=Review(request);
        var attestation=new SelectedSourceReviewAttestation(77,new(2026,9,28,0,0,0,DateTimeKind.Utc),review.SourceWitness!.WitnessFingerprint);
        request.SourceReviewAttestation=attestation;
        SelectedPayRunAppliedSourceReview.RequireStoredAttestation(request,review);
        SelectedPayRunAppliedSourceReview.RequireStoredAttestation(request,review);
        Assert.Same(attestation,request.SourceReviewAttestation);
        Assert.Equal(Witness(request),review.SourceWitness);
    }

    [Fact]
    public void Existing_default_and_transfer_one_serialization_does_not_emit_the_new_optional_metadata()
    {
        var request=Request();request.AdjustmentIds=[];
        var requestJson=JsonSerializer.Serialize(request);
        Assert.DoesNotContain("SourceReview",requestJson,StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedAdjustmentReviews",requestJson,StringComparison.Ordinal);
        var old=Review(Request()) with{SourceWitness=null};
        Assert.DoesNotContain("SourceWitness",JsonSerializer.Serialize(old),StringComparison.Ordinal);
    }
}
