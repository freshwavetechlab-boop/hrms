using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using System.Text.Json;

namespace Payroll.API.Tests.Repositories;

// Opt-in browser test with the actual Program, auth cookies and existing recruitment APIs.
// The full legacy migrator is run ONLY against this newly created loopback GUID database.
public sealed class InternalInterviewPortalTests
{
    [InternalInterviewPortalTheory]
    [InlineData("Human")]
    [InlineData("AI")]
    [InlineData("Hybrid")]
    public async Task Existing_login_schedule_feedback_and_internal_session_use_real_persistence(string mode)
    {
        var repo = Path.GetFullPath(Environment.GetEnvironmentVariable("HRMS_INTERVIEW_REPO")!);
        var connection = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("HRMS_INTERNAL_INTERVIEW_TEST_CONNECTION")!);
        Assert.Contains(connection.Server, new[] { "localhost", "127.0.0.1", "::1" });
        var database = "hrms_interview_portal_" + Guid.NewGuid().ToString("N");
        connection.Database = "";
        await using var owner = new MySqlConnection(connection.ConnectionString); await owner.OpenAsync();
        // Use the loopback server's utf8mb4 default, like the existing local deployment.
        // Legacy migrations compare CAST(... AS CHAR) using the server connection collation.
        await owner.ExecuteAsync($"CREATE DATABASE `{database}` CHARACTER SET utf8mb4");
        connection.Database = database;
        var temporary = Path.Combine(Path.GetTempPath(), database); Directory.CreateDirectory(temporary);
        var apiDll = Path.Combine(repo, ".codex-validation/internal-interviews/bin/Payroll.API/debug/Payroll.API.dll");
        var port = FreePort();
        await using var smtp = new LoopbackInterviewSmtp();
        var environment = new Dictionary<string, string> {
            ["ConnectionStrings__Default"] = connection.ConnectionString,
            ["ASPNETCORE_ENVIRONMENT"] = "Development", ["DOTNET_ENVIRONMENT"] = "Development",
            ["BackgroundWorkers__Enabled"] = "false", ["Database__AutoMigrate"] = "false",
            ["OutboundDelivery__Suppressed"] = "false", // this GUID DB points ONLY to the loopback sink below
            ["EngineHistory__Enabled"] = "false", ["EngineActivity__Enabled"] = "false",
            ["InternalInterviews__Enabled"] = "true", ["FrevoPilot__Enabled"] = "false",
            ["InternalInterviews__PublicPortalBaseUrl"] = Environment.GetEnvironmentVariable("HRMS_TEST_UI_URL")!,
            ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
            ["AttachmentStorage__DataRootPath"] = temporary,
            ["AttachmentStorage__RootPath"] = Path.Combine(temporary, "attachments"),
            ["AttachmentStorage__DataProtectionKeyPath"] = Path.Combine(temporary, "keys"),
            ["Logging__LogLevel__Default"] = "Warning"
        };
        Process? api = null;
        Task<string>? apiErrors = null;
        Task<string>? apiOutput = null;
        using var workerStop = new CancellationTokenSource();
        Task? worker = null;
        Exception? workerFailure = null;
        try
        {
            Assert.True(File.Exists(apiDll), "Build the API with the internal-interview artifacts path first.");
            await Execute("dotnet", [apiDll, "--migrate"], repo, environment, TimeSpan.FromMinutes(3));
            await Execute("dotnet", [apiDll, "--migrate-internal-interviews"], repo, environment, TimeSpan.FromMinutes(1));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connection.ConnectionString, ["InternalInterviews:Enabled"] = "true" }).Build();
            var auth = new AuthRepository(configuration);
            var password = "Synthetic-" + Guid.NewGuid().ToString("N") + "!9";
            // Fresh databases do not necessarily seed the production super_admin role.
            // Provision a fixture-only HR role through the existing repository instead.
            await auth.SaveRoleAsync(new SaveAuthRoleRequest { Code = "interview_fixture_hr", Name = "Isolated interview HR",
                Permissions = (await auth.GetPermissionsAsync()).Select(p => p.Code).ToList() });
            var user = await auth.SaveUserAsync(new SaveAuthUserRequest { Email = "interview.panel@example.invalid", DisplayName = "Isolated Interview Panel", Password = password, MustChangePassword = false, Roles = ["interview_fixture_hr"] });
            Assert.NotNull(user);
            Assert.Contains("recruitment.interview.schedule", user.Permissions);
            Assert.NotNull(await auth.LoginAsync(new LoginRequest { Email = user.Email, Password = password, Portal = "Admin" }, "127.0.0.1", "Isolated fixture password validation"));
            await using var db = new MySqlConnection(connection.ConnectionString); await db.OpenAsync();
            await db.ExecuteAsync("""
                INSERT INTO clients (Id,Name,Code) VALUES (901,'Isolated Interview Client','INTERVIEW-TEST');
                INSERT INTO recruitment_open_positions (Id,RequisitionId,PositionCode,ClientId,PositionTitle,JobLocation)
                VALUES (901,901,'POS-INTERVIEW-TEST',901,'Isolated Test Engineer','Test Campus');
                INSERT INTO recruitment_candidates (Id,CandidateCode,ClientId,FirstName,LastName,Email,PreferredLocationsJson,CreatedByUserId)
                VALUES (901,'CAN-INTERVIEW-TEST',901,'Isolated','Candidate','candidate@example.invalid','[]',@UserId);
                INSERT INTO recruitment_candidate_applications (Id,ApplicationCode,CandidateId,PositionId,ClientId,CurrentStatus,CurrentStage)
                VALUES (901,'APP-INTERVIEW-TEST',901,901,901,'Shortlisted','Shortlisted');
                """, new { UserId = user.Id });
            await db.ExecuteAsync("""
                UPDATE notification_smtp_settings SET IsEnabled=TRUE,DeliveryPaused=FALSE,Host='127.0.0.1',Port=@Port,
                    EnableSsl=FALSE,UserName='',Password='',FromEmail='fixture@example.invalid',FromName='Isolated test sink' WHERE Id=1;
                """, new { smtp.Port });
            if (mode == "Human")
                await db.ExecuteAsync("""
                    INSERT INTO recruitment_candidates (Id,CandidateCode,ClientId,FirstName,LastName,Email,PreferredLocationsJson,CreatedByUserId)
                    VALUES (902,'CAN-BATCH-ONE',901,'Batch','One','batch.one@example.invalid','[]',@UserId),
                           (903,'CAN-BATCH-TWO',901,'Batch','Two','batch.two@example.invalid','[]',@UserId);
                    INSERT INTO recruitment_candidate_applications (Id,ApplicationCode,CandidateId,PositionId,ClientId,CurrentStatus,CurrentStage)
                    VALUES (902,'APP-BATCH-ONE',902,901,901,'Interview','Interview'),(903,'APP-BATCH-TWO',903,901,901,'Interview','Interview');
                    INSERT INTO recruitment_application_scores (ApplicationId,ResumeId,TotalScore,ComponentScoresJson,MatchedSkillsJson,MissingSkillsJson,ExplanationJson,ScoringMethod)
                    VALUES (902,0,80,'{}','[]','[]','{}','Isolated fixture'),(903,0,80,'{}','[]','[]','{}','Isolated fixture');
                    """, new { UserId = user.Id });
            api = Start("dotnet", [apiDll], repo, environment);
            apiErrors = api.StandardError.ReadToEndAsync(); apiOutput = api.StandardOutput.ReadToEndAsync();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var ready = false;
            for (var i = 0; i < 60 && !api.HasExited; i++)
            {
                try { using var response = await http.GetAsync($"http://127.0.0.1:{port}/api/auth/me"); ready = response.StatusCode == HttpStatusCode.Unauthorized; if (ready) break; } catch (HttpRequestException) { }
                await Task.Delay(500);
            }
            Assert.True(ready, "Isolated API did not become ready.");
            // Run the actual durable queue/repository orchestration against this GUID database.
            // ONLY the model is mocked; no test-only runtime API/production worker switches.
            var internalRepository = new InternalInterviewRepository(configuration, TimeProvider.System);
            var model = new BrowserInterviewModel();
            var ai = new InternalInterviewAiService(model);
            worker = Task.Run(async () => {
                try {
                    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
                    while (await timer.WaitForNextTickAsync(workerStop.Token))
                        foreach (var id in await internalRepository.PendingAiAsync())
                            await internalRepository.ProcessAiAsync(id, ai, workerStop.Token);
                } catch (OperationCanceledException) when (workerStop.IsCancellationRequested) { }
                catch (Exception error) { workerFailure = error; }
            });
            await Execute("node", ["tools/test-internal-interview-portal.mjs"], repo, new Dictionary<string, string> {
                ["HRMS_TEST_API_URL"] = $"http://127.0.0.1:{port}",
                ["HRMS_TEST_UI_URL"] = Environment.GetEnvironmentVariable("HRMS_TEST_UI_URL")!,
                ["HRMS_TEST_PASSWORD"] = password, ["HRMS_TEST_USER_ID"] = user.Id.ToString(), ["HRMS_TEST_INTERVIEW_MODE"] = mode
            }, TimeSpan.FromMinutes(6));
            workerStop.Cancel(); await worker;
            Assert.Null(workerFailure);
            // Independent database assertions; no replay of UI-generated model SQL.
            Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_interviews WHERE ApplicationId=901"));
            Assert.Equal("Completed", await db.ExecuteScalarAsync<string>("SELECT Status FROM recruitment_internal_interview_sessions"));
            Assert.Equal("Pending", await db.ExecuteScalarAsync<string>("SELECT Result FROM recruitment_interviews WHERE ApplicationId=901"));
            Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_interview_feedback"));
            Assert.True(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM authsessions") > 0);
            Assert.True(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_internal_interview_events WHERE Kind='answer'") > 0);
            var expectedAnswers = mode == "Human" ? 1 : mode == "AI" ? 2 : 3;
            Assert.Equal(expectedAnswers, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_internal_interview_events WHERE Kind='answer'"));
            Assert.Equal(expectedAnswers, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_internal_interview_events WHERE Kind='ai-review'"));
            Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_internal_interview_events WHERE Kind='ai-summary'"));
            Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_internal_interview_events WHERE Kind='ai-error'"));
            Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_internal_interview_events WHERE Kind='ai-retry'"));
            Assert.Equal(expectedAnswers + 1, model.Reviews); // one malformed reply, then explicit human retry
            Assert.Equal(mode == "Human" ? 0 : 1, model.Followups);
            Assert.Equal(1, model.Summaries);
            Assert.InRange(await db.ExecuteScalarAsync<int>("SELECT TIMESTAMPDIFF(SECOND,CreatedAtUtc,UTC_TIMESTAMP()) FROM recruitment_internal_interview_events ORDER BY Id DESC LIMIT 1"), 0, 120);
            var delivered = smtp.Deliveries.ToArray();
            Assert.Equal(mode == "Human" ? 8 : 4, delivered.Length);
            Assert.Equal(delivered.Length, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM notification_queue WHERE EventCode='RECRUITMENT_INTERVIEW_INVITE' AND Status='Sent'"));
            Assert.Equal(delivered.Length, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM notification_logs WHERE EventCode='RECRUITMENT_INTERVIEW_INVITE' AND Status='Sent'"));
            var candidateInvites = delivered.Where(mail => mail.Recipient == "candidate@example.invalid").ToArray();
            Assert.Equal(2, candidateInvites.Length);
            // Verify private candidate credentials never appear in panel emails. Use Boolean assertions
            // so an unexpected payload cannot leak signed links through the test runner's diff output.
            Assert.All(candidateInvites, mail => Assert.True(mail.Html.Contains("#access=") && mail.Html.Contains("Candidate-only expiring link")));
            var panelCredentialLeaked = delivered.Any(mail => mail.Recipient == user.Email && mail.Html.Contains("#access="));
            Assert.False(panelCredentialLeaked);
            Assert.Equal(2, delivered.Count(mail => mail.Recipient == user.Email && mail.Html.Contains("/recruitment/interview-session/")));
            if (mode == "Human")
            {
                Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_interviews WHERE ApplicationId IN (902,903) AND LocationOrLink='https://meeting.example.invalid/batch' AND Status='Scheduled' AND Result='Pending'"));
                Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM recruitment_internal_interview_sessions"));
                Assert.Equal(4, delivered.Count(mail => mail.Html.Contains("https://meeting.example.invalid/batch") && !mail.Html.Contains("#access=")));
            }
        }
        finally
        {
            workerStop.Cancel(); if (worker is not null) await worker;
            if (api is not null) { if (!api.HasExited) api.Kill(true); await api.WaitForExitAsync(); api.Dispose(); }
            // Do not print process environment, tokens or login payloads.
            if (apiErrors is not null) await apiErrors;
            if (apiOutput is not null) await apiOutput;
            MySqlConnection.ClearAllPools();
            await owner.ExecuteAsync($"DROP DATABASE `{database}`");
            var resolved = Path.GetFullPath(temporary);
            Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetTempPath(), database)), resolved);
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    private sealed class BrowserInterviewModel : IInternalInterviewLanguageModel
    {
        public int Reviews { get; private set; }
        public int Followups { get; private set; }
        public int Summaries { get; private set; }
        public Task<JsonElement> GenerateAsync(string prompt, string instruction, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using var input = JsonDocument.Parse(prompt);
            string response;
            if (input.RootElement.TryGetProperty("followUps", out var allowed)) { Assert.True(allowed.GetArrayLength() > 0); Followups++; response = "{\"index\":0}"; }
            else if (input.RootElement.TryGetProperty("answers", out var answers)) {
                Summaries++;
                Assert.True(answers.GetArrayLength() > 0);
                response = JsonSerializer.Serialize(new { observations = new[] {
                    new { kind = "interviewSummary", note = "Synthetic combined draft: discusses tenant access testing.", evidence = new[] {
                        new { answer = 0, quote = answers[0].GetProperty("text").GetString() }
                    } }
                } });
            }
            else {
                Reviews++;
                response = Reviews == 1 ? "[]" : JsonSerializer.Serialize(new { observations = new[] {
                    new { kind = "answerSummary", note = "Synthetic model draft: describes access-test evidence.", evidenceQuote = input.RootElement.GetProperty("answer").GetProperty("Text").GetString() }
                } });
            }
            using var json = JsonDocument.Parse(response);
            return Task.FromResult(json.RootElement.Clone());
        }
    }

    private static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static Process Start(string command, string[] args, string directory, Dictionary<string, string> environment)
    {
        var info = new ProcessStartInfo(command) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        return Process.Start(info) ?? throw new InvalidOperationException("Cannot start isolated test process.");
    }
    private static async Task Execute(string command, string[] args, string directory, Dictionary<string, string> environment, TimeSpan timeout)
    {
        using var process = Start(command, args, directory, environment);
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(cancellation.Token); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        var report = (await output) + (await errors);
        Assert.True(process.ExitCode == 0, $"Isolated {command} exited {process.ExitCode}: {report[..Math.Min(report.Length, 7000)]}");
    }
}

public sealed class InternalInterviewPortalTheoryAttribute : TheoryAttribute
{
    public InternalInterviewPortalTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HRMS_TEST_UI_URL")) || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HRMS_INTERVIEW_REPO")))
            Skip = "Opt-in actual-API browser fixture requires a loopback UI and repository root.";
    }
}
