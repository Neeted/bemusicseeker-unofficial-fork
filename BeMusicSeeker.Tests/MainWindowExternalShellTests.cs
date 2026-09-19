using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
// The composed MainWindow resolves its view model through the process-wide WPF resource dictionary.
[DoNotParallelize]
public sealed class MainWindowExternalShellTests
{
    [TestMethod]
    public void LibraryFolderContextMenu_UsesComposedExternalShellGatewayOnlyForExistingDirectory()
    {
        ExceptionDispatchInfo? bodyFailure = null;
        Exception? cleanupFailure = null;
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                nameof(MainWindowExternalShellTests),
                Guid.NewGuid().ToString("N"));
            string directoryPath = Path.Combine(root, "existing");
            string missingDirectoryPath = Path.Combine(root, "missing");
            Directory.CreateDirectory(directoryPath);
            var gateway = new RecordingExternalShellGateway();
            MainWindow? window = null;
            MainWindowViewModel? viewModel = null;
            var lifetime = new MainWindowPresentationTestHarness.PresentationApplicationLifetime();
            try
            {
                var composition = new ApplicationComposition(
                    uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
                    applicationLifetime: lifetime,
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    externalShellGateway: gateway);
                viewModel = composition.CreateMainWindowViewModel();
                object previousVmResource = Application.Current.Resources["vm"];
                Application.Current.Resources["vm"] = viewModel;
                try
                {
                    window = new MainWindow(viewModel);

                    var contextMenu = (ContextMenu)window.FindResource("treeViewLibraryFolderContextMenu");
                    MenuItem openExplorer = contextMenu.Items
                        .OfType<MenuItem>()
                        .Single(item => Equals(item.Header, Resources.Open_folder_explorer));

                    contextMenu.PlacementTarget = new TreeViewItem { Header = missingDirectoryPath };
                    openExplorer.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, openExplorer));

                    Assert.AreEqual(0, gateway.OpenedDirectoryPaths.Count);
                    Assert.AreEqual(0, gateway.SelectedFilePaths.Count);

                    contextMenu.PlacementTarget = new TreeViewItem { Header = directoryPath };
                    openExplorer.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, openExplorer));

                    CollectionAssert.AreEqual(new[] { directoryPath }, gateway.OpenedDirectoryPaths);
                    Assert.AreEqual(0, gateway.SelectedFilePaths.Count);
                }
                finally
                {
                    if (previousVmResource == null)
                    {
                        Application.Current.Resources.Remove("vm");
                    }
                    else
                    {
                        Application.Current.Resources["vm"] = previousVmResource;
                    }
                }
            }
            catch (Exception exception)
            {
                bodyFailure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                if (window != null && viewModel != null)
                {
                    try
                    {
                        CloseWindowThroughShutdownWorkflow(window, viewModel, lifetime);
                    }
                    catch (Exception exception)
                    {
                        cleanupFailure ??= exception;
                    }
                }
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }
            }

            if (bodyFailure != null)
            {
                if (cleanupFailure != null)
                {
                    throw new AggregateException(
                        "The shell assertion failed and cleanup also failed.",
                        bodyFailure.SourceException,
                        cleanupFailure);
                }
                bodyFailure.Throw();
            }
            if (cleanupFailure != null)
            {
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }
        });
    }

    [TestMethod]
    public void ChartPackageMutationActivity_BlocksContextMenuOpenAndMutationCommands()
    {
        ExceptionDispatchInfo? bodyFailure = null;
        Exception? cleanupFailure = null;
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            MainWindow? window = null;
            MainWindowViewModel? viewModel = null;
            var lifetime = new MainWindowPresentationTestHarness.PresentationApplicationLifetime();
            try
            {
                var composition = new ApplicationComposition(
                    uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
                    applicationLifetime: lifetime,
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    externalShellGateway: new RecordingExternalShellGateway());
                viewModel = composition.CreateMainWindowViewModel();
                object previousVmResource = Application.Current.Resources["vm"];
                Application.Current.Resources["vm"] = viewModel;
                try
                {
                    window = new MainWindow(viewModel);
                    var operationNotifications = new List<string>();
                    viewModel.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(MainWindowViewModel.IsLibraryOperationInProgress))
                        {
                            operationNotifications.Add(args.PropertyName!);
                        }
                    };

                    Assert.IsFalse(viewModel.IsLibraryOperationInProgress);
                    Assert.IsTrue(viewModel.FolderAutoRenameWorkflow.IsIdle);
                    using (viewModel.ChartMutationActivity.Enter())
                    {
                        Assert.IsTrue(viewModel.ChartMutationActivity.IsActive);
                        Assert.IsTrue(viewModel.IsLibraryOperationInProgress);
                        Assert.AreEqual(1, operationNotifications.Count);

                        var tableContextMenu = (ContextMenu)window.FindResource("tableContextMenu");
                        MenuItem autoRenameMenuItem = tableContextMenu.Items
                            .OfType<MenuItem>()
                            .Single(item => item.Name == "tableContextMenuItemAutoRenameFolder");

                        var openedArgs = new RoutedEventArgs(ContextMenu.OpenedEvent, tableContextMenu);
                        tableContextMenu.RaiseEvent(openedArgs);
                        Assert.IsTrue(openedArgs.Handled);

                        var clickArgs = new RoutedEventArgs(MenuItem.ClickEvent, autoRenameMenuItem);
                        autoRenameMenuItem.RaiseEvent(clickArgs);
                        Assert.IsTrue(clickArgs.Handled);
                        Assert.IsTrue(viewModel.FolderAutoRenameWorkflow.IsIdle);
                        Assert.IsFalse(viewModel.FolderAutoRenameWorkflow.IsActive);
                    }

                    Assert.IsFalse(viewModel.ChartMutationActivity.IsActive);
                    Assert.IsFalse(viewModel.IsLibraryOperationInProgress);
                    Assert.AreEqual(2, operationNotifications.Count);
                }
                finally
                {
                    if (previousVmResource == null)
                    {
                        Application.Current.Resources.Remove("vm");
                    }
                    else
                    {
                        Application.Current.Resources["vm"] = previousVmResource;
                    }
                }
            }
            catch (Exception exception)
            {
                bodyFailure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                if (window != null && viewModel != null)
                {
                    try
                    {
                        CloseWindowThroughShutdownWorkflow(window, viewModel, lifetime);
                    }
                    catch (Exception exception)
                    {
                        cleanupFailure ??= exception;
                    }
                }
            }

            if (bodyFailure != null)
            {
                if (cleanupFailure != null)
                {
                    throw new AggregateException(
                        "The shell assertion failed and cleanup also failed.",
                        bodyFailure.SourceException,
                        cleanupFailure);
                }
                bodyFailure.Throw();
            }
            if (cleanupFailure != null)
            {
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }
        });
    }

    private static void CloseWindowThroughShutdownWorkflow(
        MainWindow window,
        MainWindowViewModel viewModel,
        MainWindowPresentationTestHarness.PresentationApplicationLifetime lifetime)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        try
        {
            window.Close();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(lifetime.ShutdownRequested.Task, "external-shell-terminal");
            window.Close();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(closed.Task, "external-shell-closed");
        }
        finally
        {
            viewModel.SettingDialog.Dispose();
        }
    }
    private sealed class RecordingExternalShellGateway : IExternalShellGateway
    {
        internal List<string> OpenedDirectoryPaths { get; } = [];

        internal List<string> SelectedFilePaths { get; } = [];

        public void Open(ExternalShellRequest request)
        {
        }

        public ExplorerOpenResult OpenFileAndSelect(string filePath)
        {
            SelectedFilePaths.Add(filePath);
            return new();
        }

        public ExplorerOpenResult OpenDirectory(string directoryPath)
        {
            OpenedDirectoryPaths.Add(directoryPath);
            return new ExplorerOpenResult
            {
                Kind = ExplorerOpenResultKind.OpenedDirectory,
                RequestedPath = directoryPath,
                OpenedPath = directoryPath
            };
        }

        public bool TryOpenDirectoryWithExplorerProcess(string directoryPath, out string failureReason)
        {
            failureReason = string.Empty;
            return true;
        }
    }
}
