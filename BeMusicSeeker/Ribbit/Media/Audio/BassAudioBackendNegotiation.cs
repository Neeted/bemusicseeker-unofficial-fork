using System;
using System.Collections.Generic;
using ManagedBass;
using Ribbit.Media;

namespace Ribbit.Media.Audio;

/// <summary>
/// Describes the caller's requested values for one backend negotiation attempt.
/// </summary>
internal sealed class BassAudioNegotiationRequest
{
    /// <summary>Creates an immutable backend negotiation request.</summary>
    internal BassAudioNegotiationRequest(
        BassAudioPlayer.DeviceDriver backend,
        BassAudioPlayer.DeviceDescriptor device,
        SampleRate rate,
        SampleFormat format,
        float latencyMilliseconds)
    {
        Backend = backend;
        Device = device;
        Rate = rate;
        Format = format;
        LatencyMilliseconds = latencyMilliseconds;
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

/// <summary>
/// Describes the values accepted by a backend independently from the caller's request.
/// </summary>
internal sealed class BassAudioBackendResult
{
    /// <summary>Creates an immutable successful backend result.</summary>
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
        int actualChannels = 2)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        ActualDevice = actualDevice;
        ActualRate = actualRate;
        EngineFormat = engineFormat;
        EndpointFormat = endpointFormat;
        LatencyMilliseconds = latencyMilliseconds;
        MixerHandle = mixerHandle;
        Attempts = attempts ?? throw new ArgumentNullException(nameof(attempts));
        FallbackReason = fallbackReason;
        ActualChannels = actualChannels;
    }

    /// <summary>Gets the original caller request.</summary>
    internal BassAudioNegotiationRequest Request { get; }

    /// <summary>Gets the endpoint selected by the native backend.</summary>
    internal BassAudioPlayer.DeviceDescriptor ActualDevice { get; }

    /// <summary>Gets the sample rate read back from the backend.</summary>
    internal SampleRate ActualRate { get; }

    /// <summary>Gets the mixer format supplied to the callback.</summary>
    internal SampleFormat EngineFormat { get; }

    /// <summary>
    /// Gets the endpoint format observed by the backend, or <see cref="SampleFormat.UNKNOWN"/>
    /// when this boundary does not read the endpoint bit depth.
    /// </summary>
    internal SampleFormat EndpointFormat { get; }

    /// <summary>Gets the measured output latency in milliseconds.</summary>
    internal double LatencyMilliseconds { get; }

    /// <summary>Gets the channel count accepted by the endpoint or callback.</summary>
    internal int ActualChannels { get; }

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
            ActualChannels);
    }
}
