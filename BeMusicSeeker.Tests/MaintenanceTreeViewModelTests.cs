using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MaintenanceTreeViewModelTests
{
    [TestMethod]
    public void Attach_ExposesCanonicalMaintenanceStateAndPublishesInitialFact()
    {
        WithLibrary(delegate (BMSLibrary library)
        {
            var owner = new MaintenanceTreeViewModel();
            var reasons = new List<string>();
            owner.DuplicatePresentationChanged += (_, args) => reasons.Add(args.Reason);

            owner.AttachLibrary(library);

            Assert.IsNull(owner.DuplicateChartGroups);
            Assert.AreEqual(library.IsWriteLockHeldInitializdBMSFilesHealthStatus, owner.IsWriteLockHeldInitializdBMSFilesHealthStatus);
            Assert.AreEqual(library.IsWriteLockHeldInitializeBMSFilesEncodingInfo, owner.IsWriteLockHeldInitializeBMSFilesEncodingInfo);
            Assert.AreEqual(library.IsWriteLockHeldInitializeBMSFilesZeroNote, owner.IsWriteLockHeldInitializeBMSFilesZeroNote);
            Assert.AreEqual(library.IsWriteLockHeldDuplicateChartGroups, owner.IsWriteLockHeldDuplicateChartGroups);
            CollectionAssert.AreEqual(new[] { "maintenance_tree_attached" }, reasons);
        });
    }

    [TestMethod]
    public void DuplicateReplacementAndInvalidation_PublishDistinctFactsAndApplyOnlyDuplicateProperty()
    {
        WithLibrary(delegate (BMSLibrary library)
        {
            var owner = new MaintenanceTreeViewModel();
            var reasons = new List<string>();
            using var replacementSignal = new ManualResetEventSlim();
            owner.DuplicatePresentationChanged += (_, args) =>
            {
                lock (reasons)
                {
                    reasons.Add(args.Reason);
                }
                replacementSignal.Set();
            };
            owner.AttachLibrary(library);
            reasons.Clear();
            replacementSignal.Reset();

            var replacement = new List<DuplicateGroup>();
            library.DuplicateChartGroups = replacement;
            Assert.IsTrue(replacementSignal.Wait(TimeSpan.FromSeconds(5)));
            CollectionAssert.AreEqual(new[] { "bms_files_duplicated_changed" }, reasons);
            Assert.AreSame(replacement, owner.DuplicateChartGroups);

            reasons.Clear();
            replacementSignal.Reset();
            library.BMSFiles = [];
            Assert.IsTrue(replacementSignal.Wait(TimeSpan.FromSeconds(5)));
            CollectionAssert.AreEqual(new[] { "bms_files_duplicated_invalidated" }, reasons);

            var propertyNames = new List<string>();
            owner.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName);
            owner.ApplyDuplicateGroupsPresentation();
            CollectionAssert.AreEqual(
                new[] { nameof(MaintenanceTreeViewModel.DuplicateChartGroups) },
                propertyNames);
        });
    }

    [TestMethod]
    public void BusyStateChanges_NotifyOnlyTheirChildProperties()
    {
        WithLibrary(delegate (BMSLibrary library)
        {
            var owner = new MaintenanceTreeViewModel();
            owner.AttachLibrary(library);
            var propertyNames = new List<string>();
            owner.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName);

            library.IsWriteLockHeldInitializdBMSFilesHealthStatus = false;
            CollectionAssert.AreEqual(
                new[] { nameof(MaintenanceTreeViewModel.IsWriteLockHeldInitializdBMSFilesHealthStatus) },
                propertyNames);
            propertyNames.Clear();
            library.IsWriteLockHeldInitializeBMSFilesEncodingInfo = false;
            CollectionAssert.AreEqual(
                new[] { nameof(MaintenanceTreeViewModel.IsWriteLockHeldInitializeBMSFilesEncodingInfo) },
                propertyNames);
            propertyNames.Clear();
            library.IsWriteLockHeldInitializeBMSFilesZeroNote = false;
            CollectionAssert.AreEqual(
                new[] { nameof(MaintenanceTreeViewModel.IsWriteLockHeldInitializeBMSFilesZeroNote) },
                propertyNames);
        });
    }

    [TestMethod]
    public void Detach_ReturnsToUnattachedDefaultsAndStopsOldLibraryNotifications()
    {
        WithLibrary(delegate (BMSLibrary library)
        {
            var owner = new MaintenanceTreeViewModel();
            owner.AttachLibrary(library);
            var propertyNames = new List<string>();
            owner.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName);

            owner.DetachLibrary();

            Assert.IsNull(owner.DuplicateChartGroups);
            Assert.IsTrue(owner.IsWriteLockHeldInitializdBMSFilesHealthStatus);
            Assert.IsTrue(owner.IsWriteLockHeldInitializeBMSFilesEncodingInfo);
            Assert.IsTrue(owner.IsWriteLockHeldInitializeBMSFilesZeroNote);
            Assert.IsFalse(owner.IsWriteLockHeldDuplicateChartGroups);
            propertyNames.Clear();
            library.IsWriteLockHeldInitializdBMSFilesHealthStatus = false;
            Assert.AreEqual(0, propertyNames.Count);
        });
    }

    private static void WithLibrary(Action<BMSLibrary> action)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_MaintenanceTree_" + Guid.NewGuid().ToString("N"));
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
            action(new BMSLibrary(songDbPath));
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
