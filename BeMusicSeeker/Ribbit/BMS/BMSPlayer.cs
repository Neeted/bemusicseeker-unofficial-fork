using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Ribbit.Logging;
using Ribbit.Math;
using Ribbit.Media;

namespace Ribbit.BMS;

/// <summary>譜面状態、画面素材、再生Taskの寿命を管理し、音声配置は機能別のplayerへ委ねます。</summary>
/// <typeparam name="TImageLoader">譜面画像を表示・解放するloader。</typeparam>
public abstract class BMSPlayer<TImageLoader> : IDisposable where TImageLoader : class, IImageLoader
{
    protected class NoteQueue : IEnumerable<BMSFile.Chart.Note>, IEnumerable
    {
        public enum QueueType
        {
            NOTE,
            JUDGE,
            STATICS,
            RESERVED_03,
            RESERVED_04,
            RESERVED_05,
            RESERVED_06,
            RESERVED_07,
            RESERVED_08,
            RESERVED_09,
            RESERVED_10,
            RESERVED_11,
            RESERVED_12,
            RESERVED_13,
            RESERVED_14,
            RESERVED_15
        }

        private readonly BMSFile.Chart.Note[] notes;

        private int[] indices = new int[Enum.GetValues(typeof(QueueType)).Length];

        public int Count => notes.Length;

        public NoteQueue(IEnumerable<BMSFile.Chart.Note> notes)
        {
            if (notes == null)
            {
                throw new ArgumentNullException("notes");
            }
            this.notes = [.. notes];
        }

        public void Reset()
        {
            indices = new int[Enum.GetValues(typeof(QueueType)).Length];
        }

        public void Reset(QueueType queue)
        {
            indices[(int)queue] = 0;
        }

        public BMSFile.Chart.Note Dequeue(QueueType queue)
        {
            if (CountEnqueued(queue) == 0)
            {
                throw new InvalidOperationException(string.Concat("Queue for ", queue, " is empty"));
            }
            int num = indices[(int)queue];
            BMSFile.Chart.Note result = notes[num];
            indices[(int)queue] = num + 1;
            return result;
        }

        public BMSFile.Chart.Note Peek(QueueType queue)
        {
            if (CountEnqueued(queue) == 0)
            {
                throw new InvalidOperationException(string.Concat("Queue for ", queue, " is empty"));
            }
            int num = indices[(int)queue];
            return notes[num];
        }

        public void CopyTo(QueueType from, QueueType to)
        {
            indices[(int)to] = indices[(int)from];
        }

        public int CountEnqueued(QueueType queue)
        {
            return notes.Length - indices[(int)queue];
        }

        public int CountDequeued(QueueType queue)
        {
            return indices[(int)queue];
        }

        public IEnumerable<BMSFile.Chart.Note> Enqueued(QueueType queue)
        {
            for (int i = indices[(int)queue]; i < notes.Length; i++)
            {
                yield return notes[i];
            }
        }

        public IEnumerable<BMSFile.Chart.Note> Dequeued(QueueType queue)
        {
            for (int i = 0; i < indices[(int)queue]; i++)
            {
                yield return notes[i];
            }
        }

        public IEnumerable<BMSFile.Chart.Note> DequeWhile(QueueType queue, Func<BMSFile.Chart.Note, bool> condition)
        {
            while (CountEnqueued(queue) != 0)
            {
                int num = indices[(int)queue];
                BMSFile.Chart.Note note = notes[num];
                if (condition(note))
                {
                    indices[(int)queue] = num + 1;
                    yield return note;
                    continue;
                }
                break;
            }
        }

        IEnumerator<BMSFile.Chart.Note> IEnumerable<BMSFile.Chart.Note>.GetEnumerator()
        {
            return ((IEnumerable<BMSFile.Chart.Note>)notes).GetEnumerator();
        }

        public IEnumerator GetEnumerator()
        {
            return notes.GetEnumerator();
        }
    }

    protected TimeSpan currentTime = TimeSpan.Zero;

    /// <summary>tick、譜面状態更新、pause、seek、速度変更を一つの制御境界へ直列化します。</summary>
    protected readonly object playbackControlSync = new();

    private TimeSpan musicDuration;

    private float playbackRate = 1f;

    private TimeSpan bgaDuration;

    protected Func<TimeSpan> durationProvider;

    private double noteDensity;

    private BMSFile.Chart.Note lastCtrlNote;

    private TimeSpan densityRange = new(0, 0, 0, 1);

    protected readonly NoteQueue ControlNotesQueue;

    protected readonly NoteQueue BgaBaseNotesQueue;

    protected readonly NoteQueue BgaPoorNotesQueue;

    protected readonly NoteQueue BgaLayerNotesQueue;

    protected readonly ReadOnlyCollection<NoteQueue> VisibleNotes1PQueue;

    protected readonly ReadOnlyCollection<NoteQueue> VisibleNotes2PQueue;

    protected readonly ReadOnlyCollection<NoteQueue> InvisibleNotes1PQueue;

    protected readonly ReadOnlyCollection<NoteQueue> InvisibleNotes2PQueue;

    protected readonly ReadOnlyCollection<NoteQueue> LongNotes1PQueue;

    protected readonly ReadOnlyCollection<NoteQueue> LongNotes2PQueue;

    protected readonly ReadOnlyCollection<NoteQueue> MineNotes1PQueue;

    protected readonly ReadOnlyCollection<NoteQueue> MineNotes2PQueue;

    protected readonly Stopwatch timer = Stopwatch.StartNew();

    protected CancellationTokenSource taskTokenSource;

    protected TimeSpan timerOffset = TimeSpan.Zero;

    protected Task playTask;

    private bool disposedValue;

    private ExceptionDispatchInfo playbackControlFailure;

    public BMSFile Bms { get; }

    public TimeSpan CurrentTime
    {
        get
        {
            return currentTime;
        }
        set
        {
            MoveTo(value);
        }
    }

    public TimeSpan BmsDuration => Bms.Duration;

    public TimeSpan MusicDuration
    {
        get
        {
            return musicDuration;
        }
        protected set
        {
            if (musicDuration != value && value >= TimeSpan.Zero)
            {
                musicDuration = value;
                Duration = durationProvider();
            }
        }
    }

    public TimeSpan BgaDuration
    {
        get
        {
            return bgaDuration;
        }
        protected set
        {
            if (bgaDuration != value && value >= TimeSpan.Zero)
            {
                bgaDuration = value;
                Duration = durationProvider();
            }
        }
    }

    public virtual float PlaybackRate
    {
        get
        {
            lock (playbackControlSync)
            {
                return playbackRate;
            }
        }
        set
        {
            lock (playbackControlSync)
            {
                if (playbackRate != value && !(value <= 0f))
                {
                    playbackRate = value;
                    ResetTimer();
                }
            }
        }
    }

    public TimeSpan Duration { get; protected set; }

    public string FileName => Bms.Path;

    public double NoteDensity
    {
        get
        {
            return noteDensity;
        }
        private set
        {
            noteDensity = value;
            if (NoteDensityMax < value)
            {
                NoteDensityMax = value;
            }
        }
    }

    public int Combo { get; private set; }

    public int MaxCombo { get; private set; }

    public double CurrentBpm { get; private set; }

    public TimeSpan StopTime { get; private set; }

    public int CurrentMeasure { get; private set; }

    public double NoteDensityMax { get; private set; }

    protected ReadOnlyCollection<TImageLoader> ImageLoaders { get; set; } = new List<TImageLoader>().AsReadOnly();

    protected TImageLoader BgaBaseLoader { get; set; }

    protected TImageLoader BgaPoorLoader { get; set; }

    protected TImageLoader BgaLayerLoader { get; set; }

    protected TImageLoader StagefileLoader { get; set; }

    protected TImageLoader BannerLoader { get; set; }

    protected TImageLoader BackbmpLoader { get; set; }

    public PlayState PlayState { get; private set; } = PlayState.Stopped;

    /// <summary>一回の再生tickで取得した時計と音声側の完了判定を変更不能な値で返します。</summary>
    protected readonly record struct PlaybackTickResult(TimeSpan Time, bool Completed);

    protected BMSPlayer()
    {
    }

    public BMSPlayer(BMSFile bms)
    {
        Bms = bms ?? throw new ArgumentNullException("bms");
        ControlNotesQueue = new NoteQueue(Bms.Measures.ControlNotes);
        BgaBaseNotesQueue = new NoteQueue(Bms.Measures.BgaBaseNotes);
        BgaPoorNotesQueue = new NoteQueue(Bms.Measures.BgaPoorNotes);
        BgaLayerNotesQueue = new NoteQueue(Bms.Measures.BgaLayerNotes);
        VisibleNotes1PQueue = Bms.Measures.VisibleNotes1P.Select(a => new NoteQueue(a)).ToList().AsReadOnly();
        VisibleNotes2PQueue = Bms.Measures.VisibleNotes2P.Select(a => new NoteQueue(a)).ToList().AsReadOnly();
        InvisibleNotes1PQueue = Bms.Measures.InvisibleNotes1P.Select(a => new NoteQueue(a)).ToList().AsReadOnly();
        InvisibleNotes2PQueue = Bms.Measures.InvisibleNotes2P.Select(a => new NoteQueue(a)).ToList().AsReadOnly();
        LongNotes1PQueue = Bms.Measures.LongNotes1P.Select(a => new NoteQueue(a)).ToList().AsReadOnly();
        LongNotes2PQueue = Bms.Measures.LongNotes2P.Select(a => new NoteQueue(a)).ToList().AsReadOnly();
        MineNotes1PQueue = Bms.Measures.MineNotes1P.Select(a => new NoteQueue(a)).ToList().AsReadOnly();
        MineNotes2PQueue = Bms.Measures.MineNotes2P.Select(a => new NoteQueue(a)).ToList().AsReadOnly();
        InitializeLoaders();
        currentTime = TimeSpan.Zero;
        durationProvider = () => TimeSpan.FromTicks(System.Math.Max(BmsDuration.Ticks, System.Math.Max(BgaDuration.Ticks, MusicDuration.Ticks)));
        MusicDuration = TimeSpan.Zero;
        BgaDuration = TimeSpan.Zero;
        CurrentBpm = Bms.Bpm?.ToDouble() ?? 0.0;
    }

    private double calculateNotesDensity()
    {
        return (double)(VisibleNotes1PQueue.Sum(q => q.CountDequeued(NoteQueue.QueueType.NOTE) - q.CountDequeued(NoteQueue.QueueType.STATICS)) + VisibleNotes2PQueue.Sum(q => q.CountDequeued(NoteQueue.QueueType.NOTE) - q.CountDequeued(NoteQueue.QueueType.STATICS)) + LongNotes1PQueue.Sum(q => q.CountDequeued(NoteQueue.QueueType.NOTE) - q.CountDequeued(NoteQueue.QueueType.STATICS)) + LongNotes2PQueue.Sum(q => q.CountDequeued(NoteQueue.QueueType.NOTE) - q.CountDequeued(NoteQueue.QueueType.STATICS))) / densityRange.TotalSeconds;
    }

    private int calculateCombo()
    {
        return VisibleNotes1PQueue.Sum(q => q.CountDequeued(NoteQueue.QueueType.NOTE)) + VisibleNotes2PQueue.Sum(q => q.CountDequeued(NoteQueue.QueueType.NOTE)) + LongNotes1PQueue.Sum(q => q.CountDequeued(NoteQueue.QueueType.NOTE)) + LongNotes2PQueue.Sum(q => q.CountDequeued(NoteQueue.QueueType.NOTE));
    }

    protected virtual void InitializeLoaders()
    {
        ImageLoaders = new TImageLoader[Bms.BmpArray.Length].ToList().AsReadOnly();
        BgaBaseLoader = null;
        BgaPoorLoader = null;
        BgaLayerLoader = null;
        StagefileLoader = null;
        BannerLoader = null;
        BackbmpLoader = null;
    }

    public abstract void LoadResources();

    /// <summary>指定時刻を譜面状態へ適用します。</summary>
    protected virtual void ForwardTo(TimeSpan time)
    {
        lock (playbackControlSync)
        {
            if (time > Duration)
            {
                time = Duration;
            }
            else if (time < TimeSpan.Zero)
            {
                time = TimeSpan.Zero;
            }
            if (!(time != TimeSpan.Zero) || !(time <= currentTime))
            {
                currentTime = time;
                ForwardControlNotesToCurrentTime();
                ForwardBgaNotesToCurrentTime();
                ForwardInvisibleNotesToCurrentTime();
                ForwardLongNotesToCurrentTime();
                ForwardMineNotesToCurrentTime();
                ForwardVisibleNotesToCurrentTime();
                NoteDensity = calculateNotesDensity();
                Combo = calculateCombo();
            }
        }
    }

    protected virtual void ForwardControlNotesToCurrentTime()
    {
        foreach (BMSFile.Chart.Note item in ControlNotesQueue.DequeWhile(NoteQueue.QueueType.NOTE, n => n.AbsoluteTime <= currentTime))
        {
            BMSFile.Chart.Note note = (lastCtrlNote = item);
            switch (note.Type)
            {
                case BMSFile.Chart.Note.NoteType.BAR_LINE:
                    if (CurrentMeasure < Bms.Measures.LastIndex)
                    {
                        CurrentMeasure++;
                    }
                    break;
                case BMSFile.Chart.Note.NoteType.BPM:
                    if (note.Value != null)
                    {
                        CurrentBpm = (int)note.Value;
                    }
                    break;
                case BMSFile.Chart.Note.NoteType.EX_BPM:
                    if (note.Value != null)
                    {
                        CurrentBpm = ((Fraction)note.Value).ToDouble();
                    }
                    break;
                default:
                    throw new NotImplementedException();
                case BMSFile.Chart.Note.NoteType.STOP:
                    break;
            }
        }
        StopTime = TimeSpan.Zero;
        if (lastCtrlNote != null && lastCtrlNote.Type == BMSFile.Chart.Note.NoteType.STOP && lastCtrlNote.Value is TimeSpan)
        {
            TimeSpan timeSpan = lastCtrlNote.AbsoluteTime + (TimeSpan)lastCtrlNote.Value;
            if (currentTime < timeSpan)
            {
                StopTime = timeSpan - currentTime;
            }
        }
    }

    protected virtual void ForwardBgaNotesToCurrentTime()
    {
        foreach (BMSFile.Chart.Note item in BgaBaseNotesQueue.DequeWhile(NoteQueue.QueueType.NOTE, n => n.AbsoluteTime <= currentTime))
        {
            TImageLoader val = ImageLoaders[item.Index];
            if (val != BgaBaseLoader)
            {
                BgaBaseLoader?.Detach();
                BgaBaseLoader = val;
                BgaBaseLoader?.Attach();
            }
        }
        foreach (BMSFile.Chart.Note item2 in BgaPoorNotesQueue.DequeWhile(NoteQueue.QueueType.NOTE, n => n.AbsoluteTime <= currentTime))
        {
            TImageLoader val2 = ImageLoaders[item2.Index];
            if (val2 != BgaPoorLoader)
            {
                BgaPoorLoader?.Detach();
                BgaPoorLoader = val2;
                BgaPoorLoader?.Attach();
            }
        }
        foreach (BMSFile.Chart.Note item3 in BgaLayerNotesQueue.DequeWhile(NoteQueue.QueueType.NOTE, n => n.AbsoluteTime <= currentTime))
        {
            TImageLoader val3 = ImageLoaders[item3.Index];
            if (val3 != BgaLayerLoader)
            {
                BgaLayerLoader?.Detach();
                BgaLayerLoader = val3;
                BgaLayerLoader?.Attach();
            }
        }
    }

    protected virtual void ForwardVisibleNotesToCurrentTime()
    {
        foreach (NoteQueue item in VisibleNotes1PQueue)
        {
            foreach (BMSFile.Chart.Note item2 in item.DequeWhile(NoteQueue.QueueType.NOTE, n => n.AbsoluteTime <= currentTime))
            {
                _ = item2;
            }
        }
        foreach (NoteQueue item3 in VisibleNotes2PQueue)
        {
            foreach (BMSFile.Chart.Note item4 in item3.DequeWhile(NoteQueue.QueueType.NOTE, n => n.AbsoluteTime <= currentTime))
            {
                _ = item4;
            }
        }
        foreach (NoteQueue item5 in VisibleNotes1PQueue)
        {
            foreach (BMSFile.Chart.Note item6 in item5.DequeWhile(NoteQueue.QueueType.STATICS, n => n.AbsoluteTime < currentTime - densityRange))
            {
                _ = item6;
            }
        }
        foreach (NoteQueue item7 in VisibleNotes2PQueue)
        {
            foreach (BMSFile.Chart.Note item8 in item7.DequeWhile(NoteQueue.QueueType.STATICS, n => n.AbsoluteTime < currentTime - densityRange))
            {
                _ = item8;
            }
        }
    }

    protected virtual void ForwardInvisibleNotesToCurrentTime()
    {
        foreach (NoteQueue item in InvisibleNotes1PQueue)
        {
            foreach (BMSFile.Chart.Note item2 in item.DequeWhile(NoteQueue.QueueType.NOTE, n => n.AbsoluteTime <= currentTime))
            {
                _ = item2;
            }
        }
        foreach (NoteQueue item3 in InvisibleNotes2PQueue)
        {
            foreach (BMSFile.Chart.Note item4 in item3.DequeWhile(NoteQueue.QueueType.NOTE, n => n.AbsoluteTime <= currentTime))
            {
                _ = item4;
            }
        }
    }

    protected virtual void ForwardLongNotesToCurrentTime()
    {
        foreach (NoteQueue item in LongNotes1PQueue)
        {
            foreach (BMSFile.Chart.Note item2 in item.DequeWhile(NoteQueue.QueueType.NOTE, n => n.AbsoluteTime <= currentTime))
            {
                if (((uint)item2.Type & 0xFFFFFFF0u) == 80)
                {
                    _ = item2;
                }
            }
        }
        foreach (NoteQueue item3 in LongNotes2PQueue)
        {
            foreach (BMSFile.Chart.Note item4 in item3.DequeWhile(NoteQueue.QueueType.NOTE, n => n.AbsoluteTime <= currentTime))
            {
                if (((uint)item4.Type & 0xFFFFFFF0u) == 96)
                {
                    _ = item4;
                }
            }
        }
        foreach (NoteQueue item5 in LongNotes1PQueue)
        {
            foreach (BMSFile.Chart.Note item6 in item5.DequeWhile(NoteQueue.QueueType.STATICS, n => n.AbsoluteTime < currentTime - densityRange))
            {
                _ = item6;
            }
        }
        foreach (NoteQueue item7 in LongNotes2PQueue)
        {
            foreach (BMSFile.Chart.Note item8 in item7.DequeWhile(NoteQueue.QueueType.STATICS, n => n.AbsoluteTime < currentTime - densityRange))
            {
                _ = item8;
            }
        }
    }

    protected virtual void ForwardMineNotesToCurrentTime()
    {
        foreach (NoteQueue item in MineNotes1PQueue)
        {
            foreach (BMSFile.Chart.Note item2 in item.DequeWhile(NoteQueue.QueueType.NOTE, n => n.AbsoluteTime <= currentTime))
            {
                _ = item2;
            }
        }
        foreach (NoteQueue item3 in MineNotes2PQueue)
        {
            foreach (BMSFile.Chart.Note item4 in item3.DequeWhile(NoteQueue.QueueType.NOTE, n => n.AbsoluteTime <= currentTime))
            {
                _ = item4;
            }
        }
    }

    /// <summary>指定時刻の譜面・画面状態を構成し、再生中なら時計を再開します。</summary>
    protected virtual void MoveTo(TimeSpan time)
    {
        lock (playbackControlSync)
        {
            if (time > Duration)
            {
                time = Duration;
            }
            if (time < TimeSpan.Zero)
            {
                time = TimeSpan.Zero;
            }
            timer.Stop();
            ResetPlaybackState();
            currentTime = time;
            MoveControlNotesToCurrentTime();
            MoveBgaNotesToCurrentTime();
            MoveInvisibleNotesToCurrentTime();
            MoveLongNotesToCurrentTime();
            MoveMineNotesToCurrentTime();
            MoveVisibleNotesToCurrentTime();
            timerOffset = currentTime;
            timer.Reset();
            if (PlayState == PlayState.Playing)
            {
                timer.Start();
                SuspendLoaders();
            }
            NoteDensity = calculateNotesDensity();
            Combo = calculateCombo();
        }
    }

    protected virtual void MoveControlNotesToCurrentTime()
    {
        ForwardControlNotesToCurrentTime();
    }

    protected virtual void MoveBgaNotesToCurrentTime()
    {
        foreach (BMSFile.Chart.Note item in BgaBaseNotesQueue.DequeWhile(NoteQueue.QueueType.NOTE, n => n.AbsoluteTime <= currentTime))
        {
            TImageLoader val = ImageLoaders[item.Index];
            if (val == BgaBaseLoader)
            {
                continue;
            }
            BgaBaseLoader?.Detach();
            BgaBaseLoader = val;
            if (BgaBaseLoader != null)
            {
                TimeSpan timeSpan = currentTime - item.AbsoluteTime;
                if (!(BgaBaseLoader.Duration <= timeSpan))
                {
                    BgaBaseLoader.CurrentTime = timeSpan;
                    BgaBaseLoader.Attach();
                }
            }
        }
        foreach (BMSFile.Chart.Note item2 in BgaPoorNotesQueue.DequeWhile(NoteQueue.QueueType.NOTE, n => n.AbsoluteTime <= currentTime))
        {
            TImageLoader val2 = ImageLoaders[item2.Index];
            if (val2 == BgaPoorLoader)
            {
                continue;
            }
            BgaPoorLoader?.Detach();
            BgaPoorLoader = val2;
            if (BgaPoorLoader != null)
            {
                TimeSpan timeSpan2 = currentTime - item2.AbsoluteTime;
                if (!(BgaPoorLoader.Duration <= timeSpan2))
                {
                    BgaPoorLoader.CurrentTime = timeSpan2;
                    BgaPoorLoader.Attach();
                }
            }
        }
        foreach (BMSFile.Chart.Note item3 in BgaLayerNotesQueue.DequeWhile(NoteQueue.QueueType.NOTE, n => n.AbsoluteTime <= currentTime))
        {
            TImageLoader val3 = ImageLoaders[item3.Index];
            if (val3 == BgaLayerLoader)
            {
                continue;
            }
            BgaLayerLoader?.Detach();
            BgaLayerLoader = val3;
            if (BgaLayerLoader != null)
            {
                TimeSpan timeSpan3 = currentTime - item3.AbsoluteTime;
                if (!(BgaLayerLoader.Duration <= timeSpan3))
                {
                    BgaLayerLoader.CurrentTime = timeSpan3;
                    BgaLayerLoader.Attach();
                }
            }
        }
    }

    protected virtual void MoveInvisibleNotesToCurrentTime()
    {
        ForwardInvisibleNotesToCurrentTime();
    }

    protected virtual void MoveLongNotesToCurrentTime()
    {
        foreach (NoteQueue item in LongNotes1PQueue)
        {
            foreach (BMSFile.Chart.Note item2 in item.DequeWhile(NoteQueue.QueueType.NOTE, n => n.AbsoluteTime <= currentTime))
            {
                if (((uint)item2.Type & 0xFFFFFFF0u) != 80)
                {
                    continue;
                }
                _ = item2;
            }
        }
        foreach (NoteQueue item3 in LongNotes2PQueue)
        {
            foreach (BMSFile.Chart.Note item4 in item3.DequeWhile(NoteQueue.QueueType.NOTE, n => n.AbsoluteTime <= currentTime))
            {
                if (((uint)item4.Type & 0xFFFFFFF0u) != 96)
                {
                    continue;
                }
                _ = item4;
            }
        }
        foreach (NoteQueue item5 in LongNotes1PQueue)
        {
            foreach (BMSFile.Chart.Note item6 in item5.DequeWhile(NoteQueue.QueueType.STATICS, n => n.AbsoluteTime < currentTime - densityRange))
            {
                _ = item6;
            }
        }
        foreach (NoteQueue item7 in LongNotes2PQueue)
        {
            foreach (BMSFile.Chart.Note item8 in item7.DequeWhile(NoteQueue.QueueType.STATICS, n => n.AbsoluteTime < currentTime - densityRange))
            {
                _ = item8;
            }
        }
    }

    protected virtual void MoveMineNotesToCurrentTime()
    {
        ForwardMineNotesToCurrentTime();
    }

    protected virtual void MoveVisibleNotesToCurrentTime()
    {
        foreach (NoteQueue item in VisibleNotes1PQueue)
        {
            foreach (BMSFile.Chart.Note item2 in item.DequeWhile(NoteQueue.QueueType.NOTE, n => n.AbsoluteTime <= currentTime))
            {
                _ = item2;
            }
        }
        foreach (NoteQueue item3 in VisibleNotes2PQueue)
        {
            foreach (BMSFile.Chart.Note item4 in item3.DequeWhile(NoteQueue.QueueType.NOTE, n => n.AbsoluteTime <= currentTime))
            {
                _ = item4;
            }
        }
        foreach (NoteQueue item5 in VisibleNotes1PQueue)
        {
            foreach (BMSFile.Chart.Note item6 in item5.DequeWhile(NoteQueue.QueueType.STATICS, n => n.AbsoluteTime < currentTime - densityRange))
            {
                _ = item6;
            }
        }
        foreach (NoteQueue item7 in VisibleNotes2PQueue)
        {
            foreach (BMSFile.Chart.Note item8 in item7.DequeWhile(NoteQueue.QueueType.STATICS, n => n.AbsoluteTime < currentTime - densityRange))
            {
                _ = item8;
            }
        }
    }

    /// <summary>再生を取り消し、再生Taskと機能固有の後処理が完了するまで合流します。</summary>
    public virtual void Stop()
    {
        Task playbackCompletion;
        bool resetPlaybackState;
        lock (playbackControlSync)
        {
            resetPlaybackState = PlayState != PlayState.Stopped;
            if (resetPlaybackState)
            {
                taskTokenSource?.Cancel();
            }
            playbackCompletion = playTask;
        }

        Exception completionFailure = null;
        try
        {
            // 再生Taskは後処理を含む。制御lock外で合流し、lock内の終了処理を妨げない。
            playbackCompletion?.Wait();
            if (resetPlaybackState)
            {
                NLogWrapper.TraceLogger?.Info("BMSPlayer stopped but task did not start");
            }
        }
        catch (Exception ex)
        {
            Exception taskFailure = UnwrapTaskWaitFailure(ex);
            if (IsOnlyCancellation(taskFailure))
            {
                NLogWrapper.TraceLogger?.Info("BMSPlayer stopped and task cancelled : " + ex);
            }
            else
            {
                completionFailure = taskFailure;
            }
        }

        if (resetPlaybackState)
        {
            lock (playbackControlSync)
            {
                ResetPlaybackState();
            }
        }

        if (completionFailure != null)
        {
            ExceptionDispatchInfo.Capture(completionFailure).Throw();
        }
    }

    protected virtual void ResetPlaybackState()
    {
        currentTime = TimeSpan.Zero;
        ControlNotesQueue.Reset();
        BgaBaseNotesQueue.Reset();
        BgaPoorNotesQueue.Reset();
        BgaLayerNotesQueue.Reset();
        foreach (NoteQueue item in VisibleNotes1PQueue)
        {
            item.Reset();
        }
        foreach (NoteQueue item2 in VisibleNotes2PQueue)
        {
            item2.Reset();
        }
        foreach (NoteQueue item3 in InvisibleNotes1PQueue)
        {
            item3.Reset();
        }
        foreach (NoteQueue item4 in InvisibleNotes2PQueue)
        {
            item4.Reset();
        }
        foreach (NoteQueue item5 in LongNotes1PQueue)
        {
            item5.Reset();
        }
        foreach (NoteQueue item6 in LongNotes2PQueue)
        {
            item6.Reset();
        }
        foreach (NoteQueue item7 in MineNotes1PQueue)
        {
            item7.Reset();
        }
        foreach (NoteQueue item8 in MineNotes2PQueue)
        {
            item8.Reset();
        }
        foreach (TImageLoader imageLoader in ImageLoaders)
        {
            imageLoader?.Detach();
        }
        BgaBaseLoader?.Detach();
        BgaPoorLoader?.Detach();
        BgaLayerLoader?.Detach();
        StagefileLoader?.Detach();
        BannerLoader?.Detach();
        BackbmpLoader?.Detach();
        NoteDensity = 0.0;
        CurrentBpm = Bms.Bpm?.ToDouble() ?? 0.0;
        StopTime = TimeSpan.Zero;
        CurrentMeasure = 0;
        lastCtrlNote = null;
        CurrentBpm = Bms.Bpm?.ToDouble() ?? 0.0;
    }

    public virtual void Pause()
    {
        lock (playbackControlSync)
        {
            if (PlayState == PlayState.Stopped)
            {
                return;
            }
            if (PlayState == PlayState.Playing)
            {
                timer.Stop();
                PlayState = PlayState.Paused;
            }
            else
            {
                timer.Start();
                PlayState = PlayState.Playing;
            }
            SuspendLoaders();
        }
    }

    private void SuspendLoaders()
    {
        foreach (TImageLoader imageLoader in ImageLoaders)
        {
            imageLoader?.Suspend();
        }
        BgaBaseLoader?.Suspend();
        BgaPoorLoader?.Suspend();
        BgaLayerLoader?.Suspend();
        StagefileLoader?.Suspend();
        BannerLoader?.Suspend();
        BackbmpLoader?.Suspend();
    }

    private void ResetTimer()
    {
        switch (PlayState)
        {
            case PlayState.Playing:
                timerOffset = currentTime;
                timer.Restart();
                break;
            case PlayState.Paused:
                timerOffset = currentTime;
                timer.Reset();
                break;
            case PlayState.Stopped:
                timerOffset = TimeSpan.Zero;
                timer.Stop();
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    /// <summary>再生を開始し、再生・機能固有の後処理の両方が終わった時点で完了します。</summary>
    public virtual async Task Start()
    {
        Task playbackCompletion;
        lock (playbackControlSync)
        {
            if (PlayState != PlayState.Stopped)
            {
                return;
            }

            try
            {
                playbackControlFailure = null;
                OnPlaybackStarting();
                PlayState = PlayState.Playing;
                taskTokenSource = new CancellationTokenSource();
                CancellationToken token = taskTokenSource.Token;
                // tokenはTaskの開始条件にせず、開始前取消でも本体の後処理を必ず通します。
                playbackCompletion = new Task(() => RunPlaybackAndCleanup(token));
                playTask = playbackCompletion;
                playbackCompletion.Start();
            }
            catch (Exception playbackFailure)
            {
                playTask = null;
                Exception stoppingFailure = null;
                try
                {
                    OnPlaybackStopping();
                }
                catch (Exception exception)
                {
                    stoppingFailure = exception;
                }
                finally
                {
                    PlayState = PlayState.Stopped;
                    ResetTimer();
                }

                if (stoppingFailure != null)
                {
                    throw new AggregateException(
                        "BMS playback failed and playback cleanup also failed.",
                        playbackFailure,
                        stoppingFailure);
                }

                ExceptionDispatchInfo.Capture(playbackFailure).Throw();
                throw;
            }
        }

        await playbackCompletion;
    }

    private void RunPlaybackAndCleanup(CancellationToken token)
    {
        ExceptionDispatchInfo playbackFailure = null;
        Exception stoppingFailure = null;
        bool completed = false;
        bool timerStarted = false;
        while (!completed)
        {
            lock (playbackControlSync)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (PlayState == PlayState.Playing)
                    {
                        if (!timerStarted)
                        {
                            timer.Restart();
                            timerStarted = true;
                        }

                        TimeSpan wallClockTime = timerOffset
                            + TimeSpan.FromTicks((long)((float)timer.Elapsed.Ticks * playbackRate));
                        PlaybackTickResult tick = OnPlaybackTick(wallClockTime);
                        ForwardTo(tick.Time);
                        completed = tick.Completed;
                    }
                }
                catch (OperationCanceledException exception)
                {
                    NLogWrapper.TraceLogger?.Info("BMSPlayer play cancelled");
                    playbackFailure = playbackControlFailure ?? ExceptionDispatchInfo.Capture(exception);
                    playbackControlFailure = null;
                    completed = true;
                }
                catch (Exception exception)
                {
                    playbackFailure = ExceptionDispatchInfo.Capture(exception);
                    completed = true;
                }

                if (completed)
                {
                    try
                    {
                        OnPlaybackStopping();
                    }
                    catch (Exception exception)
                    {
                        stoppingFailure = exception;
                    }
                    finally
                    {
                        PlayState = PlayState.Stopped;
                        ResetTimer();
                    }
                }
            }

            if (!completed)
            {
                Thread.Sleep(1);
            }
        }

        ThrowPlaybackFailures(playbackFailure, stoppingFailure);
    }

    private static void ThrowPlaybackFailures(
        ExceptionDispatchInfo playbackFailure,
        Exception stoppingFailure)
    {
        if (playbackFailure != null)
        {
            if (stoppingFailure != null)
            {
                throw new AggregateException(
                    "BMS playback failed and playback cleanup also failed.",
                    playbackFailure.SourceException,
                    stoppingFailure);
            }

            playbackFailure.Throw();
        }
        if (stoppingFailure != null)
        {
            ExceptionDispatchInfo.Capture(stoppingFailure).Throw();
        }
    }

    /// <summary>再生Task開始前に、機能固有の再生時計と処理資源を準備します。</summary>
    protected virtual void OnPlaybackStarting()
    {
    }

    /// <summary>一回の制御tickで再生時刻と音声側の完了状態を取得します。</summary>
    protected virtual PlaybackTickResult OnPlaybackTick(TimeSpan wallClockTime) =>
        new(wallClockTime, wallClockTime >= Duration);

    /// <summary>再生ループ終了後、再生Taskが完了する前に機能固有の資源を回収します。</summary>
    protected virtual void OnPlaybackStopping()
    {
    }

    /// <summary>制御操作の失敗を再生Taskの終了所有者へ渡し、通常の後処理へ合流させます。</summary>
    /// <param name="exception">制御操作で発生し、再生Taskへ公開する主失敗。</param>
    protected void FailPlayback(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (playbackControlSync)
        {
            if (PlayState == PlayState.Stopped || playTask == null || playTask.IsCompleted)
            {
                return;
            }

            playbackControlFailure ??= ExceptionDispatchInfo.Capture(exception);
            taskTokenSource?.Cancel();
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        Task playbackCompletion = null;
        if (disposing)
        {
            lock (playbackControlSync)
            {
                if (disposedValue)
                {
                    return;
                }
                taskTokenSource?.Cancel();
                playbackCompletion = playTask;
            }

            try
            {
                // 終了callbackが制御lockを必要とするため、Taskの合流はlock外で行います。
                playbackCompletion?.Wait();
                NLogWrapper.TraceLogger?.Info("BMSPlayer disposed but task did not start");
            }
            catch (Exception ex)
            {
                NLogWrapper.TraceLogger?.Info("BMSPlayer disposed and task stopped: " + ex);
            }
        }

        lock (playbackControlSync)
        {
            if (disposedValue)
            {
                return;
            }
            if (disposing)
            {
                foreach (TImageLoader imageLoader in ImageLoaders)
                {
                    imageLoader?.Dispose();
                }
                BgaBaseLoader?.Dispose();
                BgaPoorLoader?.Dispose();
                BgaLayerLoader?.Dispose();
                StagefileLoader?.Dispose();
                BannerLoader?.Dispose();
                BackbmpLoader?.Dispose();
            }
            ImageLoaders = null;
            BgaBaseLoader = null;
            BgaPoorLoader = null;
            BgaLayerLoader = null;
            StagefileLoader = null;
            BannerLoader = null;
            BackbmpLoader = null;
            disposedValue = true;
        }
    }

    private static Exception UnwrapTaskWaitFailure(Exception exception) =>
        exception is AggregateException { InnerExceptions.Count: 1 } aggregate
            ? aggregate.InnerExceptions[0]
            : exception;

    private static bool IsOnlyCancellation(Exception exception)
    {
        IReadOnlyList<Exception> failures = exception is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions
            : [exception];
        return failures.Count > 0 && failures.All(failure => failure is OperationCanceledException);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
    }
}
