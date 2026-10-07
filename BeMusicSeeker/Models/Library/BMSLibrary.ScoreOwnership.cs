using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace BeMusicSeeker.Models;

/// <summary>スコアの変更版と、変更した既存索引のキーを通知します。</summary>
internal sealed record ScoreSnapshotChange(int Version, IReadOnlyList<string> Md5Keys, IReadOnlyList<string> Sha256Keys);

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

    /// <summary>スコア正本の変更を、保存行や譜面ごとの購読を経由せず通知します。</summary>
    internal event Action<ScoreSnapshotChange> ScoreSnapshotChanged;

    /// <summary>ライブラリが現在所有するスコアの購読数です。</summary>
    internal int ScoreSubscriptionCount
    {
        get { lock (scoreSubscriptionGate) { return scoreSubscriptions.Count; } }
    }

    private void ReplaceScoreSubscriptions(ScoreSnapshot snapshot)
    {
        lock (scoreSubscriptionGate)
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
                if (!md5Keys.TryGetValue(score, out List<string> keys))
                {
                    md5Keys[score] = keys = [];
                }
                keys.Add(key);
            }
            foreach ((string key, BMSScore score) in snapshot.ScoresBySha256)
            {
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
    }

    private void PublishOwnedScoreChange(BMSScore score)
    {
        ScoreSnapshotChange change;
        lock (scoreSubscriptionGate)
        {
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
                    LoadStatus = current.LoadStatus,
                    LoadFailureMessage = current.LoadFailureMessage,
                    SourceGeneration = current.SourceGeneration
                };
            }
            change = new ScoreSnapshotChange(version, subscription.Md5Keys, subscription.Sha256Keys);
        }
        ScoreSnapshotVersion = change.Version;
        ScoreSnapshotChanged?.Invoke(change);
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
