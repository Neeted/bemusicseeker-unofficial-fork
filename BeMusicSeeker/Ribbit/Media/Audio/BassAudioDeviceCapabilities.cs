using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using ManagedBass;

namespace Ribbit.Media.Audio;

/// <summary>機器能力照会が公開できる結果の状態です。</summary>
internal enum AudioDeviceCapabilityStatus
{
    /// <summary>照会でき、少なくとも一つの能力を確認しました。</summary>
    Available,

    /// <summary>照会は完了しましたが、要求候補に対応する能力がありません。</summary>
    Unsupported,

    /// <summary>音声APIまたは機器の照会が失敗しました。</summary>
    Failed,

    /// <summary>再生session使用中または解放保留のため照会を行いませんでした。</summary>
    Busy
}

/// <summary>排他WASAPIのレートと形式の一組に対する照会結果です。</summary>
internal readonly record struct AudioDeviceFormatCapability(
    SampleRate Rate,
    SampleFormat Format,
    bool IsSupported);

/// <summary>機器能力照会一回分の変更不能な入力です。</summary>
internal sealed class AudioDeviceCapabilityRequest
{
    /// <summary>保存済みの希望値を含む機器能力照会要求を作成します。</summary>
    internal AudioDeviceCapabilityRequest(
        AudioDriver backend,
        string deviceIdentity,
        string deviceName,
        SampleRate savedRate,
        SampleFormat savedFormat)
    {
        Backend = backend;
        DeviceIdentity = string.IsNullOrWhiteSpace(deviceIdentity) ? null : deviceIdentity;
        DeviceName = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName;
        SavedRate = savedRate;
        SavedFormat = savedFormat;
    }

    /// <summary>選択した出力方式を取得します。</summary>
    internal AudioDriver Backend { get; }

    /// <summary>選択した機器識別子を取得します。Defaultの場合はnullです。</summary>
    internal string DeviceIdentity { get; }

    /// <summary>保存済みの機器名を取得します。</summary>
    internal string DeviceName { get; }

    /// <summary>保存済みの再生レート希望値を取得します。</summary>
    internal SampleRate SavedRate { get; }

    /// <summary>保存済みの再生形式希望値を取得します。</summary>
    internal SampleFormat SavedFormat { get; }
}

/// <summary>一回の機器能力照会結果です。保持期間は設定画面の表示に限ります。</summary>
internal sealed class AudioDeviceCapabilityResult
{
    /// <summary>コレクションを複製して読取専用にした照会結果を作成します。</summary>
    internal AudioDeviceCapabilityResult(
        AudioDriver backend,
        string deviceIdentity,
        string deviceName,
        AudioDeviceCapabilityStatus status,
        IReadOnlyList<SampleRate> supportedRates = null,
        IReadOnlyList<AudioDeviceFormatCapability> formatCapabilities = null,
        SampleFormat endpointFormat = SampleFormat.UNKNOWN,
        SampleFormat rightEndpointFormat = SampleFormat.UNKNOWN,
        int endpointContainerBits = 0,
        int endpointEffectiveBits = 0,
        int rightEndpointContainerBits = 0,
        int rightEndpointEffectiveBits = 0,
        int endpointChannels = 0,
        string failureStage = null,
        string nativeErrorSource = null,
        Errors? nativeErrorCode = null,
        IReadOnlyList<BassAudioBackendAttempt> attempts = null)
    {
        Backend = backend;
        DeviceIdentity = deviceIdentity;
        DeviceName = deviceName;
        Status = status;
        SupportedRates = Array.AsReadOnly(supportedRates?.Distinct().ToArray() ?? Array.Empty<SampleRate>());
        FormatCapabilities = Array.AsReadOnly(
            formatCapabilities?.ToArray() ?? Array.Empty<AudioDeviceFormatCapability>());
        EndpointFormat = endpointFormat;
        RightEndpointFormat = rightEndpointFormat;
        EndpointContainerBits = endpointContainerBits;
        EndpointEffectiveBits = endpointEffectiveBits;
        RightEndpointContainerBits = rightEndpointContainerBits;
        RightEndpointEffectiveBits = rightEndpointEffectiveBits;
        EndpointChannels = endpointChannels;
        FailureStage = failureStage;
        NativeErrorSource = nativeErrorSource;
        NativeErrorCode = nativeErrorCode;
        Attempts = Array.AsReadOnly(attempts?.ToArray() ?? Array.Empty<BassAudioBackendAttempt>());
    }

    /// <summary>照会した出力方式を取得します。</summary>
    internal AudioDriver Backend { get; }

    /// <summary>解決した機器識別子を取得します。</summary>
    internal string DeviceIdentity { get; }

    /// <summary>解決した機器名を取得します。</summary>
    internal string DeviceName { get; }

    /// <summary>明示照会の結果状態を取得します。</summary>
    internal AudioDeviceCapabilityStatus Status { get; }

    /// <summary>選択機器が明示的に受理したレートを取得します。</summary>
    internal IReadOnlyList<SampleRate> SupportedRates { get; }

    /// <summary>WASAPI排他で照会した各レートと形式の組を取得します。</summary>
    internal IReadOnlyList<AudioDeviceFormatCapability> FormatCapabilities { get; }

    /// <summary>機器または共有ミックスから読み取った形式を取得します。</summary>
    internal SampleFormat EndpointFormat { get; }

    /// <summary>ASIO右チャンネルの形式を取得します。</summary>
    internal SampleFormat RightEndpointFormat { get; }

    /// <summary>機器が報告した場合にサンプル容器幅を取得します。</summary>
    internal int EndpointContainerBits { get; }

    /// <summary>機器が報告した場合に有効精度を取得します。</summary>
    internal int EndpointEffectiveBits { get; }

    /// <summary>ASIO右チャンネルのサンプル容器幅を取得します。</summary>
    internal int RightEndpointContainerBits { get; }

    /// <summary>ASIO右チャンネルの有効精度を取得します。</summary>
    internal int RightEndpointEffectiveBits { get; }

    /// <summary>機器または共有ミックスのチャンネル数を取得します。</summary>
    internal int EndpointChannels { get; }

    /// <summary>照会に失敗した場合に処理段階を取得します。</summary>
    internal string FailureStage { get; }

    /// <summary>照会エラーを返したネイティブAPIを取得します。</summary>
    internal string NativeErrorSource { get; }

    /// <summary>失敗直後に取得したネイティブエラーを取得します。</summary>
    internal Errors? NativeErrorCode { get; }

    /// <summary>明示照会中のネイティブ判定を順に取得します。</summary>
    internal IReadOnlyList<BassAudioBackendAttempt> Attempts { get; }
}

/// <summary>ASIO能力照会のsession所有と解放確認を管理します。</summary>
internal static class BassAudioCapabilitySession
{
    /// <summary>
    /// 既存lifecycleが排他中のときにASIO機器を照会し、取得したsessionを解放確認まで所有します。
    /// </summary>
    internal static AudioDeviceCapabilityResult QueryAsio(
        AudioDeviceCapabilityRequest request,
        BassAudioSessionLifecycle sessionLifecycle,
        IAudioSessionNativeBoundary sessionNative,
        Func<BassAudioSession, AudioDeviceCapabilityResult> query,
        Action<Exception> logFailure = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sessionLifecycle);
        ArgumentNullException.ThrowIfNull(sessionNative);
        ArgumentNullException.ThrowIfNull(query);
        if (request.Backend != AudioDriver.Asio)
        {
            throw new ArgumentException("ASIO capabilities require an ASIO request.", nameof(request));
        }

        var device = new BassAudioPlayer.DeviceDescriptor(request.DeviceName, request.DeviceIdentity);
        if (!sessionLifecycle.TryBegin(
                BassAudioPlayer.DeviceDriver.ASIO,
                device,
                out BassAudioSession session))
        {
            return new AudioDeviceCapabilityResult(
                request.Backend,
                request.DeviceIdentity,
                request.DeviceName,
                AudioDeviceCapabilityStatus.Busy);
        }

        session.ActualBackend = BassAudioPlayer.DeviceDriver.ASIO;
        AudioDeviceCapabilityResult result;
        Exception primaryException = null;
        try
        {
            result = query(session);
        }
        catch (Exception exception)
        {
            primaryException = exception;
            try
            {
                logFailure?.Invoke(exception);
            }
            catch
            {
                // 診断記録の失敗でnative資源の解放を中断しません。
            }
            result = new AudioDeviceCapabilityResult(
                request.Backend,
                request.DeviceIdentity,
                request.DeviceName,
                AudioDeviceCapabilityStatus.Failed,
                failureStage: "capability query");
        }

        bool released = BassAudioSessionCleanup.Release(session, sessionNative, primaryException);
        sessionLifecycle.CompleteCleanup(session);
        if (released)
        {
            return result;
        }

        var attempts = new List<BassAudioBackendAttempt>(result.Attempts)
        {
            new(
                "audio session cleanup",
                null,
                null,
                "failed; native ownership is retained for cleanup")
        };
        return new AudioDeviceCapabilityResult(
            request.Backend,
            result.DeviceIdentity,
            result.DeviceName,
            AudioDeviceCapabilityStatus.Failed,
            failureStage: result.Status == AudioDeviceCapabilityStatus.Failed
                ? result.FailureStage
                : "audio session cleanup",
            nativeErrorSource: result.Status == AudioDeviceCapabilityStatus.Failed
                ? result.NativeErrorSource
                : null,
            nativeErrorCode: result.Status == AudioDeviceCapabilityStatus.Failed
                ? result.NativeErrorCode
                : null,
            attempts: attempts);
    }
}
