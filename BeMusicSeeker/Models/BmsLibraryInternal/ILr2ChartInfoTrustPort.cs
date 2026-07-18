namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Carries the immutable LR2 chart-info trust snapshot into catalog hydration.
/// </summary>
internal interface ILr2ChartInfoTrustPort
{
    ChartInfoCompletedLr2SongDbSyncTrustSnapshot GetCurrent();

    void Clear(string reason);
}
