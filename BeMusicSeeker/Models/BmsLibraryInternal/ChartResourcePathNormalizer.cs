using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal enum ChartResourceKind
{
    Unknown,
    Audio,
    Image,
    Movie
}

internal static class ChartResourcePathNormalizer
{
    private static readonly char[] invalidPathChars = Path.GetInvalidPathChars();

    public static string NormalizeReferencePathForLookup(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }
        string normalized = path.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        normalized = normalized.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }
        if (normalized.IndexOfAny(invalidPathChars) >= 0 || Path.IsPathRooted(normalized))
        {
            return string.Empty;
        }
        string[] segments = normalized.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any((string segment) => segment == "." || segment == ".."))
        {
            return string.Empty;
        }
        string joined = string.Join(Path.DirectorySeparatorChar.ToString(), segments);
        return NormalizeExtensionAlias(joined);
    }

    public static string NormalizeRelativePathForLookup(string rootDirectory, string filePath)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || string.IsNullOrWhiteSpace(filePath))
        {
            return string.Empty;
        }
        string normalizedRoot = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedFile = Path.GetFullPath(filePath);
        if (!normalizedFile.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }
        string relativePath = normalizedFile.Substring(normalizedRoot.Length);
        return NormalizeReferencePathForLookup(relativePath);
    }

    public static string NormalizeFileNameForLookup(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }
        string normalized = fileName.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        try
        {
            normalized = Path.GetFileName(normalized);
        }
        catch
        {
            return string.Empty;
        }
        if (string.IsNullOrWhiteSpace(normalized) || normalized.IndexOfAny(invalidPathChars) >= 0)
        {
            return string.Empty;
        }
        return NormalizeExtensionAlias(normalized);
    }

    public static string GetLookupFileName(string path)
    {
        string normalized = NormalizeReferencePathForLookup(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }
        return NormalizeFileNameForLookup(Path.GetFileName(normalized));
    }

    public static ChartResourceKind ClassifyPath(string path)
    {
        string normalized = NormalizeReferencePathForLookup(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return ChartResourceKind.Unknown;
        }
        string extension = Path.GetExtension(normalized);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return ChartResourceKind.Unknown;
        }
        if (BMSFile.wavExtensions.Any((string item) => extension.Equals(item, StringComparison.OrdinalIgnoreCase)))
        {
            return ChartResourceKind.Audio;
        }
        if (BMSFile.bgaImageExtensions.Any((string item) => extension.Equals(item, StringComparison.OrdinalIgnoreCase)))
        {
            return ChartResourceKind.Image;
        }
        if (BMSFile.bgaMovieExtensions.Any((string item) => extension.Equals(item, StringComparison.OrdinalIgnoreCase)))
        {
            return ChartResourceKind.Movie;
        }
        return ChartResourceKind.Unknown;
    }

    public static bool HasDirectorySegments(string path)
    {
        string normalized = NormalizeReferencePathForLookup(path);
        return !string.IsNullOrWhiteSpace(normalized)
            && (normalized.IndexOf(Path.DirectorySeparatorChar) >= 0 || normalized.IndexOf(Path.AltDirectorySeparatorChar) >= 0);
    }

    private static string NormalizeExtensionAlias(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        string extension = Path.GetExtension(value);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return value;
        }
        string normalizedExtension = extension.ToLowerInvariant();
        string aliasExtension = normalizedExtension switch
        {
            ".ogg" => BMSFile.wavExtensionBase,
            ".mp3" => BMSFile.wavExtensionBase,
            ".flac" => BMSFile.wavExtensionBase,
            ".bmp" => BMSFile.bgaImageExtensionBase,
            ".jpg" => BMSFile.bgaImageExtensionBase,
            ".jpeg" => BMSFile.bgaImageExtensionBase,
            _ => normalizedExtension
        };
        if (string.Equals(extension, aliasExtension, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }
        return Path.ChangeExtension(value, aliasExtension);
    }
}
