using System.Collections.Generic;
using System.ComponentModel;

namespace BeMusicSeeker.Models;

/// <summary>
/// Defines the library state and notifications required by the maintenance tree.
/// </summary>
internal interface IMaintenanceTreePresentationState : INotifyPropertyChanged
{
    /// <summary>
    /// Gets the cached duplicate chart groups, or <see langword="null"/> when the cache is invalid.
    /// </summary>
    List<DuplicateGroup> DuplicateChartGroups { get; }

    /// <summary>
    /// Gets the monotonically increasing version published when duplicate groups are invalidated.
    /// </summary>
    int DuplicateChartGroupsInvalidationVersion { get; }

    /// <summary>
    /// 分裂配置の現在の結果です。結果がない状態は <see langword="null"/>、0件は空の一覧です。変更は <c>SplitPlacementGroups</c> の通知で届きます。
    /// </summary>
    List<DuplicateGroup> SplitPlacementGroups { get; }

    /// <summary>
    /// Gets whether health-status initialization currently holds its write lock.
    /// </summary>
    bool IsWriteLockHeldInitializdBMSFilesHealthStatus { get; }

    /// <summary>
    /// Gets whether encoding-information initialization currently holds its write lock.
    /// </summary>
    bool IsWriteLockHeldInitializeBMSFilesEncodingInfo { get; }

    /// <summary>
    /// Gets whether zero-note initialization currently holds its write lock.
    /// </summary>
    bool IsWriteLockHeldInitializeBMSFilesZeroNote { get; }

    /// <summary>
    /// Gets whether duplicate-group maintenance currently holds its write lock.
    /// </summary>
    bool IsWriteLockHeldDuplicateChartGroups { get; }
}
