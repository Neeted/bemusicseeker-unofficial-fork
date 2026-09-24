using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using ManagedBass;
using ManagedBass.Asio;
using ManagedBass.Enc;
using ManagedBass.Fx;
using ManagedBass.Mix;
using ManagedBass.Wasapi;

namespace Ribbit.Media.Audio;

internal static class BassNativeRuntime
{
    private const uint RequiredBassVersion = 0x02041203;
    private const uint RequiredBassAsioVersion = 0x01040300;
    private const uint RequiredBassWasapiVersion = 0x02040401;
    private const uint RequiredBassMixVersion = 0x02040D00;
    private const uint RequiredBassFxVersion = 0x02040C06;
    private const uint RequiredBassEncVersion = 0x02041100;

    private static readonly IReadOnlyList<string> RequiredFileNames = Array.AsReadOnly(new[]
    {
        "bass.dll",
        "bassasio.dll",
        "basswasapi.dll",
        "bassmix.dll",
        "bass_fx.dll",
        "bassenc.dll",
        "bms_vorbis.dll"
    });

    private static readonly object SyncRoot = new();
    private static NativeHandleGeneration _loadedGeneration;
    private static NativeHandleGeneration _pinnedGeneration;

    private static int loadInvocationCount;

    /// <summary>Gets how many times native loading was requested in this process.</summary>
    internal static int LoadInvocationCount => System.Threading.Volatile.Read(ref loadInvocationCount);

    /// <summary>Gets whether a native generation is currently published for active operations.</summary>
    internal static bool IsLoaded
    {
        get
        {
            lock (SyncRoot)
            {
                return _loadedGeneration is not null;
            }
        }
    }

    private static string NativeDirectory
    {
        get
        {
            return Path.Combine(AppContext.BaseDirectory, "libs", "x64");
        }
    }

    internal static void Load()
    {
        System.Threading.Interlocked.Increment(ref loadInvocationCount);
        LoadCore(LoadLibrary, handle => FreeLibrary(handle), reusePinnedGeneration: true);
    }

    /// <summary>
    /// Loads the required native files through supplied handles for deterministic rollback tests.
    /// </summary>
    /// <remarks>
    /// This seam is internal and test-only. Production loading always uses the bundled files and
    /// the operating-system loader through <see cref="Load"/>.
    /// </remarks>
    internal static void LoadForTesting(
        Func<string, IntPtr> loadLibrary,
        Action<IntPtr> freeLibrary)
    {
        ArgumentNullException.ThrowIfNull(loadLibrary);
        ArgumentNullException.ThrowIfNull(freeLibrary);
        LoadCore(loadLibrary, freeLibrary, reusePinnedGeneration: false);
    }

    private static void LoadCore(
        Func<string, IntPtr> loadLibrary,
        Action<IntPtr> freeLibrary,
        bool reusePinnedGeneration)
    {
        lock (SyncRoot)
        {
            if (_loadedGeneration is not null)
            {
                throw new InvalidOperationException("BASS native runtime is already loaded.");
            }

            if (reusePinnedGeneration && _pinnedGeneration is not null)
            {
                _loadedGeneration = _pinnedGeneration;
                return;
            }

            string directory = NativeDirectory;
            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException("BASS native directory not found: " + directory);
            }

            var loadedHandles = new List<IntPtr>(RequiredFileNames.Count);
            try
            {
                foreach (string fileName in RequiredFileNames)
                {
                    string path = Path.GetFullPath(Path.Combine(directory, fileName));
                    if (!File.Exists(path))
                    {
                        throw new DllNotFoundException(path);
                    }

                    IntPtr handle = loadLibrary(path);
                    if (handle == IntPtr.Zero)
                    {
                        int errorCode = Marshal.GetLastWin32Error();
                        throw new DllNotFoundException(
                            $"Unable to load BASS native library '{path}' (Win32 error {errorCode}).");
                    }

                    loadedHandles.Add(handle);
                }

                _loadedGeneration = new NativeHandleGeneration(RequiredFileNames, loadedHandles);
            }
            catch
            {
                FreeHandles(loadedHandles, freeLibrary);
                throw;
            }
        }
    }

    /// <summary>CLRが解決済みのnative entry pointを使い終えるまで、現在のnative一式をプロセスに保持します。</summary>
    /// <remarks>
    /// CLRは解決したDllImportの関数ポインターを保持し、公開された解除手段を持ちません。
    /// またVorbis bridgeのC ABI delegateも同じgenerationの関数ポインターを保持します。
    /// そのため終了時にactive generationの公開を解除しても、ManagedBass DLLとbridgeはプロセス終了までmapしたままにします。
    /// </remarks>
    internal static void PinManagedBassGeneration()
    {
        lock (SyncRoot)
        {
            if (_pinnedGeneration is not null)
            {
                return;
            }

            _pinnedGeneration = _loadedGeneration
                ?? throw new InvalidOperationException(
                    "The ManagedBass native generation must be loaded before it is pinned.");
        }
    }

    internal static void ValidateSupportedVersions()
    {
        ValidateVersion("bass.dll", Bass.Version, RequiredBassVersion);
        ValidateVersion("bassasio.dll", BassAsio.Version, RequiredBassAsioVersion);
        ValidateVersion("basswasapi.dll", BassWasapi.Version, RequiredBassWasapiVersion);
        ValidateVersion("bassmix.dll", BassMix.Version, RequiredBassMixVersion);
        ValidateVersion("bass_fx.dll", BassFx.Version, RequiredBassFxVersion);
        ValidateVersion("bassenc.dll", BassEnc.Version, RequiredBassEncVersion);
        _ = VorbisDecoder.NativeBuildInfo;
    }

    private static void ValidateVersion(string componentName, Version actualVersion, uint requiredVersion)
    {
        uint packedVersion = BassVersionPacking.Pack(actualVersion);
        if (packedVersion != requiredVersion)
        {
            throw new InvalidOperationException(
                $"BASS native component '{componentName}' version mismatch. " +
                $"Expected 0x{requiredVersion:X8}, loaded 0x{packedVersion:X8}.");
        }
    }

    /// <summary>Validates a packed ManagedBass version for deterministic mismatch characterization.</summary>
    internal static void ValidateVersionForTesting(
        string componentName,
        Version actualVersion,
        uint requiredVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(componentName);
        ValidateVersion(componentName, actualVersion, requiredVersion);
    }

    /// <summary>Resolves a canonical native filename to the current generation's exact handle.</summary>
    /// <remarks>
    /// The resolver owns no handle or lifetime. The runtime generation remains the sole owner and
    /// clears publication before deactivating the active generation; only an incomplete candidate
    /// generation is physically unloaded during rollback.
    /// </remarks>
    internal static IntPtr ResolveLoadedLibrary(string canonicalFileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(canonicalFileName);
        lock (SyncRoot)
        {
            NativeHandleGeneration generation = _loadedGeneration
                ?? throw new DllNotFoundException(
                    $"ManagedBass native library '{canonicalFileName}' is not currently loaded.");
            if (!generation.HandlesByFileName.TryGetValue(canonicalFileName, out IntPtr handle))
            {
                throw new DllNotFoundException(
                    $"ManagedBass native library '{canonicalFileName}' is not owned by this runtime.");
            }

            return handle;
        }
    }

    internal static void Free()
    {
        lock (SyncRoot)
        {
            if (_loadedGeneration is null)
            {
                return;
            }

            NativeHandleGeneration generation = _loadedGeneration;
            _loadedGeneration = null;
            if (!ReferenceEquals(generation, _pinnedGeneration))
            {
                FreeHandles(generation.Handles, handle => FreeLibrary(handle));
            }
        }
    }

    private static void FreeHandles(
        IReadOnlyList<IntPtr> handles,
        Action<IntPtr> freeLibrary)
    {
        for (int index = handles.Count - 1; index >= 0; index--)
        {
            freeLibrary(handles[index]);
        }
    }

    private sealed class NativeHandleGeneration
    {
        internal NativeHandleGeneration(
            IReadOnlyList<string> fileNames,
            IReadOnlyList<IntPtr> handles)
        {
            Handles = new List<IntPtr>(handles).AsReadOnly();
            var handlesByFileName = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < fileNames.Count; index++)
            {
                handlesByFileName.Add(fileNames[index], handles[index]);
            }

            HandlesByFileName = handlesByFileName;
        }

        internal IReadOnlyList<IntPtr> Handles { get; }

        internal IReadOnlyDictionary<string, IntPtr> HandlesByFileName { get; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);
}
