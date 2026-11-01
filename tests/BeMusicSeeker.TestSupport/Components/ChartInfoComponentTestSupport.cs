using System.Globalization;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Files;
namespace BeMusicSeeker.TestSupport.Components;

/// <summary>旧テストとCoreテストで同一の入力・期待値・寿命を共有します。</summary>
internal static class ChartInfoComponentTestSupport
{
    /// <summary>参照値の地雷featureを共用し、期待値の二重定義を防ぎます。</summary>
    internal const int FeatureMine = 2;

    /// <summary>参照値のRANDOM featureを共用し、期待値の二重定義を防ぎます。</summary>
    internal const int FeatureRandom = 4;

    /// <summary>参照値の長ノートfeatureを共用し、期待値の二重定義を防ぎます。</summary>
    internal const int FeatureLongByLnMode = 8;

    /// <summary>参照値のcharge note featureを共用し、期待値の二重定義を防ぎます。</summary>
    internal const int FeatureChargeNote = 16;

    /// <summary>参照値のSTOP featureを共用し、期待値の二重定義を防ぎます。</summary>
    internal const int FeatureStop = 64;

    /// <summary>参照値のSCROLL featureを共用し、期待値の二重定義を防ぎます。</summary>
    internal const int FeatureScroll = 128;

    /// <summary>補完の実保存件数を読み取り専用接続で観測し、検査側でDBを更新しません。</summary>
    /// <param name="databaseScope">補完対象ライブラリと共有する接続所有者。</param>
    /// <param name="songDbPath">観測対象のsong.db。</param>
    /// <param name="sha256">保存件数を観測する譜面のSHA-256。</param>
    internal static long CountChartInfoRows(ApplicationDatabaseScope databaseScope, string songDbPath, string sha256)
    {
        using LR2SongDBExtended songDb = databaseScope.OpenSongReadOnly(songDbPath);
        return songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ?;", sha256);
    }

    /// <summary>Java参照値と同じdoubleビット列で数値互換性を検査します。</summary>
    internal static void AssertJavaDoubleParseBits(string value, string expectedHexBits)
    {
        Assert.AreEqual(expectedHexBits, JavaDoubleParserJdk17.ParseDoubleBits(value).ToString("x16", CultureInfo.InvariantCulture), value);
    }

    /// <summary>共通の詳細列を同じ期待値と許容差で比較します。</summary>
    internal static void AssertChartInfoEquivalent(BeMusicSeeker.Models.ChartDetails expected, BeMusicSeeker.Models.ChartDetails actual)
    {
        Assert.AreEqual(expected.sha256, actual.sha256);
        Assert.AreEqual(expected.md5, actual.md5);
        Assert.AreEqual(expected.charthash, actual.charthash);
        Assert.AreEqual(expected.level, actual.level);
        Assert.AreEqual(expected.difficulty, actual.difficulty);
        Assert.AreEqual(expected.difficulty_defined, actual.difficulty_defined);
        Assert.AreEqual(expected.mainbpm, actual.mainbpm);
        Assert.AreEqual(expected.maxbpm, actual.maxbpm);
        Assert.AreEqual(expected.minbpm, actual.minbpm);
        Assert.AreEqual(expected.length, actual.length);
        Assert.AreEqual(expected.mode, actual.mode);
        Assert.AreEqual(expected.judge, actual.judge);
        Assert.AreEqual(expected.bga, actual.bga);
        Assert.AreEqual(expected.exlevel, actual.exlevel);
        Assert.AreEqual(expected.feature, actual.feature);
        Assert.AreEqual(expected.notes, actual.notes);
        Assert.AreEqual(expected.n, actual.n);
        Assert.AreEqual(expected.ln, actual.ln);
        Assert.AreEqual(expected.s, actual.s);
        Assert.AreEqual(expected.ls, actual.ls);
        Assert.AreEqual(expected.total, actual.total);
        Assert.AreEqual(expected.total_defined, actual.total_defined);
        Assert.AreEqual(expected.density, actual.density);
        Assert.AreEqual(expected.peakdensity, actual.peakdensity);
        Assert.AreEqual(expected.enddensity, actual.enddensity);
        Assert.AreEqual(expected.distribution, actual.distribution);
        Assert.AreEqual(expected.speedchange, actual.speedchange);
        Assert.AreEqual(expected.speedchange_count, actual.speedchange_count);
        Assert.AreEqual(expected.lanenotes, actual.lanenotes);
        Assert.AreEqual(expected.parser_version, actual.parser_version);
    }

    /// <summary>表示に引き継ぐ詳細列を同じ期待値と許容差で比較します。</summary>
    internal static void AssertChartInfoDisplayProjectionEquivalent(BeMusicSeeker.Models.ChartDetails expected, BeMusicSeeker.Models.ChartDetails actual)
    {
        Assert.AreEqual(expected.sha256, actual.sha256);
        Assert.AreEqual(expected.md5, actual.md5);
        Assert.AreEqual(expected.level, actual.level);
        Assert.AreEqual(expected.difficulty, actual.difficulty);
        Assert.AreEqual(expected.difficulty_defined, actual.difficulty_defined);
        Assert.AreEqual(expected.mainbpm, actual.mainbpm);
        Assert.AreEqual(expected.maxbpm, actual.maxbpm);
        Assert.AreEqual(expected.minbpm, actual.minbpm);
        Assert.AreEqual(expected.length, actual.length);
        Assert.AreEqual(expected.mode, actual.mode);
        Assert.AreEqual(expected.judge, actual.judge);
        Assert.AreEqual(expected.bga, actual.bga);
        Assert.AreEqual(expected.exlevel, actual.exlevel);
        Assert.AreEqual(expected.feature, actual.feature);
        Assert.AreEqual(expected.notes, actual.notes);
        Assert.AreEqual(expected.n, actual.n);
        Assert.AreEqual(expected.ln, actual.ln);
        Assert.AreEqual(expected.s, actual.s);
        Assert.AreEqual(expected.ls, actual.ls);
        Assert.AreEqual(expected.total, actual.total);
        Assert.AreEqual(expected.total_defined, actual.total_defined);
        Assert.AreEqual(expected.density, actual.density);
        Assert.AreEqual(expected.peakdensity, actual.peakdensity);
        Assert.AreEqual(expected.enddensity, actual.enddensity);
        Assert.AreEqual(expected.speedchange_count, actual.speedchange_count);
        Assert.AreEqual(expected.parser_version, actual.parser_version);
        Assert.IsNull(actual.charthash);
        Assert.IsNull(actual.distribution);
        Assert.IsNull(actual.speedchange);
        Assert.IsNull(actual.lanenotes);
    }

    internal static void AssertNullableDouble(double? expected, double? actual, string message)
    {
        if (!expected.HasValue || !actual.HasValue)
        {
            Assert.AreEqual(expected.HasValue, actual.HasValue, message);
            return;
        }
        Assert.IsTrue(Math.Abs(expected.Value - actual.Value) <= 0.000001, message + " expected=" + expected.Value.ToString("R", CultureInfo.InvariantCulture) + " actual=" + actual.Value.ToString("R", CultureInfo.InvariantCulture));
    }

    internal static BeMusicSeeker.Models.ChartDetails CreateChartInfoRow(string sha256, string md5, int parserVersion)
    {
        return new BeMusicSeeker.Models.ChartDetails
        {
            sha256 = sha256,
            md5 = md5,
            charthash = new string('c', 64),
            level = 1,
            difficulty = 1,
            difficulty_defined = true,
            mainbpm = 120.0,
            maxbpm = 120.0,
            minbpm = 120.0,
            length = 0,
            mode = 7,
            judge = 100,
            bga = 0,
            exlevel = 0,
            feature = FeatureLongByLnMode,
            notes = 1,
            n = 1,
            ln = 0,
            s = 0,
            ls = 0,
            total = 260.0,
            total_defined = false,
            density = 1.0,
            peakdensity = 1.0,
            enddensity = 0.0,
            distribution = "#",
            speedchange = "120.0,0.0",
            speedchange_count = 0,
            lanenotes = "1,0,0",
            parser_version = parserVersion,
            updated_at = DateTime.UtcNow
        };
    }

    internal static LR2SongDBExtended.chart_digest_map CreateChartDigestRow(string md5, string sha256)
    {
        return new LR2SongDBExtended.chart_digest_map
        {
            md5 = md5,
            sha256 = sha256
        };
    }

    /// <summary>補完対象の集計を観測する最小のsong行を実DBへ保存します。</summary>
    internal static void InsertSongForSummary(LR2SongDBExtended songDb, string path, string md5)
    {
        ChartFile file = ChartTestValues.Empty() with
        {
            Path = path
        };
        file = file with { Md5 = md5 };
        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
    }

    internal static BeMusicSeeker.Models.ChartParseFailure CreateChartInfoParseFailureRow(string md5, string sha256, string path, int parserVersion, string failureKind, string exceptionType, string message, int? parseTimeoutMs)
    {
        return new BeMusicSeeker.Models.ChartParseFailure
        {
            md5 = md5,
            sha256 = sha256,
            path = path,
            parser_version = parserVersion,
            failure_kind = failureKind,
            exception_type = exceptionType,
            message = message,
            parse_timeout_ms = parseTimeoutMs,
            updated_at = DateTime.UtcNow
        };
    }

    /// <summary>継続位置だけを変えた同一形式のBMSON固定入力を作ります。</summary>
    internal static string CreateBmsonLongNoteWithContinuation(int continuationY)
    {
        return "{"
            + "\"version\":\"1.0.0\","
            + "\"info\":{\"level\":1,\"mode_hint\":\"beat-7k\",\"init_bpm\":120,\"judge_rank\":100,\"total\":100,\"resolution\":240,\"ln_type\":2},"
            + "\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":0,\"l\":480},{\"x\":0,\"y\":" + continuationY.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"c\":true}]}]"
            + "}";
    }

    /// <summary>実DB接続をcallback内で終端させ、同じ寿命でケース専有の可変媒体を回収します。</summary>
    internal static void WithTemporarySongDb(Action<string, string> testAction)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempRootPath = Path.Combine(workspace.RootDirectory, "BeMusicSeeker_ChartInfoTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        testAction(tempRootPath, songDbPath);
    }

    internal static async Task WithTemporarySongDb(Func<string, string, Task> testAction)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string tempRootPath = Path.Combine(workspace.RootDirectory, "BeMusicSeeker_ChartInfoTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        await testAction(tempRootPath, songDbPath);
    }

    /// <summary>実保存ownerを接続して詳細補完と確定通知を同じ順序で実行します。</summary>
    internal static ChartInfoBackfillResult BackfillChartInfos(
        ChartInfoBuildService service,
        BmsLibraryDbGateway gateway,
        IEnumerable<ChartFile>? currentFiles,
        IEnumerable<ChartFile>? currentBmsonSongs,
        Action<int, int, string>? reportProgress = null,
        Action<string>? logInstallPerformance = null,
        Action<string>? logInstallPerformanceWarn = null,
        Action<ChartInfoStorageCommitPublication>? storageCommitPublished = null,
        IReadOnlyDictionary<string, BeMusicSeeker.Models.ChartDetails>? existingRowsSnapshot = null)
    {
        var mutationOwner = new CatalogMutationOwner(new CatalogOwnedCollectionOwner(), gateway);
        return service.BackfillChartInfos(
            gateway,
            CreateChartSnapshot(currentFiles, currentBmsonSongs),
            reportProgress,
            logInstallPerformance,
            logInstallPerformanceWarn,
            storageCommitPublished,
            existingRowsSnapshot,
            mutationOwner.ApplyChartInfoStorageWrite);
    }

    /// <summary>BMSとBMSONの指定入力を同じ順序で一つのsnapshotへ集約します。</summary>
    internal static List<ChartFile> CreateChartSnapshot(
        IEnumerable<ChartFile>? currentFiles,
        IEnumerable<ChartFile>? currentBmsonSongs)
    {
        List<ChartFile> charts = [];
        charts.AddRange(currentFiles ?? []);
        charts.AddRange(currentBmsonSongs ?? []);
        return charts;
    }

}
