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

    private static DuplicateGroup SplitGroup(string folder) => new([], [@"C:\Songs\" + folder]) { Header = folder };

    /// <summary>
    /// 保証：分裂配置ツリーの子は、結果の順のグループ項目だけで、統合候補一覧のノードを置かない（結果が一件以上あっても）。結果なし・0件・解除では子が空になる。
    /// 検出する誤り：重複のツリーのノードの混入、結果の順とずれた並び、結果なしでの旧項目の残存。
    /// </summary>
    [TestMethod]
    public void SplitPlacementTreeChildren_AreOnlyGroupsInResultOrderWithoutCandidateListNode()
    {
        DuplicateGroup a = SplitGroup("A"), b = SplitGroup("B");
        var state = new MaintenanceTreePresentationStateFake();
        var owner = new MaintenanceTreeViewModel();
        owner.AttachPresentationState(state);
        Assert.AreEqual(0, owner.SplitPlacementTreeChildren.Count, "結果なし（null）は空");

        state.ReplaceSplitPlacementGroups([b, a]);
        owner.ApplySplitPlacementPresentation();
        CollectionAssert.AreEqual(new object[] { b, a }, owner.SplitPlacementTreeChildren.Cast<object>().ToArray());
        Assert.AreEqual(0, owner.SplitPlacementTreeChildren.Cast<object>().Count(child => child is DuplicateMergeCandidateListNode));

        state.ReplaceSplitPlacementGroups([]);
        owner.ApplySplitPlacementPresentation();
        Assert.AreEqual(0, owner.SplitPlacementTreeChildren.Count, "0件は空");

        state.ReplaceSplitPlacementGroups([a]);
        owner.ApplySplitPlacementPresentation();
        owner.DetachLibrary();
        Assert.AreEqual(0, owner.SplitPlacementTreeChildren.Count, "解除では空");
    }

    /// <summary>
    /// 保証：同じ結果への再同期や、結果にも含まれるグループを保つ同期では、そのグループの項目を除去・置換せず、Resetを使わない。
    /// 検出する誤り：同期のたびの全置換によるツリーの展開状態の消失。
    /// </summary>
    [TestMethod]
    public void SplitPlacementTreeChildren_SyncKeepsSameInstancesWithoutReset()
    {
        DuplicateGroup a = SplitGroup("A"), b = SplitGroup("B"), c = SplitGroup("C");
        var state = new MaintenanceTreePresentationStateFake();
        state.ReplaceSplitPlacementGroups([a, b]);
        var owner = new MaintenanceTreeViewModel();
        owner.AttachPresentationState(state);
        var changes = new List<NotifyCollectionChangedEventArgs>();
        ((INotifyCollectionChanged)owner.SplitPlacementTreeChildren).CollectionChanged += (_, args) => changes.Add(args);

        owner.ApplySplitPlacementPresentation();
        Assert.AreEqual(0, changes.Count, "同じ結果への再同期は何も変えません。");

        state.ReplaceSplitPlacementGroups([a, c]);
        owner.ApplySplitPlacementPresentation();

        Assert.IsFalse(changes.Any(change => change.Action == NotifyCollectionChangedAction.Reset), "Resetは使いません。");
        foreach (NotifyCollectionChangedEventArgs change in changes.Where(change => change.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace))
        {
            CollectionAssert.DoesNotContain(change.OldItems!.Cast<object>().ToArray(), a, "維持するグループの項目を除去・置換しました。");
        }
        Assert.AreSame(a, owner.SplitPlacementTreeChildren[0]);
        CollectionAssert.AreEqual(new object[] { a, c }, owner.SplitPlacementTreeChildren.Cast<object>().ToArray());
    }

    /// <summary>
    /// 保証：分裂配置の結果の失効で、重複のデータ準備が走っていても、子をすぐ空にする（準備中に直前の項目を保つ動作は重複だけのもの）。
    /// 検出する誤り：重複の準備の動作を分裂配置へ持ち込み、失効した古い項目を表示し続ける。
    /// </summary>
    [TestMethod]
    public void SplitPlacementTreeChildren_InvalidationEmptiesChildrenImmediately()
    {
        DuplicateGroup a = SplitGroup("A");
        var state = new MaintenanceTreePresentationStateFake();
        state.ReplaceSplitPlacementGroups([a]);
        var preparation = new TaskCompletionSource();
        var owner = new MaintenanceTreeViewModel { DuplicatePreparationProvider = () => preparation.Task };
        owner.AttachPresentationState(state);
        CollectionAssert.AreEqual(new object[] { a }, owner.SplitPlacementTreeChildren.Cast<object>().ToArray());

        state.ReplaceSplitPlacementGroups(null);
        owner.ApplySplitPlacementPresentation();

        Assert.AreEqual(0, owner.SplitPlacementTreeChildren.Count);
        preparation.TrySetResult();
    }

    /// <summary>
    /// 保証：失効の通知が、新しい結果の格納より後に（UIスレッドの配送が遅れて）届いても、通知を受けた時点ではなく同期の時点の現在の結果を読むため、
    /// 新しい結果を表示したままにする。検出する誤り：通知の理由（失効）で子を空にし、直後に格納された新しい結果を消す。
    /// </summary>
    [TestMethod]
    public void SplitPlacementTreeChildren_LateInvalidationNotificationKeepsNewResult()
    {
        DuplicateGroup a = SplitGroup("A"), c = SplitGroup("C");
        var state = new MaintenanceTreePresentationStateFake();
        state.ReplaceSplitPlacementGroups([a]);
        var scheduler = new QueueingUiScheduler();
        var owner = new MaintenanceTreeViewModel(scheduler);
        owner.AttachPresentationState(state);
        scheduler.RunPending();
        int notifications = 0;
        owner.SplitPlacementPresentationChanged += (_, _) => notifications++;

        state.ReplaceSplitPlacementGroupsWithoutNotification(null);
        state.ReplaceSplitPlacementGroupsWithoutNotification([c]);
        state.NotifySplitPlacementGroupsChanged();
        owner.ApplySplitPlacementPresentation();
        scheduler.RunPending();

        Assert.AreEqual(1, notifications);
        CollectionAssert.AreEqual(new object[] { c }, owner.SplitPlacementTreeChildren.Cast<object>().ToArray());
    }

    /// <summary>
    /// 保証：ワーカースレッドからの反映要求は項目を変更せずUIスレッドへ配送して待たずに戻り、項目の変更はUIスレッド（配送先）でだけ行う。
    /// 検出する誤り：ワーカースレッドでの項目の変更、UIの完了の同期的な待機。
    /// </summary>
    [TestMethod]
    public void SplitPlacementTreeChildren_MutatesOnlyOnUiThreadAndDoesNotWaitForIt()
    {
        DuplicateGroup a = SplitGroup("A");
        var state = new MaintenanceTreePresentationStateFake();
        var scheduler = new QueueingUiScheduler();
        var owner = new MaintenanceTreeViewModel(scheduler);
        owner.AttachPresentationState(state);
        scheduler.RunPending();
        var mutationThreads = new List<int>();
        ((INotifyCollectionChanged)owner.SplitPlacementTreeChildren).CollectionChanged += (_, _) => mutationThreads.Add(Environment.CurrentManagedThreadId);
        state.ReplaceSplitPlacementGroups([a]);

        Task.Run(owner.ApplySplitPlacementPresentation).Wait();

        Assert.AreEqual(0, owner.SplitPlacementTreeChildren.Count, "ワーカースレッドでは項目を変更しません。");
        Assert.AreEqual(1, scheduler.PendingCount, "UIスレッドへ配送します。");
        scheduler.RunPending();
        CollectionAssert.AreEqual(new object[] { a }, owner.SplitPlacementTreeChildren.Cast<object>().ToArray());
        CollectionAssert.AreEqual(new[] { Environment.CurrentManagedThreadId }, mutationThreads);
    }

    /// <summary>
    /// 保証：重複と分裂配置の結果の変化・失効・（重複の）準備中は互いの子・通知へ影響しない。
    /// 検出する誤り：二つのツリーが項目や通知を共有し、一方の操作でもう一方の表示が変わる。
    /// </summary>
    [TestMethod]
    public void SplitPlacementAndDuplicateTrees_AreIndependent()
    {
        DuplicateGroup d = SplitGroup("D"), s = SplitGroup("S");
        var state = new MaintenanceTreePresentationStateFake([d]);
        state.ReplaceSplitPlacementGroups([s]);
        var preparation = new TaskCompletionSource();
        var owner = new MaintenanceTreeViewModel { DuplicatePreparationProvider = () => preparation.Task };
        owner.AttachPresentationState(state);
        var duplicateReasons = new List<string>();
        var splitReasons = new List<string>();
        owner.DuplicatePresentationChanged += (_, args) => duplicateReasons.Add(args.Reason);
        owner.SplitPlacementPresentationChanged += (_, args) => splitReasons.Add(args.Reason);

        state.InvalidateDuplicateChartGroups();
        owner.ApplyDuplicateGroupsPresentation();
        state.ReplaceDuplicateChartGroups([]);
        owner.ApplyDuplicateGroupsPresentation();
        CollectionAssert.AreEqual(new object[] { s }, owner.SplitPlacementTreeChildren.Cast<object>().ToArray(), "重複の変化・失効・準備中で分裂配置の子は変わらない");
        Assert.AreEqual(0, splitReasons.Count, "重複の変化は分裂配置の通知を出さない");

        state.ReplaceDuplicateChartGroups([d]);
        owner.ApplyDuplicateGroupsPresentation();
        duplicateReasons.Clear();
        state.ReplaceSplitPlacementGroups(null);
        owner.ApplySplitPlacementPresentation();
        Assert.AreEqual(0, owner.SplitPlacementTreeChildren.Count);
        CollectionAssert.AreEqual(new[] { d }, owner.DuplicateTreeItems.ToArray(), "分裂配置の失効で重複の項目は変わらない");
        CollectionAssert.AreEqual(new[] { "split_placement_groups_changed" }, splitReasons);
        Assert.AreEqual(0, duplicateReasons.Count, "分裂配置の変化は重複の通知を出さない");
        preparation.TrySetResult();
    }

    /// <summary>配送を溜めて、テストスレッドを「UIスレッド」として実行する代替です。溜めた間は呼出しを待たずに戻ります。</summary>
    private sealed class QueueingUiScheduler : IUiScheduler
    {
        private readonly Queue<Action> pending = new();

        internal int PendingCount { get { lock (pending) { return pending.Count; } } }

        internal void RunPending()
        {
            while (true)
            {
                Action? action;
                lock (pending) { if (!pending.TryDequeue(out action)) { return; } }
                action();
            }
        }

        public bool IsAvailable => true;

        public bool CanExecuteInline => false;

        /// <summary>呼出し元を常に「UIスレッドではない」とし、反映要求が必ず配送の列に入るようにします。</summary>
        public bool CheckAccess() => false;

        public IUiScheduledOperation Schedule(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            lock (pending) { pending.Enqueue(action); }
            return new Operation();
        }

        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => throw new NotSupportedException();

        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal) => throw new NotSupportedException();

        public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => throw new NotSupportedException();

        public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal) => throw new NotSupportedException();

        private sealed class Operation : IUiScheduledOperation
        {
            public bool IsAccepted => true;
            public bool IsCompleted => false;
            public bool IsAborted => false;
            public string RejectionReason => string.Empty;
            public Task Completion => Task.CompletedTask;
            public void Abort() { }
        }
    }

    private sealed class MaintenanceTreePresentationStateFake : IMaintenanceTreePresentationState
    {
        private List<DuplicateGroup> duplicateChartGroups = null!;

        private int duplicateChartGroupsInvalidationVersion;

        private List<DuplicateGroup>? splitPlacementGroups;

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

        public List<DuplicateGroup>? SplitPlacementGroups => splitPlacementGroups;

        public bool IsWriteLockHeldInitializdBMSFilesHealthStatus { get; private set; }

        public bool IsWriteLockHeldInitializeBMSFilesEncodingInfo { get; private set; }

        public bool IsWriteLockHeldInitializeBMSFilesZeroNote { get; private set; }

        public bool IsWriteLockHeldDuplicateChartGroups { get; private set; }

        internal void ReplaceDuplicateChartGroups(List<DuplicateGroup> replacement)
        {
            duplicateChartGroups = replacement;
            RaisePropertyChanged(nameof(DuplicateChartGroups));
        }

        /// <summary>分裂配置の結果を差し替え、変更を通知します。</summary>
        internal void ReplaceSplitPlacementGroups(List<DuplicateGroup>? replacement)
        {
            splitPlacementGroups = replacement;
            RaisePropertyChanged(nameof(SplitPlacementGroups));
        }

        /// <summary>分裂配置の結果を、通知を送らずに差し替えます。通知が格納より遅れて届く状況を作ります。</summary>
        internal void ReplaceSplitPlacementGroupsWithoutNotification(List<DuplicateGroup>? replacement) => splitPlacementGroups = replacement;

        /// <summary>分裂配置の変更の通知だけを送ります。</summary>
        internal void NotifySplitPlacementGroupsChanged() => RaisePropertyChanged(nameof(SplitPlacementGroups));

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
