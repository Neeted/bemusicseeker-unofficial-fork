using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using BeMusicSeeker.Models;
using Livet;
using Livet.EventListeners;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns the installed and pending package tree presentation sources.
/// </summary>
public sealed class InstallTreeViewModel : ViewModel
{
    private BMSLibrary library;

    private PropertyChangedEventListener libraryPropertyChangedListener;

    private CollectionChangedEventListener installedPackagesListener;

    private CollectionChangedEventListener pendingPackagesListener;

    internal event EventHandler<InstallTreePresentationChangedEventArgs> PresentationChanged;

    /// <summary>
    /// Gets the installed package collection exposed to the tree view.
    /// </summary>
    public ObservableCollection<ChartPackage> ChartPackagesInstalled
        => library?.ChartPackagesInstalled;

    /// <summary>
    /// Gets the pending package collection exposed to the tree view.
    /// </summary>
    public IReadOnlyCollection<ChartPackage> ChartPackagesPending
        => library?.ChartPackagesPending;

    /// <summary>
    /// Gets whether the pending-package mutation lock is held.
    /// </summary>
    public bool IsWriteLockHeldPendingInstallCharts
        => library?.IsWriteLockHeldPendingInstallCharts ?? true;

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
            () => attachedLibrary.ChartPackagesInstalled,
            delegate
            {
                RebindInstalledPackagesListener();
                PublishPresentationChanged(InstallTreePresentationSection.Installed);
            });
        libraryPropertyChangedListener.RegisterHandler(
            () => attachedLibrary.ChartPackagesPending,
            delegate
            {
                RebindPendingPackagesListener();
                PublishPresentationChanged(InstallTreePresentationSection.Pending);
            });
        libraryPropertyChangedListener.RegisterHandler(
            () => attachedLibrary.IsWriteLockHeldPendingInstallCharts,
            delegate
            {
                RaisePropertyChanged(nameof(IsWriteLockHeldPendingInstallCharts));
            });
        RebindInstalledPackagesListener();
        RebindPendingPackagesListener();
        RaisePropertyChanged(nameof(IsWriteLockHeldPendingInstallCharts));
        PublishPresentationChanged(InstallTreePresentationSection.Installed | InstallTreePresentationSection.Pending);
    }

    internal void DetachLibrary()
    {
        DisposeListener(ref installedPackagesListener);
        DisposeListener(ref pendingPackagesListener);
        if (libraryPropertyChangedListener is IDisposable propertyChangedDisposable)
        {
            propertyChangedDisposable.Dispose();
        }
        libraryPropertyChangedListener = null;
        if (library == null)
        {
            return;
        }
        library = null;
        RaisePropertyChanged(nameof(IsWriteLockHeldPendingInstallCharts));
        PublishPresentationChanged(InstallTreePresentationSection.Installed | InstallTreePresentationSection.Pending);
    }

    /// <summary>
    /// Applies the terminal property notifications after shell suppression decisions.
    /// </summary>
    internal void ApplyPresentation(InstallTreePresentationSection sections)
    {
        if ((sections & InstallTreePresentationSection.Installed) != 0)
        {
            RaisePropertyChanged(nameof(ChartPackagesInstalled));
        }
        if ((sections & InstallTreePresentationSection.Pending) != 0)
        {
            RaisePropertyChanged(nameof(ChartPackagesPending));
        }
    }

    private void RebindInstalledPackagesListener()
    {
        DisposeListener(ref installedPackagesListener);
        if (library?.ChartPackagesInstalled == null)
        {
            return;
        }
        installedPackagesListener = new CollectionChangedEventListener(library.ChartPackagesInstalled);
        installedPackagesListener.RegisterHandler(
            (_, _) => PublishPresentationChanged(InstallTreePresentationSection.Installed));
    }

    private void RebindPendingPackagesListener()
    {
        DisposeListener(ref pendingPackagesListener);
        if (library?.PendingPackagesView == null)
        {
            return;
        }
        pendingPackagesListener = new CollectionChangedEventListener(library.PendingPackagesView);
        pendingPackagesListener.RegisterHandler(
            (_, _) => PublishPresentationChanged(InstallTreePresentationSection.Pending));
    }

    private void PublishPresentationChanged(InstallTreePresentationSection sections)
    {
        PresentationChanged?.Invoke(
            this,
            new InstallTreePresentationChangedEventArgs(sections));
    }

    private static void DisposeListener<T>(ref T listener)
        where T : class
    {
        if (listener is IDisposable disposable)
        {
            disposable.Dispose();
        }
        listener = null;
    }
}

[Flags]
internal enum InstallTreePresentationSection
{
    None = 0,
    Installed = 1,
    Pending = 2
}

internal sealed class InstallTreePresentationChangedEventArgs : EventArgs
{
    internal InstallTreePresentationChangedEventArgs(InstallTreePresentationSection sections)
    {
        Sections = sections;
    }

    internal InstallTreePresentationSection Sections { get; }
}
