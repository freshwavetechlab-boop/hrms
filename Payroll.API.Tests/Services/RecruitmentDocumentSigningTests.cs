using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Payroll.API.Tests.Services;

public sealed class RecruitmentDocumentSigningTests
{
    private static string Body(bool slot = true, bool flow = false)
    {
        var layout = new RecruitmentDocumentLayout
        {
            Sections = Enumerable.Range(0, 8).Select(index => new DocumentSection
            {
                Elements = [new() { X = 65, Y = 150, Width = 400, Height = 30, Text = "{{candidateName}} · {{workLocationName}}" }]
            }).ToList()
        };
        if (slot && flow)
            layout.Sections[4] = new() { Flow = true, Blocks = [new() { Kind = "signature", Height = 36, Text = "[Signature & Stamp of Signatory]" }, new() { Text = "Signatory name remains unchanged" }] };
        else if (slot)
            layout.Sections[4].Elements.Add(new() { Kind = "signature", X = 65, Y = 252, Width = 220, Height = 36, Text = "[Signature & Stamp of Signatory]" });
        return LayoutDocumentPdfService.Marker + JsonSerializer.Serialize(layout, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
    private static readonly Dictionary<string, string> Values = new() { ["candidateName"] = "Synthetic Example", ["workLocationName"] = "Example Institute - Mumbai" };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Applying_image_only_changes_the_signature_slot_and_keeps_eight_pages(bool flow)
    {
        var renderer = new LayoutDocumentPdfService(); var image = SyntheticPng();
        var before = JsonSerializer.Serialize(Values);
        var unsigned = renderer.Create(Body(flow: flow), Values, draft: false);
        var signed = renderer.Create(Body(flow: flow), Values, draft: false, signatureAndStamp: image);
        Assert.Equal("", signed.Error); Assert.NotNull(signed.Bytes); Assert.NotNull(unsigned.Bytes);
        using var signedPdf = PdfReader.Open(new MemoryStream(signed.Bytes!), PdfDocumentOpenMode.Import);
        using var unsignedPdf = PdfReader.Open(new MemoryStream(unsigned.Bytes!), PdfDocumentOpenMode.Import);
        Assert.Equal(8, signedPdf.PageCount); Assert.Equal(8, unsignedPdf.PageCount);
        Assert.Equal(0, ImageCount(unsignedPdf.Pages[4])); Assert.True(ImageCount(signedPdf.Pages[4]) > 0);
        Assert.All(signedPdf.Pages.Cast<PdfPage>().Where((_, index) => index != 4), page => Assert.Equal(0, ImageCount(page)));
        Assert.Equal(before, JsonSerializer.Serialize(Values));
    }

    [Fact]
    public void Draft_generation_cannot_apply_an_image_and_missing_slots_do_not_silently_drop_it()
    {
        var renderer = new LayoutDocumentPdfService();
        var draft = renderer.Create(Body(), Values, signatureAndStamp: SyntheticPng());
        Assert.Null(draft.Bytes); Assert.Contains("Draft PDFs remain unsigned", draft.Error);
        var missing = renderer.Create(Body(slot: false), Values, draft: false, signatureAndStamp: SyntheticPng());
        Assert.Null(missing.Bytes); Assert.Contains("slot", missing.Error);
        Assert.NotNull(renderer.Create(Body(), Values).Bytes);
    }

    [Fact]
    public void Existing_text_placeholder_works_without_changing_legacy_layout_json()
    {
        var layout = LayoutDocumentPdfService.Parse(Body());
        var slot = layout.Sections[4].Elements[^1]; slot.Kind = "text"; slot.Width = 465; slot.Height = 14.7;
        layout.Sections[4].Elements.Add(new() { X = 65, Y = 292, Width = 465, Height = 14.7, Text = "Original signatory name" });
        var body = LayoutDocumentPdfService.Marker + JsonSerializer.Serialize(layout, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("\"kind\":\"signature\"", body);
        var plain = new LayoutDocumentPdfService().Create(body, Values, draft: false);
        var signed = new LayoutDocumentPdfService().Create(body, Values, draft: false, signatureAndStamp: SyntheticPng());
        Assert.Equal("", signed.Error); Assert.NotNull(plain.Bytes); Assert.NotNull(signed.Bytes);
        using var parsed = PdfReader.Open(new MemoryStream(signed.Bytes!), PdfDocumentOpenMode.Import);
        Assert.Equal(8, parsed.PageCount); Assert.True(ImageCount(parsed.Pages[4]) > 0);
        Assert.True(LayoutDocumentPdfService.HasSignatureSlot(layout));
    }

    [Fact]
    public void Raster_upload_validation_rejects_remote_active_malformed_and_oversized_content()
    {
        var image = SyntheticPng(); var result = DocumentSigningImage.Validate(image);
        Assert.Equal("image/png", result.ContentType); Assert.Equal(24, result.Width); Assert.Equal(12, result.Height);
        Assert.Equal(image, result.Bytes); Assert.NotSame(image, result.Bytes);
        foreach (var invalid in new[] { Array.Empty<byte>(), Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"), Encoding.UTF8.GetBytes("https://example.test/sign.png"), image[..24], new byte[DocumentSigningImage.MaximumBytes + 1] })
            Assert.Throws<InvalidOperationException>(() => DocumentSigningImage.Validate(invalid));
        var bomb = image.ToArray(); BinaryPrimitives.WriteUInt32BigEndian(bomb.AsSpan(16, 4), 6001);
        Assert.Throws<InvalidOperationException>(() => DocumentSigningImage.Validate(bomb));
    }

    [Fact]
    public void Signature_slots_reject_external_assets_and_invalid_dimensions()
    {
        var layout = LayoutDocumentPdfService.Parse(Body());
        layout.Sections[4].Elements[^1].Asset = "https://example.test/sign.png";
        Assert.Throws<InvalidOperationException>(() => LayoutDocumentPdfService.Validate(layout));
        layout.Sections[4].Elements[^1].Asset = ""; layout.Sections[4].Elements[^1].Height = 0;
        Assert.Throws<InvalidOperationException>(() => LayoutDocumentPdfService.Validate(layout));
    }

    private static int ImageCount(PdfPage page)
    {
        var objects = page.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/XObject");
        return objects?.Elements.Values.Count(value => (value is PdfReference reference ? reference.Value as PdfDictionary : value as PdfDictionary)?.Elements.GetName("/Subtype") == "/Image") ?? 0;
    }

    // Deterministic synthetic raster: no real signatory's image in test fixtures.
    internal static byte[] SyntheticPng()
    {
        using var result = new MemoryStream(); result.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string name, byte[] data)
        {
            var size = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(size, data.Length); result.Write(size);
            var typed = Encoding.ASCII.GetBytes(name).Concat(data).ToArray(); result.Write(typed); uint crc = 0xffffffff;
            foreach (var value in typed) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0); }
            BinaryPrimitives.WriteUInt32BigEndian(size, crc ^ 0xffffffff); result.Write(size);
        }
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), 24); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), 12); header[8] = 8; header[9] = 2;
        Chunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true))
            for (var row = 0; row < 12; row++) { zlib.WriteByte(0); for (var col = 0; col < 24; col++) zlib.Write(new byte[] { 30, 120, 200 }); }
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []); return result.ToArray();
    }
}
