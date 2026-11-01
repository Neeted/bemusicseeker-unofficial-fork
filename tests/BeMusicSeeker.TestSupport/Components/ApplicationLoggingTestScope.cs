using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using BeMusicSeeker.TestSupport.Files;
using NLog;
using NLog.Config;
using Ribbit.Logging;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>非並列のログ検証が交換した構成とFile/Network参照を、配送終端後に元へ戻します。</summary>
internal sealed class ApplicationLoggingTestScope
{
    private static readonly FieldInfo FileTargetField = FindField("<FileTarget>k__BackingField");
    private static readonly FieldInfo NetworkTargetField = FindField("<NetworkTarget>k__BackingField");
    private static readonly FieldInfo FileLoggerField = FindField("_FileLogger");
    private static readonly FieldInfo NetworkLoggerField = FindField("_NetworkLogger");
    private readonly LoggingConfiguration? configuration;
    private readonly object? fileTarget;
    private readonly object? networkTarget;
    private readonly object? fileLogger;
    private readonly object? networkLogger;

    private ApplicationLoggingTestScope(string rootDirectory)
    {
        RuntimeHelpers.RunClassConstructor(typeof(NLogWrapper).TypeHandle);
        configuration = LogManager.Configuration;
        fileTarget = FileTargetField.GetValue(null);
        networkTarget = NetworkTargetField.GetValue(null);
        fileLogger = FileLoggerField.GetValue(null);
        networkLogger = NetworkLoggerField.GetValue(null);
        RootDirectory = rootDirectory;
    }

    /// <summary>ログ入力と出力を作る前から所有するケース領域です。</summary>
    internal string RootDirectory { get; }

    /// <summary>本体の失敗と構成・参照・領域の回収失敗を別々に保持し、全回収を試みます。</summary>
    internal static async Task RunAsync(Func<ApplicationLoggingTestScope, Task> body)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        ApplicationLoggingTestScope? scope = null;
        Exception? primary = null;
        var cleanup = new List<Exception>();
        try
        {
            scope = new ApplicationLoggingTestScope(workspace.RootDirectory);
            LogManager.Configuration = new LoggingConfiguration();
            NetworkTargetField.SetValue(null, null);
            NetworkLoggerField.SetValue(null, null);
            await body(scope);
        }
        catch (Exception exception) { primary = exception; }
        finally
        {
            if (scope is not null) { scope.Restore(cleanup); }
            try { workspace.Dispose(); }
            catch (Exception exception) { cleanup.Add(exception); }
        }
        if (primary is not null)
        {
            if (cleanup.Count > 0) { primary.Data["LoggingCleanupFailures"] = new AggregateException(cleanup); }
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
        if (cleanup.Count > 0) { throw new AggregateException("ログ検証の回収に失敗しました。", cleanup); }
    }

    private void Restore(List<Exception> failures)
    {
        bool? previousThrowConfigExceptions = LogManager.ThrowConfigExceptions;
        try
        {
            LogManager.ThrowConfigExceptions = true;
            try { LogManager.Configuration = configuration; }
            catch (Exception exception) { failures.Add(exception); }
            RestoreField(FileTargetField, fileTarget, failures);
            RestoreField(NetworkTargetField, networkTarget, failures);
            RestoreField(FileLoggerField, fileLogger, failures);
            RestoreField(NetworkLoggerField, networkLogger, failures);
        }
        finally { LogManager.ThrowConfigExceptions = previousThrowConfigExceptions; }
    }

    private static void RestoreField(FieldInfo field, object? value, List<Exception> failures)
    {
        try { field.SetValue(null, value); }
        catch (Exception exception) { failures.Add(exception); }
    }

    private static FieldInfo FindField(string name) => typeof(NLogWrapper).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(NLogWrapper).FullName, name);
}
