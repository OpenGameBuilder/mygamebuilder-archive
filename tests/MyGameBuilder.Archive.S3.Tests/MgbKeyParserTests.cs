using Xunit;

namespace MyGameBuilder.Archive.S3.Tests;

public sealed class MgbKeyParserTests
{
    [Theory]
    [InlineData("alice/project1/tile/Brick", "alice", "project1", "tile", "Brick")]
    [InlineData("!system/-/tutorial/001 Starting Tile Maker", "!system", "-", "tutorial", "001 Starting Tile Maker")]
    [InlineData("alice/-/profile/user", "alice", "-", "profile", "user")]
    public void TryParseAcceptsKnownMgbKeyShapes(string key, string user, string project, string pieceType, string pieceName)
    {
        Assert.True(MgbKeyParser.TryParse(key, out var part));
        Assert.Equal(user, part.UserName);
        Assert.Equal(project, part.ProjectName);
        Assert.Equal(pieceType, part.PieceType);
        Assert.Equal(pieceName, part.PieceName);
    }

    [Theory]
    [InlineData("alice/project1/not-a-piece/Thing")]
    [InlineData("alice/project1/tile")]
    [InlineData("alice//tile/Brick")]
    public void TryParseRejectsMalformedKeys(string key)
    {
        Assert.False(MgbKeyParser.TryParse(key, out _));
    }
}
