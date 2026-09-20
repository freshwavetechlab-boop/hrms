using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Services;

public sealed class EngineActivityStore(IConfiguration configuration,EngineHistoryStore history)
{
    public bool Enabled=>configuration.GetValue("EngineActivity:Enabled",true);
    public int RetentionDays=>Math.Clamp(configuration.GetValue("EngineActivity:RetentionDays",7),1,30);
    public string Deployment=>history.Deployment;
    public bool SaveUnavailable { get; set; }
    private bool initialized;
    private DateTime nextCleanup;
    public const string SchemaSql="""
CREATE TABLE IF NOT EXISTS engine_activity_log (
 Deployment VARCHAR(64) NOT NULL,Id CHAR(32) NOT NULL,EngineCode VARCHAR(40) NOT NULL,
 Operation VARCHAR(180) NOT NULL,ReferenceType VARCHAR(32) NOT NULL,ReferenceId VARCHAR(64) NOT NULL,
 ApplicationId BIGINT NULL,CandidateId BIGINT NULL,PositionId BIGINT NULL,ClientId INT NULL,Attempt INT NULL,
 StartedAtUtc DATETIME(6) NOT NULL,CompletedAtUtc DATETIME(6) NULL,UpdatedAtUtc DATETIME(6) NOT NULL,
 DurationMs DOUBLE NULL,QueueWaitMs DOUBLE NULL,AiDurationMs DOUBLE NULL,AiStatus VARCHAR(40) NOT NULL,
 Status VARCHAR(32) NOT NULL,HttpStatus INT NULL,FailureCode VARCHAR(80) NOT NULL,Revision BIGINT NOT NULL,
 PRIMARY KEY(Deployment,Id),INDEX ix_engine_activity_time(Deployment,StartedAtUtc,Id),
 INDEX ix_engine_activity_filter(Deployment,EngineCode,StartedAtUtc,Id)
);
""";
    private static CommandDefinition Command(string sql,object? args,CancellationToken ct)=>new(sql,args,commandTimeout:5,cancellationToken:ct);
    public async Task SaveAsync(IReadOnlyList<EngineActivityRow> rows,CancellationToken ct)
    {
        if(!Enabled) return;
        if(initialized && rows.Count==0 && DateTime.UtcNow<nextCleanup) return;
        await using var db=new MySqlConnection(configuration.GetConnectionString("Default"));await db.OpenAsync(ct);
        if(!initialized) { await db.ExecuteAsync(Command(SchemaSql,null,ct));initialized=true; }
        if(rows.Count>0)
        {
            var columns=new[]{"Id","EngineCode","Operation","ReferenceType","ReferenceId","ApplicationId","CandidateId","PositionId","ClientId","Attempt","StartedAtUtc","CompletedAtUtc","UpdatedAtUtc","DurationMs","QueueWaitMs","AiDurationMs","AiStatus","Status","HttpStatus","FailureCode","Revision"};
            var parameters=new DynamicParameters(new { Deployment });var values=new List<string>();
            for(var i=0;i<rows.Count;i++)
            {
                foreach(var column in columns) parameters.Add(column+i,typeof(EngineActivityRow).GetProperty(column)!.GetValue(rows[i]));
                values.Add("(@Deployment,"+string.Join(',',columns.Select(c=>"@"+c+i))+")");
            }
            // Same operation ID on retries: no duplicate task or attempt records.
            await db.ExecuteAsync(Command("INSERT INTO engine_activity_log (Deployment,"+string.Join(',',columns)+") VALUES "+string.Join(',',values)
                +" ON DUPLICATE KEY UPDATE "+string.Join(',',columns.Where(c=>c is not ("Id" or "Revision")).Select(c=>$"{c}=IF(VALUES(Revision)>=Revision,VALUES({c}),{c})"))
                +",Revision=GREATEST(Revision,VALUES(Revision))",parameters,ct));
        }
        SaveUnavailable=false;
        if(DateTime.UtcNow>=nextCleanup)
        {
            await db.ExecuteAsync(Command("DELETE FROM engine_activity_log WHERE Deployment=@Deployment AND StartedAtUtc<@Cutoff LIMIT 5000",new { Deployment,Cutoff=DateTime.UtcNow.AddDays(-RetentionDays) },ct));
            nextCleanup=DateTime.UtcNow.AddHours(1);
        }
    }
    public async Task<EngineActivityPage> ReadAsync(DateTimeOffset from,DateTimeOffset until,string? engine,string? status,
        DateTimeOffset? before,string? beforeId,CancellationToken ct)
    {
        Validate(from,until,engine,status,before,beforeId);
        var result=new EngineActivityPage { Deployment=Deployment,RecordingEnabled=Enabled,RetentionDays=RetentionDays,
            Warning=SaveUnavailable ? "Activity saving is temporarily unavailable; engines continue normally." : "" };
        await using var db=new MySqlConnection(configuration.GetConnectionString("Default"));await db.OpenAsync(ct);
        var rows=(await db.QueryAsync<EngineActivityRow>(Command("""
SELECT Id,EngineCode,Operation,ReferenceType,ReferenceId,ApplicationId,CandidateId,PositionId,ClientId,Attempt,
StartedAtUtc,CompletedAtUtc,UpdatedAtUtc,DurationMs,QueueWaitMs,AiDurationMs,AiStatus,HttpStatus,FailureCode,
CASE WHEN Status='Running' AND UpdatedAtUtc<@Stale THEN 'Interrupted' ELSE Status END Status
FROM engine_activity_log WHERE Deployment=@Deployment AND StartedAtUtc>=@From AND StartedAtUtc<@Until
AND (@Engine='' OR EngineCode=@Engine)
AND (@Status='' OR (CASE WHEN Status='Running' AND UpdatedAtUtc<@Stale THEN 'Interrupted' ELSE Status END)=@Status)
AND (@Before IS NULL OR StartedAtUtc<@Before OR (StartedAtUtc=@Before AND Id<@BeforeId))
ORDER BY StartedAtUtc DESC,Id DESC LIMIT 51
""",new { Deployment,From=from.UtcDateTime,Until=until.UtcDateTime,Engine=engine??"",Status=status??"",
            Before=before?.UtcDateTime,BeforeId=beforeId??"",Stale=DateTime.UtcNow.AddMinutes(-5) },ct))).ToList();
        result.HasMore=rows.Count>50;
        result.Items=rows.Take(50).Select(row=>row with { FailureReason=EngineFailureCatalog.Describe(row.FailureCode),StartedAtUtc=Utc(row.StartedAtUtc),UpdatedAtUtc=Utc(row.UpdatedAtUtc),CompletedAtUtc=row.CompletedAtUtc.HasValue ? Utc(row.CompletedAtUtc.Value) : null }).ToList();
        try { result.Items=await EnrichAsync(db,result.Items,ct); }
        catch(Exception) when(!ct.IsCancellationRequested) { result.Warning+=" Related names are unavailable; stable task references are still shown."; }
        return result;
    }
    internal static void Validate(DateTimeOffset from,DateTimeOffset until,string? engine,string? status,DateTimeOffset? before,string? beforeId)
    {
        if(until<=from || until-from>TimeSpan.FromDays(31) || from.UtcDateTime<DateTime.UtcNow.AddDays(-31) || from.UtcDateTime>DateTime.UtcNow)
            throw new ArgumentException("Choose a range within the last 30 days, no longer than 31 days.");
        if(!string.IsNullOrEmpty(engine) && !EngineRuntimeMonitor.EngineNames.ContainsKey(engine)) throw new ArgumentException("Unknown engine.");
        if(!string.IsNullOrEmpty(status) && status is not ("Running" or "Completed" or "Failed" or "Rejected" or "Retry" or "Cancelled" or "NeedsReview" or "Interrupted")) throw new ArgumentException("Unknown outcome.");
        if(before.HasValue != !string.IsNullOrEmpty(beforeId) || !string.IsNullOrEmpty(beforeId) && !Guid.TryParseExact(beforeId,"N",out _)) throw new ArgumentException("Invalid activity cursor.");
    }
    private static DateTime Utc(DateTime value)=>DateTime.SpecifyKind(value,DateTimeKind.Utc);
    private static async Task<List<EngineActivityRow>> EnrichAsync(MySqlConnection db,List<EngineActivityRow> rows,CancellationToken ct)
    {
        var apps=rows.Where(r=>r.ApplicationId>0).Select(r=>r.ApplicationId!.Value).Distinct().ToArray();
        var appNames=apps.Length==0 ? [] : (await db.QueryAsync<EngineActivityRow>(Command("""
SELECT a.Id ApplicationId,a.ApplicationCode,TRIM(CONCAT(c.FirstName,' ',c.LastName)) CandidateName,p.PositionTitle
FROM recruitment_candidate_applications a LEFT JOIN recruitment_candidates c ON c.Id=a.CandidateId
LEFT JOIN recruitment_open_positions p ON p.Id=a.PositionId WHERE a.Id IN @Ids
""",new { Ids=apps },ct))).ToList();
        var candidates=rows.Where(r=>r.CandidateId>0 && r.ApplicationId is null).Select(r=>r.CandidateId!.Value).Distinct().ToArray();
        var candidateNames=candidates.Length==0 ? [] : (await db.QueryAsync<EngineActivityRow>(Command("SELECT Id CandidateId,TRIM(CONCAT(FirstName,' ',LastName)) CandidateName FROM recruitment_candidates WHERE Id IN @Ids",new { Ids=candidates },ct))).ToList();
        var positions=rows.Where(r=>r.PositionId>0 && r.ApplicationId is null).Select(r=>r.PositionId!.Value).Distinct().ToArray();
        var positionNames=positions.Length==0 ? [] : (await db.QueryAsync<EngineActivityRow>(Command("SELECT Id PositionId,PositionTitle FROM recruitment_open_positions WHERE Id IN @Ids",new { Ids=positions },ct))).ToList();
        return rows.Select(row=> {
            var app=appNames.FirstOrDefault(a=>a.ApplicationId==row.ApplicationId);
            return row with { ApplicationCode=app?.ApplicationCode??"",CandidateName=app?.CandidateName??candidateNames.FirstOrDefault(c=>c.CandidateId==row.CandidateId)?.CandidateName??"",
                PositionTitle=app?.PositionTitle??positionNames.FirstOrDefault(p=>p.PositionId==row.PositionId)?.PositionTitle??"" };
        }).ToList();
    }
}
