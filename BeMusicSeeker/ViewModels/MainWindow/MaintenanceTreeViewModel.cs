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
    private BMSLibrary library;

    private PropertyChangedEventListener libraryPropertyChangedListener;

    internal event EventHandler<MaintenanceTreeDuplicatePresentationChangedEventArgs> DuplicatePresentationChanged;

    /// <summary>
    /// Gets the canonical duplicate-group collection exposed to the maintenance tree.
    /// </summary>
    public List<DuplicateGroup> DuplicateChartGroups
        => library?.DuplicateChartGroups;

    /// <summary>
    /// Gets whether health maintenance initialization is still holding its write lock.
    /// </summary>
    public bool IsWriteLockHeldInitializdBMSFilesHealthStatus
        => library?.IsWriteLockHeldInitializdBMSFilesHealthStatus ?? true;

    /// <summary>
    /// Gets whether encoding maintenance initialization is still holding its write lock.
    /// </summary>
    public bool IsWriteLockHeldInitializeBMSFilesEncodingInfo
        => library?.IsWriteLockHeldInitializeBMSFilesEncodingInfo ?? true;

    /// <summary>
    /// Gets whether zero-note maintenance initialization is still holding its write lock.
    /// </summary>
    public bool IsWriteLockHeldInitializeBMSFilesZeroNote
        => library?.IsWriteLockHeldInitializeBMSFilesZeroNote ?? true;

    /// <summary>
    /// Gets whether duplicate-group maintenance is still holding its write lock.
    /// </summary>
    public bool IsWriteLockHeldDuplicateChartGroups
        => library?.IsWriteLockHeldDuplicateChartGroups ?? false;

    internal void AttachLibrary(BMSLibrary value)
    {
        if (ReferenceEquals(library, value))
        {
            return;
        }

        DetachLibrary();
        library = value ?? throw new ArgumentNullException(nameof(value));
        BMSLibrary attachedLibrary = library;
        libraryPropertyChangedListener = new PropertyChangedEventListener(attachedLibrary);
        libraryPropertyChangedListener.RegisterHandler(
            () => attachedLibrary.DuplicateChartGroups,
            delegate
            {
                PublishDuplicatePresentationChanged("bms_files_duplicated_changed");
            });
        libraryPropertyChangedListener.RegisterHandler(
            () => attachedLibrary.DuplicateChartGroupsInvalidationVersion,
            delegate
            {
                PublishDuplicatePresentationChanged("bms_files_duplicated_invalidated");
            });
        libraryPropertyChangedListener.RegisterHandler(
            () => attachedLibrary.IsWriteLockHeldInitializdBMSFilesHealthStatus,
            delegate
            {
                RaisePropertyChanged(nameof(IsWriteLockHeldInitializdBMSFilesHealthStatus));
            });
        libraryPropertyChangedListener.RegisterHandler(
            () => attachedLibrary.IsWriteLockHeldInitializeBMSFilesEncodingInfo,
            delegate
            {
                RaisePropertyChanged(nameof(IsWriteLockHeldInitializeBMSFilesEncodingInfo));
            });
        libraryPropertyChangedListener.RegisterHandler(
            () => attachedLibrary.IsWriteLockHeldInitializeBMSFilesZeroNote,
            delegate
            {
                RaisePropertyChanged(nameof(IsWriteLockHeldInitializeBMSFilesZeroNote));
            });
        libraryPropertyChangedListener.RegisterHandler(
            () => attachedLibrary.IsWriteLockHeldDuplicateChartGroups,
            delegate
            {
                RaisePropertyChanged(nameof(IsWriteLockHeldDuplicateChartGroups));
            });
        RaiseBusyStateProperties();
        PublishDuplicatePresentationChanged("maintenance_tree_attached");
    }

    internal void DetachLibrary()
    {
        if (libraryPropertyChangedListener is IDisposable disposable)
        {
            disposable.Dispose();
        }
        libraryPropertyChangedListener = null;
        if (library == null)
        {
            return;
        }
        library = null;
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
