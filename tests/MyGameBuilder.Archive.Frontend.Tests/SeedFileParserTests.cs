using Xunit;

namespace MyGameBuilder.Archive.Frontend.Tests;

public sealed class SeedFileParserTests
{
    [Fact]
    public void ParseReadsSeedsAndExcludes()
    {
        const string text = """
            # first pass
            domain mygamebuilder.com
            exclude https://mygamebuilder.com/forum/
            prefix https://s3.amazonaws.com/apphost/
            url https://example.com/file.js
            """;

        var (seeds, excludes) = SeedFileParser.Parse(text);

        Assert.Equal(3, seeds.Count);
        Assert.Equal(FrontendSeedKind.Domain, seeds[0].Kind);
        Assert.Single(excludes);
        Assert.Equal("https://mygamebuilder.com/forum/", excludes[0].CanonicalPrefix);
        Assert.Equal("mygamebuilder.com/forum/", excludes[0].HostPathPrefix);
    }

    [Fact]
    public void ParseRejectsInvalidLine()
    {
        var ex = Assert.Throws<ArchiveFatalException>(() => SeedFileParser.Parse("domain https://mygamebuilder.com/"));

        Assert.Contains("domain seeds should be bare host names", ex.Message, StringComparison.Ordinal);
    }
}
