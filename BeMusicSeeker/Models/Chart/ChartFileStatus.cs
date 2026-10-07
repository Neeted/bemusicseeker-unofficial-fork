using System;

namespace BeMusicSeeker.Models;

/// <summary>
/// Represents chart-row runtime status flags used by list and playlist display rows.
/// </summary>
[Flags]
internal enum ChartFileStatus
{
    NONE = 0,
    PLAY = 1,
    LOADING = 2,
    PAUSE = 4,
    FORWARD = 8,
    BACKWARD = 0x10,
    PLAYALL = 0x1F,
    SEARCHING = 0x400,
    SCORE_UNSENT = 0x800
}
