using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BeMusicSeeker.TestSupport.Processes;
using FlaUI.Core.WindowsAPI;
using DrawingPoint = System.Drawing.Point;
using InputKeyboard = FlaUI.Core.Input.Keyboard;
using InputMouse = FlaUI.Core.Input.Mouse;
using InputMouseButton = FlaUI.Core.Input.MouseButton;

namespace BeMusicSeeker.Desktop.E2E.Tests.Fixtures;

/// <summary>専有STAの実WindowからFileDropを開始し、OSのドラッグ経路で製品へ入力するためのdrag sourceです。</summary>
/// <remarks>UiModuleLifetimeのlease内で使用します。Explorer・clipboard・製品のDrop handlerを直接操作しません。</remarks>
internal sealed class DesktopFileDropSource : IAsyncDisposable
{
    private readonly string[] paths;
    private readonly ProcessDeadline deadline;
    private readonly Thread thread;
    private readonly Action<string> step;
    private readonly Action beginFailureCleanup;
    private readonly Func<ProcessDeadline> cleanupDeadline;
    private readonly TaskCompletionSource<SourcePresentation> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<DrawingPoint> downAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<DragDropEffects> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource terminated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool stopping;
    private volatile Dispatcher? dispatcher;
    private Window? sourceWindow;
    private Exception? threadFailure;
    private bool mouseHeld;
    private int used;

    private DesktopFileDropSource(string[] paths, ProcessDeadline deadline, Action<string> step,
        Action beginFailureCleanup, Func<ProcessDeadline> cleanupDeadline)
    {
        this.paths = paths;
        this.deadline = deadline;
        this.step = step;
        this.beginFailureCleanup = beginFailureCleanup;
        this.cleanupDeadline = cleanupDeadline;
        thread = new Thread(Run) { IsBackground = true, Name = "Desktop FileDrop source" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>呼出元が所有する既存ファイルだけを捕捉し、専有Windowの実描画まで同じcase期限で待ちます。</summary>
    /// <param name="paths">専有する実FileDrop入力のpath。</param>
    /// <param name="deadline">起動・入力・保存観測に共通するcase期限。</param>
    /// <param name="step">複数threadからの有限段階を排他的に即時保存するcase側writer。</param>
    /// <param name="beginFailureCleanup">controllerの初回失敗時にcaseの回収期限を一度確定します。</param>
    /// <param name="cleanupDeadline">caseの操作期限または確定済み回収期限を取得します。</param>
    internal static async Task<DesktopFileDropSource> CreateAsync(IReadOnlyList<string> paths, ProcessDeadline deadline, Action<string> step,
        Action beginFailureCleanup, Func<ProcessDeadline> cleanupDeadline)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(step);
        string[] captured = paths.Select(Path.GetFullPath).ToArray();
        if (captured.Length == 0 || captured.Any(path => !File.Exists(path)))
        { throw new ArgumentException("存在する専有FileDrop入力が必要です。", nameof(paths)); }
        if (deadline.Remaining == TimeSpan.Zero) { throw new TimeoutException("FileDrop準備のcase期限を過ぎています。"); }
        var source = new DesktopFileDropSource(captured, deadline, step, beginFailureCleanup, cleanupDeadline);
        try { await source.ready.Task.WaitAsync(deadline.Remaining).ConfigureAwait(false); return source; }
        catch (Exception primary)
        {
            beginFailureCleanup();
            try { await source.DisposeAsync(cleanupDeadline()).ConfigureAwait(false); }
            catch (Exception cleanup) { primary.Data["DragSourceCleanupFailure"] = cleanup; }
            throw;
        }
    }

    /// <summary>一回の実mouse gestureでDoDragDropを開始し、指定した画面座標で離して実戻り値まで待ちます。</summary>
    /// <remarks>targetは実UIA boundsから選びます。実Downで原点を確定してから閾値越えの移動を送り、開始・完了とも元case期限で観測します。ACKやOLE効果値は保存成功を意味しません。</remarks>
    internal async Task<DragDropEffects> DragToAsync(DrawingPoint target)
    {
        if (Interlocked.Exchange(ref used, 1) != 0) { throw new InvalidOperationException("drag sourceのgestureは一回だけです。"); }
        SourcePresentation source = await ready.Task.WaitAsync(deadline.Remaining).ConfigureAwait(false);
        EnsureDeadline();
        Exception? primary = null;
        try
        {
            step("FileDrop before source Move center=" + source.Center);
            InputMouse.MoveTo(source.Center);
            step("FileDrop after source Move; before Down");
            EnsureDeadline();
            mouseHeld = true;
            InputMouse.Down(InputMouseButton.Left);
            step("FileDrop after Down; before actual Down ACK");
            DrawingPoint trigger = await downAccepted.Task.WaitAsync(deadline.Remaining).ConfigureAwait(false);
            EnsureDeadline();
            step("FileDrop after Down ACK; before trigger Move=" + trigger);
            InputMouse.MoveTo(trigger);
            step("FileDrop after trigger Move; before DoDragDrop boundary ACK");
            await Task.WhenAny(started.Task, completed.Task).WaitAsync(deadline.Remaining).ConfigureAwait(false);
            // 呼出し境界ACKと終端を同時に観測した場合も、終端を優先する。
            if (completed.Task.IsCompleted)
            {
                _ = await completed.Task.ConfigureAwait(false);
                throw new InvalidOperationException("target移動前にDoDragDropが終了しました。");
            }
            await started.Task.WaitAsync(deadline.Remaining).ConfigureAwait(false);
            EnsureDeadline();
            if (completed.Task.IsCompleted)
            {
                _ = await completed.Task.ConfigureAwait(false);
                throw new InvalidOperationException("target移動前にDoDragDropが終了しました。");
            }
            step("FileDrop after DoDragDrop boundary ACK; before target Move=" + target);
            InputMouse.MoveTo(target);
            EnsureDeadline();
            step("FileDrop after target Move; before Up");
            InputMouse.Up(InputMouseButton.Left);
            mouseHeld = false;
            step("FileDrop after Up; before OLE completion");
            DragDropEffects effect = await completed.Task.WaitAsync(deadline.Remaining).ConfigureAwait(false);
            step("FileDrop after OLE completion effect=" + effect);
            return effect;
        }
        catch (Exception failure) { beginFailureCleanup(); stopping = true; primary = failure; throw; }
        finally
        {
            try { ReleaseInput(); }
            catch (Exception cleanup) when (primary is not null) { primary.Data["DragInputCleanupFailure"] = cleanup; }
        }
    }

    private void Run()
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        this.dispatcher = dispatcher;
        Window? window = null;
        try
        {
            var surface = new Border
            {
                Background = Brushes.LightSteelBlue,
                Padding = new Thickness(20),
                Child = new TextBlock { Text = "専有FileDrop入力", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }
            };
            window = new Window
            {
                Title = "Desktop FileDrop source",
                Width = 240,
                Height = 150,
                Left = 20,
                Top = 20,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                Content = surface
            };
            sourceWindow = window;
            Point? down = null;
            bool dragStarted = false;
            surface.PreviewMouseLeftButtonDown += (_, args) =>
            {
                if (stopping || Volatile.Read(ref used) != 1 || down.HasValue) { return; }
                try
                {
                    Point origin = args.GetPosition(surface);
                    down = origin;
                    Point trigger = new(origin.X + Math.Max(10d, SystemParameters.MinimumHorizontalDragDistance * 2d), origin.Y);
                    DrawingPoint originScreen = Pixels(surface.PointToScreen(origin));
                    DrawingPoint triggerScreen = Pixels(surface.PointToScreen(trigger));
                    if (args.LeftButton != MouseButtonState.Pressed || trigger.X >= surface.ActualWidth
                        || trigger.Y < 0 || trigger.Y >= surface.ActualHeight || triggerScreen == originScreen)
                    { throw new InvalidOperationException("受理したDownから専有surface内の異なるdrag開始点を作れません。"); }
                    step($"FileDrop actual Down origin={origin} screen={originScreen} trigger={trigger} screen={triggerScreen}");
                    // 原点を保存した実DownだけをACKとする。後続Downで原点を上書きしない。
                    downAccepted.TrySetResult(triggerScreen);
                }
                catch (Exception failure) { FailGesture(failure); }
            };
            surface.PreviewMouseMove += (_, args) =>
            {
                if (dragStarted || stopping || down is not Point origin || args.LeftButton != MouseButtonState.Pressed) { return; }
                Point position = args.GetPosition(surface);
                if (Math.Abs(position.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(position.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) { return; }
                dragStarted = true;
                try
                {
                    step($"FileDrop actual Move threshold passed origin={origin} position={position}; before DoDragDrop");
                    var data = new DataObject(DataFormats.FileDrop, paths);
                    // native loop開始の証明ではなく、準備完了後の同期呼出し直前だけをACKする。
                    started.TrySetResult();
                    DragDropEffects effect = DragDrop.DoDragDrop(surface, data, DragDropEffects.Copy);
                    step("FileDrop after DoDragDrop effect=" + effect);
                    completed.TrySetResult(effect);
                }
                catch (Exception failure) { FailGesture(failure); }
            };
            // 最初のfeedbackだけを診断する。target移動をfeedback待ちで遮断しない。
            bool feedbackObserved = false;
            surface.GiveFeedback += (_, args) =>
            {
                if (feedbackObserved) { return; }
                feedbackObserved = true;
                try { step("FileDrop first actual feedback effect=" + args.Effects); }
                catch (Exception failure) { FailGesture(failure); }
            };
            surface.QueryContinueDrag += (_, args) =>
            {
                if (stopping) { args.Action = DragAction.Cancel; args.Handled = true; }
            };
            window.ContentRendered += (_, _) =>
            {
                Point center = new(surface.ActualWidth / 2d, surface.ActualHeight / 2d);
                ready.TrySetResult(new SourcePresentation(Pixels(surface.PointToScreen(center))));
            };
            window.Closed += (_, _) => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            if (!stopping) { window.Show(); Dispatcher.Run(); }
        }
        catch (Exception failure)
        {
            threadFailure = failure;
            ready.TrySetException(failure);
            downAccepted.TrySetException(failure);
            started.TrySetException(failure);
            completed.TrySetException(failure);
        }
        finally
        {
            try { window?.Close(); }
            catch (Exception failure) { threadFailure = threadFailure is null ? failure : new AggregateException(threadFailure, failure); }
            try { if (!dispatcher.HasShutdownStarted) { dispatcher.InvokeShutdown(); } }
            catch (Exception failure) { threadFailure = threadFailure is null ? failure : new AggregateException(threadFailure, failure); }
            ready.TrySetCanceled(); downAccepted.TrySetCanceled(); started.TrySetCanceled(); completed.TrySetCanceled();
            terminated.TrySetResult();
        }
    }

    private static DrawingPoint Pixels(Point point) => new(checked((int)Math.Round(point.X)), checked((int)Math.Round(point.Y)));

    private void FailGesture(Exception failure)
    {
        stopping = true;
        downAccepted.TrySetException(failure);
        started.TrySetException(failure);
        completed.TrySetException(failure);
    }

    private void EnsureDeadline()
    {
        if (deadline.Remaining == TimeSpan.Zero) { throw new TimeoutException("FileDrop入力のcase期限を過ぎています。"); }
    }

    private void ReleaseInput()
    {
        if (mouseHeld) { InputMouse.Up(InputMouseButton.Left); mouseHeld = false; }
        if (started.Task.IsCompletedSuccessfully && !completed.Task.IsCompleted)
        {
            try { InputKeyboard.Press(VirtualKeyShort.ESCAPE); }
            finally { InputKeyboard.Release(VirtualKeyShort.ESCAPE); }
        }
    }

    /// <summary>所有した押下を解放し、進行中dragを取消してWindow・Dispatcher・STA threadの実終端まで回収します。</summary>
    public ValueTask DisposeAsync() => DisposeAsync(cleanupDeadline());

    /// <summary>所有Upからsource STA終端までcaseが確定した回収期限内で待ちます。</summary>
    internal async ValueTask DisposeAsync(ProcessDeadline cleanupDeadline)
    {
        stopping = true;
        var failures = new List<Exception>();
        try { ReleaseInput(); } catch (Exception cleanup) { failures.Add(cleanup); }
        DispatcherOperation? shutdown = null;
        Dispatcher? owner = dispatcher;
        if (owner is not null && !owner.HasShutdownStarted)
        {
            try
            {
                shutdown = owner.InvokeAsync(() =>
                {
                    try { sourceWindow?.Close(); }
                    finally { owner.BeginInvokeShutdown(DispatcherPriority.Background); }
                }, DispatcherPriority.Send);
            }
            catch (Exception cleanup) { failures.Add(cleanup); }
        }
        // InvokeAsyncはcallback例外をOperation.Taskへ伝える。STAの終了だけでは成功扱いしない。
        if (shutdown is not null)
        {
            try { await shutdown.Task.WaitAsync(cleanupDeadline.Remaining).ConfigureAwait(false); }
            catch (Exception cleanup) { failures.Add(cleanup); }
        }
        try { await terminated.Task.WaitAsync(cleanupDeadline.Remaining).ConfigureAwait(false); }
        catch (Exception cleanup) { failures.Add(cleanup); }
        if (terminated.Task.IsCompleted)
        {
            if (!thread.Join(cleanupDeadline.Remaining)) { failures.Add(new TimeoutException("FileDrop source threadの終了期限を過ぎました。")); }
            if (threadFailure is not null) { failures.Add(threadFailure); }
        }
        // 呼出元が待機前に失敗した場合も、生成済みTaskの実例外を観測して回収する。
        if (ready.Task.IsFaulted) { _ = ready.Task.Exception; }
        if (downAccepted.Task.IsFaulted) { _ = downAccepted.Task.Exception; }
        if (started.Task.IsFaulted) { _ = started.Task.Exception; }
        if (completed.Task.IsFaulted) { _ = completed.Task.Exception; }
        if (failures.Count > 0) { throw new AggregateException("専有FileDrop sourceの回収に失敗しました。", failures); }
    }

    private sealed record SourcePresentation(DrawingPoint Center);
}
