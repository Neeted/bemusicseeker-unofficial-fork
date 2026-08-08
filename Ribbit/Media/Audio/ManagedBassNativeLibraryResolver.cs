using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using ManagedBass;
using ManagedBass.Asio;
using ManagedBass.Enc;
using ManagedBass.Fx;
using ManagedBass.Mix;
using ManagedBass.Wasapi;

namespace Ribbit.Media.Audio;

/// <summary>
/// Connects ManagedBass P/Invoke requests to the native handles owned by the audio runtime.
/// </summary>
internal static class ManagedBassNativeLibraryResolver
{
    private static readonly IReadOnlyDictionary<string, string> CanonicalFileNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bass"] = "bass.dll",
            ["bass.dll"] = "bass.dll",
            ["bassasio"] = "bassasio.dll",
            ["bassasio.dll"] = "bassasio.dll",
            ["basswasapi"] = "basswasapi.dll",
            ["basswasapi.dll"] = "basswasapi.dll",
            ["bassmix"] = "bassmix.dll",
            ["bassmix.dll"] = "bassmix.dll",
            ["bass_fx"] = "bass_fx.dll",
            ["bass_fx.dll"] = "bass_fx.dll",
            ["bassenc"] = "bassenc.dll",
            ["bassenc.dll"] = "bassenc.dll"
        };

    private static readonly Assembly[] ManagedBassAssemblies =
    {
        typeof(Bass).Assembly,
        typeof(BassMix).Assembly,
        typeof(BassFx).Assembly,
        typeof(BassEnc).Assembly,
        typeof(BassAsio).Assembly,
        typeof(BassWasapi).Assembly
    };

    private static readonly Lazy<bool> Installation = new(
        InstallResolvers,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Installs all six ManagedBass assembly resolvers once per process.</summary>
    internal static void EnsureInstalled()
    {
        _ = Installation.Value;
    }

    /// <summary>
    /// Resolves a library name for tests without invoking the assembly-specific callback.
    /// </summary>
    /// <remarks>
    /// Unknown names return zero so the runtime's normal native probing remains available. Known
    /// names are required to resolve through the current generation and never fall back to a
    /// second native copy after the runtime has been deactivated.
    /// </remarks>
    internal static IntPtr ResolveForTesting(string libraryName)
    {
        return Resolve(libraryName, typeof(Bass).Assembly, searchPath: null);
    }

    private static bool InstallResolvers()
    {
        foreach (Assembly assembly in ManagedBassAssemblies)
        {
            NativeLibrary.SetDllImportResolver(assembly, Resolve);
        }

        return true;
    }

    private static IntPtr Resolve(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!CanonicalFileNames.TryGetValue(libraryName, out string canonicalFileName))
        {
            return IntPtr.Zero;
        }

        return BassNativeRuntime.ResolveLoadedLibrary(canonicalFileName);
    }
}
