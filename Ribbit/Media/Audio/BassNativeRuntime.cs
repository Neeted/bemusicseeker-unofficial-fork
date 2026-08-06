using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Enc;
using Un4seen.Bass.AddOn.Fx;
using Un4seen.Bass.AddOn.Mix;
using Un4seen.BassAsio;
using Un4seen.BassWasapi;

namespace Ribbit.Media.Audio;

internal static class BassNativeRuntime
{
    private const int RequiredBassVersion = 0x02041203;
    private const int RequiredBassAsioVersion = 0x01040300;
    private const int RequiredBassWasapiVersion = 0x02040401;
    private const int RequiredBassMixVersion = 0x02040C00;
    private const int RequiredBassFxVersion = 0x02040C06;
    private const int RequiredBassEncVersion = 0x02041100;

    private static readonly IReadOnlyList<string> RequiredFileNames = Array.AsReadOnly(new[]
    {
        "bass.dll",
        "bassasio.dll",
        "basswasapi.dll",
        "bassmix.dll",
        "bass_fx.dll",
        "bassenc.dll"
    });

    private static readonly object SyncRoot = new();
    private static List<IntPtr> _loadedHandles;

    private static int loadInvocationCount;

    /// <summary>Gets how many times native loading was requested in this process.</summary>
    internal static int LoadInvocationCount => System.Threading.Volatile.Read(ref loadInvocationCount);

    /// <summary>Gets whether this process currently owns loaded BASS native module handles.</summary>
    internal static bool IsLoaded
    {
        get
        {
            lock (SyncRoot)
            {
                return _loadedHandles is not null;
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
        lock (SyncRoot)
        {
            if (_loadedHandles is not null)
            {
                throw new InvalidOperationException("BASS native runtime is already loaded.");
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

                    IntPtr handle = LoadLibrary(path);
                    if (handle == IntPtr.Zero)
                    {
                        int errorCode = Marshal.GetLastWin32Error();
                        throw new DllNotFoundException(
                            $"Unable to load BASS native library '{path}' (Win32 error {errorCode}).");
                    }

                    loadedHandles.Add(handle);
                }

                _loadedHandles = loadedHandles;
            }
            catch
            {
                FreeHandles(loadedHandles);
                throw;
            }
        }
    }

    internal static void ValidateSupportedVersions()
    {
        ValidateVersion("bass.dll", Bass.BASS_GetVersion(), RequiredBassVersion);
        ValidateVersion("bassasio.dll", BassAsio.BASS_ASIO_GetVersion(), RequiredBassAsioVersion);
        ValidateVersion("basswasapi.dll", BassWasapi.BASS_WASAPI_GetVersion(), RequiredBassWasapiVersion);
        ValidateVersion("bassmix.dll", BassMix.BASS_Mixer_GetVersion(), RequiredBassMixVersion);
        ValidateVersion("bass_fx.dll", BassFx.BASS_FX_GetVersion(), RequiredBassFxVersion);
        ValidateVersion("bassenc.dll", BassEnc.BASS_Encode_GetVersion(), RequiredBassEncVersion);
    }

    private static void ValidateVersion(string componentName, int actualVersion, int requiredVersion)
    {
        if (actualVersion != requiredVersion)
        {
            throw new InvalidOperationException(
                $"BASS native component '{componentName}' version mismatch. " +
                $"Expected 0x{requiredVersion:X8}, loaded 0x{actualVersion:X8}.");
        }
    }

    internal static void Free()
    {
        lock (SyncRoot)
        {
            if (_loadedHandles is null)
            {
                return;
            }

            List<IntPtr> loadedHandles = _loadedHandles;
            _loadedHandles = null;
            FreeHandles(loadedHandles);
        }
    }

    private static void FreeHandles(List<IntPtr> handles)
    {
        for (int index = handles.Count - 1; index >= 0; index--)
        {
            FreeLibrary(handles[index]);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);
}
