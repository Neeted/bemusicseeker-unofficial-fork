using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryLibraryFileOperationsServiceTests
{
    [TestMethod]
    public void ProcessInvalidExtensionRename_DeletesSourceWhenDestinationHasSameHash()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            BmsLibraryLibraryFileOperationsService service = new BmsLibraryLibraryFileOperationsService();
            TestFileMutationService fileMutationService = new TestFileMutationService();
            string sourcePath = Path.Combine(tempDirectoryPath, "chart.bms");
            string destinationPath = Path.Combine(tempDirectoryPath, "chart.bmx");
            File.WriteAllText(sourcePath, "same");
            File.WriteAllText(destinationPath, "same");
            TestableBmsFile file = new TestableBmsFile
            {
                path = sourcePath
            };

            RenameInvalidExtensionOutcome result = service.ProcessInvalidExtensionRename(file, destinationPath, fileMutationService, null);

            Assert.AreEqual(RenameInvalidExtensionAction.DeletedAsDuplicate, result.Action);
            Assert.IsFalse(File.Exists(sourcePath));
            Assert.IsTrue(File.Exists(destinationPath));
        });
    }

    [TestMethod]
    public void GetPendingPackagesFullyCoveredBySelection_ReturnsOnlyFullySelectedPackages()
    {
        BmsLibraryLibraryFileOperationsService service = new BmsLibraryLibraryFileOperationsService();
        TestableBmsFile selectedA = CreateFile("C:\\Pending\\Pkg1\\a.bms");
        TestableBmsFile selectedB = CreateFile("C:\\Pending\\Pkg1\\b.bms");
        TestableBmsFile partial = CreateFile("C:\\Pending\\Pkg2\\a.bms");
        TestableBmsFile partialUnselected = CreateFile("C:\\Pending\\Pkg2\\b.bms");
        BMSPackage pkg1 = new BMSPackage(new BMSFile[] { selectedA, selectedB })
        {
            path = "C:\\Pending\\Pkg1",
            delete_parent = false
        };
        BMSPackage pkg2 = new BMSPackage(new BMSFile[] { partial, partialUnselected })
        {
            path = "C:\\Pending\\Pkg2",
            delete_parent = false
        };

        List<BMSPackage> result = service.GetPendingPackagesFullyCoveredBySelection(
            new[] { pkg1, pkg2 },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { selectedA.path, selectedB.path, partial.path },
            new HashSet<BMSFile> { selectedA, selectedB, partial });

        CollectionAssert.AreEqual(new[] { pkg1 }, result);
    }

    [TestMethod]
    public void MoveFolderAndUpdateReferences_RewritesDirectoryIndexInstallDestinationsAndInstalledPaths()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            BmsLibraryLibraryFileOperationsService service = new BmsLibraryLibraryFileOperationsService();
            TestFileMutationService fileMutationService = new TestFileMutationService();
            string sourceRoot = Path.Combine(tempDirectoryPath, "Src");
            string nestedDirectoryPath = Path.Combine(sourceRoot, "Nested");
            Directory.CreateDirectory(nestedDirectoryPath);
            File.WriteAllText(Path.Combine(nestedDirectoryPath, "chart.bms"), "#PLAYER 1");
            string destinationRoot = Path.Combine(tempDirectoryPath, "Dst");
            BMSDirectoryFileNameHash folderHash = new BMSDirectoryFileNameHash();
            folderHash.AddDir(sourceRoot, update: true);
            folderHash.AddDir(nestedDirectoryPath, update: true);

            TestableBmsFile libraryFile = CreateFile(Path.Combine(sourceRoot, "Nested", "chart.bms"));
            libraryFile.instl_dst = sourceRoot;
            TestableBmsFile pendingFile = CreateFile(Path.Combine(tempDirectoryPath, "Pending", "chart.bms"));
            pendingFile.instl_dst = nestedDirectoryPath;
            BMSPackage pendingPackage = new BMSPackage(new BMSFile[] { pendingFile })
            {
                path = Path.Combine(tempDirectoryPath, "Pending"),
                delete_parent = false
            };
            BMSPackage installedPackage = new BMSPackage(new BMSFile[] { libraryFile })
            {
                path = nestedDirectoryPath,
                delete_parent = false
            };

            service.MoveFolderAndUpdateReferences(
                sourceRoot,
                destinationRoot,
                new[] { libraryFile },
                new[] { pendingPackage },
                new[] { installedPackage },
                folderHash,
                fileMutationService,
                null);

            Assert.IsFalse(Directory.Exists(sourceRoot));
            Assert.IsTrue(Directory.Exists(destinationRoot));
            CollectionAssert.Contains(folderHash.Keys, destinationRoot);
            CollectionAssert.Contains(folderHash.Keys, Path.Combine(destinationRoot, "Nested"));
            Assert.AreEqual(Path.Combine(destinationRoot, "Nested"), pendingFile.instl_dst);
            Assert.AreEqual(destinationRoot, libraryFile.instl_dst);
            Assert.AreEqual(Path.Combine(destinationRoot, "Nested"), installedPackage.path);
        });
    }

    [TestMethod]
    public void DeleteLibraryFiles_RemovesFolderAndClearsInstallDestinations()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            BmsLibraryLibraryFileOperationsService service = new BmsLibraryLibraryFileOperationsService();
            TestFileMutationService fileMutationService = new TestFileMutationService();
            string folderPath = Path.Combine(tempDirectoryPath, "Song");
            Directory.CreateDirectory(folderPath);
            string chartPath = Path.Combine(folderPath, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            TestableBmsFile libraryFile = CreateFile(chartPath);
            libraryFile.instl_dst = folderPath;
            TestableBmsFile pendingFile = CreateFile(Path.Combine(tempDirectoryPath, "Pending", "chart.bms"));
            pendingFile.instl_dst = folderPath;
            BMSPackage pendingPackage = new BMSPackage(new BMSFile[] { pendingFile })
            {
                path = Path.Combine(tempDirectoryPath, "Pending"),
                delete_parent = false
            };
            BMSDirectoryFileNameHash folderHash = new BMSDirectoryFileNameHash();
            folderHash.AddDir(folderPath, update: true);

            LibraryRemovalResult result = service.DeleteLibraryFiles(
                new[] { libraryFile },
                new[] { libraryFile },
                new[] { pendingPackage },
                folderHash,
                sendToRecycleBin: false,
                _ => true,
                fileMutationService,
                null,
                null);

            Assert.AreEqual(1, result.RemovedFiles.Count);
            Assert.AreSame(libraryFile, result.RemovedFiles[0]);
            Assert.AreEqual(0, result.Failures.Count);
            Assert.IsNull(pendingFile.instl_dst);
            Assert.IsNull(libraryFile.instl_dst);
            Assert.IsFalse(Directory.Exists(folderPath));
            CollectionAssert.DoesNotContain(folderHash.Keys, folderPath);
        });
    }

    [TestMethod]
    public void BuildFolderMoveDelta_ReturnsFolderAndFilePathChanges()
    {
        BmsLibraryLibraryFileOperationsService service = new BmsLibraryLibraryFileOperationsService();
        TestableBmsFile file1 = CreateFile("C:\\Lib\\Src\\A\\a.bms");
        TestableBmsFile file2 = CreateFile("C:\\Lib\\Src\\B\\b.bms");

        LibraryMutationDelta delta = service.BuildFolderMoveDelta("C:\\Lib\\Src", "C:\\Lib\\Dst", new[] { file1, file2 }, unregister: false);

        Assert.AreEqual(2, delta.FolderPathChanges.Count);
        Assert.AreEqual(2, delta.FilePathChanges.Count);
        Assert.AreEqual(0, delta.FilesToUnregister.Count);
        CollectionAssert.AreEquivalent(
            new[] { "C:\\Lib\\Dst\\A\\a.bms", "C:\\Lib\\Dst\\B\\b.bms" },
            delta.FilePathChanges.Select((LibraryFilePathChange change) => change.NewPath).ToArray());
        Assert.IsTrue(delta.RaiseBmsFilesChanged);
        Assert.IsTrue(delta.InvalidateInstalledDirectoryIndex);
        Assert.IsTrue(delta.InvalidateParentFolderCache);
    }

    [TestMethod]
    public void RenameLibraryFileExtensions_ReturnsRenamedAndDuplicateDeletedFiles()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            BmsLibraryLibraryFileOperationsService service = new BmsLibraryLibraryFileOperationsService();
            TestFileMutationService fileMutationService = new TestFileMutationService();
            string renameSourcePath = Path.Combine(tempDirectoryPath, "rename_me.bms");
            string duplicateSourcePath = Path.Combine(tempDirectoryPath, "duplicate.bms");
            string duplicateDestinationPath = Path.Combine(tempDirectoryPath, "duplicate.bme");
            File.WriteAllText(renameSourcePath, "rename");
            File.WriteAllText(duplicateSourcePath, "same");
            File.WriteAllText(duplicateDestinationPath, "same");
            TestableBmsFile renameFile = CreateFile(renameSourcePath);
            TestableBmsFile duplicateFile = CreateFile(duplicateSourcePath);

            LibraryMutationDelta delta = service.RenameLibraryFileExtensions(
                new[] { renameFile, duplicateFile },
                ".bme",
                unregister: false,
                (file, requestedPath) => service.ProcessInvalidExtensionRename(file, requestedPath, fileMutationService, null));

            Assert.AreEqual(1, delta.RenamedCount);
            Assert.AreEqual(1, delta.DuplicateDeletedCount);
            Assert.AreEqual(0, delta.SkippedCount);
            Assert.AreEqual(1, delta.FilePathChanges.Count);
            Assert.AreEqual(1, delta.FilesToUnregister.Count);
            Assert.AreEqual(Path.Combine(tempDirectoryPath, "rename_me.bme"), delta.FilePathChanges[0].NewPath);
            Assert.IsTrue(File.Exists(Path.Combine(tempDirectoryPath, "rename_me.bme")));
            Assert.IsFalse(File.Exists(renameSourcePath));
            Assert.IsFalse(File.Exists(duplicateSourcePath));
            Assert.IsTrue(File.Exists(duplicateDestinationPath));
        });
    }

    private static TestableBmsFile CreateFile(string path)
    {
        return new TestableBmsFile
        {
            path = path
        };
    }

    private static void WithTemporaryDirectory(Action<string> testAction)
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_FileOperationTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        try
        {
            testAction(tempDirectoryPath);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }
    private sealed class TestableBmsFile : BMSFile
    {
    }

    private sealed class TestFileMutationService : IFileMutationService
    {
        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null)
        {
            string destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }
            if (overwrite && File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
            File.Move(sourcePath, destinationPath);
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null)
        {
            if (overwrite && Directory.Exists(destinationPath))
            {
                Directory.Delete(destinationPath, recursive: true);
            }
            string destinationParentPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationParentPath))
            {
                Directory.CreateDirectory(destinationParentPath);
            }
            CopyDirectory(sourcePath, destinationPath);
            Directory.Delete(sourcePath, recursive: true);
        }

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null)
        {
            DeleteFileDirect(filePath, options);
        }

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null)
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive);
            }
        }

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null)
        {
            DeleteDirectoryDirect(directoryPath, recursive: true, options);
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null)
        {
        }

        private static void CopyDirectory(string sourcePath, string destinationPath)
        {
            Directory.CreateDirectory(destinationPath);
            foreach (string directoryPath in Directory.GetDirectories(sourcePath, "*", System.IO.SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(directoryPath.Replace(sourcePath, destinationPath));
            }
            foreach (string filePath in Directory.GetFiles(sourcePath, "*", System.IO.SearchOption.AllDirectories))
            {
                string destinationFilePath = filePath.Replace(sourcePath, destinationPath);
                string destinationDirectoryPath = Path.GetDirectoryName(destinationFilePath);
                if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
                {
                    Directory.CreateDirectory(destinationDirectoryPath);
                }
                File.Copy(filePath, destinationFilePath, overwrite: true);
            }
        }
    }
}
