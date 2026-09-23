using System;
using System.Runtime.InteropServices;
using ManagedBass;

namespace Ribbit.Media.Audio;

/// <summary>
/// Writes the two-field BASS_FX volume parameter block required by ManagedBass 4.0.2.
/// </summary>
internal static class ManagedBassVolumeEffect
{
    /// <summary>
    /// Applies a volume effect using the native BASS_BFX_VOLUME ABI. ManagedBass 4.0.2 does
    /// not expose a managed parameter type for this legacy BFX effect.
    /// </summary>
    internal static bool SetParameters(int effectHandle, float volume)
    {
        var parameters = new VolumeParameters
        {
            Channel = -1,
            Volume = volume
        };
        int size = Marshal.SizeOf<VolumeParameters>();
        IntPtr memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(parameters, memory, fDeleteOld: false);
            return Bass.FXSetParameters(effectHandle, memory);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VolumeParameters
    {
        internal int Channel;

        internal float Volume;
    }
}
