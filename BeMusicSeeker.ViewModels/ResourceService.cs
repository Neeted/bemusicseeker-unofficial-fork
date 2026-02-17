using System.Globalization;
using System.Linq;
using BeMusicSeeker.Properties;
using Livet;

namespace BeMusicSeeker.ViewModels;

public class ResourceService : ViewModel
{
	public static ResourceService Current { get; } = new ResourceService();

	public Resources Resources { get; } = new Resources();

	public void ChangeCulture(string name)
	{
		if (App.AvailableCultures.Values.Contains(name))
		{
			Resources.Culture = CultureInfo.GetCultureInfo(name);
			RaisePropertyChanged(() => Resources);
		}
	}
}
