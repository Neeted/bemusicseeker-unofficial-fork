using System;
using System.Linq;
using NLog;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;

namespace Ribbit.Logging;

public static class NLogWrapper
{
	private static Func<Logger> _DebuggerLogger;

	private static Func<Logger> _FileLogger;

	private static Func<Logger> _NetworkLogger;

	private static Func<Logger> _ColoredConsoleLogger;

	private static Func<Logger> _ConsoleLogger;

	private static Func<Logger> _DatabaseLogger;

	private static Func<Logger> _EventLogLogger;

	private static Func<Logger> _MailLogger;

	private static Func<Logger> _TraceLogger;

	private static Func<Logger> _WebServiceLogger;

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

	public static Logger DatabaseLogger => _DatabaseLogger?.Invoke();

	public static DatabaseTarget DatabaseTarget { get; private set; }

	public static Logger EventLogLogger => _EventLogLogger?.Invoke();

	public static EventLogTarget EventLogTarget { get; private set; }

	public static Logger MailLogger => _MailLogger?.Invoke();

	public static MailTarget MailTarget { get; private set; }

	public static Logger TraceLogger => _TraceLogger?.Invoke();

	public static TraceTarget TraceTarget { get; private set; }

	public static Logger WebServiceLogger => _WebServiceLogger?.Invoke();

	public static WebServiceTarget WebServiceTarget { get; private set; }

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
		foreach (LoggingRule item in LogManager.Configuration?.LoggingRules?.Where((LoggingRule r) => r.Targets.Contains(target)))
		{
			foreach (LogLevel item2 in LogLevel.AllLoggingLevels.Where((LogLevel l) => l < minlevel))
			{
				item.DisableLoggingForLevel(item2);
			}
			LogManager.ReconfigExistingLoggers();
		}
	}

	public static void SetDefaultConfigurationMinLogLevel(LogLevel minlevel)
	{
		foreach (LoggingRule item in LogManager.Configuration?.LoggingRules?.Where((LoggingRule r) => r.LoggerNamePattern == "*"))
		{
			foreach (LogLevel item2 in LogLevel.AllLoggingLevels.Where((LogLevel l) => l < minlevel))
			{
				item.DisableLoggingForLevel(item2);
			}
			LogManager.ReconfigExistingLoggers();
		}
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
			if (FileLogger == null)
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
		else if (target is DatabaseTarget)
		{
			if (DatabaseLogger == null)
			{
				_DatabaseLogger = () => LogManager.GetLogger(patternName);
				DatabaseTarget = (DatabaseTarget)target;
			}
		}
		else if (target is EventLogTarget)
		{
			if (EventLogLogger == null)
			{
				_EventLogLogger = () => LogManager.GetLogger(patternName);
				EventLogTarget = (EventLogTarget)target;
			}
		}
		else if (target is MailTarget)
		{
			if (MailLogger == null)
			{
				_MailLogger = () => LogManager.GetLogger(patternName);
				MailTarget = (MailTarget)target;
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
		else if (target is WebServiceTarget && WebServiceLogger == null)
		{
			_WebServiceLogger = () => LogManager.GetLogger(patternName);
			WebServiceTarget = (WebServiceTarget)target;
		}
	}

	public static Logger GetLogger()
	{
		return LogManager.GetCurrentClassLogger();
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
		if (target.Name == null)
		{
			target.Name = target.GetType().ToString();
		}
		if (!(target is DebuggerTarget))
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
