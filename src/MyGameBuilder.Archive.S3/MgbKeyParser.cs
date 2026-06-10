namespace MyGameBuilder.Archive.S3;

public static class MgbKeyParser
{
    private static readonly HashSet<string> PieceTypes = new(StringComparer.Ordinal)
    {
        "tile",
        "actor",
        "map",
        "screenshot",
        "profile",
        "tutorial"
    };

    public static bool TryParse(string key, out MgbKeyPart part)
    {
        part = null!;
        var pieces = key.Split('/', 4, StringSplitOptions.None);
        if (pieces.Length != 4)
        {
            return false;
        }

        if (pieces.Any(static piece => piece.Length == 0))
        {
            return false;
        }

        if (!PieceTypes.Contains(pieces[2]))
        {
            return false;
        }

        part = new MgbKeyPart(pieces[0], pieces[1], pieces[2], pieces[3]);
        return true;
    }
}
