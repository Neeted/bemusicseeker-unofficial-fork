namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ResourceHealthIndexDispatchResult
{
    internal ResourceHealthIndexSnapshot Snapshot { get; set; }

    internal bool DeltaApplied { get; set; }

    internal bool Deferred { get; set; }

    internal bool FullRebuilt { get; set; }

    internal long IndexMs { get; set; }
}
