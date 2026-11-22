#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using ManagedBass;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace Ribbit.BMS;

/// <summary>絶対開始frame予約に失敗した音声イベントとnative診断を保持します。</summary>
internal sealed class BmsScheduledAudioException : InvalidOperationException
{
    /// <summary>失敗イベント、区間、native段階を一つの再生失敗として報告します。</summary>
    internal BmsScheduledAudioException(
        string stage,
        BmsAudioFrameEvent audioEvent,
        long segmentStartSongFrame,
        long segmentStartMixerFrame,
        long originMixerFrame,
        long expectedMixerFrame,
        long? currentMixerFrame,
        int sampleRate,
        int channelCount,
        int generation,
        Exception? innerException = null)
        : base(
            "BMS scheduled audio failed. stage=" + stage
            + " wavIndex=" + audioEvent.WavIndex
            + " absoluteTimeTicks=" + audioEvent.AbsoluteTime.Ticks
            + " songStartFrame=" + audioEvent.StartFrame
            + " segmentStartSongFrame=" + segmentStartSongFrame
            + " segmentStartMixerFrame=" + segmentStartMixerFrame
            + " originMixerFrame=" + originMixerFrame
            + " expectedMixerFrame=" + expectedMixerFrame
            + " currentMixerFrame=" + (currentMixerFrame?.ToString(CultureInfo.InvariantCulture) ?? "unknown")
            + " latenessFrames=" + (currentMixerFrame is long current
                ? System.Math.Max(0, current - expectedMixerFrame).ToString(CultureInfo.InvariantCulture)
                : "unknown")
            + " sampleRate=" + sampleRate
            + " channels=" + channelCount
            + " generation=" + generation,
            innerException)
    {
        Stage = stage;
        Event = audioEvent;
        SegmentStartSongFrame = segmentStartSongFrame;
        SegmentStartMixerFrame = segmentStartMixerFrame;
        OriginMixerFrame = originMixerFrame;
        ExpectedMixerFrame = expectedMixerFrame;
        CurrentMixerFrame = currentMixerFrame;
        SampleRate = sampleRate;
        ChannelCount = channelCount;
        Generation = generation;
    }

    /// <summary>nativeまたはschedule段階。</summary>
    internal string Stage { get; }

    /// <summary>失敗した変更不能な譜面イベント。</summary>
    internal BmsAudioFrameEvent Event { get; }

    /// <summary>この例外を組み立てた再生区間の開始song frame。</summary>
    internal long SegmentStartSongFrame { get; }

    /// <summary>再生区間開始song frameに対応するnative mixer frame。</summary>
    internal long SegmentStartMixerFrame { get; }

    /// <summary>曲frame 0に対応するnative mixer frame。</summary>
    internal long OriginMixerFrame { get; }

    /// <summary>予約対象のnative mixer frame。</summary>
    internal long ExpectedMixerFrame { get; }

    /// <summary>締切確認時のnative mixer frame。位置を観測できない失敗ではnull。</summary>
    internal long? CurrentMixerFrame { get; }

    /// <summary>input mixerの実効sample rate。</summary>
    internal int SampleRate { get; }

    /// <summary>Float32 input mixerの実効channel数。</summary>
    internal int ChannelCount { get; }

    /// <summary>失敗した再生区間世代。</summary>
    internal int Generation { get; }
}

/// <summary>リアルタイムの一回の予約処理に許す時間予算です。</summary>
/// <remarks>
/// 予約処理は再生制御のロックを保持して行われるため、準備が長引くと停止・次の再生・終了が待たされます。
/// 予算を超えた準備は途中で打ち切って失敗として扱います。音声変換(Writer)は時計も期限も持たないため、予算を適用しません。
/// </remarks>
/// <param name="GetTimestamp">Stopwatch tick単位の時計です。</param>
/// <param name="DeadlineTimestamp">この時刻を超えたら予算超過とみなすStopwatch tickです。</param>
internal readonly record struct BmsReservationBudget(Func<long> GetTimestamp, long DeadlineTimestamp)
{
    /// <summary>リアルタイム一回の予約処理に許す実時間(秒)です。</summary>
    internal const long BudgetSeconds = 1;

    /// <summary>現在時刻から実時間1秒の予算を開始します。</summary>
    internal static BmsReservationBudget Start(Func<long> getTimestamp) =>
        new(getTimestamp, checked(getTimestamp() + BudgetSeconds * Stopwatch.Frequency));

    /// <summary>予算を使い切ったか取得します。</summary>
    internal bool IsExceeded => GetTimestamp() > DeadlineTimestamp;
}

/// <summary>WriterとRealtimeが共有する有限発音区間の生成・予約・回収処理です。</summary>
/// <remarks>
/// 打切り後の実効出力長が短い発音は、<see cref="BmsDirectVoiceTable"/>の変換済み波形を一回の予約につき一つのブロックへ加算し、
/// ブロックを有限voiceとして予約します。対象外の発音は発音ごとのnative voiceのままです。
/// 発音数は予約済みの論理的な発音を標本点で数え、native voice数とは別に報告します。
/// </remarks>
internal sealed class BmsScheduledAudioMixer : IDisposable
{
    /// <summary>絶対位置予約するsourceに共通して指定するmixer channel flagsです。</summary>
    internal const BassFlags ScheduledSourceFlags =
        BassFlags.MixerChanAbsolute
        | BassFlags.MixerChanPause
        | BassFlags.MixerChanMatrix
        | BassFlags.MixerChanNoRampin;

    private const BassFlags PauseFlag = BassFlags.MixerChanPause;

    private const string DirectBlockSourceName = "BMS direct voice block";

    private readonly object sync = new();
    private readonly BmsAudioFrameSchedule schedule;
    private readonly IReadOnlyList<BmsAudioResource?> resourcesByIndex;
    private readonly BassAudioSession session;
    private readonly IBassScheduledMixerNativeBoundary native;
    private readonly BmsDirectVoiceTable? directVoices;
    private readonly List<ScheduledVoice> voices = [];
    // 予約済みの論理的な発音を、開始frame昇順の未開始queueと、終了frameのminimum heapに分けて持つ。
    // 予約は開始frameの昇順に確定するため、未開始queueの先頭から順に開始する。
    private readonly Queue<LogicalVoice> logicalPending = new();
    private readonly PriorityQueue<long, long> logicalActiveEnds = new();
    private int publishedLogicalVoiceCount;
    private long reservedLogicalVoiceCount;
    private readonly int mixerHandle;
    private readonly int sampleRate;
    private readonly int channelCount;
    private readonly long bytesPerFrame;
    private readonly long originMixerFrame;
    private long segmentStartSongFrame;
    private long segmentStartMixerFrame;
    private readonly Dictionary<uint, float[,]> matricesBySpeakerMask = [];
    private int nextEventIndex;
    private int generation;
    private int preparationFailureCount;
    private int commitFailureCount;
    private int preparationOperationCount;
    private int commitOperationCount;
    private int maximumPreparedVoiceCount;
    private int maximumReservedVoiceCount;
    private int maximumActiveVoiceCount;
    private int retiredVoiceCount;
    private long preparedVoiceCount;
    private long preparationTicks;
    private long minimumPreparationTicks = long.MaxValue;
    private long maximumPreparationTicks;
    private long commitTicks;
    private long maxCommitTicks;
    private long nativeLockTicks;
    private long maximumNativeLockTicks;
    private long minimumReservationMarginFrames = long.MaxValue;
    private bool disposed;

    /// <summary>譜面scheduleを実効native mixer形式へ接続します。</summary>
    /// <param name="schedule">実効mixer rateで量子化済みschedule。</param>
    /// <param name="resourcesByIndex">path共有decoded resource。</param>
    /// <param name="session">native mixerとsource寿命を所有するsession。</param>
    /// <param name="native">絶対予約と同一thread lockを実装するnative境界。</param>
    /// <param name="originMixerFrame">省略時は現在mixer位置を曲frame 0にします。</param>
    /// <param name="generation">restart／seekごとに増加する区間世代。</param>
    /// <param name="directVoices">短い発音を直接合成するための変換済み波形の表。省略時はすべての発音をnative voiceとして予約します。</param>
    internal BmsScheduledAudioMixer(
        BmsAudioFrameSchedule schedule,
        IReadOnlyList<BmsAudioResource?> resourcesByIndex,
        BassAudioSession session,
        IBassScheduledMixerNativeBoundary native,
        long? originMixerFrame = null,
        int generation = 0,
        BmsDirectVoiceTable? directVoices = null)
    {
        this.schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        this.resourcesByIndex = resourcesByIndex ?? throw new ArgumentNullException(nameof(resourcesByIndex));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.native = native ?? throw new ArgumentNullException(nameof(native));
        this.directVoices = directVoices;
        if (session.State != BassAudioSessionState.Active || session.MixerHandle == 0)
        {
            throw new InvalidOperationException("An active input mixer session is required for BMS scheduling.");
        }
        if (directVoices != null && directVoices.SampleRate != schedule.SampleRate)
        {
            throw new InvalidOperationException(
                "The direct voice table rate does not match the schedule rate.");
        }

        using BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation();
        if (session.CoreDeviceIndex >= 0)
        {
            Bass.CurrentDevice = session.CoreDeviceIndex;
        }
        mixerHandle = session.MixerHandle;
        ChannelInfo info = Bass.ChannelGetInfo(mixerHandle);
        if (info.Frequency <= 0
            || info.Channels is < 1 or > 8
            || !info.Flags.HasFlag(BassFlags.Float)
            || !info.Flags.HasFlag(BassFlags.Decode))
        {
            throw new InvalidOperationException("The BMS input mixer must expose a valid Float32 decode format.");
        }
        if (info.Frequency != schedule.SampleRate)
        {
            throw new InvalidOperationException(
                "The BMS schedule rate does not match the active input mixer rate.");
        }
        if (directVoices != null && directVoices.WaveCount != 0 && directVoices.ChannelCount != info.Channels)
        {
            throw new InvalidOperationException(
                "The direct voice table channel count does not match the active input mixer.");
        }

        sampleRate = info.Frequency;
        channelCount = info.Channels;
        bytesPerFrame = checked((long)channelCount * sizeof(float));
        long currentBytes = GetMixerPositionBytes("initialize");
        if (currentBytes % bytesPerFrame != 0)
        {
            throw new InvalidOperationException("The input mixer position is not aligned to a Float32 frame.");
        }
        long currentFrame = currentBytes / bytesPerFrame;
        this.originMixerFrame = originMixerFrame ?? currentFrame;
        segmentStartMixerFrame = this.originMixerFrame;
        this.generation = generation;
    }

    /// <summary>曲frame 0に対応するinput mixer frame。</summary>
    internal long OriginMixerFrame => originMixerFrame;

    /// <summary>scheduleとnative mixerが共有する実効sample rate。</summary>
    internal int SampleRate => sampleRate;

    /// <summary>Float32 output frameあたりのnative byte数。</summary>
    internal long BytesPerFrame => bytesPerFrame;

    /// <summary>現在追跡中のnative voice数を取得します。直接合成のブロックは一つのvoiceとして数えます。</summary>
    internal int ReservedVoiceCount
    {
        get
        {
            lock (sync)
            {
                return voices.Count;
            }
        }
    }

    /// <summary>
    /// 直近の標本点で <c>Start ≤ 現在位置 &lt; EndExclusive</c> にある論理的な発音数を取得します。
    /// 直接合成の対象と対象外の両方を数え、ブロックの数ではありません。
    /// </summary>
    internal int ActiveVoiceCount
    {
        get
        {
            lock (sync)
            {
                return logicalActiveEnds.Count;
            }
        }
    }

    /// <summary>予約を確定した論理的な発音の累計数です。直接合成の対象と対象外の両方を含み、native voice数ではありません。</summary>
    internal long ReservedLogicalVoiceCount => Interlocked.Read(ref reservedLogicalVoiceCount);

    /// <summary>source準備に失敗した回数。</summary>
    internal int PreparationFailureCount => Volatile.Read(ref preparationFailureCount);

    /// <summary>native commitに失敗した回数。</summary>
    internal int CommitFailureCount => Volatile.Read(ref commitFailureCount);

    /// <summary>実行したvoice準備・native commit回数。</summary>
    internal int PreparationOperationCount => Volatile.Read(ref preparationOperationCount);

    /// <summary>実行したnative commit回数。</summary>
    internal int CommitOperationCount => Volatile.Read(ref commitOperationCount);

    /// <summary>一回のprepareで扱った最大voice数。</summary>
    internal int MaximumPreparedVoiceCount => Volatile.Read(ref maximumPreparedVoiceCount);

    /// <summary>区間中に同時追跡した最大native voice数。</summary>
    internal int MaximumReservedVoiceCount => Volatile.Read(ref maximumReservedVoiceCount);

    /// <summary>区間中に標本した論理的な発音数の最大。</summary>
    internal int MaximumActiveVoiceCount => Volatile.Read(ref maximumActiveVoiceCount);

    /// <summary>回収処理へ渡したvoice数。</summary>
    internal int RetiredVoiceCount => Volatile.Read(ref retiredVoiceCount);

    /// <summary>構築を試みたvoiceの累計数。</summary>
    internal long PreparedVoiceCount => Interlocked.Read(ref preparedVoiceCount);

    /// <summary>source準備に要した合計Stopwatch tick。</summary>
    internal long PreparationTicks => Interlocked.Read(ref preparationTicks);

    /// <summary>voice準備の最短Stopwatch tick。</summary>
    internal long MinimumPreparationTicks => Interlocked.Read(ref minimumPreparationTicks);

    /// <summary>voice準備の最長Stopwatch tick。</summary>
    internal long MaximumPreparationTicks => Interlocked.Read(ref maximumPreparationTicks);

    /// <summary>native commitに要した合計Stopwatch tick。</summary>
    internal long CommitTicks => Interlocked.Read(ref commitTicks);

    /// <summary>native ChannelLock内の最大Stopwatch tick。</summary>
    internal long MaxCommitTicks => Interlocked.Read(ref maxCommitTicks);

    /// <summary>input mixerをnative lockで保護した合計Stopwatch tick。</summary>
    internal long NativeLockTicks => Interlocked.Read(ref nativeLockTicks);

    /// <summary>一回のnative lock保持に要した最大Stopwatch tick。</summary>
    internal long MaximumNativeLockTicks => Interlocked.Read(ref maximumNativeLockTicks);

    /// <summary>commit直前に観測した最小の将来予約marginをframeで取得します。</summary>
    internal long MinimumReservationMarginFrames
    {
        get
        {
            long value = Interlocked.Read(ref minimumReservationMarginFrames);
            return value == long.MaxValue ? -1 : value;
        }
    }

    /// <summary>現在の区間に属するschedule eventをすべて予約cursorへ取り込んだか取得します。</summary>
    internal bool AllEventsReserved
    {
        get
        {
            lock (sync)
            {
                return nextEventIndex >= schedule.Events.Count;
            }
        }
    }

    /// <summary>指定song frameより前に始まる全イベントをまとめて予約します。</summary>
    /// <param name="exclusiveSongFrame">このframeより前に開始するイベントを予約する上限。</param>
    /// <param name="currentSongFrame">直前に観測したsong mixer frame。</param>
    /// <param name="includeEvent">Writerなどが適用する契約上のevent filter。</param>
    /// <param name="budget">リアルタイムの時間予算。省略(Writer)時は時間で打ち切りません。</param>
    internal void ReserveBeforeFrame(
        long exclusiveSongFrame,
        long currentSongFrame,
        Func<BmsAudioFrameEvent, bool>? includeEvent = null,
        BmsReservationBudget? budget = null)
    {
        lock (sync)
        {
            ThrowIfDisposed();
            try
            {
                ReserveBeforeFrameCore(exclusiveSongFrame, currentSongFrame, includeEvent, budget);
            }
            catch
            {
                // 失敗した区間は破棄されるため、論理的な発音数を残さない。
                ClearLogicalVoices();
                throw;
            }
        }
    }

    private void ReserveBeforeFrameCore(
        long exclusiveSongFrame,
        long currentSongFrame,
        Func<BmsAudioFrameEvent, bool>? includeEvent,
        BmsReservationBudget? budget)
    {
        var pending = new List<PendingVoice>();
        IReadOnlyList<BmsAudioFrameEvent> events = schedule.Events;
        while (nextEventIndex < events.Count && events[nextEventIndex].StartFrame < exclusiveSongFrame)
        {
            BmsAudioFrameEvent audioEvent = events[nextEventIndex++];
            if (includeEvent != null && !includeEvent(audioEvent))
            {
                continue;
            }
            if ((uint)audioEvent.WavIndex >= (uint)resourcesByIndex.Count)
            {
                throw CreateFailure(
                    "resource index",
                    audioEvent,
                    0,
                    GetMixerFrameIfRepresentable(currentSongFrame));
            }

            BmsAudioResource? resource = resourcesByIndex[audioEvent.WavIndex];
            if (resource == null || resource.IsEmpty)
            {
                continue;
            }

            long endFrame = GetEndFrame(audioEvent, resource);
            if (endFrame <= audioEvent.StartFrame)
            {
                continue;
            }
            if (audioEvent.StartFrame < currentSongFrame)
            {
                long expectedMixerFrame = checked(originMixerFrame + audioEvent.StartFrame);
                throw RecordAndCreateFailure(
                    "missed look-ahead deadline",
                    audioEvent,
                    expectedMixerFrame,
                    GetMixerFrameIfRepresentable(currentSongFrame),
                    null);
            }
            pending.Add(CreatePending(audioEvent, resource, audioEvent.StartFrame, endFrame, audioEvent.SourceStartFrame));
        }

        PrepareAndCommit(pending, budget);
    }

    /// <summary>新しい再生区間のactive voiceと先行範囲を共通予約処理で構成します。</summary>
    /// <param name="segmentStartSongFrame">seek後の整数song frame。</param>
    /// <param name="exclusiveSongFrame">最初に予約する先行範囲の終端。</param>
    /// <param name="budget">リアルタイムの時間予算。省略(Writer)時は時間で打ち切りません。</param>
    internal void ReserveSegmentBeforeFrame(
        long segmentStartSongFrame,
        long exclusiveSongFrame,
        BmsReservationBudget? budget = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(segmentStartSongFrame);
        if (exclusiveSongFrame <= segmentStartSongFrame)
        {
            throw new ArgumentOutOfRangeException(nameof(exclusiveSongFrame));
        }

        lock (sync)
        {
            ThrowIfDisposed();
            if (nextEventIndex != 0 || voices.Count != 0)
            {
                throw new InvalidOperationException("A new playback segment requires an unused scheduled mixer.");
            }
            try
            {
                ReserveSegmentCore(segmentStartSongFrame, exclusiveSongFrame, budget);
            }
            catch
            {
                // 失敗した区間は破棄されるため、論理的な発音数を残さない。
                ClearLogicalVoices();
                throw;
            }
        }
    }

    private void ReserveSegmentCore(
        long segmentStartSongFrame,
        long exclusiveSongFrame,
        BmsReservationBudget? budget)
    {
        long segmentMixerFrame = checked(originMixerFrame + segmentStartSongFrame);
        this.segmentStartSongFrame = segmentStartSongFrame;
        segmentStartMixerFrame = segmentMixerFrame;

        var pending = new List<PendingVoice>();
        // BMSでは次の同index発音で打切り済み、bmsonでは同channelのEOF tailも重なります。
        // 最後のpriorだけを選ばず、半開区間に含まれるすべての独立voiceを復元します。
        foreach (BmsAudioFrameEvent activeEvent in schedule.Events)
        {
            if (activeEvent.StartFrame >= segmentStartSongFrame)
            {
                break;
            }

            if ((uint)activeEvent.WavIndex >= (uint)resourcesByIndex.Count)
            {
                throw CreateFailure("resource index", activeEvent, 0, currentMixerFrame: null);
            }

            BmsAudioResource? resource = resourcesByIndex[activeEvent.WavIndex];
            if (resource == null || resource.IsEmpty)
            {
                continue;
            }

            long endFrame = GetEndFrame(activeEvent, resource);
            if (segmentStartSongFrame < endFrame)
            {
                long sourceFrame = checked(activeEvent.SourceStartFrame + AudioFrameMath.SourceFrameFromMixerFrames(
                    segmentStartSongFrame - activeEvent.StartFrame, resource.Audio.SampleRate, sampleRate));
                pending.Add(CreatePending(activeEvent, resource, segmentStartSongFrame, endFrame, sourceFrame));
            }
        }

        nextEventIndex = schedule.LowerBoundStartFrame(segmentStartSongFrame);
        IReadOnlyList<BmsAudioFrameEvent> events = schedule.Events;
        while (nextEventIndex < events.Count && events[nextEventIndex].StartFrame < exclusiveSongFrame)
        {
            BmsAudioFrameEvent audioEvent = events[nextEventIndex++];
            if ((uint)audioEvent.WavIndex >= (uint)resourcesByIndex.Count)
            {
                throw CreateFailure("resource index", audioEvent, 0, currentMixerFrame: null);
            }
            BmsAudioResource? resource = resourcesByIndex[audioEvent.WavIndex];
            if (resource == null || resource.IsEmpty)
            {
                continue;
            }
            long endFrame = GetEndFrame(audioEvent, resource);
            if (endFrame > audioEvent.StartFrame)
            {
                pending.Add(CreatePending(audioEvent, resource, audioEvent.StartFrame, endFrame, audioEvent.SourceStartFrame));
            }
        }

        PrepareAndCommit(pending, budget);
    }

    /// <summary>mixer時計に合わせてvoice数を更新し、終了区間をnativeから回収します。</summary>
    internal void AdvanceAndRetire()
    {
        lock (sync)
        {
            ThrowIfDisposed();
            long currentFrame = GetCurrentMixerFrame();
            RetireCompletedVoices(currentFrame);
            SampleLogicalVoices(currentFrame);
        }
    }

    /// <summary>現在のinput mixer位置を絶対Float32 frameで取得します。</summary>
    internal long GetCurrentMixerFrame()
    {
        long currentBytes = GetMixerPositionBytes("read position");
        if (currentBytes % bytesPerFrame != 0)
        {
            throw new InvalidOperationException("The input mixer position is not aligned to a Float32 frame.");
        }
        return currentBytes / bytesPerFrame;
    }

    /// <summary>現在のinput mixer位置を曲頭基準のframeで取得します。</summary>
    internal long GetCurrentSongFrame() => checked(GetCurrentMixerFrame() - originMixerFrame);

    private void CommitPrepared(IReadOnlyList<ScheduledVoice> prepared)
    {
        using BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation();
        if (!ReferenceEquals(BassAudioPlayer.CurrentSessionForAdmittedOperation, session)
            || session.State != BassAudioSessionState.Active)
        {
            Exception sessionFailure = new InvalidOperationException(
                "The active audio session changed after voice preparation.");
            Exception? cleanupFailure = DisposeUncommitted(prepared.Select(voice => voice.Player));
            Exception cause = cleanupFailure == null
                ? sessionFailure
                : new AggregateException(sessionFailure, cleanupFailure);
            throw RecordAndCreateFailure(
                "session changed before commit",
                prepared[0].Event,
                prepared[0].StartMixerFrame,
                currentMixerFrame: null,
                innerException: cause);
        }
        if (session.CoreDeviceIndex >= 0)
        {
            Bass.CurrentDevice = session.CoreDeviceIndex;
        }

        var added = new List<ScheduledVoice>(prepared.Count);
        var retainedOnFailure = new List<ScheduledVoice>();
        Exception? primaryFailure = null;
        ScheduledVoice diagnosticVoice = prepared[0];
        var rollbackFailures = new List<Exception>();
        long commitStart = Stopwatch.GetTimestamp();
        long nativeLockStart = 0;
        long observedFrame = -1;
        bool locked = false;
        Interlocked.Increment(ref commitOperationCount);
        try
        {
            if (!native.LockChannel(mixerHandle, locked: true))
            {
                Errors error = native.GetError();
                throw NativeFailure("BASS_ChannelLock(lock)", error, prepared[0]);
            }
            locked = true;
            nativeLockStart = Stopwatch.GetTimestamp();

            long positionBytes = native.GetPosition(mixerHandle, PositionFlags.Bytes);
            if (positionBytes < 0)
            {
                Errors error = native.GetError();
                throw NativeFailure("BASS_ChannelGetPosition", error, prepared[0]);
            }
            if (positionBytes % bytesPerFrame != 0)
            {
                throw NativeFailure("mixer position frame alignment", null, prepared[0]);
            }
            observedFrame = positionBytes / bytesPerFrame;

            foreach (ScheduledVoice voice in prepared)
            {
                diagnosticVoice = voice;
                UpdateMinimum(ref minimumReservationMarginFrames, checked(voice.StartMixerFrame - observedFrame));
                long startBytes = AudioFrameMath.Float32FrameToBytePosition(
                    voice.StartMixerFrame,
                    channelCount);
                long lengthBytes = AudioFrameMath.Float32FrameToBytePosition(
                    checked(voice.EndMixerFrame - voice.StartMixerFrame),
                    channelCount);
                if (voice.StartMixerFrame < observedFrame)
                {
                    throw RecordAndCreateFailure(
                        "deadline passed before native commit",
                        voice.Event,
                        voice.StartMixerFrame,
                        observedFrame,
                        null);
                }
                if (!native.AddChannelAt(
                        mixerHandle,
                        voice.Player.NativeHandleForScheduledVoice,
                        ScheduledSourceFlags,
                        startBytes,
                        lengthBytes))
                {
                    Errors error = native.GetError();
                    throw NativeFailure("BASS_Mixer_StreamAddChannelEx", error, voice);
                }
                added.Add(voice);

                voice.Player.ConfigureScheduledVoice(mixerHandle, voice.Matrix);
                BassFlags updatedFlags = native.SetMixerChannelFlags(
                    voice.Player.NativeHandleForScheduledVoice,
                    BassFlags.Default,
                    PauseFlag);
                if (unchecked((int)updatedFlags) == -1)
                {
                    Errors error = native.GetError();
                    throw NativeFailure("BASS_Mixer_ChannelFlags(resume)", error, voice);
                }
            }
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            Interlocked.Increment(ref commitFailureCount);
            for (int index = added.Count - 1; index >= 0; index--)
            {
                ScheduledVoice voice = added[index];
                try
                {
                    BassFlags updatedFlags = native.SetMixerChannelFlags(
                        voice.Player.NativeHandleForScheduledVoice,
                        PauseFlag,
                        PauseFlag);
                    if (unchecked((int)updatedFlags) == -1)
                    {
                        Errors error = native.GetError();
                        rollbackFailures.Add(NativeFailure("BASS_Mixer_ChannelFlags(rollback pause)", error, voice));
                    }
                }
                catch (Exception rollbackException)
                {
                    rollbackFailures.Add(rollbackException);
                }

                try
                {
                    voice.Player.RemoveScheduledVoiceFromMixer();
                }
                catch (Exception rollbackException)
                {
                    rollbackFailures.Add(rollbackException);
                    retainedOnFailure.Add(voice);
                }
            }

            _ = session.TryRecordCallbackOutputFailure(AudioPcmRenderStage.ScheduledPlayback);
        }
        finally
        {
            if (locked)
            {
                Exception? unlockFailure = null;
                try
                {
                    if (!native.LockChannel(mixerHandle, locked: false))
                    {
                        Errors error = native.GetError();
                        unlockFailure = NativeFailure("BASS_ChannelLock(unlock)", error, diagnosticVoice);
                    }
                }
                catch (Exception exception)
                {
                    unlockFailure = exception;
                }

                long nativeLockElapsed = Stopwatch.GetTimestamp() - nativeLockStart;
                Interlocked.Add(ref nativeLockTicks, nativeLockElapsed);
                UpdateMaximum(ref maximumNativeLockTicks, nativeLockElapsed);
                if (unlockFailure != null)
                {
                    rollbackFailures.Add(unlockFailure);
                    primaryFailure ??= unlockFailure;
                    retainedOnFailure.Clear();
                    retainedOnFailure.AddRange(prepared);
                    _ = session.TryRecordCallbackOutputFailure(AudioPcmRenderStage.ScheduledPlayback);
                }
            }

            long elapsed = Stopwatch.GetTimestamp() - commitStart;
            Interlocked.Add(ref commitTicks, elapsed);
            UpdateMaximum(ref maxCommitTicks, elapsed);
        }

        if (primaryFailure != null)
        {
            var failures = new List<Exception> { primaryFailure };
            failures.AddRange(rollbackFailures);
            voices.AddRange(retainedOnFailure);
            UpdateMaximum(ref maximumReservedVoiceCount, voices.Count);
            var retained = new HashSet<ScheduledVoice>(retainedOnFailure);
            failures.AddRange(DisposeUncommitted(
                prepared
                    .Where(voice => !retained.Contains(voice))
                    .Select(voice => voice.Player)) is Exception cleanup
                ? [cleanup]
                : []);
            Exception cause = failures.Count == 1 ? primaryFailure : new AggregateException(failures);
            long expected = diagnosticVoice.StartMixerFrame;
            long? current = observedFrame >= 0 ? observedFrame : null;
            throw RecordAndCreateFailure("atomic mixer commit", diagnosticVoice.Event, expected, current, cause);
        }

        voices.AddRange(prepared);
        UpdateMaximum(ref maximumReservedVoiceCount, voices.Count);
    }

    private void PrepareAndCommit(IReadOnlyList<PendingVoice> pending, BmsReservationBudget? budget)
    {
        if (pending.Count == 0)
        {
            SampleLogicalVoices(GetCurrentMixerFrame());
            return;
        }

        int nativeCount = 0;
        foreach (PendingVoice voice in pending)
        {
            if (voice.Wave == null)
            {
                nativeCount++;
            }
        }
        // 直接合成の対象は、予約一回につき一つのブロックvoiceへ加算する。
        int blockCount = nativeCount == pending.Count ? 0 : 1;
        int voiceCount = nativeCount + blockCount;
        var prepared = new List<ScheduledVoice>(voiceCount);
        var createdPlayers = new List<BassAudioPlayer>(voiceCount);
        Interlocked.Add(ref preparedVoiceCount, voiceCount);
        long preparationStart = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref preparationOperationCount);
        UpdateMaximum(ref maximumPreparedVoiceCount, voiceCount);
        int preparedIndex = 0;
        PendingVoice failedVoice = pending[0];
        bool budgetExceeded = false;
        try
        {
            foreach (PendingVoice voice in pending)
            {
                if (voice.Wave != null)
                {
                    continue;
                }
                failedVoice = voice;
                // 全voiceを準備してから判定せず、音源作成の合間で予算超過を検出して残りの作成を省く。
                ThrowIfBudgetExceeded(budget, preparedIndex, voiceCount, ref budgetExceeded);
                var player = new BassAudioPlayer(voice.Resource.Path, voice.Resource.Audio, session, native, voice.Event.SourceEndFrame);
                createdPlayers.Add(player);
                long startMixerFrame = checked(originMixerFrame + voice.PlaybackStartSongFrame);
                long endMixerFrame = checked(originMixerFrame + voice.EndSongFrame);
                float[,] matrix = GetMatrix(player);
                prepared.Add(new ScheduledVoice(voice.Event, player, startMixerFrame, endMixerFrame, matrix));
                player.Volume = voice.Resource.SourceGain;
                if (voice.SourceStartFrame != 0)
                {
                    player.SetScheduledSourceFrame(voice.SourceStartFrame);
                }
                preparedIndex++;
            }

            if (blockCount != 0)
            {
                prepared.Add(CreateDirectBlockVoice(
                    pending, budget, preparedIndex, voiceCount, createdPlayers, ref failedVoice, ref budgetExceeded));
            }
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref preparationFailureCount);
            Exception? cleanupFailure = DisposeUncommitted(createdPlayers);
            Exception cause = cleanupFailure == null
                ? exception
                : new AggregateException(exception, cleanupFailure);
            long expectedMixerFrame;
            try
            {
                expectedMixerFrame = checked(originMixerFrame + failedVoice.PlaybackStartSongFrame);
            }
            catch (Exception diagnosticFailure)
            {
                cause = new AggregateException(cause, diagnosticFailure);
                expectedMixerFrame = originMixerFrame;
            }

            throw RecordAndCreateFailure(
                budgetExceeded ? "reservation time budget" : "prepare source voice",
                failedVoice.Event,
                expectedMixerFrame,
                currentMixerFrame: null,
                innerException: cause);
        }
        finally
        {
            long elapsed = Stopwatch.GetTimestamp() - preparationStart;
            Interlocked.Add(ref preparationTicks, elapsed);
            UpdateMinimum(ref minimumPreparationTicks, elapsed);
            UpdateMaximum(ref maximumPreparationTicks, elapsed);
        }

        CommitPrepared(prepared);
        EnqueueLogicalVoices(pending);
        SampleLogicalVoices(GetCurrentMixerFrame());
    }

    private static void ThrowIfBudgetExceeded(
        BmsReservationBudget? budget,
        int preparedCount,
        int totalCount,
        ref bool budgetExceeded)
    {
        if (budget is { IsExceeded: true })
        {
            budgetExceeded = true;
            throw new TimeoutException(
                "The BMS reservation exceeded its " + BmsReservationBudget.BudgetSeconds
                + " second time budget after preparing " + preparedCount + " of " + totalCount + " voices.");
        }
    }

    private float[,] GetMatrix(BassAudioPlayer player)
    {
        uint speakerMask = player.ScheduledSourceSpeakerMask;
        if (!matricesBySpeakerMask.TryGetValue(speakerMask, out float[,]? matrix))
        {
            matrix = player.CreateScheduledMatrix(channelCount);
            matricesBySpeakerMask.Add(speakerMask, matrix);
        }
        return matrix;
    }

    /// <summary>
    /// 一回の予約に含まれる直接合成の発音を、各発音の打切り後の波形全体で一つのブロックへ加算し、
    /// ミキサーと同じレート・チャンネル配置、ゲイン1の有限voiceにします。
    /// </summary>
    /// <remarks>ブロック同士の重なりは許し、次の予約へ繰り越す状態は持ちません。</remarks>
    private ScheduledVoice CreateDirectBlockVoice(
        IReadOnlyList<PendingVoice> pending,
        BmsReservationBudget? budget,
        int preparedCount,
        int voiceCount,
        List<BassAudioPlayer> createdPlayers,
        ref PendingVoice failedVoice,
        ref bool budgetExceeded)
    {
        long blockStartSongFrame = long.MaxValue;
        long blockEndSongFrame = long.MinValue;
        PendingVoice first = default;
        bool hasFirst = false;
        foreach (PendingVoice voice in pending)
        {
            if (voice.Wave == null)
            {
                continue;
            }
            if (!hasFirst || voice.PlaybackStartSongFrame < blockStartSongFrame)
            {
                first = voice;
                hasFirst = true;
            }
            blockStartSongFrame = System.Math.Min(blockStartSongFrame, voice.PlaybackStartSongFrame);
            blockEndSongFrame = System.Math.Max(blockEndSongFrame, voice.EndSongFrame);
        }

        failedVoice = first;
        int frameCount = checked((int)(blockEndSongFrame - blockStartSongFrame));
        float[] block = new float[checked(frameCount * channelCount)];
        foreach (PendingVoice voice in pending)
        {
            BmsDirectVoiceTable.DirectVoiceWave? wave = voice.Wave;
            if (wave == null)
            {
                continue;
            }
            failedVoice = voice;
            ThrowIfBudgetExceeded(budget, preparedCount, voiceCount, ref budgetExceeded);
            // シーク復元では、区間開始frameを跨ぐ発音を変換済み波形の該当する出力frameから入れる。
            int waveOffsetFrames = checked((int)(voice.PlaybackStartSongFrame - voice.Event.StartFrame));
            int frames = checked((int)(voice.EndSongFrame - voice.PlaybackStartSongFrame));
            if (waveOffsetFrames < 0 || frames <= 0 || waveOffsetFrames + frames > wave.FrameCount)
            {
                throw new InvalidOperationException(
                    "The direct voice waveform is shorter than the effective output length of WAV index "
                    + voice.Event.WavIndex + ".");
            }
            int destination = checked((int)(voice.PlaybackStartSongFrame - blockStartSongFrame) * channelCount);
            int source = checked(waveOffsetFrames * channelCount);
            int length = checked(frames * channelCount);
            float[] samples = wave.Samples;
            for (int index = 0; index < length; index++)
            {
                block[destination + index] += samples[source + index];
            }
        }

        failedVoice = first;
        ThrowIfBudgetExceeded(budget, preparedCount, voiceCount, ref budgetExceeded);
        var decoded = new DecodedAudio(sampleRate, AudioChannelLayout.CreateBassOutput(channelCount), block);
        var player = new BassAudioPlayer(DirectBlockSourceName, decoded, session, native);
        createdPlayers.Add(player);
        float[,] matrix = GetMatrix(player);
        var blockVoice = new ScheduledVoice(
            first.Event,
            player,
            checked(originMixerFrame + blockStartSongFrame),
            checked(originMixerFrame + blockEndSongFrame),
            matrix);
        // 波形には音源ゲインを含めてあるため、ブロックのゲインは1にする。
        player.Volume = 1f;
        return blockVoice;
    }

    private void EnqueueLogicalVoices(IReadOnlyList<PendingVoice> pending)
    {
        Interlocked.Add(ref reservedLogicalVoiceCount, pending.Count);
        foreach (PendingVoice voice in pending)
        {
            logicalPending.Enqueue(new LogicalVoice(
                checked(originMixerFrame + voice.PlaybackStartSongFrame),
                checked(originMixerFrame + voice.EndSongFrame)));
        }
    }

    private PendingVoice CreatePending(
        in BmsAudioFrameEvent audioEvent,
        BmsAudioResource resource,
        long playbackStartSongFrame,
        long endSongFrame,
        long sourceStartFrame)
    {
        BmsDirectVoiceTable.DirectVoiceWave? wave = directVoices != null
            && directVoices.IsDirect(endSongFrame - audioEvent.StartFrame)
                ? directVoices.GetWave(resource, audioEvent)
                : null;
        return new PendingVoice(audioEvent, resource, playbackStartSongFrame, endSongFrame, sourceStartFrame, wave);
    }

    private long GetEndFrame(BmsAudioFrameEvent audioEvent, BmsAudioResource resource) =>
        BmsAudioFrameSchedule.GetEndFrame(audioEvent, resource, sampleRate);

    private void RetireCompletedVoices(long currentFrame)
    {
        ScheduledVoice[] completed = voices.Where(voice => currentFrame >= voice.EndMixerFrame).ToArray();
        if (completed.Length == 0)
        {
            return;
        }

        using BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation();
        if (session.CoreDeviceIndex >= 0)
        {
            Bass.CurrentDevice = session.CoreDeviceIndex;
        }
        if (!native.LockChannel(mixerHandle, locked: true))
        {
            Errors error = native.GetError();
            _ = session.TryRecordCallbackOutputFailure(AudioPcmRenderStage.ScheduledPlayback);
            throw NativeFailure("BASS_ChannelLock(retire)", error, completed[0]);
        }

        var removalFailures = new List<Exception>();
        try
        {
            foreach (ScheduledVoice voice in completed)
            {
                try
                {
                    voice.Player.RemoveScheduledVoiceFromMixer();
                }
                catch (Exception exception)
                {
                    removalFailures.Add(exception);
                }
            }
        }
        finally
        {
            // 管理リストは一回の走査で詰め直す。個別のRemoveは保持数に比例して全体が二乗に増える。
            int keep = 0;
            for (int index = 0; index < voices.Count; index++)
            {
                ScheduledVoice voice = voices[index];
                if (currentFrame < voice.EndMixerFrame)
                {
                    voices[keep++] = voice;
                }
            }
            voices.RemoveRange(keep, voices.Count - keep);

            if (!native.LockChannel(mixerHandle, locked: false))
            {
                Errors error = native.GetError();
                removalFailures.Add(NativeFailure("BASS_ChannelLock(unlock retire)", error, completed[0]));
            }
        }

        var disposeFailures = new List<Exception>();
        Interlocked.Add(ref retiredVoiceCount, completed.Length);
        foreach (ScheduledVoice voice in completed)
        {
            try
            {
                voice.Player.Dispose();
                if (!voice.Player.NativeReleaseConfirmed)
                {
                    disposeFailures.Add(voice.Player.CreateSourceReleaseFailure());
                }
            }
            catch (Exception exception)
            {
                disposeFailures.Add(exception);
            }
        }
        removalFailures.AddRange(disposeFailures);
        if (removalFailures.Count != 0)
        {
            _ = session.TryRecordCallbackOutputFailure(AudioPcmRenderStage.ScheduledPlayback);
            throw new AggregateException("A completed scheduled voice could not be fully retired.", removalFailures);
        }
    }

    /// <summary>
    /// 標本点で <c>Start ≤ 現在位置 &lt; EndExclusive</c> にある論理的な発音を数え、現行・最大の発音数へ反映します。
    /// </summary>
    /// <remarks>
    /// 計数は予約済みの発音の開始・終了だけを時刻順に進めるため、保持数に比例した全件走査をしません。
    /// </remarks>
    private void SampleLogicalVoices(long currentFrame)
    {
        while (logicalPending.TryPeek(out LogicalVoice next) && next.StartMixerFrame <= currentFrame)
        {
            logicalPending.Dequeue();
            if (next.EndMixerFrame > currentFrame)
            {
                logicalActiveEnds.Enqueue(next.EndMixerFrame, next.EndMixerFrame);
            }
        }
        while (logicalActiveEnds.TryPeek(out long end, out _) && end <= currentFrame)
        {
            logicalActiveEnds.Dequeue();
        }

        int count = logicalActiveEnds.Count;
        PublishLogicalVoiceCount(count);
        UpdateMaximum(ref maximumActiveVoiceCount, count);
    }

    /// <summary>区間の破棄・予約の失敗の後に、論理的な発音数を残さないよう取り除きます。</summary>
    private void ClearLogicalVoices()
    {
        logicalPending.Clear();
        logicalActiveEnds.Clear();
        PublishLogicalVoiceCount(0);
    }

    private void PublishLogicalVoiceCount(int count)
    {
        int delta = count - publishedLogicalVoiceCount;
        if (delta != 0)
        {
            BassAudioPlayer.AdjustScheduledVoiceCount(delta);
            publishedLogicalVoiceCount = count;
        }
    }

    private long GetMixerPositionBytes(string stage)
    {
        using BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation();
        if (!ReferenceEquals(BassAudioPlayer.CurrentSessionForAdmittedOperation, session)
            || session.State != BassAudioSessionState.Active)
        {
            throw new InvalidOperationException("The scheduled mixer session is no longer active.");
        }
        if (session.CoreDeviceIndex >= 0)
        {
            Bass.CurrentDevice = session.CoreDeviceIndex;
        }
        long position = native.GetPosition(mixerHandle, PositionFlags.Bytes);
        if (position < 0)
        {
            Errors error = native.GetError();
            throw new InvalidOperationException(
                "BASS_ChannelGetPosition failed while attempting to " + stage + ": "
                + BassNativeErrorFormatter.Format(error));
        }
        return position;
    }

    private BassAudioPlaybackException NativeFailure(
        string operation,
        Errors? error,
        ScheduledVoice voice) =>
        new(
            BassAudioPlaybackStage.ScheduledPlayback,
            voice.Player.FileName,
            voice.Player.NativeHandleForScheduledVoice,
            mixerHandle,
            mixerHandle,
            operation,
            error,
            operation + " failed while reserving WAV index " + voice.Event.WavIndex + ".",
            session);

    private BmsScheduledAudioException CreateFailure(
        string stage,
        BmsAudioFrameEvent audioEvent,
        long expectedMixerFrame,
        long? currentMixerFrame) =>
        new(stage, audioEvent, segmentStartSongFrame, segmentStartMixerFrame, originMixerFrame,
            expectedMixerFrame, currentMixerFrame,
            sampleRate, channelCount, generation);

    /// <summary>観測済み曲frameから診断可能なmixer位置を求め、表現できない場合は不明にします。</summary>
    private long? GetMixerFrameIfRepresentable(long songFrame)
    {
        try
        {
            return checked(originMixerFrame + songFrame);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private BmsScheduledAudioException RecordAndCreateFailure(
        string stage,
        BmsAudioFrameEvent audioEvent,
        long expectedMixerFrame,
        long? currentMixerFrame,
        Exception? innerException)
    {
        _ = session.TryRecordCallbackOutputFailure(AudioPcmRenderStage.ScheduledPlayback);
        return new BmsScheduledAudioException(
            stage,
            audioEvent,
            segmentStartSongFrame,
            segmentStartMixerFrame,
            originMixerFrame,
            expectedMixerFrame,
            currentMixerFrame,
            sampleRate,
            channelCount,
            generation,
            innerException);
    }

    private static Exception? DisposeUncommitted(IEnumerable<BassAudioPlayer> players)
    {
        var failures = new List<Exception>();
        foreach (BassAudioPlayer player in players)
        {
            try
            {
                player.Dispose();
                if (!player.NativeReleaseConfirmed)
                {
                    failures.Add(player.CreateSourceReleaseFailure());
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
        return failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException(failures)
        };
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(BmsScheduledAudioMixer));
        }
    }

    private static void UpdateMaximum(ref long maximum, long value)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref maximum);
            if (current >= value)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref maximum, value, current) != current);
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref maximum);
            if (current >= value)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref maximum, value, current) != current);
    }

    private static void UpdateMinimum(ref long minimum, long value)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref minimum);
            if (current <= value)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref minimum, value, current) != current);
    }

    /// <summary>残存予約をまとめて外し、解放確認済みsourceだけをsessionから取り除きます。</summary>
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            ClearLogicalVoices();
            ScheduledVoice[] remaining = voices.ToArray();
            if (remaining.Length == 0)
            {
                return;
            }

            var failures = new List<Exception>();
            BassAudioOperationLease operation = default;
            bool operationEntered = false;
            bool locked = false;
            try
            {
                operation = BassAudioRuntime.EnterAudioOperation();
                operationEntered = true;
                if (session.CoreDeviceIndex >= 0)
                {
                    Bass.CurrentDevice = session.CoreDeviceIndex;
                }
                if (!native.LockChannel(mixerHandle, locked: true))
                {
                    Errors error = native.GetError();
                    failures.Add(NativeFailure("BASS_ChannelLock(dispose)", error, remaining[0]));
                }
                else
                {
                    locked = true;
                    foreach (ScheduledVoice voice in remaining)
                    {
                        try
                        {
                            voice.Player.RemoveScheduledVoiceFromMixer();
                        }
                        catch (Exception exception)
                        {
                            failures.Add(exception);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
            finally
            {
                if (locked && !native.LockChannel(mixerHandle, locked: false))
                {
                    Errors error = native.GetError();
                    failures.Add(NativeFailure("BASS_ChannelLock(unlock dispose)", error, remaining[0]));
                }
                if (operationEntered)
                {
                    operation.Dispose();
                }
            }

            foreach (ScheduledVoice voice in remaining)
            {
                try
                {
                    voice.Player.Dispose();
                    if (!voice.Player.NativeReleaseConfirmed)
                    {
                        failures.Add(voice.Player.CreateSourceReleaseFailure());
                    }
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
            voices.Clear();
            if (failures.Count != 0)
            {
                _ = session.TryRecordCallbackOutputFailure(AudioPcmRenderStage.ScheduledPlayback);
                throw new AggregateException("Scheduled BMS voices could not be fully disposed.", failures);
            }
        }
    }

    private sealed class ScheduledVoice(
        BmsAudioFrameEvent audioEvent,
        BassAudioPlayer player,
        long startMixerFrame,
        long endMixerFrame,
        float[,] matrix)
    {
        internal BmsAudioFrameEvent Event { get; } = audioEvent;

        internal BassAudioPlayer Player { get; } = player;

        internal long StartMixerFrame { get; } = startMixerFrame;

        internal long EndMixerFrame { get; } = endMixerFrame;

        internal float[,] Matrix { get; } = matrix;
    }

    /// <summary>予約待ちの一つの論理的な発音です。<see cref="Wave"/>があれば直接合成の対象です。</summary>
    private readonly record struct PendingVoice(
        BmsAudioFrameEvent Event,
        BmsAudioResource Resource,
        long PlaybackStartSongFrame,
        long EndSongFrame,
        long SourceStartFrame,
        BmsDirectVoiceTable.DirectVoiceWave? Wave);

    /// <summary>標本点での発音数の計数に使う、絶対mixer frame上の半開区間です。</summary>
    private readonly record struct LogicalVoice(long StartMixerFrame, long EndMixerFrame);
}
