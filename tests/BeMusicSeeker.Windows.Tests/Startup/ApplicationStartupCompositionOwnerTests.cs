using System.Windows;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ApplicationStartupCompositionOwnerTests
{
    /// <summary>
    /// 所有権・設定準備・VM生成・resource登録・Window生成・MainWindow割当・表示要求の順を保ち、一つのVMとWindowを引き渡す。
    /// </summary>
    /// <remarks>callback引数に必要な実VMとWindowを共有STAで構築する。VMの共有resource購読とMainWindowの復元を所有するため並列実行を制限する。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void StartupCompositionUsesOneViewModelAndPreservesIdentityAndOrder()
    {
        RunWithViewModel((viewModel, window) =>
        {
            var events = new List<string>();
            int factoryCalls = 0;
            MainWindowViewModel? resourceViewModel = null;
            Window? assignedWindow = null;
            Exception? failure = null;

            var owner = new ApplicationStartupCompositionOwner(
                createViewModel: () =>
                {
                    factoryCalls++;
                    events.Add("create");
                    return viewModel;
                },
                assignViewModelResource: resource =>
                {
                    events.Add("resource");
                    resourceViewModel = resource;
                },
                createMainWindow: value =>
                {
                    events.Add("window-create");
                    Assert.AreSame(viewModel, value);
                    return window;
                },
                assignApplicationMainWindow: value =>
                {
                    events.Add("main-window");
                    assignedWindow = value;
                },
                showMainWindow: value =>
                {
                    events.Add("show");
                    Assert.AreSame(window, value);
                },
                handleFailure: exception =>
                {
                    failure = exception;
                    events.Add("failure");
                    return Task.CompletedTask;
                },
                acquireOwnership: () => { events.Add("ownership"); return true; },
                prepareSettings: () => events.Add("prepare"));

            TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.StartAsync(), nameof(ApplicationStartupCompositionOwner));

            Assert.AreEqual(1, factoryCalls);
            CollectionAssert.AreEqual(
                new[] { "ownership", "prepare", "create", "resource", "window-create", "main-window", "show" },
                events);
            Assert.AreSame(viewModel, resourceViewModel);
            Assert.AreSame(window, assignedWindow);
            Assert.IsNull(failure);
        });
    }

    /// <summary>
    /// VMのresource登録が失敗したとき、画面生成・割当・表示へ進まず元例外を失敗経路へ渡す。
    /// </summary>
    /// <remarks>callback引数に必要な実VMとWindowを共有STAで構築する。VMの共有resource購読とMainWindowの復元を所有するため並列実行を制限する。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void StartupCompositionResourceFailureUsesFailureRoute()
    {
        RunWithViewModel((viewModel, window) =>
        {
            var events = new List<string>();
            var expected = new InvalidOperationException("resource failed");
            Exception? handled = null;
            var owner = new ApplicationStartupCompositionOwner(
                createViewModel: () =>
                {
                    events.Add("create");
                    return viewModel;
                },
                assignViewModelResource: _ =>
                {
                    events.Add("resource");
                    throw expected;
                },
                createMainWindow: _ =>
                {
                    events.Add("window-create");
                    return window;
                },
                assignApplicationMainWindow: _ => events.Add("main-window"),
                showMainWindow: _ => events.Add("show"),
                handleFailure: exception =>
                {
                    events.Add("failure");
                    handled = exception;
                    return Task.CompletedTask;
                });

            TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.StartAsync(), nameof(ApplicationStartupCompositionOwner));

            CollectionAssert.AreEqual(new[] { "create", "resource", "failure" }, events);
            Assert.AreSame(expected, handled);
        });
    }

    /// <summary>
    /// MainWindowの割当が失敗したとき、先行構成の順序を保って表示へ進まず元例外を失敗経路へ渡す。
    /// </summary>
    /// <remarks>callback引数に必要な実VMとWindowを共有STAで構築する。VMの共有resource購読とMainWindowの復元を所有するため並列実行を制限する。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void StartupCompositionMainWindowAssignmentFailureUsesFailureRoute()
    {
        RunWithViewModel((viewModel, window) =>
        {
            var events = new List<string>();
            var expected = new InvalidOperationException("main window assignment failed");
            Exception? handled = null;
            var owner = new ApplicationStartupCompositionOwner(
                createViewModel: () =>
                {
                    events.Add("create");
                    return viewModel;
                },
                assignViewModelResource: _ => events.Add("resource"),
                createMainWindow: _ =>
                {
                    events.Add("window-create");
                    return window;
                },
                assignApplicationMainWindow: _ =>
                {
                    events.Add("main-window");
                    throw expected;
                },
                showMainWindow: _ => events.Add("show"),
                handleFailure: exception =>
                {
                    events.Add("failure");
                    handled = exception;
                    return Task.CompletedTask;
                });

            TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.StartAsync(), nameof(ApplicationStartupCompositionOwner));

            CollectionAssert.AreEqual(
                new[] { "create", "resource", "window-create", "main-window", "failure" },
                events);
            Assert.AreSame(expected, handled);
        });
    }

    /// <summary>
    /// 画面表示の要求が失敗したとき、先行構成の順序を保って元例外を失敗経路へ一回渡す。
    /// </summary>
    /// <remarks>callback引数に必要な実VMとWindowを共有STAで構築する。VMの共有resource購読とMainWindowの復元を所有するため並列実行を制限する。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void StartupCompositionShowFailureUsesFailureRoute()
    {
        RunWithViewModel((viewModel, window) =>
        {
            var events = new List<string>();
            var expected = new InvalidOperationException("show failed");
            Exception? handled = null;
            var owner = new ApplicationStartupCompositionOwner(
                createViewModel: () =>
                {
                    events.Add("create");
                    return viewModel;
                },
                assignViewModelResource: _ => events.Add("resource"),
                createMainWindow: _ =>
                {
                    events.Add("window-create");
                    return window;
                },
                assignApplicationMainWindow: _ => events.Add("main-window"),
                showMainWindow: _ =>
                {
                    events.Add("show");
                    throw expected;
                },
                handleFailure: exception =>
                {
                    events.Add("failure");
                    handled = exception;
                    return Task.CompletedTask;
                });

            TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.StartAsync(), nameof(ApplicationStartupCompositionOwner));

            CollectionAssert.AreEqual(
                new[] { "create", "resource", "window-create", "main-window", "show", "failure" },
                events);
            Assert.AreSame(expected, handled);
        });
    }

    /// <summary>表示要求後の環境収集をゲートで保持しても、本体の起動Taskは完了します。収集失敗は診断内の欠測に留めます。</summary>
    /// <remarks>既存VMとWindowの共通寿命を使うため非並列です。ゲートはfinallyで解放し、背景Taskも回収します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void ApplicationEnvironmentCollectionGateDoesNotDelayStartupCompletion()
    {
        RunWithViewModel((viewModel, window) =>
        {
            using var gate = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task diagnostics = Task.CompletedTask;
            string? recorded = null;
            try
            {
                var owner = new ApplicationStartupCompositionOwner(
                    () => viewModel, _ => { }, _ => window, _ => { },
                    _ => diagnostics = ApplicationEnvironmentDiagnostics.Queue("main_window_dpi=unavailable", () =>
                    {
                        entered.TrySetResult();
                        gate.Wait();
                        throw new InvalidOperationException("collection unavailable");
                    }, message => recorded = message),
                    exception => throw new AssertFailedException(exception.ToString()));

                Task startup = owner.StartAsync();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, diagnostics, nameof(ApplicationEnvironmentDiagnostics)),
                    nameof(ApplicationEnvironmentDiagnostics));
                Assert.IsTrue(startup.IsCompletedSuccessfully);
                Assert.IsFalse(diagnostics.IsCompleted);
                Assert.IsFalse(gate.IsSet);
            }
            finally
            {
                gate.Set();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(diagnostics, nameof(ApplicationEnvironmentDiagnostics));
            }
            Assert.IsNotNull(recorded);
            StringAssert.Contains(recorded, "main_window_dpi=unavailable");
            StringAssert.Contains(recorded, "unavailable(InvalidOperationException)");
        });
    }

    private static void RunWithViewModel(Action<MainWindowViewModel, Window> action)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            MainWindowViewModel? viewModel = null;
            ProbeWindow? window = null;
            Window? previousMainWindow = Application.Current.MainWindow;
            try
            {
                viewModel = MainWindowViewModelTestFactory.Create();
                window = new ProbeWindow();
                action(viewModel, window);
            }
            finally
            {
                try { window?.Close(); }
                finally
                {
                    Application.Current.MainWindow = previousMainWindow;
                    viewModel?.SettingDialog.Dispose();
                }
            }
        });
    }

    private sealed class ProbeWindow : Window
    {
    }

}
