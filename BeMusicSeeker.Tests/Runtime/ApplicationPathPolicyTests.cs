using System;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ApplicationPathPolicyTests
{
    /// <summary>実行ファイル基準から設定・DB・言語・試験音声の位置を解決し、作業ディレクトリに依存した保存先を防ぎます。</summary>
    [TestMethod]
    public void SnapshotDerivesAllApplicationPathsFromExecutableDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(ApplicationPathPolicyTests), Guid.NewGuid().ToString("N"));
        string executablePath = Path.Combine(root, "BeMusicSeeker.exe");

        var snapshot = ApplicationPathSnapshot.FromExecutablePath(executablePath);

        Assert.AreEqual(Path.GetFullPath(executablePath), snapshot.ExecutablePath);
        Assert.AreEqual(Path.GetFullPath(root), snapshot.BaseDirectory);
        Assert.AreEqual(Path.Combine(root, "config"), snapshot.ConfigDirectoryPath);
        Assert.AreEqual(Path.Combine(root, "config", "user.config"), snapshot.UserConfigPath);
        Assert.AreEqual(Path.Combine(root, "data"), snapshot.DataDirectoryPath);
        Assert.AreEqual(Path.Combine(root, "data", "song.db"), snapshot.StandaloneSongDbPath);
        Assert.AreEqual(Path.Combine(root, "lang"), snapshot.LanguageDirectory);
        Assert.AreEqual(Path.Combine(root, "test.mp3"), snapshot.TestSoundPath);
    }

    /// <summary>実行ファイルが未指定なら引数失敗を返し、暗黙の別基準へパスを解決しません。</summary>
    [TestMethod]
    public void SnapshotRejectsMissingExecutablePath()
    {
        Assert.ThrowsException<ArgumentException>(() => ApplicationPathSnapshot.FromExecutablePath(null));
    }

    /// <summary>注入した実行ファイル基準をnative bridgeの探索へ渡し、別のアプリ配置を参照しません。</summary>
    [TestMethod]
    public void EverythingNativeUsesInjectedApplicationBaseDirectoryForBridgePath()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(ApplicationPathPolicyTests), Guid.NewGuid().ToString("N"));
        var snapshot = ApplicationPathSnapshot.FromExecutablePath(
            Path.Combine(root, "bin", "BeMusicSeeker.exe"));

        var native = new EverythingNative(snapshot);

        Assert.AreEqual(
            Path.Combine(snapshot.BaseDirectory, "native", "EverythingBridge_x64.dll"),
            native.GetExpectedBridgeDllPath());
        native.Dispose();
    }

}
