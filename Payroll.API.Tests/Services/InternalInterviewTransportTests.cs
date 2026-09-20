using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class InternalInterviewTransportTests
{
    [Theory]
    [InlineData("<html>Not found</html>", false)]
    [InlineData("{}", false)]
    [InlineData("{\"code\":\"unauthorized\"}", false)]
    [InlineData("{\"code\":\"not_found\"}", true)]
    public async Task Room_absence_requires_a_protocol_confirmation_not_a_generic_proxy_404(string response, bool confirmed)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["InternalInterviews:LiveKitUrl"] = "ws://127.0.0.1:7880", ["InternalInterviews:LiveKitApiUrl"] = "http://127.0.0.1:7880",
            ["InternalInterviews:LiveKitApiKey"] = "test-key", ["InternalInterviews:LiveKitApiSecret"] = "synthetic-secret-never-for-deployment"
        }).Build();
        var transport = new InternalInterviewTransport(config, new Factory(response), TimeProvider.System);
        var room = Guid.NewGuid().ToString("N");
        if (confirmed) await transport.CloseRoomAsync(room, default);
        else Assert.Equal(502, (await Assert.ThrowsAsync<InternalInterviewException>(() => transport.CloseRoomAsync(room, default))).StatusCode);
    }
    [Theory]
    [InlineData(5)]
    [InlineData(60)]
    [InlineData(240)]
    public async Task Room_survives_reconnect_gap_until_bounded_schedule_end(int remainingMinutes)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["InternalInterviews:LiveKitUrl"] = "ws://127.0.0.1:7880", ["InternalInterviews:LiveKitApiUrl"] = "http://127.0.0.1:7880",
            ["InternalInterviews:LiveKitApiKey"] = "test-key", ["InternalInterviews:LiveKitApiSecret"] = "synthetic-secret-never-for-deployment"
        }).Build();
        var http = new Factory("{}", HttpStatusCode.OK);
        var transport = new InternalInterviewTransport(config, http, TimeProvider.System);
        var session = new InternalInterviewSession { RoomId = Guid.NewGuid().ToString("N"), Status = "Live", ConsentAtUtc = DateTime.UtcNow,
            ScheduledEndUtc = DateTime.UtcNow.AddMinutes(remainingMinutes) };
        await transport.CreateRoomAsync(session, 2, default);
        using var json = JsonDocument.Parse(http.Payload);
        var lifetime = json.RootElement.GetProperty("empty_timeout").GetInt32();
        Assert.InRange(lifetime, remainingMinutes * 60 + 118, remainingMinutes * 60 + 120);
        Assert.Equal(lifetime, json.RootElement.GetProperty("departure_timeout").GetInt32());
        Assert.DoesNotContain("egress", http.Payload); // recording was not consented/configured
        session.ScheduledEndUtc = DateTime.UtcNow.AddSeconds(-1);
        Assert.Equal(409, (await Assert.ThrowsAsync<InternalInterviewException>(() => transport.CreateRoomAsync(session, 2, default))).StatusCode);
        Assert.Equal(1, http.Calls);
    }
    private sealed class Factory(string text, HttpStatusCode status = HttpStatusCode.NotFound) : HttpMessageHandler, IHttpClientFactory
    {
        public string Payload { get; private set; } = "";
        public int Calls { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Payload = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(text) };
        }
    }
}
