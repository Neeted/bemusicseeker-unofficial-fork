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
    $buttonCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $windowsByHandle = @{}
    $actionsByRuntimeId = @{}
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

                foreach ($buttonElement in @($candidateWindowElement.FindAll(
                            [System.Windows.Automation.TreeScope]::Descendants,
                            $buttonCondition))) {
                    $buttonCurrent = $buttonElement.Current
                    if ([int]$buttonCurrent.ProcessId -ne $ProcessId) {
                        continue
                    }
                    $buttonWindowHandle = [IntPtr]::Zero
                    $ancestor = $buttonElement
                    while ($null -ne $ancestor) {
                        $ancestorCurrent = $ancestor.Current
                        if ([object]::Equals(
                                $ancestorCurrent.ControlType,
                                [System.Windows.Automation.ControlType]::Window)) {
                            $ancestorHandle = [IntPtr]$ancestorCurrent.NativeWindowHandle
                            if ($ancestorHandle -ne [IntPtr]::Zero) {
                                $buttonWindowHandle = $ancestorHandle
                                break
                            }
                        }
                        $ancestor = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($ancestor)
                    }
                    if ($buttonWindowHandle -eq [IntPtr]::Zero) {
                        $buttonWindowHandle = $windowHandle
                    }

                    $runtimeId = try {
                        ($buttonElement.GetRuntimeId() -join '.')
                    }
                    catch {
                        [System.Runtime.CompilerServices.RuntimeHelpers]::GetHashCode($buttonElement).ToString(
                            [Globalization.CultureInfo]::InvariantCulture)
                    }
                    $actionKey = '{0}:{1}:{2}' -f $buttonCurrent.ProcessId, $buttonWindowHandle.ToInt64(), $runtimeId
                    if ($actionsByRuntimeId.ContainsKey($actionKey)) {
                        continue
                    }
                    $invokePattern = $null
                    $supportsInvoke = $buttonElement.TryGetCurrentPattern(
                        [System.Windows.Automation.InvokePattern]::Pattern,
                        [ref]$invokePattern)
                    $actionsByRuntimeId[$actionKey] = [pscustomobject]@{
                        ProcessId = [int]$buttonCurrent.ProcessId
                        NativeWindowHandle = $buttonWindowHandle
                        IsOffscreen = [bool]$buttonCurrent.IsOffscreen
                        IsEnabled = [bool]$buttonCurrent.IsEnabled
                        AutomationId = [string]$buttonCurrent.AutomationId
                        SupportsInvoke = [bool]$supportsInvoke
                        Element = $buttonElement
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
        Actions = [object[]]$actionsByRuntimeId.Values
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
