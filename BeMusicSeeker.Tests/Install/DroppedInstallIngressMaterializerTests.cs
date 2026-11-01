using System;
using System.IO;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.TestSupport.Components.DroppedInstallIngressMaterializerTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class DroppedInstallIngressMaterializerTests
{
    /// <summary>実symlinkを含むtreeは拒否してリンク先のsentinelを保ち、外部treeを辿ってコピー・削除する誤りを検出します。</summary>
    /// <remarks>実FSの入力・専用rootと内容を観測します。属性注入ケースはreparse分類を制御し、実symlinkのケースは専用環境で実行し、作成できなければ失敗します。</remarks>
    [TestMethod]
    [TestCategory("EnvironmentIntegration")]
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

}
