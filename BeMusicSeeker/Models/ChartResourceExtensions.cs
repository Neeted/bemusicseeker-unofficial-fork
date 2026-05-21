using System;
using System.Linq;

namespace BeMusicSeeker.Models;

/// <summary>
/// Defines resource file extensions that are shared by BMS and bmson charts.
/// </summary>
internal static class ChartResourceExtensions
{
    /// <summary>
    /// Gets the canonical audio resource extension used for lookup aliases.
    /// </summary>
    internal static readonly string AudioCanonicalExtension = ".wav";

    /// <summary>
    /// Gets the canonical image resource extension used for lookup aliases.
    /// </summary>
    internal static readonly string ImageCanonicalExtension = ".png";

    /// <summary>
    /// Gets audio extensions that are normalized to <see cref="AudioCanonicalExtension"/> for resource lookup.
    /// </summary>
    internal static readonly string[] AudioAliasExtensions = [".ogg", ".mp3", ".flac"];

    /// <summary>
    /// Gets image extensions that are normalized to <see cref="ImageCanonicalExtension"/> for resource lookup.
    /// </summary>
    internal static readonly string[] ImageAliasExtensions = [".bmp", ".jpg", ".jpeg"];

    /// <summary>
    /// Gets supported chart audio resource extensions.
    /// </summary>
    internal static readonly string[] AudioExtensions = [AudioCanonicalExtension, .. AudioAliasExtensions];

    /// <summary>
    /// Gets supported chart image resource extensions.
    /// </summary>
    internal static readonly string[] ImageExtensions = [ImageCanonicalExtension, .. ImageAliasExtensions];

    /// <summary>
    /// Gets supported chart movie resource extensions.
    /// </summary>
    internal static readonly string[] MovieExtensions = [".mpg", ".mpeg", ".mp4", ".m4v", ".mp4v", ".avi", ".wmv", ".mov", ".webm", ".mkv", ".m1v", ".m2v", ".3gp", ".flv", ".rm"];

    /// <summary>
    /// Gets all supported visual resource extensions.
    /// </summary>
    internal static readonly string[] VisualExtensions = [.. ImageExtensions, .. MovieExtensions];

    /// <summary>
    /// Determines whether the extension identifies an audio resource.
    /// </summary>
    /// <param name="extension">The extension to classify.</param>
    /// <returns><c>true</c> when the extension is an audio resource extension.</returns>
    internal static bool IsAudioExtension(string extension)
    {
        return ContainsExtension(AudioExtensions, extension);
    }

    /// <summary>
    /// Determines whether the extension identifies an image resource.
    /// </summary>
    /// <param name="extension">The extension to classify.</param>
    /// <returns><c>true</c> when the extension is an image resource extension.</returns>
    internal static bool IsImageExtension(string extension)
    {
        return ContainsExtension(ImageExtensions, extension);
    }

    /// <summary>
    /// Determines whether the extension identifies a movie resource.
    /// </summary>
    /// <param name="extension">The extension to classify.</param>
    /// <returns><c>true</c> when the extension is a movie resource extension.</returns>
    internal static bool IsMovieExtension(string extension)
    {
        return ContainsExtension(MovieExtensions, extension);
    }

    /// <summary>
    /// Resolves lookup alias extensions to their canonical resource extension.
    /// </summary>
    /// <param name="extension">The extension to normalize.</param>
    /// <returns>The canonical extension for lookup keys, or the lower-case extension when no alias exists.</returns>
    internal static string ResolveLookupAliasExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        string normalizedExtension = extension.ToLowerInvariant();
        if (AudioAliasExtensions.Contains(normalizedExtension, StringComparer.OrdinalIgnoreCase))
        {
            return AudioCanonicalExtension;
        }
        if (ImageAliasExtensions.Contains(normalizedExtension, StringComparer.OrdinalIgnoreCase))
        {
            return ImageCanonicalExtension;
        }
        return normalizedExtension;
    }

    private static bool ContainsExtension(string[] extensions, string extension)
    {
        return !string.IsNullOrWhiteSpace(extension)
            && extensions.Any(item => extension.Equals(item, StringComparison.OrdinalIgnoreCase));
    }
}
