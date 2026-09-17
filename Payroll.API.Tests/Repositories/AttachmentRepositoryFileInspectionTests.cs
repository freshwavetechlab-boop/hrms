using System.IO.Compression;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class AttachmentRepositoryFileInspectionTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    [Fact]
    public void DetectMimeType_DocWithOleCompoundSignature_ReturnsMicrosoftWord()
    {
        var header = new byte[]
        {
            0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1,
            0x00, 0x00, 0x00, 0x00
        };

        var mime = DetectMimeType(header, "doc");

        Assert.Equal("application/msword", mime);
    }

    [Fact]
    public async Task OdtZip_WithContentAndDeclaredMimetype_IsRecognizedAndValidated()
    {
        var bytes = CreateOdt();
        using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "resume.odt");

        Assert.Equal("application/vnd.oasis.opendocument.text", DetectMimeType(bytes[..Math.Min(4096, bytes.Length)], "odt"));
        Assert.True(await IsOdtAsync(file));
    }

    [Fact]
    public void DetectMimeType_Utf16Txt_ReturnsPlainText()
    {
        var bytes = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("Résumé\r\nCloud engineer\r\n"))
            .ToArray();

        Assert.Equal("text/plain", DetectMimeType(bytes, "txt"));
    }

    [Fact]
    public void DetectMimeType_Latin1Txt_ReturnsPlainText()
    {
        var bytes = Encoding.Latin1.GetBytes("Résumé\r\nExpérience en déploiement cloud\r\n");

        Assert.Equal("text/plain", DetectMimeType(bytes, "txt"));
    }

    private static string? DetectMimeType(byte[] header, string extension)
    {
        var method = typeof(AttachmentRepository).GetMethod("DetectMimeType", PrivateStatic);
        Assert.NotNull(method);
        return (string?)method.Invoke(null, new object[] { header, extension });
    }

    private static async Task<bool> IsOdtAsync(IFormFile file)
    {
        var method = typeof(AttachmentRepository).GetMethod("IsOdtAsync", PrivateStatic);
        Assert.NotNull(method);
        var invocation = method.Invoke(null, new object[] { file, CancellationToken.None });
        var task = Assert.IsType<Task<bool>>(invocation);
        return await task;
    }

    private static byte[] CreateOdt()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var mimetype = archive.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var writer = new StreamWriter(mimetype.Open(), Encoding.ASCII, leaveOpen: false))
                writer.Write("application/vnd.oasis.opendocument.text");

            var content = archive.CreateEntry("content.xml");
            using var contentWriter = new StreamWriter(content.Open(), new UTF8Encoding(false), leaveOpen: false);
            contentWriter.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\"><office:body><office:text /></office:body></office:document-content>");
        }
        return memory.ToArray();
    }
}
