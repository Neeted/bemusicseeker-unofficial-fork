#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ManagedBass;
using ManagedBass.Wasapi;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace Ribbit.BMS;

/// <summary>WASAPI output deviceの実buffer量を確認するnative境界です。</summary>
internal interface IBassWasapiOutputBufferNativeBoundary
{
    /// <summary>対象deviceを選択し、output bufferの保留byte数を取得します。</summary>
    bool TryGetAvailableBytes(int deviceIndex, out int availableBytes, out Errors? error);
}

/// <summary>ManagedBass WASAPI APIからoutput buffer量と直後のnative errorを取得します。</summary>
internal sealed class BassWasapiOutputBufferNativeBoundary : IBassWasapiOutputBufferNativeBoundary
{
    /// <inheritdoc />
    public bool TryGetAvailableBytes(int deviceIndex, out int availableBytes, out Errors? error)
    {
        availableBytes = 0;
        error = null;
        try
        {
            BassWasapi.CurrentDevice = deviceIndex;
            availableBytes = BassWasapi.GetData(IntPtr.Zero, (int)DataFlags.Available);
            if (availableBytes < 0)
            {
                error = Bass.LastError;
                return false;
            }
            return true;
        }
        catch (BassException exception)
        {
            error = exception.ErrorCode;
            return false;
        }
    }
}

/// <summary>BMSの realtime 再生区間でlook-ahead予約とMixer時計を管理します。</summary>
internal sealed class BmsRealtimeAudioScheduler : IDisposable
{
    private const double TempoSequenceMilliseconds = 33d;
    /// <summary>48kHzの本番BASS_FX tempo出力で測った初回input pullの先読み量です。</summary>
    private const int MeasuredTempoInputReadAheadFrames = 4096;
    private const BassFlags InputMixerEndFlags = BassFlags.MixerEnd | BassFlags.MixerNonStop;

    private readonly object sync = new();
    private readonly BmsAudioFrameSchedule schedule;
    private readonly IReadOnlyList<BmsAudioResource?> resourcesByIndex;
    private readonly BassAudioSession session;
    private readonly IBassScheduledMixerNativeBoundary native;
    private readonly IBassWasapiOutputBufferNativeBoundary wasapiOutputBufferNative;
    private readonly long terminalSongFrame;
    private readonly TimeSpan duration;
    private BmsScheduledAudioMixer scheduledMixer;
    private AudioPcmRenderer? nullDeviceRenderer;
    private float[]? nullDeviceBuffer;
    private long originMixerFrame;
    private long segmentStartSongFrame;
    private long lastTickTimestamp;
    private long maximumTickIntervalTicks;
    private long maximumReservationTicks;
    private long lastNullPullTimestamp;
    private double nullPullRemainder;
    private long maximumWasapiQueuedFramesAfterEnd;
    private long generation;
    private float playbackRate;
    private int inputEnded;
    private int inputEndTransitionAttempted;
    private int terminalSyncHandle;
    private SyncProcedure? terminalSyncProcedure;
    private bool disposed;

    /// <summary>閉じたoutput pull境界で一つのBMS区間を構成し、最初の範囲を予約します。</summary>
    internal BmsRealtimeAudioScheduler(
        BmsAudioFrameSchedule schedule,
        IReadOnlyList<BmsAudioResource?> resourcesByIndex,
        BassAudioSession session,
        IBassScheduledMixerNativeBoundary native,
        TimeSpan duration,
        long segmentStartSongFrame,
        float playbackRate,
        int generation,
        IBassWasapiOutputBufferNativeBoundary? wasapiOutputBufferNative = null)
    {
        this.schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        this.resourcesByIndex = resourcesByIndex ?? throw new ArgumentNullException(nameof(resourcesByIndex));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.native = native ?? throw new ArgumentNullException(nameof(native));
        this.wasapiOutputBufferNative = wasapiOutputBufferNative ?? new BassWasapiOutputBufferNativeBoundary();
        this.duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        if (segmentStartSongFrame < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentStartSongFrame));
        }
        ValidatePlaybackRate(playbackRate);
        this.segmentStartSongFrame = segmentStartSongFrame;
        this.playbackRate = playbackRate;
        this.generation = generation;
        if (session.State != BassAudioSessionState.Active || session.MixerHandle == 0)
        {
            throw new InvalidOperationException("An active input mixer session is required for BMS playback.");
        }

        terminalSongFrame = GetTerminalSongFrame(schedule, resourcesByIndex, this.duration, schedule.SampleRate);
        try
        {
            CreateSegment(segmentStartSongFrame, initial: true);
        }
        catch (Exception playbackFailure)
        {
            var cleanupFailures = new List<Exception>();
            try
            {
                RemoveTerminalSync();
            }
            catch (Exception cleanupFailure)
            {
                cleanupFailures.Add(cleanupFailure);
            }
            try
            {
                scheduledMixer?.Dispose();
            }
            catch (Exception cleanupFailure)
            {
                cleanupFailures.Add(cleanupFailure);
            }
            session.ClearRealtimeReservedCallbackFrames();
            if (cleanupFailures.Count != 0)
            {
                _ = session.TryRecordCallbackOutputFailure(AudioPcmRenderStage.ScheduledPlayback);
                cleanupFailures.Insert(0, playbackFailure);
                throw new AggregateException(
                    "Initial BMS audio scheduling failed and cleanup also reported failures.",
                    cleanupFailures);
            }
            throw;
        }
    }

    /// <summary>song frame 0に対応するinput mixer frameを取得します。</summary>
    internal long OriginMixerFrame
    {
        get
        {
            lock (sync)
            {
                return originMixerFrame;
            }
        }
    }

    /// <summary>入力mixerを自然終端へ切り替えたか取得します。</summary>
    internal bool InputEnded
    {
        get => System.Threading.Volatile.Read(ref inputEnded) != 0;
    }

    /// <summary>終端flag切替を試みた区間で後処理によるmixer復帰が必要か取得します。</summary>
    internal bool InputEndTransitionAttempted
    {
        get => System.Threading.Volatile.Read(ref inputEndTransitionAttempted) != 0;
    }

    /// <summary>native input mixerへ登録中の終端syncが残っているか取得します。</summary>
    internal bool HasTerminalSync => System.Threading.Volatile.Read(ref terminalSyncHandle) != 0;

    /// <summary>song timeへ換算した現在のinput mixer frameを取得します。</summary>
    internal long CurrentSongFrame
    {
        get
        {
            lock (sync)
            {
                ThrowIfDisposed();
                return System.Math.Clamp(
                    scheduledMixer.GetCurrentSongFrame(),
                    segmentStartSongFrame,
                    terminalSongFrame);
            }
        }
    }

    /// <summary>mixer clockを表示時刻へ変換し、曲長へ制限して取得します。</summary>
    internal TimeSpan CurrentTime
    {
        get
        {
            long frame = CurrentSongFrame;
            TimeSpan time = AudioFrameMath.FrameToTime(frame, schedule.SampleRate);
            return time < duration ? time : duration;
        }
    }

    /// <summary>性能確認用に予約ownerのframe・寿命・処理時間snapshotを取得します。</summary>
    internal BmsScheduledAudioMixer ScheduledMixerDiagnostics
    {
        get
        {
            lock (sync)
            {
                ThrowIfDisposed();
                return scheduledMixer;
            }
        }
    }

    /// <summary>自然終端を判定するsong frameです。</summary>
    internal long TerminalSongFrame => terminalSongFrame;

    /// <summary>制御tick間隔の最大実測Stopwatch tick。</summary>
    internal long MaximumTickIntervalTicks => System.Threading.Interlocked.Read(ref maximumTickIntervalTicks);

    /// <summary>reservation処理の最大実測Stopwatch tick。</summary>
    internal long MaximumReservationTicks => System.Threading.Interlocked.Read(ref maximumReservationTicks);

    /// <summary>input終端後、tempo出力とendpoint報告latency分を通過したか取得します。</summary>
    internal bool IsOutputDrained
    {
        get
        {
            lock (sync)
            {
                if (System.Threading.Volatile.Read(ref inputEnded) == 0 || !session.CallbackOutputReachedEnd)
                {
                    return false;
                }

                BassAudioPlayer.DeviceDriver backend = session.ActualBackend;
                if (backend == BassAudioPlayer.DeviceDriver.NULL_DEVICE)
                {
                    return true;
                }

                long backendDrainFrames;
                if (backend is BassAudioPlayer.DeviceDriver.WASAPI_SHARED
                    or BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE)
                {
                    backendDrainFrames = GetWasapiOutputDrainFrames();
                }
                else if (backend == BassAudioPlayer.DeviceDriver.ASIO)
                {
                    backendDrainFrames = CeilingFrames(
                        session.NegotiationResult.LatencyMilliseconds,
                        schedule.SampleRate);
                }
                else
                {
                    throw new InvalidOperationException("The active output backend does not provide a BMS drain contract.");
                }

                long drainFrames = checked(backendDrainFrames + session.MaximumCallbackFrames);
                return session.CallbackFramesAfterEnd >= drainFrames;
            }
        }
    }

    /// <summary>遅れを許さず区間の次のlook-aheadを予約し、終端・回収状態を更新します。</summary>
    internal void Tick() => TickCore(advanceNullOutput: true);

    /// <summary>NullDeviceを決定的frame駆動する検証用に、予約だけを一度進めます。</summary>
    internal void TickWithoutNullOutputAdvance() => TickCore(advanceNullOutput: false);

    private void TickCore(bool advanceNullOutput)
    {
        lock (sync)
        {
            ThrowIfDisposed();
            long now = Stopwatch.GetTimestamp();
            if (lastTickTimestamp != 0)
            {
                UpdateMaximum(ref maximumTickIntervalTicks, now - lastTickTimestamp);
            }
            lastTickTimestamp = now;

            long currentMixerFrame = scheduledMixer.GetCurrentMixerFrame();
            long currentSongFrame = checked(currentMixerFrame - originMixerFrame);
            long logicalCurrentFrame = System.Math.Clamp(
                currentSongFrame,
                segmentStartSongFrame,
                terminalSongFrame);
            int reservedCallbackFrames = session.MaximumCallbackFrames;
            long leadFrames = GetLeadFrames(playbackRate, reservedCallbackFrames);
            long exclusiveSongFrame = currentSongFrame >= terminalSongFrame
                ? long.MaxValue
                : System.Math.Min(terminalSongFrame + 1, checked(logicalCurrentFrame + leadFrames));
            if (exclusiveSongFrame <= segmentStartSongFrame)
            {
                exclusiveSongFrame = checked(segmentStartSongFrame + 1);
            }

            long reservationStarted = Stopwatch.GetTimestamp();
            try
            {
                scheduledMixer.ReserveBeforeFrame(
                    exclusiveSongFrame,
                    logicalCurrentFrame,
                    audioEvent => audioEvent.AbsoluteTime <= duration);
                scheduledMixer.AdvanceAndRetire();
                PublishReservationCallbackLimit(reservedCallbackFrames);
            }
            finally
            {
                UpdateMaximum(ref maximumReservationTicks, Stopwatch.GetTimestamp() - reservationStarted);
            }

            if (advanceNullOutput && session.ActualBackend == BassAudioPlayer.DeviceDriver.NULL_DEVICE)
            {
                AdvanceNullDevice(now);
            }
        }
    }

    /// <summary>新速度に必要な範囲を先に予約してからtempo属性を変更します。</summary>
    internal void ApplyPlaybackRate(float rate, Action applyNativeRate)
    {
        ArgumentNullException.ThrowIfNull(applyNativeRate);
        ValidatePlaybackRate(rate);
        lock (sync)
        {
            ThrowIfDisposed();
            ReserveLead(rate);
            applyNativeRate();
            playbackRate = rate;
            lastTickTimestamp = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Pause解除前に現速度のlook-ahead範囲を不足なく予約します。</summary>
    internal void EnsureLeadForRate(float rate)
    {
        ValidatePlaybackRate(rate);
        lock (sync)
        {
            ThrowIfDisposed();
            ReserveLead(rate);
        }
    }

    /// <summary>Seek用に区間を破棄してsong frameとnative原点を再構成します。</summary>
    internal void Rebase(long songFrame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(songFrame);
        lock (sync)
        {
            ThrowIfDisposed();
            RemoveTerminalSync();
            scheduledMixer.Dispose();
            generation = checked(generation + 1);
            segmentStartSongFrame = System.Math.Min(songFrame, terminalSongFrame);
            System.Threading.Volatile.Write(ref inputEnded, 0);
            System.Threading.Volatile.Write(ref inputEndTransitionAttempted, 0);
            maximumWasapiQueuedFramesAfterEnd = 0;
            lastTickTimestamp = Stopwatch.GetTimestamp();
            CreateSegment(segmentStartSongFrame, initial: false);
        }
    }

    /// <summary>pause時間を予約marginのwall-clock観測へ含めないよう基準を更新します。</summary>
    internal void ResetControlInterval()
    {
        lock (sync)
        {
            lastTickTimestamp = Stopwatch.GetTimestamp();
        }
    }

    private void CreateSegment(long songStartFrame, bool initial)
    {
        long currentMixerFrame = GetCurrentMixerFrame(session, schedule.SampleRate, native);
        int reservedCallbackFrames = session.MaximumCallbackFrames;
        long leadFrames = GetLeadFrames(playbackRate, reservedCallbackFrames);
        originMixerFrame = checked(currentMixerFrame - songStartFrame);
        scheduledMixer = new BmsScheduledAudioMixer(
            schedule,
            resourcesByIndex,
            session,
            native,
            originMixerFrame,
            checked((int)generation));

        RegisterTerminalSync();

        long horizon = System.Math.Min(
            terminalSongFrame + 1,
            checked(songStartFrame + leadFrames));
        if (horizon <= songStartFrame)
        {
            horizon = checked(songStartFrame + 1);
        }
        long reservationStarted = Stopwatch.GetTimestamp();
        try
        {
            scheduledMixer.ReserveSegmentBeforeFrame(songStartFrame, horizon);
            PublishReservationCallbackLimit(reservedCallbackFrames);
            if (!initial)
            {
                scheduledMixer.AdvanceAndRetire();
            }
        }
        finally
        {
            // 初回開始とSeekは出力を凍結して準備するが、処理時間は再開後の予約余裕に反映する。
            UpdateMaximum(ref maximumReservationTicks, Stopwatch.GetTimestamp() - reservationStarted);
        }
        if (session.ActualBackend == BassAudioPlayer.DeviceDriver.NULL_DEVICE)
        {
            InitializeNullDevicePull();
            lastNullPullTimestamp = Stopwatch.GetTimestamp();
        }
    }

    private long GetLeadFrames(float rate, int callbackFrames)
    {
        double reportedLatencyMilliseconds = session.NegotiationResult.LatencyMilliseconds;
        if (!double.IsFinite(reportedLatencyMilliseconds) || reportedLatencyMilliseconds < 0d)
        {
            throw new InvalidOperationException("The active output backend reported an invalid latency.");
        }

        if (callbackFrames <= 0
            && session.ActualBackend is not BassAudioPlayer.DeviceDriver.NULL_DEVICE)
        {
            throw new InvalidOperationException(
                "The output callback has not reported a pull size; BMS reservation lead cannot be established.");
        }

        long backendOutputFrames = CeilingFrames(reportedLatencyMilliseconds, schedule.SampleRate);
        long pullOutputFrames = checked(backendOutputFrames + callbackFrames);
        long speedScaledOutputFrames = checked((long)System.Math.Ceiling(pullOutputFrames * (double)rate));
        long tempoInputReadAheadFrames = System.Math.Max(
            MeasuredTempoInputReadAheadFrames,
            CeilingFrames(TempoSequenceMilliseconds * rate, schedule.SampleRate));
        long controlAndPreparationFrames = checked((long)System.Math.Ceiling(
            (maximumTickIntervalTicks + maximumReservationTicks)
            * (double)schedule.SampleRate
            * rate
            / Stopwatch.Frequency));
        return checked(speedScaledOutputFrames + tempoInputReadAheadFrames + controlAndPreparationFrames + 1);
    }

    private void ReserveLead(float rate)
    {
        long currentMixerFrame = scheduledMixer.GetCurrentMixerFrame();
        long currentSongFrame = System.Math.Clamp(
            checked(currentMixerFrame - originMixerFrame),
            segmentStartSongFrame,
            terminalSongFrame);
        int reservedCallbackFrames = session.MaximumCallbackFrames;
        long exclusive = currentSongFrame >= terminalSongFrame
            ? long.MaxValue
            : System.Math.Min(terminalSongFrame + 1, checked(currentSongFrame + GetLeadFrames(rate, reservedCallbackFrames)));
        if (exclusive <= segmentStartSongFrame)
        {
            exclusive = checked(segmentStartSongFrame + 1);
        }

        long reserveStarted = Stopwatch.GetTimestamp();
        try
        {
            scheduledMixer.ReserveBeforeFrame(
                exclusive,
                currentSongFrame,
                audioEvent => audioEvent.AbsoluteTime <= duration);
            PublishReservationCallbackLimit(reservedCallbackFrames);
        }
        finally
        {
            UpdateMaximum(ref maximumReservationTicks, Stopwatch.GetTimestamp() - reserveStarted);
        }
    }

    private void PublishReservationCallbackLimit(int callbackFrames)
    {
        if (callbackFrames > 0)
        {
            session.PublishRealtimeReservedCallbackFrames(callbackFrames);
        }
    }

    private void RegisterTerminalSync()
    {
        long terminalMixerFrame = checked(originMixerFrame + terminalSongFrame);
        long terminalPositionBytes = checked(terminalMixerFrame * scheduledMixer.BytesPerFrame);
        SyncProcedure procedure = OnTerminalPositionSync;
        terminalSyncProcedure = procedure;
        session.RetainRealtimeTerminalSyncProcedure(procedure);
        int handle = native.SetPositionSync(session.MixerHandle, terminalPositionBytes, procedure);
        if (handle == 0)
        {
            Errors error = native.GetError();
            ReleaseTerminalSyncProcedure(procedure);
            throw new InvalidOperationException(
                "BASS_ChannelSetSync(POS|MIXTIME) failed while registering the BMS input terminal: "
                + BassNativeErrorFormatter.Format(error));
        }
        System.Threading.Volatile.Write(ref terminalSyncHandle, handle);
    }

    private void OnTerminalPositionSync(int syncHandle, int channel, int data, IntPtr user)
    {
        _ = data;
        _ = user;
        if (channel != session.MixerHandle)
        {
            System.Threading.Interlocked.Exchange(ref inputEndTransitionAttempted, 1);
            _ = session.TryRecordCallbackOutputFailure(AudioPcmRenderStage.ScheduledPlayback);
            return;
        }

        System.Threading.Interlocked.Exchange(ref inputEndTransitionAttempted, 1);
        try
        {
            BassFlags flags = native.SetMixerStreamFlags(
                session.MixerHandle,
                BassFlags.MixerEnd,
                InputMixerEndFlags);
            if (unchecked((int)flags) == -1)
            {
                Errors error = native.GetError();
                _ = session.TryRecordCallbackOutputFailure(AudioPcmRenderStage.ScheduledPlayback, error);
                return;
            }
            System.Threading.Interlocked.CompareExchange(ref terminalSyncHandle, 0, syncHandle);
            ReleaseTerminalSyncProcedure(System.Threading.Volatile.Read(ref terminalSyncProcedure));
            System.Threading.Volatile.Write(ref inputEnded, 1);
        }
        catch
        {
            _ = session.TryRecordCallbackOutputFailure(AudioPcmRenderStage.ScheduledPlayback);
        }
    }

    private void RemoveTerminalSync()
    {
        int handle = System.Threading.Interlocked.Exchange(ref terminalSyncHandle, 0);
        if (handle == 0)
        {
            return;
        }

        bool removed;
        try
        {
            removed = native.RemoveSync(session.MixerHandle, handle);
        }
        catch
        {
            System.Threading.Interlocked.CompareExchange(ref terminalSyncHandle, handle, 0);
            throw;
        }
        if (!removed)
        {
            Errors error = native.GetError();
            if (error != Errors.Handle
                && error != Errors.Init
                && System.Threading.Volatile.Read(ref inputEndTransitionAttempted) == 0)
            {
                System.Threading.Interlocked.CompareExchange(ref terminalSyncHandle, handle, 0);
                throw new InvalidOperationException(
                    "BASS_ChannelRemoveSync failed while releasing the BMS input terminal sync: "
                    + BassNativeErrorFormatter.Format(error));
            }
        }
        ReleaseTerminalSyncProcedure(System.Threading.Volatile.Read(ref terminalSyncProcedure));
    }

    private void ReleaseTerminalSyncProcedure(SyncProcedure? procedure)
    {
        if (procedure == null)
        {
            return;
        }

        System.Threading.Interlocked.CompareExchange(ref terminalSyncProcedure, null, procedure);
        session.ReleaseRealtimeTerminalSyncProcedure(procedure);
    }

    private long GetWasapiOutputDrainFrames()
    {
        if (session.WasapiDeviceIndex < 0)
        {
            RecordDrainFailure(AudioPcmRenderStage.ScheduledPlayback, null);
            throw new InvalidOperationException("The active WASAPI session has no owned device index.");
        }

        using BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation();
        if (!ReferenceEquals(BassAudioPlayer.CurrentSessionForAdmittedOperation, session)
            || session.State != BassAudioSessionState.Active)
        {
            RecordDrainFailure(AudioPcmRenderStage.ScheduledPlayback, null);
            throw new InvalidOperationException("The WASAPI output session changed before drain confirmation.");
        }
        if (!wasapiOutputBufferNative.TryGetAvailableBytes(
                session.WasapiDeviceIndex,
                out int availableBytes,
                out Errors? error))
        {
            RecordDrainFailure(AudioPcmRenderStage.NativeRead, error);
            throw new InvalidOperationException(
                "BASS_WASAPI_GetData(BASS_DATA_AVAILABLE) failed while draining BMS output: "
                + BassNativeErrorFormatter.Format(error));
        }

        AudioPcmRenderer? renderer = session.CallbackPcmRenderer;
        if (renderer == null || renderer.ChannelCount <= 0)
        {
            RecordDrainFailure(AudioPcmRenderStage.InvalidReadLength, null);
            throw new InvalidOperationException("The WASAPI callback output format is unavailable during drain.");
        }
        int bytesPerFrame = checked(renderer.ChannelCount * sizeof(float));
        if (availableBytes < 0 || availableBytes % bytesPerFrame != 0)
        {
            RecordDrainFailure(AudioPcmRenderStage.UnalignedFrame, null);
            throw new InvalidOperationException(
                "BASS_WASAPI_GetData(BASS_DATA_AVAILABLE) returned a partial Float32 frame.");
        }

        long queuedFrames = availableBytes / bytesPerFrame;
        maximumWasapiQueuedFramesAfterEnd = System.Math.Max(
            maximumWasapiQueuedFramesAfterEnd,
            queuedFrames);
        return maximumWasapiQueuedFramesAfterEnd;
    }

    private void RecordDrainFailure(AudioPcmRenderStage stage, Errors? error) =>
        _ = session.TryRecordCallbackOutputFailure(stage, error);

    private void InitializeNullDevicePull()
    {
        using BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation();
        if (session.CoreDeviceIndex >= 0)
        {
            Bass.CurrentDevice = session.CoreDeviceIndex;
        }
        int outputHandle = BassAudioPlayer.OutputMixerHandle;
        if (outputHandle == 0)
        {
            throw new InvalidOperationException("The BMS tempo output handle is unavailable for NullDevice playback.");
        }
        ChannelInfo info = Bass.ChannelGetInfo(outputHandle);
        if (info.Frequency != schedule.SampleRate
            || info.Channels <= 0
            || !info.Flags.HasFlag(BassFlags.Float))
        {
            throw new InvalidOperationException("The BMS NullDevice output format does not match the audio schedule.");
        }
        nullDeviceRenderer = new AudioPcmRenderer(outputHandle, info.Frequency, info.Channels);
        nullDeviceBuffer = new float[checked(4096 * info.Channels)];
    }

    private void AdvanceNullDevice(long currentTimestamp)
    {
        if (session.CallbackOutputReachedEnd)
        {
            return;
        }
        long elapsedTicks = System.Math.Max(0, currentTimestamp - lastNullPullTimestamp);
        lastNullPullTimestamp = currentTimestamp;
        double elapsedFrames = elapsedTicks * (double)schedule.SampleRate / Stopwatch.Frequency + nullPullRemainder;
        long framesToPull = checked((long)System.Math.Floor(elapsedFrames));
        nullPullRemainder = elapsedFrames - framesToPull;
        if (framesToPull == 0)
        {
            return;
        }

        using BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation();
        if (session.CoreDeviceIndex >= 0)
        {
            Bass.CurrentDevice = session.CoreDeviceIndex;
        }
        int outputHandle = BassAudioPlayer.OutputMixerHandle;
        AudioPcmRenderer renderer = nullDeviceRenderer
            ?? throw new InvalidOperationException("The BMS NullDevice pull renderer is unavailable.");
        float[] buffer = nullDeviceBuffer
            ?? throw new InvalidOperationException("The BMS NullDevice pull buffer is unavailable.");

        while (framesToPull > 0)
        {
            int frames = checked((int)System.Math.Min(framesToPull, buffer.Length / renderer.ChannelCount));
            if (!renderer.TryReadFrames(
                    outputHandle,
                    buffer,
                    frames,
                    out AudioPcmReadResult result,
                    out AudioPcmRenderStage stage,
                    out Errors? error))
            {
                session.TryRecordCallbackOutputFailure(stage, error);
                throw new AudioPcmRenderException(outputHandle, stage, error);
            }
            session.RecordCallbackOutput(frames, result.ReachedEnd);
            if (result.ReachedEnd)
            {
                return;
            }
            framesToPull -= frames;
        }
    }

    private static long GetTerminalSongFrame(
        BmsAudioFrameSchedule schedule,
        IReadOnlyList<BmsAudioResource?> resources,
        TimeSpan duration,
        int sampleRate)
    {
        long terminalFrame = System.Math.Max(0, AudioFrameMath.TimeToFrame(duration, sampleRate));
        foreach (BmsAudioFrameEvent audioEvent in schedule.Events)
        {
            if (audioEvent.AbsoluteTime > duration)
            {
                continue;
            }
            terminalFrame = System.Math.Max(terminalFrame, audioEvent.StartFrame);
            if ((uint)audioEvent.WavIndex >= (uint)resources.Count)
            {
                throw new InvalidOperationException("The BMS schedule references a missing WAV resource slot.");
            }
            BmsAudioResource? resource = resources[audioEvent.WavIndex];
            if (resource == null || resource.IsEmpty)
            {
                continue;
            }
            long naturalEnd = checked(audioEvent.StartFrame + resource.GetOutputFrameCount(sampleRate));
            long eventEnd = audioEvent.NextSameIndexStartFrame is long nextStart
                ? System.Math.Min(naturalEnd, nextStart)
                : naturalEnd;
            terminalFrame = System.Math.Max(terminalFrame, eventEnd);
        }
        return terminalFrame;
    }

    private static long GetCurrentMixerFrame(
        BassAudioSession session,
        int sampleRate,
        IBassScheduledMixerNativeBoundary native)
    {
        using BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation();
        if (!ReferenceEquals(BassAudioPlayer.CurrentSessionForAdmittedOperation, session)
            || session.State != BassAudioSessionState.Active)
        {
            throw new InvalidOperationException("The BMS input mixer session is no longer active.");
        }
        if (session.CoreDeviceIndex >= 0)
        {
            Bass.CurrentDevice = session.CoreDeviceIndex;
        }
        ChannelInfo info = Bass.ChannelGetInfo(session.MixerHandle);
        if (info.Frequency != sampleRate
            || info.Channels <= 0
            || !info.Flags.HasFlag(BassFlags.Float))
        {
            throw new InvalidOperationException("The BMS input mixer format changed after schedule creation.");
        }
        long bytesPerFrame = checked((long)info.Channels * sizeof(float));
        long positionBytes = native.GetPosition(session.MixerHandle, PositionFlags.Bytes);
        if (positionBytes < 0)
        {
            Errors error = native.GetError();
            throw new InvalidOperationException(
                "BASS_ChannelGetPosition failed while starting the BMS segment: " + BassNativeErrorFormatter.Format(error));
        }
        if (positionBytes % bytesPerFrame != 0)
        {
            throw new InvalidOperationException("The BMS input mixer position is not frame aligned.");
        }
        return positionBytes / bytesPerFrame;
    }

    private static long CeilingFrames(double milliseconds, int sampleRate)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        }
        return checked((long)System.Math.Ceiling(milliseconds * sampleRate / 1000d));
    }

    private static void ValidatePlaybackRate(float rate)
    {
        if (!float.IsFinite(rate) || rate < 0.05f || rate > 50f)
        {
            throw new ArgumentOutOfRangeException(nameof(rate));
        }
    }

    private static void UpdateMaximum(ref long maximum, long value)
    {
        long current;
        do
        {
            current = System.Threading.Interlocked.Read(ref maximum);
            if (current >= value)
            {
                return;
            }
        }
        while (System.Threading.Interlocked.CompareExchange(ref maximum, value, current) != current);
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(BmsRealtimeAudioScheduler));
        }
    }

    /// <summary>残存voiceを共通scheduled mixerへ戻してからnative ownerへ解放します。</summary>
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            var failures = new List<Exception>();
            try
            {
                RemoveTerminalSync();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            try
            {
                scheduledMixer.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
            finally
            {
                session.ClearRealtimeReservedCallbackFrames();
                disposed = true;
            }

            if (failures.Count == 0)
            {
                return;
            }

            _ = session.TryRecordCallbackOutputFailure(AudioPcmRenderStage.ScheduledPlayback);
            if (failures.Count == 1)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }
            throw new AggregateException(
                "BMS realtime audio cleanup reported multiple failures.",
                failures);
        }
    }
}
