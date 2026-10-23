using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.OwnedChartCollectionTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>DB起動後の保守操作で、対象外のsong全列と背景行を観測する共有fixtureです。</summary>
internal sealed class MaintenanceMetadataSongFixture : IDisposable
{
    internal const string Title = "日本語 title";
    internal const string Subtitle = "[副題]";
    internal const string Artist = "作曲者 artist";
    internal const string Subartist = "編曲者 subartist";
    internal const string Genre = "ジャンル genre";
    private readonly BmsLibraryDbGateway gateway;
    private readonly Dictionary<string, string> before;
    private readonly Dictionary<string, string> backgroundBefore;
    private readonly string backgroundPath;

    /// <summary>実ファイルの識別・固定mtimeと、全保存列の非既定値を準備し、本番読込み・詳細hydrateを通します。</summary>
    internal MaintenanceMetadataSongFixture(string songDbPath, bool matchingMetadata = false)
    {
        string root = System.IO.Path.GetDirectoryName(songDbPath) ?? throw new ArgumentException("親ディレクトリを持つDBパスが必要です。", nameof(songDbPath));
        Path = System.IO.Path.Combine(root, "metadata.bms");
        backgroundPath = System.IO.Path.Combine(root, "background.bms");
        File.WriteAllText(Path, $"#PLAYER 1\n#TITLE {Title}\n#SUBTITLE {Subtitle}\n#ARTIST {Artist}\n#SUBARTIST {Subartist}\n#GENRE {Genre}\n#BPM 120\n#WAV01 missing.wav\n#00111:01\n", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(Path, new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        File.WriteAllText(backgroundPath, "#TITLE background\n");
        ChartFile parsed = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path));
        gateway = new BmsLibraryDbGateway(songDbPath);
        gateway.EnsureChartInfoBackfillSchema();
        gateway.UpsertChartInfos([ChartInfoMetadataTestSupport.CreateChartInfoRow(parsed.Sha256, parsed.Md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)]);
        BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, db =>
        {
            LR2SongDB.song row = CreateRow(Path, parsed.Md5);
            if (matchingMetadata)
            {
                row.title = Title; row.subtitle = Subtitle; row.artist = Artist; row.subartist = Subartist; row.genre = Genre;
            }
            db.InsertOrReplace(row);
            db.InsertOrReplace(CreateRow(backgroundPath, new string('d', 32)));
            db.InsertOrReplace(ChartInfoMetadataTestSupport.CreateChartDigestRow(parsed.Md5, parsed.Sha256));
            db.InsertOrReplace(new LR2SongDBExtended.maintenance { path = Path, hash = parsed.Md5, encoding = "utf-8", is_encoding_fixed = false });
        });
        SongTableLoadResult loaded = new BmsLibraryInitializationService().LoadSongTable(gateway,
            new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false }, null, new TestFileMutationService(), null, exception => exception.Message);
        MaintenanceTableHydrationResult maintenance = new BmsLibraryInitializationService().LoadMaintenanceTable(gateway,
            new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false });
        Library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(),
            new FileDbReportRecordingDialogs(), new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false })
        {
            BmsCharts = loaded.LoadedFiles.Select(chart => maintenance.MaintenanceMap.TryGetValue(chart.Path, out ResourceHealthMaintenanceSnapshot? saved)
                ? ChartFileProjection.WithMaintenance(chart, saved) : chart).ToArray(),
            BmsonCharts = []
        };
        using (LibraryFileMutationLease lease = Library.TryBeginLibraryFileMutation("metadata-fixture-hydration"))
        {
            Assert.IsNotNull(lease);
            RequiredChartInfoHydrationResult hydrated = Library.EnsureRequiredStartupData(lease.CreateMutationCapability());
            Assert.IsTrue(hydrated.Succeeded);
            Assert.AreEqual(1, hydrated.CandidateOwnerCount, "背景一行だけが詳細未保存です。");
        }
        Assert.IsNull(Chart.ChartInfo, "DBから読んだ所持現在値へ詳細を付け直さない起動状態を使います。");
        Assert.AreEqual("utf-8", Chart.EncodingName, "明示再指定と同じ文字コードを保存済みの現在値を使います。");
        using LR2SongDBExtended read = gateway.OpenSongDbReadOnly();
        before = ReadRow(read, Path);
        backgroundBefore = ReadRow(read, backgroundPath);
    }

    internal TestBmsLibrary Library { get; }
    internal string Path { get; }
    internal ChartFile Chart => Library.BmsCharts.Single(chart => chart.Path == Path);

    /// <summary>生メタデータ5列とmtimeだけの更新、その他全列・背景行・行数・保守確定を読取り専用SELECTで確認します。</summary>
    internal void AssertSaved()
    {
        using LR2SongDBExtended read = gateway.OpenSongDbReadOnly();
        Dictionary<string, string> actual = ReadRow(read, Path);
        var expected = new Dictionary<string, string>(before)
        {
            ["title"] = Quote(Title),
            ["subtitle"] = Quote(Subtitle),
            ["artist"] = Quote(Artist),
            ["subartist"] = Quote(Subartist),
            ["genre"] = Quote(Genre),
            ["date"] = new DateTimeOffset(File.GetLastWriteTimeUtc(Path)).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        foreach ((string column, string value) in expected) { Assert.AreEqual(value, actual[column], "song." + column); }
        Dictionary<string, string> background = ReadRow(read, backgroundPath);
        foreach ((string column, string value) in backgroundBefore) { Assert.AreEqual(value, background[column], "background song." + column); }
        Assert.AreEqual(2, read.ExecuteScalar<int>("SELECT COUNT(*) FROM song;"));
        Assert.AreEqual("utf-8", read.ExecuteScalar<string>("SELECT encoding FROM maintenance WHERE path = ?;", Path));
        Assert.AreEqual(1, read.ExecuteScalar<int>("SELECT is_encoding_fixed FROM maintenance WHERE path = ?;", Path));
        Assert.IsFalse(Library.OperationAdmission.IsActive);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private static Dictionary<string, string> ReadRow(LR2SongDBExtended db, string path)
        => db.GetTableInfo("song").ToDictionary(column => column.Name,
            column => db.ExecuteScalar<string>("SELECT quote(\"" + column.Name + "\") FROM song WHERE path = ?;", path));

    private static LR2SongDB.song CreateRow(string path, string hash) => new()
    {
        path = path,
        hash = hash,
        title = "old title",
        subtitle = "old subtitle",
        artist = "old artist",
        subartist = "old subartist",
        genre = "old genre",
        tag = "retained tag",
        favorite = 7,
        adddate = 1234567,
        date = 7654321,
        type = 2,
        folder = "12345678",
        parent = "87654321",
        stagefile = "kept-stage.png",
        banner = "kept-banner.png",
        backbmp = "kept-back.png",
        level = 18,
        difficulty = 4,
        maxbpm = 345,
        minbpm = 67,
        mode = 14,
        judge = 3,
        longnote = 2,
        bga = 1,
        random = 1,
        txt = 1,
        karinotes = 2345,
        exlevel = 23
    };

    public void Dispose() => Library.RequestShutdown("metadata-song-test");
}
