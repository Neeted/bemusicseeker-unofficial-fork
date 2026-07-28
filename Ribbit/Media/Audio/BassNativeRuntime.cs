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

    private static string NativeDirectory
    {
        get
        {
            return Path.Combine(AppContext.BaseDirectory, "libs", "x64");
        }
    }

    internal static void Load()
    {
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
        if (Bass.BASS_GetVersion() != 0x02040C01 ||
            BassMix.BASS_Mixer_GetVersion() != 0x02040800 ||
            BassFx.BASS_FX_GetVersion() != 0x02040B01 ||
            BassWasapi.BASS_WASAPI_GetVersion() != 0x02040102 ||
            BassAsio.BASS_ASIO_GetVersion() != 0x01030100 ||
            BassEnc.BASS_Encode_GetVersion() != 0x02040D00)
        {
            throw new InvalidOperationException(
                "The loaded BASS native family does not match the supported x64 ABI set.");
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
