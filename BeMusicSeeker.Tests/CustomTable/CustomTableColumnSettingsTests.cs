using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class CustomTableColumnSettingsTests
{
    /// <summary>履歴のMD5/SHA256/RAWと既存Titleの利用者指定を実providerへ保存し、新wrapperと補完後も幅・順序・可視性を保持して実列へ反映します。</summary>
    [TestMethod]
    public void PlayHistoryHashLayouts_SaveAndReloadWithoutChangingExistingChoices()
    {
        string directory = MainWindowViewModelTestFactory.CreateIsolatedSettingsDirectory();
        string path = Path.Combine(directory, "user.config");
        try
        {
            Settings settings = PortableSettingsPersistenceTests.OpenSettings(path);
            var layouts = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);
            layouts.Hash.Width = 173; layouts.Hash.DisplayIndex = 100; layouts.Hash.Visibility = Visibility.Visible;
            layouts.Sha256.Width = 279; layouts.Sha256.DisplayIndex = 101; layouts.Sha256.Visibility = Visibility.Hidden;
            layouts.PlayHistoryRawHash.Width = 191; layouts.PlayHistoryRawHash.DisplayIndex = 102; layouts.PlayHistoryRawHash.Visibility = Visibility.Visible;
            layouts.Title.Width = 181; layouts.Title.DisplayIndex = 103; layouts.Title.Visibility = Visibility.Visible;
            settings.PlayHistoryCustomTableColumnSettings = layouts; settings.Save();
            Settings reopened = PortableSettingsPersistenceTests.OpenSettings(path);
            Settings.EnsureColumnSettingDefaults(reopened);
            CustomTableColumnSettings restored = reopened.PlayHistoryCustomTableColumnSettings;
            var columns = CustomTableColumnFactory.CreateMainColumns(restored).ToDictionary(column => column.Id);
            foreach ((string id, CustomTableColumnSettings.ColumnLayout actual, int width, int order, Visibility visible) in new[]
            {
                ("Hash", restored.Hash, 173, 100, Visibility.Visible), ("Sha256", restored.Sha256, 279, 101, Visibility.Hidden),
                ("RawHash", restored.PlayHistoryRawHash, 191, 102, Visibility.Visible), ("Title", restored.Title, 181, 103, Visibility.Visible)
            })
            {
                Assert.AreEqual(width, actual.Width); Assert.AreEqual(order, actual.DisplayIndex); Assert.AreEqual(visible, actual.Visibility);
                Assert.AreEqual(visible == Visibility.Visible, columns.ContainsKey(id));
                if (columns.TryGetValue(id, out CustomTableColumn? column)) { Assert.AreSame(actual, column.Layout); Assert.AreEqual(width, column.Width); Assert.AreEqual(order, column.DisplayIndex); }
            }
            restored.Sha256.Visibility = Visibility.Visible;
            CustomTableColumn sha = CustomTableColumnFactory.CreateMainColumns(restored).Single(column => column.Id == "Sha256");
            Assert.AreSame(restored.Sha256, sha.Layout); Assert.AreEqual(279, sha.Width); Assert.AreEqual(101, sha.DisplayIndex);
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); } }
    }

    /// <summary>両URL列の異なる利用者幅を実providerへ保存し、新wrapperで読戻し・既定補完した後も幅だけからボタン/通常セルを復元します。別modeの保存に依存しません。</summary>
    [TestMethod]
    public void UrlWidths_SaveAndReloadThroughProviderRestoreColumnRoles()
    {
        string directory = MainWindowViewModelTestFactory.CreateIsolatedSettingsDirectory();
        string path = Path.Combine(directory, "user.config");
        try
        {
            Settings settings = PortableSettingsPersistenceTests.OpenSettings(path);
            var columns = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAYLIST);
            columns.Url1.Visibility = columns.Url2.Visibility = Visibility.Visible;
            columns.Url1.Width = 41; columns.Url2.Width = 240;
            settings.PlaylistCustomTableColumnSettings = columns;
            settings.Save();

            Settings reloaded = PortableSettingsPersistenceTests.OpenSettings(path);
            Settings.EnsureColumnSettingDefaults(reloaded);
            CustomTableColumnSettings restored = reloaded.PlaylistCustomTableColumnSettings;
            Assert.AreEqual(41, restored.Url1.Width); Assert.AreEqual(240, restored.Url2.Width);
            var actual = CustomTableColumnFactory.CreateMainColumns(restored).ToDictionary(column => column.Id);
            foreach (string id in new[] { "Url1", "Url2" })
            {
                Assert.AreEqual(CustomTableCellKind.Text, actual[id].CellKind);
                Assert.IsTrue(actual[id].CanResize); Assert.IsTrue(actual[id].EditOnRepeatClick);
                actual[id].Layout.Width = 40;
                Assert.AreEqual(CustomTableCellKind.DownloadIcon, actual[id].CellKind);
            }
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); } }
    }

    /// <summary>全表示種別で非表示列も一意の表示位置を持ち、表示切替時の列位置衝突を防ぎます。</summary>
    [TestMethod]
    public void Constructor_AssignsUniqueDisplayIndexToHiddenColumns()
    {
        foreach (CustomTableColumnSettings.ViewKind ViewKind in Enum.GetValues(typeof(CustomTableColumnSettings.ViewKind)))
        {
            var settings = new CustomTableColumnSettings(ViewKind);
            int[] displayIndexes = [.. GetActiveLayouts(settings).Select((item) => item.Layout.DisplayIndex)];

            Assert.AreEqual(displayIndexes.Length, displayIndexes.Distinct().Count(), ViewKind.ToString());
        }
    }

    /// <summary>譜面情報列の欠損を補完し、利用者が指定したEntryLevelの幅・位置・表示とLevelの位置・表示を上書きしないことを確認します。</summary>
    [TestMethod]
    public void EnsureChartInfoColumnDefaults_CompletesMissingLayoutsWithoutOverwritingExistingChoices()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAYLIST);
        settings.EntryLevel.Width = 77;
        settings.EntryLevel.Visibility = Visibility.Visible;
        settings.EntryLevel.DisplayIndex = 77;
        settings.Level.Visibility = Visibility.Visible;
        settings.Level.DisplayIndex = 78;
        settings.ChartDifficulty = null;

        settings.EnsureChartInfoColumnDefaults(CustomTableColumnSettings.ViewKind.PLAYLIST);

        Assert.IsNotNull(settings.ChartDifficulty);
        Assert.AreEqual(77, settings.EntryLevel.Width);
        Assert.AreEqual(Visibility.Visible, settings.EntryLevel.Visibility);
        Assert.AreEqual(77, settings.EntryLevel.DisplayIndex);
        Assert.AreEqual(Visibility.Visible, settings.Level.Visibility);
        Assert.AreEqual(78, settings.Level.DisplayIndex);
    }

    /// <summary>旧設定で状態列が欠けても表示可能な列を補い、設定読込み後の欠損を残しません。</summary>
    [TestMethod]
    public void EnsureChartInfoColumnDefaults_RecreatesMissingStatusLayout()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD)
        {
            Status = null
        };

        settings.EnsureChartInfoColumnDefaults(CustomTableColumnSettings.ViewKind.STANDARD);

        Assert.IsNotNull(settings.Status);
        Assert.AreEqual(Visibility.Visible, settings.Status.Visibility);
    }

    /// <summary>プレイ履歴の欠損列だけを補完し、利用者の表示選択・位置を保持して未設定位置を残しません。</summary>
    [TestMethod]
    public void EnsurePlayHistoryColumnDefaults_CompletesMissingLayoutsWithoutOverwritingExistingChoices()
    {
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);
        settings.PlayHistoryBestExscore.Visibility = Visibility.Visible;
        settings.PlayHistoryBestExscore.DisplayIndex = 77;
        settings.PlayHistoryJudges = null;
        // 従来の履歴設定にもHashは保存されるが、履歴の有効列でなかったため位置未設定になり得ます。
        settings.Hash.Width = 119; settings.Hash.Visibility = Visibility.Visible; settings.Hash.DisplayIndex = -1;

        settings.EnsurePlayHistoryColumnDefaults();

        Assert.AreEqual(CustomTableColumnSettings.ViewKind.PLAY_HISTORY, settings.Kind);
        Assert.IsNotNull(settings.PlayHistoryJudges);
        Assert.AreEqual(Visibility.Visible, settings.PlayHistoryBestExscore.Visibility);
        Assert.AreEqual(77, settings.PlayHistoryBestExscore.DisplayIndex);
        Assert.IsTrue(GetActiveLayouts(settings).All(item => item.Layout.DisplayIndex >= 0), "missing display index remains");
        Assert.AreEqual(119, settings.Hash.Width); Assert.AreEqual(Visibility.Visible, settings.Hash.Visibility);
        Assert.IsTrue(settings.Hash.DisplayIndex >= 0);
        Assert.AreEqual(GetActiveLayouts(settings).Count, GetActiveLayouts(settings).Select(item => item.Layout.DisplayIndex).Distinct().Count());
    }

    /// <summary>既存の履歴列設定は利用者の選択を保持して補完し、設定全体が欠ける場合にも再生成します。</summary>
    [TestMethod]
    public void EnsureColumnSettingDefaults_RecreatesAndCompletesPlayHistorySettings()
    {
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
        var existing = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);
        existing.PlayHistoryBestExscore.Visibility = Visibility.Visible;
        existing.PlayHistoryBestExscore.DisplayIndex = 77;
        existing.PlayHistoryJudges = null;
        settings.PlayHistoryCustomTableColumnSettings = existing;

        Settings.EnsureColumnSettingDefaults(settings);

        Assert.AreSame(existing, settings.PlayHistoryCustomTableColumnSettings);
        Assert.AreEqual(CustomTableColumnSettings.ViewKind.PLAY_HISTORY, settings.PlayHistoryCustomTableColumnSettings.Kind);
        Assert.IsNotNull(settings.PlayHistoryCustomTableColumnSettings.PlayHistoryJudges);
        Assert.AreEqual(Visibility.Visible, settings.PlayHistoryCustomTableColumnSettings.PlayHistoryBestExscore.Visibility);
        Assert.AreEqual(77, settings.PlayHistoryCustomTableColumnSettings.PlayHistoryBestExscore.DisplayIndex);

        settings.PlayHistoryCustomTableColumnSettings = null;
        Settings.EnsureColumnSettingDefaults(settings);

        Assert.IsNotNull(settings.PlayHistoryCustomTableColumnSettings);
        Assert.AreEqual(CustomTableColumnSettings.ViewKind.PLAY_HISTORY, settings.PlayHistoryCustomTableColumnSettings.Kind);
    }

    private static IReadOnlyList<(string Name, CustomTableColumnSettings.ColumnLayout Layout)> GetLayouts(CustomTableColumnSettings settings)
    {
        return [.. typeof(CustomTableColumnSettings)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where((property) => property.PropertyType == typeof(CustomTableColumnSettings.ColumnLayout))
            .Select((property) => (property.Name, Layout: (CustomTableColumnSettings.ColumnLayout)property.GetValue(settings)!))];
    }

    private static IReadOnlyList<(string Name, CustomTableColumnSettings.ColumnLayout Layout)> GetActiveLayouts(CustomTableColumnSettings settings)
    {
        HashSet<CustomTableColumnSettings.ColumnLayout> activeLayouts = [.. CustomTableColumnFactory.EnumerateMainColumnLayouts(settings)];
        return [.. GetLayouts(settings).Where((item) => item.Layout != null && activeLayouts.Contains(item.Layout))];
    }
}
