using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace BeMusicSeeker.TestSupport.Wpf;

/// <summary>共有WPFホストで観測した記録の一件です。失敗にする項目かどうかは <see cref="HostObservationVerdict"/> が決めます。</summary>
/// <param name="Sequence">記録順です。</param>
/// <param name="Category">項目です。<c>B</c>（未準備のWindowの表示）、<c>E-a</c>〜<c>E-g</c>（ケース終了時の残留）、<c>X-detector</c>（検出器自身の失敗）。</param>
/// <param name="Detail">内容です。一行に整形して出力します。</param>
/// <param name="CaseName">記録時のケース名です。ケースの間はnullです。</param>
/// <param name="PrecedingCaseName">ケースの間に記録された場合の直前のケース名です。</param>
internal sealed record HostObservation(long Sequence, string Category, string Detail, string? CaseName, string? PrecedingCaseName)
{
    /// <summary>TRX・Consoleへ出す一行です。件数の集計は先頭の印と項目で行います。</summary>
    internal string Format()
    {
        string where = CaseName != null ? $"case '{CaseName}'" : $"between-cases(after '{PrecedingCaseName ?? "(none)"}')";
        return $"[BMS-OBS] {Category} | {where} | {Detail.ReplaceLineEndings(" / ")}";
    }
}

/// <summary>
/// 共有WPFホストで観測した記録を、ケースの帰属とともに保持し、ケースの境界やmoduleの終了で出力します。
/// </summary>
/// <remarks>
/// 保持と出力だけを担い、判定は <see cref="HostObservationVerdict"/> が境界で行います。WPFホスト・<c>Settings.Default</c>・Applicationを起動も読取りもしません。
/// ケースが直列に実行される前提（<see cref="HostUnhandledExceptionLog"/> と同じ）で、記録時のケースへ帰属させます。
/// 出力したものは消費し、繰り返し出力しません。出力に失敗しても例外を投げず、ケースの判定を変えません。
/// </remarks>
internal static class HostObservationLog
{
    private const string Prefix = "[BMS-OBS] ";
    private static readonly Lock gate = new();
    private static readonly List<HostObservation> records = [];
    private static long nextSequence;

    /// <summary>現在のケース（ケースの間なら直前のケース）の記録として加えます。どのスレッドからでも呼べます。</summary>
    /// <returns>加えた記録。後から <see cref="Append"/> で内容を追記するために使います。</returns>
    internal static HostObservation Add(string category, string detail)
    {
        (string? current, string? preceding) = HostUnhandledExceptionLog.ReadCase();
        lock (gate)
        {
            var record = new HostObservation(nextSequence++, category, detail, current, preceding);
            records.Add(record);
            return record;
        }
    }

    /// <summary>
    /// 追加済みで、まだ出力・消費されていない記録の内容に追記します。順序とケースの帰属は変わりません。
    /// 追記は同じ記録（<see cref="Add"/> が返したもの）に対して一回だけで、二回目は何もしません（記録は追記のたびに置き換わるため）。
    /// 既に出力・消費された記録にも何もしません（呼出し側の処理の後に境界を過ぎた場合）。
    /// </summary>
    internal static void Append(HostObservation record, string suffix)
    {
        lock (gate)
        {
            int index = records.IndexOf(record);
            if (index >= 0) { records[index] = record with { Detail = record.Detail + suffix }; }
        }
    }

    private static readonly Lock rawErrorGate = new();

    /// <summary>
    /// 一行を、MSTestの振り分けを通らないプロセスの標準エラー（生のストリーム）へUTF-8で書き、その場で<c>Flush</c>します。
    /// 強制終了されても行が実行ログに残るための出力で、同時の書込みで行が混ざらないよう排他し、一回の書込みにします。
    /// </summary>
    internal static void WriteRawStandardError(string line)
    {
        byte[] bytes = new System.Text.UTF8Encoding(false).GetBytes(line.ReplaceLineEndings(" / ") + "\n");
        lock (rawErrorGate)
        {
            using Stream stream = Console.OpenStandardError();
            stream.Write(bytes);
            stream.Flush();
        }
    }

    /// <summary>検出器自身の失敗を、表示を妨げずに記録へ加えます。</summary>
    internal static void AddDetectorFailure(string where, Exception failure)
        => Add("X-detector", $"{where}: {failure.GetType().FullName}: {failure.Message}");

    /// <summary>
    /// 現在のケースの期間に記録された指定項目を、記録順に受け取って消費します。消費したものは境界で出力されません。
    /// </summary>
    /// <param name="category">受け取る項目。nullなら全項目です。</param>
    /// <param name="predicate">受け取る記録の追加の条件。nullなら項目の全件です。</param>
    /// <remarks>
    /// ケースの間と他ケースの記録は受け取れません。恒久テストが自分の記録だけを消費するために使います。
    /// 条件に合わない記録は境界に残り、失敗にする項目なら境界で失敗になります。
    /// </remarks>
    internal static IReadOnlyList<HostObservation> TakeCurrentCase(string? category = null, Func<HostObservation, bool>? predicate = null)
    {
        string? current = HostUnhandledExceptionLog.ReadCase().Current;
        if (current == null) { return []; }
        lock (gate)
        {
            HostObservation[] taken = [.. records.Where(record => record.CaseName == current && (category == null || record.Category == category) && (predicate == null || predicate(record)))];
            records.RemoveAll(record => taken.Contains(record));
            return taken;
        }
    }

    /// <summary>ケースの間に、指定項目の記録があるかを、消費せずに返します。ケース開始時の観測の要否の判断に使います。</summary>
    internal static bool HasBetweenCases(string category)
    {
        lock (gate) { return records.Any(record => record.CaseName == null && record.Category == category); }
    }

    /// <summary>ケースの開始時に、ケースの間に記録された分を「ケース間（直前：ケース名）」として出力し、判定用に返します。</summary>
    /// <returns>出力した記録。出力と判定が同じ一覧を使います。</returns>
    internal static IReadOnlyList<HostObservation> WriteBetweenCases(Action<string> write)
        => WriteTaken(record => record.CaseName == null, write);

    /// <summary>ケースの終了時に、そのケースの記録を出力して消費し、判定用に返します。</summary>
    /// <returns>出力した記録。出力と判定が同じ一覧を使います。</returns>
    internal static IReadOnlyList<HostObservation> WriteCase(string caseName, Action<string> write)
        => WriteTaken(record => record.CaseName == caseName, write);

    private static IReadOnlyList<HostObservation> WriteTaken(Func<HostObservation, bool> predicate, Action<string> write)
    {
        HostObservation[] taken = [];
        try
        {
            taken = Take(predicate);
            WriteAll(taken, write);
        }
        catch (Exception failure) { ReportOutputFailure(failure); }
        return taken;
    }

    /// <summary>
    /// module終了時に残った記録（ケースの間に記録された分など）をConsoleと、結果フォルダー内のファイルへ出力し、判定用に返します。
    /// 出力の失敗は判定を変えません。
    /// </summary>
    /// <param name="resultsDirectory">実行の結果フォルダー。取得できなければnullで、Consoleだけへ出します。</param>
    /// <param name="moduleName">ファイル名に使うmodule名。</param>
    /// <returns>出力した記録。出力と判定が同じ一覧を使います。</returns>
    internal static IReadOnlyList<HostObservation> WriteModuleEnd(string? resultsDirectory, string moduleName)
    {
        HostObservation[] taken = [];
        try
        {
            taken = Take(_ => true);
            if (taken.Length == 0) { return taken; }
            string[] lines = [.. taken.Select(record => record.Format())];
            foreach (string line in lines) { Console.WriteLine(line); }
            if (string.IsNullOrWhiteSpace(resultsDirectory))
            {
                Console.WriteLine(Prefix + "X-detector | module-end | the run results directory is unavailable; residue records were written to the console only");
                return taken;
            }
            Directory.CreateDirectory(resultsDirectory);
            File.AppendAllLines(Path.Combine(resultsDirectory, $"host-observation-residue-{moduleName}.txt"), lines);
        }
        catch (Exception failure) { ReportOutputFailure(failure); }
        return taken;
    }

    private static HostObservation[] Take(Func<HostObservation, bool> predicate)
    {
        lock (gate)
        {
            HostObservation[] taken = [.. records.Where(predicate)];
            records.RemoveAll(record => taken.Contains(record));
            return taken;
        }
    }

    /// <summary>
    /// 取り出した記録を一件ずつ書きます。書込みが失敗した記録は、失われないよう失敗とともにConsoleへ出力し、残りの記録の出力を続けます。
    /// </summary>
    private static void WriteAll(HostObservation[] taken, Action<string> write)
    {
        foreach (HostObservation record in taken)
        {
            string line = record.Format();
            try { write(line); }
            catch (Exception failure)
            {
                ReportOutputFailure(failure);
                try { Console.Error.WriteLine(line); }
                catch (Exception) { /* Consoleも使えない場合は、判定を変えないため何もしません。 */ }
            }
        }
    }

    private static void ReportOutputFailure(Exception failure)
    {
        try { Console.Error.WriteLine(Prefix + "X-detector | output | " + failure.GetType().FullName + ": " + failure.Message); }
        catch (Exception) { /* 出力経路自体が使えない場合は、判定を変えないため何もしません。 */ }
    }
}

/// <summary>
/// 共有ホストのスレッドで表示（初回のレイアウト）されたWindowのうち、どの表示範囲でも準備を通していないものを記録します。
/// </summary>
/// <remarks>
/// 記録し、表示の後の最初のSendの操作でスレッドがモーダルであれば、その未準備のWindowを閉じます（記録へ<c>action=close-scheduled</c>を追記）。
/// 入れ子のモーダルループで止まるテスト本体を戻し、境界へ到達させるためで、閉じても記録は消さず判定は失敗のままです。
/// 閉鎖は境界の復元と同じ入口の選択（<see cref="HostResidueRestoration.CloseWindow"/>）を使います。検出器の中（SizeChanged）では閉じず、Dispatcherを進めません。
/// <c>ShowDialog</c>の最初のレイアウトはスレッドがモーダルになる前に起きるため、表示時の<c>thread-modal</c>は<c>no</c>になりえます。
/// 閉鎖が取り消された、または例外になった場合だけ<c>action=close-failed</c>を記録し、例外は未処理例外へ漏らしません。
/// モーダルでない未準備のWindowは閉じず、境界の復元（E-a）が閉じます。失敗にする判断は境界（<see cref="HostObservationVerdict"/>）が行います。
/// 検出器自身の失敗は記録して表示を妨げません。
/// 準備済みのWindowは、モーダルでも記録しません。対象外はWin32のコモンダイアログとネイティブのMessageBox（EmergencyDialog）です。
/// 表示の観測には、Windowの<c>SizeChanged</c>へのクラスハンドラーを使い、表示後の最初のレイアウトでWindowごとに一度観測します。
/// 表示されないWindowは通常記録されません。
/// モーダルかどうかは、公開APIで読める<c>ComponentDispatcher.IsThreadModal</c>（スレッド全体の状態）で記録します。
/// Windowごとのモーダル状態は公開APIにないため、別のモーダルが開いている間に表示された非モーダルのWindowも「thread-modal=yes」と記録します。
/// </remarks>
internal static class UnpreparedWindowDetector
{
    private static readonly ConditionalWeakTable<Window, object> observed = new();

    /// <summary>ホストのDispatcherを対象に、Window共通のSizeChangedへクラスハンドラーを一度だけ取り付けます。</summary>
    /// <remarks>
    /// クラスハンドラーはプロセス全体のため、ホスト以外のDispatcher（専用STAの検査）のWindowは対象外にします。
    /// 取り外せないため、一度だけ呼びます。ホストの起動時に、呼出し元が失敗を握りつぶさず記録します。
    /// </remarks>
    internal static void Attach(Dispatcher hostDispatcher)
    {
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.SizeChangedEvent,
            new RoutedEventHandler((sender, _) => OnShown(sender, hostDispatcher)),
            handledEventsToo: true);
    }

    private static void OnShown(object sender, Dispatcher hostDispatcher)
    {
        try
        {
            if (sender is not Window window || window.Dispatcher != hostDispatcher || TestWindowPresentationScope.IsPrepared(window)) { return; }
            // 大きさの変更は繰り返し発生するため、Windowごとに最初の一度だけ記録します。
            if (!observed.TryAdd(window, observed)) { return; }
            string modal = ComponentDispatcher.IsThreadModal ? "yes" : "no";
            string owner = window.Owner is { } ownerWindow ? ownerWindow.GetType().FullName ?? "(unnamed type)" : "(none)";
            HostObservation record = HostObservationLog.Add(
                HostObservationVerdict.UnpreparedWindowCategory,
                $"unprepared window shown: type={window.GetType().FullName}, title='{window.Title}', thread-modal={modal}, owner={owner}, hwnd=0x{new WindowInteropHelper(window).Handle:X}");
            // ShowDialogの最初のレイアウトは、スレッドがモーダルになる前に起きるため、ここではモーダルかを決められません。
            // 閉鎖の要否は、最初のレイアウトの後に最初に処理されるSendの操作で判断します。モーダルならこの操作は入れ子のループの中で実行されます。
            // ここ（SizeChangedの中）では閉じず、レイアウトへ再入しません。
            window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => CloseIfThreadModal(window, record)));
        }
        catch (Exception failure) { HostObservationLog.AddDetectorFailure("UnpreparedWindowDetector", failure); }
    }

    /// <summary>
    /// スレッドがモーダルの間だけ、未準備のWindowを閉じます。記録の内容に<c>action=close-scheduled</c>を追記します。
    /// 成功は記録せず（<c>ShowDialog</c>が戻ることで観測します）、取り消された場合と例外になった場合だけ、同じ項目Bで<c>action=close-failed</c>を記録します。
    /// 例外はDispatcherの未処理例外へ漏らしません。
    /// </summary>
    private static void CloseIfThreadModal(Window window, HostObservation record)
    {
        try
        {
            Application? application = Application.Current;
            if (!ComponentDispatcher.IsThreadModal || application == null || !HostResidueRestoration.IsListed(application, window)) { return; }
            HostObservationLog.Append(record, ", action=close-scheduled");
            string? reason = null;
            try { HostResidueRestoration.CloseWindow(window); }
            catch (Exception thrown) { reason = $"{thrown.GetType().FullName}: {thrown.Message}"; }
            if (reason == null && !HostResidueRestoration.IsListed(application, window)) { return; }
            HostObservation failed = HostObservationLog.Add(
                HostObservationVerdict.UnpreparedWindowCategory,
                $"unprepared modal window could not be closed: type={window.GetType().FullName}, title='{window.Title}', action=close-failed, reason={reason ?? "canceled"}");
            // 取消・例外で ShowDialog が戻らず停止し、強制終了されても診断が残るよう、記録の行をプロセスの標準エラーへ直接出します。
            // MSTestは Console.Error をテストごとのメモリ上のバッファへ振り分けるため、強制終了で失われます。
            // 出力の失敗は判定を変えず、検出器自身の失敗として記録します。
            try { HostObservationLog.WriteRawStandardError(failed.Format()); }
            catch (Exception outputFailure) { HostObservationLog.AddDetectorFailure("UnpreparedWindowDetector.CloseIfThreadModal output", outputFailure); }
        }
        catch (Exception failure) { HostObservationLog.AddDetectorFailure("UnpreparedWindowDetector.CloseIfThreadModal", failure); }
    }
}
