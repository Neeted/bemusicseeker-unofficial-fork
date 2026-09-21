if (-not ('VerificationUiAutomationNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class VerificationUiAutomationNative
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint command);
}
'@
}

$script:verificationUiAutomationLoaded = $false

function Initialize-VerificationUiAutomation {
    if ($script:verificationUiAutomationLoaded) {
        return
    }
    Add-Type -AssemblyName UIAutomationClient -ErrorAction Stop
    Add-Type -AssemblyName UIAutomationTypes -ErrorAction Stop
    $script:verificationUiAutomationLoaded = $true
}

function Get-VerificationUiAutomationObservations {
    param([Parameter(Mandatory)][int]$ProcessId)

    Initialize-VerificationUiAutomation
    $windowCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Window)
    $windowsByHandle = @{}
    $rootWindows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        $windowCondition)
    foreach ($windowElement in $rootWindows) {
        try {
            $windowElements = @($windowElement) + @(
                $windowElement.FindAll(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    $windowCondition))
            foreach ($candidateWindowElement in $windowElements) {
                $current = $candidateWindowElement.Current
                if ([int]$current.ProcessId -ne $ProcessId) {
                    continue
                }
                $windowHandle = [IntPtr]$current.NativeWindowHandle
                if ($windowHandle -eq [IntPtr]::Zero) {
                    continue
                }
                $windowKey = '{0}:{1}' -f $current.ProcessId, $windowHandle.ToInt64()
                if (-not $windowsByHandle.ContainsKey($windowKey)) {
                    $windowPattern = $null
                    $hasWindowPattern = $candidateWindowElement.TryGetCurrentPattern(
                        [System.Windows.Automation.WindowPattern]::Pattern,
                        [ref]$windowPattern)
                    $windowsByHandle[$windowKey] = [pscustomobject]@{
                        ProcessId = [int]$current.ProcessId
                        NativeWindowHandle = $windowHandle
                        IsOffscreen = [bool]$current.IsOffscreen
                        IsEnabled = [bool]$current.IsEnabled
                        IsModal = [bool]($hasWindowPattern -and $windowPattern.Current.IsModal)
                        OwnerWindowHandle = [VerificationUiAutomationNative]::GetWindow($windowHandle, 4)
                    }
                }
            }
        }
        catch {
            throw 'UI Automation observation failed while reading a process window.'
        }
    }
    return [pscustomobject]@{
        Windows = [object[]]$windowsByHandle.Values
    }
}

function Get-VerificationOwnedModalWindowCandidates {
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][IntPtr]$MainWindowHandle,
        [AllowEmptyCollection()][object[]]$Windows = @()
    )

    $mainHandleValue = $MainWindowHandle.ToInt64()
    return @($Windows | Where-Object {
            $_.ProcessId -eq $ProcessId -and
            $_.NativeWindowHandle -ne 0 -and
            -not [bool]$_.IsOffscreen -and
            [bool]$_.IsEnabled -and
            [bool]$_.IsModal -and
            ([Int64]$_.OwnerWindowHandle) -eq $mainHandleValue
        })
}

function Assert-VerificationNoUnexpectedOwnedModal {
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][IntPtr]$MainWindowHandle
    )

    $observations = Get-VerificationUiAutomationObservations -ProcessId $ProcessId
    $blockingWindows = @(Get-VerificationOwnedModalWindowCandidates `
            -ProcessId $ProcessId `
            -MainWindowHandle $MainWindowHandle `
            -Windows $observations.Windows)
    if ($blockingWindows.Count -gt 0) {
        throw 'Acceptance found an unexpected visible enabled modal owned by the application main window.'
    }
}
