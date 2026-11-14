using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.SettingsMediaTestFixture;

namespace BeMusicSeeker.Tests;

/// <summary>閏年修復の準備・通知で、終了判定と元の表示障害の保持を検証します。</summary>
/// <remarks>専有の初期化owner・DB・gate・注入dialogで確認します。実設定VM・root・共有ResourceService購読・Dispatcherを生成する入口の保証はWindows側が分担します。</remarks>
[TestClass]
public sealed class SettingDialogEditCompletionTests
{
    private TestWorkspace? ownedWorkspace;

    /// <summary>初期化済みのケース専有媒体だけを返し、constructorでは領域を作りません。</summary>
    private TestWorkspace CaseWorkspace => ownedWorkspace
        ?? throw new InvalidOperationException("ケースの可変媒体はTestInitialize後に利用します。");

    /// <summary>ケースのconstructorと既存field準備が成功した後に可変媒体を所有します。</summary>
    [TestInitialize]
    public void InitializeWorkspace() => ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

    /// <summary>ケース固有の処理と既存finallyが終わった後に、開始済みの所有媒体だけを回収します。</summary>
    [TestCleanup]
    public void CleanupWorkspace() => ownedWorkspace?.Dispose();

    /// <summary>このケースの準備と読取りで共有するDBの所有者です。</summary>
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>作業スレッドの実候補読取と画面結果の分類を非WPFで確認し、終了フラグだけで元の画面障害を黙殺しないことを保証します。</summary>
    [TestMethod]
    // 0:承認後終了、1:AppClosing、2:元例外付きFailed、3:owner不在、4:通知境界から元例外。
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public async Task LeapYearPreparation_ShutdownDistinguishesClosingAndPreservesActualDialogFailure(int resultKind)
    {
        string root = CreateTemporaryRoot(CaseWorkspace);
        try
        {
            Settings settings = CreateValidCustomFolderSettings(root, []);
            string folder = Path.Combine(root, "LeapCandidate");
            Directory.CreateDirectory(folder);
            DateTime sentinel = new(2024, 2, 29, 12, 0, 0);
            Directory.SetLastWriteTime(folder, sentinel);
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(settings.LR2SongDBPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.Insert(new LR2SongDB.folder { path = folder + Path.DirectorySeparatorChar, type = 1, date = null, adddate = 0 });
            }
            var original = new IOException("actual confirmation failure");
            bool shutdown = false;
            var dialogs = new RecordingRootDialogService
            {
                ConfirmationResult = resultKind switch
                {
                    1 => UiDialogResult.NotShown(UiDialogStatus.AppClosing),
                    2 => UiDialogResult.Failed(original),
                    3 => UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable),
                    _ => UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes)
                },
                ConfirmationObserved = _ =>
                {
                    shutdown = true;
                    if (resultKind == 4) { throw original; }
                }
            };
            using var gate = new SemaphoreSlim(1, 1);
            var owner = new StartupLibraryInitializationWorkflowOwner(databaseScope, new BmsDatabaseLoadOptions(true, true, true), gate);
            Task<LeapYearFolderRepairApproval> operation = owner.PrepareLeapYearFolderRepairAsync(settings.LR2SongDBPath, StartupSettingsSnapshot.CreateCurrent(settings).LR2RootPath, dialogs, () => shutdown);
            if (resultKind is 0 or 1) { await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => operation); }
            else if (resultKind == 4) { Assert.AreSame(original, await Assert.ThrowsExactlyAsync<IOException>(() => operation)); }
            else
            {
                InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => operation);
                if (resultKind == 2) { Assert.AreSame(original, failure.InnerException); }
                else { StringAssert.Contains(failure.Message, nameof(UiDialogStatus.OwnerUnavailable)); }
            }
            Assert.AreEqual(1, dialogs.ConfirmationCount);
            Assert.AreEqual(0, dialogs.MessageCount);
            Assert.AreEqual(sentinel, Directory.GetLastWriteTime(folder));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), settings.LR2SongDBPath).OpenSongDbReadOnly();
            LR2SongDB.folder row = verify.Table<LR2SongDB.folder>().Single();
            Assert.AreEqual(0, row.adddate);
            Assert.IsNull(row.date);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>修復後の通知は表示失敗を局所処理し、終了要求がある場合だけ取消しを伝播します。通知結果だけで終了したことにはしません。</summary>
    [TestMethod]
    // warningは修復警告/修復失敗を区別。0:AppClosing、1:Failed、2:owner不在、3:null、4:元例外throw、5:OK、6:終了要求のないAppClosing。
    [DataRow(true, 0, true)]
    [DataRow(false, 0, true)]
    [DataRow(true, 1, false)]
    [DataRow(true, 1, true)]
    [DataRow(false, 2, false)]
    [DataRow(false, 2, true)]
    [DataRow(true, 3, false)]
    [DataRow(true, 3, true)]
    [DataRow(false, 4, false)]
    [DataRow(false, 4, true)]
    [DataRow(true, 5, true)]
    [DataRow(true, 6, false)]
    public async Task LeapYearNotification_OnlyShutdownCancelsAndDisplayFailuresStayLocal(bool warning, int resultKind, bool shutdownAfterDisplay)
    {
        var original = new IOException("actual repair notification failure");
        bool shutdown = false;
        var dialogs = new RecordingRootDialogService
        {
            MessageResult = resultKind switch
            {
                0 or 6 => UiDialogResult.NotShown(UiDialogStatus.AppClosing),
                1 => UiDialogResult.Failed(original),
                2 => UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable),
                3 => null,
                _ => UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            },
            MessageObserved = () =>
            {
                shutdown = shutdownAfterDisplay;
                if (resultKind == 4) { throw original; }
            }
        };
        using var gate = new SemaphoreSlim(1, 1);
        var owner = new StartupLibraryInitializationWorkflowOwner(databaseScope, new BmsDatabaseLoadOptions(true, true, true), gate);
        var notification = new LeapYearFolderRepairNotification(warning, warning ? [] : [("original folder", new IOException("timestamp repair failed"))]);
        Task operation = owner.PresentLeapYearFolderRepairAsync(notification, dialogs, () => shutdown);
        if (shutdownAfterDisplay) { await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => operation); }
        else { await operation; }
        Assert.AreEqual(1, dialogs.MessageCount);
        Assert.AreEqual(0, dialogs.ConfirmationCount);
    }
}
