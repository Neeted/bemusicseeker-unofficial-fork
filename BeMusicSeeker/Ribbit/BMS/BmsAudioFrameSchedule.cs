#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Ribbit.Media.Audio;

namespace Ribbit.BMS;

/// <summary>一つの独立voiceを出力開始・source窓・実出力終端で固定します。BMSの未解決終端は従来規則で算出します。</summary>
/// <param name="WavIndex">共有PCMのindexです。voice排他の識別とは分けます。</param>
/// <param name="AbsoluteTime">表示・診断向け整数tick時刻です。bmsonのframe化には使いません。</param>
/// <param name="StartFrame">絶対曲frameの発音位置です。</param>
/// <param name="NextSameIndexStartFrame">BMSだけの再発音打切り位置です。</param>
/// <param name="StableOrder">同frameでの解析順です。</param>
/// <param name="SourceStartFrame">共有PCM内の絶対開始位置です。</param>
/// <param name="SourceEndFrame">bmsonの有限source終端です。BMSのnullは元EOFです。</param>
/// <param name="EndFrame">bmsonで一度解決した実出力終端です。予約・seek・長さ・Writerで共用します。</param>
internal readonly record struct BmsAudioFrameEvent(
    int WavIndex,
    TimeSpan AbsoluteTime,
    long StartFrame,
    long? NextSameIndexStartFrame,
    long StableOrder,
    long SourceStartFrame = 0,
    long? SourceEndFrame = null,
    long? EndFrame = null);

/// <summary>譜面の発音対象を実効sample rateへ一度だけ量子化した変更不能scheduleです。</summary>
internal sealed class BmsAudioFrameSchedule
{
    private readonly BmsAudioFrameEvent[] events;
    private readonly ReadOnlyCollection<BmsAudioFrameEvent> readOnlyEvents;
    private readonly IReadOnlyDictionary<int, BmsAudioFrameEvent[]> eventsByWavIndex;

    private BmsAudioFrameSchedule(int sampleRate, BmsAudioFrameEvent[] events)
    {
        SampleRate = sampleRate;
        this.events = events;
        readOnlyEvents = Array.AsReadOnly(events);
        eventsByWavIndex = events
            .GroupBy(item => item.WavIndex)
            .ToDictionary(group => group.Key, group => group.ToArray());
    }

    /// <summary>このscheduleを量子化した実効input mixer sample rateです。</summary>
    internal int SampleRate { get; }

    /// <summary>開始frame・元時刻・安定順で正規化した発音イベントを取得します。</summary>
    internal IReadOnlyList<BmsAudioFrameEvent> Events => readOnlyEvents;

    /// <summary>音声イベントが参照するWAV indexを昇順で取得します。</summary>
    internal static int[] GetRequiredAudioIndices(BMSFile bms) =>
        EnumerateAudioNotes(bms)
            .Select(note => note.Index)
            .Distinct()
            .Order()
            .ToArray();

    /// <summary>同じ五群の譜面音符から実効rate専用scheduleを作成します。</summary>
    /// <param name="bms">分岐解決済みの譜面。</param>
    /// <param name="sampleRate">実際のinput mixer sample rate。</param>
    /// <returns>衝突を正規化した不変発音schedule。</returns>
    internal static BmsAudioFrameSchedule Create(BMSFile bms, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(bms);
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        var candidates = new List<BmsAudioFrameEvent>();
        long stableOrder = 0;
        foreach (BMSFile.Chart.Note note in EnumerateAudioNotes(bms))
        {
            TimeSpan absoluteTime = note.AbsoluteTime;
            TimeSpan clampedTime = absoluteTime < TimeSpan.Zero ? TimeSpan.Zero : absoluteTime;
            candidates.Add(new BmsAudioFrameEvent(
                note.Index,
                absoluteTime,
                AudioFrameMath.TimeToFrame(clampedTime, sampleRate),
                null,
                stableOrder++));
        }

        candidates.Sort(static (left, right) =>
        {
            int frameComparison = left.StartFrame.CompareTo(right.StartFrame);
            if (frameComparison != 0)
            {
                return frameComparison;
            }

            int timeComparison = left.AbsoluteTime.CompareTo(right.AbsoluteTime);
            return timeComparison != 0
                ? timeComparison
                : left.StableOrder.CompareTo(right.StableOrder);
        });

        var unique = new List<BmsAudioFrameEvent>(candidates.Count);
        var seen = new HashSet<(int WavIndex, long StartFrame)>();
        foreach (BmsAudioFrameEvent candidate in candidates)
        {
            if (seen.Add((candidate.WavIndex, candidate.StartFrame)))
            {
                unique.Add(candidate);
            }
        }

        var nextStartByIndex = new Dictionary<int, long>();
        var normalized = new BmsAudioFrameEvent[unique.Count];
        for (int index = unique.Count - 1; index >= 0; index--)
        {
            BmsAudioFrameEvent item = unique[index];
            long? nextStart = nextStartByIndex.TryGetValue(item.WavIndex, out long nextFrame)
                ? nextFrame
                : null;
            normalized[index] = item with { NextSameIndexStartFrame = nextStart };
            nextStartByIndex[item.WavIndex] = item.StartFrame;
        }

        return new BmsAudioFrameSchedule(sampleRate, normalized);
    }

    /// <summary>bmsonの継続列を量子化前に連結し、独立voiceのsource窓と実EOFを一回だけ解決します。実出力のないvoiceは除きます。</summary>
    internal static BmsAudioFrameSchedule Create(PlaybackChart chart, int sampleRate, IReadOnlyList<BmsAudioResource?> resources)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (chart.Bms is BMSFile bms) return Create(bms, sampleRate);
        var resolved = new List<BmsAudioFrameEvent>(chart.AudioEvents.Count);
        foreach (PlaybackAudioEvent item in CoalesceContinuationRuns(chart.AudioEvents))
        {
            long start = item.Start.ToOutputFrame(sampleRate);
            BmsAudioResource? resource = resources[item.ResourceIndex];
            long sourceStart = 0, sourceEnd = 0, end = start;
            if (resource != null)
            {
                sourceStart = System.Math.Min(item.SourceStart.ToSourceFrame(resource.Audio.SampleRate), resource.Audio.FrameCount);
                sourceEnd = item.SourceEnd is PlaybackTime finiteSource
                    ? System.Math.Min(finiteSource.ToSourceFrame(resource.Audio.SampleRate), resource.Audio.FrameCount)
                    : resource.Audio.FrameCount;
                long naturalEnd = checked(start + AudioFrameMath.CeilingOutputFrameCount(
                    System.Math.Max(0, sourceEnd - sourceStart), resource.Audio.SampleRate, sampleRate));
                end = item.End is PlaybackTime finiteEnd ? System.Math.Min(naturalEnd, finiteEnd.ToOutputFrame(sampleRate)) : naturalEnd;
                end = System.Math.Max(start, end);
            }
            // 欠落・0frame・空source窓は音声終端を延ばしません。論理イベントはChartに保持します。
            if (end <= start)
            {
                continue;
            }
            resolved.Add(new BmsAudioFrameEvent(item.ResourceIndex, item.Start.ToTimeSpan(), start, null,
                item.StableOrder, sourceStart, sourceEnd, end));
        }
        return new BmsAudioFrameSchedule(sampleRate, resolved.OrderBy(item => item.StartFrame)
            .ThenBy(item => item.StableOrder).ToArray());
    }

    /// <summary>同じ元channelと再開原点を持つ連続sliceを、空source判定やframe丸めより前にまとめます。</summary>
    private static IReadOnlyList<PlaybackAudioEvent> CoalesceContinuationRuns(IReadOnlyList<PlaybackAudioEvent> slices)
    {
        var runs = new List<PlaybackAudioEvent>(slices.Count);
        var lastRunByChannel = new Dictionary<int, int>();
        foreach (PlaybackAudioEvent slice in slices)
        {
            if (lastRunByChannel.TryGetValue(slice.ResourceIndex, out int runIndex))
            {
                PlaybackAudioEvent run = runs[runIndex];
                if (run.End is PlaybackTime end && end == slice.Start
                    && run.SourceEnd is PlaybackTime sourceEnd && sourceEnd == slice.SourceStart
                    && run.Start - run.SourceStart == slice.Start - slice.SourceStart)
                {
                    runs[runIndex] = run with { End = slice.End, SourceEnd = slice.SourceEnd };
                    continue;
                }
            }
            lastRunByChannel[slice.ResourceIndex] = runs.Count;
            runs.Add(slice);
        }
        return runs;
    }

    /// <summary>指定indexの量子化済みイベントをframe昇順で取得します。</summary>
    internal IReadOnlyList<BmsAudioFrameEvent> GetEventsForWavIndex(int wavIndex) =>
        eventsByWavIndex.TryGetValue(wavIndex, out BmsAudioFrameEvent[]? values)
            ? Array.AsReadOnly(values)
            : Array.Empty<BmsAudioFrameEvent>();

    /// <summary>指定frame以上の最初のイベント位置を二分探索で取得します。</summary>
    internal int LowerBoundStartFrame(long frame)
    {
        int low = 0;
        int high = events.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (events[middle].StartFrame < frame)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>発音対象五群をBMSPlayerの進行順・lane順で一度だけ列挙します。</summary>
    internal static IEnumerable<BMSFile.Chart.Note> EnumerateAudioNotes(BMSFile bms)
    {
        ArgumentNullException.ThrowIfNull(bms);
        foreach (BMSFile.Chart.Note note in bms.Measures.BgmNotes)
        {
            yield return note;
        }
        foreach (BMSFile.Measure.AllNotes lane in bms.Measures.LongNotes1P)
        {
            foreach (BMSFile.Chart.Note note in lane)
            {
                if (((uint)note.Type & 0xFFFFFFF0u) == 80u)
                {
                    yield return note;
                }
            }
        }
        foreach (BMSFile.Measure.AllNotes lane in bms.Measures.LongNotes2P)
        {
            foreach (BMSFile.Chart.Note note in lane)
            {
                if (((uint)note.Type & 0xFFFFFFF0u) == 96u)
                {
                    yield return note;
                }
            }
        }
        foreach (BMSFile.Measure.AllNotes lane in bms.Measures.VisibleNotes1P)
        {
            foreach (BMSFile.Chart.Note note in lane)
            {
                yield return note;
            }
        }
        foreach (BMSFile.Measure.AllNotes lane in bms.Measures.VisibleNotes2P)
        {
            foreach (BMSFile.Chart.Note note in lane)
            {
                yield return note;
            }
        }
    }
}
