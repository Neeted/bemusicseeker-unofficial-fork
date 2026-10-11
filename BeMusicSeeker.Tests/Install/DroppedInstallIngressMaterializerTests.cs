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
    /// <summary>外部temp入力を専用rootへ確保し、借用元の消失後も内容を読み取れて未消費終端でrootを回収する寿命違反を検出します。</summary>
    /// <remarks>実FSで確保・読取り・回収の接続を観測し、queue以後の導入処理は別の入口ケースが担います。</remarks>
    [TestMethod]
    public void Acquire_ExternalTempCopyRemainsAfterBorrowedSourceIsRemoved()
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

    /// <summary>安定したfile・folderと現管理fileは元pathを渡してrootを作らず、不要なコピーやpath変更を検出します。</summary>
    /// <remarks>実FSで確保・読取り・回収の接続を観測し、queue以後の導入処理は別の入口ケースが担います。</remarks>
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

    /// <summary>安定folder内のreparse属性を見つけた場合は全体を拒否し、入力の削除や危険なtreeの受理を検出します。</summary>
    /// <remarks>実FSの入力・専用rootと内容を観測します。属性注入ケースはreparse分類を制御し、実symlinkのケースは作成できない環境で既存のInconclusiveを維持します。</remarks>
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

    /// <summary>管理folder内でもreparse属性を見つけた場合は全体を拒否し、所有分類による安全検査の迂回を検出します。</summary>
    /// <remarks>実FSの入力・専用rootと内容を観測します。属性注入ケースはreparse分類を制御し、実symlinkのケースは作成できない環境で既存のInconclusiveを維持します。</remarks>
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

    /// <summary>同名の外部入力二件をtemp相対配置と別々の内容のまま確保し、flattenによる衝突や対象の取り違えを検出します。</summary>
    /// <remarks>実FSで確保・読取り・回収の接続を観測し、queue以後の導入処理は別の入口ケースが担います。</remarks>
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

    /// <summary>重なるfile・親folder入力でも返却pathの包含関係と内容を保ち、重複入力の配置ずれを検出します。</summary>
    /// <remarks>実FSで確保・読取り・回収の接続を観測し、queue以後の導入処理は別の入口ケースが担います。</remarks>
    [TestMethod]
    public void Acquire_OverlappingFileAndDirectoryPreserveInputMapping()
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

    /// <summary>一入力の読取りlockでコピーが失敗すると作業rootを一回回収し、外部入力の誤削除や部分rootの残留を検出します。</summary>
    /// <remarks>実FSで確保・読取り・回収の接続を観測し、queue以後の導入処理は別の入口ケースが担います。</remarks>
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

    /// <summary>確保先が入力tree内にある場合は拒否して専用rootだけを回収し、再帰コピーや元入力の削除を検出します。</summary>
    /// <remarks>実FSで確保・読取り・回収の接続を観測し、queue以後の導入処理は別の入口ケースが担います。</remarks>
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

    /// <summary>temp基点そのものの入力はroot作成前に拒否し、全tempの確保を始める誤りを検出します。</summary>
    /// <remarks>実FSで確保・読取り・回収の接続を観測し、queue以後の導入処理は別の入口ケースが担います。</remarks>
    [TestMethod]
    public void Acquire_SystemTempRootIsRejectedWithoutCreatingIngressRoot()
    {
        using var fixture = new MaterializerFixture();

        DroppedInstallIngressAcquisitionResult result = fixture.Materializer.Acquire([fixture.SystemTempRoot]);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(DroppedInstallIngressFailureKind.UnsafeSource, result.FailureKind);
        Assert.IsNull(fixture.LastIngressRoot);
    }

    /// <summary>信頼するtemp基点のreparse属性は検査対象に含めず、通常の子入力を誤拒否することを検出します。</summary>
    /// <remarks>実FSの入力・専用rootと内容を観測します。属性注入ケースはreparse分類を制御し、実symlinkのケースは作成できない環境で既存のInconclusiveを維持します。</remarks>
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

    /// <summary>temp基点より下のreparse祖先で探索を止め、未存在の子へ進む誤りや専用rootを先に作る誤りを検出します。</summary>
    /// <remarks>実FSの入力・専用rootと内容を観測します。属性注入ケースはreparse分類を制御し、実symlinkのケースは作成できない環境で既存のInconclusiveを維持します。</remarks>
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

    /// <summary>実symlinkを含むtreeは拒否してリンク先のsentinelを保ち、外部treeを辿ってコピー・削除する誤りを検出します。</summary>
    /// <remarks>実FSの入力・専用rootと内容を観測します。属性注入ケースはreparse分類を制御し、実symlinkのケースは作成できない環境で既存のInconclusiveを維持します。</remarks>
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

    /// <summary>app名のtemp内に置いた手動fileも確保後の回収では元内容を保ち、名称だけで所有扱いして消す誤りを検出します。</summary>
    /// <remarks>実FSで確保・読取り・回収の接続を観測し、queue以後の導入処理は別の入口ケースが担います。</remarks>
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
