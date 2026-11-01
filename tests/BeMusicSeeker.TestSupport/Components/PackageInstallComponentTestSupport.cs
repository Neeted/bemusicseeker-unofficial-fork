using System.Collections.Concurrent;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryInitializationTestSupport;
using static BeMusicSeeker.TestSupport.Components.PackageInstallPlaybackTestSupport;
namespace BeMusicSeeker.TestSupport.Components;

/// <summary>旧テストとCoreテストで同一の入力・期待値・寿命を共有します。</summary>
internal static class PackageInstallComponentTestSupport
{

    /// <summary>実推定処理の準備と適用を、順序を変えずに観測します。</summary>
    internal sealed class AutomaticContinuationObserver : IInstallEstimationExecutionObserver
    {
        internal ConcurrentQueue<string> PreparedPaths { get; } = new();
        public void ObservePreparation(InstallEstimationPreparationObservation observation) => PreparedPaths.Enqueue(observation.PackagePath);
        internal Action? Applied { get; set; }
        public IDisposable BeginWorkItem(InstallEstimationWorkItemObservation observation) => new EmptyScope();
        public void ObserveProgress(InstallEstimationProgressObservation observation) { }
        public void ObserveResultApplied(InstallEstimationAppliedObservation observation) => Applied?.Invoke();
        private sealed class EmptyScope : IDisposable { public void Dispose() { } }
    }

    /// <summary>本番ownerと実推定・実FS/SQLiteを接続し、設定と待合せの寿命を操作終端まで所有する局所fixtureです。</summary>
    internal sealed class AutomaticContinuationFixture : IAsyncDisposable
    {
        private readonly TestWorkspace workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        internal string Root => workspace.RootDirectory;
        internal string LibraryRoot => Path.Combine(Root, "Library");
        internal string Destination => Path.Combine(LibraryRoot, "Song");
        internal string Database => Path.Combine(Root, "song.db");
        private readonly Lr2ConfigOwner lr2Owner;
        private BmsLibraryOptionsSnapshot options;

        /// <summary>既存fixtureの設定切替を、正式ownerの公開配置へも同じ接点で反映します。受理済みsnapshotは変更しません。</summary>
        internal BmsLibraryOptionsSnapshot Options
        {
            get => options;
            set
            {
                lr2Owner.Publish(new Lr2RootChange([], []), value.CaptureOutputPlacement());
                options = value;
            }
        }
        internal AutomaticContinuationObserver Observer { get; } = new();
        internal ArchiveInputFileMutationService Files { get; }
        internal TaskCompletionSource PlaybackEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleasePlayback { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TestBmsLibrary Library { get; }
        internal PackageInstallWorkflowOwner Owner { get; }
        internal PackageInstallFailure? Failure { get; private set; }
        internal PackageInstallCommandResult? Result { get; private set; }
        private readonly ConcurrentQueue<Func<Task>> background = new();
        private readonly HashSet<string> extractedRoots = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>実FSと捕捉する走査結果を同じ内容で準備し、必要な場合だけ最終子配置にも音源を置きます。</summary>
        /// <param name="databaseScope">接続先アプリケーションと共有するDB接続・排他の所有者です。</param>
        /// <param name="mutationPort">本番portの戻り値を観測する場合だけ渡します。省略時は従来と同じ本番portを使います。</param>
        internal AutomaticContinuationFixture(ApplicationDatabaseScope databaseScope, bool enabled, bool deleteSource, bool holdPlayback = false,
            ChartFileOperationSynchronizer? operationAdmission = null, ChartFileOperationSynchronizer? playlistOperationAdmission = null, IUiScheduler? uiScheduler = null, bool nestedAudio = false,
            IEnumerable<string>? destinationSounds = null, bool differentDestinations = false, bool temporaryIngress = false, IEnumerable<string>? installedChartBodies = null,
            IPackageInstallMutationPort? mutationPort = null)
        {
            try
            {
                ArgumentNullException.ThrowIfNull(databaseScope);
                Files = new ArchiveInputFileMutationService(path =>
                {
                    string? extracted = Path.GetDirectoryName(path);
                    if (extracted == null || !TempDirectoryPublisher.IsManagedPath(extracted)) { return; }
                    // この展開のwork rootまで上がり、共有sessionや過去の別実行は対象にしません。
                    while (Path.GetDirectoryName(extracted) is string parent
                        && Path.GetDirectoryName(parent) is string sessionParent && TempDirectoryPublisher.IsManagedPath(sessionParent))
                    {
                        extracted = parent;
                    }
                    extractedRoots.Add(extracted);
                });
                Directory.CreateDirectory(Destination);
                string installed = Path.Combine(Destination, installedChartBodies == null ? "owned.bms" : "owned-0000.bms");
                string[] ownedBodies = (installedChartBodies ?? [CreateValidBmsText("Song")]).ToArray();
                File.WriteAllText(installed, ownedBodies[0], installedChartBodies == null ? Encoding.ASCII : Encoding.GetEncoding("shift_jis"));
                List<string> sounds = [.. destinationSounds ?? ["sound.wav", "a.wav", "b.wav", "previous.wav"]];
                if (nestedAudio)
                {
                    Directory.CreateDirectory(Path.Combine(Destination, "sub"));
                    sounds.Add(Path.Combine("sub", "sound.wav"));
                }
                foreach (string sound in sounds)
                {
                    string path = Path.Combine(Destination, sound);
                    if (Path.GetDirectoryName(path) is string directory) { Directory.CreateDirectory(directory); }
                    File.WriteAllBytes(path, [1]);
                }
                var installedPaths = new List<string> { installed };
                for (int index = 1; index < ownedBodies.Length; index++)
                {
                    string path = Path.Combine(Destination, "owned-" + index.ToString("D4") + ".bms");
                    File.WriteAllText(path, ownedBodies[index], Encoding.GetEncoding("shift_jis"));
                    installedPaths.Add(path);
                }
                var directorySounds = new Dictionary<string, IEnumerable<string>> { [Destination] = sounds };
                if (differentDestinations)
                {
                    string second = Path.Combine(LibraryRoot, "SecondSong");
                    Directory.CreateDirectory(second);
                    string chart = Path.Combine(second, "owned-b.bms");
                    File.WriteAllText(chart, CreateValidBmsText("Song").Replace("sound.wav", "b.wav"), Encoding.ASCII);
                    File.WriteAllBytes(Path.Combine(second, "b.wav"), [1]);
                    installedPaths.Add(chart);
                    directorySounds.Add(second, ["b.wav"]);
                }
                File.WriteAllBytes(Database, []);
                options = CreateOptions(enabled, deleteSource);
                lr2Owner = new Lr2ConfigOwner(TestLr2ConfigFactory.CreateLr2Config(Root, LibraryRoot));
                ChartFileOperationSynchronizer admission = operationAdmission ?? new ChartFileOperationSynchronizer();
                Library = new TestBmsLibrary(databaseScope, Database, lr2Owner, null, Files, new FileDbReportRecordingDialogs(),
                    uiScheduler ?? new TestUiScheduler(() => null), () => Options,
                    CapturedChartFileScanner.FromFixture(installedPaths, directorySounds, directorySounds.Keys),
                    Observer, admission, playlistOperationAdmission, rootFileEnumerator: new FastRootFileEnumerator(), settings: TestBmsFactory.CreateRootSettings([LibraryRoot]))
                {

                    StartupBackgroundTaskScheduler = (_, _, _, work) => { background.Enqueue(work); return true; }
                };
                DroppedInstallIngressMaterializer? materializer = temporaryIngress ? new DroppedInstallIngressMaterializer(
                    Path.Combine(Root, "ExternalTemp"), _ => false,
                    () => { string path = Path.Combine(Root, "Ingress"); Directory.CreateDirectory(path); return path; },
                    path => Directory.Delete(path, recursive: true)) : null;
                Owner = new PackageInstallWorkflowOwner(new FileDbReportRecordingDialogs(), admission, new ChartMutationActivityOwner(),
                    mutationPort ?? new BmsLibraryPackageInstallMutationPort(), new AwaitablePlaybackStop(PlaybackEntered, ReleasePlayback.Task), action => { action(); return true; },
                    droppedInstallIngressMaterializer: materializer);
                Owner.AttachLibrary(Library);
                Owner.CompletionPublished += receipt => Result = receipt.CommandResult;
                Owner.FailurePublished += failure => { Failure = failure; Result = failure.CommandResult; };
                if (!holdPlayback) { ReleasePlayback.SetResult(); }
            }
            catch
            {
                workspace.Dispose();
                throw;
            }
        }

        internal BmsLibraryOptionsSnapshot CreateOptions(bool enabled, bool deleteSource, bool keepNewPending = true, bool lr2 = false, string? managed = null) => new()
        {
            OperationModeLR2DB = lr2,
            LR2CustomFolderOutputBaseDir = managed,
            ScanBmsFilesOnStartup = true,
            AutoInstallAfterAutomaticEstimation = enabled,
            InstallDestinationAutoApplyMinAudioHealth = 100,
            DeletePendingPackageSourceAfterInstall = deleteSource,
            KeepInstallablePackagesPending = keepNewPending,
            BMSInstallDir = LibraryRoot,
            FolderNameFormat = "%TITLE%"
        };
        internal string WriteInput(string directoryName, string chartName, string identity)
        {
            string directory = Path.Combine(Root, directoryName);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, chartName), CreateValidBmsText("Song") + "#GENRE " + identity + "\n", Encoding.ASCII);
            return directory;
        }
        internal async Task InitializeAsync()
        {
            Library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);
            while (background.TryDequeue(out Func<Task>? work)) { await work(); }
        }
        public async ValueTask DisposeAsync()
        {
            ReleasePlayback.TrySetResult();
            try { await Owner.WaitForIdleAsync(); while (background.TryDequeue(out Func<Task>? work)) { await work(); } }
            finally
            {
                Library.RequestShutdown("automatic-continuation-test");
                try
                {
                    foreach (string path in extractedRoots)
                    {
                        if (Directory.Exists(path) || File.Exists(path))
                        {
                            Assert.IsTrue(TempDirectoryPublisher.TryDeleteManagedPath(path), "当該fixtureが捕捉した展開rootを回収します: " + path);
                        }
                    }
                }
                finally { workspace.Dispose(); }
            }
        }
    }

}
