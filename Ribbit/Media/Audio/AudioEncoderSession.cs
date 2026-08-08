using System;
using ManagedBass;
using ManagedBass.Enc;

namespace Ribbit.Media.Audio;

/// <summary>
/// Lifecycle stages used when reporting an encoder failure.
/// </summary>
internal enum AudioEncoderFailureStage
{
    Start,
    NotifyRegistration,
    EncoderDied,
    Stop,
    Render,
    Dispose
}

/// <summary>
/// States owned by an <see cref="AudioEncoderSession" />.
/// </summary>
internal enum AudioEncoderSessionState
{
    Created,
    Started,
    Stopping,
    Stopped,
    Faulted,
    Disposed
}

/// <summary>
/// Typed failure raised at an encoder boundary while retaining native diagnostics.
/// </summary>
internal sealed class AudioEncoderException : Exception
{
    /// <summary>
    /// Initializes an encoder failure with its observable context.
    /// </summary>
    internal AudioEncoderException(
        EncoderType encoderType,
        AudioEncoderFailureStage stage,
        string outputFile,
        Errors? nativeError,
        EncodeNotifyStatus? notifyStatus,
        Exception innerException = null)
        : base(
            "Audio encoder " + encoderType
            + " failed at " + stage
            + (nativeError.HasValue ? " nativeError=" + nativeError.Value : string.Empty)
            + (notifyStatus.HasValue ? " notifyStatus=" + notifyStatus.Value : string.Empty),
            innerException)
    {
        EncoderType = encoderType;
        Stage = stage;
        OutputFile = outputFile;
        NativeError = nativeError;
        NotifyStatus = notifyStatus;
    }

    /// <summary>Gets the encoder format involved in the failure.</summary>
    internal EncoderType EncoderType { get; }

    /// <summary>Gets the lifecycle stage at which the failure occurred.</summary>
    internal AudioEncoderFailureStage Stage { get; }

    /// <summary>Gets the output path involved in the failure.</summary>
    internal string OutputFile { get; }

    /// <summary>Gets the ManagedBass error captured immediately after the native call.</summary>
    internal Errors? NativeError { get; }

    /// <summary>Gets the encoder notification status, when one caused the failure.</summary>
    internal EncodeNotifyStatus? NotifyStatus { get; }
}

/// <summary>
/// Narrow native boundary for the BASSenc calls owned by an encoder session.
/// </summary>
internal interface IAudioEncoderNative
{
    int EncodeStart(int channel, string commandLine, EncodeFlags flags, EncodeProcedure procedure);

    bool EncodeSetNotify(int encoderHandle, EncodeNotifyProcedure procedure);

    PlaybackState EncodeIsActive(int encoderHandle);

    bool EncodeStop(int encoderHandle);

    Errors LastError { get; }
}

/// <summary>
/// Owns a BASSenc handle, command request, notify delegate, and start/stop lifecycle.
/// </summary>
internal sealed class AudioEncoderSession : IDisposable
{
    private readonly int sourceChannel;

    private readonly IAudioEncoderNative native;

    private readonly EncodeNotifyProcedure notifyProcedure;

    private AudioEncoderCommandRequest request;

    private AudioEncoderCommand command;

    private int encoderHandle;

    private int notifyStatus = -1;

    private bool notifyRegistered;

    /// <summary>
    /// Initializes a session that has not yet started native encoding.
    /// </summary>
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

    /// <summary>Gets the encoder type owned by this session.</summary>
    internal EncoderType EncoderType => request.EncoderType;

    /// <summary>Gets the source channel passed to BASSenc.</summary>
    internal int SourceChannel => sourceChannel;

    /// <summary>Gets the output path owned by this session.</summary>
    internal string OutputFile => request.OutputFile;

    /// <summary>Gets the current deterministic command line.</summary>
    internal string CommandLine => command.CommandLine;

    /// <summary>Gets the flags used by the current command.</summary>
    internal EncodeFlags Flags => command.Flags;

    /// <summary>Gets the native encoder handle, or zero before start/after stop.</summary>
    internal int EncoderHandle => encoderHandle;

    /// <summary>Gets the current lifecycle state.</summary>
    internal AudioEncoderSessionState State { get; private set; }

    /// <summary>Gets whether notify registration succeeded for the current handle.</summary>
    internal bool NotifyRegistered => notifyRegistered;

    /// <summary>Gets the last notify status observed by the callback.</summary>
    internal EncodeNotifyStatus? LastNotifyStatus
    {
        get
        {
            int value = System.Threading.Volatile.Read(ref notifyStatus);
            return value < 0 ? null : (EncodeNotifyStatus)value;
        }
    }

    /// <summary>
    /// Rebuilds the command line with a metadata snapshot before encoding starts.
    /// </summary>
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

    /// <summary>
    /// Starts native encoding and publishes the started state only after notify registration succeeds.
    /// </summary>
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
            handle = native.EncodeStart(sourceChannel, command.CommandLine, command.Flags, null);
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

        bool notifySucceeded;
        try
        {
            notifySucceeded = native.EncodeSetNotify(handle, notifyProcedure);
        }
        catch (Exception exception)
        {
            Errors notifyExceptionError = native.LastError;
            State = AudioEncoderSessionState.Faulted;
            ReleaseFailedStartHandle();
            throw CreateException(AudioEncoderFailureStage.NotifyRegistration, exception, notifyExceptionError);
        }

        Errors notifyError = native.LastError;
        if (!notifySucceeded)
        {
            State = AudioEncoderSessionState.Faulted;
            ReleaseFailedStartHandle();
            throw CreateException(AudioEncoderFailureStage.NotifyRegistration, nativeError: notifyError);
        }

        notifyRegistered = true;
        if (LastNotifyStatus == EncodeNotifyStatus.EncoderDied)
        {
            Errors encoderDiedError = native.LastError;
            EncodeNotifyStatus encoderDiedStatus = LastNotifyStatus.Value;
            State = AudioEncoderSessionState.Faulted;
            ReleaseFailedStartHandle();
            throw CreateException(AudioEncoderFailureStage.EncoderDied, nativeError: encoderDiedError, notifyStatus: encoderDiedStatus);
        }

        State = AudioEncoderSessionState.Started;
    }

    /// <summary>
    /// Returns the native encoder activity state for the current handle.
    /// </summary>
    internal PlaybackState IsActive()
    {
        EnsureNotDisposed();
        return encoderHandle == 0 ? PlaybackState.Stopped : native.EncodeIsActive(encoderHandle);
    }

    /// <summary>
    /// Verifies that the encoder remained active after a source pull and retains notify or
    /// native activity failures as typed encoder-death errors.
    /// </summary>
    internal void EnsureActiveAfterRender()
    {
        EnsureNotDisposed();
        if (State != AudioEncoderSessionState.Started || encoderHandle == 0)
        {
            State = AudioEncoderSessionState.Faulted;
            throw CreateException(AudioEncoderFailureStage.EncoderDied);
        }

        if (LastNotifyStatus == EncodeNotifyStatus.EncoderDied)
        {
            State = AudioEncoderSessionState.Faulted;
            throw CreateException(AudioEncoderFailureStage.EncoderDied);
        }

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
        if (active != PlaybackState.Playing)
        {
            State = AudioEncoderSessionState.Faulted;
            throw CreateException(AudioEncoderFailureStage.EncoderDied, nativeError: activeError);
        }

        if (LastNotifyStatus == EncodeNotifyStatus.EncoderDied)
        {
            State = AudioEncoderSessionState.Faulted;
            throw CreateException(AudioEncoderFailureStage.EncoderDied);
        }
    }

    /// <summary>
    /// Stops native encoding and keeps the started state when native ownership remains unresolved.
    /// </summary>
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
        State = AudioEncoderSessionState.Stopped;
    }

    /// <summary>
    /// Releases the native encoder, retaining a stop failure as the primary exception.
    /// </summary>
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

        State = AudioEncoderSessionState.Disposed;
    }

    private void HandleNotify(int handle, EncodeNotifyStatus status, IntPtr user)
    {
        if (handle == encoderHandle)
        {
            System.Threading.Interlocked.Exchange(ref notifyStatus, (int)status);
        }
    }

    private void ReleaseFailedStartHandle()
    {
        if (encoderHandle == 0)
        {
            return;
        }

        try
        {
            if (native.EncodeStop(encoderHandle))
            {
                encoderHandle = 0;
                notifyRegistered = false;
            }
        }
        catch
        {
            // Keep the handle published so a later Dispose can retry ownership release.
        }
    }

    private AudioEncoderException CreateException(
        AudioEncoderFailureStage stage,
        Exception innerException = null,
        Errors? nativeError = null,
        EncodeNotifyStatus? notifyStatus = null)
    {
        return new AudioEncoderException(
            EncoderType,
            stage,
            OutputFile,
            nativeError ?? native.LastError,
            notifyStatus ?? LastNotifyStatus,
            innerException);
    }

    private void EnsureNotDisposed()
    {
        if (State == AudioEncoderSessionState.Disposed)
        {
            throw new ObjectDisposedException(nameof(AudioEncoderSession));
        }
    }
}

/// <summary>
/// Production ManagedBass boundary for BASSenc lifecycle calls.
/// </summary>
internal sealed class ManagedBassAudioEncoderNative : IAudioEncoderNative
{
    /// <summary>Gets the shared stateless boundary instance.</summary>
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
    public Errors LastError => Bass.LastError;
}
