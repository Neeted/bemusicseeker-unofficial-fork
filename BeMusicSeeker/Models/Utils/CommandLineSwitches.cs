using System;
using System.Linq;

namespace BeMusicSeeker.Models.Utils;

public static class CommandLineSwitches
{
	private static readonly string[] args = Environment.GetCommandLineArgs();

	public static bool IsInstallPerformanceLogEnabled => HasArg("--perf-log");

	public static bool IsEverythingVerifyEnabled => HasArg("--everything-verify");

	public static bool IsEverythingLogEnabled => HasArg("--everything-log");

	private static bool HasArg(string arg)
	{
		return args.Any((string x) => string.Equals(x, arg, StringComparison.OrdinalIgnoreCase));
	}
}
