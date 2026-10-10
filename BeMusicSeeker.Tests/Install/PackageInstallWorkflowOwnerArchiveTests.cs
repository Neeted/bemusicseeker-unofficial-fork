using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

public sealed partial class PackageInstallWorkflowOwnerTests
{
    /// <summary>
    /// 実owner・mutation portから展開直後取消、準備後停止失敗、譜面なし、実展開先消失へ到達します。
    /// 未引渡し管理入力の回収終端まで受付を保持し、利用者元ZIPと未変更DBを保全し、取消・元例外・準備診断を解放後へ一度渡します。
    /// </summary>
    [DataTestMethod]
    [DataRow("cancel")]
    [DataRow("stop_failure")]
    [DataRow("no_charts")]
    [DataRow("missing_expanded_input")]
    public async Task ProductionImport_PreApplyTerminalReclaimsManagedInputsBeforeAdmissionRelease(string scenario)
    {
        using var fixture = new ManagedArchiveInstallFixture();
        string archive = fixture.CreateInput("managed", chart: scenario != "no_charts");
        string borrowed = fixture.CreateInput("borrowed", "Borrowed");
        string[] paths = [archive];
        string? unexpanded = null;
        if (scenario == "cancel")
        {
            unexpanded = fixture.CreateInput("managed", "NotExpanded");
            paths = [archive, unexpanded, borrowed];
        }
        if (scenario == "stop_failure")
        {
            File.WriteAllText(borrowed, "broken zip");
            paths = [borrowed, archive];
        }
        var expectedFailure = new IOException("playback stop failure marker");
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activity = new ChartMutationActivityOwner();
        var playback = new ArchiveTestPlaybackPort(() =>
        {
            if (scenario == "stop_failure") { return Task.FromException(expectedFailure); }
            if (scenario == "missing_expanded_input") { Directory.Delete(fixture.ExtractedRoots.Single(), recursive: true); }
            return Task.CompletedTask;
        });
        var owner = new PackageInstallWorkflowOwner(fixture.Dialogs, fixture.Library.OperationAdmission,
            activity, new BmsLibraryPackageInstallMutationPort(), playback, action => { action(); return true; });
        owner.AttachLibrary(fixture.Library);
        PackageInstallFailure? failure = null;
        int terminalCount = 0;
        int preparationHeld = 0;
        bool notifiedWhileActive = false;
        owner.FailurePublished += value =>
        {
            notifiedWhileActive |= fixture.Library.OperationAdmission.IsActive;
            failure = value;
            terminalCount++;
        };
        owner.StatusChanged += snapshot =>
        {
            if (scenario == "cancel" && snapshot.IsActive && snapshot.CompletedPathCount == 1
                && Interlocked.Exchange(ref preparationHeld, 1) == 0)
            {
                prepared.TrySetResult();
                releasePreparation.Task.GetAwaiter().GetResult();
            }
        };
        activity.ActivityChanged += (_, _) =>
        {
            if (!activity.IsActive)
            {
                cleanup.TrySetResult();
                releaseCleanup.Task.GetAwaiter().GetResult();
            }
        };
        fixture.Dialogs.OnMessage = () => notifiedWhileActive |= fixture.Library.OperationAdmission.IsActive;
        Task? idle = null;
        try
        {
            Assert.IsTrue(owner.Enqueue(paths));
            idle = owner.WaitForIdleAsync();
            if (scenario == "cancel")
            {
                await Task.WhenAny(prepared.Task, idle);
                Assert.IsTrue(prepared.Task.IsCompletedSuccessfully, "実終端が展開完了通知へ先行した場合は未到達として失敗します。");
                Assert.AreEqual(1, fixture.ExtractedRoots.Count);
                Assert.IsFalse(File.Exists(archive));
                Assert.IsTrue(File.Exists(unexpanded));
                owner.CancelAll();
                releasePreparation.TrySetResult();
            }
            await Task.WhenAny(cleanup.Task, idle);
            Assert.IsTrue(cleanup.Task.IsCompletedSuccessfully, "回収到達前に終端した場合は成功と扱いません。");
            Assert.IsTrue(fixture.Library.OperationAdmission.IsActive);
            Assert.IsFalse(idle.IsCompleted);
            Assert.AreEqual(0, terminalCount);
            Assert.AreEqual(0, fixture.Dialogs.Messages.Count);
            Assert.AreEqual(1, fixture.ExtractedRoots.Count);
            Assert.IsTrue(fixture.ExtractedRoots.All(path => !Directory.Exists(path)));
            Assert.IsFalse(File.Exists(archive));
            if (unexpanded != null) { Assert.IsFalse(File.Exists(unexpanded), "後続の未展開管理ZIPも終端で回収します。"); }
            Assert.IsTrue(File.Exists(borrowed), "借用元ZIPは取消・失敗でも保全します。");
            Assert.AreEqual(0, fixture.Library.ChartPackagesPending.Count);
            Assert.AreEqual(0, fixture.Library.ChartPackagesInstalled.Count);
            Assert.AreEqual(0, fixture.Library.BmsCharts.Count);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.SongDb).OpenSongDbReadOnly())
            {
                Assert.AreEqual(0, db.Table<LR2SongDB.song>().Count());
                Assert.AreEqual(0, db.Table<LR2SongDBExtended.install>().Count());
            }
            releaseCleanup.TrySetResult();
            await idle;
            Assert.IsFalse(notifiedWhileActive, "任意案内と失敗通知は共通受付の解放後に配送します。");
            Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
            if (scenario is "cancel" or "stop_failure")
            {
                Assert.IsNotNull(failure);
                Assert.AreEqual(1, terminalCount);
                Assert.IsFalse(failure.CommandResult.HasDurableCommit);
                if (scenario == "cancel") { Assert.IsInstanceOfType<OperationCanceledException>(failure.Exception); }
                else
                {
                    Assert.AreSame(expectedFailure, failure.Exception);
                    StringAssert.Contains(failure.CommandResult.OperationMessages.Single().MessageBoxText, borrowed);
                }
            }
            else { Assert.IsNull(failure); }
            Assert.AreEqual(scenario is "stop_failure" or "missing_expanded_input" ? 1 : 0, fixture.Dialogs.Messages.Count);
            if (scenario == "missing_expanded_input")
            {
                Assert.AreEqual(Resources.Warn_InstallAbortedFilesNotFound, fixture.Dialogs.Messages.Single().MessageBoxText);
            }
            Assert.AreEqual(scenario is "stop_failure" or "missing_expanded_input" ? 1 : 0, playback.StopCount);
            Assert.IsTrue(fixture.Library.OperationAdmission.TryEnter(out IDisposable next));
            next.Dispose();
        }
        finally
        {
            releasePreparation.TrySetResult();
            releaseCleanup.TrySetResult();
            if (idle != null) { await idle; }
            await owner.WaitForIdleAsync();
        }
    }

    /// <summary>全失敗の必須日時警告と壊れZIP混在の抽出警告を、実処理が生成して受付解放後に一度表示します。正常独立対象の確定を警告で失敗へ変えません。</summary>
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ProductionImport_ArchiveWarningsAreDeliveredOnceAfterRelease(bool timestampFailure)
    {
        using var fixture = new ManagedArchiveInstallFixture();
        string rejected = fixture.CreateInput("managed", "Rejected");
        string marker = "required timestamp failure marker";
        if (timestampFailure) { fixture.Files.BeforeTimestamp = _ => throw new IOException(marker); }
        else { File.WriteAllText(rejected, "broken zip"); }
        string[] paths = timestampFailure ? [rejected] : [rejected, fixture.CreateInput("managed", "Normal")];
        bool notifiedWhileActive = false;
        fixture.Dialogs.OnMessage = () => notifiedWhileActive |= fixture.Library.OperationAdmission.IsActive;
        var owner = new PackageInstallWorkflowOwner(fixture.Dialogs, fixture.Library.OperationAdmission,
            new ChartMutationActivityOwner(), new BmsLibraryPackageInstallMutationPort(),
            new NoOpChartMutationPlaybackPort(), action => { action(); return true; });
        owner.AttachLibrary(fixture.Library);
        PackageInstallFailure? failure = null;
        PackageInstallCompletionReceipt? completion = null;
        owner.FailurePublished += value => failure = value;
        owner.CompletionPublished += value => completion = value;
        try
        {
            Assert.IsTrue(owner.Enqueue(paths));
            await owner.WaitForIdleAsync();
            Assert.IsFalse(notifiedWhileActive);
            Assert.IsNull(failure);
            Assert.AreEqual(0, fixture.Dialogs.ModelMessages);
            BeMusicSeeker.Views.Dialogs.UiMessageRequest message = fixture.Dialogs.Messages.Single();
            StringAssert.Contains(message.MessageBoxText, rejected);
            if (timestampFailure)
            {
                StringAssert.Contains(message.MessageBoxText, marker);
                StringAssert.Contains(message.MessageBoxText, "chart.bms");
                Assert.IsNull(completion);
            }
            else
            {
                Assert.IsNotNull(completion);
                Assert.IsTrue(completion.HasDurableCommit);
                Assert.AreEqual(1, completion.Packages.Count);
                Assert.AreNotEqual(string.Format(Resources.Warn_ArchiveExtractFailed, rejected, string.Empty), message.MessageBoxText,
                    "実配送でも抽出失敗の対象と原因を保持します。");
            }
            Assert.AreEqual(timestampFailure ? 0 : 1, fixture.Library.BmsCharts.Count);
            Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
        }
        finally { await owner.WaitForIdleAsync(); }
    }

    private sealed class ArchiveTestPlaybackPort(Func<Task> stop) : IChartMutationPlaybackPort
    {
        internal int StopCount { get; private set; }
        public Task StopPlaybackForMutationAsync() { StopCount++; return stop(); }
    }
}
