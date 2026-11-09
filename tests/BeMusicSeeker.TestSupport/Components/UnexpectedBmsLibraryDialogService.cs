using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Wpf;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>
/// ライブラリ用の窓口を明示しないテストの既定です。モデルは既定の実装を持たないため、
/// 同期の確認は題名・本文・ボタンを含む例外にし、待たない投函は記録して予期しない通知を見えるようにします。
/// </summary>
/// <remarks>
/// 投函の呼出し元（<c>PostOperationNotices</c>）は窓口の例外を捕捉して診断だけにするため、例外では失敗が隠れます。
/// そこで予期しない投函は例外にせず、呼ばれた時点で記録します。共有ホストの観測を持つmodule（Windowsなど）では観測記録の項目 <c>D</c> に残し、
/// ケースの境界の判定が、その投函を起こしたケースの失敗にします。観測を持たないmodule（Core）では静的な一覧に残し、
/// module終了の <see cref="TakeUnexpectedPosts"/> を持つ後始末が失敗にします（ケースの帰属は記録の行の呼出し元で辿ります）。
/// </remarks>
internal sealed class UnexpectedBmsLibraryDialogService : IBmsLibraryDialogService
{
    private const int SummaryLength = 200;
    private const int CallerFrames = 8;
    private static readonly Lock gate = new();
    private static readonly List<string> unexpectedPosts = [];

    /// <inheritdoc />
    public UiDialogDefaultResult Confirm(string messageBoxText, string caption, UiDialogButton button, UiDialogIcon icon,
        UiDialogDefaultResult defaultResult = UiDialogDefaultResult.None)
        => throw new InvalidOperationException("予期しないライブラリの確認です。題名: " + caption + " / 文言: " + messageBoxText + " / ボタン: " + button);

    /// <inheritdoc />
    public void Post(IReadOnlyList<BMSLibrary.OperationDialogMessage> messages, Func<bool> shouldSuppress)
    {
        BMSLibrary.OperationDialogMessage? first = messages.Count == 0 ? null : messages[0];
        string line = $"method={nameof(Post)} | request={typeof(BMSLibrary.OperationDialogMessage).FullName}[{messages.Count}] | title={Summarize(first?.Caption)} | text={Summarize(first?.MessageBoxText)} | caller={Caller()}";
        if (HostResidueBoundary.IsEnabled)
        {
            HostObservationLog.Add(HostObservationVerdict.UndeclaredDialogCategory, line);
            return;
        }
        lock (gate) { unexpectedPosts.Add(line); }
    }

    /// <summary>
    /// 共有ホストの観測を持たないmoduleで記録した、予期しない投函の行を受け取って消費します。
    /// module終了の後始末が呼び、一件でもあれば失敗にします。
    /// </summary>
    internal static IReadOnlyList<string> TakeUnexpectedPosts()
    {
        lock (gate)
        {
            string[] taken = [.. unexpectedPosts];
            unexpectedPosts.Clear();
            return taken;
        }
    }

    private static string Caller()
    {
        IEnumerable<string> frames = new System.Diagnostics.StackTrace(2, false).GetFrames()
            .Select(frame => frame.GetMethod())
            .Where(method => method?.DeclaringType != null)
            .Take(CallerFrames)
            .Select(method => method!.DeclaringType!.Name + "." + method.Name);
        return string.Join(" < ", frames);
    }

    private static string Summarize(string? value)
    {
        if (value == null) { return "-"; }
        string flat = value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        if (flat.Length <= SummaryLength) { return flat; }
        int length = char.IsHighSurrogate(flat[SummaryLength - 1]) ? SummaryLength - 1 : SummaryLength;
        return flat[..length] + "…";
    }
}
