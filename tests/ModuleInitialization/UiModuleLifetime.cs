using System.Globalization;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Ui;
using BeMusicSeeker.TestSupport.Wpf;

namespace BeMusicSeeker.Tests.Infrastructure;

/// <summary>直接実行を含め専有profileを準備し、Windows以外はmodule全体のsession排他を所有します。</summary>
[TestClass]
public sealed class UiModuleLifetime
{
    private static TestUiSessionLease? lease;
    private static string? resultsDirectory;

    /// <summary>共有画面資源を作る前に専有配置を確定し、Windows以外はsession排他を取得します。</summary>
    [AssemblyInitialize]
    public static async Task Initialize(TestContext context)
    {
        resultsDirectory = context.TestRunResultsDirectory;
        // 観測の記録を出力する境界を持つmoduleだけで、共有ホストの観測を有効にします（Coreは有効にしません）。
        HostResidueBoundary.Enable();
        string runRoot = TestRunDirectory.RootDirectory;
        bool windows = typeof(UiModuleLifetime).Assembly.GetName().Name == "BeMusicSeeker.Windows.Tests";
        TestUiSessionLease? acquiredLease = windows ? null : await TestUiSessionLease.AcquireAsync(ProcessDeadline.FromSuiteEnvironment(TimeSpan.FromMinutes(3)));
        lease = acquiredLease;
        try
        {
            string assets = AppContext.BaseDirectory;
            string executable = Path.Combine(assets, "BeMusicSeeker.exe");
            string language = Path.Combine(assets, "lang");
            if (!File.Exists(executable) || !File.Exists(Path.Combine(language, "ja-JP.json")))
            {
                throw new FileNotFoundException("同じbuildの実行体と日本語辞書が必要です。", executable);
            }
            string profile = Path.Combine(runRoot, typeof(UiModuleLifetime).Assembly.GetName().Name + "-profile");
            if (windows)
            {
                string privateLanguage = Path.Combine(profile, "lang");
                Directory.CreateDirectory(privateLanguage);
                foreach (string dictionary in Directory.EnumerateFiles(language, "*.json"))
                {
                    File.Copy(dictionary, Path.Combine(privateLanguage, Path.GetFileName(dictionary)));
                }
                language = privateLanguage;
            }
            ApplicationPathPolicy.Configure(ApplicationPathSnapshot.FromLayout(executable, assets, profile,
                language, Path.Combine(profile, "temp"), Path.Combine(profile, "legacy")));
            Resources.Culture = CultureInfo.GetCultureInfo("ja-JP");
        }
        catch (Exception primary)
        {
            try { acquiredLease?.Dispose(); }
            catch (Exception cleanup) { primary.Data["UiModuleLifetime.LeaseCleanupFailure"] = cleanup; }
            finally { lease = null; }
            throw;
        }
    }

    /// <summary>
    /// ケースの開始を共有ホストの未処理例外の記録へ伝え、ケースの間に残った記録を直前ケースの残留として失敗にします。
    /// </summary>
    /// <remarks>
    /// ホストが起動していない間は記録が空で、ホスト・<c>Settings.Default</c>・Applicationを起動も読取りもしません。
    /// 先行ケースがホストを起動した後は、ホストを使わないケースでも残留の報告と期間中の記録の帰属を受けます。
    /// 失敗にしたケースは本体も終了処理も実行されないため、記録側でもそのケースを開始前の扱いに戻します。
    /// </remarks>
    [GlobalTestInitialize]
    public static void BeginCaseBoundary(TestContext context)
    {
        // ケースの間に記録された未準備のWindowの表示は、判定を変えずに、このケースの出力へ「ケース間」として書きます。
        HostObservationLog.WriteBetweenCases(context.WriteLine);
        if (HostUnhandledExceptionLog.BeginCase(GetCaseName(context)) is { } residue) { throw residue; }
    }

    /// <summary>
    /// ケースの期間に共有ホストで未処理になり、ケース内で受け取られなかった例外を、そのケースの失敗として報告します。
    /// </summary>
    /// <remarks>
    /// 本体が既に失敗している場合は、境界の記録をTestContextの出力へ書いて消費し、例外を投げず本体の失敗を主として残します
    /// （この場合、置換は起きません）。
    /// 限界：クラスの[TestCleanup]の失敗はGlobalTestCleanupから観測できず、本体が成功していて境界の記録があるとき、TRX上は境界の失敗に置き換わります。
    /// 優先順位（本体、表示の観測、後片付け、境界の記録）のうち後片付けとの順位は、この限界の範囲で満たされません。
    /// 境界の記録を直せば、後片付けの失敗は次の実行で表面化します。
    /// 判定を変えない観測（未準備のWindowの表示、ケース終了時の残留）は、この処理より先にTestContextへ出力し、失敗にしません。
    /// </remarks>
    [GlobalTestCleanup]
    public static void EndCaseBoundary(TestContext context)
    {
        string caseName = GetCaseName(context);
        // 判定を変えない観測を先に出力します。観測の失敗は記録して例外にせず、未処理例外の境界処理を妨げません。
        HostResidueBoundary.ObserveAtCaseEnd();
        HostObservationLog.WriteCase(caseName, context.WriteLine);
        if (HostUnhandledExceptionLog.EndCase(caseName) is not { } failure) { return; }
        if (context.CurrentTestOutcome is UnitTestOutcome.Passed or UnitTestOutcome.InProgress) { throw failure; }
        context.WriteLine(failure.ToString());
    }

    private static string GetCaseName(TestContext context)
        => context.FullyQualifiedTestClassName + "." + context.TestDisplayName;

    /// <summary>共有hostの終了確認後だけ設定を回収し、失敗時も一時sessionとsession排他の回収を継続して失敗を集約します。</summary>
    [AssemblyCleanup]
    public static async Task Cleanup()
    {
        List<Exception> failures = [];
        try
        {
            try
            {
                TestUiDispatcherHost.ShutdownApplication();
                TestSettingsFactory.CleanupIsolatedSettingsDirectories();
            }
            catch (Exception failure) { failures.Add(failure); }
            // 共有hostの終了中に追加された記録も含め、ケースの間に残った未報告の例外をmoduleの失敗にします。
            if (HostUnhandledExceptionLog.TakeResidueAtModuleEnd() is { } residue) { failures.Add(residue); }
            // ケースの間に記録された観測の残りは、失敗にせずConsoleと結果フォルダーへ出力します。
            HostObservationLog.WriteModuleEnd(resultsDirectory, typeof(UiModuleLifetime).Assembly.GetName().Name ?? "module");
            try
            {
                await TempDirectoryPublisher.RemoveAllIfInitializedAsync((path, failure) =>
                    throw new IOException("管理一時sessionの回収に失敗しました: " + path, failure));
            }
            catch (Exception failure) { failures.Add(failure); }
        }
        finally
        {
            try { lease?.Dispose(); }
            catch (Exception failure) { failures.Add(failure); }
            finally { lease = null; }
        }
        if (failures.Count != 0) { throw new AggregateException("UI moduleの回収に失敗しました。", failures); }
    }
}
