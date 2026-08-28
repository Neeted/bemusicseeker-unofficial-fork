using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// ランプ集計へ渡す playlist の読み込み状態です。
/// </summary>
internal enum PlaylistLampInputState
{
    Loaded,
    Loading,
    Failed,
    Deleted
}

/// <summary>
/// ランプビューアの公開状態です。
/// </summary>
internal enum PlaylistLampViewerState
{
    Loading,
    Ready,
    Empty,
    Deleted,
    Failed
}

/// <summary>
/// ランプビューアが使用する score source の種別です。
/// </summary>
internal enum PlaylistLampScoreSource
{
    None,
    Lr2,
    Beatoraja
}

/// <summary>
/// ランプビューアから見た score source の健全性です。
/// </summary>
internal enum PlaylistLampScoreHealth
{
    None,
    Ready,
    Failed
}

/// <summary>
/// clear graph で公開する意味上のカテゴリです。
/// </summary>
internal enum PlaylistLampClearCategory
{
    MAX,
    PERFECT,
    FC,
    EXHARD,
    HARD,
    NORMAL,
    EASY,
    ASSIST,
    FAILED,
    NP
}

/// <summary>
/// DJ rank graph で公開する意味上のカテゴリです。
/// </summary>
internal enum PlaylistLampRankCategory
{
    AAA,
    AA,
    A,
    B,
    C,
    D,
    E,
    F,
    NP
}

/// <summary>
/// クリック対象 graph の種別です。
/// </summary>
internal enum PlaylistLampSegmentKind
{
    Clear,
    Rank
}

/// <summary>
/// score row から読み取った値を、mutable な <see cref="BMSScore"/> から切り離して保持します。
/// </summary>
internal sealed class PlaylistLampScore
{
    /// <summary>
    /// score を生成します。
    /// </summary>
    public PlaylistLampScore(
        string hash,
        string sha256,
        ClearType clear,
        RankType rank,
        int perfect,
        int great,
        int totalNotes,
        int playCount)
    {
        Hash = Normalize(hash);
        Sha256 = Normalize(sha256);
        Clear = clear;
        Rank = rank;
        Perfect = perfect;
        Great = great;
        TotalNotes = totalNotes;
        PlayCount = playCount;
    }

    /// <summary>LR2 score の MD5 です。</summary>
    public string Hash { get; }

    /// <summary>beatoraja score の SHA256 です。</summary>
    public string Sha256 { get; }

    /// <summary>保存されていた clear 値です。</summary>
    public ClearType Clear { get; }

    /// <summary>保存されていた DJ rank です。</summary>
    public RankType Rank { get; }

    /// <summary>PERFECT 判定数です。</summary>
    public int Perfect { get; }

    /// <summary>GREAT 判定数です。</summary>
    public int Great { get; }

    /// <summary>譜面の総ノーツ数です。</summary>
    public int TotalNotes { get; }

    /// <summary>プレイ回数です。</summary>
    public int PlayCount { get; }

    /// <summary>
    /// mutable な score row の値を immutable projection へコピーします。
    /// </summary>
    /// <param name="score">コピー元の score row。</param>
    /// <returns>コピーした score。<paramref name="score"/> が null の場合は null。</returns>
    public static PlaylistLampScore FromBmsScore(BMSScore score)
    {
        if (score == null)
        {
            return null;
        }
        return new PlaylistLampScore(
            score.hash,
            null,
            score.clear,
            score.rank,
            score.perfect,
            score.great,
            score.totalnotes,
            score.playcount);
    }

    private static string Normalize(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}

/// <summary>
/// playlist entry を集計用に immutable 化した snapshot です。
/// </summary>
internal sealed class PlaylistLampEntrySnapshot
{
    /// <summary>
    /// playlist entry snapshot を生成します。
    /// </summary>
    /// <param name="folderName">通常 folder の論理名。</param>
    /// <param name="identityKey">同一 chart を同一 folder 内でまとめる安定 identity。</param>
    /// <param name="isOwned">現在ライブラリに所持 chart があるか。</param>
    /// <param name="md5">playlist entry の MD5。</param>
    /// <param name="sha256">playlist entry の SHA256。</param>
    /// <param name="resolvedPath">resolve index が選択した chart path。</param>
    /// <param name="resolvedMd5">resolve index が選択した chart の MD5。</param>
    /// <param name="resolvedSha256">resolve index が選択した chart の SHA256。</param>
    /// <param name="isRemoved">削除済み entry か。</param>
    /// <param name="isDummy">空 folder 用 dummy entry か。</param>
    /// <param name="chartInfoSha256">entry に対応する chart_info の SHA256。</param>
    public PlaylistLampEntrySnapshot(
        string folderName,
        string identityKey,
        bool isOwned,
        string md5 = null,
        string sha256 = null,
        string resolvedPath = null,
        string resolvedMd5 = null,
        string resolvedSha256 = null,
        bool isRemoved = false,
        bool isDummy = false,
        string chartInfoSha256 = null)
    {
        FolderName = folderName ?? string.Empty;
        Md5 = Normalize(md5);
        Sha256 = Normalize(sha256);
        ResolvedPath = Normalize(resolvedPath);
        ResolvedMd5 = Normalize(resolvedMd5);
        ResolvedSha256 = Normalize(resolvedSha256);
        ChartInfoSha256 = Normalize(chartInfoSha256);
        IsOwned = isOwned;
        IsRemoved = isRemoved;
        IsDummy = isDummy || string.Equals(Md5, BMSTableEntry.DUMMY_MD5_FOR_EMPTY_FOLDER, StringComparison.OrdinalIgnoreCase);
        IdentityKey = Normalize(identityKey);
        if (string.IsNullOrWhiteSpace(IdentityKey))
        {
            IdentityKey = !string.IsNullOrWhiteSpace(ResolvedMd5)
                ? "md5:" + ResolvedMd5
                : !string.IsNullOrWhiteSpace(Md5)
                    ? "md5:" + Md5
                    : !string.IsNullOrWhiteSpace(ResolvedSha256)
                        ? "sha256:" + ResolvedSha256
                        : !string.IsNullOrWhiteSpace(Sha256)
                            ? "sha256:" + Sha256
                            : !string.IsNullOrWhiteSpace(ResolvedPath)
                                ? "path:" + ResolvedPath
                                : string.Empty;
        }
    }

    /// <summary>通常 folder の論理名です。</summary>
    public string FolderName { get; }

    /// <summary>同一 chart 判定に使う安定 identity です。</summary>
    public string IdentityKey { get; private set; }

    /// <summary>playlist entry の MD5 です。</summary>
    public string Md5 { get; }

    /// <summary>playlist entry の SHA256 です。</summary>
    public string Sha256 { get; }

    /// <summary>owned chart resolve index が選択した path です。</summary>
    public string ResolvedPath { get; }

    /// <summary>owned chart resolve index が選択した MD5 です。</summary>
    public string ResolvedMd5 { get; }

    /// <summary>owned chart resolve index が選択した SHA256 です。</summary>
    public string ResolvedSha256 { get; }

    /// <summary>entry の chart_info に保存された SHA256 です。</summary>
    public string ChartInfoSha256 { get; }

    /// <summary>所持 chart かどうかです。</summary>
    public bool IsOwned { get; }

    /// <summary>削除済み entry かどうかです。</summary>
    public bool IsRemoved { get; }

    /// <summary>空 folder 用 dummy entry かどうかです。</summary>
    public bool IsDummy { get; }

    /// <summary>LR2 source lookup に使う MD5 の優先解決値です。</summary>
    public string ScoreHash => !string.IsNullOrWhiteSpace(ResolvedMd5) ? ResolvedMd5 : Md5;

    /// <summary>beatoraja source lookup に使う SHA256 の優先解決値です。</summary>
    public string ScoreSha256 => !string.IsNullOrWhiteSpace(ResolvedSha256)
        ? ResolvedSha256
        : !string.IsNullOrWhiteSpace(Sha256)
            ? Sha256
            : ChartInfoSha256;

    /// <summary>集計対象の実 entry かどうかです。</summary>
    public bool IsActiveRealEntry => !IsRemoved && !IsDummy;

    /// <summary>
    /// 同一 folder 内の重複 entry を immutable に統合します。
    /// </summary>
    /// <param name="other">統合する entry。</param>
    /// <returns>両方の identity / ownership / resolve 情報を保持する snapshot。</returns>
    internal PlaylistLampEntrySnapshot Merge(PlaylistLampEntrySnapshot other)
    {
        if (other == null)
        {
            return this;
        }
        return new PlaylistLampEntrySnapshot(
            FolderName,
            IdentityKey,
            IsOwned || other.IsOwned,
            FirstNonEmpty(Md5, other.Md5),
            FirstNonEmpty(Sha256, other.Sha256),
            FirstNonEmpty(ResolvedPath, other.ResolvedPath),
            FirstNonEmpty(ResolvedMd5, other.ResolvedMd5),
            FirstNonEmpty(ResolvedSha256, other.ResolvedSha256),
            IsRemoved && other.IsRemoved,
            IsDummy || other.IsDummy,
            FirstNonEmpty(ChartInfoSha256, other.ChartInfoSha256));
    }

    private static string FirstNonEmpty(string first, string second)
    {
        return !string.IsNullOrWhiteSpace(first) ? first : second;
    }

    private static string Normalize(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}

/// <summary>
/// score source と immutable score index の snapshot です。
/// </summary>
internal sealed class PlaylistLampScoreSnapshot
{
    /// <summary>
    /// score snapshot を生成します。
    /// </summary>
    public PlaylistLampScoreSnapshot(
        ActiveScoreSource source,
        ScoreTableLoadStatus loadStatus,
        int version,
        long sourceGeneration,
        DateTime? lastUpdatedUtc,
        IReadOnlyDictionary<string, PlaylistLampScore> scoresByHash = null,
        IReadOnlyDictionary<string, PlaylistLampScore> scoresBySha256 = null,
        string failureMessage = null)
    {
        Source = source;
        LoadStatus = loadStatus;
        Version = version;
        SourceGeneration = sourceGeneration;
        LastUpdatedUtc = lastUpdatedUtc;
        FailureMessage = failureMessage ?? string.Empty;
        ScoresByHash = new ReadOnlyDictionary<string, PlaylistLampScore>(
            new Dictionary<string, PlaylistLampScore>(scoresByHash ?? new Dictionary<string, PlaylistLampScore>(), StringComparer.OrdinalIgnoreCase));
        ScoresBySha256 = new ReadOnlyDictionary<string, PlaylistLampScore>(
            new Dictionary<string, PlaylistLampScore>(scoresBySha256 ?? new Dictionary<string, PlaylistLampScore>(), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>active score source です。</summary>
    public ActiveScoreSource Source { get; }

    /// <summary>score table のロード状態です。</summary>
    public ScoreTableLoadStatus LoadStatus { get; }

    /// <summary>score snapshot の版数です。</summary>
    public int Version { get; }

    /// <summary>score source 切り替え世代です。</summary>
    public long SourceGeneration { get; }

    /// <summary>score source snapshot の更新時刻です。</summary>
    public DateTime? LastUpdatedUtc { get; }

    /// <summary>LR2 source の immutable score index です。</summary>
    public IReadOnlyDictionary<string, PlaylistLampScore> ScoresByHash { get; }

    /// <summary>beatoraja source の immutable score index です。</summary>
    public IReadOnlyDictionary<string, PlaylistLampScore> ScoresBySha256 { get; }

    /// <summary>score load failure の診断メッセージです。</summary>
    public string FailureMessage { get; }

    /// <summary>score 依存統計を計算できる状態かどうかです。</summary>
    public bool IsScoreDataAvailable => Source != ActiveScoreSource.None && LoadStatus == ScoreTableLoadStatus.Loaded;

    /// <summary>ランプビューア向け source enum を返します。</summary>
    public PlaylistLampScoreSource LampSource => Source switch
    {
        ActiveScoreSource.Lr2 => PlaylistLampScoreSource.Lr2,
        ActiveScoreSource.Beatoraja => PlaylistLampScoreSource.Beatoraja,
        _ => PlaylistLampScoreSource.None
    };

    /// <summary>ランプビューア向け health enum を返します。</summary>
    public PlaylistLampScoreHealth Health => LoadStatus switch
    {
        ScoreTableLoadStatus.Loaded when Source != ActiveScoreSource.None => PlaylistLampScoreHealth.Ready,
        ScoreTableLoadStatus.Failed => PlaylistLampScoreHealth.Failed,
        _ => PlaylistLampScoreHealth.None
    };

    /// <summary>
    /// BMSLibrary の mutable score snapshot をランプ用 immutable snapshot へ投影します。
    /// </summary>
    /// <param name="snapshot">BMSLibrary の score snapshot。</param>
    /// <returns>ランプ用 snapshot。入力が null の場合は未設定 snapshot。</returns>
    internal static PlaylistLampScoreSnapshot FromBmsLibrarySnapshot(BMSLibrary.ScoreSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return new PlaylistLampScoreSnapshot(
                ActiveScoreSource.None,
                ScoreTableLoadStatus.NotConfigured,
                0,
                0L,
                null);
        }
        var byHash = new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSScore score in snapshot.Scores ?? [])
        {
            PlaylistLampScore projected = PlaylistLampScore.FromBmsScore(score);
            if (projected != null && !string.IsNullOrWhiteSpace(projected.Hash))
            {
                byHash[projected.Hash] = projected;
            }
        }
        var bySha256 = new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, BMSScore> pair in snapshot.ScoresBySha256 ?? new Dictionary<string, BMSScore>())
        {
            PlaylistLampScore projected = PlaylistLampScore.FromBmsScore(pair.Value);
            if (projected != null && !string.IsNullOrWhiteSpace(pair.Key))
            {
                bySha256[pair.Key.Trim()] = projected;
            }
        }
        return new PlaylistLampScoreSnapshot(
            snapshot.ActiveScoreSource,
            snapshot.LoadStatus,
            snapshot.Version,
            snapshot.SourceGeneration,
            snapshot.LoadedAtUtc,
            byHash,
            bySha256,
            snapshot.LoadFailureMessage);
    }

    /// <summary>
    /// score source の規則に従って entry の score を解決します。
    /// </summary>
    /// <param name="entry">解決対象 entry。</param>
    /// <returns>score row。未登録または source unavailable の場合は null。</returns>
    internal PlaylistLampScore Resolve(PlaylistLampEntrySnapshot entry)
    {
        if (!IsScoreDataAvailable || entry == null)
        {
            return null;
        }
        if (Source == ActiveScoreSource.Beatoraja)
        {
            return !string.IsNullOrWhiteSpace(entry.ScoreSha256)
                && ScoresBySha256.TryGetValue(entry.ScoreSha256, out PlaylistLampScore bySha256)
                ? bySha256
                : null;
        }
        return !string.IsNullOrWhiteSpace(entry.ScoreHash)
            && ScoresByHash.TryGetValue(entry.ScoreHash, out PlaylistLampScore byHash)
            ? byHash
            : null;
    }
}

/// <summary>
/// playlist lamp build の version stamp です。
/// </summary>
internal readonly struct PlaylistLampDependencyStamp : IEquatable<PlaylistLampDependencyStamp>
{
    /// <summary>依存 stamp を生成します。</summary>
    public PlaylistLampDependencyStamp(
        int entriesRevision,
        int catalogVersion,
        int ownedCollectionVersion,
        int scoreSnapshotVersion,
        long scoreSourceGeneration,
        ActiveScoreSource scoreSource,
        ScoreTableLoadStatus scoreLoadStatus,
        int chartInfoIndexVersion = 0)
    {
        EntriesRevision = entriesRevision;
        CatalogVersion = catalogVersion;
        OwnedCollectionVersion = ownedCollectionVersion;
        ScoreSnapshotVersion = scoreSnapshotVersion;
        ScoreSourceGeneration = scoreSourceGeneration;
        ScoreSource = scoreSource;
        ScoreLoadStatus = scoreLoadStatus;
        ChartInfoIndexVersion = chartInfoIndexVersion;
    }

    /// <summary>playlist entries revision。</summary>
    public int EntriesRevision { get; }

    /// <summary>catalog storage version。</summary>
    public int CatalogVersion { get; }

    /// <summary>owned collection version。</summary>
    public int OwnedCollectionVersion { get; }

    /// <summary>score snapshot version。</summary>
    public int ScoreSnapshotVersion { get; }

    /// <summary>score source generation。</summary>
    public long ScoreSourceGeneration { get; }

    /// <summary>active score source。</summary>
    public ActiveScoreSource ScoreSource { get; }

    /// <summary>score table load status。</summary>
    public ScoreTableLoadStatus ScoreLoadStatus { get; }

    /// <summary>chart_info index version。</summary>
    public int ChartInfoIndexVersion { get; }

    /// <inheritdoc />
    public bool Equals(PlaylistLampDependencyStamp other)
    {
        return EntriesRevision == other.EntriesRevision
            && CatalogVersion == other.CatalogVersion
            && OwnedCollectionVersion == other.OwnedCollectionVersion
            && ScoreSnapshotVersion == other.ScoreSnapshotVersion
            && ScoreSourceGeneration == other.ScoreSourceGeneration
            && ScoreSource == other.ScoreSource
            && ScoreLoadStatus == other.ScoreLoadStatus
            && ChartInfoIndexVersion == other.ChartInfoIndexVersion;
    }

    /// <inheritdoc />
    public override bool Equals(object obj)
    {
        return obj is PlaylistLampDependencyStamp other && Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(
            EntriesRevision,
            CatalogVersion,
            OwnedCollectionVersion,
            ScoreSnapshotVersion,
            ScoreSourceGeneration,
            ScoreSource,
            ScoreLoadStatus,
            ChartInfoIndexVersion);
    }

    /// <summary>stamp の等価演算子です。</summary>
    public static bool operator ==(PlaylistLampDependencyStamp left, PlaylistLampDependencyStamp right) => left.Equals(right);

    /// <summary>stamp の非等価演算子です。</summary>
    public static bool operator !=(PlaylistLampDependencyStamp left, PlaylistLampDependencyStamp right) => !left.Equals(right);
}

/// <summary>
/// pure aggregation service へ渡す immutable input です。
/// </summary>
internal sealed class PlaylistLampAggregationRequest
{
    /// <summary>
    /// aggregation request を生成します。
    /// </summary>
    public PlaylistLampAggregationRequest(
        string playlistId,
        IEnumerable<string> folderOrder,
        IEnumerable<PlaylistLampEntrySnapshot> entries,
        PlaylistLampScoreSnapshot scoreSnapshot,
        DateTime? playlistLastUpdatedUtc = null,
        PlaylistLampInputState inputState = PlaylistLampInputState.Loaded,
        string failureMessage = null,
        PlaylistLampDependencyStamp dependencyStamp = default)
    {
        PlaylistId = playlistId ?? string.Empty;
        FolderOrder = new ReadOnlyCollection<string>(NormalizeFolderOrder(folderOrder));
        Entries = new ReadOnlyCollection<PlaylistLampEntrySnapshot>((entries ?? []).Where(entry => entry != null).ToList());
        ScoreSnapshot = scoreSnapshot ?? new PlaylistLampScoreSnapshot(
            ActiveScoreSource.None,
            ScoreTableLoadStatus.NotConfigured,
            0,
            0L,
            null);
        PlaylistLastUpdatedUtc = playlistLastUpdatedUtc;
        InputState = inputState;
        FailureMessage = failureMessage ?? string.Empty;
        DependencyStamp = dependencyStamp;
    }

    /// <summary>安定した playlist identity です。</summary>
    public string PlaylistId { get; }

    /// <summary>BMSTable.folder_list と同じ通常 folder 順です。</summary>
    public IReadOnlyList<string> FolderOrder { get; }

    /// <summary>immutable playlist entries です。</summary>
    public IReadOnlyList<PlaylistLampEntrySnapshot> Entries { get; }

    /// <summary>score source と score index の snapshot です。</summary>
    public PlaylistLampScoreSnapshot ScoreSnapshot { get; }

    /// <summary>playlist の last_update 値です。</summary>
    public DateTime? PlaylistLastUpdatedUtc { get; }

    /// <summary>entry load の状態です。</summary>
    public PlaylistLampInputState InputState { get; }

    /// <summary>entry load failure の診断メッセージです。</summary>
    public string FailureMessage { get; }

    /// <summary>build が観測した依存 version です。</summary>
    public PlaylistLampDependencyStamp DependencyStamp { get; }

    /// <summary>entry 読み込み中の request を生成します。</summary>
    /// <param name="playlistId">安定した playlist identity。</param>
    /// <param name="dependencyStamp">観測した依存 version。</param>
    /// <returns>Loading 状態の request。</returns>
    internal static PlaylistLampAggregationRequest Loading(string playlistId, PlaylistLampDependencyStamp dependencyStamp = default)
    {
        return new PlaylistLampAggregationRequest(
            playlistId,
            [],
            [],
            null,
            inputState: PlaylistLampInputState.Loading,
            dependencyStamp: dependencyStamp);
    }

    /// <summary>playlist が削除済みの request を生成します。</summary>
    /// <param name="playlistId">安定した playlist identity。</param>
    /// <param name="dependencyStamp">観測した依存 version。</param>
    /// <returns>Deleted 状態の request。</returns>
    internal static PlaylistLampAggregationRequest Deleted(string playlistId, PlaylistLampDependencyStamp dependencyStamp = default)
    {
        return new PlaylistLampAggregationRequest(
            playlistId,
            [],
            [],
            null,
            inputState: PlaylistLampInputState.Deleted,
            dependencyStamp: dependencyStamp);
    }

    /// <summary>entry snapshot の取得に失敗した request を生成します。</summary>
    /// <param name="playlistId">安定した playlist identity。</param>
    /// <param name="failureMessage">失敗理由。</param>
    /// <param name="dependencyStamp">観測した依存 version。</param>
    /// <returns>Failed 状態の request。</returns>
    internal static PlaylistLampAggregationRequest Failed(string playlistId, string failureMessage, PlaylistLampDependencyStamp dependencyStamp = default)
    {
        return new PlaylistLampAggregationRequest(
            playlistId,
            [],
            [],
            null,
            inputState: PlaylistLampInputState.Failed,
            failureMessage: failureMessage,
            dependencyStamp: dependencyStamp);
    }

    private static List<string> NormalizeFolderOrder(IEnumerable<string> folderOrder)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string folder in folderOrder ?? [])
        {
            string value = folder ?? string.Empty;
            if (string.Equals(value, "[NO SONG]", StringComparison.Ordinal))
            {
                continue;
            }
            if (seen.Add(value))
            {
                result.Add(value);
            }
        }
        return result;
    }
}

/// <summary>
/// graph 上の一つのカテゴリ segment です。
/// </summary>
internal sealed class PlaylistLampSegment
{
    private PlaylistLampSegment(
        PlaylistLampSegmentKind kind,
        PlaylistLampClearCategory? clearCategory,
        PlaylistLampRankCategory? rankCategory,
        int count,
        int denominator,
        bool scoreDataAvailable,
        bool invocationEnabled,
        string folderName)
    {
        Kind = kind;
        ClearCategory = clearCategory;
        RankCategory = rankCategory;
        Count = Math.Max(0, count);
        Denominator = Math.Max(0, denominator);
        ScoreDataAvailable = scoreDataAvailable;
        IsInvokable = Count > 0 && scoreDataAvailable && invocationEnabled && folderName != null;
        FolderName = folderName;
    }

    /// <summary>clear segment を生成します。</summary>
    public static PlaylistLampSegment CreateClear(
        PlaylistLampClearCategory category,
        int count,
        int denominator,
        bool scoreDataAvailable,
        bool invocationEnabled,
        string folderName = null)
    {
        return new PlaylistLampSegment(
            PlaylistLampSegmentKind.Clear,
            category,
            null,
            count,
            denominator,
            scoreDataAvailable,
            invocationEnabled,
            folderName);
    }

    /// <summary>rank segment を生成します。</summary>
    public static PlaylistLampSegment CreateRank(
        PlaylistLampRankCategory category,
        int count,
        int denominator,
        bool scoreDataAvailable,
        bool invocationEnabled,
        string folderName = null)
    {
        return new PlaylistLampSegment(
            PlaylistLampSegmentKind.Rank,
            null,
            category,
            count,
            denominator,
            scoreDataAvailable,
            invocationEnabled,
            folderName);
    }

    /// <summary>segment 種別です。</summary>
    public PlaylistLampSegmentKind Kind { get; }

    /// <summary>clear semantic category。rank segment では null です。</summary>
    public PlaylistLampClearCategory? ClearCategory { get; }

    /// <summary>rank semantic category。clear segment では null です。</summary>
    public PlaylistLampRankCategory? RankCategory { get; }

    /// <summary>この category の chart 数です。</summary>
    public int Count { get; }

    /// <summary>percentage の分母です。</summary>
    public int Denominator { get; }

    /// <summary>folder denominator に対する割合（0..100）です。</summary>
    public double? Percentage => ScoreDataAvailable && Denominator > 0 ? Count * 100.0 / Denominator : null;

    /// <summary>0 件 category を positive width にしないための表示幅です。</summary>
    public double? PositiveWidthPercentage => ScoreDataAvailable && Count > 0 && Denominator > 0 ? Percentage : null;

    /// <summary>score source が利用可能かどうかです。</summary>
    public bool ScoreDataAvailable { get; }

    /// <summary>segment invocation が可能かどうかです。</summary>
    public bool IsInvokable { get; }

    /// <summary>folder row の segment ならその folder identity、global graph なら null です。</summary>
    public string FolderName { get; }

    /// <summary>
    /// segment の semantic category を保持した typed invocation request を生成します。
    /// </summary>
    /// <param name="playlistId">安定した playlist identity。</param>
    /// <returns>invocation request。invocation 不可なら null。</returns>
    public PlaylistLampSegmentInvocationRequest CreateInvocationRequest(string playlistId)
    {
        if (!IsInvokable || string.IsNullOrWhiteSpace(playlistId) || FolderName == null)
        {
            return null;
        }
        return Kind == PlaylistLampSegmentKind.Clear
            ? PlaylistLampSegmentInvocationRequest.ForClear(playlistId, FolderName, ClearCategory.Value)
            : PlaylistLampSegmentInvocationRequest.ForRank(playlistId, FolderName, RankCategory.Value);
    }
}

/// <summary>
/// ランプ graph segment を検索 intent へ変換する typed request です。
/// </summary>
internal sealed class PlaylistLampSegmentInvocationRequest
{
    private PlaylistLampSegmentInvocationRequest(
        string playlistId,
        string folderName,
        PlaylistLampSegmentKind kind,
        PlaylistLampClearCategory? clearCategory,
        PlaylistLampRankCategory? rankCategory)
    {
        PlaylistId = playlistId ?? string.Empty;
        FolderName = folderName ?? string.Empty;
        Kind = kind;
        ClearCategory = clearCategory;
        RankCategory = rankCategory;
    }

    /// <summary>clear graph 用 request を生成します。</summary>
    public static PlaylistLampSegmentInvocationRequest ForClear(string playlistId, string folderName, PlaylistLampClearCategory category)
    {
        return new PlaylistLampSegmentInvocationRequest(playlistId, folderName, PlaylistLampSegmentKind.Clear, category, null);
    }

    /// <summary>rank graph 用 request を生成します。</summary>
    public static PlaylistLampSegmentInvocationRequest ForRank(string playlistId, string folderName, PlaylistLampRankCategory category)
    {
        return new PlaylistLampSegmentInvocationRequest(playlistId, folderName, PlaylistLampSegmentKind.Rank, null, category);
    }

    /// <summary>安定した playlist identity。</summary>
    public string PlaylistId { get; }

    /// <summary>通常 folder の論理 identity。</summary>
    public string FolderName { get; }

    /// <summary>graph 種別。</summary>
    public PlaylistLampSegmentKind Kind { get; }

    /// <summary>clear semantic category。</summary>
    public PlaylistLampClearCategory? ClearCategory { get; }

    /// <summary>rank semantic category。</summary>
    public PlaylistLampRankCategory? RankCategory { get; }
}

/// <summary>
/// folder 単位のランプ集計結果です。
/// </summary>
internal sealed class PlaylistLampFolderRow
{
    /// <summary>
    /// folder 単位の集計 row を生成します。
    /// </summary>
    /// <param name="folderName">通常 folder の identity。</param>
    /// <param name="count">folder denominator。</param>
    /// <param name="ownedCount">所持 chart 数。</param>
    /// <param name="missingCount">未所持 chart 数。</param>
    /// <param name="clearSegments">clear category segments。</param>
    /// <param name="rankSegments">DJ rank category segments。</param>
    internal PlaylistLampFolderRow(
        string folderName,
        int count,
        int ownedCount,
        int missingCount,
        IReadOnlyList<PlaylistLampSegment> clearSegments,
        IReadOnlyList<PlaylistLampSegment> rankSegments)
    {
        FolderName = folderName ?? string.Empty;
        Count = Math.Max(0, count);
        OwnedCount = Math.Max(0, ownedCount);
        MissingCount = Math.Max(0, missingCount);
        ClearSegments = new ReadOnlyCollection<PlaylistLampSegment>(
            (clearSegments ?? []).Where(segment => segment != null).ToList());
        RankSegments = new ReadOnlyCollection<PlaylistLampSegment>(
            (rankSegments ?? []).Where(segment => segment != null).ToList());
    }

    /// <summary>通常 folder の論理 identity。</summary>
    public string FolderName { get; }

    /// <summary>folder denominator。</summary>
    public int Count { get; }

    /// <summary>所持 chart 数。</summary>
    public int OwnedCount { get; }

    /// <summary>未所持 chart 数。</summary>
    public int MissingCount { get; }

    /// <summary>clear segments。常に仕様順です。</summary>
    public IReadOnlyList<PlaylistLampSegment> ClearSegments { get; }

    /// <summary>DJ rank segments。常に仕様順です。</summary>
    public IReadOnlyList<PlaylistLampSegment> RankSegments { get; }

    /// <summary>folder row の segment invocation が有効かどうかです。</summary>
    public bool HasInvokableSegments => ClearSegments.Any(segment => segment.IsInvokable)
        || RankSegments.Any(segment => segment.IsInvokable);
}

/// <summary>
/// playlist 全体の count と rate 統計です。
/// </summary>
internal sealed class PlaylistLampStatistics
{
    /// <summary>
    /// playlist 統計を生成します。
    /// </summary>
    /// <param name="totalCount">active real entry 数。</param>
    /// <param name="ownedCount">所持 chart 数。</param>
    /// <param name="missingCount">未所持 chart 数。</param>
    /// <param name="playedCount">score がある chart 数。</param>
    /// <param name="unplayedCount">score がない chart 数。</param>
    /// <param name="ownershipRate">owned / total。</param>
    /// <param name="playRate">played / total。</param>
    /// <param name="averageExRate">played chart の EX rate 算術平均。</param>
    /// <param name="clearRate">clear 済み chart / total。</param>
    /// <param name="scoreDataAvailable">score 依存値が利用可能かどうか。</param>
    /// <param name="sourceLastUpdatedUtc">score source の更新時刻。</param>
    /// <param name="playlistLastUpdatedUtc">playlist の更新時刻。</param>
    internal PlaylistLampStatistics(
        int totalCount,
        int ownedCount,
        int missingCount,
        int? playedCount,
        int? unplayedCount,
        double? ownershipRate,
        double? playRate,
        double? averageExRate,
        double? clearRate,
        bool scoreDataAvailable,
        DateTime? sourceLastUpdatedUtc,
        DateTime? playlistLastUpdatedUtc)
    {
        TotalCount = Math.Max(0, totalCount);
        OwnedCount = Math.Max(0, ownedCount);
        MissingCount = Math.Max(0, missingCount);
        PlayedCount = playedCount;
        UnplayedCount = unplayedCount;
        OwnershipRate = ownershipRate;
        PlayRate = playRate;
        AverageExRate = averageExRate;
        ClearRate = clearRate;
        ScoreDataAvailable = scoreDataAvailable;
        SourceLastUpdatedUtc = sourceLastUpdatedUtc;
        PlaylistLastUpdatedUtc = playlistLastUpdatedUtc;
    }

    /// <summary>active real entry 数。</summary>
    public int TotalCount { get; }

    /// <summary>所持 chart 数。</summary>
    public int OwnedCount { get; }

    /// <summary>未所持 chart 数。</summary>
    public int MissingCount { get; }

    /// <summary>score が存在する chart 数。score unavailable 時は null。</summary>
    public int? PlayedCount { get; }

    /// <summary>score がない chart 数。score unavailable 時は null。</summary>
    public int? UnplayedCount { get; }

    /// <summary>owned / total の割合。</summary>
    public double? OwnershipRate { get; }

    /// <summary>played / total の割合。</summary>
    public double? PlayRate { get; }

    /// <summary>played chart の EX rate 算術平均（0..1）。</summary>
    public double? AverageExRate { get; }

    /// <summary>clear 済み chart / total の割合。</summary>
    public double? ClearRate { get; }

    /// <summary>score 依存統計が利用可能かどうかです。</summary>
    public bool ScoreDataAvailable { get; }

    /// <summary>score source の last update。</summary>
    public DateTime? SourceLastUpdatedUtc { get; }

    /// <summary>playlist の last update。</summary>
    public DateTime? PlaylistLastUpdatedUtc { get; }

    /// <summary>rate を percentage 表示するための ownership alias。</summary>
    public double? OwnershipPercentage => OwnershipRate.HasValue ? OwnershipRate.Value * 100.0 : null;

    /// <summary>rate を percentage 表示するための play alias。</summary>
    public double? PlayPercentage => PlayRate.HasValue ? PlayRate.Value * 100.0 : null;

    /// <summary>rate を percentage 表示するための clear alias。</summary>
    public double? ClearPercentage => ClearRate.HasValue ? ClearRate.Value * 100.0 : null;

    /// <summary>EX rate を percentage 表示するための alias。</summary>
    public double? AverageExRatePercentage => AverageExRate.HasValue ? AverageExRate.Value * 100.0 : null;
}

/// <summary>
/// immutable なランプビューア集計結果です。
/// </summary>
internal sealed class PlaylistLampAggregationResult
{
    /// <summary>
    /// immutable な aggregation result を生成します。
    /// </summary>
    /// <param name="playlistId">安定した playlist identity。</param>
    /// <param name="state">ビューア状態。</param>
    /// <param name="folderRows">folder row 一覧。</param>
    /// <param name="clearSegments">playlist 全体 clear segments。</param>
    /// <param name="rankSegments">playlist 全体 rank segments。</param>
    /// <param name="statistics">playlist 統計。</param>
    /// <param name="scoreSnapshot">使用した score snapshot。</param>
    /// <param name="failureMessage">失敗理由。</param>
    internal PlaylistLampAggregationResult(
        string playlistId,
        PlaylistLampViewerState state,
        IReadOnlyList<PlaylistLampFolderRow> folderRows,
        IReadOnlyList<PlaylistLampSegment> clearSegments,
        IReadOnlyList<PlaylistLampSegment> rankSegments,
        PlaylistLampStatistics statistics,
        PlaylistLampScoreSnapshot scoreSnapshot,
        string failureMessage)
    {
        PlaylistId = playlistId ?? string.Empty;
        State = state;
        FolderRows = new ReadOnlyCollection<PlaylistLampFolderRow>(
            (folderRows ?? []).Where(row => row != null).ToList());
        ClearSegments = new ReadOnlyCollection<PlaylistLampSegment>(
            (clearSegments ?? []).Where(segment => segment != null).ToList());
        RankSegments = new ReadOnlyCollection<PlaylistLampSegment>(
            (rankSegments ?? []).Where(segment => segment != null).ToList());
        Statistics = statistics;
        ScoreSnapshot = scoreSnapshot ?? CreateUnavailableScoreSnapshot();
        ScoreDataAvailable = ScoreSnapshot.IsScoreDataAvailable;
        IsSegmentInvocationEnabled = ScoreDataAvailable && state == PlaylistLampViewerState.Ready;
        FailureMessage = failureMessage ?? string.Empty;
    }

    /// <summary>安定した playlist identity。</summary>
    public string PlaylistId { get; }

    /// <summary>ビューア状態。</summary>
    public PlaylistLampViewerState State { get; }

    /// <summary>folder row 一覧。</summary>
    public IReadOnlyList<PlaylistLampFolderRow> FolderRows { get; }

    /// <summary>playlist 全体 clear graph segments。</summary>
    public IReadOnlyList<PlaylistLampSegment> ClearSegments { get; }

    /// <summary>playlist 全体 DJ rank graph segments。</summary>
    public IReadOnlyList<PlaylistLampSegment> RankSegments { get; }

    /// <summary>playlist 全体統計。</summary>
    public PlaylistLampStatistics Statistics { get; }

    /// <summary>集計時に使用した score snapshot。</summary>
    public PlaylistLampScoreSnapshot ScoreSnapshot { get; }

    /// <summary>score 依存データが利用可能かどうかです。</summary>
    public bool ScoreDataAvailable { get; }

    /// <summary>graph segment invocation が有効かどうかです。</summary>
    public bool IsSegmentInvocationEnabled { get; }

    /// <summary>failed state の診断メッセージ。</summary>
    public string FailureMessage { get; }

    /// <summary>UI 層で扱いやすい rows alias。</summary>
    public IReadOnlyList<PlaylistLampFolderRow> Folders => FolderRows;

    /// <summary>UI 層で扱いやすい clear graph alias。</summary>
    public IReadOnlyList<PlaylistLampSegment> ClearGraphSegments => ClearSegments;

    /// <summary>UI 層で扱いやすい rank graph alias。</summary>
    public IReadOnlyList<PlaylistLampSegment> RankGraphSegments => RankSegments;

    private static PlaylistLampScoreSnapshot CreateUnavailableScoreSnapshot()
    {
        return new PlaylistLampScoreSnapshot(
            ActiveScoreSource.None,
            ScoreTableLoadStatus.NotConfigured,
            0,
            0L,
            null);
    }
}
