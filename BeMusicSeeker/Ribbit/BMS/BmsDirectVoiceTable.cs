#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using ManagedBass;
using ManagedBass.Mix;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace Ribbit.BMS;

/// <summary>
/// 打切り後の実効出力長が短い発音を直接加算で合成するための、音源・音源窓ごとの変換済み波形の表です。
/// </summary>
/// <remarks>
/// 毎秒数万回鳴る短い発音ごとにnativeストリームを作成・登録・回収すると、保持数に比例して遅くなります。
/// この表は、入力ミキサーと同じレート・チャンネル配置の波形を曲の準備時に一度だけ作り、
/// <see cref="BmsScheduledAudioMixer"/>が予約範囲ごとに一つのブロックへ加算できるようにします。
/// 波形は現行のvoiceと同じBASSmixの経路(同じSRC品質・行列・音源ゲイン、NORAMPIN)で先頭の必要な長さだけを
/// 一度レンダリングして得るため、SRCや行列の計算をアプリ側に複製しません。
/// 表は予約表(schedule、resource)と同じ寿命を持ち、managedな配列だけを保持してnative資源を残さず、永続キャッシュにもしません。
/// </remarks>
internal sealed class BmsDirectVoiceTable
{
    /// <summary>直接合成の対象とする、打切り後の実効出力長の上限(ミリ秒)です。</summary>
    internal const int MaximumVoiceMilliseconds = 50;

    private const BassFlags TemporaryMixerFlags =
        BassFlags.Float
        | BassFlags.Prescan
        | BassFlags.Decode
        | BassFlags.MixerNonStop;

    private readonly Dictionary<WaveKey, DirectVoiceWave> waves;

    private BmsDirectVoiceTable(
        int sampleRate,
        int channelCount,
        int maximumVoiceFrames,
        Dictionary<WaveKey, DirectVoiceWave> waves)
    {
        SampleRate = sampleRate;
        ChannelCount = channelCount;
        MaximumVoiceFrames = maximumVoiceFrames;
        this.waves = waves;
    }

    /// <summary>波形を作った入力ミキサーの実効sample rateです。</summary>
    internal int SampleRate { get; }

    /// <summary>波形を作った入力ミキサーのchannel数です。波形がない表では0です。</summary>
    internal int ChannelCount { get; }

    /// <summary>直接合成の対象とする実効出力長の上限frame数です。</summary>
    internal int MaximumVoiceFrames { get; }

    /// <summary>保持する波形の数です。</summary>
    internal int WaveCount => waves.Count;

    /// <summary>実効出力長から、発音が直接合成の対象かを判定します。</summary>
    /// <param name="effectiveOutputFrames">打切り後の実効出力frame数です。</param>
    internal bool IsDirect(long effectiveOutputFrames) =>
        effectiveOutputFrames > 0 && effectiveOutputFrames <= MaximumVoiceFrames;

    /// <summary>対象の発音の波形を取得します。</summary>
    /// <param name="resource">発音に使う復号済み音源です。</param>
    /// <param name="audioEvent">対象の発音です。音源窓の始点・終点を鍵にします。</param>
    /// <returns>先頭から打切り後の実効出力長以上のframeを持つ変換済み波形です。</returns>
    /// <exception cref="InvalidOperationException">表の作成時と異なる譜面・音源の組が渡された場合です。</exception>
    internal DirectVoiceWave GetWave(BmsAudioResource resource, in BmsAudioFrameEvent audioEvent)
    {
        if (!waves.TryGetValue(WaveKey.Create(resource, audioEvent), out DirectVoiceWave? wave))
        {
            throw new InvalidOperationException(
                "The direct voice table does not contain the waveform for WAV index " + audioEvent.WavIndex + ".");
        }
        return wave;
    }

    /// <summary>
    /// 予約表の発音から対象の波形を抽出し、現行のBASSmixの経路で一度だけレンダリングして表を作ります。
    /// </summary>
    /// <param name="schedule">実効mixer rateで量子化済みのscheduleです。</param>
    /// <param name="resourcesByIndex">WAV index順の復号済み音源です。</param>
    /// <param name="session">入力ミキサーを所有するactiveなsessionです。</param>
    /// <returns>対象がなければ波形を持たない表を返します。</returns>
    internal static BmsDirectVoiceTable Create(
        BmsAudioFrameSchedule schedule,
        IReadOnlyList<BmsAudioResource?> resourcesByIndex,
        BassAudioSession session)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(resourcesByIndex);
        ArgumentNullException.ThrowIfNull(session);

        int sampleRate = schedule.SampleRate;
        int maximumVoiceFrames = checked((int)((long)sampleRate * MaximumVoiceMilliseconds / 1000));
        var requiredFrames = new Dictionary<WaveKey, (BmsAudioResource Resource, int Frames)>();
        foreach (BmsAudioFrameEvent audioEvent in schedule.Events)
        {
            if ((uint)audioEvent.WavIndex >= (uint)resourcesByIndex.Count)
            {
                continue;
            }

            BmsAudioResource? resource = resourcesByIndex[audioEvent.WavIndex];
            if (resource == null || resource.IsEmpty)
            {
                continue;
            }

            long frames = BmsAudioFrameSchedule.GetEndFrame(audioEvent, resource, sampleRate) - audioEvent.StartFrame;
            if (frames <= 0 || frames > maximumVoiceFrames)
            {
                continue;
            }

            var key = WaveKey.Create(resource, audioEvent);
            if (!requiredFrames.TryGetValue(key, out (BmsAudioResource Resource, int Frames) existing)
                || existing.Frames < frames)
            {
                requiredFrames[key] = (resource, (int)frames);
            }
        }

        var waves = new Dictionary<WaveKey, DirectVoiceWave>(requiredFrames.Count);
        if (requiredFrames.Count == 0)
        {
            return new BmsDirectVoiceTable(sampleRate, 0, maximumVoiceFrames, waves);
        }

        using BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation();
        if (!ReferenceEquals(BassAudioPlayer.CurrentSessionForAdmittedOperation, session)
            || session.State != BassAudioSessionState.Active
            || session.MixerHandle == 0)
        {
            throw new InvalidOperationException("An active input mixer session is required to build the direct voice table.");
        }
        if (session.CoreDeviceIndex >= 0)
        {
            Bass.CurrentDevice = session.CoreDeviceIndex;
        }
        ChannelInfo info = Bass.ChannelGetInfo(session.MixerHandle);
        if (info.Frequency != sampleRate
            || info.Channels is < 1 or > 8
            || !info.Flags.HasFlag(BassFlags.Float)
            || !info.Flags.HasFlag(BassFlags.Decode))
        {
            throw new InvalidOperationException(
                "The input mixer format does not match the schedule used to build the direct voice table.");
        }

        var native = new BassMixerSourceNativeBoundary();
        var renderer = new AudioPcmRenderer(session.MixerHandle, sampleRate, info.Channels);
        var matrices = new Dictionary<uint, float[,]>();
        foreach ((WaveKey key, (BmsAudioResource resource, int frames)) in requiredFrames)
        {
            waves.Add(key, RenderWave(key, resource, frames, session, native, renderer, info.Channels, matrices));
        }
        return new BmsDirectVoiceTable(sampleRate, info.Channels, maximumVoiceFrames, waves);
    }

    /// <summary>
    /// 一つの音源窓を、入力ミキサーと同じ形式の一時decode mixerへ現行のvoiceと同じ設定で接続し、
    /// 先頭の指定frame数だけを取り出します。
    /// </summary>
    private static DirectVoiceWave RenderWave(
        WaveKey key,
        BmsAudioResource resource,
        int frames,
        BassAudioSession session,
        IBassScheduledMixerNativeBoundary native,
        AudioPcmRenderer renderer,
        int channelCount,
        Dictionary<uint, float[,]> matrices)
    {
        BassAudioPlayer? player = null;
        int temporaryMixer = 0;
        bool playerAttached = false;
        var failures = new List<Exception>();
        float[] samples = new float[checked(frames * channelCount)];
        try
        {
            player = new BassAudioPlayer(resource.Path, resource.Audio, session, native, key.SourceEndFrame);
            temporaryMixer = BassMix.CreateMixerStream(renderer.SampleRate, channelCount, TemporaryMixerFlags);
            if (temporaryMixer == 0)
            {
                throw new InvalidOperationException(
                    "BASS_Mixer_StreamCreate failed while building the direct voice table: "
                    + BassNativeErrorFormatter.Format(Bass.LastError));
            }
            session.TrackAdditionalStreamHandle(temporaryMixer);

            uint speakerMask = player.ScheduledSourceSpeakerMask;
            if (!matrices.TryGetValue(speakerMask, out float[,]? matrix))
            {
                matrix = player.CreateScheduledMatrix(channelCount);
                matrices.Add(speakerMask, matrix);
            }
            player.Volume = resource.SourceGain;
            if (key.SourceStartFrame != 0)
            {
                player.SetScheduledSourceFrame(key.SourceStartFrame);
            }

            long lengthBytes = AudioFrameMath.Float32FrameToBytePosition(frames, channelCount);
            if (!native.AddChannelAt(
                    temporaryMixer,
                    player.NativeHandleForScheduledVoice,
                    BmsScheduledAudioMixer.ScheduledSourceFlags,
                    0,
                    lengthBytes))
            {
                throw new InvalidOperationException(
                    "BASS_Mixer_StreamAddChannelEx failed while building the direct voice table: "
                    + BassNativeErrorFormatter.Format(native.GetError()));
            }
            playerAttached = true;
            player.ConfigureRenderVoice(temporaryMixer, matrix);
            BassFlags updatedFlags = native.SetMixerChannelFlags(
                player.NativeHandleForScheduledVoice,
                BassFlags.Default,
                BassFlags.MixerChanPause);
            if (unchecked((int)updatedFlags) == -1)
            {
                throw new InvalidOperationException(
                    "BASS_Mixer_ChannelFlags(resume) failed while building the direct voice table: "
                    + BassNativeErrorFormatter.Format(native.GetError()));
            }

            AudioPcmReadResult read = renderer.ReadFrames(temporaryMixer, samples, frames);
            if (read.FramesRead != frames)
            {
                throw new AudioPcmRenderException(
                    temporaryMixer,
                    AudioPcmRenderStage.UnexpectedEnd,
                    nativeError: Errors.Ended,
                    expectedFrames: frames,
                    actualFrames: read.FramesRead);
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        finally
        {
            if (playerAttached && player != null)
            {
                try
                {
                    player.RemoveRenderVoiceFromMixer(temporaryMixer);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
            if (temporaryMixer != 0)
            {
                try
                {
                    if (Bass.StreamFree(temporaryMixer))
                    {
                        session.ConfirmStreamReleased(temporaryMixer);
                    }
                    else
                    {
                        failures.Add(new InvalidOperationException(
                            "BASS_StreamFree failed for the direct voice table mixer: "
                            + BassNativeErrorFormatter.Format(Bass.LastError)));
                    }
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
            if (player != null)
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
        }

        if (failures.Count != 0)
        {
            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }
            throw new AggregateException(failures);
        }
        return new DirectVoiceWave(samples, frames);
    }

    /// <summary>音源の同一性と音源窓で波形を識別します。</summary>
    private readonly record struct WaveKey(BmsAudioResource Resource, long SourceStartFrame, long? SourceEndFrame)
    {
        internal static WaveKey Create(BmsAudioResource resource, in BmsAudioFrameEvent audioEvent) =>
            new(resource, audioEvent.SourceStartFrame, audioEvent.SourceEndFrame);
    }

    /// <summary>入力ミキサーと同じ形式で変換済みの、先頭から打切り長までの波形です。</summary>
    internal sealed class DirectVoiceWave
    {
        internal DirectVoiceWave(float[] samples, int frameCount)
        {
            Samples = samples;
            FrameCount = frameCount;
        }

        /// <summary>入力ミキサーのchannel数でinterleaveしたFloat32 PCMです。呼出側は変更しません。</summary>
        internal float[] Samples { get; }

        /// <summary>波形のframe数です。</summary>
        internal int FrameCount { get; }
    }
}
