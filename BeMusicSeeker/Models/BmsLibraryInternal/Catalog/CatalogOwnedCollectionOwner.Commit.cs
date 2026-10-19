using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed partial class CatalogOwnedCollectionOwner
{
    /// <summary>導入先推定のmetadata profileをカタログの実依存変更と同時に失効させる排他です。</summary>
    internal object MetadataProfileGate { get; } = new();
    /// <summary>原題・作者・所属に依存する既存のディレクトリ別profileです。</summary>
    internal Dictionary<string, InstallEstimationMetadataProfile> MetadataProfiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    private bool chartInfoInputCoverage;
    private readonly HashSet<OwnedChartToken> pendingChartInfoTokens = [];

    /// <summary>完了済み入力以降の対象だけを捕捉します。全置換・初回はfalseで全体経路を要求します。</summary>
    internal bool TryCapturePendingChartInfoInputs(out IReadOnlyList<ChartFile> values)
    {
        lock (gate)
        {
            var result = new List<ChartFile>(pendingChartInfoTokens.Count);
            if (chartInfoInputCoverage)
            {
                CatalogChartCollectionSnapshot snapshot = CaptureSnapshot();
                foreach (OwnedChartToken token in pendingChartInfoTokens)
                {
                    if (snapshot.TryGetChart(token, out ChartFile chart, out _))
                    {
                        result.Add(chart);
                    }
                }
            }
            values = result.AsReadOnly();
            return chartInfoInputCoverage;
        }
    }
    /// <summary>解析条件変更時だけ完了証拠を無効化し、次回に全体入力を要求します。</summary>
    internal void ResetChartInfoInputCoverage()
    {
        lock (gate) { chartInfoInputCoverage = false; pendingChartInfoTokens.Clear(); }
    }
    /// <summary>全体入力の捕捉と同じ排他内で、既存pending集合を未確認入力の基準にします。失敗後も再捕捉せず未完了だけを取得できます。</summary>
    internal void BeginChartInfoInputCoverage(IEnumerable<ChartFile> values)
    {
        lock (gate)
        {
            chartInfoInputCoverage = true;
            foreach (ChartFile chart in values)
            {
                if (chart?.Token != null) { pendingChartInfoTokens.Add(chart.Token); }
            }
        }
    }
    /// <summary>省略可能な結果を確認・採用した入力だけを完了します。捕捉後の入力変更は消さず、health・Title等の無関係な変更は許容します。</summary>
    internal void CompleteChartInfoInputs(IEnumerable<ChartFile> values, bool full, int? expectedCatalogRevision = null)
    {
        lock (gate)
        {
            if (expectedCatalogRevision.HasValue && expectedCatalogRevision.Value != CatalogRevision)
            {
                return;
            }

            if (full) { chartInfoInputCoverage = true; pendingChartInfoTokens.Clear(); }
            else
            {
                OwnedChartCollectionView view = collection.CreateCollectionView();
                foreach (ChartFile chart in values ?? [])
                {
                    if (chart?.Token != null && view.TryGetChart(chart.Token, out ChartFile current, out _)
                        && current.Kind == chart.Kind && current.Path == chart.Path
                        && string.Equals(current.Md5, chart.Md5, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(current.Sha256, chart.Sha256, StringComparison.OrdinalIgnoreCase)
                        && current.LastWriteTimeUtc == chart.LastWriteTimeUtc)
                    {
                        pendingChartInfoTokens.Remove(chart.Token);
                    }
                }
            }
        }
    }

    /// <summary>確定順の不変結果を登録する既存配送キューです。UI callback は呼びません。</summary>
    internal NormalLibraryRefreshPublisher RefreshPublisher { get; set; }

    /// <summary>操作が writer 内で検証・保存した属性だけを現在値に適用し、版・索引・結果を同時確定します。</summary>
    internal CatalogCommitReceipt Commit(Action<OwnedChartCollectionState> apply, bool isFullReplacement = false)
    {
        using (WriteGate.GetWriterGuard())
        {
            lock (gate)
            {
                int baseRevision = collectionVersion;
                var changes = new Dictionary<OwnedChartToken, CatalogChartChange>();
                var reorderedTokens = new HashSet<OwnedChartToken>();
                var candidateOrderKeyTokens = new HashSet<OwnedChartToken>();
                void Record(ChartFile before, ChartFile after)
                {
                    OwnedChartToken token = after?.Token ?? before?.Token;
                    if (changes.TryGetValue(token, out CatalogChartChange previous))
                    {
                        before = previous.Before;
                    }

                    changes[token] = new(before, after);
                }
                if (collection.CurrentValueChanged != null || collection.CurrentOrderChanged != null
                    || collection.CurrentCandidateOrderKeyChanged != null)
                {
                    throw new InvalidOperationException("Catalog commit cannot be nested.");
                }

                OwnedChartCollectionView beforeView = isFullReplacement ? collection.CreateCollectionView() : null;
                collection.CurrentValueChanged = Record;
                collection.CurrentOrderChanged = token => reorderedTokens.Add(token);
                collection.CurrentCandidateOrderKeyChanged = token => candidateOrderKeyTokens.Add(token);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo failure = null;
                try { apply(collection); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception);
                }
                finally
                {
                    collection.CurrentValueChanged = null;
                    collection.CurrentOrderChanged = null;
                    collection.CurrentCandidateOrderKeyChanged = null;
                }
                CatalogChartChange[] actual = [.. changes.Values.Where(change => !CatalogChartValueEquality.AreEqual(change.Before, change.After))];
                OwnedChartCollectionView currentView = collection.CreateCollectionView();
                ChartFile[] reorderedCharts = [.. reorderedTokens.Select(token => currentView.TryGetChart(token, out ChartFile chart, out _) ? chart : null)
                    .Where(chart => chart != null)];
                candidateOrderKeyTokens.UnionWith(reorderedTokens);
                ChartFile[] candidateOrderKeyCharts = [.. candidateOrderKeyTokens.Select(token => currentView.TryGetChart(token, out ChartFile chart, out _) ? chart : null)
                    .Where(chart => chart != null)];
                bool orderChanged = false;
                if (isFullReplacement && actual.Length == 0)
                {
                    OwnedChartCollectionView afterView = collection.CreateCollectionView();
                    orderChanged = !beforeView.BmsCharts.Select(chart => chart.Token).Concat(beforeView.BmsonCharts.Select(chart => chart.Token))
                        .SequenceEqual(afterView.BmsCharts.Select(chart => chart.Token).Concat(afterView.BmsonCharts.Select(chart => chart.Token)));
                }
                if (actual.Length == 0 && reorderedCharts.Length == 0 && !orderChanged)
                {
                    // 途中の故障等で値・位置が変わらなくても、採用済みcaptured pathとwarm候補のキーは揃えます。
                    if (candidateOrderKeyCharts.Length > 0) { ApplyCommitIndexes(actual, candidateOrderKeyCharts, false, baseRevision); }
                    failure?.Throw();
                    return new(baseRevision, baseRevision, [], false, CaptureSnapshot());
                }
                int revision = ++collectionVersion;
                ApplyCommitIndexes(actual, candidateOrderKeyCharts, isFullReplacement, revision);
                UpdateSpecializedDependencies(actual, isFullReplacement);
                var receipt = new CatalogCommitReceipt(baseRevision, revision, actual, isFullReplacement, CaptureSnapshot(), reorderedCharts.Select(chart => chart.Token));
                RefreshPublisher?.Register(receipt);
                failure?.Throw();
                return receipt;
            }
        }
    }

    /// <summary>同じtokenの現在値へ操作対象属性を適用します。準備時のfull値で別属性を上書きしません。</summary>
    internal CatalogCommitReceipt CommitCurrentValues(IEnumerable<ChartFile> values, Func<ChartFile, ChartFile, ChartFile> apply)
        => Commit(state =>
        {
            foreach (ChartFile prepared in values ?? [])
            {
                ChartFile current = state.ResolveCurrentChart(LibraryChartRef.FromChartFile(prepared));
                if (current?.Token != null && ReferenceEquals(current.Token, prepared?.Token))
                {
                    state.ApplyCurrentChartValue(apply(current, prepared));
                }
            }
        });

    private void UpdateSpecializedDependencies(IReadOnlyList<CatalogChartChange> changes, bool isFullReplacement)
    {
        CatalogChartChange[] analysisInputs = [.. changes.Where(change => change.ChangesCandidate
            || change.Before.LastWriteTimeUtc != change.After.LastWriteTimeUtc)];
        if (isFullReplacement)
        {
            chartInfoInputCoverage = false;
            pendingChartInfoTokens.Clear();
        }
        else if (analysisInputs.Length > 0)
        {
            foreach (CatalogChartChange change in analysisInputs)
            {
                if (change.Before?.Token != null)
                {
                    pendingChartInfoTokens.Remove(change.Before.Token);
                }

                if (change.After?.Token != null)
                {
                    pendingChartInfoTokens.Add(change.After.Token);
                }
            }
        }
        lock (MetadataProfileGate)
        {
            if (isFullReplacement) { MetadataProfiles.Clear(); return; }
            string[] paths = [.. changes.Where(change => change.Before == null || change.After == null
                || change.Before.Path != change.After.Path || change.Before.RawTitle != change.After.RawTitle
                || (change.Before.Kind == ChartFileKind.Bms ? change.Before.RawArtist != change.After.RawArtist : change.Before.Artist != change.After.Artist))
                .SelectMany(change => new[] { change.Before?.Path, change.After?.Path }).Where(path => !string.IsNullOrWhiteSpace(path))];
            foreach (string path in paths)
            {
                string directory;
                try { directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)); }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
                {
                    // 物理パスへ正規化できない保存値に対応するprofile keyは作られません。既存の受理範囲を狭めません。
                    continue;
                }
                while (!string.IsNullOrWhiteSpace(directory))
                {
                    MetadataProfiles.Remove(BeMusicSeeker.Models.Utils.LongPathFileSystem.TrimTrailingDirectorySeparators(directory));
                    string parent = System.IO.Path.GetDirectoryName(directory);
                    if (parent == directory)
                    {
                        break;
                    }

                    directory = parent;
                }
            }
        }
    }

    private void ApplyCommitIndexes(IReadOnlyList<CatalogChartChange> changes, IReadOnlyList<ChartFile> candidateOrderKeyCharts, bool isFullReplacement, int revision)
    {
        CatalogChartChange[] candidates = [.. changes.Where(change => change.ChangesCandidate)];
        if (isFullReplacement)
        {
            ClearHashIndexUnsafe();
            InvalidatePlaylistLibraryResolveIndexSnapshot();
            return;
        }
        lock (hashIndexSnapshotGate)
        {
            if (hashIndexRoot != null)
            {
                if (candidates.Length > 0)
                {
                    hashIndexRoot = hashIndexRoot.ApplyDeltas([.. candidates.Select(change => new OwnedChartHashIndexDelta(
                        change.Before?.Md5, change.Before?.Sha256, change.After?.Md5, change.After?.Sha256))], StoreWorkObserver, out bool membershipChanged);
                    if (membershipChanged)
                    {
                        hashIndexSnapshotVersion = Math.Max(1, hashIndexSnapshotVersion + 1);
                    }

                    StoreWorkObserver?.Invoke("owned_hash_delta_apply");
                }
                hashIndexSnapshot = CreateHashIndexVersionedSnapshotUnsafe(hashIndexSnapshot?.BuildElapsedMs ?? 0, revision);
            }
        }
        lock (lockPlaylistLibraryResolveIndexSnapshot)
        {
            if (playlistLibraryResolveIndexSnapshot == null)
            {
                return;
            }

            var removals = new List<PlaylistLibraryResolveChartFact>();
            var additions = new List<PlaylistLibraryResolveChartFact>();
            foreach (CatalogChartChange change in candidates)
            {
                if (change.Before != null)
                {
                    removals.Add(PlaylistLibraryResolveChartFact.ForRemoval(
                    change.Before.Kind, change.Before.Path, change.Before.Md5, change.Before.Sha256));
                }

                if (change.After != null && collection.TryGetCanonicalChartRefForExactPath(change.After.Kind, change.After.Path,
                    out LibraryChartRef reference, out OwnedChartCanonicalOrderKey order))
                {
                    additions.Add(PlaylistLibraryResolveChartFact.FromChart(reference, order));
                }
            }
            HashSet<OwnedChartToken> changedCandidates = [.. candidates.Select(change => change.Token)];
            foreach (ChartFile chart in candidateOrderKeyCharts)
            {
                if (changedCandidates.Contains(chart.Token)) { continue; }
                removals.Add(PlaylistLibraryResolveChartFact.ForRemoval(chart.Kind, chart.Path, chart.Md5, chart.Sha256));
                if (collection.TryGetCanonicalChartRefForExactPath(chart.Kind, chart.Path, out LibraryChartRef reference, out OwnedChartCanonicalOrderKey order))
                {
                    additions.Add(PlaylistLibraryResolveChartFact.FromChart(reference, order));
                }
            }
            if (removals.Count > 0 || additions.Count > 0)
            {
                if (!playlistLibraryResolveIndexSnapshot.TryApplyDelta(removals, additions,
                    PlaylistLibraryResolveIndexStoreWorkObserver, out PlaylistLibraryResolveIndexSnapshot next))
                {
                    throw new InvalidOperationException("Catalog candidate delta is inconsistent with its committed input.");
                }

                playlistLibraryResolveIndexSnapshot = next;
                playlistLibraryResolveIndexSnapshotVersion++;
            }
            playlistLibraryResolveIndexSnapshot = playlistLibraryResolveIndexSnapshot.WithMetadata(
                playlistLibraryResolveIndexSnapshotVersion, playlistLibraryResolveIndexSnapshot.BuildElapsedMs,
                playlistLibraryResolveIndexInvalidationVersion, revision);
        }
    }
}
