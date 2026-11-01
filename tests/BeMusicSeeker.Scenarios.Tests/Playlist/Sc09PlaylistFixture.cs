using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Scenarios.Tests.Fixtures;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.Scenarios.Tests.Playlist;

/// <summary>SC09の全入力と独立期待値を起動前に宣言します。操作後のDB・設定補充はしません。</summary>
internal sealed class Sc09PlaylistFixture : IAsyncDisposable
{
    internal const string ExternalName = "SC09 External";
    internal const string LocalName = "SC09 Local";
    internal const string LocalSymbol = "DL";
    internal const string ExternalMemo = "external memo 日常";
    internal const string LocalMemo = "local memo 保存";
    internal const string Alpha = "Daily Alpha";
    internal const string Beta = "Daily Beta";
    internal const double EditedLevel = 12.5;
    internal const double InitialFont = 11;
    internal const double EditedFont = 16;
    internal static readonly string[] FinalFolderOrder = [Beta, Alpha];
    private readonly ScenarioStartupFixture endpoint;

    private Sc09PlaylistFixture(TestWorkspace workspace)
    {
        ProfileDirectory = workspace.GetPath("profile");
        DatabasePath = workspace.GetPath("profile/data/song.db");
        SettingsPath = workspace.GetPath("profile/config/user.config");
        HeaderPath = workspace.GetPath("export/header.json");
        DataPath = workspace.GetPath("export/data.json");
        CandidatePath = workspace.GetPath("cancelled-beatoraja");
        string libraryRoot = workspace.GetPath("library");
        Directory.CreateDirectory(libraryRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(HeaderPath) ?? throw new InvalidOperationException("出力先の親がありません。"));
        A = WriteChart(workspace.GetPath("library/A/chart.bms"), "SC09 A", "SC09 Artist A", 2);
        B = WriteChart(workspace.GetPath("library/B/chart.bms"), "SC09 B", "SC09 Artist B", 3);
        U = new("11111111111111111111111111111111", new string('2', 64), "SC09 Unowned", "SC09 Artist U", 4);
        var scope = new ApplicationDatabaseScope();
        string executable = Path.Combine(AppContext.BaseDirectory, "fixtures", "application", "BeMusicSeeker.exe");
        var paths = ApplicationPathSnapshot.FromLayout(executable, Path.GetDirectoryName(executable)
            ?? throw new InvalidOperationException("実行体の親がありません。"), ProfileDirectory,
            Path.Combine(AppContext.BaseDirectory, "fixtures", "application", "lang"), workspace.GetPath("temp"), workspace.GetPath("legacy"));
        StandaloneLibraryDatabase.EnsurePortableSongDb(scope, paths);
        var responses = new Dictionary<string, string>
        {
            ["/daily/index.html"] = "<html><head><meta name=\"bmstable\" content=\"header.json\"></head><body>SC09</body></html>",
            ["/daily/header.json"] = JsonSerializer.Serialize(new { name = ExternalName, symbol = "DE", compat_prefix = "", data_url = "data.json", folder_order = new[] { "External" } }),
            ["/daily/data.json"] = JsonSerializer.Serialize(new[] { A, U }.Select(chart => new
            { md5 = chart.Md5, sha256 = chart.Sha256, title = chart.Title, artist = chart.Artist, org_level = chart.Level, folder = "External", level = "External" }))
        };
        endpoint = ScenarioStartupFixture.Create(SettingsPath, libraryRoot, settings =>
        {
            settings.CustomTableFontSize = InitialFont;
            settings.BeatorajaRootPath = string.Empty;
            settings.EnableBeatorajaBmtOutput = false;
        }, responses);
    }
    /// <summary>固定入力を起動前に配置します。</summary>
    internal static Sc09PlaylistFixture Create(TestWorkspace workspace) => new(workspace);
    internal string ProfileDirectory { get; }
    internal string DatabasePath { get; }
    internal string SettingsPath { get; }
    internal string HeaderPath { get; }
    internal string DataPath { get; }
    internal string CandidatePath { get; }
    internal string UpdateManifestUrl => endpoint.UpdateManifestUrl;
    internal string ExternalUrl => endpoint.BaseUrl + "/daily/index.html";
    internal Sc09Chart A { get; }
    internal Sc09Chart B { get; }
    internal Sc09Chart U { get; }
    /// <summary>実アプリ終了後に専有HTTPを回収します。</summary>
    public ValueTask DisposeAsync() => endpoint.DisposeAsync();

    private static Sc09Chart WriteChart(string path, string title, string artist, int level)
    {
        byte[] bytes = Encoding.ASCII.GetBytes($"#PLAYER 1\r\n#TITLE {title}\r\n#ARTIST {artist}\r\n#BPM 120\r\n#PLAYLEVEL {level}\r\n#RANK 2\r\n#00111:01\r\n");
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("譜面の親がありません。"));
        File.WriteAllBytes(path, bytes);
        return new(Convert.ToHexStringLower(MD5.HashData(bytes)), Convert.ToHexStringLower(SHA256.HashData(bytes)), title, artist, level);
    }
}
/// <summary>本番parserを使わず入力bytesと宣言内容から決める譜面期待値です。</summary>
internal sealed record Sc09Chart(string Md5, string Sha256, string Title, string Artist, double Level);
