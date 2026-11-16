using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.DroppedInstallIngressMaterializerTestSupport;

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
    /// <remarks>実FSの入力・専用rootと内容を観測します。属性注入ケースはreparse分類を制御し、実symlinkのケースは専用環境で実行し、作成できなければ失敗します。</remarks>
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
    /// <remarks>実FSの入力・専用rootと内容を観測します。属性注入ケースはreparse分類を制御し、実symlinkのケースは専用環境で実行し、作成できなければ失敗します。</remarks>
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

    /// <summary>平坦な複数file、親子、重複、安定・管理入力の順序を入れ替えても、各入力の配置・種別・内容と借用元を保つことを確認します。</summary>
    /// <remarks>実FSで確保・読取り・回収を観測し、包含縮約による取り違えを検出します。queue以後は入口ケースが担います。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Acquire_OverlappingFileAndDirectoryPreserveInputMapping(bool reverse)
    {
        using var fixture = new MaterializerFixture();
        string directory = fixture.CreateExternalDirectory(Path.Combine("tree", "package"));
        string file = fixture.CreateExternalFile(Path.Combine("tree", "package", "chart.bms"), "chart");
        string sibling = fixture.CreateExternalFile(Path.Combine("flat", "sibling.bms"), "sibling");
        string sameName = fixture.CreateExternalFile(Path.Combine("flat", "chart.bms"), "other-chart");
        string stable = fixture.CreateStableFile("stable/chart.bms", "stable");
        string managed = fixture.CreateManagedFile("current/chart.bms", "managed");
        (string Original, string Relative, string? Contents)[] inputs =
        [
            (file, Path.Combine("tree", "package", "chart.bms"), "chart"),
            (sibling, Path.Combine("flat", "sibling.bms"), "sibling"),
            (directory, Path.Combine("tree", "package"), null),
            (sameName, Path.Combine("flat", "chart.bms"), "other-chart"),
            (Path.Combine(directory, ".", "chart.bms"), Path.Combine("tree", "package", "chart.bms"), "chart"),
            (file.ToUpperInvariant(), Path.Combine("tree", "package", "chart.bms"), "chart"),
            (directory.ToUpperInvariant(), Path.Combine("tree", "package"), null),
            (directory + Path.DirectorySeparatorChar, Path.Combine("tree", "package"), null),
            (stable, stable, "stable"),
            (managed, managed, "managed")
        ];
        if (reverse)
        {
            Array.Reverse(inputs);
        }
        string[] originals = inputs.Select(input => input.Original).ToArray();

        DroppedInstallIngressAcquisitionResult result = fixture.Materializer.Acquire(originals);

        Assert.IsTrue(result.Succeeded, result.Exception?.ToString());
        Assert.IsNotNull(fixture.LastIngressRoot);
        CollectionAssert.AreEqual(originals, result.Request.OriginalPaths);
        Assert.AreEqual(inputs.Length, result.Request.Paths.Length);
        for (int index = 0; index < inputs.Length; index++)
        {
            (string Original, string Relative, string? Contents) input = inputs[index];
            string expected = Path.GetFullPath(Path.Combine(fixture.LastIngressRoot, input.Relative));
            string actual = result.Request.Paths[index];
            Assert.IsTrue(string.Equals(expected, Path.TrimEndingDirectorySeparator(actual), StringComparison.OrdinalIgnoreCase), input.Original);
            Assert.AreEqual(input.Contents == null, Directory.Exists(actual), input.Original);
            if (input.Contents != null)
            {
                CollectionAssert.AreEqual(File.ReadAllBytes(input.Original), File.ReadAllBytes(actual), input.Original);
            }
        }

        result.Request.TryAbandonUnconsumedSources();
        Assert.IsFalse(Directory.Exists(fixture.LastIngressRoot));
        foreach ((string Original, string Relative, string? Contents) input in inputs)
        {
            if (input.Contents == null)
            {
                Assert.IsTrue(Directory.Exists(input.Original));
            }
            else
            {
                Assert.AreEqual(input.Contents, File.ReadAllText(input.Original));
            }
        }
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
    /// <remarks>実FSの入力・専用rootと内容を観測します。属性注入ケースはreparse分類を制御し、実symlinkのケースは専用環境で実行し、作成できなければ失敗します。</remarks>
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
    /// <remarks>実FSの入力・専用rootと内容を観測します。属性注入ケースはreparse分類を制御し、実symlinkのケースは専用環境で実行し、作成できなければ失敗します。</remarks>
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

    /// <summary>ドライブ・共有ルート自身のreparse属性を一度検査して拒否し、相対値「.」による属性検査の省略を検出します。UNCの実接続成功は保証しません。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Acquire_FilesystemRootReparseAttributeIsInspectedBeforeRejection(bool unc)
    {
        using var fixture = new MaterializerFixture();
        string root = unc ? @"\\ingress-invalid-host\share" : Path.GetPathRoot(fixture.SystemTempRoot) ?? throw new AssertFailedException();
        fixture.ReportedReparsePath = root;

        DroppedInstallIngressAcquisitionResult result = fixture.Materializer.Acquire([root]);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(DroppedInstallIngressFailureKind.UnsafeSource, result.FailureKind, result.Exception?.ToString());
        CollectionAssert.AreEqual(new[] { root }, fixture.AttributeProbes);
        Assert.IsNull(fixture.LastIngressRoot);
    }

}
