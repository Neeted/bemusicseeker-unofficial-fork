using System;
using System.Collections.Generic;
using System.IO;

namespace BeMusicSeeker.Models.Utils;

internal static class LongPathFileSystem
{
    private const string ExtendedPathPrefix = @"\\?\";
    private const string ExtendedUncPathPrefix = @"\\?\UNC\";
    private const string UncPathPrefix = @"\\";

    public static string NormalizePathForStorage(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentNullException(nameof(path));
        }

        string normalized = RemoveExtendedPathPrefix(path);
        return IsFullyQualifiedWindowsPath(normalized)
            ? NormalizeFullyQualifiedWindowsPath(normalized)
            : Path.GetFullPath(normalized);
    }

    public static string ToExtendedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentNullException(nameof(path));
        }

        string fullPath = RemoveExtendedPathPrefix(path);
        fullPath = IsFullyQualifiedWindowsPath(fullPath)
            ? NormalizeFullyQualifiedWindowsPath(fullPath)
            : Path.GetFullPath(fullPath);
        if (IsExtendedPath(fullPath) || IsDevicePath(fullPath))
        {
            return fullPath;
        }
        if (fullPath.StartsWith(UncPathPrefix, StringComparison.Ordinal))
        {
            return ExtendedUncPathPrefix + fullPath.Substring(UncPathPrefix.Length);
        }
        return ExtendedPathPrefix + fullPath;
    }

    public static byte[] ReadAllBytes(string path)
    {
        using FileStream stream = OpenRead(path);
        long length = stream.Length;
        if (length > int.MaxValue)
        {
            throw new IOException("File is too large to read into memory.");
        }

        var bytes = new byte[(int)length];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of file.");
            }
            offset += read;
        }
        return bytes;
    }

    public static FileStream OpenRead(string path)
    {
        return new FileStream(ToExtendedPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public static bool FileExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        try
        {
            return File.Exists(ToExtendedPath(path));
        }
        catch (Exception ex) when (ex is IOException
            || ex is UnauthorizedAccessException
            || ex is ArgumentException
            || ex is NotSupportedException)
        {
            return false;
        }
    }

    public static DateTime GetLastWriteTimeUtc(string path)
    {
        return File.GetLastWriteTimeUtc(ToExtendedPath(path));
    }

    private static string RemoveExtendedPathPrefix(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }
        if (path.StartsWith(ExtendedUncPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return UncPathPrefix + path.Substring(ExtendedUncPathPrefix.Length);
        }
        if (path.StartsWith(ExtendedPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return path.Substring(ExtendedPathPrefix.Length);
        }
        return path;
    }

    private static bool IsExtendedPath(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && path.StartsWith(ExtendedPathPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDevicePath(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && path.StartsWith(@"\\.\", StringComparison.Ordinal);
    }

    private static bool IsFullyQualifiedWindowsPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        if (IsExtendedPath(path) || IsDevicePath(path))
        {
            return true;
        }
        if (path.Length >= 3
            && char.IsLetter(path[0])
            && path[1] == ':'
            && IsDirectorySeparator(path[2]))
        {
            return true;
        }
        return path.StartsWith(UncPathPrefix, StringComparison.Ordinal);
    }

    private static bool IsDirectorySeparator(char value)
    {
        return value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;
    }

    private static string NormalizeFullyQualifiedWindowsPath(string path)
    {
        if (!NeedsFullPathNormalization(path))
        {
            return path;
        }

        if (IsDevicePath(path))
        {
            return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        }

        string normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        string root = GetFullyQualifiedRoot(normalized);
        if (string.IsNullOrWhiteSpace(root))
        {
            return normalized;
        }

        var parts = new List<string>();
        string remainder = normalized.Substring(root.Length);
        foreach (string part in remainder.Split(Path.DirectorySeparatorChar))
        {
            if (string.IsNullOrEmpty(part) || string.Equals(part, ".", StringComparison.Ordinal))
            {
                continue;
            }
            if (string.Equals(part, "..", StringComparison.Ordinal))
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
                continue;
            }
            parts.Add(part);
        }

        return parts.Count == 0
            ? root
            : root + string.Join(Path.DirectorySeparatorChar.ToString(), parts);
    }

    private static bool NeedsFullPathNormalization(string path)
    {
        return path.IndexOf(Path.AltDirectorySeparatorChar) >= 0
            || path.IndexOf(@"\.", StringComparison.Ordinal) >= 0
            || path.EndsWith(@"\.", StringComparison.Ordinal)
            || path.IndexOf(@"\..", StringComparison.Ordinal) >= 0
            || path.EndsWith(@"\..", StringComparison.Ordinal);
    }

    private static string GetFullyQualifiedRoot(string path)
    {
        if (path.Length >= 3
            && char.IsLetter(path[0])
            && path[1] == ':'
            && IsDirectorySeparator(path[2]))
        {
            return path.Substring(0, 3);
        }
        if (!path.StartsWith(UncPathPrefix, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        int serverEnd = path.IndexOf(Path.DirectorySeparatorChar, UncPathPrefix.Length);
        if (serverEnd < 0)
        {
            return path;
        }
        int shareEnd = path.IndexOf(Path.DirectorySeparatorChar, serverEnd + 1);
        return shareEnd < 0
            ? path
            : path.Substring(0, shareEnd + 1);
    }
}
