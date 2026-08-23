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
                    });

                owner.StartAsync().GetAwaiter().GetResult();

                Assert.AreEqual(1, factoryCalls);
                CollectionAssert.AreEqual(
                    new[] { "create", "resource", "window-create", "main-window", "show" },
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
