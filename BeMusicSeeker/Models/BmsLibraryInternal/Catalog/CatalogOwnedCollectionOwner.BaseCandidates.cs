using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>本体候補の索引の準備が終わった状態です。</summary>
internal enum BaseCandidateIndexPrepareResult
{
    /// <summary>既に構築済みで、何も構築しませんでした。</summary>
    Cached,
    /// <summary>この呼出しが構築した索引を設置しました。</summary>
    Installed,
    /// <summary>構築中に全置換または別の構築の設置があり、結果を破棄しました。</summary>
    Discarded
}

/// <summary>
/// 所持譜面の原題・作者・親フォルダから本体候補を引く派生索引を所有します。DBへ保存せず、スナップショットと確定結果へ含めません。
/// 書込み（確定時の差分と構築結果の設置）は既存の集合排他の下で直列にし、読取りは排他を取らず不変の索引への参照だけを読みます。
/// </summary>
internal sealed partial class CatalogOwnedCollectionOwner
{
    private CatalogBaseCandidateIndex baseCandidateIndex;
    private int baseCandidateInvalidationVersion;
    private readonly List<BaseCandidateBuildSession> activeBaseCandidateBuilds = [];

    /// <summary>
    /// 本体候補の索引の実処理を観測します。callbackはownerへ再入しません。
    /// "base_candidate_delta_apply"（反映した所属数の組）と "base_candidate_normalize"（確定時に、譜面一件のために題名または作者の正規化を実際に行ったこと。
    /// 同じ確定の中で入力が同じなら再利用し、呼びません）は集合排他の中で、
    /// "base_candidate_install_pending"（取込み後・設置前）は排他の外で呼ばれます。
    /// </summary>
    internal Action<string> BaseCandidateIndexWorkObserver { get; set; }

    /// <summary>構築済みの索引です。未構築（全置換の直後を含む）では null で、読取りは構築を起こしません。</summary>
    internal CatalogBaseCandidateIndex BaseCandidateIndex => Volatile.Read(ref baseCandidateIndex);

    /// <summary>
    /// 現在の索引から、項目の比較キーに合う所持フォルダを返します。未構築なら空です。排他を取りません。
    /// </summary>
    /// <param name="titleKey">項目の題名の比較キー。</param>
    /// <param name="artistKey">項目の作者の比較キー。</param>
    internal IReadOnlyList<BaseCandidate> FindBaseCandidates(string titleKey, string artistKey)
        => Volatile.Read(ref baseCandidateIndex)?.Find(titleKey, artistKey) ?? [];

    /// <summary>
    /// 確定区間の内部から、この確定の変更を索引へ反映します。全置換では索引を破棄します。
    /// 未構築なら構築せず、構築中の準備にだけ変更を渡します。途中で例外になった場合は索引を破棄して再送出します。
    /// </summary>
    private void UpdateBaseCandidateIndex(IReadOnlyList<CatalogChartChange> changes, bool isFullReplacement)
    {
        try
        {
            if (isFullReplacement)
            {
                DiscardBaseCandidateIndexUnsafe();
                return;
            }
            CatalogBaseCandidateIndex index = baseCandidateIndex;
            if (index == null && activeBaseCandidateBuilds.Count == 0) { return; }
            var deltas = new List<BaseCandidateDelta>();
            // 正規化の結果はこの確定の中だけで共有します。パスだけの変更は題名・作者の入力が前後で同じなので、同じキーを使います。
            var normalizer = new BaseCandidateKeyNormalizer(BaseCandidateIndexWorkObserver);
            foreach (CatalogChartChange change in changes)
            {
                if (!CatalogMetadataDependency.Affects(change)) { continue; }
                AddDelta(deltas, normalizer, change.Before, -1);
                AddDelta(deltas, normalizer, change.After, 1);
            }
            if (deltas.Count == 0) { return; }
            if (index != null)
            {
                BaseCandidateIndexWorkObserver?.Invoke("base_candidate_delta_apply");
                Volatile.Write(ref baseCandidateIndex, index.Apply(deltas));
                return;
            }
            foreach (BaseCandidateBuildSession session in activeBaseCandidateBuilds) { session.Pending.Add(deltas); }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DiscardBaseCandidateIndexUnsafe();
            throw;
        }
    }

    private static void AddDelta(List<BaseCandidateDelta> deltas, BaseCandidateKeyNormalizer normalizer, ChartFile chart, int count)
    {
        if (chart != null && normalizer.TryBuildKey(chart, out BaseCandidateKey key)) { deltas.Add(new BaseCandidateDelta(key, count)); }
    }

    /// <summary>集合排他の内側で、索引を未構築へ戻し、進行中の構築結果を無効にします。</summary>
    private void DiscardBaseCandidateIndexUnsafe()
    {
        Volatile.Write(ref baseCandidateIndex, null);
        baseCandidateInvalidationVersion++;
        foreach (BaseCandidateBuildSession session in activeBaseCandidateBuilds) { session.Pending.Clear(); }
    }

    /// <summary>
    /// 構築済みでなければ、所持譜面の現在値から索引を一度だけ構築して設置します。再試行はしません。
    /// 取消されたら未構築のまま例外で終わります。構築中の全置換や別の設置があれば結果を破棄し、構築中の局所確定は設置時にその時点の内容へ追いつかせます。
    /// </summary>
    /// <param name="cancellationToken">構築の取消。</param>
    /// <param name="maxDegreeOfParallelism">正規化の並列度。省略時は max(1, CPU数-1) です。結果は並列度によりません。</param>
    internal BaseCandidateIndexPrepareResult PrepareBaseCandidateIndex(CancellationToken cancellationToken, int? maxDegreeOfParallelism = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BaseCandidateBuildSession session;
        OwnedChartCollectionView view;
        lock (gate)
        {
            if (baseCandidateIndex != null) { return BaseCandidateIndexPrepareResult.Cached; }
            // 所持譜面のビューと無効化の版を同じ排他の中で捕捉し、以後の確定を設置まで受け取る。
            view = collection.CreateCollectionView();
            session = new BaseCandidateBuildSession(baseCandidateInvalidationVersion);
            activeBaseCandidateBuilds.Add(session);
        }
        try
        {
            CatalogBaseCandidateIndex built = BuildBaseCandidateIndex([.. view.BmsCharts.Concat(view.BmsonCharts)],
                maxDegreeOfParallelism ?? Math.Max(1, Environment.ProcessorCount - 1), cancellationToken);
            BaseCandidateIndexWorkObserver?.Invoke("base_candidate_install_pending");
            lock (gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (session.InvalidationVersion != baseCandidateInvalidationVersion || baseCandidateIndex != null)
                {
                    return BaseCandidateIndexPrepareResult.Discarded;
                }
                foreach (List<BaseCandidateDelta> deltas in session.Pending) { built = built.Apply(deltas); }
                Volatile.Write(ref baseCandidateIndex, built);
                return BaseCandidateIndexPrepareResult.Installed;
            }
        }
        finally
        {
            lock (gate) { activeBaseCandidateBuilds.Remove(session); }
        }
    }

    private CatalogBaseCandidateIndex BuildBaseCandidateIndex(ChartFile[] charts, int degree, CancellationToken cancellationToken)
    {
        // 生の題名と作者を別々に重複除去し、各一意値を一度だけ並列に正規化する。辞書は並列ループの後に一つのスレッドで作る。
        var titleIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var artistIds = new Dictionary<string, int>(StringComparer.Ordinal);
        int[] titleOf = new int[charts.Length];
        int[] artistOf = new int[charts.Length];
        var uniqueTitles = new List<string>();
        var uniqueArtists = new List<string>();
        for (int i = 0; i < charts.Length; i++)
        {
            if ((i & 1023) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
            (string title, string artist, _) = PackageInstallEstimationSnapshotBuilder.ProjectMetadataRecord(charts[i]);
            if (!titleIds.TryGetValue(title, out int titleId)) { titleIds.Add(title, titleId = uniqueTitles.Count); uniqueTitles.Add(title); }
            if (!artistIds.TryGetValue(artist, out int artistId)) { artistIds.Add(artist, artistId = uniqueArtists.Count); uniqueArtists.Add(artist); }
            titleOf[i] = titleId;
            artistOf[i] = artistId;
        }
        string[] titleKeys = new string[uniqueTitles.Count];
        string[] artistKeys = new string[uniqueArtists.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, degree), CancellationToken = cancellationToken };
        Parallel.For(0, titleKeys.Length, options, i => titleKeys[i] = InstallEstimationMetadataNormalizer.NormalizeTitleForTieBreak(uniqueTitles[i]));
        Parallel.For(0, artistKeys.Length, options, i => artistKeys[i] = InstallEstimationMetadataNormalizer.NormalizeArtistForTieBreak(uniqueArtists[i]));
        var builder = new CatalogBaseCandidateIndex.Builder();
        // 同じ親フォルダの文字列は一つのインスタンスへ集約し、譜面ごとや（題名, 作者, フォルダ）の組ごとに別の文字列を残さない。
        var folderPool = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < charts.Length; i++)
        {
            if ((i & 1023) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
            string titleKey = titleKeys[titleOf[i]];
            if (titleKey.Length == 0) { continue; }
            string folder = CatalogMetadataDependency.TryBuildParentFolderKey(charts[i].Path);
            if (folder == null) { continue; }
            if (!folderPool.TryGetValue(folder, out string pooled)) { folderPool.Add(folder, pooled = folder); }
            folder = pooled;
            builder.Add(new BaseCandidateKey(titleKey, artistKeys[artistOf[i]], folder));
        }
        return builder.ToIndex();
    }

    /// <summary>
    /// 一回の確定の中だけで使う、譜面一件の照合キーの作成器です。題名・作者の生文字列ごとに正規化の結果を保持して同じ入力の再計算を避け、
    /// 確定をまたいで保持しません。題名キーが空の譜面や親フォルダを作れない譜面は対象外（false）です。
    /// </summary>
    private sealed class BaseCandidateKeyNormalizer(Action<string> observer)
    {
        private readonly Dictionary<string, string> titleKeys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> artistKeys = new(StringComparer.Ordinal);

        /// <summary>
        /// 原題と、BMSは元の作者・bmsonは現在の作者を導入先推定と同じ射影で使ってキーを作ります。
        /// </summary>
        internal bool TryBuildKey(ChartFile chart, out BaseCandidateKey key)
        {
            key = default;
            (string title, string artist, string path) = PackageInstallEstimationSnapshotBuilder.ProjectMetadataRecord(chart);
            bool normalized = false;
            if (!titleKeys.TryGetValue(title, out string titleKey))
            {
                titleKeys.Add(title, titleKey = InstallEstimationMetadataNormalizer.NormalizeTitleForTieBreak(title));
                normalized = true;
            }
            string folder = titleKey.Length == 0 ? null : CatalogMetadataDependency.TryBuildParentFolderKey(path);
            string artistKey = null;
            if (folder != null && !artistKeys.TryGetValue(artist, out artistKey))
            {
                artistKeys.Add(artist, artistKey = InstallEstimationMetadataNormalizer.NormalizeArtistForTieBreak(artist));
                normalized = true;
            }
            if (normalized) { observer?.Invoke("base_candidate_normalize"); }
            if (folder == null) { return false; }
            key = new BaseCandidateKey(titleKey, artistKey, folder);
            return true;
        }
    }

    /// <summary>構築中に確定した変更を設置まで受け取る、一回の構築の記録です。集合排他の下でだけ触れます。</summary>
    private sealed class BaseCandidateBuildSession(int invalidationVersion)
    {
        internal int InvalidationVersion { get; } = invalidationVersion;

        internal List<List<BaseCandidateDelta>> Pending { get; } = [];
    }
}
