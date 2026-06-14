namespace OpenGameBuilder.Mgb.Archive.S3Redactor;

public sealed class ScreenshotPropagation
{
    private readonly ArchiveDb _archive;

    public ScreenshotPropagation(ArchiveDb archive)
    {
        _archive = archive;
    }

    public IReadOnlySet<long> FindScreenshotEntryIds(IReadOnlyList<ReviewCandidate> redactedCandidates)
    {
        var redactedTiles = redactedCandidates
            .Where(static c => string.Equals(c.PieceType, "tile", StringComparison.Ordinal) &&
                               c.UserName is not null &&
                               c.ProjectName is not null &&
                               c.PieceName is not null)
            .Select(static c => new MgbPieceKey(c.UserName!, c.ProjectName!, "tile", c.PieceName!))
            .ToHashSet(PieceKeyComparer.Instance);

        if (redactedTiles.Count == 0)
        {
            return new HashSet<long>();
        }

        var tileEntries = _archive.GetEntriesByPieceType("tile");
        var tileNamesByProject = BuildNamesByProject(tileEntries);
        var redactedTileNamesByProject = BuildNamesByProject(redactedTiles);
        var redactedSystemTileNames = redactedTiles
            .Where(static k => string.Equals(k.UserName, "!system", StringComparison.Ordinal))
            .Select(static k => k.PieceName)
            .ToHashSet(StringComparer.Ordinal);

        var actorEntries = _archive.GetEntriesByPieceType("actor");
        var actorNamesByProject = BuildNamesByProject(actorEntries);
        var impactedActors = new HashSet<MgbPieceKey>(PieceKeyComparer.Instance);
        foreach (var actor in actorEntries)
        {
            if (actor.UserName is null || actor.ProjectName is null || actor.PieceName is null)
            {
                continue;
            }

            IReadOnlySet<string> references;
            try
            {
                references = MgbDecoders.ReadActorTileReferences(actor.Body);
            }
            catch (Exception)
            {
                continue;
            }

            if (references.Any(name => ResolvesToRedacted(name, actor.UserName, actor.ProjectName, tileNamesByProject, redactedTileNamesByProject, redactedSystemTileNames)))
            {
                impactedActors.Add(new MgbPieceKey(actor.UserName, actor.ProjectName, "actor", actor.PieceName));
            }
        }

        if (impactedActors.Count == 0)
        {
            return new HashSet<long>();
        }

        var impactedActorNamesByProject = BuildNamesByProject(impactedActors);
        var impactedSystemActorNames = impactedActors
            .Where(static k => string.Equals(k.UserName, "!system", StringComparison.Ordinal))
            .Select(static k => k.PieceName)
            .ToHashSet(StringComparer.Ordinal);

        var impactedMaps = new HashSet<MgbPieceKey>(PieceKeyComparer.Instance);
        foreach (var map in _archive.GetEntriesByPieceType("map"))
        {
            if (map.UserName is null || map.ProjectName is null || map.PieceName is null)
            {
                continue;
            }

            IReadOnlySet<string> references;
            try
            {
                references = MgbDecoders.ReadMapActorReferences(map.Body);
            }
            catch (Exception)
            {
                continue;
            }

            if (references.Any(name => ResolvesToRedacted(name, map.UserName, map.ProjectName, actorNamesByProject, impactedActorNamesByProject, impactedSystemActorNames)))
            {
                impactedMaps.Add(new MgbPieceKey(map.UserName, map.ProjectName, "map", map.PieceName));
            }
        }

        if (impactedMaps.Count == 0)
        {
            return new HashSet<long>();
        }

        var screenshotIds = new HashSet<long>();
        foreach (var screenshot in _archive.GetEntriesByPieceType("screenshot"))
        {
            if (screenshot.UserName is null || screenshot.ProjectName is null || screenshot.PieceName is null)
            {
                continue;
            }

            if (impactedMaps.Contains(new MgbPieceKey(screenshot.UserName, screenshot.ProjectName, "map", screenshot.PieceName)))
            {
                screenshotIds.Add(screenshot.EntryId);
            }
        }

        return screenshotIds;
    }

    private static bool ResolvesToRedacted(
        string pieceName,
        string userName,
        string projectName,
        IReadOnlyDictionary<ProjectKey, HashSet<string>> allNamesByProject,
        IReadOnlyDictionary<ProjectKey, HashSet<string>> redactedNamesByProject,
        IReadOnlySet<string> redactedSystemNames)
    {
        var project = new ProjectKey(userName, projectName);
        if (allNamesByProject.TryGetValue(project, out var localNames) && localNames.Contains(pieceName))
        {
            return redactedNamesByProject.TryGetValue(project, out var redactedNames) && redactedNames.Contains(pieceName);
        }

        return redactedSystemNames.Contains(pieceName);
    }

    private static Dictionary<ProjectKey, HashSet<string>> BuildNamesByProject(IEnumerable<PngArchiveEntry> entries)
    {
        var map = new Dictionary<ProjectKey, HashSet<string>>();
        foreach (var entry in entries)
        {
            if (entry.UserName is null || entry.ProjectName is null || entry.PieceName is null)
            {
                continue;
            }

            Add(map, new ProjectKey(entry.UserName, entry.ProjectName), entry.PieceName);
        }

        return map;
    }

    private static Dictionary<ProjectKey, HashSet<string>> BuildNamesByProject(IEnumerable<MgbPieceKey> pieces)
    {
        var map = new Dictionary<ProjectKey, HashSet<string>>();
        foreach (var piece in pieces)
        {
            Add(map, new ProjectKey(piece.UserName, piece.ProjectName), piece.PieceName);
        }

        return map;
    }

    private static void Add(Dictionary<ProjectKey, HashSet<string>> map, ProjectKey project, string pieceName)
    {
        if (!map.TryGetValue(project, out var names))
        {
            names = new HashSet<string>(StringComparer.Ordinal);
            map.Add(project, names);
        }

        names.Add(pieceName);
    }

    private sealed record ProjectKey(string UserName, string ProjectName);

    private sealed class PieceKeyComparer : IEqualityComparer<MgbPieceKey>
    {
        public static PieceKeyComparer Instance { get; } = new();

        public bool Equals(MgbPieceKey? x, MgbPieceKey? y) =>
            x is not null &&
            y is not null &&
            string.Equals(x.UserName, y.UserName, StringComparison.Ordinal) &&
            string.Equals(x.ProjectName, y.ProjectName, StringComparison.Ordinal) &&
            string.Equals(x.PieceType, y.PieceType, StringComparison.Ordinal) &&
            string.Equals(x.PieceName, y.PieceName, StringComparison.Ordinal);

        public int GetHashCode(MgbPieceKey obj) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.UserName),
                StringComparer.Ordinal.GetHashCode(obj.ProjectName),
                StringComparer.Ordinal.GetHashCode(obj.PieceType),
                StringComparer.Ordinal.GetHashCode(obj.PieceName));
    }
}
