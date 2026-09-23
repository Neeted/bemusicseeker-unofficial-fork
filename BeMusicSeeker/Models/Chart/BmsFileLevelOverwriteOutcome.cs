namespace BeMusicSeeker.Models;

/// <summary>
/// Describes whether playlist level overwrite reached the LR2 song database writer.
/// </summary>
internal enum BmsFileLevelOverwriteOutcome
{
    /// <summary>
    /// The request was not started because the table was null.
    /// </summary>
    NotStarted,

    /// <summary>
    /// LR2 synchronization state prevented the mutation from starting.
    /// </summary>
    BlockedByLr2Synchronization,

    /// <summary>
    /// The live rows were applied and the database write completed.
    /// </summary>
    Completed
}
