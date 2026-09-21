using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[TestCategory("ProcessIntegration")]
public sealed class ExistingDataAcceptanceDialogContractTests
{
    [TestMethod]
    public void OwnedModalCandidatesRejectUnsafeObservationsAndPreserveProcessResultGate()
    {
        using JsonDocument document = RunPowerShell("""
            $ErrorActionPreference = 'Stop'
            $scriptRoot = Split-Path -Parent $env:BMS_TEST_SCRIPT
            . (Join-Path $scriptRoot 'verification-process-lifecycle.ps1')
            . (Join-Path $scriptRoot 'verification-ui-automation.ps1')

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

            $targetPid = 4201
            $mainHandle = [IntPtr]1001
            $dialogHandle = [IntPtr]2001
            $validWindow = New-TestWindow $targetPid $dialogHandle.ToInt64()

            function Get-CandidateCount {
                param([object[]]$Windows)
                return @(
                    Get-VerificationOwnedModalWindowCandidates `
                        -ProcessId $targetPid `
                        -MainWindowHandle $mainHandle `
                        -Windows $Windows).Count
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
                ownedModal = Get-CandidateCount @($validWindow)
                multipleOwnedModals = Get-CandidateCount `
                    @($validWindow, (New-TestWindow $targetPid ($dialogHandle.ToInt64() + 1)))
                wrongProcess = Get-CandidateCount `
                    @(New-TestWindow ($targetPid + 1) $dialogHandle.ToInt64())
                ownerless = Get-CandidateCount `
                    @(New-TestWindow $targetPid $dialogHandle.ToInt64() $false $true $true 0)
                zeroHandle = Get-CandidateCount `
                    @(New-TestWindow $targetPid 0)
                hiddenWindow = Get-CandidateCount `
                    @(New-TestWindow $targetPid $dialogHandle.ToInt64() $true)
                disabledWindow = Get-CandidateCount `
                    @(New-TestWindow $targetPid $dialogHandle.ToInt64() $false $false)
                nonModal = Get-CandidateCount `
                    @(New-TestWindow $targetPid $dialogHandle.ToInt64() $false $true $false)
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
            Assert.AreEqual(1, document.RootElement.GetProperty("ownedModal").GetInt32());
            Assert.AreEqual(2, document.RootElement.GetProperty("multipleOwnedModals").GetInt32());
            foreach (string propertyName in new[]
                     { "wrongProcess", "ownerless", "zeroHandle", "hiddenWindow", "disabledWindow", "nonModal" })
            {
                Assert.AreEqual(
                    0,
                    document.RootElement.GetProperty(propertyName).GetInt32(),
                    $"{propertyName} unexpectedly became an owned modal candidate.");
            }

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
        string repositoryRoot = PowerShellTestProcess.FindRepositoryRoot();
        PowerShellTestResult result = PowerShellTestProcess.Run(
            repositoryRoot,
            command,
            "UI observation",
            new Dictionary<string, string>
            {
                ["BMS_TEST_SCRIPT"] = Path.Combine(
                    repositoryRoot,
                    "scripts",
                    "accept-net10-existing-data.ps1")
            });
        Assert.IsTrue(string.IsNullOrWhiteSpace(result.Error), $"PowerShell UI observation wrote stderr: {result.Error}");
        return JsonDocument.Parse(result.Output.Trim());
    }
}
