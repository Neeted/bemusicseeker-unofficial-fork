using System;
using System.Linq;
using Ribbit.Logging;
using Un4seen.Bass;

namespace Ribbit.Media.Audio;

public static class BassNet
{
    private static readonly object SessionOwnerSync = new();
    private static readonly BassAudioOperationGate RuntimeGate = new();

    private static bool _isInitialized;

    private static Func<bool> _releaseAudioSessionForShutdown;

    /// <summary>
    /// Loads and validates the bundled native runtime. Repeated calls are safe.
    /// </summary>
    public static void Initialize()
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

        try
        {
            BassNativeRuntime.Load();
            BassNativeRuntime.ValidateSupportedVersions();
            ((Action<Action<string, string>, string, string>)delegate (Action<string, string> f, string i, string j)
            {
                f(new string([.. (from n in "d7cbxdba4x22b9xd7daxdf35xd7cexdf3fxd7caxdc65xd7d8xdc43xd7d2xdc78x2689xd7dcxdbbcxd7d8xdca8xd7d6xdbddxd7dcxdddexd7d0xded6xd7d9xdf53xd7d0".Split('x')
                                      select Convert.ToInt32(n, 16)).Zip(i.ToCharArray(), (w, v) => v - w).Select(Convert.ToChar)]), new string([.. (from n in "d80axdf58xd80axdc26xd80bxddc5xd80axdf2fxd80axdf79xd80bxdf2dxd80axdfbcxd80bxddcc".Split('x')
                                      select Convert.ToInt32(n, 16)).Zip(j.ToCharArray(), (w, v) => v - w).Select(Convert.ToChar)]));
            })(Un4seen.Bass.BassNet.Registration, "\ud83d\udc0d⌛\ud83c\udfa4\ud83c\udfa4\ud83d\udcd9\ud83d\udca4\ud83d\udce8⛵\ud83d\udc1f\ud83d\udce8\ud83d\udc4a\ud83d\ude47\ud83c\udf04\ud83c\udfc2\ud83d\udc4a\ud83c\udf74\ud83d\udce8\ud83d\udc63\ud83d\udc11\ud83c\udf68\ud83d\udc4a⌛\ud83c\udfc2\ud83d\udc0d\ud83c\udf74\ud83d\udcd9\ud83c\udf68", "\ud83c\udfb0\ud83d\udc5d\ud83d\uddfe\ud83c\udf62\ud83c\udfb0\ud83c\udf62\ud83c\udfee\ud83d\uddfe\ud83c\udfb0\ud83c\udf62\ud83d\ude93\ud83c\udf70\ud83c\udfb0\ud83d\udc70\ud83d\ude0f\ud83c\udfb0");
            _isInitialized = true;
            lifecycle.Complete(success: true);
        }
        catch
        {
            BassNativeRuntime.Free();
            _isInitialized = false;
            lifecycle.Complete(success: false);
            throw;
        }
    }

    /// <summary>
    /// Registers the one audio-session owner that must release its graph before native unload.
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
    /// Enters a native operation that must finish before runtime shutdown can unload the DLLs.
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

    /// <summary>
    /// Frees the current BASS core device. An already-free device is an idempotent no-op.
    /// </summary>
    internal static void FreeDevice()
    {
        using BassAudioOperationLease operation = EnterAudioOperation();
        if (Bass.BASS_Free())
        {
            return;
        }

        BASSError error = Bass.BASS_ErrorGetCode();
        if (error != BASSError.BASS_ERROR_INIT)
        {
            throw new Exception("BASS_Free failed: " + error);
        }

        TryLogAlreadyFreedCore();
    }

    /// <summary>
    /// Drains audio operations, releases the owned graph, and unloads the native runtime.
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

            if (_isInitialized && !Bass.BASS_Free())
            {
                BASSError error = Bass.BASS_ErrorGetCode();
                if (error != BASSError.BASS_ERROR_INIT)
                {
                    throw new Exception("BASS_Free failed: " + error);
                }
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
