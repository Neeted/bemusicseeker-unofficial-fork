using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.Localization;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
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

    private static Mutex _mutex = null;

    private static bool _mutexOwned;

    public bool forceReinitializationCustomFolders { get; set; }

    public bool firstStartup { get; set; }

    public static ReadOnlyDictionary<string, string> AvailableCultures { get; private set; }

    /// <summary>
    /// lang/ フォルダ内のJSONファイルをスキャンし、利用可能な言語リストを構築する
    /// </summary>
    private static void InitializeAvailableCultures()
    {
        AvailableCultures = new ReadOnlyDictionary<string, string>(JsonLanguageCatalog.DiscoverLanguages());
    }

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
        ServicePointManager.DefaultConnectionLimit = 16;
        // 起動ログ比較では既定値より MinThreads=200 の方が startup_ready_* 指標が安定して短かったため維持。
        ThreadPool.SetMinThreads(200, 200);
        LogLevel defaultFileLogLevel = ConvertToNLogLevel(CommandLineSwitches.LogLevel);
        NLogWrapper.AddTarget(new FileTarget
        {
            FileName = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "application.log")
        }, defaultFileLogLevel);
        NLogWrapper.AddTarget(new NetworkTarget
        {
            Address = "http://www.ribbit.xyz/bms/tools/bemusicseeker/report.cgi"
        }, LogLevel.Error, asDefault: false);
        NLogWrapper.SetDefaultConfigurationMinLogLevel(defaultFileLogLevel);
        if (CommandLineSwitches.HasInvalidLogLevelValue)
        {
            NLogWrapper.TraceLogger?.Warn("Invalid --log-level value '" + CommandLineSwitches.InvalidLogLevelValue + "'. Fallback to Warn.");
        }
        ConfigureExtraLogging();
        LegacyUserConfigMigrator.MigrateIfNeeded();
        EquationTokenizer.AddNamespace(typeof(object));
        EquationTokenizer.AddNamespace(typeof(Visibility));
        EquationTokenizer.AddNamespace(typeof(DataGridLength));
        EquationTokenizer.AddNamespace(typeof(DataGridLengthUnitType));
        EquationTokenizer.AddExtensionMethods(typeof(Enumerable));
        EquationTokenizer.AddNamespace(typeof(BMSFile));
        EquationTokenizer.AddNamespace(typeof(BMSTable));
        EquationTokenizer.AddNamespace(typeof(Path));
        EquationTokenizer.AddNamespace(typeof(SystemInformation));
        InitializeAvailableCultures();
        try
        {
            SerializableVersion serializableVersion = new SerializableVersion(Assembly.GetExecutingAssembly().GetName().Version);
            if (Settings.Default.AssemblyVersion == null || Settings.Default.AssemblyVersion != serializableVersion)
            {
                Settings.Default.Upgrade();
                MigrateApplicationSettings();
                Settings.Default.AssemblyVersion = serializableVersion;
                Settings.Default.Save();
            }
        }
        catch (Exception ex)
        {
            NLogWrapper.TraceLogger?.Warn(ex, "Settings upgrade/migration failed");
        }
        if (!AvailableCultures.Values.Contains(Settings.Default.Lang))
        {
            Settings.Default.Lang = "ja-JP";
        }
        Settings.Default.AppearanceTheme = AppThemeService.NormalizeTheme(Settings.Default.AppearanceTheme);
        BeMusicSeeker.Properties.Resources.Culture = CultureInfo.GetCultureInfo(Settings.Default.Lang);
    }

    private static LogLevel ConvertToNLogLevel(NormalLogLevel level)
    {
        switch (level)
        {
            case NormalLogLevel.Info:
                return LogLevel.Info;
            case NormalLogLevel.Error:
                return LogLevel.Error;
            default:
                return LogLevel.Warn;
        }
    }

    private static void ConfigureExtraLogging()
    {
        if (CommandLineSwitches.IsInfoLoggingEnabled)
        {
            string path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "install-performance.log");
            FileTarget target = new FileTarget
            {
                Name = "InstallPerformanceFileTarget",
                FileName = path,
                Layout = NLogWrapper.DefaultLayout
            };
            LogManager.Configuration?.AddTarget(target);
            LogManager.Configuration?.LoggingRules.Insert(0, new LoggingRule("InstallPerformance*", LogLevel.Info, target)
            {
                Final = true
            });
            LogManager.ReconfigExistingLoggers();
            NLogWrapper.TraceLogger?.Info("Install performance logging enabled: " + path);
        }
    }

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        while (_mutex == null)
        {
            try
            {
                _mutex = new Mutex(initiallyOwned: false, MutexName);
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
        _mutexOwned = true;
        DispatcherHelper.UIDispatcher = base.Dispatcher;
        AppThemeService.ApplyTheme(Settings.Default.AppearanceTheme);
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        base.DispatcherUnhandledException += Application_DispatcherUnhandledException;
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        ReleaseSingleInstanceMutex();
    }

    public void RestartApplication()
    {
        string executablePath = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            executablePath = Process.GetCurrentProcess().MainModule?.FileName;
        }
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("Application executable path is not available.");
        }

        string arguments = BuildCommandLineArguments(Environment.GetCommandLineArgs().Skip(1));
        ReleaseSingleInstanceMutex();
        Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
            UseShellExecute = false
        });
        Shutdown();
    }

    private static void ReleaseSingleInstanceMutex()
    {
        if (_mutex == null)
        {
            return;
        }
        try
        {
            if (_mutexOwned)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch (ApplicationException)
        {
        }
        finally
        {
            _mutexOwned = false;
            _mutex.Dispose();
            _mutex = null;
        }
    }

    private static string BuildCommandLineArguments(IEnumerable<string> args)
    {
        return string.Join(" ", args.Select(QuoteCommandLineArgument));
    }

    private static string QuoteCommandLineArgument(string argument)
    {
        if (string.IsNullOrEmpty(argument))
        {
            return "\"\"";
        }
        if (!argument.Any(char.IsWhiteSpace) && !argument.Contains("\""))
        {
            return argument;
        }

        StringBuilder builder = new StringBuilder();
        builder.Append('"');
        int backslashCount = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashCount++;
                continue;
            }
            if (c == '"')
            {
                builder.Append('\\', backslashCount * 2 + 1);
                builder.Append('"');
                backslashCount = 0;
                continue;
            }
            builder.Append('\\', backslashCount);
            backslashCount = 0;
            builder.Append(c);
        }
        builder.Append('\\', backslashCount * 2);
        builder.Append('"');
        return builder.ToString();
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
        string empty = string.Empty;
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
            logger?.Error(ex, empty + " - UNKNOWN" + ((!showMessage) ? " IGNORED" : string.Empty) + Environment.NewLine + ex.ToString());
        }
        catch
        {
        }
    }

    private void MigrateApplicationSettings()
    {
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
        catch (Exception ex)
        {
            NLogWrapper.TraceLogger?.Warn(ex, "LR2RootPath migration failed");
        }
        if (Settings.Default.TableListURL != null && string.Equals(Settings.Default.TableListURL.ToString(), Settings.LegacyTableListUrl, StringComparison.OrdinalIgnoreCase))
        {
            Settings.Default.TableListURL = new Uri(Settings.DefaultTableListUrl);
        }
        if (Settings.Default.AssemblyVersion == null || Settings.Default.AssemblyVersion <= new SerializableVersion(0, 1, 6654, 30787))
        {
            string name = CultureInfo.CurrentCulture.Name;
            Settings.Default.Lang = (AvailableCultures.Values.Contains(name) ? name : "en-US");
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
