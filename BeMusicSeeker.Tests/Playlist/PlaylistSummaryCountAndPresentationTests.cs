using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.PlaylistSummaryAggregationTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>サマリーの所持件数・キャッシュ失効と表示用の検索・並替えを検証する。</summary>
/// <remarks>件数判断と表示投影を直接呼び、DB取込み・実一覧通知は所有者のテストへ分担する。</remarks>
[TestClass]
public sealed class PlaylistSummaryCountAndPresentationTests
{
    /// <summary>有効なハッシュ付きエントリは重複も行数として数え、ハッシュなしと削除済みは除外する。譜面集合としての重複排除や無効行の加算を検出する。</summary>
    [TestMethod]
    public void CalculatePlaylistSummaryCounts_CountsActiveHashedRowsWithoutDedup()
    {
        BMSTableEntry[] entries =
        [
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null),
            CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null),
            CreateEntry(null, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            CreateEntry(null, null),
            CreateEntry("cccccccccccccccccccccccccccccccc", null, isRemoved: true)
        ];

        PlaylistSummaryCountResult result = PlaylistCatalogSummaryOwner.CalculateTableCount(
            entries,
            CreateHashSet("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            CreateHashSet("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));

        Assert.AreEqual(3, result.TotalCharts);
        Assert.AreEqual(3, result.OwnedCharts);
    }

    /// <summary>MD5があるエントリはSHAが所持に一致してもMD5不一致なら未所持と数える。別のハッシュで所持判断を上書きする誤りを検出する。</summary>
    [TestMethod]
    public void CalculatePlaylistSummaryCounts_DoesNotFallbackToSha256WhenMd5Exists()
    {
        BMSTableEntry[] entries =
        [
            CreateEntry(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
        ];

        PlaylistSummaryCountResult result = PlaylistCatalogSummaryOwner.CalculateTableCount(
            entries,
            CreateHashSet("cccccccccccccccccccccccccccccccc"),
            CreateHashSet("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));

        Assert.AreEqual(1, result.TotalCharts);
        Assert.AreEqual(0, result.OwnedCharts);
    }

    /// <summary>MD5がないエントリはSHAによって所持数へ含める。SHAだけのエントリを未所持として扱う誤りを検出する。</summary>
    [TestMethod]
    public void CalculatePlaylistSummaryCounts_UsesSha256WhenMd5IsMissing()
    {
        BMSTableEntry[] entries =
        [
            CreateEntry(null, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
        ];

        PlaylistSummaryCountResult result = PlaylistCatalogSummaryOwner.CalculateTableCount(
            entries,
            CreateHashSet(),
            CreateHashSet("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));

        Assert.AreEqual(1, result.TotalCharts);
        Assert.AreEqual(1, result.OwnedCharts);
    }

    /// <summary>同じplaylist_idでも別のテーブル実体に変われば件数を再計算する。識別子だけで旧所持数を再利用する誤りを検出する。</summary>
    [TestMethod]
    public void PlaylistCatalogSummaryOwner_DoesNotReuseCountForReplacedTableInstance()
    {
        var ownedHashes = new OwnedChartHashIndexSnapshot();
        ownedHashes.AddMd5("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var ownedSnapshot = new OwnedChartHashIndexVersionedSnapshot(ownedHashes, version: 1, buildElapsedMs: 0, invalidationVersion: 1, catalogRevision: 1);
        var owner = new PlaylistCatalogSummaryOwner();
        var firstTable = new BMSTable
        {
            playlist_id = 42
        };
        firstTable.entries = [CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null)];

        PlaylistSummaryCountResult first = owner.GetOrBuildTableCount(
            firstTable,
            ownedSnapshot,
            CancellationToken.None,
            out bool firstCacheHit);

        var replacementTable = new BMSTable
        {
            playlist_id = 42
        };
        replacementTable.entries = [CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", null)];

        PlaylistSummaryCountResult replacement = owner.GetOrBuildTableCount(
            replacementTable,
            ownedSnapshot,
            CancellationToken.None,
            out bool replacementCacheHit);

        Assert.IsFalse(firstCacheHit);
        Assert.AreEqual(1, first.TotalCharts);
        Assert.AreEqual(1, first.OwnedCharts);
        Assert.IsFalse(replacementCacheHit);
        Assert.AreEqual(1, replacement.TotalCharts);
        Assert.AreEqual(0, replacement.OwnedCharts);
    }

    /// <summary>同じテーブルのエントリ差替後はキャッシュを使わず、新しい所持数を計算する。内容変更をキャッシュ失効へ反映しない誤りを検出する。</summary>
    [TestMethod]
    public void PlaylistCatalogSummaryOwner_InvalidatesCountAfterTableEntriesChange()
    {
        var ownedHashes = new OwnedChartHashIndexSnapshot();
        ownedHashes.AddMd5("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var ownedSnapshot = new OwnedChartHashIndexVersionedSnapshot(ownedHashes, version: 1, buildElapsedMs: 0, invalidationVersion: 1, catalogRevision: 1);
        var owner = new PlaylistCatalogSummaryOwner();
        var table = new BMSTable
        {
            playlist_id = 42
        };
        table.entries = [CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null)];

        PlaylistSummaryCountResult first = owner.GetOrBuildTableCount(
            table,
            ownedSnapshot,
            CancellationToken.None,
            out bool firstCacheHit);

        table.entries = [CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", null)];

        PlaylistSummaryCountResult changed = owner.GetOrBuildTableCount(
            table,
            ownedSnapshot,
            CancellationToken.None,
            out bool changedCacheHit);

        Assert.IsFalse(firstCacheHit);
        Assert.AreEqual(1, first.OwnedCharts);
        Assert.IsFalse(changedCacheHit);
        Assert.AreEqual(0, changed.OwnedCharts);
    }

    /// <summary>全名がキーワードaに適合する入力を名前昇順条件で要求し、完全所持のalphaを除いたbeta・gammaと件数2を返すことを確認する。</summary>
    [TestMethod]
    public void BuildPlaylistSummaryPresentationRows_AppliesOwnershipFilterAndNameSort()
    {
        List<PlaylistSummaryRow> rows =
        [
            new PlaylistSummaryRow
            {
                Name = "beta",
                Symbol = "B",
                TotalCharts = 4,
                OwnedCharts = 2
            },
            new PlaylistSummaryRow
            {
                Name = "alpha",
                Symbol = "A",
                TotalCharts = 3,
                OwnedCharts = 3
            },
            new PlaylistSummaryRow
            {
                Name = "gamma",
                Symbol = "G",
                TotalCharts = 1,
                OwnedCharts = 0
            }
        ];

        PlaylistSummaryPresentationResult result = PlaylistWorkspaceViewModel.BuildPlaylistSummaryPresentationRows(
            rows,
            "a",
            PlaylistOwnedFilter.OwnedIncomplete,
            new ChartListSortParameters
            {
                ColumnsName = nameof(PlaylistSummaryRow.Name),
                Direction = System.ComponentModel.ListSortDirection.Ascending
            });

        Assert.AreEqual(2, result.FilteredCount);
        CollectionAssert.AreEqual(new[] { "beta", "gamma" }, result.Rows.Select(row => row.Name).ToArray());
    }

    /// <summary>名前・フォルダ・prefix・記号・header・dataの全条件を満たす一行を返す。フィールドの誤対応やAND条件の欠落を検出する。</summary>
    [TestMethod]
    public void BuildPlaylistSummaryPresentationRows_KeywordFilterSupportsAndAndFieldQueries()
    {
        List<PlaylistSummaryRow> rows =
        [
            new PlaylistSummaryRow
            {
                PlaylistId = 10,
                Name = "alpha pack",
                FolderName = "folderalpha",
                CompatPrefix = "A",
                Symbol = "A",
                HeaderUri = new Uri("https://example.com/header-alpha.json"),
                DataUri = new Uri("https://example.com/data-alpha.json")
            },
            new PlaylistSummaryRow
            {
                PlaylistId = 20,
                Name = "alpha other",
                FolderName = "folderbeta",
                CompatPrefix = "B",
                Symbol = "B",
                HeaderUri = new Uri("https://example.com/header-beta.json"),
                DataUri = new Uri("https://example.com/data-beta.json")
            }
        ];

        PlaylistSummaryPresentationResult result = PlaylistWorkspaceViewModel.BuildPlaylistSummaryPresentationRows(
            rows,
            "name:alpha foldername:folderalpha prefix:A symbol:A header:header-alpha data:data-alpha",
            PlaylistOwnedFilter.All,
            new ChartListSortParameters
            {
                ColumnsName = nameof(PlaylistSummaryRow.PlaylistId),
                Direction = System.ComponentModel.ListSortDirection.Ascending
            });

        Assert.AreEqual(1, result.FilteredCount);
        Assert.AreEqual(10, result.Rows[0].PlaylistId);
    }

    /// <summary>サマリーに存在しないmd5フィールドの検索は一致なしにする。未知フィールドを無視して全件返す誤りを検出する。</summary>
    [TestMethod]
    public void BuildPlaylistSummaryPresentationRows_UnknownFieldDoesNotMatch()
    {
        List<PlaylistSummaryRow> rows =
        [
            new PlaylistSummaryRow
            {
                PlaylistId = 10,
                Name = "alpha pack",
                Symbol = "A"
            }
        ];

        PlaylistSummaryPresentationResult result = PlaylistWorkspaceViewModel.BuildPlaylistSummaryPresentationRows(
            rows,
            "md5:aaaaaaaa",
            PlaylistOwnedFilter.All,
            new ChartListSortParameters
            {
                ColumnsName = nameof(PlaylistSummaryRow.PlaylistId),
                Direction = System.ComponentModel.ListSortDirection.Ascending
            });

        Assert.AreEqual(0, result.FilteredCount);
    }

    /// <summary>引用名・記号OR・ID否定・名前正規表現を組合せ、条件に合う一行だけを返す。検索演算の解釈や複合条件の配送漏れを検出する。</summary>
    [TestMethod]
    public void BuildPlaylistSummaryPresentationRows_KeywordFilterSupportsQuoteNegationOrAndRegex()
    {
        List<PlaylistSummaryRow> rows =
        [
            new PlaylistSummaryRow
            {
                PlaylistId = 10,
                Name = "alpha pack",
                Symbol = "A"
            },
            new PlaylistSummaryRow
            {
                PlaylistId = 20,
                Name = "alpha pack",
                Symbol = "B"
            },
            new PlaylistSummaryRow
            {
                PlaylistId = 30,
                Name = "beta pack",
                Symbol = "C"
            }
        ];

        PlaylistSummaryPresentationResult result = PlaylistWorkspaceViewModel.BuildPlaylistSummaryPresentationRows(
            rows,
            "name:\"alpha pack\" symbol:A|C -id:20 name:re:^alpha",
            PlaylistOwnedFilter.All,
            new ChartListSortParameters
            {
                ColumnsName = nameof(PlaylistSummaryRow.PlaylistId),
                Direction = System.ComponentModel.ListSortDirection.Ascending
            });

        Assert.AreEqual(1, result.FilteredCount);
        Assert.AreEqual(10, result.Rows[0].PlaylistId);
    }

}
