using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using Livet;
using Livet.EventListeners;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns the maintenance tree presentation state and library notification boundary.
/// </summary>
public sealed class MaintenanceTreeViewModel : ViewModel
{
    private IMaintenanceTreePresentationState presentationState;

    private PropertyChangedEventListener presentationStatePropertyChangedListener;

    private readonly IUiScheduler uiScheduler;

    private readonly DuplicateTreeChildCollection duplicateTree = new(new DuplicateMergeCandidateListNode());

    private readonly DuplicateTreeChildCollection splitPlacementTree = new(new DuplicateMergeCandidateListNode());

    private Task observedPreparation;

    /// <summary>未接続の窓口です。項目の同期はその場で行います。</summary>
    public MaintenanceTreeViewModel()
    {
    }

    /// <param name="uiScheduler">項目の同期を実行するUIの窓口。未指定ならその場で同期します。</param>
    internal MaintenanceTreeViewModel(IUiScheduler uiScheduler)
    {
        this.uiScheduler = uiScheduler;
    }

    internal event EventHandler<MaintenanceTreeDuplicatePresentationChangedEventArgs> DuplicatePresentationChanged;

    /// <summary>分裂配置の結果の変更・失効、接続・解除で発生します。重複の通知とは独立です。</summary>
    internal event EventHandler<MaintenanceTreeDuplicatePresentationChangedEventArgs> SplitPlacementPresentationChanged;

    /// <summary>
    /// 接続中ライブラリで開始済みの重複データ準備を返す窓口です。未完了のTaskが返る間は、結果が失効していても直前の項目を維持します。
    /// 新しい解析は開始しません。未設定なら失効した結果は空として扱います。
    /// </summary>
    internal Func<Task> DuplicatePreparationProvider { get; set; }

    /// <summary>
    /// Gets the canonical duplicate-group collection exposed to the maintenance tree.
    /// </summary>
    public List<DuplicateGroup> DuplicateChartGroups
        => presentationState?.DuplicateChartGroups;

    /// <summary>
    /// 重複ツリーの項目です。コレクションのインスタンスは接続・解除・同期を通じて変えず、
    /// 新しい結果にも含まれる同じグループの項目は除去・置換しないため、ツリーの展開状態を保ちます。変更はUIスレッドだけで行います。
    /// </summary>
    public ObservableCollection<DuplicateGroup> DuplicateTreeItems => duplicateTree.Items;

    /// <summary>
    /// 重複ツリーの子として表示する項目です。先頭に統合候補一覧のノード（重複検索結果が一件以上あるときだけ現れる）を置き、
    /// その後ろに <see cref="DuplicateTreeItems"/> のグループ項目を並べます。インスタンスは接続・解除・同期を通じて変えず、
    /// 元の集合の変更を位置を補正した個別の変更として通知するため、グループ項目の同一性とツリーの展開状態を保ちます。変更はUIスレッドだけで行います。
    /// </summary>
    public System.Collections.IList DuplicateTreeChildren => duplicateTree;

    /// <summary>重複ツリーの先頭に置く統合候補一覧のノードです。項目の有無とは別に、常に同じインスタンスを返します。</summary>
    internal DuplicateMergeCandidateListNode DuplicateMergeCandidateListNodeItem => duplicateTree.CandidateListNode;

    /// <summary>
    /// 分裂配置ツリーの子として表示する項目です。先頭に統合候補一覧のノード（分裂配置の結果が一件以上あるときだけ現れる、重複のノードとは別の項目）を置き、
    /// その後ろにグループ項目を結果の順に並べます。インスタンスは接続・解除・同期を通じて変えず、新しい結果にも含まれる同じグループの項目は除去・置換しません。
    /// 変更はUIスレッドだけで行います。
    /// </summary>
    public System.Collections.IList SplitPlacementTreeChildren => splitPlacementTree;

    /// <summary>分裂配置ツリーの先頭に置く統合候補一覧のノードです。項目の有無とは別に、常に同じインスタンスを返します。</summary>
    internal DuplicateMergeCandidateListNode SplitPlacementCandidateListNodeItem => splitPlacementTree.CandidateListNode;

    /// <summary>分裂配置の現在の結果です。結果がない状態は <see langword="null"/> です。</summary>
    internal List<DuplicateGroup> SplitPlacementChartGroups
        => presentationState?.SplitPlacementGroups;

    /// <summary>
    /// Gets whether health maintenance initialization is still holding its write lock.
    /// </summary>
    public bool IsWriteLockHeldInitializdBMSFilesHealthStatus
        => presentationState?.IsWriteLockHeldInitializdBMSFilesHealthStatus ?? true;

    /// <summary>
    /// Gets whether encoding maintenance initialization is still holding its write lock.
    /// </summary>
    public bool IsWriteLockHeldInitializeBMSFilesEncodingInfo
        => presentationState?.IsWriteLockHeldInitializeBMSFilesEncodingInfo ?? true;

    /// <summary>
    /// Gets whether zero-note maintenance initialization is still holding its write lock.
    /// </summary>
    public bool IsWriteLockHeldInitializeBMSFilesZeroNote
        => presentationState?.IsWriteLockHeldInitializeBMSFilesZeroNote ?? true;

    /// <summary>
    /// Gets whether duplicate-group maintenance is still holding its write lock.
    /// </summary>
    public bool IsWriteLockHeldDuplicateChartGroups
        => presentationState?.IsWriteLockHeldDuplicateChartGroups ?? false;

    /// <summary>
    /// Attaches a maintenance presentation state and subscribes to its existing notifications.
    /// </summary>
    internal void AttachPresentationState(IMaintenanceTreePresentationState value)
    {
        if (ReferenceEquals(presentationState, value))
        {
            return;
        }

        DetachLibrary();
        presentationState = value ?? throw new ArgumentNullException(nameof(value));
        IMaintenanceTreePresentationState attachedState = presentationState;
        presentationStatePropertyChangedListener = new PropertyChangedEventListener(attachedState);
        presentationStatePropertyChangedListener.RegisterHandler(
            () => attachedState.DuplicateChartGroups,
            delegate
            {
                PublishDuplicatePresentationChanged("bms_files_duplicated_changed");
            });
        presentationStatePropertyChangedListener.RegisterHandler(
            () => attachedState.DuplicateChartGroupsInvalidationVersion,
            delegate
            {
                PublishDuplicatePresentationChanged("bms_files_duplicated_invalidated");
            });
        presentationStatePropertyChangedListener.RegisterHandler(
            () => attachedState.SplitPlacementGroups,
            delegate
            {
                PublishSplitPlacementPresentationChanged("split_placement_groups_changed");
            });
        presentationStatePropertyChangedListener.RegisterHandler(
            () => attachedState.IsWriteLockHeldInitializdBMSFilesHealthStatus,
            delegate
            {
                RaisePropertyChanged(nameof(IsWriteLockHeldInitializdBMSFilesHealthStatus));
            });
        presentationStatePropertyChangedListener.RegisterHandler(
            () => attachedState.IsWriteLockHeldInitializeBMSFilesEncodingInfo,
            delegate
            {
                RaisePropertyChanged(nameof(IsWriteLockHeldInitializeBMSFilesEncodingInfo));
            });
        presentationStatePropertyChangedListener.RegisterHandler(
            () => attachedState.IsWriteLockHeldInitializeBMSFilesZeroNote,
            delegate
            {
                RaisePropertyChanged(nameof(IsWriteLockHeldInitializeBMSFilesZeroNote));
            });
        presentationStatePropertyChangedListener.RegisterHandler(
            () => attachedState.IsWriteLockHeldDuplicateChartGroups,
            delegate
            {
                RaisePropertyChanged(nameof(IsWriteLockHeldDuplicateChartGroups));
            });
        RaiseBusyStateProperties();
        ApplyDuplicateGroupsPresentation();
        ApplySplitPlacementPresentation();
        PublishDuplicatePresentationChanged("maintenance_tree_attached");
        PublishSplitPlacementPresentationChanged("maintenance_tree_attached");
    }

    /// <summary>
    /// Attaches a BMS library through the maintenance presentation contract.
    /// </summary>
    internal void AttachLibrary(BMSLibrary value)
    {
        AttachPresentationState(value);
    }

    /// <summary>
    /// Detaches the current maintenance presentation state and returns to unattached defaults.
    /// </summary>
    internal void DetachLibrary()
    {
        if (presentationStatePropertyChangedListener is IDisposable disposable)
        {
            disposable.Dispose();
        }
        presentationStatePropertyChangedListener = null;
        if (presentationState == null)
        {
            return;
        }
        presentationState = null;
        RaiseBusyStateProperties();
        ApplyDuplicateGroupsPresentation();
        ApplySplitPlacementPresentation();
        PublishDuplicatePresentationChanged("maintenance_tree_detached");
        PublishSplitPlacementPresentationChanged("maintenance_tree_detached");
    }

    /// <summary>
    /// 更新抑制の判断後に、重複ツリーの項目を現在の結果へ同期します。通知はワーカースレッドから届き得るため、UIスレッドへ配送します。
    /// </summary>
    internal void ApplyDuplicateGroupsPresentation()
    {
        if (uiScheduler == null || uiScheduler.CanExecuteInline || uiScheduler.CheckAccess())
        {
            SynchronizeDuplicateTreeItems();
            return;
        }
        uiScheduler.Schedule(SynchronizeDuplicateTreeItems);
    }

    /// <summary>
    /// 重複ツリーの項目を、その時点の結果へ同期します。呼出し側がUIスレッドであることを保証します。
    /// 結果が失効中でも、接続中ライブラリの重複データ準備が未完了なら直前の項目を維持し、準備の完了で再び同期します。
    /// 準備が進行中でない失効と、ライブラリの解除では項目を空にします。
    /// </summary>
    internal void SynchronizeDuplicateTreeItems()
    {
        List<DuplicateGroup> groups = presentationState?.DuplicateChartGroups;
        if (groups != null)
        {
            duplicateTree.SynchronizeItems(groups);
            duplicateTree.SynchronizeCandidateListNode(groups.Count > 0);
            return;
        }
        if (presentationState != null && TryGetActivePreparation(out Task preparation))
        {
            if (!ReferenceEquals(observedPreparation, preparation))
            {
                observedPreparation = preparation;
                _ = preparation.ContinueWith(
                    _ => ApplyDuplicateGroupsPresentation(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            return;
        }
        duplicateTree.Clear();
    }

    /// <summary>
    /// 更新抑制の判断後に、分裂配置ツリーの項目を現在の結果へ同期します。通知はワーカースレッドから届き得るため、UIスレッドへ配送します。
    /// </summary>
    internal void ApplySplitPlacementPresentation()
    {
        if (uiScheduler == null || uiScheduler.CanExecuteInline || uiScheduler.CheckAccess())
        {
            SynchronizeSplitPlacementTreeItems();
            return;
        }
        uiScheduler.Schedule(SynchronizeSplitPlacementTreeItems);
    }

    /// <summary>
    /// 分裂配置ツリーの項目を、その時点の現在の結果へ同期します。呼出し側がUIスレッドであることを保証します。
    /// 結果が一件以上あるときだけ統合候補一覧のノードを先頭に置き、結果がない状態（失効・未探索・ライブラリの解除）では、ノードも含めて子を空にします。通知の時点ではなく同期の時点の現在値を読むため、
    /// 遅れて届いた失効の通知で、その後に格納された新しい結果を消しません。
    /// </summary>
    internal void SynchronizeSplitPlacementTreeItems()
    {
        List<DuplicateGroup> groups = presentationState?.SplitPlacementGroups;
        IsSynchronizingSplitPlacementTree = true;
        try
        {
            if (groups != null)
            {
                splitPlacementTree.SynchronizeItems(groups);
                splitPlacementTree.SynchronizeCandidateListNode(groups.Count > 0);
                return;
            }
            splitPlacementTree.Clear();
        }
        finally { IsSynchronizingSplitPlacementTree = false; }
    }

    /// <summary>
    /// 分裂配置ツリーの項目を同期している区間（UIスレッド）だけ真です。選択中の子が除去されると画面が親のルートを自動で選び直すため、
    /// 画面がその選択を利用者の選択と区別するために読みます。
    /// </summary>
    internal bool IsSynchronizingSplitPlacementTree { get; private set; }

    private bool TryGetActivePreparation(out Task preparation)
    {
        preparation = DuplicatePreparationProvider?.Invoke();
        return preparation != null && !preparation.IsCompleted;
    }

    /// <summary>操作前の現在順序から次グループの所属を値で捕捉します。末尾や退役した入力には対象を返しません。</summary>
    internal DuplicateViewContext CaptureNextDuplicateGroupSelection(DuplicateGroup duplicateGroup)
    {
        if (duplicateGroup == null || DuplicateChartGroups == null)
        {
            return null;
        }
        int currentIndex = DuplicateChartGroups.IndexOf(duplicateGroup);
        return currentIndex >= 0 && currentIndex + 1 < DuplicateChartGroups.Count
            ? DuplicateViewContext.CaptureGroup(DuplicateChartGroups[currentIndex + 1])
            : null;
    }

    private void RaiseBusyStateProperties()
    {
        RaisePropertyChanged(nameof(IsWriteLockHeldInitializdBMSFilesHealthStatus));
        RaisePropertyChanged(nameof(IsWriteLockHeldInitializeBMSFilesEncodingInfo));
        RaisePropertyChanged(nameof(IsWriteLockHeldInitializeBMSFilesZeroNote));
        RaisePropertyChanged(nameof(IsWriteLockHeldDuplicateChartGroups));
    }

    private void PublishDuplicatePresentationChanged(string reason)
    {
        DuplicatePresentationChanged?.Invoke(
            this,
            new MaintenanceTreeDuplicatePresentationChangedEventArgs(reason));
    }

    private void PublishSplitPlacementPresentationChanged(string reason)
    {
        SplitPlacementPresentationChanged?.Invoke(
            this,
            new MaintenanceTreeDuplicatePresentationChangedEventArgs(reason));
    }
}

internal sealed class MaintenanceTreeDuplicatePresentationChangedEventArgs : EventArgs
{
    internal MaintenanceTreeDuplicatePresentationChangedEventArgs(string reason)
    {
        Reason = reason;
    }

    internal string Reason { get; }
}
