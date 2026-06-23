using System;
using System.Collections.Generic;
using System.IO;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ExplorerOpenServiceTests
{
    [TestMethod]
    public void OpenFileAndSelect_SelectsExistingFileWithShellApi()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            string filePath = Path.Combine(tempDirectory, "chart.bms");
            File.WriteAllText(filePath, "#PLAYER 1");
            var shell = new FakeExplorerShell
            {
                ResolvePathResult = true,
                SelectFileResult = true
            };

            ExplorerOpenResult result = ExplorerOpenService.OpenFileAndSelect(filePath, shell);

            Assert.AreEqual(ExplorerOpenResultKind.SelectedFile, result.Kind);
            Assert.AreEqual(LongPathFileSystem.NormalizePathForStorage(filePath), result.OpenedPath);
            Assert.AreEqual(1, shell.ResolvePathPaths.Count);
            Assert.AreEqual(1, shell.SelectFilePaths.Count);
            Assert.AreEqual(0, shell.OpenDirectoryPaths.Count);
            Assert.AreEqual(0, shell.ExplorerDirectoryPaths.Count);
        });
    }

    [TestMethod]
    public void OpenFileAndSelect_OpensParentDirectoryWhenFileCannotBeResolvedBeforeAnyOpen()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            string childDirectory = Path.Combine(tempDirectory, "deep");
            Directory.CreateDirectory(childDirectory);
            string filePath = Path.Combine(childDirectory, "chart.bms");
            File.WriteAllText(filePath, "#PLAYER 1");
            var shell = new FakeExplorerShell();
            shell.ResolvePathResults.Enqueue(false);
            shell.ResolvePathResults.Enqueue(false);
            shell.ResolvePathResults.Enqueue(true);
            shell.OpenDirectoryResult = true;

            ExplorerOpenResult result = ExplorerOpenService.OpenFileAndSelect(filePath, shell);

            Assert.AreEqual(ExplorerOpenResultKind.OpenedParentDirectory, result.Kind);
            Assert.AreEqual(LongPathFileSystem.NormalizePathForStorage(childDirectory), result.OpenedPath);
            Assert.AreEqual(0, shell.SelectFilePaths.Count);
            Assert.AreEqual(1, shell.OpenDirectoryPaths.Count);
            Assert.AreEqual(LongPathFileSystem.NormalizePathForStorage(childDirectory), shell.OpenDirectoryPaths[0]);
            Assert.AreEqual(0, shell.ExplorerDirectoryPaths.Count);
        });
    }

    [TestMethod]
    public void OpenFileAndSelect_ResolvesExtendedPathCandidateBeforeSingleSelectionOpen()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            string filePath = Path.Combine(tempDirectory, "chart.bms");
            File.WriteAllText(filePath, "#PLAYER 1");
            var shell = new FakeExplorerShell();
            shell.ResolvePathResults.Enqueue(false);
            shell.ResolvePathResults.Enqueue(true);
            shell.SelectFileResult = true;

            ExplorerOpenResult result = ExplorerOpenService.OpenFileAndSelect(filePath, shell);

            Assert.AreEqual(ExplorerOpenResultKind.SelectedFile, result.Kind);
            Assert.AreEqual(2, shell.ResolvePathPaths.Count);
            Assert.AreEqual(LongPathFileSystem.NormalizePathForStorage(filePath), shell.ResolvePathPaths[0]);
            StringAssert.StartsWith(shell.ResolvePathPaths[1], @"\\?\");
            Assert.AreEqual(1, shell.SelectFilePaths.Count);
            Assert.AreEqual(shell.ResolvePathPaths[1], shell.SelectFilePaths[0]);
            Assert.AreEqual(0, shell.OpenDirectoryPaths.Count);
        });
    }

    [TestMethod]
    public void OpenFileAndSelect_PrefersExtendedPathForLongPathBeforeSingleSelectionOpen()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            string longDirectory = CreateLongDirectory(tempDirectory);
            string filePath = Path.Combine(longDirectory, "chart.bms");
            using (FileStream stream = LongPathFileSystem.Open(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write("#PLAYER 1");
            }
            var shell = new FakeExplorerShell
            {
                ResolvePathResult = true,
                SelectFileResult = true
            };

            ExplorerOpenResult result = ExplorerOpenService.OpenFileAndSelect(filePath, shell);

            Assert.AreEqual(ExplorerOpenResultKind.SelectedFile, result.Kind);
            Assert.AreEqual(1, shell.ResolvePathPaths.Count);
            StringAssert.StartsWith(shell.ResolvePathPaths[0], @"\\?\");
            Assert.AreEqual(1, shell.SelectFilePaths.Count);
            Assert.AreEqual(shell.ResolvePathPaths[0], shell.SelectFilePaths[0]);
            Assert.AreEqual(0, shell.OpenDirectoryPaths.Count);
        });
    }

    [TestMethod]
    public void OpenFileAndSelect_DoesNotInvokeShellWhenFileDoesNotExist()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            string filePath = Path.Combine(tempDirectory, "missing.bms");
            var shell = new FakeExplorerShell
            {
                ResolvePathResult = true,
                SelectFileResult = true,
                OpenDirectoryResult = true,
                ExplorerResult = true
            };

            ExplorerOpenResult result = ExplorerOpenService.OpenFileAndSelect(filePath, shell);

            Assert.AreEqual(ExplorerOpenResultKind.NotFound, result.Kind);
            Assert.AreEqual(0, shell.ResolvePathPaths.Count);
            Assert.AreEqual(0, shell.SelectFilePaths.Count);
            Assert.AreEqual(0, shell.OpenDirectoryPaths.Count);
            Assert.AreEqual(0, shell.ExplorerDirectoryPaths.Count);
        });
    }

    [TestMethod]
    public void OpenFileAndSelect_DoesNotInvokeParentFallbackAfterSelectionOpenFails()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            string filePath = Path.Combine(tempDirectory, "chart.bms");
            File.WriteAllText(filePath, "#PLAYER 1");
            var shell = new FakeExplorerShell
            {
                ResolvePathResult = true,
                SelectFileResult = false,
                OpenDirectoryResult = false,
                ExplorerResult = false
            };

            ExplorerOpenResult result = ExplorerOpenService.OpenFileAndSelect(filePath, shell);

            Assert.AreEqual(ExplorerOpenResultKind.Failed, result.Kind);
            StringAssert.Contains(result.FailureReason, "select_failed");
            Assert.AreEqual(1, shell.SelectFilePaths.Count);
            Assert.AreEqual(0, shell.OpenDirectoryPaths.Count);
            Assert.AreEqual(0, shell.ExplorerDirectoryPaths.Count);
        });
    }

    [TestMethod]
    public void OpenDirectory_UsesShellApiForExistingDirectory()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            var shell = new FakeExplorerShell
            {
                ResolvePathResult = true,
                OpenDirectoryResult = true
            };

            ExplorerOpenResult result = ExplorerOpenService.OpenDirectory(tempDirectory, shell);

            Assert.AreEqual(ExplorerOpenResultKind.OpenedDirectory, result.Kind);
            Assert.AreEqual(LongPathFileSystem.NormalizePathForStorage(tempDirectory), result.OpenedPath);
            Assert.AreEqual(1, shell.ResolvePathPaths.Count);
            Assert.AreEqual(1, shell.OpenDirectoryPaths.Count);
            Assert.AreEqual(0, shell.ExplorerDirectoryPaths.Count);
        });
    }

    [TestMethod]
    public void OpenDirectory_DoesNotInvokeExplorerProcessFallbackAfterShellOpenFails()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            var shell = new FakeExplorerShell
            {
                ResolvePathResult = true,
                OpenDirectoryResult = false,
                ExplorerResult = true
            };

            ExplorerOpenResult result = ExplorerOpenService.OpenDirectory(tempDirectory, shell);

            Assert.AreEqual(ExplorerOpenResultKind.Failed, result.Kind);
            StringAssert.Contains(result.FailureReason, "open_directory_failed");
            Assert.AreEqual(1, shell.OpenDirectoryPaths.Count);
            Assert.AreEqual(0, shell.ExplorerDirectoryPaths.Count);
        });
    }

    [TestMethod]
    public void OpenDirectory_FallsBackToExplorerProcessOnlyWhenNoShellPathResolves()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            var shell = new FakeExplorerShell
            {
                ResolvePathResult = false,
                ExplorerResult = true
            };

            ExplorerOpenResult result = ExplorerOpenService.OpenDirectory(tempDirectory, shell);

            Assert.AreEqual(ExplorerOpenResultKind.OpenedDirectory, result.Kind);
            Assert.AreEqual(0, shell.OpenDirectoryPaths.Count);
            Assert.AreEqual(1, shell.ExplorerDirectoryPaths.Count);
            Assert.AreEqual(LongPathFileSystem.NormalizePathForStorage(tempDirectory), shell.ExplorerDirectoryPaths[0]);
        });
    }

    [TestMethod]
    public void OpenDirectory_ResolvesExtendedPathCandidateBeforeSingleOpen()
    {
        WithTemporaryDirectory(delegate (string tempDirectory)
        {
            var shell = new FakeExplorerShell();
            shell.ResolvePathResults.Enqueue(false);
            shell.ResolvePathResults.Enqueue(true);
            shell.OpenDirectoryResult = true;

            ExplorerOpenResult result = ExplorerOpenService.OpenDirectory(tempDirectory, shell);

            Assert.AreEqual(ExplorerOpenResultKind.OpenedDirectory, result.Kind);
            Assert.AreEqual(2, shell.ResolvePathPaths.Count);
            Assert.AreEqual(LongPathFileSystem.NormalizePathForStorage(tempDirectory), shell.ResolvePathPaths[0]);
            StringAssert.StartsWith(shell.ResolvePathPaths[1], @"\\?\");
            Assert.AreEqual(1, shell.OpenDirectoryPaths.Count);
            Assert.AreEqual(shell.ResolvePathPaths[1], shell.OpenDirectoryPaths[0]);
            Assert.AreEqual(0, shell.ExplorerDirectoryPaths.Count);
        });
    }

    [TestMethod]
    public void MainWindowExplorerContextMenusUseExplorerOpenService()
    {
        string root = FindRepositoryRoot();
        string mainWindow = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.cs"));

        Assert.IsFalse(mainWindow.Contains("Process.Start(\"EXPLORER.EXE\""));
        StringAssert.Contains(mainWindow, "ExplorerOpenService.OpenFileAndSelect");
        StringAssert.Contains(mainWindow, "ExplorerOpenService.OpenDirectory");
    }

    private static void WithTemporaryDirectory(Action<string> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            action(directory);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                LongPathFileSystem.DeleteDirectory(directory, recursive: true);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        string directory = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (File.Exists(Path.Combine(directory, "BeMusicSeeker-decomp.sln")))
            {
                return directory;
            }
            DirectoryInfo parent = Directory.GetParent(directory);
            if (parent == null)
            {
                break;
            }
            directory = parent.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string CreateLongDirectory(string tempDirectory)
    {
        string directory = tempDirectory;
        int index = 0;
        while (directory.Length < 270)
        {
            directory = Path.Combine(directory, "long-path-segment-" + index.ToString("00"));
            index++;
        }
        LongPathFileSystem.CreateDirectory(directory);
        return directory;
    }

    private sealed class FakeExplorerShell : IExplorerShell
    {
        public bool SelectFileResult { get; set; }

        public bool OpenDirectoryResult { get; set; }

        public bool ExplorerResult { get; set; }

        public List<string> ResolvePathPaths { get; } = [];

        public List<string> SelectFilePaths { get; } = [];

        public List<string> OpenDirectoryPaths { get; } = [];

        public List<string> ExplorerDirectoryPaths { get; } = [];

        public Queue<bool> ResolvePathResults { get; } = [];

        public Queue<bool> SelectFileResults { get; } = [];

        public Queue<bool> OpenDirectoryResults { get; } = [];

        public Queue<bool> ExplorerResults { get; } = [];

        public bool ResolvePathResult { get; set; }

        public bool TryResolvePath(string path, out string failureReason)
        {
            ResolvePathPaths.Add(path);
            bool result = ResolvePathResults.Count > 0 ? ResolvePathResults.Dequeue() : ResolvePathResult;
            failureReason = result ? string.Empty : "resolve_failed";
            return result;
        }

        public bool TrySelectFile(string filePath, out string failureReason)
        {
            SelectFilePaths.Add(filePath);
            bool result = SelectFileResults.Count > 0 ? SelectFileResults.Dequeue() : SelectFileResult;
            failureReason = result ? string.Empty : "select_failed";
            return result;
        }

        public bool TryOpenDirectory(string directoryPath, out string failureReason)
        {
            OpenDirectoryPaths.Add(directoryPath);
            bool result = OpenDirectoryResults.Count > 0 ? OpenDirectoryResults.Dequeue() : OpenDirectoryResult;
            failureReason = result ? string.Empty : "open_directory_failed";
            return result;
        }

        public bool TryOpenDirectoryWithExplorer(string directoryPath, out string failureReason)
        {
            ExplorerDirectoryPaths.Add(directoryPath);
            bool result = ExplorerResults.Count > 0 ? ExplorerResults.Dequeue() : ExplorerResult;
            failureReason = result ? string.Empty : "explorer_failed";
            return result;
        }
    }
}
