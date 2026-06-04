using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class Lr2FullGenerationSignatureBuilder
{
    private const string Version = "lr2_full_generation_v1";

    internal static string Build(
        BmsLibraryOptionsSnapshot options,
        IEnumerable<string> rootDirectories,
        IEnumerable<string> lr2FolderDiscoveryDirectories = null)
    {
        return Version
            + "|operationModeLR2DB=" + ((options?.OperationModeLR2DB ?? false) ? "1" : "0")
            + "|fullGeneration=" + ((options?.EnableLR2SongDbFullGeneration ?? false) ? "1" : "0")
            + "|roots=" + FormatRootSet(rootDirectories)
            + "|lr2folderRoots=" + FormatRootSet(lr2FolderDiscoveryDirectories);
    }

    private static string FormatRootSet(IEnumerable<string> rootDirectories)
    {
        string[] roots = [.. (rootDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)];
        return string.Concat(roots.Select(root => root.Length.ToString(CultureInfo.InvariantCulture) + ":" + root));
    }
}
