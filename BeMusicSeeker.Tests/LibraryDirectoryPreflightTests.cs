using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class LibraryDirectoryPreflightTests
{
    [TestMethod]
    public void StandaloneConfiguredAdapterPreservesMissingRootsOnRoundTrip()
    {
        string missing = Path.Combine(
            Path.GetTempPath(),
            nameof(LibraryDirectoryPreflightTests),
            "missing-" + Guid.NewGuid().ToString("N"));

        IReadOnlyList<string> loaded = StandaloneBmsRootPathSettings.Deserialize(missing);
        string serialized = StandaloneBmsRootPathSettings.Serialize(loaded);
        IReadOnlyList<string> reloaded = StandaloneBmsRootPathSettings.Deserialize(serialized);

        CollectionAssert.AreEqual(new[] { Path.GetFullPath(missing) }, loaded.ToArray());
        CollectionAssert.AreEqual(new[] { Path.GetFullPath(missing) }, reloaded.ToArray());
    }

    [TestMethod]
    public void Lr2RawSearchRootGetterPreservesDriveRootAndResolvesRelativeRoot()
    {
        string root = CreateTemporaryDirectory();
        string configDirectory = Path.Combine(root, "LR2files", "Config");
        string configPath = Path.Combine(configDirectory, "config.xml");
        Directory.CreateDirectory(configDirectory);
        string driveRoot = Path.GetPathRoot(Path.GetFullPath(Environment.SystemDirectory));
        File.WriteAllText(
            configPath,
            "<config><jukebox><path>relative\\</path><path>"
            + driveRoot
            + "</path></jukebox></config>");
        try
        {
            LR2Config config = new(configPath);
            List<string> roots = config.GetBMSSearchDirectoriesForChangeTracking();

            CollectionAssert.Contains(roots, Path.Combine(root, "relative"));
            CollectionAssert.Contains(roots, driveRoot);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [TestMethod]
    public void RawAdditionalOutputDriveRootPreservesRootDuringRequestCapture()
    {
        string driveRoot = Path.GetPathRoot(Path.GetFullPath(Environment.SystemDirectory));
        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
            LR2CustomFolderAdditionalOutputBaseDirsSerialized =
                JsonConvert.SerializeObject(new[] { driveRoot })
        };

        LibraryDirectoryPreflightRequest request = new LibraryDirectoryPreflightService()
            .CreateRequest([], [], options);

        LibraryDirectoryPreflightOutputBase target = request.OutputBaseTargets.Single();
        Assert.AreEqual(LibraryDirectoryPreflightOutputBaseKind.Additional, target.Kind);
        Assert.AreEqual(driveRoot, target.DirectoryPath);
    }

    [TestMethod]
    public void EmptyBmsRoot_IsReadOnlyAndSucceeds()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            var fileSystem = new RecordingPreflightFileSystem();
            LibraryDirectoryPreflightService service = new(fileSystem);
            LibraryDirectoryPreflightRequest request = service.CreateRequest(
                [root],
                [root],
                new BmsLibraryOptionsSnapshot());

            service.EnsureAvailable(request, probeOutputBases: true);

            CollectionAssert.Contains(fileSystem.AttributePaths, root);
            CollectionAssert.Contains(fileSystem.EnumeratedPaths, root);
            Assert.AreEqual(0, fileSystem.CreatedProbePaths.Count);
            Assert.AreEqual(0, fileSystem.OpenedPaths.Count);
            Assert.AreEqual(0, fileSystem.DeletedPaths.Count);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [TestMethod]
    public void MissingAndFileRootsProduceTypedFailuresWithCause()
    {
        string root = CreateTemporaryDirectory();
        string missing = Path.Combine(root, "missing");
        string filePath = Path.Combine(root, "root-file");
        File.WriteAllText(filePath, "file");
        try
        {
            LibraryDirectoryPreflightService service = new();
            LibraryDirectoryPreflightException missingFailure = Assert.ThrowsException<LibraryDirectoryPreflightException>(
                () => service.EnsureAvailable(
                    service.CreateRequest([missing], [missing], new BmsLibraryOptionsSnapshot()),
                    probeOutputBases: false));
            Assert.AreEqual(LibraryDirectoryPreflightFailureCause.NotFound, missingFailure.Cause);
            Assert.AreEqual(LibraryDirectoryPreflightUse.BmsRoot, missingFailure.Use);
            Assert.AreEqual(Path.GetFullPath(missing), missingFailure.DirectoryPath);

            LibraryDirectoryPreflightException fileFailure = Assert.ThrowsException<LibraryDirectoryPreflightException>(
                () => service.EnsureAvailable(
                    service.CreateRequest([filePath], [filePath], new BmsLibraryOptionsSnapshot()),
                    probeOutputBases: false));
            Assert.AreEqual(LibraryDirectoryPreflightFailureCause.NotDirectory, fileFailure.Cause);
            Assert.AreEqual(LibraryDirectoryPreflightUse.BmsRoot, fileFailure.Use);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [TestMethod]
    public void BmsRootEnumerationFailureIsTypedAndDoesNotWrite()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            var fileSystem = new RecordingPreflightFileSystem
            {
                EnumerationFailure = new UnauthorizedAccessException("enumeration denied")
            };
            LibraryDirectoryPreflightService service = new(fileSystem);
            LibraryDirectoryPreflightException failure = Assert.ThrowsException<LibraryDirectoryPreflightException>(
                () => service.EnsureAvailable(
                    service.CreateRequest([root], [root], new BmsLibraryOptionsSnapshot()),
                    probeOutputBases: false));

            Assert.AreEqual(LibraryDirectoryPreflightFailureCause.AccessDenied, failure.Cause);
            Assert.AreEqual(LibraryDirectoryPreflightUse.BmsRoot, failure.Use);
            Assert.AreEqual(0, fileSystem.CreatedProbePaths.Count);
            Assert.AreEqual(0, fileSystem.OpenedPaths.Count);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [TestMethod]
    public void MalformedAdditionalOutputConfigurationIsNotTreatedAsEmpty()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                LR2CustomFolderAdditionalOutputBaseDirsSerialized = "{malformed-json"
            };

            LibraryDirectoryPreflightException failure = Assert.ThrowsException<LibraryDirectoryPreflightException>(
                () => new LibraryDirectoryPreflightService().CreateRequest([root], [root], options));

            Assert.AreEqual(LibraryDirectoryPreflightUse.Lr2OutputBase, failure.Use);
            Assert.AreEqual(LibraryDirectoryPreflightOutputBaseKind.Additional, failure.OutputBaseKind);
            Assert.AreEqual(LibraryDirectoryPreflightFailureCause.InvalidConfiguration, failure.Cause);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [TestMethod]
    public void LinkedModeProbesEveryOutputBaseAndDoesNotCreateMissingBase()
    {
        string root = CreateTemporaryDirectory();
        string normal = Path.Combine(root, "normal");
        string additional = Path.Combine(root, "additional");
        string missingRootType = Path.Combine(root, "root-type-missing");
        Directory.CreateDirectory(normal);
        Directory.CreateDirectory(additional);
        try
        {
            var options = CreateLinkedOptions(normal, additional, missingRootType);
            LibraryDirectoryPreflightService service = new();
            LibraryDirectoryPreflightRequest request = service.CreateRequest(
                [normal],
                [normal],
                options);

            LibraryDirectoryPreflightException failure = Assert.ThrowsException<LibraryDirectoryPreflightException>(
                () => service.EnsureAvailable(request, probeOutputBases: true));

            Assert.AreEqual(LibraryDirectoryPreflightUse.Lr2OutputBase, failure.Use);
            Assert.AreEqual(LibraryDirectoryPreflightOutputBaseKind.RootType, failure.OutputBaseKind);
            Assert.AreEqual(LibraryDirectoryPreflightFailureCause.NotFound, failure.Cause);
            Assert.IsFalse(Directory.Exists(missingRootType));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [TestMethod]
    public void StandaloneModeIgnoresUnusedMissingOutputBases()
    {
        string root = CreateTemporaryDirectory();
        string bmsRoot = Path.Combine(root, "bms");
        Directory.CreateDirectory(bmsRoot);
        try
        {
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = false,
                LR2CustomFolderOutputBaseDir = Path.Combine(root, "unused")
            };
            LibraryDirectoryPreflightService service = new();
            LibraryDirectoryPreflightRequest request = service.CreateRequest(
                [bmsRoot],
                [bmsRoot],
                options);

            service.EnsureAvailable(request, probeOutputBases: true);

            Assert.AreEqual(0, request.OutputBaseTargets.Count);
            Assert.IsFalse(Directory.Exists(options.LR2CustomFolderOutputBaseDir));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [TestMethod]
    public void ManagedOutputChildIsNotRequiredAsASeparateRoot()
    {
        string root = CreateTemporaryDirectory();
        string normal = Path.Combine(root, "normal");
        string managedRoot = Path.Combine(root, "managed");
        string uncreatedChild = Path.Combine(managedRoot, "playlist-child");
        Directory.CreateDirectory(normal);
        Directory.CreateDirectory(managedRoot);
        try
        {
            var options = CreateLinkedOptions(normal, additional: null, rootType: managedRoot);
            LibraryDirectoryPreflightService service = new();
            LibraryDirectoryPreflightRequest request = service.CreateRequest(
                [normal, managedRoot, uncreatedChild],
                [normal],
                options);

            service.EnsureAvailable(request, probeOutputBases: true);

            Assert.IsFalse(Directory.Exists(uncreatedChild));
            CollectionAssert.AreEqual(
                new[] { Path.GetFullPath(normal) },
                request.ScanRootDirectories.ToArray());
            Assert.IsTrue(request.OutputBaseTargets.Any(target =>
                string.Equals(target.DirectoryPath, Path.GetFullPath(managedRoot), StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [TestMethod]
    public void OutputProbeIsOwnedInsideBaseAndLeavesExistingFilesUnchanged()
    {
        string root = CreateTemporaryDirectory();
        string outputBase = Path.Combine(root, "output");
        Directory.CreateDirectory(outputBase);
        string sentinel = Path.Combine(outputBase, "existing-sentinel.txt");
        byte[] sentinelBytes = [0x10, 0x20, 0x30];
        File.WriteAllBytes(sentinel, sentinelBytes);
        try
        {
            var fileSystem = new RecordingPreflightFileSystem();
            LibraryDirectoryPreflightService service = new(fileSystem);
            LibraryDirectoryPreflightRequest request = service.CreateRequest(
                [],
                [],
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                    LR2CustomFolderOutputBaseDir = outputBase
                });

            service.EnsureAvailable(request, probeOutputBases: true);
            int createdProbeCountAfterWriteProbe = fileSystem.CreatedProbePaths.Count;
            int openedCountAfterWriteProbe = fileSystem.OpenedPaths.Count;
            int deletedCountAfterWriteProbe = fileSystem.DeletedPaths.Count;

            // late check は同じ request の read-only 検査だけを行います。
            service.EnsureAvailable(request, probeOutputBases: false);

            Assert.AreEqual(1, fileSystem.CreatedProbePaths.Count);
            Assert.AreEqual(createdProbeCountAfterWriteProbe, fileSystem.CreatedProbePaths.Count);
            Assert.AreEqual(openedCountAfterWriteProbe, fileSystem.OpenedPaths.Count);
            Assert.AreEqual(deletedCountAfterWriteProbe, fileSystem.DeletedPaths.Count);
            string probePath = fileSystem.CreatedProbePaths.Single();
            StringAssert.StartsWith(probePath, Path.GetFullPath(outputBase) + Path.DirectorySeparatorChar);
            CollectionAssert.Contains(fileSystem.OpenedPaths, probePath);
            CollectionAssert.Contains(fileSystem.DeletedPaths, probePath);
            CollectionAssert.AreEqual(sentinelBytes, File.ReadAllBytes(sentinel));
            Assert.IsFalse(File.Exists(probePath));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [TestMethod]
    public void OutputCreateFailureDoesNotAttemptDelete()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string outputBase = Path.Combine(root, "output");
            Directory.CreateDirectory(outputBase);
            var fileSystem = new RecordingPreflightFileSystem
            {
                OpenFailure = new IOException("write denied")
            };
            LibraryDirectoryPreflightService service = new(fileSystem);
            LibraryDirectoryPreflightException failure = Assert.ThrowsException<LibraryDirectoryPreflightException>(
                () => service.EnsureAvailable(
                    service.CreateRequest(
                        [],
                        [],
                        new BmsLibraryOptionsSnapshot
                        {
                            OperationModeLR2DB = true,
                            LR2CustomFolderOutputBaseDir = outputBase
                        }),
                    probeOutputBases: true));

            Assert.AreEqual(LibraryDirectoryPreflightFailureCause.Write, failure.Cause);
            Assert.AreEqual(0, fileSystem.DeletedPaths.Count);
            Assert.IsNotNull(failure.ProbePath);
            Assert.IsNull(failure.CleanupException);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [TestMethod]
    public void OutputCleanupFailureKeepsProbeDiagnostics()
    {
        string root = CreateTemporaryDirectory();
        string probePath = null;
        try
        {
            string outputBase = Path.Combine(root, "output");
            Directory.CreateDirectory(outputBase);
            var fileSystem = new RecordingPreflightFileSystem
            {
                DeleteFailure = new UnauthorizedAccessException("cleanup denied")
            };
            LibraryDirectoryPreflightService service = new(fileSystem);
            LibraryDirectoryPreflightException failure = Assert.ThrowsException<LibraryDirectoryPreflightException>(
                () => service.EnsureAvailable(
                    service.CreateRequest(
                        [],
                        [],
                        new BmsLibraryOptionsSnapshot
                        {
                            OperationModeLR2DB = true,
                            LR2CustomFolderOutputBaseDir = outputBase
                        }),
                    probeOutputBases: true));
            probePath = failure.ProbePath;

            Assert.AreEqual(LibraryDirectoryPreflightFailureCause.Cleanup, failure.Cause);
            Assert.AreEqual(LibraryDirectoryPreflightFailureCause.Cleanup, failure.CleanupCause);
            Assert.IsNotNull(failure.CleanupException);
            Assert.IsFalse(string.IsNullOrWhiteSpace(probePath));
            Assert.IsTrue(File.Exists(probePath));
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(probePath) && File.Exists(probePath))
            {
                File.Delete(probePath);
            }
            DeleteTemporaryDirectory(root);
        }
    }

    private static BmsLibraryOptionsSnapshot CreateLinkedOptions(
        string normal,
        string additional,
        string rootType)
    {
        return new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
            LR2CustomFolderOutputBaseDir = normal,
            LR2CustomFolderAdditionalOutputBaseDirs = additional == null ? [] : [additional],
            LR2CustomFolderOutputBaseDirRootType = rootType
        };
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            nameof(LibraryDirectoryPreflightTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class RecordingPreflightFileSystem : ILibraryDirectoryPreflightFileSystem
    {
        internal List<string> AttributePaths { get; } = [];

        internal List<string> EnumeratedPaths { get; } = [];

        internal List<string> CreatedProbePaths { get; } = [];

        internal List<string> OpenedPaths { get; } = [];

        internal List<string> DeletedPaths { get; } = [];

        internal Exception EnumerationFailure { get; init; }

        internal Exception OpenFailure { get; init; }

        internal Exception DeleteFailure { get; init; }

        public FileAttributes GetAttributes(string path)
        {
            AttributePaths.Add(path);
            return LongPathFileSystem.GetAttributes(path);
        }

        public IEnumerable<string> EnumerateDirectoryEntries(string path)
        {
            EnumeratedPaths.Add(path);
            return EnumerateDirectoryEntriesCore(path);
        }

        private IEnumerable<string> EnumerateDirectoryEntriesCore(string path)
        {
            if (EnumerationFailure != null)
            {
                throw EnumerationFailure;
            }

            foreach (string entry in LongPathFileSystem.EnumerateFileSystemEntries(path))
            {
                yield return entry;
            }
        }

        public string CreateOwnedProbePath(string directoryPath, string purpose)
        {
            string path = Path.Combine(
                directoryPath,
                ".bemusicseeker-" + purpose + "-" + Guid.NewGuid().ToString("N") + ".tmp");
            CreatedProbePaths.Add(path);
            return path;
        }

        public Stream Open(string path, FileMode mode, FileAccess access, FileShare share)
        {
            OpenedPaths.Add(path);
            if (OpenFailure != null)
            {
                throw OpenFailure;
            }
            return LongPathFileSystem.Open(path, mode, access, share);
        }

        public void DeleteFile(string path)
        {
            DeletedPaths.Add(path);
            if (DeleteFailure != null)
            {
                throw DeleteFailure;
            }
            LongPathFileSystem.DeleteFile(path);
        }
    }
}
