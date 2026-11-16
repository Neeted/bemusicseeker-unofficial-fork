using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static partial class ChartInfoParser
{
    private readonly record struct BmsDataElement(int Index, int Value);

    /// <summary>候補の最終BASEで一度だけ変換し、必要な非ゼロ要素と診断位置を保持します。</summary>
    private sealed class BmsChannelLine(int section, int channel, BmsTextRange data)
    {
        private int cursor;

        public int Section { get; } = section;

        public int Channel { get; } = channel;

        public BmsTextRange Data { get; } = data;

        public int PairCount { get; } = data.Span.IsWhiteSpace() || data.Span.Length < 2 ? 0 : data.Span.Length / 2;

        public List<BmsDataElement> Elements { get; } = [];

        public void Expand(int numberBase, bool stopAtFirstValue, ParseTimeoutGuard timeoutGuard)
        {
            ReadOnlySpan<char> text = Data.Span;
            while (cursor < PairCount)
            {
                timeoutGuard.ThrowIfTimedOutEvery(cursor + 1, "bms_expand_pairs");
                int index = cursor++;
                int value = ParseBase(text[index * 2], text[index * 2 + 1], numberBase);
                if (value != 0)
                {
                    Elements.Add(new BmsDataElement(index, value));
                }
                if (stopAtFirstValue && value > 0)
                {
                    break;
                }
            }
        }
    }

    private enum BmsOperationKind
    {
        Section, Diagnostic, Scroll, Bpm, Stop, Background, Bga, Hidden, Normal, Long, Mine
    }

    private struct BmsOperation(BmsOperationKind kind, double section, int lane = -1, int value = 0, double eventValue = 0.0)
    {
        // 種別とレーンは小さい固定範囲です。制御値と整数ノート値は同時に使わず、疎な操作の領域を共有します。
        public double Section = section;

        public readonly double EventValue = kind is BmsOperationKind.Scroll or BmsOperationKind.Bpm or BmsOperationKind.Stop ? eventValue : value;

        public int Position;

        private readonly int kindAndLane = (int)kind | ((lane + 1) << 8);

        public readonly BmsOperationKind Kind => (BmsOperationKind)(kindAndLane & 255);

        public readonly int Lane => (kindAndLane >> 8) - 1;

        public readonly int Value => (int)EventValue;
    }

    private sealed partial class BmsChartBuilder
    {
        public ChartModel Build()
        {
            timeoutGuard.ThrowIfTimedOut("bms_build_start");
            ChartMode mode = DetectMode();
            var model = new ChartModel(filePath, mode)
            {
                InitialBpm = InitialBpm,
                Title = Title,
                Subtitle = Subtitle,
                Level = Level,
                Difficulty = Difficulty,
                DifficultyDefined = DifficultyDefined,
                JudgeRank = NormalizeJudgeRank(JudgeRank, JudgeRankType, mode),
                Total = Total,
                TotalDefined = TotalDefined,
                LnMode = LnMode,
                ExLevel = ExLevel,
                HasRandom = HasRandom
            };
            List<BmsOperation> operations = PrepareOperations(mode);
            var timelines = new BmsPositionIndex(operations, mode.KeyCount, InitialBpm, timeoutGuard);
            BmsLaneIndex[] lanes = PrepareLanes(operations, mode.KeyCount);
            var pending = new ChartNote[mode.KeyCount];
            for (int index = 0; index < operations.Count; index++)
            {
                timeoutGuard.ThrowIfTimedOutEvery(index + 1, "bms_replay_operations");
                BmsOperation operation = operations[index];
                if (operation.Kind == BmsOperationKind.Diagnostic)
                {
                    InvalidPair();
                    continue;
                }
                ChartTimeline timeline = timelines.Get(operation.Position, operation.Section);
                switch (operation.Kind)
                {
                    case BmsOperationKind.Section: timeline.HasSectionLine = true; break;
                    case BmsOperationKind.Scroll: timeline.Scroll = operation.EventValue; break;
                    case BmsOperationKind.Bpm: timeline.Bpm = operation.EventValue; break;
                    case BmsOperationKind.Stop:
                        if (timeline.Bpm <= 0)
                        {
                            throw new BmsRecoverableParseException("BMS timeline BPM is not defined before STOP.");
                        }
                        timeline.StopMicroseconds = (long)(1000.0 * 1000.0 * 60.0 * 4.0 * operation.EventValue / timeline.Bpm);
                        break;
                    case BmsOperationKind.Background: timeline.HasBackground = true; break;
                    case BmsOperationKind.Bga: timeline.HasBga = true; break;
                    case BmsOperationKind.Hidden: timeline.HasHiddenNote = true; break;
                    case BmsOperationKind.Normal:
                        ApplyNormal(timelines, lanes, pending, operation, timeline);
                        break;
                    case BmsOperationKind.Long:
                        ApplyLong(timelines, lanes, pending, operation, timeline);
                        break;
                    case BmsOperationKind.Mine:
                        if (timeline.Notes[operation.Lane] == null && !(lanes[operation.Lane]?.IsCovered(operation.Position) ?? false))
                        {
                            SetNote(lanes, operation.Lane, operation.Position, timeline, ChartNote.CreateMine(operation.Value));
                        }
                        break;
                }
            }
            for (int lane = 0; lane < pending.Length; lane++)
            {
                ChartNote start = pending[lane];
                if (start != null && start.Owner != null && start.Section != double.MinValue)
                {
                    SetNote(lanes, lane, timelines.Position(start.Section), start.Owner, null);
                }
            }
            if (timelines.First.Bpm <= 0)
            {
                AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Error, "BMS_INITIAL_BPM_INVALID", "#BPMが定義されていないか無効です");
                throw new BmsRecoverableParseException("BMS initial BPM is not defined or invalid.");
            }
            model.SetTimelines(timelines.FinalValues(), timeoutGuard);
            int totalNotes = model.Summary.TotalNotes;
            if (!model.DifficultyDefined)
            {
                model.Difficulty = InferBeatorajaDifficulty(model.Title, model.Subtitle, totalNotes);
            }
            if (!TotalDefined)
            {
                model.Total = CalculateDefaultTotal(mode, totalNotes);
            }
            return model;
        }

        private ChartMode DetectMode()
        {
            if (isPms)
            {
                return ChartMode.Popn9;
            }
            bool seven = false;
            bool secondPlayer = false;
            int lineIndex = 0;
            foreach (BmsChannelLine line in channelLines)
            {
                timeoutGuard.ThrowIfTimedOutEvery(++lineIndex, "bms_detect_mode");
                line.Expand(Base, true, timeoutGuard);
                bool nonzero = false;
                foreach (BmsDataElement element in line.Elements)
                {
                    if (element.Value < 0)
                    {
                        InvalidPair();
                    }
                    else
                    {
                        nonzero = true;
                        break;
                    }
                }
                if (!nonzero)
                {
                    continue;
                }
                int channel = line.Channel;
                if (IsWithin(channel, P1KeyBase, 9) || IsWithin(channel, P1InvisibleKeyBase, 9) || IsWithin(channel, P1LongKeyBase, 9) || IsWithin(channel, P1MineKeyBase, 9))
                {
                    int offset = channel % 36 - 1;
                    seven |= offset == 7 || offset == 8;
                }
                if (IsWithin(channel, P2KeyBase, 9) || IsWithin(channel, P2InvisibleKeyBase, 9) || IsWithin(channel, P2LongKeyBase, 9) || IsWithin(channel, P2MineKeyBase, 9))
                {
                    secondPlayer = true;
                    int offset = channel % 36 - 1;
                    seven |= offset == 7 || offset == 8;
                }
            }
            return secondPlayer ? (seven ? ChartMode.Beat14 : ChartMode.Beat10) : (seven ? ChartMode.Beat7 : ChartMode.Beat5);
        }

        private List<BmsOperation> PrepareOperations(ChartMode mode)
        {
            double[] rates = new double[Math.Max(1, maxSection + 1)];
            Array.Fill(rates, 1.0);
            var buckets = new List<BmsChannelLine>[rates.Length];
            int lineIndex = 0;
            foreach (BmsChannelLine line in channelLines)
            {
                timeoutGuard.ThrowIfTimedOutEvery(++lineIndex, "bms_prepare_sections");
                (buckets[line.Section] ??= []).Add(line);
                if (line.Channel == SectionRate && TryParseJavaDouble(line.Data.Span.ToString(), out double rate))
                {
                    rates[line.Section] = rate;
                }
            }
            double[] starts = new double[rates.Length + 1];
            for (int section = 1; section < starts.Length; section++)
            {
                timeoutGuard.ThrowIfTimedOutEvery(section, "bms_section_start_accumulate");
                starts[section] = starts[section - 1] + rates[section - 1];
            }
            int capacity = rates.Length;
            foreach (BmsChannelLine line in channelLines)
            {
                timeoutGuard.ThrowIfTimedOutEvery(++lineIndex, "bms_operation_capacity");
                bool consumed = line.Channel is BpmChange or BpmChangeExtend or Stop or Scroll or LaneAutoplay or BgaPlay or LayerPlay;
                if (!consumed)
                {
                    consumed = ResolveLane(line.Channel, mode, out _) >= 0;
                }
                if (consumed)
                {
                    line.Expand(Base, false, timeoutGuard);
                    capacity = checked(capacity + line.Elements.Count);
                }
            }
            var operations = new List<BmsOperation>(capacity);
            for (int section = 0; section < rates.Length; section++)
            {
                timeoutGuard.ThrowIfTimedOutEvery(section + 1, "bms_prepare_operations");
                double start = starts[section];
                double rate = rates[section];
                operations.Add(new BmsOperation(BmsOperationKind.Section, start));
                List<BmsChannelLine> lines = buckets[section];
                if (lines == null)
                {
                    continue;
                }
                List<BmsTimelineEvent> events = [];
                int sequence = 0;
                foreach (BmsChannelLine line in lines)
                {
                    if (line.Channel != BpmChange && line.Channel != BpmChangeExtend && line.Channel != Stop && line.Channel != Scroll)
                    {
                        continue;
                    }
                    line.Expand(Base, false, timeoutGuard);
                    int controlIndex = 0;
                    foreach (BmsDataElement element in line.Elements)
                    {
                        timeoutGuard.ThrowIfTimedOutEvery(++controlIndex, "bms_prepare_control_operations");
                        if (element.Value < 0)
                        {
                            operations.Add(new BmsOperation(BmsOperationKind.Diagnostic, 0.0));
                            continue;
                        }
                        double position = (double)element.Index / line.PairCount;
                        if (line.Channel == BpmChange)
                        {
                            ReadOnlySpan<char> text = line.Data.Span;
                            int value = Base == 62 ? ParseBase36(text[element.Index * 2], text[element.Index * 2 + 1]) : element.Value;
                            if (value >= 0)
                            {
                                events.Add(BmsTimelineEvent.CreateBpm(position, (value / 36) * 16 + value % 36, sequence++));
                            }
                        }
                        else if (line.Channel == BpmChangeExtend && bpmTable.TryGetValue(element.Value, out double bpm))
                        {
                            events.Add(BmsTimelineEvent.CreateBpm(position, bpm, sequence++));
                        }
                        else if (line.Channel == Stop && stopTable.TryGetValue(element.Value, out double stop))
                        {
                            events.Add(BmsTimelineEvent.CreateStop(position, stop, sequence++));
                        }
                        else if (line.Channel == Scroll && scrollTable.TryGetValue(element.Value, out double scroll))
                        {
                            events.Add(BmsTimelineEvent.CreateScroll(position, scroll, sequence++));
                        }
                    }
                }
                events.Sort(BmsTimelineEvent.Comparer);
                foreach (BmsTimelineEvent item in events)
                {
                    BmsOperationKind kind = item.Kind == BmsTimelineEventKind.Bpm ? BmsOperationKind.Bpm
                        : item.Kind == BmsTimelineEventKind.Stop ? BmsOperationKind.Stop : BmsOperationKind.Scroll;
                    operations.Add(new BmsOperation(kind, start + rate * item.Position, eventValue: item.Value));
                }
                foreach (BmsChannelLine line in lines)
                {
                    int lane = ResolveLane(line.Channel, mode, out BmsLaneChannelKind kind);
                    BmsOperationKind operationKind;
                    if (kind == BmsLaneChannelKind.None)
                    {
                        if (line.Channel == LaneAutoplay)
                        {
                            operationKind = BmsOperationKind.Background;
                        }
                        else if (line.Channel == BgaPlay || line.Channel == LayerPlay)
                        {
                            operationKind = BmsOperationKind.Bga;
                        }
                        else
                        {
                            continue;
                        }
                    }
                    else if (lane < 0)
                    {
                        continue;
                    }
                    else
                    {
                        operationKind = kind == BmsLaneChannelKind.Normal ? BmsOperationKind.Normal
                            : kind == BmsLaneChannelKind.Long ? BmsOperationKind.Long
                            : kind == BmsLaneChannelKind.Mine ? BmsOperationKind.Mine : BmsOperationKind.Hidden;
                    }
                    line.Expand(Base, false, timeoutGuard);
                    int elementIndex = 0;
                    foreach (BmsDataElement element in line.Elements)
                    {
                        timeoutGuard.ThrowIfTimedOutEvery(++elementIndex, "bms_prepare_note_operations");
                        operations.Add(element.Value < 0 ? new BmsOperation(BmsOperationKind.Diagnostic, 0.0)
                            : new BmsOperation(operationKind, start + rate * ((double)element.Index / line.PairCount), lane, element.Value));
                    }
                }
            }
            return operations;
        }

        private BmsLaneIndex[] PrepareLanes(List<BmsOperation> operations, int count)
        {
            bool[] needed = new bool[count];
            bool[] queried = new bool[count];
            for (int index = 0; index < operations.Count; index++)
            {
                timeoutGuard.ThrowIfTimedOutEvery(index + 1, "bms_lane_requirements");
                BmsOperation item = operations[index];
                if (item.Kind == BmsOperationKind.Long || (item.Kind == BmsOperationKind.Normal && item.Value == LnObject))
                {
                    needed[item.Lane] = true;
                }
                if (item.Kind == BmsOperationKind.Long || item.Kind == BmsOperationKind.Mine)
                {
                    queried[item.Lane] = true;
                }
            }
            var positions = new List<int>[count];
            for (int index = 0; index < operations.Count; index++)
            {
                timeoutGuard.ThrowIfTimedOutEvery(index + 1, "bms_lane_positions");
                BmsOperation item = operations[index];
                if (item.Lane >= 0 && needed[item.Lane] && (item.Kind == BmsOperationKind.Normal || item.Kind == BmsOperationKind.Long || item.Kind == BmsOperationKind.Mine))
                {
                    (positions[item.Lane] ??= []).Add(item.Position);
                }
            }
            var lanes = new BmsLaneIndex[count];
            for (int lane = 0; lane < count; lane++)
            {
                if (needed[lane])
                {
                    lanes[lane] = new BmsLaneIndex(positions[lane], queried[lane], timeoutGuard);
                }
            }
            return lanes;
        }

        private void ApplyNormal(BmsPositionIndex timelines, BmsLaneIndex[] lanes, ChartNote[] pending, BmsOperation item, ChartTimeline timeline)
        {
            int lane = item.Lane;
            if (item.Value != LnObject)
            {
                SetNote(lanes, lane, item.Position, timeline, ChartNote.CreateNormal(item.Value));
                return;
            }
            int previousPosition = lanes[lane].Previous(item.Position);
            if (previousPosition < 0)
            {
                return;
            }
            ChartTimeline previous = timelines.Values[previousPosition];
            ChartNote note = previous.Notes[lane];
            if (note.Kind == ChartNoteKind.Normal)
            {
                var start = ChartNote.CreateLong(note.Wav, LnMode);
                var end = ChartNote.CreateLong(-2, LnMode);
                SetNote(lanes, lane, previousPosition, previous, start);
                SetNote(lanes, lane, item.Position, timeline, end);
                start.PairWith(end);
                lanes[lane].Cover(previousPosition, item.Position);
            }
            else if (note.Kind == ChartNoteKind.Long && note.Pair == null)
            {
                var end = ChartNote.CreateLong(-2, note.LongType);
                SetNote(lanes, lane, item.Position, timeline, end);
                note.PairWith(end);
                lanes[lane].Cover(previousPosition, item.Position);
                pending[lane] = null;
            }
        }

        private void ApplyLong(BmsPositionIndex timelines, BmsLaneIndex[] lanes, ChartNote[] pending, BmsOperation item, ChartTimeline timeline)
        {
            int lane = item.Lane;
            if (lanes[lane].IsCovered(item.Position))
            {
                ChartNote pendingNote = pending[lane];
                if (pendingNote == null)
                {
                    var ignored = ChartNote.CreateLong(item.Value, LnMode);
                    ignored.Section = double.MinValue;
                    pending[lane] = ignored;
                }
                else
                {
                    if (pendingNote.Section != double.MinValue && pendingNote.Owner != null)
                    {
                        SetNote(lanes, lane, timelines.Position(pendingNote.Section), pendingNote.Owner, null);
                    }
                    pending[lane] = null;
                }
                return;
            }
            ChartNote start = pending[lane];
            if (start != null && start.Section == double.MinValue)
            {
                pending[lane] = null;
                return;
            }
            if (start == null)
            {
                ChartNote existing = timeline.Notes[lane];
                if (existing != null && existing.Kind == ChartNoteKind.Normal && existing.Wav != item.Value)
                {
                    timeline.HasBackground = true;
                }
                var note = ChartNote.CreateLong(item.Value, LnMode);
                SetNote(lanes, lane, item.Position, timeline, note);
                pending[lane] = note;
                return;
            }
            int startPosition = timelines.Position(start.Section);
            int lower = start.Section < timeline.Section ? startPosition : -1;
            int before = item.Position;
            int removed = 0;
            for (int previous = lanes[lane].Previous(before); previous > lower; previous = lanes[lane].Previous(before))
            {
                timeoutGuard.ThrowIfTimedOutEvery(++removed, "bms_long_note_remove");
                ChartTimeline owner = timelines.Values[previous];
                ChartNote existing = owner.Notes[lane];
                SetNote(lanes, lane, previous, owner, null);
                if (existing.Kind == ChartNoteKind.Normal)
                {
                    owner.HasBackground = true;
                }
                before = previous;
            }
            // 始点セルの現在値ではなく、生成済み始点の座標へ到達できるかで閉鎖します。
            if (lower < 0)
            {
                return;
            }
            var endNote = ChartNote.CreateLong(item.Value == start.Wav ? -2 : item.Value, start.LongType);
            SetNote(lanes, lane, item.Position, timeline, endNote);
            start.PairWith(endNote);
            lanes[lane].Cover(startPosition, item.Position);
            pending[lane] = null;
        }

        private static void SetNote(BmsLaneIndex[] lanes, int lane, int position, ChartTimeline timeline, ChartNote note)
        {
            bool wasPresent = timeline.Notes[lane] != null;
            timeline.SetNote(lane, note);
            if (wasPresent != (note != null))
            {
                lanes[lane]?.Change(position, note == null ? -1 : 1);
            }
        }

        private void InvalidPair()
        {
            AddDiagnostic(diagnostics, ChartInfoParseDiagnosticSeverity.Warning, "BMS_CHANNEL_DATA_INVALID", "チャンネル定義中の不正な値です");
        }

        private static bool IsWithin(int channel, int start, int count)
        {
            return channel >= start && channel < start + count;
        }

        private static int ResolveLane(int channel, ChartMode mode, out BmsLaneChannelKind kind)
        {
            int[] assignment = mode.GetBmsChannelAssign();
            if (TryResolveLane(channel, P1KeyBase, P2KeyBase, assignment, out int lane))
            {
                kind = BmsLaneChannelKind.Normal;
            }
            else if (TryResolveLane(channel, P1InvisibleKeyBase, P2InvisibleKeyBase, assignment, out lane))
            {
                kind = BmsLaneChannelKind.Hidden;
            }
            else if (TryResolveLane(channel, P1LongKeyBase, P2LongKeyBase, assignment, out lane))
            {
                kind = BmsLaneChannelKind.Long;
            }
            else if (TryResolveLane(channel, P1MineKeyBase, P2MineKeyBase, assignment, out lane))
            {
                kind = BmsLaneChannelKind.Mine;
            }
            else
            {
                kind = BmsLaneChannelKind.None;
                lane = -1;
            }
            return lane;
        }

        private static bool TryResolveLane(int channel, int first, int second, int[] assignment, out int lane)
        {
            lane = -1;
            if (IsWithin(channel, first, 9))
            {
                lane = assignment[channel - first];
                return true;
            }
            if (IsWithin(channel, second, 9))
            {
                lane = assignment[channel - second + 9];
                return true;
            }
            return false;
        }
    }

    /// <summary>座標圧縮と実生成の寿命を分け、要求時だけ生成済みの前駆を参照します。</summary>
    private sealed class BmsPositionIndex
    {
        private readonly Dictionary<double, int> positions;
        private readonly BmsFenwick generated;
        private readonly int laneCount;
        private readonly ParseTimeoutGuard timeoutGuard;

        public ChartTimeline[] Values { get; }

        public ChartTimeline First => Values[generated.Select(1)];

        public BmsPositionIndex(List<BmsOperation> operations, int laneCount, double initialBpm, ParseTimeoutGuard timeoutGuard)
        {
            this.laneCount = laneCount;
            this.timeoutGuard = timeoutGuard;
            HashSet<double> uniqueCoordinates = [0.0];
            for (int index = 0; index < operations.Count; index++)
            {
                timeoutGuard.ThrowIfTimedOutEvery(index + 1, "bms_coordinate_collect");
                if (operations[index].Kind != BmsOperationKind.Diagnostic)
                {
                    uniqueCoordinates.Add(operations[index].Section);
                }
            }
            List<double> coordinates = [.. uniqueCoordinates];
            try
            {
                coordinates.Sort(new CoordinateComparer(timeoutGuard));
            }
            catch (InvalidOperationException ex) when (ex.InnerException is ChartInfoParseTimeoutException)
            {
                // Sortが比較器の例外を包むため、協調timeoutの失敗分類を保ちます。
                ExceptionDispatchInfo.Throw(ex.InnerException);
            }
            positions = new Dictionary<double, int>(coordinates.Count);
            double previous = 0.0;
            for (int index = 0; index < coordinates.Count; index++)
            {
                timeoutGuard.ThrowIfTimedOutEvery(index + 1, "bms_coordinate_compress");
                double value = coordinates[index];
                if (index == 0 || value.CompareTo(previous) != 0)
                {
                    positions.Add(value, positions.Count);
                }
                previous = value;
            }
            Values = new ChartTimeline[positions.Count];
            generated = new BmsFenwick(positions.Count);
            int origin = positions[0.0];
            Values[origin] = new ChartTimeline(0.0, 0.0, laneCount) { Bpm = initialBpm };
            generated.Add(origin, 1);
            for (int index = 0; index < operations.Count; index++)
            {
                timeoutGuard.ThrowIfTimedOutEvery(index + 1, "bms_operation_positions");
                BmsOperation operation = operations[index];
                if (operation.Kind != BmsOperationKind.Diagnostic)
                {
                    operation.Position = positions[operation.Section];
                    operations[index] = operation;
                }
            }
        }

        public int Position(double section)
        {
            return positions[section];
        }

        public ChartTimeline Get(int position, double section)
        {
            ChartTimeline existing = Values[position];
            if (existing != null)
            {
                return existing;
            }
            int before = generated.Sum(position);
            ChartTimeline previous = Values[generated.Select(before > 0 ? before : 1)];
            if (previous.Bpm <= 0)
            {
                throw new BmsRecoverableParseException("BMS timeline BPM is not defined before a future timeline.");
            }
            double preciseTime = previous.PreciseTimeMicroseconds + previous.StopMicroseconds + 240000.0 * 1000.0 * (section - previous.Section) / previous.Bpm;
            var timeline = new ChartTimeline(section, preciseTime, laneCount) { Bpm = previous.Bpm, Scroll = previous.Scroll };
            Values[position] = timeline;
            generated.Add(position, 1);
            return timeline;
        }

        public List<ChartTimeline> FinalValues()
        {
            var result = new List<ChartTimeline>();
            for (int index = 0; index < Values.Length; index++)
            {
                timeoutGuard.ThrowIfTimedOutEvery(index + 1, "bms_final_timelines");
                if (Values[index] != null)
                {
                    result.Add(Values[index]);
                }
            }
            return result;
        }

        private sealed class CoordinateComparer(ParseTimeoutGuard guard) : IComparer<double>
        {
            private int count;

            public int Compare(double left, double right)
            {
                guard.ThrowIfTimedOutEvery(++count, "bms_coordinate_sort");
                return left.CompareTo(right);
            }
        }
    }

    private sealed class BmsLaneIndex
    {
        private readonly int[] positions;
        private readonly BmsFenwick notes;
        private readonly BmsFenwick coverage;

        public BmsLaneIndex(List<int> values, bool needsCoverage, ParseTimeoutGuard timeoutGuard)
        {
            values.Sort();
            var unique = new List<int>();
            for (int index = 0; index < values.Count; index++)
            {
                timeoutGuard.ThrowIfTimedOutEvery(index + 1, "bms_lane_compress");
                if (index == 0 || values[index] != values[index - 1])
                {
                    unique.Add(values[index]);
                }
            }
            positions = [.. unique];
            notes = new BmsFenwick(positions.Length);
            coverage = needsCoverage ? new BmsFenwick(positions.Length + 1) : null;
        }

        public void Change(int position, int value)
        {
            notes.Add(Array.BinarySearch(positions, position), value);
        }

        public int Previous(int position)
        {
            int local = Array.BinarySearch(positions, position);
            if (local < 0)
            {
                local = ~local;
            }
            int count = notes.Sum(local);
            return count == 0 ? -1 : positions[notes.Select(count)];
        }

        public bool IsCovered(int position)
        {
            return coverage != null && coverage.Sum(Array.BinarySearch(positions, position) + 1) > 0;
        }

        public void Cover(int start, int end)
        {
            if (coverage != null && start <= end)
            {
                coverage.Add(Array.BinarySearch(positions, start), 1);
                coverage.Add(Array.BinarySearch(positions, end) + 1, -1);
            }
        }
    }

    /// <summary>BMS候補の圧縮座標だけに使う個数・順位選択と被覆差分の索引です。</summary>
    private sealed class BmsFenwick(int count)
    {
        private readonly int[] values = new int[count + 1];

        public void Add(int position, int delta)
        {
            for (int index = position + 1; index < values.Length; index += index & -index)
            {
                values[index] += delta;
            }
        }

        public int Sum(int exclusive)
        {
            int result = 0;
            for (int index = exclusive; index > 0; index -= index & -index)
            {
                result += values[index];
            }
            return result;
        }

        public int Select(int rank)
        {
            int position = 0;
            int step = 1;
            while (step < values.Length)
            {
                step <<= 1;
            }
            for (step >>= 1; step > 0; step >>= 1)
            {
                int next = position + step;
                if (next < values.Length && values[next] < rank)
                {
                    position = next;
                    rank -= values[next];
                }
            }
            return position;
        }
    }
}
