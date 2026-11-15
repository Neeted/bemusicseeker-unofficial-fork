using System.Windows;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.TestSupport.Wpf;

/// <summary>
/// 表示フォントを適用するケースが、共有ホストのアプリのリソースを開始前の状態へ戻すための退避です。
/// 共有ホストは表示フォントを適用しないため、キーがなかった場合は値を戻さずキーを除きます。UIスレッドで使います。
/// </summary>
internal sealed class AppFontResourceSnapshot : IDisposable
{
    private readonly (object Key, bool Present, object? Value)[] entries;

    private AppFontResourceSnapshot((object Key, bool Present, object? Value)[] entries)
    {
        this.entries = entries;
    }

    /// <summary><see cref="AppFontService.ApplyFontFamily"/> が置き換えるキーの現在の状態を退避します。</summary>
    internal static AppFontResourceSnapshot Capture()
    {
        ResourceDictionary resources = Application.Current.Resources;
        object[] keys =
        [
            AppFontService.ResourceKey,
            SystemFonts.MenuFontFamilyKey,
            SystemFonts.StatusFontFamilyKey,
            SystemFonts.MessageFontFamilyKey
        ];
        return new AppFontResourceSnapshot([.. keys.Select(key => (key, resources.Contains(key), resources.Contains(key) ? resources[key] : null))]);
    }

    /// <summary>退避した状態へ戻します。</summary>
    public void Dispose()
    {
        ResourceDictionary resources = Application.Current.Resources;
        foreach ((object key, bool present, object? value) in entries)
        {
            if (present)
            {
                resources[key] = value;
            }
            else
            {
                resources.Remove(key);
            }
        }
    }
}
