using System;
using System.IO;

namespace BeMusicSeeker.Models;

/// <summary>
/// 実行体・読取り素材と可変プロファイルの配置を、構成境界で一度確定します。
/// </summary>
internal sealed class ApplicationPathSnapshot
{
    private ApplicationPathSnapshot(string executablePath, string baseDirectory, string assetsDirectory,
        string profileDirectory, string languageDirectory, string tempDirectory, string legacySettingsDirectory)
    {
        ExecutablePath = executablePath;
        BaseDirectory = baseDirectory;
        AssetsDirectory = assetsDirectory;
        ProfileDirectory = profileDirectory;
        ConfigDirectoryPath = Path.Combine(profileDirectory, "config");
        UserConfigPath = Path.Combine(ConfigDirectoryPath, "user.config");
        DataDirectoryPath = Path.Combine(profileDirectory, "data");
        StandaloneSongDbPath = Path.Combine(DataDirectoryPath, "song.db");
        LanguageDirectory = languageDirectory;
        TestSoundPath = Path.Combine(assetsDirectory, "test.mp3");
        LogDirectory = Path.Combine(profileDirectory, "log");
        TempDirectory = tempDirectory;
        UpdateWorkDirectory = Path.Combine(profileDirectory, "update_work");
        DownloadsDirectory = Path.Combine(UpdateWorkDirectory, "downloads");
        LegacySettingsDirectory = legacySettingsDirectory;
    }

    /// <summary>実際に起動する実行ファイルの絶対パスを取得します。</summary>
    internal string ExecutablePath { get; }

    /// <summary>実行ファイルの所在ディレクトリを取得します。可変プロファイルの基準ではありません。</summary>
    internal string BaseDirectory { get; }

    /// <summary>nativeライブラリや試験音声など、読取り素材の基準ディレクトリを取得します。</summary>
    internal string AssetsDirectory { get; }

    /// <summary>設定・DB・ログ・更新作業を所有するプロファイルの基準ディレクトリを取得します。</summary>
    internal string ProfileDirectory { get; }

    /// <summary>ポータブル設定のディレクトリを取得します。</summary>
    internal string ConfigDirectoryPath { get; }

    /// <summary>ポータブル設定ファイルのパスを取得します。</summary>
    internal string UserConfigPath { get; }

    /// <summary>アプリデータのディレクトリを取得します。</summary>
    internal string DataDirectoryPath { get; }

    /// <summary>単独モードの譜面DBのパスを取得します。</summary>
    internal string StandaloneSongDbPath { get; }

    /// <summary>言語辞書のディレクトリを取得します。素材全体と独立して指定できます。</summary>
    internal string LanguageDirectory { get; }

    /// <summary>音声設定の試験音声ファイルのパスを取得します。</summary>
    internal string TestSoundPath { get; }

    /// <summary>通常ログと性能ログのディレクトリを取得します。</summary>
    internal string LogDirectory { get; }

    /// <summary>管理一時セッションを作る親ディレクトリを取得します。</summary>
    internal string TempDirectory { get; }

    /// <summary>更新の作業ディレクトリを取得します。</summary>
    internal string UpdateWorkDirectory { get; }

    /// <summary>更新パッケージのダウンロード先を取得します。</summary>
    internal string DownloadsDirectory { get; }

    /// <summary>旧設定の移行元ディレクトリを取得します。</summary>
    internal string LegacySettingsDirectory { get; }

    /// <summary>実プロセスの実行ファイルから、従来の通常配置を捕捉します。</summary>
    internal static ApplicationPathSnapshot Capture()
    {
        string executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("Application process path is not available.");
        }
        return FromExecutablePath(executablePath);
    }

    /// <summary>実行ファイル配下の通常配置を求めます。一時領域と旧設定の移行元は従来のOS配置です。</summary>
    internal static ApplicationPathSnapshot FromExecutablePath(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("An executable path is required.", nameof(executablePath));
        }

        string normalizedExecutablePath = Path.GetFullPath(executablePath);
        string baseDirectory = Path.GetDirectoryName(normalizedExecutablePath);
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("The executable path must have a base directory.", nameof(executablePath));
        }

        return FromLayout(normalizedExecutablePath, baseDirectory, baseDirectory,
            Path.Combine(baseDirectory, "lang"), Path.Combine(Path.GetTempPath(), "BeMusicSeeker"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeMusicSeeker"));
    }

    /// <summary>実行体を偽装せず、素材・可変プロファイル・言語・一時領域・移行元を独立して指定します。</summary>
    /// <remarks>パスを絶対化するだけで、ディレクトリの生成や存在確認は行いません。</remarks>
    internal static ApplicationPathSnapshot FromLayout(string executablePath, string assetsDirectory,
        string profileDirectory, string languageDirectory, string tempDirectory, string legacySettingsDirectory)
    {
        string normalizedExecutablePath = NormalizePath(executablePath, nameof(executablePath));
        string baseDirectory = Path.GetDirectoryName(normalizedExecutablePath);
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("The executable path must have a base directory.", nameof(executablePath));
        }
        return new ApplicationPathSnapshot(normalizedExecutablePath, baseDirectory,
            NormalizePath(assetsDirectory, nameof(assetsDirectory)),
            NormalizePath(profileDirectory, nameof(profileDirectory)),
            NormalizePath(languageDirectory, nameof(languageDirectory)),
            NormalizePath(tempDirectory, nameof(tempDirectory)),
            NormalizePath(legacySettingsDirectory, nameof(legacySettingsDirectory)));
    }

    private static string NormalizePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path)) { throw new ArgumentException("A path is required.", parameterName); }
        return Path.GetFullPath(path);
    }
}

/// <summary>
/// staticなフレームワーク窓口へ、プロセス内で一つの確定済み配置を渡します。
/// </summary>
internal static class ApplicationPathPolicy
{
    private static readonly object gate = new();

    private static ApplicationPathSnapshot current;

    internal static ApplicationPathSnapshot Current
    {
        get
        {
            lock (gate)
            {
                return current ??= ApplicationPathSnapshot.Capture();
            }
        }
    }

    internal static void Configure(ApplicationPathSnapshot snapshot)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        lock (gate)
        {
            if (current != null && !ReferenceEquals(current, snapshot))
            {
                throw new InvalidOperationException("Application path policy is already configured.");
            }

            current = snapshot;
        }
    }
}
