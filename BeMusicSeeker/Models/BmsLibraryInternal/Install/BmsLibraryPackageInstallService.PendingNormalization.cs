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
        IReadOnlyDictionary<string, PendingPackageScopeObservation> sourceScopes = null,
        Func<ChartFileSnapshot, ChartFile> fixedEncodingSource = null)
    {
        List<ChartPackage> sources = [.. pending ?? []];
        fixedEncodingSource ??= CapturePendingFixedEncodingSource(sources);
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
                ? known : DiscoverDirectory(group.RootPath, recursive: false, token: token, sourceScopes: sourceScopes, fixedEncodingSource: fixedEncodingSource);
            if (group.IsComplete) { group.Packages.AddRange(group.Discovery.Packages); }
            if (group.IsComplete) { InheritPendingNormalizationState(group); }
        }
        return groups;
    }

    /// <summary>保留の既取得指定を同path・SHA単位で捕捉し、一回の入力取得後・発見前にだけ解決します。旧本文・元配置健康度は保持しません。</summary>
    private static Func<ChartFileSnapshot, ChartFile> CapturePendingFixedEncodingSource(IEnumerable<ChartPackage> sources)
    {
        var byPath = new Dictionary<string, Dictionary<string, ChartFile>>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartPackage source in sources ?? [])
        {
            foreach (PackageChartEntry entry in source.CaptureMaterializedChartEntries())
            {
                ChartFile chart = entry.Chart;
                if (chart.Kind != ChartFileKind.Bms || chart.ResourceHealthMaintenanceSnapshot?.EncodingFixed != true || string.IsNullOrWhiteSpace(chart.Sha256)) { continue; }
                if (!byPath.TryGetValue(chart.Path, out Dictionary<string, ChartFile> byHash))
                { byPath.Add(chart.Path, byHash = new Dictionary<string, ChartFile>(StringComparer.OrdinalIgnoreCase)); }
                byHash.TryAdd(chart.Sha256, chart);
            }
        }
        return snapshot => byPath.TryGetValue(snapshot.Path, out Dictionary<string, ChartFile> byHash)
            && byHash.TryGetValue(snapshot.Sha256, out ChartFile chart) ? chart : null;
    }

    /// <summary>同一物理単位の配置指定だけを継承します。文字コードは同path・同内容を確認した入力解析時に適用済みです。</summary>
    internal static void InheritPendingNormalizationState(PendingPackageNormalizationGroup group)
    {
        var sameUnits = new Dictionary<string, ChartPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartPackage source in group.Sources)
        {
            string path = group.SourceScopes[source].Path;
            sameUnits.TryAdd(path, source);
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

            }
        }
    }
}
