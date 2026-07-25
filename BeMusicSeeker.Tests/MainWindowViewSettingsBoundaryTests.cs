using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MainWindowViewSettingsBoundaryTests
{
    [TestMethod]
    public void ViewSettingsStoreCapturesViewStateAndForwardsDraftChanges()
    {
        Settings settings = Settings.Default;
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
}
