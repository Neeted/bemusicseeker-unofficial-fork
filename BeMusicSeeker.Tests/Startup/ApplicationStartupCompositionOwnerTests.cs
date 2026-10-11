using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ApplicationStartupCompositionOwnerTests
{
    /// <summary>
    /// 単一起動の所有権を取得できなければ設定準備・構成・失敗表示を一つも開始せず起動を終端する。
    /// </summary>
    [TestMethod]
    public async Task OwnershipDenialSkipsEverySettingsAndCompositionStep()
    {
        int preparations = 0, compositions = 0, failures = 0;
        var owner = new ApplicationStartupCompositionOwner(
            () => { compositions++; return null!; }, _ => Assert.Fail(), _ => throw new AssertFailedException(),
            _ => Assert.Fail(), _ => Assert.Fail(),
            _ => { failures++; return Task.CompletedTask; },
            acquireOwnership: () => false, prepareSettings: () => preparations++);

        await owner.StartAsync();

        Assert.AreEqual(0, preparations);
        Assert.AreEqual(0, compositions);
        Assert.AreEqual(0, failures);
    }

    /// <summary>
    /// 所有権取得後に設定準備が失敗したとき、構成を開始せず元例外を一回だけ終端経路へ渡す。
    /// </summary>
    [TestMethod]
    public async Task PreparationFailureUsesTerminalRouteOnceWithoutComposition()
    {
        var expected = new System.IO.IOException("settings inaccessible");
        var events = new List<string>();
        var owner = new ApplicationStartupCompositionOwner(
            () => { events.Add("compose"); return null!; }, _ => Assert.Fail(), _ => throw new AssertFailedException(),
            _ => Assert.Fail(), _ => Assert.Fail(),
            exception => { Assert.AreSame(expected, exception); events.Add("terminal"); return Task.CompletedTask; },
            acquireOwnership: () => { events.Add("ownership"); return true; },
            prepareSettings: () => { events.Add("prepare"); throw expected; });

        await owner.StartAsync();

        CollectionAssert.AreEqual(new[] { "ownership", "prepare", "terminal" }, events);
    }

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
    /// VM生成の失敗では後続構成を開始せず元例外を一回渡し、失敗handlerの実終端まで起動Taskを完了しない。
    /// </summary>
    [TestMethod]
    public async Task StartupCompositionCreationFailureUsesFailureRoute()
    {
        var events = new List<string>();
        var expected = new InvalidOperationException("create failed");
        Exception? handled = null;
        var failureReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new ApplicationStartupCompositionOwner(
            createViewModel: () => { events.Add("create"); throw expected; },
            assignViewModelResource: _ => Assert.Fail("生成失敗後にresource登録を開始しません。"),
            createMainWindow: _ => throw new AssertFailedException("生成失敗後にWindowを構築しません。"),
            assignApplicationMainWindow: _ => Assert.Fail("生成失敗後にMainWindowを割り当てません。"),
            showMainWindow: _ => Assert.Fail("生成失敗後に表示を要求しません。"),
            handleFailure: async exception =>
            {
                events.Add("failure");
                handled = exception;
                failureReceived.TrySetResult();
                await releaseFailure.Task;
            });

        Task startup = owner.StartAsync();
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(failureReceived.Task, startup, nameof(owner));
            Assert.IsFalse(startup.IsCompleted);
            releaseFailure.TrySetResult();
            await startup;
            CollectionAssert.AreEqual(new[] { "create", "failure" }, events);
            Assert.AreSame(expected, handled);
        }
        finally
        {
            releaseFailure.TrySetResult();
            await startup;
        }
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
