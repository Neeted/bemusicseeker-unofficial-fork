#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;
using Ribbit.Logging;
using Ribbit.Media;
using Ribbit.Media.Audio;
using Ribbit.Util;

namespace Ribbit.BMS;

public class BMSAutoPlayer : BMSPlayer<NullImageLoader>
{
    private IReadOnlyList<BmsAudioSourceOmission> omittedAudioSources = Array.Empty<BmsAudioSourceOmission>();
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
    internal IReadOnlyList<BmsAudioSourceOmission> OmittedAudioSources => omittedAudioSources;

    /// <summary>読み込んだpath共有resourceをWAV index順で取得します。</summary>
    internal IReadOnlyList<BmsAudioResource?> AudioResourcesByIndex => audioResourcesByIndex;

    /// <summary>譜面の発音scheduleを捕捉した実効mixer rateで取得します。</summary>
    internal BmsAudioFrameSchedule AudioSchedule =>
        audioSchedule ?? throw new InvalidOperationException("BMS audio resources have not been loaded.");

    /// <summary>resourceとscheduleを作成したnative mixer sessionを取得します。</summary>
    internal BassAudioSession ResourceSession =>
        resourceSession ?? throw new InvalidOperationException("BMS audio resources have not been loaded.");

    /// <summary>再生中に実際のRealtime予約と曲内frame位置を管理するownerです。</summary>
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

    /// <summary>Pause中もnative output callbackと直列化し、再生時計を凍結します。</summary>
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

    /// <summary>再生Taskの開始前に、予約区間とnative tempoを準備します。</summary>
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
                BassAudioPlayer.SetBmsTempoChange(base.PlaybackRate);
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

    /// <summary>出力故障を確認し、次の発音予約を補充します。</summary>
    /// <param name="playbackTime">共通再生時計から算出された現在の曲内時刻。</param>
    protected override void OnPlaybackTick(TimeSpan playbackTime)
    {
        lock (playbackControlSync)
        {
            BassAudioPlayer.CheckOutputHealth();
            realtimeScheduler?.Tick();
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
            var failures = new List<Exception>();
            if (PlayState != PlayState.Stopped)
            {
                try
                {
                    // 終了tick後の進行中callback故障も、出力を停止してから回収します。
                    BassAudioPlayer.CheckOutputHealth();
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

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
            try
            {
                realtimeScheduler.Dispose();
                realtimeScheduler = null;
                BassAudioPlayer.ResetTempoChange();
                if (BassAudioPlayer.OutputMixerHandle != session.MixerHandle)
                {
                    throw new InvalidOperationException("The BMS tempo output could not be released before seek.");
                }
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
                    ResetPlaybackClock();
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
    public override void LoadResources()
    {
        AdoptPreparedSong(PreparedBmsSong.Prepare(base.Bms, ResourceSourceGain));
    }

    /// <summary>解析済み譜面とPCMを一回採用し、現在の実効出力レートでscheduleを構築します。</summary>
    internal void AdoptPreparedSong(PreparedBmsSong prepared)
    {
        if (!ReferenceEquals(base.Bms, prepared.Chart))
        {
            throw new InvalidOperationException("The prepared chart and playback chart must be identical.");
        }
        BmsAudioResourceLoadResult result = prepared.TakeResources();
        // 出力設定は採用時に捕捉します。PCM本体は複製せず、曲内alias共有も維持します。
        var rebound = new Dictionary<BmsAudioResource, BmsAudioResource>();
        for (int index = 0; index < result.ResourcesByIndex.Length; index++)
        {
            if (result.ResourcesByIndex[index] is BmsAudioResource resource && resource.SourceGain != ResourceSourceGain)
            {
                if (!rebound.TryGetValue(resource, out BmsAudioResource? replacement))
                {
                    replacement = new BmsAudioResource(resource.Path, resource.Audio, ResourceSourceGain);
                    rebound.Add(resource, replacement);
                }
                result.ResourcesByIndex[index] = replacement;
            }
        }

        foreach (BmsAudioLoadFailure failure in result.Failures)
        {
            if (failure.Exception is not AudioSourceLoadException { IsInputFailure: true })
            {
                throw new AudioSourceFatalException(
                    "The BMS audio loader returned a failure that is not caused by an input file.",
                    failure.Exception);
            }
        }

        BmsAudioSourceOmission[] omissions = result.Failures
            .OrderBy(failure => failure.Index)
            .Select(failure => new BmsAudioSourceOmission(failure.ResourceName, failure.NormalizedPath))
            .ToArray();
        LogAudioOmissions(omissions, result.Failures);

        if (result.UniquePathCount > 0 && omissions.Length == result.UniquePathCount)
        {
            BmsAudioLoadFailure failure = result.Failures
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

        BassAudioSession session;
        BmsAudioFrameSchedule schedule;
        try
        {
            using (BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation())
            {
                BassAudioSession? currentSession = BassAudioPlayer.CurrentSessionForAdmittedOperation;
                if (currentSession?.State != BassAudioSessionState.Active || currentSession.MixerHandle == 0)
                {
                    throw new InvalidOperationException("An active input mixer session is required for BMS playback.");
                }

                if (currentSession.CoreDeviceIndex >= 0)
                {
                    ManagedBass.Bass.CurrentDevice = currentSession.CoreDeviceIndex;
                }
                ManagedBass.ChannelInfo mixerInfo = ManagedBass.Bass.ChannelGetInfo(currentSession.MixerHandle);
                if (mixerInfo.Frequency <= 0 || mixerInfo.Channels <= 0)
                {
                    throw new InvalidOperationException("The input mixer reported an invalid Float32 format.");
                }

                session = currentSession;
                schedule = BmsAudioFrameSchedule.Create(base.Bms, mixerInfo.Frequency);
            }
        }
        catch (AudioSourceFatalException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AudioSourceFatalException(
                "The BMS audio session or schedule could not be prepared.",
                exception);
        }

        omittedAudioSources = Array.AsReadOnly(omissions);
        audioResourcesByIndex = Array.AsReadOnly(result.ResourcesByIndex);
        resourceSession = session;
        audioSchedule = schedule;
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

    /// <summary>次曲の出力接続前に旧曲sourceを停止・解放し、未確認をSourceReleaseで通知します。</summary>
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

    private static void LogAudioOmissions(
        IReadOnlyList<BmsAudioSourceOmission> omissions,
        IReadOnlyList<BmsAudioLoadFailure> failures)
    {
        if (omissions.Count == 0)
        {
            return;
        }

        var failuresByPath = failures
            .GroupBy(failure => failure.NormalizedPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (BmsAudioSourceOmission omission in omissions)
        {
            if (!failuresByPath.TryGetValue(omission.Path, out BmsAudioLoadFailure? failure))
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
