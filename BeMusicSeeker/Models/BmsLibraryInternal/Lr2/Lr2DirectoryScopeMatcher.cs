using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2DirectoryScopeMatcher
{
    private readonly HashSet<string> scopeDirectories;

    private Lr2DirectoryScopeMatcher(HashSet<string> scopeDirectories)
    {
        this.scopeDirectories = scopeDirectories ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    internal static Lr2DirectoryScopeMatcher Create(IEnumerable<string> directories)
    {
        var scopeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in directories ?? [])
        {
            string normalized = Lr2FolderPath.NormalizeDirectoryPath(directory);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                scopeDirectories.Add(normalized);
            }
        }

        return new Lr2DirectoryScopeMatcher(scopeDirectories);
    }

    internal bool IsEmpty => scopeDirectories.Count == 0;

    internal bool ContainsFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return ContainsDirectory(Lr2FolderPath.SafeGetDirectoryName(path));
    }

    internal bool ContainsNormalizedFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return ContainsNormalizedDirectory(Lr2FolderPath.SafeGetParentNormalizedDirectory(path));
    }

    internal bool ContainsDirectory(string directory)
    {
        return ContainsNormalizedDirectory(Lr2FolderPath.NormalizeDirectoryPath(directory));
    }

    internal bool ContainsNormalizedDirectory(string normalizedDirectory)
    {
        if (scopeDirectories.Count == 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(normalizedDirectory))
        {
            return false;
        }

        string current = normalizedDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (scopeDirectories.Contains(current))
            {
                return true;
            }

            string parent = Lr2FolderPath.SafeGetParentNormalizedDirectory(current);
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = parent;
        }

        return false;
    }
}
