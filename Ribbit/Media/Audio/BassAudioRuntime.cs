using System;
using System.Linq;
using ManagedBass;
using Ribbit.Logging;

namespace Ribbit.Media.Audio;

public static class BassAudioRuntime
{
    private enum InitializationStage
    {
        NativeLoad,
        ResolverInstallation,
        LegacyWrapperRegistration,
        VersionValidation,
        Completed
    }

    private static readonly object SessionOwnerSync = new();
    private static readonly BassAudioOperationGate RuntimeGate = new();

    private static bool _isInitialized;

    private static Func<bool> _releaseAudioSessionForShutdown;

    /// <summary>
    /// Loads and validates the bundled native runtime. Repeated calls are safe.
    /// </summary>
    public static void Initialize()
    {
        InitializeRuntime(includeWrapperRegistration: true);
    }

    /// <summary>
    /// Loads and validates the bundled native runtime for registration-free characterization.
    /// </summary>
    /// <remarks>
    /// This internal seam is limited to Unit 0 tests so native/session behavior can be
    /// characterized without entering the legacy wrapper-registration stage. It reuses the
    /// production runtime gate, native owner, validation, and shutdown path.
    /// </remarks>
    internal static void InitializeWithoutWrapperRegistrationForCharacterization()
    {
        InitializeRuntime(includeWrapperRegistration: false);
    }

    private static void InitializeRuntime(bool includeWrapperRegistration)
    {
        using BassAudioExclusiveLease lifecycle = RuntimeGate.EnterRuntimeInitialization();
        if (_isInitialized)
        {
            if (RuntimeGate.AdmissionClosed)
            {
                throw new InvalidOperationException(
                    "The native audio runtime remains closed after an incomplete shutdown or cleanup.");
            }

            lifecycle.Complete(success: true);
            return;
        }

        InitializationStage stage = InitializationStage.NativeLoad;
        bool wrapperRegistered = false;
        try
        {
            InitializeRuntimeCore(
                () =>
                {
                    stage = InitializationStage.NativeLoad;
                    BassNativeRuntime.Load();
                    stage = InitializationStage.ResolverInstallation;
                    ManagedBassNativeLibraryResolver.EnsureInstalled();
                    BassNativeRuntime.PinManagedBassGeneration();
                },
                () =>
                {
                    if (!includeWrapperRegistration)
                    {
                        return;
                    }

                    stage = InitializationStage.LegacyWrapperRegistration;
                    RegisterLegacyBassNetWrapper();
                    wrapperRegistered = true;
                },
                () =>
                {
                    stage = InitializationStage.VersionValidation;
                    BassNativeRuntime.ValidateSupportedVersions();
                });
            stage = InitializationStage.Completed;
            _isInitialized = true;
            lifecycle.Complete(success: true);
        }
        catch (Exception exception)
        {
            Errors? nativeError = CaptureInitializationNativeError(stage, wrapperRegistered);
            TryLogInitializationFailure(stage, nativeError, exception);
            TryFreeAfterInitializationFailure(stage);
            _isInitialized = false;
            lifecycle.Complete(success: false);
            throw;
        }
    }

    /// <summary>
    /// Runs the native bootstrap stages in the order required by the BASS.NET wrapper.
    /// The callbacks keep registration arguments outside the orchestration seam.
    /// </summary>
    internal static void InitializeRuntimeCore(
        Action loadNative,
        Action registerWrapper,
        Action validateVersions)
    {
        ArgumentNullException.ThrowIfNull(loadNative);
        ArgumentNullException.ThrowIfNull(registerWrapper);
        ArgumentNullException.ThrowIfNull(validateVersions);
        loadNative();
        registerWrapper();
        validateVersions();
    }

    private static void RegisterLegacyBassNetWrapper()
    {
        ((Action<Action<string, string>, string, string>)delegate (Action<string, string> f, string i, string j)
        {
            f(new string([.. (from n in "d7cbxdba4x22b9xd7daxdf35xd7cexdf3fxd7caxdc65xd7d8xdc43xd7d2xdc78x2689xd7dcxdbbcxd7d8xdca8xd7d6xdbddxd7dcxdddexd7d0xded6xd7d9xdf53xd7d0".Split('x')
                                  select Convert.ToInt32(n, 16)).Zip(i.ToCharArray(), (w, v) => v - w).Select(Convert.ToChar)]), new string([.. (from n in "d80axdf58xd80axdc26xd80bxddc5xd80axdf2fxd80axdf79xd80bxdf2dxd80axdfbcxd80bxddcc".Split('x')
                                  select Convert.ToInt32(n, 16)).Zip(j.ToCharArray(), (w, v) => v - w).Select(Convert.ToChar)]));
        })(Un4seen.Bass.BassNet.Registration, "\ud83d\udc0d⌛\ud83c\udfa4\ud83c\udfa4\ud83d\udcd9\ud83d\udca4\ud83d\udce8⛵\ud83d\udc1f\ud83d\udce8\ud83d\udc4a\ud83d\ude47\ud83c\udf04\ud83c\udfc2\ud83d\udc4a\ud83c\udf74\ud83d\udce8\ud83d\udc63\ud83d\udc11\ud83c\udf68\ud83d\udc4a⌛\ud83c\udfc2\ud83d\udc0d\ud83c\udf74\ud83d\udcd9\ud83c\udf68", "\ud83c\udfb0\ud83d\udc5d\ud83d\uddfe\ud83c\udf62\ud83c\udfb0\ud83c\udf62\ud83c\udfee\ud83d\uddfe\ud83c\udfb0\ud83c\udf62\ud83d\ude93\ud83c\udf70\ud83c\udfb0\ud83d\udc70\ud83d\ude0f\ud83c\udfb0");
    }

    private static Errors? CaptureInitializationNativeError(
        InitializationStage stage,
        bool wrapperRegistered)
    {
        if (!wrapperRegistered
            || (stage != InitializationStage.VersionValidation
                && stage != InitializationStage.Completed))
        {
            return null;
        }

        try
        {
            return Bass.LastError;
        }
        catch
        {
            return null;
        }
    }

    private static void TryLogInitializationFailure(
        InitializationStage stage,
        Errors? nativeError,
        Exception exception)
    {
        try
        {
            string nativeErrorSource = nativeError.HasValue ? "BASS" : "none";
            string nativeErrorDescription = nativeError.HasValue
                ? BassNativeErrorFormatter.Format(nativeError.Value)
                : "none";

            NLogWrapper.GetLogger(nameof(BassAudioRuntime)).Error(
                "BASS native bootstrap failed. stage=" + stage
                + " component=" + GetStageComponent(stage)
                + " exceptionType=" + exception.GetType().FullName
                + " nativeErrorSource=" + nativeErrorSource
                + " nativeErrorCode=" + nativeErrorDescription
                + " nativeRuntimeLoaded=" + BassNativeRuntime.IsLoaded);
        }
        catch
        {
            // Bootstrap diagnostics must not replace the primary initialization error.
        }
    }

    private static void TryFreeAfterInitializationFailure(InitializationStage stage)
    {
        try
        {
            BassNativeRuntime.Free();
        }
        catch (Exception cleanupException)
        {
            try
            {
                NLogWrapper.GetLogger(nameof(BassAudioRuntime)).Warn(
                    "BASS native bootstrap cleanup failed. stage=" + stage
                    + " exceptionType=" + cleanupException.GetType().FullName
                    + " nativeRuntimeLoaded=" + BassNativeRuntime.IsLoaded);
            }
            catch
            {
                // Cleanup diagnostics must not replace the primary initialization error.
            }
        }
    }

    private static string GetStageComponent(InitializationStage stage) => stage switch
    {
        InitializationStage.NativeLoad => nameof(BassNativeRuntime),
        InitializationStage.ResolverInstallation => nameof(ManagedBassNativeLibraryResolver),
        InitializationStage.LegacyWrapperRegistration => "BASS.NET",
        InitializationStage.VersionValidation => nameof(BassNativeRuntime),
        InitializationStage.Completed => nameof(BassAudioRuntime),
        _ => nameof(BassAudioRuntime)
    };

    /// <summary>
    /// Registers the one audio-session owner that must release its graph before runtime deactivation.
    /// </summary>
    internal static void RegisterAudioSessionShutdown(Func<bool> releaseAudioSessionForShutdown)
    {
        ArgumentNullException.ThrowIfNull(releaseAudioSessionForShutdown);
        lock (SessionOwnerSync)
        {
            _releaseAudioSessionForShutdown = releaseAudioSessionForShutdown;
        }
    }

    /// <summary>Tries to enter a shared native operation before admission closes.</summary>
    internal static bool TryEnterAudioOperation(out BassAudioOperationLease lease) =>
        RuntimeGate.TryEnterOperation(out lease);

    /// <summary>Tries to enter a callback or finalizer without waiting for cleanup.</summary>
    internal static bool TryEnterAudioCallbackOperation(out BassAudioOperationLease lease) =>
        RuntimeGate.TryEnterNonBlockingOperation(out lease);

    /// <summary>
    /// Enters a native operation that must finish before runtime shutdown deactivates the
    /// active native generation.
    /// </summary>
    internal static BassAudioOperationLease EnterAudioOperation()
    {
        if (!TryEnterAudioOperation(out BassAudioOperationLease lease))
        {
            throw new InvalidOperationException(
                "The native audio operation was rejected because shutdown is in progress.");
        }

        return lease;
    }

    /// <summary>Enters audio-session initialization after draining shared native calls.</summary>
    internal static BassAudioExclusiveLease EnterAudioSessionInitialization() =>
        RuntimeGate.EnterSessionInitialization();

    /// <summary>Tries to enter exclusive session cleanup without promoting a shared caller.</summary>
    internal static bool TryEnterAudioSessionCleanup(out BassAudioExclusiveLease lease) =>
        RuntimeGate.TryEnterSessionCleanup(out lease);

    /// <summary>Waits until a concurrent native runtime shutdown attempt has completed.</summary>
    internal static void WaitForAudioShutdownCompletion() =>
        RuntimeGate.WaitForShutdownCompletion();

    /// <summary>Waits until shutdown has closed new native operation admission.</summary>
    internal static bool WaitForAudioShutdownRequest(TimeSpan timeout) =>
        RuntimeGate.WaitForShutdownRequest(timeout);

    /// <summary>
    /// Frees the current BASS core device. An already-free device is an idempotent no-op.
    /// </summary>
    internal static void FreeDevice()
    {
        using BassAudioOperationLease operation = EnterAudioOperation();
        FreeCoreDevice(Bass.Free, () => Bass.LastError, TryLogAlreadyFreedCore);
    }

    /// <summary>
    /// Executes one core-device release and captures its error before any diagnostic or cleanup
    /// operation can overwrite it. This narrow seam keeps the runtime lifecycle deterministic
    /// under tests while production callers pass the ManagedBass boundary directly.
    /// </summary>
    internal static void FreeCoreDevice(
        Func<bool> freeCore,
        Func<Errors> getLastError,
        Action logAlreadyFreed)
    {
        ArgumentNullException.ThrowIfNull(freeCore);
        ArgumentNullException.ThrowIfNull(getLastError);
        ArgumentNullException.ThrowIfNull(logAlreadyFreed);

        bool freed;
        try
        {
            freed = freeCore();
        }
        catch
        {
            try
            {
                _ = getLastError();
            }
            catch
            {
                // Preserve the release exception when error retrieval is unavailable.
            }

            throw;
        }

        if (freed)
        {
            return;
        }

        Errors error = getLastError();
        if (error != Errors.Init)
        {
            throw new Exception("BASS_Free failed: " + BassNativeErrorFormatter.Format(error));
        }

        logAlreadyFreed();
    }

    /// <summary>
    /// Drains audio operations, releases the owned graph, and deactivates the native runtime.
    /// ManagedBass-bound modules remain mapped until process exit because the CLR caches their
    /// resolved native function pointers.
    /// </summary>
    internal static void Shutdown()
    {
        using BassAudioExclusiveLease lifecycle = RuntimeGate.EnterRuntimeShutdown();
        bool succeeded = false;
        try
        {
            Func<bool> releaseAudioSession;
            lock (SessionOwnerSync)
            {
                releaseAudioSession = _releaseAudioSessionForShutdown;
            }
            if (releaseAudioSession?.Invoke() == false)
            {
                throw new InvalidOperationException(
                    "BASS native runtime shutdown was deferred because audio cleanup was not confirmed.");
            }

            if (_isInitialized)
            {
                FreeCoreDevice(Bass.Free, () => Bass.LastError, TryLogAlreadyFreedCore);
            }

            BassNativeRuntime.Free();
            _isInitialized = false;
            succeeded = true;
        }
        finally
        {
            lifecycle.Complete(succeeded);
        }
    }

    private static void TryLogAlreadyFreedCore()
    {
        try
        {
            NLogWrapper.GetLogger("AudioSession").Debug(
                "BASS_Free found no initialized core device.");
        }
        catch
        {
            // BASS_ERROR_INIT is an idempotent cleanup result regardless of diagnostics.
        }
    }
}
