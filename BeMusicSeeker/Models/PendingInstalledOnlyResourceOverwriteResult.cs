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
}
