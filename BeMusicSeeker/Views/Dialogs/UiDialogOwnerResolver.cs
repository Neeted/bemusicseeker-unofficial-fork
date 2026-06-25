using System;
using System.Linq;
using System.Windows;

namespace BeMusicSeeker.Views.Dialogs;

/// <summary>
/// 通常 dialog route の owner window を一箇所で解決します。modal の前後関係を call site ごとに推測しないための部品です。
/// </summary>
internal sealed class UiDialogOwnerResolver
{
    private readonly Func<Application> getApplication;

    /// <summary>
    /// 現在の WPF application を使う owner resolver を初期化します。
    /// </summary>
    internal UiDialogOwnerResolver()
        : this(() => Application.Current)
    {
    }

    /// <summary>
    /// test で application 取得を差し替えられる owner resolver を初期化します。
    /// </summary>
    /// <param name="getApplication">現在の application を返す関数。</param>
    internal UiDialogOwnerResolver(Func<Application> getApplication)
    {
        this.getApplication = getApplication ?? throw new ArgumentNullException(nameof(getApplication));
    }

    /// <summary>
    /// 明示 owner、active window、main window の順で通常 dialog 用 owner を解決します。
    /// </summary>
    /// <param name="requestedOwner">呼び出し側が明示した owner window。</param>
    /// <returns>通常 dialog に使う owner。利用できない場合は null。</returns>
    internal Window ResolveOwner(Window requestedOwner = null)
    {
        if (IsUsableOwner(requestedOwner))
        {
            return requestedOwner;
        }

        Application application = getApplication();
        if (application == null)
        {
            return null;
        }

        Window activeWindow = application.Windows
            .OfType<Window>()
            .FirstOrDefault(window => IsUsableOwner(window) && window.IsActive);
        if (activeWindow != null)
        {
            return activeWindow;
        }

        return IsUsableOwner(application.MainWindow) ? application.MainWindow : null;
    }

    /// <summary>
    /// owner として使える表示中 window かどうかを判定します。
    /// </summary>
    /// <param name="window">判定対象 window。</param>
    /// <returns>owner として使える場合は true。</returns>
    internal static bool IsUsableOwner(Window window)
    {
        return window != null && window.IsLoaded && window.Visibility == Visibility.Visible;
    }
}
