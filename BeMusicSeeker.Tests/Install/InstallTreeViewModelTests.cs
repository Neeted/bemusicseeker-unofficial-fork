using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Livet;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class InstallTreeViewModelTests
{
    [TestMethod]
    public void Attach_ExposesCanonicalCollectionsAndPublishesSections()
    {
        WithLibrary(delegate (BMSLibrary library)
        {
            var owner = new InstallTreeViewModel();
            var sections = new List<InstallTreePresentationSection>();
            owner.PresentationChanged += (_, args) => sections.Add(args.Sections);

            owner.AttachLibrary(library);

            Assert.AreSame(library.ChartPackagesInstalled, owner.ChartPackagesInstalled);
            Assert.AreSame(library.ChartPackagesPending, owner.ChartPackagesPending);
            Assert.AreEqual(library.IsWriteLockHeldPendingInstallCharts, owner.IsWriteLockHeldPendingInstallCharts);
            Assert.AreEqual(
                InstallTreePresentationSection.Installed | InstallTreePresentationSection.Pending,
                sections.Single());
        });
    }

    [TestMethod]
    public void PendingCollection_IsReadOnlyAtLibraryBoundary()
    {
        WithLibrary(delegate (BMSLibrary library)
        {
            Assert.IsInstanceOfType(
                library.ChartPackagesPending,
                typeof(ReadOnlyObservableCollection<ChartPackage>));
            Assert.IsFalse(library.ChartPackagesPending is ObservableCollection<ChartPackage>);
        });
    }

    [TestMethod]
    public void CollectionChanges_PublishOnlyTheChangedSectionAndReplacementRebinds()
    {
        WithLibrary(delegate (BMSLibrary library)
        {
            var owner = new InstallTreeViewModel();
            var sections = new List<InstallTreePresentationSection>();
            owner.PresentationChanged += (_, args) => sections.Add(args.Sections);
            ObservableCollection<ChartPackage> pending = CreatePackageCollection([]);
            library.ChartPackagesPending = pending;
            owner.AttachLibrary(library);
            sections.Clear();

            ObservableCollection<ChartPackage> installed = library.ChartPackagesInstalled;
            installed.Add(new ChartPackage { path = "installed" });
            Assert.AreEqual(InstallTreePresentationSection.Installed, sections.Single());

            sections.Clear();
            pending.Add(new ChartPackage { path = "pending" });
            Assert.AreEqual(InstallTreePresentationSection.Pending, sections.Single());

            sections.Clear();
            ObservableCollection<ChartPackage> replacement = CreatePackageCollection([]);
            library.ChartPackagesInstalled = replacement;
            Assert.AreEqual(InstallTreePresentationSection.Installed, sections.Single());
            sections.Clear();

            installed.Add(new ChartPackage { path = "old" });
            Assert.AreEqual(0, sections.Count);
            replacement.Add(new ChartPackage { path = "new" });
            Assert.AreEqual(InstallTreePresentationSection.Installed, sections.Single());
        });
    }

    [TestMethod]
    public void ApplyPresentation_NotifiesOnlyRequestedSections()
    {
        WithLibrary(delegate (BMSLibrary library)
        {
            var owner = new InstallTreeViewModel();
            owner.AttachLibrary(library);
            var propertyNames = new List<string>();
            owner.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName!);

            owner.ApplyPresentation(InstallTreePresentationSection.Installed);
            CollectionAssert.AreEqual(
                new[] { nameof(InstallTreeViewModel.ChartPackagesInstalled) },
                propertyNames);

            propertyNames.Clear();
            owner.ApplyPresentation(
                InstallTreePresentationSection.Installed | InstallTreePresentationSection.Pending);
            CollectionAssert.AreEqual(
                new[]
                {
                    nameof(InstallTreeViewModel.ChartPackagesInstalled),
                    nameof(InstallTreeViewModel.ChartPackagesPending)
                },
                propertyNames);
        });
    }

    [TestMethod]
    public void Detach_ClearsPresentationAndStopsOldCollectionNotifications()
    {
        WithLibrary(delegate (BMSLibrary library)
        {
            var owner = new InstallTreeViewModel();
            var sections = new List<InstallTreePresentationSection>();
            owner.PresentationChanged += (_, args) => sections.Add(args.Sections);
            ObservableCollection<ChartPackage> pending = CreatePackageCollection([]);
            library.ChartPackagesPending = pending;
            owner.AttachLibrary(library);
            ObservableCollection<ChartPackage> oldInstalled = library.ChartPackagesInstalled;
            ObservableCollection<ChartPackage> oldPending = pending;
            sections.Clear();

            owner.DetachLibrary();

            Assert.IsNull(owner.ChartPackagesInstalled);
            Assert.IsNull(owner.ChartPackagesPending);
            Assert.IsTrue(owner.IsWriteLockHeldPendingInstallCharts);
            sections.Clear();
            oldInstalled.Add(new ChartPackage { path = "old-installed" });
            oldPending.Add(new ChartPackage { path = "old-pending" });
            Assert.AreEqual(0, sections.Count);
        });
    }

    private static ObservableCollection<ChartPackage> CreatePackageCollection(IEnumerable<ChartPackage> packages)
    {
        return new ObservableCollection<ChartPackage>(packages ?? []);
    }

    private static void WithLibrary(Action<BMSLibrary> action)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_InstallTree_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
            }
            var library = new TestBmsLibrary(songDbPath)
            {
                ChartPackagesInstalled = CreatePackageCollection([]),
                ChartPackagesPending = CreatePackageCollection([])
            };
            action(library);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }
}
