using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryPendingLegacyMutationTests
{
    [TestMethod]
    public void DeletePendingCharts_MissingSourceIsFailureWithoutFileMutation()
    {
        WithTemporaryDirectory(tempDirectoryPath =>
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "target");
            Directory.CreateDirectory(packageDirectoryPath);
            var chartFile = new TestableBmsFile
            {
                path = Path.Combine(packageDirectoryPath, "missing.bms")
            };
            chartFile.SetHash(new string('a', 32));
            var package = ChartPackageTestExtensions.CreatePackage([chartFile]);
            package.path = packageDirectoryPath;
            var fileMutationService = new RecordingDeleteFileMutationService();
            var service = new BmsLibraryPackageInstallService();

            PendingFileDeletionResult result = service.DeletePendingCharts(
                [ChartFileProjection.FromBmsFile(chartFile)],
                [package],
                sendToRecycleBin: false,
                deleteContainingPackageFoldersWhenNoBms: true,
                fileMutationService,
                null,
                null);

            Assert.AreEqual(1, result.Requested);
            Assert.AreEqual(1, result.Processed);
            Assert.AreEqual(0, result.Removed);
            Assert.AreEqual(1, result.Failed);
            Assert.AreEqual(0, fileMutationService.DeleteFileShellPaths.Count);
            Assert.AreEqual(0, fileMutationService.DeleteDirectoryDirectPaths.Count);
            Assert.AreEqual(0, fileMutationService.DeleteDirectoryShellPaths.Count);
            Assert.AreEqual(chartFile.path, result.Failures[0].Path);
            Assert.IsInstanceOfType(result.Failures[0].Exception, typeof(FileNotFoundException));
        });
    }

    [TestMethod]
    public void DeletePendingCharts_WrongKindSourceIsFailureWithoutMutation()
    {
        WithTemporaryDirectory(tempDirectoryPath =>
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "target");
            Directory.CreateDirectory(packageDirectoryPath);
            var chartFile = new TestableBmsFile
            {
                path = packageDirectoryPath
            };
            chartFile.SetHash(new string('a', 32));
            var package = ChartPackageTestExtensions.CreatePackage([chartFile]);
            package.path = packageDirectoryPath;
            var fileMutationService = new RecordingDeleteFileMutationService();
            var service = new BmsLibraryPackageInstallService();

            PendingFileDeletionResult result = service.DeletePendingCharts(
                [ChartFileProjection.FromBmsFile(chartFile)],
                [package],
                sendToRecycleBin: false,
                deleteContainingPackageFoldersWhenNoBms: true,
                fileMutationService,
                null,
                null);

            Assert.AreEqual(1, result.Requested);
            Assert.AreEqual(1, result.Processed);
            Assert.AreEqual(0, result.Removed);
            Assert.AreEqual(1, result.Failed);
            Assert.IsInstanceOfType(result.Failures[0].Exception, typeof(IOException));
            Assert.AreEqual(0, fileMutationService.DeleteFileShellPaths.Count);
            Assert.AreEqual(0, fileMutationService.DeleteDirectoryDirectPaths.Count);
            Assert.IsTrue(Directory.Exists(packageDirectoryPath));
        });
    }

    [TestMethod]
    public void DeletePendingCharts_ReparseSourceIsFailureWithoutMutation()
    {
        WithTemporaryDirectory(tempDirectoryPath =>
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "target");
            Directory.CreateDirectory(packageDirectoryPath);
            string targetPath = Path.Combine(tempDirectoryPath, "external.bms");
            File.WriteAllText(targetPath, "#PLAYER 1\r\n");
            string linkPath = Path.Combine(packageDirectoryPath, "linked.bms");
            try
            {
                File.CreateSymbolicLink(linkPath, targetPath);
            }
            catch (Exception exception) when (exception is IOException
                || exception is UnauthorizedAccessException
                || exception is PlatformNotSupportedException)
            {
                Assert.Inconclusive("The test environment does not permit file symbolic links: " + exception.Message);
                return;
            }
            var chartFile = new TestableBmsFile
            {
                path = linkPath
            };
            chartFile.SetHash(new string('a', 32));
            var package = ChartPackageTestExtensions.CreatePackage([chartFile]);
            package.path = packageDirectoryPath;
            var fileMutationService = new RecordingDeleteFileMutationService();
            var service = new BmsLibraryPackageInstallService();

            PendingFileDeletionResult result = service.DeletePendingCharts(
                [ChartFileProjection.FromBmsFile(chartFile)],
                [package],
                sendToRecycleBin: false,
                deleteContainingPackageFoldersWhenNoBms: true,
                fileMutationService,
                null,
                null);

            Assert.AreEqual(1, result.Requested);
            Assert.AreEqual(1, result.Processed);
            Assert.AreEqual(0, result.Removed);
            Assert.AreEqual(1, result.Failed);
            Assert.IsInstanceOfType(result.Failures[0].Exception, typeof(IOException));
            Assert.AreEqual(0, fileMutationService.DeleteFileShellPaths.Count);
            Assert.AreEqual(0, fileMutationService.DeleteDirectoryDirectPaths.Count);
            Assert.IsTrue(File.Exists(targetPath));
        });
    }

    [TestMethod]
    public void DeletePendingCharts_ChildFailureKeepsSiblingAndPreventsRecursiveAncestorDelete()
    {
        WithTemporaryDirectory(tempDirectoryPath =>
        {
            string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
            File.WriteAllBytes(songDbPath, []);
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "target");
            Directory.CreateDirectory(packageDirectoryPath);
            TestableBmsFile failedFile = CreateChart(packageDirectoryPath, "failed.bms", "a");
            TestableBmsFile successfulFile = CreateChart(packageDirectoryPath, "successful.bms", "b");
            var package = ChartPackageTestExtensions.CreatePackage([failedFile, successfulFile]);
            package.path = packageDirectoryPath;
            var fileMutationService = new RecordingDeleteFileMutationService(failedFile.path);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(package, typeof(LR2SongDBExtended.install));
            }
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                fileMutationService,
                new BmsLibraryInitializationTestSupport.RecordingDialogService())
            {
                ChartPackagesPending = new System.Collections.ObjectModel.ObservableCollection<ChartPackage>([package])
            };

            library.RemovePendingCharts(
                [ChartFileProjection.FromBmsFile(failedFile), ChartFileProjection.FromBmsFile(successfulFile)],
                sendToRecycleBin: false,
                deleteContainingPackageFoldersWhenNoBms: true);

            Assert.AreEqual(1, fileMutationService.DeleteFileDirectPaths.Count);
            CollectionAssert.AreEqual(new[] { successfulFile.path }, fileMutationService.DeleteFileDirectPaths);
            Assert.IsTrue(File.Exists(failedFile.path));
            Assert.IsFalse(File.Exists(successfulFile.path));
            Assert.AreEqual(0, fileMutationService.DeleteDirectoryShellPaths.Count);
            Assert.AreEqual(0, fileMutationService.DeleteDirectoryDirectPaths.Count);
            Assert.IsTrue(Directory.Exists(packageDirectoryPath));
            Assert.AreSame(package, library.ChartPackagesPending.Single());
            Assert.AreEqual(1, package.ChartEntries.Count);
            Assert.AreEqual(failedFile.path, package.ChartEntries.Single().Chart.Path);
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            verifySongDb.CreateTable<LR2SongDBExtended.install>();
            Assert.AreEqual(1, verifySongDb.Table<LR2SongDBExtended.install>().Count());
            Assert.AreEqual(package.path, verifySongDb.Table<LR2SongDBExtended.install>().Single().path);
        });
    }

    [TestMethod]
    public void InvalidExtensionPlan_LateMissingSourceIsFailureWithoutMutation()
    {
        WithTemporaryDirectory(tempDirectoryPath =>
        {
            string sourcePath = Path.Combine(tempDirectoryPath, "chart.bms");
            File.WriteAllText(sourcePath, "#PLAYER 1\r\n");
            var sourceFile = new TestableBmsFile
            {
                path = sourcePath
            };
            sourceFile.SetHash(new string('a', 32));
            var service = new BmsLibraryLibraryFileOperationsService();
            LibraryFileOperationTargetSnapshot snapshot = LibraryFileOperationTargetSnapshot.FromChart(
                ChartFileProjection.FromBmsFile(sourceFile),
                captureSourceFileExistence: true);
            LegacyInvalidExtensionRenamePlan plan = service.BuildInvalidExtensionRenamePlan(
                [snapshot],
                ".bmx",
                rejectUnsafeSource: true);
            var fileMutationService = new RecordingDeleteFileMutationService();
            File.Delete(sourcePath);

            LegacyInvalidExtensionRenameExecutionResult result = service.ExecuteInvalidExtensionRenamePlan(
                plan,
                fileMutationService,
                null);

            Assert.AreEqual(1, result.SkippedCount);
            Assert.AreEqual(1, result.FailedCount);
            Assert.AreEqual(0, result.RenamedCount);
            Assert.AreEqual(0, result.DuplicateDeletedCount);
            Assert.AreEqual(0, fileMutationService.DeleteFileDirectPaths.Count);
            Assert.AreEqual(0, fileMutationService.MoveFilePaths.Count);
            Assert.IsInstanceOfType(result.Items[0].Outcome.FailureException, typeof(FileNotFoundException));
        });
    }

    [TestMethod]
    public void InvalidExtensionPlan_ReparseSourceIsFailureWithoutMutation()
    {
        WithTemporaryDirectory(tempDirectoryPath =>
        {
            string targetPath = Path.Combine(tempDirectoryPath, "external.bms");
            File.WriteAllText(targetPath, "#PLAYER 1\r\n");
            string sourcePath = Path.Combine(tempDirectoryPath, "linked.bms");
            try
            {
                File.CreateSymbolicLink(sourcePath, targetPath);
            }
            catch (Exception exception) when (exception is IOException
                || exception is UnauthorizedAccessException
                || exception is PlatformNotSupportedException)
            {
                Assert.Inconclusive("The test environment does not permit file symbolic links: " + exception.Message);
                return;
            }
            var sourceFile = new TestableBmsFile
            {
                path = sourcePath
            };
            sourceFile.SetHash(new string('a', 32));
            var service = new BmsLibraryLibraryFileOperationsService();
            LibraryFileOperationTargetSnapshot snapshot = LibraryFileOperationTargetSnapshot.FromChart(
                ChartFileProjection.FromBmsFile(sourceFile),
                captureSourceFileExistence: true);
            LegacyInvalidExtensionRenamePlan plan = service.BuildInvalidExtensionRenamePlan(
                [snapshot],
                ".bmx",
                rejectUnsafeSource: true);
            var fileMutationService = new RecordingDeleteFileMutationService();

            LegacyInvalidExtensionRenameExecutionResult result = service.ExecuteInvalidExtensionRenamePlan(
                plan,
                fileMutationService,
                null);

            Assert.AreEqual(1, result.SkippedCount);
            Assert.AreEqual(1, result.FailedCount);
            Assert.AreEqual(0, result.RenamedCount);
            Assert.AreEqual(0, result.DuplicateDeletedCount);
            Assert.AreEqual(0, fileMutationService.DeleteFileDirectPaths.Count);
            Assert.AreEqual(0, fileMutationService.MoveFilePaths.Count);
            Assert.IsInstanceOfType(result.Items[0].Outcome.FailureException, typeof(IOException));
            Assert.IsTrue(File.Exists(targetPath));
        });
    }

    private static TestableBmsFile CreateChart(string directoryPath, string fileName, string hashSeed)
    {
        string filePath = Path.Combine(directoryPath, fileName);
        File.WriteAllText(filePath, "#PLAYER 1\r\n#TITLE " + fileName + "\r\n");
        var file = new TestableBmsFile
        {
            path = filePath
        };
        file.SetHash(hashSeed.PadRight(32, hashSeed[0]));
        return file;
    }

    private static void WithTemporaryDirectory(Action<string> action)
    {
        string directoryPath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker_PendingLegacyTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            action(directoryPath);
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    private sealed class TestableBmsFile : BMSFile
    {
        internal void SetHash(string value)
        {
            hash = value;
        }
    }

    private sealed class RecordingDeleteFileMutationService : IFileMutationService
    {
        private readonly string failurePath;

        internal RecordingDeleteFileMutationService(string failurePath = null)
        {
            this.failurePath = failurePath;
        }

        internal List<string> DeleteFileDirectPaths { get; } = [];

        internal List<string> DeleteFileShellPaths { get; } = [];

        internal List<string> DeleteDirectoryDirectPaths { get; } = [];

        internal List<string> DeleteDirectoryShellPaths { get; } = [];

        internal List<string> MoveFilePaths { get; } = [];

        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null)
        {
            MoveFilePaths.Add(sourcePath);
            throw new NotSupportedException();
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null)
        {
            throw new NotSupportedException();
        }

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null)
        {
            throw new NotSupportedException();
        }

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null)
        {
            throw new NotSupportedException();
        }

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null)
        {
            DeleteFileDirectPaths.Add(filePath);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null)
        {
            DeleteFileShellPaths.Add(filePath);
            if (string.Equals(filePath, failurePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("injected child delete failure");
            }
            DeleteFileDirect(filePath, options);
        }

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null)
        {
            DeleteDirectoryDirectPaths.Add(directoryPath);
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive);
            }
        }

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null)
        {
            DeleteDirectoryShellPaths.Add(directoryPath);
            DeleteDirectoryDirect(directoryPath, recursive: true, options);
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null)
        {
        }
    }
}
