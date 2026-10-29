using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>一操作でだけ保持する旧物理範囲と、その根を一度探索した登録置換結果です。</summary>
internal sealed class PendingPackageNormalizationGroup
{
    /// <summary>パス要素境界で包含する最上位の物理範囲です。</summary>
    internal string RootPath { get; init; }
    /// <summary>根がdirectory範囲の場合は真です。file兄弟の共通親へ拡張しません。</summary>
    internal bool IsDirectory => RootScope.IsDirectory == true;
    /// <summary>取得済みroot事実です。unknownをdirectory成功kindへ変えません。</summary>
    internal PendingPackageScopeObservation RootScope { get; init; }
    /// <summary>子別結果の採用とmissing解除を抑止する保全境界です。物理操作の資格ではありません。</summary>
    internal bool ProtectsDescendants => RootScope.ProtectsDescendants;
    /// <summary>今回の実入力と接続している場合だけ、従来の後段資格を持ちます。</summary>
    internal bool IsInputConnected { get; init; }
    /// <summary>状態の継承と公開集合からの除去に使う旧保留参照です。起動復元の生DBキーはその試行の初期snapshotが別に保持します。</summary>
    internal List<ChartPackage> Sources { get; } = [];
    /// <summary>旧登録のcanonical物理範囲です。getterを呼ばずに構築します。</summary>
    internal Dictionary<ChartPackage, PendingPackageScopeObservation> SourceScopes { get; } = [];
    /// <summary>完全に読めた場合だけ採用できる、現在の非重複単位です。</summary>
    internal List<ChartPackage> Packages { get; } = [];
    /// <summary>新scopeの健康度を初期評価した後に採用する、同path・同SHAの確定復号だけです。旧健康度のfallbackに使いません。</summary>
    internal Dictionary<PackageChartEntry, ChartFile> FixedEncodings { get; } = [];
    /// <summary>全譜面と完全性を共有する取得結果です。</summary>
    internal ChartPackageDiscoveryResult Discovery { get; set; }
    /// <summary>この群の新集合を一括採用できるかを示します。</summary>
    internal bool IsComplete => Discovery?.IsComplete == true;

    /// <summary>canonical索引と祖先探索で重複群を作ります。重複しない保留は具体化せず、全組合せ比較を行いません。</summary>
    /// <param name="pending">状態継承と登録差分の元になる旧保留です。</param>
    /// <param name="inputs">今回入力の範囲です。起動復元では指定しません。</param>
    /// <param name="directoryInputs">通常入力の分類で確定したdirectory範囲です。</param>
    /// <param name="sourceScopes">同じ要求で一回取得したsource属性事実です。失敗は取得不能のまま保全へ接続します。</param>
    internal static List<PendingPackageNormalizationGroup> Build(
        IEnumerable<ChartPackage> pending, IReadOnlyDictionary<string, PendingPackageScopeObservation> sourceScopes,
        IEnumerable<string> inputs = null, IReadOnlySet<string> directoryInputs = null)
    {
        ArgumentNullException.ThrowIfNull(sourceScopes);
        var nodes = new Dictionary<string, ScopeNode>(StringComparer.OrdinalIgnoreCase);
        var sourceNodes = new Dictionary<ChartPackage, ScopeNode>();
        var sourceOrder = new Dictionary<ChartPackage, int>();
        foreach (ChartPackage source in pending ?? [])
        {
            if (source == null || !LongPathFileSystem.TryNormalizePhysicalScopePath(source.path, out string path)) { continue; }
            if (!sourceScopes.TryGetValue(path, out PendingPackageScopeObservation scope)) { continue; }
            ScopeNode node = GetNode(scope);
            node.Sources.Add(source);
            sourceNodes.Add(source, node);
            sourceOrder.Add(source, sourceOrder.Count);
        }
        foreach (string input in inputs ?? [])
        {
            if (!LongPathFileSystem.TryNormalizePhysicalScopePath(input, out string path)) { continue; }
            GetNode(PendingPackageScopeObservation.FromInput(path, directoryInputs?.Contains(path) == true)).IsInput = true;
        }
        foreach (string input in directoryInputs ?? new HashSet<string>())
        {
            if (LongPathFileSystem.TryNormalizePhysicalScopePath(input, out string path)) { GetNode(PendingPackageScopeObservation.FromInput(path, true)).IsInput = true; }
        }
        var byRoot = new Dictionary<ScopeNode, List<ScopeNode>>();
        foreach (ScopeNode node in nodes.Values)
        {
            ScopeNode root = node;
            for (string parent = Path.GetDirectoryName(node.Path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            {
                if (nodes.TryGetValue(parent, out ScopeNode ancestor) && ancestor.Scope.ProtectsDescendants) { root = ancestor; }
            }
            if (!byRoot.TryGetValue(root, out List<ScopeNode> members)) { byRoot.Add(root, members = []); }
            members.Add(node);
        }
        List<PendingPackageNormalizationGroup> groups = [];
        foreach ((ScopeNode root, List<ScopeNode> members) in byRoot)
        {
            List<ChartPackage> sources = [.. members.SelectMany(node => node.Sources)];
            bool connected = members.Any(node => node.IsInput);
            bool unavailable = members.Any(node => node.Scope.Failure != null);
            if (sources.Count == 0 || (sources.Count == 1 && !connected && !unavailable)) { continue; }
            if (!root.Scope.ProtectsDescendants) { continue; }
            var group = new PendingPackageNormalizationGroup
            {
                RootPath = root.Path,
                RootScope = root.Scope,
                IsInputConnected = connected
            };
            // 元の列挙順を優先し、canonical表記かどうかでsurvivorを変えません。
            foreach (ChartPackage source in sources.OrderBy(source => sourceOrder[source]))
            {
                ScopeNode node = sourceNodes[source];
                group.Sources.Add(source);
                group.SourceScopes.Add(source, node.Scope);
            }
            groups.Add(group);
        }
        return groups;

        ScopeNode GetNode(PendingPackageScopeObservation scope)
        {
            if (!nodes.TryGetValue(scope.Path, out ScopeNode node)) { nodes.Add(scope.Path, node = new ScopeNode(scope)); }
            return node;
        }
    }

    private sealed class ScopeNode(PendingPackageScopeObservation scope)
    {
        internal string Path => Scope.Path;
        internal PendingPackageScopeObservation Scope { get; } = scope;
        internal bool IsInput { get; set; }
        internal List<ChartPackage> Sources { get; } = [];
    }
}
