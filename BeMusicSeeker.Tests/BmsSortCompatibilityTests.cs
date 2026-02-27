using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Util;
using SQLite;

namespace BeMusicSeeker.Tests;

/// <summary>
/// ソート最適化後の並び順が従来ロジックと一致することを検証します。
/// </summary>
[TestClass]
public sealed class BmsSortCompatibilityTests
{
    private const int SampleSongCount = 20000;
    private const int PerfWarmupCount = 1;
    private const int PerfMeasureCount = 3;

    private static readonly string TestSongDbRelativePath = Path.Combine("TestData", "song_snapshot", "song.db");

    public TestContext? TestContext { get; set; }

    /// <summary>
    /// 代表カラムの昇順/降順で従来ロジックとの完全一致を検証します。
    /// </summary>
    [TestMethod]
    [TestCategory("Compatibility")]
    [Microsoft.VisualStudio.TestTools.UnitTesting.Ignore("Manual compatibility check. Excluded from default build/test pass criteria.")]
    public void SortOrder_ShouldMatchLegacyImplementation_ForRepresentativeColumns()
    {
        string testSongDbFullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, TestSongDbRelativePath);
        Assert.IsTrue(File.Exists(testSongDbFullPath), "Test song.db not found: " + testSongDbFullPath);

        List<BMSFile> sourceRows = LoadRowsFromSongDb(testSongDbFullPath, SampleSongCount);
        Assert.IsTrue(sourceRows.Count > 0, "No song rows loaded from test song.db.");

        CultureInfo previousCurrentCulture = CultureInfo.CurrentCulture;
        CultureInfo previousCurrentUICulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo stableCulture = CultureInfo.GetCultureInfo("ja-JP");
            CultureInfo.CurrentCulture = stableCulture;
            CultureInfo.CurrentUICulture = stableCulture;

            List<(string columnName, ListSortDirection direction)> sortCases = new List<(string, ListSortDirection)>
            {
                (nameof(BMSFile.Title), ListSortDirection.Ascending),
                (nameof(BMSFile.Title), ListSortDirection.Descending),
                (nameof(BMSFile.Artist), ListSortDirection.Ascending),
                (nameof(BMSFile.path), ListSortDirection.Ascending),
                (nameof(BMSFile.Level), ListSortDirection.Ascending),
                (nameof(BMSFile.mode), ListSortDirection.Ascending),
                (nameof(BMSFile.notes), ListSortDirection.Descending)
            };

            foreach ((string columnName, ListSortDirection direction) in sortCases)
            {
                MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
                {
                    ColumnsName = columnName,
                    Direction = direction
                };

                List<BMSFile> legacyResult = SortByLegacyImplementation(sourceRows, sortParameters);
                List<BMSFile> optimizedResult = BMSFileSortEngine.Sort(sourceRows, sortParameters);

                Assert.AreEqual(legacyResult.Count, optimizedResult.Count, $"Row count mismatch for {columnName}/{direction}");

                string legacyDigest = ComputeDigest(legacyResult);
                string optimizedDigest = ComputeDigest(optimizedResult);
                if (!string.Equals(legacyDigest, optimizedDigest, StringComparison.Ordinal))
                {
                    int mismatchIndex = FindFirstMismatchIndex(legacyResult, optimizedResult);
                    string legacyRow = (mismatchIndex >= 0 && mismatchIndex < legacyResult.Count) ? (legacyResult[mismatchIndex]?.path ?? string.Empty) : "(n/a)";
                    string optimizedRow = (mismatchIndex >= 0 && mismatchIndex < optimizedResult.Count) ? (optimizedResult[mismatchIndex]?.path ?? string.Empty) : "(n/a)";
                    Assert.Fail($"Sort order mismatch for {columnName}/{direction} at index={mismatchIndex} legacy={legacyRow} optimized={optimizedRow}");
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCurrentCulture;
            CultureInfo.CurrentUICulture = previousCurrentUICulture;
        }
    }

    /// <summary>
    /// 旧実装と最適化実装のソート処理時間を比較出力します（参考計測）。
    /// </summary>
    [TestMethod]
    [TestCategory("Performance")]
    [DoNotParallelize]
    public void SortPerformance_ReportLegacyVsOptimized_ForRepresentativeColumns()
    {
        string testSongDbFullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, TestSongDbRelativePath);
        Assert.IsTrue(File.Exists(testSongDbFullPath), "Test song.db not found: " + testSongDbFullPath);

        List<BMSFile> sourceRows = LoadRowsFromSongDb(testSongDbFullPath, SampleSongCount);
        Assert.IsTrue(sourceRows.Count > 0, "No song rows loaded from test song.db.");

        List<(string columnName, ListSortDirection direction)> sortCases = new List<(string, ListSortDirection)>
        {
            (nameof(BMSFile.Level), ListSortDirection.Descending),
            (nameof(BMSFile.path), ListSortDirection.Ascending),
            (nameof(BMSFile.totalnotes), ListSortDirection.Descending),
            (nameof(BMSFile.mode), ListSortDirection.Ascending),
            (nameof(BMSFile.rate), ListSortDirection.Descending),
            (nameof(BMSFile.scoreDifficulty), ListSortDirection.Descending),
            (nameof(BMSFile.rankingLastupdate), ListSortDirection.Descending)
        };

        foreach ((string columnName, ListSortDirection direction) in sortCases)
        {
            MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
            {
                ColumnsName = columnName,
                Direction = direction
            };

            for (int warmup = 0; warmup < PerfWarmupCount; warmup++)
            {
                _ = SortByLegacyImplementation(sourceRows, sortParameters);
                _ = BMSFileSortEngine.Sort(sourceRows, sortParameters, out _);
            }

            List<long> legacyMs = new List<long>(PerfMeasureCount);
            List<long> optimizedMs = new List<long>(PerfMeasureCount);
            for (int i = 0; i < PerfMeasureCount; i++)
            {
                Stopwatch swLegacy = Stopwatch.StartNew();
                _ = SortByLegacyImplementation(sourceRows, sortParameters);
                swLegacy.Stop();
                legacyMs.Add(swLegacy.ElapsedMilliseconds);

                Stopwatch swOptimized = Stopwatch.StartNew();
                _ = BMSFileSortEngine.Sort(sourceRows, sortParameters, out string sortProfile);
                swOptimized.Stop();
                optimizedMs.Add(swOptimized.ElapsedMilliseconds);

                TestContext?.WriteLine($"sort_perf_iter column={columnName} direction={direction} iter={i + 1} legacyMs={swLegacy.ElapsedMilliseconds} optimizedMs={swOptimized.ElapsedMilliseconds} sortProfile={sortProfile} rows={sourceRows.Count}");
            }

            long legacyMedian = Median(legacyMs);
            long optimizedMedian = Median(optimizedMs);
            double ratio = legacyMedian == 0 ? 0.0 : (double)optimizedMedian / legacyMedian;
            TestContext?.WriteLine($"sort_perf_summary column={columnName} direction={direction} rows={sourceRows.Count} legacyMedianMs={legacyMedian} optimizedMedianMs={optimizedMedian} ratio={ratio:F3}");
        }
    }

    /// <summary>
    /// song.db からソート検証に必要な行を読み込みます。
    /// </summary>
    /// <param name="songDbPath">song.db の絶対パス。</param>
    /// <param name="limit">読み込み上限件数。</param>
    /// <returns>検証対象の BMS 行。</returns>
    private static List<BMSFile> LoadRowsFromSongDb(string songDbPath, int limit)
    {
        using SQLiteConnection connection = new SQLiteConnection(songDbPath, SQLiteOpenFlags.ReadOnly);
        string sql = "SELECT path, hash, title, subtitle, artist, subartist, genre, tag, level, mode, karinotes FROM song WHERE path IS NOT NULL ORDER BY path LIMIT ?";
        List<SongSnapshotRow> rows = connection.Query<SongSnapshotRow>(sql, limit);
        List<BMSFile> result = new List<BMSFile>(rows.Count);
        for (int index = 0; index < rows.Count; index++)
        {
            SongSnapshotRow row = rows[index];
            if (string.IsNullOrWhiteSpace(row.path))
            {
                continue;
            }
            TestableBmsFile bmsFile = new TestableBmsFile();
            bmsFile.ApplySnapshot(row);
            result.Add(bmsFile);
        }
        return result;
    }

    /// <summary>
    /// 最適化前の実装と同等のソートをテスト側で再現します。
    /// </summary>
    /// <param name="source">ソート対象。</param>
    /// <param name="sortParameters">ソート条件。</param>
    /// <returns>ソート済みリスト。</returns>
    private static List<BMSFile> SortByLegacyImplementation(IEnumerable<BMSFile> source, MainWindowViewModel.cSortParameters sortParameters)
    {
        IEnumerable<BMSFile> safeSource = source ?? Enumerable.Empty<BMSFile>();
        string? columnName = sortParameters?.ColumnsName;
        ListSortDirection direction = sortParameters?.Direction ?? ListSortDirection.Ascending;
        if (string.IsNullOrWhiteSpace(columnName))
        {
            columnName = nameof(BMSFile.Title);
        }
        if (string.Equals(columnName, nameof(BMSFile.rank), StringComparison.Ordinal))
        {
            columnName = nameof(BMSFile.rateDouble);
        }
        PropertyInfo? property = typeof(BMSFile).GetProperty(columnName);
        Func<BMSFile, string> keySelector = delegate (BMSFile row)
        {
            if (row == null || property == null)
            {
                return string.Empty;
            }
            object value = property.GetValue(row);
            if (value == null)
            {
                return string.Empty;
            }
            if (property.PropertyType.IsEnum)
            {
                return ((int)value).ToString();
            }
            return value.ToString() ?? string.Empty;
        };
        if (direction == ListSortDirection.Ascending)
        {
            return safeSource.OrderBy(keySelector, new LegacyNaturalComparer<string>()).ThenBy((BMSFile row) => row?.Title ?? string.Empty, new LegacyNaturalComparer<string>()).ToList();
        }
        return safeSource.OrderByDescending(keySelector, new LegacyNaturalComparer<string>(isWhiteSpacePrior: true)).ThenBy((BMSFile row) => row?.Title ?? string.Empty, new LegacyNaturalComparer<string>()).ToList();
    }

    /// <summary>
    /// ソート結果比較用の SHA-256 ダイジェストを作成します。
    /// </summary>
    /// <param name="rows">ソート結果。</param>
    /// <returns>比較用ダイジェスト。</returns>
    private static string ComputeDigest(IEnumerable<BMSFile> rows)
    {
        StringBuilder builder = new StringBuilder();
        foreach (BMSFile row in rows)
        {
            builder.Append(row?.path ?? string.Empty).Append('\t').Append(row?.hash ?? string.Empty).Append('\n');
        }
        byte[] bytes = Encoding.UTF8.GetBytes(builder.ToString());
        byte[] hash;
        using (SHA256 sha256 = SHA256.Create())
        {
            hash = sha256.ComputeHash(bytes);
        }
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// 中央値を返します。
    /// </summary>
    /// <param name="values">計測値。</param>
    /// <returns>中央値。</returns>
    private static long Median(List<long> values)
    {
        if (values == null || values.Count == 0)
        {
            return 0L;
        }
        List<long> sorted = values.OrderBy((long v) => v).ToList();
        return sorted[sorted.Count / 2];
    }

    /// <summary>
    /// 2つの並び順の最初の差分インデックスを返します。
    /// </summary>
    private static int FindFirstMismatchIndex(List<BMSFile> left, List<BMSFile> right)
    {
        int count = Math.Min(left.Count, right.Count);
        for (int i = 0; i < count; i++)
        {
            BMSFile l = left[i] ?? throw new InvalidOperationException("Left row is null at index " + i);
            BMSFile r = right[i] ?? throw new InvalidOperationException("Right row is null at index " + i);
            if (!string.Equals(l.path, r.path, StringComparison.Ordinal) || !string.Equals(l.hash, r.hash, StringComparison.Ordinal))
            {
                return i;
            }
        }
        if (left.Count != right.Count)
        {
            return count;
        }
        return -1;
    }

    [Table("song")]
    private sealed class SongSnapshotRow
    {
        public string? path { get; set; }

        public string? hash { get; set; }

        public string? title { get; set; }

        public string? subtitle { get; set; }

        public string? artist { get; set; }

        public string? subartist { get; set; }

        public string? genre { get; set; }

        public string? tag { get; set; }

        public int? level { get; set; }

        public int? mode { get; set; }

        public int? karinotes { get; set; }
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void ApplySnapshot(SongSnapshotRow row)
        {
            path = row.path ?? string.Empty;
            hash = row.hash ?? string.Empty;
            title = row.title ?? string.Empty;
            subtitle = row.subtitle ?? string.Empty;
            artist = row.artist ?? string.Empty;
            subartist = row.subartist ?? string.Empty;
            genre = row.genre ?? string.Empty;
            tag = row.tag ?? string.Empty;
            level = row.level;
            mode = row.mode;
            karinotes = row.karinotes;
        }
    }
}
