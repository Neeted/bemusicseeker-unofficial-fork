using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Deployment.Application;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Livet;
using NLog;
using NLog.Config;
using NLog.Targets;
using QuickConverter;
using Ribbit.Logging;

namespace BeMusicSeeker;

public partial class App : System.Windows.Application
{
	private const string MutexName = "a601b8c6-41c3-4182-950b-b96a0f0c8b0c";

	private const string InstallPerformanceLogArg = "--perf-log";

	private const string EverythingVerifyLogArg = "--everything-verify";

	private const string EverythingDebugLogArg = "--everything-log";

	private static Mutex _mutex = null;

	public bool forceReinitializationCustomFolders { get; set; }

	public bool showVersionUpMessage { get; set; }

	public bool firstStartup { get; set; }

	public static ReadOnlyDictionary<string, string> AvailableCultures { get; } = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>
	{
		{ "Japanese", "ja-JP" },
		{ "English", "en-US" },
		{ "French", "fr-FR" }
	});

	public App()
	{
		try
		{
			Application_Initialization();
		}
		catch (Exception ex)
		{
			ExceptionLogger(ex);
		}
	}

	private void Application_Initialization()
	{
		ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
		ThreadPool.SetMinThreads(200, 200);
		NLogWrapper.AddTarget(new FileTarget
		{
			FileName = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "application.log")
		}, LogLevel.Error);
		NLogWrapper.AddTarget(new NetworkTarget
		{
			Address = "http://www.ribbit.xyz/bms/tools/bemusicseeker/report.cgi"
		}, LogLevel.Error, asDefault: false);
		NLogWrapper.SetDefaultConfigurationMinLogLevel(LogLevel.Info);
		ConfigureInstallPerformanceLoggingIfEnabled();
		LegacyUserConfigMigrator.MigrateIfNeeded();
		try
		{
			new AutoUpdater().Execute();
		}
		catch
		{
		}
		EquationTokenizer.AddNamespace(typeof(object));
		EquationTokenizer.AddNamespace(typeof(Visibility));
		EquationTokenizer.AddNamespace(typeof(DataGridLength));
		EquationTokenizer.AddNamespace(typeof(DataGridLengthUnitType));
		EquationTokenizer.AddExtensionMethods(typeof(Enumerable));
		EquationTokenizer.AddNamespace(typeof(BMSFile));
		EquationTokenizer.AddNamespace(typeof(VirtualBMSFile));
		EquationTokenizer.AddNamespace(typeof(BMSTable));
		EquationTokenizer.AddNamespace(typeof(Path));
		EquationTokenizer.AddNamespace(typeof(SystemInformation));
		try
		{
			SerializableVersion serializableVersion = new SerializableVersion(Assembly.GetExecutingAssembly().GetName().Version);
			if (Settings.Default.AssemblyVersion == null || Settings.Default.AssemblyVersion != serializableVersion)
			{
				Settings.Default.Upgrade();
				MigrateApplicationSettings();
				Settings.Default.AssemblyVersion = serializableVersion;
				try
				{
					Settings.Default.PublishVersion = new SerializableVersion(ApplicationDeployment.CurrentDeployment.CurrentVersion);
				}
				catch
				{
					Settings.Default.PublishVersion = null;
				}
				Settings.Default.Save();
			}
		}
		catch (Exception)
		{
		}
		if (!AvailableCultures.Values.Contains(Settings.Default.Lang))
		{
			Settings.Default.Lang = "ja-JP";
		}
		BeMusicSeeker.Properties.Resources.Culture = CultureInfo.GetCultureInfo(Settings.Default.Lang);
	}

	private static bool IsInstallPerformanceLoggingEnabled()
	{
		return Environment.GetCommandLineArgs().Any((string arg) => string.Equals(arg, InstallPerformanceLogArg, StringComparison.OrdinalIgnoreCase) || string.Equals(arg, EverythingVerifyLogArg, StringComparison.OrdinalIgnoreCase) || string.Equals(arg, EverythingDebugLogArg, StringComparison.OrdinalIgnoreCase));
	}

	private static void ConfigureInstallPerformanceLoggingIfEnabled()
	{
		if (!IsInstallPerformanceLoggingEnabled())
		{
			return;
		}
		string path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "install-performance.log");
		FileTarget target = new FileTarget
		{
			Name = "InstallPerformanceFileTarget",
			FileName = path,
			Layout = NLogWrapper.DefaultLayout
		};
		LogManager.Configuration?.AddTarget(target);
		LogManager.Configuration?.LoggingRules.Insert(0, new LoggingRule("InstallPerformance*", LogLevel.Info, target));
		LogManager.ReconfigExistingLoggers();
		NLogWrapper.TraceLogger?.Info("Install performance logging enabled: " + path);
	}

	private void Application_Startup(object sender, StartupEventArgs e)
	{
		while (_mutex == null)
		{
			try
			{
				_mutex = new Mutex(initiallyOwned: false, "a601b8c6-41c3-4182-950b-b96a0f0c8b0c");
			}
			catch
			{
			}
		}
		if (!_mutex.WaitOne(5000, exitContext: false))
		{
			_mutex.Close();
			_mutex = null;
			System.Windows.MessageBox.Show((CultureInfo.CurrentCulture.Name == "ja-JP") ? "既に起動しています。" : "BeMusicSeeker is already started.", "Error", MessageBoxButton.OK, MessageBoxImage.Hand);
			Shutdown();
		}
		DispatcherHelper.UIDispatcher = base.Dispatcher;
		AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
		base.DispatcherUnhandledException += Application_DispatcherUnhandledException;
	}

	private void Application_Exit(object sender, ExitEventArgs e)
	{
		if (_mutex != null)
		{
			_mutex.ReleaseMutex();
			_mutex.Dispose();
		}
	}

	private void Application_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		Exception exception = e.Exception;
		bool showMessage = true;
		e.Handled = true;
		if ((exception is COMException && ((COMException)exception).ErrorCode == -2147221040) || (exception is COMException && ((COMException)exception).ErrorCode == -2147467259))
		{
			return;
		}
		if (exception is NullReferenceException)
		{
			showMessage = false;
			return;
		}
		if (exception is InvalidOperationException)
		{
			showMessage = false;
			return;
		}
		try
		{
			ExceptionLogger(exception, showMessage);
		}
		finally
		{
			if (!Debugger.IsAttached)
			{
				Thread.Sleep(1000);
				System.Windows.MessageBox.Show("アプリケーションを終了します。", "確認", MessageBoxButton.OK, MessageBoxImage.Asterisk, MessageBoxResult.Yes);
				try
				{
					System.Windows.Application.Current.MainWindow.Close();
				}
				finally
				{
					Environment.Exit(1);
				}
			}
		}
	}

	private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
	{
		if (Debugger.IsAttached)
		{
			return;
		}
		try
		{
			Exception ex = (Exception)e.ExceptionObject;
			ExceptionLogger(ex);
		}
		finally
		{
			Thread.Sleep(1000);
			System.Windows.MessageBox.Show("アプリケーションを終了します", "確認", MessageBoxButton.OK, MessageBoxImage.Asterisk, MessageBoxResult.Yes);
			try
			{
				System.Windows.Application.Current.MainWindow.Close();
			}
			finally
			{
				Environment.Exit(1);
			}
		}
	}

	private void ExceptionLogger(Exception ex, bool showMessage = true)
	{
		Logger fileLogger = NLogWrapper.FileLogger;
		Logger networkLogger = NLogWrapper.NetworkLogger;
		Logger logger = fileLogger;
		string text = string.Empty;
		string empty = string.Empty;
		try
		{
			text = ApplicationDeployment.CurrentDeployment.CurrentVersion.ToString() + "/";
		}
		catch
		{
		}
		empty = Assembly.GetEntryAssembly().GetName().Version.ToString();
		if (showMessage)
		{
			System.Windows.MessageBox.Show(".NET Frameworkでエラーが発生しました" + Environment.NewLine + Environment.NewLine + "原因の追跡が困難なため、どの操作で発生したか" + Environment.NewLine + "開発者に報告頂けると助かります" + Environment.NewLine + Environment.NewLine + "エラー概要:" + Environment.NewLine + ex.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
			logger = networkLogger;
		}
		else
		{
			logger = networkLogger;
		}
		try
		{
			logger?.Error(ex, text + empty + " - UNKNOWN" + ((!showMessage) ? " IGNORED" : string.Empty) + Environment.NewLine + ex.ToString());
		}
		catch
		{
		}
	}

	private void MigrateApplicationSettings()
	{
		if (Settings.Default.AssemblyVersion == null || Settings.Default.AssemblyVersion <= new SerializableVersion(0, 1, 5816, 2047))
		{
			Settings.Default.StandardColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.STANDARD);
			Settings.Default.PlaylistColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.PLAYLIST);
			Settings.Default.FullScanColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.FULLSCAN);
			Settings.Default.DuplicateColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.DUPLICATE);
			Settings.Default.EncodingColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.ENCODING);
			Settings.Default.InstallColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.INSTALL);
		}
		if (Settings.Default.AssemblyVersion == null || Settings.Default.AssemblyVersion <= new SerializableVersion(0, 1, 5803, 41786))
		{
			forceReinitializationCustomFolders = true;
		}
		else
		{
			forceReinitializationCustomFolders = false;
		}
		try
		{
			if (Settings.Default.AssemblyVersion != null && Settings.Default.OperationModeLR2DB && string.IsNullOrWhiteSpace(Settings.Default.LR2RootPath) && !string.IsNullOrWhiteSpace(Settings.Default.LR2ConfigXmlPath) && File.Exists(Settings.Default.LR2ConfigXmlPath) && !string.IsNullOrWhiteSpace(Settings.Default.LR2SongDBPath) && File.Exists(Settings.Default.LR2SongDBPath))
			{
				string directoryName = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Settings.Default.LR2ConfigXmlPath)));
				string directoryName2 = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Settings.Default.LR2SongDBPath)));
				if (!string.IsNullOrWhiteSpace(directoryName) && directoryName.Equals(directoryName2, StringComparison.OrdinalIgnoreCase) && Directory.Exists(directoryName))
				{
					string path = Path.Combine(directoryName, "LR2body.exe");
					string path2 = Path.Combine(directoryName, "LRHbody.exe");
					if (File.Exists(path) || File.Exists(path2))
					{
						Settings.Default.LR2RootPath = directoryName;
					}
				}
			}
		}
		catch
		{
		}
		if (Settings.Default.AssemblyVersion == null || Settings.Default.AssemblyVersion <= new SerializableVersion(0, 1, 6654, 30787))
		{
			string name = CultureInfo.CurrentCulture.Name;
			Settings.Default.Lang = (AvailableCultures.Keys.Contains(name) ? name : "en-US");
		}
		showVersionUpMessage = false;
		try
		{
			if (Settings.Default.PublishVersion != null)
			{
				SerializableVersion publishVersion = Settings.Default.PublishVersion;
				Version currentVersion = ApplicationDeployment.CurrentDeployment.CurrentVersion;
				if (publishVersion.Major < currentVersion.Major || publishVersion.Minor < currentVersion.Minor || publishVersion.Build < currentVersion.Build)
				{
					showVersionUpMessage = true;
				}
			}
		}
		catch
		{
		}
		if (Settings.Default.AssemblyVersion == null)
		{
			firstStartup = true;
		}
		else
		{
			firstStartup = false;
		}
	}
}
