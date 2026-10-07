using System;
using System.Collections.Generic;
using System.IO;
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

    /// <summary>取得済みの確定結果から原文のCP932長と互換性を評価します。取得や照合用別名への置換は行いません。</summary>
    internal static Lr2ResourceReferenceEvaluation EvaluateResourceReferences(
        string chartPath,
        ChartResourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Lr2CompatibilityWarningFlags flags = Lr2CompatibilityWarningFlags.None;
        int? maxRelativeBytes = null;
        bool hasParentTraversal = false;
        Lr2ResourcePathEvaluationContext context = CreateResourcePathEvaluationContext(chartPath);
        foreach (ChartResourceReference reference in snapshot.ResourceReferences)
        {
            if (reference.Status == ChartResourcePathNormalizationStatus.Cp932DecodeUnsupported)
            {
                flags |= Lr2CompatibilityWarningFlags.ResourcePathEncodingUnsupported;
                continue;
            }
            // 空定義は従来の列挙対象外です。非空のUnknownや空LookupKeyの原文は引き続き評価します。
            if (string.IsNullOrWhiteSpace(reference.RawPath))
            {
                continue;
            }
            ApplyResourcePathEvaluation(context, reference.RawPath,
                ref flags, ref maxRelativeBytes, ref hasParentTraversal);
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

    /// <summary>移転先の保守事実を更新します。親参照は呼出元が明示取得した結果で再評価し、取得できない場合は既存の事実を維持します。</summary>
    /// <summary>移転先の経路に依存する保守事実だけを不変共通値へ反映します。</summary>
    internal static ResourceHealthMaintenanceSnapshot RefreshRelocatedMaintenanceFacts(
        ResourceHealthMaintenanceSnapshot value, string path, ChartResourceSnapshot resources = null)
    {
        if (value == null || (!value.Lr2WarningFlags.HasValue && !value.Lr2ResourceMaxRelativeCp932Bytes.HasValue
            && !value.Lr2ResourceHasParentTraversal.HasValue))
        {
            return value;
        }

        Lr2ChartPathEvaluation pathEvaluation = EvaluateChartPath(path);
        var previousFlags = (Lr2CompatibilityWarningFlags)(value.Lr2WarningFlags ?? 0);
        Lr2ResourceReferenceEvaluation resourceEvaluation = value.Lr2ResourceHasParentTraversal == true
            ? resources == null
                ? new(previousFlags & (Lr2CompatibilityWarningFlags.ResourcePathEncodingUnsupported | Lr2CompatibilityWarningFlags.ResourcePathTooLong), value.Lr2ResourceMaxRelativeCp932Bytes, true)
                : EvaluateResourceReferences(path, resources)
            : ReevaluateResourceReferencesForRelocatedPath(path, value.Lr2ResourceMaxRelativeCp932Bytes, previousFlags);
        return value with
        {
            Path = path,
            Lr2WarningFlags = (int)(pathEvaluation.WarningFlags | resourceEvaluation.WarningFlags),
            Lr2ResourceMaxRelativeCp932Bytes = resourceEvaluation.MaxRelativeCp932Bytes,
            Lr2ResourceHasParentTraversal = resourceEvaluation.HasParentTraversal,
            Origin = MaintenanceInfoOrigin.Calculated
        };
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
        ref Lr2CompatibilityWarningFlags flags,
        ref int? maxRelativeBytes,
        ref bool hasParentTraversal)
    {
        hasParentTraversal |= HasParentTraversalSegment(rawPath);
        if (!TryGetCp932ByteCount(rawPath, out _))
        {
            flags |= Lr2CompatibilityWarningFlags.ResourcePathEncodingUnsupported;
            return;
        }

        if (!TryGetResourceRelativePathCp932ByteCount(rawPath, out int relativeBytes))
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
        out int byteCount)
    {
        if (!TryNormalizeRelativeResourcePathForByteCount(rawPath, out string relativePath))
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
