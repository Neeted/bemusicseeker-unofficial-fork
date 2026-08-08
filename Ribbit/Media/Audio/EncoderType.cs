namespace Ribbit.Media.Audio;

/// <summary>
/// Persisted encoder selection values. Numeric values are part of the settings contract.
/// </summary>
public enum EncoderType
{
    /// <summary>PCM/WAVE output.</summary>
    WAVE = 0,

    /// <summary>LAME MP3 output.</summary>
    MP3_LAME = 1,

    /// <summary>Nero AAC output.</summary>
    AAC_NERO = 2,

    /// <summary>Opus output.</summary>
    OPUS = 3,

    /// <summary>FLAC output.</summary>
    FLAC = 4,

    /// <summary>Ogg Vorbis output.</summary>
    OGG_VORBIS = 5
}
