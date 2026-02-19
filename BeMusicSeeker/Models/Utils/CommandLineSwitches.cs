using System;
using System.Linq;

namespace BeMusicSeeker.Models.Utils;

public enum NormalLogLevel
{
	Info,
	Warn,
	Error
}

public static class CommandLineSwitches
{
	private static readonly string[] args = Environment.GetCommandLineArgs();

	public static NormalLogLevel LogLevel => ParseLogLevel();

	public static bool IsInfoLoggingEnabled => LogLevel == NormalLogLevel.Info;

	public static bool IsEverythingVerifyEnabled => HasArg("--everything-verify");

	private static bool HasArg(string arg)
	{
		return args.Any((string x) => string.Equals(x, arg, StringComparison.OrdinalIgnoreCase));
	}

	private static NormalLogLevel ParseLogLevel()
	{
		string[] source = args.Where((string x) => x.StartsWith("--log-level=", StringComparison.OrdinalIgnoreCase)).ToArray();
		if (source.Length == 0)
		{
			return NormalLogLevel.Warn;
		}
		string text = source[source.Length - 1].Substring("--log-level=".Length);
		if (string.Equals(text, "info", StringComparison.OrdinalIgnoreCase))
		{
			return NormalLogLevel.Info;
		}
		if (string.Equals(text, "error", StringComparison.OrdinalIgnoreCase))
		{
			return NormalLogLevel.Error;
		}
		return NormalLogLevel.Warn;
	}
}
