using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Windows;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MainWindowViewSettingsBoundaryTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = new();
    [TestMethod]
    public void ViewSettingsStoreCapturesViewStateAndForwardsDraftChanges()
    {
        Settings settings = testSettings;
        double originalTreeViewWidth = settings.TreeViewWidth;
        bool originalStartupSelection = settings.StartupSelectInstallPending;
        double originalRowHeight = settings.CustomTableRowHeight;
        double originalHeaderHeight = settings.CustomTableHeaderHeight;
        double originalFontSize = settings.CustomTableFontSize;
        try
        {
            settings.TreeViewWidth = 280d;
            settings.StartupSelectInstallPending = false;
            settings.CustomTableRowHeight = 22d;
            settings.CustomTableHeaderHeight = 24d;
            settings.CustomTableFontSize = 12d;

            var store = new SettingsMainWindowViewSettingsStore(() => settings);
            int fontSizeNotifications = 0;
            store.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(IMainWindowViewSettingsStore.CustomTableFontSize))
                {
                    fontSizeNotifications++;
                }
            };

            Assert.AreEqual(280d, store.TreeViewWidth);
            Assert.IsFalse(store.StartupSelectInstallPending);
            Assert.AreEqual(22d, store.CustomTableRowHeight);
            Assert.AreEqual(24d, store.CustomTableHeaderHeight);
            Assert.AreEqual(12d, store.CustomTableFontSize);

            store.CaptureTreeViewWidth(320d, 280d);
            Assert.AreEqual(320d, settings.TreeViewWidth);

            settings.CustomTableFontSize = 13d;
            Assert.AreEqual(13d, store.CustomTableFontSize);
            Assert.AreEqual(1, fontSizeNotifications);
        }
        finally
        {
            settings.TreeViewWidth = originalTreeViewWidth;
            settings.StartupSelectInstallPending = originalStartupSelection;
            settings.CustomTableRowHeight = originalRowHeight;
            settings.CustomTableHeaderHeight = originalHeaderHeight;
            settings.CustomTableFontSize = originalFontSize;
        }
    }

    [TestMethod]
    public void ViewSettingsStoreNormalizesAndPersistsInvalidTreeViewWidthOnRead()
    {
        Settings settings = testSettings;
        double originalTreeViewWidth = settings.TreeViewWidth;
        try
        {
            settings["TreeViewWidth"] = double.NaN;
            var store = new SettingsMainWindowViewSettingsStore(() => settings);

            Assert.AreEqual(Settings.DefaultTreeViewWidth, store.TreeViewWidth);
            Assert.AreEqual(Settings.DefaultTreeViewWidth, (double)settings["TreeViewWidth"]);
        }
        finally
        {
            settings.TreeViewWidth = originalTreeViewWidth;
        }
    }

    [TestMethod]
    public void ViewSettingsStoreUsesTechnologyNeutralWindowPlacement()
    {
        Settings settings = testSettings;
        Win32API.WINDOWPLACEMENT originalPlacement = settings.WindowPlacement;
        try
        {
            settings.WindowPlacement = new Win32API.WINDOWPLACEMENT
            {
                Flags = 3,
                ShowCmd = Win32API.ShowWindowCommands.ShowMaximized,
                MinPosition = new Win32API.POINT(1, 2),
                MaxPosition = new Win32API.POINT(3, 4),
                NormalPosition = new Win32API.RECT(10, 20, 810, 620)
            };

            var store = new SettingsMainWindowViewSettingsStore(() => settings);
            WindowPlacement captured = store.WindowPlacement;

            Assert.AreEqual(3, captured.Flags);
            Assert.AreEqual((int)Win32API.ShowWindowCommands.ShowMaximized, captured.ShowCommand);
            Assert.AreEqual(10, captured.Left);
            Assert.AreEqual(620, captured.Bottom);

            var replacement = new WindowPlacement(0, (int)Win32API.ShowWindowCommands.Normal, 0, 0, 0, 0, 30, 40, 930, 740);
            store.CaptureWindowPlacement(replacement);

            Assert.AreEqual(30, settings.WindowPlacement.NormalPosition.Left);
            Assert.AreEqual(740, settings.WindowPlacement.NormalPosition.Bottom);
        }
        finally
        {
            settings.WindowPlacement = originalPlacement;
        }
    }
}
