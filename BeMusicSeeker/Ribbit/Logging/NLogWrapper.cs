using System;
using System.IO;
using System.Linq;
using System.Text;
using NLog;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;

namespace Ribbit.Logging;

/// <summary>
/// Provides the application-wide NLog configuration surface so callers do not
/// need to create file targets or logging rules directly.
/// </summary>
public static class NLogWrapper
{
    private const string LogDirectoryName = "log";

    private const string ArchiveDirectoryName = "archive";

    private const string ApplicationLogFileName = "application.log";

    private const string InstallPerformanceLogFileName = "install-performance.log";

    private const long DefaultArchiveAboveSizeBytes = 20L * 1024L * 1024L;

    private const int DefaultMaxArchiveFiles = 5;

    private static readonly Encoding LogFileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static Func<Logger> _DebuggerLogger;

    private static Func<Logger> _FileLogger;

    private static Func<Logger> _NetworkLogger;

    private static Func<Logger> _ColoredConsoleLogger;

    private static Func<Logger> _ConsoleLogger;

    private static Func<Logger> _TraceLogger;

    private const string layoutDefaultFormat = "'${longdate}|${level:uppercase=true}|${logger}|${message}'";

    public static readonly Layout DebugLayout;

    public static readonly Layout DefaultLayout;

    public static Logger DebuggerLogger => _DebuggerLogger?.Invoke();

    public static DebuggerTarget DebuggerTarget { get; private set; }

    public static Logger FileLogger => _FileLogger?.Invoke();

    public static FileTarget FileTarget { get; private set; }

    public static Logger NetworkLogger => _NetworkLogger?.Invoke();

    public static NetworkTarget NetworkTarget { get; private set; }

    public static Logger ColoredConsoleLogger => _ColoredConsoleLogger?.Invoke();

    public static ColoredConsoleTarget ColoredConsoleTarget { get; private set; }

    public static Logger ConsoleLogger => _ConsoleLogger?.Invoke();

    public static ConsoleTarget ConsoleTarget { get; private set; }

    public static Logger TraceLogger => _TraceLogger?.Invoke();

    internal static TraceTarget TraceTarget { get; private set; }

    static NLogWrapper()
    {
        DebugLayout = Layout.FromString("${longdate} [${uppercase:${level}}] [${threadname:whenEmpty=*}:${threadid}] ${callsite}() ${message} ${exception:format=tostring}");
        DefaultLayout = Layout.FromString("${longdate} [${uppercase:${level}}] ${message} ${exception:format=tostring}");
        LogManager.Configuration = new LoggingConfiguration();
        new DebuggerTarget().Layout = DebugLayout;
        AddTarget(new TraceTarget
        {
            Layout = DebugLayout
        });
    }

    public static void SetMinLogLevel(this Target target, LogLevel minlevel)
    {
        foreach (LoggingRule item in LogManager.Configuration?.LoggingRules?.Where(r => r.Targets.Contains(target)))
        {
            foreach (LogLevel item2 in LogLevel.AllLoggingLevels.Where(l => l < minlevel))
            {
                item.DisableLoggingForLevel(item2);
            }
            LogManager.ReconfigExistingLoggers();
        }
    }

    public static void SetDefaultConfigurationMinLogLevel(LogLevel minlevel)
    {
        foreach (LoggingRule item in LogManager.Configuration?.LoggingRules?.Where(r => r.LoggerNamePattern == "*"))
        {
            foreach (LogLevel item2 in LogLevel.AllLoggingLevels.Where(l => l < minlevel))
            {
                item.DisableLoggingForLevel(item2);
            }
            LogManager.ReconfigExistingLoggers();
        }
    }

    /// <summary>
    /// Configures the app-owned rolling file logs under the executable
    /// directory. BeMusicSeeker keeps normal diagnostic logs and expensive
    /// install/performance traces separate so daily troubleshooting can start
    /// from the smaller file.
    /// </summary>
    /// <param name="applicationBaseDirectory">Directory that contains the running executable.</param>
    /// <param name="minimumFileLogLevel">Minimum level written to the normal application log.</param>
    /// <param name="enableInstallPerformanceLogging">Whether to write the separated install/performance log.</param>
    public static void ConfigureApplicationFileLogging(string applicationBaseDirectory, LogLevel minimumFileLogLevel, bool enableInstallPerformanceLogging)
    {
        if (string.IsNullOrWhiteSpace(applicationBaseDirectory))
        {
            throw new ArgumentException("Application base directory is required.", nameof(applicationBaseDirectory));
        }
        LogLevel effectiveMinimumFileLogLevel = minimumFileLogLevel ?? LogLevel.Info;
        string logDirectoryPath = Path.Combine(applicationBaseDirectory, LogDirectoryName);
        FileTarget applicationFileTarget = CreateRollingFileTarget(
            "ApplicationFileTarget",
            Path.Combine(logDirectoryPath, ApplicationLogFileName),
            Path.Combine(logDirectoryPath, ArchiveDirectoryName, ApplicationLogFileName));
        AddTarget(applicationFileTarget, effectiveMinimumFileLogLevel);
        SetDefaultConfigurationMinLogLevel(effectiveMinimumFileLogLevel);
        if (enableInstallPerformanceLogging)
        {
            AddInstallPerformanceFileTarget(logDirectoryPath);
        }
    }

    private static FileTarget CreateRollingFileTarget(string targetName, string filePath, string archiveFilePath)
    {
        return new FileTarget
        {
            Name = targetName,
            FileName = filePath,
            ArchiveFileName = archiveFilePath,
            ArchiveSuffixFormat = ".{0}",
            ArchiveAboveSize = DefaultArchiveAboveSizeBytes,
            MaxArchiveFiles = DefaultMaxArchiveFiles,
            CreateDirs = true,
            Encoding = LogFileEncoding,
            Layout = DefaultLayout
        };
    }

    private static void AddInstallPerformanceFileTarget(string logDirectoryPath)
    {
        FileTarget target = CreateRollingFileTarget(
            "InstallPerformanceFileTarget",
            Path.Combine(logDirectoryPath, InstallPerformanceLogFileName),
            Path.Combine(logDirectoryPath, ArchiveDirectoryName, InstallPerformanceLogFileName));
        LogManager.Configuration?.AddTarget(target);
        AddRule(target, LogLevel.Info, "InstallPerformance*", final: true);
        LogManager.ReconfigExistingLoggers();
        TraceLogger?.Info("Install performance logging enabled: " + target.FileName);
    }

    private static void AddRule(Target target, LogLevel minLevel = null, string patternName = "*", bool final = false)
    {
        if (target == null)
        {
            throw new ArgumentNullException("target");
        }
        if (patternName == null)
        {
            throw new ArgumentNullException("patternName");
        }
        if (minLevel == null)
        {
            minLevel = LogLevel.Trace;
        }
        if (patternName == "*")
        {
            LogManager.Configuration?.LoggingRules?.Add(new LoggingRule(patternName, minLevel, target));
            return;
        }
        LogManager.Configuration?.LoggingRules?.Insert(0, new LoggingRule(patternName, minLevel, target)
        {
            Final = final
        });
        if (target is DebuggerTarget)
        {
            if (DebuggerLogger == null)
            {
                _DebuggerLogger = () => LogManager.GetLogger(patternName);
                DebuggerTarget = (DebuggerTarget)target;
            }
        }
        else if (target is FileTarget)
        {
            if (FileLogger == null || string.Equals(target.Name, "ApplicationFileTarget", StringComparison.Ordinal))
            {
                _FileLogger = () => LogManager.GetLogger(patternName);
                FileTarget = (FileTarget)target;
            }
        }
        else if (target is NetworkTarget)
        {
            if (NetworkLogger == null)
            {
                _NetworkLogger = () => LogManager.GetLogger(patternName);
                NetworkTarget = (NetworkTarget)target;
            }
        }
        else if (target is ColoredConsoleTarget)
        {
            if (ColoredConsoleLogger == null)
            {
                _ColoredConsoleLogger = () => LogManager.GetLogger(patternName);
                ColoredConsoleTarget = (ColoredConsoleTarget)target;
            }
        }
        else if (target is ConsoleTarget)
        {
            if (ConsoleLogger == null)
            {
                _ConsoleLogger = () => LogManager.GetLogger(patternName);
                ConsoleTarget = (ConsoleTarget)target;
            }
        }
        else if (target is TraceTarget)
        {
            if (TraceLogger == null)
            {
                _TraceLogger = () => LogManager.GetLogger(patternName);
                TraceTarget = (TraceTarget)target;
            }
        }
    }

    public static Logger GetLogger()
    {
        return LogManager.GetCurrentClassLogger();
    }

    /// <summary>
    /// Gets a named logger while keeping logger creation behind the shared
    /// wrapper. Named loggers are used for channels such as InstallPerformance
    /// where routing depends on the logger name.
    /// </summary>
    /// <param name="loggerName">Name used by NLog logging rules.</param>
    /// <returns>The named logger.</returns>
    public static Logger GetLogger(string loggerName)
    {
        return LogManager.GetLogger(loggerName);
    }

    /// <summary>
    /// Gets a logger for a specific owner type. Use this instead of current
    /// class lookup when the call is routed through this wrapper, because stack
    /// based lookup would otherwise identify the wrapper itself.
    /// </summary>
    /// <param name="loggerOwnerType">Type whose full name should become the logger name.</param>
    /// <returns>The logger for the specified owner type.</returns>
    public static Logger GetLogger(Type loggerOwnerType)
    {
        if (loggerOwnerType == null)
        {
            throw new ArgumentNullException(nameof(loggerOwnerType));
        }
        return LogManager.GetLogger(loggerOwnerType.FullName);
    }

    public static void AddTarget(TargetWithLayout target, LogLevel minLevel = null, bool asDefault = true)
    {
        if (target == null)
        {
            throw new ArgumentNullException("target");
        }
        if (target.Layout.ToString() == "'${longdate}|${level:uppercase=true}|${logger}|${message}'")
        {
            target.Layout = DefaultLayout;
        }
        target.Name ??= target.GetType().ToString();
        if (target is not NLog.Targets.DebuggerTarget)
        {
            if (asDefault)
            {
                AddRule(target, minLevel);
            }
            AddRule(target, minLevel, target.GetType().ToString(), asDefault);
        }
        LogManager.ReconfigExistingLoggers();
    }
}
