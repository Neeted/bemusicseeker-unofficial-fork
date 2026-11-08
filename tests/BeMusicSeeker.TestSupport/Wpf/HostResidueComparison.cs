using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BeMusicSeeker.Properties;
using NLog;

namespace BeMusicSeeker.TestSupport.Wpf;

/// <summary>ケース終了時に観測した、共有ホストの状態の読取り結果です。</summary>
/// <param name="Windows">ホストのスレッドの<c>Application.Windows</c>。</param>
/// <param name="MainWindow"><c>Application.MainWindow</c>。</param>
/// <param name="ViewModelResource"><c>Application.Resources["vm"]</c>。なければnull。</param>
/// <param name="Culture"><c>Properties.Resources.Culture</c>の名前。未設定ならnull。</param>
/// <param name="StoredTheme">保存値（<c>Settings.Default.AppearanceTheme</c>）。</param>
/// <param name="LogConfiguration"><c>LogManager.Configuration</c>。</param>
/// <param name="UnmatchedHandles">ホストのスレッドのトップレベルHWNDのうち、<c>Application.Windows</c>のどれにも（所有関係を含めて）対応しないもの。</param>
/// <param name="PendingOperations">Background以上で未消化のDispatcher操作。</param>
internal sealed record HostResidueSnapshot(
    IReadOnlyList<Window> Windows,
    Window? MainWindow,
    object? ViewModelResource,
    string? Culture,
    string? StoredTheme,
    object? LogConfiguration,
    IReadOnlyList<nint> UnmatchedHandles,
    IReadOnlyList<DispatcherOperation> PendingOperations);

/// <summary>基準と比べて報告した残留の一件です。</summary>
/// <param name="Category"><c>E-a</c>（Application.Windows）、<c>E-a-hwnd</c>（HWNDのみ）、<c>E-b</c>（MainWindowのみ）、<c>E-c</c>〜<c>E-g</c>。</param>
/// <param name="Detail">内容です。</param>
/// <param name="Window"><c>E-a</c>・<c>E-b</c>で報告したWindow。復元が閉じる対象です。それ以外はnullです。</param>
internal sealed record HostResidue(string Category, string Detail, Window? Window = null);

/// <summary>
/// 基準と、報告済みの印を持つ比較の状態です。共有ホスト用と、恒久テスト用の独立した状態を同じ形で作れます。
/// </summary>
/// <remarks>
/// 同一性は、文化圏・保存値は値、<c>Resources["vm"]</c>・NLog構成・Windowは参照で判定します。
/// 報告済みの印は、その値が基準へ戻る（Windowは残留でなくなる）と消え、後で再びずれたら再び報告します。
/// 状態は単一のスレッド（ホストのスレッドか、比較を直列に呼ぶ側）から使います。
/// </remarks>
internal sealed class HostResidueState
{
    internal HostResidueState(HostResidueSnapshot baseline)
    {
        BaselineWindows = new HashSet<Window>(baseline.Windows, ReferenceEqualityComparer.Instance);
        BaselineMainWindow = baseline.MainWindow;
        BaselineHandles = [.. baseline.UnmatchedHandles];
        ViewModel = new Mark(baseline.ViewModelResource, ReferenceEquals);
        Culture = new Mark(baseline.Culture, SameString);
        Theme = new Mark(baseline.StoredTheme, SameString);
        LogConfiguration = new Mark(baseline.LogConfiguration, ReferenceEquals);
        // 基準の時点で未消化だった操作は、基準の一部として報告しません。
        foreach (DispatcherOperation operation in baseline.PendingOperations) { ReportedOperations.Add(operation); }
    }

    private static bool SameString(object? left, object? right)
        => string.Equals(left as string, right as string, StringComparison.Ordinal);

    internal HashSet<Window> BaselineWindows { get; }

    internal Window? BaselineMainWindow { get; }

    internal HashSet<nint> BaselineHandles { get; }

    internal HashSet<Window> ReportedWindows { get; } = new(ReferenceEqualityComparer.Instance);

    internal HashSet<nint> ReportedHandles { get; } = [];

    internal HashSet<DispatcherOperation> ReportedOperations { get; } = new(ReferenceEqualityComparer.Instance);

    internal Mark ViewModel { get; }

    internal Mark Culture { get; }

    internal Mark Theme { get; }

    /// <summary>基準が「最初に観測した非nullの構成」のため、nullの間は基準を採用しません。</summary>
    internal Mark LogConfiguration { get; }

    /// <summary>基準の値と、現在報告済みの値を持つ、値または参照で比べる印です。</summary>
    internal sealed class Mark(object? baseline, Func<object?, object?, bool> same)
    {
        private bool hasReported;
        private object? reported;

        internal object? Baseline { get; set; } = baseline;

        /// <summary>報告済みの印を消します。復元で値を基準へ戻したときに呼び、同じ値への再びのずれを再び報告できるようにします。</summary>
        internal void Reset()
        {
            hasReported = false;
            reported = null;
        }

        /// <summary>基準と異なり、まだ報告していない値のときだけtrueを返します。基準へ戻ったら報告済みの印を消します。</summary>
        internal bool ShouldReport(object? current)
        {
            if (same(Baseline, current))
            {
                hasReported = false;
                reported = null;
                return false;
            }
            if (hasReported && same(reported, current)) { return false; }
            hasReported = true;
            reported = current;
            return true;
        }
    }
}

/// <summary>
/// 共有ホストの状態を、基準と比べる処理です。状態を変更しません（閉じない、復元しない）。戻す処理は <see cref="HostResidueRestoration"/> です。
/// </summary>
internal static class HostResidueComparison
{
    /// <summary>ホストのスレッドで、共有ホストの現在の状態を読み取ります。ホストのスレッドから呼びます。</summary>
    /// <param name="application">共有ホストのApplication。</param>
    /// <param name="nativeThreadId">ホストのスレッドのWin32 thread ID。</param>
    /// <param name="counter">未消化操作の計数。</param>
    internal static HostResidueSnapshot Observe(Application application, uint nativeThreadId, PendingOperationCounter counter)
    {
        Window[] windows = [.. application.Windows.Cast<Window>()];
        HashSet<nint> matched = [];
        foreach (Window window in windows)
        {
            nint handle = new WindowInteropHelper(window).Handle;
            if (handle == 0) { continue; }
            matched.Add(handle);
            // ShowInTaskbar=falseのWindowにはWPFが作るnative ownerがあり、Windowの一部として扱います。
            for (nint owner = TestWindowPresentationScope.GetNativeOwner(handle);
                owner != 0 && matched.Add(owner);
                owner = TestWindowPresentationScope.GetNativeOwner(owner))
            {
            }
        }
        return new HostResidueSnapshot(
            windows,
            application.MainWindow,
            application.Resources["vm"],
            Resources.Culture?.Name,
            Settings.Default.AppearanceTheme,
            LogManager.Configuration,
            [.. TestWindowPresentationScope.EnumerateNativeWindows(nativeThreadId).Where(handle => !matched.Contains(handle))],
            counter.PendingAtOrAboveBackground());
    }

    /// <summary>
    /// 基準と比べ、まだ報告していない残留だけを返し、報告済みの印を更新します。
    /// </summary>
    /// <remarks>
    /// 同じWindowは、<c>Application.Windows</c>とMainWindowの両方に該当しても一つの記録にします。
    /// Windowの内容の読取りがあるため、Windowを観測したスレッド（ホストのスレッド）から呼びます。
    /// </remarks>
    internal static IReadOnlyList<HostResidue> Compare(HostResidueState state, HostResidueSnapshot snapshot)
    {
        List<HostResidue> found = [];

        HashSet<Window> inWindows = new(snapshot.Windows, ReferenceEqualityComparer.Instance);
        List<Window> candidates = [.. snapshot.Windows.Where(window => !state.BaselineWindows.Contains(window))];
        if (snapshot.MainWindow is { } main && !ReferenceEquals(main, state.BaselineMainWindow) && !candidates.Contains(main))
        {
            candidates.Add(main);
        }
        state.ReportedWindows.RemoveWhere(window => !candidates.Contains(window));
        foreach (Window window in candidates)
        {
            if (!state.ReportedWindows.Add(window)) { continue; }
            bool isMain = ReferenceEquals(window, snapshot.MainWindow);
            bool listed = inWindows.Contains(window);
            found.Add(new HostResidue(
                listed ? "E-a" : "E-b",
                $"window remained: type={window.GetType().FullName}, title='{window.Title}', in Application.Windows={(listed ? "yes" : "no")}, Application.MainWindow={(isMain ? "yes" : "no")}, loaded={window.IsLoaded}, visible={window.IsVisible}, owner={window.Owner?.GetType().FullName ?? "(none)"}",
                window));
        }

        HashSet<nint> residueHandles = [.. snapshot.UnmatchedHandles.Where(handle => !state.BaselineHandles.Contains(handle))];
        state.ReportedHandles.RemoveWhere(handle => !residueHandles.Contains(handle));
        foreach (nint handle in snapshot.UnmatchedHandles)
        {
            if (residueHandles.Contains(handle) && state.ReportedHandles.Add(handle))
            {
                // 分類の入力は、ホストのスレッドで、この観測の時点のHWNDから収集します。記録だけの項目で、失敗にも復元にもしません。
                string className = TestWindowPresentationScope.GetNativeClassName(handle);
                bool hasHwndSource = HwndSource.FromHwnd(handle) != null;
                bool visible = TestWindowPresentationScope.IsNativeWindowVisible(handle);
                found.Add(new HostResidue("E-a-hwnd", $"HWND without a WPF Window remained on the host thread: hwnd=0x{handle:X}, class='{className}', kind={ClassifyHwnd(className, hasHwndSource, visible)}, hwnd-source={(hasHwndSource ? "yes" : "no")}, visible={(visible ? "yes" : "no")}"));
            }
        }

        if (state.ViewModel.ShouldReport(snapshot.ViewModelResource))
        {
            found.Add(new HostResidue("E-c", $"Application.Resources[\"vm\"] differs from the baseline: {Describe(snapshot.ViewModelResource)}"));
        }
        if (state.Culture.ShouldReport(snapshot.Culture))
        {
            found.Add(new HostResidue("E-d", $"Resources.Culture differs from the baseline '{state.Culture.Baseline ?? "(unset)"}': '{snapshot.Culture ?? "(unset)"}'"));
        }
        if (state.Theme.ShouldReport(snapshot.StoredTheme))
        {
            found.Add(new HostResidue("E-e", $"the stored AppearanceTheme differs from the baseline '{state.Theme.Baseline ?? "(unset)"}': '{snapshot.StoredTheme ?? "(unset)"}'"));
        }
        // 基準は最初に観測した非nullの構成です。nullから非nullへの変化（NLogWrapperの遅延初期化）は基準として採用します。
        if (state.LogConfiguration.Baseline == null && snapshot.LogConfiguration != null)
        {
            state.LogConfiguration.Baseline = snapshot.LogConfiguration;
        }
        if (state.LogConfiguration.ShouldReport(snapshot.LogConfiguration))
        {
            found.Add(new HostResidue("E-f", $"LogManager.Configuration is not the baseline object: {Describe(snapshot.LogConfiguration)}"));
        }

        state.ReportedOperations.RemoveWhere(operation => !snapshot.PendingOperations.Contains(operation));
        DispatcherOperation[] unreported = [.. snapshot.PendingOperations.Where(operation => !state.ReportedOperations.Contains(operation))];
        if (unreported.Length != 0)
        {
            foreach (DispatcherOperation operation in unreported) { state.ReportedOperations.Add(operation); }
            string priorities = string.Join(", ", unreported.GroupBy(operation => operation.Priority).OrderByDescending(group => group.Key)
                .Select(group => $"{group.Key}={group.Count()}"));
            found.Add(new HostResidue("E-g", $"{unreported.Length} pending dispatcher operation(s) at Background or higher: {priorities}"));
        }
        return found;
    }

    /// <summary>
    /// Windowに対応しないHWNDの残留を分類します（記録だけの項目の補助で、判定には使いません）。
    /// </summary>
    /// <returns>
    /// <c>wpf-internal</c>（クラス名が序数比較で<c>HwndWrapper[</c>で始まり、HwndSourceが無く、非表示）、
    /// <c>ime</c>（クラス名が<c>IME</c>または<c>MSCTFIME UI</c>に完全一致し、非表示）、それ以外は<c>other</c>。
    /// </returns>
    /// <remarks>限界：Windowの隠し所有者の漏れは、WPFの内部HWNDと同じ特徴を持つため<c>wpf-internal</c>と区別できません。</remarks>
    internal static string ClassifyHwnd(string? className, bool hasHwndSource, bool isVisible)
    {
        if (className == null || isVisible) { return "other"; }
        if (className.StartsWith("HwndWrapper[", StringComparison.Ordinal) && !hasHwndSource) { return "wpf-internal"; }
        if (className is "IME" or "MSCTFIME UI") { return "ime"; }
        return "other";
    }

    private static string Describe(object? value)
        => value == null ? "(null)" : $"{value.GetType().FullName}#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value)}";
}

/// <summary>
/// ホストのDispatcherのHooksで、Background以上で未消化の操作を数えます。基準はホストの起動時点です。
/// </summary>
/// <remarks>
/// 公開の<c>Dispatcher.Hooks</c>（OperationPosted・OperationCompleted・OperationAborted）で、投稿された操作を追跡します。
/// 優先度の変更（OperationPriorityChanged）は購読せず、数える時点の<c>Priority</c>と<c>Status</c>で判定します。
/// 費用は操作ごとの辞書への追加・削除です。どのスレッドから投稿されても数え落とさないよう、スレッド安全な辞書を使います。
/// </remarks>
internal sealed class PendingOperationCounter
{
    private readonly ConcurrentDictionary<DispatcherOperation, byte> operations = new(ReferenceEqualityComparer.Instance);

    internal PendingOperationCounter(Dispatcher dispatcher)
    {
        DispatcherHooks hooks = dispatcher.Hooks;
        hooks.OperationPosted += (_, args) =>
        {
            if (args.Operation.Status == DispatcherOperationStatus.Pending) { operations.TryAdd(args.Operation, 0); }
        };
        hooks.OperationCompleted += (_, args) => operations.TryRemove(args.Operation, out byte _);
        hooks.OperationAborted += (_, args) => operations.TryRemove(args.Operation, out byte _);
    }

    /// <summary>未消化で優先度がBackground以上の操作を返します。投稿の通知と完了の通知が競合して残った終了済みの操作は取り除きます。</summary>
    internal IReadOnlyList<DispatcherOperation> PendingAtOrAboveBackground()
    {
        List<DispatcherOperation> pending = [];
        foreach (DispatcherOperation operation in operations.Keys)
        {
            if (operation.Status != DispatcherOperationStatus.Pending)
            {
                if (operation.Status != DispatcherOperationStatus.Executing) { operations.TryRemove(operation, out byte _); }
                continue;
            }
            if (operation.Priority >= DispatcherPriority.Background) { pending.Add(operation); }
        }
        return pending;
    }
}

/// <summary>
/// 共有ホストの起動時に基準を取り、ケースの境界で比較して記録へ加え、失敗にする残留を戻す入口です。
/// </summary>
/// <remarks>
/// 起動していない間は何も起動せず何も読みません（<see cref="TestUiDispatcherHost.IsRunning"/>）。
/// 観測自体の失敗は記録して例外を投げません。失敗にする判断は <see cref="HostObservationVerdict"/> です。
/// </remarks>
internal static class HostResidueBoundary
{
    private static volatile bool enabled;
    private static Application? application;
    private static uint nativeThreadId;
    private static PendingOperationCounter? counter;
    private static HostResidueState? sharedState;

    /// <summary>観測を有効にしたUI moduleかどうかを返します。有効でなければ、ホストを起動しても何も取り付けません。</summary>
    internal static bool IsEnabled => enabled;

    /// <summary>
    /// 観測を有効にします。観測の記録を出力する境界（<c>UiModuleLifetime</c>）を持つUI moduleが、最初のケースより前に呼びます。
    /// </summary>
    /// <remarks>
    /// 有効にしないmodule（Core）では、共有ホストが起動しても未準備のWindowの検出器・Dispatcher Hooks・基準の読取りを取り付けません
    /// （出力する境界がなく、記録が消費されないため）。ホストの起動後に呼んだ場合、その起動には取り付けず、観測は動きません。
    /// </remarks>
    internal static void Enable() => enabled = true;

    /// <summary>
    /// ホストのスレッドで、ホストの準備が済んだ後に検出器を取り付け、基準を取ります。失敗は記録し、ホストの起動を妨げません。
    /// </summary>
    internal static void Attach(Application hostApplication, Dispatcher dispatcher, uint hostNativeThreadId)
    {
        try { UnpreparedWindowDetector.Attach(dispatcher); }
        catch (Exception failure) { HostObservationLog.AddDetectorFailure("UnpreparedWindowDetector.Attach", failure); }
        try
        {
            var attachedCounter = new PendingOperationCounter(dispatcher);
            HostResidueSnapshot baseline = HostResidueComparison.Observe(hostApplication, hostNativeThreadId, attachedCounter);
            // 文化圏の基準は、ホストの起動時の値ではなくmoduleの基準（ja-JP）です。
            sharedState = new HostResidueState(baseline with { Culture = "ja-JP" });
            application = hostApplication;
            nativeThreadId = hostNativeThreadId;
            counter = attachedCounter;
        }
        catch (Exception failure) { HostObservationLog.AddDetectorFailure("HostResidueBoundary.Attach", failure); }
    }

    /// <summary>ホストのスレッドで、共有ホストの現在の状態を読み取ります。基準を取れていなければ失敗します。</summary>
    internal static HostResidueSnapshot Observe()
        => HostResidueComparison.Observe(
            application ?? throw new InvalidOperationException("The host residue baseline is unavailable."),
            nativeThreadId,
            counter ?? throw new InvalidOperationException("The host residue baseline is unavailable."));

    /// <summary>
    /// ケース終了時の境界で、共有ホストの状態を基準と比べ、新しい残留を現在のケースの記録へ加え、失敗にする残留を基準へ戻します。
    /// </summary>
    /// <param name="restoreFailures">戻せなかった残留の追加先。</param>
    /// <remarks>
    /// ホストが未起動・終端済みなら何もしません。観測と復元はSend優先度のInvokeを一回だけ使い、復元は失敗にする残留があるときだけ同じInvokeの中で行います。
    /// 残留は、このケースまたはその直前のケースの間に生じたもので、発生時刻は観測しません。観測自体の失敗は記録して例外にしません。
    /// </remarks>
    internal static void ObserveAtCaseEnd(List<HostRestoreFailure> restoreFailures)
        => ObserveAndRestore(restoreFailures, "HostResidueBoundary.ObserveAtCaseEnd", "(occurred during this case or the preceding between-case period) ");

    /// <summary>
    /// ケース開始時の境界で、ケースの間に未準備のWindowの表示（B）の記録があるときだけ、終了時と同じ観測と復元を一回行います。
    /// </summary>
    /// <param name="restoreFailures">戻せなかった残留の追加先。</param>
    /// <remarks>
    /// 記録が無ければInvokeも読取りもしません（成功経路の費用をケースごとにInvoke一回のまま保つため）。
    /// 見つけた残留は、前のケースの終了後に生じたため、ケースの間（直前：前のケース）へ帰属します。呼出しは<see cref="HostUnhandledExceptionLog.BeginCase"/>の前です。
    /// </remarks>
    internal static void ObserveAtCaseStart(List<HostRestoreFailure> restoreFailures)
    {
        if (!HostObservationLog.HasBetweenCases(HostObservationVerdict.UnpreparedWindowCategory)) { return; }
        ObserveAndRestore(restoreFailures, "HostResidueBoundary.ObserveAtCaseStart", "(occurred between cases) ");
    }

    private static void ObserveAndRestore(List<HostRestoreFailure> restoreFailures, string where, string attribution)
    {
        if (!TestUiDispatcherHost.IsRunning || sharedState is not { } state || application is not { } hostApplication) { return; }
        IReadOnlyList<HostResidue> found = [];
        List<HostRestoreFailure> failures = [];
        try
        {
            TestUiDispatcherHost.Invoke(() =>
            {
                // 比較は報告済みの印を更新するため、後続の失敗でも観測した記録を失わないよう、foundは比較の直後に確定させます。
                found = HostResidueComparison.Compare(state, Observe());
                if (!found.Any(residue => HostObservationVerdict.IsFailureCategory(residue.Category))) { return; }
                try { failures.AddRange(HostResidueRestoration.Restore(hostApplication, state, found)); }
                catch (Exception failure) { failures.Add(new HostRestoreFailure("restore (unexpected failure of the restoration itself)", failure)); }
            });
        }
        catch (Exception failure) { HostObservationLog.AddDetectorFailure(where, failure); }
        // 観測した記録と復元の失敗は、Invokeの失敗の有無にかかわらず記録と判定へ反映します。
        try
        {
            foreach (HostResidue residue in found)
            {
                HostObservationLog.Add(residue.Category, attribution + residue.Detail);
            }
        }
        catch (Exception failure) { HostObservationLog.AddDetectorFailure(where, failure); }
        restoreFailures.AddRange(failures);
    }
}
