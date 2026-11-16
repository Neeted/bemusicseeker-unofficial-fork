using System.Globalization;
using BeMusicSeeker.Models.Localization;
using BeMusicSeeker.Properties;
using Livet;

namespace BeMusicSeeker.ViewModels;

public class ResourceService : ViewModel
{
    public static ResourceService Current { get; } = new ResourceService();

    public Resources Resources { get; private set; } = new Resources();

    /// <summary>表示部品の書式に使う現在の文化圏です。起動時の設定順に依存せず、未指定時は文字列資源と同じUI文化圏を使います。</summary>
    public CultureInfo DisplayCulture => Properties.Resources.Culture ?? CultureInfo.CurrentUICulture;

    /// <summary>対応する表示言語を適用し、文字列と日付書式の更新を通知します。</summary>
    /// <param name="name">同梱または追加された言語の文化圏名。</param>
    public void ChangeCulture(string name)
    {
        if (JsonLanguageCatalog.ContainsCulture(name))
        {
            Ribbit.Logging.NLogWrapper.FileLogger?.Info($"[ResourceService] ChangeCulture: switching to '{name}'");
            JsonLanguageCatalog.Invalidate(name);
            BeMusicSeeker.Properties.Resources.Culture = CultureInfo.GetCultureInfo(name);
            Resources = new Resources();
            RaisePropertyChanged(nameof(Resources));
            RaisePropertyChanged(nameof(DisplayCulture));
        }
    }
}
