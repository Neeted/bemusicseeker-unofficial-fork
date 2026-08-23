using System;
using System.Collections.Generic;
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

    internal event EventHandler<MaintenanceTreeDuplicatePresentationChangedEventArgs> DuplicatePresentationChanged;

    /// <summary>
    /// Gets the canonical duplicate-group collection exposed to the maintenance tree.
    /// </summary>
    public List<DuplicateGroup> DuplicateChartGroups
        => presentationState?.DuplicateChartGroups;

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
        PublishDuplicatePresentationChanged("maintenance_tree_attached");
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
        PublishDuplicatePresentationChanged("maintenance_tree_detached");
    }

    /// <summary>
    /// Applies the terminal duplicate-list notification after shell suppression decisions.
    /// </summary>
    internal void ApplyDuplicateGroupsPresentation()
    {
        RaisePropertyChanged(nameof(DuplicateChartGroups));
    }

    internal string CaptureNextDuplicateGroupHeader(DuplicateGroup duplicateGroup)
    {
        if (duplicateGroup == null || DuplicateChartGroups == null)
        {
            return null;
        }
        int currentIndex = DuplicateChartGroups.IndexOf(duplicateGroup);
        return currentIndex >= 0 && currentIndex + 1 < DuplicateChartGroups.Count
            ? DuplicateChartGroups[currentIndex + 1].Header
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
}

internal sealed class MaintenanceTreeDuplicatePresentationChangedEventArgs : EventArgs
{
    internal MaintenanceTreeDuplicatePresentationChangedEventArgs(string reason)
    {
        Reason = reason;
    }

    internal string Reason { get; }
}
