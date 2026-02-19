using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace BeMusicSeeker.Views;

internal class playlistTableFolderListConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (!(value is List<string> source))
		{
			return null;
		}
		return (from PlaylistTableHeaderSpecialFolder f in Enum.GetValues(typeof(PlaylistTableHeaderSpecialFolder))
			select new Tuple<string, bool>(f.ToDisplayName(), item2: true)).Concat(source.Select((string f) => new Tuple<string, bool>(f, item2: false)));
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
