using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace BeMusicSeeker.Views.Dialogs;

/// <summary>
/// 通常 dialog route の owner window を一箇所で解決します。modal の前後関係を call site ごとに推測しないための部品です。
/// </summary>
internal sealed class UiDialogOwnerResolver
{
    private static readonly object activeModalLock = new();

    private static readonly List<Window> activeModalWindows = [];

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
    /// coordinator 管理中 modal、明示 owner、active window、main window の順で通常 dialog 用 owner を解決します。
    /// </summary>
    /// <param name="requestedOwner">呼び出し側が明示した owner window。</param>
    /// <returns>通常 dialog に使う owner。利用できない場合は null。</returns>
    internal Window ResolveOwner(Window requestedOwner = null)
    {
        Window activeModalWindow = ResolveActiveModalWindow();
        if (activeModalWindow != null)
        {
            return activeModalWindow;
        }

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

    internal static IDisposable PushActiveModal(Window modalWindow)
    {
        if (modalWindow == null)
        {
            throw new ArgumentNullException(nameof(modalWindow));
        }

        lock (activeModalLock)
        {
            activeModalWindows.Add(modalWindow);
        }

        return new ActiveModalRegistration(modalWindow);
    }

    private static Window ResolveActiveModalWindow()
    {
        lock (activeModalLock)
        {
            for (int i = activeModalWindows.Count - 1; i >= 0; i--)
            {
                Window window = activeModalWindows[i];
                if (IsUsableOwner(window))
                {
                    return window;
                }
                if (window == null)
                {
                    activeModalWindows.RemoveAt(i);
                }
            }
        }

        return null;
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

    private sealed class ActiveModalRegistration : IDisposable
    {
        private Window modalWindow;

        internal ActiveModalRegistration(Window modalWindow)
        {
            this.modalWindow = modalWindow;
        }

        public void Dispose()
        {
            Window window = modalWindow;
            modalWindow = null;
            if (window == null)
            {
                return;
            }

            lock (activeModalLock)
            {
                activeModalWindows.Remove(window);
            }
        }
    }
}
