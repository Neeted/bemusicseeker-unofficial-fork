using System;
using System.Collections.Generic;
using System.ComponentModel;
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

    private static readonly string TestSongDbRelativePath = Path.Combine("TestData", "song_snapshot", "song.db");

    /// <summary>
    /// 代表カラムの昇順/降順で従来ロジックとの完全一致を検証します。
    /// </summary>
    [TestMethod]
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
                Assert.AreEqual(legacyDigest, optimizedDigest, $"Sort order mismatch for {columnName}/{direction}");
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCurrentCulture;
            CultureInfo.CurrentUICulture = previousCurrentUICulture;
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
        Func<BMSFile, string> keySelector = delegate(BMSFile row)
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
            return safeSource.OrderBy(keySelector, new NaturalComparer<string>()).ThenBy((BMSFile row) => row?.Title ?? string.Empty, new NaturalComparer<string>()).ToList();
        }
        return safeSource.OrderByDescending(keySelector, new NaturalComparer<string>(isWhiteSpacePrior: true)).ThenBy((BMSFile row) => row?.Title ?? string.Empty, new NaturalComparer<string>()).ToList();
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
