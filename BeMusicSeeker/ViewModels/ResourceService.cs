using System.Globalization;
using BeMusicSeeker.Models.Localization;
using BeMusicSeeker.Properties;
using Livet;

namespace BeMusicSeeker.ViewModels;

public class ResourceService : ViewModel
{
    public static ResourceService Current { get; } = new ResourceService();

    public Resources Resources { get; private set; } = new Resources();

    public void ChangeCulture(string name)
    {
        if (JsonLanguageCatalog.ContainsCulture(name))
        {
            Ribbit.Logging.NLogWrapper.FileLogger?.Info($"[ResourceService] ChangeCulture: switching to '{name}'");
            JsonLanguageCatalog.Invalidate(name);
            BeMusicSeeker.Properties.Resources.Culture = CultureInfo.GetCultureInfo(name);
            Resources = new Resources();
            RaisePropertyChanged(() => Resources);
        }
    }
}
