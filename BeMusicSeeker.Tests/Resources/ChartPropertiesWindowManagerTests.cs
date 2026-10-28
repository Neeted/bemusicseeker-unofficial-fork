using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <remarks>実manager/Windowの寿命と実読取りTaskを検証します。UI値と対象条件は既存Window/VMテスト、メイン終了への接続はMainWindowViewHostTestsへ分担します。</remarks>
[TestClass]
[DoNotParallelize]
public sealed class ChartPropertiesWindowManagerTests
{
    /// <summary>実Windowの個別Closeで言語購読を終え、遅延成功/通常失敗と後の言語変更も通知しません。終了受付後は新規窓・読取りを拒否し、実読取り終端までshutdownを完了しません。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Shutdown_JoinsReadOfIndividuallyClosedWindowWithoutLatePublication(bool fail)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var owner = new ThemedWindow { Width = 300, Height = 200, Content = new System.Windows.Controls.TextBlock { Text = "Owner" } };
            scope.ShowAndWaitForContentRendered(owner);
            CultureInfo? previousCulture = Resources.Culture;
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var model = new ChartPropertiesViewModel(ChartTestValues.Empty(), () =>
            {
                entered.TrySetResult();
                release.Wait();
                if (fail) { throw new IOException("closed modeless read failure"); }
                return new ChartResourceDiagnostics("Late title", "Late artist", [], [], []);
            });
            ChartPropertiesWindow? child = null;
            int created = 0;
            var manager = new ChartPropertiesWindowManager(owner, (_, _) =>
            {
                created++;
                return child = new ChartPropertiesWindow(model);
            }, scope.PrepareForOwnedPresentation);
            try
            {
                ResourceService.Current.ChangeCulture("ja-JP");
                manager.Open(ChartTestValues.Empty());
                Task read = manager.ReadCompletion;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(
                    entered.Task, read, "modeless-read-entered"), "modeless-read-entered");
                Assert.IsNotNull(child);
                Assert.IsNull(child.Owner);
                Assert.IsTrue(owner.IsEnabled);
                child.Close();
                int notifications = 0;
                model.PropertyChanged += (_, _) => notifications++;
                ResourceService.Current.ChangeCulture("en-US");
                Assert.AreEqual(0, notifications);
                Task shutdown = manager.CompleteShutdownAsync();
                Assert.IsFalse(shutdown.IsCompleted);
                Assert.IsFalse(read.IsCompleted);
                manager.Open(ChartTestValues.Empty());
                Assert.AreEqual(1, created);
                release.Set();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(shutdown, "modeless-read-shutdown");
                Assert.IsTrue(read.IsCompletedSuccessfully);
                ResourceService.Current.ChangeCulture("ja-JP");
                Assert.AreEqual(0, notifications);
                Assert.AreEqual(string.Empty, model.Status);
                Assert.AreEqual(string.Empty, model.Title);
            }
            finally
            {
                child?.Close();
                model.Close();
                release.Set();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(manager.CompleteShutdownAsync(), "modeless-read-finally");
                ResourceService.Current.ChangeCulture(previousCulture?.Name ?? "en-US");
                Resources.Culture = previousCulture;
            }
        });
    }

    /// <summary>実ShowのSourceInitialized中に終了を再入させると、登録済み窓はCloseされ、Showから戻っても読取りを始めません。</summary>
    [TestMethod]
    public void Open_ShutdownDuringShowClosesRegisteredWindowWithoutStartingRead()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var owner = new ThemedWindow { Width = 300, Height = 200, Content = new System.Windows.Controls.TextBlock { Text = "Owner" } };
            scope.ShowAndWaitForContentRendered(owner);
            int calls = 0;
            using var model = new ChartPropertiesViewModel(ChartTestValues.Empty(), () =>
            {
                calls++;
                return new ChartResourceDiagnostics("", "", [], [], []);
            });
            ChartPropertiesWindowManager? manager = null;
            var child = new ChartPropertiesShowFaultWindow(model, () => manager?.BeginShutdown(), fail: false);
            manager = new ChartPropertiesWindowManager(owner, (_, _) => child, scope.PrepareForOwnedPresentation);
            try
            {
                manager.Open(ChartTestValues.Empty());
                TestUiDispatcherHost.AwaitTaskOnDispatcher(manager.CompleteShutdownAsync(), "modeless-reentrant-show");
                Assert.IsTrue(child.ThemeCloseCompleted);
                Assert.IsFalse(child.IsVisible);
                Assert.AreEqual(0, calls);
            }
            finally { TestUiDispatcherHost.AwaitTaskOnDispatcher(manager.CompleteShutdownAsync(), "modeless-reentrant-finally"); }
        });
    }

    /// <summary>実Show内部で基底SourceInitialized後に失敗しても、実CloseがThemedWindowの回収を通り、読取りを開始せず元の失敗を返します。</summary>
    [TestMethod]
    public void Open_RealShowFailureClosesThemedWindowAndDoesNotStartRead()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var owner = new ThemedWindow { Width = 300, Height = 200, Content = new System.Windows.Controls.TextBlock { Text = "Owner" } };
            scope.ShowAndWaitForContentRendered(owner);
            int calls = 0;
            using var model = new ChartPropertiesViewModel(ChartTestValues.Empty(), () =>
            {
                calls++;
                return new ChartResourceDiagnostics("", "", [], [], []);
            });
            var child = new ChartPropertiesShowFaultWindow(model);
            var manager = new ChartPropertiesWindowManager(owner, (_, _) => child, scope.PrepareForOwnedPresentation);
            try
            {
                InvalidOperationException failure = Assert.ThrowsException<InvalidOperationException>(() => manager.Open(ChartTestValues.Empty()));
                Assert.AreEqual("chart properties real Show failure", failure.Message);
                Assert.IsTrue(child.ThemeCloseCompleted);
                Assert.IsFalse(child.IsVisible);
                Assert.AreEqual(0, calls);
            }
            finally { TestUiDispatcherHost.AwaitTaskOnDispatcher(manager.CompleteShutdownAsync(), "modeless-show-failure-finally"); }
        });
    }
}

/// <summary>基底のHWND初期化とテーマ接続後にShow内部で失敗・終了再入を発生させる実Windowです。通常表示のcompiled画面はメニューとbindingテストが検証します。</summary>
internal sealed class ChartPropertiesShowFaultWindow : ThemedWindow
{
    private readonly Action? duringShow;
    private readonly bool fail;
    internal bool ThemeCloseCompleted { get; private set; }

    internal ChartPropertiesShowFaultWindow(ChartPropertiesViewModel model, Action? duringShow = null, bool fail = true)
    {
        this.duringShow = duringShow;
        this.fail = fail;
        DataContext = model;
        Width = 300;
        Height = 200;
        Content = new System.Windows.Controls.TextBlock { Text = "Property presentation fault" };
        Closed += (_, _) => model.Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        duringShow?.Invoke();
        if (fail) { throw new InvalidOperationException("chart properties real Show failure"); }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ThemeCloseCompleted = true;
    }
}
