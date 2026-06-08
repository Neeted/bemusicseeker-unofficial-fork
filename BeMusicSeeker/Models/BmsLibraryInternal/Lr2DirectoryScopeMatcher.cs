using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2DirectoryScopeMatcher
{
    private readonly HashSet<string> scopeDirectories;
    private readonly string[] scopeDirectoryPrefixes;

    private Lr2DirectoryScopeMatcher(HashSet<string> scopeDirectories)
    {
        this.scopeDirectories = scopeDirectories ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        scopeDirectoryPrefixes = [.. this.scopeDirectories
            .Select(Lr2FolderPath.ToFolderPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))];
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

        if (scopeDirectories.Contains(normalizedDirectory))
        {
            return true;
        }

        string normalizedWithSeparator = Lr2FolderPath.ToFolderPath(normalizedDirectory);
        if (string.IsNullOrWhiteSpace(normalizedWithSeparator))
        {
            return false;
        }

        foreach (string scopeDirectoryPrefix in scopeDirectoryPrefixes)
        {
            if (normalizedWithSeparator.StartsWith(scopeDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
