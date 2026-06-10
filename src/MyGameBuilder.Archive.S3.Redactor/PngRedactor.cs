using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace MyGameBuilder.Archive.S3.Redactor;

public static class PngRedactor
{
    public static byte[] CreateBlackPng(byte[] originalBody)
    {
        using var original = Image.Load<Rgba32>(originalBody);
        using var image = new Image<Rgba32>(original.Width, original.Height, new Rgba32(0, 0, 0, 255));
        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());
        return stream.ToArray();
    }

    public static string Sha256Hex(byte[] body) => Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
}
