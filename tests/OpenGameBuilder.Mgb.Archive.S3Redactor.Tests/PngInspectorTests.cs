using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace OpenGameBuilder.Mgb.Archive.S3Redactor.Tests;

public sealed class PngInspectorTests
{
    [Fact]
    public void CountsTransparentPixelsAsOneColor()
    {
        using var image = new Image<Rgba32>(2, 1);
        image[0, 0] = new Rgba32(255, 0, 0, 0);
        image[1, 0] = new Rgba32(0, 255, 0, 0);
        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());

        var result = PngInspector.Inspect(stream.ToArray());

        Assert.Equal(1, result.VisibleUniqueColorCount);
    }

    [Fact]
    public void CountsVisibleRgbaColors()
    {
        using var image = new Image<Rgba32>(2, 1);
        image[0, 0] = new Rgba32(255, 0, 0, 255);
        image[1, 0] = new Rgba32(0, 255, 0, 255);
        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());

        var result = PngInspector.Inspect(stream.ToArray());

        Assert.Equal(2, result.VisibleUniqueColorCount);
    }
}
