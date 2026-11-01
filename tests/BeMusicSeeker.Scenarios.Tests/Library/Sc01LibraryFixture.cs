using System.IO;
using System.Security.Cryptography;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Scenarios.Tests.Fixtures;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.Scenarios.Tests.Library;

/// <summary>SC01 が起動前に準備する専有設定・DB・譜面と、外部変更の入力を所有します。</summary>
/// <remarks>Workspace の削除と実アプリの終了は呼出元が担当し、終了後にこの fixture の HTTP 待受を解放します。</remarks>
internal sealed class Sc01LibraryFixture : IAsyncDisposable
{
    private static readonly DateTime BLastWriteTimeUtc = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private readonly ScenarioStartupFixture updateEndpoint;
    private readonly byte[] sentinelBytes = Encoding.ASCII.GetBytes(
        "#PLAYER 1\r\n#TITLE match outside sentinel\r\n#ARTIST SC01\r\n#BPM 120\r\n#PLAYLEVEL 1\r\n#00111:01\r\n");

    private Sc01LibraryFixture(TestWorkspace workspace)
    {
        ProfileDirectory = workspace.GetPath("profile");
        SongDatabasePath = workspace.GetPath("profile/data/song.db");
        SettingsPath = workspace.GetPath("profile/config/user.config");
        LibraryRoot = workspace.GetPath("library");
        SentinelPath = workspace.GetPath("library-outside/sentinel.bms");
        Directory.CreateDirectory(LibraryRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(SentinelPath) ?? throw new InvalidOperationException("sentinel の親がありません。"));
        File.WriteAllBytes(SentinelPath, sentinelBytes);

        Encoding cp932 = CodePagesEncodingProvider.Instance.GetEncoding(932)
            ?? throw new InvalidOperationException("CP932 を利用できません。");
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        byte[] Utf8Bytes(string text) => [.. utf8.GetPreamble(), .. utf8.GetBytes(text)];
        A = CreateChart(workspace.GetPath("library/A/chart.bms"), "SC01 A 日本語", Sc01ChartFormat.Bms,
            cp932.GetBytes("#PLAYER 1\r\n#TITLE SC01 A 日本語\r\n#ARTIST SC01\r\n#BPM 120\r\n#PLAYLEVEL 1\r\n#RANK 2\r\n#WAV01 tone.wav\r\n#00111:01\r\n"));
        B = CreateChart(workspace.GetPath("library/B/chart.bms"), "match B UTF8 日本語", Sc01ChartFormat.Bms,
            Utf8Bytes("#PLAYER 1\r\n#TITLE match B UTF8 日本語\r\n#ARTIST SC01\r\n#BPM 120\r\n#PLAYLEVEL 2\r\n#RANK 2\r\n#WAV01 tone.wav\r\n#00111:01\r\n"));
        C = CreateChart(workspace.GetPath("library/C/chart.bmson"), "SC01 C bmson", Sc01ChartFormat.Bmson,
            Encoding.UTF8.GetBytes("""
                {"version":"1.0.0","info":{"title":"SC01 C bmson","artist":"SC01","mode_hint":"beat-7k","level":3,"init_bpm":120,"judge_rank":100,"total":100,"resolution":240},"lines":[{"y":0},{"y":960}],"sound_channels":[{"name":"tone.wav","notes":[{"x":1,"y":240,"l":0,"c":false}]}]}
                """));
        D = CreateChart(workspace.GetPath("library/D/chart.bms"), "match D added", Sc01ChartFormat.Bms,
            Utf8Bytes("#PLAYER 1\r\n#TITLE match D added\r\n#ARTIST SC01\r\n#BPM 120\r\n#PLAYLEVEL 4\r\n#RANK 2\r\n#WAV01 tone.wav\r\n#00111:01\r\n"));
        InitialCharts = Array.AsReadOnly(new[] { A, B, C });
        UpdatedCharts = Array.AsReadOnly(new[] { B, C, D });
        foreach (Sc01ChartManifest chart in InitialCharts) { WriteChart(chart); }
        File.SetLastWriteTimeUtc(B.Path, BLastWriteTimeUtc);

        SeedDatabase(workspace);
        updateEndpoint = ScenarioStartupFixture.Create(SettingsPath, LibraryRoot);
    }

    /// <summary>設定・DB・ログだけを配置する、実アプリ専有のプロファイルです。</summary>
    internal string ProfileDirectory { get; }

    /// <summary>読取り専用 oracle が観測する Standalone song DB です。</summary>
    internal string SongDatabasePath { get; }

    /// <summary>起動前に保存する専有設定ファイルです。</summary>
    internal string SettingsPath { get; }

    /// <summary>このシナリオで登録する唯一の root です。</summary>
    internal string LibraryRoot { get; }

    /// <summary>root と文字列 prefix が似ていても走査範囲へ含めない、兄弟ディレクトリのファイルです。</summary>
    internal string SentinelPath { get; }

    /// <summary>走査で変更してはいけない sentinel の元 bytes です。</summary>
    internal ReadOnlyMemory<byte> SentinelBytes => sentinelBytes;

    /// <summary>CP932 の日本語 title を持ち、外部変更で削除する譜面です。</summary>
    internal Sc01ChartManifest A { get; }

    /// <summary>本文を変更せず、既存利用者列の保持と検索を確認する UTF-8 譜面です。</summary>
    internal Sc01ChartManifest B { get; }

    /// <summary>初回・再走査を通して維持する bmson 譜面です。</summary>
    internal Sc01ChartManifest C { get; }

    /// <summary>外部変更で追加し、共通検索語に一致する譜面です。</summary>
    internal Sc01ChartManifest D { get; }

    /// <summary>初回走査後に存在するべき譜面集合です。</summary>
    internal IReadOnlyList<Sc01ChartManifest> InitialCharts { get; }

    /// <summary>外部変更後の再走査で存在するべき譜面集合です。</summary>
    internal IReadOnlyList<Sc01ChartManifest> UpdatedCharts { get; }

    /// <summary>標準更新確認に渡す、外部通信を行わない固定 NoUpdate manifest の URL です。</summary>
    internal string UpdateManifestUrl => updateEndpoint.UpdateManifestUrl;

    /// <summary>B の既存タグとして、初期 DB に保存する値です。</summary>
    internal const string PersistedTag = "SC01-persist";

    /// <summary>B の既存お気に入りとして、初期 DB に保存する値です。</summary>
    internal const int PersistedFavorite = 3;

    /// <summary>B の既存追加日時として、初期 DB に保存する固定 Unix 秒です。</summary>
    internal const int PersistedAddDate = 1_700_000_000;

    /// <summary>アプリ起動前の入力だけを構築します。本番 parser とグローバル設定は使用しません。</summary>
    internal static Sc01LibraryFixture Create(TestWorkspace workspace) => new(workspace);

    /// <summary>A のファイルを削除して D を追加します。B・C・DB・表示状態は書き換えません。</summary>
    internal void ApplyExternalChanges()
    {
        File.Delete(A.Path);
        WriteChart(D);
    }

    /// <summary>実アプリ終了後に HTTP 待受と処理中接続を終了し、障害は呼出元へ返します。</summary>
    public ValueTask DisposeAsync() => updateEndpoint.DisposeAsync();

    private void SeedDatabase(TestWorkspace workspace)
    {
        var scope = new ApplicationDatabaseScope();
        string executable = Path.Combine(AppContext.BaseDirectory, "fixtures", "application", "BeMusicSeeker.exe");
        var paths = ApplicationPathSnapshot.FromLayout(executable, Path.GetDirectoryName(executable)
            ?? throw new InvalidOperationException("実行体の親がありません。"), ProfileDirectory,
            Path.Combine(AppContext.BaseDirectory, "fixtures", "application", "lang"), workspace.GetPath("temp"), workspace.GetPath("legacy"));
        StandaloneLibraryDatabase.EnsurePortableSongDb(scope, paths);
        using LR2SongDBExtended db = scope.OpenSongWrite(SongDatabasePath);
        if (!Lr2SongFolderParentNormalizer.TryComputeExpectedHashes(B.Path, out string folder, out string parent))
        {
            throw new InvalidOperationException("B の保存用ディレクトリ情報を構成できません。");
        }
        int date = checked((int)new DateTimeOffset(BLastWriteTimeUtc).ToUnixTimeSeconds());
        // 同内容 BMS の走査は maintenance を再生成しないため、B は保存済みの入力一式を起動前に用意します。
        // 資源件数・文字コードは明示した入力契約から固定し、本番 parser で期待値を逆生成しません。
        db.RunInTransaction(() =>
        {
            db.Execute("INSERT INTO song (path, hash, title, artist, folder, parent, tag, favorite, adddate, date, txt, mode, level, judge) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                B.Path, B.Md5, B.Title, "SC01", folder, parent, PersistedTag, PersistedFavorite, PersistedAddDate, date, 0, 7, 2, 2);
            db.Execute("INSERT INTO chart_digest_map (md5, sha256) VALUES (?, ?)", B.Md5, B.Sha256);
            db.Execute("INSERT INTO maintenance (path, hash, encoding, is_encoding_fixed, wav_files_defined, wav_files_existing, bga_files_defined, bga_files_existing, movie_files_defined, movie_files_existing, is_stagefile_defined, is_stagefile_existing, is_banner_defined, is_banner_existing, is_backbmp_defined, is_backbmp_existing) VALUES (?, ?, 'utf-8', 0, 1, 1, 0, 0, 0, 0, 0, NULL, 0, NULL, 0, NULL)",
                B.Path, B.Md5);
        });
    }

    private static Sc01ChartManifest CreateChart(string path, string title, Sc01ChartFormat format, byte[] bytes)
    {
        byte[] wav = CreateWave();
        var resource = new Sc01ResourceManifest(Path.Combine(Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("譜面の親がありません。"), "tone.wav"), "audio", wav);
        return new Sc01ChartManifest(path, title, format, bytes,
            Convert.ToHexStringLower(MD5.HashData(bytes)), Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Array.AsReadOnly(new[] { resource }));
    }

    private static void WriteChart(Sc01ChartManifest chart)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(chart.Path) ?? throw new InvalidOperationException("譜面の親がありません。"));
        File.WriteAllBytes(chart.Path, chart.RawBytes.ToArray());
        foreach (Sc01ResourceManifest resource in chart.Resources) { File.WriteAllBytes(resource.Path, resource.RawBytes.ToArray()); }
    }

    private static byte[] CreateWave()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        const int sampleCount = 80;
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + sampleCount * sizeof(short));
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(8000);
        writer.Write(16000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(sampleCount * sizeof(short));
        for (int index = 0; index < sampleCount; index++) { writer.Write((short)(index % 8 < 4 ? 2048 : -2048)); }
        writer.Flush();
        return stream.ToArray();
    }

}

/// <summary>fixture が明示する譜面形式です。本番 parser の判定から期待値を作りません。</summary>
internal enum Sc01ChartFormat
{
    /// <summary>BMS 系のテキスト譜面です。</summary>
    Bms,
    /// <summary>bmson JSON 譜面です。</summary>
    Bmson
}

/// <summary>parser に依存せず宣言した、SC01 の入力 bytes と期待する保存値です。</summary>
/// <param name="Path">譜面の絶対パスです。</param>
/// <param name="Title">期待する title です。</param>
/// <param name="Format">明示した譜面形式です。</param>
/// <param name="RawBytes">変更してはいけない元 bytes です。</param>
/// <param name="Md5">元 bytes の MD5 です。</param>
/// <param name="Sha256">元 bytes の SHA-256 です。</param>
/// <param name="Resources">使用資源の種類・パス・元 bytes です。</param>
internal sealed record Sc01ChartManifest(string Path, string Title, Sc01ChartFormat Format, ReadOnlyMemory<byte> RawBytes,
    string Md5, string Sha256, IReadOnlyList<Sc01ResourceManifest> Resources);

/// <summary>fixture が明示した使用資源です。</summary>
/// <param name="Path">資源の絶対パスです。</param>
/// <param name="Kind">資源種別です。この fixture は audio のみを使用します。</param>
/// <param name="RawBytes">変更してはいけない実 WAV の bytes です。</param>
internal sealed record Sc01ResourceManifest(string Path, string Kind, ReadOnlyMemory<byte> RawBytes);
