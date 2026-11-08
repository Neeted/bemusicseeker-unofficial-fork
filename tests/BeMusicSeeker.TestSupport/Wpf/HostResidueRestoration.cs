using System.Text;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using NLog;
using NLog.Config;

namespace BeMusicSeeker.TestSupport.Wpf;

/// <summary>共有ホストの境界の種類です。判定の定義は三つの境界で共通で、メッセージの帰属だけが異なります。</summary>
internal enum HostBoundaryKind
{
    /// <summary>ケースの開始時（ケースの間に残った記録を、次のケースの開始時に失敗にします）。</summary>
    CaseStart,

    /// <summary>ケースの終了時。</summary>
    CaseEnd,

    /// <summary>moduleの終了時（ホストは終端済みで、何も観測も復元もしません）。</summary>
    ModuleEnd
}

/// <summary>復元で戻せなかった残留の一件です。</summary>
/// <param name="Item">戻そうとした対象と、戻せなかった理由の説明です。</param>
/// <param name="Cause">復元が投げた元の例外。例外を伴わない失敗（閉鎖の取消など）はnullです。</param>
internal sealed record HostRestoreFailure(string Item, Exception? Cause);

/// <summary>
/// 境界で出力した観測の記録、復元の失敗、同じ境界の未処理例外から、ケースまたはmoduleの失敗を一つ決める判定です。WPFに依存しません。
/// </summary>
/// <remarks>
/// 失敗にする項目はここの一つの定義（<see cref="IsFailureCategory"/>）だけで、ケースの開始・終了・moduleの終了の三つの境界が同じ定義を使います。
/// 記録だけの項目（<c>E-g</c>、<c>E-a-hwnd</c>、<c>X-detector</c>）は、どの境界でも判定を変えません。
/// 項目の比較は完全一致です。前方一致にすると、<c>E-a-hwnd</c>が<c>E-a</c>として失敗になるためです。
/// </remarks>
internal static class HostObservationVerdict
{
    /// <summary>未準備のWindowの表示の項目名です。検出器・ケース間の観測の要否・失敗の定義が共有します。</summary>
    internal const string UnpreparedWindowCategory = "B";

    private static readonly string[] failureCategories = [UnpreparedWindowCategory, "E-a", "E-b", "E-c", "E-d", "E-e", "E-f"];

    /// <summary>指定の項目が、失敗にする項目（B、E-a〜E-f）かを完全一致で返します。</summary>
    internal static bool IsFailureCategory(string category) => failureCategories.Contains(category, StringComparer.Ordinal);

    /// <summary>
    /// 境界の失敗を決めます。
    /// </summary>
    /// <param name="kind">境界の種類。</param>
    /// <param name="caseName">境界のケース名。moduleの終了ではnullです。</param>
    /// <param name="observations">その境界で出力した記録の一覧（出力と同じ一覧）。</param>
    /// <param name="restoreFailures">その境界の復元で戻せなかった残留。</param>
    /// <param name="unhandled">同じ境界の未処理例外の失敗（<see cref="HostUnhandledExceptionLog"/> の戻り値）。</param>
    /// <returns>
    /// 失敗にする項目の記録も復元の失敗も無ければ <paramref name="unhandled"/> を同じ参照で返します（なければnull）。
    /// それ以外は一つの <see cref="AggregateException"/> を返し、<c>Flatten()</c> した内部例外に、未処理例外の元の例外と復元の元の例外を
    /// 参照のまま含めます。
    /// </returns>
    internal static Exception? Evaluate(
        HostBoundaryKind kind,
        string? caseName,
        IReadOnlyList<HostObservation> observations,
        IReadOnlyList<HostRestoreFailure> restoreFailures,
        Exception? unhandled)
    {
        HostObservation[] failing = [.. observations.Where(observation => IsFailureCategory(observation.Category))];
        if (failing.Length == 0 && restoreFailures.Count == 0) { return unhandled; }

        string where = kind switch
        {
            HostBoundaryKind.CaseStart => $"Shared host residue was observed between the previous case and the start of case '{caseName}'; the case was not started.",
            HostBoundaryKind.CaseEnd => $"Shared host residue was observed during case '{caseName}' (or the preceding between-case period). A failure of the class's TestCleanup for the same case may have been replaced by this failure.",
            _ => "Shared host residue remained at the end of the module (attributed to the module cleanup).",
        };
        var message = new StringBuilder(where);
        foreach (HostObservation observation in failing)
        {
            message.AppendLine().Append("  ").Append(observation.Format());
        }
        if (restoreFailures.Count != 0)
        {
            message.AppendLine().Append(
                "  The residue below could not be restored to the baseline. It may remain for later cases and is not reported again unless it returns to the baseline and diverges again.");
            foreach (HostRestoreFailure restoreFailure in restoreFailures)
            {
                message.AppendLine().Append("  [restore failed] ").Append(restoreFailure.Item);
                if (restoreFailure.Cause != null)
                {
                    message.Append(" | ").Append(restoreFailure.Cause.GetType().FullName).Append(": ").Append(restoreFailure.Cause.Message);
                }
            }
        }
        List<Exception> inner = [];
        if (unhandled != null)
        {
            message.AppendLine().Append("  Unhandled dispatcher exceptions at the same boundary: ").Append(unhandled.Message.ReplaceLineEndings(" / "));
            inner.Add(unhandled);
        }
        inner.AddRange(restoreFailures.Select(failure => failure.Cause).OfType<Exception>());
        return new AggregateException(message.ToString(), inner);
    }
}

/// <summary>
/// 失敗にした残留を、ホストのスレッドで同期的に共有ホストの基準へ戻す処理です。
/// </summary>
/// <remarks>
/// Dispatcherを進めません（PushFrame・ProcessQueued・AwaitPresentationを使いません）。戻すのは、その観測で新しく報告した失敗にする項目だけです。
/// 順序はWindow（閉鎖）、MainWindow、値（<c>Resources["vm"]</c>、文化圏、テーマ、NLog）です。NLogを最後にするのは、文化圏の変更が
/// <c>NLogWrapper</c>を通してログを書き、未初期化なら構成を差し替えるためです。
/// 項目ごとに捕捉して残りの復元を続け、再試行しません。戻せなかった残留は、報告済みの印が残るため以後のケースでは再報告されず、
/// 持ち越されうるまま失敗として一度だけ報告されます。戻せた項目（Window・値）は報告済みの印を消し、再びずれたら再び報告します。
/// Windowの閉鎖は<c>CloseForOwnerShutdown</c>を持つ三つの型（<see cref="SettingsWindow"/>、<see cref="PlaylistPropertyDialog"/>、
/// <see cref="PlaylistSummaryBulkEditDialog"/>）ではその入口、それ以外は<c>Close</c>です。通常の<c>Close</c>は、これらの型では
/// 閉鎖が取り消されて処理が開始されます。<c>MainWindow</c>・<c>ProgressDialog</c>は特別な入口がなく、取り消されれば復元の失敗です。
/// 成功条件はWindowが<c>Application.Windows</c>から外れたことだけで、HWNDの破棄は含めません（残れば<c>E-a-hwnd</c>の記録に表れます）。
/// 文化圏は<c>ResourceService</c>の経路（<c>ChangeCulture</c>）だけで戻し、通知の無い直接代入への後退はしません。
/// テーマは保存値だけを判定し、起動時と同じく保存値の代入と適用を行います（保存はしません）。
/// 限界：NLogの基準は最初に観測した非nullの構成のため、テストが置いた構成が基準になりうることがあります。また、同じ観測で<c>E-f</c>が
/// 報告されていなくても、文化圏の復元が<c>NLogWrapper</c>の初期化で構成を差し替えた場合は、次の境界で<c>E-f</c>として表れえます。
/// </remarks>
internal static class HostResidueRestoration
{
    /// <summary>
    /// 失敗にする残留を基準へ戻し、戻せなかったものを返します。ホストのスレッドから呼びます。例外は投げません。
    /// </summary>
    /// <param name="application">共有ホストのApplication。</param>
    /// <param name="state">基準を持つ比較の状態。</param>
    /// <param name="residues">その観測で報告した残留。失敗にする項目以外は無視します。</param>
    internal static IReadOnlyList<HostRestoreFailure> Restore(Application application, HostResidueState state, IReadOnlyList<HostResidue> residues)
    {
        List<HostRestoreFailure> failures = [];
        Window[] windows = [.. residues.Where(residue => residue.Category is "E-a" or "E-b").Select(residue => residue.Window).OfType<Window>()];

        foreach (Window window in TestWindowPresentationScope.OrderForClosing(windows))
        {
            if (RestoreWindow(application, window, failures)) { state.ReportedWindows.Remove(window); }
        }
        if (windows.Length != 0)
        {
            Attempt(failures, "Application.MainWindow", () =>
            {
                if (application.MainWindow is { } main && windows.Contains(main)) { application.MainWindow = state.BaselineMainWindow; }
            });
        }
        // 値は、戻せた項目だけ報告済みの印を消します。印が残ると、次のケースが同じ値を残しても報告も復元もされず、黙って持ち越されます。
        if (residues.Any(residue => residue.Category == "E-c") && Attempt(failures, "Application.Resources[\"vm\"]", () => RestoreViewModel(application, state))) { state.ViewModel.Reset(); }
        if (residues.Any(residue => residue.Category == "E-d") && Attempt(failures, "Resources.Culture", () => RestoreCulture(state))) { state.Culture.Reset(); }
        if (residues.Any(residue => residue.Category == "E-e") && Attempt(failures, "stored AppearanceTheme", () => RestoreTheme(state))) { state.Theme.Reset(); }
        if (residues.Any(residue => residue.Category == "E-f") && Attempt(failures, "LogManager.Configuration", () => RestoreLogConfiguration(state))) { state.LogConfiguration.Reset(); }
        return failures;
    }

    /// <summary>Windowを閉じ、Application.Windowsから外れていればtrueを返します。</summary>
    private static bool RestoreWindow(Application application, Window window, List<HostRestoreFailure> failures)
    {
        // 所有者の閉鎖などで既に閉じていれば、閉じた扱いにしてCloseを呼びません（閉じ済みのCloseは例外になりえます）。
        if (!IsListed(application, window)) { return true; }
        string item = $"close {window.GetType().FullName} '{window.Title}'";
        Exception? thrown = null;
        try
        {
            switch (window)
            {
                case SettingsWindow settings: settings.CloseForOwnerShutdown(); break;
                case PlaylistPropertyDialog property: property.CloseForOwnerShutdown(); break;
                case PlaylistSummaryBulkEditDialog bulk: bulk.CloseForOwnerShutdown(); break;
                default: window.Close(); break;
            }
        }
        catch (Exception exception) { thrown = exception; }
        bool remains = IsListed(application, window);
        if (thrown != null) { failures.Add(new HostRestoreFailure(remains ? item + " threw and the window remained in Application.Windows" : item + " threw", thrown)); }
        else if (remains) { failures.Add(new HostRestoreFailure(item + ": the closing was cancelled or did not complete (the window remained in Application.Windows)", null)); }
        return !remains;
    }

    private static bool IsListed(Application application, Window window) => application.Windows.Cast<Window>().Contains(window);

    private static void RestoreViewModel(Application application, HostResidueState state)
    {
        object? baseline = state.ViewModel.Baseline;
        // 基準にキーが無ければ、nullを代入せずキーを削除します（観測はキーが無いことと値がnullであることを区別しません）。
        if (baseline == null) { application.Resources.Remove("vm"); }
        else { application.Resources["vm"] = baseline; }
        bool restored = baseline == null ? !application.Resources.Contains("vm") : ReferenceEquals(application.Resources["vm"], baseline);
        if (!restored) { throw new InvalidOperationException("Application.Resources[\"vm\"] did not return to the baseline."); }
    }

    private static void RestoreCulture(HostResidueState state)
    {
        string name = state.Culture.Baseline as string ?? throw new InvalidOperationException("The baseline culture is unset.");
        ResourceService.Current.ChangeCulture(name);
        if (!string.Equals(Resources.Culture?.Name, name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"ResourceService.ChangeCulture did not apply the baseline culture '{name}' (for example, it is not in the language catalog).");
        }
    }

    private static void RestoreTheme(HostResidueState state)
    {
        string theme = state.Theme.Baseline as string ?? throw new InvalidOperationException("The baseline theme is unset.");
        Settings.Default.AppearanceTheme = theme;
        AppThemeService.ApplyTheme(theme);
        if (!string.Equals(Settings.Default.AppearanceTheme, theme, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The stored AppearanceTheme did not return to the baseline '{theme}'.");
        }
    }

    private static void RestoreLogConfiguration(HostResidueState state)
    {
        LoggingConfiguration baseline = state.LogConfiguration.Baseline as LoggingConfiguration
            ?? throw new InvalidOperationException("The baseline LogManager.Configuration is unavailable.");
        LogManager.Configuration = baseline;
        if (!ReferenceEquals(LogManager.Configuration, baseline))
        {
            throw new InvalidOperationException("LogManager.Configuration did not return to the baseline object.");
        }
    }

    /// <summary>復元を試み、成功すればtrueを返します。失敗は復元の失敗として加えます。</summary>
    private static bool Attempt(List<HostRestoreFailure> failures, string item, Action restore)
    {
        try
        {
            restore();
            return true;
        }
        catch (Exception exception)
        {
            failures.Add(new HostRestoreFailure("restore " + item, exception));
            return false;
        }
    }
}
