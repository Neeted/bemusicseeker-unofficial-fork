using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Components.Update;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Verification;

namespace BeMusicSeeker.Tests;

/// <summary>旧PowerShellの実配布受入二件を通常起動と実updaterの境界で保持します。Dailyには含めません。</summary>
[TestClass]
[TestCategory("ReleaseAcceptance")]
public sealed class PublishedExistingDataAcceptanceTests
{
    /// <summary>Standalone/LR2各二回の通常起動・実HWND正常終了後に旧設定/DBの保持、LR2同期、二回目設定hash安定と元入力不変を確認します。</summary>
    [TestMethod(DisplayName = "[SC-ExistingData] 既存StandaloneとLR2を通常起動し二回目の保存と元入力を保持する")]
    [TestCategory("SC-ExistingData")]
    public async Task PublishedCurrent_ImportsExistingProfilesAndPreservesSecondStartup()
    {
        var session = new PublishedApplicationSession(180);
        Exception? primary = null;
        try
        {
            string repository = UpdateDeploymentTestSupport.FindRepositoryRoot();
            DistributionCatalog catalog = await DistributionCatalog.ReadFromEnvironmentAsync(repository, session.Deadline);
            Console.WriteLine($"受入入力: Current={catalog.Current.Version}, SHA256={catalog.Current.Sha256}, ZIP={catalog.Current.PackagePath}; ソースbuildの実行体とは別のcatalog配布を使用します。");
            foreach (bool lr2 in new[] { false, true })
            {
                string name = lr2 ? "lr2" : "standalone";
                string app = await catalog.Current.ExtractInstallationAsync(session.Workspace, name + "/app", session.Deadline);
                var fixture = new ExistingDataAcceptanceFixture(repository, session.Workspace.GetPath(name), app, lr2, update: false);
                await session.AssertIsolationAsync(fixture);
                string? savedHash = null;
                for (int startup = 0; startup < 2; startup++)
                {
                    (OwnedProcess owner, Process process) = await session.StartApplicationAsync(fixture, maintenance: true);
                    await session.CloseAsync(owner, process, waitWholeJob: true);
                    fixture.AssertSavedState();
                    string currentHash = ExistingDataAcceptanceFixture.Hash(fixture.PortablePath);
                    if (savedHash is not null) { Assert.AreEqual(savedHash, currentHash, "二回目の起動で設定bytesが変わりました。"); }
                    savedHash = currentHash;
                }
            }
        }
        catch (Exception exception) { primary = exception; }
        await session.CompleteAsync(primary);
    }

    /// <summary>Current→Currentの実handshake、旧App正常終了、実updater適用、引き継いだ引数で更新確認する自動後継の通常閉鎖を通し、旧意味/設定/元入力/未管理file保持、obsolete除去と更新後の正本配布配置を確認します。</summary>
    [TestMethod(DisplayName = "[SC-CurrentUpdate] 現行updaterが実アプリを自動再起動し既存データを保持する")]
    [TestCategory("SC-CurrentUpdate")]
    public async Task PublishedCurrentUpdater_RestartsRealApplicationAndPreservesExistingData()
    {
        var session = new PublishedApplicationSession(240);
        Exception? primary = null;
        try
        {
            string repository = UpdateDeploymentTestSupport.FindRepositoryRoot();
            DistributionCatalog catalog = await DistributionCatalog.ReadFromEnvironmentAsync(repository, session.Deadline);
            Console.WriteLine($"受入入力: Current={catalog.Current.Version}, SHA256={catalog.Current.Sha256}, ZIP={catalog.Current.PackagePath}; ソースbuildの実行体とは別のcatalog配布を使用します。");
            string app = await catalog.Current.ExtractInstallationAsync(session.Workspace, "success/app", session.Deadline);
            var fixture = new ExistingDataAcceptanceFixture(repository, session.Workspace.GetPath("success"), app, lr2: false, update: true);
            await session.AssertIsolationAsync(fixture);
            string executable = Path.Combine(app, "BeMusicSeeker.exe");
            string expectedExecutableHash = ExistingDataAcceptanceFixture.Hash(executable);
            string obsolete = Path.Combine(app, "verification-obsolete.txt");
            File.WriteAllText(obsolete, "obsolete managed file", new UTF8Encoding(false));
            File.AppendAllText(Path.Combine(app, "update-managed-files.txt"), "\nverification-obsolete.txt\n", new UTF8Encoding(false));
            string marker = Path.Combine(app, @"user-data\e2-unmanaged.marker");
            Directory.CreateDirectory(Path.GetDirectoryName(marker) ?? throw new InvalidDataException());
            File.WriteAllText(marker, "E2 unmanaged marker standalone\n", new UTF8Encoding(false));
            string markerHash = ExistingDataAcceptanceFixture.Hash(marker);
            (OwnedProcess originalOwner, Process original) = await session.StartApplicationAsync(fixture, maintenance: false);
            Dictionary<string, string> settingsBefore = fixture.ReadSettings();
            string current = Path.Combine(app, @"update_work\current");
            string downloads = Path.Combine(app, @"update_work\downloads");
            Directory.CreateDirectory(current);
            Directory.CreateDirectory(downloads);
            string package = Path.Combine(downloads, Path.GetFileName(catalog.Current.PackagePath));
            File.Copy(catalog.Current.PackagePath, package);
            Assert.AreEqual(catalog.Current.Sha256.ToUpperInvariant(), ExistingDataAcceptanceFixture.Hash(package));
            string updaterPath = Path.Combine(current, "BeMusicSeeker.Updater.exe");
            File.Copy(Path.Combine(app, "BeMusicSeeker.Updater.exe"), updaterPath);
            string ready = Path.Combine(current, "updater-ready.txt");
            string decision = Path.Combine(current, "updater-decision.txt");
            DateTime earliestCreation = DateTime.UtcNow;
            // 後継は引き継いだ引数で、元アプリのものとは別のloopbackへ更新確認を送る。
            SingleRequestHttpServer successorManifest = session.StartNoUpdateServer();
            string successorArguments = JsonSerializer.Serialize(new[] { "--update-manifest-url=" + successorManifest.Address.AbsoluteUri });
            OwnedProcess updater = session.Start(updaterPath,
                ["--app-dir", app, "--package", package, "--backup-dir", Path.Combine(app, "update_backup"),
                 "--ready-file", ready, "--decision-file", decision, "--pid", original.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                 "--restart-exe", executable, "--restart-arguments-json", successorArguments], current, fixture.ChildEnvironment);
            await session.WaitHandshakeAsync(updater, ready);
            await session.CloseAsync(originalOwner, original, waitWholeJob: true);
            fixture.AssertSavedState();
            PublishedApplicationSession.ArchiveLogs(app);
            UpdaterPackageTestSupport.PublishUpdaterDecision(decision, "proceed");
            Assert.AreEqual(0, await updater.WaitForRootExitAsync(session.Deadline));
            // 自動後継は引き継いだ--update-manifest-urlのloopbackで更新なしの応答を受けます。公開サービスには依存せず、通知を自動応答で隠しません。
            Process successor = await session.FindSuccessorAsync(updater, executable, original.Id, earliestCreation);
            await session.WaitReadyAsync(successor, app, maintenance: false);
            await session.WaitRequestAsync(successorManifest, successor);
            await session.CloseAsync(updater, successor, waitWholeJob: false);
            Assert.AreEqual(0, (await updater.WaitForExitAsync(session.Deadline)).ExitCode);
            fixture.AssertSavedState();
            Dictionary<string, string> settingsAfter = fixture.ReadSettings();
            foreach (string key in new[] { "OperationModeLR2DB", "Lang", "AppearanceTheme", "BMSRootPath", "BMSInstallDir", "LR2RootPath", "LR2SongDBPath", "LR2ConfigXmlPath" })
            {
                Assert.AreEqual(settingsBefore.GetValueOrDefault(key, string.Empty), settingsAfter.GetValueOrDefault(key, string.Empty), key);
            }
            Assert.AreEqual(markerHash, ExistingDataAcceptanceFixture.Hash(marker));
            Assert.AreEqual(expectedExecutableHash, ExistingDataAcceptanceFixture.Hash(executable));
            Assert.IsFalse(File.Exists(obsolete));
            // 更新前ZIPの検査では代替できない更新後配置を、旧受入と同じ正本で検査します。
            // runtime dataは許可し、全禁止配置・必須file・root DLL・言語辞書の集合を維持します。
            string layoutScript = Path.Combine(repository, "scripts", "portable-package-layout.ps1");
            string layoutCommand = "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); $ErrorActionPreference='Stop'; . '"
                + layoutScript.Replace("'", "''") + "'; Assert-PortableSingleFilePayloadLayout '" + app.Replace("'", "''") + "'";
            // CreateProcessWのapplication指定はPATH探索しないため、既存layout検証と同じ解決方法を使います。
            string powershell = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.GetFullPath(Path.Combine(directory, "pwsh.exe"))).FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("公開layout検証に必要なpwsh.exeがPATHにありません。");
            OwnedProcess layout = session.Start(powershell, ["-NoProfile", "-NonInteractive", "-Command", layoutCommand], repository, fixture.ChildEnvironment);
            ProcessCompletion layoutResult = await layout.WaitForExitAsync(session.Deadline);
            Assert.AreEqual(0, layoutResult.ExitCode, "更新後の配布layoutが不正です: " + layoutResult.StandardOutput + layoutResult.StandardError);
            foreach (string managed in catalog.Current.ManagedFiles)
            {
                Assert.IsTrue(File.Exists(Path.Combine(app, managed)), "配布layoutが欠けています: " + managed);
            }
        }
        catch (Exception exception) { primary = exception; }
        await session.CompleteAsync(primary);
    }
}
