using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>一回の自動推定で実適用した項目と導入先だけを、再グループ化を越えて保持します。永続状態にはしません。</summary>
internal sealed class AutomaticInstallEstimateTargets
{
    private readonly Dictionary<PackageChartEntry, string> destinations = [];
    private readonly HashSet<ChartPackage> packages = [];

    /// <summary>既存の適用検査を通過し、実際に設定された項目だけを記録します。</summary>
    internal void Record(ChartPackage package, IEnumerable<PackageChartEntry> entries, string destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) { return; }
        foreach (PackageChartEntry entry in entries.Where(entry => string.Equals(entry.Chart?.InstallDestination, destination, StringComparison.OrdinalIgnoreCase)))
        {
            destinations[entry] = destination;
            packages.Add(package);
        }
    }

    /// <summary>再グループ化が作成した実項目対応に従い、今回の対象を置換先へ引き継ぎます。</summary>
    internal void Replace(IEnumerable<ChartPackage> sources, ChartPackage replacement, IReadOnlyDictionary<PackageChartEntry, PackageChartEntry> replacements)
    {
        bool affected = false;
        foreach (ChartPackage source in sources) { affected |= packages.Remove(source); }
        foreach ((PackageChartEntry oldEntry, PackageChartEntry newEntry) in replacements)
        {
            if (destinations.Remove(oldEntry, out string destination)) { destinations[newEntry] = destination; }
        }
        if (affected) { packages.Add(replacement); }
    }

    /// <summary>現在も保留にあり、全未所持項目が今回の実適用に由来して同じ導入先を持つ有限候補を返します。</summary>
    internal IReadOnlyList<ChartPackage> GetEligiblePackages(IReadOnlyCollection<ChartPackage> pending, Func<ChartFile, bool> isOwned)
    {
        // 全保留を複製せず、追跡対象の小さい集合だけを現在の保留と一回照合します。
        var pendingTargets = new HashSet<ChartPackage>(packages);
        pendingTargets.IntersectWith(pending);
        return packages.Where(package => pendingTargets.Contains(package)
            && package.ChartEntries.Any(entry => destinations.ContainsKey(entry))
            && package.ChartEntries.Where(entry => entry.Chart != null && !isOwned(entry.Chart)).All(entry =>
                destinations.TryGetValue(entry, out string destination)
                && string.Equals(entry.Chart.InstallDestination, destination, StringComparison.OrdinalIgnoreCase))
            && package.ChartEntries.Where(entry => entry.Chart != null && !isOwned(entry.Chart))
                .Select(entry => entry.Chart.InstallDestination).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1).ToArray();
    }
}
