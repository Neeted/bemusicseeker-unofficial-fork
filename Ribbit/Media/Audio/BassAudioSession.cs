using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Ribbit.Logging;
using Ribbit.Media;
using Un4seen.Bass;
using Un4seen.BassAsio;
using Un4seen.BassWasapi;

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

/// <summary>
/// Records native ownership for one BASS audio graph from the start of initialization
/// until every acquired layer has been released.
/// </summary>
internal sealed class BassAudioSession
{
    private readonly object playerStreamSync = new();
    private readonly List<BassAudioOwnedStream> playerStreams = [];
    private int callbackOutputHandle;

    /// <summary>
    /// Creates an initializing session for the requested backend and endpoint.
    /// </summary>
    internal BassAudioSession(
        BassAudioPlayer.DeviceDriver requestedBackend,
        BassAudioPlayer.DeviceDescriptor requestedDevice = default)
    {
        RequestedBackend = requestedBackend;
        RequestedDevice = requestedDevice;
        ActualBackend = BassAudioPlayer.DeviceDriver.INVALID;
        CoreDeviceIndex = -1;
        WasapiDeviceIndex = -1;
        AsioDeviceIndex = -1;
        State = BassAudioSessionState.Initializing;
    }

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
    /// Gets or sets the BASS_FX volume effect attached to the session's decode mixer.
    /// The effect is owned by the mixer and is released with that mixer.
    /// </summary>
    internal int VolumeEffectHandle { get; set; }

    /// <summary>
    /// Gets the stream currently published to callback-driven output without taking the
    /// lifecycle lock.
    /// </summary>
    internal int CallbackOutputHandle => Volatile.Read(ref callbackOutputHandle);

    /// <summary>Gets additional stream handles created after initialization.</summary>
    internal IList<int> AdditionalStreamHandles { get; } = new List<int>();

    /// <summary>Gets or sets whether the backend output was started.</summary>
    internal bool IsStarted { get; set; }

    /// <summary>Gets or sets the values accepted by the initialized native backend.</summary>
    internal BassAudioBackendResult NegotiationResult { get; set; }

    /// <summary>Gets the current ownership phase.</summary>
    internal BassAudioSessionState State { get; set; }

    /// <summary>Gets whether native resources still require cleanup.</summary>
    internal bool HasNativeOwnership =>
        CoreInitialized
        || WasapiInitialized
        || AsioInitialized
        || MixerHandle != 0
        || OutputHandle != 0
        || AdditionalStreamHandles.Count != 0
        || HasPlayerStreams
        || IsStarted;

    /// <summary>Gets whether cleanup has been fully confirmed.</summary>
    internal bool IsReleased =>
        State == BassAudioSessionState.Released && !HasNativeOwnership;

    /// <summary>Records the stream currently supplying the backend output.</summary>
    internal void TrackOutputHandle(int handle)
    {
        OutputHandle = handle;
        PublishCallbackOutputHandle(handle);
        RemoveAdditionalHandle(handle);
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
            VolumeEffectHandle = 0;
            MixerHandle = 0;
        }
        if (OutputHandle == handle)
        {
            OutputHandle = 0;
        }
        Interlocked.CompareExchange(ref callbackOutputHandle, 0, handle);
        RemoveAdditionalHandle(handle);
    }

    /// <summary>
    /// Retains a source stream and its managed callback owner until native release is confirmed.
    /// </summary>
    internal void TrackPlayerStream(int handle, object owner, Action<int> releaseConfirmed)
    {
        if (handle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(handle));
        }
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(releaseConfirmed);

        lock (playerStreamSync)
        {
            if (playerStreams.Any(stream => stream.Handle == handle))
            {
                throw new InvalidOperationException("The BASS source stream is already owned by this session.");
            }

            playerStreams.Add(new BassAudioOwnedStream(handle, owner, releaseConfirmed));
        }
    }

    /// <summary>Gets a stable snapshot of source streams retained by this session.</summary>
    internal IReadOnlyList<BassAudioOwnedStream> GetPlayerStreams()
    {
        lock (playerStreamSync)
        {
            return [.. playerStreams];
        }
    }

    /// <summary>
    /// Forgets a source stream and notifies its owner only after native release is confirmed.
    /// </summary>
    internal void ConfirmPlayerStreamReleased(int handle)
    {
        BassAudioOwnedStream[] released;
        lock (playerStreamSync)
        {
            released = [.. playerStreams.Where(stream => stream.Handle == handle)];
            playerStreams.RemoveAll(stream => stream.Handle == handle);
        }

        foreach (BassAudioOwnedStream stream in released)
        {
            stream.NotifyReleased();
        }
    }

    private bool HasPlayerStreams
    {
        get
        {
            lock (playerStreamSync)
            {
                return playerStreams.Count != 0;
            }
        }
    }

    private void RemoveAdditionalHandle(int handle)
    {
        for (int index = AdditionalStreamHandles.Count - 1; index >= 0; index--)
        {
            if (AdditionalStreamHandles[index] == handle)
            {
                AdditionalStreamHandles.RemoveAt(index);
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
    internal BassAudioOwnedStream(int handle, object owner, Action<int> releaseConfirmed)
    {
        Handle = handle;
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.releaseConfirmed = releaseConfirmed ?? throw new ArgumentNullException(nameof(releaseConfirmed));
    }

    /// <summary>Gets the native stream handle.</summary>
    internal int Handle { get; }

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

    /// <summary>
    /// Starts ownership for a new initialization when no session is active or quarantined.
    /// </summary>
    internal bool TryBegin(
        BassAudioPlayer.DeviceDriver requestedBackend,
        BassAudioPlayer.DeviceDescriptor requestedDevice,
        out BassAudioSession session)
    {
        EnsureEntered();
        if (currentSession != null)
        {
            session = currentSession;
            return false;
        }

        session = new BassAudioSession(requestedBackend, requestedDevice);
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

    /// <summary>Closes admission and unloads the native runtime.</summary>
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
internal ref struct BassAudioExclusiveLease
{
    private BassAudioOperationGate gate;
    private readonly BassAudioExclusiveOperation operation;
    private readonly bool runtimeWasOpen;
    private readonly int ownerThreadId;
    private bool completionSpecified;
    private bool success;

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
        gate = null;
        ownedGate.ExitExclusive(
            operation,
            runtimeWasOpen,
            completionSpecified,
            success,
            ownerThreadId);
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
    BASSError GetCoreError();

    /// <summary>Selects the BASSWASAPI device.</summary>
    bool SetWasapiDevice(int deviceIndex);

    /// <summary>Stops the selected BASSWASAPI device.</summary>
    bool StopWasapi(bool reset);

    /// <summary>Frees the selected BASSWASAPI device.</summary>
    bool FreeWasapi();

    /// <summary>Gets the last BASSWASAPI error exposed through BASS.</summary>
    BASSError GetWasapiError();

    /// <summary>Selects the BASSASIO device.</summary>
    bool SetAsioDevice(int deviceIndex);

    /// <summary>Stops the selected BASSASIO device.</summary>
    bool StopAsio();

    /// <summary>Frees the selected BASSASIO device.</summary>
    bool FreeAsio();

    /// <summary>Gets the last BASSASIO error from the ASIO API.</summary>
    BASSError GetAsioError();

    /// <summary>Frees a stream on the selected BASS core device.</summary>
    bool FreeStream(int handle);

    /// <summary>Gets the last BASS stream error.</summary>
    BASSError GetStreamError();
}

/// <summary>Binds session cleanup to the currently bundled Bass.Net API.</summary>
internal sealed class BassAudioSessionNativeBoundary : IAudioSessionNativeBoundary
{
    /// <inheritdoc />
    public bool SetCoreDevice(int deviceIndex) => Bass.BASS_SetDevice(deviceIndex);

    /// <inheritdoc />
    public bool FreeCore() => Bass.BASS_Free();

    /// <inheritdoc />
    public BASSError GetCoreError() => Bass.BASS_ErrorGetCode();

    /// <inheritdoc />
    public bool SetWasapiDevice(int deviceIndex) => BassWasapi.BASS_WASAPI_SetDevice(deviceIndex);

    /// <inheritdoc />
    public bool StopWasapi(bool reset) => BassWasapi.BASS_WASAPI_Stop(reset);

    /// <inheritdoc />
    public bool FreeWasapi() => BassWasapi.BASS_WASAPI_Free();

    /// <inheritdoc />
    public BASSError GetWasapiError() => Bass.BASS_ErrorGetCode();

    /// <inheritdoc />
    public bool SetAsioDevice(int deviceIndex) => BassAsio.BASS_ASIO_SetDevice(deviceIndex);

    /// <inheritdoc />
    public bool StopAsio() => BassAsio.BASS_ASIO_Stop();

    /// <inheritdoc />
    public bool FreeAsio() => BassAsio.BASS_ASIO_Free();

    /// <inheritdoc />
    public BASSError GetAsioError() => BassAsio.BASS_ASIO_ErrorGetCode();

    /// <inheritdoc />
    public bool FreeStream(int handle) => Bass.BASS_StreamFree(handle);

    /// <inheritdoc />
    public BASSError GetStreamError() => Bass.BASS_ErrorGetCode();
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
        try
        {
            bool backendReleased = ReleaseBackend(session, native, failures);
            if (backendReleased)
            {
                ReleaseCore(session, native, failures);
            }
        }
        catch (Exception exception)
        {
            failures.Add("cleanup orchestration threw: " + exception.Message);
        }
        finally
        {
            session.State = session.HasNativeOwnership
                ? BassAudioSessionState.CleanupPending
                : BassAudioSessionState.Released;

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
        List<string> failures)
    {
        if (session.AsioInitialized)
        {
            DeviceSelectionResult selection = TrySelect(
                () => native.SetAsioDevice(session.AsioDeviceIndex),
                "BASS_ASIO_SetDevice(" + session.AsioDeviceIndex + ")",
                native.GetAsioError,
                failures);
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
                    && !TryReleaseCall(native.StopAsio, "BASS_ASIO_Stop", native.GetAsioError, failures))
                {
                    return false;
                }
                session.IsStarted = false;

                if (!TryReleaseCall(native.FreeAsio, "BASS_ASIO_Free", native.GetAsioError, failures))
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
                failures);
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
                        failures))
                {
                    return false;
                }
                session.IsStarted = false;

                if (!TryReleaseCall(native.FreeWasapi, "BASS_WASAPI_Free", native.GetWasapiError, failures))
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
        List<string> failures)
    {
        if (!session.CoreInitialized
            && session.MixerHandle == 0
            && session.OutputHandle == 0
            && session.AdditionalStreamHandles.Count == 0
            && session.GetPlayerStreams().Count == 0)
        {
            return;
        }

        DeviceSelectionResult selection = TrySelect(
            () => native.SetCoreDevice(session.CoreDeviceIndex),
            "BASS_SetDevice(" + session.CoreDeviceIndex + ")",
            native.GetCoreError,
            failures);
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
        foreach (BassAudioOwnedStream stream in session.GetPlayerStreams())
        {
            AddHandle(handles, stream.Handle);
        }

        AddHandle(handles, session.OutputHandle);
        AddHandle(handles, session.MixerHandle);
        foreach (int handle in session.AdditionalStreamHandles)
        {
            AddHandle(handles, handle);
        }

        foreach (int handle in handles)
        {
            if (TryReleaseCall(
                () => native.FreeStream(handle),
                "BASS_StreamFree(" + handle + ")",
                native.GetStreamError,
                failures))
            {
                session.ConfirmPlayerStreamReleased(handle);
                ClearHandle(session, handle);
            }
        }

        if (session.CoreInitialized
            && session.MixerHandle == 0
            && session.OutputHandle == 0
            && session.AdditionalStreamHandles.Count == 0
            && session.GetPlayerStreams().Count == 0
            && TryReleaseCall(native.FreeCore, "BASS_Free", native.GetCoreError, failures))
        {
            session.CoreInitialized = false;
        }
    }

    private static DeviceSelectionResult TrySelect(
        Func<bool> select,
        string operation,
        Func<BASSError> getError,
        List<string> failures)
    {
        try
        {
            if (select())
            {
                return DeviceSelectionResult.Selected;
            }

            BASSError error = getError();
            if (error == BASSError.BASS_ERROR_INIT)
            {
                TryLogAlreadyReleased(operation);
                return DeviceSelectionResult.AlreadyReleased;
            }

            failures.Add(operation + " failed: " + error);
        }
        catch (Exception exception)
        {
            failures.Add(operation + " threw: " + exception.Message);
        }

        return DeviceSelectionResult.Failed;
    }

    private static void ConfirmCoreAlreadyReleased(BassAudioSession session)
    {
        foreach (BassAudioOwnedStream stream in session.GetPlayerStreams())
        {
            session.ConfirmPlayerStreamReleased(stream.Handle);
        }
        foreach (int handle in session.AdditionalStreamHandles.ToArray())
        {
            session.ConfirmStreamReleased(handle);
        }
        session.ConfirmStreamReleased(session.OutputHandle);
        session.ConfirmStreamReleased(session.MixerHandle);
        session.CoreInitialized = false;
        session.IsStarted = false;
    }

    private static bool TryReleaseCall(
        Func<bool> release,
        string operation,
        Func<BASSError> getError,
        List<string> failures)
    {
        try
        {
            if (release())
            {
                return true;
            }

            BASSError error = getError();
            if (error == BASSError.BASS_ERROR_INIT)
            {
                TryLogAlreadyReleased(operation);
                return true;
            }

            failures.Add(operation + " failed: " + error);
        }
        catch (Exception exception)
        {
            failures.Add(operation + " threw: " + exception.Message);
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

/// <summary>
/// Describes a native initialization failure before cleanup can overwrite the error state.
/// </summary>
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

    /// <summary>Creates a contextual native audio initialization failure.</summary>
    internal AudioInitializationException(
        BassAudioPlayer.DeviceDriver requestedBackend,
        BassAudioPlayer.DeviceDriver actualBackend,
        string stage,
        BassAudioPlayer.DeviceDescriptor requestedDevice,
        BassAudioPlayer.DeviceDescriptor actualDevice,
        string nativeErrorSource,
        BASSError? nativeErrorCode,
        string message,
        Exception innerException = null)
        : base(message, innerException)
    {
        RequestedBackend = requestedBackend;
        ActualBackend = actualBackend;
        Stage = stage;
        RequestedDevice = requestedDevice;
        ActualDevice = actualDevice;
        NativeErrorSource = nativeErrorSource;
        NativeErrorCode = nativeErrorCode;
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
    internal BASSError? NativeErrorCode { get; }
}
