using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Xml.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class CustomTableColumnSettingsTests
{
    /// <summary>履歴のMD5/SHA256/RAWと既存Titleの利用者指定を実providerへ保存し、新wrapperと補完後も幅・順序・可視性を保持して実列へ反映します。</summary>
    [TestMethod]
    public void PlayHistoryHashLayouts_SaveAndReloadWithoutChangingExistingChoices()
    {
        string directory = TestSettingsFactory.CreateIsolatedSettingsDirectory();
        string path = Path.Combine(directory, "user.config");
        try
        {
            Settings settings = TestSettingsFactory.OpenSettings(path);
            var layouts = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);
            layouts.Hash.Width = 173; layouts.Hash.DisplayIndex = 100; layouts.Hash.Visibility = Visibility.Visible;
            layouts.Sha256.Width = 279; layouts.Sha256.DisplayIndex = 101; layouts.Sha256.Visibility = Visibility.Hidden;
            layouts.PlayHistoryRawHash.Width = 191; layouts.PlayHistoryRawHash.DisplayIndex = 102; layouts.PlayHistoryRawHash.Visibility = Visibility.Visible;
            layouts.Title.Width = 181; layouts.Title.DisplayIndex = 103; layouts.Title.Visibility = Visibility.Visible;
            settings.PlayHistoryCustomTableColumnSettings = layouts; settings.Save();
            Settings reopened = TestSettingsFactory.OpenSettings(path);
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
        string directory = TestSettingsFactory.CreateIsolatedSettingsDirectory();
        string path = Path.Combine(directory, "user.config");
        try
        {
            Settings settings = TestSettingsFactory.OpenSettings(path);
            var columns = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAYLIST);
            columns.Url1.Visibility = columns.Url2.Visibility = Visibility.Visible;
            columns.Url1.Width = 41; columns.Url2.Width = 240;
            settings.PlaylistCustomTableColumnSettings = columns;
            settings.Save();

            Settings reloaded = TestSettingsFactory.OpenSettings(path);
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
        Settings settings = TestSettingsFactory.CreateIsolatedSettings();
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

    private static readonly (CustomTableColumnSettings.ViewKind Kind, string SettingName)[] SavedMainSettings =
    [
        (CustomTableColumnSettings.ViewKind.STANDARD, "StandardCustomTableColumnSettings"),
        (CustomTableColumnSettings.ViewKind.PLAYLIST, "PlaylistCustomTableColumnSettings"),
        (CustomTableColumnSettings.ViewKind.FULLSCAN, "FullScanCustomTableColumnSettings"),
        (CustomTableColumnSettings.ViewKind.DUPLICATE, "DuplicateCustomTableColumnSettings"),
        (CustomTableColumnSettings.ViewKind.ENCODING, "EncodingCustomTableColumnSettings"),
        (CustomTableColumnSettings.ViewKind.INSTALL, "InstallCustomTableColumnSettings"),
        (CustomTableColumnSettings.ViewKind.ZERO_NOTE, "ZeroNoteCustomTableColumnSettings"),
        (CustomTableColumnSettings.ViewKind.CHART_INFO_PARSE_ERROR, "ChartInfoParseErrorCustomTableColumnSettings"),
        (CustomTableColumnSettings.ViewKind.UNREGISTERED, "UnregisteredCustomTableColumnSettings"),
        (CustomTableColumnSettings.ViewKind.PLAY_HISTORY, "PlayHistoryCustomTableColumnSettings")
    ];

    private const string SavedPlaylistSummarySettingName = "PlaylistSummaryColumnsSettings";

    /// <summary>
    /// 列設定の型を <c>BeMusicSeeker.ViewModels</c> に置いていた版（本番のprovider）が保存したuser.configから、列設定の11項目を変更せずに切り出した固定入力です。
    /// 保存形式を意図して変え移行仕様を定めた場合も、旧形式の入力として残し、作り直しません。
    /// </summary>
    private static string SavedColumnSettingsFixturePath => Path.Combine(AppContext.BaseDirectory, "CustomTable", "TestData", "saved-column-settings.user.config");

    /// <summary>
    /// 列設定の型を <c>BeMusicSeeker.ViewModels</c> に置いていた版が保存した列設定XMLを現在の本番の読込みと補完で読み、全表示先とプレイリスト一覧の利用者の幅・表示位置・表示状態を既定値へ戻さず保つことを確認します。
    /// 全10種別のTitle幅、Visible/Hidden/Collapsedの3種類、綴り誤りの列名CharcterEncoding、履歴の固有列、19列がそろった一覧の保持を値で見ます。
    /// クラス名・列挙名・プロパティ名の変更やXmlRoot/TypeConverterの付与で、読込みに失敗して既定へ戻る誤りを検出します。
    /// </summary>
    [TestMethod]
    public void SavedColumnSettingsFromPreviousVersion_AreReadWithoutRevertingUserChoices()
    {
        Settings settings = TestSettingsFactory.OpenSettings(CopySavedColumnSettingsFixture());

        AssertSavedUserChoices(settings);
    }

    /// <summary>
    /// 列設定の型を <c>BeMusicSeeker.ViewModels</c> に置いていた版が保存した列設定XMLを読んで保存し直した後も、ルート要素名・全要素の名前の経路・列挙名・serializeAsがその版と同じで、型名や名前空間を出さず、読み直しても同じ値になることを確認します。
    /// 目印の値が保存XMLに現れることで、固定入力ではなく新しい直列化の出力を比較していることを確かめます。
    /// 新しい基底の公開プロパティの混入、XmlElementによる改名、列挙子の改名、TypeConverterの付与を検出します。
    /// 意図して列を追加するときは、固定入力を作り直さず、期待する要素の集合へ追加した列を明示的に足します。
    /// </summary>
    [TestMethod]
    public void SavedColumnSettingsFromPreviousVersion_AreSavedAgainWithSameElementAndEnumNames()
    {
        string path = CopySavedColumnSettingsFixture();
        Settings settings = TestSettingsFactory.OpenSettings(path);
        AssertSavedUserChoices(settings);
        var store = new SettingsMainChartColumnSettingsStore(() => settings);
        foreach ((CustomTableColumnSettings.ViewKind kind, _) in SavedMainSettings)
        {
            store.GetMain(kind, reset: false).Url1.Width = 1000 + (int)kind;
        }
        store.GetPlaylistSummary(ensureCompatibility: true).OwnedRatio.Width = 1100;
        settings.Save();

        var expected = XDocument.Load(SavedColumnSettingsFixturePath);
        var actual = XDocument.Load(path);
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        string[] settingNames = [.. SavedMainSettings.Select(item => item.SettingName), SavedPlaylistSummarySettingName];
        foreach (string settingName in settingNames)
        {
            XElement expectedSetting = SavedSettingElement(expected, settingName);
            XElement actualSetting = SavedSettingElement(actual, settingName);
            Assert.AreEqual("Xml", (string?)expectedSetting.Attribute("serializeAs"), settingName);
            Assert.AreEqual("Xml", (string?)actualSetting.Attribute("serializeAs"), settingName);
            XElement expectedRoot = expectedSetting.Element("value")!.Elements().Single();
            XElement actualRoot = actualSetting.Element("value")!.Elements().Single();
            Assert.AreEqual(expectedRoot.Name, actualRoot.Name, settingName);
            Assert.AreEqual(string.Empty, actualRoot.Name.NamespaceName, settingName);
            Assert.IsFalse(actualRoot.DescendantsAndSelf().Any(element => element.Attribute(xsi + "type") != null), settingName + " has xsi:type");
            CollectionAssert.AreEquivalent(ElementPaths(expectedRoot), ElementPaths(actualRoot), settingName);
            foreach (string columnName in new[] { "Genre", "Memo", "PlayHistoryOption", "Hash", "FolderName", "Link" })
            {
                Assert.AreEqual((string?)expectedRoot.Element(columnName)?.Element("Visibility"), (string?)actualRoot.Element(columnName)?.Element("Visibility"), settingName + "/" + columnName);
            }
            Assert.AreEqual((string?)expectedRoot.Element("Kind"), (string?)actualRoot.Element("Kind"), settingName);
        }
        foreach ((CustomTableColumnSettings.ViewKind kind, string settingName) in SavedMainSettings)
        {
            XElement root = SavedSettingElement(actual, settingName).Element("value")!.Elements().Single();
            Assert.AreEqual(kind.ToString(), (string?)root.Element("Kind"), settingName);
            Assert.AreEqual((1000 + (int)kind).ToString(System.Globalization.CultureInfo.InvariantCulture), (string?)root.Element("Url1")?.Element("Width"), settingName + " marker");
        }
        XElement summaryRoot = SavedSettingElement(actual, SavedPlaylistSummarySettingName).Element("value")!.Elements().Single();
        Assert.AreEqual("1100", (string?)summaryRoot.Element("OwnedRatio")?.Element("Width"), "summary marker");
        XElement standardRoot = SavedSettingElement(actual, "StandardCustomTableColumnSettings").Element("value")!.Elements().Single();
        Assert.AreEqual(Visibility.Collapsed.ToString(), (string?)standardRoot.Element("Genre")?.Element("Visibility"));

        Settings reopened = TestSettingsFactory.OpenSettings(path);
        AssertSavedUserChoices(reopened);
        var reopenedStore = new SettingsMainChartColumnSettingsStore(() => reopened);
        foreach ((CustomTableColumnSettings.ViewKind kind, _) in SavedMainSettings)
        {
            Assert.AreEqual(1000 + (int)kind, reopenedStore.GetMain(kind, reset: false).Url1.Width, kind.ToString());
        }
        Assert.AreEqual(1100, reopenedStore.GetPlaylistSummary(ensureCompatibility: true).OwnedRatio.Width);
    }

    /// <summary>
    /// 一覧が購読する両方のColumnLayoutが、幅・表示位置・表示状態の変更をsetterが戻る前に、同じプロパティ名で1回ずつ通知することを確認します。
    /// 基底の差し替えで通知を失う、名前が変わる、INotifyPropertyChangedを実装しなくなる誤りを検出します。
    /// </summary>
    [TestMethod]
    public void ColumnLayouts_NotifyWidthDisplayIndexAndVisibilityChangesSynchronously()
    {
        (string Label, Func<ICustomTableColumnLayout> Create)[] factories =
        [
            ("CustomTableColumnSettings.ColumnLayout", () => new CustomTableColumnSettings.ColumnLayout()),
            ("PlaylistSummaryColumnSettings.ColumnLayout", () => new PlaylistSummaryColumnSettings.ColumnLayout())
        ];
        (string Name, Action<ICustomTableColumnLayout> Change, Func<ICustomTableColumnLayout, object> Read, object Expected)[] changes =
        [
            (nameof(ICustomTableColumnLayout.Width), layout => layout.Width = 77, layout => layout.Width, 77),
            (nameof(ICustomTableColumnLayout.DisplayIndex), layout => layout.DisplayIndex = 5, layout => layout.DisplayIndex, 5),
            (nameof(ICustomTableColumnLayout.Visibility), layout => layout.Visibility = Visibility.Collapsed, layout => layout.Visibility, Visibility.Collapsed)
        ];
        foreach ((string label, Func<ICustomTableColumnLayout> create) in factories)
        {
            foreach ((string name, Action<ICustomTableColumnLayout> change, Func<ICustomTableColumnLayout, object> read, object expectedValue) in changes)
            {
                ICustomTableColumnLayout layout = create();
                Assert.IsInstanceOfType(layout, typeof(INotifyPropertyChanged), label);
                var notifications = new List<(object? Sender, string? Name)>();
                ((INotifyPropertyChanged)layout).PropertyChanged += (sender, e) => notifications.Add((sender, e.PropertyName));

                change(layout);

                Assert.AreEqual(1, notifications.Count, label + "." + name);
                Assert.AreSame(layout, notifications[0].Sender, label + "." + name);
                Assert.AreEqual(name, notifications[0].Name, label);
                Assert.AreEqual(expectedValue, read(layout), label + "." + name);
            }
        }
    }

    private static string CopySavedColumnSettingsFixture()
    {
        string path = Path.Combine(TestSettingsFactory.CreateIsolatedSettingsDirectory(), "user.config");
        File.Copy(SavedColumnSettingsFixturePath, path);
        return path;
    }

    private static XElement SavedSettingElement(XDocument document, string settingName)
    {
        return document.Descendants("setting").Single(element => (string?)element.Attribute("name") == settingName);
    }

    private static List<string> ElementPaths(XElement root)
    {
        return [.. root.DescendantsAndSelf().Select(element => string.Join("/", element.AncestorsAndSelf().Reverse().Select(ancestor => ancestor.Name.ToString()))).Distinct()];
    }

    private static void AssertSavedUserChoices(Settings settings)
    {
        Settings.EnsureColumnSettingDefaults(settings);
        var store = new SettingsMainChartColumnSettingsStore(() => settings);
        foreach ((CustomTableColumnSettings.ViewKind kind, _) in SavedMainSettings)
        {
            Assert.AreEqual(300 + (int)kind, store.GetMain(kind, reset: false).Title.Width, kind.ToString());
        }

        CustomTableColumnSettings standard = store.GetMain(CustomTableColumnSettings.ViewKind.STANDARD, reset: false);
        Assert.AreEqual(Visibility.Visible, standard.Memo.Visibility);
        Assert.AreEqual(Visibility.Collapsed, standard.Genre.Visibility);
        Assert.AreEqual(45, standard.Artist.DisplayIndex);

        Assert.AreEqual(120, store.GetMain(CustomTableColumnSettings.ViewKind.ENCODING, reset: false).CharcterEncoding.Width);

        CustomTableColumnSettings history = store.GetMain(CustomTableColumnSettings.ViewKind.PLAY_HISTORY, reset: false);
        Assert.AreEqual(211, history.PlayHistoryJudges.Width);
        Assert.AreEqual(Visibility.Hidden, history.PlayHistoryOption.Visibility);
        Assert.AreEqual(Visibility.Visible, history.Hash.Visibility);
        Assert.AreEqual(30, history.Hash.DisplayIndex);

        PlaylistSummaryColumnSettings summary = store.GetPlaylistSummary(ensureCompatibility: true);
        Assert.AreEqual(999, summary.Name.Width);
        Assert.AreEqual(Visibility.Visible, summary.FolderName.Visibility);
        Assert.AreEqual(Visibility.Collapsed, summary.Link.Visibility);
        Assert.AreEqual(18, summary.Symbol.DisplayIndex);
        Assert.AreEqual(5, summary.IsBmtOutput.DisplayIndex);
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
