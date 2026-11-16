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
    /// ケースの開始を共有ホストの未処理例外の記録へ伝え、ケースの間に残った記録と未準備のWindowの表示を、直前ケースの残留として失敗にします。
    /// </summary>
    /// <remarks>
    /// ホストが起動していない間は、ホスト由来の記録（未処理例外・B・E）が空で、ホスト・<c>Settings.Default</c>・Applicationを起動も読取りもしません。
    /// 未宣言のダイアログ窓口の記録（D）はホストの起動と関係なく呼んだスレッドで記録され、境界はホストを起動せずに出力・判定します。
    /// 先行ケースがホストを起動した後は、ホストを使わないケースでも、ホスト由来の残留の報告と期間中の記録の帰属を受けます。
    /// ケースの間に未準備のWindowの表示（B）があるときだけ、Send優先度のInvokeを一回使って終了時と同じ観測と復元を行います（常には行いません）。
    /// 失敗にしたケースは本体も終了処理も実行されない（MSTest 4.5.2で確認）ため、記録側でもそのケースを開始前の扱いに戻します。
    /// 開始時に失敗したケースの出力（TestContextへ書いた記録の行）はTRXのStdOutに残ります。記録の行（<c>[BMS-OBS]</c>）は失敗のメッセージにも含めます。
    /// </remarks>
    [GlobalTestInitialize]
    public static void BeginCaseBoundary(TestContext context)
    {
        string caseName = GetCaseName(context);
        List<HostRestoreFailure> restoreFailures = [];
        // 観測と復元は、ケースの間の状態（直前のケースへの帰属）で行うため、BeginCaseより前に行います。
        HostResidueBoundary.ObserveAtCaseStart(restoreFailures);
        IReadOnlyList<HostObservation> records = HostObservationLog.WriteBetweenCases(context.WriteLine);
        Exception? unhandled = HostUnhandledExceptionLog.BeginCase(caseName);
        if (HostObservationVerdict.Evaluate(HostBoundaryKind.CaseStart, caseName, records, restoreFailures, unhandled) is not { } failure) { return; }
        // BeginCaseが成功していても、このケースは開始しなかった扱いにします（終了処理が呼ばれないため）。
        HostUnhandledExceptionLog.AbandonCase(caseName);
        throw failure;
    }

    /// <summary>
    /// ケースの期間に共有ホストで未処理になり、ケース内で受け取られなかった例外と、失敗にする項目（B、E-a〜E-f、D）の記録と復元の失敗を、そのケースの失敗として報告します。
    /// </summary>
    /// <remarks>
    /// 失敗にする残留は、観測したケースで基準へ戻します（復元は本体の成否にかかわらず行います）。同じ境界の未処理例外とは一つの失敗にまとめ、
    /// 元の例外を内部例外として保持します。観測や復元が失敗しても、記録側のケースの終了処理（<c>EndCase</c>）は必ず通します。
    /// 本体が既に失敗している場合は、境界の失敗をTestContextの出力へ書いて消費し、例外を投げず本体の失敗を主として残します
    /// （この場合、置換は起きません）。
    /// 限界：クラスの[TestCleanup]の失敗はGlobalTestCleanupから観測できず、本体が成功していて境界の失敗があるとき、TRX上は境界の失敗に置き換わります。
    /// 優先順位（本体、表示の観測、後片付け、境界の記録）のうち後片付けとの順位は、この限界の範囲で満たされません。
    /// 境界の失敗を直せば、後片付けの失敗は次の実行で表面化します。
    /// 記録だけの項目（E-g、E-a-hwnd、X-detector）は、この処理より先にTestContextへ出力し、失敗にしません。
    /// </remarks>
    [GlobalTestCleanup]
    public static void EndCaseBoundary(TestContext context)
    {
        string caseName = GetCaseName(context);
        List<HostRestoreFailure> restoreFailures = [];
        IReadOnlyList<HostObservation> records = [];
        Exception? unhandled;
        try
        {
            // 観測と復元の失敗は記録して例外にせず、未処理例外の境界処理（finally）を妨げません。復元は記録の出力より先に行います。
            HostResidueBoundary.ObserveAtCaseEnd(restoreFailures);
            records = HostObservationLog.WriteCase(caseName, context.WriteLine);
        }
        finally { unhandled = HostUnhandledExceptionLog.EndCase(caseName); }
        if (HostObservationVerdict.Evaluate(HostBoundaryKind.CaseEnd, caseName, records, restoreFailures, unhandled) is not { } failure) { return; }
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
            // 共有hostの終了中に追加された記録も含め、ケースの間に残った未報告の例外と、ケースの間に記録された観測の残り（主に未準備のWindowの表示）を、
            // moduleの失敗一件にします。終端済みのhostは観測も復元もしないため、記録だけを判定します。出力はConsoleと結果フォルダーへ従来どおり行います。
            Exception? residue = HostUnhandledExceptionLog.TakeResidueAtModuleEnd();
            IReadOnlyList<HostObservation> remaining = HostObservationLog.WriteModuleEnd(resultsDirectory, typeof(UiModuleLifetime).Assembly.GetName().Name ?? "module");
            if (HostObservationVerdict.Evaluate(HostBoundaryKind.ModuleEnd, null, remaining, [], residue) is { } moduleFailure) { failures.Add(moduleFailure); }
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
