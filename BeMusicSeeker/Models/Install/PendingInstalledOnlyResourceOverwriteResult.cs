using System.Collections.Generic;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Models;

public sealed class PendingInstalledOnlyResourceOverwriteResult
{
    public int Requested { get; set; }

    public int Processed { get; set; }

    public int SucceededInstall { get; set; }

    public int SucceededCleanupOnly { get; set; }

    public int SkippedNotPending { get; set; }

    public int SkippedMissingInstlDst { get; set; }

    public int SkippedMultiDestination { get; set; }

    public int SkippedNoComponentTarget { get; set; }

    public int Failed { get; set; }

    public bool Canceled { get; set; }

    /// <summary>
    /// Gets whether at least one filesystem/DB mutation crossed its durable
    /// point.  This is separate from cleanup and notification completion.
    /// </summary>
    public bool HasDurableCommit { get; internal set; }

    /// <summary>
    /// Gets whether compensation failed and manual recovery is required.
    /// </summary>
    public bool ManualRecoveryRequired { get; internal set; }

    /// <summary>
    /// Gets whether a post-durable finalizer failed.  Durable filesystem and
    /// database state remains authoritative, but the command is non-success.
    /// </summary>
    public bool HasDurableFinalizationFailure { get; internal set; }

    /// <summary>
    /// Gets whether the durable operation completed with retained cleanup
    /// leftovers.
    /// </summary>
    public bool CompletedWithCleanupFailure { get; internal set; }

    /// <summary>
    /// Gets the source, staging, backup, or destination paths requiring
    /// manual inspection when cleanup or compensation could not finish.
    /// </summary>
    public IReadOnlyList<string> RecoveryPaths { get; internal set; } = [];

    /// <summary>operation-scoped install session の canonical terminal facts。</summary>
    internal LibraryMutationSessionReceipt SessionReceipt { get; set; }
}
