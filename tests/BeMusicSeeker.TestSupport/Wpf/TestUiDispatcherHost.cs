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
    // 待機中のフレームはDispatcherスレッドだけが積み降ろしし、未処理例外のハンドラーも同じスレッドで読みます。
    private static readonly Stack<DispatcherFrame> waitFrames = new();

    private static readonly Lazy<HostState> host = new(
        CreateHost,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// 共有test Applicationの活動中Dispatcherを取得し、既終端なら所有threadと元例外を回収します。
    /// </summary>
    internal static Dispatcher Dispatcher
        => GetActiveHost().Dispatcher;

    /// <summary>
    /// 共有ホストが起動済みで、まだ終端していないかを返します。ホストを起動せず、<c>Settings.Default</c>・Applicationも読みません。
    /// </summary>
    internal static bool IsRunning
        => host.IsValueCreated && !host.Value.Completed.IsSet;

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
    /// 呼出し中に記録された未処理例外は元の例外で失敗し、記録を消費します。2件目以降は最初の例外のDataへ添付します。
    /// </remarks>
    internal static void ProcessQueuedPresentation()
        => ProcessQueuedAt(DispatcherPriority.Loaded);

    /// <summary>呼出し前に積まれたBackground以上のDispatcher予約を、戻る前にすべて実行済みにします。</summary>
    /// <remarks>
    /// 同じ優先度内のFIFOを保ち、消化中に積まれたBackgroundより高い優先度の予約も実行済みです。
    /// UIスレッド上では同期の入れ子フレームで、テストスレッドからは要求の終端を待って消化します。
    /// ホストが終端済みなら、元のホスト例外を返します。
    /// 呼出し中に記録された未処理例外は元の例外で失敗し、記録を消費します。2件目以降は最初の例外のDataへ添付します。
    /// ContextIdle・ApplicationIdle・SystemIdleは境界にせず、この補助では選べません。
    /// 消化中に積まれたBackgroundの予約や、まだ積まれていないworkerの後続予約は対象外です。
    /// それらはそれぞれの実通知・Taskで待ちます。
    /// </remarks>
    internal static void ProcessQueuedBackground()
        => ProcessQueuedAt(DispatcherPriority.Background);

    private static void ProcessQueuedAt(DispatcherPriority priority)
    {
        HostState state = GetActiveHost();
        long position = HostUnhandledExceptionLog.Position;
        InvokeOnOwnedDispatcher(state.Dispatcher, state.Thread, state.Completed, () => state.TerminalFailure,
            () => { }, priority);
        HostUnhandledExceptionLog.ThrowRecordedSince(position);
    }

    /// <summary>
    /// 現在のケースの期間に共有Dispatcherで未処理になった例外を、記録順に受け取って消費します。
    /// </summary>
    /// <returns>受け取った例外。なければ空です。</returns>
    /// <remarks>
    /// ケースが未処理例外を期待する場合に、ケース境界の報告より先に自分で受け取るために使います。
    /// 受け取ったものはケースの終了時に再報告されません。他ケースの記録とケースの間の記録は見えず、受け取れません。
    /// 待機・消化APIの呼出し中に記録された例外は、そのAPIが元の例外で失敗した時点で消費済みです。
    /// </remarks>
    internal static IReadOnlyList<Exception> TakeUnhandledExceptions()
        => HostUnhandledExceptionLog.TakeCurrentCase();

    /// <summary>現在のケースの期間に未処理例外がないことを表明し、あれば各例外の内容を保持して失敗します。</summary>
    /// <param name="context">どの期間の表明かを示す呼出し側の文脈。失敗のメッセージへ含めます。</param>
    /// <exception cref="AggregateException">
    /// 記録がある場合。元の例外を内部例外に持ち、メッセージに各例外の型・メッセージ・スタック（ToString）を含みます。
    /// 記録は消費され、ケースの終了時に再報告されません。
    /// </exception>
    internal static void AssertNoUnhandledExceptions(string context)
    {
        IReadOnlyList<Exception> taken = HostUnhandledExceptionLog.TakeCurrentCase();
        if (taken.Count == 0) { return; }
        throw new AggregateException(
            $"Unhandled dispatcher exceptions occurred ({context}):" + Environment.NewLine
            + string.Join(Environment.NewLine, taken.Select((exception, index) => $"#{index} {exception}")),
            taken);
    }

    /// <summary>
    /// 実機能Taskの成功・失敗・取消まで共通Dispatcherを進めます。通常完了へ局所期限を加えません。
    /// </summary>
    /// <param name="task">検査する実処理の終端Task。</param>
    /// <param name="operationName">未終端時の診断に使う操作名。</param>
    /// <remarks>
    /// 表示・閉鎖などの期限付き観測にはAwaitPresentationOnDispatcherを使います。
    /// 呼出し中に記録された未処理例外は元の例外で失敗し、記録を消費します。2件目以降は最初の例外のDataへ添付します。通知を待ち続けません。
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
    /// <remarks>呼出し中に記録された未処理例外は元の例外で失敗し、記録を消費します。2件目以降は最初の例外のDataへ添付します。通知や期限を待ち続けません。</remarks>
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
        DispatcherFrame frame = new();
        long position = HostUnhandledExceptionLog.Position;
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
            while (true)
            {
                waitFrames.Push(frame);
                try
                {
                    Dispatcher.PushFrame(frame);
                }
                finally
                {
                    waitFrames.Pop();
                }

                if (dispatchFailure != null)
                {
                    dispatchFailure.Throw();
                }

                // 待機中に未処理になった例外は、通知を待たずに元の例外で失敗し、報告済みとして消費します。
                HostUnhandledExceptionLog.ThrowRecordedSince(position);

                if (task.IsCompleted || timedOut) { break; }
                // 入れ子の待機が記録を先に消費してこのフレームだけが止まった場合は、UIスレッドを塞がず待機を続けます。
                frame = new DispatcherFrame();
            }
        }
        finally
        {
            watchdog?.Stop();
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

    /// <summary>
    /// Dispatcherに達した例外を記録して継続し、待機中のフレームがあれば止めて待機側に元の例外で失敗させます。
    /// </summary>
    /// <remarks>
    /// 共有ホストにだけ付けます。記録の対象はここへ達した例外（BeginInvoke・Post、Delegateを受け取る旧形式の
    /// Dispatcher.Invoke(DispatcherPriority, Delegate)の呼出し先、Win32メッセージから配送される入力・ウィンドウイベントの
    /// ハンドラーなど）です。旧形式のInvokeの呼出し先の例外は、Handled=trueのため呼出し側へ返りません。
    /// そのInvokeを囲む待機・消化APIの呼出し中なら、そのAPIが元の例外で失敗します。それ以外はAssertNoUnhandledExceptions・
    /// TakeUnhandledExceptionsか、ケースの終了時に表れます。後から呼んだ待機・消化APIは、呼出し開始時の位置以降の記録だけを
    /// 使うため、失敗しません。Action・Funcを受け取るInvoke・InvokeAsyncの例外は呼出し側（Task）へ返り、ここへ達しません。
    /// </remarks>
    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        HostUnhandledExceptionLog.Record(e.Exception);
        e.Handled = true;
        if (waitFrames.TryPeek(out DispatcherFrame? frame)) { frame.Continue = false; }
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
                dispatcher.UnhandledException += OnDispatcherUnhandledException;
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
                // 判定を変えない観測（未準備のWindowの記録と、ケース終了時の残留の基準）を、有効にしたUI moduleでだけ取り付けます。失敗は記録し、起動を妨げません。
                if (HostResidueBoundary.IsEnabled) { HostResidueBoundary.Attach(application, dispatcher, nativeThreadId); }
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
