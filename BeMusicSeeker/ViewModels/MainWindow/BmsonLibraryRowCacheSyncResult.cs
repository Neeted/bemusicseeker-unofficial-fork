namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Describes whether bmson-backed library row cache synchronization changed visible row membership or sort identity.
/// </summary>
internal readonly struct BmsonLibraryRowCacheSyncResult
{
    internal BmsonLibraryRowCacheSyncResult(bool membershipChanged, bool sortKeyChanged, bool sourceIdentityChanged = false, bool sourceReferenceChanged = false)
    {
        MembershipChanged = membershipChanged;
        SortKeyChanged = sortKeyChanged;
        SourceIdentityChanged = sourceIdentityChanged;
        SourceReferenceChanged = sourceReferenceChanged;
    }

    internal bool MembershipChanged { get; }

    internal bool SortKeyChanged { get; }

    internal bool SourceIdentityChanged { get; }

    internal bool SourceReferenceChanged { get; }

    internal bool SourceChanged => MembershipChanged || SourceIdentityChanged || SourceReferenceChanged;
}
