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
        if (scopeDirectories.Count == 0)
        {
            return false;
        }

        string current = Lr2FolderPath.NormalizeDirectoryPath(directory);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (scopeDirectories.Contains(current))
            {
                return true;
            }

            string parent = Lr2FolderPath.NormalizeDirectoryPath(Lr2FolderPath.SafeGetDirectoryName(current));
            if (string.IsNullOrWhiteSpace(parent)
                || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            current = parent;
        }

        return false;
    }
}
