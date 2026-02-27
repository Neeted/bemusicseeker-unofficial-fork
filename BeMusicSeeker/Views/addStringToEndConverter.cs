using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;

namespace BeMusicSeeker.Views;

internal class addStringToEndConverter : IValueConverter
{
	private readonly struct ParsedParameter
	{
		public readonly string Suffix;

		public readonly string EmptyResult;

		public ParsedParameter(string suffix, string emptyResult)
		{
			Suffix = suffix;
			EmptyResult = emptyResult;
		}
	}

	private static readonly Dictionary<string, ParsedParameter> parameterCache = new Dictionary<string, ParsedParameter>(StringComparer.Ordinal);

	private static readonly object parameterCacheLock = new object();

	private static ParsedParameter ParseParameter(object parameter)
	{
		if (!(parameter is string text))
		{
			return new ParsedParameter(string.Empty, string.Empty);
		}
		lock (parameterCacheLock)
		{
			if (!parameterCache.TryGetValue(text, out var value))
			{
				string suffix = string.Empty;
				string emptyResult = string.Empty;
				string[] array = text.Split(new char[1] { '|' }, 2, StringSplitOptions.None);
				if (array.Length > 0)
				{
					suffix = array[0];
				}
				if (array.Length > 1)
				{
					emptyResult = array[1];
				}
				value = new ParsedParameter(suffix, emptyResult);
				parameterCache[text] = value;
			}
			return value;
		}
	}

	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		ParsedParameter parsedParameter = ParseParameter(parameter);
		if (value == null)
		{
			return parsedParameter.EmptyResult;
		}
		string text = ((value as string) ?? value.ToString());
		if (string.IsNullOrWhiteSpace(text))
		{
			return parsedParameter.EmptyResult;
		}
		return text + parsedParameter.Suffix;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
