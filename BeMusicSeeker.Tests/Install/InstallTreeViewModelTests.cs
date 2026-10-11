using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class InstallTreeViewModelTests
{
    /// <summary>library接続で両区画の現在collectionと保留書込状態を公開し、初回区画通知や参照接続の欠落を検出します。</summary>
    /// <remarks>library窓口の公開collectionと購読を接続します。実WindowとDB保存は使わず、空のDBファイルは構築条件だけを満たします。</remarks>
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

    /// <summary>公開された保留collectionをBCLの読取り専用observable wrapperとして保ち、consumerへ構造変更可能なcollectionを公開する誤りを検出します。</summary>
    /// <remarks>architectureの読取りモデル責務をcompiled境界で検査します。個別Addの拒否や所有者の更新は観測せず、接続・通知・置換・detachは他ケースが担います。</remarks>
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

    /// <summary>追加・除去・resetを対応区画だけへ通知し、collection置換後の旧購読残留と新購読の欠落を検出します。</summary>
    /// <remarks>library窓口の公開collectionと購読を接続します。実WindowとDB保存は使わず、空のDBファイルは構築条件だけを満たします。</remarks>
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
            sections.Clear();
            replacement.RemoveAt(0);
            Assert.AreEqual(InstallTreePresentationSection.Installed, sections.Single());
            replacement.Add(new ChartPackage { path = "reset" });
            sections.Clear();
            replacement.Clear();
            Assert.AreEqual(InstallTreePresentationSection.Installed, sections.Single());
            sections.Clear();
            installed.Clear();
            Assert.AreEqual(0, sections.Count);
            owner.DetachLibrary();
        });
    }

    /// <summary>指定区画だけのproperty更新を通知し、他区画の余計な更新や複合要求の通知漏れを検出します。</summary>
    /// <remarks>library窓口の公開collectionと購読を接続します。実WindowとDB保存は使わず、空のDBファイルは構築条件だけを満たします。</remarks>
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

    /// <summary>detachで公開値を解除して保留編集を抑止し、旧collection変更から再び通知が届く誤りを検出します。</summary>
    /// <remarks>library窓口の公開collectionと購読を接続します。実WindowとDB保存は使わず、空のDBファイルは構築条件だけを満たします。</remarks>
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
