using System.Diagnostics;
using System.IO;
using BeMusicSeeker.Desktop.E2E.Tests.Fixtures;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Scenarios;
using BeMusicSeeker.TestSupport.Verification;

namespace BeMusicSeeker.Desktop.E2E.Tests.Startup;

/// <summary>SC-16が通常起動の前に置く、portable設定・譜面root・専有子環境・CWDを準備します。</summary>
/// <remarks>
/// 設定は現行providerを使わず、旧版と同じ型付きXMLで書きます。既定値や補正先と区別できる固有値だけを使い、
/// 旧設定(%LOCALAPPDATA%)と`data/`は置きません。起動後に入力を補充しません。
/// </remarks>
internal sealed class NormalStartupFixture
{
    /// <summary>検索入力に使う語です。一致する譜面のtitleだけが含みます。</summary>
    internal const string SearchText = "needle";
    /// <summary>設定画面で選ばれていることを確認するテーマの保存値です。既定は`Light`です。</summary>
    internal const string ThemeValue = "GreenTerminal";
    /// <summary>テーマ名を引く言語辞書の鍵です。</summary>
    internal const string ThemeNameKey = "Appearance_theme_green_terminal";
    /// <summary>既定・補正先の`ja-JP`と区別する言語です。</summary>
    internal const string Language = "en-US";
    /// <summary>製品が知らない目印キーです。終了保存で失われないことを値まで確認します。</summary>
    internal const string UnknownKey = "Sc16UnknownSentinel";
    /// <summary>目印キーの値です。XML特殊文字を含めます。</summary>
    internal const string UnknownValue = "sc16 <&> sentinel";

    /// <summary>配布アプリを展開した場所の下に`config/user.config`を置き、譜面と子環境を専有領域へ作ります。</summary>
    internal NormalStartupFixture(TestWorkspace workspace, string appRoot)
    {
        AppRoot = appRoot;
        LibraryRoot = workspace.GetPath("library");
        WorkingDirectory = workspace.GetPath("cwd");
        DiagnosticsDirectory = workspace.GetPath("diagnostics");
        Directory.CreateDirectory(WorkingDirectory);
        Directory.CreateDirectory(DiagnosticsDirectory);
        Charts = Array.AsReadOnly(new[]
        {
            DesktopPlaylistFixture.CreateChart(Path.Combine(LibraryRoot, "alpha"), "alpha", "SC16 needle Alpha"),
            DesktopPlaylistFixture.CreateChart(Path.Combine(LibraryRoot, "gamma"), "gamma", "SC16 needle Gamma"),
            DesktopPlaylistFixture.CreateChart(Path.Combine(LibraryRoot, "beta"), "beta", "SC16 other Beta")
        });
        string profile = workspace.GetPath("profile");
        string user = Path.Combine(profile, "user");
        string local = Path.Combine(user, @"AppData\Local");
        string temp = Path.Combine(profile, "temp");
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(temp);
        ChildEnvironment = new Dictionary<string, string?>
        {
            ["LOCALAPPDATA"] = local,
            ["USERPROFILE"] = user,
            ["TEMP"] = temp,
            ["TMP"] = temp,
            // test harnessの入力を一切持ち込まず、製品が通常配布として起動する条件にします。
            [TestRunDirectory.EnvironmentVariable] = null,
            [ProcessDeadline.SuiteDeadlineEnvironmentVariable] = null,
            [ScenarioApplicationPaths.EnvironmentVariable] = null,
            [DistributionCatalog.EnvironmentVariable] = null
        };
        // 配布の製品versionと同じ値にして、初回起動や版更新の経路を通しません。
        // 単一ファイル配布のexeはmanaged metadataを直接持たないため、AssemblyVersionから生成されるFileVersionを読みます。
        var version = FileVersionInfo.GetVersionInfo(Path.Combine(appRoot, "BeMusicSeeker.exe"));
        Settings = new Dictionary<string, string>
        {
            ["AssemblyVersion"] = $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}.{version.FilePrivatePart}",
            ["Lang"] = Language,
            ["AppearanceTheme"] = ThemeValue,
            ["OperationModeLR2DB"] = "False",
            ["BMSRootPath"] = LibraryRoot,
            ["StandaloneBmsRootPaths"] = LibraryRoot,
            ["BMSInstallDir"] = LibraryRoot,
            ["ScanBmsFilesOnStartup"] = "True",
            // 既定のTrueは起動時に導入待ちの表示を選ぶため、登録rootの一覧を見るために通常の表示へ戻します。
            ["StartupSelectInstallPending"] = "False",
            // 外部通信が既定で有効な項目は、解決されない名前または無効値にします。
            ["EnablePlaylistUrlCompletion"] = "False",
            ["EnableStellaFullPlaylistUrlCompletion"] = "False",
            ["SkipInitPlaylistLoad"] = "True",
            ["TableListURL"] = "https://example.invalid/sc16-table-list",
            [UnknownKey] = UnknownValue
        };
        Directory.CreateDirectory(Path.GetDirectoryName(PortablePath) ?? throw new InvalidDataException());
        LegacySettingsFixture.Write(PortablePath, Settings);
        Assert.IsFalse(Directory.Exists(Path.Combine(appRoot, "data")), "通常走査が標準配置へ新規作成できるよう、dataは置きません。");
    }

    /// <summary>展開済み実配布の専有installationです。</summary>
    internal string AppRoot { get; }
    /// <summary>登録した譜面rootです。</summary>
    internal string LibraryRoot { get; }
    /// <summary>appの外に置く空のCWDです。</summary>
    internal string WorkingDirectory { get; }
    /// <summary>CWDではない場所に保存する診断fileの置き場です。</summary>
    internal string DiagnosticsDirectory { get; }
    /// <summary>parserに依存せず宣言した譜面の入力です。</summary>
    internal IReadOnlyList<DesktopChartInput> Charts { get; }
    /// <summary>起動前にportable設定へ書いた値です。</summary>
    internal IReadOnlyDictionary<string, string> Settings { get; }
    /// <summary>親を変更せず通常childへ渡す専有環境です。</summary>
    internal IReadOnlyDictionary<string, string?> ChildEnvironment { get; }
    /// <summary>通常配置の設定ファイルです。</summary>
    internal string PortablePath => Path.Combine(AppRoot, @"config\user.config");
    /// <summary>通常配置のsong DBです。</summary>
    internal string DatabasePath => Path.Combine(AppRoot, @"data\song.db");
    /// <summary>検索語に一致する譜面です。</summary>
    internal IReadOnlyList<DesktopChartInput> Matching
        => Charts.Where(chart => chart.Title.Contains(SearchText, StringComparison.Ordinal)).ToArray();
    /// <summary>起動記述です。CWDはappの外の空ディレクトリです。</summary>
    internal PublishedApplicationSession.Launch Launch => new(AppRoot, WorkingDirectory, ChildEnvironment, DiagnosticsDirectory);
}
