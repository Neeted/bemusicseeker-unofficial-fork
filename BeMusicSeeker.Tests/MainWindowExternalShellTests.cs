using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Threading.Tasks;
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
            try
            {
                var composition = new ApplicationComposition(
                    uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
                    applicationLifetime: TestApplicationContext.CreateLifetime(),
                    cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    externalShellGateway: gateway);
                viewModel = composition.CreateMainWindowViewModel();
                object previousVmResource = Application.Current.Resources["vm"];
                Application.Current.Resources["vm"] = viewModel;
                try
                {
                    window = new MainWindow(viewModel);

                    ContextMenu contextMenu = (ContextMenu)window.FindResource("treeViewLibraryFolderContextMenu");
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
                        CloseWindowThroughShutdownWorkflow(window, viewModel);
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

    private static void CloseWindowThroughShutdownWorkflow(MainWindow window, MainWindowViewModel viewModel)
    {
        Task closeRequest = viewModel.ShellShutdownWorkflow.RequestWindowCloseAsync();
        if (!closeRequest.IsCompleted)
        {
            Dispatcher dispatcher = window.Dispatcher;
            var frame = new DispatcherFrame();
            bool watchdogExpired = false;
            var watchdog = new DispatcherTimer(
                TimeSpan.FromSeconds(5),
                DispatcherPriority.ApplicationIdle,
                (_, _) =>
                {
                    watchdogExpired = true;
                    frame.Continue = false;
                },
                dispatcher);
            closeRequest.ContinueWith(_ =>
            {
                dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() => frame.Continue = false));
            });
            watchdog.Start();
            try
            {
                Dispatcher.PushFrame(frame);
            }
            finally
            {
                watchdog.Stop();
            }
            if (watchdogExpired)
            {
                throw new TimeoutException("MainWindow shutdown did not complete within the cleanup watchdog.");
            }
        }

        closeRequest.GetAwaiter().GetResult();
        window.Close();
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
