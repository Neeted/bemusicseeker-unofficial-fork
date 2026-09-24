using System;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using Microsoft.Win32.SafeHandles;
using ManagedBass;
using ManagedBass.Enc;

namespace Ribbit.Media.Audio;

/// <summary>encoder failureの発生段階です。</summary>
internal enum AudioEncoderFailureStage
{
    Start,
    /// <summary>外部プロセスhandleの複製に失敗した段階です。</summary>
    ProcessHandleDuplicate,
    NotifyRegistration,
    EncoderDied,
    Write,
    Stop,
    /// <summary>外部プロセスの終了状態を確定する段階です。</summary>
    ProcessExit,
    Render,
    Dispose
}

/// <summary><see cref="AudioEncoderSession"/>が管理するlifecycle stateです。</summary>
internal enum AudioEncoderSessionState
{
    Created,
    Started,
    Stopping,
    Stopped,
    Faulted,
    Disposed
}

/// <summary>native、通知、external processのdiagnosticを保持します。</summary>
[Serializable]
internal sealed class AudioEncoderException : Exception
{
    /// <summary>空のencoder failureを初期化します。</summary>
    public AudioEncoderException()
    {
    }

    /// <summary>messageを持つencoder failureを初期化します。</summary>
    public AudioEncoderException(string message)
        : base(message)
    {
    }

    /// <summary>messageとinner exceptionを持つencoder failureを初期化します。</summary>
    public AudioEncoderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>encoderの失敗段階と診断情報を持つ例外を初期化します。</summary>
    /// <param name="encoderType">失敗した出力形式です。</param>
    /// <param name="stage">失敗した処理段階です。</param>
    /// <param name="outputFile">出力先のパスです。</param>
    /// <param name="nativeError">失敗直後に取得したBASSのエラーです。</param>
    /// <param name="notifyStatus">通知処理が示した状態です。</param>
    /// <param name="innerException">元の例外です。</param>
    /// <param name="processExitCode">終了した外部エンコーダーの終了コードです。</param>
    /// <param name="processWin32Error">プロセスハンドル照会に失敗したWin32エラーです。</param>
    internal AudioEncoderException(
        EncoderType encoderType,
        AudioEncoderFailureStage stage,
        string outputFile,
        Errors? nativeError,
        EncodeNotifyStatus? notifyStatus,
        Exception innerException = null,
        uint? processExitCode = null,
        int? processWin32Error = null)
        : base(
            "Audio encoder " + encoderType
            + " failed at " + stage
            + (nativeError.HasValue ? " nativeError=" + nativeError.Value : string.Empty)
            + (notifyStatus.HasValue ? " notifyStatus=" + notifyStatus.Value : string.Empty)
            + (processExitCode.HasValue ? " processExitCode=" + processExitCode.Value : string.Empty)
            + (processWin32Error.HasValue ? " processWin32Error=" + processWin32Error.Value : string.Empty),
            innerException)
    {
        EncoderType = encoderType;
        Stage = stage;
        OutputFile = outputFile;
        NativeError = nativeError;
        NotifyStatus = notifyStatus;
        ProcessExitCode = processExitCode;
        ProcessWin32Error = processWin32Error;
    }

    [System.Obsolete(DiagnosticId = "SYSLIB0051")]
    private AudioEncoderException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
    }

    /// <summary>failureが発生したencoder formatを取得します。</summary>
    internal EncoderType EncoderType { get; }

    /// <summary>failureが発生したlifecycle段階を取得します。</summary>
    internal AudioEncoderFailureStage Stage { get; }

    /// <summary>failureが発生した出力pathを取得します。</summary>
    internal string OutputFile { get; }

    /// <summary>native call直後に取得したManagedBass errorを取得します。</summary>
    internal Errors? NativeError { get; }

    /// <summary>failure要因となった場合にencoder notification statusを取得します。</summary>
    internal EncodeNotifyStatus? NotifyStatus { get; }

    /// <summary>終了したexternal encoderのexit codeを取得します。Win32照会errorとは区別します。</summary>
    internal uint? ProcessExitCode { get; }

    /// <summary>process handle複製または照会に失敗したときのWin32 errorを取得します。</summary>
    internal int? ProcessWin32Error { get; }
}

/// <summary>BASSencが返した外部プロセスhandleの複製を所有する契約です。</summary>
internal interface IAudioEncoderProcessHandle : IDisposable
{
}

/// <summary>エンコーダーセッションが使うBASSenc呼び出しとプロセスhandle照会の境界です。</summary>
internal interface IAudioEncoderNative
{
    /// <summary>エンコーダーを開始し、native handleまたは失敗値を返します。</summary>
    /// <param name="channel">入力するBASSチャンネルです。</param>
    /// <param name="commandLine">外部エンコーダーのコマンドラインです。</param>
    /// <param name="flags">BASSencの出力変換フラグです。</param>
    /// <param name="procedure">エンコーダー出力を受け取る処理です。</param>
    /// <returns>開始したエンコーダーのhandleです。</returns>
    int EncodeStart(int channel, string commandLine, EncodeFlags flags, EncodeProcedure procedure);

    /// <summary>Float32サンプルをエンコーダーへ書き込みます。</summary>
    /// <param name="encoderHandle">書き込み先のエンコーダーhandleです。</param>
    /// <param name="samples">インターリーブされたサンプルです。</param>
    /// <param name="sampleOffset">サンプル配列内の開始位置です。</param>
    /// <param name="lengthBytes">書き込むデータ長です。</param>
    /// <returns>書き込みに成功した場合は<c>true</c>です。</returns>
    bool EncodeWrite(int encoderHandle, float[] samples, int sampleOffset, int lengthBytes);

    /// <summary>エンコーダーの終了通知を登録します。</summary>
    /// <param name="encoderHandle">対象のエンコーダーhandleです。</param>
    /// <param name="procedure">通知を受け取る処理です。</param>
    /// <returns>登録に成功した場合は<c>true</c>です。</returns>
    bool EncodeSetNotify(int encoderHandle, EncodeNotifyProcedure procedure);

    /// <summary>エンコーダーの現在の動作状態を取得します。</summary>
    /// <param name="encoderHandle">状態を調べるエンコーダーhandleです。</param>
    /// <returns>再生、停止または一時停止の状態です。</returns>
    PlaybackState EncodeIsActive(int encoderHandle);

    /// <summary>エンコーダーを停止します。</summary>
    /// <param name="encoderHandle">停止するエンコーダーhandleです。</param>
    /// <returns>停止に成功した場合は<c>true</c>です。</returns>
    bool EncodeStop(int encoderHandle);

    /// <summary>BASSencが返した外部プロセスhandleを非継承で複製します。</summary>
    /// <param name="encoderHandle">BASSencのエンコーダーhandleです。</param>
    /// <param name="processHandle">成功時に所有権を受け取る複製handleです。</param>
    /// <param name="win32Error">失敗時に取得したWin32エラーです。</param>
    /// <returns>複製に成功した場合は<c>true</c>です。</returns>
    bool TryDuplicateProcessHandle(
        int encoderHandle,
        out IAudioEncoderProcessHandle processHandle,
        out int win32Error);

    /// <summary>複製した外部プロセスhandleから終了コードを取得します。</summary>
    /// <param name="processHandle">照会するプロセスhandleです。</param>
    /// <param name="exitCode">成功時に取得した終了コードです。</param>
    /// <param name="win32Error">失敗時に取得したWin32エラーです。</param>
    /// <returns>終了コードの取得に成功した場合は<c>true</c>です。</returns>
    bool TryGetProcessExitCode(
        IAudioEncoderProcessHandle processHandle,
        out uint exitCode,
        out int win32Error);

    /// <summary>直前のManagedBass呼び出しが記録したBASSエラーを取得します。</summary>
    Errors LastError { get; }
}

/// <summary>BASSenc handle、command、notify delegateとstart/stopのlifecycleを所有します。</summary>
internal sealed class AudioEncoderSession : IDisposable
{
    // 固定したBASSenc 2.4.17のbassenc.h。ManagedBass 4.0.2には未定義。
    private const EncodeNotifyStatus EncoderTerminated = (EncodeNotifyStatus)0x10003;
    private const uint StillActiveProcessExitCode = 259;
    private const int MaxPcm24SamplesPerWrite = 32 * 1024;
    private const EncodeFlags SampleConversionMask =
        EncodeFlags.ConvertFloatTo8BitInt
        | EncodeFlags.ConvertFloatTo16BitInt
        | EncodeFlags.ConvertFloatTo32Bit;
    private int failureNotifyStatus = -1;
    private readonly int sourceChannel;

    private readonly IAudioEncoderNative native;

    private readonly EncodeNotifyProcedure notifyProcedure;

    private AudioEncoderCommandRequest request;

    private AudioEncoderCommand command;

    private int encoderHandle;

    private IAudioEncoderProcessHandle processHandle;

    private AudioEncoderException outputFailure;

    private readonly Random ditherRandom = new();

    private float[] pcm24Scratch = [];

    private int notifyStatus = -1;

    private bool notifyRegistered;

    /// <summary>native encoding開始前のsessionを初期化します。</summary>
    internal AudioEncoderSession(
        int sourceChannel,
        AudioEncoderCommandRequest request,
        IAudioEncoderNative native = null)
    {
        if (sourceChannel == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceChannel));
        }

        this.sourceChannel = sourceChannel;
        this.request = request ?? throw new ArgumentNullException(nameof(request));
        command = AudioEncoderCommandFactory.Create(request);
        this.native = native ?? ManagedBassAudioEncoderNative.Instance;
        notifyProcedure = HandleNotify;
        State = AudioEncoderSessionState.Created;
    }

    /// <summary>このsessionが所有するencoder typeを取得します。</summary>
    internal EncoderType EncoderType => request.EncoderType;

    /// <summary>BASSencへ渡すsource channelを取得します。</summary>
    internal int SourceChannel => sourceChannel;

    /// <summary>このsessionが所有する出力pathを取得します。</summary>
    internal string OutputFile => request.OutputFile;

    /// <summary>現在のcommand lineを取得します。</summary>
    internal string CommandLine => command.CommandLine;

    /// <summary>現在のcommandで使うflagsを取得します。</summary>
    internal EncodeFlags Flags => command.Flags;

    /// <summary>encoder requestが定めるinterleaved source channel数を取得します。</summary>
    internal int ChannelCount => request.ChannelCount;

    /// <summary>commandがFloat32を整数input formatへ変換するかを取得します。</summary>
    internal bool RequiresIntegerInput =>
        (command.Flags & SampleConversionMask) != EncodeFlags.Default;

    /// <summary>BASSencの重複値を含む変換flagを正確に比較し、24bit手動量子化の要否を取得します。</summary>
    private bool Requires24BitQuantization =>
        (command.Flags & SampleConversionMask) == EncodeFlags.ConvertFloatTo24Bit;

    /// <summary>native encoder handleを取得します。start前またはstop後は0です。</summary>
    internal int EncoderHandle => encoderHandle;

    /// <summary>現在のlifecycle stateを取得します。</summary>
    internal AudioEncoderSessionState State { get; private set; }

    /// <summary>現在のhandleでnotify登録に成功したかを取得します。</summary>
    internal bool NotifyRegistered => notifyRegistered;

    /// <summary>管理側がcallback故障または最終process結果を観測するための保持済みfailureです。</summary>
    internal AudioEncoderException OutputFailure => System.Threading.Volatile.Read(ref outputFailure);

    /// <summary>外部process HANDLE複製がsessionに残っているかを取得します。</summary>
    internal bool OwnsProcessHandle => processHandle != null;

    /// <summary>callbackが最後に観測したnotify statusを取得します。</summary>
    internal EncodeNotifyStatus? LastNotifyStatus
    {
        get
        {
            int value = System.Threading.Volatile.Read(ref notifyStatus);
            return value < 0 ? null : (EncodeNotifyStatus)value;
        }
    }

    /// <summary>
    /// callbackから記録された最初の故障を管理側へ伝えます。callback側では例外を作らず、
    /// 再生管理側の既存catchとcleanupへこのメソッドから例外を渡します。
    /// </summary>
    internal void ThrowPendingOutputFailure()
    {
        AudioEncoderException failure = System.Threading.Volatile.Read(ref outputFailure);
        if (failure == null)
        {
            int status = System.Threading.Volatile.Read(ref failureNotifyStatus);
            if (status >= 0)
            {
                var notify = (EncodeNotifyStatus)status;
                failure = PreserveOutputFailure(CreateException(
                    notify == EncodeNotifyStatus.EncoderDied
                        ? AudioEncoderFailureStage.EncoderDied
                        : AudioEncoderFailureStage.Write,
                    notifyStatus: notify));
            }
        }

        if (failure != null)
        {
            State = AudioEncoderSessionState.Faulted;
            throw failure;
        }
    }

    /// <summary>encoding開始前にmetadata snapshotでcommand lineを再構成します。</summary>
    internal void SetTagInfo(AudioTagInfo tags)
    {
        EnsureNotDisposed();
        if (State is AudioEncoderSessionState.Started or AudioEncoderSessionState.Stopping)
        {
            throw new InvalidOperationException("Recording has started already");
        }

        request = request with { Tags = tags ?? AudioTagInfo.Empty };
        command = AudioEncoderCommandFactory.Create(request);
    }

    /// <summary>encodingを開始し、notify登録とprocess handle複製の完了後にstarted stateを公開します。</summary>
    internal void Start()
    {
        EnsureNotDisposed();
        if (State is AudioEncoderSessionState.Started or AudioEncoderSessionState.Stopping)
        {
            throw new InvalidOperationException("Recording has started already");
        }

        if (State == AudioEncoderSessionState.Faulted)
        {
            throw new InvalidOperationException("Encoder session has failed");
        }

        int handle;
        try
        {
            handle = native.EncodeStart(
                sourceChannel,
                command.CommandLine,
                command.Flags | EncodeFlags.Pause,
                null);
        }
        catch (Exception exception)
        {
            State = AudioEncoderSessionState.Faulted;
            throw CreateException(AudioEncoderFailureStage.Start, exception);
        }

        Errors startError = native.LastError;
        if (handle == 0)
        {
            State = AudioEncoderSessionState.Faulted;
            throw CreateException(AudioEncoderFailureStage.Start, nativeError: startError);
        }

        encoderHandle = handle;
        notifyRegistered = false;
        System.Threading.Interlocked.Exchange(ref notifyStatus, -1);
        System.Threading.Interlocked.Exchange(ref failureNotifyStatus, -1);

        if (EncoderType != EncoderType.WAVE)
        {
            IAudioEncoderProcessHandle duplicatedProcessHandle = null;
            int duplicateError = 0;
            bool duplicated;
            try
            {
                duplicated = native.TryDuplicateProcessHandle(handle, out duplicatedProcessHandle, out duplicateError);
            }
            catch (Exception exception)
            {
                AudioEncoderException duplicateFailure = CreateFailureAfterFailedStart(
                    AudioEncoderFailureStage.ProcessHandleDuplicate,
                    exception,
                    processWin32Error: null);
                throw duplicateFailure;
            }

            if (duplicatedProcessHandle != null)
            {
                processHandle = duplicatedProcessHandle;
            }

            if (!duplicated || duplicatedProcessHandle == null)
            {
                throw CreateFailureAfterFailedStart(
                    AudioEncoderFailureStage.ProcessHandleDuplicate,
                    processWin32Error: duplicateError);
            }
        }

        bool notifySucceeded;
        try
        {
            notifySucceeded = native.EncodeSetNotify(handle, notifyProcedure);
        }
        catch (Exception exception)
        {
            Errors notifyExceptionError = native.LastError;
            throw CreateFailureAfterFailedStart(
                AudioEncoderFailureStage.NotifyRegistration,
                exception,
                notifyExceptionError);
        }

        Errors notifyError = native.LastError;
        if (!notifySucceeded)
        {
            throw CreateFailureAfterFailedStart(
                AudioEncoderFailureStage.NotifyRegistration,
                nativeError: notifyError);
        }

        notifyRegistered = true;
        if (LastNotifyStatus == EncodeNotifyStatus.EncoderDied)
        {
            Errors encoderDiedError = native.LastError;
            EncodeNotifyStatus encoderDiedStatus = LastNotifyStatus.Value;
            throw CreateFailureAfterFailedStart(
                AudioEncoderFailureStage.EncoderDied,
                nativeError: encoderDiedError,
                notifyStatus: encoderDiedStatus);
        }

        State = AudioEncoderSessionState.Started;
    }

    /// <summary>現在のhandleが示すnative encoder activityを取得します。</summary>
    internal PlaybackState IsActive()
    {
        EnsureNotDisposed();
        return encoderHandle == 0 ? PlaybackState.Stopped : native.EncodeIsActive(encoderHandle);
    }

    /// <summary>
    /// 手動供給中のencoder生存状態を確認します。session中は自動供給を無効にするため、
    /// Pausedは正常な状態です。
    /// </summary>
    internal void EnsureActiveAfterRender()
    {
        EnsureNotDisposed();
        if (State != AudioEncoderSessionState.Started || encoderHandle == 0)
        {
            State = AudioEncoderSessionState.Faulted;
            throw PreserveOutputFailure(CreateException(AudioEncoderFailureStage.EncoderDied));
        }

        ThrowPendingOutputFailure();

        PlaybackState active;
        try
        {
            active = native.EncodeIsActive(encoderHandle);
        }
        catch (Exception exception)
        {
            State = AudioEncoderSessionState.Faulted;
            throw CreateException(AudioEncoderFailureStage.EncoderDied, exception);
        }

        Errors activeError = native.LastError;
        if (active is not PlaybackState.Playing and not PlaybackState.Paused)
        {
            State = AudioEncoderSessionState.Faulted;
            throw PreserveOutputFailure(CreateException(AudioEncoderFailureStage.EncoderDied, nativeError: activeError));
        }

        ThrowPendingOutputFailure();
    }

    /// <summary>interleaved Float32 dataをnative encoder handleへ手動供給します。</summary>
    internal void EncodeWrite(float[] samples, int sampleOffset, int sampleCount)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(samples);
        if (sampleOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleOffset));
        }
        if (sampleCount < 0 || sampleOffset > samples.Length - sampleCount)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        }
        if (sampleCount % request.ChannelCount != 0)
        {
            throw new ArgumentException("Encoder input must contain complete interleaved frames.", nameof(sampleCount));
        }
        if (sampleCount == 0)
        {
            return;
        }
        if (State != AudioEncoderSessionState.Started || encoderHandle == 0)
        {
            State = AudioEncoderSessionState.Faulted;
            throw CreateException(AudioEncoderFailureStage.Write);
        }

        EnsureActiveAfterRender();
        int writtenSamples = 0;
        int maxSamplesPerCall = Requires24BitQuantization
            ? GetMaxPcm24SamplesPerWrite(request.ChannelCount)
            : GetMaxSamplesPerWrite(request.ChannelCount);
        while (writtenSamples < sampleCount)
        {
            int samplesToWrite = System.Math.Min(sampleCount - writtenSamples, maxSamplesPerCall);
            int lengthBytes = checked(samplesToWrite * sizeof(float));
            int blockOffset = checked(sampleOffset + writtenSamples);
            for (int sampleIndex = blockOffset; sampleIndex < blockOffset + samplesToWrite; sampleIndex++)
            {
                if (!float.IsFinite(samples[sampleIndex]))
                {
                    State = AudioEncoderSessionState.Faulted;
                    throw new InvalidOperationException("Encoder input PCM must be finite.");
                }

                if (Requires24BitQuantization
                    && (samples[sampleIndex] < -1f || samples[sampleIndex] > 1f))
                {
                    State = AudioEncoderSessionState.Faulted;
                    throw new AudioOutputRangeException(System.Math.Abs((double)samples[sampleIndex]));
                }
            }

            bool written;
            try
            {
                float[] writeSamples = samples;
                int writeOffset = blockOffset;
                if (Requires24BitQuantization)
                {
                    EnsurePcm24ScratchCapacity(samplesToWrite);
                    AudioPcm24Quantizer.Apply(
                        samples.AsSpan(blockOffset, samplesToWrite),
                        pcm24Scratch.AsSpan(0, samplesToWrite),
                        ditherRandom);
                    writeSamples = pcm24Scratch;
                    writeOffset = 0;
                }

                written = native.EncodeWrite(encoderHandle, writeSamples, writeOffset, lengthBytes);
            }
            catch (Exception exception)
            {
                State = AudioEncoderSessionState.Faulted;
                throw CreateException(AudioEncoderFailureStage.Write, exception);
            }

            Errors writeError = native.LastError;
            if (!written)
            {
                State = AudioEncoderSessionState.Faulted;
                throw CreateException(AudioEncoderFailureStage.Write, nativeError: writeError);
            }

            writtenSamples = checked(writtenSamples + samplesToWrite);
            EnsureActiveAfterRender();
        }
    }

    /// <summary>encoder byte length上限内の最大完全interleaved frame数を返します。</summary>
    internal static int GetMaxSamplesPerWrite(int channelCount)
    {
        if (channelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCount));
        }

        int maxSamples = int.MaxValue / sizeof(float);
        int maxSamplesPerWrite = maxSamples - maxSamples % channelCount;
        if (maxSamplesPerWrite == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(channelCount),
                "The channel count exceeds the encoder byte-length limit.");
        }

        return maxSamplesPerWrite;
    }

    /// <summary>24bit量子化scratchの上限内で、完全なインターリーブframe数を返します。</summary>
    /// <param name="channelCount">1 frameを構成するチャンネル数です。</param>
    /// <returns>1回のnative書き込みで渡す最大サンプル数です。</returns>
    internal static int GetMaxPcm24SamplesPerWrite(int channelCount)
    {
        if (channelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCount));
        }

        int maxSamplesPerWrite = MaxPcm24SamplesPerWrite - MaxPcm24SamplesPerWrite % channelCount;
        if (maxSamplesPerWrite == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(channelCount),
                "The channel count exceeds the 24-bit scratch limit.");
        }

        return maxSamplesPerWrite;
    }

    /// <summary>native encoderを停止し、native handleとprocess終了の所有を確定します。</summary>
    internal void Stop()
    {
        EnsureNotDisposed();
        if (State != AudioEncoderSessionState.Started)
        {
            throw new InvalidOperationException("Not recording started");
        }

        State = AudioEncoderSessionState.Stopping;
        bool stopped;
        try
        {
            stopped = native.EncodeStop(encoderHandle);
        }
        catch (Exception exception)
        {
            State = AudioEncoderSessionState.Started;
            throw CreateException(AudioEncoderFailureStage.Stop, exception);
        }

        Errors stopError = native.LastError;
        if (!stopped)
        {
            State = AudioEncoderSessionState.Started;
            throw CreateException(AudioEncoderFailureStage.Stop, nativeError: stopError);
        }

        encoderHandle = 0;
        notifyRegistered = false;
        int terminalFailure = System.Threading.Volatile.Read(ref failureNotifyStatus);
        if (terminalFailure >= 0)
        {
            PreserveOutputFailure(CreateException(
                AudioEncoderFailureStage.Stop,
                notifyStatus: (EncodeNotifyStatus)terminalFailure));
        }

        ProcessExitCheck processExit = CheckProcessExitAfterNativeStop();
        AudioEncoderException processFailure = CreateProcessExitFailure(processExit);
        if (processFailure != null)
        {
            PreserveOutputFailure(processFailure);
        }

        AudioEncoderException outputFailure = System.Threading.Volatile.Read(ref this.outputFailure);
        if (outputFailure != null)
        {
            State = AudioEncoderSessionState.Faulted;
            throw outputFailure;
        }

        State = AudioEncoderSessionState.Stopped;
    }

    /// <summary>native encoderとprocess handle複製を解放し、未解決の所有があれば保持します。</summary>
    public void Dispose()
    {
        if (State == AudioEncoderSessionState.Disposed)
        {
            return;
        }

        if (encoderHandle != 0)
        {
            AudioEncoderSessionState failureState = State == AudioEncoderSessionState.Faulted
                ? AudioEncoderSessionState.Faulted
                : AudioEncoderSessionState.Started;
            try
            {
                if (!native.EncodeStop(encoderHandle))
                {
                    throw CreateException(AudioEncoderFailureStage.Dispose, nativeError: native.LastError);
                }

                encoderHandle = 0;
                notifyRegistered = false;
            }
            catch (AudioEncoderException)
            {
                State = failureState;
                throw;
            }
            catch (Exception exception)
            {
                State = failureState;
                throw CreateException(AudioEncoderFailureStage.Dispose, exception);
            }
        }

        ProcessExitCheck processExit = CheckProcessExitAfterNativeStop();
        AudioEncoderException processFailure = CreateProcessExitFailure(processExit);
        if (processFailure != null)
        {
            PreserveOutputFailure(processFailure);
            if (processExit.IsUnresolved)
            {
                State = AudioEncoderSessionState.Faulted;
                throw processFailure;
            }
        }

        State = AudioEncoderSessionState.Disposed;
    }

    private void HandleNotify(int handle, EncodeNotifyStatus status, IntPtr user)
    {
        if (handle == encoderHandle)
        {
            if (status is EncodeNotifyStatus.EncoderDied or EncodeNotifyStatus.QueueFull or EncoderTerminated)
            {
                System.Threading.Interlocked.CompareExchange(ref failureNotifyStatus, (int)status, -1);
            }
            System.Threading.Interlocked.Exchange(ref notifyStatus, (int)status);
        }
    }

    private AudioEncoderException CreateFailureAfterFailedStart(
        AudioEncoderFailureStage stage,
        Exception innerException = null,
        Errors? nativeError = null,
        EncodeNotifyStatus? notifyStatus = null,
        int? processWin32Error = null)
    {
        ProcessExitCheck processExit = ReleaseFailedStartHandle();
        State = AudioEncoderSessionState.Faulted;
        AudioEncoderException failure = CreateException(
            stage,
            innerException,
            nativeError,
            notifyStatus,
            processExit.ExitCode,
            processExit.Win32Error ?? processWin32Error);
        return PreserveOutputFailure(failure);
    }

    private ProcessExitCheck ReleaseFailedStartHandle()
    {
        if (encoderHandle == 0)
        {
            return default;
        }

        try
        {
            if (!native.EncodeStop(encoderHandle))
            {
                return default;
            }

            encoderHandle = 0;
            notifyRegistered = false;
            return CheckProcessExitAfterNativeStop();
        }
        catch
        {
            // Keep both owners so the management side can retry cleanup without masking the start failure.
            return default;
        }
    }

    private ProcessExitCheck CheckProcessExitAfterNativeStop()
    {
        IAudioEncoderProcessHandle ownedProcessHandle = processHandle;
        if (ownedProcessHandle == null)
        {
            return default;
        }

        uint exitCode = 0;
        int win32Error = 0;
        bool queried;
        try
        {
            queried = native.TryGetProcessExitCode(ownedProcessHandle, out exitCode, out win32Error);
        }
        catch (Exception exception)
        {
            return new ProcessExitCheck(true, false, null, null, exception);
        }

        if (!queried)
        {
            return new ProcessExitCheck(true, false, null, win32Error, null);
        }

        if (exitCode == StillActiveProcessExitCode)
        {
            return new ProcessExitCheck(true, true, exitCode, null, null);
        }

        try
        {
            ownedProcessHandle.Dispose();
            processHandle = null;
        }
        catch (Exception exception)
        {
            return new ProcessExitCheck(true, false, exitCode, null, exception);
        }

        return new ProcessExitCheck(true, true, exitCode, null, null);
    }

    private AudioEncoderException CreateProcessExitFailure(ProcessExitCheck processExit)
    {
        if (!processExit.HasHandle)
        {
            return null;
        }

        if (processExit.QuerySucceeded
            && processExit.ExitCode.HasValue
            && processExit.ExitCode.Value == 0)
        {
            return null;
        }

        return CreateException(
            AudioEncoderFailureStage.ProcessExit,
            processExit.Exception,
            processExitCode: processExit.ExitCode,
            processWin32Error: processExit.Win32Error);
    }

    private AudioEncoderException PreserveOutputFailure(AudioEncoderException failure)
    {
        return System.Threading.Interlocked.CompareExchange(ref outputFailure, failure, null) ?? failure;
    }

    private void EnsurePcm24ScratchCapacity(int sampleCount)
    {
        if (pcm24Scratch.Length < sampleCount)
        {
            Array.Resize(ref pcm24Scratch, sampleCount);
        }
    }

    private AudioEncoderException CreateException(
        AudioEncoderFailureStage stage,
        Exception innerException = null,
        Errors? nativeError = null,
        EncodeNotifyStatus? notifyStatus = null,
        uint? processExitCode = null,
        int? processWin32Error = null)
    {
        Errors? reportedNativeError = nativeError;
        if (!reportedNativeError.HasValue
            && stage is not AudioEncoderFailureStage.ProcessHandleDuplicate
                and not AudioEncoderFailureStage.ProcessExit)
        {
            reportedNativeError = native.LastError;
        }

        EncodeNotifyStatus? reportedNotifyStatus = stage == AudioEncoderFailureStage.ProcessExit
            ? notifyStatus
            : notifyStatus ?? LastNotifyStatus;
        return new AudioEncoderException(
            EncoderType,
            stage,
            OutputFile,
            reportedNativeError,
            reportedNotifyStatus,
            innerException,
            processExitCode,
            processWin32Error);
    }

    private readonly record struct ProcessExitCheck(
        bool HasHandle,
        bool QuerySucceeded,
        uint? ExitCode,
        int? Win32Error,
        Exception Exception)
    {
        internal bool IsUnresolved =>
            HasHandle && (!QuerySucceeded || ExitCode == StillActiveProcessExitCode || Exception != null);
    }

    private void EnsureNotDisposed()
    {
        if (State == AudioEncoderSessionState.Disposed)
        {
            throw new ObjectDisposedException(nameof(AudioEncoderSession));
        }
    }
}

/// <summary>BASSencの開始・停止と外部プロセスhandleを扱うManagedBassの実装です。</summary>
internal sealed class ManagedBassAudioEncoderNative : IAudioEncoderNative
{
    /// <summary>共有するstateless境界を取得します。</summary>
    internal static ManagedBassAudioEncoderNative Instance { get; } = new();

    private ManagedBassAudioEncoderNative()
    {
    }

    /// <inheritdoc />
    public int EncodeStart(int channel, string commandLine, EncodeFlags flags, EncodeProcedure procedure)
    {
        return BassEnc.EncodeStart(channel, commandLine, flags, procedure, IntPtr.Zero);
    }

    /// <inheritdoc />
    public bool EncodeWrite(int encoderHandle, float[] samples, int sampleOffset, int lengthBytes)
    {
        if (sampleOffset == 0)
        {
            return BassEnc.EncodeWrite(encoderHandle, samples, lengthBytes);
        }

        var pin = GCHandle.Alloc(samples, GCHandleType.Pinned);
        try
        {
            IntPtr buffer = IntPtr.Add(pin.AddrOfPinnedObject(), checked(sampleOffset * sizeof(float)));
            return BassEnc.EncodeWrite(encoderHandle, buffer, lengthBytes);
        }
        finally
        {
            pin.Free();
        }
    }

    /// <inheritdoc />
    public bool EncodeSetNotify(int encoderHandle, EncodeNotifyProcedure procedure)
    {
        return BassEnc.EncodeSetNotify(encoderHandle, procedure, IntPtr.Zero);
    }

    /// <inheritdoc />
    public PlaybackState EncodeIsActive(int encoderHandle)
    {
        return BassEnc.EncodeIsActive(encoderHandle);
    }

    /// <inheritdoc />
    public bool EncodeStop(int encoderHandle)
    {
        return BassEnc.EncodeStop(encoderHandle);
    }

    /// <inheritdoc />
    public bool TryDuplicateProcessHandle(
        int encoderHandle,
        out IAudioEncoderProcessHandle processHandle,
        out int win32Error)
    {
        return AudioEncoderProcessHandle.TryDuplicate(
            AudioEncoderProcessHandle.FromBassEncoderHandle(encoderHandle),
            out processHandle,
            out win32Error);
    }

    /// <inheritdoc />
    public bool TryGetProcessExitCode(
        IAudioEncoderProcessHandle processHandle,
        out uint exitCode,
        out int win32Error)
    {
        return AudioEncoderProcessHandle.TryGetExitCode(processHandle, out exitCode, out win32Error);
    }

    /// <inheritdoc />
    public Errors LastError => Bass.LastError;
}

/// <summary>BASSencが返すWindowsプロセスhandleを複製・照会する処理です。</summary>
internal static class AudioEncoderProcessHandle
{
    private const uint DuplicateSameAccess = 0x00000002;
    private const int ErrorInvalidHandle = 6;

    /// <summary>BASSencの32bit handle値をWindows handleとして同じビット列で表します。</summary>
    /// <param name="handle">BASSencが返したhandle値です。</param>
    /// <returns>Windows APIへ渡すhandleです。</returns>
    internal static IntPtr FromBassEncoderHandle(int handle)
    {
        return IntPtr.Size == sizeof(int)
            ? new IntPtr(handle)
            : new IntPtr(unchecked((long)(uint)handle));
    }

    /// <summary>継承されない同一権限のプロセスhandle複製を作成します。</summary>
    /// <param name="sourceHandle">複製元のhandleです。</param>
    /// <param name="processHandle">成功時に所有権を受け取る複製handleです。</param>
    /// <param name="win32Error">失敗時に取得したWin32エラーです。</param>
    /// <returns>複製に成功した場合は<c>true</c>です。</returns>
    internal static bool TryDuplicate(
        IntPtr sourceHandle,
        out IAudioEncoderProcessHandle processHandle,
        out int win32Error)
    {
        IntPtr currentProcess = GetCurrentProcess();
        if (!DuplicateHandle(
            currentProcess,
            sourceHandle,
            currentProcess,
            out IntPtr duplicatedHandle,
            dwDesiredAccess: 0,
            bInheritHandle: false,
            dwOptions: DuplicateSameAccess))
        {
            processHandle = null;
            win32Error = Marshal.GetLastWin32Error();
            return false;
        }

        if (duplicatedHandle == IntPtr.Zero)
        {
            processHandle = null;
            win32Error = ErrorInvalidHandle;
            return false;
        }

        processHandle = new OwnedProcessHandle(new SafeProcessHandle(duplicatedHandle, ownsHandle: true));
        win32Error = 0;
        return true;
    }

    /// <summary>複製したプロセスhandleから終了コードを照会します。</summary>
    /// <param name="processHandle">照会するプロセスhandleです。</param>
    /// <param name="exitCode">成功時に取得した終了コードです。</param>
    /// <param name="win32Error">失敗時に取得したWin32エラーです。</param>
    /// <returns>終了コードの取得に成功した場合は<c>true</c>です。</returns>
    internal static bool TryGetExitCode(
        IAudioEncoderProcessHandle processHandle,
        out uint exitCode,
        out int win32Error)
    {
        if (processHandle is not OwnedProcessHandle ownedHandle)
        {
            exitCode = 0;
            win32Error = ErrorInvalidHandle;
            return false;
        }

        if (!GetExitCodeProcess(ownedHandle.Handle, out exitCode))
        {
            win32Error = Marshal.GetLastWin32Error();
            return false;
        }

        win32Error = 0;
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr hSourceProcessHandle,
        IntPtr hSourceHandle,
        IntPtr hTargetProcessHandle,
        out IntPtr lpTargetHandle,
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwOptions);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle hProcess, out uint lpExitCode);

    private sealed class OwnedProcessHandle(SafeProcessHandle handle) : IAudioEncoderProcessHandle
    {
        internal SafeProcessHandle Handle { get; } = handle;

        public void Dispose() => Handle.Dispose();
    }
}
