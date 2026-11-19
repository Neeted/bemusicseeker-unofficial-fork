using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MaintenanceTreeViewModelTests
{
    /// <summary>
    /// 接続した保守状態の重複集合と各稼働状態を投影し、初回の接続通知を一回出して、接続時の状態欠落を検出する。
    /// </summary>
    [TestMethod]
    public void Attach_ProjectsStateAndPublishesInitialFact()
    {
        var attachedGroup = new DuplicateGroup([], [@"C:\Songs\Attached"]);
        var duplicateGroups = new List<DuplicateGroup> { attachedGroup };
        var state = new MaintenanceTreePresentationStateFake(
            duplicateGroups,
            healthStatusWriteLockHeld: false,
            encodingInfoWriteLockHeld: false,
            zeroNoteWriteLockHeld: false,
            duplicateGroupsWriteLockHeld: true);
        var owner = new MaintenanceTreeViewModel();
        var reasons = new List<string>();
        owner.DuplicatePresentationChanged += (_, args) => reasons.Add(args.Reason);
        ObservableCollection<DuplicateGroup> itemsBeforeAttach = owner.DuplicateTreeItems;

        owner.AttachPresentationState(state);

        Assert.AreSame(duplicateGroups, owner.DuplicateChartGroups);
        Assert.AreSame(itemsBeforeAttach, owner.DuplicateTreeItems, "項目のコレクションは接続で差し替えません。");
        CollectionAssert.AreEqual(new[] { attachedGroup }, owner.DuplicateTreeItems.ToArray());
        Assert.IsFalse(owner.IsWriteLockHeldInitializdBMSFilesHealthStatus);
        Assert.IsFalse(owner.IsWriteLockHeldInitializeBMSFilesEncodingInfo);
        Assert.IsFalse(owner.IsWriteLockHeldInitializeBMSFilesZeroNote);
        Assert.IsTrue(owner.IsWriteLockHeldDuplicateChartGroups);
        CollectionAssert.AreEqual(new[] { "maintenance_tree_attached" }, reasons);
    }

    /// <summary>
    /// 重複集合の置換と失効を同期で区別して通知し、表示反映で対象プロパティだけを通知することで、失効の取り落としや別状態の誤更新を検出する。
    /// </summary>
    [TestMethod]
    public void DuplicateReplacementAndInvalidation_PublishDistinctFactsSynchronously()
    {
        var state = new MaintenanceTreePresentationStateFake();
        var owner = new MaintenanceTreeViewModel();
        var reasons = new List<string>();
        owner.DuplicatePresentationChanged += (_, args) => reasons.Add(args.Reason);
        owner.AttachPresentationState(state);
        reasons.Clear();

        var replacement = new List<DuplicateGroup>();
        state.ReplaceDuplicateChartGroups(replacement);

        CollectionAssert.AreEqual(new[] { "bms_files_duplicated_changed" }, reasons);
        Assert.AreSame(replacement, owner.DuplicateChartGroups);

        reasons.Clear();
        state.InvalidateDuplicateChartGroups();

        CollectionAssert.AreEqual(new[] { "bms_files_duplicated_invalidated" }, reasons);
        Assert.IsNull(owner.DuplicateChartGroups);

        // 表示の反映は項目の同期であり、通知理由とは独立に、重複の結果のプロパティ変更を出さない（項目の同期はD1の別ケースで確認）。
        var propertyNames = new List<string>();
        owner.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName!);
        owner.ApplyDuplicateGroupsPresentation();
        CollectionAssert.DoesNotContain(propertyNames, nameof(MaintenanceTreeViewModel.DuplicateChartGroups));
    }

    /// <summary>
    /// 資源・文字コード・ゼロノート・重複の各稼働状態変更を対応するプロパティだけへ伝え、状態間の通知混線を検出する。
    /// </summary>
    [TestMethod]
    public void BusyStateChanges_ProjectPrecisely()
    {
        var state = new MaintenanceTreePresentationStateFake();
        var owner = new MaintenanceTreeViewModel();
        owner.AttachPresentationState(state);
        var propertyNames = new List<string>();
        owner.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName!);

        state.SetHealthStatusWriteLockHeld(false);
        CollectionAssert.AreEqual(
            new[] { nameof(MaintenanceTreeViewModel.IsWriteLockHeldInitializdBMSFilesHealthStatus) },
            propertyNames);
        propertyNames.Clear();

        state.SetEncodingInfoWriteLockHeld(false);
        CollectionAssert.AreEqual(
            new[] { nameof(MaintenanceTreeViewModel.IsWriteLockHeldInitializeBMSFilesEncodingInfo) },
            propertyNames);
        propertyNames.Clear();

        state.SetZeroNoteWriteLockHeld(false);
        CollectionAssert.AreEqual(
            new[] { nameof(MaintenanceTreeViewModel.IsWriteLockHeldInitializeBMSFilesZeroNote) },
            propertyNames);
        propertyNames.Clear();

        state.SetDuplicateGroupsWriteLockHeld(true);
        CollectionAssert.AreEqual(
            new[] { nameof(MaintenanceTreeViewModel.IsWriteLockHeldDuplicateChartGroups) },
            propertyNames);
    }

    /// <summary>
    /// 接続解除で重複集合を消して解除を通知し、旧状態の変更を購読しないことを確認して解除後の通知漏れを検出する。
    /// </summary>
    [TestMethod]
    public void Detach_ClearsDuplicateStateAndStopsOldStateNotifications()
    {
        var state = new MaintenanceTreePresentationStateFake();
        var owner = new MaintenanceTreeViewModel();
        var reasons = new List<string>();
        var propertyNames = new List<string>();
        owner.DuplicatePresentationChanged += (_, args) => reasons.Add(args.Reason);
        owner.AttachPresentationState(state);
        owner.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName!);
        reasons.Clear();
        propertyNames.Clear();

        owner.DetachLibrary();

        Assert.IsNull(owner.DuplicateChartGroups);
        Assert.AreEqual(0, owner.DuplicateTreeItems.Count, "解除では項目を空にします。");
        CollectionAssert.AreEqual(new[] { "maintenance_tree_detached" }, reasons);

        reasons.Clear();
        propertyNames.Clear();
        state.ReplaceDuplicateChartGroups(new List<DuplicateGroup>());
        state.SetHealthStatusWriteLockHeld(false);

        Assert.AreEqual(0, reasons.Count);
        Assert.AreEqual(0, propertyNames.Count);
    }

    /// <summary>
    /// 同じ現在状態への再接続でも一回だけ通知を受け、旧状態の変更を無視して二重購読や旧接続の残存を検出する。
    /// </summary>
    [TestMethod]
    public void Reattach_SubscribeOnceToCurrentStateAndIgnorePreviousState()
    {
        var previousState = new MaintenanceTreePresentationStateFake();
        var currentState = new MaintenanceTreePresentationStateFake();
        var owner = new MaintenanceTreeViewModel();
        var reasons = new List<string>();
        owner.DuplicatePresentationChanged += (_, args) => reasons.Add(args.Reason);

        owner.AttachPresentationState(previousState);
        owner.AttachPresentationState(currentState);
        owner.AttachPresentationState(currentState);
        reasons.Clear();

        currentState.ReplaceDuplicateChartGroups(new List<DuplicateGroup>());
        previousState.ReplaceDuplicateChartGroups(new List<DuplicateGroup>());

        CollectionAssert.AreEqual(new[] { "bms_files_duplicated_changed" }, reasons);
    }

    /// <summary>
    /// 保証：重複ツリーの項目は、結果にも含まれる同じグループのインスタンスを除去・置換せず、消えたグループだけを除き新しいグループを結果の位置へ入れ、
    /// 同期後は結果と各位置で同じインスタンスになる。コレクションは差し替えず、Resetも使わない。同じ内容の同期は何も変えない。
    /// 検出する誤り：全置換（Reset）による展開状態の消失、維持すべき項目の作り直し、結果とずれた順序。
    /// </summary>
    /// <remarks>同じ見出しどうしの並べ替えは Move を許容し、最終の順序だけを判定する。</remarks>
    [TestMethod]
    public void DuplicateTreeItems_SyncKeepsSameInstancesAndFollowsResultOrder()
    {
        DuplicateGroup Group(string folder, string header) => new([], [@"C:\Songs\" + folder]) { Header = header };
        DuplicateGroup a = Group("A", "a"), b = Group("B", "b"), c = Group("C", "c"), d = Group("D", "a0"), c2 = Group("C", "c");
        var state = new MaintenanceTreePresentationStateFake([a, b, c]);
        var owner = new MaintenanceTreeViewModel();
        ObservableCollection<DuplicateGroup> items = owner.DuplicateTreeItems;
        owner.AttachPresentationState(state);
        var changes = new List<NotifyCollectionChangedEventArgs>();
        items.CollectionChanged += (_, args) => changes.Add(args);

        void Sync(params DuplicateGroup[] result)
        {
            changes.Clear();
            state.ReplaceDuplicateChartGroups([.. result]);
            owner.ApplyDuplicateGroupsPresentation();
            Assert.AreSame(items, owner.DuplicateTreeItems, "コレクションのインスタンスは変えません。");
            Assert.AreEqual(result.Length, items.Count);
            for (int i = 0; i < result.Length; i++) { Assert.AreSame(result[i], items[i], "位置" + i); }
            Assert.IsFalse(changes.Any(change => change.Action == NotifyCollectionChangedAction.Reset), "Resetは使いません。");
        }

        CollectionAssert.AreEqual(new[] { a, b, c }, items.ToArray());
        Sync(d, a, c2);
        foreach (NotifyCollectionChangedEventArgs change in changes.Where(change => change.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace))
        {
            CollectionAssert.DoesNotContain(change.OldItems!.Cast<object>().ToArray(), a, "維持するグループの項目を除去・置換しました。");
        }
        Assert.IsTrue(changes.Any(change => change.Action == NotifyCollectionChangedAction.Remove && change.OldItems!.Contains(b)));

        Sync(d, a, c2);
        Assert.AreEqual(0, changes.Count, "同じ内容の同期は何も変えません。");

        Sync(c2, a, d);
        foreach (NotifyCollectionChangedEventArgs change in changes)
        {
            Assert.AreEqual(NotifyCollectionChangedAction.Move, change.Action, "並べ替えだけでは除去・挿入をしません。");
        }
    }

    /// <summary>
    /// 保証：結果が失効しても、接続中ライブラリの重複データ準備が未完了の間は直前の項目を維持し、準備が新しい結果を作って完了したらその結果へ同期し、
    /// 結果を作らずに終わった（失敗・正常終了とも）場合と、準備が進行中でない失効と、解除では項目を空にする。
    /// 検出する誤り：準備中の項目の消去（展開状態の消失）、結果なしの終了後に旧項目が残る誤り、準備の完了を待たない空表示。
    /// </summary>
    /// <remarks>スケジューラーは未指定（その場で同期）で、準備は TaskCompletionSource で制御し、固定時間の待機を使わない。</remarks>
    [TestMethod]
    [DataRow("running")]
    [DataRow("completedWithResult")]
    [DataRow("failedWithoutResult")]
    [DataRow("finishedWithoutResult")]
    [DataRow("noPreparation")]
    [DataRow("detachedWhileRunning")]
    public void DuplicateTreeItems_InvalidationKeepsItemsOnlyWhilePreparationRuns(string condition)
    {
        var a = new DuplicateGroup([], [@"C:\Songs\A"]) { Header = "a" };
        var b = new DuplicateGroup([], [@"C:\Songs\B"]) { Header = "b" };
        var c = new DuplicateGroup([], [@"C:\Songs\C"]) { Header = "c" };
        var state = new MaintenanceTreePresentationStateFake([a, b]);
        var preparation = new TaskCompletionSource();
        var owner = new MaintenanceTreeViewModel
        {
            DuplicatePreparationProvider = () => condition == "noPreparation" ? Task.CompletedTask : preparation.Task
        };
        owner.AttachPresentationState(state);
        CollectionAssert.AreEqual(new[] { a, b }, owner.DuplicateTreeItems.ToArray());

        state.InvalidateDuplicateChartGroups();
        owner.ApplyDuplicateGroupsPresentation();

        if (condition == "noPreparation")
        {
            Assert.AreEqual(0, owner.DuplicateTreeItems.Count, "準備が進行中でない失効は空にします。");
            return;
        }
        CollectionAssert.AreEqual(new[] { a, b }, owner.DuplicateTreeItems.ToArray(), "準備が未完了の間は直前の項目を維持します。");
        switch (condition)
        {
            case "completedWithResult":
                state.ReplaceDuplicateChartGroups([a, c]);
                preparation.SetResult();
                CollectionAssert.AreEqual(new[] { a, c }, owner.DuplicateTreeItems.ToArray(), "準備が作った結果へ同期します。");
                Assert.AreSame(a, owner.DuplicateTreeItems[0]);
                break;
            case "failedWithoutResult":
                preparation.SetException(new InvalidOperationException("preparation failed"));
                _ = preparation.Task.Exception;
                Assert.AreEqual(0, owner.DuplicateTreeItems.Count, "結果を作らず失敗した準備の後は空にします。");
                break;
            case "finishedWithoutResult":
                preparation.SetResult();
                Assert.AreEqual(0, owner.DuplicateTreeItems.Count, "結果を作らず正常終了した準備の後は空にします。");
                break;
            case "detachedWhileRunning":
                owner.DetachLibrary();
                Assert.AreEqual(0, owner.DuplicateTreeItems.Count, "解除では空にします。");
                break;
        }
    }

    /// <summary>
    /// 保証：重複ツリーの子の先頭に置く「統合候補一覧」のノードは、重複検索結果が一件以上あるときだけ現れ、
    /// 未解析・0件・準備が走っていない失効・解除では現れず、準備が走っている失効の間は直前の状態を保つ。
    /// ノードの後ろには、グループ項目を結果の順に並べる。
    /// 検出する誤り：0件でもノードが残る、未解析でノードが現れる、準備中のノードの消去（選択表示の消失）、ノードがグループ項目の後ろに回る。
    /// </summary>
    [TestMethod]
    [DataRow("unanalyzed", false)]
    [DataRow("zeroResult", false)]
    [DataRow("oneResult", true)]
    [DataRow("invalidatedWhilePreparing", true)]
    [DataRow("invalidatedNotPreparing", false)]
    [DataRow("detached", false)]
    public void CandidateListNode_ExistsOnlyWhileResultsExist(string condition, bool expectedNode)
    {
        var a = new DuplicateGroup([], [@"C:\Songs\A"]) { Header = "a" };
        List<DuplicateGroup>? groups = condition switch
        {
            "unanalyzed" => null,
            "zeroResult" => [],
            _ => [a]
        };
        var state = new MaintenanceTreePresentationStateFake(groups);
        var preparation = new TaskCompletionSource();
        var owner = new MaintenanceTreeViewModel
        {
            DuplicatePreparationProvider = () => condition == "invalidatedWhilePreparing" ? preparation.Task : Task.CompletedTask
        };
        owner.AttachPresentationState(state);
        switch (condition)
        {
            case "invalidatedWhilePreparing":
            case "invalidatedNotPreparing":
                state.InvalidateDuplicateChartGroups();
                owner.ApplyDuplicateGroupsPresentation();
                break;
            case "detached":
                owner.DetachLibrary();
                break;
        }

        object[] children = [.. owner.DuplicateTreeChildren.Cast<object>()];
        Assert.AreEqual(expectedNode, children.Contains(owner.DuplicateMergeCandidateListNodeItem), "ノードの有無");
        if (expectedNode)
        {
            Assert.AreSame(owner.DuplicateMergeCandidateListNodeItem, children[0], "ノードは先頭です。");
            CollectionAssert.AreEqual(new object[] { owner.DuplicateMergeCandidateListNodeItem, a }, children);
        }
        else
        {
            Assert.AreEqual(0, children.Count(child => child is DuplicateMergeCandidateListNode));
        }
        preparation.TrySetResult();
    }

    /// <summary>
    /// 保証：結果が一件以上ある間の同期では、ノード項目を除去・置換せず（同じインスタンスを保ち）、グループ項目の変更はノードの分だけ位置を補正した個別の変更として届き、
    /// Resetを使わない。結果が0件になるとノードを一件だけ除去し、再び結果が現れるとノードを先頭へ挿入する。
    /// 検出する誤り：同期のたびにノードを作り直してツリーの展開・選択を失う、位置の補正漏れ、Resetへの置換。
    /// </summary>
    [TestMethod]
    public void CandidateListNode_KeepsInstanceAndRelaysGroupChangesWithoutReset()
    {
        DuplicateGroup Group(string folder) => new([], [@"C:\Songs\" + folder]) { Header = folder };
        DuplicateGroup a = Group("A"), b = Group("B"), c = Group("C");
        var state = new MaintenanceTreePresentationStateFake([a, b]);
        var owner = new MaintenanceTreeViewModel();
        owner.AttachPresentationState(state);
        var changes = new List<NotifyCollectionChangedEventArgs>();
        ((INotifyCollectionChanged)owner.DuplicateTreeChildren).CollectionChanged += (_, args) => changes.Add(args);
        object node = owner.DuplicateMergeCandidateListNodeItem;

        state.ReplaceDuplicateChartGroups([b, c, a]);
        owner.ApplyDuplicateGroupsPresentation();

        Assert.IsFalse(changes.Any(change => change.Action == NotifyCollectionChangedAction.Reset), "Resetは使いません。");
        Assert.IsTrue(changes.All(change => change.NewStartingIndex != 0 && change.OldStartingIndex != 0),
            "グループ項目の変更はノードの分だけ位置を補正し、先頭（ノードの位置）へ届けません。");
        CollectionAssert.AreEqual(new object[] { node, b, c, a }, owner.DuplicateTreeChildren.Cast<object>().ToArray());
        Assert.AreSame(node, owner.DuplicateTreeChildren[0], "ノードは同じインスタンスを保ちます。");

        changes.Clear();
        state.ReplaceDuplicateChartGroups([]);
        owner.ApplyDuplicateGroupsPresentation();
        Assert.AreEqual(0, owner.DuplicateTreeChildren.Count, "0件ではノードも消えます。");
        Assert.IsTrue(changes.Any(change => change.Action == NotifyCollectionChangedAction.Remove && change.OldItems!.Contains(node)),
            "ノードは個別の除去として届きます。");

        changes.Clear();
        state.ReplaceDuplicateChartGroups([a]);
        owner.ApplyDuplicateGroupsPresentation();
        Assert.IsTrue(changes.Any(change => change.Action == NotifyCollectionChangedAction.Add && change.NewItems!.Contains(node) && change.NewStartingIndex == 0),
            "結果が現れるとノードを先頭へ挿入します。");
        CollectionAssert.AreEqual(new object[] { node, a }, owner.DuplicateTreeChildren.Cast<object>().ToArray());
    }

    /// <summary>同名・空名の次群を操作前の所属値で固定し、後の可変Foldersや順序・見出しの変化に置き換えません。末尾と退役した群は対象なしです。</summary>
    [TestMethod]
    [DataRow("same")]
    [DataRow("")]
    public void NextDuplicateSelection_CapturesMembershipBeforeResultsChange(string header)
    {
        var current = new DuplicateGroup([], [@"C:\Songs\Current"]) { Header = header };
        var next = new DuplicateGroup([], [@"C:\Songs\Next"]) { Header = header };
        var other = new DuplicateGroup([], [@"C:\Songs\Other"]) { Header = header };
        var state = new MaintenanceTreePresentationStateFake([current, next, other]);
        using var tree = new MaintenanceTreeViewModel();
        tree.AttachPresentationState(state);
        DuplicateViewContext captured = tree.CaptureNextDuplicateGroupSelection(current);
        Assert.IsNotNull(captured);
        Assert.IsNull(tree.CaptureNextDuplicateGroupSelection(other));
        Assert.IsNull(tree.CaptureNextDuplicateGroupSelection(new([], [@"C:\Songs\Old"])));
        next.Folders.Clear();
        var replacement = new DuplicateGroup([], [@"c:\songs\next"]) { Header = "changed" };
        state.ReplaceDuplicateChartGroups([other, replacement]);
        Assert.AreSame(replacement, captured.ResolveGroup(tree.DuplicateChartGroups));
        state.ReplaceDuplicateChartGroups([other]);
        Assert.IsNull(captured.ResolveGroup(tree.DuplicateChartGroups));
    }

    private sealed class MaintenanceTreePresentationStateFake : IMaintenanceTreePresentationState
    {
        private List<DuplicateGroup> duplicateChartGroups = null!;

        private int duplicateChartGroupsInvalidationVersion;

        internal MaintenanceTreePresentationStateFake(
            List<DuplicateGroup>? duplicateChartGroups = null,
            bool healthStatusWriteLockHeld = true,
            bool encodingInfoWriteLockHeld = true,
            bool zeroNoteWriteLockHeld = true,
            bool duplicateGroupsWriteLockHeld = false)
        {
            this.duplicateChartGroups = duplicateChartGroups!;
            IsWriteLockHeldInitializdBMSFilesHealthStatus = healthStatusWriteLockHeld;
            IsWriteLockHeldInitializeBMSFilesEncodingInfo = encodingInfoWriteLockHeld;
            IsWriteLockHeldInitializeBMSFilesZeroNote = zeroNoteWriteLockHeld;
            IsWriteLockHeldDuplicateChartGroups = duplicateGroupsWriteLockHeld;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public List<DuplicateGroup> DuplicateChartGroups => duplicateChartGroups;

        public int DuplicateChartGroupsInvalidationVersion => duplicateChartGroupsInvalidationVersion;

        public bool IsWriteLockHeldInitializdBMSFilesHealthStatus { get; private set; }

        public bool IsWriteLockHeldInitializeBMSFilesEncodingInfo { get; private set; }

        public bool IsWriteLockHeldInitializeBMSFilesZeroNote { get; private set; }

        public bool IsWriteLockHeldDuplicateChartGroups { get; private set; }

        internal void ReplaceDuplicateChartGroups(List<DuplicateGroup> replacement)
        {
            duplicateChartGroups = replacement;
            RaisePropertyChanged(nameof(DuplicateChartGroups));
        }

        internal void InvalidateDuplicateChartGroups()
        {
            duplicateChartGroups = null!;
            duplicateChartGroupsInvalidationVersion++;
            RaisePropertyChanged(nameof(DuplicateChartGroupsInvalidationVersion));
        }

        internal void SetHealthStatusWriteLockHeld(bool value)
        {
            IsWriteLockHeldInitializdBMSFilesHealthStatus = value;
            RaisePropertyChanged(nameof(IsWriteLockHeldInitializdBMSFilesHealthStatus));
        }

        internal void SetEncodingInfoWriteLockHeld(bool value)
        {
            IsWriteLockHeldInitializeBMSFilesEncodingInfo = value;
            RaisePropertyChanged(nameof(IsWriteLockHeldInitializeBMSFilesEncodingInfo));
        }

        internal void SetZeroNoteWriteLockHeld(bool value)
        {
            IsWriteLockHeldInitializeBMSFilesZeroNote = value;
            RaisePropertyChanged(nameof(IsWriteLockHeldInitializeBMSFilesZeroNote));
        }

        internal void SetDuplicateGroupsWriteLockHeld(bool value)
        {
            IsWriteLockHeldDuplicateChartGroups = value;
            RaisePropertyChanged(nameof(IsWriteLockHeldDuplicateChartGroups));
        }

        private void RaisePropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
