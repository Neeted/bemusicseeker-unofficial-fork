namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ResourceHealthIndexFullRebuildResult
{
    internal ResourceHealthIndexFullRebuildResult(
        ResourceHealthIndexSnapshot snapshot,
        bool staleFullOwnedTarget)
    {
        Snapshot = snapshot ?? ResourceHealthIndexSnapshot.Empty;
        StaleFullOwnedTarget = staleFullOwnedTarget;
    }

    internal ResourceHealthIndexSnapshot Snapshot { get; }

    internal bool StaleFullOwnedTarget { get; }
}
