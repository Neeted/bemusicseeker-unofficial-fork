namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>資源入力に依存する版だけで、全所持資源事実の鮮度を検査します。</summary>
internal static class ResourceHealthFullOwnedTargetFreshness
{
    /// <summary>無関係な題名・警告の確定では資源結果を失効させません。</summary>
    internal static bool IsCurrent(ResourceMaintenanceTargetSet targetSet, int currentResourceHealthInputVersion,
        bool currentResourceHealthInputVersionStable)
        => targetSet.HasFullOwnedVersion && currentResourceHealthInputVersionStable
            && targetSet.ResourceHealthInputVersion.Value == currentResourceHealthInputVersion;
}
