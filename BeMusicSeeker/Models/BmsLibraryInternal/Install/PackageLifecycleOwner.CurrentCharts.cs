using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed partial class PackageLifecycleOwner
{
    // 所属の派生参照だけを持つ。共通現在値、識別の世代、永続状態は所有しない。
    private readonly Dictionary<ChartPackage, HashSet<PackageChartEntry>> installedEntriesByPackage = [];
    private readonly Dictionary<PackageChartEntry, InstalledEntryMembership> installedEntryMemberships = [];
    private readonly Dictionary<OwnedChartToken, HashSet<PackageChartEntry>> installedEntriesByToken = [];

    /// <summary>確定値の局所適用で訪問したlive entry数です。既存索引の仕事量検証だけに使います。</summary>
    internal int LastCommittedChartEntryVisitCount { get; private set; }

    /// <summary>
    /// DBと共通現在値、派生索引の確定後に対応する導入済み項目だけへ適用します。
    /// 通知は返した処理を操作の排他解放後に実行し、離脱した所属へ遅延通知しません。
    /// </summary>
    /// <param name="charts">同じ所持tokenを継承した確定済みの共通現在値。</param>
    /// <param name="changes">今回だけの旧新値と所属参照を受け取る通知factsの収集先。</param>
    /// <returns>既存の通知延期を終了した後、排他解放後に実行する公開処理。</returns>
    internal Action PrepareCommittedChartApplication(IEnumerable<ChartFile> charts, ICollection<InstalledChartCurrentChange> changes = null)
    {
        var publications = new List<(PackageChartEntry Entry, InstalledEntryMembership Membership, Action Publish)>();
        lock (packageCollectionStateLock)
        {
            LastCommittedChartEntryVisitCount = 0;
            var currentByToken = new Dictionary<OwnedChartToken, ChartFile>();
            foreach (ChartFile chart in charts ?? [])
            {
                if (chart?.Token != null)
                {
                    currentByToken[chart.Token] = chart;
                }
            }
            foreach (ChartFile chart in currentByToken.Values)
            {
                if (!installedEntriesByToken.TryGetValue(chart.Token, out HashSet<PackageChartEntry> entries))
                {
                    continue;
                }
                foreach (PackageChartEntry entry in entries)
                {
                    LastCommittedChartEntryVisitCount++;
                    InstalledEntryMembership membership = installedEntryMemberships[entry];
                    ChartFile before = entry.Chart;
                    changes?.Add(new InstalledChartCurrentChange(entry, [.. membership.Packages], before, chart));
                    Func<Action> complete = entry.DeferPropertyChangedNotificationPublication();
                    try
                    {
                        entry.ApplyCurrentChart(chart);
                    }
                    finally
                    {
                        Action publish = complete();
                        if (publish != null)
                        {
                            publications.Add((entry, installedEntryMemberships[entry], publish));
                        }
                    }
                }
            }
        }
        return () =>
        {
            foreach ((PackageChartEntry entry, InstalledEntryMembership membership, Action publish) in publications)
            {
                bool current;
                ChartPackage[] packages;
                lock (packageCollectionStateLock)
                {
                    current = installedEntryMemberships.TryGetValue(entry, out InstalledEntryMembership active)
                        && ReferenceEquals(active, membership);
                    packages = current ? [.. membership.Packages] : [];
                }
                if (current)
                {
                    publish();
                    foreach (ChartPackage package in packages)
                    {
                        package.PublishDisplayTitle();
                    }
                }
            }
        };
    }

    private void OnInstalledPackageCollectionChanged(object sender, NotifyCollectionChangedEventArgs args)
    {
        lock (packageCollectionStateLock)
        {
            ReconcileInstalledPackageMembership(installedPackages);
        }
    }

    private void ReconcileInstalledPackageMembership(IEnumerable<ChartPackage> packages)
    {
        var next = new HashSet<ChartPackage>((packages ?? []).Where(package => package != null));
        foreach (ChartPackage package in installedEntriesByPackage.Keys.Where(package => !next.Contains(package)).ToArray())
        {
            package.ChartEntriesChanged -= OnInstalledPackageEntriesChanged;
            foreach (PackageChartEntry entry in installedEntriesByPackage[package])
            {
                UnregisterInstalledEntry(entry, package);
            }
            installedEntriesByPackage.Remove(package);
        }
        foreach (ChartPackage package in next)
        {
            if (installedEntriesByPackage.ContainsKey(package))
            {
                continue;
            }
            package.RetainMaterializedChartEntries();
            installedEntriesByPackage.Add(package, []);
            package.ChartEntriesChanged += OnInstalledPackageEntriesChanged;
            ReconcileInstalledEntries(package);
        }
    }

    private void OnInstalledPackageEntriesChanged(ChartPackage package)
    {
        lock (packageCollectionStateLock)
        {
            if (installedEntriesByPackage.ContainsKey(package))
            {
                package.RetainMaterializedChartEntries();
                ReconcileInstalledEntries(package);
            }
        }
    }

    private void ReconcileInstalledEntries(ChartPackage package)
    {
        var next = new HashSet<PackageChartEntry>(package.CaptureMaterializedChartEntries());
        HashSet<PackageChartEntry> previous = installedEntriesByPackage[package];
        foreach (PackageChartEntry entry in previous.Where(entry => !next.Contains(entry)))
        {
            UnregisterInstalledEntry(entry, package);
        }
        foreach (PackageChartEntry entry in next.Where(entry => !previous.Contains(entry)))
        {
            OwnedChartToken token = entry.Chart.Token;
            if (token == null)
            {
                continue;
            }
            if (installedEntryMemberships.TryGetValue(entry, out InstalledEntryMembership membership))
            {
                membership.Packages.Add(package);
            }
            else
            {
                installedEntryMemberships.Add(entry, new InstalledEntryMembership(token, package));
                if (token != null)
                {
                    if (!installedEntriesByToken.TryGetValue(token, out HashSet<PackageChartEntry> entries))
                    {
                        entries = [];
                        installedEntriesByToken.Add(token, entries);
                    }
                    entries.Add(entry);
                }
            }
        }
        installedEntriesByPackage[package] = next;
    }

    private void UnregisterInstalledEntry(PackageChartEntry entry, ChartPackage package)
    {
        if (!installedEntryMemberships.TryGetValue(entry, out InstalledEntryMembership membership))
        {
            return;
        }
        membership.Packages.Remove(package);
        if (membership.Packages.Count > 0)
        {
            return;
        }
        installedEntryMemberships.Remove(entry);
        if (membership.Token != null)
        {
            HashSet<PackageChartEntry> entries = installedEntriesByToken[membership.Token];
            entries.Remove(entry);
            if (entries.Count == 0)
            {
                installedEntriesByToken.Remove(membership.Token);
            }
        }
    }

    private sealed class InstalledEntryMembership(OwnedChartToken token, ChartPackage package)
    {
        internal OwnedChartToken Token { get; } = token;
        internal HashSet<ChartPackage> Packages { get; } = [package];
    }
}

/// <summary>確定したentryの旧新基本値とその時点の所属参照です。通知消費までだけ保持します。</summary>
internal sealed class InstalledChartCurrentChange
{
    /// <summary>共通現在値の適用時だけ、対応entryと所属参照列を捕捉します。</summary>
    internal InstalledChartCurrentChange(PackageChartEntry entry, IReadOnlyList<ChartPackage> packages, ChartFile before, ChartFile current)
    {
        Entry = entry;
        Packages = Array.AsReadOnly(packages.ToArray());
        Before = before;
        Current = current;
    }
    /// <summary>確定値を受け取ったlive entry参照です。</summary>
    internal PackageChartEntry Entry { get; }
    /// <summary>適用時点の所属参照です。呼出元の可変列を保持しません。</summary>
    internal IReadOnlyList<ChartPackage> Packages { get; }
    /// <summary>適用前の不変基本値です。</summary>
    internal ChartFile Before { get; }
    /// <summary>確定した不変基本現在値です。</summary>
    internal ChartFile Current { get; }
}
