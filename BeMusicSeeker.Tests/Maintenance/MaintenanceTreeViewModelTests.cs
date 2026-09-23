using System;
using System.Collections.Generic;
using System.ComponentModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MaintenanceTreeViewModelTests
{
    [TestMethod]
    public void Unattached_UsesCurrentDefaults()
    {
        var owner = new MaintenanceTreeViewModel();

        Assert.IsNull(owner.DuplicateChartGroups);
        Assert.IsTrue(owner.IsWriteLockHeldInitializdBMSFilesHealthStatus);
        Assert.IsTrue(owner.IsWriteLockHeldInitializeBMSFilesEncodingInfo);
        Assert.IsTrue(owner.IsWriteLockHeldInitializeBMSFilesZeroNote);
        Assert.IsFalse(owner.IsWriteLockHeldDuplicateChartGroups);
    }

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

    [TestMethod]
    public void Detach_ReturnsToDefaultsAndStopsOldStateNotifications()
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
        Assert.IsTrue(owner.IsWriteLockHeldInitializdBMSFilesHealthStatus);
        Assert.IsTrue(owner.IsWriteLockHeldInitializeBMSFilesEncodingInfo);
        Assert.IsTrue(owner.IsWriteLockHeldInitializeBMSFilesZeroNote);
        Assert.IsFalse(owner.IsWriteLockHeldDuplicateChartGroups);
        CollectionAssert.AreEqual(new[] { "maintenance_tree_detached" }, reasons);

        reasons.Clear();
        propertyNames.Clear();
        state.ReplaceDuplicateChartGroups(new List<DuplicateGroup>());
        state.SetHealthStatusWriteLockHeld(false);

        Assert.AreEqual(0, reasons.Count);
        Assert.AreEqual(0, propertyNames.Count);
    }

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
