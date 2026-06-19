using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class CustomTableColumnFactoryTests
{
    [TestMethod]
    public void CreateMainColumns_UsesVisiblePhaseThreePointFiveColumnsForStandardView()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);

        string[] ids = [.. CustomTableColumnFactory.CreateMainColumns(settings).Select(column => column.Id)];

        CollectionAssert.AreEqual(
            new[]
            {
                "Status",
                "Title",
                "Artist",
                "Genre",
                "Mode",
                "Folder",
                "Path",
                "Clear",
                "Rank",
                "Rate",
                "Bp",
                "Level",
                "ChartDifficulty",
                "ChartJudge",
                "ChartJudgePercent",
                "Notes",
                "ChartLongNotes",
                "ChartScratchNotes",
                "ChartMainBpm",
                "ChartMinBpm",
                "ChartMaxBpm",
                "ChartSoflan",
                "ChartTotal",
                "ChartTotalPerNote",
                "ChartDuration",
                "ChartFeature",
                "ChartDensity",
                "ChartPeakDensity",
                "ChartEndDensity",
                "PlaylistSymbols"
            },
            ids);
    }

    [TestMethod]
    public void CreateMainColumns_CanCreateAllMainTableColumns()
    {
        var settings = new CustomTableColumnSettings();
        foreach (CustomTableColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            layout.DisplayIndex = -1;
            layout.Visibility = Visibility.Visible;
        }

        string[] ids = [.. CustomTableColumnFactory.CreateMainColumns(settings).Select(column => column.Id)];

        CollectionAssert.AreEqual(
            new[]
            {
                "Status",
                "EntryLevel",
                "Title",
                "Artist",
                "Genre",
                "Mode",
                "Tag",
                "Url1",
                "Url2",
                "Warning",
                "Comment",
                "Memo",
                "Hash",
                "Sha256",
                "Folder",
                "Path",
                "InstallDst",
                "InstallDstTitle",
                "InstallDstArtist",
                "WavHealth",
                "BgaHealth",
                "MovieHealth",
                "CharcterEncoding",
                "PlaylistSymbols",
                "Level",
                "ChartDifficulty",
                "ChartMainBpm",
                "ChartMaxBpm",
                "ChartMinBpm",
                "ChartDuration",
                "ChartJudge",
                "ChartJudgePercent",
                "ChartFeature",
                "Notes",
                "ChartLongNotes",
                "ChartScratchNotes",
                "ChartTotal",
                "ChartTotalPerNote",
                "ChartDensity",
                "ChartPeakDensity",
                "ChartEndDensity",
                "ChartSoflan",
                "Clear",
                "Rank",
                "Rate",
                "Score",
                "Combo",
                "Bp",
                "Ranking",
                "RankingLastupdate",
                "TScore",
                "ScoreDifficulty"
            },
            ids);
    }

    [TestMethod]
    public void CreateMainColumns_KeepsStatusColumnVisibleFirstAndFixed()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAYLIST);

        CustomTableColumn status = CustomTableColumnFactory.CreateMainColumns(settings).First();

        Assert.AreEqual("Status", status.Id);
        Assert.AreEqual(18, status.Width);
        Assert.IsFalse(status.CanResize);
        Assert.IsFalse(status.CanReorder);
        Assert.IsNull(status.SortMemberPath);
        Assert.AreEqual(CustomTableCellKind.StatusIcon, status.CellKind);
    }

    [TestMethod]
    public void CreateMainColumns_KeepsModeColumnFixedAtFifty()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);

        CustomTableColumn mode = CustomTableColumnFactory.CreateMainColumns(settings).Single(column => column.Id == "Mode");

        Assert.AreEqual(50, mode.Width);
        Assert.AreEqual(50, mode.MinWidth);
        Assert.AreEqual(50, mode.MaxWidth);
        Assert.IsFalse(mode.CanResize);
    }

    [TestMethod]
    public void CreateMainColumns_UsesExpectedWidthConstraintsForHashAndRanking()
    {
        var settings = new CustomTableColumnSettings();
        foreach (CustomTableColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            layout.DisplayIndex = -1;
            layout.Visibility = Visibility.Visible;
        }
        var columns = CustomTableColumnFactory.CreateMainColumns(settings).ToDictionary(column => column.Id);

        Assert.AreEqual(40, columns["Hash"].MinWidth);
        Assert.AreEqual(240, columns["Hash"].MaxWidth);
        Assert.IsTrue(columns["Hash"].CanResize);
        Assert.AreEqual(40, columns["Ranking"].MinWidth);
        Assert.AreEqual(int.MaxValue, columns["Ranking"].MaxWidth);
        Assert.IsTrue(columns["Ranking"].CanResize);
    }

    [TestMethod]
    public void CreateMainColumns_ReflectsVisibilityWidthAndDisplayIndex()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        settings.Status.Visibility = Visibility.Hidden;
        settings.Title.Visibility = Visibility.Hidden;
        settings.ChartJudge.DisplayIndex = 0;
        settings.ChartJudge.Width = 77;

        CustomTableColumn[] columns = [.. CustomTableColumnFactory.CreateMainColumns(settings)];

        Assert.IsFalse(columns.Any(column => column.Id == "Title"));
        Assert.AreEqual("ChartJudge", columns[0].Id);
        Assert.AreEqual(77, columns[0].Width);
    }

    [TestMethod]
    public void CreateMainColumns_UsesPlayHistoryColumnsForPlayHistoryView()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);

        string[] ids = [.. CustomTableColumnFactory.CreateMainColumns(settings).Select(column => column.Id)];

        CollectionAssert.AreEqual(
            new[]
            {
                "PlayedAt",
                "FolderLabels",
                "Title",
                "BestClear",
                "BestDjLevel",
                "BestRate",
                "BestBp",
                "BestCombo",
                "Kind",
                "OpHistory"
            },
            ids);
    }

    [TestMethod]
    public void CreateMainColumns_PlayHistoryColumnsDoNotEnablePlaylistOrInstallEditing()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);
        foreach (CustomTableColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            layout.Visibility = Visibility.Visible;
        }

        CustomTableColumn[] columns = [.. CustomTableColumnFactory.CreateMainColumns(settings)];

        Assert.IsFalse(columns.Any(column => !string.IsNullOrWhiteSpace(column.EditPropertyName)));
        Assert.IsFalse(columns.Any(column => column.CellKind is CustomTableCellKind.DownloadIcon or CustomTableCellKind.CheckBox or CustomTableCellKind.ActionText));
        Assert.IsFalse(columns.Any(column => column.Id is "Url1" or "Url2" or "InstallDst"));
    }

    [TestMethod]
    public void CreateMainColumns_PlayHistoryVisibleSortMemberPathsAreAcceptedByPlayHistorySortEngine()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);
        foreach (CustomTableColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            layout.Visibility = Visibility.Visible;
        }

        CustomTableColumn[] sortableColumns = [.. CustomTableColumnFactory.CreateMainColumns(settings).Where(column => !string.IsNullOrWhiteSpace(column.SortMemberPath))];

        CollectionAssert.AreEqual(
            new[]
            {
                "PlayedAt",
                "FolderLabels",
                "Title",
                "BestClear",
                "BestDjLevel",
                "BestRate",
                "BestBp",
                "BestCombo",
                "Kind",
                "OpHistory",
                "Artist",
                "BestExscore",
                "PlayExscore",
                "Judges",
                "Option",
                "Sha256",
                "RawHash",
                "Finalized"
            },
            sortableColumns.Select(column => column.Id).ToArray());
        Assert.IsTrue(sortableColumns.Length > 0);
        foreach (CustomTableColumn column in sortableColumns)
        {
            Assert.IsTrue(
                PlayHistorySortEngine.TryNormalizeSortColumn(column.SortMemberPath, out _),
                column.Id + " uses unsupported PlayHistory sort path " + column.SortMemberPath);
        }
    }

    [TestMethod]
    public void CreateMainColumns_PlayHistoryBestDjAndRateUseTransitionText()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);
        foreach (CustomTableColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            layout.Visibility = Visibility.Visible;
        }

        CustomTableColumn[] columns = [.. CustomTableColumnFactory.CreateMainColumns(settings)];
        PlayHistoryRow row = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    new Lr2PlayHistoryRecord
                    {
                        history_id = 1,
                        hash = new string('a', 32),
                        played_at = 1000,
                        finalized = 1,
                        score_write_type = "update",
                        new_playcount = 1,
                        playcount_delta = 1,
                        old_clear = 0,
                        new_clear = 3,
                        old_exscore = 200,
                        new_exscore = 250,
                        new_totalnotes = 150
                    }
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            PlayHistoryProjectionIndex.Empty).Rows.Single();

        CustomTableColumn clearColumn = columns.Single(column => column.Id == "BestClear");
        CustomTableColumn bestDjColumn = columns.Single(column => column.Id == "BestDjLevel");
        Assert.AreEqual("NO PLAY -> NORMAL", clearColumn.GetText(row));
        IReadOnlyList<CustomTableTextRunStyle> clearRuns = clearColumn.GetTextRuns(row, clearColumn.GetText(row));
        Assert.AreEqual(3, clearRuns.Count);
        Assert.AreEqual(0, clearRuns[0].StartIndex);
        Assert.AreEqual("NO PLAY".Length, clearRuns[0].Length);
        Assert.AreEqual("NO PLAY".Length, clearRuns[1].StartIndex);
        Assert.AreEqual(" -> ".Length, clearRuns[1].Length);
        Assert.AreEqual("NO PLAY -> ".Length, clearRuns[2].StartIndex);
        Assert.AreEqual("NORMAL".Length, clearRuns[2].Length);
        Assert.AreEqual("A -> AA", bestDjColumn.GetText(row));
        IReadOnlyList<CustomTableTextRunStyle> bestDjRuns = bestDjColumn.GetTextRuns(row, bestDjColumn.GetText(row));
        Assert.AreEqual(3, bestDjRuns.Count);
        Assert.AreEqual(0, bestDjRuns[0].StartIndex);
        Assert.AreEqual(1, bestDjRuns[0].Length);
        Assert.AreEqual(1, bestDjRuns[1].StartIndex);
        Assert.AreEqual(" -> ".Length, bestDjRuns[1].Length);
        Assert.AreEqual("A -> ".Length, bestDjRuns[2].StartIndex);
        Assert.AreEqual(2, bestDjRuns[2].Length);
        Assert.AreEqual("66.67% -> 83.33%", columns.Single(column => column.Id == "BestRate").GetText(row));
    }

    [TestMethod]
    public void CreatePlaylistSummaryColumns_CreatesAllSummaryColumns()
    {
        var settings = new PlaylistSummaryColumnSettings();
        foreach (PlaylistSummaryColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumeratePlaylistSummaryColumnLayouts(settings))
        {
            layout.DisplayIndex = -1;
            layout.Visibility = Visibility.Visible;
        }

        string[] ids = [.. CustomTableColumnFactory.CreatePlaylistSummaryColumns(settings).Select(column => column.Id)];

        CollectionAssert.AreEqual(
            new[]
            {
                "PlaylistId",
                "OutputBase",
                "Name",
                "Symbol",
                "LastUpdate",
                "TotalCharts",
                "OwnedCharts",
                "MissingCharts",
                "OwnedRatio",
                "Link",
                "IsExternalSync",
                "Status",
                "IsRootFolder",
                "BmtSort",
                "IsBmtOutput"
            },
            ids);
    }

    [TestMethod]
    public void CreatePlaylistSummaryColumns_ReflectsVisibilityWidthAndDisplayIndex()
    {
        var settings = new PlaylistSummaryColumnSettings();
        settings.PlaylistId.Visibility = Visibility.Hidden;
        settings.Name.DisplayIndex = 20;
        settings.Status.DisplayIndex = 0;
        settings.Status.Width = 123;

        CustomTableColumn[] columns = [.. CustomTableColumnFactory.CreatePlaylistSummaryColumns(settings)];

        Assert.IsFalse(columns.Any(column => column.Id == "PlaylistId"));
        Assert.AreEqual("Status", columns[0].Id);
        Assert.AreEqual(123, columns[0].Width);
        Assert.AreEqual("Name", columns.Last().Id);
    }

    [TestMethod]
    public void CreatePlaylistSummaryColumns_AssignsSortPathsAndActionMetadata()
    {
        var settings = new PlaylistSummaryColumnSettings();
        var columns = CustomTableColumnFactory.CreatePlaylistSummaryColumns(settings).ToDictionary(column => column.Id);

        Assert.AreEqual(nameof(PlaylistSummaryRow.PlaylistId), columns["PlaylistId"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.Name), columns["Name"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.Symbol), columns["Symbol"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.LastUpdate), columns["LastUpdate"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.TotalCharts), columns["TotalCharts"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.OwnedCharts), columns["OwnedCharts"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.MissingCharts), columns["MissingCharts"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.OwnedRatio), columns["OwnedRatio"].SortMemberPath);
        Assert.IsNull(columns["Link"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.IsExternalSync), columns["IsExternalSync"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.StatusSortOrder), columns["Status"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.IsRootFolder), columns["IsRootFolder"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.BmtSort), columns["BmtSort"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.IsBmtOutput), columns["IsBmtOutput"].SortMemberPath);
        Assert.AreEqual(CustomTableCellKind.ActionText, columns["Link"].CellKind);
        Assert.AreEqual(CustomTableCellKind.CheckBox, columns["IsExternalSync"].CellKind);
        Assert.AreEqual(CustomTableCellKind.CheckBox, columns["IsRootFolder"].CellKind);
        Assert.AreEqual(CustomTableCellKind.CheckBox, columns["IsBmtOutput"].CellKind);
    }

    [TestMethod]
    public void CreateMainColumns_AssignsAutoTrimTooltipPolicy()
    {
        var settings = new CustomTableColumnSettings();
        foreach (CustomTableColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            layout.DisplayIndex = -1;
            layout.Visibility = Visibility.Visible;
        }

        var columns = CustomTableColumnFactory.CreateMainColumns(settings).ToDictionary(column => column.Id);
        string[] disabled =
        [
            "Status",
            "Mode",
            "Clear",
            "Rank",
            "Rate",
            "ChartDifficulty",
            "ChartJudge",
            "WavHealth",
            "BgaHealth",
            "MovieHealth"
        ];

        foreach (string id in disabled)
        {
            Assert.IsFalse(columns[id].AutoTrimTooltip, id);
        }
        Assert.IsTrue(columns["Title"].AutoTrimTooltip);
        Assert.IsTrue(columns["Path"].AutoTrimTooltip);
        Assert.IsTrue(columns["Hash"].AutoTrimTooltip);
    }

    [TestMethod]
    public void CreatePlaylistSummaryColumns_AssignsAutoTrimTooltipPolicy()
    {
        var settings = new PlaylistSummaryColumnSettings();
        foreach (PlaylistSummaryColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumeratePlaylistSummaryColumnLayouts(settings))
        {
            layout.DisplayIndex = -1;
            layout.Visibility = Visibility.Visible;
        }

        var columns = CustomTableColumnFactory.CreatePlaylistSummaryColumns(settings).ToDictionary(column => column.Id);

        Assert.IsFalse(columns["PlaylistId"].AutoTrimTooltip);
        Assert.IsFalse(columns["Symbol"].AutoTrimTooltip);
        Assert.IsFalse(columns["IsExternalSync"].AutoTrimTooltip);
        Assert.IsFalse(columns["IsRootFolder"].AutoTrimTooltip);
        Assert.IsFalse(columns["BmtSort"].AutoTrimTooltip);
        Assert.IsFalse(columns["IsBmtOutput"].AutoTrimTooltip);
        Assert.IsTrue(columns["Name"].AutoTrimTooltip);
        Assert.IsTrue(columns["Link"].AutoTrimTooltip);
    }

    [TestMethod]
    public void CreatePlaylistSummaryColumns_FormatsActionsTooltipsAndFailureBackground()
    {
        var settings = new PlaylistSummaryColumnSettings();
        var columns = CustomTableColumnFactory.CreatePlaylistSummaryColumns(settings).ToDictionary(column => column.Id);
        var row = new PlaylistSummaryRow
        {
            LinkUri = new Uri("https://example.com/"),
            IsExternalSync = true,
            IsRootFolder = false,
            Status = "-",
            StatusDetail = "detail",
            HasFailureStatus = true,
            LastUpdate = new DateTime(2026, 4, 30, 12, 34, 56),
            OwnedRatio = 99.8
        };

        Assert.AreEqual("Open", columns["Link"].GetText(row));
        Assert.AreEqual("https://example.com/", columns["Link"].GetTooltip(row));
        Assert.AreEqual("https://example.com/", columns["Link"].GetEditText(row));
        Assert.AreEqual("https://example.com/", CustomTableDataTransfer.BuildCellText(row, columns["Link"]));
        Assert.AreEqual(true, columns["IsExternalSync"].GetChecked(row));
        Assert.AreEqual(false, columns["IsRootFolder"].GetChecked(row));
        Assert.AreEqual("detail", columns["Status"].GetTooltip(row));
        Assert.AreEqual("2026/04/30 12:34:56", columns["LastUpdate"].GetText(row));
        Assert.AreEqual("99.8%", columns["OwnedRatio"].GetText(row));
        Assert.IsTrue(CustomTableColumnFactory.HasHighlightedWarning(row));
    }

    [TestMethod]
    public void CreateMainColumns_AssignsPhaseTwoSortMemberPaths()
    {
        var settings = new CustomTableColumnSettings();
        foreach (CustomTableColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            layout.Visibility = Visibility.Visible;
        }

        var paths = CustomTableColumnFactory.CreateMainColumns(settings).ToDictionary(column => column.Id, column => column.SortMemberPath);

        Assert.AreEqual("Title", paths["Title"]);
        Assert.AreEqual("Artist", paths["Artist"]);
        Assert.AreEqual("genre", paths["Genre"]);
        Assert.AreEqual("mode", paths["Mode"]);
        Assert.AreEqual("tag", paths["Tag"]);
        Assert.AreEqual("path", paths["Path"]);
        Assert.AreEqual("hash", paths["Hash"]);
        Assert.AreEqual("sha256", paths["Sha256"]);
        Assert.IsNull(paths["Url1"]);
        Assert.IsNull(paths["Url2"]);
        Assert.AreEqual("WarningDigestText", paths["Warning"]);
        Assert.IsNull(paths["Comment"]);
        Assert.IsNull(paths["Memo"]);
        Assert.AreEqual("instl_dst", paths["InstallDst"]);
        Assert.AreEqual("WAVHealth", paths["WavHealth"]);
        Assert.AreEqual("ChartMainBpmSortKey", paths["ChartMainBpm"]);
        Assert.AreEqual("ChartJudgeSortKey", paths["ChartJudgePercent"]);
        Assert.AreEqual("RefTablesSymbols", paths["PlaylistSymbols"]);
        Assert.AreEqual("clear", paths["Clear"]);
        Assert.AreEqual("rank", paths["Rank"]);
        Assert.AreEqual("rateDouble", paths["Rate"]);
        Assert.AreEqual("stddevVal", paths["TScore"]);
        Assert.AreEqual("ChartLevelSortKey", paths["Level"]);
        Assert.AreEqual("ChartDifficultySortKey", paths["ChartDifficulty"]);
        Assert.AreEqual("ChartJudgeSortKey", paths["ChartJudge"]);
    }

    [TestMethod]
    public void CreateMainColumns_StandardVisibleSortMemberPathsAreVirtualRegistryColumns()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);

        CustomTableColumn[] sortableColumns = [.. CustomTableColumnFactory.CreateMainColumns(settings).Where(column => !string.IsNullOrWhiteSpace(column.SortMemberPath))];

        Assert.IsTrue(sortableColumns.Length > 0);
        foreach (CustomTableColumn column in sortableColumns)
        {
            Assert.IsTrue(
                ChartListOrder.TryNormalizeVirtualSortColumn(column.SortMemberPath, out _),
                column.Id + " uses unsupported SortMemberPath " + column.SortMemberPath);
        }
    }

    [TestMethod]
    public void CreateMainColumns_DuplicateVisibleSortMemberPathsAreVirtualRegistryColumns()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.DUPLICATE);

        AssertVisibleSortMemberPathsAreVirtualRegistryColumns(settings);
    }

    [TestMethod]
    public void CreateMainColumns_FullScanVisibleSortMemberPathsAreVirtualRegistryColumns()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.FULLSCAN);

        AssertVisibleSortMemberPathsAreVirtualRegistryColumns(settings);
    }

    [TestMethod]
    public void CreateMainColumns_InstallVisibleSortMemberPathsAreVirtualRegistryColumns()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.INSTALL);

        AssertVisibleSortMemberPathsAreVirtualRegistryColumns(settings);
    }

    [TestMethod]
    public void CreateMainColumns_AllSortableMainColumnsAreVirtualRegistryColumnsOrExplicitlyExcluded()
    {
        var settings = new CustomTableColumnSettings();
        foreach (CustomTableColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            layout.Visibility = Visibility.Visible;
        }

        CustomTableColumn[] unsupportedSortableColumns = [.. CustomTableColumnFactory.CreateMainColumns(settings)
            .Where(column => !string.IsNullOrWhiteSpace(column.SortMemberPath))
            .Where(column => !ChartListOrder.TryNormalizeVirtualSortColumn(column.SortMemberPath, out _))];

        Assert.AreEqual(1, unsupportedSortableColumns.Length);
        Assert.AreEqual("EntryLevel", unsupportedSortableColumns[0].Id);
        Assert.AreEqual("EntryLevelSortKey", unsupportedSortableColumns[0].SortMemberPath);
    }

    private static void AssertVisibleSortMemberPathsAreVirtualRegistryColumns(CustomTableColumnSettings settings)
    {
        CustomTableColumn[] sortableColumns = [.. CustomTableColumnFactory.CreateMainColumns(settings).Where(column => !string.IsNullOrWhiteSpace(column.SortMemberPath))];

        Assert.IsTrue(sortableColumns.Length > 0);
        foreach (CustomTableColumn column in sortableColumns)
        {
            Assert.IsTrue(
                ChartListOrder.TryNormalizeVirtualSortColumn(column.SortMemberPath, out _),
                column.Id + " uses unsupported SortMemberPath " + column.SortMemberPath);
        }
    }

    [TestMethod]
    public void ScoreBrushProvider_MapsFutureClearTypes()
    {
        Assert.AreSame(CustomTableScoreBrushProvider.PurpleBrush, CustomTableScoreBrushProvider.ConvertClear(ClearType.INVALID));
        Assert.AreSame(CustomTableScoreBrushProvider.LightPurpleBrush, CustomTableScoreBrushProvider.ConvertClear(ClearType.L_ASSIST));
        Assert.AreSame(CustomTableScoreBrushProvider.YellowBrush, CustomTableScoreBrushProvider.ConvertClear(ClearType.EX_HARD));
        Assert.AreSame(CustomTableScoreBrushProvider.YellowOrangeBrush, CustomTableScoreBrushProvider.ConvertClear(ClearType.MAX));
    }

    [TestMethod]
    public void CreateMainColumns_AssignsPhaseFourEditableMetadata()
    {
        var settings = new CustomTableColumnSettings();
        foreach (CustomTableColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            layout.DisplayIndex = -1;
            layout.Visibility = Visibility.Visible;
        }

        var columns = CustomTableColumnFactory.CreateMainColumns(settings).ToDictionary(column => column.Id);

        Assert.AreEqual("Level", columns["EntryLevel"].EditPropertyName);
        Assert.IsFalse(columns["EntryLevel"].EditTextWrapping);
        Assert.AreEqual("comment", columns["Comment"].EditPropertyName);
        Assert.IsTrue(columns["Comment"].EditTextWrapping);
        Assert.AreEqual("memo", columns["Memo"].EditPropertyName);
        Assert.IsTrue(columns["Memo"].EditTextWrapping);
        Assert.AreEqual("Url", columns["Url1"].EditPropertyName);
        Assert.IsFalse(columns["Url1"].EditOnRepeatClick);
        Assert.AreEqual(250, columns["Url1"].EditOverlayWidth);
        Assert.AreEqual("Url_diff", columns["Url2"].EditPropertyName);
        Assert.IsFalse(columns["Url2"].EditOnRepeatClick);
        Assert.AreEqual(250, columns["Url2"].EditOverlayWidth);
        Assert.AreEqual("Folder", columns["Folder"].EditPropertyName);
        Assert.AreEqual("instl_dst", columns["InstallDst"].EditPropertyName);

        var bmsFile = new BMSFile();
        var bmsRow = LibraryChartRow.FromChartFile(ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsFile(bmsFile),
            null,
            string.Empty,
            string.Empty,
            ["C:\\BMS\\A", "", "C:\\BMS\\B"],
            []));
        CollectionAssert.AreEqual(
            new[] { "C:\\BMS\\A", "C:\\BMS\\B" },
            columns["InstallDst"].GetEditSuggestions(bmsRow).ToArray());

        var adapterlessBmsonRow = LibraryChartRow.FromChartFile(ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
            {
                path = "C:\\Bmson\\adapterless.bmson",
                folder = "C:\\Bmson",
                md5 = "cccccccccccccccccccccccccccccccc",
                sha256 = new string('d', 64)
            }),
            null,
            "Candidate",
            "Artist",
            ["C:\\Bmson\\EntryA", "", "C:\\Bmson\\EntryB"],
            []));
        CollectionAssert.AreEqual(
            new[] { "C:\\Bmson\\EntryA", "C:\\Bmson\\EntryB" },
            columns["InstallDst"].GetEditSuggestions(adapterlessBmsonRow).ToArray());

        var bmsonRow = LibraryChartRow.FromChartFile(ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
            {
                path = "C:\\Bmson\\chart.bmson",
                folder = "C:\\Bmson",
                md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                sha256 = new string('b', 64)
            }),
            null,
            string.Empty,
            string.Empty,
            ["C:\\Bmson\\A", "", "C:\\Bmson\\B"],
            []));
        CollectionAssert.AreEqual(
            new[] { "C:\\Bmson\\A", "C:\\Bmson\\B" },
            columns["InstallDst"].GetEditSuggestions(bmsonRow).ToArray());
    }

    [TestMethod]
    public void CreateMainColumns_UsesActualUrlTextForUrlEditing()
    {
        var settings = new CustomTableColumnSettings();
        settings.Url1.Visibility = Visibility.Visible;
        settings.Url2.Visibility = Visibility.Visible;
        var entry = new BMSTableEntry
        {
            Url = new Uri("https://example.test/main"),
            Url_diff = new Uri("https://example.test/diff")
        };
        PlaylistDetailRow row = new PlaylistDetailSourceRow(entry, resolvedChart: null).CreateViewRow();

        var columns = CustomTableColumnFactory.CreateMainColumns(settings).ToDictionary(column => column.Id);

        Assert.AreEqual("\uE14F", columns["Url1"].GetText(row));
        Assert.AreEqual("https://example.test/main", columns["Url1"].GetEditText(row));
        Assert.AreEqual("\uE14F", columns["Url2"].GetText(row));
        Assert.AreEqual("https://example.test/diff", columns["Url2"].GetEditText(row));
    }

    [TestMethod]
    public void CreateMainColumns_AssignsPhaseSevenTextStyles()
    {
        var settings = new CustomTableColumnSettings();
        foreach (CustomTableColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            layout.DisplayIndex = -1;
            layout.Visibility = Visibility.Visible;
        }

        var columns = CustomTableColumnFactory.CreateMainColumns(settings).ToDictionary(column => column.Id);

        Assert.AreSame(CustomTableTextStyle.Score, columns["Clear"].TextStyle);
        Assert.AreSame(CustomTableTextStyle.Rank, columns["Rank"].TextStyle);
        Assert.AreSame(CustomTableTextStyle.Score, columns["ChartDifficulty"].TextStyle);
        Assert.AreSame(CustomTableTextStyle.Score, columns["ChartJudge"].TextStyle);
        Assert.AreSame(CustomTableTextStyle.Normal, columns["Level"].TextStyle);
    }

    [TestMethod]
    public void CreateMainColumns_AssignsUndefinedBackgroundSelectors()
    {
        var settings = new CustomTableColumnSettings();
        foreach (CustomTableColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            layout.DisplayIndex = -1;
            layout.Visibility = Visibility.Visible;
        }
        var columns = CustomTableColumnFactory.CreateMainColumns(settings).ToDictionary(column => column.Id);
        var undefinedRow = new UndefinedChartInfoRow
        {
            ChartLevelUndefined = true,
            ChartDifficultyUndefined = true,
            ChartTotalUndefined = true
        };
        var definedRow = new UndefinedChartInfoRow();

        Assert.IsNotNull(columns["Level"].GetBackground(undefinedRow));
        Assert.IsNotNull(columns["ChartDifficulty"].GetBackground(undefinedRow));
        Assert.IsNotNull(columns["ChartTotal"].GetBackground(undefinedRow));
        Assert.IsNotNull(columns["ChartTotalPerNote"].GetBackground(undefinedRow));
        Assert.IsNull(columns["Level"].GetBackground(definedRow));
        Assert.IsNull(columns["ChartDifficulty"].GetBackground(definedRow));
        Assert.IsNull(columns["ChartTotal"].GetBackground(definedRow));
        Assert.IsNull(columns["ChartTotalPerNote"].GetBackground(definedRow));
    }

    [TestMethod]
    public void CreateMainColumns_AssignsTooltipSelectors()
    {
        var settings = new CustomTableColumnSettings();
        foreach (CustomTableColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            layout.DisplayIndex = -1;
            layout.Visibility = Visibility.Visible;
        }
        var columns = CustomTableColumnFactory.CreateMainColumns(settings).ToDictionary(column => column.Id);
        var row = new
        {
            DisplayWarning = "warning text",
            WarningDigestText = "[1] digest",
            WarningTooltipText = "warning text",
            comment = "comment text",
            memo = "memo text",
            UrlToolTipText = "https://example.test/main",
            UrlDiffToolTipText = "diff\nhttps://example.test/diff",
            RefTablesNames = "table A"
        };

        Assert.AreEqual("[1] digest", columns["Warning"].GetText(row));
        Assert.AreEqual("warning text", columns["Warning"].GetTooltip(row));
        Assert.AreEqual("comment text", columns["Comment"].GetTooltip(row));
        Assert.AreEqual("memo text", columns["Memo"].GetTooltip(row));
        Assert.AreEqual("https://example.test/main", columns["Url1"].GetTooltip(row));
        Assert.AreEqual("diff\nhttps://example.test/diff", columns["Url2"].GetTooltip(row));
        Assert.AreEqual("table A", columns["PlaylistSymbols"].GetTooltip(row));
    }

    [TestMethod]
    public void CreateMainColumns_ConvertsUrlDownloadTextToIconGlyph()
    {
        var settings = new CustomTableColumnSettings();
        settings.Url1.Visibility = Visibility.Visible;
        CustomTableColumn urlColumn = CustomTableColumnFactory.CreateMainColumns(settings).Single(column => column.Id == "Url1");
        var row = new { UrlDownloadIconText = "download" };

        Assert.AreEqual("\uE14F", urlColumn.GetText(row));
    }

    [TestMethod]
    public void CreateMainColumns_FormatsStatusAndSuffixColumns()
    {
        var settings = new CustomTableColumnSettings();
        foreach (CustomTableColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            layout.Visibility = Visibility.Visible;
        }
        var columns = CustomTableColumnFactory.CreateMainColumns(settings).ToDictionary(column => column.Id);
        var row = new
        {
            status = BeMusicSeeker.Models.ChartFileStatus.PLAY | BeMusicSeeker.Models.ChartFileStatus.SCORE_UNSENT,
            mode = 7,
            rateDouble = 0.912345,
            WAVHealth = 100,
            stddevVal = 12.345,
            scoreDifficulty = 6.789
        };

        Assert.AreEqual("Play", columns["Status"].GetText(row));
        Assert.AreEqual("7KEYS", columns["Mode"].GetText(row));
        Assert.AreEqual("91.23%", columns["Rate"].GetText(row));
        Assert.AreEqual("100%", columns["WavHealth"].GetText(row));
        Assert.AreEqual("12.35", columns["TScore"].GetText(row));
        Assert.AreEqual("6.79", columns["ScoreDifficulty"].GetText(row));
    }

    [TestMethod]
    public void CustomTableColumnLayout_ResolvesHorizontalOffset()
    {
        var settings = new CustomTableColumnSettings();
        settings.Title.DisplayIndex = -1;
        settings.Artist.DisplayIndex = -1;
        settings.Path.DisplayIndex = -1;
        settings.Title.Width = 100;
        settings.Artist.Width = 80;
        settings.Path.Width = 70;
        settings.Status.Visibility = Visibility.Hidden;
        settings.Genre.Visibility = Visibility.Hidden;
        settings.Mode.Visibility = Visibility.Hidden;
        settings.Url1.Visibility = Visibility.Hidden;
        settings.Url2.Visibility = Visibility.Hidden;
        settings.Tag.Visibility = Visibility.Hidden;
        settings.Warning.Visibility = Visibility.Hidden;
        settings.Comment.Visibility = Visibility.Hidden;
        settings.Memo.Visibility = Visibility.Hidden;
        settings.Hash.Visibility = Visibility.Hidden;
        settings.Sha256.Visibility = Visibility.Hidden;
        settings.Folder.Visibility = Visibility.Hidden;
        settings.InstallDst.Visibility = Visibility.Hidden;
        settings.InstallDstTitle.Visibility = Visibility.Hidden;
        settings.InstallDstArtist.Visibility = Visibility.Hidden;
        settings.WavHealth.Visibility = Visibility.Hidden;
        settings.BgaHealth.Visibility = Visibility.Hidden;
        settings.MovieHealth.Visibility = Visibility.Hidden;
        settings.CharcterEncoding.Visibility = Visibility.Hidden;
        settings.PlaylistSymbols.Visibility = Visibility.Hidden;
        settings.Clear.Visibility = Visibility.Hidden;
        settings.Rank.Visibility = Visibility.Hidden;
        settings.Level.Visibility = Visibility.Hidden;
        settings.ChartDifficulty.Visibility = Visibility.Hidden;
        settings.ChartMainBpm.Visibility = Visibility.Hidden;
        settings.ChartMaxBpm.Visibility = Visibility.Hidden;
        settings.ChartMinBpm.Visibility = Visibility.Hidden;
        settings.ChartDuration.Visibility = Visibility.Hidden;
        settings.ChartJudge.Visibility = Visibility.Hidden;
        settings.ChartJudgePercent.Visibility = Visibility.Hidden;
        settings.ChartFeature.Visibility = Visibility.Hidden;
        settings.Notes.Visibility = Visibility.Hidden;
        settings.ChartLongNotes.Visibility = Visibility.Hidden;
        settings.ChartScratchNotes.Visibility = Visibility.Hidden;
        settings.ChartTotal.Visibility = Visibility.Hidden;
        settings.ChartTotalPerNote.Visibility = Visibility.Hidden;
        settings.ChartDensity.Visibility = Visibility.Hidden;
        settings.ChartPeakDensity.Visibility = Visibility.Hidden;
        settings.ChartEndDensity.Visibility = Visibility.Hidden;
        settings.ChartSoflan.Visibility = Visibility.Hidden;
        settings.Rate.Visibility = Visibility.Hidden;
        settings.Score.Visibility = Visibility.Hidden;
        settings.Combo.Visibility = Visibility.Hidden;
        settings.Bp.Visibility = Visibility.Hidden;
        settings.Ranking.Visibility = Visibility.Hidden;
        settings.RankingLastupdate.Visibility = Visibility.Hidden;
        settings.TScore.Visibility = Visibility.Hidden;
        settings.ScoreDifficulty.Visibility = Visibility.Hidden;
        CustomTableColumn[] columns = [.. CustomTableColumnFactory.CreateMainColumns(settings)];

        bool resolved = CustomTableColumnLayout.TryResolveColumn(columns, tableX: 125, out CustomTableColumn column, out int columnIndex, out double columnX);

        Assert.IsTrue(resolved);
        Assert.AreEqual("Artist", column.Id);
        Assert.AreEqual(1, columnIndex);
        Assert.AreEqual(100d, columnX);
        Assert.AreEqual(3, CustomTableColumnLayout.CountColumnsWithinViewport(columns, horizontalOffset: 90, viewportWidth: 100));
        Rect contentRect = CustomTableColumnLayout.CreateContentColumnRect(0, 100, 25, 0, 20);
        Rect visibleRect = CustomTableColumnLayout.CreateVisibleColumnRect(0, 100, 25, 100, 0, 20);
        Assert.AreEqual(-25d, contentRect.X);
        Assert.AreEqual(100d, contentRect.Width);
        Assert.AreEqual(0d, visibleRect.X);
        Assert.AreEqual(75d, visibleRect.Width);
    }

    [TestMethod]
    public void CustomTableColumnLayout_ResizeHitPrefersResizableColumnBoundary()
    {
        var settings = new CustomTableColumnSettings();
        settings.Title.DisplayIndex = -1;
        settings.Artist.DisplayIndex = -1;
        settings.Url1.DisplayIndex = -1;
        settings.Title.Width = 100;
        settings.Artist.Width = 80;
        settings.Url1.Width = 40;
        settings.Status.Visibility = Visibility.Hidden;
        settings.Path.Visibility = Visibility.Hidden;
        settings.Genre.Visibility = Visibility.Hidden;
        settings.Mode.Visibility = Visibility.Hidden;
        settings.Tag.Visibility = Visibility.Hidden;
        settings.Url2.Visibility = Visibility.Hidden;
        settings.Warning.Visibility = Visibility.Hidden;
        settings.Comment.Visibility = Visibility.Hidden;
        settings.Memo.Visibility = Visibility.Hidden;
        settings.Hash.Visibility = Visibility.Hidden;
        settings.Sha256.Visibility = Visibility.Hidden;
        settings.Folder.Visibility = Visibility.Hidden;
        settings.InstallDst.Visibility = Visibility.Hidden;
        settings.InstallDstTitle.Visibility = Visibility.Hidden;
        settings.InstallDstArtist.Visibility = Visibility.Hidden;
        settings.WavHealth.Visibility = Visibility.Hidden;
        settings.BgaHealth.Visibility = Visibility.Hidden;
        settings.MovieHealth.Visibility = Visibility.Hidden;
        settings.CharcterEncoding.Visibility = Visibility.Hidden;
        settings.PlaylistSymbols.Visibility = Visibility.Hidden;
        settings.Clear.Visibility = Visibility.Hidden;
        settings.Rank.Visibility = Visibility.Hidden;
        settings.Level.Visibility = Visibility.Hidden;
        settings.ChartDifficulty.Visibility = Visibility.Hidden;
        settings.ChartMainBpm.Visibility = Visibility.Hidden;
        settings.ChartMaxBpm.Visibility = Visibility.Hidden;
        settings.ChartMinBpm.Visibility = Visibility.Hidden;
        settings.ChartDuration.Visibility = Visibility.Hidden;
        settings.ChartJudge.Visibility = Visibility.Hidden;
        settings.ChartJudgePercent.Visibility = Visibility.Hidden;
        settings.ChartFeature.Visibility = Visibility.Hidden;
        settings.Notes.Visibility = Visibility.Hidden;
        settings.ChartLongNotes.Visibility = Visibility.Hidden;
        settings.ChartScratchNotes.Visibility = Visibility.Hidden;
        settings.ChartTotal.Visibility = Visibility.Hidden;
        settings.ChartTotalPerNote.Visibility = Visibility.Hidden;
        settings.ChartDensity.Visibility = Visibility.Hidden;
        settings.ChartPeakDensity.Visibility = Visibility.Hidden;
        settings.ChartEndDensity.Visibility = Visibility.Hidden;
        settings.ChartSoflan.Visibility = Visibility.Hidden;
        settings.Rate.Visibility = Visibility.Hidden;
        settings.Score.Visibility = Visibility.Hidden;
        settings.Combo.Visibility = Visibility.Hidden;
        settings.Bp.Visibility = Visibility.Hidden;
        settings.Ranking.Visibility = Visibility.Hidden;
        settings.RankingLastupdate.Visibility = Visibility.Hidden;
        settings.TScore.Visibility = Visibility.Hidden;
        settings.ScoreDifficulty.Visibility = Visibility.Hidden;
        CustomTableColumn[] columns = [.. CustomTableColumnFactory.CreateMainColumns(settings)];

        bool titleResize = CustomTableColumnLayout.TryResolveResizeColumn(columns, surfaceX: 99, horizontalOffset: 0, viewportWidth: 200, margin: 4, out CustomTableColumn titleColumn, out _, out _);
        bool fixedUrlResize = CustomTableColumnLayout.TryResolveResizeColumn(columns, surfaceX: 220, horizontalOffset: 0, viewportWidth: 240, margin: 4, out CustomTableColumn urlColumn, out _, out _);

        Assert.IsTrue(titleResize);
        Assert.AreEqual("Title", titleColumn.Id);
        Assert.IsFalse(fixedUrlResize);
        Assert.IsNull(urlColumn);
    }

    [TestMethod]
    public void CustomTableDataTransfer_BuildsVisibleColumnTsvAndNormalizesCellText()
    {
        CustomTableColumnSettings settings = CreateOnlyTitleArtistSettings();
        CustomTableColumn[] columns = [.. CustomTableColumnFactory.CreateMainColumns(settings)];
        var rows = new[]
        {
            new { Title = "A\tTitle", Artist = "Artist\r\nOne" },
            new { Title = "B", Artist = "Artist Two" }
        };

        string tsv = CustomTableDataTransfer.BuildTsv(rows, columns);

        Assert.AreEqual("A Title\tArtist  One\r\nB\tArtist Two", tsv);
    }

    [TestMethod]
    public void CustomTableDataTransfer_BuildsSingleCellTextFromEditTextAndNormalizes()
    {
        var column = new CustomTableColumn(
            "Url",
            "URL",
            null,
            0,
            null,
            TextAlignment.Left,
            row => "display icon",
            editTextSelector: row => "https://example.test/a\tb\r\nc");

        string text = CustomTableDataTransfer.BuildCellText(new object(), column);

        Assert.AreEqual("https://example.test/a b  c", text);
        Assert.AreEqual(string.Empty, CustomTableDataTransfer.BuildCellText(null, column));
        Assert.AreEqual(string.Empty, CustomTableDataTransfer.BuildCellText(new object(), null));
    }

    [TestMethod]
    public void CustomTableView_ResolvesCopyKeyboardShortcuts()
    {
        Assert.AreEqual(CustomTableKeyboardCommand.CopyCurrentCell, CustomTableView.ResolveKeyboardCommand(Key.C, ModifierKeys.Control));
        Assert.AreEqual(CustomTableKeyboardCommand.CopySelectedRowsTsv, CustomTableView.ResolveKeyboardCommand(Key.C, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.AreEqual(CustomTableKeyboardCommand.None, CustomTableView.ResolveKeyboardCommand(Key.C, ModifierKeys.Control | ModifierKeys.Alt));
        Assert.AreEqual(CustomTableKeyboardCommand.None, CustomTableView.ResolveKeyboardCommand(Key.C, ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt));
        Assert.AreEqual(CustomTableKeyboardCommand.SelectAllRows, CustomTableView.ResolveKeyboardCommand(Key.A, ModifierKeys.Control));
    }

    [TestMethod]
    public void CustomTableDataTransfer_CreatesLegacyAndCustomDragFormats()
    {
        object[] rows = [new(), new()];

        DataObject dataObject = CustomTableDataTransfer.CreateSelectedRowsDataObject(rows, CustomTableRowDragKind.PlaylistSummaryRows, rows[1]);

        Assert.IsTrue(dataObject.GetDataPresent(CustomTableDataTransfer.SelectedRowsDataFormat));
        Assert.IsTrue(dataObject.GetDataPresent(CustomTableDataTransfer.LegacySelectedRowsDataFormat));
        Assert.IsTrue(CustomTableDataTransfer.HasRowDragKind(dataObject, CustomTableRowDragKind.PlaylistSummaryRows));
        Assert.IsTrue(CustomTableDataTransfer.TryGetPrimaryRow(dataObject, out object primaryRow));
        Assert.AreSame(rows[1], primaryRow);
        Assert.IsTrue(CustomTableDataTransfer.TryGetSelectedRows(dataObject, out System.Collections.Generic.List<object>? resolvedRows));
        Assert.AreEqual(2, resolvedRows.Count);
    }

    [TestMethod]
    public void CustomTableDataTransfer_ReordersColumnsWithoutMovingStatus()
    {
        CustomTableColumnSettings settings = CreateOnlyTitleArtistSettings(includeStatus: true);
        CustomTableColumn[] columns = [.. CustomTableColumnFactory.CreateMainColumns(settings)];
        CustomTableColumn title = columns.Single(column => column.Id == "Title");
        CustomTableColumn artist = columns.Single(column => column.Id == "Artist");
        CustomTableColumn status = columns.Single(column => column.Id == "Status");

        bool reordered = CustomTableDataTransfer.TryReorderVisibleColumns(columns, artist, status, insertAfterTarget: false);

        Assert.IsTrue(reordered);
        CustomTableColumn[] reorderedColumns = [.. CustomTableColumnFactory.CreateMainColumns(settings)];
        CollectionAssert.AreEqual(new[] { "Status", "Artist", "Title" }, reorderedColumns.Select(column => column.Id).ToArray());
        Assert.IsFalse(status.CanReorder);
        Assert.IsTrue(title.CanReorder);
    }

    private static CustomTableColumnSettings CreateOnlyTitleArtistSettings(bool includeStatus = false)
    {
        var settings = new CustomTableColumnSettings();
        foreach (CustomTableColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            layout.DisplayIndex = -1;
            layout.Visibility = Visibility.Hidden;
        }
        settings.Status.Visibility = includeStatus ? Visibility.Visible : Visibility.Hidden;
        settings.Title.Visibility = Visibility.Visible;
        settings.Artist.Visibility = Visibility.Visible;
        settings.Status.DisplayIndex = 0;
        settings.Title.DisplayIndex = 1;
        settings.Artist.DisplayIndex = 2;
        settings.Title.Width = 100;
        settings.Artist.Width = 100;
        return settings;
    }

    private sealed class UndefinedChartInfoRow
    {
        public bool ChartLevelUndefined { get; set; }

        public bool ChartDifficultyUndefined { get; set; }

        public bool ChartTotalUndefined { get; set; }
    }
}
