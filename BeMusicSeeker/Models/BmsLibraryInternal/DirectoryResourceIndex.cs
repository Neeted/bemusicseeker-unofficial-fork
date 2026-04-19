using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class DirectoryResourceIndex
{
    public string DirectoryPath { get; private set; }

    public HashSet<string> AudioRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> AudioBaseNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> VisualRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> VisualBaseNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> MovieRelativePaths { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> MovieBaseNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public int AudioFileCount => AudioBaseNames.Count;

    public static DirectoryResourceIndex Build(string directoryPath)
    {
        DirectoryResourceIndex index = new DirectoryResourceIndex
        {
            DirectoryPath = string.IsNullOrWhiteSpace(directoryPath)
                ? string.Empty
                : Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        };
        if (string.IsNullOrWhiteSpace(index.DirectoryPath) || !Directory.Exists(index.DirectoryPath))
        {
            return index;
        }
        foreach (FileData file in FastDirectoryEnumerator.EnumerateFiles(index.DirectoryPath, "*", SearchOption.AllDirectories))
        {
            if (file == null || string.IsNullOrWhiteSpace(file.Path))
            {
                continue;
            }
            string relativePath = ChartResourcePathNormalizer.NormalizeRelativePathForLookup(index.DirectoryPath, file.Path);
            string baseName = ChartResourcePathNormalizer.GetLookupFileName(relativePath);
            if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(baseName))
            {
                continue;
            }
            switch (ChartResourcePathNormalizer.ClassifyPath(relativePath))
            {
                case ChartResourceKind.Audio:
                    index.AudioRelativePaths.Add(relativePath);
                    index.AudioBaseNames.Add(baseName);
                    break;
                case ChartResourceKind.Image:
                    index.VisualRelativePaths.Add(relativePath);
                    index.VisualBaseNames.Add(baseName);
                    break;
                case ChartResourceKind.Movie:
                    index.MovieRelativePaths.Add(relativePath);
                    index.MovieBaseNames.Add(baseName);
                    break;
            }
        }
        return index;
    }
}
