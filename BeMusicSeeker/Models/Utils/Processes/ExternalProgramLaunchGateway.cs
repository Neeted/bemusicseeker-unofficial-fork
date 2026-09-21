using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Ribbit.Logging;

namespace BeMusicSeeker.Models.Utils;

/// <summary>
/// One-shot external program launch request.  The chart path is retained so the
/// gateway can re-check it at click time instead of trusting a stale menu.
/// </summary>
internal sealed class ExternalProgramLaunchRequest
{
    internal ExternalProgramLaunchRequest(
        string actionId,
        string executablePath,
        string chartFilePath,
        IEnumerable<string> arguments)
    {
        ActionId = actionId ?? string.Empty;
        ExecutablePath = executablePath ?? string.Empty;
        ChartFilePath = chartFilePath ?? string.Empty;
        Arguments = Array.AsReadOnly([.. arguments ?? throw new ArgumentNullException(nameof(arguments))]);
    }

    /// <summary>設定された action の安定 ID。</summary>
    internal string ActionId { get; }

    /// <summary>起動対象 executable の絶対パス。</summary>
    internal string ExecutablePath { get; }

    /// <summary>再検証対象 chart の絶対パス。</summary>
    internal string ChartFilePath { get; }

    /// <summary>ArgumentList に渡す exact token 列。</summary>
    internal IReadOnlyList<string> Arguments { get; }
}

/// <summary>
/// Typed outcome of a one-shot external program launch.
/// </summary>
internal sealed class ExternalProgramLaunchResult
{
    private ExternalProgramLaunchResult(
        bool succeeded,
        ExternalProgramLaunchFailureKind failureKind,
        string diagnostic,
        Exception exception)
    {
        Succeeded = succeeded;
        FailureKind = failureKind;
        Diagnostic = diagnostic ?? string.Empty;
        Exception = exception;
    }

    /// <summary>起動要求が Process.Start まで成功したか。</summary>
    internal bool Succeeded { get; }

    /// <summary>失敗時の typed category。</summary>
    internal ExternalProgramLaunchFailureKind FailureKind { get; }

    /// <summary>ログ/UI へ渡す診断文字列。</summary>
    internal string Diagnostic { get; }

    /// <summary>starter が返した例外。通常の missing path では null。</summary>
    internal Exception Exception { get; }

    internal static ExternalProgramLaunchResult Success { get; } =
        new(true, ExternalProgramLaunchFailureKind.None, string.Empty, null);

    internal static ExternalProgramLaunchResult Failure(
        ExternalProgramLaunchFailureKind kind,
        string diagnostic,
        Exception exception = null)
    {
        return new(false, kind, diagnostic, exception);
    }
}

/// <summary>
/// Failure categories exposed by the external program boundary.
/// </summary>
internal enum ExternalProgramLaunchFailureKind
{
    /// <summary>成功または failure なし。</summary>
    None,
    /// <summary>executable path が absolute ではない。</summary>
    InvalidExecutablePath,
    /// <summary>executable が click 時点で存在しない。</summary>
    MissingExecutable,
    /// <summary>chart が click 時点で存在しない。</summary>
    MissingChart,
    /// <summary>starter が例外を返した。</summary>
    StartFailed,
    /// <summary>starter が null process を返した。</summary>
    NullProcess
}

/// <summary>
/// Starts one configured executable without a shell or an owned process lifecycle.
/// </summary>
internal interface IExternalProgramLaunchGateway
{
    ExternalProgramLaunchResult Launch(ExternalProgramLaunchRequest request);
}

/// <summary>
/// Production gateway policy for configured program actions.
/// </summary>
internal static class ExternalProgramLaunchGatewayPolicy
{
    internal static IExternalProgramLaunchGateway Current { get; } = new WindowsExternalProgramLaunchGateway();
}

/// <summary>
/// Windows implementation using ProcessStartInfo.ArgumentList.  It deliberately
/// does not wait for, redirect, or retain the child process.
/// </summary>
internal sealed class WindowsExternalProgramLaunchGateway : IExternalProgramLaunchGateway
{
    private readonly Func<string, bool> fileExists;
    private readonly Func<ProcessStartInfo, Process> starter;

    internal WindowsExternalProgramLaunchGateway(
        Func<string, bool> fileExists = null,
        Func<ProcessStartInfo, Process> starter = null)
    {
        this.fileExists = fileExists ?? LongPathFileSystem.FileExists;
        this.starter = starter ?? Process.Start;
    }

    public ExternalProgramLaunchResult Launch(ExternalProgramLaunchRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (!Path.IsPathFullyQualified(request.ExecutablePath))
        {
            return ExternalProgramLaunchResult.Failure(
                ExternalProgramLaunchFailureKind.InvalidExecutablePath,
                "The configured executable path is not absolute.");
        }
        if (!fileExists(request.ExecutablePath))
        {
            return ExternalProgramLaunchResult.Failure(
                ExternalProgramLaunchFailureKind.MissingExecutable,
                "The configured executable does not exist.");
        }
        if (!Path.IsPathFullyQualified(request.ChartFilePath) || !fileExists(request.ChartFilePath))
        {
            return ExternalProgramLaunchResult.Failure(
                ExternalProgramLaunchFailureKind.MissingChart,
                "The chart file does not exist.");
        }

        string workingDirectory = Path.GetDirectoryName(request.ExecutablePath);
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return ExternalProgramLaunchResult.Failure(
                ExternalProgramLaunchFailureKind.InvalidExecutablePath,
                "The configured executable has no parent directory.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            WindowStyle = ProcessWindowStyle.Normal
        };
        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument ?? string.Empty);
        }

        try
        {
            Process process = starter(startInfo);
            if (process == null)
            {
                return ExternalProgramLaunchResult.Failure(
                    ExternalProgramLaunchFailureKind.NullProcess,
                    "The operating system did not return a process.");
            }

            process.Dispose();
            NLogWrapper.FileLogger?.Info(
                "external_program_launch started actionId=" + (request.ActionId ?? string.Empty)
                + " failureKind=None");
            return ExternalProgramLaunchResult.Success;
        }
        catch (Exception exception)
        {
            NLogWrapper.FileLogger?.Warn(
                exception,
                "external_program_launch failed actionId=" + (request.ActionId ?? string.Empty)
                + " failureKind=StartFailed");
            return ExternalProgramLaunchResult.Failure(
                ExternalProgramLaunchFailureKind.StartFailed,
                exception.Message,
                exception);
        }
    }
}
