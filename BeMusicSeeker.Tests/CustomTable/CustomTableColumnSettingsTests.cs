using System;
using System.Collections.Generic;
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

        settings.EnsurePlayHistoryColumnDefaults();

        Assert.AreEqual(CustomTableColumnSettings.ViewKind.PLAY_HISTORY, settings.Kind);
        Assert.IsNotNull(settings.PlayHistoryJudges);
        Assert.AreEqual(Visibility.Visible, settings.PlayHistoryBestExscore.Visibility);
        Assert.AreEqual(77, settings.PlayHistoryBestExscore.DisplayIndex);
        Assert.IsTrue(GetActiveLayouts(settings).All(item => item.Layout.DisplayIndex >= 0), "missing display index remains");
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
