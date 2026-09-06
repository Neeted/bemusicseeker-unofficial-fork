using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Localization;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Livet;
using NLog;
using NLog.Targets;
using Ribbit.Logging;

namespace BeMusicSeeker;

public partial class App : System.Windows.Application
{
    private static readonly TimeSpan PerformanceDiagnosticsDrainLimit = TimeSpan.FromSeconds(5);

    private const string MutexName = "a601b8c6-41c3-4182-950b-b96a0f0c8b0c";

    private static Mutex _mutex = null;

    private static bool _mutexOwned;

    private static int coordinatedShutdownStarted;

    private static string coordinatedShutdownReason;

    private ApplicationPathSnapshot applicationPathSnapshot;

    private readonly IApplicationRestartGateway applicationRestartGateway;

    private readonly ApplicationSettingsLifecycle applicationSettingsLifecycle;

    private readonly object restartSyncRoot = new();

    private Task restartTask;

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
        : this(ApplicationRestartGatewayPolicy.Current)
    {
    }

    internal App(IApplicationRestartGateway applicationRestartGateway)
    {
        applicationSettingsLifecycle = new ApplicationSettingsLifecycle(
            normalizeSettings: PreparePortableSettings, warnSaveFailure: ShowSettingsSaveWarning);
        this.applicationRestartGateway = applicationRestartGateway
            ?? throw new ArgumentNullException(nameof(applicationRestartGateway));
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
        LogLevel defaultFileLogLevel = ConvertToNLogLevel(CommandLineSwitches.LogLevel);
        NLogWrapper.ConfigureApplicationFileLogging(applicationPathSnapshot.BaseDirectory, defaultFileLogLevel, CommandLineSwitches.IsInfoLoggingEnabled);
        Net10PerformanceLog.Start();
        NLogWrapper.AddTarget(new NetworkTarget
        {
            Address = "http://www.ribbit.xyz/bms/tools/bemusicseeker/report.cgi"
        }, LogLevel.Error, asDefault: false);
        if (CommandLineSwitches.HasInvalidLogLevelValue)
        {
            NLogWrapper.FileLogger?.Warn("Invalid --log-level value '" + CommandLineSwitches.InvalidLogLevelValue + "'. Fallback to Warn.");
        }
        InitializeAvailableCultures();
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

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        await new ApplicationStartupCompositionOwner(
            createViewModel: () =>
            {
                ApplicationComposition composition = new ApplicationComposition(
                    uiScheduler: new WpfUiScheduler(() => base.Dispatcher),
                    applicationLifetime: new AppApplicationLifetime(this),
                    cultureCatalog: new AppCultureCatalog(this),
                    applicationPathSnapshot: applicationPathSnapshot,
                    reportTerminalSettingsSaveFailure: exception => EmergencyDialog.Show(
                        string.Format(BeMusicSeeker.Properties.Resources.SettingsSaveFailedDuringShutdown, SettingsFailureMessage.Format(exception)),
                        BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Warning));
                return composition.CreateMainWindowViewModel();
            },
            assignViewModelResource: viewModel => Resources["vm"] = viewModel,
            createMainWindow: viewModel => new MainWindow(viewModel),
            assignApplicationMainWindow: mainWindow => MainWindow = mainWindow,
            showMainWindow: mainWindow => mainWindow.Show(),
            handleFailure: HandleStartupFailureAsync,
            acquireOwnership: AcquireSingleInstanceOwnership,
            prepareSettings: InitializeOwnedSettings)
            .StartAsync()
            .ConfigureAwait(true);
    }

    private bool AcquireSingleInstanceOwnership()
    {
        _mutex = new Mutex(initiallyOwned: false, MutexName);
        try
        {
            _mutexOwned = _mutex.WaitOne(5000, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            _mutexOwned = true;
        }
        if (_mutexOwned)
        {
            return true;
        }
        ReleaseSingleInstanceMutex();
        EmergencyDialog.Show(BeMusicSeeker.Properties.Resources.ApplicationAlreadyStarted,
            BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown();
        return false;
    }

    private void PreparePortableSettings()
    {
        new PortableSettingsStartupFile(PortableSettingsPath.UserConfigPath).Prepare(
            () => applicationSettingsLifecycle.MigrateLegacy(AvailableCultures.Values, CultureInfo.CurrentCulture.Name),
            backup => EmergencyDialog.Show(
                string.Format(BeMusicSeeker.Properties.Resources.PortableSettingsRecovered, backup),
                BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    private static void ShowSettingsSaveWarning(Exception exception)
    {
        NLogWrapper.FileLogger?.Warn(exception, "startup_settings_save_failed");
        string path = exception is PortableSettingsException failure ? failure.FilePath : PortableSettingsPath.UserConfigPath;
        EmergencyDialog.Show(string.Format(BeMusicSeeker.Properties.Resources.PortableSettingsStartupSaveFailed,
            path, exception.GetBaseException().Message), BeMusicSeeker.Properties.Resources.Warning,
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void InitializeOwnedSettings()
    {
        applicationSettingsLifecycle.Initialize(
            AvailableCultures.Values,
            () => new SerializableVersion(Assembly.GetExecutingAssembly().GetName().Version),
            value => firstStartup = value);
        DispatcherHelper.UIDispatcher = base.Dispatcher;
        AppThemeService.ApplyTheme(applicationSettingsLifecycle.GetCurrentAppearanceTheme());
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        base.DispatcherUnhandledException += Application_DispatcherUnhandledException;
        TempDirectoryPublisher.StartCleanupStaleDirectoriesAsync(
            message => NLogWrapper.FileLogger?.Info(message),
            (path, ex) => NLogWrapper.FileLogger?.Warn(ex, "temp_startup_cleanup_failed path=" + path));
    }

    private async Task HandleStartupFailureAsync(Exception exception)
    {
        MarkCoordinatedShutdownStarted("startup_composition_failed");
        try
        {
            try
            {
                if (exception is PortableSettingsException settingsFailure)
                {
                    NLogWrapper.FileLogger?.Error(exception, "startup_settings_failed");
                    EmergencyDialog.Show(string.Format(BeMusicSeeker.Properties.Resources.PortableSettingsStartupFailed,
                        settingsFailure.FilePath, settingsFailure.GetBaseException().Message),
                        BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    ExceptionLogger(exception);
                }
            }
            catch
            {
            }
            await StopPerformanceDiagnosticsBeforeTerminalActionAsync(
                "startup_composition_failed").ConfigureAwait(true);
        }
        finally
        {
            ReleaseSingleInstanceMutex();
            Shutdown(1);
        }
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        try
        {
            Task stopTask = Net10PerformanceLog.StopAsync();
            if (!stopTask.Wait(PerformanceDiagnosticsDrainLimit))
            {
                NLogWrapper.FileLogger?.Warn(
                    "performance_diagnostics_stop_timed_out reason=application_exit");
            }
        }
        catch (Exception exception)
        {
            NLogWrapper.FileLogger?.Warn(
                exception,
                "performance_diagnostics_stop_failed reason=application_exit");
        }
        ReleaseSingleInstanceMutex();
    }

    /// <summary>
    /// 終端 cleanup 後に後継 process の起動だけを要求します。
    /// WPF application の shutdown は MainWindow の認可済み terminal が行います。
    /// </summary>
    public Task RestartApplicationAsync()
    {
        if (applicationPathSnapshot == null)
        {
            throw new InvalidOperationException("Application executable path is not available.");
        }

        lock (restartSyncRoot)
        {
            return restartTask ??= StartReplacementApplicationAsync();
        }
    }

    private Task StartReplacementApplicationAsync()
    {
        try
        {
            new ApplicationRestartCoordinator(
                applicationPathSnapshot,
                applicationRestartGateway,
                () => ApplicationRestartArgumentsPolicy.BuildCommandLineArguments(
                    Environment.GetCommandLineArgs()),
                ReleaseSingleInstanceMutex).Restart();
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            try
            {
                NLogWrapper.FileLogger?.Error(
                    exception,
                    "application_restart_failed");
            }
            catch
            {
            }
            ReleaseSingleInstanceMutex();
            return Task.FromException(exception);
        }
    }

    private static async Task StopPerformanceDiagnosticsBeforeTerminalActionAsync(string reason)
    {
        Task stopTask = Net10PerformanceLog.StopAsync();
        if (await Task.WhenAny(
                stopTask,
                Task.Delay(PerformanceDiagnosticsDrainLimit)).ConfigureAwait(false) != stopTask)
        {
            NLogWrapper.FileLogger?.Warn(
                "performance_diagnostics_stop_timed_out reason=" + reason);
            return;
        }

        await stopTask.ConfigureAwait(false);
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
