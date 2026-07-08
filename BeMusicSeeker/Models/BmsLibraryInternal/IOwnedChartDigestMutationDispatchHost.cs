namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface IOwnedChartDigestMutationDispatchHost
{
    void DispatchOwnedChartDigestMutation(OwnedChartDigestMutationPlan plan, string reason);
}
