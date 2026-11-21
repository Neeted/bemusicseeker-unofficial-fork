using System;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>分裂配置の関係を判定するときに、共有率の基準とする音源の集合です。</summary>
public enum SplitPlacementShareBasis
{
    /// <summary>判定用の音源集合が小さい方（同数ならどちらでも同じ）の個数を基準にします。</summary>
    SmallerSide = 0,

    /// <summary>判定用の音源集合が大きい方の個数を基準にします。どちらのフォルダから見ても共有率を満たすことを意味します。</summary>
    BothSides = 1,
}

/// <summary>
/// 分裂配置の探索条件です。共有率、汎用名とみなすフォルダ数、共有率の基準を持つ変更不能な値で、値が等しければ等価です。
/// 範囲の外の値・刻みに合わない共有率・列挙の外の値は、構築時に <see cref="ArgumentOutOfRangeException"/> で拒否します。
/// </summary>
/// <remarks>
/// 結果の有効性は探索条件の値の等価で判定するため、同じ値の別インスタンスは同じ条件として扱われます。保存はしません。
/// </remarks>
internal sealed record SplitPlacementSearchCriteria
{
    /// <summary>共有率の最小値（百分率）です。</summary>
    internal const int MinimumSharePercent = 50;

    /// <summary>共有率の最大値（百分率）です。</summary>
    internal const int MaximumSharePercent = 100;

    /// <summary>共有率の刻み（百分率）です。</summary>
    internal const int SharePercentStep = 5;

    /// <summary>汎用名とみなすフォルダ数の最小値です。</summary>
    internal const int MinimumGenericFolderLimit = 2;

    /// <summary>汎用名とみなすフォルダ数の最大値です。無関係なフォルダが連結した巨大なグループを防ぐ上限です。</summary>
    internal const int MaximumGenericFolderLimit = 100;

    /// <summary>既定の探索条件（共有率80%、汎用名とみなすフォルダ数20、基準は音源の少ない方）です。</summary>
    internal static SplitPlacementSearchCriteria Default { get; } = new(80, 20, SplitPlacementShareBasis.SmallerSide);

    /// <param name="sharePercent">共有率（百分率の整数）。50〜100で5刻みです。</param>
    /// <param name="genericFolderLimit">この数を超える対象フォルダが持つ音源キーを汎用名とします。2〜100です。</param>
    /// <param name="basis">共有率の基準です。</param>
    internal SplitPlacementSearchCriteria(int sharePercent, int genericFolderLimit, SplitPlacementShareBasis basis)
    {
        if (sharePercent < MinimumSharePercent || sharePercent > MaximumSharePercent
            || (sharePercent - MinimumSharePercent) % SharePercentStep != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sharePercent), sharePercent, "The share percent must be 50 to 100 in steps of 5.");
        }
        if (genericFolderLimit < MinimumGenericFolderLimit || genericFolderLimit > MaximumGenericFolderLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(genericFolderLimit), genericFolderLimit, "The generic folder limit must be 2 to 100.");
        }
        if (!Enum.IsDefined(basis))
        {
            throw new ArgumentOutOfRangeException(nameof(basis), basis, "The share basis is not defined.");
        }
        SharePercent = sharePercent;
        GenericFolderLimit = genericFolderLimit;
        Basis = basis;
    }

    /// <summary>共有率（百分率の整数）です。</summary>
    internal int SharePercent { get; }

    /// <summary>この数を超える対象フォルダが持つ音源キーを汎用名とします。</summary>
    internal int GenericFolderLimit { get; }

    /// <summary>共有率の基準です。</summary>
    internal SplitPlacementShareBasis Basis { get; }
}
