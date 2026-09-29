using System;
using System.Collections.Generic;
using BeMusicSeeker.Models;
using ManagedBass;
using Ribbit.Media;

namespace Ribbit.Media.Audio;

/// <summary>一回の音声backend交渉へ渡す変更不能な要求です。</summary>
internal sealed class BassAudioNegotiationRequest
{
    /// <summary>backend交渉へ渡す出力条件、SRC品質、再生ミキサーthread数を捕捉します。</summary>
    /// <param name="playerMixerThreadCount">通常再生またはデバイステストで使う1～4のnativeミキサーthread数です。</param>
    internal BassAudioNegotiationRequest(
        BassAudioPlayer.DeviceDriver backend,
        BassAudioPlayer.DeviceDescriptor device,
        SampleRate rate,
        SampleFormat format,
        float latencyMilliseconds,
        bool eventModeRequested = false,
        int sampleRateConversionQuality = AudioResamplingQuality.Default,
        AudioOutputPurpose purpose = AudioOutputPurpose.Playback,
        int playerMixerThreadCount = BassMixerThreadConfigurator.RealtimeDefaultThreadCount)
    {
        Backend = backend;
        Device = device;
        Rate = rate;
        Format = format;
        LatencyMilliseconds = latencyMilliseconds;
        EventModeRequested = eventModeRequested;
        SampleRateConversionQuality = AudioResamplingQuality.Validate(sampleRateConversionQuality);
        PlayerMixerThreadCount = BassMixerThreadConfigurator.ValidateRealtimeThreadCount(playerMixerThreadCount);
        Purpose = purpose;
    }

    /// <summary>Gets the requested backend.</summary>
    internal BassAudioPlayer.DeviceDriver Backend { get; }

    /// <summary>Gets the requested endpoint.</summary>
    internal BassAudioPlayer.DeviceDescriptor Device { get; }

    /// <summary>Gets the requested sample rate, or <see cref="SampleRate.AUTO"/>.</summary>
    internal SampleRate Rate { get; }

    /// <summary>Gets the requested sample format.</summary>
    internal SampleFormat Format { get; }

    /// <summary>Gets the requested backend latency in milliseconds.</summary>
    internal float LatencyMilliseconds { get; }

    /// <summary>イベント駆動の出力を要求したか取得します。</summary>
    internal bool EventModeRequested { get; }

    /// <summary>開始時に捕捉した標本化周波数変換品質を取得します。</summary>
    internal int SampleRateConversionQuality { get; }

    /// <summary>通常再生または設定テストで指定されたnativeミキサーthread数を取得します。</summary>
    internal int PlayerMixerThreadCount { get; }

    /// <summary>通常再生または選択条件テストの用途を取得します。</summary>
    internal AudioOutputPurpose Purpose { get; }

    /// <summary>選択した出力条件の代替を禁止する要求か取得します。</summary>
    internal bool RequiresExactSelection => Purpose == AudioOutputPurpose.DeviceTest;
}

/// <summary>
/// Records one native negotiation decision without relying on the mutable native last-error slot.
/// </summary>
internal sealed class BassAudioBackendAttempt
{
    /// <summary>Creates a diagnostic record for one native decision.</summary>
    internal BassAudioBackendAttempt(
        string stage,
        string nativeErrorSource,
        Errors? nativeErrorCode,
        string outcome)
    {
        Stage = stage;
        NativeErrorSource = nativeErrorSource;
        NativeErrorCode = nativeErrorCode;
        Outcome = outcome;
    }

    /// <summary>Gets the initialization stage.</summary>
    internal string Stage { get; }

    /// <summary>Gets the native API that supplied the captured error.</summary>
    internal string NativeErrorSource { get; }

    /// <summary>Gets the error captured immediately after failure, if any.</summary>
    internal Errors? NativeErrorCode { get; }

    /// <summary>Gets a compact description of the accepted or rejected value.</summary>
    internal string Outcome { get; }
}

/// <summary>利用者の要求から独立して、backendが受理した値を保持します。</summary>
internal sealed class BassAudioBackendResult
{
    /// <summary>backendが受理した出力値、形式精度、交渉試行を保持する結果を作成します。</summary>
    /// <param name="endpointContainerBits">機器が報告したサンプル容器幅です。</param>
    /// <param name="endpointEffectiveBits">機器が報告した有効精度です。</param>
    internal BassAudioBackendResult(
        BassAudioNegotiationRequest request,
        BassAudioPlayer.DeviceDescriptor actualDevice,
        SampleRate actualRate,
        SampleFormat engineFormat,
        SampleFormat endpointFormat,
        double latencyMilliseconds,
        int mixerHandle,
        IReadOnlyList<BassAudioBackendAttempt> attempts,
        string fallbackReason,
        int actualChannels = 2,
        SampleFormat callbackFormat = SampleFormat.UNKNOWN,
        int endpointContainerBits = 0,
        int endpointEffectiveBits = 0)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        ActualDevice = actualDevice;
        ActualRate = actualRate;
        EngineFormat = engineFormat;
        CallbackFormat = callbackFormat == SampleFormat.UNKNOWN ? engineFormat : callbackFormat;
        EndpointFormat = endpointFormat;
        LatencyMilliseconds = latencyMilliseconds;
        MixerHandle = mixerHandle;
        Attempts = attempts ?? throw new ArgumentNullException(nameof(attempts));
        FallbackReason = fallbackReason;
        ActualChannels = actualChannels;
        EndpointContainerBits = endpointContainerBits;
        EndpointEffectiveBits = endpointEffectiveBits;
    }

    /// <summary>Gets the original caller request.</summary>
    internal BassAudioNegotiationRequest Request { get; }

    /// <summary>Gets the endpoint selected by the native backend.</summary>
    internal BassAudioPlayer.DeviceDescriptor ActualDevice { get; }

    /// <summary>Gets the sample rate read back from the backend.</summary>
    internal SampleRate ActualRate { get; }

    /// <summary>callbackへ供給するmixerのformatを取得します。</summary>
    internal SampleFormat EngineFormat { get; }

    /// <summary>backend callbackへ渡すdataのformatを取得します。</summary>
    internal SampleFormat CallbackFormat { get; }

    /// <summary>
    /// Gets the endpoint format observed by the backend, or <see cref="SampleFormat.UNKNOWN"/>
    /// when this boundary does not read the endpoint bit depth.
    /// </summary>
    internal SampleFormat EndpointFormat { get; }

    /// <summary>Gets the measured output latency in milliseconds.</summary>
    internal double LatencyMilliseconds { get; }

    /// <summary>Gets the channel count accepted by the endpoint or callback.</summary>
    internal int ActualChannels { get; }

    /// <summary>backendが報告する場合に機器のサンプル容器幅を取得します。</summary>
    internal int EndpointContainerBits { get; }

    /// <summary>backendが報告する場合に機器の有効精度を取得します。</summary>
    internal int EndpointEffectiveBits { get; }

    /// <summary>Gets the decode mixer owned by the audio session.</summary>
    internal int MixerHandle { get; }

    /// <summary>Gets the ordered native decisions made during negotiation.</summary>
    internal IReadOnlyList<BassAudioBackendAttempt> Attempts { get; }

    /// <summary>Gets why a value other than the first-choice format was used.</summary>
    internal string FallbackReason { get; }

    /// <summary>
    /// Creates a copy that prepends failures from earlier backend attempts.
    /// </summary>
    internal BassAudioBackendResult WithEarlierAttempts(
        IReadOnlyList<BassAudioBackendAttempt> earlierAttempts,
        string earlierFallbackReason)
    {
        ArgumentNullException.ThrowIfNull(earlierAttempts);
        if (earlierAttempts.Count == 0 && string.IsNullOrWhiteSpace(earlierFallbackReason))
        {
            return this;
        }

        var attempts = new List<BassAudioBackendAttempt>(earlierAttempts.Count + Attempts.Count);
        attempts.AddRange(earlierAttempts);
        attempts.AddRange(Attempts);
        string fallbackReason = string.IsNullOrWhiteSpace(earlierFallbackReason)
            ? FallbackReason
            : string.IsNullOrWhiteSpace(FallbackReason)
                ? earlierFallbackReason
                : earlierFallbackReason + "; " + FallbackReason;
        return new BassAudioBackendResult(
            Request,
            ActualDevice,
            ActualRate,
            EngineFormat,
            EndpointFormat,
            LatencyMilliseconds,
            MixerHandle,
            attempts,
            fallbackReason,
            ActualChannels,
            CallbackFormat,
            EndpointContainerBits,
            EndpointEffectiveBits);
    }
}
