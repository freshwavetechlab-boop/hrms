using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MimeKit;

namespace Payroll.API.Tests.Repositories;

// Private test-only SMTP sink. Never forwards mail or binds a non-loopback interface.
// Signed links remain in memory; no MIME bodies or credentials are printed/persisted.
internal sealed class LoopbackInterviewSmtp : IAsyncDisposable
{
    internal sealed record Delivery(string Recipient, string Subject, string Html);
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task run;
    public ConcurrentQueue<Delivery> Deliveries { get; } = new();
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public LoopbackInterviewSmtp() { listener.Start(); run = ReceiveAsync(); }

    private async Task ReceiveAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
                await writer.WriteLineAsync("220 loopback.invalid ESMTP test sink");
                var recipients = new List<string>();
                while (await reader.ReadLineAsync(stop.Token) is { } line)
                {
                    if (line.StartsWith("EHLO ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("HELO ", StringComparison.OrdinalIgnoreCase))
                        await writer.WriteLineAsync("250 loopback.invalid");
                    else if (line.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase)) { recipients.Clear(); await writer.WriteLineAsync("250 OK"); }
                    else if (line.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase))
                    {
                        var address = line[(line.IndexOf('<') + 1)..line.IndexOf('>')];
                        if (!address.EndsWith("@example.invalid", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("SMTP fixture refuses a non-synthetic recipient.");
                        recipients.Add(address); await writer.WriteLineAsync("250 OK");
                    }
                    else if (line == "DATA")
                    {
                        await writer.WriteLineAsync("354 End with a single dot");
                        var body = new StringBuilder();
                        while (await reader.ReadLineAsync(stop.Token) is { } part && part != ".")
                        {
                            body.Append(part.StartsWith("..", StringComparison.Ordinal) ? part[1..] : part).Append("\r\n");
                            if (body.Length > 1_000_000) throw new InvalidOperationException("Fixture message is too large.");
                        }
                        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(body.ToString()));
                        using var message = MimeMessage.Load(bytes);
                        foreach (var recipient in recipients) Deliveries.Enqueue(new(recipient, message.Subject, message.HtmlBody ?? ""));
                        await writer.WriteLineAsync("250 Accepted by isolated test sink");
                    }
                    else if (line == "QUIT") { await writer.WriteLineAsync("221 Bye"); break; }
                    else if (line == "RSET") { recipients.Clear(); await writer.WriteLineAsync("250 OK"); }
                    else await writer.WriteLineAsync("502 Unsupported by fixture");
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop();
        try { await run; } finally { stop.Dispose(); }
    }
}
