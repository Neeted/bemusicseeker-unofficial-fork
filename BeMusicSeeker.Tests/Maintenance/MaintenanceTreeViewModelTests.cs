using System.Collections.Generic;
using System.ComponentModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
        var duplicateGroups = new List<DuplicateGroup>();
        var state = new MaintenanceTreePresentationStateFake(
            duplicateGroups,
            healthStatusWriteLockHeld: false,
            encodingInfoWriteLockHeld: false,
            zeroNoteWriteLockHeld: false,
            duplicateGroupsWriteLockHeld: true);
        var owner = new MaintenanceTreeViewModel();
        var reasons = new List<string>();
        owner.DuplicatePresentationChanged += (_, args) => reasons.Add(args.Reason);

        owner.AttachPresentationState(state);

        Assert.AreSame(duplicateGroups, owner.DuplicateChartGroups);
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

        var propertyNames = new List<string>();
        owner.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName!);
        owner.ApplyDuplicateGroupsPresentation();
        CollectionAssert.AreEqual(
            new[] { nameof(MaintenanceTreeViewModel.DuplicateChartGroups) },
            propertyNames);
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

    /// <summary>同名・空名の次群を操作前の所属値で固定し、後の可変Foldersや順序・見出しの変化に置き換えません。末尾と退役した群は対象なしです。</summary>
    [DataTestMethod]
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
