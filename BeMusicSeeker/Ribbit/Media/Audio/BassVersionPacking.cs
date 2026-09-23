using System;

namespace Ribbit.Media.Audio;

/// <summary>
/// Converts between the packed native BASS version representation and <see cref="Version"/>.
/// </summary>
/// <remarks>
/// BASS stores major, minor, build, and revision in one byte each. The managed
/// wrapper exposes the same value as <see cref="Version"/>, so the conversion
/// remains explicit at the native boundary instead of relying on an enum cast.
/// </remarks>
internal static class BassVersionPacking
{
    /// <summary>Converts a managed BASS version into the native packed representation.</summary>
    /// <param name="version">The managed version to convert.</param>
    /// <returns>The four-byte native version value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="version"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A version component cannot fit in one byte.</exception>
    internal static uint Pack(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);

        uint major = NormalizeComponent(version.Major, nameof(version.Major));
        uint minor = NormalizeComponent(version.Minor, nameof(version.Minor));
        uint build = NormalizeComponent(version.Build, nameof(version.Build));
        uint revision = NormalizeComponent(version.Revision, nameof(version.Revision));

        return (major << 24) | (minor << 16) | (build << 8) | revision;
    }

    /// <summary>Converts a native packed BASS version into a managed version.</summary>
    /// <param name="packedVersion">The four-byte native version value.</param>
    /// <returns>The corresponding four-component managed version.</returns>
    internal static Version Unpack(uint packedVersion) => new(
        (int)(packedVersion >> 24),
        (int)((packedVersion >> 16) & 0xff),
        (int)((packedVersion >> 8) & 0xff),
        (int)(packedVersion & 0xff));

    private static uint NormalizeComponent(int component, string parameterName)
    {
        if (component < 0)
        {
            // Version uses -1 for an omitted build or revision. Native BASS
            // represents omitted trailing components as zero.
            return 0;
        }

        if (component > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(parameterName, component, "BASS version components must fit in one byte.");
        }

        return (uint)component;
    }
}
