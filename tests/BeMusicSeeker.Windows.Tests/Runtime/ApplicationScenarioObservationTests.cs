using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using BeMusicSeeker.Models;
using BeMusicSeeker.Runtime;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Windows.Tests.Runtime;

/// <summary>実Dispatcher・typed pipe・controlの観測寿命を検査します。各familyのUI入力とraw保存はDesktopが分担します。</summary>
[TestClass]
[DoNotParallelize]
public sealed class ApplicationScenarioObservationTests
{
    /// <summary>登録ACK・別Await・早期完了・重複拒否・一回消費・破棄を実通信で確認し、投影を必要としないqueryが行を実体化する退行を検出します。</summary>
    [TestMethod]
    public void RegistrationConsumesOnlyItsTaskAndDoesNotProjectRows()
        => Run(async (model, peer) =>
        {
            var table = new BMSTable { playlist_id = 17 };
            var entry = new BMSTableEntry { parent = table, md5 = new string('a', 32), folder = "folder" };
            var sourceRow = new PlaylistDetailSourceRow(entry, null, entryOrder: 3);
            var source = new PlaylistDetailVirtualView([sourceRow], observeWork: work =>
            {
                if (work == "playlist_detail_materialized") { throw new InvalidOperationException("Query projected a row."); }
            });
            model.MainChartList.Rows = source;
            var target = new ScenarioCellEditTarget(source.GetAutomationItem(0)?.AutomationId ?? throw new InvalidOperationException(), "memo", "value", true);
            var state = (ScenarioTableStateCompleted)await peer.Send(new ScenarioTableStateCommand(1));
            Assert.AreEqual(1, state.State.Count);
            Assert.AreEqual(0, state.State.RealizedRowCount);
            var registered = (ScenarioObservationRegistered)await peer.Send(new ScenarioRegisterObservationCommand(2, target));
            Assert.AreEqual(state.State, registered.TableState);
            Assert.IsInstanceOfType<ScenarioCommandFailed>(await peer.Send(new ScenarioRegisterObservationCommand(3, target)));
            var row = new PlaylistDetailRow(sourceRow);
            var request = new MainChartListCellEditEndedEventArgs(new(row, "memo", model.MainChartList.CurrentOperationContext.SourceScope,
                model.MainChartList.CurrentOperationContext.OperationSection), "value", true);
            ScenarioUiOperationInput input = ScenarioUiOperationInput.Capture(model, "cell-edit", [row], edit: request) ?? throw new InvalidOperationException();
            model.PublishScenarioOperation(input, Task.CompletedTask);
            var completed = (ScenarioObservationCompleted)await peer.Send(new ScenarioAwaitObservationCommand(4, 2));
            Assert.AreEqual(2L, completed.RegistrationRequestId);
            Assert.AreEqual(4L, completed.RequestId);
            Assert.IsNull(completed.Result.TaskFailure);
            Assert.AreEqual(0, source.RealizedRowCount);
            Assert.IsInstanceOfType<ScenarioCommandFailed>(await peer.Send(new ScenarioAwaitObservationCommand(5, 2)));
            await peer.Send(new ScenarioRegisterObservationCommand(6, target));
            Assert.IsInstanceOfType<ScenarioObservationCanceled>(await peer.Send(new ScenarioCancelObservationCommand(7, 6)));
            Assert.IsInstanceOfType<ScenarioCommandFailed>(await peer.Send(new ScenarioAwaitObservationCommand(8, 6)));
            await peer.Send(new ScenarioRegisterObservationCommand(9, target));
            await peer.Write(new ScenarioInitialPlaylistSyncCommand(10));
            // 同じreaderから後続のDispatcher要求が応答した時点で、先行する初回同期待機は受付済みです。
            Assert.IsInstanceOfType<ScenarioTableStateCompleted>(await peer.Send(new ScenarioTableStateCommand(11)));
            Task pendingInitialSync = await peer.CapturePendingInitialSyncAsync();
            Assert.IsFalse(pendingInitialSync.IsCompleted, "初回同期を開始していないため、受理済みの待機commandが残ります。");
            await peer.DisconnectAsync();
            await pendingInitialSync.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.IsFalse(model.HasScenarioOperationObservers, "EOFのreader終端はDispatcher上の購読解除を含みます。");
        });

    /// <summary>捕捉前のsource交換・別入力を失敗として固定し、捕捉後の画面交換では元Taskの実例外を回収します。</summary>
    [TestMethod]
    [DataRow("source-before")]
    [DataRow("wrong-input")]
    [DataRow("source-after")]
    public void ObservationKeepsOriginalFailureAcrossDisplayChanges(string condition)
        => Run(async (model, peer) =>
        {
            var entry = new BMSTableEntry { parent = new BMSTable { playlist_id = 18 }, md5 = new string('b', 32) };
            var sourceRow = new PlaylistDetailSourceRow(entry, null, entryOrder: 1);
            var source = new PlaylistDetailVirtualView([sourceRow]);
            model.MainChartList.Rows = source;
            string id = source.GetAutomationItem(0)?.AutomationId ?? throw new InvalidOperationException();
            await peer.Send(new ScenarioRegisterObservationCommand(1, new ScenarioCellEditTarget(id, "memo", "expected", true)));
            if (condition == "source-before") { model.MainChartList.Rows = new PlaylistDetailVirtualView([sourceRow]); }
            var row = new PlaylistDetailRow(sourceRow);
            var request = new MainChartListCellEditEndedEventArgs(new(row, "memo", model.MainChartList.CurrentOperationContext.SourceScope,
                model.MainChartList.CurrentOperationContext.OperationSection), condition == "wrong-input" ? "different" : "expected", true);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                ScenarioUiOperationInput input = ScenarioUiOperationInput.Capture(model, "cell-edit", [row], edit: request) ?? throw new InvalidOperationException();
                model.PublishScenarioOperation(input, gate.Task);
                if (condition == "source-after")
                {
                    await peer.Write(new ScenarioAwaitObservationCommand(2, 1));
                    await peer.Write(new ScenarioAwaitObservationCommand(3, 1));
                    ScenarioMessage duplicate = await peer.Read();
                    Assert.IsInstanceOfType<ScenarioCommandFailed>(duplicate);
                    Assert.AreEqual(3L, duplicate.RequestId);
                    model.MainChartList.Rows = new List<object>();
                    gate.SetException(new IOException("original-task-failure"));
                    var result = (ScenarioObservationCompleted)await peer.Read();
                    Assert.AreEqual("original-task-failure", result.Result.TaskFailure?.Message);
                }
                else
                {
                    gate.SetResult();
                    model.PublishScenarioOperation(input, Task.CompletedTask);
                    Assert.IsInstanceOfType<ScenarioCommandFailed>(await peer.Send(new ScenarioAwaitObservationCommand(2, 1)));
                }
            }
            finally
            {
                gate.TrySetResult();
                try { await gate.Task; } catch (IOException) { }
            }
        });

    /// <summary>Attachの実購読が最初のInitializeの外側Taskとreceiptを一回消費し、後発同期を初回へ付け替えないことを確認します。</summary>
    [TestMethod]
    public void InitialPlaylistSyncUsesAttachedOwnerAndConsumesOnce()
        => TestUiDispatcherHost.Invoke(() =>
        {
            using (var fixture = new StartupCompletionTestFixture.CompletionFixture())
            {
                fixture.OptionalRelease.TrySetResult();
                // 必須player HWND表示はこの観測境界の対象外なので、既存fixtureの必須起動をWindow接続前に完了する。
                // fixtureは初回外部同期をskipし、Attach後の最初の実Initialize Taskだけを以下で開始する。
                Task<bool> startup = fixture.Start();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(startup, "observation-startup-preparation");
                Assert.IsTrue(startup.GetAwaiter().GetResult());
                // 空の実storeを構成する必須Taskだけを前提とする。起動後idleは同期観測の条件にしない。
                Run(async (model, peer) =>
                {
                    Task? rejected = null;
                    model.PlaylistWorkspace.PlaylistExternalSyncQueued += (_, request) =>
                    {
                        if (request.Reason == "Initialize")
                        { rejected = model.PlaylistWorkspace.RunExternalPlaylistSyncAsync("Initialize", false, true, 0); }
                    };
                    model.PlaylistWorkspace.QueueExternalPlaylistSync("Initialize", false, true, 0);
                    var initial = (ScenarioInitialPlaylistSyncCompleted)await peer.Send(new ScenarioInitialPlaylistSyncCommand(1));
                    Assert.AreEqual("Initialize", initial.Reason);
                    Assert.IsTrue(initial.Version > 0);
                    Assert.IsTrue(initial.Succeeded);
                    Assert.IsFalse(initial.WasSkipped);
                    Assert.IsNull(initial.TaskFailure);
                    Assert.IsNotNull(rejected);
                    await rejected;
                    Assert.IsTrue(fixture.Playlist.TryEnterPlaylistMutation(out IDisposable released));
                    released.Dispose();
                    var laterStarted = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
                    void LaterStarted(string reason, Task operation)
                    {
                        if (reason == "later") { laterStarted.TrySetResult(operation); }
                    }
                    model.PlaylistWorkspace.ExternalPlaylistSyncStarted += LaterStarted;
                    try
                    {
                        model.PlaylistWorkspace.QueueExternalPlaylistSync("later", false, true, 0);
                        Task later = await laterStarted.Task;
                        await later;
                        Assert.IsInstanceOfType<ScenarioCommandFailed>(await peer.Send(new ScenarioInitialPlaylistSyncCommand(2)));
                    }
                    finally { model.PlaylistWorkspace.ExternalPlaylistSyncStarted -= LaterStarted; }
                }, fixture.Owner);
            }
        });

    /// <summary>storeなしで正常終端した最初のTaskへ後発receiptを補完せず、実Attachからの観測を非成功にします。</summary>
    [TestMethod]
    public void InitialPlaylistSyncWithoutReceiptRemainsFailure()
        => Run(async (model, peer) =>
        {
            StartupBackgroundTaskSchedulerOwner scheduler = StartupCompletionTestFixture.GetPrivateField<StartupBackgroundTaskSchedulerOwner>(model, "startupBackgroundTaskScheduler");
            scheduler.Reset(startImmediately: true);
            scheduler.MarkRequiredInitializationSchedulingComplete();
            scheduler.MarkPostInitializationSchedulingComplete();
            model.PlaylistWorkspace.QueueExternalPlaylistSync("Initialize", false, true, 0);
            Assert.IsInstanceOfType<ScenarioCommandFailed>(await peer.Send(new ScenarioInitialPlaylistSyncCommand(1)));
            model.PlaylistWorkspace.QueueExternalPlaylistSync("Initialize", false, true, 0);
            Assert.IsInstanceOfType<ScenarioCommandFailed>(await peer.Send(new ScenarioInitialPlaylistSyncCommand(2)));
        });

    private static void Run(Func<MainWindowViewModel, Peer, Task> body, MainWindowViewModel? preparedModel = null)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        TestUiDispatcherHost.Invoke(() =>
        {
            MainWindowViewModel model = preparedModel ?? MainWindowViewModelTestFactory.Create();
            // 通信観測のfixtureでは、実Window constructorが開始する無関係な更新HTTPを受付前に閉じる。
            model.StartupUpdateWorkflow.NotifyClosing();
            using var lifetime = new MainWindowTestLifetime(model, Task.CompletedTask);
            MainWindow window = lifetime.CreateWindow(() => new MainWindow(model));
            lifetime.Run(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(ExecuteAsync(), "scenario-observation"));
            async Task ExecuteAsync()
            {
                string name = "observation-" + Guid.NewGuid().ToString("N");
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                Task connection = server.WaitForConnectionAsync();
                Task<ApplicationScenarioControl> connecting = ApplicationScenarioControl.ConnectAsync(
                    new ApplicationTestSession(workspace.GetPath("manifest.json"), "run", "case", name), TestUiDispatcherHost.Dispatcher);
                await connection;
                // 接続完了はhello書込みを含むため、実driver同様にserver readerを先に開始します。
                // reader寿命を待つこのfixture内だけの反射です。通常のCompletion境界が整えば置換します。
                // 通信待機のTaskだけを検査し、業務構造を固定しません。
                using var peer = new Peer(server, () => connecting);
                Task<ScenarioMessage> hello = peer.Read();
                ApplicationScenarioControl control = await connecting;
                Assert.IsInstanceOfType<ScenarioConnected>(await hello);
                control.Attach(model, window);
                Exception? primary = null;
                try { await body(model, peer); }
                catch (Exception failure) { primary = failure; throw; }
                finally
                {
                    try { await control.DisposeAsync(); }
                    catch (Exception cleanup) when (primary != null) { primary.Data["ControlCleanupFailure"] = cleanup; }
                }
                Assert.IsFalse(model.HasScenarioOperationObservers, "終了時に一回観測の購読を残しません。");
            }
        });
    }

    private sealed class Peer : IDisposable
    {
        private readonly StreamReader reader;
        private readonly StreamWriter writer;
        private readonly NamedPipeServerStream pipe;
        private readonly Func<Task<ApplicationScenarioControl>> control;
        private bool disposed;
        internal Peer(NamedPipeServerStream stream, Func<Task<ApplicationScenarioControl>> control)
        {
            pipe = stream;
            this.control = control;
            reader = new(stream, new UTF8Encoding(false), leaveOpen: true);
            writer = new(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        }
        internal async Task<ScenarioMessage> Send(ScenarioCommand command)
        {
            await Write(command);
            return await Read();
        }
        internal Task Write(ScenarioCommand command)
        {
            return writer.WriteLineAsync(JsonSerializer.Serialize(command));
        }
        internal async Task<ScenarioMessage> Read()
        {
            string line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) ?? throw new EndOfStreamException();
            return JsonSerializer.Deserialize<ScenarioMessage>(line) ?? throw new InvalidDataException();
        }
        internal async Task DisconnectAsync()
        {
            Dispose();
            pipe.Disconnect();
            await StartupCompletionTestFixture.GetPrivateField<Task>(await control(), "reading").WaitAsync(TimeSpan.FromSeconds(30));
        }
        /// <summary>未開始の初回同期を含む受理済み通信Taskを、切断前にこのcaseの回収対象として固定します。</summary>
        internal async Task<Task> CapturePendingInitialSyncAsync()
        {
            ApplicationScenarioControl owner = await control();
            HashSet<Task> commands = StartupCompletionTestFixture.GetPrivateField<HashSet<Task>>(owner, "commands");
            lock (commands) { return Task.WhenAll(commands.ToArray()); }
        }
        public void Dispose()
        {
            if (disposed) { return; }
            disposed = true;
            reader.Dispose();
            writer.Dispose();
        }
    }
}
