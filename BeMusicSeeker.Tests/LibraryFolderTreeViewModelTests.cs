using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
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
                var owner = new LibraryFolderTreeViewModel(
                    _ => true,
                    _ => new ExplorerOpenResult());
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

    [TestMethod]
    public void OpenFolderInExplorer_ValidatesBeforeOpeningExactlyOnce()
    {
        List<string> validationPaths = [];
        List<string> openedPaths = [];
        var owner = new LibraryFolderTreeViewModel(
            path =>
            {
                validationPaths.Add(path);
                return true;
            },
            path =>
            {
                openedPaths.Add(path);
                return new ExplorerOpenResult
                {
                    Kind = ExplorerOpenResultKind.OpenedDirectory,
                    RequestedPath = path,
                    OpenedPath = path
                };
            });

        owner.OpenFolderInExplorer("C:\\Library");

        CollectionAssert.AreEqual(new[] { "C:\\Library" }, validationPaths);
        CollectionAssert.AreEqual(new[] { "C:\\Library" }, openedPaths);
    }

    [TestMethod]
    public void OpenFolderInExplorer_MissingFolderDoesNotInvokeExplorer()
    {
        var owner = new LibraryFolderTreeViewModel(
            _ => false,
            _ => throw new AssertFailedException("Explorer should not be invoked for a missing folder."));

        owner.OpenFolderInExplorer("C:\\Missing");
    }

    [TestMethod]
    public void OpenFolderInExplorer_PreservesFailedExplorerResultWithoutFallback()
    {
        int openCount = 0;
        var owner = new LibraryFolderTreeViewModel(
            _ => true,
            path =>
            {
                openCount++;
                return new ExplorerOpenResult
                {
                    Kind = ExplorerOpenResultKind.Failed,
                    RequestedPath = path,
                    FailureReason = "shell_failed"
                };
            });

        owner.OpenFolderInExplorer("C:\\Library");

        Assert.AreEqual(1, openCount);
    }
}
