using System.Buffers.Binary;
using PdfSharp.Drawing;

namespace Payroll.API.Services;

public sealed record DocumentSigningImageData(byte[] Bytes, string ContentType, int Width, int Height);

// Private, explicitly uploaded raster asset. It cannot contain paths, SVG, HTML
// or remote URLs, and is never inferred from an unrelated client's signatory.
public static class DocumentSigningImage
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    private const long MaximumPixels = 12_000_000;

    public static DocumentSigningImageData Validate(byte[] bytes)
    {
        if (bytes is null || bytes.Length is < 24 or > MaximumBytes)
            throw new InvalidOperationException("Upload one PNG or JPEG sign-and-stamp image of up to 2 MB.");
        var span = bytes.AsSpan();
        string type; int width; int height;
        if (span[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && span.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            type = "image/png";
            var w = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(16, 4));
            var h = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(20, 4));
            if (w > int.MaxValue || h > int.MaxValue) throw InvalidImage();
            width = (int)w; height = (int)h;
        }
        else if (span[0] == 0xff && span[1] == 0xd8)
        {
            type = "image/jpeg"; (width, height) = JpegSize(span);
        }
        else throw InvalidImage();
        CheckSize(width, height);
        try
        {
            using var stream = new MemoryStream(bytes, false);
            using var image = XImage.FromStream(stream);
            CheckSize(image.PixelWidth, image.PixelHeight);
            if (image.PixelWidth != width || image.PixelHeight != height) throw InvalidImage();
        }
        catch (Exception error) when (error is not (OutOfMemoryException or OperationCanceledException))
        { throw new InvalidOperationException("The sign-and-stamp image cannot be read. Upload a valid PNG or JPEG."); }
        return new(bytes.ToArray(), type, width, height);
    }

    private static void CheckSize(int width, int height)
    {
        if (width < 1 || height < 1 || width > 6000 || height > 6000 || (long)width * height > MaximumPixels)
            throw new InvalidOperationException("The sign-and-stamp image is too large. Use an image up to 6000 pixels per side and 12 megapixels.");
    }
    private static (int Width, int Height) JpegSize(ReadOnlySpan<byte> bytes)
    {
        var offset = 2;
        while (offset < bytes.Length)
        {
            if (bytes[offset++] != 0xff) throw InvalidImage();
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset >= bytes.Length) break;
            var marker = bytes[offset++];
            if (marker is 0xd9 or 0xda) break;
            if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
            if (offset + 2 > bytes.Length) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
            if (length < 2 || offset + length > bytes.Length) break;
            if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
            {
                if (length < 8) break;
                return (BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2)), BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 3, 2)));
            }
            offset += length;
        }
        throw InvalidImage();
    }
    private static InvalidOperationException InvalidImage() => new("Upload a valid PNG or JPEG sign-and-stamp image.");
}
