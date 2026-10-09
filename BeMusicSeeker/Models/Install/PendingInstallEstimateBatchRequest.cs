using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Models;

internal sealed class PendingInstallEstimateBatchRequest
{
    public PendingInstallEstimateBatchSource Source { get; }

    public ChartPackage[] Packages { get; }

    public int PackageCount => Packages.Length;

    public int DeferredPackageCount { get; }

    public int TotalPackageCount => PackageCount + DeferredPackageCount;

    public string DisplayName { get; }

    public string[] RegroupEligibleSourceDirectories { get; }

    /// <summary>
    /// 同じ受理操作の評価へ再利用する、準備済み分割と入力元リソースです。
    /// </summary>
    public PendingEstimateSourceBatchSnapshot BatchSourceSnapshot { get; }

    /// <summary>同じ取り込み操作が捕捉した設定。手動推定は受付後の評価準備で捕捉します。</summary>
    internal BmsLibraryOptionsSnapshot OptionsSnapshot { get; }

    internal PerformanceInteraction PerformanceInteraction { get; }

    public PendingInstallEstimateBatchRequest(
        PendingInstallEstimateBatchSource source,
        IEnumerable<ChartPackage> packages,
        string displayName,
        IEnumerable<string> regroupEligibleSourceDirectories = null,
        int deferredPackageCount = 0,
        PendingEstimateSourceBatchSnapshot batchSourceSnapshot = null,
        PerformanceInteraction? performanceInteraction = null,
        BmsLibraryOptionsSnapshot optionsSnapshot = null)
    {
        OptionsSnapshot = optionsSnapshot;
        Source = source;
        Packages = [.. (packages ?? []).Where(package => package != null)];
        DeferredPackageCount = Math.Max(0, deferredPackageCount);
        DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? GetDisplayName(Packages.FirstOrDefault()?.path)
            : displayName;
        RegroupEligibleSourceDirectories = [.. (regroupEligibleSourceDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        BatchSourceSnapshot = batchSourceSnapshot;
        PerformanceInteraction = performanceInteraction
            ?? PerformanceInteraction.Start("install_estimation");
    }

    internal static string GetDisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fileName = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(fileName) ? path : fileName;
    }
}
