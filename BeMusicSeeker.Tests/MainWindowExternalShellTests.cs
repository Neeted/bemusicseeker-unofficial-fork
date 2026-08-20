using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    public void LibraryFolderContextMenu_OpensDirectoryThroughComposedExternalShellGateway()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            string directoryPath = Path.Combine(
                Path.GetTempPath(),
                nameof(MainWindowExternalShellTests),
                Guid.NewGuid().ToString("N"));
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
                    var placementTarget = new TreeViewItem { Header = directoryPath };
                    contextMenu.PlacementTarget = placementTarget;
                    MenuItem openExplorer = contextMenu.Items
                        .OfType<MenuItem>()
                        .Single(item => Equals(item.Header, Resources.Open_folder_explorer));

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
            finally
            {
                if (window != null && viewModel != null)
                {
                    CloseWindowThroughShutdownWorkflow(window, viewModel);
                }
                if (Directory.Exists(directoryPath))
                {
                    Directory.Delete(directoryPath, recursive: true);
                }
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
            closeRequest.ContinueWith(_ =>
            {
                dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() => frame.Continue = false));
            });
            Dispatcher.PushFrame(frame);
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
