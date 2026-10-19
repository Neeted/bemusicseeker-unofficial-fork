using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace BeMusicSeeker.Models;

/// <summary>score正本で確定した変更範囲と実キーです。全score値の不変snapshotではなく、表示の反映済み位置を進める結果です。</summary>
internal sealed record ScoreSnapshotChange
{
    /// <summary>確定前後の既存版と対象キーを固定します。空キーでも確定版の位置を保持します。</summary>
    internal ScoreSnapshotChange(int baseVersion, int version, IReadOnlyList<string> md5Keys, IReadOnlyList<string> sha256Keys)
    {
        BaseVersion = baseVersion;
        Version = version;
        Md5Keys = Array.AsReadOnly((md5Keys ?? []).ToArray());
        Sha256Keys = Array.AsReadOnly((sha256Keys ?? []).ToArray());
    }
    /// <summary>この変更の直前まで確定したscore版です。</summary>
    internal int BaseVersion { get; }
    /// <summary>このキー集合を反映して覆えるscore版です。</summary>
    internal int Version { get; }
    /// <summary>変更・削除・全lookup置換で影響するMD5です。</summary>
    internal IReadOnlyList<string> Md5Keys { get; }
    /// <summary>変更・削除・全lookup置換で影響するSHA-256です。</summary>
    internal IReadOnlyList<string> Sha256Keys { get; }
}

public partial class BMSLibrary
{
    private readonly object scoreSubscriptionGate = new();
    private readonly Dictionary<BMSScore, ScoreSubscription> scoreSubscriptions = [];

    private sealed class ScoreSubscription(PropertyChangedEventHandler handler, string[] md5Keys, string[] sha256Keys)
    {
        internal PropertyChangedEventHandler Handler { get; } = handler;
        internal string[] Md5Keys { get; } = md5Keys;
        internal string[] Sha256Keys { get; } = sha256Keys;
    }

    /// <summary>scoreの実lookup・列挙・複写地点を観測します。callbackはscoreへ再入せず、状態を保持しません。</summary>
    internal Action<string> ScoreSnapshotWorkObserver { get; set; }

    /// <summary>ライブラリが現在所有するスコアの購読数です。</summary>
    internal int ScoreSubscriptionCount
    {
        get { lock (scoreSubscriptionGate) { return scoreSubscriptions.Count; } }
    }

    private void ReplaceScoreSubscriptionsUnsafe(ScoreSnapshot snapshot)
    {
        DetachScoreSubscriptionsUnsafe();
        if (IsShutdownRequested || snapshot == null)
        {
            return;
        }
        var md5Keys = new Dictionary<BMSScore, List<string>>();
        var sha256Keys = new Dictionary<BMSScore, List<string>>();
        foreach ((string key, BMSScore score) in snapshot.ScoresByHash)
        {
            ScoreSnapshotWorkObserver?.Invoke("score_subscription_key_enumerated");
            if (!md5Keys.TryGetValue(score, out List<string> keys))
            {
                md5Keys[score] = keys = [];
            }
            keys.Add(key);
        }
        foreach ((string key, BMSScore score) in snapshot.ScoresBySha256)
        {
            ScoreSnapshotWorkObserver?.Invoke("score_subscription_key_enumerated");
            if (!sha256Keys.TryGetValue(score, out List<string> keys))
            {
                sha256Keys[score] = keys = [];
            }
            keys.Add(key);
        }
        foreach (BMSScore score in md5Keys.Keys.Concat(sha256Keys.Keys).Distinct())
        {
            PropertyChangedEventHandler handler = (_, _) => PublishOwnedScoreChange(score);
            var subscription = new ScoreSubscription(handler,
                md5Keys.TryGetValue(score, out List<string> md5) ? [.. md5] : [],
                sha256Keys.TryGetValue(score, out List<string> sha256) ? [.. sha256] : []);
            scoreSubscriptions.Add(score, subscription);
            score.PropertyChanged += handler;
        }
    }

    /// <summary>現在実体の変更版を先に採用し、版と変更キーの純通知を互いの通常失敗から分離します。</summary>
    private void PublishOwnedScoreChange(BMSScore score)
    {
        lock (scoreSubscriptionGate)
        {
            ScoreSnapshotWorkObserver?.Invoke("score_subscription_lookup");
            if (IsShutdownRequested || !scoreSubscriptions.TryGetValue(score, out ScoreSubscription subscription))
            {
                return;
            }
            int version;
            lock (lockScoreSnapshot)
            {
                ScoreSnapshot current = scoreSnapshot;
                if (current == null)
                {
                    return;
                }
                int baseVersion = current.Version;
                version = ++scoreSnapshotVersion;
                scoreSnapshot = new ScoreSnapshot
                {
                    Version = version,
                    LoadedAtUtc = current.LoadedAtUtc,
                    BuildElapsedMs = current.BuildElapsedMs,
                    Scores = current.Scores,
                    ScoresByHash = current.ScoresByHash,
                    ScoresBySha256 = current.ScoresBySha256,
                    ActiveScoreSource = current.ActiveScoreSource,
                    ScoreDbPath = current.ScoreDbPath,
                    LoadStatus = current.LoadStatus,
                    LoadFailureMessage = current.LoadFailureMessage,
                    SourceGeneration = current.SourceGeneration
                };
                normalLibraryRefreshPublisher.Register(new ScoreSnapshotChange(baseVersion, version, subscription.Md5Keys, subscription.Sha256Keys));
            }
        }
        TryInvokePostLeaseNotification(() => RaisePropertyChanged(nameof(ScoreSnapshotVersion)), "score_snapshot_version_notification_failed");
        TryInvokePostLeaseNotification(() => RaisePropertyChanged(nameof(NormalLibraryRefreshNotificationVersion)), "score_snapshot_change_notification_failed");
    }

    private void DetachScoreSubscriptions()
    {
        lock (scoreSubscriptionGate) { DetachScoreSubscriptionsUnsafe(); }
    }

    private void DetachScoreSubscriptionsUnsafe()
    {
        foreach ((BMSScore score, ScoreSubscription subscription) in scoreSubscriptions)
        {
            score.PropertyChanged -= subscription.Handler;
        }
        scoreSubscriptions.Clear();
    }
}
