#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using ManagedBass;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;
using Ribbit.Logging;
using Ribbit.Media;
using Ribbit.Media.Audio;
using Ribbit.Util;

namespace Ribbit.BMS;

public class BMSAutoPlayer : BMSPlayer<NullImageLoader>
{
    private IReadOnlyList<AudioSourceOmission> omittedAudioSources = Array.Empty<AudioSourceOmission>();
    private IReadOnlyList<BmsAudioResource?> audioResourcesByIndex;
    private BassAudioSession? resourceSession;
    private BmsAudioFrameSchedule? audioSchedule;
    private BmsRealtimeAudioScheduler? realtimeScheduler;
    private readonly IBassScheduledMixerNativeBoundary realtimeMixerNative;
    private int playbackGeneration;

    public BMSAutoPlayer(BMSFile bms)
        : this(bms, new BassMixerSourceNativeBoundary())
    {
    }

    /// <summary>指定native境界でBMS再生sourceを構築します。</summary>
    /// <param name="bms">再生対象の譜面。</param>
    /// <param name="realtimeMixerNative">schedulerが使うnative mixer境界。</param>
    internal BMSAutoPlayer(BMSFile bms, IBassScheduledMixerNativeBoundary realtimeMixerNative)
        : base(bms)
    {
        this.realtimeMixerNative = realtimeMixerNative
            ?? throw new ArgumentNullException(nameof(realtimeMixerNative));
        audioResourcesByIndex = Array.AsReadOnly(new BmsAudioResource?[bms.WavArray.Length]);
    }

    /// <summary>今回の譜面読み込みで入力失敗により省略した音源を変更不能な一覧で取得します。</summary>
    internal IReadOnlyList<AudioSourceOmission> OmittedAudioSources => omittedAudioSources;

    /// <summary>読み込んだpath共有resourceをWAV index順で取得します。</summary>
    internal IReadOnlyList<BmsAudioResource?> AudioResourcesByIndex => audioResourcesByIndex;

    /// <summary>譜面の発音scheduleを捕捉した実効mixer rateで取得します。</summary>
    internal BmsAudioFrameSchedule AudioSchedule =>
        audioSchedule ?? throw new InvalidOperationException("BMS audio resources have not been loaded.");

    /// <summary>resourceとscheduleを作成したnative mixer sessionを取得します。</summary>
    internal BassAudioSession ResourceSession =>
        resourceSession ?? throw new InvalidOperationException("BMS audio resources have not been loaded.");

    /// <summary>再生中に実際のRealtime予約、Mixer時計、排出状態を検証するownerです。</summary>
    internal BmsRealtimeAudioScheduler? RealtimeScheduler => realtimeScheduler;

    /// <summary>BMS resourceへ捕捉するvoice source gainを取得します。</summary>
    protected virtual float ResourceSourceGain => BassAudioPlayer.DefaultVolume;

    /// <summary>再生速度を取得し、変更時は先行予約後にnative tempoを更新します。</summary>
    public override float PlaybackRate
    {
        get => base.PlaybackRate;
        set
        {
            if (!float.IsFinite(value) || value < 0.05f || value > 50f)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            lock (playbackControlSync)
            {
                try
                {
                    if (realtimeScheduler != null)
                    {
                        realtimeScheduler.ApplyPlaybackRate(value, () => BassAudioPlayer.SetBmsTempoChange(value));
                    }
                    else if (resourceSession?.State == BassAudioSessionState.Active)
                    {
                        BassAudioPlayer.SetBmsTempoChange(value);
                    }
                    base.PlaybackRate = value;
                }
                catch (Exception exception)
                {
                    resourceSession?.TryRecordCallbackOutputFailure(AudioPcmRenderStage.ScheduledPlayback);
                    if (PlayState == PlayState.Paused)
                    {
                        // Pause中はnative graphを凍結したまま、Start Taskの終了所有者へ失敗を渡す。
                        FailPlayback(exception);
                    }
                    throw;
                }
            }
        }
    }

    /// <summary>Pause中もnative output callbackと直列化し、曲のmixer時計を凍結します。</summary>
    public override void Pause()
    {
        lock (playbackControlSync)
        {
            if (PlayState == PlayState.Stopped)
            {
                return;
            }

            BassAudioSession? session = resourceSession;
            if (PlayState == PlayState.Playing)
            {
                session?.SetCallbackOutputPaused(true);
                realtimeScheduler?.ResetControlInterval();
                base.Pause();
                return;
            }

            try
            {
                realtimeScheduler?.EnsureLeadForRate(base.PlaybackRate);
                base.Pause();
                realtimeScheduler?.ResetControlInterval();
                session?.SetCallbackOutputPaused(false);
            }
            catch (Exception exception)
            {
                session?.TryRecordCallbackOutputFailure(AudioPcmRenderStage.ScheduledPlayback);
                // 予約に失敗した再生区間は出力を再開せず、同じ終了所有者へ失敗を渡す。
                FailPlayback(exception);
                throw;
            }
        }
    }

    /// <summary>再生Taskへmixer位置を渡し、再生区間の寿命と排出を管理します。</summary>
    protected override void OnPlaybackStarting()
    {
        lock (playbackControlSync)
        {
            BassAudioSession session = ResourceSession;
            session.SetCallbackOutputPaused(true);
            try
            {
                session.ThrowIfCallbackOutputFailed();
                if (realtimeScheduler != null)
                {
                    throw new InvalidOperationException("A prior realtime BMS playback segment is still owned.");
                }
                RestoreInputMixerForPlayback(session);
                BassAudioPlayer.SetBmsTempoChange(base.PlaybackRate);
                session.ResetCallbackOutputProgress();
                long segmentStartFrame = AudioFrameMath.TimeToFrame(
                    currentTime < TimeSpan.Zero ? TimeSpan.Zero : currentTime,
                    AudioSchedule.SampleRate);
                segmentStartFrame = System.Math.Min(
                    segmentStartFrame,
                    AudioFrameMath.TimeToFrame(Duration < TimeSpan.Zero ? TimeSpan.Zero : Duration, AudioSchedule.SampleRate));
                realtimeScheduler = new BmsRealtimeAudioScheduler(
                    AudioSchedule,
                    AudioResourcesByIndex,
                    session,
                    realtimeMixerNative,
                    Duration,
                    segmentStartFrame,
                    base.PlaybackRate,
                    checked(playbackGeneration++));
                session.SetCallbackOutputPaused(false);
            }
            catch
            {
                session.TryRecordCallbackOutputFailure(AudioPcmRenderStage.ScheduledPlayback);
                if (!session.HasCallbackOutputFailure)
                {
                    session.SetCallbackOutputPaused(false);
                }
                throw;
            }
        }
    }

    /// <summary>次のreservationを行い、callback故障を再生Taskへ返します。</summary>
    protected override PlaybackTickResult OnPlaybackTick(TimeSpan wallClockTime)
    {
        lock (playbackControlSync)
        {
            BassAudioPlayer.CheckOutputHealth();
            realtimeScheduler?.Tick();

            BmsRealtimeAudioScheduler? scheduler = realtimeScheduler;
            return scheduler == null
                ? new PlaybackTickResult(wallClockTime, wallClockTime >= Duration)
                : new PlaybackTickResult(scheduler.CurrentTime, scheduler.IsOutputDrained);
        }
    }

    /// <summary>再生ループ終了後に予約voiceとtempo処理器を解放し、Stopと次曲移行へ完了を返します。</summary>
    protected override void OnPlaybackStopping()
    {
        lock (playbackControlSync)
        {
            BassAudioSession? session = resourceSession;
            if (session == null)
            {
                return;
            }
            session.SetCallbackOutputPaused(true);
            bool restoreInput = realtimeScheduler?.InputEndTransitionAttempted == true;
            var failures = new List<Exception>();
            if (realtimeScheduler != null)
            {
                try
                {
                    realtimeScheduler.Dispose();
                    realtimeScheduler = null;
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            try
            {
                BassAudioPlayer.ResetTempoChange();
                if (BassAudioPlayer.OutputMixerHandle != session.MixerHandle)
                {
                    throw new InvalidOperationException("The BMS tempo output could not be released after playback.");
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            if (restoreInput)
            {
                try
                {
                    RestoreInputMixerAfterNaturalEnd(session);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            if (failures.Count == 0 && !session.HasCallbackOutputFailure)
            {
                try
                {
                    session.ResetCallbackOutputProgress();
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            if (failures.Count != 0 || session.HasCallbackOutputFailure)
            {
                session.TryRecordCallbackOutputFailure(AudioPcmRenderStage.ScheduledPlayback);
            }
            else
            {
                session.SetCallbackOutputPaused(false);
            }

            ThrowCleanupFailures(failures);
        }
    }

    /// <summary>音声区間をseek位置から再構築し、操作前のpause状態を保持します。</summary>
    protected override void MoveTo(TimeSpan time)
    {
        lock (playbackControlSync)
        {
            if (realtimeScheduler == null || resourceSession == null)
            {
                base.MoveTo(time);
                return;
            }

            TimeSpan boundedTime = time < TimeSpan.Zero
                ? TimeSpan.Zero
                : time > Duration ? Duration : time;
            bool resumeAfterSeek = PlayState == PlayState.Playing;
            BassAudioSession session = resourceSession;
            session.SetCallbackOutputPaused(true);
            bool restoreInput = realtimeScheduler.InputEndTransitionAttempted;
            try
            {
                realtimeScheduler.Dispose();
                realtimeScheduler = null;
                BassAudioPlayer.ResetTempoChange();
                if (BassAudioPlayer.OutputMixerHandle != session.MixerHandle)
                {
                    throw new InvalidOperationException("The BMS tempo output could not be released before seek.");
                }
                if (restoreInput)
                {
                    RestoreInputMixerAfterNaturalEnd(session);
                }
                session.ResetCallbackOutputProgress();
                base.MoveTo(boundedTime);
                BassAudioPlayer.SetBmsTempoChange(base.PlaybackRate);
                long songFrame = AudioFrameMath.TimeToFrame(boundedTime, AudioSchedule.SampleRate);
                songFrame = System.Math.Min(
                    songFrame,
                    AudioFrameMath.TimeToFrame(Duration < TimeSpan.Zero ? TimeSpan.Zero : Duration, AudioSchedule.SampleRate));
                realtimeScheduler = new BmsRealtimeAudioScheduler(
                    AudioSchedule,
                    AudioResourcesByIndex,
                    session,
                    realtimeMixerNative,
                    Duration,
                    songFrame,
                    base.PlaybackRate,
                    checked(playbackGeneration++));
                realtimeScheduler.ResetControlInterval();
                if (resumeAfterSeek)
                {
                    session.SetCallbackOutputPaused(false);
                }
            }
            catch (Exception exception)
            {
                session.TryRecordCallbackOutputFailure(AudioPcmRenderStage.ScheduledPlayback);
                if (resumeAfterSeek)
                {
                    session.SetCallbackOutputPaused(false);
                }
                else
                {
                    // Pause中はnative graphを凍結したまま、Start Taskの終了所有者へ失敗を渡す。
                    FailPlayback(exception);
                }
                throw;
            }
        }
    }

    /// <summary>再生に使う音源を事前に復号します。</summary>
    public override void LoadResources() => LoadResources(asParallel: true);

    /// <summary>再生に使う音源を、指定に応じた並列度で事前に復号します。</summary>
    public void LoadResources(bool asParallel) => LoadResources(asParallel, observer: null);

    internal void LoadResources(bool asParallel, AudioSourceLoadPipelineObserver? observer)
    {
        string basePath = Path.GetDirectoryName(base.Bms.Path) ?? string.Empty;
        int[] requiredIndices = BmsAudioFrameSchedule.GetRequiredAudioIndices(base.Bms);
        var requests = new List<AudioLoadRequest>(requiredIndices.Length);
        foreach (int index in requiredIndices)
        {
            string resourceName = base.Bms.WavArray[index];
            string? path = FindAudioPath(basePath, resourceName);
            if (path != null)
            {
                requests.Add(new AudioLoadRequest(index, resourceName, path));
            }
        }

        float sourceGain = ResourceSourceGain;
        AudioSourceLoadResult<BmsAudioResource> result = AudioSourceLoadPipeline.Load(
            base.Bms.WavArray.Length,
            requests,
            asParallel,
            (decoded, path, _) => new BmsAudioResource(path, decoded, sourceGain),
            observer);

        foreach (AudioLoadFailure failure in result.Failures)
        {
            if (failure.Exception is not AudioSourceLoadException { IsInputFailure: true })
            {
                throw new AudioSourceFatalException(
                    "The audio source pipeline returned a failure that is not caused by an input file.",
                    failure.Exception);
            }
        }

        AudioSourceOmission[] omissions = result.Failures
            .OrderBy(failure => failure.Index)
            .GroupBy(failure => GetNormalizedPath(failure.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => new AudioSourceOmission(group.First().ResourceName, group.Key))
            .ToArray();
        LogAudioOmissions(omissions, result.Failures);

        int requestedPathCount = requests
            .Select(request => GetNormalizedPath(request.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (requestedPathCount > 0 && omissions.Length == requestedPathCount)
        {
            AudioLoadFailure failure = result.Failures
                .OrderBy(item => item.Index)
                .First();
            string stage = GetFailureStage(failure.Exception);
            string nativeError = GetNativeError(failure.Exception);
            var chartFailure = new InvalidDataException(
                string.Format(BeMusicSeeker.Properties.Resources.AudioRequiredResourceLoadFailureFormat,
                    failure.ResourceName, failure.Path, stage, nativeError),
                failure.Exception);
            throw chartFailure;
        }

        omittedAudioSources = Array.AsReadOnly(omissions);
        audioResourcesByIndex = Array.AsReadOnly(result.ResourcesByIndex);
        using (BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation())
        {
            BassAudioSession? session = BassAudioPlayer.CurrentSessionForAdmittedOperation;
            if (session?.State != BassAudioSessionState.Active || session.MixerHandle == 0)
            {
                throw new InvalidOperationException("An active input mixer session is required for BMS playback.");
            }

            if (session.CoreDeviceIndex >= 0)
            {
                ManagedBass.Bass.CurrentDevice = session.CoreDeviceIndex;
            }
            ManagedBass.ChannelInfo mixerInfo = ManagedBass.Bass.ChannelGetInfo(session.MixerHandle);
            if (mixerInfo.Frequency <= 0 || mixerInfo.Channels <= 0)
            {
                throw new InvalidOperationException("The input mixer reported an invalid Float32 format.");
            }

            resourceSession = session;
            audioSchedule = BmsAudioFrameSchedule.Create(base.Bms, mixerInfo.Frequency);
        }
        durationProvider = () => base.MusicDuration;
        base.MusicDuration = base.Bms.Measures.SelectMany(measure =>
            new ReadOnlyCollection<Func<IList<BMSFile.Chart.Note>>>[5]
            {
                measure.GetPropertiesAllBgmNotes,
                measure.GetPropertiesAll1PVisNotes,
                measure.GetPropertiesAll2PVisNotes,
                measure.GetPropertiesAll1PLngNotes,
                measure.GetPropertiesAll2PLngNotes
            }
            .SelectMany(properties => properties)
            .SelectMany(getter => getter())
            .Select(note => note.AbsoluteTime + (audioResourcesByIndex[note.Index]?.Duration ?? TimeSpan.Zero)))
            .DefaultIfEmpty(TimeSpan.Zero)
            .Max();
        base.BgaDuration = TimeSpan.Zero;
    }

    /// <summary>次曲の解析前に旧曲sourceを停止・解放し、未確認をSourceReleaseで通知します。</summary>
    internal virtual void DisposeBeforeNextSong()
    {
        // 再生taskのResetPlaybackStateも旧配列を参照するため、配列を置き換える前に停止・合流します。
        Stop();
        DisposeAudioSourcesAfterUse();
    }

    /// <summary>音声使用終了後に譜面資源を解放し、全sourceのnative解放を確認します。</summary>
    /// <remarks>
    /// 呼出し前に再生または変換が終了し、sourceを参照する処理がないことが必要です。
    /// sourceをすべて解放試行し、native解放を確認できない場合はsessionが所有を維持したまま失敗します。
    /// </remarks>
    internal void DisposeAudioSourcesAfterUse()
    {
        Exception? disposalFailure = null;
        try
        {
            base.Dispose(disposing: true);
        }
        catch (Exception exception)
        {
            disposalFailure = exception;
        }

        if (disposalFailure != null)
        {
            throw disposalFailure;
        }

        audioResourcesByIndex = Array.AsReadOnly(Array.Empty<BmsAudioResource?>());
        audioSchedule = null;
        resourceSession = null;
    }

    /// <summary>再生Taskを合流して音声resourceと基底player資源を解放します。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAudioSourcesAfterUse();
        }
        else
        {
            base.Dispose(disposing: false);
        }
    }

    private static string? FindAudioPath(string basePath, string resourceName)
    {
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return null;
        }

        string? firstCandidate = null;
        foreach (string item in Resources.NormalizeExtension(resourceName))
        {
            string path = Path.Combine(basePath, item);
            firstCandidate ??= path;
            if (LongPathFileSystem.FileExists(path))
            {
                return path;
            }
        }

        string fileName = Path.GetFileName(resourceName);
        if (!string.Equals(resourceName, fileName, StringComparison.Ordinal))
        {
            foreach (string item in Resources.NormalizeExtension(fileName))
            {
                string path = Path.Combine(basePath, item);
                firstCandidate ??= path;
                if (LongPathFileSystem.FileExists(path))
                {
                    return path;
                }
            }
        }

        // 明示された非空参照が見つからない場合も、最初に探索したpathをpipelineへ渡して
        // 読取り失敗として警告・省略数・全件失敗判定へ一貫して反映します。
        return firstCandidate;
    }

    private static void RestoreInputMixerForPlayback(BassAudioSession session) =>
        ConfigureInputMixer(session, resetEndedPosition: true);

    private static void RestoreInputMixerAfterNaturalEnd(BassAudioSession session) =>
        ConfigureInputMixer(session, resetEndedPosition: true);

    private static void ConfigureInputMixer(BassAudioSession session, bool resetEndedPosition)
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

        BassFlags mixerFlags = BassFlags.MixerNonStop | BassFlags.MixerEnd;
        bool locked = false;
        Exception? failure = null;
        bool mixerHadEnded = false;
        try
        {
            if (!Bass.ChannelLock(session.MixerHandle, true))
            {
                Errors error = Bass.LastError;
                throw new InvalidOperationException(
                    "BASS_ChannelLock failed while preparing the BMS input mixer: " + BassNativeErrorFormatter.Format(error));
            }
            locked = true;

            BassFlags currentFlags = Bass.ChannelFlags(
                session.MixerHandle,
                BassFlags.Default,
                BassFlags.Default);
            if (unchecked((int)currentFlags) == -1)
            {
                Errors error = Bass.LastError;
                throw new InvalidOperationException(
                    "BASS_ChannelFlags(read) failed while preparing the BMS input mixer: " + BassNativeErrorFormatter.Format(error));
            }
            mixerHadEnded = currentFlags.HasFlag(BassFlags.MixerEnd);

            BassFlags updatedFlags = Bass.ChannelFlags(
                session.MixerHandle,
                BassFlags.MixerNonStop,
                mixerFlags);
            if (unchecked((int)updatedFlags) == -1)
            {
                Errors error = Bass.LastError;
                throw new InvalidOperationException(
                    "BASS_ChannelFlags(update) failed while preparing the BMS input mixer: " + BassNativeErrorFormatter.Format(error));
            }

            if (resetEndedPosition && mixerHadEnded
                && !Bass.ChannelSetPosition(session.MixerHandle, 0, PositionFlags.Bytes))
            {
                Errors error = Bass.LastError;
                throw new InvalidOperationException(
                    "BASS_ChannelSetPosition failed while restarting the BMS input mixer: " + BassNativeErrorFormatter.Format(error));
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            session.TryRecordCallbackOutputFailure(AudioPcmRenderStage.ScheduledPlayback);
        }
        finally
        {
            if (locked && !Bass.ChannelLock(session.MixerHandle, false))
            {
                Errors error = Bass.LastError;
                var unlockFailure = new InvalidOperationException(
                    "BASS_ChannelLock failed while releasing the BMS input mixer: " + BassNativeErrorFormatter.Format(error));
                session.TryRecordCallbackOutputFailure(AudioPcmRenderStage.ScheduledPlayback);
                failure = failure == null ? unlockFailure : new AggregateException(failure, unlockFailure);
            }
        }

        if (failure != null)
        {
            throw failure;
        }
    }

    private static void ThrowCleanupFailures(IReadOnlyList<Exception> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }
        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException("BMS playback cleanup reported multiple failures.", failures);
    }

    private static string GetNormalizedPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return path;
        }
    }

    private static void LogAudioOmissions(
        IReadOnlyList<AudioSourceOmission> omissions,
        IReadOnlyList<AudioLoadFailure> failures)
    {
        if (omissions.Count == 0)
        {
            return;
        }

        var failuresByPath = failures
            .GroupBy(failure => GetNormalizedPath(failure.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (AudioSourceOmission omission in omissions)
        {
            if (!failuresByPath.TryGetValue(omission.Path, out AudioLoadFailure? failure))
            {
                continue;
            }
            try
            {
                NLogWrapper.GetLogger(nameof(BMSAutoPlayer)).Warn(
                    failure.Exception,
                    "Skipped an audio source that BASS/Vorbis could not decode. resource="
                    + omission.ResourceName
                    + " path=" + omission.Path
                    + " stage=" + GetFailureStage(failure.Exception)
                    + " nativeError=" + GetNativeError(failure.Exception));
            }
            catch
            {
                // ログ障害で譜面の再生や変換結果を置き換えません。
            }
        }
    }

    private static string GetFailureStage(Exception exception)
    {
        return exception switch
        {
            AudioSourceLoadException sourceFailure => sourceFailure.Stage.ToString(),
            BassAudioPlaybackException playbackFailure => playbackFailure.Stage.ToString(),
            _ => exception.GetType().Name
        };
    }

    private static string GetNativeError(Exception exception)
    {
        return exception switch
        {
            AudioSourceLoadException sourceFailure => sourceFailure.NativeErrorCode?.ToString() ?? "none",
            BassAudioPlaybackException playbackFailure => playbackFailure.NativeErrorCode?.ToString() ?? "none",
            _ => "none"
        };
    }

}
