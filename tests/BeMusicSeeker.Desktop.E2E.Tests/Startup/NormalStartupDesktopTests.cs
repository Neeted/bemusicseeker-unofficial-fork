using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using BeMusicSeeker.Desktop.E2E.Tests.Fixtures;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Scenarios;
using BeMusicSeeker.TestSupport.Verification;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using SQLite;
using static BeMusicSeeker.Desktop.E2E.Tests.Fixtures.DesktopScenarioUi;

namespace BeMusicSeeker.Desktop.E2E.Tests.Startup;

/// <summary>配布物を利用者と同じ形で通常起動し、portable設定の読込・実UI操作・正常終了後の保存までを確認します。</summary>
[TestClass]
public sealed class NormalStartupDesktopTests
{
    /// <summary>
    /// 条件: catalogのCurrentを専有コピーへ展開し、`config/user.config`に既定値と区別できるLang・テーマ・BMS登録と未知キーを置き、
    /// `data/`・旧設定・test session・control pipe・`BMS_TEST_*`なしで、appの外の空CWDから起動する。
    /// 期待結果: 起動が操作可能になり所有modalがなく、一覧に設定のrootの全譜面が並ぶ。検索入力で一致譜面だけに絞られ、
    /// 設定画面は一つだけ開閉でき、外観ページの選択テーマが設定値と一致する。正常終了(exit 0)後、設定は値と未知キーを保持し、
    /// 標準配置のDBに全譜面があり、CWDは空のままである。
    /// 検出する誤り: test session前提でしか成立しない起動、設定の読込元違い、検索や設定画面が通常構成で動かない、
    /// 終了保存での値・未知キーの消失、DBや設定の配置がCWDやapp外へ依存する誤り。
    /// </summary>
    [TestMethod(DisplayName = "[SC-16] 配布物を通常起動しportable設定を読んで検索と設定画面の開閉後に正常終了する")]
    [TestCategory("ReleaseAcceptance")]
    [TestCategory("SC-16")]
    public async Task PublishedCurrent_StartsNormallyReadsPortableSettingsAndClosesNormally()
    {
        // 同じprocessでSystem.Windows.AutomationとFlaUIを併用すると、後続のDesktop caseのproperty取得が不安定になるため、
        // 起動・終了時のwindow観測にもFlaUI(UIA3)を使います。この観測用接続はprocessの終端確認まで保持します。
        UIA3Automation? observer = null;
        var session = PublishedApplicationSession.ForDesktop(TimeSpan.FromMinutes(3),
            window => Describe(observer ?? throw new InvalidOperationException("観測用のUIA接続がありません。"), window));
        Exception? primary = null;
        StreamWriter? progress = null;
        UIA3Automation? automation = null;
        Application? attached = null;
        try
        {
            observer = new UIA3Automation();
            var log = new StreamWriter(session.Workspace.GetPath("uia-steps.log")) { AutoFlush = true };
            progress = log;
            void Step(string message) => log.WriteLine($"{DateTime.UtcNow:O} remainingMs={session.Deadline.Remaining.TotalMilliseconds:F0} {message}");
            string repository = UpdateDeploymentTestSupport.FindRepositoryRoot();
            DistributionCatalog catalog = await DistributionCatalog.ReadFromEnvironmentAsync(repository, session.Deadline);
            Console.WriteLine($"受入入力: Current={catalog.Current.Version}, SHA256={catalog.Current.Sha256}, ZIP={catalog.Current.PackagePath}; ソースbuildの実行体とは別のcatalog配布を使用します。");
            string app = await catalog.Current.ExtractInstallationAsync(session.Workspace, "app", session.Deadline);
            var fixture = new NormalStartupFixture(session.Workspace, app);
            await session.AssertIsolationAsync(fixture.Launch);

            // test session・manifest・control pipeなしで、一般CLIのNoUpdate指定だけで操作可能になる。
            Step("before normal start");
            (OwnedProcess owner, Process process) = await session.StartApplicationAsync(fixture.Launch, maintenance: true);
            Step("after ready markers/NoUpdate request");
            attached = Application.Attach(process.Id);
            automation = new UIA3Automation();
            Window window = DesktopApplicationSession.FindMainWindow(attached, automation, () => process.HasExited, session.Deadline);
            Assert.IsTrue(window.IsEnabled && !window.IsOffscreen, "MainWindowが操作可能ではありません。");
            Assert.AreEqual(0, window.ModalWindows.Length, "起動直後に予期しない所有modalがあります。");

            // 設定のBMS登録が指すrootの全譜面が、identity(path)で一覧に並ぶ。
            AutomationElement table = FindUnique(window, "MainChartTable", session.Deadline, Step);
            DesktopChartInput[] all = [.. fixture.Charts];
            WaitForRows(table, all, "起動後の一覧に設定rootの全譜面が並びませんでした。", session.Deadline);
            Step("after initial rows");

            // 実UIの検索入力が実検索と表示へ接続する。
            AutomationElement host = FindUnique(window, "MainKeywordSearch", session.Deadline, Step);
            AutomationElement input = FindUnique(host, "KeywordSearchInput", session.Deadline, Step);
            EnsureInputDeadline(session.Deadline);
            input.Patterns.Value.Pattern.SetValue(NormalStartupFixture.SearchText);
            Assert.AreEqual(NormalStartupFixture.SearchText, input.Patterns.Value.Pattern.Value.Value);
            DesktopChartInput[] matching = [.. fixture.Matching];
            Assert.IsTrue(matching.Length > 0 && matching.Length < all.Length, "検索の入力は一致と不一致の両方を含む必要があります。");
            WaitForRows(table, matching, "検索語に一致する譜面だけへ一覧が絞られませんでした。", session.Deadline);
            Assert.AreEqual(0, window.ModalWindows.Length, "検索後に予期しない所有modalがあります。");
            Step("after keyword rows");

            // 設定画面が一つだけ開き、外観ページの選択テーマが設定値と一致して、閉じるとMainWindowへ戻る。
            string themeName = ReadThemeName(app);
            Window settings = OpenSettings(window, session.Deadline, Step);
            Assert.AreEqual(1, window.ModalWindows.Length, "設定画面は所有modalとして一つだけ開く必要があります。");
            FindUnique(settings, "SettingsCategoryAppearance", session.Deadline, Step).Patterns.SelectionItem.Pattern.Select();
            AssertSelectedOption(FindUnique(settings, "SettingsTheme", session.Deadline, Step), themeName);
            CloseSettings(window, settings, session.Deadline, Step);
            Assert.IsTrue(window.IsEnabled);
            Assert.AreEqual(0, window.ModalWindows.Length);
            Step("after settings close");

            // UIA接続を先に解放し、通常Closeからapp・所有Job全体のexit 0まで待つ。
            automation.Dispose(); automation = null;
            attached.Dispose(); attached = null;
            // 終了時の設定保存が起きたことを見るため、Closeを送る直前のbytesを控える。
            string hashBeforeClose = HashSharedRead(fixture.PortablePath);
            Step("before normal Close");
            await session.CloseAsync(owner, process, waitWholeJob: true);
            Step("after full owned process exit");
            AssertSavedState(fixture, hashBeforeClose);

            // 標準配置は実行ファイル基準で、appの外のCWDへ何も書かない。
            Assert.IsFalse(Directory.EnumerateFileSystemEntries(fixture.WorkingDirectory).Any(), "CWDへ書込みがあります。");
        }
        catch (Exception exception)
        {
            // 失敗を検出した時点で回収期限を確定し、以後のUIA破棄と回収を同じ期限内で行います。
            session.BeginFailureCleanup();
            primary = exception;
        }
        try { observer?.Dispose(); }
        catch (Exception cleanup) { primary ??= cleanup; }
        try { automation?.Dispose(); }
        catch (Exception cleanup) { primary ??= cleanup; }
        try { attached?.Dispose(); }
        catch (Exception cleanup) { primary ??= cleanup; }
        progress?.Dispose();
        await session.CompleteAsync(primary);
    }

    /// <summary>session側のwindow観測をFlaUIで行います。provider未準備でpropertyを読めないwindowはnullとし、次の観測で判定させます。</summary>
    private static PublishedApplicationSession.WindowFacts? Describe(UIA3Automation automation, nint handle)
    {
        try
        {
            AutomationElement element = automation.FromHandle(handle);
            bool modal = element.Patterns.Window.PatternOrDefault?.IsModal.ValueOrDefault ?? false;
            return new PublishedApplicationSession.WindowFacts(element.AutomationId, element.IsOffscreen, modal);
        }
        catch (FlaUI.Core.Exceptions.PropertyNotSupportedException) { return null; }
    }

    private static string ReadThemeName(string app)
    {
        using var dictionary = JsonDocument.Parse(File.ReadAllText(Path.Combine(app, "lang", NormalStartupFixture.Language + ".json")));
        return dictionary.RootElement.GetProperty(NormalStartupFixture.ThemeNameKey).GetString()
            ?? throw new InvalidDataException("設定言語のテーマ表示名がありません。");
    }

    /// <summary>一覧のListItemが期待譜面とちょうど同数で、全件が可視かつidentity(path)とtitleが一致するまで待ちます。スクロールはしません。</summary>
    private static void WaitForRows(AutomationElement table, IReadOnlyList<DesktopChartInput> expected, string message, ProcessDeadline deadline)
    {
        string[] expectedKeys = [.. expected.Select(chart => Key(chart.Path, chart.Title)).Order(StringComparer.Ordinal)];
        string[] actual = [];
        Exception? lastUnavailable = null;
        bool matched = Retry.WhileFalse(() =>
        {
            try
            {
                AutomationElement[] rows = table.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem));
                actual = [.. rows.Select(row => row.IsOffscreen ? "<offscreen>" : Key(DecodePath(row.AutomationId), row.Name)).Order(StringComparer.Ordinal)];
                return actual.SequenceEqual(expectedKeys, StringComparer.Ordinal);
            }
            // 一覧の切替え中は行が消える瞬間がある。期限内の再観測で待ち、他の例外は失敗にする。
            catch (Exception exception) when (IsElementNotAvailable(exception)) { lastUnavailable = exception; return false; }
        }, timeout: deadline.Remaining, throwOnTimeout: false, ignoreException: false).Success;
        Assert.IsTrue(matched, message + " expected=[" + string.Join(" | ", expectedKeys) + "] actual=[" + string.Join(" | ", actual) + "]"
            + (lastUnavailable is null ? string.Empty : " lastUnavailable=" + lastUnavailable));
    }

    /// <summary>UIA_E_ELEMENTNOTAVAILABLE(0x80040201)または同等のFlaUI例外かどうかを返します。</summary>
    private static bool IsElementNotAvailable(Exception exception)
        => exception is FlaUI.Core.Exceptions.ElementNotAvailableException
            || exception is System.Runtime.InteropServices.COMException { HResult: unchecked((int)0x80040201) };

    private static string Key(string path, string title) => Path.GetFullPath(path).ToUpperInvariant() + "|" + title;

    private static string DecodePath(string automationId)
    {
        string[] parts = automationId.Split(':');
        Assert.AreEqual(4, parts.Length, automationId);
        Assert.AreEqual("chart", parts[0]);
        Assert.AreEqual("Bms", parts[2]);
        return Encoding.UTF8.GetString(Convert.FromBase64String(parts[3]));
    }

    private static string HashSharedRead(string path)
    {
        // 製品の原子的な保存を妨げず、開いた一世代のbytesだけを読む。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static void AssertSavedState(NormalStartupFixture fixture, string hashBeforeClose)
    {
        // 終了後のuser.configはXMLとして読め、固有値・BMS登録・未知キーが保持されている。
        var saved = XDocument.Load(fixture.PortablePath).Descendants("setting")
            .ToDictionary(element => (string?)element.Attribute("name") ?? throw new InvalidDataException(),
                element => element.Element("value")?.Value ?? string.Empty);
        foreach (string key in new[] { "Lang", "AppearanceTheme", "OperationModeLR2DB", NormalStartupFixture.UnknownKey })
        {
            Assert.IsTrue(saved.ContainsKey(key), "保存後の設定に " + key + " がありません。");
            Assert.AreEqual(fixture.Settings[key], saved[key], key);
        }
        foreach (string key in new[] { "BMSRootPath", "StandaloneBmsRootPaths", "BMSInstallDir" })
        {
            Assert.IsTrue(saved.ContainsKey(key), "保存後の設定に " + key + " がありません。");
            Assert.IsTrue(string.Equals(Path.GetFullPath(fixture.Settings[key]), Path.GetFullPath(saved[key]), StringComparison.OrdinalIgnoreCase),
                key + " expected=" + fixture.Settings[key] + " actual=" + saved[key]);
        }
        // 終了時の保存が実際に起きた: Closeを送る直前から、設定ファイルのbytesが変わっている。
        Assert.AreNotEqual(hashBeforeClose, HashSharedRead(fixture.PortablePath), "通常終了で設定が保存されていません。");

        // AssemblyVersionは四成分を型付きXMLから読み、入力と一致する。不一致はupgrade経路に入った、
        // または代理値(FileVersion)が製品のAssemblyVersionとずれたことを意味する。
        XElement version = XDocument.Load(fixture.PortablePath).Descendants("setting")
            .Single(element => (string?)element.Attribute("name") == "AssemblyVersion")
            .Element("value")?.Element("SerializableVersion") ?? throw new InvalidDataException("AssemblyVersionがSerializableVersion形式で保存されていません。");
        int Part(string name) => int.Parse(version.Element(name)?.Value ?? throw new InvalidDataException(name), System.Globalization.CultureInfo.InvariantCulture);
        var savedVersion = new Version(Part("Major"), Part("Minor"), Part("Build"), Part("Revision"));
        Assert.AreEqual(Version.Parse(fixture.Settings["AssemblyVersion"]), savedVersion,
            "AssemblyVersionが入力と異なります。upgrade経路に入った、または代理値のFileVersionが製品のAssemblyVersionとずれています。");

        // 標準配置のDBに、入力bytesのMD5とtitleを持つ全譜面が各1行ある。
        using var database = new SQLiteConnection(fixture.DatabasePath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
        List<SongRow> songs = database.Query<SongRow>("SELECT path AS Path, hash AS Hash, title AS Title FROM song");
        foreach (DesktopChartInput chart in fixture.Charts)
        {
            Assert.AreEqual(1, songs.Count(song => string.Equals(Path.GetFullPath(song.Path), Path.GetFullPath(chart.Path), StringComparison.OrdinalIgnoreCase)
                && string.Equals(song.Hash, chart.Md5, StringComparison.OrdinalIgnoreCase) && song.Title == chart.Title), chart.Path);
        }
    }

    private sealed class SongRow
    {
        public SongRow() { }
        public string Path { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
    }
}
