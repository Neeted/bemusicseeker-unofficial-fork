using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.DroppedInstallIngressMaterializerTestSupport;
using static BeMusicSeeker.TestSupport.Components.PackageInstallComponentTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class DroppedInstallIngressMaterializerTests
{
    /// <summary>実symlinkを含むtreeは拒否してリンク先のsentinelを保ち、外部treeを辿ってコピー・削除する誤りを検出します。</summary>
    /// <remarks>実FSの入力・専用rootと内容を観測します。属性注入ケースはreparse分類を制御し、実symlinkのケースは専用環境で実行し、作成できなければ失敗します。</remarks>
    [TestMethod]
    [TestCategory("EnvironmentIntegration")]
    [TestCategory("Suite.DirectorySymbolicLink")]
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
            Assert.Fail("The test environment does not permit directory symbolic links: " + exception.Message);
            return;
        }

        DroppedInstallIngressAcquisitionResult result = fixture.Materializer.Acquire([sourceDirectory]);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(DroppedInstallIngressFailureKind.UnsafeSource, result.FailureKind);
        Assert.IsFalse(Directory.Exists(fixture.LastIngressRoot));
        Assert.AreEqual("external-sentinel", File.ReadAllText(sentinel));
    }

    /// <summary>専有ドライブのルートを実属性検査してコピーせず受理し、本番owner・portからBMSの保留登録とSQLite確定・受付終端まで接続します。</summary>
    [TestMethod]
    [TestCategory("EnvironmentIntegration")]
    [TestCategory("Suite.DriveRoot")]
    public async Task Acquire_DriveRootReachesPendingRegistrationWithoutCopy()
    {
        using var ingress = new MaterializerFixture();
        using var drive = OwnedTestDrive.Create(ingress.CreateStableDirectory("drive"), 'Z');
        await using var fixture = new AutomaticContinuationFixture(new ApplicationDatabaseScope(), false, false);
        drive.AssertOwned();
        string root = drive.Root[0] + @":\";
        string chart = Path.Combine(root, "chart.bms");
        string contents = "#PLAYER 1\r\n#TITLE Root ingress\r\n#BPM 120\r\n#WAV01 missing.wav\r\n#00111:01\r\n";
        File.WriteAllText(chart, contents);
        await fixture.InitializeAsync();

        DroppedInstallIngressAcquisitionResult result = ingress.Materializer.Acquire([root]);

        Assert.IsTrue(result.Succeeded, result.Exception?.ToString());
        CollectionAssert.AreEqual(new[] { root }, result.Request.Paths);
        Assert.AreEqual(1, ingress.AttributeProbes.Count(path => path == root));
        Assert.IsNull(ingress.LastIngressRoot);
        Assert.IsTrue(fixture.Owner.TryEnqueue(result.Request));
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
        Assert.AreEqual(chart, fixture.Library.ChartPackagesPending.Single().ChartEntries.Single().Chart.Path);
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Library.DatabaseScope,
            new BmsDatabaseLoadOptions(true, true, true), fixture.Database).OpenSongDbReadOnly();
        Assert.AreEqual(root, db.Table<LR2SongDBExtended.install>().Single().path);
        Assert.AreEqual(contents, File.ReadAllText(chart));
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
        drive.AssertOwned();
    }

}
