using System.Linq;
using System;
using System.Windows;
using BeMusicSeeker.Models;
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
        dataGridColumnsSettings settings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.STANDARD);

        string[] ids = CustomTableColumnFactory.CreateMainColumns(settings).Select(column => column.Id).ToArray();

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
        dataGridColumnsSettings settings = new dataGridColumnsSettings();
        foreach (dataGridColumnsSettings.dataGridColumnlayouts layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            layout.DisplayIndex = -1;
            layout.Visibility = Visibility.Visible;
        }

        string[] ids = CustomTableColumnFactory.CreateMainColumns(settings).Select(column => column.Id).ToArray();

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
        dataGridColumnsSettings settings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.PLAYLIST);

        CustomTableColumn status = CustomTableColumnFactory.CreateMainColumns(settings).First();

        Assert.AreEqual("Status", status.Id);
        Assert.AreEqual(18, status.Width);
        Assert.IsFalse(status.CanResize);
        Assert.IsFalse(status.CanReorder);
        Assert.IsNull(status.SortMemberPath);
        Assert.AreEqual(CustomTableCellKind.StatusIcon, status.CellKind);
    }

    [TestMethod]
    public void CreateMainColumns_ReflectsVisibilityWidthAndDisplayIndex()
    {
        dataGridColumnsSettings settings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.STANDARD);
        settings.Status.Visibility = Visibility.Hidden;
        settings.Title.Visibility = Visibility.Hidden;
        settings.ChartJudge.DisplayIndex = 0;
        settings.ChartJudge.Width = 77;

        CustomTableColumn[] columns = CustomTableColumnFactory.CreateMainColumns(settings).ToArray();

        Assert.IsFalse(columns.Any(column => column.Id == "Title"));
        Assert.AreEqual("ChartJudge", columns[0].Id);
        Assert.AreEqual(77, columns[0].Width);
    }

    [TestMethod]
    public void CreatePlaylistSummaryColumns_CreatesAllSummaryColumns()
    {
        PlaylistSummaryColumnSettings settings = new PlaylistSummaryColumnSettings();
        foreach (PlaylistSummaryColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumeratePlaylistSummaryColumnLayouts(settings))
        {
            layout.DisplayIndex = -1;
            layout.Visibility = Visibility.Visible;
        }

        string[] ids = CustomTableColumnFactory.CreatePlaylistSummaryColumns(settings).Select(column => column.Id).ToArray();

        CollectionAssert.AreEqual(
            new[]
            {
                "PlaylistId",
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
                "IsRootFolder"
            },
            ids);
    }

    [TestMethod]
    public void CreatePlaylistSummaryColumns_ReflectsVisibilityWidthAndDisplayIndex()
    {
        PlaylistSummaryColumnSettings settings = new PlaylistSummaryColumnSettings();
        settings.PlaylistId.Visibility = Visibility.Hidden;
        settings.Name.DisplayIndex = 20;
        settings.Status.DisplayIndex = 0;
        settings.Status.Width = 123;

        CustomTableColumn[] columns = CustomTableColumnFactory.CreatePlaylistSummaryColumns(settings).ToArray();

        Assert.IsFalse(columns.Any(column => column.Id == "PlaylistId"));
        Assert.AreEqual("Status", columns[0].Id);
        Assert.AreEqual(123, columns[0].Width);
        Assert.AreEqual("Name", columns.Last().Id);
    }

    [TestMethod]
    public void CreatePlaylistSummaryColumns_AssignsSortPathsAndActionMetadata()
    {
        PlaylistSummaryColumnSettings settings = new PlaylistSummaryColumnSettings();
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
        Assert.AreEqual(CustomTableCellKind.ActionText, columns["Link"].CellKind);
        Assert.AreEqual(CustomTableCellKind.CheckBox, columns["IsExternalSync"].CellKind);
        Assert.AreEqual(CustomTableCellKind.CheckBox, columns["IsRootFolder"].CellKind);
    }

    [TestMethod]
    public void CreatePlaylistSummaryColumns_FormatsActionsTooltipsAndFailureBackground()
    {
        PlaylistSummaryColumnSettings settings = new PlaylistSummaryColumnSettings();
        var columns = CustomTableColumnFactory.CreatePlaylistSummaryColumns(settings).ToDictionary(column => column.Id);
        PlaylistSummaryRow row = new PlaylistSummaryRow
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
        dataGridColumnsSettings settings = new dataGridColumnsSettings();
        foreach (dataGridColumnsSettings.dataGridColumnlayouts layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
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
        Assert.AreEqual("DisplayWarning", paths["Warning"]);
        Assert.IsNull(paths["Comment"]);
        Assert.IsNull(paths["Memo"]);
        Assert.AreEqual("instl_dst", paths["InstallDst"]);
        Assert.AreEqual("WAVHealth", paths["WavHealth"]);
        Assert.AreEqual("ChartMainBpmSortKey", paths["ChartMainBpm"]);
        Assert.AreEqual("ChartJudgeSortKey", paths["ChartJudgePercent"]);
        Assert.AreEqual("RefTablesSymbols", paths["PlaylistSymbols"]);
        Assert.AreEqual("clear", paths["Clear"]);
        Assert.AreEqual("RankDisplayText", paths["Rank"]);
        Assert.AreEqual("rate", paths["Rate"]);
        Assert.AreEqual("stddevVal", paths["TScore"]);
        Assert.AreEqual("ChartLevelSortKey", paths["Level"]);
        Assert.AreEqual("ChartDifficultySortKey", paths["ChartDifficulty"]);
        Assert.AreEqual("ChartJudgeSortKey", paths["ChartJudge"]);
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
        dataGridColumnsSettings settings = new dataGridColumnsSettings();
        foreach (dataGridColumnsSettings.dataGridColumnlayouts layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
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

        var bmsFile = new BMSFile
        {
            InstallDestinationSuggestions = new[] { "C:\\BMS\\A", "", "C:\\BMS\\B" }
        };
        CollectionAssert.AreEqual(
            new[] { "C:\\BMS\\A", "C:\\BMS\\B" },
            columns["InstallDst"].GetEditSuggestions(bmsFile).ToArray());
    }

    [TestMethod]
    public void CreateMainColumns_UsesActualUrlTextForUrlEditing()
    {
        dataGridColumnsSettings settings = new dataGridColumnsSettings();
        settings.Url1.Visibility = Visibility.Visible;
        settings.Url2.Visibility = Visibility.Visible;
        var entry = new BMSTableEntry
        {
            Url = new Uri("https://example.test/main"),
            Url_diff = new Uri("https://example.test/diff")
        };
        PlaylistDetailRow row = new PlaylistDetailSourceRow(entry, null).CreateViewRow();

        var columns = CustomTableColumnFactory.CreateMainColumns(settings).ToDictionary(column => column.Id);

        Assert.AreEqual("\uE14F", columns["Url1"].GetText(row));
        Assert.AreEqual("https://example.test/main", columns["Url1"].GetEditText(row));
        Assert.AreEqual("\uE14F", columns["Url2"].GetText(row));
        Assert.AreEqual("https://example.test/diff", columns["Url2"].GetEditText(row));
    }

    [TestMethod]
    public void CreateMainColumns_AssignsPhaseSevenTextStyles()
    {
        dataGridColumnsSettings settings = new dataGridColumnsSettings();
        foreach (dataGridColumnsSettings.dataGridColumnlayouts layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
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
        dataGridColumnsSettings settings = new dataGridColumnsSettings();
        foreach (dataGridColumnsSettings.dataGridColumnlayouts layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            layout.DisplayIndex = -1;
            layout.Visibility = Visibility.Visible;
        }
        var columns = CustomTableColumnFactory.CreateMainColumns(settings).ToDictionary(column => column.Id);
        UndefinedChartInfoRow undefinedRow = new UndefinedChartInfoRow
        {
            ChartLevelUndefined = true,
            ChartDifficultyUndefined = true,
            ChartTotalUndefined = true
        };
        UndefinedChartInfoRow definedRow = new UndefinedChartInfoRow();

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
        dataGridColumnsSettings settings = new dataGridColumnsSettings();
        foreach (dataGridColumnsSettings.dataGridColumnlayouts layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            layout.DisplayIndex = -1;
            layout.Visibility = Visibility.Visible;
        }
        var columns = CustomTableColumnFactory.CreateMainColumns(settings).ToDictionary(column => column.Id);
        var row = new
        {
            DisplayWarning = "warning text",
            comment = "comment text",
            memo = "memo text",
            UrlToolTipText = "https://example.test/main",
            UrlDiffToolTipText = "diff\nhttps://example.test/diff",
            RefTablesNames = "table A"
        };

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
        dataGridColumnsSettings settings = new dataGridColumnsSettings();
        settings.Url1.Visibility = Visibility.Visible;
        CustomTableColumn urlColumn = CustomTableColumnFactory.CreateMainColumns(settings).Single(column => column.Id == "Url1");
        var row = new { UrlDownloadIconText = "download" };

        Assert.AreEqual("\uE14F", urlColumn.GetText(row));
    }

    [TestMethod]
    public void CreateMainColumns_FormatsStatusAndSuffixColumns()
    {
        dataGridColumnsSettings settings = new dataGridColumnsSettings();
        foreach (dataGridColumnsSettings.dataGridColumnlayouts layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            layout.Visibility = Visibility.Visible;
        }
        var columns = CustomTableColumnFactory.CreateMainColumns(settings).ToDictionary(column => column.Id);
        var row = new
        {
            status = BeMusicSeeker.Models.BMSFile.BMSFileStatus.PLAY | BeMusicSeeker.Models.BMSFile.BMSFileStatus.SCORE_UNSENT,
            mode = 7,
            rate = 98,
            WAVHealth = 100,
            stddevVal = 12.345,
            scoreDifficulty = 6.789
        };

        Assert.AreEqual("Play", columns["Status"].GetText(row));
        Assert.AreEqual("7KEYS", columns["Mode"].GetText(row));
        Assert.AreEqual("98%", columns["Rate"].GetText(row));
        Assert.AreEqual("100%", columns["WavHealth"].GetText(row));
        Assert.AreEqual("12.35", columns["TScore"].GetText(row));
        Assert.AreEqual("6.79", columns["ScoreDifficulty"].GetText(row));
    }

    [TestMethod]
    public void CustomTableColumnLayout_ResolvesHorizontalOffset()
    {
        dataGridColumnsSettings settings = new dataGridColumnsSettings();
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
        CustomTableColumn[] columns = CustomTableColumnFactory.CreateMainColumns(settings).ToArray();

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
        dataGridColumnsSettings settings = new dataGridColumnsSettings();
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
        CustomTableColumn[] columns = CustomTableColumnFactory.CreateMainColumns(settings).ToArray();

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
        dataGridColumnsSettings settings = CreateOnlyTitleArtistSettings();
        CustomTableColumn[] columns = CustomTableColumnFactory.CreateMainColumns(settings).ToArray();
        var rows = new[]
        {
            new { Title = "A\tTitle", Artist = "Artist\r\nOne" },
            new { Title = "B", Artist = "Artist Two" }
        };

        string tsv = CustomTableDataTransfer.BuildTsv(rows, columns);

        Assert.AreEqual("A Title\tArtist  One\r\nB\tArtist Two", tsv);
    }

    [TestMethod]
    public void CustomTableDataTransfer_CreatesLegacyAndCustomDragFormats()
    {
        object[] rows = { new object(), new object() };

        DataObject dataObject = CustomTableDataTransfer.CreateSelectedRowsDataObject(rows);

        Assert.IsTrue(dataObject.GetDataPresent(CustomTableDataTransfer.SelectedRowsDataFormat));
        Assert.IsTrue(dataObject.GetDataPresent(CustomTableDataTransfer.LegacySelectedRowsDataFormat));
        Assert.IsTrue(CustomTableDataTransfer.TryGetSelectedRows(dataObject, out var resolvedRows));
        Assert.AreEqual(2, resolvedRows.Count);
    }

    [TestMethod]
    public void CustomTableDataTransfer_ReordersColumnsWithoutMovingStatus()
    {
        dataGridColumnsSettings settings = CreateOnlyTitleArtistSettings(includeStatus: true);
        CustomTableColumn[] columns = CustomTableColumnFactory.CreateMainColumns(settings).ToArray();
        CustomTableColumn title = columns.Single(column => column.Id == "Title");
        CustomTableColumn artist = columns.Single(column => column.Id == "Artist");
        CustomTableColumn status = columns.Single(column => column.Id == "Status");

        bool reordered = CustomTableDataTransfer.TryReorderVisibleColumns(columns, artist, status, insertAfterTarget: false);

        Assert.IsTrue(reordered);
        CustomTableColumn[] reorderedColumns = CustomTableColumnFactory.CreateMainColumns(settings).ToArray();
        CollectionAssert.AreEqual(new[] { "Status", "Artist", "Title" }, reorderedColumns.Select(column => column.Id).ToArray());
        Assert.IsFalse(status.CanReorder);
        Assert.IsTrue(title.CanReorder);
    }

    private static dataGridColumnsSettings CreateOnlyTitleArtistSettings(bool includeStatus = false)
    {
        dataGridColumnsSettings settings = new dataGridColumnsSettings();
        foreach (dataGridColumnsSettings.dataGridColumnlayouts layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
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
