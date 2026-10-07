namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>共通現在値の適用前後の集合版です。保存行専用の版を持ちません。</summary>
internal readonly struct OwnedChartCollectionVersionSnapshot
{
    /// <summary>変更を伴わない捕捉時点の集合版を保持します。</summary>
    internal OwnedChartCollectionVersionSnapshot(int ownedCollectionVersion)
        : this(ownedCollectionVersion, ownedCollectionVersion)
    {
    }

    /// <summary>確定した変更の適用前後を、同じ共通集合の版として保持します。</summary>
    internal OwnedChartCollectionVersionSnapshot(int previousOwnedCollectionVersion, int ownedCollectionVersion)
    {
        PreviousOwnedCollectionVersion = previousOwnedCollectionVersion;
        OwnedCollectionVersion = ownedCollectionVersion;
    }

    /// <summary>変更適用前の集合版です。</summary>
    internal int PreviousOwnedCollectionVersion { get; }

    /// <summary>変更適用後または捕捉時点の集合版です。</summary>
    internal int OwnedCollectionVersion { get; }
}
