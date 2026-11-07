namespace BeMusicSeeker.TestSupport.Wpf;

/// <summary>実Dispatcherへ配送するcompiled async voidハンドラーの終端だけを捕捉します。</summary>
internal static class MainWindowEventCompletion
{
    /// <summary>compiledイベントのasync void寿命を現在のDispatcherへ接続し、Taskや通知の完了から推測せず実ハンドラー終端を返します。</summary>
    /// <remarks>完了・失敗の条件と診断は <see cref="Capture"/> と <see cref="AsyncEventCapture"/> に従います。</remarks>
    internal static Task CaptureAsyncEventCompletion(Action raiseEvent)
        => Capture(raiseEvent).Completion;

    /// <summary>compiledイベントのasync void寿命を現在のDispatcherへ接続し、終端のTaskと開始数の診断を返します。</summary>
    /// <param name="raiseEvent">現在のDispatcherスレッドでイベントを発生させる処理。同期で戻る間に開始したasync voidを捕捉します。</param>
    /// <returns>
    /// Completionは、<paramref name="raiseEvent"/>が戻り、かつ捕捉中に現在の文脈を取り込んだ処理の全Post（awaitの継続などを含む）の
    /// 配送が終端し、開始済みの全操作が終端した時点で完了します。
    /// 同じ発生の中で後から開始したハンドラーも、戻る前なら待ちます。ハンドラーが一度も開始しなければ、戻った時点で
    /// 「started=0」を含む<see cref="InvalidOperationException"/>で失敗し、無期限には待ちません。
    /// Postのcallbackが投げた例外（async voidの例外を含む）は、元の例外オブジェクトのままCompletionの失敗になります（複数なら全件を保持します）。
    /// Dispatcherへの送出は従来どおり維持するため、共有ホストの記録にも残り、報告は重複します。一次の失敗はCompletion側です。
    /// </returns>
    /// <exception cref="InvalidOperationException">現在の同期コンテキストがnullの場合。</exception>
    /// <remarks>
    /// 契約外：Completionの完了後に外部のcallbackから開始した操作は追跡しません。
    /// 例外の送出はSynchronizationContext.Postの配送を包んで捕捉し、BCLの内部形式には依存しません。
    /// 送出の配送が終わるまでは完了させないため、async voidの終端通知が先に来ても成功にはなりません。
    /// AsyncVoidMethodBuilderが例外時に「例外のPost、続いてOperationCompleted」の順で呼ぶ公開の呼出し順に依存します。
    /// 順序が逆転した場合は例外のPostの前に完了しうるため、例外を投げるハンドラーの検査（`MainWindowEventCompletionTests.Capture_HandlerThrowingAfterAwait_FailsWithOriginalException`）が検出します。その例外は共有ホストの記録にも残ります。
    /// </remarks>
    internal static AsyncEventCapture Capture(Action raiseEvent)
    {
        ArgumentNullException.ThrowIfNull(raiseEvent);
        SynchronizationContext original = SynchronizationContext.Current
            ?? throw new InvalidOperationException("The event fixture requires the WPF synchronization context.");
        var context = new EventCompletionContext(original);
        SynchronizationContext.SetSynchronizationContext(context);
        try { raiseEvent(); }
        finally { SynchronizationContext.SetSynchronizationContext(original); }
        context.RaiseReturned();
        return new AsyncEventCapture(context);
    }

    /// <summary>捕捉したイベント発生の終端と、診断用の開始数を公開します。</summary>
    internal sealed class AsyncEventCapture
    {
        private readonly EventCompletionContext context;

        /// <summary>捕捉用の文脈から結果と診断を参照する口を作ります。</summary>
        internal AsyncEventCapture(EventCompletionContext context) => this.context = context;

        /// <summary>発生が戻り、全操作とPostの配送が終端した時点の結果です。</summary>
        internal Task Completion => context.Completion.Task;

        /// <summary>捕捉中のOperationStarted通知の累計回数です。async voidのほかAsyncOperationなども含みます。</summary>
        internal int StartedCount => context.StartedCount;
    }

    /// <summary>開始・終端の通知とPostの配送・例外だけを観測し、処理の配送は実Dispatcherへ委譲します。</summary>
    internal sealed class EventCompletionContext(SynchronizationContext original) : SynchronizationContext
    {
        private readonly object gate = new();
        private readonly List<Exception> failures = [];
        private int startedOperations;
        private int activeOperations;
        private int pendingPosts;
        private bool raiseReturned;

        /// <summary>発生が戻り、全操作とPostの配送が終端した時点の結果です。</summary>
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>OperationStartedの通知の累計回数です。</summary>
        internal int StartedCount
        {
            get { lock (gate) { return startedOperations; } }
        }

        /// <summary>イベントの発生が戻ったことを記録し、開始0件なら即座に失敗、終端済みなら完了させます。</summary>
        internal void RaiseReturned()
        {
            lock (gate)
            {
                raiseReturned = true;
                if (startedOperations == 0)
                {
                    Completion.TrySetException(new InvalidOperationException(
                        "The async event handler did not start (started=0)."));
                    return;
                }
                CompleteIfIdle();
            }
        }

        public override void OperationStarted()
        {
            lock (gate)
            {
                startedOperations++;
                activeOperations++;
            }
            original.OperationStarted();
        }

        public override void OperationCompleted()
        {
            original.OperationCompleted();
            lock (gate)
            {
                activeOperations--;
                CompleteIfIdle();
            }
        }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (gate) { pendingPosts++; }
            try
            {
                original.Post(_ =>
                {
                    try { callback(state); }
                    catch (Exception exception)
                    {
                        lock (gate) { failures.Add(exception); }
                        throw;
                    }
                    finally
                    {
                        lock (gate)
                        {
                            pendingPosts--;
                            CompleteIfIdle();
                        }
                    }
                }, null);
            }
            catch
            {
                lock (gate) { pendingPosts--; }
                throw;
            }
        }

        public override void Send(SendOrPostCallback callback, object? state) => original.Send(callback, state);
        public override SynchronizationContext CreateCopy() => this;

        private void CompleteIfIdle()
        {
            if (!raiseReturned || activeOperations != 0 || pendingPosts != 0 || startedOperations == 0) { return; }
            if (failures.Count == 0) { Completion.TrySetResult(); }
            else { Completion.TrySetException(failures.ToArray()); }
        }
    }
}
