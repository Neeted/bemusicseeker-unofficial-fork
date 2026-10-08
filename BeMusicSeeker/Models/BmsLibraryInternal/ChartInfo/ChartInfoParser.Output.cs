using System;
using System.Globalization;
using System.Text;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static partial class ChartInfoParser
{
    // LNの確定・未閉鎖始点の除去後だけ作る、解析一回の共有要約です。
    private sealed class ChartSummary
    {
        public int TotalNotes;
        public int NormalKeyNotes;
        public int LongKeyNotes;
        public int NormalScratchNotes;
        public int LongScratchNotes;
        public int[] TimelineNoteCounts;
        public double MinBpm;
        public double MaxBpm;
        public int Features;
        public bool HasBga;
        public long LastTimeMilliseconds;
        public bool HasTimeWrap;
        public long WrappedRawTime;
        public double WrappedSection;

        public static ChartSummary Create(ChartModel model, ParseTimeoutGuard timeoutGuard)
        {
            var summary = new ChartSummary
            {
                TimelineNoteCounts = new int[model.Timelines.Count],
                MinBpm = model.InitialBpm,
                MaxBpm = model.InitialBpm,
                Features = model.HasRandom ? FeatureRandom : 0
            };
            for (int index = 0; index < model.Timelines.Count; index++)
            {
                timeoutGuard.ThrowIfTimedOutEvery(index + 1, "model_summary");
                ChartTimeline timeline = model.Timelines[index];
                if (timeline.Bpm < summary.MinBpm)
                {
                    summary.MinBpm = timeline.Bpm;
                }
                if (timeline.Bpm > summary.MaxBpm)
                {
                    summary.MaxBpm = timeline.Bpm;
                }
                summary.HasBga |= timeline.HasBga;
                bool hasEvent = timeline.HasBackground || timeline.HasHiddenNote || timeline.HasBga;
                if (!summary.HasTimeWrap && timeline.TimeMillisecondsLong != ToJavaInt(timeline.TimeMillisecondsLong))
                {
                    summary.HasTimeWrap = true;
                    summary.WrappedRawTime = timeline.TimeMillisecondsLong;
                    summary.WrappedSection = timeline.Section;
                }
                if (timeline.StopMilliseconds > 0)
                {
                    summary.Features |= FeatureStopSequence;
                }
                if (Math.Abs(timeline.Scroll - 1.0) > double.Epsilon)
                {
                    summary.Features |= FeatureScroll;
                }
                int count = 0;
                for (int lane = 0; lane < model.Mode.KeyCount; lane++)
                {
                    ChartNote note = timeline.Notes[lane];
                    if (note == null)
                    {
                        continue;
                    }
                    hasEvent = true;
                    bool scratch = model.Mode.IsScratchKey(lane);
                    if (note.Kind == ChartNoteKind.Normal)
                    {
                        count++;
                        if (scratch)
                        {
                            summary.NormalScratchNotes++;
                        }
                        else
                        {
                            summary.NormalKeyNotes++;
                        }
                    }
                    else if (note.Kind == ChartNoteKind.Mine)
                    {
                        summary.Features |= FeatureMineNote;
                    }
                    else if (note.Kind == ChartNoteKind.Long)
                    {
                        summary.Features |= note.LongType switch
                        {
                            LongNoteTypeUndefined => FeatureUndefinedLongNote,
                            LongNoteTypeLongNote => FeatureLongNote,
                            LongNoteTypeChargeNote => FeatureChargeNote,
                            LongNoteTypeHellChargeNote => FeatureHellChargeNote,
                            _ => 0
                        };
                        if (ShouldCountLongNote(note, LntypeLongNote))
                        {
                            count++;
                            if (scratch)
                            {
                                summary.LongScratchNotes++;
                            }
                            else
                            {
                                summary.LongKeyNotes++;
                            }
                        }
                    }
                }
                if (hasEvent)
                {
                    summary.LastTimeMilliseconds = timeline.TimeMillisecondsLong;
                }
                summary.TimelineNoteCounts[index] = count;
                summary.TotalNotes = checked(summary.TotalNotes + count);
            }
            return summary;
        }
    }

    private sealed partial class ChartModel
    {
        public ChartUtf8Buffer WriteChartUtf8(ParseTimeoutGuard timeoutGuard)
        {
            var output = new ChartUtf8Buffer();
            output.Append("JUDGERANK:");
            output.AppendInteger(JudgeRank);
            output.Append('\n');
            output.Append("TOTAL:");
            output.Append(FormatDouble(Total));
            output.Append('\n');
            if (LnMode != 0)
            {
                output.Append("LNMODE:");
                output.AppendInteger(LnMode);
                output.Append('\n');
            }
            double? currentBpm = null;
            int timelineIndex = 0;
            foreach (ChartTimeline timeline in Timelines)
            {
                timeoutGuard.ThrowIfTimedOutEvery(++timelineIndex, "chart_string");
                int lineStart = output.Count;
                bool shouldWrite = false;
                output.AppendInteger(timeline.TimeMilliseconds);
                output.Append(':');
                if (!currentBpm.HasValue || Math.Abs(currentBpm.Value - timeline.Bpm) > double.Epsilon)
                {
                    currentBpm = timeline.Bpm;
                    output.Append("B(");
                    output.Append(timeline.GetBpmChartText());
                    output.Append(')');
                    shouldWrite = true;
                }
                if (timeline.StopMilliseconds != 0)
                {
                    output.Append("S(");
                    output.AppendInteger(timeline.StopMilliseconds);
                    output.Append(')');
                    shouldWrite = true;
                }
                if (timeline.HasSectionLine)
                {
                    output.Append('L');
                    shouldWrite = true;
                }
                output.Append('[');
                for (int lane = 0; lane < Mode.KeyCount; lane++)
                {
                    ChartNote note = timeline.Notes[lane];
                    if (note == null)
                    {
                        output.Append('0');
                    }
                    else if (note.Kind == ChartNoteKind.Normal)
                    {
                        output.Append('1');
                        shouldWrite = true;
                    }
                    else if (note.Kind == ChartNoteKind.Long)
                    {
                        if (!note.IsEnd)
                        {
                            char marker = "lLCH"[Math.Max(0, Math.Min(3, note.LongType))];
                            output.AppendInteger((long)marker + note.AudioDurationMilliseconds);
                            shouldWrite = true;
                        }
                    }
                    else if (note.Kind == ChartNoteKind.Mine)
                    {
                        output.Append('m');
                        output.Append(FormatDouble(note.Damage));
                        shouldWrite = true;
                    }
                    else
                    {
                        output.Append('0');
                    }
                    if (lane < Mode.KeyCount - 1)
                    {
                        output.Append(',');
                    }
                }
                output.Append("]\n");
                if (!shouldWrite)
                {
                    output.Count = lineStart;
                }
            }
            return output;
        }
    }

    // 整数は従来のStringBuilder.Appendと同じカルチャ、doubleはJava書式の結果を使います。
    // 完成した連続領域を一括hashに渡し、検証要求時だけ同じ領域を文字列にします。
    private sealed class ChartUtf8Buffer
    {
        private byte[] buffer = new byte[4096];

        public int Count { get; set; }

        public ReadOnlySpan<byte> WrittenSpan => buffer.AsSpan(0, Count);

        public void Append(ReadOnlySpan<char> text)
        {
            EnsureCapacity(Encoding.UTF8.GetMaxByteCount(text.Length));
            Count += Encoding.UTF8.GetBytes(text, buffer.AsSpan(Count));
        }

        public void Append(char ascii)
        {
            EnsureCapacity(1);
            buffer[Count++] = (byte)ascii;
        }

        public void AppendInteger(long value)
        {
            Span<char> text = stackalloc char[32];
            if (value.TryFormat(text, out int written, provider: CultureInfo.CurrentCulture))
            {
                Append(text[..written]);
            }
            else
            {
                Append(value.ToString(CultureInfo.CurrentCulture));
            }
        }

        private void EnsureCapacity(int addition)
        {
            int required = checked(Count + addition);
            if (required > buffer.Length)
            {
                Array.Resize(ref buffer, Math.Max(required, checked(buffer.Length * 2)));
            }
        }
    }
}
