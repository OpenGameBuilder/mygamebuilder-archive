using Xunit;

namespace OpenGameBuilder.Mgb.Archive.ClientArchiver.Tests;

public sealed class SeedFileParserTests
{
    [Fact]
    public void ParseReadsSeedsAndExcludes()
    {
        const string text = """
            # first pass
            domain mygamebuilder.com
            exclude https://www.mygamebuilder.com/forum/
            exclude-contains forum
            prefix https://s3.amazonaws.com/apphost/
            url https://example.com/file.js
            """;

        var (seeds, excludes) = SeedFileParser.Parse(text);

        Assert.Equal(3, seeds.Count);
        Assert.Equal(FrontendSeedKind.Domain, seeds[0].Kind);
        Assert.Equal(2, excludes.Count);
        Assert.Equal(FrontendExcludeKind.Prefix, excludes[0].Kind);
        Assert.Equal("http://mygamebuilder.com/forum/", excludes[0].CanonicalPrefix);
        Assert.Equal("mygamebuilder.com/forum/", excludes[0].HostPathPrefix);
        Assert.Equal(FrontendExcludeKind.Contains, excludes[1].Kind);
        Assert.Equal("forum", excludes[1].MatchText);
    }

    [Fact]
    public void ParseRejectsInvalidLine()
    {
        var ex = Assert.Throws<ArchiveFatalException>(() => SeedFileParser.Parse("domain https://mygamebuilder.com/"));

        Assert.Contains("domain seeds should be bare host names", ex.Message, StringComparison.Ordinal);
    }
}
