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
        Assert.AreEqual(root, snapshot.AssetsDirectory);
        Assert.AreEqual(root, snapshot.ProfileDirectory);
        Assert.AreEqual(Path.Combine(root, "log"), snapshot.LogDirectory);
        Assert.AreEqual(Path.Combine(Path.GetTempPath(), "BeMusicSeeker"), snapshot.TempDirectory);
        Assert.AreEqual(Path.Combine(root, "update_work"), snapshot.UpdateWorkDirectory);
        Assert.AreEqual(Path.Combine(root, "update_work", "downloads"), snapshot.DownloadsDirectory);
        Assert.AreEqual(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeMusicSeeker"), snapshot.LegacySettingsDirectory);
    }

    /// <summary>実行体・素材・可変領域が別々でも、保存先を実行体へ戻さず、言語の専有化でnative素材を移動させません。</summary>
    [TestMethod]
    public void ExplicitLayoutSeparatesExecutableAssetsAndMutableProfile()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(ApplicationPathPolicyTests), Guid.NewGuid().ToString("N"));
        string executable = Path.Combine(root, "bin", "BeMusicSeeker.exe");
        string assets = Path.Combine(root, "assets");
        string profile = Path.Combine(root, "profile");
        string language = Path.Combine(root, "language");
        string temp = Path.Combine(root, "temporary");
        string legacy = Path.Combine(root, "legacy");

        var snapshot = ApplicationPathSnapshot.FromLayout(executable, assets, profile, language, temp, legacy);

        Assert.AreEqual(executable, snapshot.ExecutablePath);
        Assert.AreEqual(Path.Combine(root, "bin"), snapshot.BaseDirectory);
        Assert.AreEqual(assets, snapshot.AssetsDirectory);
        Assert.AreEqual(profile, snapshot.ProfileDirectory);
        Assert.AreEqual(Path.Combine(profile, "config"), snapshot.ConfigDirectoryPath);
        Assert.AreEqual(Path.Combine(profile, "config", "user.config"), snapshot.UserConfigPath);
        Assert.AreEqual(Path.Combine(profile, "data"), snapshot.DataDirectoryPath);
        Assert.AreEqual(Path.Combine(profile, "data", "song.db"), snapshot.StandaloneSongDbPath);
        Assert.AreEqual(language, snapshot.LanguageDirectory);
        Assert.AreEqual(Path.Combine(assets, "test.mp3"), snapshot.TestSoundPath);
        Assert.AreEqual(Path.Combine(profile, "log"), snapshot.LogDirectory);
        Assert.AreEqual(temp, snapshot.TempDirectory);
        Assert.AreEqual(Path.Combine(profile, "update_work"), snapshot.UpdateWorkDirectory);
        Assert.AreEqual(Path.Combine(profile, "update_work", "downloads"), snapshot.DownloadsDirectory);
        Assert.AreEqual(legacy, snapshot.LegacySettingsDirectory);
        Assert.IsFalse(Directory.Exists(root), "配置の計算だけでディレクトリを作成しません。");
    }

    /// <summary>実行ファイルが未指定なら引数失敗を返し、暗黙の別基準へパスを解決しません。</summary>
    [TestMethod]
    public void SnapshotRejectsMissingExecutablePath()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ApplicationPathSnapshot.FromExecutablePath(null));
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
