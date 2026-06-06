using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

[Flags]
internal enum Lr2PathWarningFlags
{
    None = 0,
    PathEncodingUnsupported = 1,
    PathTooLong = 2,
    FolderScanPathEncodingUnsupported = 4,
    FolderScanPathTooLong = 8
}

[Flags]
internal enum Lr2ResourceWarningFlags
{
    None = 0,
    RawPathEncodingUnsupported = 1,
    RawPathTooLong = 2,
    ResolvedPathEncodingUnsupported = 4,
    ResolvedPathTooLong = 8,
    ParentTraversalUnsupported = 16
}

internal readonly struct Lr2ChartPathEvaluation(
    Lr2PathWarningFlags warningFlags,
    int? chartPathCp932Bytes,
    int? folderScanPathCp932Bytes,
    string folderHash,
    string parentHash)
{
    public Lr2PathWarningFlags WarningFlags { get; } = warningFlags;

    public int? ChartPathCp932Bytes { get; } = chartPathCp932Bytes;

    public int? FolderScanPathCp932Bytes { get; } = folderScanPathCp932Bytes;

    public string FolderHash { get; } = folderHash;

    public string ParentHash { get; } = parentHash;

    public bool HasWarning => WarningFlags != Lr2PathWarningFlags.None;

    public bool CanComputeFolderParent => !string.IsNullOrWhiteSpace(FolderHash)
        && !string.IsNullOrWhiteSpace(ParentHash)
        && !WarningFlags.HasFlag(Lr2PathWarningFlags.PathEncodingUnsupported);
}

internal readonly struct Lr2ResourceReferenceEvaluation(
    Lr2ResourceWarningFlags warningFlags,
    int? maxRawCp932Bytes,
    int? maxResolvedCp932Bytes,
    int unsupportedCount)
{
    public Lr2ResourceWarningFlags WarningFlags { get; } = warningFlags;

    public int? MaxRawCp932Bytes { get; } = maxRawCp932Bytes;

    public int? MaxResolvedCp932Bytes { get; } = maxResolvedCp932Bytes;

    public int UnsupportedCount { get; } = unsupportedCount;

    public bool HasWarning => WarningFlags != Lr2ResourceWarningFlags.None;
}

internal static class Lr2CompatibilityEvaluator
{
    internal const int MaxLegacyPathBytes = 259;

    private static readonly Encoding StrictShiftJis = Encoding.GetEncoding(
        "shift_jis",
        EncoderFallback.ExceptionFallback,
        DecoderFallback.ExceptionFallback);

    internal static Lr2ChartPathEvaluation EvaluateChartPath(string chartPath)
    {
        Lr2PathWarningFlags flags = Lr2PathWarningFlags.None;
        int? chartPathBytes = TryGetCp932ByteCount(chartPath, out int pathBytes)
            ? pathBytes
            : null;
        if (chartPathBytes == null)
        {
            flags |= Lr2PathWarningFlags.PathEncodingUnsupported;
        }
        else if (chartPathBytes.Value > MaxLegacyPathBytes)
        {
            flags |= Lr2PathWarningFlags.PathTooLong;
        }

        string folderScanPath = CreateFolderScanPath(chartPath);
        int? folderScanBytes = TryGetCp932ByteCount(folderScanPath, out int scanBytes)
            ? scanBytes
            : null;
        if (folderScanBytes == null)
        {
            flags |= Lr2PathWarningFlags.FolderScanPathEncodingUnsupported;
        }
        else if (folderScanBytes.Value > MaxLegacyPathBytes)
        {
            flags |= Lr2PathWarningFlags.FolderScanPathTooLong;
        }

        string folder = null;
        string parent = null;
        if (!flags.HasFlag(Lr2PathWarningFlags.PathEncodingUnsupported))
        {
            TryComputeExpectedHashes(chartPath, out folder, out parent);
        }

        return new Lr2ChartPathEvaluation(flags, chartPathBytes, folderScanBytes, folder, parent);
    }

    internal static Lr2ResourceReferenceEvaluation EvaluateResourceReferences(
        string chartPath,
        ChartResourceSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return new Lr2ResourceReferenceEvaluation(Lr2ResourceWarningFlags.None, null, null, 0);
        }

        Lr2ResourceWarningFlags flags = Lr2ResourceWarningFlags.None;
        int? maxRawBytes = null;
        int? maxResolvedBytes = null;
        foreach (ChartResourceSnapshot.ResourceReference reference in snapshot.ResourceReferences)
        {
            string rawPath = string.IsNullOrWhiteSpace(reference.RawPath)
                ? reference.NormalizedPath
                : reference.RawPath;
            ApplyResourcePathEvaluation(
                chartPath,
                rawPath,
                ref flags,
                ref maxRawBytes,
                ref maxResolvedBytes);
        }

        int unsupportedCount = snapshot.UnsupportedResourceReferenceCount;
        if (unsupportedCount > 0)
        {
            flags |= Lr2ResourceWarningFlags.ParentTraversalUnsupported;
        }

        return new Lr2ResourceReferenceEvaluation(flags, maxRawBytes, maxResolvedBytes, unsupportedCount);
    }

    internal static Lr2ResourceReferenceEvaluation EvaluateBmsResourceReferences(
        string chartPath,
        BMSFile file)
    {
        if (file == null)
        {
            return new Lr2ResourceReferenceEvaluation(Lr2ResourceWarningFlags.None, null, null, 0);
        }

        Lr2ResourceWarningFlags flags = Lr2ResourceWarningFlags.None;
        int? maxRawBytes = null;
        int? maxResolvedBytes = null;
        foreach (string rawPath in EnumerateBmsSupportedResourcePaths(file))
        {
            ApplyResourcePathEvaluation(
                chartPath,
                rawPath,
                ref flags,
                ref maxRawBytes,
                ref maxResolvedBytes);
        }

        int unsupportedCount = file.UnsupportedResourceReferences?
            .Count(reference => reference.Reason == ChartResourcePathNormalizationStatus.ParentTraversalUnsupported) ?? 0;
        if (unsupportedCount > 0)
        {
            flags |= Lr2ResourceWarningFlags.ParentTraversalUnsupported;
        }

        return new Lr2ResourceReferenceEvaluation(flags, maxRawBytes, maxResolvedBytes, unsupportedCount);
    }

    internal static bool TryGetCp932ByteCount(string value, out int byteCount)
    {
        byteCount = 0;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }
        try
        {
            byteCount = StrictShiftJis.GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateBmsSupportedResourcePaths(BMSFile file)
    {
        if (file == null)
        {
            yield break;
        }

        if ((file.ResourceReferences?.Count ?? 0) > 0)
        {
            foreach (ChartResourceReference reference in file.ResourceReferences)
            {
                string rawPath = string.IsNullOrWhiteSpace(reference.RawPath)
                    ? reference.NormalizedPath
                    : reference.RawPath;
                if (!string.IsNullOrWhiteSpace(rawPath))
                {
                    yield return rawPath;
                }
            }
        }
        else
        {
            foreach (string audioPath in file.WAVfiles ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(audioPath))
                {
                    yield return audioPath;
                }
            }
            foreach (string visualPath in file.BGAfiles ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(visualPath))
                {
                    yield return visualPath;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(file.banner))
        {
            yield return file.banner;
        }
        if (!string.IsNullOrWhiteSpace(file.backbmp))
        {
            yield return file.backbmp;
        }
        if (!string.IsNullOrWhiteSpace(file.stagefile))
        {
            yield return file.stagefile;
        }
    }

    private static void ApplyResourcePathEvaluation(
        string chartPath,
        string rawPath,
        ref Lr2ResourceWarningFlags flags,
        ref int? maxRawBytes,
        ref int? maxResolvedBytes)
    {
        if (TryGetCp932ByteCount(rawPath, out int rawBytes))
        {
            maxRawBytes = Math.Max(maxRawBytes.GetValueOrDefault(), rawBytes);
            if (rawBytes > MaxLegacyPathBytes)
            {
                flags |= Lr2ResourceWarningFlags.RawPathTooLong;
            }
        }
        else
        {
            flags |= Lr2ResourceWarningFlags.RawPathEncodingUnsupported;
        }

        string resolvedPath = ResolveResourcePath(chartPath, rawPath);
        if (TryGetCp932ByteCount(resolvedPath, out int resolvedBytes))
        {
            maxResolvedBytes = Math.Max(maxResolvedBytes.GetValueOrDefault(), resolvedBytes);
            if (resolvedBytes > MaxLegacyPathBytes)
            {
                flags |= Lr2ResourceWarningFlags.ResolvedPathTooLong;
            }
        }
        else
        {
            flags |= Lr2ResourceWarningFlags.ResolvedPathEncodingUnsupported;
        }
    }

    private static string CreateFolderScanPath(string chartPath)
    {
        try
        {
            string directory = Path.GetDirectoryName(chartPath ?? string.Empty);
            return string.IsNullOrWhiteSpace(directory)
                ? "*.*"
                : Path.Combine(directory, "*.*");
        }
        catch (Exception ex) when (ex is ArgumentException || ex is PathTooLongException)
        {
            return chartPath ?? string.Empty;
        }
    }

    private static bool TryComputeExpectedHashes(string chartPath, out string folder, out string parent)
    {
        try
        {
            return Lr2SongFolderParentNormalizer.TryComputeExpectedHashes(chartPath, out folder, out parent);
        }
        catch (PathTooLongException)
        {
            folder = null;
            parent = null;
            return false;
        }
    }

    private static string ResolveResourcePath(string chartPath, string rawPath)
    {
        try
        {
            string directory = Path.GetDirectoryName(chartPath ?? string.Empty) ?? string.Empty;
            return Path.GetFullPath(Path.Combine(directory, rawPath ?? string.Empty));
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return rawPath ?? string.Empty;
        }
    }
}
