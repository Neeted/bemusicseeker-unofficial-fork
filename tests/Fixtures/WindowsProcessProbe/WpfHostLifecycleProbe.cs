using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Wpf;

namespace BeMusicSeeker.WindowsProcessProbe;

/// <summary>親moduleのsession lease内で共有hostの実終端を観測し、生存中に結果を親へ渡します。</summary>
internal static class WpfHostLifecycleProbe
{
    /// <summary>専有profileと同build素材を使い、実Window・Popup・thread・tempの回収後にreleaseを待ちます。</summary>
    internal static async Task<int> RunAsync(string[] arguments)
    {
        try
        {
            if (arguments.Length != 3) { throw new ArgumentException("profile、ready、releaseを指定してください。"); }
            string profile = Path.GetFullPath(arguments[0]);
            string assets = AppContext.BaseDirectory;
            string executable = Path.Combine(assets, "BeMusicSeeker.exe");
            string language = Path.Combine(assets, "lang");
            if (!File.Exists(executable) || !File.Exists(Path.Combine(language, "ja-JP.json")))
            {
                throw new FileNotFoundException("同じbuildの実行体と日本語辞書が必要です。", executable);
            }
            ApplicationPathPolicy.Configure(ApplicationPathSnapshot.FromLayout(executable, assets, profile,
                language, Path.Combine(profile, "temp"), Path.Combine(profile, "legacy")));
            Resources.Culture = CultureInfo.GetCultureInfo("ja-JP");
            Thread? dispatcherThread = null;
            nint windowHandle = 0;
            nint popupHandle = 0;
            string? leaf = null;
            Exception? primary = null;
            List<Exception> cleanupFailures = [];
            try
            {
                TestUiDispatcherHost.RunWindowTest(scope =>
                {
                    dispatcherThread = Thread.CurrentThread;
                    var target = new Button { Content = "lifecycle" };
                    var window = new Window { Width = 320, Height = 200, Content = target };
                    scope.ShowAndWaitForContentRendered(window);
                    windowHandle = new WindowInteropHelper(window).Handle;
                    var popup = new Popup { PlacementTarget = target, Child = new Border { Width = 80, Height = 40 } };
                    scope.TrackPopup(popup);
                    popup.IsOpen = true;
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    popupHandle = (PresentationSource.FromVisual(popup.Child) as HwndSource)?.Handle ?? 0;
                    if (windowHandle == 0 || popupHandle == 0)
                    {
                        throw new InvalidOperationException("実WindowとPopupのHWNDが必要です。");
                    }
                });
                leaf = TempDirectoryPublisher.Get("wpf-host-lifecycle");
                File.WriteAllText(Path.Combine(leaf, "owned.txt"), "lifecycle temporary payload");
            }
            catch (Exception failure) { primary = failure; throw; }
            finally
            {
                try { TestUiDispatcherHost.ShutdownApplication(); }
                catch (Exception failure) { cleanupFailures.Add(failure); }
                try
                {
                    await TempDirectoryPublisher.RemoveAllIfInitializedAsync((path, failure) =>
                        throw new IOException("管理一時sessionの回収に失敗しました: " + path, failure)).ConfigureAwait(false);
                }
                catch (Exception failure) { cleanupFailures.Add(failure); }
                if (cleanupFailures.Count != 0)
                {
                    var cleanup = new AggregateException("共有hostの回収に失敗しました。", cleanupFailures);
                    if (primary is null) { throw cleanup; }
                    primary.Data["WpfHostLifecycleProbe.CleanupFailure"] = cleanup;
                }
            }
            if (dispatcherThread is null || leaf is null) { throw new InvalidOperationException("hostとtempの実利用が必要です。"); }
            var result = new LifecycleResult(windowHandle.ToInt64(), popupHandle.ToInt64(), leaf,
                Path.GetDirectoryName(leaf) ?? throw new InvalidOperationException("temp sessionの所在が不明です。"),
                dispatcherThread.IsAlive);
            // stdout/errの所有はOwnedProcessへ残し、process生存中の観測だけを専有fileで公開します。
            string pending = arguments[1] + ".pending";
            File.WriteAllText(pending, JsonSerializer.Serialize(result));
            File.Move(pending, arguments[1]);
            while (!File.Exists(arguments[2])) { await Task.Delay(20).ConfigureAwait(false); }
            Program.WriteResult(result);
            return 0;
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine(failure);
            return 2;
        }
    }

    private sealed record LifecycleResult(long WindowHandle, long PopupHandle, string LeafPath, string SessionPath,
        bool DispatcherThreadIsAlive);
}
