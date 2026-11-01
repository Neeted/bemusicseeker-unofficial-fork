using System.Collections.Concurrent;
using System.Configuration;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>画面やテストクラスへ依存せず、専用設定とホスト終端まで保持する領域を準備します。</summary>
internal static class TestSettingsFactory
{
    private static readonly ConcurrentBag<TestWorkspace> isolatedSettingsWorkspaces = [];

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

    /// <summary>専用設定と実LR2 XMLを同じtesthost終端で回収する領域を作成します。設定ファイル自体は保存まで作りません。</summary>
    internal static string CreateIsolatedSettingsDirectory()
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        isolatedSettingsWorkspaces.Add(workspace);
        return workspace.RootDirectory;
    }

    /// <summary>全テストと共有画面の終端後に、所有する全設定領域の回収を試し、失敗を集約して返します。</summary>
    internal static void CleanupIsolatedSettingsDirectories()
    {
        var failures = new List<Exception>();
        while (isolatedSettingsWorkspaces.TryTake(out TestWorkspace? workspace))
        {
            try { workspace.Dispose(); }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (failures.Count != 0) { throw new AggregateException("設定fixtureの回収に失敗しました。", failures); }
    }
}

/// <summary>保存テストが独占する設定ファイルと一時ディレクトリを所有します。</summary>
internal sealed class SettingsFiles : IDisposable
{
    private readonly TestWorkspace workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

    /// <summary>このcaseだけが所有し、Disposeで回収する設定領域です。</summary>
    internal string Directory => workspace.RootDirectory;
    /// <summary>実providerへ渡す設定ファイルのパスです。</summary>
    internal string Path => System.IO.Path.Combine(Directory, "user.config");
    /// <summary>caseの開始時に専有ディレクトリを作成します。</summary>
    internal SettingsFiles() { }
    /// <summary>失敗再現に使う読取り専用属性を解除してから専有領域を回収します。</summary>
    public void Dispose()
    {
        if (File.Exists(Path))
        {
            File.SetAttributes(Path, FileAttributes.Normal);
        }

        workspace.Dispose();
    }
}
