using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class DroppedInstallIngressMaterializerTests
{
    [TestMethod]
    public void Acquire_ExternalTempFileStagesDurableCopyAndNeverDeletesOriginal()
    {
        using var fixture = new MaterializerFixture();
        string source = fixture.CreateExternalFile(Path.Combine("archiver", "chart.bms"), "borrowed");

        DroppedInstallIngressAcquisitionResult result = fixture.Materializer.Acquire([source]);

        Assert.IsTrue(result.Succeeded, result.Exception?.ToString());
        string durablePath = result.Request.Paths.Single();
        Assert.AreNotEqual(source, durablePath);
        Assert.AreEqual("borrowed", File.ReadAllText(durablePath));

        File.Delete(source);
        Assert.AreEqual("borrowed", File.ReadAllText(durablePath), "The queued copy must outlive the borrowed source.");

        result.Request.TryAbandonUnconsumedSources();
        Assert.IsFalse(Directory.Exists(fixture.LastIngressRoot));
    }

    [TestMethod]
    public void Acquire_StableAndCurrentManagedPathsPassThroughWithoutCopy()
    {
        using var fixture = new MaterializerFixture();
        string stableFile = fixture.CreateStableFile("stable/chart.bms", "stable");
        string stableDirectory = fixture.CreateStableDirectory("stable/folder");
        string managedFile = fixture.CreateManagedFile("current/chart.bms", "managed");

        DroppedInstallIngressAcquisitionResult result = fixture.Materializer.Acquire(
            [stableFile, stableDirectory, managedFile]);

        Assert.IsTrue(result.Succeeded, result.Exception?.ToString());
        CollectionAssert.AreEqual(
            new[]
            {
                Path.GetFullPath(stableFile),
                Path.GetFullPath(stableDirectory),
                Path.GetFullPath(managedFile)
            },
            result.Request.Paths);
        Assert.IsNull(fixture.LastIngressRoot);
    }

    [TestMethod]
    public void Acquire_StableDirectoryContainingInjectedReparseDescendantRejectsWholeBatch()
    {
        using var fixture = new MaterializerFixture();
        string stableDirectory = fixture.CreateStableDirectory("stable/reparse-tree");
        string descendant = Path.Combine(stableDirectory, "unsafe-child");
        Directory.CreateDirectory(descendant);
        File.WriteAllText(Path.Combine(descendant, "chart.bms"), "chart");
        fixture.ReportedReparsePath = descendant;

        DroppedInstallIngressAcquisitionResult result = fixture.Materializer.Acquire([stableDirectory]);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(DroppedInstallIngressFailureKind.UnsafeSource, result.FailureKind);
        Assert.IsNull(result.Request);
        Assert.IsNull(fixture.LastIngressRoot);
        Assert.IsTrue(Directory.Exists(stableDirectory));
        Assert.IsTrue(File.Exists(Path.Combine(descendant, "chart.bms")));
    }

    [TestMethod]
    public void Acquire_ManagedDirectoryContainingInjectedReparseDescendantRejectsWholeBatch()
    {
        using var fixture = new MaterializerFixture();
        string managedDirectory = fixture.CreateManagedDirectory("current/reparse-tree");
        string descendant = Path.Combine(managedDirectory, "unsafe-child");
        Directory.CreateDirectory(descendant);
        File.WriteAllText(Path.Combine(descendant, "chart.bms"), "chart");
        fixture.ReportedReparsePath = descendant;

        DroppedInstallIngressAcquisitionResult result = fixture.Materializer.Acquire([managedDirectory]);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(DroppedInstallIngressFailureKind.UnsafeSource, result.FailureKind);
        Assert.IsNull(result.Request);
        Assert.IsNull(fixture.LastIngressRoot);
        Assert.IsTrue(Directory.Exists(managedDirectory));
        Assert.IsTrue(File.Exists(Path.Combine(descendant, "chart.bms")));
    }

    [TestMethod]
    public void Acquire_MultipleSourcesPreserveSystemTempRelativeLayout()
    {
        using var fixture = new MaterializerFixture();
        string first = fixture.CreateExternalFile(Path.Combine("shared", "one", "chart.bms"), "one");
        string second = fixture.CreateExternalFile(Path.Combine("shared", "two", "chart.bms"), "two");

        DroppedInstallIngressAcquisitionResult result = fixture.Materializer.Acquire([first, second]);

        Assert.IsTrue(result.Succeeded, result.Exception?.ToString());
        Assert.AreEqual(
            Path.Combine(fixture.LastIngressRoot!, "shared", "one", "chart.bms"),
            result.Request.Paths[0]);
        Assert.AreEqual(
            Path.Combine(fixture.LastIngressRoot!, "shared", "two", "chart.bms"),
            result.Request.Paths[1]);
        Assert.AreEqual("one", File.ReadAllText(result.Request.Paths[0]));
        Assert.AreEqual("two", File.ReadAllText(result.Request.Paths[1]));
    }

    [TestMethod]
    public void Acquire_OverlappingDirectoryAndFileCopiesTreeOnceAndPreservesInputMapping()
    {
        using var fixture = new MaterializerFixture();
        string directory = fixture.CreateExternalDirectory(Path.Combine("tree", "package"));
        string file = Path.Combine(directory, "chart.bms");
        File.WriteAllText(file, "chart");

        DroppedInstallIngressAcquisitionResult result = fixture.Materializer.Acquire([file, directory]);

        Assert.IsTrue(result.Succeeded, result.Exception?.ToString());
        Assert.AreEqual(Path.Combine(result.Request.Paths[1], "chart.bms"), result.Request.Paths[0]);
        Assert.AreEqual("chart", File.ReadAllText(result.Request.Paths[0]));
    }

    [TestMethod]
    public void Acquire_PartialCopyFailureDeletesIngressRootButNotExternalOriginals()
    {
        using var fixture = new MaterializerFixture();
        string first = fixture.CreateExternalFile("partial/first.bms", "first");
        string locked = fixture.CreateExternalFile("partial/locked.bms", "locked");
        using FileStream lockStream = new(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        DroppedInstallIngressAcquisitionResult result = fixture.Materializer.Acquire([first, locked]);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(DroppedInstallIngressFailureKind.CopyFailed, result.FailureKind);
        Assert.IsFalse(Directory.Exists(fixture.LastIngressRoot));
        Assert.AreEqual(1, fixture.DeleteIngressRootCount);
        Assert.IsTrue(File.Exists(first));
        Assert.IsTrue(File.Exists(locked));
    }

    [TestMethod]
    public void Acquire_DestinationInsideSourceIsRejectedAndOriginalIsPreserved()
    {
        using var fixture = new MaterializerFixture(createIngressInsideSource: true);
        string sourceDirectory = fixture.CreateExternalDirectory("ancestor");
        File.WriteAllText(Path.Combine(sourceDirectory, "chart.bms"), "chart");
        fixture.DestinationAncestor = sourceDirectory;

        DroppedInstallIngressAcquisitionResult result = fixture.Materializer.Acquire([sourceDirectory]);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(DroppedInstallIngressFailureKind.UnsafeSource, result.FailureKind);
        Assert.IsTrue(Directory.Exists(sourceDirectory));
        Assert.IsTrue(File.Exists(Path.Combine(sourceDirectory, "chart.bms")));
        Assert.IsFalse(Directory.Exists(fixture.LastIngressRoot));
        Assert.AreEqual(1, fixture.DeleteIngressRootCount);
    }

    [TestMethod]
    public void Acquire_SystemTempRootIsRejectedWithoutCreatingIngressRoot()
    {
        using var fixture = new MaterializerFixture();

        DroppedInstallIngressAcquisitionResult result = fixture.Materializer.Acquire([fixture.SystemTempRoot]);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(DroppedInstallIngressFailureKind.UnsafeSource, result.FailureKind);
        Assert.IsNull(fixture.LastIngressRoot);
    }

    [TestMethod]
    public void Acquire_TrustedReparseTempRootDoesNotRejectNormalLogicalDescendant()
    {
        using var fixture = new MaterializerFixture();
        string source = fixture.CreateExternalFile("trusted-root/chart.bms", "chart");
        fixture.ReportedReparsePath = fixture.SystemTempRoot;

        DroppedInstallIngressAcquisitionResult result = fixture.Materializer.Acquire([source]);

        Assert.IsTrue(result.Succeeded, result.Exception?.ToString());
        Assert.IsFalse(
            fixture.AttributeProbes.Any(path => string.Equals(
                LongPathFileSystem.TrimTrailingDirectorySeparators(path),
                LongPathFileSystem.TrimTrailingDirectorySeparators(fixture.SystemTempRoot),
                StringComparison.OrdinalIgnoreCase)),
            "The configured temp root is the trusted anchor, not an inspected source component.");
        Assert.AreEqual("chart", File.ReadAllText(result.Request.Paths.Single()));
    }

    [TestMethod]
    public void Acquire_ReparseAncestorBelowTrustedRootIsRejectedBeforeCopy()
    {
        using var fixture = new MaterializerFixture();
        string reparseAncestor = Path.Combine(fixture.SystemTempRoot, "unsafe-ancestor");
        string source = Path.Combine(reparseAncestor, "missing-child", "missing-chart.bms");
        fixture.ReportedReparsePath = reparseAncestor;

        DroppedInstallIngressAcquisitionResult result = fixture.Materializer.Acquire([source]);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(DroppedInstallIngressFailureKind.UnsafeSource, result.FailureKind);
        Assert.IsNull(fixture.LastIngressRoot);
        CollectionAssert.AreEqual(
            new[] { LongPathFileSystem.NormalizePathForStorage(reparseAncestor) },
            fixture.AttributeProbes,
            "The first reparse ancestor must stop root-to-leaf probing before a missing descendant is touched.");
    }

    [TestMethod]
    public void Acquire_DirectoryContainingReparsePointRejectsBatchAndPreservesTarget()
    {
        using var fixture = new MaterializerFixture();
        string sourceDirectory = fixture.CreateExternalDirectory("reparse-source");
        string externalTarget = fixture.CreateStableDirectory("reparse-target");
        string sentinel = Path.Combine(externalTarget, "sentinel.txt");
        File.WriteAllText(sentinel, "external-sentinel");
        string link = Path.Combine(sourceDirectory, "linked-directory");
        try
        {
            Directory.CreateSymbolicLink(link, externalTarget);
        }
        catch (Exception exception) when (exception is IOException
            || exception is UnauthorizedAccessException
            || exception is PlatformNotSupportedException)
        {
            Assert.Inconclusive("The test environment does not permit directory symbolic links: " + exception.Message);
            return;
        }

        DroppedInstallIngressAcquisitionResult result = fixture.Materializer.Acquire([sourceDirectory]);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(DroppedInstallIngressFailureKind.UnsafeSource, result.FailureKind);
        Assert.IsFalse(Directory.Exists(fixture.LastIngressRoot));
        Assert.AreEqual("external-sentinel", File.ReadAllText(sentinel));
    }

    [TestMethod]
    public void Acquire_ManualFileUnderBeMusicSeekerTempIsCopiedButNeverOwnedAsOriginal()
    {
        using var fixture = new MaterializerFixture();
        string source = fixture.CreateExternalFile(Path.Combine("BeMusicSeeker", "manual.zip"), "user-owned");

        DroppedInstallIngressAcquisitionResult result = fixture.Materializer.Acquire([source]);

        Assert.IsTrue(result.Succeeded, result.Exception?.ToString());
        result.Request.TryAbandonUnconsumedSources();
        Assert.IsTrue(File.Exists(source));
        Assert.AreEqual("user-owned", File.ReadAllText(source));
    }

    private sealed class MaterializerFixture : IDisposable
    {
        private readonly string root;
        private readonly string stableRoot;
        private readonly string managedRoot;
        private readonly string ingressParent;
        private readonly bool createIngressInsideSource;

        internal MaterializerFixture(bool createIngressInsideSource = false)
        {
            root = Path.Combine(Path.GetTempPath(), nameof(DroppedInstallIngressMaterializerTests), Guid.NewGuid().ToString("N"));
            SystemTempRoot = Path.Combine(root, "system-temp");
            stableRoot = Path.Combine(root, "stable-root");
            managedRoot = Path.Combine(SystemTempRoot, "managed-session");
            ingressParent = Path.Combine(root, "managed-ingress");
            this.createIngressInsideSource = createIngressInsideSource;
            Directory.CreateDirectory(SystemTempRoot);
            Directory.CreateDirectory(stableRoot);
            Directory.CreateDirectory(managedRoot);
            Directory.CreateDirectory(ingressParent);
            Materializer = new DroppedInstallIngressMaterializer(
                SystemTempRoot,
                path => LongPathFileSystem.IsSameOrDescendantDirectoryPath(path, managedRoot),
                CreateIngressRoot,
                DeleteIngressRoot,
                getAttributes: GetAttributes);
        }

        internal DroppedInstallIngressMaterializer Materializer { get; }

        internal string SystemTempRoot { get; }

        internal string LastIngressRoot { get; private set; } = null!;

        internal string DestinationAncestor { get; set; } = null!;

        internal string? ReportedReparsePath { get; set; }

        internal int DeleteIngressRootCount { get; private set; }

        internal List<string> AttributeProbes { get; } = [];

        internal string CreateExternalFile(string relativePath, string contents)
        {
            string path = Path.Combine(SystemTempRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            return path;
        }

        internal string CreateExternalDirectory(string relativePath)
        {
            string path = Path.Combine(SystemTempRoot, relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        internal string CreateStableFile(string relativePath, string contents)
        {
            string path = Path.Combine(stableRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            return path;
        }

        internal string CreateStableDirectory(string relativePath)
        {
            string path = Path.Combine(stableRoot, relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        internal string CreateManagedFile(string relativePath, string contents)
        {
            string path = Path.Combine(managedRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            return path;
        }

        internal string CreateManagedDirectory(string relativePath)
        {
            string path = Path.Combine(managedRoot, relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        private string CreateIngressRoot()
        {
            LastIngressRoot = createIngressInsideSource
                ? Path.Combine(DestinationAncestor!, "nested-ingress")
                : Path.Combine(ingressParent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(LastIngressRoot);
            return LastIngressRoot;
        }

        private void DeleteIngressRoot(string path)
        {
            DeleteIngressRootCount++;
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        private FileAttributes GetAttributes(string path)
        {
            AttributeProbes.Add(path);
            if (!string.IsNullOrWhiteSpace(ReportedReparsePath)
                && string.Equals(
                    LongPathFileSystem.NormalizePathForStorage(path),
                    LongPathFileSystem.NormalizePathForStorage(ReportedReparsePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return FileAttributes.Directory | FileAttributes.ReparsePoint;
            }
            return LongPathFileSystem.GetAttributes(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
