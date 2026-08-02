using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[TestCategory("ProcessIntegration")]
public sealed class UpdaterPackageSyncTests
{
    [TestMethod]
    public void UpdaterWaitsForProceedDecisionBeforeApplyingOrWaitingForApplicationExit()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "update_work/downloads/package.zip", "not-yet-applied");

            using Process process = StartUpdater(
                appDirectoryPath,
                packagePath,
                backupDirectoryPath,
                publishProceed: false);
            Assert.IsFalse(process.WaitForExit(250));
            Assert.AreEqual(1, GetRecoveryRunOnceValues(appDirectoryPath).Count, "The transaction must arm an independent recovery handoff.");
            Assert.AreEqual(1, GetRecoverySupervisorValues(appDirectoryPath).Count, "The transaction must arm a persistent recovery supervisor.");

            string decisionFilePath = Path.Combine(appDirectoryPath, "update_work", "current", "updater-decision.txt");
            PublishUpdaterDecision(decisionFilePath, "cancel");
            Assert.IsTrue(process.WaitForExit(5000));
            Assert.AreEqual(0, process.ExitCode);
            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.AreEqual(0, GetRecoveryRunOnceValues(appDirectoryPath).Count, "Cancellation must clear the recovery handoff.");
            Assert.AreEqual(0, GetRecoverySupervisorValues(appDirectoryPath).Count, "Cancellation must clear the persistent recovery supervisor.");
        });
    }

    [TestMethod]
    public void ApplyUpdateRecoversIncompleteDurableJournalBeforeNewMutation()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            string previousDirectoryPath = Path.Combine(backupDirectoryPath, "previous");
            string extractDirectoryPath = Path.Combine(appDirectoryPath, "update_work", "extracted");
            string journalPath = Path.Combine(appDirectoryPath, "update_work", "update-transaction.json");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "partial-new");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "update-managed-files.txt", "BeMusicSeeker.exe\nrestart.exe");
            WriteTextFile(appDirectoryPath, "update_backup/previous/BeMusicSeeker.exe", "old-app");
            File.Copy(
                FindUpdaterExecutable(),
                Path.Combine(previousDirectoryPath, "restart.exe"),
                overwrite: true);
            WriteTextFile(previousDirectoryPath, "update-managed-files.txt", "BeMusicSeeker.exe\nrestart.exe");
            WriteTextFile(
                appDirectoryPath,
                "update_work/update-transaction.json",
                JsonSerializer.Serialize(new
                {
                    Version = 1,
                    Phase = "applying",
                    AppDirectory = appDirectoryPath,
                    PackagePath = packagePath,
                    BackupDirectory = backupDirectoryPath,
                    PreviousDirectory = previousDirectoryPath,
                    ExtractDirectory = extractDirectoryPath,
                    RestartExecutablePath = Path.Combine(appDirectoryPath, "restart.exe"),
                    BackupComplete = true,
                    NewPackagePaths = new[] { "BeMusicSeeker.exe", "restart.exe", "update-managed-files.txt" }
                }));

            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", "BeMusicSeeker.exe\nrestart.exe");
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdater(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("new-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.IsFalse(File.Exists(journalPath), "A committed transaction must remove its durable journal.");
            Assert.IsFalse(Directory.Exists(backupDirectoryPath), "A committed transaction must remove its previous-generation backup.");
        });
    }

    [TestMethod]
    public void RecoverCommandRestoresPartialBackupWithoutDeletingUnmovedApplicationFiles()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            string previousDirectoryPath = Path.Combine(backupDirectoryPath, "previous");
            string extractDirectoryPath = Path.Combine(appDirectoryPath, "update_work", "extracted");
            string journalPath = Path.Combine(appDirectoryPath, "update_work", "update-transaction.json");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            WriteTextFile(appDirectoryPath, "docs/unmoved.txt", "still-old");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(previousDirectoryPath, "BeMusicSeeker.exe", "partial-copy");
            WriteTextFile(
                appDirectoryPath,
                "update_work/update-transaction.json",
                JsonSerializer.Serialize(new
                {
                    Version = 1,
                    Phase = "backing-up",
                    AppDirectory = appDirectoryPath,
                    PackagePath = packagePath,
                    BackupDirectory = backupDirectoryPath,
                    PreviousDirectory = previousDirectoryPath,
                    ExtractDirectory = extractDirectoryPath,
                    RestartExecutablePath = Path.Combine(appDirectoryPath, "restart.exe"),
                    NewPackagePaths = new[] { "BeMusicSeeker.exe", "docs/unmoved.txt" }
                }));

            RunUpdaterRecovery(appDirectoryPath);

            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.AreEqual("still-old", File.ReadAllText(Path.Combine(appDirectoryPath, "docs", "unmoved.txt")));
            Assert.IsFalse(File.Exists(journalPath));
            Assert.IsFalse(Directory.Exists(backupDirectoryPath));
        });
    }

    [TestMethod]
    public void WatchdogRecoversApplyingTransactionAndRestartsPreviousApplication()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            string previousDirectoryPath = Path.Combine(backupDirectoryPath, "previous");
            string extractDirectoryPath = Path.Combine(appDirectoryPath, "update_work", "extracted");
            string journalPath = Path.Combine(appDirectoryPath, "update_work", "update-transaction.json");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "partial-new");
            WriteTextFile(appDirectoryPath, "restart.cmd", "@echo restarted>restart-marker.txt");
            WriteTextFile(appDirectoryPath, "update_work/downloads/package.zip", "package");
            WriteTextFile(previousDirectoryPath, "BeMusicSeeker.exe", "old-app");
            WriteTextFile(
                appDirectoryPath,
                "update_work/update-transaction.json",
                JsonSerializer.Serialize(new
                {
                    Version = 1,
                    Phase = "applying",
                    AppDirectory = appDirectoryPath,
                    PackagePath = packagePath,
                    ApplicationProcessId = 0,
                    BackupDirectory = backupDirectoryPath,
                    PreviousDirectory = previousDirectoryPath,
                    ExtractDirectory = extractDirectoryPath,
                    RestartExecutablePath = Path.Combine(appDirectoryPath, "restart.cmd"),
                    BackupComplete = true,
                    NewPackagePaths = new[] { "BeMusicSeeker.exe" }
                }));

            RunUpdaterWatchdogRecovery(appDirectoryPath);

            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            WaitForFile(Path.Combine(appDirectoryPath, "restart-marker.txt"));
            Assert.IsFalse(File.Exists(journalPath));
            Assert.IsFalse(Directory.Exists(backupDirectoryPath));
        });
    }

    [TestMethod]
    public void RecoverCommandDoesNotTrustReusedApplicationPidAfterRestart()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            string previousDirectoryPath = Path.Combine(backupDirectoryPath, "previous");
            string extractDirectoryPath = Path.Combine(appDirectoryPath, "update_work", "extracted");
            string journalPath = Path.Combine(appDirectoryPath, "update_work", "update-transaction.json");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "partial-new");
            WriteTextFile(appDirectoryPath, "restart.cmd", "@echo restarted>restart-marker.txt");
            WriteTextFile(previousDirectoryPath, "BeMusicSeeker.exe", "old-app");
            WriteTextFile(
                appDirectoryPath,
                "update_work/update-transaction.json",
                JsonSerializer.Serialize(new
                {
                    Version = 1,
                    Phase = "applying",
                    AppDirectory = appDirectoryPath,
                    PackagePath = packagePath,
                    ApplicationProcessId = Environment.ProcessId,
                    BackupDirectory = backupDirectoryPath,
                    PreviousDirectory = previousDirectoryPath,
                    ExtractDirectory = extractDirectoryPath,
                    RestartExecutablePath = Path.Combine(appDirectoryPath, "restart.cmd"),
                    BackupComplete = true,
                    NewPackagePaths = new[] { "BeMusicSeeker.exe" }
                }));

            RunUpdaterRecovery(appDirectoryPath);

            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            WaitForFile(Path.Combine(appDirectoryPath, "restart-marker.txt"));
            Assert.IsFalse(File.Exists(journalPath));
        });
    }

    [TestMethod]
    public void RecoverCommandDefersWhenTheApplicationExecutableIsStillRunning()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            string previousDirectoryPath = Path.Combine(backupDirectoryPath, "previous");
            string extractDirectoryPath = Path.Combine(appDirectoryPath, "update_work", "extracted");
            string journalPath = Path.Combine(appDirectoryPath, "update_work", "update-transaction.json");
            string restartExecutablePath = Path.Combine(appDirectoryPath, "restart.exe");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "partial-new");
            File.Copy(
                Environment.GetEnvironmentVariable("ComSpec") ?? throw new InvalidOperationException("ComSpec was not available."),
                restartExecutablePath,
                overwrite: true);
            WriteTextFile(previousDirectoryPath, "BeMusicSeeker.exe", "old-app");
            WriteTextFile(
                appDirectoryPath,
                "update_work/update-transaction.json",
                JsonSerializer.Serialize(new
                {
                    Version = 1,
                    Phase = "applying",
                    AppDirectory = appDirectoryPath,
                    PackagePath = packagePath,
                    ApplicationProcessId = Environment.ProcessId,
                    BackupDirectory = backupDirectoryPath,
                    PreviousDirectory = previousDirectoryPath,
                    ExtractDirectory = extractDirectoryPath,
                    RestartExecutablePath = restartExecutablePath,
                    BackupComplete = true,
                    NewPackagePaths = new[] { "BeMusicSeeker.exe" }
                }));

            using Process liveApplication = Process.Start(new ProcessStartInfo
            {
                FileName = restartExecutablePath,
                ArgumentList =
                {
                    "/c",
                    "ping 127.0.0.1 -n 30 >nul"
                },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = appDirectoryPath
            }) ?? throw new InvalidOperationException("The live application fixture was not started.");
            try
            {
                Thread.Sleep(250);

                using Process normalUpdater = StartUpdater(
                    appDirectoryPath,
                    packagePath,
                    backupDirectoryPath,
                    restartExecutablePath,
                    Environment.ProcessId.ToString());
                Assert.IsTrue(normalUpdater.WaitForExit(5000), "The normal updater must defer while recovery sees a live application executable.");
                Assert.AreEqual(2, normalUpdater.ExitCode);
                Assert.AreEqual("partial-new", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
                Assert.AreEqual(1, GetRecoveryRunOnceValues(appDirectoryPath).Count);
                Assert.AreEqual(1, GetRecoverySupervisorValues(appDirectoryPath).Count);

                using Process recovery = StartUpdaterRecoveryProcess(appDirectoryPath);
                Assert.IsTrue(recovery.WaitForExit(5000), "Recovery must defer while the application executable is alive.");
                Assert.AreEqual(2, recovery.ExitCode);
                Assert.AreEqual("partial-new", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
                Assert.IsTrue(File.Exists(journalPath));
                Assert.AreEqual(1, GetRecoveryRunOnceValues(appDirectoryPath).Count);
                Assert.AreEqual(1, GetRecoverySupervisorValues(appDirectoryPath).Count);
            }
            finally
            {
                if (!liveApplication.HasExited)
                {
                    liveApplication.Kill(entireProcessTree: true);
                }
                liveApplication.WaitForExit(5000);
            }
        });
    }

    [TestMethod]
    public void RolledBackJournalCleanupDoesNotReplayRestoreAfterBackupWasRemoved()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            string previousDirectoryPath = Path.Combine(backupDirectoryPath, "previous");
            string extractDirectoryPath = Path.Combine(appDirectoryPath, "update_work", "extracted");
            string journalPath = Path.Combine(appDirectoryPath, "update_work", "update-transaction.json");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "restored-old");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(
                appDirectoryPath,
                "update_work/update-transaction.json",
                JsonSerializer.Serialize(new
                {
                    Version = 1,
                    Phase = "rolled-back",
                    AppDirectory = appDirectoryPath,
                    PackagePath = packagePath,
                    BackupDirectory = backupDirectoryPath,
                    PreviousDirectory = previousDirectoryPath,
                    ExtractDirectory = extractDirectoryPath,
                    RestartExecutablePath = Path.Combine(appDirectoryPath, "restart.exe"),
                    NewPackagePaths = new[] { "BeMusicSeeker.exe" }
                }));

            RunUpdaterRecovery(appDirectoryPath);

            Assert.AreEqual("restored-old", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.IsFalse(File.Exists(journalPath));
        });
    }

    [TestMethod]
    public void UpdaterRejectsConcurrentTransactionLeaseWithoutMutation()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "update_work/downloads/package.zip", "not-used");

            using Process firstUpdater = StartUpdater(
                appDirectoryPath,
                packagePath,
                backupDirectoryPath,
                publishProceed: false);
            using Process secondUpdater = Process.Start(CreateUpdaterStartInfo(
                appDirectoryPath,
                packagePath,
                backupDirectoryPath))
                ?? throw new InvalidOperationException("The concurrent updater process was not started.");

            Assert.IsTrue(secondUpdater.WaitForExit(5000), "The second updater must reject the held transaction lease promptly.");
            Assert.AreNotEqual(0, secondUpdater.ExitCode);
            PublishUpdaterDecision(
                Path.Combine(appDirectoryPath, "update_work", "current", "updater-decision.txt"),
                "cancel");
            Assert.IsTrue(firstUpdater.WaitForExit(5000), "The first updater must release the lease after cancellation.");
            Assert.AreEqual(0, firstUpdater.ExitCode);
            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
        });
    }

    [TestMethod]
    public void RecoverCommandSerializesRunOnceConsumersWhenTransactionLeaseIsUnavailable()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packagePath = Path.Combine(appDirectoryPath, "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "update_work/downloads/package.zip", "not-used");

            using Process firstUpdater = StartUpdater(
                appDirectoryPath,
                packagePath,
                backupDirectoryPath,
                publishProceed: false);
            Assert.AreEqual(1, GetRecoveryRunOnceValues(appDirectoryPath).Count);
            DeleteRecoveryRunOnceValues(appDirectoryPath);

            using Process recovery = StartUpdaterRecoveryProcess(appDirectoryPath);
            Thread.Sleep(250);
            Assert.IsFalse(recovery.HasExited, "A competing recovery must wait for the transaction owner.");

            PublishUpdaterDecision(
                Path.Combine(appDirectoryPath, "update_work", "current", "updater-decision.txt"),
                "cancel");
            Assert.IsTrue(firstUpdater.WaitForExit(5000));
            Assert.AreEqual(0, firstUpdater.ExitCode);
            Assert.IsTrue(recovery.WaitForExit(5000), "The competing recovery must finish after the owner releases the lease.");
            Assert.AreEqual(0, recovery.ExitCode);
            Assert.AreEqual(0, GetRecoveryRunOnceValues(appDirectoryPath).Count);
            Assert.AreEqual(0, GetRecoverySupervisorValues(appDirectoryPath).Count);
        });
    }

    [TestMethod]
    public void ApplyUpdate_RemovesLegacyDllLayoutWithoutPreviousManifest()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            WriteTextFile(appDirectoryPath, "restart.cmd", "@exit /b 0");
            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe.config", "old-config");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "libs/SevenZipExtractor.dll", "old-sevenzip");
            WriteTextFile(appDirectoryPath, "libs/x64/7z.dll", "old-7z-native");
            WriteTextFile(appDirectoryPath, "libs/x64/OggVorbis.NET64.dll", "old-wrong-ogv");
            WriteTextFile(appDirectoryPath, "libs/x86/7z.dll", "old-7z-x86");
            WriteTextFile(appDirectoryPath, "libs/x64/sqlite3.dll", "old-libs-x64-sqlite");
            WriteTextFile(appDirectoryPath, "x86/sqlite3.dll", "old-root-x86");
            WriteTextFile(appDirectoryPath, "libs/x86/user.dll", "user-libs-x86");
            WriteTextFile(appDirectoryPath, "x86/user.dll", "user-root-x86");
            WriteTextFile(appDirectoryPath, "x64/sqlite3.dll", "old-root-x64");
            WriteTextFile(appDirectoryPath, "runtimes/win-x64/native/e_sqlite3.dll", "old-rid-e-sqlite3");
            foreach (string legacyManagedLibraryName in new[]
            {
                "Bass.Net.dll",
                "DynamicJson.dll",
                "IniLibrary.dll",
                "Livet.dll",
                "Livet.Extensions.dll",
                "MetroRadiance.Chrome.dll",
                "MetroRadiance.Core.dll",
                "MetroRadiance.dll",
                "Microsoft.Expression.Drawing.dll",
                "Microsoft.Expression.Effects.dll",
                "Microsoft.Expression.Interactions.dll",
                "Microsoft.WindowsAPICodePack.dll",
                "Microsoft.WindowsAPICodePack.Shell.dll",
                "Newtonsoft.Json.dll",
                "NLog.Database.dll",
                "NLog.dll",
                "NLog.WindowsEventLog.dll",
                "QuickConverter.dll",
                "SgmlReaderDll.dll",
                "System.Collections.Immutable.dll",
                "System.Resources.Extensions.dll",
                "System.Memory.dll",
                "System.Buffers.dll",
                "System.Numerics.Vectors.dll",
                "System.Runtime.CompilerServices.Unsafe.dll",
                "System.Windows.Interactivity.dll",
                "sqlite.net.dll"
            })
            {
                WriteTextFile(appDirectoryPath, "libs/" + legacyManagedLibraryName, "legacy-managed-library");
            }
            foreach (string legacyRootX64File in new[]
            {
                "OggVorbis.NET.dll",
                "x64/OggVorbis.NET64.dll",
                "x64/7z.dll",
                "x64/bass.dll",
                "x64/bass_fx.dll",
                "x64/bassasio.dll",
                "x64/bassenc.dll",
                "x64/bassmix.dll",
                "x64/basswasapi.dll"
            })
            {
                WriteTextFile(appDirectoryPath, legacyRootX64File, "legacy-root-x64");
            }
            WriteTextFile(appDirectoryPath, "x64/user.dll", "user-x64");
            WriteTextFile(appDirectoryPath, "config/user.config", "user-config");

            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            WriteTextFile(packageSourceDirectoryPath, "restart.cmd", "@exit /b 0");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.Updater.exe", "new-updater");
            WriteTextFile(packageSourceDirectoryPath, "SevenZipExtractor.dll", "new-sevenzip");
            WriteTextFile(packageSourceDirectoryPath, "OggVorbis.NET64.dll", "new-ogv");
            WriteTextFile(packageSourceDirectoryPath, "libs/x64/7z.dll", "new-7z-native");
            WriteTextFile(packageSourceDirectoryPath, "e_sqlite3.dll", "new-e-sqlite3");
            WriteTextFile(packageSourceDirectoryPath, "native/EverythingBridge_x64.dll", "new-bridge");
            WriteTextFile(packageSourceDirectoryPath, "lang/ja-JP.json", "{}");
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "restart.cmd",
                "restart.exe",
                "BeMusicSeeker.Updater.exe",
                "SevenZipExtractor.dll",
                "OggVorbis.NET64.dll",
                "libs/x64/7z.dll",
                "e_sqlite3.dll",
                "native/EverythingBridge_x64.dll",
                "lang/ja-JP.json"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdater(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("new-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.AreEqual("new-ogv", File.ReadAllText(Path.Combine(appDirectoryPath, "OggVorbis.NET64.dll")));
            Assert.AreEqual("new-7z-native", File.ReadAllText(Path.Combine(appDirectoryPath, "libs", "x64", "7z.dll")));
            Assert.AreEqual("new-e-sqlite3", File.ReadAllText(Path.Combine(appDirectoryPath, "e_sqlite3.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "runtimes", "win-x64", "native", "e_sqlite3.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "x64", "sqlite3.dll")));
            Assert.AreEqual("user-x64", File.ReadAllText(Path.Combine(appDirectoryPath, "x64", "user.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "libs", "SevenZipExtractor.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "libs", "x64", "OggVorbis.NET64.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "libs", "x64", "sqlite3.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe.config")));
            foreach (string legacyManagedLibraryName in new[]
            {
                "Bass.Net.dll",
                "DynamicJson.dll",
                "IniLibrary.dll",
                "Livet.dll",
                "Livet.Extensions.dll",
                "MetroRadiance.Chrome.dll",
                "MetroRadiance.Core.dll",
                "MetroRadiance.dll",
                "Microsoft.Expression.Drawing.dll",
                "Microsoft.Expression.Effects.dll",
                "Microsoft.Expression.Interactions.dll",
                "Microsoft.WindowsAPICodePack.dll",
                "Microsoft.WindowsAPICodePack.Shell.dll",
                "Newtonsoft.Json.dll",
                "NLog.Database.dll",
                "NLog.dll",
                "NLog.WindowsEventLog.dll",
                "QuickConverter.dll",
                "SgmlReaderDll.dll",
                "System.Collections.Immutable.dll",
                "System.Resources.Extensions.dll",
                "System.Memory.dll",
                "System.Buffers.dll",
                "System.Numerics.Vectors.dll",
                "System.Runtime.CompilerServices.Unsafe.dll",
                "System.Windows.Interactivity.dll",
                "sqlite.net.dll"
            })
            {
                Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "libs", legacyManagedLibraryName)));
            }
            foreach (string legacyRootX64File in new[]
            {
                "OggVorbis.NET.dll",
                "x64/OggVorbis.NET64.dll",
                "x64/7z.dll",
                "x64/bass.dll",
                "x64/bass_fx.dll",
                "x64/bassasio.dll",
                "x64/bassenc.dll",
                "x64/bassmix.dll",
                "x64/basswasapi.dll"
            })
            {
                Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, legacyRootX64File.Replace('/', Path.DirectorySeparatorChar))));
            }
            Assert.AreEqual("user-libs-x86", File.ReadAllText(Path.Combine(appDirectoryPath, "libs", "x86", "user.dll")));
            Assert.AreEqual("user-root-x86", File.ReadAllText(Path.Combine(appDirectoryPath, "x86", "user.dll")));
            Assert.AreEqual("user-config", File.ReadAllText(Path.Combine(appDirectoryPath, "config", "user.config")));

            string[] managedManifestLines = File.ReadAllLines(Path.Combine(appDirectoryPath, "update-managed-files.txt"));
            CollectionAssert.Contains(managedManifestLines, "OggVorbis.NET64.dll");
            CollectionAssert.Contains(managedManifestLines, "libs\\x64\\7z.dll");
            CollectionAssert.DoesNotContain(managedManifestLines, "libs\\x64\\OggVorbis.NET64.dll");
        });
    }

    [TestMethod]
    public void ApplyUpdate_RemovesAppManagedMetadataArtifactsWhenPackageHasNoMetadata()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            WriteTextFile(appDirectoryPath, "restart.cmd", "@exit /b 0");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "chart-info-metadata.7z", "old-root-archive");
            WriteTextFile(appDirectoryPath, "chart-info-metadata.db", "old-root-db");
            WriteTextFile(appDirectoryPath, "imported_metadata/chart-info-metadata.7z", "old-imported-archive");
            WriteTextFile(appDirectoryPath, "imported_metadata/chart-info-metadata.aaaaaaaaaaaa.7z", "old-history-archive");

            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            WriteTextFile(packageSourceDirectoryPath, "restart.cmd", "@exit /b 0");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "restart.cmd",
                "restart.exe"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdater(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("new-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "chart-info-metadata.7z")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "chart-info-metadata.db")));
            Assert.IsFalse(Directory.Exists(Path.Combine(appDirectoryPath, "imported_metadata")));
        });
    }

    [TestMethod]
    public void ApplyUpdate_ReplacesAppManagedMetadataArtifactsWithBundledArchive()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            WriteTextFile(appDirectoryPath, "restart.cmd", "@exit /b 0");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "chart-info-metadata.7z", "old-root-archive");
            WriteTextFile(appDirectoryPath, "chart-info-metadata.db", "old-root-db");
            WriteTextFile(appDirectoryPath, "imported_metadata/chart-info-metadata.7z", "old-imported-archive");
            WriteTextFile(appDirectoryPath, "imported_metadata/chart-info-metadata.bbbbbbbbbbbb.7z", "old-history-archive");

            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            WriteTextFile(packageSourceDirectoryPath, "restart.cmd", "@exit /b 0");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "chart-info-metadata.7z", "new-bundled-archive");
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "restart.cmd",
                "restart.exe",
                "chart-info-metadata.7z"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdater(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("new-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.AreEqual("new-bundled-archive", File.ReadAllText(Path.Combine(appDirectoryPath, "chart-info-metadata.7z")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "chart-info-metadata.db")));
            Assert.IsFalse(Directory.Exists(Path.Combine(appDirectoryPath, "imported_metadata")));
        });
    }

    [TestMethod]
    public void ApplyUpdate_RollbackRestoresAppManagedMetadataArtifactsAfterFailure()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            WriteTextFile(appDirectoryPath, "restart.cmd", "@exit /b 0");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "chart-info-metadata.7z", "old-root-archive");
            WriteTextFile(appDirectoryPath, "chart-info-metadata.db", "old-root-db");
            WriteTextFile(appDirectoryPath, "imported_metadata/chart-info-metadata.7z", "old-imported-archive");

            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            WriteTextFile(packageSourceDirectoryPath, "restart.cmd", "@exit /b 0");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "restart.exe", "not-an-executable");
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "restart.cmd",
                "restart.exe"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.AreEqual("old-root-archive", File.ReadAllText(Path.Combine(appDirectoryPath, "chart-info-metadata.7z")));
            Assert.AreEqual("old-root-db", File.ReadAllText(Path.Combine(appDirectoryPath, "chart-info-metadata.db")));
            Assert.AreEqual("old-imported-archive", File.ReadAllText(Path.Combine(appDirectoryPath, "imported_metadata", "chart-info-metadata.7z")));
            Assert.IsFalse(File.Exists(packagePath));
            Assert.IsFalse(Directory.Exists(Path.Combine(appDirectoryPath, "update_work", "extracted")));
            Assert.IsTrue(File.Exists(Path.Combine(appDirectoryPath, "update_work", "update-failure.txt")));
        });
    }

    [TestMethod]
    public void ApplyUpdate_RejectsBackupDirectoryOutsideDedicatedChildWithoutMutation()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            WriteTextFile(appDirectoryPath, "restart.cmd", "@exit /b 0");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            WriteTextFile(packageSourceDirectoryPath, "restart.cmd", "@exit /b 0");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "restart.cmd",
                "restart.exe"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdaterExpectFailure(appDirectoryPath, packagePath, appDirectoryPath);

            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.IsTrue(File.Exists(packagePath));
        });
    }

    [TestMethod]
    public void ApplyUpdate_RejectsMissingRestartTargetBeforeMutation()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            string missingRestartPath = Path.Combine(appDirectoryPath, "missing.exe");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", "BeMusicSeeker.exe");
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath, missingRestartPath);

            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.IsTrue(File.Exists(packagePath));
        });
    }

    [TestMethod]
    public void ApplyUpdate_RejectsPackageWithoutManagedRestartTargetBeforeMutation()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            string restartExecutablePath = Path.Combine(appDirectoryPath, "BeMusicSeeker.exe");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            WriteTextFile(appDirectoryPath, "update-managed-files.txt", "BeMusicSeeker.exe");
            WriteTextFile(packageSourceDirectoryPath, "replacement.txt", "new-file");
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", "replacement.txt");
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath, restartExecutablePath);

            Assert.AreEqual("old-app", File.ReadAllText(restartExecutablePath));
            Assert.AreEqual("BeMusicSeeker.exe", File.ReadAllText(Path.Combine(appDirectoryPath, "update-managed-files.txt")));
            Assert.IsTrue(File.Exists(packagePath));
            Assert.IsFalse(Directory.Exists(backupDirectoryPath));
        });
    }

    [TestMethod]
    public void ApplyUpdate_RejectsInvalidProcessIdBeforeMutation()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", "BeMusicSeeker.exe");
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath, processId: "0");

            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.IsTrue(File.Exists(packagePath));
            Assert.IsFalse(Directory.Exists(backupDirectoryPath));
        });
    }

    [TestMethod]
    public void ApplyUpdate_RejectsPackageOutsideDownloadsBeforeMutation()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "outside.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "restart.exe"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.IsTrue(File.Exists(packagePath));
            Assert.IsFalse(Directory.Exists(backupDirectoryPath));
        });
    }

    [TestMethod]
    public void ApplyUpdate_DoesNotRollbackAfterRestartWhenCleanupFails()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");
            string restartExecutablePath = Path.Combine(appDirectoryPath, "restart.cmd");
            string restartMarkerPath = Path.Combine(appDirectoryPath, "restart.marker");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            WriteTextFile(appDirectoryPath, "restart.cmd", "@echo old>\"%~dp0old.marker\"");
            WriteTextFile(appDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "restart.cmd"
            }));
            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            WriteTextFile(packageSourceDirectoryPath, "restart.cmd", "@echo restarted>\"%~dp0restart.marker\"");
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "restart.cmd"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            using FileStream packageLock = new(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            RunUpdater(appDirectoryPath, packagePath, backupDirectoryPath, restartExecutablePath);

            WaitForFile(restartMarkerPath);
            Assert.AreEqual("new-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.IsTrue(File.Exists(packagePath), "The package remains available when post-restart cleanup cannot delete it.");
            Assert.IsTrue(File.Exists(restartMarkerPath), "The updated restart target must be started before cleanup.");
        });
    }

    [TestMethod]
    public void ApplyUpdate_RollbackRestoresFileDirectoryTransition()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "a", "old-file");
            WriteTextFile(appDirectoryPath, "z", "blocking-file");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "a",
                "restart.exe"
            }));
            WriteTextFile(packageSourceDirectoryPath, "a/b/c.dll", "new-child");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "restart.exe", "not-an-executable");
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "a/b/c.dll",
                "restart.exe"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("old-file", File.ReadAllText(Path.Combine(appDirectoryPath, "a")));
            Assert.IsTrue(File.Exists(Path.Combine(appDirectoryPath, "z")));
            Assert.IsFalse(Directory.Exists(Path.Combine(appDirectoryPath, "a")) && File.Exists(Path.Combine(appDirectoryPath, "a", "b", "c.dll")));
        });
    }

    [TestMethod]
    public void ApplyUpdate_ReplacesManagedDirectoryWithFile()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "a/b/old.dll", "old-child");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "a/b/old.dll",
                "restart.exe"
            }));
            WriteTextFile(packageSourceDirectoryPath, "a", "new-file");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "a",
                "restart.exe"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdater(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.IsTrue(File.Exists(Path.Combine(appDirectoryPath, "a")));
            Assert.AreEqual("new-file", File.ReadAllText(Path.Combine(appDirectoryPath, "a")));
            Assert.IsFalse(File.Exists(Path.Combine(appDirectoryPath, "a", "b", "old.dll")));
        });
    }

    [TestMethod]
    public void ApplyUpdate_RejectsDirectoryToFileTransitionWithUnmanagedChildBeforeMutation()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "a/old.dll", "old-child");
            WriteTextFile(appDirectoryPath, "a/user.dll", "user-child");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "a/old.dll",
                "restart.exe"
            }));
            WriteTextFile(packageSourceDirectoryPath, "a", "new-file");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "a",
                "restart.exe"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);
            WriteTextFile(backupDirectoryPath, "previous/keep.txt", "previous-generation");

            RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.IsTrue(Directory.Exists(Path.Combine(appDirectoryPath, "a")));
            Assert.AreEqual("old-child", File.ReadAllText(Path.Combine(appDirectoryPath, "a", "old.dll")));
            Assert.AreEqual("user-child", File.ReadAllText(Path.Combine(appDirectoryPath, "a", "user.dll")));
            Assert.AreEqual("previous-generation", File.ReadAllText(Path.Combine(backupDirectoryPath, "previous", "keep.txt")));
            Assert.IsTrue(File.Exists(packagePath));
            string failureReceiptPath = Path.Combine(appDirectoryPath, "update_work", "update-failure.txt");
            Assert.IsTrue(File.Exists(failureReceiptPath));
            StringAssert.Contains(File.ReadAllText(failureReceiptPath), "unmanaged");
        });
    }

    [TestMethod]
    public void ApplyUpdate_RejectsReplacingUnmanagedFileWhenManifestExistsBeforeMutation()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "user-added.dll", "user-content");
            WriteTextFile(appDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "restart.exe"
            }));
            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "user-added.dll", "package-content");
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "restart.exe",
                "user-added.dll"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.AreEqual("user-content", File.ReadAllText(Path.Combine(appDirectoryPath, "user-added.dll")));
            Assert.AreEqual(
                string.Join(Environment.NewLine, new[] { "BeMusicSeeker.exe", "restart.exe" }),
                File.ReadAllText(Path.Combine(appDirectoryPath, "update-managed-files.txt")));
            Assert.IsTrue(File.Exists(packagePath));
            Assert.IsFalse(Directory.Exists(backupDirectoryPath));
        });
    }

    [TestMethod]
    public void ApplyUpdate_RejectsManagedFilePathThatBecameDirectoryWithUserChild()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "a/user.txt", "user-content");
            WriteTextFile(appDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "a",
                "restart.exe"
            }));
            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "a/new.dll", "package-content");
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "a/new.dll",
                "restart.exe"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("user-content", File.ReadAllText(Path.Combine(appDirectoryPath, "a", "user.txt")));
            Assert.AreEqual(
                string.Join(Environment.NewLine, new[] { "BeMusicSeeker.exe", "a", "restart.exe" }),
                File.ReadAllText(Path.Combine(appDirectoryPath, "update-managed-files.txt")));
            Assert.IsTrue(File.Exists(packagePath));
            Assert.IsFalse(Directory.Exists(backupDirectoryPath));
        });
    }

    [TestMethod]
    public void ApplyUpdate_RollbackPreservesUnmanagedSiblingInSharedDirectory()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "docs/a.txt", "old-managed");
            WriteTextFile(appDirectoryPath, "docs/user.txt", "user-file");
            WriteTextFile(appDirectoryPath, "zz", "blocking-file");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "docs/a.txt",
                "restart.exe"
            }));
            WriteTextFile(packageSourceDirectoryPath, "docs/a.txt", "new-managed");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "restart.exe", "not-an-executable");
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "docs/a.txt",
                "restart.exe"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("old-managed", File.ReadAllText(Path.Combine(appDirectoryPath, "docs", "a.txt")));
            Assert.AreEqual("user-file", File.ReadAllText(Path.Combine(appDirectoryPath, "docs", "user.txt")));
            Assert.AreEqual("blocking-file", File.ReadAllText(Path.Combine(appDirectoryPath, "zz")));
        });
    }

    [TestMethod]
    public void ApplyUpdate_RejectsReparseAncestorBeforeMovingManagedPath()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string appDirectoryPath = Path.Combine(tempDirectoryPath, "app");
            string packageSourceDirectoryPath = Path.Combine(tempDirectoryPath, "package-source");
            string externalDirectoryPath = Path.Combine(tempDirectoryPath, "external");
            string packagePath = Path.Combine(tempDirectoryPath, "app", "update_work", "downloads", "package.zip");
            string backupDirectoryPath = Path.Combine(appDirectoryPath, "update_backup");

            WriteTextFile(appDirectoryPath, "BeMusicSeeker.exe", "old-app");
            CopyRestartExecutable(appDirectoryPath);
            WriteTextFile(appDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "plugins/old.dll",
                "restart.exe"
            }));
            WriteTextFile(externalDirectoryPath, "old.dll", "external-sentinel");
            string linkPath = Path.Combine(appDirectoryPath, "plugins");
            try
            {
                Directory.CreateSymbolicLink(linkPath, externalDirectoryPath);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is PlatformNotSupportedException)
            {
                Assert.Inconclusive("The test environment does not permit directory symbolic links: " + exception.Message);
                return;
            }

            WriteTextFile(packageSourceDirectoryPath, "BeMusicSeeker.exe", "new-app");
            CopyRestartExecutable(packageSourceDirectoryPath);
            WriteTextFile(packageSourceDirectoryPath, "update-managed-files.txt", string.Join(Environment.NewLine, new[]
            {
                "BeMusicSeeker.exe",
                "restart.exe"
            }));
            ZipFile.CreateFromDirectory(packageSourceDirectoryPath, packagePath);

            RunUpdaterExpectFailure(appDirectoryPath, packagePath, backupDirectoryPath);

            Assert.AreEqual("external-sentinel", File.ReadAllText(Path.Combine(externalDirectoryPath, "old.dll")));
            Assert.AreEqual("old-app", File.ReadAllText(Path.Combine(appDirectoryPath, "BeMusicSeeker.exe")));
            Assert.IsTrue(File.Exists(packagePath));
        });
    }

    private static void RunUpdater(string appDirectoryPath, string packagePath, string backupDirectoryPath, string restartExecutablePath = null, string processId = null)
    {
        string effectiveRestartExecutablePath = restartExecutablePath ?? Path.Combine(appDirectoryPath, "restart.exe");
        using Process process = StartUpdater(appDirectoryPath, packagePath, backupDirectoryPath, restartExecutablePath, processId);
        if (!process.WaitForExit(30000))
        {
            process.Kill();
            Assert.Fail("Updater process timed out.");
        }

        WaitForFileAvailable(effectiveRestartExecutablePath);

        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        if (process.ExitCode != 0)
        {
            Assert.Fail("Updater failed with exit code " + process.ExitCode + Environment.NewLine + standardOutput + Environment.NewLine + standardError);
        }
    }

    private static void RunUpdaterRecovery(string appDirectoryPath)
    {
        using Process process = StartUpdaterRecoveryProcess(appDirectoryPath);
        if (!process.WaitForExit(30000))
        {
            process.Kill();
            Assert.Fail("Updater recovery process timed out.");
        }

        if (process.ExitCode != 0)
        {
            Assert.Fail(
                "Updater recovery failed with exit code "
                + process.ExitCode
                + Environment.NewLine
                + process.StandardOutput.ReadToEnd()
                + Environment.NewLine
                + process.StandardError.ReadToEnd());
        }
    }

    private static Process StartUpdaterRecoveryProcess(string appDirectoryPath)
    {
        return Process.Start(new ProcessStartInfo
        {
            FileName = FindUpdaterExecutable(),
            Arguments = string.Join(" ", new[]
            {
                "--recover",
                "--app-dir",
                appDirectoryPath
            }.Select(QuoteArgument)),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(FindUpdaterExecutable()) ?? Environment.CurrentDirectory
        }) ?? throw new InvalidOperationException("Updater recovery process was not started.");
    }

    private static void RunUpdaterWatchdogRecovery(string appDirectoryPath)
    {
        string updaterPath = FindUpdaterExecutable();
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = updaterPath,
            Arguments = string.Join(" ", new[]
            {
                "--watch",
                "--app-dir",
                appDirectoryPath,
                "--pid",
                GetExitedProcessId().ToString()
            }.Select(QuoteArgument)),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(updaterPath) ?? Environment.CurrentDirectory
        }) ?? throw new InvalidOperationException("Updater watchdog process was not started.");
        if (!process.WaitForExit(30000))
        {
            process.Kill();
            Assert.Fail("Updater watchdog process timed out.");
        }

        if (process.ExitCode != 0)
        {
            Assert.Fail(
                "Updater watchdog failed with exit code "
                + process.ExitCode
                + Environment.NewLine
                + process.StandardOutput.ReadToEnd()
                + Environment.NewLine
                + process.StandardError.ReadToEnd());
        }
    }

    private static void RunUpdaterExpectFailure(string appDirectoryPath, string packagePath, string backupDirectoryPath, string restartExecutablePath = null, string processId = null)
    {
        string effectiveRestartExecutablePath = restartExecutablePath ?? Path.Combine(appDirectoryPath, "restart.exe");
        using Process process = StartUpdater(appDirectoryPath, packagePath, backupDirectoryPath, restartExecutablePath, processId);
        if (!process.WaitForExit(30000))
        {
            process.Kill();
            Assert.Fail("Updater process timed out.");
        }

        WaitForFileAvailable(effectiveRestartExecutablePath);

        if (process.ExitCode == 0)
        {
            string standardOutput = process.StandardOutput.ReadToEnd();
            string standardError = process.StandardError.ReadToEnd();
            Assert.Fail("Updater unexpectedly succeeded." + Environment.NewLine + standardOutput + Environment.NewLine + standardError);
        }
    }

    private static Process StartUpdater(string appDirectoryPath, string packagePath, string backupDirectoryPath, string restartExecutablePath = null, string processId = null, bool publishProceed = true)
    {
        restartExecutablePath ??= Path.Combine(appDirectoryPath, "restart.exe");
        processId ??= GetExitedProcessId().ToString();
        string readyFilePath = Path.Combine(appDirectoryPath, "update_work", "current", "updater-ready.txt");
        string decisionFilePath = Path.Combine(appDirectoryPath, "update_work", "current", "updater-decision.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(readyFilePath)!);
        if (File.Exists(readyFilePath))
        {
            File.Delete(readyFilePath);
        }
        if (File.Exists(decisionFilePath))
        {
            File.Delete(decisionFilePath);
        }

        Process process = Process.Start(CreateUpdaterStartInfo(
            appDirectoryPath,
            packagePath,
            backupDirectoryPath,
            restartExecutablePath,
            processId))
            ?? throw new InvalidOperationException("Updater process was not started.");
        bool validProcessId = int.TryParse(processId, out int parsedProcessId) && parsedProcessId > 0;
        if (validProcessId)
        {
            if (WaitForReadyOrProcessExit(process, readyFilePath) && publishProceed)
            {
                PublishUpdaterDecision(decisionFilePath, "proceed");
            }
        }
        return process;
    }

    private static void PublishUpdaterDecision(string decisionFilePath, string decision)
    {
        string temporaryPath = decisionFilePath + ".tmp";
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(decision);
        using (FileStream stream = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, decisionFilePath, overwrite: true);
    }

    private static ProcessStartInfo CreateUpdaterStartInfo(
        string appDirectoryPath,
        string packagePath,
        string backupDirectoryPath,
        string restartExecutablePath = null,
        string processId = null)
    {
        string updaterPath = FindUpdaterExecutable();
        restartExecutablePath ??= Path.Combine(appDirectoryPath, "restart.exe");
        processId ??= GetExitedProcessId().ToString();
        string readyFilePath = Path.Combine(appDirectoryPath, "update_work", "current", "updater-ready.txt");
        string decisionFilePath = Path.Combine(appDirectoryPath, "update_work", "current", "updater-decision.txt");
        return new ProcessStartInfo
        {
            FileName = updaterPath,
            Arguments = string.Join(" ", new[]
            {
                "--app-dir",
                appDirectoryPath,
                "--package",
                packagePath,
                "--backup-dir",
                backupDirectoryPath,
                "--ready-file",
                readyFilePath,
                "--decision-file",
                decisionFilePath,
                "--pid",
                processId,
                "--restart-exe",
                restartExecutablePath
            }.Select(QuoteArgument)),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(updaterPath) ?? Environment.CurrentDirectory
        };
    }

    private static int GetExitedProcessId()
    {
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = "/c exit 0",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("The exited process fixture was not started.");
        int processId = process.Id;
        if (!process.WaitForExit(5000))
        {
            process.Kill();
            Assert.Fail("The exited process fixture did not terminate.");
        }

        return processId;
    }

    private static void WaitForFile(string filePath)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(filePath) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        Assert.IsTrue(File.Exists(filePath), "Expected file was not created: " + filePath);
    }

    private static bool WaitForReadyOrProcessExit(Process process, string filePath)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(filePath) && DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                return false;
            }

            Thread.Sleep(50);
        }

        if (File.Exists(filePath))
        {
            return true;
        }

        Assert.Fail("Expected file was not created: " + filePath);
        return false;
    }

    private static void WaitForFileAvailable(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }

        Assert.Fail("Restart target remained locked: " + filePath);
    }

    private static string FindUpdaterExecutable()
    {
        string repositoryRoot = FindRepositoryRoot();
        var frameworkDirectory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string targetFramework = frameworkDirectory.Name;
        string configuration = frameworkDirectory.Parent?.Name ?? "Debug";
        string platform = frameworkDirectory.Parent?.Parent?.Name ?? "x64";

        string[] candidates =
        [
            Path.Combine(repositoryRoot, "BeMusicSeeker.Updater", "bin", platform, configuration, targetFramework, "BeMusicSeeker.Updater.exe"),
            Path.Combine(repositoryRoot, "BeMusicSeeker.Updater", "bin", configuration, targetFramework, "BeMusicSeeker.Updater.exe")
        ];
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Updater executable was not found.", candidates[0]);
    }

    private static IReadOnlyList<string> GetRecoveryRunOnceValues(string appDirectoryPath)
    {
        using RegistryKey? runOnce = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
            writable: false);
        if (runOnce == null)
        {
            return Array.Empty<string>();
        }

        return runOnce.GetValueNames()
            .Where(valueName => valueName.StartsWith("!BeMusicSeeker.UpdateRecovery-", StringComparison.Ordinal)
                || valueName.StartsWith("BeMusicSeeker.UpdateRecovery-", StringComparison.Ordinal))
            .Where(valueName => (runOnce.GetValue(valueName) as string)?.Contains(
                appDirectoryPath,
                StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
    }

    private static void DeleteRecoveryRunOnceValues(string appDirectoryPath)
    {
        using RegistryKey? runOnce = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
            writable: true);
        if (runOnce != null)
        {
            foreach (string valueName in GetRecoveryRunOnceValues(appDirectoryPath))
            {
                runOnce.DeleteValue(valueName, throwOnMissingValue: false);
            }

            runOnce.Flush();
        }

        using RegistryKey? run = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            writable: true);
        if (run == null)
        {
            return;
        }

        foreach (string valueName in run.GetValueNames()
            .Where(valueName => valueName.StartsWith("BeMusicSeeker.UpdateRecoverySupervisor-", StringComparison.Ordinal))
            .Where(valueName => (run.GetValue(valueName) as string)?.Contains(
                appDirectoryPath,
                StringComparison.OrdinalIgnoreCase) == true)
            .ToArray())
        {
            run.DeleteValue(valueName, throwOnMissingValue: false);
        }

        run.Flush();
    }

    private static IReadOnlyList<string> GetRecoverySupervisorValues(string appDirectoryPath)
    {
        using RegistryKey? run = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            writable: false);
        if (run == null)
        {
            return Array.Empty<string>();
        }

        return run.GetValueNames()
            .Where(valueName => valueName.StartsWith("BeMusicSeeker.UpdateRecoverySupervisor-", StringComparison.Ordinal))
            .Where(valueName => (run.GetValue(valueName) as string)?.Contains(
                appDirectoryPath,
                StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
    }

    private static string FindRepositoryRoot()
    {
        string? directoryPath = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directoryPath))
        {
            if (File.Exists(Path.Combine(directoryPath, "BeMusicSeeker.csproj")))
            {
                return directoryPath!;
            }

            DirectoryInfo? parent = Directory.GetParent(directoryPath);
            directoryPath = parent?.FullName;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static void WriteTextFile(string rootDirectoryPath, string relativePath, string contents)
    {
        string filePath = Path.Combine(rootDirectoryPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        string? directoryPath = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        File.WriteAllText(filePath, contents);
    }

    private static void CopyRestartExecutable(string rootDirectoryPath)
    {
        Directory.CreateDirectory(rootDirectoryPath);
        File.Copy(FindUpdaterExecutable(), Path.Combine(rootDirectoryPath, "restart.exe"), overwrite: true);
    }

    private static string QuoteArgument(string argument)
    {
        if (string.IsNullOrEmpty(argument))
        {
            return "\"\"";
        }

        if (!argument.Any(char.IsWhiteSpace) && !argument.Contains("\""))
        {
            return argument;
        }

        var builder = new System.Text.StringBuilder();
        builder.Append('"');
        int backslashCount = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashCount++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', backslashCount * 2 + 1);
                builder.Append('"');
                backslashCount = 0;
                continue;
            }

            builder.Append('\\', backslashCount);
            backslashCount = 0;
            builder.Append(c);
        }

        builder.Append('\\', backslashCount * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private static void WithTemporaryDirectory(Action<string> action)
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerUpdaterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        Directory.CreateDirectory(Path.Combine(tempDirectoryPath, "app", "update_work", "downloads"));
        try
        {
            action(tempDirectoryPath);
        }
        finally
        {
            DeleteRecoveryRunOnceValues(Path.Combine(tempDirectoryPath, "app"));
            if (Directory.Exists(tempDirectoryPath))
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                while (Directory.Exists(tempDirectoryPath) && DateTime.UtcNow < deadline)
                {
                    try
                    {
                        Directory.Delete(tempDirectoryPath, recursive: true);
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(50);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        Thread.Sleep(50);
                    }
                }

                Assert.IsFalse(Directory.Exists(tempDirectoryPath), "Temporary updater test directory remained locked: " + tempDirectoryPath);
            }
        }
    }
}
