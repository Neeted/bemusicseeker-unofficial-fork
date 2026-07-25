using System;
using System.Collections.Generic;
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
using BeMusicSeeker.Models.Localization;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Livet;
using NLog;
using NLog.Targets;
using QuickConverter;
using Ribbit.Logging;

namespace BeMusicSeeker;

public partial class App : System.Windows.Application
{
    private const string MutexName = "a601b8c6-41c3-4182-950b-b96a0f0c8b0c";

    private static Mutex _mutex = null;

    private static bool _mutexOwned;

    private static int coordinatedShutdownStarted;

    private static string coordinatedShutdownReason;

    private ApplicationPathSnapshot applicationPathSnapshot;

    private readonly ApplicationSettingsLifecycle applicationSettingsLifecycle = new();

    public bool firstStartup { get; set; }

    public static ReadOnlyDictionary<string, string> AvailableCultures { get; private set; }

    internal static bool IsCoordinatedShutdownStarted => Volatile.Read(ref coordinatedShutdownStarted) != 0;

    internal static void MarkCoordinatedShutdownStarted(string reason)
    {
        coordinatedShutdownReason = reason ?? "shutdown";
        Volatile.Write(ref coordinatedShutdownStarted, 1);
    }

    /// <summary>
    /// lang/ フォルダ内のJSONファイルをスキャンし、利用可能な言語リストを構築する
    /// </summary>
    private static void InitializeAvailableCultures()
    {
        AvailableCultures = JsonLanguageCatalog.GetLanguagesSnapshot();
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
        applicationPathSnapshot = ApplicationPathPolicy.Current;
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
        ServicePointManager.DefaultConnectionLimit = 16;
        // 起動ログ比較では既定値より MinThreads=200 の方が startup_ready_* 指標が安定して短かったため維持。
        ThreadPool.SetMinThreads(200, 200);
        LogLevel defaultFileLogLevel = ConvertToNLogLevel(CommandLineSwitches.LogLevel);
        NLogWrapper.ConfigureApplicationFileLogging(applicationPathSnapshot.BaseDirectory, defaultFileLogLevel, CommandLineSwitches.IsInfoLoggingEnabled);
        NLogWrapper.AddTarget(new NetworkTarget
        {
            Address = "http://www.ribbit.xyz/bms/tools/bemusicseeker/report.cgi"
        }, LogLevel.Error, asDefault: false);
        if (CommandLineSwitches.HasInvalidLogLevelValue)
        {
            NLogWrapper.FileLogger?.Warn("Invalid --log-level value '" + CommandLineSwitches.InvalidLogLevelValue + "'. Fallback to Warn.");
        }
        InitializeAvailableCultures();
        applicationSettingsLifecycle.MigrateLegacy(AvailableCultures.Values, CultureInfo.CurrentCulture.Name);
        EquationTokenizer.AddNamespace(typeof(object));
        EquationTokenizer.AddNamespace(typeof(Visibility));
        EquationTokenizer.AddNamespace(typeof(DataGridLength));
        EquationTokenizer.AddNamespace(typeof(DataGridLengthUnitType));
        EquationTokenizer.AddExtensionMethods(typeof(Enumerable));
        EquationTokenizer.AddNamespace(typeof(BMSTable));
        EquationTokenizer.AddNamespace(typeof(Path));
        EquationTokenizer.AddNamespace(typeof(SystemInformation));
        applicationSettingsLifecycle.Initialize(
            AvailableCultures.Values,
            () => new SerializableVersion(Assembly.GetExecutingAssembly().GetName().Version),
            value => firstStartup = value);
    }

    private static LogLevel ConvertToNLogLevel(NormalLogLevel level)
    {
        return level switch
        {
            NormalLogLevel.Info => LogLevel.Info,
            NormalLogLevel.Error => LogLevel.Error,
            _ => LogLevel.Warn,
        };
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
            EmergencyDialog.Show((CultureInfo.CurrentCulture.Name == "ja-JP") ? "既に起動しています。" : "BeMusicSeeker is already started.", "Error", MessageBoxButton.OK, MessageBoxImage.Hand);
            Shutdown();
            return;
        }
        _mutexOwned = true;
        DispatcherHelper.UIDispatcher = base.Dispatcher;
        AppThemeService.ApplyTheme(applicationSettingsLifecycle.GetCurrentAppearanceTheme());
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        base.DispatcherUnhandledException += Application_DispatcherUnhandledException;
        TempDirectoryPublisher.StartCleanupStaleDirectoriesAsync(
            message => NLogWrapper.FileLogger?.Info(message),
            (path, ex) => NLogWrapper.FileLogger?.Warn(ex, "temp_startup_cleanup_failed path=" + path));
        ManagedDependencyPreloader.Preload();

        CreateAndShowMainWindow();
    }

    private void CreateAndShowMainWindow()
    {
        try
        {
            ApplicationComposition composition = new ApplicationComposition(
                uiScheduler: new WpfUiScheduler(() => base.Dispatcher),
                applicationLifetime: new AppApplicationLifetime(this),
                cultureCatalog: new AppCultureCatalog(this),
                applicationPathSnapshot: applicationPathSnapshot);
            MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();
            Resources["vm"] = viewModel;
            MainWindow mainWindow = new(viewModel);
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception exception)
        {
            HandleStartupCompositionFailure(exception);
        }
    }

    private void HandleStartupCompositionFailure(Exception exception)
    {
        MarkCoordinatedShutdownStarted("startup_composition_failed");
        try
        {
            ExceptionLogger(exception);
        }
        finally
        {
            ReleaseSingleInstanceMutex();
            Shutdown(1);
        }
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

        var builder = new StringBuilder();
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
        if (TrySuppressShutdownSqliteCloseException(exception, "dispatcher"))
        {
            return;
        }
        if ((exception is COMException && ((COMException)exception).ErrorCode == -2147221040) || (exception is COMException && ((COMException)exception).ErrorCode == -2147467259))
        {
            return;
        }
        if (exception is NullReferenceException)
        {
            return;
        }
        if (exception is InvalidOperationException)
        {
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
                EmergencyDialog.Show("アプリケーションを終了します。", "確認", MessageBoxButton.OK, MessageBoxImage.Asterisk, MessageBoxResult.Yes);
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
        bool suppressed = false;
        try
        {
            var ex = (Exception)e.ExceptionObject;
            suppressed = TrySuppressShutdownSqliteCloseException(ex, "current_domain");
            if (!suppressed)
            {
                ExceptionLogger(ex);
            }
        }
        finally
        {
            if (!suppressed)
            {
                Thread.Sleep(1000);
                EmergencyDialog.Show("アプリケーションを終了します", "確認", MessageBoxButton.OK, MessageBoxImage.Asterisk, MessageBoxResult.Yes);
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

    private void ExceptionLogger(Exception ex, bool showMessage = true)
    {
        Logger fileLogger = NLogWrapper.FileLogger;
        Logger networkLogger = NLogWrapper.NetworkLogger;
        string empty = Assembly.GetEntryAssembly().GetName().Version.ToString();
        Logger logger;
        if (showMessage)
        {
            EmergencyDialog.Show(".NET Frameworkでエラーが発生しました" + Environment.NewLine + Environment.NewLine + "原因の追跡が困難なため、どの操作で発生したか" + Environment.NewLine + "開発者に報告頂けると助かります" + Environment.NewLine + Environment.NewLine + "エラー概要:" + Environment.NewLine + ex.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
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

    private static bool TrySuppressShutdownSqliteCloseException(Exception exception, string source)
    {
        if (!IsCoordinatedShutdownStarted || !IsSqliteCloseException(exception))
        {
            return false;
        }
        try
        {
            NLogWrapper.FileLogger?.Warn(
                exception,
                "Suppressed SQLite close exception during coordinated shutdown. source="
                    + (source ?? "unknown")
                    + " reason="
                    + (coordinatedShutdownReason ?? "shutdown"));
        }
        catch
        {
        }
        return true;
    }

    private static bool IsSqliteCloseException(Exception exception)
    {
        while (exception != null)
        {
            if ((exception.Message ?? string.Empty).IndexOf(
                "unable to close due to unfinalized statements or unfinished backups",
                StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            exception = exception.InnerException;
        }
        return false;
    }

}
