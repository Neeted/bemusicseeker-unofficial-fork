using System;
using ManagedBass;

namespace Ribbit.Media.Audio;

/// <summary>ミキサーのnative thread数を設定・読戻しする境界です。</summary>
internal interface IBassMixerThreadNativeBoundary
{
    /// <summary>ミキサーのnative thread数を設定します。</summary>
    bool SetMixerThreadCount(int mixerHandle, float threadCount);

    /// <summary>ミキサーのnative thread数を読み戻します。</summary>
    bool GetMixerThreadCount(int mixerHandle, out float threadCount);

    /// <summary>直前のBASS呼出しが返したエラーを取得します。</summary>
    Errors GetMixerThreadError();
}

/// <summary>ManagedBassの属性APIでBASSmixのスレッド数を設定します。</summary>
internal sealed class BassMixerThreadNativeBoundary : IBassMixerThreadNativeBoundary
{
    private const ChannelAttribute MixerThreadsAttribute = (ChannelAttribute)0x15001;

    /// <inheritdoc />
    public bool SetMixerThreadCount(int mixerHandle, float threadCount) =>
        Bass.ChannelSetAttribute(mixerHandle, MixerThreadsAttribute, threadCount);

    /// <inheritdoc />
    public bool GetMixerThreadCount(int mixerHandle, out float threadCount) =>
        Bass.ChannelGetAttribute(mixerHandle, MixerThreadsAttribute, out threadCount);

    /// <inheritdoc />
    public Errors GetMixerThreadError() => Bass.LastError;
}

/// <summary>ミキサーのthread数を要求値へ設定・検証します。</summary>
internal static class BassMixerThreadConfigurator
{
    /// <summary>各ミキサーに設定するnative thread数です。</summary>
    internal static int RequiredThreadCount => System.Math.Min(4, Environment.ProcessorCount);

    /// <summary>指定したミキサーでスレッド数を設定し、同じ値が読み戻せることを確認します。</summary>
    internal static void SetAndConfirm(
        int mixerHandle,
        IBassMixerThreadNativeBoundary native)
    {
        ArgumentNullException.ThrowIfNull(native);
        if (mixerHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mixerHandle));
        }

        float requested = RequiredThreadCount;
        const string setApi = "BASS_ChannelSetAttribute(BASS_ATTRIB_MIXER_THREADS)";
        const string getApi = "BASS_ChannelGetAttribute(BASS_ATTRIB_MIXER_THREADS)";
        bool set;
        try
        {
            set = native.SetMixerThreadCount(mixerHandle, requested);
        }
        catch (Exception exception)
        {
            throw Failure(setApi, null, "Setting mixer thread count threw an exception.", exception);
        }

        if (!set)
        {
            Errors error = native.GetMixerThreadError();
            throw Failure(
                setApi,
                error,
                "Setting mixer thread count failed: " + BassNativeErrorFormatter.Format(error));
        }

        bool read;
        float actual;
        try
        {
            read = native.GetMixerThreadCount(mixerHandle, out actual);
        }
        catch (Exception exception)
        {
            throw Failure(getApi, null, "Reading mixer thread count threw an exception.", exception);
        }

        if (!read)
        {
            Errors error = native.GetMixerThreadError();
            throw Failure(
                getApi,
                error,
                "Reading mixer thread count failed: " + BassNativeErrorFormatter.Format(error));
        }

        if (actual != requested)
        {
            throw Failure(
                getApi,
                null,
                $"BASS reported {actual} mixer threads after requesting {requested}.");
        }
    }

    private static BassMixerThreadConfigurationException Failure(
        string nativeApi,
        Errors? nativeErrorCode,
        string message,
        Exception innerException = null) =>
        new(nativeApi, nativeErrorCode, message, innerException);
}

/// <summary>ミキサーのスレッド設定で発生したnative失敗とエラー値を保持します。</summary>
internal sealed class BassMixerThreadConfigurationException : InvalidOperationException
{
    /// <summary>native API、native error、診断メッセージを保持する例外を作成します。</summary>
    /// <param name="nativeApi">失敗したnative API名です。</param>
    /// <param name="nativeErrorCode">取得できた場合のnative errorです。</param>
    /// <param name="message">失敗した設定操作の説明です。</param>
    /// <param name="innerException">native呼び出しが送出した例外です。</param>
    internal BassMixerThreadConfigurationException(
        string nativeApi,
        Errors? nativeErrorCode,
        string message,
        Exception innerException = null)
        : base(message, innerException)
    {
        NativeApi = nativeApi;
        NativeErrorCode = nativeErrorCode;
    }

    /// <summary>失敗したnative API名を取得します。</summary>
    internal string NativeApi { get; }

    /// <summary>失敗直後に取得したnative errorを取得します。</summary>
    internal Errors? NativeErrorCode { get; }
}
