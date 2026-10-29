using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsLibraryInitializationTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
// 実VMがprocess共通のライブラリ通知を購読し、同じDispatcher上の構成と終了寿命まで所有するため並列化しません。
[DoNotParallelize]
public sealed class PendingPackageRestoreInitializationTests
{
    /// <summary>通常起動の最終失敗確定前に三択を待ち、A変更とB解放後のRetryだけを新規取得します。DB・model保護外待機と、files段階を再実行しないことを実VMで保証します。</summary>
    /// <remarks>実DBと実ステージを使い、表示応答だけを代替します。保存ヘッダーはfilesとのJoin前に一回開始する接続を静的点検し、複合条件は共通owner、画面操作はWindowへ分担します。finallyで選択・readlock・実起動・scheduler・終了準備を全回収します。</remarks>
    [TestMethod]
    public void Startup_PendingFailureWaitsOutsideModelProtectionAndRetryReadsFreshInput()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            using var input = new PendingRestoreInput();
            var failures = new List<StartupLibraryInitializationFailurePresentation>();
            using var fixture = new MainWindowViewModelStartupProgressTests.CompletionFixture(
                new FailureRecorder(failures));
            fixture.OptionalRelease.TrySetResult();
            input.Register(fixture.Library.InitializationSongDbPath);
            string originalHash = ChartFileContentReader.ReadSnapshot(input.A).Sha256;
            FileStream? held = input.LockB();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<PendingPackageRestoreChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Dialogs.PendingRestoreObservedAsync = async () =>
            {
                // model lock・transactionを保持する誤りは、選択到達前のこの独立操作を完了できない。
                await Task.WhenAll(Task.Run(() => fixture.Library.GetPendingBmsFormatChartFilesSnapshot()),
                    Task.Run(() => fixture.Library.CaptureNormalLibraryChartSnapshot()),
                    Task.Run(() => new BmsLibraryDbGateway(fixture.Library.InitializationSongDbPath).ExecuteSongDbTransaction(
                        db => db.Execute("CREATE TABLE pending_recovery_probe (value TEXT)"))));
                entered.TrySetResult();
                return new(UiDialogStatus.Accepted, await release.Task);
            };
            int filesCompleted = 0;
            var filesLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            PropertyChangedEventHandler observeFiles = (_, args) =>
            {
                if (args.PropertyName == nameof(BMSLibrary.LibraryDatabaseLoadCompletedVersion))
                {
                    Interlocked.Increment(ref filesCompleted);
                    filesLoaded.TrySetResult();
                }
            };
            fixture.Library.PropertyChanged += observeFiles;
            Task<bool> startup = fixture.Start();
            try
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(filesLoaded.Task, startup, "pending-retry-files-loaded"), "pending-retry-files-loaded");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, startup, "pending-retry-choice"), "pending-retry-entry");
                Assert.IsFalse(startup.IsCompleted);
                Assert.AreEqual(1, Volatile.Read(ref filesCompleted));
                int filesVersionBeforeRetry = fixture.Library.LibraryDatabaseLoadCompletedVersion;
                Assert.IsTrue(filesVersionBeforeRetry > 0);
                Assert.AreEqual(0, failures.Count);
                Assert.IsFalse(fixture.Owner.ProgressHub.StartupProgress.IsFailed);
                Assert.IsFalse(fixture.Owner.IsInitializationCompleted);
                Assert.AreEqual(3, new BmsLibraryDbGateway(fixture.Library.InitializationSongDbPath).LoadInstallPackages().Count);
                Assert.AreEqual(0, fixture.Library.ChartPackagesPending.Count);
                File.WriteAllText(input.A, PendingRestoreInput.Chart("Fresh A"));
                held?.Dispose(); held = null;
                Assert.AreEqual(filesVersionBeforeRetry, fixture.Library.LibraryDatabaseLoadCompletedVersion);
                release.TrySetResult(PendingPackageRestoreChoice.Retry);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(startup, "pending-retry-startup");
                Assert.IsTrue(startup.GetAwaiter().GetResult());
                TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.WaitForBackgroundTerminalAsync(), "pending-retry-background");
                Assert.AreEqual(1, fixture.Dialogs.PendingRestoreCount);
                Assert.AreEqual(1, Volatile.Read(ref filesCompleted));
                Assert.AreEqual(filesVersionBeforeRetry, fixture.Library.LibraryDatabaseLoadCompletedVersion);
                Assert.AreEqual(0, failures.Count);
                Assert.AreEqual(2, fixture.Library.ChartPackagesPending.Count);
                string actual = fixture.Library.ChartPackagesPending.Single(package => package.path == Path.GetDirectoryName(input.A)).ChartEntries.Single().Chart.Sha256;
                Assert.AreEqual(ChartFileContentReader.ReadSnapshot(input.A).Sha256, actual);
                Assert.AreNotEqual(originalHash, actual);
                Assert.IsTrue(File.Exists(input.B));
            }
            finally
            {
                held?.Dispose(); release.TrySetResult(PendingPackageRestoreChoice.None);
                fixture.Library.PropertyChanged -= observeFiles;
            }
        });
    }

    /// <summary>既存実VMの全再初期化でResetのDB失敗後も全登録を保持して再承認を待ち、成功後だけemptyで通常完了します。同じlibraryの通常取込みも再開できます。</summary>
    /// <remarks>削除最終行の実SQLite triggerでrollbackを検証します。入力は登録root外であり、通常の初期化剪定と混同しません。</remarks>
    [TestMethod]
    public void Reinitialize_ResetFailureRequiresAnotherExplicitSelectionBeforeNormalImport()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            using var input = new PendingRestoreInput();
            using var fixture = new MainWindowViewModelStartupProgressTests.CompletionFixture();
            fixture.Settings.AutoInstallAfterAutomaticEstimation = false;
            fixture.OptionalRelease.TrySetResult();
            Task<bool> startup = fixture.Start();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(startup, "pending-reset-first-startup");
            Assert.IsTrue(startup.GetAwaiter().GetResult());
            TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.WaitForBackgroundTerminalAsync(), "pending-reset-first-background");
            input.Register(fixture.Library.InitializationSongDbPath);
            input.BlockReset(fixture.Library.InitializationSongDbPath);
            byte[] originalA = File.ReadAllBytes(input.A);
            byte[] originalB = File.ReadAllBytes(input.B);
            using FileStream held = input.LockB();
            var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<PendingPackageRestoreChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Dialogs.PendingRestoreObservedAsync = async () =>
            {
                if (fixture.Dialogs.PendingRestoreCount == 1) { return new(UiDialogStatus.Accepted, PendingPackageRestoreChoice.Reset); }
                second.TrySetResult();
                return new(UiDialogStatus.Accepted, await release.Task);
            };
            Task reinitialize = fixture.Owner.ReinitializeLibraryAsync(); fixture.Track(reinitialize);
            try
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(second.Task, reinitialize, "pending-reset-reselection"), "pending-reset-second-choice");
                Assert.IsFalse(reinitialize.IsCompleted);
                Assert.AreEqual(3, new BmsLibraryDbGateway(fixture.Library.InitializationSongDbPath).LoadInstallPackages().Count);
                Assert.AreEqual(0, fixture.Library.ChartPackagesPending.Count);
                ExecuteSongDbFixtureTransaction(fixture.Library.InitializationSongDbPath, db => db.Execute("DROP TRIGGER pending_reset_fail"));
                release.TrySetResult(PendingPackageRestoreChoice.Reset);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(reinitialize, "pending-reset-reinitialize");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.WaitForBackgroundTerminalAsync(), "pending-reset-background");
                Assert.IsTrue(fixture.Owner.IsInitializationCompleted);
                Assert.AreEqual(2, fixture.Dialogs.PendingRestoreCount);
                Assert.AreEqual(0, new BmsLibraryDbGateway(fixture.Library.InitializationSongDbPath).LoadInstallPackages().Count);
                Assert.AreEqual(0, fixture.Library.ChartPackagesPending.Count);
                CollectionAssert.AreEqual(originalA, File.ReadAllBytes(input.A));
                held.Dispose();
                CollectionAssert.AreEqual(originalB, File.ReadAllBytes(input.B));
                PackageInstallFailure? importFailure = null;
                fixture.Owner.PackageInstallWorkflow.FailurePublished += value => importFailure = value;
                Assert.IsTrue(fixture.Owner.PackageInstallWorkflow.Enqueue([input.A]));
                Task import = fixture.Owner.PackageInstallWorkflow.WaitForIdleAsync(); fixture.Track(import);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(import, "pending-reset-normal-import");
                Assert.IsNull(importFailure, importFailure?.Exception.ToString());
                Assert.IsTrue(fixture.Library.ChartPackagesPending.Count > 0);
                Assert.IsFalse(fixture.Composition.OperationAdmission.IsActive);
                Assert.IsFalse(fixture.Composition.PlaylistOperationAdmission.IsActive);
            }
            finally { release.TrySetResult(PendingPackageRestoreChoice.None); }
        });
    }

    /// <summary>×・Escは登録を保持して当回失敗、Exitだけは親L/P・gate・UI抑止の解放後に通常Closeへ配送します。終了選択から成功・後続を公開しません。</summary>
    /// <remarks>実VMと実終了準備を使い、WindowのClose要求配送先だけを観測します。初期化Taskからその終了準備をawaitしないことを同じ起動Taskの完了で検出します。</remarks>
    [DataTestMethod]
    [DataRow("close")]
    [DataRow("escape")]
    [DataRow("exit")]
    public void Startup_CloseAndExitRemainDistinctAndReleaseParentBeforeShutdown(string choice)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            using var input = new PendingRestoreInput();
            var failures = new List<StartupLibraryInitializationFailurePresentation>();
            using var fixture = new MainWindowViewModelStartupProgressTests.CompletionFixture(new FailureRecorder(failures));
            fixture.OptionalRelease.TrySetResult();
            input.Register(fixture.Library.InitializationSongDbPath);
            using FileStream held = input.LockB();
            int exits = 0;
            bool releasedAtExit = false;
            Task? shutdown = null;
            SemaphoreSlim gate = typeof(MainWindowViewModel).GetField("_semaphore", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as SemaphoreSlim
                ?? throw new AssertFailedException("起動gateが見つかりません。");
            Action exit = () =>
            {
                exits++;
                releasedAtExit = !fixture.Composition.OperationAdmission.IsActive && !fixture.Composition.PlaylistOperationAdmission.IsActive
                    && gate.CurrentCount == 1 && !fixture.Owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked;
                if (choice == "exit") { shutdown = fixture.Owner.ShellShutdownWorkflow.RequestWindowCloseAsync(); fixture.Track(shutdown); }
            };
            fixture.Owner.ShellShutdownWorkflow.InitializationExitRequested += exit;
            fixture.Dialogs.PendingRestoreObservedAsync = () => Task.FromResult(new UiWindowDialogResult<PendingPackageRestoreChoice>(
                choice == "exit" ? UiDialogStatus.Accepted : choice == "close" ? UiDialogStatus.ClosedByUser : UiDialogStatus.CancelledByUser,
                choice == "exit" ? PendingPackageRestoreChoice.Exit : PendingPackageRestoreChoice.None));
            try
            {
                Task<bool> startup = fixture.Start();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(startup, "pending-close-exit-startup");
                Assert.IsFalse(startup.GetAwaiter().GetResult());
                Assert.AreEqual(choice == "exit" ? 1 : 0, exits);
                if (choice == "exit")
                {
                    Assert.IsTrue(releasedAtExit);
                    Assert.AreEqual(0, failures.Count);
                    Assert.IsFalse(fixture.Scheduler.IsStarted);
                }
                else
                {
                    Assert.AreEqual(1, failures.Count);
                    Assert.IsInstanceOfType<PendingPackageRestoreException>(failures.Single().Exception);
                    Assert.IsTrue(fixture.Owner.ProgressHub.StartupProgress.IsFailed);
                }
                Assert.IsFalse(fixture.Owner.IsInitializationCompleted);
                Assert.AreEqual(3, new BmsLibraryDbGateway(fixture.Library.InitializationSongDbPath).LoadInstallPackages().Count);
                if (shutdown != null)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(shutdown, "pending-exit-shutdown");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.Owner.ShellShutdownWorkflow.RequestInitializationExitAsync(), "pending-exit-duplicate-request");
                    Assert.AreEqual(1, exits);
                }
            }
            finally { fixture.Owner.ShellShutdownWorkflow.InitializationExitRequested -= exit; }
        });
    }

    /// <summary>保留復元以外のファイル段階の元失敗を通常の初期化失敗へ渡し、三択を提示しません。</summary>
    /// <remarks>実VMの他段階失敗を代表します。DB確定後の通知故障の分類は、非WPFの実Libraryを使う共通workflowで確認します。</remarks>
    [TestMethod]
    public void Startup_OtherPhaseFailureDoesNotOfferRecovery()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var failures = new List<StartupLibraryInitializationFailurePresentation>();
            using var fixture = new MainWindowViewModelStartupProgressTests.CompletionFixture(new FailureRecorder(failures));
            fixture.OptionalRelease.TrySetResult();
            var expected = new IOException("files phase failed");
            fixture.OptionsFailure = expected;
            Task<bool> startup = fixture.Start();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(startup, "pending-other-failure");
            Assert.IsFalse(startup.GetAwaiter().GetResult());
            Assert.AreEqual(0, fixture.Dialogs.PendingRestoreCount);
            Assert.AreEqual(1, failures.Count);
            Assert.AreSame(expected, failures.Single().Exception);
        });
    }

    private sealed class FailureRecorder(List<StartupLibraryInitializationFailurePresentation> failures) : IStartupLibraryInitializationFailurePresenter
    {
        public void Present(StartupLibraryInitializationFailurePresentation presentation) => failures.Add(presentation);
    }

}
