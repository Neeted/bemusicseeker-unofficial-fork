using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[TestCategory("ProcessIntegration")]
public sealed class ExistingDataAcceptanceDialogContractTests
{
    [TestMethod]
    public void DialogClassifier_RejectsUnsafeObservationsAndTracksExactResidualIdentity()
    {
        using JsonDocument document = RunPowerShell("""
            $ErrorActionPreference = 'Stop'
            . $env:BMS_TEST_SCRIPT -ImportFunctionsOnly

            function New-TestWindow {
                param(
                    [int]$ProcessId,
                    [long]$NativeWindowHandle,
                    [bool]$IsOffscreen = $false,
                    [bool]$IsEnabled = $true,
                    [bool]$IsModal = $true,
                    [long]$OwnerWindowHandle = 1001)

                [pscustomobject]@{
                    ProcessId = $ProcessId
                    NativeWindowHandle = [IntPtr]$NativeWindowHandle
                    IsOffscreen = $IsOffscreen
                    IsEnabled = $IsEnabled
                    IsModal = $IsModal
                    OwnerWindowHandle = [IntPtr]$OwnerWindowHandle
                }
            }

            function New-TestAction {
                param(
                    [int]$ProcessId,
                    [long]$NativeWindowHandle,
                    [string]$AutomationId = 'ThemedMessageBoxOK',
                    [bool]$IsOffscreen = $false,
                    [bool]$IsEnabled = $true,
                    [bool]$SupportsInvoke = $true)

                [pscustomobject]@{
                    ProcessId = $ProcessId
                    NativeWindowHandle = [IntPtr]$NativeWindowHandle
                    IsOffscreen = $IsOffscreen
                    IsEnabled = $IsEnabled
                    AutomationId = $AutomationId
                    SupportsInvoke = $SupportsInvoke
                }
            }

            $targetPid = 4201
            $mainHandle = [IntPtr]1001
            $dialogHandle = [IntPtr]2001
            $validWindow = New-TestWindow $targetPid $dialogHandle.ToInt64()
            $validAction = New-TestAction $targetPid $dialogHandle.ToInt64()

             function Test-Rejected {
                param([object[]]$Windows, [object[]]$Actions)
                $classification = Resolve-ExistingDataCompletionDialog `
                    -ProcessId $targetPid `
                    -CapturedMainWindowHandle $mainHandle `
                    -Windows $Windows `
                    -Actions $Actions
                [ordered]@{
                    accepted = [bool]$classification.Accepted
                    actionSelected = $null -ne $classification.Action
                 }
             }

             function Test-ResultRejected {
                 param(
                     [object]$Result,
                     [switch]$RequireSuccess,
                     [string]$ExpectedPrimaryFailureKind)

                 try {
                     if ($RequireSuccess) {
                         [void](Assert-VerificationProcessResult `
                                 -Result $Result `
                                 -Label 'synthetic first-hop updater result' `
                                 -RequireSuccess)
                     }
                     else {
                         [void](Assert-VerificationProcessResult `
                                 -Result $Result `
                                 -Label 'synthetic first-hop updater result' `
                                 -ExpectedPrimaryFailureKind $ExpectedPrimaryFailureKind)
                     }
                     return $false
                 }
                 catch {
                     return $true
                 }
             }

             function New-TestResult {
                 param(
                     [object]$PrimaryFailureKind,
                     [object[]]$SecondaryDiagnostics = @())

                 [pscustomobject]@{
                     PrimaryFailureKind = $PrimaryFailureKind
                     SecondaryDiagnostics = @($SecondaryDiagnostics)
                 }
             }

            [ordered]@{
                ownerless = Test-Rejected `
                    @(New-TestWindow $targetPid $dialogHandle.ToInt64() $false $true $true 0) `
                    @($validAction)
                nonModal = Test-Rejected `
                    @(New-TestWindow $targetPid $dialogHandle.ToInt64() $false $true $false) `
                    @($validAction)
                disabledWindow = Test-Rejected `
                    @(New-TestWindow $targetPid $dialogHandle.ToInt64() $false $false) `
                    @($validAction)
                hiddenWindow = Test-Rejected `
                    @(New-TestWindow $targetPid $dialogHandle.ToInt64() $true) `
                    @($validAction)
                wrongProcess = Test-Rejected `
                    @(New-TestWindow ($targetPid + 1) $dialogHandle.ToInt64()) `
                    @($validAction)
                multipleOwnedModals = Test-Rejected `
                    @($validWindow, (New-TestWindow $targetPid ($dialogHandle.ToInt64() + 1))) `
                    @($validAction)
                duplicateOkActions = Test-Rejected `
                    @($validWindow) `
                    @($validAction, (New-TestAction $targetPid $dialogHandle.ToInt64()))
                nonInvokableOkAction = Test-Rejected `
                    @($validWindow) `
                    @(New-TestAction $targetPid $dialogHandle.ToInt64() 'ThemedMessageBoxOK' $false $true $false)
                hiddenOkAction = Test-Rejected `
                    @($validWindow) `
                    @(New-TestAction $targetPid $dialogHandle.ToInt64() 'ThemedMessageBoxOK' $true)
                disabledOkAction = Test-Rejected `
                    @($validWindow) `
                    @(New-TestAction $targetPid $dialogHandle.ToInt64() 'ThemedMessageBoxOK' $false $false)
                wrongActionId = Test-Rejected `
                    @($validWindow) `
                    @(New-TestAction $targetPid $dialogHandle.ToInt64() 'ThemedMessageBoxCancel')
                wrongActionProcess = Test-Rejected `
                    @($validWindow) `
                    @(New-TestAction ($targetPid + 1) $dialogHandle.ToInt64())
                wrongActionHandle = Test-Rejected `
                    @($validWindow) `
                    @(New-TestAction $targetPid ($dialogHandle.ToInt64() + 1))
                residualSamePidAndHandle = Test-ExistingDataCompletionDialogDisappeared `
                    -ProcessId $targetPid `
                    -NativeWindowHandle $dialogHandle `
                    -Windows @($validWindow)
                residualOtherIdentityIsAbsent = Test-ExistingDataCompletionDialogDisappeared `
                    -ProcessId ($targetPid + 1) `
                    -NativeWindowHandle $dialogHandle `
                    -Windows @($validWindow)
                unknownOwnerBoundModalDetected = @(
                    Get-VerificationOwnedModalWindowCandidates `
                        -ProcessId $targetPid `
                        -MainWindowHandle $mainHandle `
                        -Windows @($validWindow)).Count -eq 1
                resultGate = [ordered]@{
                    happyTimeoutRejected = Test-ResultRejected `
                        -Result (New-TestResult 'timeout') `
                        -RequireSuccess
                    happySecondaryRejected = Test-ResultRejected `
                        -Result (New-TestResult $null @('stream-drain-timeout')) `
                        -RequireSuccess
                    lockTimeoutRejected = Test-ResultRejected `
                        -Result (New-TestResult 'timeout') `
                        -ExpectedPrimaryFailureKind 'nonzero-exit'
                    lockSecondaryRejected = Test-ResultRejected `
                        -Result (New-TestResult 'nonzero-exit' @('stream-drain-timeout')) `
                        -ExpectedPrimaryFailureKind 'nonzero-exit'
                    happyCleanAccepted = -not (Test-ResultRejected `
                        -Result (New-TestResult $null) `
                        -RequireSuccess)
                    lockNonzeroAccepted = -not (Test-ResultRejected `
                        -Result (New-TestResult 'nonzero-exit') `
                        -ExpectedPrimaryFailureKind 'nonzero-exit')
                }
            } | ConvertTo-Json -Compress
            """);

        {
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Name.StartsWith("residual", StringComparison.Ordinal) ||
                    property.Name == "unknownOwnerBoundModalDetected" ||
                    property.Name == "resultGate")
                {
                    continue;
                }

                Assert.IsFalse(
                    property.Value.GetProperty("accepted").GetBoolean(),
                    $"{property.Name} unexpectedly accepted an unsafe dialog.");
                Assert.IsFalse(
                    property.Value.GetProperty("actionSelected").GetBoolean(),
                    $"{property.Name} selected an action after rejection.");
            }

            Assert.IsFalse(document.RootElement.GetProperty("residualSamePidAndHandle").GetBoolean());
            Assert.IsTrue(document.RootElement.GetProperty("residualOtherIdentityIsAbsent").GetBoolean());
            Assert.IsTrue(document.RootElement.GetProperty("unknownOwnerBoundModalDetected").GetBoolean());

            JsonElement resultGate = document.RootElement.GetProperty("resultGate");
            Assert.IsTrue(resultGate.GetProperty("happyTimeoutRejected").GetBoolean());
            Assert.IsTrue(resultGate.GetProperty("happySecondaryRejected").GetBoolean());
            Assert.IsTrue(resultGate.GetProperty("lockTimeoutRejected").GetBoolean());
            Assert.IsTrue(resultGate.GetProperty("lockSecondaryRejected").GetBoolean());
            Assert.IsTrue(resultGate.GetProperty("happyCleanAccepted").GetBoolean());
            Assert.IsTrue(resultGate.GetProperty("lockNonzeroAccepted").GetBoolean());
        }
    }

    private static JsonDocument RunPowerShell(string command)
    {
        const int processTimeoutMilliseconds = 30_000;
        const int cleanupTimeoutMilliseconds = 5_000;
        const int streamTimeoutMilliseconds = 5_000;
        string repositoryRoot = FindRepositoryRoot();
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);
        startInfo.Environment["BMS_TEST_SCRIPT"] = Path.Combine(
            repositoryRoot,
            "scripts",
            "accept-net10-existing-data.ps1");

        using var process = new Process { StartInfo = startInfo };
        Assert.IsTrue(process.Start(), "The dialog classifier PowerShell process did not start.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        Exception? primaryFailure = null;
        var secondaryDiagnostics = new List<string>();
        try
        {
            if (!process.WaitForExit(processTimeoutMilliseconds))
            {
                primaryFailure = new AssertFailedException(
                    $"The dialog classifier PowerShell process exceeded {processTimeoutMilliseconds / 1000} seconds.");
            }
            else
            {
                if (!Task.WaitAll(
                        new Task[] { outputTask, errorTask },
                        streamTimeoutMilliseconds))
                {
                    primaryFailure = new AssertFailedException(
                        $"The dialog classifier PowerShell output did not close within {streamTimeoutMilliseconds / 1000} seconds.");
                }
                else if (process.ExitCode != 0)
                {
                    primaryFailure = new AssertFailedException(
                        $"PowerShell classifier exited with code {process.ExitCode}.");
                }
            }
        }
        catch (Exception exception) when (primaryFailure is null)
        {
            primaryFailure = exception;
        }
        finally
        {
            if (primaryFailure is not null)
            {
                string cleanup = StopProcessTree(process, cleanupTimeoutMilliseconds);
                if (!string.Equals(cleanup, "completed", StringComparison.Ordinal))
                {
                    secondaryDiagnostics.Add(cleanup);
                }
            }

            try
            {
                if (!Task.WaitAll(
                        new Task[] { outputTask, errorTask },
                        streamTimeoutMilliseconds))
                {
                    secondaryDiagnostics.Add(
                        $"Redirected streams remained incomplete at the {streamTimeoutMilliseconds / 1000}-second cleanup cutoff.");
                }
            }
            catch (Exception exception)
            {
                secondaryDiagnostics.Add(
                    $"Redirected stream failure observed during cleanup: {exception.GetType().Name}: {exception.Message}");
            }
        }

        if (primaryFailure is not null)
        {
            if (secondaryDiagnostics.Count > 0)
            {
                primaryFailure.Data["ProcessSecondaryDiagnostics"] = secondaryDiagnostics.ToArray();
            }
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

        string output = outputTask.GetAwaiter().GetResult();
        string error = errorTask.GetAwaiter().GetResult();
        Assert.IsTrue(string.IsNullOrWhiteSpace(error), $"PowerShell classifier wrote stderr: {error}");
        return JsonDocument.Parse(output.Trim());
    }

    private static string StopProcessTree(Process process, int cleanupTimeoutMilliseconds)
    {
        var diagnostics = new List<string>();
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception)
        {
            diagnostics.Add("managed process-tree termination failed: " + exception.Message);
        }

        try
        {
            if (!process.HasExited)
            {
                if (!process.WaitForExit(cleanupTimeoutMilliseconds))
                {
                    diagnostics.Add(
                        $"PowerShell PID {process.Id} remained active after {cleanupTimeoutMilliseconds / 1000} seconds.");
                }
            }
        }
        catch (Exception exception)
        {
            diagnostics.Add("process cleanup wait failed: " + exception.Message);
        }

        return diagnostics.Count == 0 ? "completed" : string.Join("; ", diagnostics);
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "accept-net10-existing-data.ps1")) &&
                File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker.Tests", "BeMusicSeeker.Tests.csproj")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the BeMusicSeeker repository root.");
    }
}
