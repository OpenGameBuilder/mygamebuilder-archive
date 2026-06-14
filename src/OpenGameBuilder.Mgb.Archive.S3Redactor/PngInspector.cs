using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace OpenGameBuilder.Mgb.Archive.S3Redactor;

public sealed record PngInspectionResult(int Width, int Height, int VisibleUniqueColorCount);

public static class PngInspector
{
    private const ulong TransparentColor = ulong.MaxValue;

    public static bool HasPngSignature(byte[] body) =>
        body.Length >= 8 &&
        body[0] == 0x89 &&
        body[1] == 0x50 &&
        body[2] == 0x4E &&
        body[3] == 0x47 &&
        body[4] == 0x0D &&
        body[5] == 0x0A &&
        body[6] == 0x1A &&
        body[7] == 0x0A;

    public static PngInspectionResult Inspect(byte[] body, int? stopAfterUniqueColors = null)
    {
        using var image = Image.Load<Rgba32>(body);
        var uniqueColors = new HashSet<ulong>();

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];
                    var color = pixel.A == 0
                        ? TransparentColor
                        : ((ulong)pixel.R << 24) | ((ulong)pixel.G << 16) | ((ulong)pixel.B << 8) | pixel.A;
                    uniqueColors.Add(color);
                    if (stopAfterUniqueColors is { } limit && uniqueColors.Count >= limit)
                    {
                        return;
                    }
                }
            }
        });

        return new PngInspectionResult(image.Width, image.Height, uniqueColors.Count);
    }
}
