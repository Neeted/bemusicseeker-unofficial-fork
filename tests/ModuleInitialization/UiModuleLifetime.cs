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

    /// <summary>共有画面資源を作る前に専有配置を確定し、Windows以外はsession排他を取得します。</summary>
    [AssemblyInitialize]
    public static async Task Initialize(TestContext context)
    {
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
