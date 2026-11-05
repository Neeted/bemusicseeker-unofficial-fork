using BeMusicSeeker.TestSupport.Processes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using static BeMusicSeeker.Desktop.E2E.Tests.Fixtures.DesktopScenarioUi;

namespace BeMusicSeeker.Desktop.E2E.Tests.Update;

/// <summary>起動時の更新ダイアログを、AutomationIdと実UIAパターンだけで操作・観測します。</summary>
/// <remarks>文言や構造位置では特定しません。予期しない所有modalは閉じず、失敗として報告します。</remarks>
internal static class UpdateDialogUi
{
    private const string DialogId = "UpdateAvailableDialog";

    /// <summary>所有MainWindowの更新ダイアログを期限内に一意に見つけます。他の所有modalがあれば閉じず失敗にします。</summary>
    /// <param name="shouldAbort">真を返したら待機を失敗として打ち切る条件です。起動の失敗など、待っても出現しない場合に使います。</param>
    internal static Window WaitForDialog(Window mainWindow, ProcessDeadline deadline, Action<string> step, Func<bool>? shouldAbort = null)
    {
        step("before update dialog wait");
        Window result = Retry.WhileNull(() =>
        {
            if (shouldAbort?.Invoke() == true) { throw new InvalidOperationException("更新ダイアログの出現前に、待機を打ち切る条件が成立しました。"); }
            Window[] modals = mainWindow.ModalWindows;
            Window[] dialogs = [.. modals.Where(window => window.AutomationId == DialogId)];
            if (modals.Length != dialogs.Length)
            { throw new InvalidOperationException("更新ダイアログ以外の所有modalがあります: " + modals.Length + " 件中 " + dialogs.Length + " 件が更新ダイアログ"); }
            if (dialogs.Length > 1) { throw new InvalidOperationException("更新ダイアログが重複しています。"); }
            return dialogs.SingleOrDefault();
        }, timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false,
            timeoutMessage: "所有MainWindowの更新ダイアログが現れませんでした。").Result
            ?? throw new InvalidOperationException("更新ダイアログがありません。");
        step("after update dialog wait");
        return result;
    }

    /// <summary>現在版の値のテキストを読みます。</summary>
    internal static string ReadCurrentVersion(Window dialog, ProcessDeadline deadline, Action<string> step)
        => FindUnique(dialog, "UpdateCurrentVersion", deadline, step).Name;

    /// <summary>最新版の値のテキストを読みます。</summary>
    internal static string ReadLatestVersion(Window dialog, ProcessDeadline deadline, Action<string> step)
        => FindUnique(dialog, "UpdateLatestVersion", deadline, step).Name;

    /// <summary>package一覧の項目それぞれについて、項目内のテキストと選択状態を読みます。</summary>
    internal static IReadOnlyList<PackageItem> ReadPackages(Window dialog, ProcessDeadline deadline, Action<string> step)
    {
        AutomationElement list = FindUnique(dialog, "UpdatePackages", deadline, step);
        return [.. list.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)).Select(item =>
            new PackageItem(item, [.. item.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(text => text.Name)],
                item.Patterns.SelectionItem.Pattern.IsSelected.Value))];
    }

    /// <summary>項目内のテキストにファイル名が完全一致する項目を、一意に選択します。項目のアクセシブル名は使いません。</summary>
    internal static PackageItem SelectPackage(Window dialog, string fileName, ProcessDeadline deadline, Action<string> step)
    {
        EnsureInputDeadline(deadline);
        IReadOnlyList<PackageItem> items = ReadPackages(dialog, deadline, step);
        PackageItem[] matches = [.. items.Where(item => item.Texts.Contains(fileName, StringComparer.Ordinal))];
        Assert.AreEqual(1, matches.Length, "package項目内のファイル名テキストが一意に見つかりません。items=" + string.Join(" | ", items.Select(item => string.Join(",", item.Texts))));
        matches[0].Element.Patterns.SelectionItem.Pattern.Select();
        _ = Retry.WhileFalse(() => matches[0].Element.Patterns.SelectionItem.Pattern.IsSelected.Value,
            timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false, timeoutMessage: "package項目の選択が反映されません。");
        return matches[0];
    }

    /// <summary>適用ボタンの有効化を待ち、有効なことを確認します。</summary>
    internal static AutomationElement WaitApplyEnabled(Window dialog, ProcessDeadline deadline, Action<string> step)
    {
        AutomationElement apply = FindUnique(dialog, "UpdateApply", deadline, step);
        step("before UpdateApply IsEnabled wait");
        _ = Retry.WhileFalse(() => apply.IsEnabled, timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false,
            timeoutMessage: "適用ボタンが有効になりません。");
        step("after UpdateApply IsEnabled wait");
        return apply;
    }

    /// <summary>適用ボタンをInvokeします。入力の前に期限と有効状態を確認します。</summary>
    internal static void Apply(AutomationElement apply, ProcessDeadline deadline, Action<string> step)
    {
        EnsureInputDeadline(deadline);
        Assert.IsTrue(apply.IsEnabled, "適用ボタンが無効です。");
        step("before Invoke UpdateApply");
        apply.Patterns.Invoke.Pattern.Invoke();
        step("after Invoke UpdateApply");
    }

    /// <summary>package項目の要素と、項目内のテキスト、選択状態です。</summary>
    internal sealed record PackageItem(AutomationElement Element, IReadOnlyList<string> Texts, bool IsSelected);
}
