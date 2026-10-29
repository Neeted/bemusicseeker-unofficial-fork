using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed partial class BmsLibraryPackageInstallService
{
    /// <summary>getterより先に重複の物理範囲をまとめ、群の最上位根を一度だけ現行分類へ渡します。取得済みdirectoryのentryと完全性を共用し、単独の旧保留は読みません。属性取得の正常NotFoundだけを空群とし、取得不能は原因付き不完全群へ保持します。</summary>
    /// <param name="sourceScopes">準備段階で捕捉済みのsource属性です。独立利用時だけここで一回取得し、rootを再取得しません。</param>
    internal List<PendingPackageNormalizationGroup> DiscoverPendingPackageNormalizations(
        IEnumerable<ChartPackage> pending, IEnumerable<string> inputs = null,
        IReadOnlySet<string> directoryInputs = null, CancellationToken token = default,
        IReadOnlyList<ChartPackageDiscoveryResult> knownDiscoveries = null,
        IReadOnlyDictionary<string, PendingPackageScopeObservation> sourceScopes = null)
    {
        List<ChartPackage> sources = [.. pending ?? []];
        sourceScopes ??= PendingPackageScopeObservation.CaptureSources(sources);
        List<PendingPackageNormalizationGroup> groups = PendingPackageNormalizationGroup.Build(sources, sourceScopes, inputs, directoryInputs);
        var knownByDirectory = new Dictionary<string, ChartPackageDiscoveryResult>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartPackageDiscoveryResult discovery in knownDiscoveries ?? [])
        {
            if (LongPathFileSystem.TryNormalizePhysicalScopePath(discovery.SourceDirectory, out string path))
            { knownByDirectory.TryAdd(path, discovery); }

        }
        foreach (PendingPackageNormalizationGroup group in groups)
        {
            List<Exception> failures = [.. group.SourceScopes.Values.Where(scope => scope.Failure != null).Select(scope => scope.Failure).Distinct()];
            if (failures.Count > 0)
            {
                group.Discovery = new ChartPackageDiscoveryResult { IsComplete = false };
                group.Discovery.IncompleteScopes.Add(group.RootPath);
                group.Discovery.Failures.AddRange(failures);
                continue;
            }
            if (group.RootScope.IsMissing)
            {
                group.Discovery = new ChartPackageDiscoveryResult();
                continue;
            }
            group.Discovery = knownByDirectory.TryGetValue(group.RootPath, out ChartPackageDiscoveryResult known)
                ? known : DiscoverDirectory(group.RootPath, recursive: false, token: token, sourceScopes: sourceScopes);
            if (group.IsComplete) { group.Packages.AddRange(group.Discovery.Packages); }
            if (group.IsComplete) { InheritPendingNormalizationState(group); }
        }
        return groups;
    }

    /// <summary>同一物理単位の指定を継承し、同path・同内容の確定復号を初期評価後へ準備します。別単位の配置・診断・元健康度は使いません。</summary>
    internal static void InheritPendingNormalizationState(PendingPackageNormalizationGroup group)
    {
        var fixedChartsByPath = new Dictionary<string, Dictionary<string, ChartFile>>(StringComparer.OrdinalIgnoreCase);
        var sameUnits = new Dictionary<string, ChartPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartPackage source in group.Sources)
        {
            string path = group.SourceScopes[source].Path;
            sameUnits.TryAdd(path, source);
            foreach (PackageChartEntry entry in source.CaptureMaterializedChartEntries())
            {
                ChartFile chart = entry.Chart;
                if (chart.ResourceHealthMaintenanceSnapshot?.EncodingFixed != true || string.IsNullOrWhiteSpace(chart.Sha256)) { continue; }
                if (!fixedChartsByPath.TryGetValue(chart.Path, out Dictionary<string, ChartFile> byHash))
                { fixedChartsByPath.Add(chart.Path, byHash = new Dictionary<string, ChartFile>(StringComparer.OrdinalIgnoreCase)); }
                byHash.TryAdd(chart.Sha256, chart);
            }
        }
        foreach (ChartPackage package in group.Packages)
        {
            // 同pathの実kindは要求の属性観測が正本です。再取得したfalseや拡張子推定へ戻りません。
            bool sameUnit = sameUnits.TryGetValue(package.path, out ChartPackage source)
                && group.SourceScopes[source].IsDirectory == PendingPackageScopeObservation.IsDirectoryPackage(package);
            if (sameUnit) { package.delete_parent = source.delete_parent; }
            Dictionary<string, PackageChartEntry> sameUnitEntries = sameUnit
                ? source.CaptureMaterializedChartEntries().ToDictionary(entry => entry.Chart.Path, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, PackageChartEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (PackageChartEntry entry in package.ChartEntries)
            {
                if (sameUnitEntries.TryGetValue(entry.Chart.Path, out PackageChartEntry sameEntry))
                {
                    entry.RestoreInstallDestinationState(sameEntry.CaptureInstallDestinationState());
                }
                if (!string.IsNullOrWhiteSpace(entry.Chart.Sha256)
                    && fixedChartsByPath.TryGetValue(entry.Chart.Path, out Dictionary<string, ChartFile> byHash)
                    && byHash.TryGetValue(entry.Chart.Sha256, out ChartFile previous))
                {
                    group.FixedEncodings.Add(entry, previous);
                }
            }
        }
    }
}
