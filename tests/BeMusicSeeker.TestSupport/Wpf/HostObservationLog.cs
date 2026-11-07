using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace BeMusicSeeker.TestSupport.Wpf;

/// <summary>共有WPFホストで観測した、判定を変えない記録の一件です。</summary>
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
/// 記録だけで、ケースの判定を変えません。WPFホスト・<c>Settings.Default</c>・Applicationを起動も読取りもしません。
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
    internal static void Add(string category, string detail)
    {
        (string? current, string? preceding) = HostUnhandledExceptionLog.ReadCase();
        lock (gate)
        {
            records.Add(new HostObservation(nextSequence++, category, detail, current, preceding));
        }
    }

    /// <summary>検出器自身の失敗を、表示を妨げずに記録へ加えます。</summary>
    internal static void AddDetectorFailure(string where, Exception failure)
        => Add("X-detector", $"{where}: {failure.GetType().FullName}: {failure.Message}");

    /// <summary>
    /// 現在のケースの期間に記録された指定項目を、記録順に受け取って消費します。消費したものは境界で出力されません。
    /// </summary>
    /// <param name="category">受け取る項目。nullなら全項目です。</param>
    /// <remarks>ケースの間と他ケースの記録は受け取れません。恒久テストが自分の記録を観測するために使います。</remarks>
    internal static IReadOnlyList<HostObservation> TakeCurrentCase(string? category = null)
    {
        string? current = HostUnhandledExceptionLog.ReadCase().Current;
        if (current == null) { return []; }
        lock (gate)
        {
            HostObservation[] taken = [.. records.Where(record => record.CaseName == current && (category == null || record.Category == category))];
            records.RemoveAll(record => taken.Contains(record));
            return taken;
        }
    }

    /// <summary>ケースの開始時に、ケースの間に記録された分を「ケース間（直前：ケース名）」として出力します。</summary>
    internal static void WriteBetweenCases(Action<string> write)
    {
        try
        {
            WriteAll(Take(record => record.CaseName == null), write);
        }
        catch (Exception failure) { ReportOutputFailure(failure); }
    }

    /// <summary>ケースの終了時に、そのケースの記録を出力して消費します。</summary>
    internal static void WriteCase(string caseName, Action<string> write)
    {
        try
        {
            WriteAll(Take(record => record.CaseName == caseName), write);
        }
        catch (Exception failure) { ReportOutputFailure(failure); }
    }

    /// <summary>
    /// module終了時に残った記録（ケースの間に記録された分など）をConsoleと、結果フォルダー内のファイルへ出力します。失敗にはしません。
    /// </summary>
    /// <param name="resultsDirectory">実行の結果フォルダー。取得できなければnullで、Consoleだけへ出します。</param>
    /// <param name="moduleName">ファイル名に使うmodule名。</param>
    internal static void WriteModuleEnd(string? resultsDirectory, string moduleName)
    {
        try
        {
            HostObservation[] taken = Take(_ => true);
            if (taken.Length == 0) { return; }
            string[] lines = [.. taken.Select(record => record.Format())];
            foreach (string line in lines) { Console.WriteLine(line); }
            if (string.IsNullOrWhiteSpace(resultsDirectory))
            {
                Console.WriteLine(Prefix + "X-detector | module-end | the run results directory is unavailable; residue records were written to the console only");
                return;
            }
            Directory.CreateDirectory(resultsDirectory);
            File.AppendAllLines(Path.Combine(resultsDirectory, $"host-observation-residue-{moduleName}.txt"), lines);
        }
        catch (Exception failure) { ReportOutputFailure(failure); }
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
/// 記録だけで、Windowを閉じず、失敗にせず、Dispatcherを進めません。検出器自身の失敗は記録して表示を妨げません。
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
            HostObservationLog.Add(
                "B",
                $"unprepared window shown: type={window.GetType().FullName}, title='{window.Title}', thread-modal={modal}, owner={owner}, hwnd=0x{new WindowInteropHelper(window).Handle:X}");
        }
        catch (Exception failure) { HostObservationLog.AddDetectorFailure("UnpreparedWindowDetector", failure); }
    }
}
