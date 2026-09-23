using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ApplicationStartupCompositionOwnerTests
{
    // PORTABLE-SETTINGS-FAILURE-20260905 P05: actual owner task is the completion signal.
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

    [TestMethod]
    public void StartupCompositionUsesOneViewModelAndPreservesIdentityAndOrder()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            MainWindowViewModel? viewModel = null;
            try
            {
                viewModel = MainWindowViewModelTestFactory.Create();
                var window = new ProbeWindow();
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
                        return viewModel!;
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

                owner.StartAsync().GetAwaiter().GetResult();

                Assert.AreEqual(1, factoryCalls);
                CollectionAssert.AreEqual(
                    new[] { "ownership", "prepare", "create", "resource", "window-create", "main-window", "show" },
                    events);
                Assert.AreSame(viewModel, resourceViewModel);
                Assert.AreSame(window, assignedWindow);
                Assert.IsFalse(window.IsVisible);
                Assert.IsNull(failure);
            }
            finally
            {
                viewModel?.SettingDialog.Dispose();
            }
        });
    }

    [TestMethod]
    public void StartupCompositionCreationFailureUsesFailureRoute()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            var events = new List<string>();
            var expected = new InvalidOperationException("create failed");
            Exception? handled = null;
            var owner = new ApplicationStartupCompositionOwner(
                createViewModel: () =>
                {
                    events.Add("create");
                    throw expected;
                },
                assignViewModelResource: _ => events.Add("resource"),
                createMainWindow: _ =>
                {
                    events.Add("window-create");
                    return new ProbeWindow();
                },
                assignApplicationMainWindow: _ => events.Add("main-window"),
                showMainWindow: _ => events.Add("show"),
                handleFailure: exception =>
                {
                    events.Add("failure");
                    handled = exception;
                    return Task.CompletedTask;
                });

            owner.StartAsync().GetAwaiter().GetResult();

            CollectionAssert.AreEqual(new[] { "create", "failure" }, events);
            Assert.AreSame(expected, handled);
        });
    }

    [TestMethod]
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

            owner.StartAsync().GetAwaiter().GetResult();

            CollectionAssert.AreEqual(new[] { "create", "resource", "failure" }, events);
            Assert.AreSame(expected, handled);
        });
    }

    [TestMethod]
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

            owner.StartAsync().GetAwaiter().GetResult();

            CollectionAssert.AreEqual(
                new[] { "create", "resource", "window-create", "main-window", "failure" },
                events);
            Assert.AreSame(expected, handled);
        });
    }

    [TestMethod]
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

            owner.StartAsync().GetAwaiter().GetResult();

            CollectionAssert.AreEqual(
                new[] { "create", "resource", "window-create", "main-window", "show", "failure" },
                events);
            Assert.AreSame(expected, handled);
        });
    }

    private static void RunWithViewModel(Action<MainWindowViewModel, Window> action)
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            MainWindowViewModel? viewModel = null;
            try
            {
                viewModel = MainWindowViewModelTestFactory.Create();
                action(viewModel, new ProbeWindow());
            }
            finally
            {
                viewModel?.SettingDialog.Dispose();
            }
        });
    }

    private sealed class ProbeWindow : Window
    {
    }
}
