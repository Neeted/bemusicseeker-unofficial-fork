using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models.LR2;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal enum Lr2FolderRowSourceKind
{
    NormalDirectory = 1,
    FolderInfoDirectory = 2,
    PlaylistOutputLr2Folder = 3,
    DiscoveredLr2Folder = 4
}

internal sealed class Lr2FolderDirectoryMetadata(DateTime? lastWriteTimeUtc, string folderInfoTitle = null)
{
    public DateTime? LastWriteTimeUtc { get; } = lastWriteTimeUtc;

    public string FolderInfoTitle { get; } = folderInfoTitle;

    public bool HasFolderInfoTitle => !string.IsNullOrWhiteSpace(FolderInfoTitle);
}

internal sealed class Lr2FolderGenerationRequest
{
    public IReadOnlyCollection<string> RootDirectories { get; set; } = [];

    public IReadOnlyCollection<string> ChartPaths { get; set; } = [];

    public IReadOnlyCollection<LR2SongDB.folder> ExistingRows { get; set; } = [];

    public Func<string, Lr2FolderDirectoryMetadata> DirectoryMetadataResolver { get; set; }

    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class Lr2FolderGenerationResult(
    IReadOnlyList<LR2SongDB.folder> rows,
    IReadOnlyCollection<string> scopePaths,
    IReadOnlyDictionary<string, Lr2FolderRowSourceKind> sourceKinds,
    int skippedUnsupportedPathCount,
    int skippedMissingMetadataCount)
{
    public IReadOnlyList<LR2SongDB.folder> Rows { get; } = rows;

    public IReadOnlyCollection<string> ScopePaths { get; } = scopePaths;

    public IReadOnlyDictionary<string, Lr2FolderRowSourceKind> SourceKinds { get; } = sourceKinds;

    public int SkippedUnsupportedPathCount { get; } = skippedUnsupportedPathCount;

    public int SkippedMissingMetadataCount { get; } = skippedMissingMetadataCount;
}

internal static class Lr2FolderRowGenerator
{
    private const int NormalFolderType = 1;

    private static readonly IEqualityComparer<string> PathComparer = StringComparer.OrdinalIgnoreCase;

    internal static Lr2FolderGenerationResult GenerateNormalDirectoryRows(Lr2FolderGenerationRequest request)
    {
        request ??= new Lr2FolderGenerationRequest();
        Dictionary<string, LR2SongDB.folder> existingRowsByPath = CreateExistingRowMap(request.ExistingRows);
        var sourceKinds = new Dictionary<string, Lr2FolderRowSourceKind>(PathComparer);
        var rowsByPath = new Dictionary<string, LR2SongDB.folder>(PathComparer);
        var skippedPaths = new HashSet<string>(PathComparer);
        var orderedPaths = new List<string>();
        int skippedUnsupportedPathCount = 0;
        int skippedMissingMetadataCount = 0;
        int generatedAt = request.GeneratedAtUtc.ToUnixtime();

        List<string> roots = NormalizeRootDirectories(request.RootDirectories);
        List<string> rootsForMatching = [.. roots.OrderByDescending(root => root.Length)];
        foreach (string root in roots)
        {
            AddDirectory(
                root,
                root,
                isRoot: true,
                request,
                existingRowsByPath,
                sourceKinds,
                rowsByPath,
                skippedPaths,
                orderedPaths,
                generatedAt,
                ref skippedUnsupportedPathCount,
                ref skippedMissingMetadataCount);
        }

        foreach (string chartPath in request.ChartPaths ?? [])
        {
            string chartDirectory = Lr2FolderPath.NormalizeDirectoryPath(Lr2FolderPath.SafeGetDirectoryName(chartPath));
            if (string.IsNullOrWhiteSpace(chartDirectory))
            {
                continue;
            }

            string root = FindContainingRoot(chartDirectory, rootsForMatching);
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            foreach (string directory in EnumerateDirectoriesFromRoot(root, chartDirectory))
            {
                AddDirectory(
                    directory,
                    root,
                    isRoot: string.Equals(directory, root, StringComparison.OrdinalIgnoreCase),
                    request,
                    existingRowsByPath,
                    sourceKinds,
                    rowsByPath,
                    skippedPaths,
                    orderedPaths,
                    generatedAt,
                    ref skippedUnsupportedPathCount,
                    ref skippedMissingMetadataCount);
            }
        }

        var scopePaths = new HashSet<string>(orderedPaths, PathComparer);
        return new Lr2FolderGenerationResult(
            [.. orderedPaths.Select(path => rowsByPath[path])],
            scopePaths,
            sourceKinds,
            skippedUnsupportedPathCount,
            skippedMissingMetadataCount);
    }

    internal static string ParseFolderInfoTitle(IEnumerable<string> lines)
    {
        if (lines == null)
        {
            return null;
        }

        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string trimmed = line.TrimStart();
            if (!trimmed.StartsWith("#TITLE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string title = trimmed.Length > "#TITLE".Length
                ? trimmed.Substring("#TITLE".Length).Trim()
                : string.Empty;
            return string.IsNullOrWhiteSpace(title) ? null : title;
        }

        return null;
    }

    private static void AddDirectory(
        string directory,
        string root,
        bool isRoot,
        Lr2FolderGenerationRequest request,
        IReadOnlyDictionary<string, LR2SongDB.folder> existingRowsByPath,
        IDictionary<string, Lr2FolderRowSourceKind> sourceKinds,
        IDictionary<string, LR2SongDB.folder> rowsByPath,
        ISet<string> skippedPaths,
        ICollection<string> orderedPaths,
        int generatedAt,
        ref int skippedUnsupportedPathCount,
        ref int skippedMissingMetadataCount)
    {
        string path = Lr2FolderPath.ToFolderPath(directory);
        if (string.IsNullOrWhiteSpace(path) || rowsByPath.ContainsKey(path) || skippedPaths.Contains(path))
        {
            return;
        }

        string parentHash;
        if (isRoot)
        {
            parentHash = Lr2SongFolderParentNormalizer.RootParentHash;
        }
        else if (!TryComputeDirectoryHash(Lr2FolderPath.SafeGetDirectoryName(directory), out parentHash))
        {
            skippedPaths.Add(path);
            skippedUnsupportedPathCount++;
            return;
        }

        if (!TryGetCp932Bytes(path, out _))
        {
            skippedPaths.Add(path);
            skippedUnsupportedPathCount++;
            return;
        }

        Lr2FolderDirectoryMetadata metadata = request.DirectoryMetadataResolver?.Invoke(directory);
        if (metadata?.LastWriteTimeUtc == null)
        {
            skippedPaths.Add(path);
            skippedMissingMetadataCount++;
            return;
        }

        Lr2FolderRowSourceKind sourceKind = metadata?.HasFolderInfoTitle == true
            ? Lr2FolderRowSourceKind.FolderInfoDirectory
            : Lr2FolderRowSourceKind.NormalDirectory;
        int? date = metadata.LastWriteTimeUtc.Value.ToUnixtime();
        int? adddate = existingRowsByPath.TryGetValue(path, out LR2SongDB.folder existing)
            ? existing.adddate
            : generatedAt;

        rowsByPath[path] = new LR2SongDB.folder
        {
            title = ResolveTitle(directory, metadata),
            path = path,
            type = NormalFolderType,
            parent = parentHash,
            date = date,
            adddate = adddate
        };
        sourceKinds[path] = sourceKind;
        orderedPaths.Add(path);
    }

    private static Dictionary<string, LR2SongDB.folder> CreateExistingRowMap(IEnumerable<LR2SongDB.folder> existingRows)
    {
        var result = new Dictionary<string, LR2SongDB.folder>(PathComparer);
        foreach (LR2SongDB.folder row in existingRows ?? [])
        {
            string path = Lr2FolderPath.ToFolderPath(row?.path);
            if (string.IsNullOrWhiteSpace(path) || result.ContainsKey(path))
            {
                continue;
            }
            result.Add(path, row);
        }
        return result;
    }

    private static List<string> NormalizeRootDirectories(IEnumerable<string> rootDirectories)
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(PathComparer);
        foreach (string root in rootDirectories ?? [])
        {
            string normalized = Lr2FolderPath.NormalizeDirectoryPath(root);
            if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
            {
                continue;
            }
            roots.Add(normalized);
        }
        return roots;
    }

    private static IEnumerable<string> EnumerateDirectoriesFromRoot(string root, string targetDirectory)
    {
        var stack = new Stack<string>();
        string current = targetDirectory;
        while (!string.IsNullOrWhiteSpace(current)
            && Lr2FolderPath.IsSameOrDescendant(current, root))
        {
            stack.Push(current);
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = Lr2FolderPath.NormalizeDirectoryPath(Lr2FolderPath.SafeGetDirectoryName(current));
        }

        while (stack.Count > 0)
        {
            yield return stack.Pop();
        }
    }

    private static string FindContainingRoot(string directory, IEnumerable<string> roots)
    {
        foreach (string root in roots)
        {
            if (Lr2FolderPath.IsSameOrDescendant(directory, root))
            {
                return root;
            }
        }
        return null;
    }

    private static string ResolveTitle(string directory, Lr2FolderDirectoryMetadata metadata)
    {
        if (metadata?.HasFolderInfoTitle == true)
        {
            return metadata.FolderInfoTitle;
        }

        string trimmed = (Lr2FolderPath.NormalizeDirectoryPath(directory) ?? directory ?? string.Empty)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fileName = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(fileName) ? trimmed : fileName;
    }

    private static bool TryComputeDirectoryHash(string directoryPath, out string hash)
    {
        hash = null;
        try
        {
            hash = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(directoryPath);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryGetCp932Bytes(string value, out int byteCount)
    {
        return Lr2CompatibilityEvaluator.TryGetCp932ByteCount(value, out byteCount);
    }

}
