using System;
using System.Collections.Concurrent;
using System.Configuration;
using System.IO;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Tests;

/// <summary>画面やテストクラスへ依存せず、専用設定とホスト終端まで保持する領域を準備します。</summary>
internal static class TestSettingsFactory
{
    private static readonly ConcurrentBag<string> isolatedSettingsDirectories = [];

    /// <summary>専用パスの本番 provider を生成済み Settings wrapper に接続し、実保存・読戻しを確認できるようにします。</summary>
    internal static Settings OpenSettings(string path)
    {
        var settings = new Settings();
        var provider = new PortableSettingsProvider(path);
        provider.Initialize(nameof(PortableSettingsProvider), null!);
        settings.Providers.Clear();
        settings.Providers.Add(provider);
        foreach (SettingsProperty property in settings.Properties)
        {
            property.Provider = provider;
        }

        return settings;
    }

    /// <summary>テストホスト間で共有しないポータブル設定インスタンスを作成します。</summary>
    internal static Settings CreateIsolatedSettings(Action<Settings>? configure = null)
    {
        string directory = CreateIsolatedSettingsDirectory();
        Settings settings = OpenSettings(Path.Combine(directory, "user.config"));
        configure?.Invoke(settings);
        return settings;
    }

    /// <summary>専用設定と実LR2 XMLを、同じtesthost終端で回収する領域へ準備します。</summary>
    internal static string CreateIsolatedSettingsDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BmsViewModelSettings-" + Guid.NewGuid().ToString("N"));
        isolatedSettingsDirectories.Add(directory);
        return directory;
    }

    /// <summary>このtesthostが生成した設定専用ディレクトリだけを、全テストと共有画面の終端後に回収します。</summary>
    internal static void CleanupIsolatedSettingsDirectories()
    {
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        while (isolatedSettingsDirectories.TryTake(out string? ownedDirectory))
        {
            string fullPath = Path.GetFullPath(ownedDirectory);
            if (!string.Equals(Path.GetDirectoryName(fullPath), temporaryRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(fullPath).StartsWith("BmsViewModelSettings-", StringComparison.Ordinal))
            { throw new InvalidOperationException("設定fixture所有外の削除を拒否しました: " + fullPath); }
            if (Directory.Exists(fullPath)) { Directory.Delete(fullPath, recursive: true); }
        }
    }
}

/// <summary>保存テストが独占する設定ファイルと一時ディレクトリを所有します。</summary>
internal sealed class SettingsFiles : IDisposable
{
    /// <summary>このcaseだけが所有し、Disposeで回収する設定領域です。</summary>
    internal string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BmsSettings-" + Guid.NewGuid().ToString("N"));
    /// <summary>実providerへ渡す設定ファイルのパスです。</summary>
    internal string Path => System.IO.Path.Combine(Directory, "user.config");
    /// <summary>caseの開始時に専有ディレクトリを作成します。</summary>
    internal SettingsFiles() => System.IO.Directory.CreateDirectory(Directory);
    /// <summary>失敗再現に使う読取り専用属性を解除してから専有領域を回収します。</summary>
    public void Dispose()
    {
        if (File.Exists(Path))
        {
            File.SetAttributes(Path, FileAttributes.Normal);
        }

        System.IO.Directory.Delete(Directory, true);
    }
}
