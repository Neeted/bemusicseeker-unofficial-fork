using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Localization;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Livet;
using NLog;
using Ribbit.Logging;

namespace BeMusicSeeker;

public partial class App : System.Windows.Application
{
    private static readonly TimeSpan PerformanceDiagnosticsDrainLimit = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ApplicationLogFlushLimit = TimeSpan.FromSeconds(2);

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
    }

    /// <summary>独立した任意診断を設定します。一つの故障で必須起動準備や他の診断を飛ばしません。</summary>
    private void InitializeStartupDiagnostics()
    {
        TryInitializeDiagnostic(() =>
        {
            LogLevel defaultFileLogLevel = ConvertToNLogLevel(CommandLineSwitches.LogLevel);
            NLogWrapper.ConfigureApplicationFileLogging(applicationPathSnapshot.BaseDirectory,
                defaultFileLogLevel, CommandLineSwitches.IsInfoLoggingEnabled);
        }, "startup_file_logging_failed");
        TryInitializeDiagnostic(Net10PerformanceLog.Start, "startup_performance_logging_failed");
        TryInitializeDiagnostic(() =>
        {
            if (CommandLineSwitches.HasInvalidLogLevelValue)
            {
                NLogWrapper.FileLogger?.Warn("Invalid --log-level value '" + CommandLineSwitches.InvalidLogLevelValue + "'. Fallback to Warn.");
            }
        }, "startup_log_level_warning_failed");
    }

    private static void TryInitializeDiagnostic(Action initialize, string diagnostic)
    {
        try { initialize(); }
        catch (Exception exception) { LogFailureBestEffort(exception, diagnostic); }
    }

    private static void LogFailureBestEffort(Exception exception, string diagnostic)
    {
        try { NLogWrapper.FileLogger?.Warn(exception, diagnostic); }
        catch (Exception)
        {
            // 診断先の故障を新しい起動・終了失敗にしません。
        }
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
                var composition = new ApplicationComposition(
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
            showMainWindow: mainWindow =>
            {
                mainWindow.Show();
                // 表示そのものの失敗と任意診断の失敗は、異なる境界で扱います。
                TryInitializeDiagnostic(() =>
                    ApplicationEnvironmentDiagnostics.Start(mainWindow).ObserveFault(),
                    "startup_environment_snapshot_failed");
            },
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
        LogFailureBestEffort(exception, "startup_settings_save_failed");
        string path = exception is PortableSettingsException failure ? failure.FilePath : PortableSettingsPath.UserConfigPath;
        EmergencyDialog.Show(string.Format(BeMusicSeeker.Properties.Resources.PortableSettingsStartupSaveFailed,
            path, exception.GetBaseException().Message), BeMusicSeeker.Properties.Resources.Warning,
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void InitializeOwnedSettings()
    {
        // 必須準備の故障は既存の起動ownerが受け止め、未構成のまま終了します。
        applicationPathSnapshot = ApplicationPathPolicy.Current;
        InitializeStartupDiagnostics();
        InitializeAvailableCultures();
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
                    try { NLogWrapper.FileLogger?.Error(exception, "startup_settings_failed"); }
                    catch (Exception) { /* 元の起動失敗の通知と終了を妨げません。 */ }
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
            // Shutdownが発火するApplication_Exitで通常終了と同じ一回のフラッシュを行います。
            try { ReleaseSingleInstanceMutex(); }
            finally { Shutdown(1); }
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
            LogFailureBestEffort(exception, "performance_diagnostics_stop_failed reason=application_exit");
        }
        finally
        {
            try { NLogWrapper.FlushBeforeExit(ApplicationLogFlushLimit); }
            finally { ReleaseSingleInstanceMutex(); }
        }
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
        try
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
        catch (Exception exception)
        {
            LogFailureBestEffort(exception, "performance_diagnostics_stop_failed reason=" + reason);
        }
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
        bool suppressed = false;
        e.Handled = true;
        try
        {
            suppressed = TrySuppressShutdownSqliteCloseException(exception, "dispatcher");
            if (!suppressed) { ExceptionLogger(exception); }
        }
        finally
        {
            if (!suppressed && !Debugger.IsAttached)
            {
                ExitAfterUnhandledException();
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
            // CLRが既に終了を決めた例外を、統制終了の記録だけで回復扱いにしません。
            suppressed = !e.IsTerminating && TrySuppressShutdownSqliteCloseException(ex, "current_domain");
            if (!suppressed)
            {
                ExceptionLogger(ex);
            }
        }
        finally
        {
            if (!suppressed)
            {
                ExitAfterUnhandledException();
            }
        }
    }

    /// <summary>非常時の案内・窓閉鎖が失敗しても既存の最終終了へ到達します。通常終了の整合性待ちは代替しません。</summary>
    private static void ExitAfterUnhandledException(bool closeMainWindow = true)
    {
        try
        {
            Thread.Sleep(1000);
            try
            {
                EmergencyDialog.Show(BeMusicSeeker.Properties.Resources.Msg_ApplicationWillExit,
                    BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OK,
                    MessageBoxImage.Asterisk, MessageBoxResult.Yes);
            }
            catch (Exception exception) { LogFailureBestEffort(exception, "unhandled_exception_exit_notification_failed"); }
            if (closeMainWindow && System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
            {
                try { System.Windows.Application.Current?.MainWindow?.Close(); }
                catch (Exception exception) { LogFailureBestEffort(exception, "unhandled_exception_window_close_failed"); }
            }
        }
        finally
        {
            try { NLogWrapper.FlushBeforeExit(ApplicationLogFlushLimit); }
            finally { Environment.Exit(1); }
        }
    }

    /// <summary>操作所有の終端後に既存宛先の復元不能を受け、通常Closeを通さず保全物を維持して最終終了します。</summary>
    internal void ExitForUnrestoredDestination(Exception failure)
    {
        try { ExceptionLogger(failure, showMessage: false); }
        finally { ExitAfterUnhandledException(closeMainWindow: false); }
    }

    /// <summary>元原因のローカル診断と案内を個別に試し、診断や表示の故障で終端を変えません。</summary>
    private void ExceptionLogger(Exception ex, bool showMessage = true)
    {
        try { NLogWrapper.FileLogger?.Error(ex, "unhandled_exception"); }
        catch (Exception) { /* 他の診断と必要終了を妨げません。 */ }
        if (showMessage)
        {
            try
            {
                EmergencyDialog.Show(string.Format(BeMusicSeeker.Properties.Resources.Error_UnhandledExceptionFormat, ex.Message),
                    BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            }
            catch (Exception) { /* 表示失敗から再通知や例外輸送を始めません。 */ }
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
