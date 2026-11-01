using System.IO;
using System.Text.Json;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Runtime;

namespace BeMusicSeeker.WindowsProcessProbe;

/// <summary>製品の配置・排他・一時leaseをfresh processで観測します。期限と強制停止は親のOwnedProcessが所有します。</summary>
internal static class RuntimeIsolationProbe
{
    /// <summary>固定modeだけを受け付け、stdoutへ変更不能な最終観測結果を一行で返します。</summary>
    internal static async Task<int> RunAsync(string[] arguments)
    {
        try
        {
            if (arguments.Length == 0) { throw new ArgumentException("A runtime isolation mode is required."); }
            return arguments[0] switch
            {
                "hold-profile" => HoldProfile(arguments),
                "hold-temp" => await HoldTempAsync(arguments).ConfigureAwait(false),
                "cleanup-temp" => await CleanupTempAsync(arguments).ConfigureAwait(false),
                "capture-runtime" => CaptureRuntime(arguments),
                _ => throw new ArgumentException("Unknown runtime isolation mode: " + arguments[0])
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 2;
        }
    }

    private static int HoldProfile(string[] arguments)
    {
        RequireCount(arguments, 6);
        // 常にケース専有の通常mutex名を必須入力とし、利用者の通常アプリを妨げない。
        if (!arguments[3].StartsWith("Local\\BeMusicSeeker.RuntimeTests.", StringComparison.Ordinal))
        {
            throw new ArgumentException("A case-owned normal mutex name is required.");
        }
        using var lease = ApplicationInstanceLease.TryAcquire(arguments[1],
            bool.Parse(arguments[2]), TimeSpan.Zero, arguments[3]);
        var result = new ProfileLeaseResult(lease != null);
        PublishReady(arguments[4], result);
        if (lease != null)
        {
            // Mutexの取得から解放まで同じスレッドを保つ。stdoutの途中読取りは共通回収と競合させない。
            WaitForRelease(arguments[5]);
        }
        Program.WriteResult(result);
        return 0;
    }

    private static async Task<int> HoldTempAsync(string[] arguments)
    {
        RequireCount(arguments, 4);
        var session = new TemporaryDirectorySession(arguments[1]);
        TemporaryLeaseResult result;
        try
        {
            string leaf = session.Get("process-probe");
            File.WriteAllText(Path.Combine(leaf, "owned.txt"), "live temporary payload");
            result = new TemporaryLeaseResult(leaf);
            PublishReady(arguments[2], result);
            WaitForRelease(arguments[3]);
        }
        finally
        {
            await session.RemoveAllAsync(ThrowCleanupFailure).ConfigureAwait(false);
        }
        Program.WriteResult(result);
        return 0;
    }

    private static async Task<int> CleanupTempAsync(string[] arguments)
    {
        RequireCount(arguments, 2);
        var session = new TemporaryDirectorySession(arguments[1]);
        try { await session.StartCleanupStaleDirectoriesAsync(logWarning: ThrowCleanupFailure).ConfigureAwait(false); }
        finally { await session.RemoveAllAsync(ThrowCleanupFailure).ConfigureAwait(false); }
        Program.WriteResult(new TemporaryCleanupResult(true));
        return 0;
    }

    private static int CaptureRuntime(string[] arguments)
    {
        if (arguments.Length < 2) { throw new ArgumentException("An executable path is required."); }
        ApplicationRuntimeOptions options = ApplicationRuntimeOptionsParser.Capture(arguments[1], arguments[2..]);
        CommandLineSwitches.Configure(options.LogOptions);
        ApplicationPathPolicy.Configure(options.Paths);
        string bundledSevenZipLibraryPath = SevenZipArchiveExtractor.ResolveBundledSevenZipLibraryPath();
        Program.WriteResult(new RuntimeCaptureResult(
            options.Paths.ExecutablePath, options.Paths.AssetsDirectory, options.Paths.ProfileDirectory,
            PortableSettingsPath.UserConfigPath, PortableSettingsPath.DataDirectoryPath,
            options.Paths.LanguageDirectory, options.Paths.LogDirectory, options.Paths.TempDirectory,
            options.Paths.LegacySettingsDirectory, options.UpdateManifestUrl,
            options.DatabaseLoadOptions.UseRawSongCatalog, options.DatabaseLoadOptions.UseRawMaintenanceTable,
            options.DatabaseLoadOptions.UseRawChartInfoHydration, options.RestartArguments.ToArray(), bundledSevenZipLibraryPath,
            CommandLineSwitches.LogLevel, CommandLineSwitches.IsInfoLoggingEnabled,
            CommandLineSwitches.HasInvalidLogLevelValue, CommandLineSwitches.InvalidLogLevelValue));
        return 0;
    }

    private static void RequireCount(string[] arguments, int count)
    {
        if (arguments.Length != count) { throw new ArgumentException("Invalid runtime isolation argument count."); }
    }

    private static void PublishReady<T>(string path, T result)
    {
        string pending = path + ".pending";
        File.WriteAllText(pending, JsonSerializer.Serialize(result));
        File.Move(pending, path);
    }

    private static void WaitForRelease(string releasePath)
    {
        while (!File.Exists(releasePath)) { Thread.Sleep(20); }
    }

    private static void ThrowCleanupFailure(string path, Exception exception)
        => throw new IOException("Temporary session cleanup failed: " + path, exception);

    private sealed record ProfileLeaseResult(bool Acquired);
    private sealed record TemporaryLeaseResult(string LeafPath);
    private sealed record TemporaryCleanupResult(bool Completed);
    private sealed record RuntimeCaptureResult(string ExecutablePath, string AssetsDirectory, string ProfileDirectory,
        string UserConfigPath, string DataDirectoryPath, string LanguageDirectory, string LogDirectory,
        string TempDirectory, string LegacySettingsDirectory, string UpdateManifestUrl,
        bool UseRawSongCatalog, bool UseRawMaintenanceTable, bool UseRawChartInfoHydration, string[] RestartArguments,
        string BundledSevenZipLibraryPath, NormalLogLevel LogLevel, bool IsInfoLoggingEnabled,
        bool HasInvalidLogLevelValue, string InvalidLogLevelValue);
}
