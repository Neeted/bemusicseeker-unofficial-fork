using System.Configuration;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Xml.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Newtonsoft.Json.Linq;
using Ribbit.Media.Audio;
using static BeMusicSeeker.Tests.SettingsDialogHarness;

namespace BeMusicSeeker.Tests;

/// <summary>項目・record・BMSの確定値と局所候補を実媒体で検証します。</summary>
/// <remarks>実 XML／LR2 config の失敗と境界差替えを併用し、保存済み値と draft の分離を確認します。実 Window は presentation、実 mode 保存と終了の構成接続は Admission／Shell が分担します。取消・Reset・適用が共有 theme／culture を変更するケースは非並列です。</remarks>
[TestClass]
[TestCategory("Windows.Settings")]
[DoNotParallelize]
public sealed class SettingsDialogBehaviorTests
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

    /// <summary>未設定・不正な確定LR2パスでもモードだけを保存し、有効な詳細候補を便乗させません。実保存失敗後も次の明示入力を受け付け、別wrapperの確定値と未知キーを保ちます。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public void OperationModeWithoutActiveProfile_PreservesRawPathsAndAllowsExplicitSaveAfterFailure(bool malformedConfig)
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "mode-without-profile");
            Settings values = OpenPersistedSettings(directory.Path);
            string path = Path.Combine(directory.Path, "user.config");
            values.LR2RootPath = malformedConfig ? Path.Combine(directory.Path, "missing-root") : string.Empty;
            values.LR2SongDBPath = malformedConfig ? Path.Combine(directory.Path, "missing-song.db") : string.Empty;
            values.LR2ConfigXmlPath = malformedConfig ? Path.Combine(directory.Path, "malformed.xml") : string.Empty;
            if (malformedConfig) { File.WriteAllText(values.LR2ConfigXmlPath, "<config>"); }
            values.Save();
            var document = XDocument.Load(path);
            document.Descendants("setting").First().Parent!.Add(new XElement("setting",
                new XAttribute("name", "FutureModeKey"), new XAttribute("serializeAs", "String"), new XElement("value", "retain")));
            document.Save(path);
            var confirmed = EnvironmentSettingsChange.Capture(values);
            var failures = new List<Exception>();
            var dialogs = new RecordingDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            using var harness = SettingsDialogHarness.Create(values, reportApplyFailure: failures.Add, dialogs: dialogs);
            (string SongDbPath, string ConfigPath, string BmsRoot) draft = CreateValidLr2Layout(Path.Combine(directory.Path, "draft"));
            await harness.Dialog.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2SongDBPath), draft.SongDbPath);
            await harness.Dialog.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2ConfigXmlPath), draft.ConfigPath);
            harness.Dialog.EnableBeatorajaBmtOutput = true;
            Settings other = TestSettingsFactory.OpenSettings(path);
            other.Commit((nameof(Settings.PlayerMixerThreadCount), 3));
            byte[] before = File.ReadAllBytes(path);
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            { await harness.Dialog.SelectOperationModeAsync(true); }
            Assert.AreEqual(1, failures.Count);
            Assert.IsInstanceOfType<PortableSettingsException>(failures[0]);
            Assert.AreEqual(confirmed, EnvironmentSettingsChange.Capture(values));
            Assert.IsTrue(harness.Dialog.OperationModeLR2DB, "実保存失敗は今回選択を画面に残します。");
            CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.AreEqual(confirmed, EnvironmentSettingsChange.Capture(TestSettingsFactory.OpenSettings(path)), "保存先解放だけでは再実行しません。");
            await harness.Dialog.SelectOperationModeAsync(true);
            Settings saved = TestSettingsFactory.OpenSettings(path);
            Assert.AreEqual(confirmed with { OperationModeLR2DB = true }, EnvironmentSettingsChange.Capture(saved));
            Assert.AreEqual(3, saved.PlayerMixerThreadCount);
            Assert.IsFalse(saved.EnableBeatorajaBmtOutput);
            Assert.IsTrue(harness.Dialog.EnableBeatorajaBmtOutput, "無関係なBMT候補は保持します。");
            Assert.AreEqual("retain", XDocument.Load(path).Descendants("setting").Single(element => (string?)element.Attribute("name") == "FutureModeKey").Element("value")!.Value);
            Assert.AreEqual(1, dialogs.ConfirmationCount, "最初の詳細候補破棄だけを確認し、非稼働時に再起動確認は要求しません。");
            Assert.AreEqual(0, dialogs.MessageCount);
            Assert.AreEqual(0, harness.State.InitializeCount);
            Assert.AreEqual(0, harness.State.FileDiffReloadCount);
            Assert.AreEqual(0, harness.State.ScoreReloadCount);
            Assert.IsTrue(harness.Dialog.IsInitialEnvironment);
            Assert.IsTrue(harness.Dialog.IsEnvironmentSelectionEnabled);
            Assert.AreEqual(1, failures.Count);
            if (malformedConfig) { Assert.AreEqual("<config>", File.ReadAllText(confirmed.LR2ConfigXmlPath)); }
        }, nameof(OperationModeWithoutActiveProfile_PreservesRawPathsAndAllowsExplicitSaveAfterFailure));
    }

    /// <summary>最初の待機前から実処理・後片付け・失敗通知まで単一受付を保ち、競合delegateを開始・予約せず、独立同期保存と失敗後の明示再操作を許可します。Window接続はcompiled系列が分担します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void SettingsOperation_RejectsCompetingRequestsUntilCleanupAndFailureNotificationTerminate()
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "settings-operation-admission");
            Settings values = OpenPersistedSettings(directory.Path);
            using var harness = SettingsDialogHarness.Create(values);
            SettingsDialogViewModel dialog = harness.Dialog;
            var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var notice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var noticeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var expected = new IOException("operation failure");
            int started = 0;
            int competing = 0;
            int notifications = 0;
            Task operation = dialog.RunSettingsOperationAsync(async () =>
            {
                started++;
                Assert.IsFalse(dialog.IsSettingsOperationAvailable, "最初の非同期待機より先に受付を閉じます。");
                try { await work.Task; throw expected; }
                finally { cleanupEntered.TrySetResult(); await cleanup.Task; }
            }, async failure =>
            {
                Assert.AreSame(expected, failure);
                notifications++;
                noticeEntered.TrySetResult();
                await notice.Task;
            });
            Task terminal = dialog.SettingsOperationCompletionTask;
            async Task AssertRejected()
            {
                await dialog.RunSettingsOperationAsync(() => { competing++; return Task.CompletedTask; });
                Assert.IsFalse(dialog.IsSettingsOperationAvailable);
                Assert.IsFalse(dialog.IsEditCompletionEnabled);
                Assert.AreSame(terminal, dialog.SettingsOperationCompletionTask);
                Assert.IsFalse(terminal.IsCompleted);
                Assert.AreEqual(0, competing);
            }
            Exception? primaryFailure = null;
            try
            {
                await AssertRejected();
                float quality = values.EncoderQuality == 0.5f ? 0.6f : 0.5f;
                dialog.EncoderQuality = quality;
                Assert.AreEqual(quality, values.EncoderQuality);
                Assert.AreEqual(quality, TestSettingsFactory.OpenSettings(Path.Combine(directory.Path, "user.config")).EncoderQuality);
                work.TrySetResult();
                await cleanupEntered.Task;
                await AssertRejected();
                cleanup.TrySetResult();
                await noticeEntered.Task;
                await AssertRejected();
            }
            catch (Exception failure) { primaryFailure = failure; throw; }
            finally
            {
                work.TrySetResult(); cleanup.TrySetResult(); notice.TrySetResult();
                // 本体の失敗時も両Taskの終端を待ち、意図した操作失敗で元の表明失敗を置き換えません。
                try { await Task.WhenAll(operation, terminal); }
                catch when (primaryFailure != null) { }
                catch (IOException failure) when (ReferenceEquals(expected, failure)) { }
            }
            IOException actual = await Assert.ThrowsExactlyAsync<IOException>(() => operation);
            Assert.AreSame(expected, actual);
            await terminal;
            Assert.IsTrue(dialog.IsSettingsOperationAvailable);
            await dialog.RunSettingsOperationAsync(() => { competing++; return Task.CompletedTask; });
            Assert.AreEqual(1, competing, "拒否した要求は予約せず、新しい明示要求だけ一回受け付けます。");
            Assert.AreEqual(1, started);
            Assert.AreEqual(1, notifications);
        }, nameof(SettingsOperation_RejectsCompetingRequestsUntilCleanupAndFailureNotificationTerminate));
    }

    /// <summary>root確定は保存済みBMTとscore使用方針で両取得先を反映し、別BMT・score候補を採用せず破棄します。OFFのroot変更はscoreを再読込みせず、ONのroot/player変更は各一回です。物理出力・所有権は実媒体系列が分担します。</summary>
    [TestMethod]
    [DataRow(true, false, true, false, false)]
    [DataRow(false, true, true, true, false)]
    [DataRow(false, false, true, false, true)]
    [DataRow(true, false, false, true, true)]
    [DoNotParallelize]
    public void BeatorajaRootConfirmation_PassesSavedBmtPolicyWithoutDraftOptions(bool enabled, bool keepFiles, bool registerUrls, bool oldUseScore, bool draftUseScore)
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "beatoraja-saved-policy");
            string oldRoot = Path.Combine(directory.Path, "old");
            string newRoot = Path.Combine(directory.Path, "new");
            foreach (string root in new[] { oldRoot, newRoot })
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "beatoraja.jar"), string.Empty);
                string player = root == oldRoot ? "old-player" : "new-player";
                File.WriteAllText(Path.Combine(root, BeatorajaConfigService.ConfigFileName), System.Text.Json.JsonSerializer.Serialize(
                    new { tablepath = "tables", playerpath = "player", playername = player }));
                string playerPath = Directory.CreateDirectory(Path.Combine(root, "player", player)).FullName;
                File.WriteAllBytes(Path.Combine(playerPath, "score.db"), []);
            }
            Settings values = OpenPersistedSettings(directory.Path);
            values.BeatorajaRootPath = oldRoot;
            values.UseBeatorajaScoreDb = oldUseScore;
            values.BeatorajaPlayerId = "old-player";
            values.BeatorajaScoreDbPath = Path.Combine(oldRoot, "player", "old-player", "score.db");
            values.BeatorajaBmtTablePath = Path.Combine(oldRoot, "tables");
            values.EnableBeatorajaBmtOutput = enabled;
            values.KeepBeatorajaBmtFilesWhenOutputDisabled = keepFiles;
            values.RegisterBeatorajaBmtUrls = registerUrls;
            values.BeatorajaBmtHashOutputMode = "Original";
            values.Save();
            using var harness = SettingsDialogHarness.Create(values, activeLibraryProfile: true,
                reportApplyFailure: failure => Assert.Fail(failure.ToString()),
                dialogs: new RecordingDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) });
            int saves = 0;
            System.Configuration.SettingsSavingEventHandler saving = (_, _) => saves++;
            values.SettingsSaving += saving;
            try
            {
                harness.Dialog.BeatorajaRootPath = oldRoot.ToUpperInvariant();
                await harness.Dialog.CommitScoreSettingsAsync();
                Assert.AreEqual(oldRoot, harness.Dialog.BeatorajaRootPath);
                Assert.AreEqual(values.BeatorajaScoreDbPath, harness.Dialog.BeatorajaScoreDbPath);
                Assert.IsFalse(harness.Dialog.HasScoreSelectionChanges);
                Assert.AreEqual(0, saves);
                Assert.AreEqual(0, harness.State.ScoreReloadCount);
                Assert.AreEqual(0, harness.Workspace.BmtRequests.Count);
            }
            finally { values.SettingsSaving -= saving; }
            harness.Dialog.EnableBeatorajaBmtOutput = !enabled;
            harness.Dialog.KeepBeatorajaBmtFilesWhenOutputDisabled = !keepFiles;
            harness.Dialog.RegisterBeatorajaBmtUrls = !registerUrls;
            harness.Dialog.BeatorajaBmtHashOutputMode = "PreferSha256Only";
            harness.Dialog.UseBeatorajaScoreDb = draftUseScore;
            harness.Dialog.BeatorajaRootPath = newRoot;
            await harness.Dialog.CommitBeatorajaRootSettingsAsync();
            Assert.AreEqual(1, harness.Workspace.BmtRequests.Count);
            (BeatorajaBmtOptionsSnapshot Previous, BeatorajaBmtOptionsSnapshot Target) request = harness.Workspace.BmtRequests.Single();
            Assert.AreEqual(Path.Combine(oldRoot, "tables"), request.Previous.BeatorajaBmtTablePath);
            Assert.AreEqual(newRoot, request.Target.BeatorajaRootPath);
            Assert.AreEqual(Path.Combine(newRoot, "tables"), request.Target.BeatorajaBmtTablePath);
            Assert.AreEqual(enabled, request.Target.EnableBeatorajaBmtOutput);
            Assert.AreEqual(keepFiles, request.Target.KeepBeatorajaBmtFilesWhenOutputDisabled);
            Assert.AreEqual(registerUrls, request.Target.RegisterBeatorajaBmtUrls);
            Assert.AreEqual("Original", request.Target.BeatorajaBmtHashOutputMode);
            Settings saved = TestSettingsFactory.OpenSettings(Path.Combine(directory.Path, "user.config"));
            Assert.AreEqual(newRoot, saved.BeatorajaRootPath);
            Assert.AreEqual(enabled, saved.EnableBeatorajaBmtOutput);
            Assert.AreEqual(keepFiles, saved.KeepBeatorajaBmtFilesWhenOutputDisabled);
            Assert.AreEqual(registerUrls, saved.RegisterBeatorajaBmtUrls);
            Assert.AreEqual("new-player", saved.BeatorajaPlayerId);
            Assert.AreEqual(oldUseScore, saved.UseBeatorajaScoreDb);
            Assert.AreEqual(oldUseScore, harness.Dialog.UseBeatorajaScoreDb);
            Assert.IsFalse(harness.Dialog.CanCancelBmtSettings);
            Assert.AreEqual(0, harness.State.InitializeCount);
            Assert.AreEqual(oldUseScore ? 1 : 0, harness.State.ScoreReloadCount);
            if (oldUseScore)
            {
                string alternate = Directory.CreateDirectory(Path.Combine(newRoot, "player", "alternate")).FullName;
                File.WriteAllBytes(Path.Combine(alternate, "score.db"), []);
                harness.Dialog.BeatorajaPlayerId = "alternate";
                await harness.Dialog.CommitScoreSettingsAsync();
                Assert.AreEqual(2, harness.State.ScoreReloadCount, "ONの同root内player変更も追加のscore-only一回だけです。");
                Assert.AreEqual(1, harness.Workspace.BmtRequests.Count);
                Assert.AreEqual(0, harness.State.InitializeCount);
            }
        }, nameof(BeatorajaRootConfirmation_PassesSavedBmtPolicyWithoutDraftOptions));
    }

    /// <summary>score確定は確定rootのDBだけを保存し、別root・BMT候補を保持します。過去BMT失敗があってもscore失敗後の別入力を保存・読込みでき、BMT要求を再開しません。BMT確定は不正なscore候補に依存しません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void ScoreConfirmation_KeepsRootAndBmtCandidatesAndNeverRestartsBmt()
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "score-independent");
            Settings values = OpenPersistedSettings(directory.Path);
            string root = CreateBeatorajaRoot(directory.Path, "saved");
            string otherRoot = CreateBeatorajaRoot(directory.Path, "draft");
            values.BeatorajaRootPath = root;
            values.BeatorajaPlayerId = "player";
            values.BeatorajaScoreDbPath = Path.Combine(root, "player", "player", "score.db");
            values.BeatorajaBmtTablePath = Path.Combine(root, "table");
            values.Save();
            var failures = new List<Exception>();
            var dialogs = new RecordingDialogService();
            using var harness = SettingsDialogHarness.Create(values, activeLibraryProfile: true, reportApplyFailure: failures.Add, dialogs: dialogs);
            harness.Dialog.BeatorajaRootPath = otherRoot;
            harness.Dialog.EnableBeatorajaBmtOutput = true;
            harness.Workspace.ThrowOnBmt = true;
            await harness.Dialog.CommitBmtSettingsAsync();
            Assert.AreEqual(1, harness.Workspace.BmtRequests.Count);
            harness.Dialog.KeepBeatorajaBmtFilesWhenOutputDisabled = !values.KeepBeatorajaBmtFilesWhenOutputDisabled;
            harness.Dialog.UseBeatorajaScoreDb = true;
            harness.State.ThrowOnScoreReload = true;
            await harness.Dialog.CommitScoreSettingsAsync();
            Assert.IsTrue(harness.Dialog.IsScoreSelectionEnabled);
            Assert.AreEqual(root, values.BeatorajaRootPath);
            Assert.AreEqual(Path.Combine(root, "player", "player", "score.db"), values.BeatorajaScoreDbPath);
            Assert.AreEqual(otherRoot, harness.Dialog.BeatorajaRootPath);
            Assert.IsTrue(values.EnableBeatorajaBmtOutput);
            Assert.IsTrue(harness.Dialog.EnableBeatorajaBmtOutput);
            Assert.AreEqual(1, harness.Workspace.BmtRequests.Count);
            Assert.AreEqual(0, dialogs.ConfirmationCount);
            harness.State.ThrowOnScoreReload = false;
            harness.Dialog.UseBeatorajaScoreDb = false;
            await harness.Dialog.CommitScoreSettingsAsync();
            Settings fresh = TestSettingsFactory.OpenSettings(Path.Combine(directory.Path, "user.config"));
            Assert.IsFalse(fresh.UseBeatorajaScoreDb);
            Assert.AreEqual(root, fresh.BeatorajaRootPath);
            Assert.IsTrue(harness.Dialog.CanCancelBmtSettings);
            Assert.AreEqual(2, harness.State.ScoreReloadCount);
            byte[] savedScoreSettings = File.ReadAllBytes(Path.Combine(directory.Path, "user.config"));
            await harness.Dialog.CommitScoreSettingsAsync();
            Assert.AreEqual(2, harness.State.ScoreReloadCount, "同値score確定では再取得しません。");
            CollectionAssert.AreEqual(savedScoreSettings, File.ReadAllBytes(Path.Combine(directory.Path, "user.config")));
            Assert.AreEqual(1, harness.Workspace.BmtRequests.Count);
            harness.Workspace.ThrowOnBmt = false;
            harness.Dialog.UseBeatorajaScoreDb = true;
            harness.Dialog.BeatorajaPlayerId = "missing-score";
            Assert.IsFalse(harness.Dialog.IsScoreSelectionValid);
            await harness.Dialog.CommitBmtSettingsAsync();
            Assert.AreEqual(root, harness.Workspace.BmtRequests.Last().Target.BeatorajaRootPath);
            Assert.AreEqual("missing-score", harness.Dialog.BeatorajaPlayerId);
            Assert.AreEqual("player", values.BeatorajaPlayerId);
            Assert.AreEqual(otherRoot, harness.Dialog.BeatorajaRootPath);
            Assert.AreEqual(0, dialogs.ConfirmationCount);
            Assert.AreEqual(2, failures.Count);
            // 確定済みscoreが外部操作で不備となった場合も、BMTだけの明示更新を受け付けます。
            values.UseBeatorajaScoreDb = true;
            values.Save();
            File.Delete(values.BeatorajaScoreDbPath);
            int scoreCalls = harness.State.ScoreReloadCount;
            await harness.Dialog.CommitBmtSettingsAsync();
            Assert.AreEqual(scoreCalls, harness.State.ScoreReloadCount);
            Assert.AreEqual(3, harness.Workspace.BmtRequests.Count);
            Assert.AreEqual("missing-score", harness.Dialog.BeatorajaPlayerId);
            Assert.AreEqual(otherRoot, harness.Dialog.BeatorajaRootPath);
            Assert.IsTrue(TestSettingsFactory.OpenSettings(Path.Combine(directory.Path, "user.config")).UseBeatorajaScoreDb);
            Assert.AreEqual(2, failures.Count);
        }, nameof(ScoreConfirmation_KeepsRootAndBmtCandidatesAndNeverRestartsBmt));
    }

    /// <summary>BMT/scoreの過去失敗後も通常LR2確定へ進み、確認中に要求した別の設定実操作だけを拒否します。独立score保存と無関係な同期保存も維持します。</summary>
    [TestMethod]
    [DataRow("mode", true, false)]
    [DataRow("root", true, true)]
    [DataRow("details", false, false)]
    [DataRow("mode", false, true)]
    [DoNotParallelize]
    public void EnvironmentRestart_AllowsPreviousBeatorajaFailureAndRejectsConcurrentSettings(string route, bool bmtFails, bool duringConfirmation)
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "restart-repair-lifetime");
            Settings values = OpenPersistedSettings(directory.Path);
            string oldRoot = CreateBeatorajaRoot(directory.Path, "old");
            string next = CreateBeatorajaRoot(directory.Path, "next");
            (string SongDbPath, string ConfigPath, string BmsRoot) lr2 = CreateValidLr2Layout(Path.Combine(directory.Path, "lr2"));
            (string SongDbPath, string ConfigPath, string BmsRoot) otherLr2 = CreateValidLr2Layout(Path.Combine(directory.Path, "other-lr2"));
            File.WriteAllBytes(Path.Combine(directory.Path, "other-lr2", "LR2body.exe"), []);
            values.LR2RootPath = Path.Combine(directory.Path, "lr2");
            values.LR2SongDBPath = lr2.SongDbPath;
            values.LR2ConfigXmlPath = lr2.ConfigPath;
            values.BeatorajaRootPath = oldRoot;
            values.BeatorajaPlayerId = "player";
            values.BeatorajaScoreDbPath = Path.Combine(oldRoot, "player", "player", "score.db");
            values.BeatorajaBmtTablePath = Path.Combine(oldRoot, "table");
            values.UseBeatorajaScoreDb = true;
            values.Save();
            int restarts = 0;
            var failures = new List<Exception>();
            var dialogs = new RecordingDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK),
                MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            using var harness = SettingsDialogHarness.Create(values, activeLibraryProfile: true, dialogs: dialogs,
                reportApplyFailure: failures.Add, restart: request =>
                {
                    request.BeforeSave?.Invoke();
                    request.Environment.Commit(values);
                    restarts++;
                    return Task.FromResult(true);
                });
            string draft = Directory.CreateDirectory(Path.Combine(directory.Path, "draft-bms")).FullName;
            if (route == "details") { await harness.Dialog.SelectLr2DetailPathAsync(nameof(SettingsDialogViewModel.LR2SongDBPath), otherLr2.SongDbPath); }
            else { await harness.Dialog.AddBmsSearchRootPathsAsync([draft]); }
            async Task CreateRepair()
            {
                harness.Workspace.ThrowOnBmt = bmtFails;
                harness.State.ThrowOnScoreReload = !bmtFails;
                await harness.Dialog.CommitBeatorajaRootSettingsAsync(next);
                if (bmtFails)
                {
                    await harness.Dialog.CommitScoreSettingsAsync();
                    harness.Dialog.UseBeatorajaScoreDb = false;
                    await harness.Dialog.CommitScoreSettingsAsync();
                    Assert.IsFalse(values.UseBeatorajaScoreDb, "BMT未了でも独立したscore使用切替は保存できます。");
                    Assert.AreEqual(1, harness.Workspace.BmtRequests.Count);
                }
                else { Assert.IsTrue(harness.Dialog.IsScoreSelectionEnabled); }
            }
            Task ChangeEnvironment() => route switch
            {
                "mode" => harness.Dialog.SelectOperationModeAsync(true),
                "root" => harness.Dialog.SelectLr2RootAsync(Path.Combine(directory.Path, "other-lr2")),
                _ => harness.Dialog.CommitEnvironmentAsync()
            };
            if (duringConfirmation)
            {
                dialogs.ConfirmationObservedAsync = async () =>
                {
                    Task original = harness.Dialog.SettingsOperationCompletionTask;
                    await harness.Dialog.CommitBeatorajaRootSettingsAsync(next);
                    await harness.Dialog.CommitScoreSettingsAsync();
                    Assert.AreEqual(oldRoot, values.BeatorajaRootPath);
                    Assert.AreEqual(0, harness.Workspace.BmtRequests.Count);
                    Assert.AreEqual(0, harness.State.ScoreReloadCount);
                    Assert.AreSame(original, harness.Dialog.SettingsOperationCompletionTask);
                    Assert.IsFalse(original.IsCompleted);
                };
            }
            else { await CreateRepair(); }
            await ChangeEnvironment();
            dialogs.ConfirmationObservedAsync = null;
            if (duringConfirmation)
            {
                Assert.AreEqual(1, restarts, "競合操作を予約せず、先に受理した環境変更だけを完了します。");
                Assert.AreEqual(oldRoot, values.BeatorajaRootPath);
                Assert.AreEqual(0, failures.Count);
                return;
            }
            Assert.AreEqual(next, values.BeatorajaRootPath);
            Assert.AreEqual(1, restarts, "過去のBMT/score失敗だけでは通常の環境確定を拒否しません。");
            Assert.AreEqual(route == "mode" ? lr2.SongDbPath : otherLr2.SongDbPath, values.LR2SongDBPath);
            Assert.AreEqual(1, failures.Count, string.Join(Environment.NewLine, failures));
            bool ordinaryValue = !values.ShowNewPackageInstallConfirmMsg;
            harness.Dialog.ShowNewPackageInstallConfirmMsg = ordinaryValue;
            Assert.AreEqual(ordinaryValue, TestSettingsFactory.OpenSettings(Path.Combine(directory.Path, "user.config")).ShowNewPackageInstallConfirmMsg);
        }, nameof(EnvironmentRestart_AllowsPreviousBeatorajaFailureAndRejectsConcurrentSettings));
    }

    /// <summary>root採用後のURL/整理失敗でも同じ受理Taskでscoreと必要表示を回収します。score/必須UIの二次失敗がBMT元原因を消す誤りと、親受付の早期解放を検出します。</summary>
    [TestMethod]
    [DataRow("URL", "success")]
    [DataRow("cleanup", "success")]
    [DataRow("URL", "score-failure")]
    [DataRow("URL", "ui-failure")]
    [DoNotParallelize]
    public void BeatorajaRootConfirmation_BmtFailureStillAwaitsScoresAndRequiredUi(string bmtStage, string secondary)
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "root-bmt-score-terminal");
            Settings values = OpenPersistedSettings(directory.Path);
            string oldRoot = CreateBeatorajaRoot(directory.Path, "old");
            string newRoot = CreateBeatorajaRoot(directory.Path, "next");
            values.BeatorajaRootPath = oldRoot;
            values.BeatorajaBmtTablePath = Path.Combine(oldRoot, "table");
            values.BeatorajaPlayerId = "player";
            values.BeatorajaScoreDbPath = Path.Combine(oldRoot, "player", "player", "score.db");
            values.UseBeatorajaScoreDb = true;
            values.Save();
            var failures = new List<Exception>();
            var composition = new ApplicationComposition(runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(), settingsEditSession: new SettingsEditSession(values),
                reportSettingsApplyFailure: failures.Add, uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                dialogService: UndeclaredDialogs.Create());
            using var harness = SettingsDialogHarness.Create(values, activeLibraryProfile: true, composition: composition);
            harness.Workspace.CompletionFailure = new IOException("BMT " + bmtStage + " target-A");
            var scoreEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var scoreRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var uiEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var uiRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.State.ScoreReloadHandler = async () =>
            {
                scoreEntered.TrySetResult();
                await scoreRelease.Task;
                if (secondary == "score-failure") { throw new IOException("score secondary failure"); }
            };
            harness.State.CompletionHandler = async result =>
            {
                uiEntered.TrySetResult();
                await uiRelease.Task;
                if (secondary == "ui-failure") { throw new IOException("UI secondary failure"); }
                return result with { CompletionPublished = true };
            };
            Task operation = harness.Dialog.CommitBeatorajaRootSettingsAsync(newRoot);
            try
            {
                await TestUiDispatcherHost.AwaitNotificationAsync(scoreEntered.Task, operation, "root score after BMT failure");
                Assert.AreEqual(newRoot, TestSettingsFactory.OpenSettings(Path.Combine(directory.Path, "user.config")).BeatorajaRootPath);
                Assert.IsFalse(operation.IsCompleted);
                Assert.IsTrue(composition.OperationAdmission.IsActive);
                Assert.IsFalse(harness.Output.CanBeginOutputOperation, "設定操作が実際に借用するPを保持します。");
                await harness.Dialog.CommitBmtSettingsAsync();
                Assert.AreEqual(1, harness.Workspace.BmtRequests.Count);
                scoreRelease.TrySetResult();
                if (secondary != "score-failure")
                {
                    await TestUiDispatcherHost.AwaitNotificationAsync(uiEntered.Task, operation, "root required UI after BMT failure");
                    Assert.IsFalse(operation.IsCompleted);
                    Assert.IsFalse(composition.OperationAdmission.IsActive);
                    Assert.IsTrue(harness.Output.CanBeginOutputOperation);
                    Assert.IsFalse(harness.Dialog.IsSettingsOperationAvailable);
                }
            }
            finally { scoreRelease.TrySetResult(); uiRelease.TrySetResult(); await operation; }
            Assert.AreEqual(1, harness.State.ScoreReloadCount);
            Assert.AreEqual(1, failures.Count);
            StringAssert.Contains(failures[0].ToString(), "BMT " + bmtStage + " target-A");
            if (secondary != "success") { StringAssert.Contains(failures[0].ToString(), secondary == "score-failure" ? "score secondary failure" : "UI secondary failure"); }
            Assert.IsTrue(harness.Dialog.IsBeatorajaSelectionEnabled);
        }, nameof(BeatorajaRootConfirmation_BmtFailureStillAwaitsScoresAndRequiredUi));
    }

    private static string CreateBeatorajaRoot(string parent, string name)
    {
        string root = Directory.CreateDirectory(Path.Combine(parent, name)).FullName;
        File.WriteAllText(Path.Combine(root, "beatoraja.jar"), string.Empty);
        File.WriteAllText(Path.Combine(root, BeatorajaConfigService.ConfigFileName), "{}");
        string player = Directory.CreateDirectory(Path.Combine(root, "player", "player")).FullName;
        File.WriteAllBytes(Path.Combine(player, "score.db"), []);
        return root;
    }

    /// <summary>音声候補だけの確定が実XMLと次回gateway入力を更新し、標準compositionの現再生Task・曲・playerと別セクション候補を維持します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void AudioConfirmationKeepsCurrentPlaybackAndPublishesOnlyNextOutputRequest()
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "audio-confirmation");
            Settings values = OpenPersistedSettings(directory.Path);
            string path = Path.Combine(directory.Path, "song.bms");
            File.WriteAllText(path, "#TITLE test");
            var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var player = new PreloadBmsPlayer { Completion = playing.Task };
            var composition = new ApplicationComposition(runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(), settingsEditSession: new SettingsEditSession(values),
                defaultBmsPlayerFactory: () => player, audioDeviceCatalog: new TestAudioDeviceCatalog(),
                reportSettingsApplyFailure: exception => Assert.Fail(exception.ToString()),
                uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                dialogService: UndeclaredDialogs.Create());
            MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
            try
            {
                owner.MainChartList.Rows = new List<object> { ChartTestValues.Empty() with { Path = path } };
                await owner.PlaybackPanel.StartAtIndex(0);
                ChartFile? current = owner.PlaybackPanel.NowPlayingChart;
                Assert.IsNotNull(current);
                owner.SettingDialog.PlayerSampleRate = SampleRate.SAMPLE_RATE_48000Hz;
                owner.SettingDialog.PlayerFormat = SampleFormat.SAMPLE_FLOAT_32BIT;
                owner.SettingDialog.PlayerBufferSize = 25;
                owner.SettingDialog.UseExternalPanelImage = true;
                Settings fresh = TestSettingsFactory.OpenSettings(Path.Combine(directory.Path, "user.config"));
                Assert.AreEqual(SampleRate.SAMPLE_RATE_48000Hz, fresh.PlayerSampleRate);
                Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, fresh.PlayerFormat);
                Assert.AreEqual(25, fresh.PlayerBufferSize);
                Assert.IsFalse(fresh.UseExternalPanelImage);
                PlayerSettingsSnapshot next = new SettingsPlayerSettingsGateway(() => values).CaptureSnapshot();
                Assert.AreEqual(fresh.PlayerSampleRate, next.PlayerSampleRate);
                Assert.AreEqual(fresh.PlayerFormat, next.PlayerFormat);
                Assert.AreEqual(fresh.PlayerBufferSize, next.PlayerBufferSize);
                Assert.AreSame(current, owner.PlaybackPanel.NowPlayingChart);
                Assert.IsTrue(owner.PlaybackPanel.IsPlaying);
                Assert.IsFalse(playing.Task.IsCompleted);
                Assert.AreEqual(0, player.CloseProcessCount);
                Assert.AreEqual(1, player.Starts.Count);
            }
            finally
            {
                playing.TrySetResult();
                await owner.ShellShutdownWorkflow.RequestWindowCloseAsync();
                owner.SettingDialog.Dispose();
            }
        }, nameof(AudioConfirmationKeepsCurrentPlaybackAndPublishesOnlyNextOutputRequest));
    }

    /// <summary>実player選択の自動保存は現在曲・操作能力・実効面を維持し、次回開始で実BMIIDXViewへ置換した通知がcompiled controlと実効面を変えます。</summary>
    /// <remarks>実BMIIDXViewの曲開始まで通し、processとwindow操作は既存gatewayで代替します。実Viewはcompiled controlと実効面の通知を担当します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void CompiledPlayerSelection_AutomaticallySavesAndDefersFactoryUntilNextPlayback()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "player-auto-confirmation");
            Settings values = OpenPersistedSettings(directory.Path);
            values.BMIIDXViewPath = Path.Combine(directory.Path, "BMIIDXView2015.exe");
            File.WriteAllBytes(values.BMIIDXViewPath, []);
            values.PlayerPanelState = PlayerPanelState.TITLE_LARGE | PlayerPanelState.BMS_PLAYER;
            values.UseExternalPanelImage = false;
            values.Save();
            string[] paths = [Path.Combine(directory.Path, "first.bms"), Path.Combine(directory.Path, "next.bms")];
            foreach (string path in paths) { File.WriteAllText(path, "#TITLE test"); }
            var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var currentPlayer = new PreloadBmsPlayer { Completion = playing.Task };
            var composition = new ApplicationComposition(runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(), settingsEditSession: new SettingsEditSession(values),
                defaultBmsPlayerFactory: () => currentPlayer, audioDeviceCatalog: new TestAudioDeviceCatalog(),
                reportSettingsApplyFailure: exception => Assert.Fail(exception.ToString()),
                uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                dialogService: UndeclaredDialogs.Create());
            MainWindowViewModel owner = composition.CreateMainWindowViewModelForTest();
            var processGateway = new RecordingExternalPlayerProcessGateway();
            processGateway.Session.KeepRunning = true;
            processGateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(21));
            var factory = new TestSettingsDialogPlayerFactoryPort
            {
                ConfiguredPlayerFactory = snapshot => new BMIIDXView2015(snapshot.BMIIDXViewPath,
                    new RecordingPlayerSettingsGateway(),
                    processGateway)
            };
            using var harness = SettingsDialogHarness.Create(values, activeLibraryProfile: true, composition: composition,
                playerFactory: factory, playbackRuntime: owner.PlaybackPanel);
            SettingsWindow? window = null;
            var playbackView = new PlaybackPanelView { DataContext = owner.PlaybackPanel };
            var playbackWindow = new Window { Content = playbackView, Width = 640d, Height = 360d, ShowInTaskbar = false };
            ContextMenu? menu = null;
            IEnumerable<DependencyObject> Descendants(DependencyObject item)
            {
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(item); index++)
                {
                    DependencyObject child = VisualTreeHelper.GetChild(item, index);
                    yield return child;
                    foreach (DependencyObject descendant in Descendants(child)) { yield return descendant; }
                }
            }
            try
            {
                owner.MainChartList.Rows = paths.Select(path => (object)(ChartTestValues.Empty() with { Path = path })).ToList();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.PlaybackPanel.StartAtIndex(0), "player-auto-initial-playback");
                ChartFile? current = owner.PlaybackPanel.NowPlayingChart;
                scope.ShowAndWaitForContentRendered(playbackWindow);
                DropDownMenuButton dropdown = Descendants(playbackView).OfType<DropDownMenuButton>().Single();
                menu = dropdown.DropDownContextMenu;
                menu.PlacementTarget = dropdown;
                menu.IsOpen = true;
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Button highSpeed = Descendants(menu).OfType<Button>().Single(button => Equals(button.Content, "HS UP"));
                Assert.IsFalse(highSpeed.IsEnabled);
                Assert.AreEqual(PlayerPanelState.TITLE_LARGE, playbackView.EffectivePlayerPanelState);
                window = new SettingsWindow(UndeclaredDialogs.Create()) { DataContext = harness.Dialog };
                scope.ShowAndWaitForContentRendered(window);
                ((ListBox)window.FindName("settingsNavigation")).SelectedItem = window.FindName("navigationPlayback");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var page = (BeMusicSeeker.Views.Settings.Pages.PlaybackSettingsPage)((ContentControl)window.FindName("settingsPageContent")).Content;
                ((RadioButton)page.FindName("radioButtonPlayBMIIDXView")).SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Assert.IsTrue(TestSettingsFactory.OpenSettings(Path.Combine(directory.Path, "user.config")).UsePlayerBMIIDXView);
                Assert.AreSame(current, owner.PlaybackPanel.NowPlayingChart);
                Assert.IsTrue(owner.PlaybackPanel.IsPlaying);
                Assert.AreEqual(0, currentPlayer.CloseProcessCount);
                Assert.IsNull(factory.LastConfiguredSettings);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsFalse(highSpeed.IsEnabled, "保存時点では現在playerの操作能力を維持します。");
                Assert.AreEqual(PlayerPanelState.TITLE_LARGE, playbackView.EffectivePlayerPanelState);
                owner.PlaybackPanel.AttachWindowHost(new RecordingExternalPlayerWindowHost(default));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.PlaybackPanel.StartAtIndex(1), "player-auto-next-playback");
                Assert.AreEqual(1, currentPlayer.CloseProcessCount);
                Assert.IsNotNull(factory.LastConfiguredSettings);
                Assert.IsTrue(factory.LastConfiguredSettings.UsePlayerBMIIDXView);
                Assert.IsTrue(processGateway.Session.Started);
                StringAssert.Contains(processGateway.LaunchRequest.Arguments, paths[1]);
                Assert.AreEqual(paths[1], owner.PlaybackPanel.NowPlayingChart?.Path);
                Assert.IsTrue(owner.PlaybackPanel.IsPlaying);
                Assert.AreEqual(values.BMIIDXViewPath, factory.LastConfiguredSettings.BMIIDXViewPath);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsTrue(highSpeed.IsEnabled, "実playerの置換完了通知でcompiled controlを更新します。");
                Assert.AreEqual(PlayerPanelState.TITLE_LARGE | PlayerPanelState.BMS_PLAYER, playbackView.EffectivePlayerPanelState);
            }
            finally
            {
                processGateway.Session.KeepRunning = false;
                playing.TrySetResult();
                if (menu != null) { menu.IsOpen = false; }
                window?.CloseForOwnerShutdown();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "player-auto-owner-terminal");
                owner.SettingDialog.Dispose();
                playbackView.DisposePlayerHost();
                playbackWindow.Content = null;
                playbackWindow.Close();
            }
        });
    }

    /// <summary>複数カテゴリの未確定候補を持った通常項目保存から、実XML・標準composition・次回音声要求へ候補が漏れないことを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void OrdinaryConfirmationDoesNotPublishOtherSectionCandidates()
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "confirmed-settings");
            Settings values = OpenPersistedSettings(directory.Path);
            values.PlayerDevice = "missing-device"; values.PlayerDeviceName = "Missing device"; values.Save();
            string configPath = Path.Combine(directory.Path, "user.config");
            var failures = new List<Exception>();
            var composition = new ApplicationComposition(runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(), settingsEditSession: new SettingsEditSession(values),
                reportSettingsApplyFailure: failures.Add, audioDeviceCatalog: new TestAudioDeviceCatalog(),
                uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                dialogService: UndeclaredDialogs.Create());
            using var harness = SettingsDialogHarness.Create(values, composition: composition);
            SampleRate oldRate = values.PlayerSampleRate;
            // 利用不能な出力先の組は、通常保存から確定しません。
            harness.Dialog.PlayerBufferSize = float.NaN;
            harness.Dialog.PlayerSampleRate = SampleRate.SAMPLE_RATE_48000Hz;
            harness.Dialog.EnableBeatorajaBmtOutput = true;
            harness.Dialog.UseExternalPanelImage = true;
            harness.Dialog.UsePlayeruBMplay = true;
            harness.Dialog.BeatorajaRootPath = Path.Combine(directory.Path, "unconfirmed");
            harness.Dialog.StartupSortCachePrewarmPriority = 3;
            harness.Dialog.RequireCompleteUsedResourcesForAutoInstall = true;
            Settings fresh = TestSettingsFactory.OpenSettings(configPath);
            Assert.AreEqual(3, fresh.StartupSortCachePrewarmPriority);
            Assert.IsTrue(fresh.RequireCompleteUsedResourcesForAutoInstall);
            Assert.IsFalse(fresh.EnableBeatorajaBmtOutput);
            Assert.IsFalse(fresh.UseExternalPanelImage);
            Assert.IsFalse(fresh.UsePlayeruBMplay);
            Assert.AreEqual(string.Empty, composition.StartupSettingsProvider().BeatorajaRootPath);
            Assert.IsFalse(composition.BeatorajaBmtOptionsProvider().EnableBeatorajaBmtOutput);
            Assert.IsTrue(composition.BmsLibraryOptionsProvider().RequireCompleteUsedResourcesForAutoInstall);
            Assert.AreEqual(oldRate, new SettingsPlayerSettingsGateway(() => values).CaptureSnapshot().PlayerSampleRate);
            Assert.AreEqual(0, harness.State.InitializeCount);
            Assert.AreEqual(0, harness.State.FileDiffReloadCount);
            Assert.AreEqual(0, failures.Count);
            await Task.CompletedTask;
        }, nameof(OrdinaryConfirmationDoesNotPublishOtherSectionCandidates));
    }

    /// <summary>外部encoderが利用不能でも保存済みMP3を表示し、別形式の拒否では確定値を保ち、明示WAVEだけを保存して他カテゴリの候補を保持します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void RecordingEncoderSelection_PreservesUnavailableSavedFormatAndCommitsExplicitWaveOnly()
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "recording-encoder");
            Settings values = OpenPersistedSettings(directory.Path);
            values.Encoder = EncoderType.MP3_LAME;
            values.Save();
            string configPath = Path.Combine(directory.Path, "user.config");
            byte[] savedMp3 = File.ReadAllBytes(configPath);
            var dialogs = new RecordingDialogService { MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
            using var harness = SettingsDialogHarness.Create(values, dialogs: dialogs, audioDeviceCatalog: new WaveOnlyAudioDeviceCatalog());
            var gateway = new SettingsAudioGateway(() => values);
            harness.Dialog.EnableBeatorajaBmtOutput = true;

            Assert.AreEqual((int)EncoderType.MP3_LAME, harness.Dialog.EncoderIndex, "利用不能でも表示は保存済み形式です。");
            Assert.AreEqual(EncoderType.MP3_LAME, SelectedChartAudioConversionSettingsSnapshot.CreateCurrent(gateway.CaptureEncodingSettings()).Encoder);
            CollectionAssert.AreEqual(savedMp3, File.ReadAllBytes(configPath), "初期化と読取りは保存しません。");

            harness.Dialog.EncoderIndex = (int)EncoderType.AAC_NERO;
            Assert.AreEqual(1, dialogs.MessageCount);
            Assert.AreEqual(string.Format(CultureInfo.CurrentCulture, Resources.Warn_EncoderExecutableNotFoundFormat, EncoderType.AAC_NERO.GetEncoderFileName()), dialogs.LastMessageText);
            Assert.AreEqual((int)EncoderType.MP3_LAME, harness.Dialog.EncoderIndex);
            Assert.AreEqual(EncoderType.MP3_LAME, values.Encoder);
            Assert.AreEqual(EncoderType.MP3_LAME, TestSettingsFactory.OpenSettings(configPath).Encoder);
            Assert.AreEqual(EncoderType.MP3_LAME, SelectedChartAudioConversionSettingsSnapshot.CreateCurrent(gateway.CaptureEncodingSettings()).Encoder);
            CollectionAssert.AreEqual(savedMp3, File.ReadAllBytes(configPath), "拒否した形式と暗黙のWAVEを保存しません。");

            harness.Dialog.EncoderIndex = (int)EncoderType.WAVE;
            Settings fresh = TestSettingsFactory.OpenSettings(configPath);
            Assert.AreEqual(EncoderType.WAVE, fresh.Encoder);
            Assert.AreEqual(EncoderType.WAVE, values.Encoder);
            Assert.AreEqual((int)EncoderType.WAVE, harness.Dialog.EncoderIndex);
            Assert.AreEqual(EncoderType.WAVE, SelectedChartAudioConversionSettingsSnapshot.CreateCurrent(gateway.CaptureEncodingSettings()).Encoder);
            Assert.IsFalse(fresh.EnableBeatorajaBmtOutput, "録音形式の保存へ別カテゴリの候補を便乗させません。");
            Assert.IsTrue(harness.Dialog.EnableBeatorajaBmtOutput, "別カテゴリの候補は画面で保持します。");
            Assert.AreEqual(1, dialogs.MessageCount);
            await Task.CompletedTask;
        }, nameof(RecordingEncoderSelection_PreservesUnavailableSavedFormatAndCommitsExplicitWaveOnly));
    }

    /// <summary>実ファイル置換失敗で当該項目の確定値と次回表示を旧値に保ち、別の成功項目と次の保存へ失敗候補を混ぜません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void OrdinarySaveFailureKeepsOnlyThatItemsPreviousValue()
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "ordinary-save-failure");
            Settings values = OpenPersistedSettings(directory.Path);
            string configPath = Path.Combine(directory.Path, "user.config");
            var failures = new List<Exception>();
            using var harness = SettingsDialogHarness.Create(values, reportApplyFailure: failures.Add);
            harness.Dialog.StartupSortCachePrewarmPriority = 3;
            bool previous = values.ShowDiffBMSInstallConfirmMsg;
            using (File.Open(configPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            { harness.Dialog.ShowDiffBMSInstallConfirmMsg = !previous; }
            Assert.AreEqual(1, failures.Count);
            Assert.AreEqual(previous, harness.Dialog.ShowDiffBMSInstallConfirmMsg);
            Assert.AreEqual(previous, values.ShowDiffBMSInstallConfirmMsg);
            harness.Dialog.ShowNewPackageInstallConfirmMsg = false;
            Settings fresh = TestSettingsFactory.OpenSettings(configPath);
            Assert.AreEqual(previous, fresh.ShowDiffBMSInstallConfirmMsg);
            Assert.AreEqual(3, fresh.StartupSortCachePrewarmPriority);
            Assert.IsFalse(fresh.ShowNewPackageInstallConfirmMsg);
            harness.Dialog.ShowDiffBMSInstallConfirmMsg = !previous;
            Assert.AreEqual(!previous, TestSettingsFactory.OpenSettings(configPath).ShowDiffBMSInstallConfirmMsg);
            await Task.CompletedTask;
        }, nameof(OrdinarySaveFailureKeepsOnlyThatItemsPreviousValue));
    }

    /// <summary>保存後の差分失敗を通知して終端し、次の別入力は保存から処理します。configロック下で旧保存を流用する再試行を検出します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void BmsConfirmation_FileDiffFailureAllowsLatestInputResave()
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "bms-runtime-resave");
            Settings values = OpenPersistedSettings(directory.Path);
            string configPath = Path.Combine(directory.Path, "user.config");
            var failures = new List<Exception>();
            using var harness = SettingsDialogHarness.Create(values, activeLibraryProfile: true, reportApplyFailure: failures.Add);
            string added = Directory.CreateDirectory(Path.Combine(directory.Path, "added")).FullName;
            string latest = Directory.CreateDirectory(Path.Combine(directory.Path, "latest")).FullName;
            var failure = new IOException("controlled settings continuation failure");
            harness.State.ReloadFileDiffHandler = () => Task.FromException(failure);
            await harness.Dialog.AddBmsSearchRootPathsAsync([added]);
            await harness.Dialog.CommitBmsRootsAsync();
            Assert.AreSame(failure, failures.Single());
            CollectionAssert.Contains(StandaloneBmsRootPathSettings.Deserialize(TestSettingsFactory.OpenSettings(configPath).StandaloneBmsRootPaths).ToArray(), added);
            CollectionAssert.Contains(harness.Dialog.ConfirmedBMSDirectories.ToArray(), added);
            harness.Dialog.ShowNewPackageInstallConfirmMsg = false;
            harness.State.ReloadFileDiffHandler = null;
            await harness.Dialog.AddBmsSearchRootPathsAsync([latest]);
            foreach (string root in harness.Dialog.AvailableBMSDirectories.Where(root => root != latest).ToArray())
            { await harness.Dialog.RemoveBmsSearchRootPathAsync(root); }
            harness.Dialog.ReplacementBmsInstallDir = latest;
            using (File.Open(configPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            { await harness.Dialog.CommitBmsRootsAsync(); }
            Assert.AreEqual(2, failures.Count, "前回の保存成立を流用せず、今回のconfig保存失敗を通知します。");
            Assert.AreEqual(1, harness.State.FileDiffReloadCount);
            CollectionAssert.DoesNotContain(harness.Dialog.ConfirmedBMSDirectories.ToArray(), latest);
            await harness.Dialog.CommitBmsRootsAsync();
            Assert.AreEqual(2, failures.Count, string.Join(Environment.NewLine, failures));
            Assert.AreEqual(2, harness.State.FileDiffReloadCount);
            CollectionAssert.AreEqual(new[] { latest }, harness.Dialog.ConfirmedBMSDirectories.ToArray());
            Assert.AreEqual(latest, TestSettingsFactory.OpenSettings(configPath).BMSInstallDir);
            Assert.IsFalse(TestSettingsFactory.OpenSettings(configPath).ShowNewPackageInstallConfirmMsg);
        }, nameof(BmsConfirmation_FileDiffFailureAllowsLatestInputResave));
    }

    /// <summary>無関係なライブラリ処理中も既存の画面編集可否に従ってBMS候補を追加・削除できます。実確定の拒否と、解放だけでは保存しない境界を確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void BmsCandidateEditing_DuringLibraryProcessingDoesNotAcquireOrSave()
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "bms-candidate-library-processing");
            Settings values = OpenPersistedSettings(directory.Path);
            string oldRoot = values.BMSInstallDir;
            string added = Directory.CreateDirectory(Path.Combine(directory.Path, "Added")).FullName;
            using var harness = SettingsDialogHarness.Create(values, activeLibraryProfile: true);
            byte[] before = File.ReadAllBytes(Path.Combine(directory.Path, "user.config"));
            harness.State.IsLibraryOperationInProgress = true;
            Assert.IsTrue(harness.Dialog.IsBmsSearchRootEditorEnabled);
            await harness.Dialog.AddBmsSearchRootPathsAsync([added]);
            await harness.Dialog.RemoveBmsSearchRootPathAsync(oldRoot);
            CollectionAssert.AreEqual(new[] { added }, harness.Dialog.AvailableBMSDirectories);
            harness.Dialog.ReplacementBmsInstallDir = added;
            await harness.Dialog.CommitBmsRootsAsync();
            CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(directory.Path, "user.config")));
            Assert.AreEqual(0, harness.State.FileDiffReloadCount);
            harness.State.IsLibraryOperationInProgress = false;
            await Task.CompletedTask;
            CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(directory.Path, "user.config")));
            Assert.AreEqual(0, harness.State.FileDiffReloadCount);
        }, nameof(BmsCandidateEditing_DuringLibraryProcessingDoesNotAcquireOrSave));
    }

    /// <summary>追加と解除の最終一覧を一度保存し、導入先を外す場合は明示代替を必要とします。成功後は候補を解消し、同値の明示確定でも再保存・必要再読込みを行います。明示代替なしの確定の拒否では通知がちょうど一件（導入先の選択を求める文言と見出し）出て、成功した確定では通知も確認も増えません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void BmsRootConfirmationUsesFinalListAndExplicitInstallReplacement()
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "bms-root-final");
            Settings values = OpenPersistedSettings(directory.Path);
            string oldRoot = values.BMSInstallDir;
            string newRoot = Directory.CreateDirectory(Path.Combine(directory.Path, "new-library")).FullName;
            string transient = Directory.CreateDirectory(Path.Combine(directory.Path, "transient")).FullName;
            var dialogs = new RecordingDialogService();
            using var harness = SettingsDialogHarness.Create(values, activeLibraryProfile: true, dialogs: dialogs);
            harness.Dialog.AddBmsSearchRootPathsAsync([newRoot, transient]).GetAwaiter().GetResult();
            harness.Dialog.RemoveBmsSearchRootPathAsync(transient).GetAwaiter().GetResult();
            await harness.Dialog.CommitBmsRootsAsync();
            int initialReloads = harness.State.FileDiffReloadCount;
            byte[] confirmed = File.ReadAllBytes(Path.Combine(directory.Path, "user.config"));
            harness.Dialog.RemoveBmsSearchRootPathAsync(oldRoot).GetAwaiter().GetResult();
            byte[] before = File.ReadAllBytes(Path.Combine(directory.Path, "user.config"));
            Assert.AreEqual(0, dialogs.MessageCount, "ここまでの確定と候補の編集は通知を出しません。");
            await harness.Dialog.CommitBmsRootsAsync();
            CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(directory.Path, "user.config")));
            Assert.AreEqual(initialReloads, harness.State.FileDiffReloadCount);
            // 導入先を外した一覧は明示代替がないため、保存せず導入先の選択を求める通知を一件だけ出します。
            Assert.AreEqual(1, dialogs.MessageCount);
            Assert.AreEqual(Resources.Error_InvalidBmsInstallDir, dialogs.LastMessageText);
            Assert.AreEqual(Resources.Error, dialogs.LastMessageCaption);
            harness.Dialog.ReplacementBmsInstallDir = newRoot;
            CollectionAssert.AreEqual(confirmed, File.ReadAllBytes(Path.Combine(directory.Path, "user.config")));
            Assert.AreEqual(oldRoot, values.BMSInstallDir);
            Assert.AreEqual(string.Empty, harness.Dialog.ReplacementBmsInstallDirValidationMessage);
            await harness.Dialog.RemoveBmsSearchRootPathAsync(newRoot);
            Assert.AreEqual(Resources.Settings_replacement_install_empty, harness.Dialog.ReplacementBmsInstallDirValidationMessage);
            Assert.IsFalse(harness.Dialog.CanConfirmBmsRoots);
            harness.Dialog.CancelBmsRoots();
            CollectionAssert.AreEquivalent(new[] { oldRoot, newRoot }, harness.Dialog.AvailableBMSDirectories.ToArray());
            Assert.IsFalse(harness.Dialog.RequiresReplacementBmsInstallDir);
            await harness.Dialog.RemoveBmsSearchRootPathAsync(newRoot);
            Assert.IsFalse(harness.Dialog.RequiresReplacementBmsInstallDir);
            harness.Dialog.CancelBmsRoots();
            await harness.Dialog.RemoveBmsSearchRootPathAsync(oldRoot);
            harness.Dialog.ReplacementBmsInstallDir = newRoot;
            await harness.Dialog.CommitBmsRootsAsync();
            Settings fresh = TestSettingsFactory.OpenSettings(Path.Combine(directory.Path, "user.config"));
            Assert.AreEqual(newRoot, fresh.BMSInstallDir);
            CollectionAssert.AreEqual(new[] { newRoot }, StandaloneBmsRootPathSettings.Deserialize(fresh.StandaloneBmsRootPaths).ToArray());
            Assert.AreEqual(initialReloads + 1, harness.State.FileDiffReloadCount);
            // 差分要求へ渡す最終範囲は確定後の一覧だけです。旧rootと途中で外した候補を含めません。
            CollectionAssert.AreEqual(new[] { newRoot }, harness.State.LastSearchRoots!.Roots.ToArray());
            Assert.IsTrue(harness.Dialog.CanConfirmBmsRoots);
            Assert.IsFalse(harness.Dialog.CanCancelBmsRoots);
            await harness.Dialog.CommitBmsRootsAsync();
            Assert.AreEqual(initialReloads + 2, harness.State.FileDiffReloadCount);
            Assert.AreEqual(1, dialogs.MessageCount, "成功した確定は追加の通知を出しません。");
            Assert.AreEqual(0, dialogs.ConfirmationCount);
            InstallServiceComponentTestSupport.WithTemporarySongDb((songDbPath, installScope) =>
            {
                string source = Directory.CreateDirectory(Path.Combine(installScope, "Package")).FullName;
                string chartPath = Path.Combine(source, "chart.bms");
                File.WriteAllText(chartPath, "#TITLE ReplacementDestination\n");
                var package = ChartPackage.FromChartEntries([PackageChartEntry.FromPath(chartPath)]); package.path = source;
                var library = new TestBmsLibrary(new ApplicationDatabaseScope(), songDbPath, null, null, new InstallServiceComponentTestSupport.RealFileMutationService(),
                    new FileDbReportRecordingDialogs(), new TestUiScheduler(() => null), () => BmsLibraryOptionsSnapshot.CreateCurrent(fresh), settings: fresh)
                { BmsCharts = [], BmsonCharts = [], ChartPackagesPending = [package], ChartPackagesInstalled = [] };
                try
                {
                    library.ForceInstallPendingPackages([package], approveNormalInstallOverride: true, notices: new LibraryOperationNotices());
                    ChartFile installed = library.BmsCharts.Single();
                    Assert.IsTrue(installed.Path.StartsWith(newRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                    Assert.IsTrue(File.Exists(installed.Path));
                    Assert.IsFalse(installed.Path.StartsWith(oldRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                }
                finally { library.RequestShutdown("settings-replacement-install"); }
            });
        }, nameof(BmsRootConfirmationUsesFinalListAndExplicitInstallReplacement));
    }

    /// <summary>既存workspace準備待ち中の閉鎖・再表示が今回のBMS一覧と明示代替導入先を失わせず、捕捉入力だけを保存します。成功後の同値明示確定も新要求として再保存し、無関係な候補を保ちます。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void BmsConfirmation_CapturesRootsAndReplacementBeforeCloseDuringPreparation()
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "bms-close-confirmation");
            Settings values = OpenPersistedSettings(directory.Path);
            string original = values.BMSInstallDir;
            string target = Directory.CreateDirectory(Path.Combine(directory.Path, "target")).FullName;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failures = new List<Exception>();
            using var harness = SettingsDialogHarness.Create(values, activeLibraryProfile: true, reportApplyFailure: failures.Add);
            harness.Workspace.BeforeOperationAsync = () => { entered.TrySetResult(); return release.Task; };
            SettingsDialogViewModel dialog = harness.Dialog;
            dialog.SetPresentationActive(true);
            await dialog.AddBmsSearchRootPathsAsync([target]);
            await dialog.RemoveBmsSearchRootPathAsync(original);
            dialog.ReplacementBmsInstallDir = target;
            dialog.EnableBeatorajaBmtOutput = true;
            Task operation = dialog.CommitBmsRootsAsync();
            try
            {
                await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, operation, "settings preparation entered");
                Task terminal = dialog.SettingsOperationCompletionTask;
                Assert.IsTrue(dialog.IsEditCancellationEnabled);
                dialog.CloseCommand.Execute();
                dialog.SetPresentationActive(false);
                dialog.SetPresentationActive(true);
                CollectionAssert.AreEqual(new[] { original }, dialog.AvailableBMSDirectories.ToArray());
                Assert.IsNull(dialog.ReplacementBmsInstallDir);
                Assert.IsFalse(dialog.EnableBeatorajaBmtOutput);
                Assert.AreSame(terminal, dialog.SettingsOperationCompletionTask);
                Assert.IsFalse(terminal.IsCompleted);
                dialog.StartupSortCachePrewarmPriority = 3;
                dialog.UseExternalPanelImage = true;
                Assert.AreEqual(0, harness.State.FileDiffReloadCount);
                release.TrySetResult();
                await operation;
                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
                Assert.AreEqual(target, values.BMSInstallDir);
                CollectionAssert.AreEqual(new[] { target }, StandaloneBmsRootPathSettings.Deserialize(values.StandaloneBmsRootPaths).ToArray());
                Assert.AreEqual(1, harness.State.FileDiffReloadCount);
                Assert.AreEqual(3, TestSettingsFactory.OpenSettings(Path.Combine(directory.Path, "user.config")).StartupSortCachePrewarmPriority);
                CollectionAssert.AreEqual(new[] { target }, dialog.AvailableBMSDirectories.ToArray(), "保存前に再表示しても、保存成功後の候補は最新確定一覧に揃います。");
                Assert.IsFalse(dialog.CanCancelBmsRoots);
                Assert.IsTrue(dialog.CanConfirmBmsRoots);
                Assert.IsTrue(dialog.UseExternalPanelImage, "保存対象外の未成立画像候補を全初期化しません。");
                await dialog.CommitBmsRootsAsync();
                Assert.AreEqual(2, harness.State.FileDiffReloadCount, "明示再確定は最新の捕捉一覧で再保存し、旧候補へ戻しません。");
            }
            finally { release.TrySetResult(); await operation; }
        }, nameof(BmsConfirmation_CapturesRootsAndReplacementBeforeCloseDuringPreparation));
    }

    /// <summary>保存前の準備中に通常閉鎖・再表示した各欄は、捕捉入力の保存採用時に自分の候補だけを確定値へ揃えます。後段失敗でも旧候補へ戻さず、無関係な未成立画像候補を保持します。</summary>
    [TestMethod]
    [DataRow("score", false)]
    [DataRow("score", true)]
    [DataRow("bmt", false)]
    [DataRow("bmt", true)]
    [DataRow("root", false)]
    [DataRow("output", false)]
    [DoNotParallelize]
    public void SectionConfirmation_PreparationReopenAdoptsOnlySavedSection(string section, bool failContinuation)
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "section-preparation-reopen");
            Settings values = OpenPersistedSettings(directory.Path);
            string root = CreateBeatorajaRoot(directory.Path, "saved");
            string nextRoot = CreateBeatorajaRoot(directory.Path, "next");
            string nextScore = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "player", "next-player")).FullName, "score.db");
            File.WriteAllBytes(nextScore, []);
            values.BeatorajaRootPath = root;
            values.BeatorajaPlayerId = "player";
            values.BeatorajaScoreDbPath = Path.Combine(root, "player", "player", "score.db");
            values.BeatorajaBmtTablePath = Path.Combine(root, "table");
            values.KeepBeatorajaBmtFilesWhenOutputDisabled = false;
            values.LR2CustomFolderOutputBaseDir = Directory.CreateDirectory(Path.Combine(directory.Path, "output")).FullName;
            values.LR2CustomFolderOutputBaseDirRootType = Directory.CreateDirectory(Path.Combine(directory.Path, "root-output")).FullName;
            values.LR2CustomFolderAdditionalOutputBaseDirs = string.Empty;
            values.Save();
            string nextOutput = Directory.CreateDirectory(Path.Combine(directory.Path, "next-output")).FullName;
            var failures = new List<Exception>();
            using var harness = SettingsDialogHarness.Create(values, activeLibraryProfile: true, reportApplyFailure: failures.Add);
            SettingsDialogViewModel dialog = harness.Dialog;
            dialog.SetPresentationActive(true);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Workspace.BeforeOperationAsync = () => { entered.TrySetResult(); return release.Task; };
            harness.State.ThrowOnScoreReload = failContinuation && section == "score";
            harness.Workspace.ThrowOnBmt = failContinuation && section == "bmt";
            System.Globalization.CultureInfo? previousLanguage = Resources.Culture;
            Task operation;
            switch (section)
            {
                case "score": dialog.BeatorajaPlayerId = "next-player"; dialog.UseBeatorajaScoreDb = true; operation = dialog.CommitScoreSettingsAsync(); break;
                case "bmt": dialog.KeepBeatorajaBmtFilesWhenOutputDisabled = true; operation = dialog.CommitBmtSettingsAsync(); break;
                case "root": dialog.BeatorajaRootPath = nextRoot; operation = dialog.CommitBeatorajaRootSettingsAsync(); break;
                default: dialog.LR2CustomFolderOutputDir = nextOutput; operation = dialog.CommitOutputSettingsAsync("normal"); break;
            }
            try
            {
                await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, operation, "settings preparation entered");
                dialog.CloseCommand.Execute();
                dialog.SetPresentationActive(false);
                dialog.SetPresentationActive(true);
                Assert.AreEqual(root, dialog.BeatorajaRootPath);
                Assert.AreEqual("player", dialog.BeatorajaPlayerId);
                Assert.IsFalse(dialog.KeepBeatorajaBmtFilesWhenOutputDisabled);
                Assert.AreEqual(values.LR2CustomFolderOutputBaseDir, dialog.LR2CustomFolderOutputDir);
                dialog.UseExternalPanelImage = true;
                ResourceService.Current.ChangeCulture("ja-JP");
                Assert.IsTrue(dialog.UseExternalPanelImage);
                release.TrySetResult();
                await operation;
                Assert.AreEqual(failContinuation ? 1 : 0, failures.Count, string.Join(Environment.NewLine, failures));
                ResourceService.Current.ChangeCulture("en-US");
                Assert.IsTrue(dialog.UseExternalPanelImage);
                Assert.IsFalse(values.UseExternalPanelImage);
                Assert.AreEqual(values.BeatorajaRootPath, dialog.BeatorajaRootPath);
                Assert.AreEqual(values.UseBeatorajaScoreDb, dialog.UseBeatorajaScoreDb);
                Assert.AreEqual(values.BeatorajaPlayerId, dialog.BeatorajaPlayerId);
                Assert.AreEqual(values.BeatorajaScoreDbPath, dialog.BeatorajaScoreDbPath);
                Assert.IsFalse(dialog.HasScoreSelectionChanges);
                Assert.IsFalse(dialog.CanCancelBmtSettings);
                Assert.AreEqual(values.LR2CustomFolderOutputBaseDir, dialog.LR2CustomFolderOutputDir);
                if (section == "score")
                {
                    Assert.IsTrue(values.UseBeatorajaScoreDb); Assert.AreEqual(nextScore, values.BeatorajaScoreDbPath); Assert.IsTrue(dialog.IsScoreSelectionEnabled);
                    harness.State.ThrowOnScoreReload = false;
                    dialog.UseBeatorajaScoreDb = false;
                    await dialog.CommitScoreSettingsAsync();
                    Assert.IsFalse(TestSettingsFactory.OpenSettings(Path.Combine(directory.Path, "user.config")).UseBeatorajaScoreDb);
                }
                if (section == "bmt") { Assert.IsTrue(values.KeepBeatorajaBmtFilesWhenOutputDisabled); }
                if (section == "root") { Assert.AreEqual(nextRoot, values.BeatorajaRootPath); }
                if (section == "output") { Assert.AreEqual(nextOutput, values.LR2CustomFolderOutputBaseDir); }
            }
            finally
            {
                release.TrySetResult(); try { await operation; }
                finally
                {
                    if (previousLanguage == null) { Resources.Culture = null; }
                    else { ResourceService.Current.ChangeCulture(previousLanguage.Name); }
                }
            }
        }, nameof(SectionConfirmation_PreparationReopenAdoptsOnlySavedSection));
    }

    /// <summary>部分保存後の通常Close/reopenは候補だけを戻し、共有reader・実媒体を再保存せず、次の明示候補を保存して差分を完了します。媒体条件表はBmsRootRegistrationTestsへ分担します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void Lr2BmsConfirmation_PartialSaveCloseKeepsPublishedInputsUntilExplicitSave()
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "lr2-partial-close");
            (string SongDbPath, string ConfigPath, string BmsRoot) layout = CreateValidLr2Layout(directory.Path);
            string first = Directory.CreateDirectory(Path.Combine(directory.Path, "first")).FullName;
            string latest = Directory.CreateDirectory(Path.Combine(directory.Path, "latest")).FullName;
            string configPath = Path.Combine(directory.Path, "user.config");
            Settings values = OpenPersistedSettings(directory.Path);
            values.OperationModeLR2DB = true;
            values.LR2RootPath = directory.Path; values.LR2ConfigXmlPath = layout.ConfigPath; values.LR2SongDBPath = layout.SongDbPath;
            values.BMSInstallDir = layout.BmsRoot; values.Save();
            var failures = new List<Exception>();
            using var harness = SettingsDialogHarness.Create(values, activeLibraryProfile: true, reportApplyFailure: failures.Add);
            SettingsDialogViewModel dialog = harness.Dialog;
            async Task Select(string target)
            {
                await dialog.AddBmsSearchRootPathsAsync([target]);
                foreach (string root in dialog.AvailableBMSDirectories.Where(root => root != target).ToArray())
                { await dialog.RemoveBmsSearchRootPathAsync(root); }
                dialog.ReplacementBmsInstallDir = target;
            }
            await Select(first);
            using (File.Open(configPath, FileMode.Open, FileAccess.Read, FileShare.Read)) { await dialog.CommitBmsRootsAsync(); }
            Assert.AreEqual(1, failures.Count);
            byte[] partialConfig = File.ReadAllBytes(configPath);
            byte[] partialXml = File.ReadAllBytes(layout.ConfigPath);
            dialog.CloseCommand.Execute(); dialog.SetPresentationActive(false); dialog.SetPresentationActive(true);
            CollectionAssert.AreEqual(new[] { layout.BmsRoot }, dialog.AvailableBMSDirectories.ToArray());
            CollectionAssert.AreEqual(new[] { layout.BmsRoot }, dialog.ConfirmedBMSDirectories.ToArray());
            Assert.IsNull(dialog.ReplacementBmsInstallDir);
            Assert.IsFalse(dialog.CanCancelBmsRoots);
            Assert.IsTrue(dialog.CanConfirmBmsRoots);
            Assert.AreEqual(0, harness.State.FileDiffReloadCount);
            CollectionAssert.AreEqual(partialConfig, File.ReadAllBytes(configPath));
            CollectionAssert.AreEqual(partialXml, File.ReadAllBytes(layout.ConfigPath));
            await Select(latest);
            await dialog.CommitBmsRootsAsync();
            CollectionAssert.AreEqual(new[] { latest }, dialog.ConfirmedBMSDirectories.ToArray());
            Assert.AreEqual(latest, TestSettingsFactory.OpenSettings(configPath).BMSInstallDir);
            Assert.AreEqual(1, harness.State.FileDiffReloadCount);
            Assert.AreEqual(1, failures.Count);
        }, nameof(Lr2BmsConfirmation_PartialSaveCloseKeepsPublishedInputsUntilExplicitSave));
    }

    /// <summary>右クリックrecordの確定が別recordの不正候補を保存せず、実JSONの他recordを保持することを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void RightClickRecordConfirmationPreservesOtherSavedRecords()
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "rightclick-record");
            Settings values = OpenPersistedSettings(directory.Path);
            using var harness = SettingsDialogHarness.Create(values);
            RightClickActionSettings before = RightClickActionSettingsSerializer.Parse(values.RightClickActionsJson).Settings;
            RightClickActionSettingsEditor editor = harness.Dialog.RightClickActionSettingsEditor;
            editor.SelectedWebAction = editor.WebActions[0];
            editor.SelectedWebAction.Name = "saved record";
            editor.AddProgramAction();
            editor.SelectedProgramAction!.ArgumentTemplate = "invalid candidate";
            harness.Dialog.CommitRightClickRecord(true);
            RightClickActionSettings saved = RightClickActionSettingsSerializer.Parse(TestSettingsFactory.OpenSettings(Path.Combine(directory.Path, "user.config")).RightClickActionsJson).Settings;
            Assert.AreEqual("saved record", saved.WebActions[0].Name);
            Assert.AreEqual(before.ProgramActions.Count, saved.ProgramActions.Count);
            Assert.AreEqual("invalid candidate", editor.SelectedProgramAction.ArgumentTemplate);
            await Task.CompletedTask;
        }, nameof(RightClickRecordConfirmationPreservesOtherSavedRecords));
    }

    /// <summary>Web/Programとも未確定Cを含む表示順から確定IDだけを保存し、Cの確定位置とBの明示移動を保ちます。実保存失敗では画面順・媒体を保持し、他行の入力は保存せず削除も明示対象だけへ適用します。</summary>
    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    [DoNotParallelize]
    public void RightClickRecordOrderProjectsDisplayedPositionsWithoutSavingOtherCandidates(bool web, bool moveSavedB)
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "rightclick-record-order");
            Settings values = OpenPersistedSettings(directory.Path);
            string path = Path.Combine(directory.Path, "user.config");
            var initial = new RightClickActionSettings(
                [new RightClickWebActionDefinition("web-a", "A", "https://example.com/a/{sha256}", true, ExternalChartKind.All),
                 new RightClickWebActionDefinition("web-b", "B", "https://example.com/b/{sha256}", true, ExternalChartKind.All)],
                [new RightClickProgramActionDefinition("program-a", "A", Path.Combine(directory.Path, "tool.exe"), "{filePath}", true),
                 new RightClickProgramActionDefinition("program-b", "B", Path.Combine(directory.Path, "tool.exe"), "{filePath}", true)]);
            Assert.IsTrue(RightClickActionSettingsSerializer.TrySerialize(initial, out string json, out RightClickActionSettingsParseError parseError), parseError?.ToString());
            values.RightClickActionsJson = json;
            values.Save();
            var failures = new List<Exception>();
            var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
            using var harness = SettingsDialogHarness.Create(values, reportApplyFailure: failures.Add, dialogs: dialogs);
            RightClickActionSettingsEditor editor = harness.Dialog.RightClickActionSettingsEditor;
            string addedId;
            if (web)
            {
                editor.WebActions[0].Name = "unsaved A";
                editor.AddWebAction();
                editor.SelectedWebAction!.Name = "C";
                editor.SelectedWebAction.UrlTemplate = "https://example.com/c/{sha256}";
                addedId = editor.SelectedWebAction.Id;
            }
            else
            {
                editor.ProgramActions[0].Name = "unsaved A";
                editor.AddProgramAction();
                editor.SelectedProgramAction!.Name = "C";
                editor.SelectedProgramAction.ExecutablePath = Path.Combine(directory.Path, "tool.exe");
                addedId = editor.SelectedProgramAction.Id;
            }
            string prefix = web ? "web-" : "program-";
            string[] DisplayedIds() => web ? editor.WebActions.Select(row => row.Id).ToArray() : editor.ProgramActions.Select(row => row.Id).ToArray();
            string[] SavedIds() => web
                ? RightClickActionSettingsSerializer.Parse(TestSettingsFactory.OpenSettings(path).RightClickActionsJson).Settings.WebActions.Select(row => row.Id).ToArray()
                : RightClickActionSettingsSerializer.Parse(TestSettingsFactory.OpenSettings(path).RightClickActionsJson).Settings.ProgramActions.Select(row => row.Id).ToArray();
            harness.Dialog.CommitRightClickRecord(web, "up");
            CollectionAssert.AreEqual(new[] { prefix + "a", addedId, prefix + "b" }, DisplayedIds());
            CollectionAssert.AreEqual(new[] { prefix + "a", prefix + "b" }, SavedIds());
            if (moveSavedB)
            {
                if (web) { editor.SelectedWebAction = editor.WebActions.Single(row => row.Id == prefix + "b"); }
                else { editor.SelectedProgramAction = editor.ProgramActions.Single(row => row.Id == prefix + "b"); }
            }
            string operation = moveSavedB ? "up" : "save";
            if (!moveSavedB)
            {
                byte[] previousMedia = File.ReadAllBytes(path);
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                { harness.Dialog.CommitRightClickRecord(web, operation); }
                Assert.AreEqual(1, failures.Count);
                CollectionAssert.AreEqual(previousMedia, File.ReadAllBytes(path));
                CollectionAssert.AreEqual(new[] { prefix + "a", addedId, prefix + "b" }, DisplayedIds());
            }
            harness.Dialog.CommitRightClickRecord(web, operation);
            string[] expected = moveSavedB ? new[] { prefix + "a", prefix + "b" } : new[] { prefix + "a", addedId, prefix + "b" };
            CollectionAssert.AreEqual(expected, SavedIds());
            CollectionAssert.AreEqual(moveSavedB ? new[] { prefix + "a", prefix + "b", addedId } : expected, DisplayedIds());
            RightClickActionSettings saved = RightClickActionSettingsSerializer.Parse(TestSettingsFactory.OpenSettings(path).RightClickActionsJson).Settings;
            Assert.AreEqual("A", web ? saved.WebActions[0].Name : saved.ProgramActions[0].Name);
            Assert.AreEqual("unsaved A", web ? editor.WebActions[0].Name : editor.ProgramActions[0].Name);
            CollectionAssert.AreEqual(new[] { "A", "B" }, web ? saved.ProgramActions.Select(row => row.Name).ToArray() : saved.WebActions.Select(row => row.Name).ToArray());
            if (moveSavedB)
            {
                if (web) { editor.SelectedWebAction = editor.WebActions.Single(row => row.Id == addedId); }
                else { editor.SelectedProgramAction = editor.ProgramActions.Single(row => row.Id == addedId); }
                harness.Dialog.CommitRightClickRecord(web);
                CollectionAssert.AreEqual(new[] { prefix + "a", prefix + "b", addedId }, SavedIds());
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                { harness.Dialog.CommitRightClickRecord(web, "up"); }
                Assert.AreEqual(1, failures.Count);
                CollectionAssert.AreEqual(new[] { prefix + "a", prefix + "b", addedId }, SavedIds());
                CollectionAssert.AreEqual(new[] { prefix + "a", prefix + "b", addedId }, DisplayedIds());
                harness.Dialog.CommitRightClickRecord(web, "up");
                CollectionAssert.AreEqual(new[] { prefix + "a", addedId, prefix + "b" }, SavedIds());
                CollectionAssert.AreEqual(new[] { prefix + "a", addedId, prefix + "b" }, DisplayedIds());
            }
            Assert.AreEqual(0, dialogs.ConfirmationCount, "並べ替えと項目の確定には確認を加えません。");
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            { harness.Dialog.CommitRightClickRecord(web, "delete"); }
            Assert.AreEqual(1, dialogs.ConfirmationCount, "削除は保存失敗の回も保存の前に確認します。");
            Assert.AreEqual(2, failures.Count);
            CollectionAssert.AreEqual(new[] { prefix + "a", addedId, prefix + "b" }, SavedIds());
            CollectionAssert.AreEqual(new[] { prefix + "a", addedId, prefix + "b" }, DisplayedIds());
            harness.Dialog.CommitRightClickRecord(web, "delete");
            Assert.AreEqual(2, dialogs.ConfirmationCount, "成功する削除もちょうど一回確認します。");
            CollectionAssert.AreEqual(new[] { prefix + "a", prefix + "b" }, SavedIds());
            CollectionAssert.AreEqual(new[] { prefix + "a", prefix + "b" }, DisplayedIds());
            await Task.CompletedTask;
        }, nameof(RightClickRecordOrderProjectsDisplayedPositionsWithoutSavingOtherCandidates));
    }

    /// <summary>前後空白を含むプリセット名の確定後、再表示せず同じeditorで対象変更・改名・削除しても保存済みの一件へ作用し、取消した別recordとBMT候補を保存・破棄しません。</summary>
    /// <remarks>実編集sessionとproviderを通し、生JSONを正規化せず照合します。拒否条件・検索・実履歴への配送とWindow接続は既存系列が分担します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void PlayHistoryPreset_ConfirmedNameKeepsSavedRecordAcrossEditsAndRemoval()
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "history-preset-confirmed-name");
            Settings values = OpenPersistedSettings(directory.Path);
            const string retainedJson = """[{"Name":"保持","Targets":[{"PlaylistId":101}]}]""";
            values.PlayHistoryDisplayTargetSetsJson = retainedJson;
            values.Save();
            var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
            using var harness = SettingsDialogHarness.Create(values, dialogs: dialogs, playlistSnapshots:
            [
                PlaylistTablePresentationSnapshot.From(new BMSTable { playlist_id = 101, name = "表A" }),
                PlaylistTablePresentationSnapshot.From(new BMSTable { playlist_id = 202, name = "表B" })
            ]);
            SettingsDialogViewModel dialog = harness.Dialog;
            PlayHistoryFolderDisplayPresetEditor retained = dialog.PlayHistoryFolderDisplayPresets.Single();
            PlayHistoryFolderDisplayPresetEditSession canceled = dialog.CreatePlayHistoryFolderDisplayPresetEditSession(retained);
            canceled.Name = "取消";
            foreach (PlayHistoryFolderPresetPlaylistOption option in canceled.PlaylistOptions)
            {
                option.IsSelected = option.Table.PlaylistId == 202;
            }
            Assert.AreEqual("保持", retained.Name);
            Assert.AreEqual<int?>(101, retained.Targets.Single().PlaylistId);
            Assert.AreEqual(retainedJson, TestSettingsFactory.OpenSettings(Path.Combine(directory.Path, "user.config")).PlayHistoryDisplayTargetSetsJson);
            dialog.EnableBeatorajaBmtOutput = true;

            void AssertConfirmed(string? name, int? playlistId)
            {
                Settings saved = TestSettingsFactory.OpenSettings(Path.Combine(directory.Path, "user.config"));
                var records = JArray.Parse(saved.PlayHistoryDisplayTargetSetsJson);
                Assert.AreEqual(name == null ? 1 : 2, records.Count);
                JToken savedRetained = records.Single(record => record.Value<string>("Name") == "保持");
                Assert.IsTrue(JToken.DeepEquals(JArray.Parse(retainedJson).Single(), savedRetained), "取消した別record候補を混入させません。");
                Assert.AreSame(retained, dialog.PlayHistoryFolderDisplayPresets.Single(preset => preset.Name == "保持"));
                Assert.AreEqual("保持", retained.Name);
                Assert.AreEqual<int?>(101, retained.Targets.Single().PlaylistId);
                string[] expectedNames = name == null ? ["保持"] : ["保持", name];
                CollectionAssert.AreEquivalent(expectedNames, records.Select(record => record.Value<string>("Name")).ToArray());
                CollectionAssert.AreEquivalent(expectedNames, dialog.PlayHistoryFolderDisplayPresets.Select(preset => preset.Name).ToArray());
                if (name != null)
                {
                    JToken record = records.Single(item => item.Value<string>("Name") == name);
                    JArray targets = record.Value<JArray>("Targets") ?? throw new AssertFailedException("保存済み対象がありません。");
                    Assert.AreEqual(1, targets.Count);
                    Assert.AreEqual(playlistId, targets.Single().Value<int?>("PlaylistId"));
                    PlayHistoryFolderDisplayPresetEditor editor = dialog.SelectedPlayHistoryFolderDisplayPreset;
                    Assert.AreEqual(name, editor.Name);
                    Assert.AreEqual(playlistId, editor.Targets.Single().PlaylistId);
                }
                Assert.IsFalse(saved.EnableBeatorajaBmtOutput, "プリセット保存に別BMT候補を便乗させません。");
                Assert.IsTrue(dialog.EnableBeatorajaBmtOutput, "別BMT候補を破棄しません。");
                Assert.AreEqual("取消", canceled.Name);
                Assert.AreEqual<int?>(202, canceled.PlaylistOptions.Single(option => option.IsSelected).Table.PlaylistId);
            }

            AssertConfirmed(null, null);
            PlayHistoryFolderDisplayPresetEditSession added = dialog.CreatePlayHistoryFolderDisplayPresetEditSession(null);
            added.Name = " 練習 ";
            added.PlaylistOptions.Single(option => option.Table.PlaylistId == 101).IsSelected = true;
            Assert.IsTrue(dialog.TryApplyPlayHistoryFolderDisplayPresetEditSession(added, out string addError), addError);
            PlayHistoryFolderDisplayPresetEditor preset = dialog.SelectedPlayHistoryFolderDisplayPreset;
            AssertConfirmed("練習", 101);

            PlayHistoryFolderDisplayPresetEditSession changed = dialog.CreatePlayHistoryFolderDisplayPresetEditSession(preset);
            foreach (PlayHistoryFolderPresetPlaylistOption option in changed.PlaylistOptions)
            {
                option.IsSelected = option.Table.PlaylistId == 202;
            }
            Assert.IsTrue(dialog.TryApplyPlayHistoryFolderDisplayPresetEditSession(changed, out string changeError), changeError);
            Assert.AreSame(preset, dialog.SelectedPlayHistoryFolderDisplayPreset);
            AssertConfirmed("練習", 202);

            PlayHistoryFolderDisplayPresetEditSession renamed = dialog.CreatePlayHistoryFolderDisplayPresetEditSession(preset);
            renamed.Name = " 集中 ";
            Assert.IsTrue(dialog.TryApplyPlayHistoryFolderDisplayPresetEditSession(renamed, out string renameError), renameError);
            Assert.AreSame(preset, dialog.SelectedPlayHistoryFolderDisplayPreset);
            AssertConfirmed("集中", 202);
            Assert.AreEqual(0, dialogs.ConfirmationCount, "確定・対象変更・改名には確認を加えません。");

            dialog.RemoveSelectedPlayHistoryFolderDisplayPreset();
            Assert.AreEqual(1, dialogs.ConfirmationCount, "削除だけを一回確認します。");
            AssertConfirmed(null, null);
            Assert.AreSame(retained, dialog.SelectedPlayHistoryFolderDisplayPreset);
            await Task.CompletedTask;
        }, nameof(PlayHistoryPreset_ConfirmedNameKeepsSavedRecordAcrossEditsAndRemoval));
    }

    /// <summary>
    /// 条件：確定済みのBMSディレクトリ [R1, R2, R3] に R4 を候補として追加し、R2 を選択して明示確定します。単独モードとLR2モードの両方で行います。
    /// 期待結果：確定後の一覧に R4 が加わり、選択は R2 のままで先頭の R1 へ移りません。
    /// 検出する誤り：確定後の再同期が、確定前の選択が新しい一覧に残っていても先頭へ移す。
    /// </summary>
    /// <remarks>実 ListBox の Binding による選択解除は CompiledAdditionalOutputOperations_KeepOperatedItemSelectedInList が追加出力先で代表します。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public void BmsRootConfirmation_KeepsSelectionWhileItRemains(bool lr2)
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "bms-root-selection");
            string[] roots = [.. new[] { "r1", "r2", "r3", "r4" }.Select(name => Directory.CreateDirectory(Path.Combine(directory.Path, "roots", name)).FullName)];
            Settings values = OpenPersistedSettings(directory.Path);
            if (lr2)
            {
                (string SongDbPath, string ConfigPath, string BmsRoot) layout = CreateValidLr2Layout(directory.Path, roots[..3]);
                values.OperationModeLR2DB = true;
                values.LR2RootPath = directory.Path; values.LR2ConfigXmlPath = layout.ConfigPath; values.LR2SongDBPath = layout.SongDbPath;
            }
            else { values.StandaloneBmsRootPaths = StandaloneBmsRootPathSettings.Serialize(roots[..3]); values.BMSRootPath = roots[0]; }
            values.BMSInstallDir = roots[0];
            values.Save();
            using var harness = SettingsDialogHarness.Create(values, activeLibraryProfile: true);
            SettingsDialogViewModel dialog = harness.Dialog;
            CollectionAssert.AreEqual(roots[..3], dialog.ConfirmedBMSDirectories.ToArray());
            await dialog.AddBmsSearchRootPathsAsync([roots[3]]);
            dialog.SelectedBmsSearchRootPath = roots[1];
            await dialog.CommitBmsRootsAsync();
            CollectionAssert.AreEqual(roots, dialog.ConfirmedBMSDirectories.ToArray());
            Assert.AreEqual(roots[1], dialog.SelectedBmsSearchRootPath, "確定前の選択が残っていれば先頭へ移しません。");
        }, nameof(BmsRootConfirmation_KeepsSelectionWhileItRemains));
    }

    /// <summary>
    /// 条件：右クリック設定ページの「既定値へ戻す」、保存済みのWeb項目・プログラム項目の削除、未保存の新規行の削除を、確認がOK・キャンセル・表示失敗の各結果のときに実行します。
    /// 期待結果：表示失敗は取消へ置き換えず例外として呼出し元へ伝わり、確認は一回で本文は新しいリソース（削除は行の現在の入力名入り）です。OKのときだけ保存し（保存は一回、他行の未確定入力は保存しない）、キャンセル・表示失敗では保存0回でファイル・両一覧の内容と順序・各行の未確定入力・選択が変わりません。現在値が既定値と同じでも「既定値へ戻す」は確認します。
    /// 検出する誤り：確認なしの即時保存、キャンセルでの保存や下書きの破棄、表示失敗を承認や取消へ置き換えて呼出し元へ伝えない、確認本文に別の項目名を入れる、他行の未確定入力を保存へ混ぜる。
    /// </summary>
    [TestMethod]
    [DataRow("defaults", "ok")]
    [DataRow("defaults", "cancel")]
    [DataRow("defaults", "display-failed")]
    [DataRow("defaults-already-default", "cancel")]
    [DataRow("delete-web", "ok")]
    [DataRow("delete-web", "cancel")]
    [DataRow("delete-program", "ok")]
    [DataRow("delete-program", "cancel")]
    [DataRow("delete-unsaved", "ok")]
    [DataRow("delete-unsaved", "cancel")]
    [DoNotParallelize]
    public void RightClickRecordDefaultsAndDelete_ConfirmBeforeSaving(string operation, string outcome)
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "rightclick-confirm");
            Settings values = OpenPersistedSettings(directory.Path);
            string path = Path.Combine(directory.Path, "user.config");
            string tool = Path.Combine(directory.Path, "tool.exe");
            if (operation != "defaults-already-default")
            {
                var initial = new RightClickActionSettings(
                    [new RightClickWebActionDefinition("web-a", "A", "https://example.com/a/{sha256}", true, ExternalChartKind.All),
                     new RightClickWebActionDefinition("web-b", "B", "https://example.com/b/{sha256}", true, ExternalChartKind.All)],
                    [new RightClickProgramActionDefinition("program-a", "PA", tool, "{filePath}", true),
                     new RightClickProgramActionDefinition("program-b", "PB", tool, "{filePath}", true)]);
                Assert.IsTrue(RightClickActionSettingsSerializer.TrySerialize(initial, out string json, out RightClickActionSettingsParseError parseError), parseError?.ToString());
                values.RightClickActionsJson = json;
                values.Save();
            }
            string savedJsonBefore = values.RightClickActionsJson;
            var dialogs = new RecordingRootDialogService { ConfirmationResult = ConfirmationFor(outcome) };
            SettingsProperty property = values.Properties[nameof(Settings.RightClickActionsJson)];
            SettingsProvider originalProvider = property.Provider;
            var provider = new InstallAdmissionObservingSettingsProvider(originalProvider, () => { });
            property.Provider = provider;
            try
            {
                using var harness = SettingsDialogHarness.Create(values, dialogs: dialogs);
                RightClickActionSettingsEditor editor = harness.Dialog.RightClickActionSettingsEditor;
                bool web = operation is not "delete-program";
                if (operation != "defaults-already-default")
                {
                    editor.WebActions[0].Name = "unsaved A";
                    editor.ProgramActions[0].Name = "unsaved PA";
                }
                if (operation == "delete-web") { editor.SelectedWebAction = editor.WebActions.Single(row => row.Id == "web-b"); }
                else if (operation == "delete-program") { editor.SelectedProgramAction = editor.ProgramActions.Single(row => row.Id == "program-b"); }
                else if (operation == "delete-unsaved")
                {
                    editor.AddWebAction();
                    editor.SelectedWebAction!.Name = "C";
                    editor.SelectedWebAction.UrlTemplate = "https://example.com/c/{sha256}";
                }
                else
                {
                    editor.AddWebAction();
                    editor.SelectedWebAction!.Name = "unsaved C";
                }
                string expectedMessage = operation switch
                {
                    "delete-web" => string.Format(Resources.Confirm_RightClickDeleteActionFormat, "B"),
                    "delete-program" => string.Format(Resources.Confirm_RightClickDeleteActionFormat, "PB"),
                    "delete-unsaved" => string.Format(Resources.Confirm_RightClickDeleteActionFormat, "C"),
                    _ => Resources.Confirm_RightClickRestoreDefaults
                };
                string Snapshot() => string.Join("|", editor.WebActions.Select(row => $"{row.Id}:{row.Name}:{row.UrlTemplate}"))
                    + "||" + string.Join("|", editor.ProgramActions.Select(row => $"{row.Id}:{row.Name}:{row.ExecutablePath}"));
                string snapshotBefore = Snapshot();
                RightClickWebActionEditorRow? selectedWeb = editor.SelectedWebAction;
                RightClickProgramActionEditorRow? selectedProgram = editor.SelectedProgramAction;
                byte[] fileBefore = File.ReadAllBytes(path);

                Exception? thrown = null;
                try { harness.Dialog.CommitRightClickRecord(web, operation.StartsWith("defaults", StringComparison.Ordinal) ? "defaults" : "delete"); }
                catch (Exception failure) { thrown = failure; }
                if (outcome == "display-failed") { Assert.IsNotNull(thrown, "確認の表示失敗を取消へ置き換えず、呼出し元へ伝えます。"); }
                else { Assert.IsNull(thrown); }

                UiConfirmationRequest request = dialogs.ConfirmationRequests.Single();
                Assert.AreEqual(expectedMessage, request.MessageBoxText);
                Assert.AreEqual(MessageBoxButton.OKCancel, request.Button);
                Assert.AreEqual(0, dialogs.MessageCount);
                if (outcome != "ok")
                {
                    Assert.AreEqual(0, provider.SaveCount, "取消・表示失敗では保存要求を出しません。");
                    CollectionAssert.AreEqual(fileBefore, File.ReadAllBytes(path));
                    Assert.AreEqual(snapshotBefore, Snapshot(), "両一覧の順序と各行の未確定入力を変えません。");
                    Assert.AreSame(selectedWeb, editor.SelectedWebAction);
                    Assert.AreSame(selectedProgram, editor.SelectedProgramAction);
                    Assert.AreEqual(savedJsonBefore, values.RightClickActionsJson);
                    return;
                }
                RightClickActionSettings saved = RightClickActionSettingsSerializer.Parse(TestSettingsFactory.OpenSettings(path).RightClickActionsJson).Settings!;
                switch (operation)
                {
                    case "defaults":
                        Assert.AreEqual(1, provider.SaveCount);
                        Assert.AreEqual(RightClickActionSettingsDefaults.SerializedJson, TestSettingsFactory.OpenSettings(path).RightClickActionsJson);
                        CollectionAssert.AreEqual(RightClickActionSettingsDefaults.Create().WebActions.Select(row => row.Id).ToArray(), editor.WebActions.Select(row => row.Id).ToArray());
                        CollectionAssert.AreEqual(RightClickActionSettingsDefaults.Create().ProgramActions.Select(row => row.Id).ToArray(), editor.ProgramActions.Select(row => row.Id).ToArray());
                        break;
                    case "delete-web":
                        Assert.AreEqual(1, provider.SaveCount);
                        CollectionAssert.AreEqual(new[] { "web-a" }, saved.WebActions.Select(row => row.Id).ToArray());
                        Assert.AreEqual("A", saved.WebActions.Single().Name, "他行の未確定入力を保存へ混ぜません。");
                        Assert.AreEqual("PA", saved.ProgramActions[0].Name, "他行の未確定入力を保存へ混ぜません。");
                        break;
                    case "delete-program":
                        Assert.AreEqual(1, provider.SaveCount);
                        CollectionAssert.AreEqual(new[] { "program-a" }, saved.ProgramActions.Select(row => row.Id).ToArray());
                        Assert.AreEqual("PA", saved.ProgramActions.Single().Name, "他行の未確定入力を保存へ混ぜません。");
                        Assert.AreEqual("A", saved.WebActions[0].Name, "他行の未確定入力を保存へ混ぜません。");
                        break;
                    default:
                        Assert.IsTrue(provider.SaveCount <= 1);
                        CollectionAssert.AreEqual(new[] { "web-a", "web-b" }, saved.WebActions.Select(row => row.Id).ToArray());
                        Assert.AreEqual("A", saved.WebActions[0].Name);
                        CollectionAssert.AreEqual(new[] { "web-a", "web-b" }, editor.WebActions.Select(row => row.Id).ToArray(), "未保存の新規行だけが消えます。");
                        break;
                }
                await Task.CompletedTask;
            }
            finally { property.Provider = originalProvider; }
        }, nameof(RightClickRecordDefaultsAndDelete_ConfirmBeforeSaving));
    }

    /// <summary>
    /// 条件：保存済みの履歴表示プリセット2件のうち選択中の1件を、確認がOK・キャンセル・表示失敗の各結果のときに削除します。
    /// 期待結果：表示失敗は取消へ置き換えず例外として呼出し元へ伝わり、確認は一回で本文は新しいリソースにプリセット名を入れたものです。OKのときだけ保存し、保存JSONから消え、プレイ履歴側の反映を保存JSONで一回呼びます。キャンセル・表示失敗では保存0回でファイル・一覧・選択が変わらず、プレイ履歴側へ反映しません。
    /// 検出する誤り：確認なしの即時削除、キャンセルや表示失敗での削除、表示失敗を取消へ置き換えて呼出し元へ伝えない、プレイ履歴側への余分・不足の反映、確認本文の名前の取り違え。
    /// </summary>
    [TestMethod]
    [DataRow("ok")]
    [DataRow("cancel")]
    [DataRow("display-failed")]
    [DoNotParallelize]
    public void PlayHistoryPresetRemoval_ConfirmsBeforeSavingAndCancelKeepsHistory(string outcome)
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "history-preset-removal-confirm");
            Settings values = OpenPersistedSettings(directory.Path);
            string path = Path.Combine(directory.Path, "user.config");
            values.PlayHistoryDisplayTargetSetsJson = """[{"Name":"保持","Targets":[{"PlaylistId":101}]},{"Name":"削除","Targets":[{"PlaylistId":202}]}]""";
            values.Save();
            var dialogs = new RecordingRootDialogService { ConfirmationResult = ConfirmationFor(outcome) };
            SettingsProperty property = values.Properties[nameof(Settings.PlayHistoryDisplayTargetSetsJson)];
            SettingsProvider originalProvider = property.Provider;
            var provider = new InstallAdmissionObservingSettingsProvider(originalProvider, () => { });
            property.Provider = provider;
            try
            {
                using var harness = SettingsDialogHarness.Create(values, dialogs: dialogs, playlistSnapshots:
                [
                    PlaylistTablePresentationSnapshot.From(new BMSTable { playlist_id = 101, name = "表A" }),
                    PlaylistTablePresentationSnapshot.From(new BMSTable { playlist_id = 202, name = "表B" })
                ]);
                SettingsDialogViewModel dialog = harness.Dialog;
                PlayHistoryFolderDisplayPresetEditor retained = dialog.PlayHistoryFolderDisplayPresets.Single(preset => preset.Name == "保持");
                PlayHistoryFolderDisplayPresetEditor target = dialog.PlayHistoryFolderDisplayPresets.Single(preset => preset.Name == "削除");
                dialog.SelectedPlayHistoryFolderDisplayPreset = target;
                int refreshesBefore = harness.PlayHistory.RefreshCount;
                byte[] fileBefore = File.ReadAllBytes(path);

                Exception? thrown = null;
                try { dialog.RemoveSelectedPlayHistoryFolderDisplayPreset(); }
                catch (Exception failure) { thrown = failure; }
                if (outcome == "display-failed") { Assert.IsNotNull(thrown, "確認の表示失敗を取消へ置き換えず、呼出し元へ伝えます。"); }
                else { Assert.IsNull(thrown); }

                UiConfirmationRequest request = dialogs.ConfirmationRequests.Single();
                Assert.AreEqual(string.Format(Resources.Confirm_RemovePlayHistoryFolderDisplayPresetFormat, "削除"), request.MessageBoxText);
                Assert.AreEqual(MessageBoxButton.OKCancel, request.Button);
                if (outcome != "ok")
                {
                    Assert.AreEqual(0, provider.SaveCount);
                    CollectionAssert.AreEqual(fileBefore, File.ReadAllBytes(path));
                    CollectionAssert.AreEqual(new[] { retained, target }, dialog.PlayHistoryFolderDisplayPresets.ToArray());
                    Assert.AreSame(target, dialog.SelectedPlayHistoryFolderDisplayPreset);
                    Assert.AreEqual(refreshesBefore, harness.PlayHistory.RefreshCount, "取消・表示失敗ではプレイ履歴側へ反映しません。");
                    return;
                }
                Assert.AreEqual(1, provider.SaveCount);
                string savedJson = TestSettingsFactory.OpenSettings(path).PlayHistoryDisplayTargetSetsJson;
                CollectionAssert.AreEqual(new[] { "保持" }, JArray.Parse(savedJson).Select(record => record.Value<string>("Name")).ToArray());
                CollectionAssert.AreEqual(new[] { retained }, dialog.PlayHistoryFolderDisplayPresets.ToArray());
                Assert.AreEqual(refreshesBefore + 1, harness.PlayHistory.RefreshCount);
                Assert.AreEqual(savedJson, harness.PlayHistory.LastSerializedDisplayTargetSets);
                await Task.CompletedTask;
            }
            finally { property.Provider = originalProvider; }
        }, nameof(PlayHistoryPresetRemoval_ConfirmsBeforeSavingAndCancelKeepsHistory));
    }

    private static UiDialogResult ConfirmationFor(string outcome) => outcome switch
    {
        "ok" => UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK),
        "cancel" => UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel),
        _ => UiDialogResult.Failed(new InvalidOperationException("controlled confirmation display failure"))
    };

    /// <summary>閉じる操作は局所候補だけを破棄し、再表示では確定値から作り、既に項目保存した通常設定を保持します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void ClosingDiscardsCandidatesAndKeepsConfirmedOrdinaryItems()
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "settings-close");
            Settings values = OpenPersistedSettings(directory.Path);
            var session = new CountingSettingsEditSession(values);
            using var harness = SettingsDialogHarness.Create(values, settingsEditSession: session);
            harness.Dialog.SetPresentationActive(true);
            IReadOnlyList<SettingsDialogViewModel.BeatorajaBmtHashOutputModeOption> optionIdentity = harness.Dialog.BeatorajaBmtHashOutputModeOptions;
            int scoreInputs = 0;
            harness.Dialog.ScoreSelectionChanged += (_, _) => scoreInputs++;
            var presentation = new RecordingSettingsDialogPresentationPort();
            harness.Dialog.AttachPresentationPort(presentation);
            harness.Dialog.EnableBeatorajaBmtOutput = true;
            harness.Dialog.KeepBeatorajaBmtFilesWhenOutputDisabled = !values.KeepBeatorajaBmtFilesWhenOutputDisabled;
            harness.Dialog.RegisterBeatorajaBmtUrls = !values.RegisterBeatorajaBmtUrls;
            harness.Dialog.BeatorajaBmtHashOutputMode = "PreferSha256Only";
            harness.Dialog.UseExternalPanelImage = true;
            harness.Dialog.BeatorajaRootPath = Path.Combine(directory.Path, "candidate");
            harness.Dialog.StartupSortCachePrewarmPriority = 3;
            int saves = session.SaveCount;
            var notifications = new List<string>();
            harness.Dialog.PropertyChanged += (_, args) => notifications.Add(args.PropertyName ?? string.Empty);
            harness.Dialog.CloseCommand.Execute();
            harness.Dialog.SetPresentationActive(false);
            harness.Dialog.RequestOpen();
            harness.Dialog.SetPresentationActive(true);
            Assert.IsFalse(harness.Dialog.EnableBeatorajaBmtOutput);
            Assert.AreEqual(values.KeepBeatorajaBmtFilesWhenOutputDisabled, harness.Dialog.KeepBeatorajaBmtFilesWhenOutputDisabled);
            Assert.AreEqual(values.RegisterBeatorajaBmtUrls, harness.Dialog.RegisterBeatorajaBmtUrls);
            Assert.AreEqual(values.BeatorajaBmtHashOutputMode, harness.Dialog.BeatorajaBmtHashOutputMode);
            Assert.AreEqual(values.UseExternalPanelImage, harness.Dialog.UseExternalPanelImage);
            Assert.AreEqual(values.BeatorajaRootPath, harness.Dialog.BeatorajaRootPath);
            Assert.AreSame(optionIdentity, harness.Dialog.BeatorajaBmtHashOutputModeOptions);
            foreach (string property in new[] { nameof(harness.Dialog.EnableBeatorajaBmtOutput), nameof(harness.Dialog.KeepBeatorajaBmtFilesWhenOutputDisabled),
                nameof(harness.Dialog.RegisterBeatorajaBmtUrls), nameof(harness.Dialog.BeatorajaBmtHashOutputMode), nameof(harness.Dialog.BeatorajaRootPath),
                nameof(harness.Dialog.UseExternalPanelImage) }) { CollectionAssert.Contains(notifications, property); }
            CollectionAssert.DoesNotContain(notifications, string.Empty);
            Assert.AreEqual(saves, session.SaveCount);
            Assert.AreEqual(0, scoreInputs);
            Assert.AreEqual(0, harness.State.ScoreReloadCount);
            Assert.AreEqual(0, harness.Workspace.BmtRequests.Count);
            Assert.AreEqual(3, harness.Dialog.StartupSortCachePrewarmPriority);
            Assert.IsFalse(TestSettingsFactory.OpenSettings(Path.Combine(directory.Path, "user.config")).EnableBeatorajaBmtOutput);
            await Task.CompletedTask;
        }, nameof(ClosingDiscardsCandidatesAndKeepsConfirmedOrdinaryItems));
    }

    /// <summary>standaloneの実file diffで登録・配置を観測表記へ揃え、候補とscoreを保持します。LR2の管理階層と同じ公開保証は実Shellの通常出力代表へ分担します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void FileDiffRootSpelling_PublishesConfirmedSettingsAndKeepsNextOutputPlacement()
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "scan-root-publication");
            Settings values = OpenPersistedSettings(directory.Path);
            string root = Directory.CreateDirectory(Path.Combine(directory.Path, "Music")).FullName;
            string chart = Path.Combine(root, "chart.bms");
            File.WriteAllText(chart, BmsLibraryInitializationTestSupport.CreateValidBmsText("Confirmed root"));
            string observedRoot = char.ToLowerInvariant(root[0]) + root[1..];
            string observedChart = Path.Combine(observedRoot, "chart.bms");
            (string SongDbPath, string ConfigPath, string BmsRoot) layout = CreateValidLr2Layout(directory.Path, [root]);
            values.OperationModeLR2DB = false;
            values.LR2RootPath = directory.Path;
            values.LR2SongDBPath = layout.SongDbPath;
            values.LR2ConfigXmlPath = layout.ConfigPath;
            values.BMSRootPath = root;
            values.StandaloneBmsRootPaths = StandaloneBmsRootPathSettings.Serialize([root]);
            values.BMSInstallDir = root;
            values.LR2CustomFolderOutputBaseDir = Directory.CreateDirectory(Path.Combine(root, "Output")).FullName;
            values.LR2CustomFolderOutputBaseDirRootType = Directory.CreateDirectory(Path.Combine(root, "RootOutput")).FullName;
            values.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories(
                [Directory.CreateDirectory(Path.Combine(root, "Additional")).FullName]);
            values.Save();
            var scanner = CapturedChartFileScanner.FromFixture([observedChart],
                new Dictionary<string, IEnumerable<string>> { [observedRoot] = [] }, [observedRoot]);
            var composition = new ApplicationComposition(settingsEditSession: new SettingsEditSession(values),
                uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(ApplicationPathSnapshot.FromExecutablePath(Path.Combine(directory.Path, "BeMusicSeeker.exe"))),
                rootFileEnumerator: new FastRootFileEnumerator(), chartFileScanner: scanner,
                dialogService: UndeclaredDialogs.Create());
            string database = composition.ApplicationPathSnapshot.StandaloneSongDbPath;
            Directory.CreateDirectory(Path.GetDirectoryName(database) ?? throw new InvalidOperationException());
            if (!File.Exists(database)) { File.WriteAllBytes(database, []); }
            var profile = new LibraryProfile(false, database, [root], null, null, false, false, false, false);
            BMSLibrary library = composition.CreateBmsLibrary(profile);
            using var harness = SettingsDialogHarness.Create(values, activeLibraryProfile: true, composition: composition);
            string candidate = Path.Combine(directory.Path, "Unconfirmed");
            harness.Dialog.BmsRootPathList.Add(candidate);
            var publications = new List<(long Version, long ScoreGeneration, string[] Roots, string Install)>();
            int confirmedNotifications = 0;
            harness.Dialog.PropertyChanged += (_, args) =>
            { if (args.PropertyName == nameof(SettingsDialogViewModel.ConfirmedBMSDirectories)) { Interlocked.Increment(ref confirmedNotifications); } };
            try
            {
                await Task.Run(() => library.InitializeScoresOnly([]));
                BMSLibrary.ScoreSnapshot beforeScore = library.CaptureScoreSnapshotForView();
                long version = library.BMSParentFolderListCacheVersion;
                composition.BmsRootRegistrations.Published += (_, _) => publications.Add((library.BMSParentFolderListCacheVersion,
                    library.GetScoreRuntimeStateForDiagnostics().SourceGeneration,
                    harness.Dialog.ConfirmedBMSDirectories.ToArray(), harness.Dialog.BMSInstallDir));
                LibraryFileInitializationResult result = await Task.Run(() => library.ReloadFileDiff(null));
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(1, publications.Count);
                Assert.IsTrue(publications[0].Version > version);
                Assert.AreEqual(beforeScore.SourceGeneration, publications[0].ScoreGeneration);
                CollectionAssert.Contains(publications[0].Roots, observedRoot);
                Assert.AreEqual(observedRoot, publications[0].Install, false);
                Assert.IsTrue(Volatile.Read(ref confirmedNotifications) > 0);
                Assert.AreSame(beforeScore.PlayHistorySnapshot, library.CaptureScoreSnapshotForView().PlayHistorySnapshot);
                CollectionAssert.Contains(harness.Dialog.BmsRootPathList.ToArray(), candidate);
                CollectionAssert.Contains(harness.Dialog.ConfirmedBMSDirectories.ToArray(), observedRoot);
                Assert.AreEqual(observedRoot, harness.Dialog.BMSInstallDir);
                Settings fresh = TestSettingsFactory.OpenSettings(Path.Combine(directory.Path, "user.config"));
                Assert.AreEqual(observedRoot, fresh.BMSInstallDir, false);
                Assert.AreEqual(Path.Combine(observedRoot, "Output"), fresh.LR2CustomFolderOutputBaseDir, false);
                Assert.AreEqual(Path.Combine(observedRoot, "Output"), composition.CustomFolderOutputSettingsProvider().LR2CustomFolderOutputBaseDir, false);
                Assert.AreEqual(Path.Combine(observedRoot, "RootOutput"), composition.BmsLibraryOptionsProvider().LR2CustomFolderOutputBaseDirRootType, false);
                Assert.AreEqual(Path.Combine(observedRoot, "Additional"), composition.BmsLibraryOptionsProvider().LR2CustomFolderAdditionalOutputBaseDirs.Single(), false);
                Assert.AreEqual(observedRoot, result.Options.BMSInstallDir, false);
                LibraryFileInitializationResult next = await Task.Run(() => library.ReloadFileDiff(null));
                Assert.AreEqual(observedRoot, next.Options.BMSInstallDir, false);
                Assert.AreEqual(Path.Combine(observedRoot, "Output"), composition.CustomFolderOutputSettingsProvider().LR2CustomFolderOutputBaseDir, false);
                Assert.AreEqual(beforeScore.SourceGeneration, library.GetScoreRuntimeStateForDiagnostics().SourceGeneration);
                Assert.AreEqual(1, publications.Count, "二回目の同じ実返却で登録を再公開しません。");
            }
            finally { library.RequestStop("root-spelling-test"); }
        }, nameof(FileDiffRootSpelling_PublishesConfirmedSettingsAndKeepsNextOutputPlacement));
    }

    /// <summary>設定受理の予約拒否は三sectionとも保存・XML登録・後続準備より前で止まり、候補setterだけでは予約検査を行わない。</summary>
    [TestMethod]
    [DataRow("normal")]
    [DataRow("additional")]
    [DataRow("root")]
    [DoNotParallelize]
    public void OutputConfirmation_ReservedBaseKeepsAllMediaAndBackendUnchanged(string section)
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "reserved-output-acceptance");
            Settings values = OpenPersistedSettings(directory.Path);
            string lr2 = Path.Combine(directory.Path, "LR2");
            string manual = values.BMSRootPath;
            (string SongDbPath, string ConfigPath, string BmsRoot) layout = CreateValidLr2Layout(lr2, [manual]);
            string builtin = Directory.CreateDirectory(Path.Combine(lr2, "LR2files", "CustomFolder")).FullName;
            string sentinel = Path.Combine(builtin, "sentinel.lr2folder");
            File.WriteAllText(sentinel, "builtin");
            values.OperationModeLR2DB = true; values.LR2RootPath = lr2;
            values.LR2SongDBPath = layout.SongDbPath; values.LR2ConfigXmlPath = layout.ConfigPath;
            values.LR2CustomFolderOutputBaseDir = Directory.CreateDirectory(Path.Combine(directory.Path, "Normal")).FullName;
            values.LR2CustomFolderOutputBaseDirRootType = Directory.CreateDirectory(Path.Combine(directory.Path, "Root")).FullName;
            values.LR2CustomFolderAdditionalOutputBaseDirs = "[]"; values.Save();
            var dialogs = new RecordingDialogService { MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
            var failures = new List<Exception>();
            var composition = new ApplicationComposition(runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(), settingsEditSession: new SettingsEditSession(values), settingsDialogService: dialogs,
                reportSettingsApplyFailure: failures.Add, uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                dialogService: UndeclaredDialogs.Create());
            using var harness = SettingsDialogHarness.Create(values, activeLibraryProfile: true, composition: composition);
            byte[] settingsBefore = File.ReadAllBytes(Path.Combine(directory.Path, "user.config"));
            byte[] xmlBefore = File.ReadAllBytes(layout.ConfigPath);
            if (section == "normal") { harness.Dialog.LR2CustomFolderOutputDir = builtin; }
            else if (section == "root") { harness.Dialog.LR2CustomFolderAsRootOutputDir = builtin; }
            else { harness.Dialog.AddCustomFolderAdditionalOutputBaseDirs([builtin]); }
            Assert.AreEqual(0, dialogs.MessageCount, "候補では形式と従来衝突だけを検査します。");
            await harness.Dialog.CommitOutputSettingsAsync(section);
            Assert.AreEqual(1, dialogs.MessageCount);
            Assert.IsNotNull(dialogs.LastMessageText);
            StringAssert.Contains(dialogs.LastMessageText, builtin);
            Assert.AreEqual(0, failures.Count);
            Assert.IsFalse(harness.Output.BackendCalled);
            CollectionAssert.AreEqual(settingsBefore, File.ReadAllBytes(Path.Combine(directory.Path, "user.config")));
            CollectionAssert.AreEqual(xmlBefore, File.ReadAllBytes(layout.ConfigPath));
            Assert.AreEqual("builtin", File.ReadAllText(sentinel));
        }, nameof(OutputConfirmation_ReservedBaseKeepsAllMediaAndBackendUnchanged));
    }

    /// <summary>実section入口で旧禁止設定を順に訂正する。未訂正区分と候補を巻き込まず、追加集合を一件ずつ縮小し、ROOTは現在XMLの禁止exactだけを外して旧物理を保つ。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void OutputCorrection_SectionsAndAdditionalShrinkKeepBuiltinFilesWithoutStartingBackend()
    {
        RunOnUiDispatcher(async () =>
        {
            using var directory = new TemporaryDirectory(CaseWorkspace, "output-section-correction");
            Settings values = OpenPersistedSettings(directory.Path);
            string manual = values.BMSRootPath;
            string lr2 = Path.Combine(directory.Path, "LR2");
            string oldNormal = Path.Combine(lr2, "LR2files");
            string builtin = Path.Combine(oldNormal, "CustomFolder");
            string one = Path.Combine(builtin, "One"), two = Path.Combine(builtin, "Two"), unknown = Path.Combine(builtin, "Unclassified");
            (string SongDbPath, string ConfigPath, string BmsRoot) layout = CreateValidLr2Layout(lr2, [manual, lr2, oldNormal, builtin, one, two, unknown]);
            string[] sentinels = new[] { builtin, one, two, unknown }.Select(path => Path.Combine(path, "sentinel.lr2folder")).ToArray();
            foreach (string path in sentinels) { File.WriteAllText(path, "preserve-old-physical"); }
            values.OperationModeLR2DB = true; values.LR2RootPath = lr2;
            values.LR2SongDBPath = layout.SongDbPath; values.LR2ConfigXmlPath = layout.ConfigPath;
            values.LR2CustomFolderOutputBaseDir = oldNormal; values.LR2CustomFolderOutputBaseDirRootType = builtin;
            values.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([one, two]); values.Save();
            var failures = new List<Exception>();
            var dialogs = new RecordingDialogService { MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK), ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
            var composition = new ApplicationComposition(runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(), settingsEditSession: new SettingsEditSession(values), settingsDialogService: dialogs,
                reportSettingsApplyFailure: failures.Add, uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                dialogService: UndeclaredDialogs.Create());
            using var harness = SettingsDialogHarness.Create(values, composition: composition);
            Lr2ConfigOwner xml = composition.ResolveLr2Config(layout.ConfigPath);
            string normal = Directory.CreateDirectory(Path.Combine(directory.Path, "SafeNormal")).FullName;
            harness.Dialog.LR2CustomFolderOutputDir = normal;
            await harness.Dialog.CommitOutputSettingsAsync("normal");
            Settings Fresh() => TestSettingsFactory.OpenSettings(Path.Combine(directory.Path, "user.config"));
            Assert.AreEqual(normal, Fresh().LR2CustomFolderOutputBaseDir);
            Assert.AreEqual(builtin, Fresh().LR2CustomFolderOutputBaseDirRootType);
            CollectionAssert.AreEqual(new[] { one, two }, CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(Fresh().LR2CustomFolderAdditionalOutputBaseDirs).ToArray());
            string unselected = Path.Combine(directory.Path, "UnselectedNormal");
            harness.Dialog.LR2CustomFolderOutputDir = unselected;
            foreach (string removed in new[] { one, two })
            {
                harness.Dialog.SelectedCustomFolderAdditionalOutputBaseDir = removed;
                harness.Dialog.RemoveSelectedCustomFolderAdditionalOutputBaseDir();
                await harness.Dialog.CommitOutputSettingsAsync("additional");
                CollectionAssert.DoesNotContain(xml.Capture().RegisteredRoots.ToArray(), removed);
                Assert.AreEqual(normal, Fresh().LR2CustomFolderOutputBaseDir);
                Assert.AreEqual(unselected, harness.Dialog.LR2CustomFolderOutputDir);
            }
            string extra = Directory.CreateDirectory(Path.Combine(directory.Path, "SafeExtra")).FullName;
            harness.Dialog.AddCustomFolderAdditionalOutputBaseDirs([extra]);
            await harness.Dialog.CommitOutputSettingsAsync("additional");
            string root = Directory.CreateDirectory(Path.Combine(directory.Path, "SafeRoot")).FullName;
            harness.Dialog.LR2CustomFolderAsRootOutputDir = root;
            await harness.Dialog.CommitOutputSettingsAsync("root");
            Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
            Assert.AreEqual(0, dialogs.MessageCount);
            Assert.IsFalse(harness.Output.BackendCalled);
            Assert.AreEqual(0, harness.State.InitializeCount);
            Assert.IsTrue(harness.Dialog.IsInitialEnvironment);
            Assert.AreEqual(root, Fresh().LR2CustomFolderOutputBaseDirRootType);
            CollectionAssert.AreEqual(new[] { extra }, CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(Fresh().LR2CustomFolderAdditionalOutputBaseDirs).ToArray());
            CollectionAssert.AreEquivalent(new[] { manual, lr2, normal, extra }, xml.Capture().RegisteredRoots.ToArray());
            CollectionAssert.AreEquivalent(xml.Capture().RegisteredRoots.ToArray(), TestLr2ConfigFactory.Load(layout.ConfigPath).GetBMSSearchDirectoriesForChangeTracking().ToArray());
            Assert.AreEqual(root, composition.CustomFolderOutputSettingsProvider().LR2CustomFolderOutputBaseDirRootType);
            foreach (string path in sentinels) { Assert.AreEqual("preserve-old-physical", File.ReadAllText(path)); }
        }, nameof(OutputCorrection_SectionsAndAdditionalShrinkKeepBuiltinFilesWithoutStartingBackend));
    }

    private static Settings OpenPersistedSettings(string root)
    {
        Settings values = TestSettingsFactory.OpenSettings(Path.Combine(root, "user.config"));
        string bms = Directory.CreateDirectory(Path.Combine(root, "library")).FullName;
        values.OperationModeLR2DB = false; values.StandaloneBmsRootPaths = bms; values.BMSRootPath = bms; values.BMSInstallDir = bms;
        values.BeatorajaRootPath = string.Empty; values.UseBeatorajaScoreDb = false; values.EnableBeatorajaBmtOutput = false;
        values.UseExternalPanelImage = false; values.UsePlayeruBMplay = false; values.UsePlayerLR2body = false; values.UsePlayerBMIIDXView = false;
        values.ScanBmsFilesOnStartup = false; values.RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson;
        values.Save(); return values;
    }

    private static void RunOnUiDispatcher(Func<Task> operation, string operationName)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string previousTheme = Settings.Default.AppearanceTheme;
            CultureInfo? previousCulture = Resources.Culture;
            try
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(operation(), operationName);
            }
            finally
            {
                Settings.Default.AppearanceTheme = previousTheme;
                AppThemeService.ApplyTheme(previousTheme);
                if (previousCulture is null)
                {
                    Resources.Culture = null;
                }
                else
                {
                    ResourceService.Current.ChangeCulture(previousCulture.Name);
                }
            }
        });
    }

    private static (string SongDbPath, string ConfigPath, string BmsRoot) CreateValidLr2Layout(
        string root,
        IReadOnlyList<string>? bmsRoots = null)
    {
        string songDbPath = Path.Combine(root, "LR2files", "Database", "song.db");
        string configPath = Path.Combine(root, "LR2files", "Config", "config.xml");
        string bmsRoot = Path.Combine(root, "BMS");
        IReadOnlyList<string> searchRoots = bmsRoots ?? [bmsRoot];
        Directory.CreateDirectory(Path.GetDirectoryName(songDbPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        foreach (string searchRoot in searchRoots)
        {
            Directory.CreateDirectory(searchRoot);
        }
        File.WriteAllBytes(songDbPath, []);
        File.WriteAllText(configPath, "<config><system /><jukebox /></config>");

        LR2Config config = BeMusicSeeker.TestSupport.Components.TestLr2ConfigFactory.Load(configPath);
        config.SetBMSSearchDirectories(searchRoots);
        config.Save(config.ConfigFilePath);
        return (songDbPath, configPath, bmsRoot);
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        Directory.Delete(path, recursive: true);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory(TestWorkspace workspace, string prefix)
        {
            Path = Directory.CreateDirectory(System.IO.Path.Combine(workspace.RootDirectory, "BmsSettings-" + prefix + "-" + Guid.NewGuid().ToString("N"))).FullName;
        }

        internal string Path { get; }

        public void Dispose()
        {
            TryDeleteDirectory(Path);
        }
    }

}
