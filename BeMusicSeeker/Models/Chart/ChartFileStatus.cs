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

/// <summary>
/// Converts BMS storage status flags to chart-domain display status flags.
/// </summary>
internal static class ChartFileStatusMapper
{
    /// <summary>
    /// Converts a BMS storage row status to a chart display status.
    /// </summary>
    /// <param name="status">The BMS storage status.</param>
    /// <returns>The equivalent chart display status.</returns>
    internal static ChartFileStatus FromBmsFileStatus(BMSFile.BMSFileStatus status)
    {
        ChartFileStatus result = ChartFileStatus.NONE;
        if (status.HasFlag(BMSFile.BMSFileStatus.PLAY))
        {
            result |= ChartFileStatus.PLAY;
        }
        if (status.HasFlag(BMSFile.BMSFileStatus.LOADING))
        {
            result |= ChartFileStatus.LOADING;
        }
        if (status.HasFlag(BMSFile.BMSFileStatus.PAUSE))
        {
            result |= ChartFileStatus.PAUSE;
        }
        if (status.HasFlag(BMSFile.BMSFileStatus.FORWARD))
        {
            result |= ChartFileStatus.FORWARD;
        }
        if (status.HasFlag(BMSFile.BMSFileStatus.BACKWARD))
        {
            result |= ChartFileStatus.BACKWARD;
        }
        return result;
    }

}
