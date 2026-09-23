using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

[Flags]
internal enum Lr2CompatibilityWarningFlags
{
    None = 0,
    PathEncodingUnsupported = 1,
    PathTooLong = 2,
    ResourcePathEncodingUnsupported = 4,
    ResourcePathTooLong = 8
}

internal readonly struct Lr2ChartPathEvaluation(
    Lr2CompatibilityWarningFlags warningFlags,
    string folderHash,
    string parentHash)
{
    public Lr2CompatibilityWarningFlags WarningFlags { get; } = warningFlags;

    public string FolderHash { get; } = folderHash;

    public string ParentHash { get; } = parentHash;

    public bool HasWarning => WarningFlags != Lr2CompatibilityWarningFlags.None;

    public bool CanComputeFolderParent => !string.IsNullOrWhiteSpace(FolderHash)
        && !string.IsNullOrWhiteSpace(ParentHash)
        && !WarningFlags.HasFlag(Lr2CompatibilityWarningFlags.PathEncodingUnsupported);
}

internal readonly struct Lr2ResourceReferenceEvaluation(
    Lr2CompatibilityWarningFlags warningFlags,
    int? maxRelativeCp932Bytes,
    bool hasParentTraversal)
{
    public Lr2CompatibilityWarningFlags WarningFlags { get; } = warningFlags;

    public int? MaxRelativeCp932Bytes { get; } = maxRelativeCp932Bytes;

    public bool HasParentTraversal { get; } = hasParentTraversal;

    public bool HasWarning => WarningFlags != Lr2CompatibilityWarningFlags.None;
}

internal readonly struct Lr2ResourcePathEvaluationContext(
    int? chartDirectoryCp932Bytes,
    bool chartDirectoryEncodingSupported)
{
    public int? ChartDirectoryCp932Bytes { get; } = chartDirectoryCp932Bytes;

    public bool ChartDirectoryEncodingSupported { get; } = chartDirectoryEncodingSupported;
}

internal readonly struct Lr2ResourcePathReference(
    string rawPath,
    string normalizedPath)
{
    public string RawPath { get; } = rawPath ?? string.Empty;

    public string NormalizedPath { get; } = normalizedPath ?? string.Empty;
}

internal static class Lr2CompatibilityEvaluator
{
    internal const int MaxLegacyPathBytes = 259;

    private static readonly Encoding StrictShiftJis = Encoding.GetEncoding(
        "shift_jis",
        EncoderFallback.ExceptionFallback,
        DecoderFallback.ExceptionFallback);

    private static readonly char[] InvalidPathChars = Path.GetInvalidPathChars();

    internal static Lr2ChartPathEvaluation EvaluateChartPath(string chartPath)
    {
        Lr2CompatibilityWarningFlags flags = Lr2CompatibilityWarningFlags.None;
        int? chartPathBytes = TryGetCp932ByteCount(chartPath, out int pathBytes)
            ? pathBytes
            : null;
        if (chartPathBytes == null)
        {
            flags |= Lr2CompatibilityWarningFlags.PathEncodingUnsupported;
        }
        else if (chartPathBytes.Value > MaxLegacyPathBytes)
        {
            flags |= Lr2CompatibilityWarningFlags.PathTooLong;
        }

        string folder = null;
        string parent = null;
        if (!flags.HasFlag(Lr2CompatibilityWarningFlags.PathEncodingUnsupported))
        {
            TryComputeExpectedHashes(chartPath, out folder, out parent);
        }

        return new Lr2ChartPathEvaluation(flags, folder, parent);
    }

    internal static bool IsLegacyChartPathCompatible(string chartPath)
    {
        return EvaluateChartPath(chartPath).WarningFlags == Lr2CompatibilityWarningFlags.None;
    }

    internal static bool IsLegacyRootPathCompatible(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }
        try
        {
            string normalizedRoot = LongPathFileSystem.TrimTrailingDirectorySeparators(LongPathFileSystem.NormalizePathForStorage(rootPath));
            if (normalizedRoot.StartsWith(@"\\?\", StringComparison.Ordinal)
                || normalizedRoot.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                return false;
            }
            return IsLegacyChartPathCompatible(Path.Combine(normalizedRoot, "a.bms"));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return false;
        }
    }

    internal static Lr2ResourceReferenceEvaluation EvaluateResourceReferences(
        string chartPath,
        ChartResourceSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return new Lr2ResourceReferenceEvaluation(Lr2CompatibilityWarningFlags.None, null, hasParentTraversal: false);
        }

        Lr2CompatibilityWarningFlags flags = Lr2CompatibilityWarningFlags.None;
        int? maxRelativeBytes = null;
        bool hasParentTraversal = false;
        Lr2ResourcePathEvaluationContext context = CreateResourcePathEvaluationContext(chartPath);
        foreach (ChartResourceSnapshot.ResourceReference reference in snapshot.ResourceReferences)
        {
            string rawPath = string.IsNullOrWhiteSpace(reference.RawPath)
                ? reference.NormalizedPath
                : reference.RawPath;
            ApplyResourcePathEvaluation(
                context,
                rawPath,
                null,
                ref flags,
                ref maxRelativeBytes,
                ref hasParentTraversal);
        }

        foreach (UnsupportedChartResourceReference unsupportedReference in snapshot.UnsupportedResourceReferences ?? [])
        {
            ApplyUnsupportedResourceReferenceEvaluation(
                unsupportedReference,
                context,
                ref flags,
                ref maxRelativeBytes,
                ref hasParentTraversal);
        }

        return new Lr2ResourceReferenceEvaluation(flags, maxRelativeBytes, hasParentTraversal);
    }

    internal static Lr2ResourceReferenceEvaluation EvaluateBmsResourceReferences(
        string chartPath,
        BMSFile file)
    {
        if (file == null)
        {
            return new Lr2ResourceReferenceEvaluation(Lr2CompatibilityWarningFlags.None, null, hasParentTraversal: false);
        }

        Lr2CompatibilityWarningFlags flags = Lr2CompatibilityWarningFlags.None;
        int? maxRelativeBytes = null;
        bool hasParentTraversal = false;
        Lr2ResourcePathEvaluationContext context = CreateResourcePathEvaluationContext(chartPath);
        foreach (Lr2ResourcePathReference reference in EnumerateBmsSupportedResourcePaths(file))
        {
            ApplyResourcePathEvaluation(
                context,
                reference.RawPath,
                reference.NormalizedPath,
                ref flags,
                ref maxRelativeBytes,
                ref hasParentTraversal);
        }

        foreach (UnsupportedChartResourceReference unsupportedReference in file.UnsupportedResourceReferences ?? [])
        {
            ApplyUnsupportedResourceReferenceEvaluation(
                unsupportedReference,
                context,
                ref flags,
                ref maxRelativeBytes,
                ref hasParentTraversal);
        }

        return new Lr2ResourceReferenceEvaluation(flags, maxRelativeBytes, hasParentTraversal);
    }

    internal static Lr2ResourceReferenceEvaluation ReevaluateResourceReferencesForRelocatedPath(
        string chartPath,
        int? maxRelativeCp932Bytes,
        Lr2CompatibilityWarningFlags previousFlags)
    {
        Lr2CompatibilityWarningFlags flags = previousFlags & Lr2CompatibilityWarningFlags.ResourcePathEncodingUnsupported;
        Lr2ResourcePathEvaluationContext context = CreateResourcePathEvaluationContext(chartPath);
        if (maxRelativeCp932Bytes.HasValue
            && TryGetResolvedResourcePathCp932ByteCount(context, maxRelativeCp932Bytes.Value, out int resolvedBytes)
            && resolvedBytes > MaxLegacyPathBytes)
        {
            flags |= Lr2CompatibilityWarningFlags.ResourcePathTooLong;
        }

        return new Lr2ResourceReferenceEvaluation(flags, maxRelativeCp932Bytes, hasParentTraversal: false);
    }

    internal static void RefreshRelocatedMaintenanceFacts(
        BMSFileMaintenanceInfo maintenanceInfo,
        string chartPath,
        Func<ChartFileSnapshot> snapshotProvider = null)
    {
        if (maintenanceInfo == null
            || (!maintenanceInfo.lr2_warning_flags.HasValue
                && !maintenanceInfo.lr2_resource_max_relative_cp932_bytes.HasValue
                && !maintenanceInfo.lr2_resource_has_parent_traversal.HasValue))
        {
            return;
        }

        Lr2ChartPathEvaluation pathEvaluation = EvaluateChartPath(chartPath);
        Lr2ResourceReferenceEvaluation resourceEvaluation;
        if (maintenanceInfo.lr2_resource_has_parent_traversal == true)
        {
            var previousFlags = (Lr2CompatibilityWarningFlags)(maintenanceInfo.lr2_warning_flags ?? 0);
            if (snapshotProvider == null
                || !TryEvaluateRelocatedParentTraversalResourceReferences(chartPath, snapshotProvider, out resourceEvaluation))
            {
                resourceEvaluation = new Lr2ResourceReferenceEvaluation(
                    previousFlags & (Lr2CompatibilityWarningFlags.ResourcePathEncodingUnsupported | Lr2CompatibilityWarningFlags.ResourcePathTooLong),
                    maintenanceInfo.lr2_resource_max_relative_cp932_bytes,
                    hasParentTraversal: true);
            }
        }
        else
        {
            var previousFlags = (Lr2CompatibilityWarningFlags)(maintenanceInfo.lr2_warning_flags ?? 0);
            resourceEvaluation = ReevaluateResourceReferencesForRelocatedPath(
                chartPath,
                maintenanceInfo.lr2_resource_max_relative_cp932_bytes,
                previousFlags);
        }

        maintenanceInfo.ApplyLr2CompatibilityEvaluation(pathEvaluation, resourceEvaluation);
    }

    private static bool TryEvaluateRelocatedParentTraversalResourceReferences(
        string chartPath,
        Func<ChartFileSnapshot> snapshotProvider,
        out Lr2ResourceReferenceEvaluation evaluation)
    {
        evaluation = default;
        try
        {
            ChartFileSnapshot snapshot = snapshotProvider();
            var parsed = BMSFile.CreateBMSFileFromSnapshot(snapshot);
            evaluation = EvaluateBmsResourceReferences(chartPath, parsed);
            return true;
        }
        catch (Exception ex) when (ex is IOException
            || ex is UnauthorizedAccessException
            || ex is ArgumentException
            || ex is NotSupportedException
            || ex is PathTooLongException)
        {
            return false;
        }
    }

    internal static bool TryGetCp932ByteCount(string value, out int byteCount)
    {
        byteCount = 0;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }
        if (TryGetAsciiByteCount(value, out byteCount))
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

    private static IEnumerable<Lr2ResourcePathReference> EnumerateBmsSupportedResourcePaths(BMSFile file)
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
                    yield return new Lr2ResourcePathReference(rawPath, reference.NormalizedPath);
                }
            }
        }
        else
        {
            foreach (string audioPath in file.WAVfiles ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(audioPath))
                {
                    yield return new Lr2ResourcePathReference(audioPath, null);
                }
            }
            foreach (string visualPath in file.BGAfiles ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(visualPath))
                {
                    yield return new Lr2ResourcePathReference(visualPath, null);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(file.banner))
        {
            yield return new Lr2ResourcePathReference(file.banner, null);
        }
        if (!string.IsNullOrWhiteSpace(file.backbmp))
        {
            yield return new Lr2ResourcePathReference(file.backbmp, null);
        }
        if (!string.IsNullOrWhiteSpace(file.stagefile))
        {
            yield return new Lr2ResourcePathReference(file.stagefile, null);
        }
    }

    private static void ApplyUnsupportedResourceReferenceEvaluation(
        UnsupportedChartResourceReference reference,
        Lr2ResourcePathEvaluationContext context,
        ref Lr2CompatibilityWarningFlags flags,
        ref int? maxRelativeBytes,
        ref bool hasParentTraversal)
    {
        if (reference.Reason == ChartResourcePathNormalizationStatus.Cp932DecodeUnsupported)
        {
            flags |= Lr2CompatibilityWarningFlags.ResourcePathEncodingUnsupported;
            return;
        }
        if (reference.Reason == ChartResourcePathNormalizationStatus.ParentTraversalUnsupported
            && !string.IsNullOrWhiteSpace(reference.RawPath))
        {
            hasParentTraversal = true;
            ApplyResourcePathEvaluation(
                context,
                reference.RawPath,
                reference.RawPath,
                ref flags,
                ref maxRelativeBytes,
                ref hasParentTraversal);
        }
    }

    private static Lr2ResourcePathEvaluationContext CreateResourcePathEvaluationContext(string chartPath)
    {
        try
        {
            string directory = Path.GetDirectoryName(chartPath ?? string.Empty);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return new Lr2ResourcePathEvaluationContext(null, chartDirectoryEncodingSupported: true);
            }
            return TryGetCp932ByteCount(directory, out int directoryBytes)
                ? new Lr2ResourcePathEvaluationContext(directoryBytes, chartDirectoryEncodingSupported: true)
                : new Lr2ResourcePathEvaluationContext(null, chartDirectoryEncodingSupported: false);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return new Lr2ResourcePathEvaluationContext(null, chartDirectoryEncodingSupported: false);
        }
    }

    private static void ApplyResourcePathEvaluation(
        Lr2ResourcePathEvaluationContext context,
        string rawPath,
        string normalizedPath,
        ref Lr2CompatibilityWarningFlags flags,
        ref int? maxRelativeBytes,
        ref bool hasParentTraversal)
    {
        hasParentTraversal |= HasParentTraversalSegment(rawPath)
            || HasParentTraversalSegment(normalizedPath);
        if (!TryGetCp932ByteCount(rawPath, out _))
        {
            flags |= Lr2CompatibilityWarningFlags.ResourcePathEncodingUnsupported;
            return;
        }

        if (!TryGetResourceRelativePathCp932ByteCount(rawPath, normalizedPath, out int relativeBytes))
        {
            return;
        }

        maxRelativeBytes = Math.Max(maxRelativeBytes.GetValueOrDefault(), relativeBytes);
        if (TryGetResolvedResourcePathCp932ByteCount(context, relativeBytes, out int resolvedBytes)
            && resolvedBytes > MaxLegacyPathBytes)
        {
            flags |= Lr2CompatibilityWarningFlags.ResourcePathTooLong;
        }
    }

    private static bool TryGetResourceRelativePathCp932ByteCount(
        string rawPath,
        string normalizedPath,
        out int byteCount)
    {
        string relativePath = normalizedPath;
        if (string.IsNullOrWhiteSpace(relativePath)
            && !TryNormalizeRelativeResourcePathForByteCount(rawPath, out relativePath))
        {
            relativePath = rawPath;
        }
        return TryGetCp932ByteCount(relativePath, out byteCount);
    }

    private static bool HasParentTraversalSegment(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return false;
        }

        string normalized = rawPath.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        foreach (string segment in normalized.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryGetResolvedResourcePathCp932ByteCount(
        Lr2ResourcePathEvaluationContext context,
        int relativeBytes,
        out int byteCount)
    {
        byteCount = 0;
        if (!context.ChartDirectoryEncodingSupported)
        {
            return false;
        }
        if (!context.ChartDirectoryCp932Bytes.HasValue)
        {
            byteCount = relativeBytes;
            return true;
        }
        byteCount = context.ChartDirectoryCp932Bytes.Value + 1 + relativeBytes;
        return true;
    }

    private static bool TryGetAsciiByteCount(string value, out int byteCount)
    {
        byteCount = 0;
        if (value == null)
        {
            return false;
        }
        foreach (char ch in value)
        {
            if (ch > 0x7f)
            {
                byteCount = 0;
                return false;
            }
        }
        byteCount = value.Length;
        return true;
    }

    private static bool TryNormalizeRelativeResourcePathForByteCount(string rawPath, out string relativePath)
    {
        relativePath = null;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return false;
        }

        string normalized = rawPath.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        normalized = normalized.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.IndexOfAny(InvalidPathChars) >= 0
            || Path.IsPathRooted(normalized))
        {
            return false;
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
                return false;
            }
            segments.Add(segment);
        }
        if (segments.Count == 0)
        {
            return false;
        }
        relativePath = string.Join(Path.DirectorySeparatorChar.ToString(), segments);
        return true;
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

}
