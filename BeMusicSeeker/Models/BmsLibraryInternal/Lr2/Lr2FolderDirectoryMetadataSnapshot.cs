using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2FolderDirectoryMetadataBuildRequest
{
    public IReadOnlyCollection<string> DirectoryPaths { get; set; } = [];

    public IReadOnlyCollection<string> FolderInfoFilePaths { get; set; } = [];

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> FolderInfoFileEntries { get; set; } =
        new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

    public Func<string, DateTime?> DirectoryLastWriteTimeUtcResolver { get; set; }

    public Func<string, IEnumerable<string>> FolderInfoLinesReader { get; set; }
}

internal sealed class Lr2FolderDirectoryMetadataSnapshot(
    IReadOnlyDictionary<string, Lr2FolderDirectoryMetadata> metadataByDirectory,
    int requestedDirectoryCount,
    int resolvedDirectoryCount,
    int missingDirectoryCount,
    int folderInfoCandidateCount,
    int folderInfoAppliedCount,
    int folderInfoReadFailureCount)
{
    public IReadOnlyDictionary<string, Lr2FolderDirectoryMetadata> MetadataByDirectory { get; } = metadataByDirectory;

    public int RequestedDirectoryCount { get; } = requestedDirectoryCount;

    public int ResolvedDirectoryCount { get; } = resolvedDirectoryCount;

    public int MissingDirectoryCount { get; } = missingDirectoryCount;

    public int FolderInfoCandidateCount { get; } = folderInfoCandidateCount;

    public int FolderInfoAppliedCount { get; } = folderInfoAppliedCount;

    public int FolderInfoReadFailureCount { get; } = folderInfoReadFailureCount;

    internal bool TryGetMetadata(string directoryPath, out Lr2FolderDirectoryMetadata metadata)
    {
        metadata = null;
        string key = Lr2FolderPath.NormalizeDirectoryPath(directoryPath);
        return !string.IsNullOrWhiteSpace(key)
            && MetadataByDirectory.TryGetValue(key, out metadata);
    }

    internal Lr2FolderDirectoryMetadata Resolve(string directoryPath)
    {
        return TryGetMetadata(directoryPath, out Lr2FolderDirectoryMetadata metadata) ? metadata : null;
    }
}

internal static class Lr2FolderDirectoryMetadataBuilder
{
    private static readonly Encoding ShiftJis = Encoding.GetEncoding("shift_jis");

    internal static Lr2FolderDirectoryMetadataSnapshot Build(Lr2FolderDirectoryMetadataBuildRequest request)
    {
        request ??= new Lr2FolderDirectoryMetadataBuildRequest();

        Dictionary<string, string> folderInfoPathsByDirectory = CreateFolderInfoPathMap(
            request.FolderInfoFilePaths,
            request.FolderInfoFileEntries);
        var metadataByDirectory = new Dictionary<string, Lr2FolderDirectoryMetadata>(StringComparer.OrdinalIgnoreCase);
        int requestedDirectoryCount = 0;
        int missingDirectoryCount = 0;
        int folderInfoAppliedCount = 0;
        int folderInfoReadFailureCount = 0;

        foreach (string directoryPath in NormalizeDirectoryPaths(request.DirectoryPaths))
        {
            requestedDirectoryCount++;
            DateTime? lastWriteTimeUtc = ResolveLastWriteTimeUtc(directoryPath, request.DirectoryLastWriteTimeUtcResolver);
            if (lastWriteTimeUtc == null)
            {
                missingDirectoryCount++;
                continue;
            }

            string folderInfoTitle = null;
            if (folderInfoPathsByDirectory.TryGetValue(directoryPath, out string folderInfoPath))
            {
                try
                {
                    folderInfoTitle = Lr2FolderRowGenerator.ParseFolderInfoTitle(ReadFolderInfoLines(folderInfoPath, request.FolderInfoLinesReader));
                    if (!string.IsNullOrWhiteSpace(folderInfoTitle))
                    {
                        folderInfoAppliedCount++;
                    }
                }
                catch (IOException)
                {
                    folderInfoReadFailureCount++;
                }
                catch (UnauthorizedAccessException)
                {
                    folderInfoReadFailureCount++;
                }
                catch (NotSupportedException)
                {
                    folderInfoReadFailureCount++;
                }
                catch (ArgumentException)
                {
                    folderInfoReadFailureCount++;
                }
                catch (SecurityException)
                {
                    folderInfoReadFailureCount++;
                }
            }

            metadataByDirectory[directoryPath] = new Lr2FolderDirectoryMetadata(lastWriteTimeUtc, folderInfoTitle);
        }

        return new Lr2FolderDirectoryMetadataSnapshot(
            metadataByDirectory,
            requestedDirectoryCount,
            metadataByDirectory.Count,
            missingDirectoryCount,
            folderInfoPathsByDirectory.Count,
            folderInfoAppliedCount,
            folderInfoReadFailureCount);
    }

    private static IEnumerable<string> NormalizeDirectoryPaths(IEnumerable<string> directoryPaths)
    {
        return (directoryPaths ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> CreateFolderInfoPathMap(
        IEnumerable<string> folderInfoFilePaths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> folderInfoFileEntries)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddFolderInfoPaths(result, (folderInfoFileEntries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase))
            .Values
            .Select(entry => entry?.Path));
        AddFolderInfoPaths(result, folderInfoFilePaths);
        return result;
    }

    private static void AddFolderInfoPaths(Dictionary<string, string> result, IEnumerable<string> folderInfoFilePaths)
    {
        if (result == null)
        {
            return;
        }

        foreach (string filePath in NormalizeFolderInfoFilePaths(folderInfoFilePaths))
        {
            string directoryPath = Lr2FolderPath.NormalizeDirectoryPath(Path.GetDirectoryName(filePath));
            if (!string.IsNullOrWhiteSpace(directoryPath) && !result.ContainsKey(directoryPath))
            {
                result.Add(directoryPath, filePath);
            }
        }
    }

    private static IEnumerable<string> NormalizeFolderInfoFilePaths(IEnumerable<string> folderInfoFilePaths)
    {
        var result = new List<string>();
        foreach (string filePath in folderInfoFilePaths ?? [])
        {
            if (!TryNormalizeFolderInfoFilePath(filePath, out string normalized))
            {
                continue;
            }
            result.Add(normalized);
        }
        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeFolderInfoFilePath(string filePath, out string normalized)
    {
        normalized = null;
        try
        {
            if (!IsFolderInfoFilePath(filePath))
            {
                return false;
            }
            normalized = Path.GetFullPath(filePath);
            return !string.IsNullOrWhiteSpace(normalized);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (SecurityException)
        {
            return false;
        }
    }

    private static bool IsFolderInfoFilePath(string filePath)
    {
        return !string.IsNullOrWhiteSpace(filePath)
            && string.Equals(Path.GetFileName(filePath), "folderinfo.txt", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime? ResolveLastWriteTimeUtc(string directoryPath, Func<string, DateTime?> resolver)
    {
        if (resolver == null)
        {
            return null;
        }
        try
        {
            return NormalizeLastWriteTimeUtc(resolver(directoryPath));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (SecurityException)
        {
            return null;
        }
    }

    private static IEnumerable<string> ReadFolderInfoLines(string folderInfoPath, Func<string, IEnumerable<string>> reader)
    {
        return reader != null
            ? reader(folderInfoPath) ?? []
            : LongPathFileSystem.ReadLines(folderInfoPath, ShiftJis);
    }

    private static DateTime? NormalizeLastWriteTimeUtc(DateTime? timestamp)
    {
        if (timestamp == null)
        {
            return null;
        }

        DateTime utc = timestamp.Value.Kind == DateTimeKind.Utc
            ? timestamp.Value
            : timestamp.Value.ToUniversalTime();
        // Unix epoch and pre-epoch mtimes are valid LR2 generated values.  A
        // missing timestamp is represented by null at the input boundary;
        // do not turn an ordinary zero/negative projection into missing data.
        return utc;
    }
}
