using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class RecruitmentInterviewRoundsTests
{
    private static List<RecruitmentInterviewRoundDefinition> Definitions() => [new() { Name="Technical L1" },new() { Name="Technical L2" },new() { Name="Client discussion",InterviewType="Client" }];
    private static RecruitmentInterviewRounds.RoundAttempt Attempt(long id,string name,string status="Completed",string result="Selected") => new() { Id=id,RoundCode=name,Status=status,Result=result };

    [Fact]
    public void Next_round_is_available_only_after_previous_round_clears()
    {
        var definitions=Definitions();
        Assert.Equal([true,false,false],RecruitmentInterviewRounds.States(definitions,[]).Select(r=>r.CanSchedule));
        var one=RecruitmentInterviewRounds.States(definitions,[Attempt(1,"Technical L1")]);
        Assert.Equal([true,false,false],one.Select(r=>r.Passed));
        Assert.Equal([false,true,false],one.Select(r=>r.CanSchedule));
        var active=RecruitmentInterviewRounds.States(definitions,[Attempt(1,"Technical L1"),Attempt(2,"Technical L2","Scheduled","Pending")]);
        Assert.DoesNotContain(active,r=>r.CanSchedule);
    }

    [Fact]
    public void All_rounds_must_pass_and_a_later_failed_attempt_supersedes_an_earlier_pass()
    {
        var attempts=new[] { Attempt(1,"Technical L1"),Attempt(2,"Technical L2"),Attempt(3,"Client discussion") };
        Assert.All(RecruitmentInterviewRounds.States(Definitions(),attempts),r=>Assert.True(r.Passed));
        var retry=RecruitmentInterviewRounds.States(Definitions(),attempts.Append(Attempt(4,"Technical L2","No Show","No Show")));
        Assert.False(retry[1].Passed);Assert.True(retry[1].CanSchedule);
        Assert.False(retry.All(r=>r.Passed));
    }

    [Theory]
    [InlineData("Cancelled","Pending",true)]
    [InlineData("No Show","No Show",true)]
    [InlineData("Completed","On Hold",true)]
    [InlineData("Completed","Rejected",false)]
    [InlineData("Rescheduled","Pending",false)]
    public void Retry_and_active_schedule_have_distinct_states(string status,string result,bool expected) =>
        Assert.Equal(expected,RecruitmentInterviewRounds.States(Definitions(),[Attempt(1,"Technical L1",status,result)])[0].CanSchedule);

    [Fact]
    public void Scheduled_round_cannot_be_removed_renamed_retyped_or_reordered()
    {
        var before=Definitions();var used=new HashSet<string>(["Technical L1"],StringComparer.OrdinalIgnoreCase);
        Assert.Empty(RecruitmentInterviewRounds.ValidateDefinitions(before,before,used));
        Assert.NotEmpty(RecruitmentInterviewRounds.ValidateDefinitions(before.Skip(1).ToList(),before,used));
        var renamed=Definitions();renamed[0].Name="Changed";
        Assert.NotEmpty(RecruitmentInterviewRounds.ValidateDefinitions(renamed,before,used));
        var retyped=Definitions();retyped[0].InterviewType="HR";
        Assert.NotEmpty(RecruitmentInterviewRounds.ValidateDefinitions(retyped,before,used));
        var extra=Definitions();extra.Add(new() { Name="HR final",InterviewType="HR" });
        Assert.Empty(RecruitmentInterviewRounds.ValidateDefinitions(extra,before,used));
    }

    [Fact]
    public void Names_are_required_unique_and_fit_the_existing_round_column()
    {
        Assert.NotEmpty(RecruitmentInterviewRounds.ValidateDefinitions([],[],new HashSet<string>()));
        var duplicate=Definitions();duplicate[1].Name="technical l1";
        Assert.NotEmpty(RecruitmentInterviewRounds.ValidateDefinitions(duplicate,[],new HashSet<string>()));
        var tooLong=Definitions();tooLong[1].Name=new string('x',81);
        Assert.NotEmpty(RecruitmentInterviewRounds.ValidateDefinitions(tooLong,[],new HashSet<string>()));
    }

    [Fact]
    public void Settings_are_isolated_by_client_job_and_stage_without_exceeding_the_existing_key_column()
    {
        var key=RecruitmentInterviewRounds.Key(20,18,"INTERVIEW_PANEL_ASSESSMENT");
        Assert.NotEqual(key,RecruitmentInterviewRounds.Key(21,18,"INTERVIEW_PANEL_ASSESSMENT"));
        Assert.NotEqual(key,RecruitmentInterviewRounds.Key(20,19,"INTERVIEW_PANEL_ASSESSMENT"));
        Assert.NotEqual(key,RecruitmentInterviewRounds.Key(20,18,"FINAL_PANEL"));
        Assert.True(RecruitmentInterviewRounds.Key(int.MaxValue,long.MaxValue,new string('X',80)).Length<=80);
    }
}
