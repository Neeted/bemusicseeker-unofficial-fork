using ManagedBass;

namespace Ribbit.Media.Audio;

/// <summary>
/// Keeps the established BASS diagnostic spelling while native boundaries use
/// <see cref="Errors"/>.
/// </summary>
internal static class BassNativeErrorFormatter
{
    /// <summary>Formats one ManagedBass error using the existing BASS diagnostic contract.</summary>
    internal static string Format(Errors error) => ((int)error) switch
    {
        0 => "BASS_OK",
        1 => "BASS_ERROR_MEM",
        2 => "BASS_ERROR_FILEOPEN",
        3 => "BASS_ERROR_DRIVER",
        4 => "BASS_ERROR_BUFLOST",
        5 => "BASS_ERROR_HANDLE",
        6 => "BASS_ERROR_FORMAT",
        7 => "BASS_ERROR_POSITION",
        8 => "BASS_ERROR_INIT",
        9 => "BASS_ERROR_START",
        10 => "BASS_ERROR_SSL",
        11 => "BASS_ERROR_REINIT",
        12 => "BASS_ERROR_NOCD",
        13 => "BASS_ERROR_CDTRACK",
        14 => "BASS_ERROR_ALREADY",
        16 => "BASS_ERROR_NOPAUSE",
        17 => "BASS_ERROR_NOTAUDIO",
        18 => "BASS_ERROR_NOCHAN",
        19 => "BASS_ERROR_ILLTYPE",
        20 => "BASS_ERROR_ILLPARAM",
        21 => "BASS_ERROR_NO3D",
        22 => "BASS_ERROR_NOEAX",
        23 => "BASS_ERROR_DEVICE",
        24 => "BASS_ERROR_NOPLAY",
        25 => "BASS_ERROR_FREQ",
        27 => "BASS_ERROR_NOTFILE",
        29 => "BASS_ERROR_NOHW",
        31 => "BASS_ERROR_EMPTY",
        32 => "BASS_ERROR_NONET",
        33 => "BASS_ERROR_CREATE",
        34 => "BASS_ERROR_NOFX",
        35 => "BASS_ERROR_PLAYING",
        37 => "BASS_ERROR_NOTAVAIL",
        38 => "BASS_ERROR_DECODE",
        39 => "BASS_ERROR_DX",
        40 => "BASS_ERROR_TIMEOUT",
        41 => "BASS_ERROR_FILEFORM",
        42 => "BASS_ERROR_SPEAKER",
        43 => "BASS_ERROR_VERSION",
        44 => "BASS_ERROR_CODEC",
        45 => "BASS_ERROR_ENDED",
        46 => "BASS_ERROR_BUSY",
        47 => "BASS_ERROR_UNSTREAMABLE",
        48 => "BASS_ERROR_PROTOCOL",
        49 => "BASS_ERROR_DENIED",
        50 => "BASS_ERROR_FREEING",
        51 => "BASS_ERROR_CANCEL",
        1000 => "BASS_ERROR_WMA_LICENSE",
        1001 => "BASS_ERROR_WMA_WM9",
        1002 => "BASS_ERROR_WMA_DENIED",
        1003 => "BASS_ERROR_WMA_CODEC",
        1004 => "BASS_ERROR_WMA_INDIVIDUAL",
        2000 => "BASS_ERROR_ACM_CANCEL",
        2100 => "BASS_ERROR_CAST_DENIED",
        2101 => "BASS_ERROR_SERVER_CERT",
        3000 => "BASS_VST_ERROR_NOINPUTS",
        3001 => "BASS_VST_ERROR_NOOUTPUTS",
        3002 => "BASS_VST_ERROR_NOREALTIME",
        5000 => "BASS_ERROR_WASAPI",
        5001 => "BASS_ERROR_WASAPI_BUFFER",
        5002 => "BASS_ERROR_WASAPI_CATEGORY",
        6000 => "BASS_ERROR_MP4_NOSTREAM",
        7000 => "BASS_ERROR_MIDI_INCLUDE",
        8000 => "BASS_ERROR_WEBM_NOTAUDIO",
        8001 => "BASS_ERROR_WEBM_TRACK",
        -1 => "BASS_ERROR_UNKNOWN",
        _ => "BASS_ERROR_" + error.ToString().ToUpperInvariant()
    };

    /// <summary>Formats an optional error for diagnostics.</summary>
    internal static string Format(Errors? error) => error.HasValue ? Format(error.Value) : "-";
}
