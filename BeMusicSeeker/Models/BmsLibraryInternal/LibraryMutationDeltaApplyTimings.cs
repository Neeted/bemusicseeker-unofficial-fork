namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Carries the timing breakdown for the extracted library mutation delta workflow.
/// </summary>
internal sealed class LibraryMutationDeltaApplyTimings
{
    /// <summary>Gets or sets the elapsed milliseconds for starting the resource-health mutation window.</summary>
    internal long ResourceHealthBeginMs { get; set; }

    /// <summary>Gets or sets the elapsed milliseconds for building the owned mutation result.</summary>
    internal long BuildMutationMs { get; set; }

    /// <summary>Gets or sets the elapsed milliseconds for publishing the owned collection notification.</summary>
    internal long PublishNotificationMs { get; set; }

    /// <summary>Gets or sets the elapsed milliseconds for unregistering storage rows.</summary>
    internal long UnregisterStorageRowsMs { get; set; }

    /// <summary>Gets or sets the elapsed milliseconds for applying the state mutation.</summary>
    internal long StateApplyMs { get; set; }

    /// <summary>Gets or sets the elapsed milliseconds spent in folder DB work.</summary>
    internal long StateFolderDbMs { get; set; }

    /// <summary>Gets or sets the elapsed milliseconds spent applying path memory.</summary>
    internal long StatePathMemoryApplyMs { get; set; }

    /// <summary>Gets or sets the elapsed milliseconds spent updating BMS path DB rows.</summary>
    internal long StateBmsPathDbMs { get; set; }

    /// <summary>Gets or sets the elapsed milliseconds spent updating bmson path DB rows.</summary>
    internal long StateBmsonPathDbMs { get; set; }

    /// <summary>Gets or sets the elapsed milliseconds spent applying package state.</summary>
    internal long StatePackageApplyMs { get; set; }

    /// <summary>Gets or sets the elapsed milliseconds for applying owned collection state.</summary>
    internal long OwnedCollectionApplyMs { get; set; }

    /// <summary>Gets or sets the elapsed milliseconds for disposing the resource-health mutation window.</summary>
    internal long ResourceHealthDisposeMs { get; set; }

    /// <summary>Gets or sets the elapsed milliseconds for LR2 normal folder synchronization.</summary>
    internal long Lr2NormalFolderSyncMs { get; set; }

    /// <summary>Gets or sets the elapsed milliseconds for dispatching the owned mutation.</summary>
    internal long DispatchMs { get; set; }

    /// <summary>Gets or sets the total elapsed milliseconds for the workflow.</summary>
    internal long ElapsedMs { get; set; }
}
