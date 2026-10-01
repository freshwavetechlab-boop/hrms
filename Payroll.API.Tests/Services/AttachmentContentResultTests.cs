using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class AttachmentContentResultTests
{
    [Theory]
    [InlineData("resume.pdf", "application/pdf", true, "inline")]
    [InlineData("photo.png", "image/png", true, "inline")]
    [InlineData("JD.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", true, "attachment")]
    [InlineData("data.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", true, "attachment")]
    [InlineData("resume.pdf", "application/pdf", false, "attachment")]
    public async Task Files_KeepOriginalBytesAndUseTheCorrectDisposition(string fileName, string mime, bool preview, string disposition)
    {
        var bytes = Encoding.UTF8.GetBytes("Original document bytes");
        var result = await ReadAsync(bytes, fileName, mime, preview);
        Assert.Equal(bytes, result.Bytes);
        Assert.Equal(mime, result.Context.Response.ContentType);
        Assert.StartsWith(disposition + ";", result.Context.Response.Headers.ContentDisposition.ToString());
    }

    [Fact]
    public async Task MissingLocalFile_UsesTheSharedTicketOnTheProductionApi()
    {
        var result = await ReadAsync(null, remoteAvailable: true);
        Assert.Equal(200, result.Context.Response.StatusCode);
        Assert.Equal("%PDF-remote original", Encoding.UTF8.GetString(result.Bytes));
        Assert.Equal(1, result.RemoteRequests);
    }

    [Theory]
    [InlineData(false, true, "/api/public/attachments/content", 0)]
    [InlineData(true, true, "/api/attachments/file/content", 0)]
    [InlineData(true, false, "/api/public/attachments/content", 1)]
    public async Task MissingFile_ShowsAnExplanationWithoutAStackTrace(bool development, bool remoteAvailable, string route, int requests)
    {
        var result = await ReadAsync(null, development: development, remoteAvailable: remoteAvailable, route: route);
        Assert.Equal(404, result.Context.Response.StatusCode);
        Assert.Contains("restore or re-upload", Encoding.UTF8.GetString(result.Bytes));
        Assert.DoesNotContain("FileNotFoundException", Encoding.UTF8.GetString(result.Bytes));
        Assert.Equal(requests, result.RemoteRequests);
    }

    private static async Task<(DefaultHttpContext Context, byte[] Bytes, int RemoteRequests)> ReadAsync(byte[]? bytes,
        string fileName = "resume.pdf", string mime = "application/pdf", bool preview = true,
        bool development = true, bool remoteAvailable = false, string route = "/api/public/attachments/content")
    {
        var folder = Path.Combine(Path.GetTempPath(), "frevo-preview-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "attachment");
        try
        {
            if (bytes is not null) await File.WriteAllBytesAsync(file, bytes);
            var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AttachmentStorage:RemoteReadBaseUrl"] = "https://production.example" }).Build();
            var factory = new RemoteFileFactory(remoteAvailable);
            using var services = new ServiceCollection().AddSingleton<IConfiguration>(settings)
                .AddSingleton<IHostEnvironment>(new Host { EnvironmentName = development ? "Development" : "Production" })
                .AddSingleton<IHttpClientFactory>(factory).BuildServiceProvider();
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Host = new HostString("localhost", 5062);
            context.Request.Path = route;
            context.Request.QueryString = new QueryString("?token=unit-test-ticket");
            using var output = new MemoryStream();
            context.Response.Body = output;
            var storage = new AttachmentStorageService(null!, null!, settings, null!);
            await new AttachmentContentResult(storage,
                new AttachmentStorageServer { StorageType = "MountedFileSystem", BasePath = folder },
                new EntityAttachment { StorageKey = "attachment", OriginalFileName = fileName, DetectedMimeType = mime, FileSizeBytes = bytes?.Length ?? 20 }, preview).ExecuteAsync(context);
            return (context, output.ToArray(), factory.Requests);
        }
        finally { File.Delete(file); Directory.Delete(folder); }
    }

    private sealed class Host : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RemoteFileFactory(bool available) : HttpMessageHandler, IHttpClientFactory
    {
        public int Requests { get; private set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal("https://production.example/api/public/attachments/content?token=unit-test-ticket", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(available ? HttpStatusCode.OK : HttpStatusCode.NotFound) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("%PDF-remote original")) });
        }
    }
}
