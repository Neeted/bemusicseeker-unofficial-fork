[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('normal', 'utf8-output', 'nonzero', 'descendant-root', 'descendant-child', 'late-success')]
    [string]$Scenario,

    [string]$LedgerPath,

    [string]$DescendantScriptPath,

    [string]$ReadyEventName,

    [string]$ReleaseEventName
)

function Write-LifecycleLedgerEntry {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process
    )

    if ([string]::IsNullOrWhiteSpace($LedgerPath)) {
        return
    }

    $entry = [ordered]@{
        pid = $Process.Id
        creationIdentity = $Process.StartTime.ToUniversalTime().Ticks
    } | ConvertTo-Json -Compress
    [System.IO.File]::AppendAllText(
        $LedgerPath,
        $entry + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))
}

switch ($Scenario) {
    'utf8-output' {
        # Only this disposable child changes its console writer. Emit non-ASCII on both
        # pipes, including characters that cannot round-trip through the Japanese ANSI page.
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        [Console]::Out.Write('stdout-日本語-✓-😀')
        [Console]::Error.Write('stderr-失敗-✓-😀')
        exit 0
    }
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
    'late-success' {
        [Console]::Out.Write('late-success-stdout')
        [Console]::Out.Flush()
        if (-not [string]::IsNullOrEmpty($ReadyEventName)) {
            $ready = [System.Threading.EventWaitHandle]::OpenExisting($ReadyEventName)
            $release = [System.Threading.EventWaitHandle]::OpenExisting($ReleaseEventName)
            try {
                [void]$ready.Set()
                [void]$release.WaitOne()
            }
            finally {
                $ready.Dispose()
                $release.Dispose()
            }
        }
        exit 0
    }
    'descendant-root' {
        if ([string]::IsNullOrWhiteSpace($DescendantScriptPath) -or
            -not [System.IO.File]::Exists($DescendantScriptPath)) {
            throw "The descendant sleeper script was not found: $DescendantScriptPath"
        }
        $childInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $childInfo.FileName = 'wscript.exe'
        $childInfo.UseShellExecute = $false
        $childInfo.CreateNoWindow = $true
        $childArguments = @(
            '//B',
            '//NoLogo',
            $DescendantScriptPath)
        foreach ($argument in $childArguments) {
            [void]$childInfo.ArgumentList.Add($argument)
        }
        $child = [System.Diagnostics.Process]::new()
        $child.StartInfo = $childInfo
        if (-not $child.Start()) {
            throw 'Unable to start the owned descendant probe.'
        }
        Write-LifecycleLedgerEntry -Process $child
        $child.Dispose()
        [Console]::Out.Write('root-exit')
        if ($env:BMS_LIFECYCLE_PROBE_NONZERO -ceq '1') {
            [Console]::Error.Write('primary-stderr')
            exit 7
        }
        exit 0
    }
    'descendant-child' {
        # Fanout probes use this process itself as the retained root.  Descendant-root
        # scenarios use the GUI-subsystem sleeper above so their exact sidecar contains
        # every process that can remain alive at the forced residual boundary.
        [Console]::Out.Write('descendant-handle')
        [Console]::Out.Flush()
        Start-Sleep -Seconds 30
        exit 0
    }
}
