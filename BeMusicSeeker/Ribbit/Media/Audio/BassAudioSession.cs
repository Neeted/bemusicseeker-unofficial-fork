#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using ManagedBass;
using ManagedBass.Asio;
using ManagedBass.Wasapi;
using Ribbit.Logging;

namespace Ribbit.Media.Audio;

/// <summary>
/// Identifies the lifecycle phase of one native audio graph.
/// </summary>
internal enum BassAudioSessionState
{
    /// <summary>The native graph is being acquired.</summary>
    Initializing,

    /// <summary>The native graph is available to audio players.</summary>
    Active,

    /// <summary>Cleanup failed and ownership is retained for a later retry.</summary>
    CleanupPending,

    /// <summary>No native resource remains owned by the session.</summary>
    Released
}

/// <summary>callbackで観測した出力故障を値だけで保持します。</summary>
internal readonly record struct AudioCallbackOutputFailure(
    AudioPcmRenderStage? RenderStage,
    AudioOutputProcessFailure? ProcessStage,
    Errors? NativeError);

/// <summary>管理側がcallback出力故障を消費したときに投げる診断例外です。</summary>
internal sealed class AudioCallbackOutputFailureException : InvalidOperationException
{
    /// <summary>故障の分類とネイティブエラーを保持し、表示用の説明を作成します。</summary>
    internal AudioCallbackOutputFailureException(AudioCallbackOutputFailure failure)
        : base(string.Format(BeMusicSeeker.Properties.Resources.AudioCallbackOutputFailureFormat,
            failure.RenderStage?.ToString() ?? failure.ProcessStage?.ToString() ?? "Unknown",
            failure.NativeError?.ToString() ?? "-"))
    {
        Failure = failure;
    }

    /// <summary>callbackが一度だけ記録した故障stageとnative errorを取得します。</summary>
    internal AudioCallbackOutputFailure Failure { get; }
}

/// <summary>
/// Records native ownership for one BASS audio graph from the start of initialization
/// until every acquired layer has been released.
/// </summary>
internal sealed class BassAudioSession
{
    private readonly object callbackPullSync = new();
    private readonly object playerStreamSync = new();
    private readonly Dictionary<int, BassAudioOwnedStream> ownedStreams = [];
    private int callbackOutputHandle;
    private int callbackOutputFailureState;
    private AudioCallbackOutputFailure callbackOutputFailure;
    private int outputOverLevelNotificationState;
    private int callbackOutputPaused;
    private int realtimeReservedCallbackFrames = -1;
    private int maximumCallbackFrames;
    private readonly TaskCompletionSource firstCallbackObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>要求backend、endpoint、SRC品質を捕捉した初期化中sessionを作成します。</summary>
    internal BassAudioSession(
        BassAudioPlayer.DeviceDriver requestedBackend,
        BassAudioPlayer.DeviceDescriptor requestedDevice = default,
        int sampleRateConversionQuality = AudioResamplingQuality.Default,
        AudioOutputRequest outputRequest = null)
    {
        RequestedBackend = requestedBackend;
        RequestedDevice = requestedDevice;
        SampleRateConversionQuality = AudioResamplingQuality.Validate(sampleRateConversionQuality);
        OutputRequest = outputRequest;
        ActualBackend = BassAudioPlayer.DeviceDriver.INVALID;
        CoreDeviceIndex = -1;
        WasapiDeviceIndex = -1;
        AsioDeviceIndex = -1;
        State = BassAudioSessionState.Initializing;
    }

    /// <summary>初期化時に捕捉した、このsessionが使うSRC品質を取得します。</summary>
    internal int SampleRateConversionQuality { get; }

    /// <summary>このsessionの初期化条件全体です。旧変換入口で未設定の場合はnullです。</summary>
    internal AudioOutputRequest OutputRequest { get; }

    /// <summary>Gets the backend selected by the caller.</summary>
    internal BassAudioPlayer.DeviceDriver RequestedBackend { get; }

    /// <summary>Gets the endpoint selected by the caller.</summary>
    internal BassAudioPlayer.DeviceDescriptor RequestedDevice { get; }

    /// <summary>Gets or sets the backend currently being initialized or used.</summary>
    internal BassAudioPlayer.DeviceDriver ActualBackend { get; set; }

    /// <summary>Gets or sets the endpoint selected by native initialization.</summary>
    internal BassAudioPlayer.DeviceDescriptor ActualDevice { get; set; }

    /// <summary>Gets or sets the BASS core device that owns core streams.</summary>
    internal int CoreDeviceIndex { get; set; }

    /// <summary>Gets or sets the BASSWASAPI device owned by the session.</summary>
    internal int WasapiDeviceIndex { get; set; }

    /// <summary>Gets or sets the BASSASIO device owned by the session.</summary>
    internal int AsioDeviceIndex { get; set; }

    /// <summary>Gets or sets whether BASS core initialization completed.</summary>
    internal bool CoreInitialized { get; set; }

    /// <summary>Gets or sets whether BASSWASAPI initialization completed.</summary>
    internal bool WasapiInitialized { get; set; }

    /// <summary>Gets or sets whether BASSASIO initialization completed.</summary>
    internal bool AsioInitialized { get; set; }

    /// <summary>Gets or sets the primary mixer handle owned by the session.</summary>
    internal int MixerHandle { get; set; }

    /// <summary>Gets or sets the output handle owned by the session.</summary>
    internal int OutputHandle { get; set; }

    /// <summary>
    /// Gets the stream currently published to callback-driven output without taking the
    /// lifecycle lock.
    /// </summary>
    internal int CallbackOutputHandle => Volatile.Read(ref callbackOutputHandle);

    /// <summary>追加stream所有の互換表示をsnapshotで取得します。</summary>
    internal IReadOnlyList<int> AdditionalStreamHandles => GetAdditionalStreamHandles();

    /// <summary>sessionが所有辞書で追跡する追加streamとplayer sourceの件数を取得します。</summary>
    internal int OwnedStreamCount
    {
        get
        {
            lock (playerStreamSync)
            {
                return ownedStreams.Count;
            }
        }
    }

    /// <summary>Gets or sets whether the backend output was started.</summary>
    internal bool IsStarted { get; set; }

    /// <summary>Gets or sets the values accepted by the initialized native backend.</summary>
    internal BassAudioBackendResult NegotiationResult { get; set; }

    /// <summary>callback backendがDSP後のFloat32 gainに使うprocessorを取得または設定します。</summary>
    internal AudioOutputProcessor? OutputProcessor { get; set; }

    /// <summary>callback backendが再利用するFloat32 PCM pull rendererを取得または設定します。</summary>
    internal AudioPcmRenderer? CallbackPcmRenderer { get; set; }

    /// <summary>再生callbackが一回に要求した最大frame数を取得します。</summary>
    internal int MaximumCallbackFrames => Volatile.Read(ref maximumCallbackFrames);

    /// <summary>物理出力の初回実pullまたは故障を待ち、管理側で故障を取り出します。NullDeviceはcallbackを待ちません。</summary>
    internal async Task WaitForOutputReadyAsync()
    {
        if (ActualBackend != BassAudioPlayer.DeviceDriver.NULL_DEVICE)
        {
            await firstCallbackObserved.Task.ConfigureAwait(false);
        }
        ThrowIfCallbackOutputFailed();
    }

    /// <summary>callback出力がnative pullとの合流境界でpause中か取得します。</summary>
    internal bool IsCallbackOutputPaused => Volatile.Read(ref callbackOutputPaused) != 0;

    /// <summary>Realtime予約が先読みへ含めたcallback frame数を取得します。</summary>
    internal int RealtimeReservedCallbackFrames => Volatile.Read(ref realtimeReservedCallbackFrames);

    /// <summary>callback入力の実測block上限をBMS発音の先行予約へ反映します。</summary>
    internal void ObserveCallbackPullSize(int requestedFrames)
    {
        if (requestedFrames <= 0)
        {
            return;
        }

        int observedMaximum = Volatile.Read(ref maximumCallbackFrames);
        while (requestedFrames > observedMaximum)
        {
            int prior = Interlocked.CompareExchange(ref maximumCallbackFrames, requestedFrames, observedMaximum);
            if (prior == observedMaximum)
            {
                break;
            }
            observedMaximum = prior;
        }
        firstCallbackObserved.TrySetResult();
    }

    /// <summary>予約が先読みへ含めたcallback block上限を公開します。</summary>
    internal void PublishRealtimeReservedCallbackFrames(int requestedFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedFrames);
        Volatile.Write(ref realtimeReservedCallbackFrames, requestedFrames);
    }

    /// <summary>Realtime予約の解放後にcallback blockの予約上限を無効化します。</summary>
    internal void ClearRealtimeReservedCallbackFrames() => Volatile.Write(ref realtimeReservedCallbackFrames, -1);

    /// <summary>callback pullと直列化して譜面・tempoを含む出力graphを凍結または再開します。</summary>
    internal void SetCallbackOutputPaused(bool paused)
    {
        lock (callbackPullSync)
        {
            Volatile.Write(ref callbackOutputPaused, paused ? 1 : 0);
        }
    }

    /// <summary>出力callbackがpause gateを保持している間にPCM取得と公開を実行します。</summary>
    internal T WithCallbackOutputPull<T>(Func<bool, T> pull)
    {
        ArgumentNullException.ThrowIfNull(pull);
        lock (callbackPullSync)
        {
            return pull(Volatile.Read(ref callbackOutputPaused) != 0);
        }
    }

    /// <summary>Gets the current ownership phase.</summary>
    internal BassAudioSessionState State { get; set; }

    /// <summary>直近の解放で確認できなかったnative操作を、そのsessionの終端診断へ渡します。</summary>
    internal IReadOnlyList<BassAudioCleanupDiagnostic> CleanupDiagnostics { get; set; }
        = Array.Empty<BassAudioCleanupDiagnostic>();

    /// <summary>Gets whether native resources still require cleanup.</summary>
    internal bool HasNativeOwnership =>
        CoreInitialized
        || WasapiInitialized
        || AsioInitialized
        || MixerHandle != 0
        || OutputHandle != 0
        || HasOwnedStreams
        || IsStarted;

    /// <summary>Gets whether cleanup has been fully confirmed.</summary>
    internal bool IsReleased =>
        State == BassAudioSessionState.Released && !HasNativeOwnership;

    /// <summary>最初のcallback出力故障だけをallocationなしで記録します。</summary>
    internal bool TryRecordCallbackOutputFailure(
        AudioPcmRenderStage renderStage,
        Errors? nativeError = null) =>
        TryRecordCallbackOutputFailure(new AudioCallbackOutputFailure(renderStage, null, nativeError));

    /// <summary>最初のcallback gain故障だけをallocationなしで記録します。</summary>
    internal bool TryRecordCallbackOutputFailure(AudioOutputProcessFailure processStage) =>
        TryRecordCallbackOutputFailure(new AudioCallbackOutputFailure(null, processStage, null));

    /// <summary>callback故障が観測済みで、以後のcallbackを無音にする必要があるかを取得します。</summary>
    internal bool HasCallbackOutputFailure => Volatile.Read(ref callbackOutputFailureState) != 0;

    /// <summary>保持したcallback出力故障を管理側で一度だけ消費します。</summary>
    internal bool TryConsumeCallbackOutputFailure(out AudioCallbackOutputFailure failure)
    {
        if (Interlocked.CompareExchange(ref callbackOutputFailureState, 3, 2) != 2)
        {
            failure = default;
            return false;
        }

        failure = callbackOutputFailure;
        return true;
    }

    /// <summary>callback出力故障が既に回収済みでも、故障sessionの再利用を拒否します。</summary>
    internal void ThrowIfCallbackOutputFailed()
    {
        int state = Volatile.Read(ref callbackOutputFailureState);
        if (state == 0)
        {
            return;
        }

        var spin = new SpinWait();
        while (state == 1)
        {
            spin.SpinOnce();
            state = Volatile.Read(ref callbackOutputFailureState);
        }

        throw new AudioCallbackOutputFailureException(callbackOutputFailure);
    }

    /// <summary>保留故障があれば管理側の既存例外経路へ投げます。</summary>
    internal void ThrowPendingOutputFailure()
    {
        if (TryConsumeCallbackOutputFailure(out AudioCallbackOutputFailure failure))
        {
            throw new AudioCallbackOutputFailureException(failure);
        }
    }

    /// <summary>最初のfull scale超過を管理側への一回通知待ちとして記録します。</summary>
    internal bool TryMarkOutputOverLevelPending() =>
        Interlocked.CompareExchange(ref outputOverLevelNotificationState, 1, 0) == 0;

    /// <summary>保留中のfull scale超過を管理側で一度だけ消費します。</summary>
    internal bool TryConsumeOutputOverLevelPending() =>
        Interlocked.CompareExchange(ref outputOverLevelNotificationState, 2, 1) == 1;

    private bool TryRecordCallbackOutputFailure(AudioCallbackOutputFailure failure)
    {
        if (Interlocked.CompareExchange(ref callbackOutputFailureState, 1, 0) != 0)
        {
            return false;
        }

        callbackOutputFailure = failure;
        Volatile.Write(ref callbackOutputFailureState, 2);
        firstCallbackObserved.TrySetResult();
        return true;
    }

    /// <summary>Records the stream currently supplying the backend output.</summary>
    internal void TrackOutputHandle(int handle)
    {
        OutputHandle = handle;
        PublishCallbackOutputHandle(handle);
        RemoveAdditionalStreamHandle(handle);
    }

    /// <summary>
    /// Publishes a valid callback source before replacing or releasing the previously
    /// tracked output stream.
    /// </summary>
    internal void PublishCallbackOutputHandle(int handle)
    {
        Volatile.Write(ref callbackOutputHandle, handle);
    }

    /// <summary>
    /// Publishes a replacement callback source before releasing the previous stream and
    /// restores the previous source when release cannot be confirmed, including when the
    /// release delegate throws.
    /// </summary>
    internal bool TryPrepareCallbackOutputReplacement(
        int previousHandle,
        int replacementHandle,
        Func<int, bool> releasePrevious)
    {
        ArgumentNullException.ThrowIfNull(releasePrevious);
        PublishCallbackOutputHandle(replacementHandle);
        try
        {
            if (releasePrevious(previousHandle))
            {
                return true;
            }
        }
        catch
        {
            PublishCallbackOutputHandle(previousHandle);
            throw;
        }

        PublishCallbackOutputHandle(previousHandle);
        return false;
    }

    /// <summary>Records that a stream release was confirmed by the native API.</summary>
    internal void ConfirmStreamReleased(int handle)
    {
        if (MixerHandle == handle)
        {
            MixerHandle = 0;
        }
        if (OutputHandle == handle)
        {
            OutputHandle = 0;
        }
        Interlocked.CompareExchange(ref callbackOutputHandle, 0, handle);
        BassAudioOwnedStream released = null;
        lock (playerStreamSync)
        {
            if (ownedStreams.Remove(handle, out BassAudioOwnedStream owned))
            {
                released = owned;
            }
        }
        released?.NotifyReleased();
    }

    /// <summary>session cleanup対象となる追加handleを一つだけ登録します。</summary>
    internal void TrackAdditionalStreamHandle(int handle)
    {
        if (!TryTrackOwnedStream(handle, this, static _ => { }, isPlayerStream: false, out _))
        {
            throw new InvalidOperationException("The BASS stream handle is already owned by this session.");
        }
    }

    /// <summary>追加handleが一意所有としてsessionに登録されているか確認します。</summary>
    internal bool IsAdditionalStreamHandleTracked(int handle)
    {
        lock (playerStreamSync)
        {
            return ownedStreams.TryGetValue(handle, out BassAudioOwnedStream stream)
                && !stream.IsPlayerStream;
        }
    }

    /// <summary>
    /// Retains a source stream and its managed callback owner until native release is confirmed.
    /// </summary>
    internal void TrackPlayerStream(int handle, object owner, Action<int> releaseConfirmed)
    {
        if (!TryTrackPlayerStreamForCleanup(handle, owner, releaseConfirmed, out _))
        {
            throw new InvalidOperationException("The BASS source stream is already owned by this session.");
        }
    }

    /// <summary>
    /// Retains a newly created source for cleanup after the normal tracking path has failed.
    /// The result distinguishes an existing owner so cleanup never claims another player's
    /// native handle.
    /// </summary>
    internal bool TryTrackPlayerStreamForCleanup(
        int handle,
        object owner,
        Action<int> releaseConfirmed,
        out bool alreadyOwned)
    {
        if (handle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(handle));
        }
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(releaseConfirmed);

        return TryTrackOwnedStream(handle, owner, releaseConfirmed, isPlayerStream: true, out alreadyOwned);
    }

    private bool TryTrackOwnedStream(
        int handle,
        object owner,
        Action<int> releaseConfirmed,
        bool isPlayerStream,
        out bool alreadyOwned)
    {
        if (handle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(handle));
        }
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(releaseConfirmed);
        lock (playerStreamSync)
        {
            alreadyOwned = ownedStreams.ContainsKey(handle);
            if (alreadyOwned)
            {
                return false;
            }
            ownedStreams.Add(
                handle,
                new BassAudioOwnedStream(handle, owner, releaseConfirmed, isPlayerStream));
            return true;
        }
    }

    /// <summary>Gets a stable snapshot of source streams retained by this session.</summary>
    internal IReadOnlyList<BassAudioOwnedStream> GetPlayerStreams()
    {
        lock (playerStreamSync)
        {
            return ownedStreams.Values.Where(stream => stream.IsPlayerStream).ToArray();
        }
    }

    /// <summary>sessionが所有するstreamを安定したsnapshotで列挙します。</summary>
    internal IReadOnlyList<BassAudioOwnedStream> GetOwnedStreams()
    {
        lock (playerStreamSync)
        {
            return ownedStreams.Values.ToArray();
        }
    }

    private IReadOnlyList<int> GetAdditionalStreamHandles()
    {
        lock (playerStreamSync)
        {
            return ownedStreams.Values
                .Where(stream => !stream.IsPlayerStream)
                .Select(stream => stream.Handle)
                .ToArray();
        }
    }

    /// <summary>
    /// Resolves a retained player owner for a native source callback without creating a
    /// separate unmanaged handle registry.
    /// </summary>
    internal bool TryGetPlayerStreamOwner<T>(int handle, out T owner)
        where T : class
    {
        lock (playerStreamSync)
        {
            if (ownedStreams.TryGetValue(handle, out BassAudioOwnedStream stream)
                && stream.IsPlayerStream
                && stream.TryGetOwner(out owner))
            {
                return true;
            }
        }

        owner = null;
        return false;
    }

    /// <summary>
    /// Forgets a source stream and notifies its owner only after native release is confirmed.
    /// </summary>
    internal void ConfirmPlayerStreamReleased(int handle)
    {
        ConfirmStreamReleased(handle);
    }

    private bool HasOwnedStreams
    {
        get
        {
            lock (playerStreamSync)
            {
                return ownedStreams.Count != 0;
            }
        }
    }

    private void RemoveAdditionalStreamHandle(int handle)
    {
        lock (playerStreamSync)
        {
            if (ownedStreams.TryGetValue(handle, out BassAudioOwnedStream stream)
                && !stream.IsPlayerStream)
            {
                ownedStreams.Remove(handle);
            }
        }
    }
}

/// <summary>
/// Keeps a BASS source stream's callback owner alive until native release is confirmed.
/// </summary>
internal sealed class BassAudioOwnedStream
{
    private readonly object owner;
    private readonly Action<int> releaseConfirmed;

    /// <summary>Creates retained ownership for one source stream.</summary>
    internal BassAudioOwnedStream(int handle, object owner, Action<int> releaseConfirmed, bool isPlayerStream)
    {
        Handle = handle;
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.releaseConfirmed = releaseConfirmed ?? throw new ArgumentNullException(nameof(releaseConfirmed));
        IsPlayerStream = isPlayerStream;
    }

    /// <summary>Gets the native stream handle.</summary>
    internal int Handle { get; }

    /// <summary>source callback ownerを持つplayer streamか取得します。</summary>
    internal bool IsPlayerStream { get; }

    /// <summary>Tries to expose the retained managed owner to a lifecycle-safe callback.</summary>
    internal bool TryGetOwner<T>(out T typedOwner)
        where T : class
    {
        typedOwner = owner as T;
        return typedOwner != null;
    }

    /// <summary>Notifies the managed owner that native callbacks can no longer occur.</summary>
    internal void NotifyReleased()
    {
        GC.KeepAlive(owner);
        releaseConfirmed(Handle);
    }
}

/// <summary>
/// Retains a scoped session token until native cleanup has been confirmed.
/// </summary>
internal sealed class BassAudioSessionLease
{
    /// <summary>Gets the session token currently retained by this consumer.</summary>
    internal BassAudioSession Session { get; private set; }

    /// <summary>Attaches the session initialized for this consumer.</summary>
    internal void Attach(BassAudioSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (Session != null
            && !ReferenceEquals(Session, session)
            && !Session.IsReleased)
        {
            throw new InvalidOperationException("The audio consumer already owns another session token.");
        }

        Session = session;
    }

    /// <summary>
    /// Attempts cleanup and forgets the token only after the release boundary confirms success.
    /// </summary>
    internal bool TryRelease(Func<BassAudioSession, bool> release)
    {
        ArgumentNullException.ThrowIfNull(release);
        BassAudioSession session = Session;
        if (session == null)
        {
            return true;
        }
        if (!release(session))
        {
            return false;
        }

        Session = null;
        return true;
    }
}

/// <summary>
/// Serializes initialization and release while retaining the one authoritative session,
/// including a session whose native cleanup must be retried.
/// </summary>
internal sealed class BassAudioSessionLifecycle
{
    private readonly object syncRoot = new();
    private BassAudioSession currentSession;

    /// <summary>Enters the lifecycle gate. Dispose the returned lease to leave it.</summary>
    internal IDisposable Enter()
    {
        Monitor.Enter(syncRoot);
        return new MonitorLease(syncRoot);
    }

    /// <summary>Gets the current session while the caller owns the lifecycle gate.</summary>
    internal BassAudioSession CurrentSession => currentSession;

    /// <summary>
    /// Gets the current session without the lifecycle lock while the caller owns an admitted
    /// audio operation that prevents lifecycle replacement and cleanup.
    /// </summary>
    internal BassAudioSession CurrentSessionForAdmittedOperation =>
        Volatile.Read(ref currentSession);

    /// <summary>Gets whether an active graph is currently available.</summary>
    internal bool IsActive => currentSession?.State == BassAudioSessionState.Active;

    /// <summary>Gets whether native ownership remains after a cleanup failure.</summary>
    internal bool HasCleanupPending => currentSession?.State == BassAudioSessionState.CleanupPending;

    /// <summary>
    /// Gets whether a non-active session still requires cleanup or lifecycle recovery.
    /// </summary>
    internal bool HasUnconfirmedOwnership =>
        currentSession?.State is BassAudioSessionState.Initializing or BassAudioSessionState.CleanupPending;

    /// <summary>有効なsessionや解放保留sessionがない場合、新しい初期化の所有sessionを開始します。</summary>
    internal bool TryBegin(
        BassAudioPlayer.DeviceDriver requestedBackend,
        BassAudioPlayer.DeviceDescriptor requestedDevice,
        out BassAudioSession session,
        int sampleRateConversionQuality = AudioResamplingQuality.Default)
    {
        EnsureEntered();
        if (currentSession != null)
        {
            session = currentSession;
            return false;
        }

        session = new BassAudioSession(requestedBackend, requestedDevice, sampleRateConversionQuality);
        Volatile.Write(ref currentSession, session);
        return true;
    }

    /// <summary>変更不能な音声出力要求を保持するsessionを開始します。</summary>
    internal bool TryBegin(AudioOutputRequest request, out BassAudioSession session)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEntered();
        if (currentSession != null)
        {
            session = currentSession;
            return false;
        }

        AudioDriver backend = request.Backend;
        AudioOutputSelection selection = AudioDriverPolicy.NormalizePersistedSelection(
            new AudioOutputSelection(backend, request.DeviceIdentity, request.DeviceName));
        BassAudioPlayer.DeviceDescriptor device = string.IsNullOrWhiteSpace(selection.DeviceIdentity)
            ? default
            : new BassAudioPlayer.DeviceDescriptor(selection.DeviceName, selection.DeviceIdentity);
        session = new BassAudioSession(
            BassAudioMapping.ToBassDriver(selection.Backend),
            device,
            request.SampleRateConversionQuality,
            request);
        Volatile.Write(ref currentSession, session);
        return true;
    }

    /// <summary>Marks the owned session as usable after initialization succeeds.</summary>
    internal void MarkActive(BassAudioSession session)
    {
        EnsureOwned(session);
        session.State = BassAudioSessionState.Active;
    }

    /// <summary>
    /// Applies a cleanup result without transferring ownership to an exception or caller.
    /// </summary>
    internal void CompleteCleanup(BassAudioSession session)
    {
        EnsureOwned(session);
        if (session.HasNativeOwnership)
        {
            session.State = BassAudioSessionState.CleanupPending;
            return;
        }

        session.State = BassAudioSessionState.Released;
        Volatile.Write(ref currentSession, null);
    }

    /// <summary>
    /// Resolves the current session for cleanup without allowing a stale scoped token
    /// to select a different workflow's graph.
    /// </summary>
    internal bool TryGetForRelease(
        BassAudioSession expectedSession,
        bool allowAnySession,
        out BassAudioSession session)
    {
        EnsureEntered();
        session = currentSession;
        return session != null
            && (allowAnySession || ReferenceEquals(session, expectedSession));
    }

    private void EnsureEntered()
    {
        if (!Monitor.IsEntered(syncRoot))
        {
            throw new SynchronizationLockException("The audio lifecycle gate is not held.");
        }
    }

    private void EnsureOwned(BassAudioSession session)
    {
        EnsureEntered();
        if (!ReferenceEquals(currentSession, session))
        {
            throw new InvalidOperationException("The audio session is not owned by this lifecycle.");
        }
    }

    private sealed class MonitorLease(object gate) : IDisposable
    {
        private object gate = gate;

        public void Dispose()
        {
            object ownedGate = Interlocked.Exchange(ref gate, null);
            if (ownedGate != null)
            {
                Monitor.Exit(ownedGate);
            }
        }
    }
}

/// <summary>Identifies one exclusive native lifecycle transition.</summary>
internal enum BassAudioExclusiveOperation
{
    /// <summary>Loads and validates the native runtime.</summary>
    RuntimeInitialization,

    /// <summary>Creates or negotiates one native audio session.</summary>
    SessionInitialization,

    /// <summary>Releases one native audio session.</summary>
    SessionCleanup,

    /// <summary>Closes admission and deactivates the active native runtime publication.</summary>
    RuntimeShutdown
}

/// <summary>
/// Coordinates shared native calls with exclusive initialization, cleanup, and shutdown.
/// Nested calls on an already admitted thread remain valid while a lifecycle writer drains roots.
/// </summary>
internal sealed class BassAudioOperationGate
{
    private enum ThreadOperationKind
    {
        None,
        Shared,
        Exclusive
    }

    private sealed class ThreadOperationContext
    {
        internal ThreadOperationKind Kind;
        internal int Depth;
    }

    private readonly object syncRoot = new();
    private readonly ThreadLocal<ThreadOperationContext> threadContext =
        new(() => new ThreadOperationContext());
    private int activeRoots;
    private int waitingExclusive;
    private bool exclusiveActive;
    private bool runtimeOpen;
    private bool cleanupQuarantined;
    private bool shutdownRequested;
    private bool shutdownInProgress;
    private readonly AsyncLocal<BassAudioRequestLease> requestContext = new();
    private BassAudioRequestLease activeRequest;

    /// <summary>受理済み要求の処理・後片付けが終わり、論理的な受付を解放したことを通知します。</summary>
    internal event Action RequestReleased;

    /// <summary>副作用より前に一件の要求を受理します。受理済み要求の非同期継続だけが受付を引き継ぎます。</summary>
    internal bool TryEnterRequest(out IDisposable lease)
    {
        lock (syncRoot)
        {
            if (activeRequest != null && ReferenceEquals(activeRequest, requestContext.Value))
            {
                lease = new BassAudioRequestLease(null);
                return true;
            }
            if (activeRequest != null || exclusiveActive || waitingExclusive != 0 || IsAdmissionClosed)
            {
                lease = null;
                return false;
            }
            var request = new BassAudioRequestLease(this);
            activeRequest = request;
            requestContext.Value = request;
            lease = request;
            return true;
        }
    }

    private void ReleaseRequest(BassAudioRequestLease request)
    {
        lock (syncRoot)
        {
            if (ReferenceEquals(activeRequest, request))
            {
                activeRequest = null;
            }
        }
        requestContext.Value = null;
        RequestReleased?.Invoke();
    }

    private sealed class BassAudioRequestLease(BassAudioOperationGate owner) : IDisposable
    {
        private BassAudioOperationGate owner = owner;

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.ReleaseRequest(this);
    }

    /// <summary>Creates a closed gate, or an open test gate when requested.</summary>
    internal BassAudioOperationGate(bool initiallyOpen = false)
    {
        runtimeOpen = initiallyOpen;
    }

    /// <summary>Enters a shared native operation, waiting behind an earlier lifecycle writer.</summary>
    internal bool TryEnterOperation(out BassAudioOperationLease lease)
    {
        ThreadOperationContext context = threadContext.Value;
        if (context.Kind != ThreadOperationKind.None)
        {
            context.Depth++;
            lease = new BassAudioOperationLease(this, isRoot: false, Environment.CurrentManagedThreadId);
            return true;
        }

        lock (syncRoot)
        {
            while (runtimeOpen
                && !IsAdmissionClosed
                && (exclusiveActive || waitingExclusive != 0))
            {
                Monitor.Wait(syncRoot);
            }

            if (!runtimeOpen || IsAdmissionClosed)
            {
                lease = default;
                return false;
            }

            activeRoots++;
            context.Kind = ThreadOperationKind.Shared;
            context.Depth = 1;
            lease = new BassAudioOperationLease(this, isRoot: true, Environment.CurrentManagedThreadId);
            return true;
        }
    }

    /// <summary>
    /// Tries to enter a callback or finalizer without waiting behind lifecycle cleanup.
    /// </summary>
    internal bool TryEnterNonBlockingOperation(out BassAudioOperationLease lease)
    {
        ThreadOperationContext context = threadContext.Value;
        if (context.Kind == ThreadOperationKind.Shared)
        {
            context.Depth++;
            lease = new BassAudioOperationLease(this, isRoot: false, Environment.CurrentManagedThreadId);
            return true;
        }
        if (context.Kind == ThreadOperationKind.Exclusive)
        {
            lease = default;
            return false;
        }

        lock (syncRoot)
        {
            if (!runtimeOpen
                || IsAdmissionClosed
                || exclusiveActive
                || waitingExclusive != 0)
            {
                lease = default;
                return false;
            }

            activeRoots++;
            context.Kind = ThreadOperationKind.Shared;
            context.Depth = 1;
            lease = new BassAudioOperationLease(this, isRoot: true, Environment.CurrentManagedThreadId);
            return true;
        }
    }

    /// <summary>Enters native runtime initialization exclusively.</summary>
    internal BassAudioExclusiveLease EnterRuntimeInitialization()
    {
        BassAudioExclusiveLease lease =
            EnterExclusive(BassAudioExclusiveOperation.RuntimeInitialization, closeAdmission: false);
        lock (syncRoot)
        {
            if (!IsAdmissionClosed)
            {
                return lease;
            }
        }

        lease.Dispose();
        throw new InvalidOperationException(
            "Native runtime initialization was rejected until quarantined cleanup succeeds.");
    }

    /// <summary>Enters audio-session initialization exclusively.</summary>
    internal BassAudioExclusiveLease EnterSessionInitialization()
    {
        BassAudioExclusiveLease lease =
            EnterExclusive(BassAudioExclusiveOperation.SessionInitialization, closeAdmission: false);
        lock (syncRoot)
        {
            if (runtimeOpen && !IsAdmissionClosed)
            {
                return lease;
            }
        }

        lease.Dispose();
        throw new InvalidOperationException(
            "Audio session initialization was rejected because the native runtime is closed.");
    }

    /// <summary>
    /// Tries to promote a top-level caller into exclusive session cleanup.
    /// Promotion from a shared operation is rejected to avoid self-deadlock.
    /// </summary>
    internal bool TryEnterSessionCleanup(out BassAudioExclusiveLease lease)
    {
        if (threadContext.Value.Kind != ThreadOperationKind.None)
        {
            lease = default;
            return false;
        }

        lease = EnterExclusive(BassAudioExclusiveOperation.SessionCleanup, closeAdmission: false);
        lock (syncRoot)
        {
            if (runtimeOpen)
            {
                return true;
            }
        }

        lease.Complete(success: true);
        lease.Dispose();
        lease = default;
        return false;
    }

    /// <summary>Closes new admission, drains roots, and enters runtime shutdown exclusively.</summary>
    internal BassAudioExclusiveLease EnterRuntimeShutdown() =>
        EnterExclusive(BassAudioExclusiveOperation.RuntimeShutdown, closeAdmission: true);

    /// <summary>Waits for a concurrent shutdown attempt to finish.</summary>
    internal void WaitForShutdownCompletion()
    {
        lock (syncRoot)
        {
            while (shutdownInProgress)
            {
                Monitor.Wait(syncRoot);
            }
        }
    }

    /// <summary>Waits until a shutdown caller has closed new root admission.</summary>
    /// <returns><see langword="true"/> when shutdown was observed before the timeout.</returns>
    internal bool WaitForShutdownRequest(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var stopwatch = Stopwatch.StartNew();
        lock (syncRoot)
        {
            while (!shutdownRequested)
            {
                TimeSpan remaining = timeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(syncRoot, remaining))
                {
                    return shutdownRequested;
                }
            }

            return true;
        }
    }

    /// <summary>Gets whether admission is closed by shutdown or retained cleanup.</summary>
    internal bool AdmissionClosed
    {
        get
        {
            lock (syncRoot)
            {
                return IsAdmissionClosed;
            }
        }
    }

    /// <summary>Gets whether new audio requests are quarantined after unconfirmed cleanup.</summary>
    internal bool IsCleanupQuarantined
    {
        get
        {
            lock (syncRoot)
            {
                return cleanupQuarantined;
            }
        }
    }

    /// <summary>Gets whether shutdown has rejected new root operations.</summary>
    internal bool IsShutdownRequested
    {
        get
        {
            lock (syncRoot)
            {
                return shutdownRequested;
            }
        }
    }

    private bool IsAdmissionClosed => shutdownRequested || cleanupQuarantined;

    private BassAudioExclusiveLease EnterExclusive(
        BassAudioExclusiveOperation operation,
        bool closeAdmission)
    {
        if (threadContext.Value.Kind != ThreadOperationKind.None)
        {
            throw new InvalidOperationException(
                "A shared native operation cannot be promoted to an exclusive lifecycle transition.");
        }

        lock (syncRoot)
        {
            if (closeAdmission)
            {
                while (shutdownInProgress)
                {
                    Monitor.Wait(syncRoot);
                }
                shutdownRequested = true;
                shutdownInProgress = true;
                Monitor.PulseAll(syncRoot);
            }

            waitingExclusive++;
            try
            {
                while (exclusiveActive || activeRoots != 0)
                {
                    Monitor.Wait(syncRoot);
                }
                exclusiveActive = true;
            }
            finally
            {
                waitingExclusive--;
            }

            ThreadOperationContext context = threadContext.Value;
            context.Kind = ThreadOperationKind.Exclusive;
            context.Depth = 1;
            return new BassAudioExclusiveLease(
                this,
                operation,
                runtimeOpen,
                Environment.CurrentManagedThreadId);
        }
    }

    internal void ExitOperation(bool isRoot, int ownerThreadId)
    {
        if (ownerThreadId != Environment.CurrentManagedThreadId)
        {
            throw new SynchronizationLockException("Audio operation leases must be disposed on their owning thread.");
        }

        ThreadOperationContext context = threadContext.Value;
        if (context.Depth == 0
            || (isRoot && (context.Kind != ThreadOperationKind.Shared || context.Depth != 1))
            || (!isRoot && context.Depth == 1))
        {
            throw new SynchronizationLockException("Audio operation leases must be disposed in LIFO order.");
        }

        context.Depth--;
        if (!isRoot)
        {
            return;
        }

        context.Kind = ThreadOperationKind.None;
        lock (syncRoot)
        {
            activeRoots--;
            Monitor.PulseAll(syncRoot);
        }
    }

    internal void ExitExclusive(
        BassAudioExclusiveOperation operation,
        bool runtimeWasOpen,
        bool completionSpecified,
        bool success,
        int ownerThreadId)
    {
        if (ownerThreadId != Environment.CurrentManagedThreadId)
        {
            throw new SynchronizationLockException("Audio lifecycle leases must be disposed on their owning thread.");
        }

        ThreadOperationContext context = threadContext.Value;
        if (context.Kind != ThreadOperationKind.Exclusive || context.Depth != 1)
        {
            throw new SynchronizationLockException("Nested audio operations must finish before lifecycle completion.");
        }
        context.Kind = ThreadOperationKind.None;
        context.Depth = 0;

        lock (syncRoot)
        {
            switch (operation)
            {
                case BassAudioExclusiveOperation.RuntimeInitialization:
                    if (completionSpecified && success)
                    {
                        runtimeOpen = true;
                    }
                    else if (!runtimeWasOpen)
                    {
                        runtimeOpen = false;
                    }
                    break;
                case BassAudioExclusiveOperation.SessionInitialization:
                case BassAudioExclusiveOperation.SessionCleanup:
                    if (completionSpecified)
                    {
                        cleanupQuarantined = !success;
                    }
                    else
                    {
                        cleanupQuarantined = true;
                    }
                    break;
                case BassAudioExclusiveOperation.RuntimeShutdown:
                    if (completionSpecified && success)
                    {
                        runtimeOpen = false;
                        cleanupQuarantined = false;
                        shutdownRequested = false;
                    }
                    else
                    {
                        cleanupQuarantined = true;
                        shutdownRequested = true;
                    }
                    shutdownInProgress = false;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }

            exclusiveActive = false;
            Monitor.PulseAll(syncRoot);
        }
    }
}

/// <summary>Releases one shared native-operation admission.</summary>
internal ref struct BassAudioOperationLease
{
    private BassAudioOperationGate gate;
    private readonly bool isRoot;
    private readonly int ownerThreadId;

    internal BassAudioOperationLease(BassAudioOperationGate gate, bool isRoot, int ownerThreadId)
    {
        this.gate = gate;
        this.isRoot = isRoot;
        this.ownerThreadId = ownerThreadId;
    }

    /// <summary>Leaves the native-operation gate once.</summary>
    public void Dispose()
    {
        BassAudioOperationGate ownedGate = gate;
        if (ownedGate == null)
        {
            return;
        }
        gate = null;
        ownedGate.ExitOperation(isRoot, ownerThreadId);
    }
}

/// <summary>Owns one exclusive audio lifecycle transition until its outcome is recorded.</summary>
internal sealed class BassAudioExclusiveLease : IDisposable
{
    private BassAudioOperationGate gate;
    private readonly BassAudioExclusiveOperation operation;
    private readonly bool runtimeWasOpen;
    private readonly int ownerThreadId;
    private bool completionSpecified;
    private bool success;
    private Func<bool> ownershipConsistent;

    /// <summary>session所有者の解放確認から終端を判定し、呼出元のComplete忘れに依存しません。</summary>
    internal void ObserveOwnership(Func<bool> ownershipConsistent)
        => this.ownershipConsistent = ownershipConsistent ?? throw new ArgumentNullException(nameof(ownershipConsistent));

    internal BassAudioExclusiveLease(
        BassAudioOperationGate gate,
        BassAudioExclusiveOperation operation,
        bool runtimeWasOpen,
        int ownerThreadId)
    {
        this.gate = gate;
        this.operation = operation;
        this.runtimeWasOpen = runtimeWasOpen;
        this.ownerThreadId = ownerThreadId;
        completionSpecified = false;
        success = false;
    }

    /// <summary>Records whether the lifecycle transition left native ownership consistent.</summary>
    internal void Complete(bool success)
    {
        completionSpecified = true;
        this.success = success;
    }

    /// <summary>Releases exclusive ownership and publishes the recorded lifecycle outcome.</summary>
    public void Dispose()
    {
        BassAudioOperationGate ownedGate = gate;
        if (ownedGate == null)
        {
            return;
        }
        ownedGate.ExitExclusive(
            operation,
            runtimeWasOpen,
            ownershipConsistent != null || completionSpecified,
            ownershipConsistent?.Invoke() ?? success,
            ownerThreadId);
        gate = null;
    }
}

/// <summary>
/// Exposes only native operations needed to release a recorded audio session.
/// </summary>
internal interface IAudioSessionNativeBoundary
{
    /// <summary>Selects the BASS core device.</summary>
    bool SetCoreDevice(int deviceIndex);

    /// <summary>Frees the selected BASS core device.</summary>
    bool FreeCore();

    /// <summary>Gets the last BASS core error.</summary>
    Errors GetCoreError();

    /// <summary>Selects the BASSWASAPI device.</summary>
    bool SetWasapiDevice(int deviceIndex);

    /// <summary>Stops the selected BASSWASAPI device.</summary>
    bool StopWasapi(bool reset);

    /// <summary>Frees the selected BASSWASAPI device.</summary>
    bool FreeWasapi();

    /// <summary>Gets the last BASSWASAPI error exposed through BASS.</summary>
    Errors GetWasapiError();

    /// <summary>Selects the BASSASIO device.</summary>
    bool SetAsioDevice(int deviceIndex);

    /// <summary>Stops the selected BASSASIO device.</summary>
    bool StopAsio();

    /// <summary>Frees the selected BASSASIO device.</summary>
    bool FreeAsio();

    /// <summary>Gets the last BASSASIO error from the ASIO API.</summary>
    Errors GetAsioError();

    /// <summary>Frees a stream on the selected BASS core device.</summary>
    bool FreeStream(int handle);

    /// <summary>Gets the last BASS stream error.</summary>
    Errors GetStreamError();
}

/// <summary>Binds session cleanup to the ManagedBass API.</summary>
internal sealed class BassAudioSessionNativeBoundary : IAudioSessionNativeBoundary
{
    private Errors? coreErrorOverride;
    private Errors? wasapiErrorOverride;
    private Errors? asioErrorOverride;

    /// <inheritdoc />
    public bool SetCoreDevice(int deviceIndex)
    {
        coreErrorOverride = null;
        try
        {
            Bass.CurrentDevice = deviceIndex;
            return true;
        }
        catch (BassException exception)
        {
            coreErrorOverride = exception.ErrorCode;
            return false;
        }
    }

    /// <inheritdoc />
    public bool FreeCore()
    {
        coreErrorOverride = null;
        try
        {
            return Bass.Free();
        }
        catch (BassException exception)
        {
            coreErrorOverride = exception.ErrorCode;
            return false;
        }
    }

    /// <inheritdoc />
    public Errors GetCoreError() => coreErrorOverride ?? Bass.LastError;

    /// <inheritdoc />
    public bool SetWasapiDevice(int deviceIndex)
    {
        wasapiErrorOverride = null;
        try
        {
            BassWasapi.CurrentDevice = deviceIndex;
            return true;
        }
        catch (BassException exception)
        {
            wasapiErrorOverride = exception.ErrorCode;
            return false;
        }
    }

    /// <inheritdoc />
    public bool StopWasapi(bool reset)
    {
        wasapiErrorOverride = null;
        try
        {
            return BassWasapi.Stop(reset);
        }
        catch (BassException exception)
        {
            wasapiErrorOverride = exception.ErrorCode;
            return false;
        }
    }

    /// <inheritdoc />
    public bool FreeWasapi()
    {
        wasapiErrorOverride = null;
        try
        {
            return BassWasapi.Free();
        }
        catch (BassException exception)
        {
            wasapiErrorOverride = exception.ErrorCode;
            return false;
        }
    }

    /// <inheritdoc />
    public Errors GetWasapiError() => wasapiErrorOverride ?? Bass.LastError;

    /// <inheritdoc />
    public bool SetAsioDevice(int deviceIndex)
    {
        asioErrorOverride = null;
        try
        {
            BassAsio.CurrentDevice = deviceIndex;
            return true;
        }
        catch (BassException exception)
        {
            asioErrorOverride = exception.ErrorCode;
            return false;
        }
    }

    /// <inheritdoc />
    public bool StopAsio()
    {
        asioErrorOverride = null;
        try
        {
            return BassAsio.Stop();
        }
        catch (BassException exception)
        {
            asioErrorOverride = exception.ErrorCode;
            return false;
        }
    }

    /// <inheritdoc />
    public bool FreeAsio()
    {
        asioErrorOverride = null;
        try
        {
            return BassAsio.Free();
        }
        catch (BassException exception)
        {
            asioErrorOverride = exception.ErrorCode;
            return false;
        }
    }

    /// <inheritdoc />
    public Errors GetAsioError() => asioErrorOverride ?? BassAsio.LastError;

    /// <inheritdoc />
    public bool FreeStream(int handle)
    {
        coreErrorOverride = null;
        try
        {
            return Bass.StreamFree(handle);
        }
        catch (BassException exception)
        {
            coreErrorOverride = exception.ErrorCode;
            return false;
        }
    }

    /// <inheritdoc />
    public Errors GetStreamError() => coreErrorOverride ?? Bass.LastError;
}

/// <summary>
/// Releases only resources whose successful acquisition is recorded by a session.
/// Cleanup errors are logged and retained in the session instead of being thrown.
/// </summary>
internal static class BassAudioSessionCleanup
{
    private enum DeviceSelectionResult
    {
        Selected,
        AlreadyReleased,
        Failed
    }

    /// <summary>
    /// Tries to release all recorded native ownership and returns whether release was confirmed.
    /// </summary>
    internal static bool Release(
        BassAudioSession session,
        IAudioSessionNativeBoundary native,
        Exception primaryException = null)
    {
        ArgumentNullException.ThrowIfNull(native);
        if (session == null || session.IsReleased)
        {
            return true;
        }

        var failures = new List<string>();
        var diagnostics = new List<BassAudioCleanupDiagnostic>();
        try
        {
            bool backendReleased = ReleaseBackend(session, native, failures, diagnostics);
            if (backendReleased)
            {
                ReleaseCore(session, native, failures, diagnostics);
            }
        }
        catch (Exception exception)
        {
            failures.Add("cleanup orchestration threw: " + exception.Message);
            diagnostics.Add(new BassAudioCleanupDiagnostic(
                "audio session cleanup",
                "BassAudioSession",
                null,
                exception.GetType().Name));
        }
        finally
        {
            session.State = session.HasNativeOwnership
                ? BassAudioSessionState.CleanupPending
                : BassAudioSessionState.Released;

            session.CleanupDiagnostics = diagnostics.ToArray();
            if (failures.Count != 0)
            {
                TryLogCleanupFailure(session, failures, primaryException);
            }
        }

        return !session.HasNativeOwnership;
    }

    private static bool ReleaseBackend(
        BassAudioSession session,
        IAudioSessionNativeBoundary native,
        List<string> failures,
        List<BassAudioCleanupDiagnostic> diagnostics)
    {
        if (session.AsioInitialized)
        {
            DeviceSelectionResult selection = TrySelect(
                () => native.SetAsioDevice(session.AsioDeviceIndex),
                "BASS_ASIO_SetDevice(" + session.AsioDeviceIndex + ")",
                native.GetAsioError,
                "BASSASIO",
                failures,
                diagnostics);
            if (selection == DeviceSelectionResult.Failed)
            {
                return false;
            }
            if (selection == DeviceSelectionResult.AlreadyReleased)
            {
                session.AsioInitialized = false;
                session.IsStarted = false;
            }
            else
            {
                if (session.IsStarted
                    && !TryReleaseCall(
                        native.StopAsio,
                        "BASS_ASIO_Stop",
                        native.GetAsioError,
                        "BASSASIO",
                        failures,
                        diagnostics))
                {
                    return false;
                }
                session.IsStarted = false;

                if (!TryReleaseCall(
                    native.FreeAsio,
                    "BASS_ASIO_Free",
                    native.GetAsioError,
                    "BASSASIO",
                    failures,
                    diagnostics))
                {
                    return false;
                }
                session.AsioInitialized = false;
            }
        }

        if (session.WasapiInitialized)
        {
            DeviceSelectionResult selection = TrySelect(
                () => native.SetWasapiDevice(session.WasapiDeviceIndex),
                "BASS_WASAPI_SetDevice(" + session.WasapiDeviceIndex + ")",
                native.GetWasapiError,
                "BASSWASAPI",
                failures,
                diagnostics);
            if (selection == DeviceSelectionResult.Failed)
            {
                return false;
            }
            if (selection == DeviceSelectionResult.AlreadyReleased)
            {
                session.WasapiInitialized = false;
                session.IsStarted = false;
            }
            else
            {
                if (session.IsStarted
                    && !TryReleaseCall(
                        () => native.StopWasapi(reset: true),
                        "BASS_WASAPI_Stop",
                        native.GetWasapiError,
                        "BASSWASAPI",
                        failures,
                        diagnostics))
                {
                    return false;
                }
                session.IsStarted = false;

                if (!TryReleaseCall(
                    native.FreeWasapi,
                    "BASS_WASAPI_Free",
                    native.GetWasapiError,
                    "BASSWASAPI",
                    failures,
                    diagnostics))
                {
                    return false;
                }
                session.WasapiInitialized = false;
            }
        }

        return true;
    }

    private static void ReleaseCore(
        BassAudioSession session,
        IAudioSessionNativeBoundary native,
        List<string> failures,
        List<BassAudioCleanupDiagnostic> diagnostics)
    {
        if (!session.CoreInitialized
            && session.MixerHandle == 0
            && session.OutputHandle == 0
            && session.GetOwnedStreams().Count == 0)
        {
            return;
        }

        DeviceSelectionResult selection = TrySelect(
            () => native.SetCoreDevice(session.CoreDeviceIndex),
            "BASS_SetDevice(" + session.CoreDeviceIndex + ")",
            native.GetCoreError,
            "BASS",
            failures,
            diagnostics);
        if (selection == DeviceSelectionResult.Failed)
        {
            return;
        }
        if (selection == DeviceSelectionResult.AlreadyReleased)
        {
            ConfirmCoreAlreadyReleased(session);
            return;
        }

        var handles = new HashSet<int>();
        foreach (BassAudioOwnedStream stream in session.GetOwnedStreams())
        {
            AddHandle(handles, stream.Handle);
        }

        AddHandle(handles, session.OutputHandle);
        AddHandle(handles, session.MixerHandle);
        foreach (int handle in handles)
        {
            if (TryReleaseCall(
                () => native.FreeStream(handle),
                "BASS_StreamFree(" + handle + ")",
                native.GetStreamError,
                "BASS",
                failures,
                diagnostics))
            {
                session.ConfirmStreamReleased(handle);
                ClearHandle(session, handle);
            }
        }

        if (session.CoreInitialized
            && session.MixerHandle == 0
            && session.OutputHandle == 0
            && session.GetOwnedStreams().Count == 0
            && TryReleaseCall(
                native.FreeCore,
                "BASS_Free",
                native.GetCoreError,
                "BASS",
                failures,
                diagnostics))
        {
            session.CoreInitialized = false;
        }
    }

    private static DeviceSelectionResult TrySelect(
        Func<bool> select,
        string operation,
        Func<Errors> getError,
        string nativeErrorSource,
        List<string> failures,
        List<BassAudioCleanupDiagnostic> diagnostics)
    {
        try
        {
            if (select())
            {
                return DeviceSelectionResult.Selected;
            }

            Errors error = getError();
            if (error == Errors.Init)
            {
                TryLogAlreadyReleased(operation);
                return DeviceSelectionResult.AlreadyReleased;
            }

            failures.Add(operation + " failed: " + BassNativeErrorFormatter.Format(error));
            diagnostics.Add(new BassAudioCleanupDiagnostic(operation, nativeErrorSource, error, string.Empty));
        }
        catch (Exception exception)
        {
            failures.Add(operation + " threw: " + exception.Message);
            diagnostics.Add(new BassAudioCleanupDiagnostic(
                operation,
                nativeErrorSource,
                null,
                exception.GetType().Name));
        }

        return DeviceSelectionResult.Failed;
    }

    private static void ConfirmCoreAlreadyReleased(BassAudioSession session)
    {
        foreach (BassAudioOwnedStream stream in session.GetOwnedStreams())
        {
            session.ConfirmStreamReleased(stream.Handle);
        }
        session.ConfirmStreamReleased(session.OutputHandle);
        session.ConfirmStreamReleased(session.MixerHandle);
        session.CoreInitialized = false;
        session.IsStarted = false;
    }

    private static bool TryReleaseCall(
        Func<bool> release,
        string operation,
        Func<Errors> getError,
        string nativeErrorSource,
        List<string> failures,
        List<BassAudioCleanupDiagnostic> diagnostics)
    {
        try
        {
            if (release())
            {
                return true;
            }

            Errors error = getError();
            if (error == Errors.Init)
            {
                TryLogAlreadyReleased(operation);
                return true;
            }

            failures.Add(operation + " failed: " + BassNativeErrorFormatter.Format(error));
            diagnostics.Add(new BassAudioCleanupDiagnostic(operation, nativeErrorSource, error, string.Empty));
        }
        catch (Exception exception)
        {
            failures.Add(operation + " threw: " + exception.Message);
            diagnostics.Add(new BassAudioCleanupDiagnostic(
                operation,
                nativeErrorSource,
                null,
                exception.GetType().Name));
        }

        return false;
    }

    private static void TryLogAlreadyReleased(string operation)
    {
        try
        {
            NLogWrapper.GetLogger("AudioSession").Debug(
                operation + " returned BASS_ERROR_INIT and was treated as already released.");
        }
        catch
        {
            // Idempotent cleanup diagnostics must not change the release result.
        }
    }

    private static void AddHandle(HashSet<int> handles, int handle)
    {
        if (handle != 0)
        {
            handles.Add(handle);
        }
    }

    private static void ClearHandle(BassAudioSession session, int handle)
    {
        session.ConfirmStreamReleased(handle);
        if (session.OutputHandle == 0
            && !session.AsioInitialized
            && !session.WasapiInitialized)
        {
            session.IsStarted = false;
        }
    }

    private static void TryLogCleanupFailure(
        BassAudioSession session,
        IReadOnlyCollection<string> failures,
        Exception primaryException)
    {
        try
        {
            NLogWrapper.GetLogger("AudioSession").Warn(
                "Audio cleanup retained native ownership. requestedBackend=" + session.RequestedBackend
                + " actualBackend=" + session.ActualBackend
                + " coreDevice=" + session.CoreDeviceIndex
                + " wasapiDevice=" + session.WasapiDeviceIndex
                + " asioDevice=" + session.AsioDeviceIndex
                + " failures=" + string.Join("; ", failures)
                + (primaryException == null ? string.Empty : " primary=" + primaryException.Message));
        }
        catch
        {
            // Diagnostics must never replace the primary initialization or cleanup contract.
        }
    }
}

/// <summary>native session cleanupの失敗段階を任意例外メッセージから独立して保持します。</summary>
internal sealed record BassAudioCleanupDiagnostic(
    string Stage,
    string NativeErrorSource,
    Errors? NativeErrorCode,
    string ExceptionType);

/// <summary>解放処理がnative errorを上書きする前の初期化失敗情報を保持します。</summary>
internal sealed class AudioInitializationException : Exception
{
    /// <summary>Creates an empty initialization failure for exception infrastructure.</summary>
    internal AudioInitializationException()
    {
    }

    /// <summary>Creates an initialization failure with a diagnostic message.</summary>
    internal AudioInitializationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an initialization failure with a message and underlying cause.</summary>
    internal AudioInitializationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>初期化要求、失敗段階、native error、先行試行を保持する例外を作成します。</summary>
    /// <param name="attempts">初期化時に記録したnative交渉の判定です。</param>
    internal AudioInitializationException(
        BassAudioPlayer.DeviceDriver requestedBackend,
        BassAudioPlayer.DeviceDriver actualBackend,
        string stage,
        BassAudioPlayer.DeviceDescriptor requestedDevice,
        BassAudioPlayer.DeviceDescriptor actualDevice,
        string nativeErrorSource,
        Errors? nativeErrorCode,
        string message,
        Exception innerException = null,
        IReadOnlyList<BassAudioBackendAttempt> attempts = null)
        : base(message, innerException)
    {
        RequestedBackend = requestedBackend;
        ActualBackend = actualBackend;
        Stage = stage;
        RequestedDevice = requestedDevice;
        ActualDevice = actualDevice;
        NativeErrorSource = nativeErrorSource;
        NativeErrorCode = nativeErrorCode;
        Attempts = Array.AsReadOnly(attempts?.ToArray() ?? Array.Empty<BassAudioBackendAttempt>());
    }

    /// <summary>Gets the backend selected by the caller.</summary>
    internal BassAudioPlayer.DeviceDriver RequestedBackend { get; }

    /// <summary>Gets the backend whose initialization failed.</summary>
    internal BassAudioPlayer.DeviceDriver ActualBackend { get; }

    /// <summary>Gets the initialization stage that failed.</summary>
    internal string Stage { get; }

    /// <summary>Gets the endpoint selected by the caller.</summary>
    internal BassAudioPlayer.DeviceDescriptor RequestedDevice { get; }

    /// <summary>Gets the endpoint selected during initialization.</summary>
    internal BassAudioPlayer.DeviceDescriptor ActualDevice { get; }

    /// <summary>Gets the native API that supplied the error code.</summary>
    internal string NativeErrorSource { get; }

    /// <summary>Gets the captured native error code.</summary>
    internal Errors? NativeErrorCode { get; }

    /// <summary>初期化失敗までに保持したネイティブ判定を順に取得します。</summary>
    internal IReadOnlyList<BassAudioBackendAttempt> Attempts { get; }

    /// <summary>この失敗を主エラーとして保ち、先行backendの判定を前置きします。</summary>
    internal AudioInitializationException WithEarlierAttempts(
        IReadOnlyList<BassAudioBackendAttempt> earlierAttempts)
    {
        ArgumentNullException.ThrowIfNull(earlierAttempts);
        if (earlierAttempts.Count == 0)
        {
            return this;
        }

        var attempts = new List<BassAudioBackendAttempt>(earlierAttempts.Count + Attempts.Count);
        attempts.AddRange(earlierAttempts);
        attempts.AddRange(Attempts);
        return new AudioInitializationException(
            RequestedBackend,
            ActualBackend,
            Stage,
            RequestedDevice,
            ActualDevice,
            NativeErrorSource,
            NativeErrorCode,
            Message,
            InnerException,
            attempts);
    }
}
