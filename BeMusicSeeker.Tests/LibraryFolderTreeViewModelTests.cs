using System;
using System.Collections.Generic;
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
public sealed class LibraryFolderTreeViewModelTests
{
    [TestMethod]
    public void AttachedLibrary_ExposesSortedDistinctFoldersAndInvalidatesAfterSearchRootChange()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_LibraryFolderTree_" + Guid.NewGuid().ToString("N"));
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        string firstRoot = Path.Combine(tempRootPath, "Zeta");
        string secondRoot = Path.Combine(tempRootPath, "Alpha");
        string replacementRoot = Path.Combine(tempRootPath, "Beta");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        Directory.CreateDirectory(replacementRoot);
        File.WriteAllBytes(songDbPath, []);

        try
        {
            Dispatcher previousDispatcher = DispatcherHelper.UIDispatcher;
            DispatcherHelper.UIDispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.CreateTable<LR2SongDB.song>();
                    songDb.CreateTable<LR2SongDB.folder>();
                    songDb.CreateTable<LR2SongDBExtended.maintenance>();
                    songDb.CreateTable<LR2SongDBExtended.bmson_song>();
                }

                var library = new BMSLibrary(songDbPath);
                library.SearchTargets = [firstRoot, secondRoot, firstRoot];
                var owner = new LibraryFolderTreeViewModel();
                int parentFolderPropertyChanges = 0;
                int cacheRefreshRequests = 0;
                owner.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(LibraryFolderTreeViewModel.BMSParentFolderList))
                    {
                        parentFolderPropertyChanges++;
                    }
                };
                owner.CacheRefreshRequested += (_, _) => cacheRefreshRequests++;

                owner.AttachLibrary(library);
                Assert.AreEqual(0, parentFolderPropertyChanges);
                CollectionAssert.AreEqual(
                    new[] { secondRoot, firstRoot },
                    owner.BMSParentFolderList.ToArray());
                Assert.AreEqual(library.IsWriteLockHeldInitializeBMSFiles, owner.IsWriteLockHeldInitializeBMSFiles);
                cacheRefreshRequests = 0;

                int propertyChangesBeforeInvalidation = parentFolderPropertyChanges;
                library.SearchTargets = [replacementRoot, secondRoot];
                owner.InvalidateLibraryFolderCache();

                CollectionAssert.AreEqual(
                    new[] { secondRoot, replacementRoot },
                    owner.BMSParentFolderList.ToArray());
                Assert.AreEqual(propertyChangesBeforeInvalidation, parentFolderPropertyChanges);
                Assert.AreEqual(1, cacheRefreshRequests);
            }
            finally
            {
                DispatcherHelper.UIDispatcher = previousDispatcher;
            }
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
