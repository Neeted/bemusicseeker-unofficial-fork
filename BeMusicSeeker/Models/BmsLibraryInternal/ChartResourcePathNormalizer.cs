using System;
using System.Collections.Generic;
using System.IO;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal enum ChartResourceKind
{
    Unknown,
    Audio,
    Image,
    Movie
}

internal enum ChartResourcePathNormalizationStatus
{
    Valid,
    Empty,
    InvalidPath,
    RootedOrAbsolute,
    ParentTraversalUnsupported
}

internal readonly struct ChartResourcePathNormalizationResult(
    string normalizedPath,
    ChartResourcePathNormalizationStatus status)
{
    public string NormalizedPath { get; } = normalizedPath ?? string.Empty;

    public ChartResourcePathNormalizationStatus Status { get; } = status;

    public bool IsValid => Status == ChartResourcePathNormalizationStatus.Valid;
}

internal static class ChartResourcePathNormalizer
{
    private static readonly char[] invalidPathChars = Path.GetInvalidPathChars();

    public static ChartResourcePathNormalizationResult AnalyzeReferencePathForLookup(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ChartResourcePathNormalizationResult(string.Empty, ChartResourcePathNormalizationStatus.Empty);
        }
        string normalized = path.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        normalized = normalized.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new ChartResourcePathNormalizationResult(string.Empty, ChartResourcePathNormalizationStatus.Empty);
        }
        if (normalized.IndexOfAny(invalidPathChars) >= 0)
        {
            return new ChartResourcePathNormalizationResult(string.Empty, ChartResourcePathNormalizationStatus.InvalidPath);
        }
        if (Path.IsPathRooted(normalized))
        {
            return new ChartResourcePathNormalizationResult(string.Empty, ChartResourcePathNormalizationStatus.RootedOrAbsolute);
        }

        var segments = new List<string>();
        foreach (string segment in normalized.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                return new ChartResourcePathNormalizationResult(string.Empty, ChartResourcePathNormalizationStatus.ParentTraversalUnsupported);
            }
            segments.Add(segment);
        }
        if (segments.Count == 0)
        {
            return new ChartResourcePathNormalizationResult(string.Empty, ChartResourcePathNormalizationStatus.Empty);
        }
        string joined = string.Join(Path.DirectorySeparatorChar.ToString(), segments);
        return new ChartResourcePathNormalizationResult(NormalizeExtensionAlias(joined), ChartResourcePathNormalizationStatus.Valid);
    }

    public static string NormalizeReferencePathForLookup(string path)
    {
        ChartResourcePathNormalizationResult result = AnalyzeReferencePathForLookup(path);
        return result.IsValid ? result.NormalizedPath : string.Empty;
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
        return StripLookupExtension(NormalizeExtensionAlias(normalized));
    }

    public static string NormalizeResourceKeyForLookup(string path)
    {
        string normalized = NormalizeReferencePathForLookup(path);
        return StripLookupExtension(normalized);
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
        if (ChartResourceExtensions.IsAudioExtension(extension))
        {
            return ChartResourceKind.Audio;
        }
        if (ChartResourceExtensions.IsImageExtension(extension))
        {
            return ChartResourceKind.Image;
        }
        if (ChartResourceExtensions.IsMovieExtension(extension))
        {
            return ChartResourceKind.Movie;
        }
        return ChartResourceKind.Unknown;
    }

    public static ChartResourceKind ClassifyReferencePathExtension(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ChartResourceKind.Unknown;
        }
        string normalized = path.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        string extension;
        try
        {
            extension = Path.GetExtension(normalized);
        }
        catch
        {
            return ChartResourceKind.Unknown;
        }
        if (string.IsNullOrWhiteSpace(extension))
        {
            return ChartResourceKind.Unknown;
        }
        extension = ChartResourceExtensions.ResolveLookupAliasExtension(extension);
        if (ChartResourceExtensions.IsAudioExtension(extension))
        {
            return ChartResourceKind.Audio;
        }
        if (ChartResourceExtensions.IsImageExtension(extension))
        {
            return ChartResourceKind.Image;
        }
        if (ChartResourceExtensions.IsMovieExtension(extension))
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
        string aliasExtension = ChartResourceExtensions.ResolveLookupAliasExtension(extension);
        if (string.Equals(extension, aliasExtension, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }
        return Path.ChangeExtension(value, aliasExtension);
    }

    private static string StripLookupExtension(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        string extension = Path.GetExtension(value);
        return string.IsNullOrWhiteSpace(extension) ? value : Path.ChangeExtension(value, null);
    }
}
