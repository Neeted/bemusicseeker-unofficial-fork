using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.TestSupport.Wpf;

/// <summary>
/// テストプロセス内の単一WPF Applicationと専用STA Dispatcherを必要時だけ生成して所有します。
/// </summary>
internal static class TestUiDispatcherHost
{
    private static readonly Lazy<HostState> host = new(
        CreateHost,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// 共有test Applicationの活動中Dispatcherを取得し、既終端なら所有threadと元例外を回収します。
    /// </summary>
    internal static Dispatcher Dispatcher
        => GetActiveHost().Dispatcher;

    private static HostState GetActiveHost()
    {
        HostState state = host.Value;
        if (state.Completed.IsSet)
        {
            JoinOwnedDispatcher(state.Thread, state.Completed, () => state.TerminalFailure);
            throw new InvalidOperationException("The owned test dispatcher has completed.");
        }
        return state;
    }

    /// <summary>
    /// 共有Dispatcherの要求終端を待ち、host終了時は元例外と所有thread回収を保持します。
    /// </summary>
    internal static void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        HostState state = GetActiveHost();
        InvokeOnOwnedDispatcher(state.Dispatcher, state.Thread, state.Completed, () => state.TerminalFailure, action);
    }

    /// <summary>実Dispatcher要求か所有ホストの終端を観測し、元ホスト例外とthread回収を保持します。</summary>
    /// <remarks>専用STAの検査も共有Applicationと同じ受付・終端処理を使います。状態の再起動は行いません。</remarks>
    internal static void InvokeOnOwnedDispatcher(Dispatcher dispatcher, Thread thread, ManualResetEventSlim completed,
        Func<Exception?> terminalFailure, Action action, DispatcherPriority priority = DispatcherPriority.Send)
    {
        if (completed.IsSet)
        {
            JoinOwnedDispatcher(thread, completed, terminalFailure);
            throw new InvalidOperationException("The owned test dispatcher has completed.");
        }
        if (dispatcher.CheckAccess())
        {
            dispatcher.Invoke(action, priority);
            return;
        }

        DispatcherOperation operation = dispatcher.InvokeAsync(action, priority);
        int outcome = WaitHandle.WaitAny([((IAsyncResult)operation.Task).AsyncWaitHandle, completed.WaitHandle]);
        if (outcome == 1 || operation.Status == DispatcherOperationStatus.Aborted)
        {
            // Dispatcher停止の二次失敗でqueueの自動Abortまで進まなくても、所有要求を終端化します。
            operation.Abort();
            JoinOwnedDispatcher(thread, completed, terminalFailure);
            throw new InvalidOperationException("The owned test dispatcher ended before completing its request.");
        }
        operation.Task.GetAwaiter().GetResult();
    }

    /// <summary>Dispatcher停止の二次失敗を記録し、所有threadの終端通知を必ず発行します。</summary>
    internal static void CompleteOwnedDispatcher(Dispatcher? dispatcher, ManualResetEventSlim completed, Action<Exception> recordFailure)
    {
        try
        {
            if (dispatcher != null && !dispatcher.HasShutdownFinished) { dispatcher.InvokeShutdown(); }
        }
        catch (Exception exception) { recordFailure(exception); }
        finally { completed.Set(); }
    }

    /// <summary>回収が戻る前に所有threadをjoinし、保存済みの元ホスト例外を返します。</summary>
    internal static void JoinOwnedDispatcher(Thread thread, ManualResetEventSlim completed, Func<Exception?> terminalFailure)
    {
        completed.Wait();
        thread.Join();
        if (terminalFailure() is { } failure) { ExceptionDispatchInfo.Capture(failure).Throw(); }
    }

    /// <summary>
    /// 共有Dispatcherで実画面を検査し、表示失敗と所有資源の回収を保持します。
    /// </summary>
    internal static void RunWindowTest(Action<TestWindowPresentationScope> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        HostState state = GetActiveHost();
        Invoke(() =>
        {
            var scope = new TestWindowPresentationScope(state.Application, state.NativeThreadId);
            ExceptionDispatchInfo? bodyFailure = null;
            ExceptionDispatchInfo? presentationFailure;
            Exception? cleanupFailure = null;
            try
            {
                action(scope);
            }
            catch (Exception ex)
            {
                bodyFailure = ExceptionDispatchInfo.Capture(ex);
            }

            try
            {
                scope.Cleanup();
            }
            catch (Exception ex)
            {
                cleanupFailure = ex;
            }

            presentationFailure = scope.GetPresentationFailure();

            if (bodyFailure != null)
            {
                if (presentationFailure != null)
                {
                    bodyFailure.SourceException.Data["TestWindowPresentationFailure"] =
                        presentationFailure.SourceException.ToString();
                }
                if (cleanupFailure != null)
                {
                    bodyFailure.SourceException.Data["TestWindowPresentationCleanupFailure"] = cleanupFailure.ToString();
                }

                bodyFailure.Throw();
            }

            if (presentationFailure != null)
            {
                if (cleanupFailure != null)
                {
                    presentationFailure.SourceException.Data["TestWindowPresentationCleanupFailure"] =
                        cleanupFailure.ToString();
                }

                presentationFailure.Throw();
            }

            if (cleanupFailure != null)
            {
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }
        });
    }

    /// <summary>予約済みのBinding・配置・描画反映をLoaded境界まで進めます。</summary>
    /// <remarks>
    /// 実controlの値・選択・配置は呼出側で確認します。機能Task、閉鎖の終端や
    /// 全Dispatcherのidleは保証せず、それぞれの実通知・Taskで待ちます。Background優先度までの消化にはProcessQueuedBackgroundを使います。
    /// </remarks>
    internal static void ProcessQueuedPresentation()
        => ProcessQueuedAt(DispatcherPriority.Loaded);

    /// <summary>呼出し前に積まれたBackground以上のDispatcher予約を、戻る前にすべて実行済みにします。</summary>
    /// <remarks>
    /// 同じ優先度内のFIFOを保ち、消化中に積まれたBackgroundより高い優先度の予約も実行済みです。
    /// UIスレッド上では同期の入れ子フレームで、テストスレッドからは要求の終端を待って消化します。
    /// 消化中の予約の例外は既存の要求と同じ経路で表面化し、ホストが終端済みなら元のホスト例外を返します。
    /// ContextIdle・ApplicationIdle・SystemIdleは境界にせず、この補助では選べません。
    /// 消化中に積まれたBackgroundの予約や、まだ積まれていないworkerの後続予約は対象外です。
    /// それらはそれぞれの実通知・Taskで待ちます。
    /// </remarks>
    internal static void ProcessQueuedBackground()
        => ProcessQueuedAt(DispatcherPriority.Background);

    private static void ProcessQueuedAt(DispatcherPriority priority)
    {
        HostState state = GetActiveHost();
        InvokeOnOwnedDispatcher(state.Dispatcher, state.Thread, state.Completed, () => state.TerminalFailure,
            () => { }, priority);
    }

    /// <summary>
    /// 実機能Taskの成功・失敗・取消まで共通Dispatcherを進めます。通常完了へ局所期限を加えません。
    /// </summary>
    /// <param name="task">検査する実処理の終端Task。</param>
    /// <param name="operationName">未終端時の診断に使う操作名。</param>
    /// <remarks>
    /// 表示・閉鎖などの期限付き観測にはAwaitPresentationOnDispatcherを使います。
    /// </remarks>
    internal static void AwaitTaskOnDispatcher(Task task, string operationName)
        => AwaitOnDispatcher(task, operationName, boundedObservation: false);

    /// <summary>
    /// 実処理の終端前に必要な通知を待ち、通知前の失敗・取消と正常終端時の通知欠落を表面化します。
    /// 呼出元は実処理が待つ入力をfinallyで解放し、開始済みTaskを回収します。
    /// </summary>
    internal static async Task AwaitNotificationAsync(Task notification, Task operation, string operationName)
    {
        Task first = await Task.WhenAny(notification, operation).ConfigureAwait(false);
        if (first == operation)
        {
            await operation.ConfigureAwait(false);
            if (!notification.IsCompleted)
            {
                throw new InvalidOperationException($"The operation '{operationName}' completed without its required notification.");
            }
        }
        await notification.ConfigureAwait(false);
    }

    /// <summary>描画・閉鎖・native解放の通知欠落を期限付きで検出します。</summary>
    /// <param name="task">観測する実通知のTask。</param>
    /// <param name="operationName">未到達時の診断名。</param>
    /// <param name="notificationTimeout">通知の観測期限。省略時は15秒です。通常Taskの期限には使いません。</param>
    internal static void AwaitPresentationOnDispatcher(Task task, string operationName, TimeSpan? notificationTimeout = null)
        => AwaitOnDispatcher(task, operationName, boundedObservation: true, notificationTimeout);

    private static void AwaitOnDispatcher(Task task, string operationName, bool boundedObservation, TimeSpan? notificationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        Dispatcher dispatcher = Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            throw new InvalidOperationException(
                "AwaitTaskOnDispatcher must be called from the test application dispatcher.");
        }

        if (task.IsCompleted)
        {
            task.GetAwaiter().GetResult();
            return;
        }

        TimeSpan timeout = notificationTimeout ?? TimeSpan.FromSeconds(15);
        var frame = new DispatcherFrame();
        bool timedOut = false;
        ExceptionDispatchInfo? dispatchFailure = null;
        DispatcherTimer? watchdog = boundedObservation ? new DispatcherTimer(
            timeout,
            DispatcherPriority.Send,
            (_, _) =>
            {
                timedOut = true;
                frame.Continue = false;
            },
            dispatcher) : null;

        _ = task.ContinueWith(
            _ =>
            {
                try
                {
                    dispatcher.BeginInvoke(
                        DispatcherPriority.Send,
                        new Action(() => frame.Continue = false));
                }
                catch (Exception ex)
                {
                    dispatchFailure = ExceptionDispatchInfo.Capture(ex);
                    frame.Continue = false;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        if (boundedObservation)
        {
            watchdog?.Start();
        }
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            watchdog?.Stop();
        }

        if (dispatchFailure != null)
        {
            dispatchFailure.Throw();
        }

        if (timedOut && !task.IsCompleted)
        {
            throw new TimeoutException(
                $"The dispatcher outcome '{operationName}' did not complete within {timeout}. "
                + $"Task status: {task.Status}.");
        }

        task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// プロセス内のテーマ設定を復元し、起動済みApplicationと所有スレッドを終端します。
    /// </summary>
    internal static void ShutdownApplication()
    {
        if (!host.IsValueCreated)
        {
            return;
        }

        HostState state = host.Value;
        Exception? cleanupFailure = null;

        try
        {
            if (!state.Completed.IsSet)
            {
                Invoke(() =>
            {
                try
                {
                    Settings.Default.AppearanceTheme = state.OriginalAppearanceTheme;
                    AppThemeService.ApplyTheme(state.OriginalAppearanceTheme);
                }
                catch (Exception ex)
                {
                    cleanupFailure = ex;
                }
                finally
                {
                    try
                    {
                        state.Application.Shutdown();
                    }
                    finally
                    {
                        if (!state.Dispatcher.HasShutdownStarted)
                        {
                            state.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                        }
                    }
                }
            });
            }
        }
        catch (Exception ex)
        {
            cleanupFailure ??= ex;
        }

        try { JoinOwnedDispatcher(state.Thread, state.Completed, () => state.TerminalFailure); }
        catch (Exception exception)
        {
            if (cleanupFailure != null) { exception.Data["TestApplicationCleanupFailure"] = cleanupFailure.ToString(); }
            throw;
        }
        if (cleanupFailure != null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    private static HostState CreateHost()
    {
        using var ready = new ManualResetEventSlim();
        var completed = new ManualResetEventSlim();
        Application? application = null;
        Dispatcher? dispatcher = null;
        string? originalAppearanceTheme = null;
        uint nativeThreadId = 0;
        Exception? terminalFailure = null;
        var applicationThread = new Thread(() =>
        {
            try
            {
                if (Application.Current != null)
                {
                    throw new InvalidOperationException(
                        "A conflicting WPF Application already exists before the shared test host starts.");
                }

                application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                dispatcher = Dispatcher.CurrentDispatcher;
                nativeThreadId = TestWindowPresentationScope.GetCurrentNativeThreadId();
                originalAppearanceTheme = Settings.Default.AppearanceTheme;
                Settings.Default.AppearanceTheme = AppThemeService.Light;
                AppThemeService.ApplyTheme(AppThemeService.Light);
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(
                        "/BeMusicSeeker;component/Themes/CanonicalDialogStyles.xaml",
                        UriKind.RelativeOrAbsolute)
                });
                ready.Set();
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                terminalFailure = ex;
                ready.Set();
            }
            finally
            {
                CompleteOwnedDispatcher(dispatcher, completed, exception =>
                {
                    if (terminalFailure == null) { terminalFailure = exception; }
                    else { terminalFailure.Data["TestDispatcherShutdownFailure"] = exception.ToString(); }
                });
            }
        })
        {
            IsBackground = true,
            Name = "BeMusicSeeker.Tests shared WPF application"
        };
        applicationThread.SetApartmentState(ApartmentState.STA);
        applicationThread.Start();
        ready.Wait();

        if (terminalFailure != null)
        {
            applicationThread.Join();
            ExceptionDispatchInfo.Capture(terminalFailure).Throw();
        }

        return new HostState(
            application!,
            dispatcher!,
            applicationThread,
            completed,
            originalAppearanceTheme!,
            nativeThreadId,
            () => terminalFailure);
    }

    private sealed class HostState(
        Application application,
        Dispatcher dispatcher,
        Thread thread,
        ManualResetEventSlim completed,
        string originalAppearanceTheme,
        uint nativeThreadId,
        Func<Exception?> terminalFailureProvider)
    {
        internal Application Application { get; } = application;

        internal Dispatcher Dispatcher { get; } = dispatcher;

        internal Thread Thread { get; } = thread;

        internal ManualResetEventSlim Completed { get; } = completed;

        internal string OriginalAppearanceTheme { get; } = originalAppearanceTheme;

        internal uint NativeThreadId { get; } = nativeThreadId;

        internal Exception? TerminalFailure => terminalFailureProvider();
    }
}
