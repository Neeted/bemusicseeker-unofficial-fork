using System.Reflection;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.TestSupport.Wpf;

/// <summary>実起動の必須UI・任意処理・受理済みTaskとshutdownの同じ所有境界を共用します。</summary>
internal static class StartupCompletionTestFixture
{

    /// <summary>
    /// 次の明示操作の準備として、起動後の表示が非活動かつ実schedulerが全体idleである同じ時点まで待ちます。必須起動成功の条件には使いません。
    /// </summary>
    /// <param name="owner">待機対象の実rootです。</param>
    /// <remarks>
    /// UIスレッドから呼びます。表示の終了は表示の通知、全体idleはschedulerのTaskで待つため、表示終了後に追跡対象外の仕事が終わっても通知を必要としません。
    /// 局所的な期限は設けず、停止は検証全体の制限時間で検出します。
    /// </remarks>
    internal static async Task WaitForStartupBackgroundTerminalAsync(MainWindowViewModel owner)
    {
        StartupBackgroundTaskSchedulerOwner scheduler = GetPrivateField<StartupBackgroundTaskSchedulerOwner>(owner, "startupBackgroundTaskScheduler");
        while (true)
        {
            // 購読を確認より先に行い、確認と待機の間の表示通知を失いません。
            var displayChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => displayChanged.TrySetResult();
            owner.ProgressHub.PropertyChanged += Changed;
            try
            {
                bool displayActive = owner.ProgressHub.IsStartupBackgroundInitializationActive;
                bool fullyIdle = scheduler.IsFullyIdle;
                if (!displayActive && fullyIdle) { return; }
                // 表示が活動中なら表示の通知を、busyなら全体idleのTaskを待ち、どちらかが進んだら論理積を確認し直します。
                var waits = new List<Task>(2);
                if (displayActive) { waits.Add(displayChanged.Task); }
                if (!fullyIdle) { waits.Add(scheduler.WaitForFullyIdleAsync()); }
                await Task.WhenAny(waits);
            }
            finally { owner.ProgressHub.PropertyChanged -= Changed; }
        }
    }

    /// <summary>この終端領域だけで使う実UI Taskのgateです。Arm後の受付外の必須UI一回を保持します。</summary>
    internal sealed class CompletionUiScheduler(IUiScheduler inner) : IUiScheduler
    {
        private bool armed;
        private readonly object scheduledOperationsLock = new();
        private readonly List<Task> scheduledOperations = [];
        internal Func<bool> IsCompletionBoundary { get; set; } = () => false;
        internal TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Arm()
        {
            armed = true;
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public bool IsAvailable => inner.IsAvailable;
        public bool CanExecuteInline => inner.CanExecuteInline;
        public bool CheckAccess() => inner.CheckAccess();
        public IUiScheduledOperation Schedule(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            IUiScheduledOperation operation = inner.Schedule(action, priority);
            lock (scheduledOperationsLock) { scheduledOperations.Add(operation.Completion); }
            return operation;
        }
        /// <summary>生成元の初期化・終了Taskを回収した後、既に登録された実表示Taskを古いsnapshotも含めて待ちます。</summary>
        internal Task JoinScheduledOperationsAsync()
        {
            lock (scheduledOperationsLock) { return Task.WhenAll(scheduledOperations); }
        }
        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => inner.Invoke(action, priority);
        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal) => inner.Invoke(action, priority);
        public async Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            if (armed && IsCompletionBoundary())
            {
                armed = false;
                Entered.TrySetResult();
                await Release.Task;
            }
            await inner.InvokeAsync(action, priority);
        }
        public async Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            if (armed && IsCompletionBoundary())
            {
                armed = false;
                Entered.TrySetResult();
                await Release.Task;
            }
            await inner.InvokeAsync(action, priority);
        }
    }

    /// <summary>通常起動の実構成・DB・L/Pを使い、必須UIと既存任意workだけを到達前に捕捉します。OS一時領域の専有媒体は受理済み処理とshutdownの実終端後に回収します。</summary>
    internal sealed class CompletionFixture : IDisposable
    {
        private readonly TestWorkspace ownedWorkspace = TestWorkspace.Create(Path.GetTempPath());
        private readonly string directory;
        private readonly List<Task> tasks = [];
        private Func<string, string, string, Func<Task>, bool>? wrappedScheduler;
        private readonly bool isolateSortWarmup;
        internal Settings Settings { get; }
        internal ApplicationComposition Composition { get; }
        internal IApplicationLifetimePort Lifetime { get; }
        internal MainWindowViewModel Owner { get; }
        internal TestBmsLibrary Library { get; }
        internal TestBmsPlaylist Playlist { get; }
        internal CompletionUiScheduler Ui { get; } = new(new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher));
        internal CompletionDialogs Dialogs { get; } = new();
        internal RecordingSettingsDialogPresentationPort Presentation { get; } = new();
        internal Exception? OptionsFailure { get; set; }
        internal StartupBackgroundTaskSchedulerOwner Scheduler => GetPrivateField<StartupBackgroundTaskSchedulerOwner>(Owner, "startupBackgroundTaskScheduler");
        internal TaskCompletionSource OptionalEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource OptionalRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object maintenanceRequestGate = new();
        private Func<bool>? deferredMaintenanceRequest;
        private bool maintenanceRequestReleased;
        internal TaskCompletionSource<StartupPostInitializationWarmupCompletion> SortWarmupCompletion { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void ArmSortWarmupGate()
        {
            lock (maintenanceRequestGate)
            {
                Assert.IsNull(deferredMaintenanceRequest, "前回の保守要求を回収してから次の起動へ進みます。");
                maintenanceRequestReleased = false;
                SortWarmupCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        /// <summary>保持した実要求を一度だけ元schedulerへ渡します。拒否を完了として扱わず失敗にします。</summary>
        internal void ReleaseDeferredMaintenance()
        {
            Func<bool>? submit;
            lock (maintenanceRequestGate)
            {
                maintenanceRequestReleased = true;
                submit = deferredMaintenanceRequest;
                deferredMaintenanceRequest = null;
            }
            if (submit != null && !submit())
            {
                throw new AssertFailedException("保留した保守要求が元schedulerに拒否されました。");
            }
        }
        private readonly TaskCompletionSource<Task> optionalWork = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task OptionalWork => optionalWork.Task.Unwrap();
        internal bool OptionalStartedAfterCompletion { get; private set; }
        internal int CompletionNotifications { get; private set; }
        /// <summary>専用設定・DB・既存構成で起動と処理終端を準備します。</summary>
        /// <param name="ownHttpSettings">HTTP利用時は設定もfixtureのdirectoryで所有し、未終端時にAssemblyCleanupで削除されないようにします。</param>
        /// <param name="rootFileEnumerator">実LR2同期にも使う列挙境界。省略時は既存ケースの未接続Everything入力を維持します。</param>
        internal CompletionFixture(IStartupLibraryInitializationFailurePresenter? failurePresenter = null,
            Func<IBMSPlayer>? defaultPlayer = null, bool lr2Mode = false, IApplicationLifetimePort? lifetime = null,
            Action<Exception>? settingsFailureReporter = null, Func<Settings, ISettingsEditSession>? settingsSessionFactory = null, bool isolateSortWarmup = false, IRootFileEnumerator? rootFileEnumerator = null, bool ownHttpSettings = false)
        {
            directory = Path.Combine(ownedWorkspace.RootDirectory, "StartupCompletion-" + Guid.NewGuid().ToString("N"));
            this.isolateSortWarmup = isolateSortWarmup;
            Lifetime = lifetime ?? new TestApplicationLifetime(firstStartup: true);
            Directory.CreateDirectory(directory);
            string lr2DatabaseDirectory = Path.Combine(directory, "LR2files", "Database");
            string lr2ConfigDirectory = Path.Combine(directory, "LR2files", "Config");
            string lr2SongDb = Path.Combine(lr2DatabaseDirectory, "song.db");
            string lr2Config = Path.Combine(lr2ConfigDirectory, "config.xml");
            if (lr2Mode)
            {
                Directory.CreateDirectory(lr2DatabaseDirectory);
                Directory.CreateDirectory(lr2ConfigDirectory);
                File.WriteAllText(lr2Config, "<config><system /><jukebox /></config>");
                File.WriteAllBytes(Path.Combine(directory, "LR2body.exe"), []);
                string songs = Path.Combine(directory, "Songs");
                Directory.CreateDirectory(songs);
                var config = new LR2Config(lr2Config);
                config.AddBMSSearchDirectories([songs]);
                config.Save(config.ConfigFilePath);
                Directory.CreateDirectory(Path.Combine(directory, "NormalOutput"));
                Directory.CreateDirectory(Path.Combine(directory, "RootOutput"));
            }
            Settings = ownHttpSettings
                ? TestSettingsFactory.OpenSettings(Path.Combine(directory, "owner.config"))
                : TestSettingsFactory.CreateIsolatedSettings();
            ConfigureSettings(Settings);
            void ConfigureSettings(Settings values)
            {
                values.OperationModeLR2DB = lr2Mode;
                if (lr2Mode)
                {
                    values.LR2RootPath = directory;
                    values.LR2SongDBPath = lr2SongDb;
                    values.LR2ConfigXmlPath = lr2Config;
                    values.LR2CustomFolderOutputBaseDir = Path.Combine(directory, "NormalOutput");
                    values.LR2CustomFolderOutputBaseDirRootType = Path.Combine(directory, "RootOutput");
                    values.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
                }
                values.BMSRootPath = directory;
                values.StandaloneBmsRootPaths = directory;
                values.BMSInstallDir = lr2Mode ? Path.Combine(directory, "Songs") : directory;
                values.ScanBmsFilesOnStartup = false;
                values.SkipInitPlaylistLoad = true;
                values.EnablePlaylistUrlCompletion = false;
                values.EnableBeatorajaBmtOutput = false;
                values.UseBeatorajaScoreDb = false;
                values.UsePlayeruBMplay = false;
                values.UsePlayerLR2body = false;
                values.UsePlayerBMIIDXView = false;
                values.IsLR2BackupEnabled = false;
                values.TableListURL = UnreachableHttpEndpoint.TableListUrl;
            }
            var path = ApplicationPathSnapshot.FromExecutablePath(Path.Combine(directory, "BeMusicSeeker.exe"));
            Directory.CreateDirectory(path.DataDirectoryPath);
            string songDb = lr2Mode ? lr2SongDb : path.StandaloneSongDbPath;
            Composition = new ApplicationComposition(settingsEditSession: settingsSessionFactory?.Invoke(Settings) ?? new NoOpSettingsEditSession(Settings), reportSettingsApplyFailure: settingsFailureReporter,
                uiScheduler: Ui, applicationLifetime: Lifetime, cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(path), fileDbMutationDialogService: Dialogs, settingsDialogService: Dialogs,
                defaultBmsPlayerFactory: defaultPlayer);
            StartupLibraryConstructionTestSupport.CreateSongDatabase(Composition.DatabaseScope, songDb);
            new BmsLibraryDbGateway(Composition.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), songDb).EnsureAppOwnedSchema();
            PlaylistPersistenceRepository.EnsureSchema(Composition.DatabaseScope, songDb);
            // Libraryの既定値はOwnerと異なるため、別設定のまま同じdirectoryへ所有を移します。
            Settings? librarySettings = ownHttpSettings ? TestSettingsFactory.OpenSettings(Path.Combine(directory, "library.config")) : null;
            if (librarySettings != null) { librarySettings.OperationModeLR2DB = lr2Mode; }
            Library = new TestBmsLibrary(Composition.DatabaseScope, songDb, lr2Mode ? Composition.ResolveLr2Config(lr2Config) : null, null, null, new UnexpectedBmsLibraryDialogService(),
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), CaptureOptions,
                chartFileScanner: CapturedChartFileScanner.FromFixture([], new Dictionary<string, IEnumerable<string>>(), []),
                rootFileEnumerator: rootFileEnumerator,
                operationAdmission: Composition.OperationAdmission, playlistOperationAdmission: Composition.PlaylistOperationAdmission, settings: librarySettings);
            Playlist = MainWindowViewModelTestFactory.CreatePlaylist(Composition.DatabaseScope, songDb, Settings,
                getLr2Config: lr2Mode ? Composition.ResolveLr2Config(lr2Config) : null, library: Library);
            Owner = new MainWindowViewModel(Composition, new CompletionLibraryFactory(Library, Playlist),
                startupLibraryInitializationFailurePresenter: failurePresenter ?? new CallbackFailurePresenter(_ => { }));
            if (isolateSortWarmup)
            {
                StartupPostInitializationWarmupOwner warmup = GetPrivateField<StartupPostInitializationWarmupOwner>(Owner, "startupPostInitializationWarmupOwner");
                FieldInfo callbackField = typeof(StartupPostInitializationWarmupOwner).GetField("completed", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new AssertFailedException("Warmup completion callback is unavailable.");
                var original = (Action<StartupPostInitializationWarmupCompletion>)(callbackField.GetValue(warmup)
                    ?? throw new AssertFailedException("Warmup completion callback is missing."));
                callbackField.SetValue(warmup, (Action<StartupPostInitializationWarmupCompletion>)(completion =>
                {
                    try { original(completion); SortWarmupCompletion.TrySetResult(completion); }
                    catch (Exception failure) { SortWarmupCompletion.TrySetException(failure); throw; }
                }));
            }
            Owner.SettingDialog.AttachPresentationPort(Presentation);
            Owner.PlaylistWorkspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
            Owner.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.IsInitializationCompleted) && Owner.IsInitializationCompleted) { CompletionNotifications++; }
            };
            Ui.IsCompletionBoundary = () => !Composition.OperationAdmission.IsActive && !Composition.PlaylistOperationAdmission.IsActive
                && Owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked;
        }
        private BmsLibraryOptionsSnapshot CaptureOptions()
        {
            if (OptionsFailure != null) { throw OptionsFailure; }
            if (Library?.StartupBackgroundTaskScheduler is { } scheduler && scheduler != wrappedScheduler)
            {
                wrappedScheduler = (name, reason, dependency, work) =>
                {
                    if (isolateSortWarmup && name == "maintenance_hydration")
                    {
                        lock (maintenanceRequestGate)
                        {
                            if (!maintenanceRequestReleased)
                            {
                                Assert.IsNull(deferredMaintenanceRequest, "同じ初期化の保守要求を重複保持しません。");
                                deferredMaintenanceRequest = () => scheduler(name, reason, dependency, work);
                                return true;
                            }
                        }
                    }
                    return scheduler(name, reason, dependency,
                        name == "chart_info_backfill" ? () =>
                        {
                            Task actualWork = RunOptionalWorkAsync(work);
                            optionalWork.TrySetResult(actualWork);
                            return actualWork;
                        }
                    : work);
                };
                Library.StartupBackgroundTaskScheduler = wrappedScheduler;
            }
            return BmsLibraryOptionsSnapshot.CreateCurrent(Settings);
        }
        private async Task RunOptionalWorkAsync(Func<Task> work)
        {
            OptionalStartedAfterCompletion = Owner.IsInitializationCompleted && !Lifetime.IsFirstStartup
                && !Owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked && Scheduler.IsPostInitializationSchedulingComplete;
            OptionalEntered.TrySetResult();
            await OptionalRelease.Task;
            await work();
        }
        internal Task<bool> Start()
        {
            Task<bool> task = Owner.InitializeAsync();
            Track(task);
            return task;
        }
        /// <summary>表示の非活動と実schedulerの全体idleが同時に成り立つまで待ちます。必須成功の条件には使いません。</summary>
        internal Task WaitForBackgroundTerminalAsync() => WaitForStartupBackgroundTerminalAsync(Owner);
        internal void Track(Task task) => tasks.Add(task);
        internal void AssertBeforeUiCompletion()
        {
            Assert.IsFalse(Composition.OperationAdmission.IsActive);
            Assert.IsFalse(Composition.PlaylistOperationAdmission.IsActive);
            Assert.IsFalse(Owner.IsInitializationCompleted);
            Assert.IsFalse(Owner.HasActiveLibraryProfile);
            Assert.IsTrue(Lifetime.IsFirstStartup);
            Assert.IsTrue(Owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
            Assert.IsTrue(Owner.IsLibraryOperationInProgress, "必須UI実Taskの終端前は実成功を公開しません。");
            Assert.IsFalse(Scheduler.IsStarted);
            Assert.IsFalse(Scheduler.IsPostInitializationSchedulingComplete);
            Assert.AreEqual(0, CompletionNotifications);
        }
        /// <summary>
        /// HTTP・受理済み処理・実shutdownの元Taskを同じcleanup期限で回収し、
        /// 一つの実終端確認でWindow・設定VM・一時DBを通常解放します。
        /// 未終端では資源を保持して失敗し、Windowの失敗時最終回収は表示scopeに委ねます。
        /// </summary>
        /// <param name="cleanupToken">呼出し側のfinallyで一度設定した共有cleanup期限。</param>
        /// <param name="ownedHttpTasks">HTTPの元Task・終端通知の監視Taskと全serverの実Completion。</param>
        /// <param name="settingsWindow">同じ実終端確認後に通常閉鎖する設定Window。未作成の場合はnull。</param>
        internal void Dispose(CancellationToken cleanupToken, IEnumerable<Task> ownedHttpTasks, SettingsWindow? settingsWindow = null)
        {
            var failures = new List<Exception>();
            OptionsFailure = null;
            Ui.Release.TrySetResult();
            OptionalRelease.TrySetResult();
            try { ReleaseDeferredMaintenance(); }
            catch (Exception failure) { failures.Add(failure); }
            Task? shutdown = null;
            try { shutdown = Owner.ShellShutdownWorkflow.RequestWindowCloseAsync(); }
            catch (Exception failure) { failures.Add(failure); }
            var owned = Task.WhenAll(tasks.Concat(ownedHttpTasks).Concat(new Task?[] { shutdown }.OfType<Task>()).Distinct());
            try { TestUiDispatcherHost.AwaitTaskOnDispatcher(owned.WaitAsync(cleanupToken), "http-fixture-all-owned-terminal"); }
            catch (Exception failure)
            {
                if (owned.Exception != null) { failures.AddRange(owned.Exception.InnerExceptions); }
                else { failures.Add(failure); }
            }
            // 待機wrapperの完了ではなく、HTTPも含む全元Taskの実終端を共通の解放条件にします。
            if (shutdown != null && owned.IsCompleted)
            {
                try { settingsWindow?.CloseForOwnerShutdown(); }
                catch (Exception failure) { failures.Add(failure); }
                try { Owner.SettingDialog.Dispose(); }
                catch (Exception failure) { failures.Add(failure); }
                try { Directory.Delete(directory, recursive: true); ownedWorkspace.Dispose(); }
                catch (Exception failure) { failures.Add(failure); }
            }
            else
            {
                failures.Add(new InvalidOperationException("HTTPを含むfixtureの所有処理が未回収のため、Windowの通常閉鎖と設定VM・一時DBの解放を行いません。"));
            }
            if (failures.Count > 0) { throw new AggregateException(failures); }
        }

        public void Dispose()
        {
            OptionsFailure = null;
            Ui.Release.TrySetResult();
            OptionalRelease.TrySetResult();
            try { ReleaseDeferredMaintenance(); }
            finally
            {
                var accepted = Task.WhenAll(tasks);
                Task? shutdown = null;
                try { TestUiDispatcherHost.AwaitTaskOnDispatcher(accepted, "startup-completion-fixture-accepted-tasks"); }
                finally
                {
                    try
                    {
                        shutdown = Owner.ShellShutdownWorkflow.RequestWindowCloseAsync();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(shutdown, "startup-completion-fixture-shutdown");
                    }
                    finally
                    {
                        if (accepted.IsCompleted && shutdown is { IsCompleted: true })
                        {
                            Owner.SettingDialog.Dispose();
                            Directory.Delete(directory, recursive: true);
                            ownedWorkspace.Dispose();
                        }
                    }
                }
            }
        }
    }

    private sealed class CompletionLibraryFactory(BMSLibrary library, BMSPlaylist playlist) : IStartupLibraryFactory
    {
        public BMSLibrary CreateBmsLibrary(LibraryProfile profile) => library;
        public BMSPlaylist CreateBmsPlaylist(LibraryProfile profile, BMSLibrary source) => playlist;
    }

    /// <summary>ケースが指定した失敗通知の観測先へ、そのまま配送します。</summary>
    internal sealed class CallbackFailurePresenter(Action<StartupLibraryInitializationFailurePresentation> present) : IStartupLibraryInitializationFailurePresenter
    {
        public void Present(StartupLibraryInitializationFailurePresentation presentation) => present(presentation);
    }
    internal sealed class CompletionDialogs : IUiDialogService
    {
        internal Func<UiMessageRequest, Task>? MessageObservedAsync { get; set; }
        internal MessageBoxResult ConfirmationResult { get; set; } = MessageBoxResult.Yes;
        internal Func<UiConfirmationRequest, Task<UiDialogResult>>? ConfirmationObservedAsync { get; set; }
        internal int ConfirmationCount { get; private set; }
        internal Func<Task<UiWindowDialogResult<PendingPackageRestoreChoice>>>? PendingRestoreObservedAsync { get; set; }
        internal int PendingRestoreCount { get; private set; }
        /// <summary>設定モーダルの表示中の登録を、呼出し側の表示範囲へ接続する境界です。設定モーダルを実表示するケースが、表示範囲の準備を通して設定します。</summary>
        internal Func<Window, IDisposable>? SettingsModalScopeFactory { get; set; }
        public async Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            if (MessageObservedAsync != null) { await MessageObservedAsync(request); }
            return UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);
        }
        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmationCount++;
            return ConfirmationObservedAsync?.Invoke(request) ?? Task.FromResult(UiDialogResult.FromMessageBoxResult(ConfirmationResult));
        }
        public async Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, CancellationToken cancellationToken = default) where TWindow : Window
        {
            // Settingsの型組だけ実モーダルへ渡し、起動復元の固定応答は既存の観測境界に残します。
            if (typeof(TWindow) == typeof(SettingsWindow) && typeof(TResult) == typeof(SettingsWindowCloseReason))
            {
                Func<Window, IDisposable> modalScopeFactory = SettingsModalScopeFactory
                    ?? throw new InvalidOperationException("設定モーダルを実表示するケースは SettingsModalScopeFactory で表示範囲の準備を接続してください。");
                return await new UiDialogCoordinator(new UiDialogOwnerResolver(), modalScopeFactory).ShowWindowAsync(request, cancellationToken);
            }
            if (typeof(TWindow) != typeof(PendingPackageRestoreWindow) || PendingRestoreObservedAsync == null) { throw new NotSupportedException(); }
            PendingRestoreCount++;
            return (UiWindowDialogResult<TResult>)(object)await PendingRestoreObservedAsync();
        }
        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>初期化失敗の実通知を記録し、実Window終了ケースでも新規提示を観測します。</summary>
    internal sealed class RecordingStartupLibraryInitializationFailurePresenter
        : IStartupLibraryInitializationFailurePresenter
    {
        internal List<StartupLibraryInitializationFailurePresentation> Presentations { get; } = new();

        public void Present(StartupLibraryInitializationFailurePresentation presentation)
            => Presentations.Add(presentation);
    }

    /// <summary>実起動fixtureと既存ケースが観測する内部状態を、同じ反射入口から取得します。</summary>
    internal static T GetPrivateField<T>(object target, string name)
    {
        return (T)target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target)!;
    }
}
