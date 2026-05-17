using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ribbit.Logging;
using Ribbit.Math;
using Ribbit.Media;

namespace Ribbit.BMS;

public abstract class BMSPlayer<TAudioPlayer, TImageLoader> : IDisposable where TAudioPlayer : class, IAudioPlayer where TImageLoader : class, IImageLoader
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
            this.notes = notes.ToArray();
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

    private TimeSpan musicDuration;

    private float playbackRate = 1f;

    private TimeSpan bgaDuration;

    protected Func<TimeSpan> durationProvider;

    private double noteDensity;

    private BMSFile.Chart.Note lastCtrlNote;

    private TimeSpan densityRange = new TimeSpan(0, 0, 0, 1);

    protected readonly NoteQueue ControlNotesQueue;

    protected readonly NoteQueue BgaBaseNotesQueue;

    protected readonly NoteQueue BgaPoorNotesQueue;

    protected readonly NoteQueue BgaLayerNotesQueue;

    protected readonly NoteQueue BgmNotesQueue;

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
            return playbackRate;
        }
        set
        {
            if (playbackRate != value && !(value <= 0f))
            {
                playbackRate = value;
                ResetTimer();
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

    protected ReadOnlyCollection<TAudioPlayer> AudioPlayers { get; set; } = new List<TAudioPlayer>().AsReadOnly();

    protected ReadOnlyCollection<TImageLoader> ImageLoaders { get; set; } = new List<TImageLoader>().AsReadOnly();

    protected TImageLoader BgaBaseLoader { get; set; }

    protected TImageLoader BgaPoorLoader { get; set; }

    protected TImageLoader BgaLayerLoader { get; set; }

    protected TImageLoader StagefileLoader { get; set; }

    protected TImageLoader BannerLoader { get; set; }

    protected TImageLoader BackbmpLoader { get; set; }

    public PlayState PlayState { get; private set; } = PlayState.Stopped;

    protected BMSPlayer()
    {
    }

    public BMSPlayer(BMSFile bms)
    {
        if (bms == null)
        {
            throw new ArgumentNullException("bms");
        }
        Bms = bms;
        ControlNotesQueue = new NoteQueue(Bms.Measures.ControlNotes);
        BgaBaseNotesQueue = new NoteQueue(Bms.Measures.BgaBaseNotes);
        BgaPoorNotesQueue = new NoteQueue(Bms.Measures.BgaPoorNotes);
        BgaLayerNotesQueue = new NoteQueue(Bms.Measures.BgaLayerNotes);
        BgmNotesQueue = new NoteQueue(Bms.Measures.BgmNotes);
        VisibleNotes1PQueue = Bms.Measures.VisibleNotes1P.Select((BMSFile.Measure.AllNotes a) => new NoteQueue(a)).ToList().AsReadOnly();
        VisibleNotes2PQueue = Bms.Measures.VisibleNotes2P.Select((BMSFile.Measure.AllNotes a) => new NoteQueue(a)).ToList().AsReadOnly();
        InvisibleNotes1PQueue = Bms.Measures.InvisibleNotes1P.Select((BMSFile.Measure.AllNotes a) => new NoteQueue(a)).ToList().AsReadOnly();
        InvisibleNotes2PQueue = Bms.Measures.InvisibleNotes2P.Select((BMSFile.Measure.AllNotes a) => new NoteQueue(a)).ToList().AsReadOnly();
        LongNotes1PQueue = Bms.Measures.LongNotes1P.Select((BMSFile.Measure.AllNotes a) => new NoteQueue(a)).ToList().AsReadOnly();
        LongNotes2PQueue = Bms.Measures.LongNotes2P.Select((BMSFile.Measure.AllNotes a) => new NoteQueue(a)).ToList().AsReadOnly();
        MineNotes1PQueue = Bms.Measures.MineNotes1P.Select((BMSFile.Measure.AllNotes a) => new NoteQueue(a)).ToList().AsReadOnly();
        MineNotes2PQueue = Bms.Measures.MineNotes2P.Select((BMSFile.Measure.AllNotes a) => new NoteQueue(a)).ToList().AsReadOnly();
        InitializeLoaders();
        currentTime = TimeSpan.Zero;
        durationProvider = () => TimeSpan.FromTicks(System.Math.Max(BmsDuration.Ticks, System.Math.Max(BgaDuration.Ticks, MusicDuration.Ticks)));
        MusicDuration = TimeSpan.Zero;
        BgaDuration = TimeSpan.Zero;
        CurrentBpm = Bms.Bpm?.ToDouble() ?? 0.0;
    }

    private double calculateNotesDensity()
    {
        return (double)(VisibleNotes1PQueue.Sum((NoteQueue q) => q.CountDequeued(NoteQueue.QueueType.NOTE) - q.CountDequeued(NoteQueue.QueueType.STATICS)) + VisibleNotes2PQueue.Sum((NoteQueue q) => q.CountDequeued(NoteQueue.QueueType.NOTE) - q.CountDequeued(NoteQueue.QueueType.STATICS)) + LongNotes1PQueue.Sum((NoteQueue q) => q.CountDequeued(NoteQueue.QueueType.NOTE) - q.CountDequeued(NoteQueue.QueueType.STATICS)) + LongNotes2PQueue.Sum((NoteQueue q) => q.CountDequeued(NoteQueue.QueueType.NOTE) - q.CountDequeued(NoteQueue.QueueType.STATICS))) / densityRange.TotalSeconds;
    }

    private int calculateCombo()
    {
        return VisibleNotes1PQueue.Sum((NoteQueue q) => q.CountDequeued(NoteQueue.QueueType.NOTE)) + VisibleNotes2PQueue.Sum((NoteQueue q) => q.CountDequeued(NoteQueue.QueueType.NOTE)) + LongNotes1PQueue.Sum((NoteQueue q) => q.CountDequeued(NoteQueue.QueueType.NOTE)) + LongNotes2PQueue.Sum((NoteQueue q) => q.CountDequeued(NoteQueue.QueueType.NOTE));
    }

    protected virtual void InitializeLoaders()
    {
        AudioPlayers = new TAudioPlayer[Bms.WavArray.Length].ToList().AsReadOnly();
        ImageLoaders = new TImageLoader[Bms.BmpArray.Length].ToList().AsReadOnly();
        BgaBaseLoader = null;
        BgaPoorLoader = null;
        BgaLayerLoader = null;
        StagefileLoader = null;
        BannerLoader = null;
        BackbmpLoader = null;
    }

    public abstract void LoadResources();

    protected void ForwardTo(TimeSpan time)
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
            ForwardBgmNotesToCurrentTime();
            ForwardInvisibleNotesToCurrentTime();
            ForwardLongNotesToCurrentTime();
            ForwardMineNotesToCurrentTime();
            ForwardVisibleNotesToCurrentTime();
            NoteDensity = calculateNotesDensity();
            Combo = calculateCombo();
        }
    }

    protected virtual void ForwardControlNotesToCurrentTime()
    {
        foreach (BMSFile.Chart.Note item in ControlNotesQueue.DequeWhile(NoteQueue.QueueType.NOTE, (BMSFile.Chart.Note n) => n.AbsoluteTime <= currentTime))
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
        foreach (BMSFile.Chart.Note item in BgaBaseNotesQueue.DequeWhile(NoteQueue.QueueType.NOTE, (BMSFile.Chart.Note n) => n.AbsoluteTime <= currentTime))
        {
            TImageLoader val = ImageLoaders[item.Index];
            if (val != BgaBaseLoader)
            {
                BgaBaseLoader?.Detach();
                BgaBaseLoader = val;
                BgaBaseLoader?.Attach();
            }
        }
        foreach (BMSFile.Chart.Note item2 in BgaPoorNotesQueue.DequeWhile(NoteQueue.QueueType.NOTE, (BMSFile.Chart.Note n) => n.AbsoluteTime <= currentTime))
        {
            TImageLoader val2 = ImageLoaders[item2.Index];
            if (val2 != BgaPoorLoader)
            {
                BgaPoorLoader?.Detach();
                BgaPoorLoader = val2;
                BgaPoorLoader?.Attach();
            }
        }
        foreach (BMSFile.Chart.Note item3 in BgaLayerNotesQueue.DequeWhile(NoteQueue.QueueType.NOTE, (BMSFile.Chart.Note n) => n.AbsoluteTime <= currentTime))
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

    protected virtual void ForwardBgmNotesToCurrentTime()
    {
        foreach (BMSFile.Chart.Note item in BgmNotesQueue.DequeWhile(NoteQueue.QueueType.NOTE, (BMSFile.Chart.Note n) => n.AbsoluteTime <= currentTime))
        {
            AudioPlayers[item.Index]?.Play();
        }
    }

    protected virtual void ForwardVisibleNotesToCurrentTime()
    {
        foreach (NoteQueue item in VisibleNotes1PQueue)
        {
            foreach (BMSFile.Chart.Note item2 in item.DequeWhile(NoteQueue.QueueType.NOTE, (BMSFile.Chart.Note n) => n.AbsoluteTime <= currentTime))
            {
                AudioPlayers[item2.Index]?.Play();
            }
        }
        foreach (NoteQueue item3 in VisibleNotes2PQueue)
        {
            foreach (BMSFile.Chart.Note item4 in item3.DequeWhile(NoteQueue.QueueType.NOTE, (BMSFile.Chart.Note n) => n.AbsoluteTime <= currentTime))
            {
                AudioPlayers[item4.Index]?.Play();
            }
        }
        foreach (NoteQueue item5 in VisibleNotes1PQueue)
        {
            foreach (BMSFile.Chart.Note item6 in item5.DequeWhile(NoteQueue.QueueType.STATICS, (BMSFile.Chart.Note n) => n.AbsoluteTime < currentTime - densityRange))
            {
                _ = item6;
            }
        }
        foreach (NoteQueue item7 in VisibleNotes2PQueue)
        {
            foreach (BMSFile.Chart.Note item8 in item7.DequeWhile(NoteQueue.QueueType.STATICS, (BMSFile.Chart.Note n) => n.AbsoluteTime < currentTime - densityRange))
            {
                _ = item8;
            }
        }
    }

    protected virtual void ForwardInvisibleNotesToCurrentTime()
    {
        foreach (NoteQueue item in InvisibleNotes1PQueue)
        {
            foreach (BMSFile.Chart.Note item2 in item.DequeWhile(NoteQueue.QueueType.NOTE, (BMSFile.Chart.Note n) => n.AbsoluteTime <= currentTime))
            {
                _ = item2;
            }
        }
        foreach (NoteQueue item3 in InvisibleNotes2PQueue)
        {
            foreach (BMSFile.Chart.Note item4 in item3.DequeWhile(NoteQueue.QueueType.NOTE, (BMSFile.Chart.Note n) => n.AbsoluteTime <= currentTime))
            {
                _ = item4;
            }
        }
    }

    protected virtual void ForwardLongNotesToCurrentTime()
    {
        foreach (NoteQueue item in LongNotes1PQueue)
        {
            foreach (BMSFile.Chart.Note item2 in item.DequeWhile(NoteQueue.QueueType.NOTE, (BMSFile.Chart.Note n) => n.AbsoluteTime <= currentTime))
            {
                if (((uint)item2.Type & 0xFFFFFFF0u) == 80)
                {
                    AudioPlayers[item2.Index]?.Play();
                }
            }
        }
        foreach (NoteQueue item3 in LongNotes2PQueue)
        {
            foreach (BMSFile.Chart.Note item4 in item3.DequeWhile(NoteQueue.QueueType.NOTE, (BMSFile.Chart.Note n) => n.AbsoluteTime <= currentTime))
            {
                if (((uint)item4.Type & 0xFFFFFFF0u) == 96)
                {
                    AudioPlayers[item4.Index]?.Play();
                }
            }
        }
        foreach (NoteQueue item5 in LongNotes1PQueue)
        {
            foreach (BMSFile.Chart.Note item6 in item5.DequeWhile(NoteQueue.QueueType.STATICS, (BMSFile.Chart.Note n) => n.AbsoluteTime < currentTime - densityRange))
            {
                _ = item6;
            }
        }
        foreach (NoteQueue item7 in LongNotes2PQueue)
        {
            foreach (BMSFile.Chart.Note item8 in item7.DequeWhile(NoteQueue.QueueType.STATICS, (BMSFile.Chart.Note n) => n.AbsoluteTime < currentTime - densityRange))
            {
                _ = item8;
            }
        }
    }

    protected virtual void ForwardMineNotesToCurrentTime()
    {
        foreach (NoteQueue item in MineNotes1PQueue)
        {
            foreach (BMSFile.Chart.Note item2 in item.DequeWhile(NoteQueue.QueueType.NOTE, (BMSFile.Chart.Note n) => n.AbsoluteTime <= currentTime))
            {
                _ = item2;
            }
        }
        foreach (NoteQueue item3 in MineNotes2PQueue)
        {
            foreach (BMSFile.Chart.Note item4 in item3.DequeWhile(NoteQueue.QueueType.NOTE, (BMSFile.Chart.Note n) => n.AbsoluteTime <= currentTime))
            {
                _ = item4;
            }
        }
    }

    protected void MoveTo(TimeSpan time)
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
        MoveBgmNotesToCurrentTime();
        MoveInvisibleNotesToCurrentTime();
        MoveLongNotesToCurrentTime();
        MoveMineNotesToCurrentTime();
        MoveVisibleNotesToCurrentTime();
        timerOffset = currentTime;
        timer.Reset();
        if (PlayState == PlayState.Playing)
        {
            PlayState = PlayState.Paused;
            Pause();
        }
        NoteDensity = calculateNotesDensity();
        Combo = calculateCombo();
    }

    protected virtual void MoveControlNotesToCurrentTime()
    {
        ForwardControlNotesToCurrentTime();
    }

    protected virtual void MoveBgaNotesToCurrentTime()
    {
        foreach (BMSFile.Chart.Note item in BgaBaseNotesQueue.DequeWhile(NoteQueue.QueueType.NOTE, (BMSFile.Chart.Note n) => n.AbsoluteTime <= currentTime))
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
        foreach (BMSFile.Chart.Note item2 in BgaPoorNotesQueue.DequeWhile(NoteQueue.QueueType.NOTE, (BMSFile.Chart.Note n) => n.AbsoluteTime <= currentTime))
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
        foreach (BMSFile.Chart.Note item3 in BgaLayerNotesQueue.DequeWhile(NoteQueue.QueueType.NOTE, (BMSFile.Chart.Note n) => n.AbsoluteTime <= currentTime))
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

    protected virtual void MoveBgmNotesToCurrentTime()
    {
        foreach (BMSFile.Chart.Note item in BgmNotesQueue.DequeWhile(NoteQueue.QueueType.NOTE, (BMSFile.Chart.Note n) => n.AbsoluteTime <= currentTime))
        {
            TAudioPlayer val = AudioPlayers[item.Index];
            if (val != null)
            {
                TimeSpan timeSpan = currentTime - item.AbsoluteTime;
                if (!(val.Duration <= timeSpan))
                {
                    val.CurrentTime = timeSpan;
                    val.Play(PlayWith.PAUSE);
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
            foreach (BMSFile.Chart.Note item2 in item.DequeWhile(NoteQueue.QueueType.NOTE, (BMSFile.Chart.Note n) => n.AbsoluteTime <= currentTime))
            {
                if (((uint)item2.Type & 0xFFFFFFF0u) != 80)
                {
                    continue;
                }
                TAudioPlayer val = AudioPlayers[item2.Index];
                if (val != null)
                {
                    TimeSpan timeSpan = currentTime - item2.AbsoluteTime;
                    if (!(val.Duration <= timeSpan))
                    {
                        val.CurrentTime = timeSpan;
                        val.Play(PlayWith.PAUSE);
                    }
                }
            }
        }
        foreach (NoteQueue item3 in LongNotes2PQueue)
        {
            foreach (BMSFile.Chart.Note item4 in item3.DequeWhile(NoteQueue.QueueType.NOTE, (BMSFile.Chart.Note n) => n.AbsoluteTime <= currentTime))
            {
                if (((uint)item4.Type & 0xFFFFFFF0u) != 96)
                {
                    continue;
                }
                TAudioPlayer val2 = AudioPlayers[item4.Index];
                if (val2 != null)
                {
                    TimeSpan timeSpan2 = currentTime - item4.AbsoluteTime;
                    if (!(val2.Duration <= timeSpan2))
                    {
                        val2.CurrentTime = timeSpan2;
                        val2.Play(PlayWith.PAUSE);
                    }
                }
            }
        }
        foreach (NoteQueue item5 in LongNotes1PQueue)
        {
            foreach (BMSFile.Chart.Note item6 in item5.DequeWhile(NoteQueue.QueueType.STATICS, (BMSFile.Chart.Note n) => n.AbsoluteTime < currentTime - densityRange))
            {
                _ = item6;
            }
        }
        foreach (NoteQueue item7 in LongNotes2PQueue)
        {
            foreach (BMSFile.Chart.Note item8 in item7.DequeWhile(NoteQueue.QueueType.STATICS, (BMSFile.Chart.Note n) => n.AbsoluteTime < currentTime - densityRange))
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
            foreach (BMSFile.Chart.Note item2 in item.DequeWhile(NoteQueue.QueueType.NOTE, (BMSFile.Chart.Note n) => n.AbsoluteTime <= currentTime))
            {
                TAudioPlayer val = AudioPlayers[item2.Index];
                if (val != null)
                {
                    TimeSpan timeSpan = currentTime - item2.AbsoluteTime;
                    if (!(val.Duration <= timeSpan))
                    {
                        val.CurrentTime = timeSpan;
                        val.Play(PlayWith.PAUSE);
                    }
                }
            }
        }
        foreach (NoteQueue item3 in VisibleNotes2PQueue)
        {
            foreach (BMSFile.Chart.Note item4 in item3.DequeWhile(NoteQueue.QueueType.NOTE, (BMSFile.Chart.Note n) => n.AbsoluteTime <= currentTime))
            {
                TAudioPlayer val2 = AudioPlayers[item4.Index];
                if (val2 != null)
                {
                    TimeSpan timeSpan2 = currentTime - item4.AbsoluteTime;
                    if (!(val2.Duration <= timeSpan2))
                    {
                        val2.CurrentTime = timeSpan2;
                        val2.Play(PlayWith.PAUSE);
                    }
                }
            }
        }
        foreach (NoteQueue item5 in VisibleNotes1PQueue)
        {
            foreach (BMSFile.Chart.Note item6 in item5.DequeWhile(NoteQueue.QueueType.STATICS, (BMSFile.Chart.Note n) => n.AbsoluteTime < currentTime - densityRange))
            {
                _ = item6;
            }
        }
        foreach (NoteQueue item7 in VisibleNotes2PQueue)
        {
            foreach (BMSFile.Chart.Note item8 in item7.DequeWhile(NoteQueue.QueueType.STATICS, (BMSFile.Chart.Note n) => n.AbsoluteTime < currentTime - densityRange))
            {
                _ = item8;
            }
        }
    }

    public virtual void Stop()
    {
        if (PlayState != PlayState.Stopped)
        {
            try
            {
                taskTokenSource?.Cancel();
                playTask?.Wait();
                NLogWrapper.TraceLogger?.Info("BMSPlayer stopped but task did not start");
            }
            catch (Exception ex)
            {
                NLogWrapper.TraceLogger?.Info("BMSPlayer stopped and task cancelled : " + ex);
            }
            ResetPlaybackState();
        }
    }

    protected virtual void ResetPlaybackState()
    {
        currentTime = TimeSpan.Zero;
        ControlNotesQueue.Reset();
        BgaBaseNotesQueue.Reset();
        BgaPoorNotesQueue.Reset();
        BgaLayerNotesQueue.Reset();
        BgmNotesQueue.Reset();
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
        foreach (TAudioPlayer audioPlayer in AudioPlayers)
        {
            audioPlayer?.Stop();
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
        foreach (TAudioPlayer audioPlayer in AudioPlayers)
        {
            audioPlayer?.Pause();
        }
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

    public virtual async Task Start()
    {
        if (PlayState != PlayState.Stopped)
        {
            return;
        }
        PlayState = PlayState.Playing;
        taskTokenSource = new CancellationTokenSource();
        CancellationToken token = taskTokenSource.Token;
        playTask = new Task(delegate
        {
            timer.Restart();
            while (true)
            {
                if (timer.IsRunning)
                {
                    TimeSpan time = timerOffset + TimeSpan.FromTicks((long)((float)timer.Elapsed.Ticks * PlaybackRate));
                    ForwardTo(time);
                    if (currentTime >= Duration)
                    {
                        break;
                    }
                }
                token.ThrowIfCancellationRequested();
                Thread.Sleep(1);
            }
        }, token);
        try
        {
            playTask.Start();
            await playTask;
        }
        catch (OperationCanceledException)
        {
            NLogWrapper.TraceLogger?.Info("BMSPlayer play cancelled");
            throw;
        }
        finally
        {
            playTask = null;
            PlayState = PlayState.Stopped;
            ResetTimer();
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposedValue)
        {
            return;
        }
        if (disposing)
        {
            try
            {
                taskTokenSource?.Cancel();
                playTask?.Wait();
                NLogWrapper.TraceLogger?.Info("BMSPlayer disposed but task did not start");
            }
            catch (Exception ex)
            {
                NLogWrapper.TraceLogger?.Info("BMSPlayer disposed and task stopped: " + ex);
            }
            foreach (TAudioPlayer audioPlayer in AudioPlayers)
            {
                audioPlayer?.Dispose();
            }
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
        AudioPlayers = null;
        ImageLoaders = null;
        BgaBaseLoader = null;
        BgaPoorLoader = null;
        BgaLayerLoader = null;
        StagefileLoader = null;
        BannerLoader = null;
        BackbmpLoader = null;
        disposedValue = true;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
    }
}
