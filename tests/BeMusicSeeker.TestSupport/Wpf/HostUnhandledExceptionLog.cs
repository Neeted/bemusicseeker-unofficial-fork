using System.Runtime.ExceptionServices;

namespace BeMusicSeeker.TestSupport.Wpf;

/// <summary>
/// 共有WPFホストのDispatcherで<c>UnhandledException</c>に達した例外を、順序と発生時のケースとともに保持します。
/// </summary>
/// <remarks>
/// WPFホストを起動したり読んだりしません。ホストが起動していない間は記録が空で、境界の処理は何も報告しません（ケースの開始・終了の状態は更新します）。
/// 先行ケースがホストを起動した後は、ホストを使わないケースでも残留の報告と期間中の記録の帰属を受けます。
/// 帰属はケースが直列に実行される前提（Windowsは1 worker）に基づきます。ケースの重なりを検出した場合は、
/// 誤った帰属をせず「帰属不能」として報告します。
/// 記録の対象は<c>Dispatcher.UnhandledException</c>に達した例外です。BeginInvoke・Post、Delegateを受け取る旧形式の
/// <c>Dispatcher.Invoke(DispatcherPriority, Delegate)</c>の呼出し先、Win32メッセージから配送される入力・ウィンドウイベントの
/// ハンドラーなどが該当します。旧形式のInvokeの呼出し先の例外は、Handled=trueのため呼出し側へ返りません。
/// そのInvokeを囲む待機・消化APIの呼出し中なら、そのAPIが元の例外で失敗します。それ以外はAssertNoUnhandledExceptions・
/// TakeUnhandledExceptionsか、ケースの終了時に表れます。後から呼んだ待機・消化APIは、呼出し開始時の位置以降の記録だけを
/// 使うため、失敗しません。Action・Funcを受け取るInvoke・InvokeAsyncの例外は
/// 呼出し側（Task）へ返り、記録されません。
/// </remarks>
internal static class HostUnhandledExceptionLog
{
    private static readonly Lock gate = new();
    private static readonly List<Entry> entries = [];
    private static long nextSequence;
    private static string? currentCase;
    private static string? lastCase;

    /// <summary>記録の位置です。これ以降に記録された例外を「呼出し中」の記録として扱います。</summary>
    internal static long Position
    {
        get { lock (gate) { return nextSequence; } }
    }

    /// <summary>現在のケース名と、ケースの間なら直前のケース名を読み取ります。記録・帰属・消費の状態は変えません。</summary>
    /// <returns>ケース中は <c>Current</c> が非nullで <c>Preceding</c> がnull、ケースの間は <c>Current</c> がnullで <c>Preceding</c> が直前のケース名（なければnull）。</returns>
    internal static (string? Current, string? Preceding) ReadCase()
    {
        lock (gate) { return (currentCase, currentCase == null ? lastCase : null); }
    }

    /// <summary>Dispatcherスレッド上で、到達した例外を現在のケース（ケースの間なら直前のケース）の記録に加えます。</summary>
    internal static void Record(Exception exception)
    {
        lock (gate)
        {
            entries.Add(new Entry(nextSequence++, exception, currentCase, currentCase == null ? lastCase : null));
        }
    }

    /// <summary>
    /// 指定位置以降に記録された未報告の例外を消費し、最初の例外を元の例外のまま再送出します。
    /// 同じ区間の二番目以降は最初の例外のDataへ添付します。記録がなければ何もしません。
    /// </summary>
    internal static void ThrowRecordedSince(long position)
    {
        Exception[] taken;
        lock (gate)
        {
            taken = [.. entries.Where(entry => entry.Sequence >= position).Select(entry => entry.Exception)];
            entries.RemoveAll(entry => entry.Sequence >= position);
        }
        if (taken.Length == 0) { return; }
        for (int index = 1; index < taken.Length; index++)
        {
            taken[0].Data["HostUnhandledException." + index] = taken[index].ToString();
        }
        ExceptionDispatchInfo.Capture(taken[0]).Throw();
    }

    /// <summary>現在のケースの期間に記録された例外だけを、記録順に受け取って消費します。他ケースとケースの間の記録は受け取れません。</summary>
    internal static IReadOnlyList<Exception> TakeCurrentCase()
    {
        lock (gate)
        {
            if (currentCase == null) { return []; }
            Exception[] taken = [.. entries.Where(entry => entry.CaseName == currentCase).Select(entry => entry.Exception)];
            entries.RemoveAll(entry => entry.CaseName == currentCase);
            return taken;
        }
    }

    /// <summary>
    /// ケースの開始を記録します。ケースの間に残った記録は直前のケースの残留として消費して返します。
    /// 前ケースの終了処理を経ずに始まった重なりは、記録の有無にかかわらず失敗として返し、消費されていない記録は帰属不能として添えます。
    /// 前ケースの終了処理が呼ばれないこと自体が境界処理の前提崩れで、以後の帰属を信頼できないためです。
    /// 報告が返る場合、このケースは開始しなかった扱い（終了処理が呼ばれない）です。
    /// </summary>
    internal static Exception? BeginCase(string name)
    {
        lock (gate)
        {
            Exception? failure = null;
            if (currentCase != null)
            {
                failure = CreateFailure(
                    $"Case '{name}' started before case '{currentCase}' ended its boundary; case attribution is no longer reliable, so unhandled dispatcher exceptions (if any) cannot be attributed to one case.",
                    TakeAll());
            }
            else if (entries.Count != 0)
            {
                failure = CreateFailure(
                    $"Unhandled dispatcher exceptions remained from before case '{name}' (residue of the previous case '{lastCase ?? "(none)"}').",
                    TakeAll());
            }
            if (failure == null) { currentCase = name; }
            else
            {
                // 報告したケースは終了処理が呼ばれないため、ケースの間へ戻して次の開始の重なり判定を避けます。
                currentCase = null;
                lastCase = name;
            }
            return failure;
        }
    }

    /// <summary>
    /// <see cref="BeginCase"/> が成功した後に、境界の別の判定（観測の失敗）でそのケースを開始しなかった扱いへ戻します。
    /// </summary>
    /// <remarks>
    /// 開始に失敗したケースは本体も終了処理も実行されないため、ケースの間（直前：このケース）の状態へ戻します。
    /// 既に開始前の状態なら何もしません（<see cref="BeginCase"/> が失敗を返した場合）。
    /// </remarks>
    internal static void AbandonCase(string name)
    {
        lock (gate)
        {
            if (currentCase != name) { return; }
            currentCase = null;
            lastCase = name;
        }
    }

    /// <summary>ケースの終了を記録し、そのケースで消費されなかった記録を消費して返します。</summary>
    internal static Exception? EndCase(string name)
    {
        lock (gate)
        {
            Exception? failure = null;
            if (currentCase == name)
            {
                Entry[] own = [.. entries.Where(entry => entry.CaseName == name)];
                entries.RemoveAll(entry => entry.CaseName == name);
                if (own.Length != 0) { failure = CreateFailure($"Unhandled dispatcher exceptions occurred during case '{name}'. A failure of the class's TestCleanup for the same case may have been replaced by this failure.", own); }
                currentCase = null;
                lastCase = name;
            }
            else
            {
                failure = entries.Count == 0 ? null : CreateFailure(
                    $"Case '{name}' ended while '{currentCase ?? "(none)"}' was current; unhandled dispatcher exceptions cannot be attributed.",
                    TakeAll());
            }
            return failure;
        }
    }

    /// <summary>module終了時に、未報告の記録を最後のケースの残留として消費して返します。</summary>
    internal static Exception? TakeResidueAtModuleEnd()
    {
        lock (gate)
        {
            if (entries.Count == 0) { return null; }
            return CreateFailure(
                $"Unhandled dispatcher exceptions remained at the end of the module (residue of the last case '{currentCase ?? lastCase ?? "(none)"}').",
                TakeAll());
        }
    }

    private static Entry[] TakeAll()
    {
        Entry[] all = [.. entries];
        entries.Clear();
        return all;
    }

    private static AggregateException CreateFailure(string message, Entry[] taken)
    {
        string detail = string.Join(
            Environment.NewLine,
            taken.Select(entry => $"  #{entry.Sequence} case={entry.CaseName ?? "(between cases; after '" + (entry.PrecedingCaseName ?? "(none)") + "')"}: {entry.Exception.GetType().FullName}: {entry.Exception.Message}"));
        return new AggregateException(message + Environment.NewLine + detail, taken.Select(entry => entry.Exception));
    }

    private sealed record Entry(long Sequence, Exception Exception, string? CaseName, string? PrecedingCaseName);
}
