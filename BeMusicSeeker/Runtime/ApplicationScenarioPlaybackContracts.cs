#nullable enable
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Runtime;

/// <summary>通常の表activation・開始・次・停止だけを指定します。</summary>
internal enum ScenarioPlaybackAction { Activate, Start, Next, Stop }

/// <summary>一回の通常入力と、その実Task・開始receiptの観測を同じ呼出しで行います。</summary>
internal sealed record ScenarioPlaybackCommand(long RequestId, ScenarioPlaybackAction Action, string ChartPath,
    bool WaitForCompletion = false) : ScenarioCommand(RequestId);

/// <summary>同じ入力が開始した実操作、PCM、停止解放の事実です。</summary>
internal sealed record ScenarioPlaybackReceipt(long Generation, string ChartPath, string PlaybackPath,
    bool ReadySucceeded, string CompletionStatus, bool StopCompleted, bool NativeReleased, PlaybackPcmSnapshot? Pcm);

/// <summary>当該入力と必要な実終端を待ち終えた結果です。</summary>
internal sealed record ScenarioPlaybackCompleted(long RequestId, ScenarioPlaybackReceipt Playback) : ScenarioMessage(RequestId);
