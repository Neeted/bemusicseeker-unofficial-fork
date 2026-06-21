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

    private static readonly (NormalLogLevel Level, string InvalidValue) parsedLogLevelResult = ParseLogLevel();

    private static readonly string parsedUpdateManifestUrl = ParseStringSwitch("--update-manifest-url=");

    public static NormalLogLevel LogLevel => parsedLogLevelResult.Level;

    public static bool IsInfoLoggingEnabled => parsedLogLevelResult.Level == NormalLogLevel.Info;

    public static bool HasInvalidLogLevelValue => !string.IsNullOrWhiteSpace(parsedLogLevelResult.InvalidValue);

    public static string InvalidLogLevelValue => parsedLogLevelResult.InvalidValue ?? string.Empty;

    public static string UpdateManifestUrl => parsedUpdateManifestUrl ?? string.Empty;

    private static (NormalLogLevel Level, string InvalidValue) ParseLogLevel()
    {
        string[] source = [.. args.Where(x => x.StartsWith("--log-level=", StringComparison.OrdinalIgnoreCase))];
        if (source.Length == 0)
        {
            return (NormalLogLevel.Warn, null);
        }
        string text = source[source.Length - 1].Substring("--log-level=".Length);
        if (string.Equals(text, "info", StringComparison.OrdinalIgnoreCase))
        {
            return (NormalLogLevel.Info, null);
        }
        if (string.Equals(text, "error", StringComparison.OrdinalIgnoreCase))
        {
            return (NormalLogLevel.Error, null);
        }
        if (string.Equals(text, "warn", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "warning", StringComparison.OrdinalIgnoreCase))
        {
            return (NormalLogLevel.Warn, null);
        }
        return (NormalLogLevel.Warn, text);
    }

    private static string ParseStringSwitch(string prefix)
    {
        string[] source = [.. args.Where(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))];
        return source.Length == 0 ? null : source[source.Length - 1].Substring(prefix.Length);
    }
}
