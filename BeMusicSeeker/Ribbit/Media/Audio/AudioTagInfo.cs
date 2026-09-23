namespace Ribbit.Media.Audio;

/// <summary>
/// Immutable metadata captured for one audio-encoder session.
/// </summary>
public sealed class AudioTagInfo
{
    /// <summary>
    /// Initializes a metadata snapshot and canonicalizes missing text values to empty strings.
    /// </summary>
    public AudioTagInfo(
        string artist,
        string title,
        string genre,
        double durationSeconds,
        string bpm,
        string fileName,
        string comment)
    {
        Artist = artist ?? string.Empty;
        Title = title ?? string.Empty;
        Genre = genre ?? string.Empty;
        DurationSeconds = durationSeconds;
        Bpm = bpm ?? string.Empty;
        FileName = fileName ?? string.Empty;
        Comment = comment ?? string.Empty;
    }

    /// <summary>Gets the artist text.</summary>
    public string Artist { get; }

    /// <summary>Gets the title text.</summary>
    public string Title { get; }

    /// <summary>Gets the genre text.</summary>
    public string Genre { get; }

    /// <summary>Gets the duration in seconds.</summary>
    public double DurationSeconds { get; }

    /// <summary>Gets the beats-per-minute text.</summary>
    public string Bpm { get; }

    /// <summary>Gets the source file name text.</summary>
    public string FileName { get; }

    /// <summary>Gets the comment text.</summary>
    public string Comment { get; }

    /// <summary>Gets an empty metadata snapshot.</summary>
    public static AudioTagInfo Empty { get; } = new(string.Empty, string.Empty, string.Empty, 0d, string.Empty, string.Empty, string.Empty);
}
