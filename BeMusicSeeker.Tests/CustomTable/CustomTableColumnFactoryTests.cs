using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Xml.Linq;
using System.Xml.Serialization;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class CustomTableColumnFactoryTests
{
    /// <summary>利用者設定で題名を非表示にし、判定列を先頭・指定幅に変えて列生成へ反映することを確認し、列配置を既定値へ戻す誤りを検出します。</summary>
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

    /// <summary>全列を表示した履歴に編集先・ダウンロード・チェック操作や導入先列を設けないことを確認し、閲覧用の履歴から別の譜面状態を編集する接続を検出します。</summary>
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

    /// <summary>表示した全履歴列の並替え先を履歴エンジンが受理することを確認し、画面で選べる並替えが未対応の項目へ向く接続漏れを検出します。</summary>
    [TestMethod]
    public void CreateMainColumns_PlayHistoryVisibleSortMemberPathsAreAcceptedByPlayHistorySortEngine()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);
        foreach (CustomTableColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            layout.Visibility = Visibility.Visible;
        }

        CustomTableColumn[] sortableColumns = [.. CustomTableColumnFactory.CreateMainColumns(settings).Where(column => !string.IsNullOrWhiteSpace(column.SortMemberPath))];

        Assert.IsTrue(sortableColumns.Length > 0);
        foreach (CustomTableColumn column in sortableColumns)
        {
            Assert.IsTrue(
                PlayHistorySortEngine.TryNormalizeSortColumn(column.SortMemberPath, out _),
                column.Id + " uses unsupported PlayHistory sort path " + column.SortMemberPath);
        }
    }

    /// <summary>更新前後のクリア・DJランク・率を遷移表記へ投影し、前後それぞれの文字範囲を保持することを確認し、履歴を現在成績だけで描画したり前後の強調範囲を混ぜる誤りを検出します。</summary>
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
        Assert.AreEqual("NP -> NORMAL", clearColumn.GetText(row));
        IReadOnlyList<CustomTableTextRunStyle> clearRuns = clearColumn.GetTextRuns(row, clearColumn.GetText(row));
        Assert.AreEqual(3, clearRuns.Count);
        Assert.AreEqual(0, clearRuns[0].StartIndex);
        Assert.AreEqual("NP".Length, clearRuns[0].Length);
        Assert.AreEqual("NP".Length, clearRuns[1].StartIndex);
        Assert.AreEqual(" -> ".Length, clearRuns[1].Length);
        Assert.AreEqual("NP -> ".Length, clearRuns[2].StartIndex);
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
        Assert.AreEqual("66.67 -> 83.33", columns.Single(column => column.Id == "BestRate").GetText(row));
    }

    /// <summary>互換接頭辞の配置が欠けた設定を受け取ると全配置を補完して既定配置へ戻すことを確認し、一部欠損のまま使う誤りや不整合な旧配置の温存を検出します。</summary>
    [TestMethod]
    public void PlaylistSummaryColumnSettings_EnsureCompatibilityResetsToDefaultsWhenLayoutIsMissing()
    {
        var expectedDefaults = new PlaylistSummaryColumnSettings();
        expectedDefaults.EnsureCompatibility();
        var settings = new PlaylistSummaryColumnSettings
        {
            CompatPrefix = null
        };
        settings.Name.Width = 999;
        settings.Symbol.DisplayIndex = 20;

        settings.EnsureCompatibility();

        Assert.IsNotNull(settings.CompatPrefix);
        Assert.AreEqual(expectedDefaults.Name.Width, settings.Name.Width);
        Assert.AreEqual(expectedDefaults.FolderName.Width, settings.FolderName.Width);
        Assert.AreEqual(expectedDefaults.CompatPrefix.Width, settings.CompatPrefix.Width);
        Assert.AreEqual(expectedDefaults.Header.Width, settings.Header.Width);
        Assert.AreEqual(expectedDefaults.Data.Width, settings.Data.Width);
        Assert.AreEqual(expectedDefaults.Name.DisplayIndex, settings.Name.DisplayIndex);
        Assert.AreEqual(expectedDefaults.FolderName.DisplayIndex, settings.FolderName.DisplayIndex);
        Assert.AreEqual(expectedDefaults.CompatPrefix.DisplayIndex, settings.CompatPrefix.DisplayIndex);
        Assert.AreEqual(expectedDefaults.Symbol.DisplayIndex, settings.Symbol.DisplayIndex);
        Assert.AreEqual(expectedDefaults.Header.DisplayIndex, settings.Header.DisplayIndex);
        Assert.AreEqual(expectedDefaults.Data.DisplayIndex, settings.Data.DisplayIndex);
        Assert.AreEqual(expectedDefaults.FolderName.Visibility, settings.FolderName.Visibility);
        Assert.AreEqual(expectedDefaults.Header.Visibility, settings.Header.Visibility);
        Assert.AreEqual(expectedDefaults.Data.Visibility, settings.Data.Visibility);
    }

    /// <summary>保存XMLからフォルダ名配置が欠けた場合に欠損を認識して全配置を既定へ戻すことを確認し、デシリアライズで不足を隠して互換補完を省く誤りを検出します。</summary>
    [TestMethod]
    public void PlaylistSummaryColumnSettings_EnsureCompatibilityResetsToDefaultsWhenSerializedLayoutIsMissing()
    {
        var expectedDefaults = new PlaylistSummaryColumnSettings();
        expectedDefaults.EnsureCompatibility();
        var serializer = new XmlSerializer(typeof(PlaylistSummaryColumnSettings));
        var original = new PlaylistSummaryColumnSettings();
        original.EnsureCompatibility();
        original.Name.Width = 999;
        original.Symbol.DisplayIndex = 20;
        using var writer = new StringWriter();
        serializer.Serialize(writer, original);
        var doc = XDocument.Parse(writer.ToString());
        doc.Root?.Element(nameof(PlaylistSummaryColumnSettings.FolderName))?.Remove();

        PlaylistSummaryColumnSettings restored;
        using (var reader = new StringReader(doc.ToString()))
        {
            restored = (PlaylistSummaryColumnSettings)serializer.Deserialize(reader)!;
        }

        Assert.IsFalse(restored.HasAllLayouts());

        restored.EnsureCompatibility();

        Assert.IsTrue(restored.HasAllLayouts());
        Assert.AreEqual(expectedDefaults.Name.Width, restored.Name.Width);
        Assert.AreEqual(expectedDefaults.FolderName.Width, restored.FolderName.Width);
        Assert.AreEqual(expectedDefaults.CompatPrefix.Width, restored.CompatPrefix.Width);
        Assert.AreEqual(expectedDefaults.Header.Width, restored.Header.Width);
        Assert.AreEqual(expectedDefaults.Data.Width, restored.Data.Width);
        Assert.AreEqual(expectedDefaults.Name.DisplayIndex, restored.Name.DisplayIndex);
        Assert.AreEqual(expectedDefaults.FolderName.DisplayIndex, restored.FolderName.DisplayIndex);
        Assert.AreEqual(expectedDefaults.CompatPrefix.DisplayIndex, restored.CompatPrefix.DisplayIndex);
        Assert.AreEqual(expectedDefaults.Symbol.DisplayIndex, restored.Symbol.DisplayIndex);
        Assert.AreEqual(expectedDefaults.Header.DisplayIndex, restored.Header.DisplayIndex);
        Assert.AreEqual(expectedDefaults.Data.DisplayIndex, restored.Data.DisplayIndex);
        Assert.AreEqual(expectedDefaults.FolderName.Visibility, restored.FolderName.Visibility);
        Assert.AreEqual(expectedDefaults.Header.Visibility, restored.Header.Visibility);
        Assert.AreEqual(expectedDefaults.Data.Visibility, restored.Data.Visibility);
    }

    /// <summary>一覧のID非表示・status先頭と指定幅・名前末尾という利用者配置を列生成へ反映することを確認し、一覧設定の無視や並びの取り違えを検出します。</summary>
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

    /// <summary>一覧各列の並替え先・編集先・URL操作とチェック操作の種別を確認し、同じ表示に見えて別の状態を変更する列接続の誤りを検出します。</summary>
    [TestMethod]
    public void CreatePlaylistSummaryColumns_AssignsSortPathsAndActionMetadata()
    {
        var settings = new PlaylistSummaryColumnSettings();
        settings.FolderName.Visibility = Visibility.Visible;
        settings.Header.Visibility = Visibility.Visible;
        settings.Data.Visibility = Visibility.Visible;
        var columns = CustomTableColumnFactory.CreatePlaylistSummaryColumns(settings).ToDictionary(column => column.Id);

        Assert.AreEqual(nameof(PlaylistSummaryRow.PlaylistId), columns["PlaylistId"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.Name), columns["Name"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.FolderName), columns["FolderName"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.CompatPrefix), columns["CompatPrefix"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.Symbol), columns["Symbol"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.LastUpdate), columns["LastUpdate"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.TotalCharts), columns["TotalCharts"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.OwnedCharts), columns["OwnedCharts"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.MissingCharts), columns["MissingCharts"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.OwnedRatio), columns["OwnedRatio"].SortMemberPath);
        Assert.IsNull(columns["Link"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.HeaderUriText), columns["Header"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.DataUriText), columns["Data"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.IsExternalSync), columns["IsExternalSync"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.StatusSortOrder), columns["Status"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.IsRootFolder), columns["IsRootFolder"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.BmtSort), columns["BmtSort"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.IsBmtOutput), columns["IsBmtOutput"].SortMemberPath);
        Assert.AreEqual(nameof(PlaylistSummaryRow.Name), columns["Name"].EditPropertyName);
        Assert.AreEqual(nameof(PlaylistSummaryRow.FolderName), columns["FolderName"].EditPropertyName);
        Assert.AreEqual(nameof(PlaylistSummaryRow.CompatPrefix), columns["CompatPrefix"].EditPropertyName);
        Assert.AreEqual(nameof(PlaylistSummaryRow.Symbol), columns["Symbol"].EditPropertyName);
        Assert.AreEqual(CustomTableCellKind.ActionText, columns["Link"].CellKind);
        Assert.AreEqual(CustomTableCellKind.ActionText, columns["Header"].CellKind);
        Assert.AreEqual(CustomTableCellKind.ActionText, columns["Data"].CellKind);
        Assert.AreEqual(CustomTableCellKind.CheckBox, columns["IsExternalSync"].CellKind);
        Assert.AreEqual(CustomTableCellKind.CheckBox, columns["IsRootFolder"].CellKind);
        Assert.AreEqual(CustomTableCellKind.CheckBox, columns["IsBmtOutput"].CellKind);
    }

    /// <summary>長い題名・パス・ハッシュは省略時tooltipの対象とし、アイコンや分類値は対象外にすることを確認し、列別の省略内容公開条件の誤りを検出します。</summary>
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

    /// <summary>実健康度列のformatterで未検査・要求0・未解決を「-」にし、要求ありの0/25/100%と別列のKEYS表示を保持します。</summary>
    [TestMethod]
    public void CreateMainColumns_ResourceHealthFormatsUnknownAsDashAndRetainsNumericSuffixes()
    {
        var settings = new CustomTableColumnSettings();
        settings.WavHealth.Visibility = settings.BgaHealth.Visibility = settings.MovieHealth.Visibility = settings.Mode.Visibility = Visibility.Visible;
        var columns = CustomTableColumnFactory.CreateMainColumns(settings).ToDictionary(column => column.Id);
        CustomTableColumn[] healthColumns = [columns["WavHealth"], columns["BgaHealth"], columns["MovieHealth"]];
        var uncheckedRow = LibraryChartRow.FromChartFile(ChartTestValues.Empty());
        foreach (CustomTableColumn column in healthColumns)
        {
            Assert.AreEqual("-", column.GetText(uncheckedRow), column.Id);
        }

        // 件数を持つ通常行を実列へ渡し、0要求と要求あり0%を表示境界でも区別します。
        foreach ((int? defined, int? existing, string expected) in new (int?, int?, string)[]
            { (0, 0, "-"), (null, null, "-"), (4, 0, "0%"), (4, 1, "25%"), (1, 1, "100%") })
        {
            var info = new ResourceHealthMaintenanceSnapshot
            {
                StagefileDefined = false,
                BannerDefined = false,
                BackbmpDefined = false,
                WavFilesDefined = defined,
                WavFilesExisting = existing,
                BgaFilesDefined = defined,
                BgaFilesExisting = existing,
                MovieFilesDefined = defined,
                MovieFilesExisting = existing
            };
            var row = LibraryChartRow.FromChartFile(ChartFileProjection.WithMaintenance(ChartTestValues.Empty(), info));
            foreach (CustomTableColumn column in healthColumns)
            {
                Assert.AreEqual(expected, column.GetText(row), column.Id);
            }
        }
        Assert.AreEqual("?KEYS", columns["Mode"].GetText(uncheckedRow));
        Assert.AreEqual("7KEYS", columns["Mode"].GetText(LibraryChartRow.FromChartFile(ChartTestValues.Empty() with { Mode = 7 })));
    }

    /// <summary>表示した履歴の全テキスト列を省略時tooltipの対象へ渡すことを確認し、履歴の長い表示内容へ到達できなくなる設定漏れを検出します。</summary>
    [TestMethod]
    public void CreateMainColumns_PlayHistoryTextColumnsUseTrimTooltips()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);
        foreach (CustomTableColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            layout.DisplayIndex = -1;
            layout.Visibility = Visibility.Visible;
        }

        CustomTableColumn[] columns = [.. CustomTableColumnFactory.CreateMainColumns(settings)];

        Assert.IsTrue(columns.Length > 0);
        foreach (CustomTableColumn column in columns.Where(column => column.CellKind == CustomTableCellKind.Text))
        {
            Assert.IsTrue(column.AutoTrimTooltip, column.Id);
        }
    }

    /// <summary>一覧の名前・出力用名とURL表示は省略時tooltipの対象とし、短い分類やチェック列を除くことを確認し、列別の補助表示条件の誤りを検出します。</summary>
    [TestMethod]
    public void CreatePlaylistSummaryColumns_AssignsAutoTrimTooltipPolicy()
    {
        var settings = new PlaylistSummaryColumnSettings();
        settings.Header.Visibility = Visibility.Visible;
        settings.Data.Visibility = Visibility.Visible;
        foreach (PlaylistSummaryColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumeratePlaylistSummaryColumnLayouts(settings))
        {
            layout.DisplayIndex = -1;
            layout.Visibility = Visibility.Visible;
        }

        var columns = CustomTableColumnFactory.CreatePlaylistSummaryColumns(settings).ToDictionary(column => column.Id);

        Assert.IsFalse(columns["PlaylistId"].AutoTrimTooltip);
        Assert.IsFalse(columns["CompatPrefix"].AutoTrimTooltip);
        Assert.IsFalse(columns["Symbol"].AutoTrimTooltip);
        Assert.IsFalse(columns["IsExternalSync"].AutoTrimTooltip);
        Assert.IsFalse(columns["IsRootFolder"].AutoTrimTooltip);
        Assert.IsFalse(columns["BmtSort"].AutoTrimTooltip);
        Assert.IsFalse(columns["IsBmtOutput"].AutoTrimTooltip);
        Assert.IsTrue(columns["Name"].AutoTrimTooltip);
        Assert.IsTrue(columns["FolderName"].AutoTrimTooltip);
        Assert.IsTrue(columns["Link"].AutoTrimTooltip);
        Assert.IsTrue(columns["Header"].AutoTrimTooltip);
        Assert.IsTrue(columns["Data"].AutoTrimTooltip);
    }

    /// <summary>外部URLは操作表示と実URLのtooltip・コピーを分け、未設定と失敗の強調・日時・所持率を投影することを確認し、表示用ラベルをコピーしたり失敗表示を落とす誤りを検出します。</summary>
    [TestMethod]
    public void CreatePlaylistSummaryColumns_FormatsActionsTooltipsAndFailureBackground()
    {
        var settings = new PlaylistSummaryColumnSettings();
        settings.FolderName.Visibility = Visibility.Visible;
        settings.Header.Visibility = Visibility.Visible;
        settings.Data.Visibility = Visibility.Visible;
        var columns = CustomTableColumnFactory.CreatePlaylistSummaryColumns(settings).ToDictionary(column => column.Id);
        var row = new PlaylistSummaryRow
        {
            LinkUri = new Uri("https://example.com/"),
            HeaderUri = new Uri("https://example.com/header.json"),
            DataUri = new Uri("https://example.com/data/score.json"),
            FolderName = "AlphaTable",
            FolderNameUndefined = true,
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
        Assert.AreEqual("AlphaTable", columns["FolderName"].GetText(row));
        Assert.IsNotNull(columns["FolderName"].GetBackground(row));
        Assert.AreEqual("Open", columns["Header"].GetText(row));
        Assert.AreEqual("https://example.com/header.json", columns["Header"].GetTooltip(row));
        Assert.AreEqual("https://example.com/header.json", CustomTableDataTransfer.BuildCellText(row, columns["Header"]));
        Assert.AreEqual("Open", columns["Data"].GetText(row));
        Assert.AreEqual("https://example.com/data/score.json", columns["Data"].GetTooltip(row));
        Assert.AreEqual("https://example.com/data/score.json", CustomTableDataTransfer.BuildCellText(row, columns["Data"]));
        Assert.AreEqual(true, columns["IsExternalSync"].GetChecked(row));
        Assert.AreEqual(false, columns["IsRootFolder"].GetChecked(row));
        Assert.AreEqual("detail", columns["Status"].GetTooltip(row));
        Assert.AreEqual("2026-04-30 12:34:56", columns["LastUpdate"].GetText(row));
        Assert.AreEqual("99.8%", columns["OwnedRatio"].GetText(row));
        Assert.IsTrue(CustomTableColumnFactory.HasHighlightedWarning(row));
    }

    /// <summary>譜面各列の並替えを元値または専用数値キーへ接続し、非対応のURL等は並替えを持たないことを確認し、列の見出しと異なる値で並替える誤りを検出します。</summary>
    [TestMethod]
    public void CreateMainColumns_AssignsSortMemberPaths()
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

    /// <summary>通常一覧の表示列に設定した並替え先を仮想一覧が受理することを確認し、通常画面だけ並替え受付から外れる接続漏れを検出します。</summary>
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

    /// <summary>重複表示の列で設定した並替え先を仮想一覧が受理することを確認し、重複画面だけ並替え受付から外れる接続漏れを検出します。</summary>
    [TestMethod]
    public void CreateMainColumns_DuplicateVisibleSortMemberPathsAreVirtualRegistryColumns()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.DUPLICATE);

        AssertVisibleSortMemberPathsAreVirtualRegistryColumns(settings);
    }

    /// <summary>全走査表示の列で設定した並替え先を仮想一覧が受理することを確認し、全走査画面だけ並替え受付から外れる接続漏れを検出します。</summary>
    [TestMethod]
    public void CreateMainColumns_FullScanVisibleSortMemberPathsAreVirtualRegistryColumns()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.FULLSCAN);

        AssertVisibleSortMemberPathsAreVirtualRegistryColumns(settings);
    }

    /// <summary>導入表示の列で設定した並替え先を仮想一覧が受理することを確認し、導入画面だけ並替え受付から外れる接続漏れを検出します。</summary>
    [TestMethod]
    public void CreateMainColumns_InstallVisibleSortMemberPathsAreVirtualRegistryColumns()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.INSTALL);

        AssertVisibleSortMemberPathsAreVirtualRegistryColumns(settings);
    }

    /// <summary>全列の並替え先で仮想一覧の対象外がプレイリストレベルだけであることを確認し、所持一覧の列へ未対応の並替えを追加する誤りを検出します。</summary>
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

    /// <summary>レベル・メモ・URL・フォルダ・導入先の編集先と入力種別を接続し、BMSとbmsonの導入候補から空値を除くことを確認し、編集する状態や候補供給の取り違えを検出します。</summary>
    [TestMethod]
    public void CreateMainColumns_AssignsEditableMetadata()
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
        Assert.IsNull(columns["Url1"].TooltipTextWidth);
        Assert.IsFalse(columns["Url1"].EditOnRepeatClick);
        Assert.AreEqual("Url_diff", columns["Url2"].EditPropertyName);
        Assert.IsFalse(columns["Url2"].EditOnRepeatClick);
        Assert.AreEqual("Folder", columns["Folder"].EditPropertyName);
        Assert.AreEqual("instl_dst", columns["InstallDst"].EditPropertyName);

        ChartFile bmsFile = ChartTestValues.Empty();
        var bmsRow = LibraryChartRow.FromChartFile(ChartFileProjection.WithPackageState(
            (bmsFile),
            null,
            string.Empty,
            string.Empty,
            ["C:\\BMS\\A", "", "C:\\BMS\\B"],
            []));
        CollectionAssert.AreEqual(
            new[] { "C:\\BMS\\A", "C:\\BMS\\B" },
            columns["InstallDst"].GetEditSuggestions(bmsRow).ToArray());

        var adapterlessBmsonRow = LibraryChartRow.FromChartFile(ChartFileProjection.WithPackageState(
            (ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = "C:\\Bmson\\adapterless.bmson",
                Folder = "C:\\Bmson",
                Md5 = "cccccccccccccccccccccccccccccccc",
                Sha256 = new string('d', 64)
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
            (ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = "C:\\Bmson\\chart.bmson",
                Folder = "C:\\Bmson",
                Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Sha256 = new string('b', 64)
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

    /// <summary>候補0・1・複数と導入先有無を同じ値で比較し、保留/全件確認だけ案内を出し、文脈なし/不可はpathだけにします。編集・コピー・TSVの生値と候補は保ちます。</summary>
    [DataTestMethod]
    [DataRow(0, false)]
    [DataRow(0, true)]
    [DataRow(1, false)]
    [DataRow(1, true)]
    [DataRow(2, false)]
    [DataRow(2, true)]
    public void InstallDestinationCell_DecoratesCandidatesWithoutChangingEditPaths(int candidateCount, bool hasDestination)
    {

        var settings = new CustomTableColumnSettings();
        settings.InstallDst.Visibility = Visibility.Visible;
        string destination = hasDestination ? @"C:\BMS\A" : string.Empty;
        string[] candidates = new[] { @"C:\BMS\A", @"C:\BMS\B" }.Take(candidateCount).ToArray();
        ChartFile chart = ChartFileProjection.WithPackageState(
            (ChartTestValues.Empty() with { Path = @"C:\owned\target.bms", Md5 = new string('a', 32) }),
            destination, string.Empty, string.Empty, candidates, []);
        foreach (string context in new[] { "pending", "fullscan", "blocked", "unspecified" })
        {
            LibraryChartRow row = context == "pending" ? LibraryChartRow.FromPackageChartEntry(PackageChartEntry.FromChart(chart)) : LibraryChartRow.FromChartFile(chart);
            Func<object, bool>? permission = context == "unspecified" ? null : value => GridRowResolver.TryGetInstallDestinationEditTarget(value,
                context == "pending" ? ChartOperationSourceScope.PendingPackage : ChartOperationSourceScope.Library,
                context == "pending" ? MainViewOperationSection.InstallPending : context == "fullscan" ? MainViewOperationSection.FullScanCheck : MainViewOperationSection.Library, out _);
            CustomTableColumn column = CustomTableColumnFactory.CreateMainColumns(settings, permission).Single(candidate => candidate.Id == "InstallDst");
            string displayText = column.GetText(row);
            bool allowed = context is "pending" or "fullscan";
            Assert.AreEqual(allowed && candidateCount > 0
                ? "▼ " + (hasDestination ? destination : BeMusicSeeker.Properties.Resources.InstallDestination_SelectCandidate)
                : destination, displayText, context);
            Assert.AreEqual(destination, column.GetEditText(row));
            Assert.AreEqual(destination, CustomTableDataTransfer.BuildCellText(row, column));
            Assert.IsTrue(CustomTableDataTransfer.TryBuildTsv([row], [column], out string tsv));
            Assert.AreEqual(destination, tsv);
            Assert.AreEqual(destination, chart.InstallDestination);
            CollectionAssert.AreEqual(candidates, column.GetEditSuggestions(row).ToArray());
        }
    }

    /// <summary>本体URLと差分URLのある未所持行から各編集欄へ実URLを渡すことを確認し、操作用の表示文字やもう一方のURLを編集値に使う誤りを検出します。</summary>
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

        Assert.AreEqual("https://example.test/main", columns["Url1"].GetEditText(row));
        Assert.AreEqual("https://example.test/diff", columns["Url2"].GetEditText(row));
    }

    /// <summary>レベル・難度・TOTALの定義状態を変えると未定義の列だけ強調されることを確認し、欠損を通常値と同じ表示にする誤りを検出します。</summary>
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

    /// <summary>警告の要約と詳細、メモ・本体/差分URL・参照表名をそれぞれ表示とtooltipへ渡すことを確認し、列ごとの詳細内容を取り違える誤りを検出します。</summary>
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

    /// <summary>異文化と非Gregorian暦でも再生中の複合状態・キー数・率・健全率・統計値・日付を固定書式と既存丸めで表示することを確認し、状態分類や数値の尺度を変える誤りを検出します。</summary>
    [TestMethod]
    [DoNotParallelize]
    [DataRow("en-US")]
    [DataRow("de-DE")]
    [DataRow("fr-FR")]
    [DataRow("th-TH")]
    public void CreateMainColumns_FormatsStatusAndSuffixColumns(string cultureName)
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
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
                scoreDifficulty = 6.789,
                rankingLastupdate = new DateTime(2026, 4, 5, 13, 14, 15)
            };

            Assert.AreEqual("Play", columns["Status"].GetText(row));
            Assert.AreEqual("7KEYS", columns["Mode"].GetText(row));
            Assert.AreEqual("91.23%", columns["Rate"].GetText(row));
            Assert.AreEqual("100%", columns["WavHealth"].GetText(row));
            Assert.AreEqual("12.35", columns["TScore"].GetText(row));
            Assert.AreEqual("6.79", columns["ScoreDifficulty"].GetText(row));
            Assert.AreEqual("2026-04-05", columns["RankingLastupdate"].GetText(row));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    /// <summary>指定幅の三列で水平移動後の列判定・可視セル領域と端の罫線を計算することを確認し、移動量やクリップを無視して別のセルへ操作を届ける誤りを検出します。</summary>
    [TestMethod]
    public void CustomTableColumnLayout_ResolvesHorizontalOffset()
    {
        var settings = new CustomTableColumnSettings();
        foreach (CustomTableColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            layout.Visibility = Visibility.Hidden;
        }
        settings.Title.Visibility = Visibility.Visible;
        settings.Artist.Visibility = Visibility.Visible;
        settings.Path.Visibility = Visibility.Visible;
        settings.Title.DisplayIndex = -1;
        settings.Artist.DisplayIndex = -1;
        settings.Path.DisplayIndex = -1;
        settings.Title.Width = 100;
        settings.Artist.Width = 80;
        settings.Path.Width = 70;
        CustomTableColumn[] columns = [.. CustomTableColumnFactory.CreateMainColumns(settings)];

        bool resolved = CustomTableColumnLayout.TryResolveColumn(columns, tableX: 125, out CustomTableColumn column, out int columnIndex, out double columnX);

        Assert.IsTrue(resolved);
        Assert.AreEqual("Artist", column.Id);
        Assert.AreEqual(1, columnIndex);
        Assert.AreEqual(100d, columnX);
        Assert.AreEqual(3, CustomTableColumnLayout.CountColumnsWithinViewport(columns, horizontalOffset: 90, viewportWidth: 100));
        Rect slotRect = CustomTableColumnLayout.CreateColumnSlotRect(0, 100, 25, 0, 20);
        Rect visibleSlotRect = CustomTableColumnLayout.CreateVisibleColumnSlotRect(0, 100, 25, 100, 0, 20);
        Rect interiorRect = CustomTableColumnLayout.CreateInteriorRect(slotRect);
        Rect visibleInteriorRect = CustomTableColumnLayout.CreateVisibleColumnInteriorRect(0, 100, 25, 100, 0, 20);
        Rect clippedVisibleInteriorRect = CustomTableColumnLayout.CreateVisibleColumnInteriorRect(0, 150, 25, 100, 0, 20);
        Rect partiallyClippedGridLineInteriorRect = CustomTableColumnLayout.CreateVisibleColumnInteriorRect(0, 100, 0, 99.5, 0, 20);
        Assert.AreEqual(-25d, slotRect.X);
        Assert.AreEqual(100d, slotRect.Width);
        Assert.AreEqual(0d, visibleSlotRect.X);
        Assert.AreEqual(75d, visibleSlotRect.Width);
        Assert.AreEqual(-25d, interiorRect.X);
        Assert.AreEqual(99d, interiorRect.Width);
        Assert.AreEqual(19d, interiorRect.Height);
        Assert.AreEqual(74d, visibleInteriorRect.Width);
        Assert.AreEqual(100d, clippedVisibleInteriorRect.Width);
        Assert.AreEqual(99d, partiallyClippedGridLineInteriorRect.Width);
        Assert.AreEqual(74.5d, CustomTableColumnLayout.CreateRightGridLineX(0, 100, 25));
        Assert.IsTrue(CustomTableColumnLayout.IsGridLineFullyVisible(74.5d, 0d, 75d));
        Assert.IsTrue(CustomTableColumnLayout.IsGridLineFullyVisible(0.5d, 0d, 75d));
        Assert.IsFalse(CustomTableColumnLayout.IsGridLineFullyVisible(75d, 0d, 75d));
        Assert.IsFalse(CustomTableColumnLayout.IsGridLineFullyVisible(0d, 0d, 75d));
    }

    /// <summary>題名の端では幅変更を受理し固定URL列の端では拒否することを確認し、列境界のヒット判定で固定列を変更可能にする誤りを検出します。</summary>
    [TestMethod]
    public void CustomTableColumnLayout_ResizeHitPrefersResizableColumnBoundary()
    {
        var settings = new CustomTableColumnSettings();
        foreach (CustomTableColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
        {
            layout.Visibility = Visibility.Hidden;
        }
        settings.Title.Visibility = Visibility.Visible;
        settings.Artist.Visibility = Visibility.Visible;
        settings.Url1.Visibility = Visibility.Visible;
        settings.Title.DisplayIndex = -1;
        settings.Artist.DisplayIndex = -1;
        settings.Url1.DisplayIndex = -1;
        settings.Title.Width = 100;
        settings.Artist.Width = 80;
        settings.Url1.Width = 40;
        CustomTableColumn[] columns = [.. CustomTableColumnFactory.CreateMainColumns(settings)];

        bool titleResize = CustomTableColumnLayout.TryResolveResizeColumn(columns, surfaceX: 99, horizontalOffset: 0, viewportWidth: 200, margin: 4, out CustomTableColumn titleColumn, out _, out _);
        bool fixedUrlResize = CustomTableColumnLayout.TryResolveResizeColumn(columns, surfaceX: 220, horizontalOffset: 0, viewportWidth: 240, margin: 4, out CustomTableColumn urlColumn, out _, out _);

        Assert.IsTrue(titleResize);
        Assert.AreEqual("Title", titleColumn.Id);
        Assert.IsFalse(fixedUrlResize);
        Assert.IsNull(urlColumn);
    }

    /// <summary>題名・作者の二行を可視列順のTSVへ変換し、セル内Tab・NUL・改行だけを空白にして入力値を保つことを確認し、NUL以降の欠落や列・行の区切りの破壊を検出します。</summary>
    [TestMethod]
    public void CustomTableDataTransfer_BuildsVisibleColumnTsvAndNormalizesCellText()
    {
        CustomTableColumnSettings settings = CreateOnlyTitleArtistSettings();
        CustomTableColumn[] columns = [.. CustomTableColumnFactory.CreateMainColumns(settings)];
        var rows = new[]
        {
            new { Title = "A\tTitle", Artist = "Artist\0Part\r\nOne" },
            new { Title = "B\0Title", Artist = "Artist Two" }
        };

        Assert.IsTrue(CustomTableDataTransfer.TryBuildTsv(rows, columns, out string tsv));

        Assert.AreEqual("A Title\tArtist Part  One\r\nB Title\tArtist Two", tsv);
        Assert.AreEqual("A\tTitle", rows[0].Title);
        Assert.AreEqual("Artist\0Part\r\nOne", rows[0].Artist);
        Assert.AreEqual("B\0Title", rows[1].Title);
        Assert.AreEqual("Artist Two", rows[1].Artist);
    }

    /// <summary>一列の空セルも有効な行として先頭・中間・末尾・全空の順序と数を保ち、対象なしと空文字の成功を区別します。非表示列とnull行を除外し、入力を変更しないことも確認します。</summary>
    [DataTestMethod]
    [DataRow("leading", true, "\r\nSong")]
    [DataRow("middle", true, "Song\r\n\r\nEnd")]
    [DataRow("trailing", true, "Song\r\n")]
    [DataRow("all-empty", true, "\r\n")]
    [DataRow("one-empty", true, "")]
    [DataRow("null-value", true, "")]
    [DataRow("no-rows", false, "")]
    [DataRow("null-rows", false, "")]
    [DataRow("mixed-null-rows", true, "\r\nSong")]
    [DataRow("no-columns", false, "")]
    [DataRow("hidden-only", false, "")]
    public void CustomTableDataTransfer_TsvPreservesEmptyRowsAndDistinguishesMissingTargets(string scenario, bool expectedSuccess, string expectedText)
    {
        object?[] rows = scenario switch
        {
            "leading" => [string.Empty, "Song"],
            "middle" => ["Song", string.Empty, "End"],
            "trailing" => ["Song", string.Empty],
            "all-empty" => [string.Empty, string.Empty],
            "one-empty" => [string.Empty],
            "null-value" => [new object()],
            "no-rows" => [],
            "null-rows" => [null, null],
            "mixed-null-rows" => [null, string.Empty, null, "Song", null],
            "no-columns" or "hidden-only" => ["Song"],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        object?[] originalRows = [.. rows];
        var visible = new CustomTableColumn("Title", "TITLE", new CustomTableColumnSettings.ColumnLayout(), 1,
            null, TextAlignment.Left, _ => "display", editTextSelector: row => row as string);
        var hidden = new CustomTableColumn("Hidden", "Hidden", new CustomTableColumnSettings.ColumnLayout { Visibility = Visibility.Hidden }, 0,
            null, TextAlignment.Left, _ => throw new AssertFailedException("非表示列をコピーしました。"));
        CustomTableColumn?[] columns = scenario switch
        {
            "no-columns" => [],
            "hidden-only" => [hidden, null],
            _ => [hidden, null, visible]
        };

        bool success = CustomTableDataTransfer.TryBuildTsv(rows, columns, out string text);

        Assert.AreEqual(expectedSuccess, success);
        Assert.AreEqual(expectedText, text);
        CollectionAssert.AreEqual(originalRows, rows);
    }

    /// <summary>表示ラベルではなく編集値をコピーし、C0・C1の各文字を空白一つへ変換してNUL前後と通常文字・Unicodeを保ち、欠損対象・null・空の編集値を空文字にすることを確認し、文字の欠落や過剰な正規化を検出します。</summary>
    [TestMethod]
    public void CustomTableDataTransfer_BuildsSingleCellTextFromEditTextAndNormalizes()
    {
        string? editText = "https://example.test/a\tb\r\nc";
        var column = new CustomTableColumn(
            "Url",
            "URL",
            null,
            0,
            null,
            TextAlignment.Left,
            row => "display icon",
            editTextSelector: row => editText);

        string text = CustomTableDataTransfer.BuildCellText(new object(), column);

        Assert.AreEqual("https://example.test/a b  c", text);
        Assert.AreEqual(string.Empty, CustomTableDataTransfer.BuildCellText(null, column));
        Assert.AreEqual(string.Empty, CustomTableDataTransfer.BuildCellText(new object(), null));

        const string controlCharacters =
            "\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u0009\u000A\u000B\u000C\u000D\u000E\u000F"
            + "\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001A\u001B\u001C\u001D\u001E\u001F"
            + "\u007F\u0080\u0081\u0082\u0083\u0084\u0085\u0086\u0087\u0088\u0089\u008A\u008B\u008C\u008D\u008E\u008F"
            + "\u0090\u0091\u0092\u0093\u0094\u0095\u0096\u0097\u0098\u0099\u009A\u009B\u009C\u009D\u009E\u009F";
        const string ordinaryText = " 日本語 × (括弧)  ~\u00A0\u200B\u2028\u2029😀 ";
        (string Name, string? Input, string Expected)[] cases =
        [
            ("C0・C1の65文字", controlCharacters, new string(' ', 65)),
            ("NULの前後", "before\0after", "before after"),
            ("通常文字・空白・制御文字以外のUnicode", ordinaryText, ordinaryText),
            ("nullの編集値", null, string.Empty),
            ("空の編集値", string.Empty, string.Empty)
        ];
        foreach ((string name, string? input, string expected) in cases)
        {
            editText = input;

            Assert.AreEqual(expected, CustomTableDataTransfer.BuildCellText(new object(), column), name);
        }
    }

    /// <summary>Ctrl+C・Ctrl+Shift+C・Ctrl+Aを各操作へ割り当て、Alt併用は受理しないことを確認し、コピーの単位や無関係な修飾キーを取り違える誤りを検出します。</summary>
    [TestMethod]
    public void CustomTableView_ResolvesCopyKeyboardShortcuts()
    {
        Assert.AreEqual(CustomTableKeyboardCommand.CopyCurrentCell, CustomTableView.ResolveKeyboardCommand(Key.C, ModifierKeys.Control));
        Assert.AreEqual(CustomTableKeyboardCommand.CopySelectedRowsTsv, CustomTableView.ResolveKeyboardCommand(Key.C, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.AreEqual(CustomTableKeyboardCommand.None, CustomTableView.ResolveKeyboardCommand(Key.C, ModifierKeys.Control | ModifierKeys.Alt));
        Assert.AreEqual(CustomTableKeyboardCommand.None, CustomTableView.ResolveKeyboardCommand(Key.C, ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt));
        Assert.AreEqual(CustomTableKeyboardCommand.SelectAllRows, CustomTableView.ResolveKeyboardCommand(Key.A, ModifierKeys.Control));
    }

    /// <summary>選択二行のドラッグデータから新旧形式・行種別・主行と選択一覧を取得できることを確認し、既存ドラッグ受付との互換や主行の指定を失う誤りを検出します。</summary>
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

    /// <summary>作者をstatusの前へ移す要求でもstatus先頭を保ち、利用者設定の作者と題名の順を更新することを確認し、固定列まで動かす誤りや並替えの未保存を検出します。</summary>
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
