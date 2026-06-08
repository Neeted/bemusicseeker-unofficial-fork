using System;
using System.IO;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class Lr2FolderPath
{
    internal static string NormalizeDirectoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return TrimTrailingSeparators(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    internal static string ToFolderPath(string directory)
    {
        string normalized = NormalizeDirectoryPath(directory);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return ToFolderPathFromNormalizedDirectory(normalized);
    }

    internal static string ToFolderPathFromNormalizedDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        return AppendDirectorySeparator(TrimTrailingSeparators(directory));
    }

    internal static string SafeGetDirectoryName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetDirectoryName(path);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    internal static bool IsSameOrDescendant(string path, string ancestor)
    {
        string normalizedPath = NormalizeDirectoryPath(path);
        string normalizedAncestor = NormalizeDirectoryPath(ancestor);
        return IsSameOrDescendantNormalized(normalizedPath, normalizedAncestor);
    }

    internal static bool IsSameOrDescendantNormalized(string path, string ancestor)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(ancestor))
        {
            return false;
        }
        if (string.Equals(path, ancestor, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.Length > ancestor.Length
            && path.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase)
            && (IsDirectorySeparator(ancestor[ancestor.Length - 1])
                || IsDirectorySeparator(path[ancestor.Length]));
    }

    internal static string SafeGetParentNormalizedDirectory(string normalizedDirectory)
    {
        if (string.IsNullOrWhiteSpace(normalizedDirectory))
        {
            return null;
        }

        try
        {
            string parent = Path.GetDirectoryName(TrimTrailingSeparators(normalizedDirectory));
            return string.IsNullOrWhiteSpace(parent)
                ? null
                : TrimTrailingSeparators(parent);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    internal static bool IsDirectorySeparator(char value)
    {
        return value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;
    }

    private static string TrimTrailingSeparators(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        string root = Path.GetPathRoot(path);
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrEmpty(root)
            && string.Equals(trimmed, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return root.TrimEnd(Path.AltDirectorySeparatorChar);
        }
        return trimmed;
    }

    private static string AppendDirectorySeparator(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            || path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
    }
}
