using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.Tests;

/// <summary>重複音源の確認を、実のシンボリックリンクで確認します。作成できない環境は失敗にします。</summary>
[TestClass]
[TestCategory("Suite.DirectorySymbolicLink")]
public sealed class AudioDuplicateSymbolicLinkTests
{
    /// <summary>
    /// 保証：ファイルのシンボリックリンクは音源として数えない。リンクの <c>x.wav</c> と実ファイルの <c>x.ogg</c> は衝突名にならず、リンクを実ファイルとして読まず、
    /// リンク先は変わらない。実ファイル同士の組だけが衝突名で、同等性の判定で要注意になる。この環境のように作成の権限がなければ失敗にする。
    /// 検出する誤り：ファイルのリンクを通常のWAVとして衝突名に数える、リンク先の読取り・変更。
    /// </summary>
    [TestMethod]
    [TestCategory("EnvironmentIntegration")]
    public void Inspect_FileSymbolicLinkIsNotCountedAsAnAudioSource()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string folder = Path.Combine(workspace.RootDirectory, "own");
        string external = Path.Combine(workspace.RootDirectory, "external");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(external);
        string target = Path.Combine(external, "target.wav");
        File.WriteAllText(target, "linked audio");
        File.WriteAllText(Path.Combine(folder, "x.ogg"), "own ogg");
        File.WriteAllText(Path.Combine(folder, "real.wav"), "");
        File.WriteAllText(Path.Combine(folder, "real.ogg"), "");
        try
        {
            File.CreateSymbolicLink(Path.Combine(folder, "x.wav"), target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Fail("実シンボリックリンクを作成できない環境のため、専用環境の準備が不足しています: " + exception.Message);
            return;
        }

        AudioDuplicateFolderInspection result = new AudioDuplicateFolderInspector().Inspect(new AudioDuplicateInspectionRequest(folder));

        Assert.AreEqual(0, result.Reasons.Count, "配下のリンクは整理不可の理由にしません。");
        CollectionAssert.AreEqual(new[] { "real" }, result.CollisionNames.ToArray(), "リンクのWAVは音源として数えません。");
        Assert.AreEqual(AudioDuplicateInspectionStage.Caution, result.Stage);
        Assert.AreEqual("linked audio", File.ReadAllText(target), "リンク先は変わりません。");
    }
}
