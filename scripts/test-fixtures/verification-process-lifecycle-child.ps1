[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('normal', 'nonzero', 'descendant-root', 'descendant-child')]
    [string]$Scenario
)

switch ($Scenario) {
    'normal' {
        [Console]::Out.Write('stdout-complete')
        [Console]::Error.Write('stderr-complete')
        exit 0
    }
    'nonzero' {
        [Console]::Out.Write('primary-stdout')
        [Console]::Error.Write('primary-stderr')
        exit 7
    }
    'descendant-root' {
        $childInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $childInfo.FileName = 'pwsh'
        $childInfo.UseShellExecute = $false
        $childInfo.CreateNoWindow = $false
        foreach ($argument in @(
                '-NoProfile',
                '-File',
                $PSCommandPath,
                '-Scenario',
                'descendant-child')) {
            [void]$childInfo.ArgumentList.Add($argument)
        }
        $child = [System.Diagnostics.Process]::new()
        $child.StartInfo = $childInfo
        if (-not $child.Start()) {
            throw 'Unable to start the owned descendant probe.'
        }
        $child.Dispose()
        [Console]::Out.Write('root-exit')
        if ($env:BMS_LIFECYCLE_PROBE_NONZERO -ceq '1') {
            [Console]::Error.Write('primary-stderr')
            exit 7
        }
        exit 0
    }
    'descendant-child' {
        # The child deliberately keeps the inherited stdout handle open.  The production
        # lifecycle must identify and stop this child by lineage, not by process name.
        [Console]::Out.Write('descendant-handle')
        [Console]::Out.Flush()
        Start-Sleep -Seconds 30
        exit 0
    }
}
