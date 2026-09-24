using System;
using ManagedBass;
using ManagedBass.Mix;

namespace Ribbit.Media.Audio;

/// <summary>Identifies the native phase that failed while managing a mixer source.</summary>
internal enum BassAudioPlaybackStage
{
    /// <summary>The owning core device could not be selected for source creation.</summary>
    SourceDeviceSelection,

    /// <summary>The source stream could not be created.</summary>
    SourceCreate,

    /// <summary>The source could not be retained by its owning session.</summary>
    SourceTracking,

    /// <summary>The source's current mixer membership could not be established.</summary>
    MixerMembership,

    /// <summary>The source could not be attached to the expected mixer.</summary>
    MixerAttach,

    /// <summary>The source could not be resumed after attachment.</summary>
    MixerResume,

    /// <summary>ソース形式またはサンプルレート変換設定を確認できなかった。</summary>
    MixerSourceFormat,

    /// <summary>チャンネル行列を設定できなかった。</summary>
    MixerMatrix,

    /// <summary>The source could not be paused.</summary>
    MixerPause,

    /// <summary>The source could not be removed from its expected mixer.</summary>
    MixerRemove,

    /// <summary>The source position could not be changed.</summary>
    SetPosition
}

/// <summary>
/// Describes a native playback failure without relying on a later BASS call's last-error slot.
/// </summary>
internal sealed class BassAudioPlaybackException : InvalidOperationException
{
    /// <summary>Creates an empty playback failure for exception infrastructure.</summary>
    internal BassAudioPlaybackException()
    {
    }

    /// <summary>Creates a playback failure with a diagnostic message.</summary>
    internal BassAudioPlaybackException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a playback failure with a message and underlying cause.</summary>
    internal BassAudioPlaybackException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates a typed failure for one playback operation.</summary>
    internal BassAudioPlaybackException(
        BassAudioPlaybackStage stage,
        string fileName,
        int sourceHandle,
        int expectedMixerHandle,
        int actualMixerHandle,
        string nativeErrorSource,
        Errors? nativeErrorCode,
        string message,
        Exception innerException = null)
        : this(
            stage,
            fileName,
            sourceHandle,
            expectedMixerHandle,
            actualMixerHandle,
            nativeErrorSource,
            nativeErrorCode,
            message,
            session: null,
            innerException: innerException)
    {
    }

    /// <summary>Creates a playback failure with an immutable audio-session snapshot.</summary>
    internal BassAudioPlaybackException(
        BassAudioPlaybackStage stage,
        string fileName,
        int sourceHandle,
        int expectedMixerHandle,
        int actualMixerHandle,
        string nativeErrorSource,
        Errors? nativeErrorCode,
        string message,
        BassAudioSession session,
        Exception innerException = null)
        : base(message, innerException)
    {
        Stage = stage;
        FileName = fileName;
        SourceHandle = sourceHandle;
        ExpectedMixerHandle = expectedMixerHandle;
        ActualMixerHandle = actualMixerHandle;
        NativeErrorSource = nativeErrorSource;
        NativeErrorCode = nativeErrorCode;
        Backend = session?.ActualBackend;
        SessionState = session?.State;
        CoreDeviceIndex = session?.CoreDeviceIndex;
    }

    /// <summary>Gets the playback stage that failed.</summary>
    internal BassAudioPlaybackStage Stage { get; }

    /// <summary>Gets the source file used by the player.</summary>
    internal string FileName { get; }

    /// <summary>Gets the source stream handle.</summary>
    internal int SourceHandle { get; }

    /// <summary>Gets the mixer handle that owns the player source.</summary>
    internal int ExpectedMixerHandle { get; }

    /// <summary>Gets the mixer handle observed during the failed operation.</summary>
    internal int ActualMixerHandle { get; }

    /// <summary>Gets the native API that supplied the captured error.</summary>
    internal string NativeErrorSource { get; }

    /// <summary>Gets the native error captured immediately after the failed API call.</summary>
    internal Errors? NativeErrorCode { get; }

    /// <summary>Gets the backend selected by the owning session when the failure occurred.</summary>
    internal BassAudioPlayer.DeviceDriver? Backend { get; }

    /// <summary>Gets the owning session state when the failure occurred.</summary>
    internal BassAudioSessionState? SessionState { get; }

    /// <summary>Gets the BASS core device selected by the owning session.</summary>
    internal int? CoreDeviceIndex { get; }
}

/// <summary>Provides the small ManagedBass surface needed by mixer-source lifecycle code.</summary>
internal interface IBassMixerSourceNativeBoundary
{
    /// <summary>Gets the mixer that currently owns a source, or zero when it is not attached.</summary>
    int GetMixer(int sourceHandle);

    /// <summary>Adds a source to a mixer with the supplied mixer-channel flags.</summary>
    bool AddChannel(int mixerHandle, int sourceHandle, BassFlags flags);

    /// <summary>Sets or retrieves mixer-channel flags.</summary>
    BassFlags SetMixerChannelFlags(int sourceHandle, BassFlags flags, BassFlags mask);

    /// <summary>BASS channelの周波数、チャンネル数、flagsを取得します。</summary>
    BassMixerChannelInfo GetChannelInfo(int channelHandle);

    /// <summary>sourceのサンプルレート変換品質を設定します。</summary>
    bool SetSampleRateConversion(int sourceHandle, float quality);

    /// <summary>sourceのサンプルレート変換品質を取得します。</summary>
    bool GetSampleRateConversion(int sourceHandle, out float quality);

    /// <summary>pause中のsourceへnative契約のmixer出力行・source入力列の明示行列を設定します。</summary>
    bool SetMatrix(int sourceHandle, float[,] matrix);

    /// <summary>Removes a source from its mixer.</summary>
    bool RemoveChannel(int sourceHandle);

    /// <summary>Sets a source position using the supplied native position mode.</summary>
    bool SetPosition(int sourceHandle, long position, PositionFlags mode);

    /// <summary>Reads the BASS error immediately after a failed native call.</summary>
    Errors GetError();
}

/// <summary>Calls ManagedBass mixer-source APIs without hiding their failure contracts.</summary>
internal sealed class BassMixerSourceNativeBoundary : IBassMixerSourceNativeBoundary
{
    /// <inheritdoc />
    public int GetMixer(int sourceHandle) => BassMix.ChannelGetMixer(sourceHandle);

    /// <inheritdoc />
    public bool AddChannel(int mixerHandle, int sourceHandle, BassFlags flags)
        => BassMix.MixerAddChannel(mixerHandle, sourceHandle, flags);

    /// <inheritdoc />
    public BassFlags SetMixerChannelFlags(int sourceHandle, BassFlags flags, BassFlags mask)
        => BassMix.ChannelFlags(sourceHandle, flags, mask);

    /// <inheritdoc />
    public BassMixerChannelInfo GetChannelInfo(int channelHandle)
    {
        ChannelInfo info = Bass.ChannelGetInfo(channelHandle);
        return new BassMixerChannelInfo(info.Frequency, info.Channels, info.Flags);
    }

    /// <inheritdoc />
    public bool SetSampleRateConversion(int sourceHandle, float quality)
        => Bass.ChannelSetAttribute(sourceHandle, ChannelAttribute.SampleRateConversion, quality);

    /// <inheritdoc />
    public bool GetSampleRateConversion(int sourceHandle, out float quality)
        => Bass.ChannelGetAttribute(sourceHandle, ChannelAttribute.SampleRateConversion, out quality);

    /// <inheritdoc />
    public bool SetMatrix(int sourceHandle, float[,] matrix)
        => BassMix.ChannelSetMatrix(sourceHandle, matrix);

    /// <inheritdoc />
    public bool RemoveChannel(int sourceHandle) => BassMix.MixerRemoveChannel(sourceHandle);

    /// <inheritdoc />
    public bool SetPosition(int sourceHandle, long position, PositionFlags mode)
    {
        // 接続済みsourceはSRCの先読み状態もリセットし、再生ごとの終端を揃えます。
        return BassMix.ChannelGetMixer(sourceHandle) != 0
            ? BassMix.ChannelSetPosition(sourceHandle, position, mode)
            : Bass.ChannelSetPosition(sourceHandle, position, mode);
    }

    /// <inheritdoc />
    public Errors GetError() => Bass.LastError;
}

/// <summary>BASS channelの形式情報をnative API呼出し直後に保持します。</summary>
internal readonly record struct BassMixerChannelInfo(int Frequency, int Channels, BassFlags Flags);

/// <summary>Reports whether a source was newly attached to an expected mixer.</summary>
internal readonly struct BassMixerSourceAttachment
{
    /// <summary>Creates one attachment result.</summary>
    internal BassMixerSourceAttachment(bool newlyAttached, int actualMixerHandle)
    {
        NewlyAttached = newlyAttached;
        ActualMixerHandle = actualMixerHandle;
    }

    /// <summary>Gets whether this operation performed the attachment.</summary>
    internal bool NewlyAttached { get; }

    /// <summary>Gets the verified mixer handle.</summary>
    internal int ActualMixerHandle { get; }
}

/// <summary>Reports a verified source removal.</summary>
internal readonly struct BassMixerSourceRemoval
{
    /// <summary>Creates one removal result.</summary>
    internal BassMixerSourceRemoval(bool alreadyDetached, int actualMixerHandle)
    {
        AlreadyDetached = alreadyDetached;
        ActualMixerHandle = actualMixerHandle;
    }

    /// <summary>Gets whether the source was already detached before removal.</summary>
    internal bool AlreadyDetached { get; }

    /// <summary>Gets the final observed mixer handle, normally zero.</summary>
    internal int ActualMixerHandle { get; }
}

/// <summary>
/// Maintains mixer-source membership using BASS_Mixer_ChannelGetMixer as the native source of
/// truth. It never uses ChannelIsActive to decide whether a source is attached.
/// </summary>
internal sealed class BassMixerSourceController
{
    private const BassFlags MixerPauseFlag = BassFlags.MixerChanPause;
    private const BassFlags MixerSourceCreationFlags = BassFlags.MixerChanPause
        | BassFlags.MixerChanMatrix
        | BassFlags.MixerChanNoRampin;
    private const float RequiredSrcQuality = 6f;
    // BASS_MIXER_CHAN_NORAMP (0x00100000)。BASS_ChannelFlags用で、NORAMPINとは別のbitです。

    private readonly IBassMixerSourceNativeBoundary native;

    private readonly Func<BassAudioSession> sessionProvider;

    /// <summary>
    /// Creates a controller over the supplied native boundary and optional session provider.
    /// The provider is evaluated only when a failure needs diagnostic context.
    /// </summary>
    internal BassMixerSourceController(
        IBassMixerSourceNativeBoundary native,
        Func<BassAudioSession> sessionProvider = null)
    {
        this.native = native ?? throw new ArgumentNullException(nameof(native));
        this.sessionProvider = sessionProvider;
    }

    /// <summary>
    /// Ensures a source is attached to the expected mixer in paused mode and verifies the
    /// membership after an add, including the benign BASS_ERROR_ALREADY race.
    /// </summary>
    internal BassMixerSourceAttachment EnsureAttachedPaused(
        int expectedMixerHandle,
        int sourceHandle,
        string fileName)
    {
        ValidateHandles(expectedMixerHandle, sourceHandle, fileName);
        int actualMixerHandle = ReadMixer(
            sourceHandle,
            expectedMixerHandle,
            fileName,
            BassAudioPlaybackStage.MixerMembership,
            allowDetached: true,
            out Errors? membershipError);
        if (actualMixerHandle == expectedMixerHandle)
        {
            return new BassMixerSourceAttachment(newlyAttached: false, actualMixerHandle);
        }

        if (actualMixerHandle != 0)
        {
            throw Failure(
                BassAudioPlaybackStage.MixerMembership,
                fileName,
                sourceHandle,
                expectedMixerHandle,
                actualMixerHandle,
                "BASS_Mixer_ChannelGetMixer",
                membershipError,
                "The source is already attached to a different mixer.");
        }

        bool added;
        try
        {
            added = native.AddChannel(expectedMixerHandle, sourceHandle, MixerSourceCreationFlags);
        }
        catch (Exception exception)
        {
            throw Failure(
                BassAudioPlaybackStage.MixerAttach,
                fileName,
                sourceHandle,
                expectedMixerHandle,
                0,
                "BASS_Mixer_StreamAddChannel",
                null,
                "Adding the source to the expected mixer threw an exception.",
                exception);
        }

        if (!added)
        {
            Errors addError = native.GetError();
            if (addError == Errors.Already)
            {
                int racedMixer = ReadMixer(
                    sourceHandle,
                    expectedMixerHandle,
                    fileName,
                    BassAudioPlaybackStage.MixerMembership,
                    allowDetached: true,
                    out Errors? racedMembershipError);
                if (racedMixer == expectedMixerHandle)
                {
                    return new BassMixerSourceAttachment(newlyAttached: false, racedMixer);
                }

                if (racedMixer != 0)
                {
                    throw Failure(
                        BassAudioPlaybackStage.MixerMembership,
                        fileName,
                        sourceHandle,
                        expectedMixerHandle,
                        racedMixer,
                        "BASS_Mixer_ChannelGetMixer",
                        racedMembershipError ?? addError,
                        "A concurrent source attachment selected a different mixer.");
                }
            }

            throw Failure(
                BassAudioPlaybackStage.MixerAttach,
                fileName,
                sourceHandle,
                expectedMixerHandle,
                0,
                "BASS_Mixer_StreamAddChannel",
                addError,
                "Adding the source to the expected mixer failed.");
        }

        int verifiedMixer = ReadMixer(
            sourceHandle,
            expectedMixerHandle,
            fileName,
            BassAudioPlaybackStage.MixerAttach,
            allowDetached: true,
            out Errors? verifyError);
        if (verifiedMixer != expectedMixerHandle)
        {
            throw Failure(
                BassAudioPlaybackStage.MixerAttach,
                fileName,
                sourceHandle,
                expectedMixerHandle,
                verifiedMixer,
                "BASS_Mixer_ChannelGetMixer",
                verifyError,
                "The source attachment was not verified in the expected mixer.");
        }

        return new BassMixerSourceAttachment(newlyAttached: true, verifiedMixer);
    }

    /// <summary>pause 状態で source 形式、SRC 品質、行列を検証して設定します。</summary>
    internal void ConfigurePausedSource(
        int expectedMixerHandle,
        int sourceHandle,
        AudioChannelLayout sourceLayout,
        string fileName)
    {
        ArgumentNullException.ThrowIfNull(sourceLayout);
        ValidateHandles(expectedMixerHandle, sourceHandle, fileName);
        EnsureExpectedMixer(
            expectedMixerHandle,
            sourceHandle,
            fileName,
            BassAudioPlaybackStage.MixerSourceFormat);

        BassMixerChannelInfo sourceInfo = ReadChannelInfo(
            sourceHandle,
            expectedMixerHandle,
            fileName,
            "BASS_ChannelGetInfo(source)");
        if (sourceInfo.Frequency <= 0
            || sourceInfo.Channels != sourceLayout.ChannelCount
            || (sourceInfo.Flags & (BassFlags.Float | BassFlags.Decode)) != (BassFlags.Float | BassFlags.Decode))
        {
            throw Failure(
                BassAudioPlaybackStage.MixerSourceFormat,
                fileName,
                sourceHandle,
                expectedMixerHandle,
                expectedMixerHandle,
                "BASS_ChannelGetInfo(source)",
                null,
                "The mixer source must be a matching float32 decode channel.");
        }

        BassMixerChannelInfo mixerInfo = ReadChannelInfo(
            expectedMixerHandle,
            expectedMixerHandle,
            fileName,
            "BASS_ChannelGetInfo(mixer)");
        if (mixerInfo.Frequency <= 0 || mixerInfo.Channels is < 1 or > 8)
        {
            throw Failure(
                BassAudioPlaybackStage.MixerSourceFormat,
                fileName,
                sourceHandle,
                expectedMixerHandle,
                expectedMixerHandle,
                "BASS_ChannelGetInfo(mixer)",
                null,
                "The output mixer must expose a known one-to-eight-channel format.");
        }

        if (sourceInfo.Frequency != mixerInfo.Frequency)
        {
            SetAndConfirmSrc(sourceHandle, expectedMixerHandle, fileName);
        }

        // pause中のmatrix設定はnative仕様上rampされず、開始時はNORAMPINで抑制します。
        float[,] matrix;
        try
        {
            matrix = AudioChannelMatrix.Create(
                sourceLayout,
                AudioChannelLayout.CreateBassOutput(mixerInfo.Channels));
        }
        catch (ArgumentException exception)
        {
            throw Failure(
                BassAudioPlaybackStage.MixerMatrix,
                fileName,
                sourceHandle,
                expectedMixerHandle,
                expectedMixerHandle,
                "AudioChannelMatrix.Create",
                null,
                "The source or output speaker layout has no defined routing matrix.",
                exception);
        }

        bool succeeded;
        try
        {
            succeeded = native.SetMatrix(sourceHandle, matrix);
        }
        catch (Exception exception)
        {
            throw Failure(
                BassAudioPlaybackStage.MixerMatrix,
                fileName,
                sourceHandle,
                expectedMixerHandle,
                expectedMixerHandle,
                "BASS_Mixer_ChannelSetMatrix",
                null,
                "Setting the paused source matrix threw an exception.",
                exception);
        }

        if (!succeeded)
        {
            Errors error = native.GetError();
            throw Failure(
                BassAudioPlaybackStage.MixerMatrix,
                fileName,
                sourceHandle,
                expectedMixerHandle,
                expectedMixerHandle,
                "BASS_Mixer_ChannelSetMatrix",
                error,
                "Setting the paused source matrix failed.");
        }
    }

    /// <summary>Verifies that a source belongs to the expected mixer without attaching it.</summary>
    internal int EnsureExpectedMixer(
        int expectedMixerHandle,
        int sourceHandle,
        string fileName,
        BassAudioPlaybackStage stage = BassAudioPlaybackStage.MixerMembership)
    {
        ValidateHandles(expectedMixerHandle, sourceHandle, fileName);
        int actualMixerHandle = ReadMixer(
            sourceHandle,
            expectedMixerHandle,
            fileName,
            stage,
            allowDetached: true,
            out Errors? membershipError);
        if (actualMixerHandle == expectedMixerHandle)
        {
            return actualMixerHandle;
        }

        throw Failure(
            stage,
            fileName,
            sourceHandle,
            expectedMixerHandle,
            actualMixerHandle,
            "BASS_Mixer_ChannelGetMixer",
            membershipError,
            actualMixerHandle == 0
                ? "The source is not attached to the expected mixer."
                : "The source is attached to a different mixer.");
    }

    /// <summary>Removes the source from its expected mixer and verifies detachment.</summary>
    internal BassMixerSourceRemoval RemoveFromExpectedMixer(
        int expectedMixerHandle,
        int sourceHandle,
        string fileName)
    {
        ValidateHandles(expectedMixerHandle, sourceHandle, fileName);
        int actualMixerHandle = ReadMixer(
            sourceHandle,
            expectedMixerHandle,
            fileName,
            BassAudioPlaybackStage.MixerMembership,
            allowDetached: true,
            out Errors? membershipError);
        if (actualMixerHandle == 0)
        {
            return new BassMixerSourceRemoval(alreadyDetached: true, actualMixerHandle);
        }
        if (actualMixerHandle != expectedMixerHandle)
        {
            throw Failure(
                BassAudioPlaybackStage.MixerMembership,
                fileName,
                sourceHandle,
                expectedMixerHandle,
                actualMixerHandle,
                "BASS_Mixer_ChannelGetMixer",
                membershipError,
                "The source is attached to a different mixer and was not removed.");
        }

        bool removed;
        try
        {
            removed = native.RemoveChannel(sourceHandle);
        }
        catch (Exception exception)
        {
            throw Failure(
                BassAudioPlaybackStage.MixerRemove,
                fileName,
                sourceHandle,
                expectedMixerHandle,
                actualMixerHandle,
                "BASS_Mixer_ChannelRemove",
                null,
                "Removing the source from the expected mixer threw an exception.",
                exception);
        }

        if (!removed)
        {
            Errors removeError = native.GetError();
            int afterFailedRemove = ReadMixer(
                sourceHandle,
                expectedMixerHandle,
                fileName,
                BassAudioPlaybackStage.MixerRemove,
                allowDetached: true,
                out _);
            if (removeError == Errors.Handle && afterFailedRemove == 0)
            {
                return new BassMixerSourceRemoval(alreadyDetached: true, afterFailedRemove);
            }

            throw Failure(
                BassAudioPlaybackStage.MixerRemove,
                fileName,
                sourceHandle,
                expectedMixerHandle,
                afterFailedRemove,
                "BASS_Mixer_ChannelRemove",
                removeError,
                "Removing the source from the expected mixer failed.");
        }

        int verifiedMixer = ReadMixer(
            sourceHandle,
            expectedMixerHandle,
            fileName,
            BassAudioPlaybackStage.MixerRemove,
            allowDetached: true,
            out Errors? verifyError);
        if (verifiedMixer != 0)
        {
            throw Failure(
                BassAudioPlaybackStage.MixerRemove,
                fileName,
                sourceHandle,
                expectedMixerHandle,
                verifiedMixer,
                "BASS_Mixer_ChannelGetMixer",
                verifyError,
                "The source removal was not verified.");
        }

        return new BassMixerSourceRemoval(alreadyDetached: false, verifiedMixer);
    }

    /// <summary>Sets the mixer pause flag after verifying source membership.</summary>
    internal void Pause(int expectedMixerHandle, int sourceHandle, string fileName)
        => SetPauseFlag(expectedMixerHandle, sourceHandle, fileName, paused: true);

    /// <summary>Removes the mixer pause flag after verifying source membership.</summary>
    internal void Resume(int expectedMixerHandle, int sourceHandle, string fileName)
        => SetPauseFlag(expectedMixerHandle, sourceHandle, fileName, paused: false);

    /// <summary>Sets the source position and captures a failure immediately if BASS rejects it.</summary>
    internal void SetPosition(int sourceHandle, long position, string fileName, int expectedMixerHandle)
    {
        if (sourceHandle == 0)
        {
            throw Failure(
                BassAudioPlaybackStage.SetPosition,
                fileName,
                sourceHandle,
                expectedMixerHandle,
                0,
                "BASS_ChannelSetPosition",
                null,
                "The source handle is not valid.");
        }

        bool succeeded;
        try
        {
            succeeded = native.SetPosition(sourceHandle, position, PositionFlags.Bytes);
        }
        catch (Exception exception)
        {
            throw Failure(
                BassAudioPlaybackStage.SetPosition,
                fileName,
                sourceHandle,
                expectedMixerHandle,
                0,
                "BASS_ChannelSetPosition",
                null,
                "Setting the source position threw an exception.",
                exception);
        }

        if (!succeeded)
        {
            Errors error = native.GetError();
            throw Failure(
                BassAudioPlaybackStage.SetPosition,
                fileName,
                sourceHandle,
                expectedMixerHandle,
                0,
                "BASS_ChannelSetPosition",
                error,
                "Setting the source position failed.");
        }
    }

    private void SetPauseFlag(
        int expectedMixerHandle,
        int sourceHandle,
        string fileName,
        bool paused)
    {
        EnsureExpectedMixer(
            expectedMixerHandle,
            sourceHandle,
            fileName,
            paused ? BassAudioPlaybackStage.MixerPause : BassAudioPlaybackStage.MixerResume);
        BassFlags flags = paused ? MixerPauseFlag : BassFlags.Default;
        BassFlags updatedFlags;
        try
        {
            updatedFlags = native.SetMixerChannelFlags(sourceHandle, flags, MixerPauseFlag);
        }
        catch (Exception exception)
        {
            throw Failure(
                paused ? BassAudioPlaybackStage.MixerPause : BassAudioPlaybackStage.MixerResume,
                fileName,
                sourceHandle,
                expectedMixerHandle,
                expectedMixerHandle,
                "BASS_Mixer_ChannelFlags",
                null,
                paused ? "Pausing the mixer source threw an exception." : "Resuming the mixer source threw an exception.",
                exception);
        }

        if (unchecked((int)updatedFlags) == -1)
        {
            Errors error = native.GetError();
            throw Failure(
                paused ? BassAudioPlaybackStage.MixerPause : BassAudioPlaybackStage.MixerResume,
                fileName,
                sourceHandle,
                expectedMixerHandle,
                expectedMixerHandle,
                "BASS_Mixer_ChannelFlags",
                error,
                paused ? "Pausing the mixer source failed." : "Resuming the mixer source failed.");
        }
    }

    private BassMixerChannelInfo ReadChannelInfo(
        int channelHandle,
        int expectedMixerHandle,
        string fileName,
        string nativeSource)
    {
        try
        {
            return native.GetChannelInfo(channelHandle);
        }
        catch (Exception exception)
        {
            throw Failure(
                BassAudioPlaybackStage.MixerSourceFormat,
                fileName,
                channelHandle,
                expectedMixerHandle,
                expectedMixerHandle,
                nativeSource,
                null,
                "Reading a BASS channel format threw an exception.",
                exception);
        }
    }

    private void SetAndConfirmSrc(int sourceHandle, int expectedMixerHandle, string fileName)
    {
        bool set;
        try
        {
            set = native.SetSampleRateConversion(sourceHandle, RequiredSrcQuality);
        }
        catch (Exception exception)
        {
            throw Failure(
                BassAudioPlaybackStage.MixerSourceFormat,
                fileName,
                sourceHandle,
                expectedMixerHandle,
                expectedMixerHandle,
                "BASS_ChannelSetAttribute(BASS_ATTRIB_SRC)",
                null,
                "Setting the source SRC quality threw an exception.",
                exception);
        }
        if (!set)
        {
            Errors error = native.GetError();
            throw Failure(
                BassAudioPlaybackStage.MixerSourceFormat,
                fileName,
                sourceHandle,
                expectedMixerHandle,
                expectedMixerHandle,
                "BASS_ChannelSetAttribute(BASS_ATTRIB_SRC)",
                error,
                "Setting source SRC quality to six failed.");
        }

        bool read;
        float actualQuality;
        try
        {
            read = native.GetSampleRateConversion(sourceHandle, out actualQuality);
        }
        catch (Exception exception)
        {
            throw Failure(
                BassAudioPlaybackStage.MixerSourceFormat,
                fileName,
                sourceHandle,
                expectedMixerHandle,
                expectedMixerHandle,
                "BASS_ChannelGetAttribute(BASS_ATTRIB_SRC)",
                null,
                "Reading back source SRC quality threw an exception.",
                exception);
        }
        if (!read)
        {
            Errors error = native.GetError();
            throw Failure(
                BassAudioPlaybackStage.MixerSourceFormat,
                fileName,
                sourceHandle,
                expectedMixerHandle,
                expectedMixerHandle,
                "BASS_ChannelGetAttribute(BASS_ATTRIB_SRC)",
                error,
                "Reading back source SRC quality failed.");
        }
        if (actualQuality != RequiredSrcQuality)
        {
            throw Failure(
                BassAudioPlaybackStage.MixerSourceFormat,
                fileName,
                sourceHandle,
                expectedMixerHandle,
                expectedMixerHandle,
                "BASS_ChannelGetAttribute(BASS_ATTRIB_SRC)",
                null,
                $"BASS reported SRC quality {actualQuality} after requesting six.");
        }
    }

    private int ReadMixer(
        int sourceHandle,
        int expectedMixerHandle,
        string fileName,
        BassAudioPlaybackStage stage,
        bool allowDetached,
        out Errors? error)
    {
        int actualMixerHandle;
        try
        {
            actualMixerHandle = native.GetMixer(sourceHandle);
        }
        catch (Exception exception)
        {
            error = null;
            throw Failure(
                stage,
                fileName,
                sourceHandle,
                expectedMixerHandle,
                0,
                "BASS_Mixer_ChannelGetMixer",
                null,
                "Reading mixer membership threw an exception.",
                exception);
        }

        if (actualMixerHandle != 0)
        {
            error = null;
            return actualMixerHandle;
        }

        error = native.GetError();
        if (allowDetached && error == Errors.Handle)
        {
            return 0;
        }

        throw Failure(
            stage,
            fileName,
            sourceHandle,
            expectedMixerHandle,
            0,
            "BASS_Mixer_ChannelGetMixer",
            error,
            "Reading mixer membership failed.");
    }

    private void ValidateHandles(int expectedMixerHandle, int sourceHandle, string fileName)
    {
        if (expectedMixerHandle == 0 || sourceHandle == 0)
        {
            throw Failure(
                BassAudioPlaybackStage.MixerMembership,
                fileName,
                sourceHandle,
                expectedMixerHandle,
                0,
                "BASS_Mixer_ChannelGetMixer",
                null,
                "The expected mixer handle and source handle must be valid.");
        }
    }

    private BassAudioPlaybackException Failure(
        BassAudioPlaybackStage stage,
        string fileName,
        int sourceHandle,
        int expectedMixerHandle,
        int actualMixerHandle,
        string nativeErrorSource,
        Errors? nativeErrorCode,
        string message,
        Exception innerException = null)
        => new(
            stage,
            fileName,
            sourceHandle,
            expectedMixerHandle,
            actualMixerHandle,
            nativeErrorSource,
            nativeErrorCode,
            message,
            GetSessionSnapshot(),
            innerException);

    private BassAudioSession GetSessionSnapshot()
    {
        try
        {
            return sessionProvider?.Invoke();
        }
        catch
        {
            return null;
        }
    }
}
