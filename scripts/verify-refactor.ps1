[CmdletBinding()]
param(
    [ValidateSet('Quick', 'Functional', 'Full')]
    [string]$Mode = 'Quick',

    [string]$TestFilter,

    # This seam only lowers the canonical budget so the timeout path can be
    # verified without waiting three minutes. It cannot relax the policy limit.
    [ValidateRange(1, 180)]
    [int]$FunctionalTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$commandStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'BeMusicSeeker.sln'
$uiExecutable = Join-Path $repoRoot 'bin\x64\Release\net10.0-windows\BeMusicSeeker.exe'
$toolExecutables = @(
    (Join-Path $repoRoot 'tools\chart-info-compare\bin\x64\Release\net10.0\ChartInfoCompare.exe'),
    (Join-Path $repoRoot 'tools\chart-info-export\bin\x64\Release\net10.0\ChartInfoExport.exe'))
$verificationArtifactsDirectory = Join-Path $repoRoot 'artifacts\verification'
$scdPublishRoot = Join-Path $repoRoot 'artifacts\publish'
$scdAppPublishOutput = Join-Path $scdPublishRoot 'app'
$scdUpdaterPublishOutput = Join-Path $scdPublishRoot 'updater'
$existingDataAcceptanceScript = Join-Path $repoRoot 'scripts\accept-net10-existing-data.ps1'
$updateAcceptanceScript = Join-Path $repoRoot 'scripts\accept-net10-update.ps1'
$testHangTimeoutSeconds = 120
$functionalCleanupReserveSeconds = 10
$functionalProcessCleanupSeconds = 7
$functionalFilter = @(
    'TestCategory!=Net10Performance',
    'TestCategory!=Performance',
    'TestCategory!=LargeFixture',
    'TestCategory!=ParserCompatibilityFull',
    'TestCategory!=ParserCompatibilitySlow',
    'TestCategory!=ProductionDiffFull',
    'TestCategory!=ProcessIntegration',
    'TestCategory!=ReleaseAcceptance') -join '&'
$functionalBassCollectibleLoadContextClass = 'BeMusicSeeker.Tests.BassCollectibleLoadContextTests'
$functionalSettingsPresentationClasswideClasses = @(
    'BeMusicSeeker.Tests.ApplicationCompositionTests',
    'BeMusicSeeker.Tests.ApplicationSettingsLifecycleTests',
    'BeMusicSeeker.Tests.ApplicationUiSchedulerBoundaryTests',
    'BeMusicSeeker.Tests.BeatorajaBmtOptionsSnapshotTests',
    'BeMusicSeeker.Tests.BmsLibraryOptionsSnapshotTests',
    'BeMusicSeeker.Tests.CustomFolderOutputSettingsSnapshotTests',
    'BeMusicSeeker.Tests.MainWindowViewSettingsBoundaryTests',
    'BeMusicSeeker.Tests.PlayerSettingsGatewayTests',
    'BeMusicSeeker.Tests.PlaylistUrlCompletionOptionsSnapshotTests',
    'BeMusicSeeker.Tests.ResourceIconContractTests',
    'BeMusicSeeker.Tests.SettingDialogCustomFolderOutputBaseTests',
    'BeMusicSeeker.Tests.SettingDialogEditCompletionTests',
    'BeMusicSeeker.Tests.SettingDialogOpenCommandTests',
    'BeMusicSeeker.Tests.SettingsWindowPresentationTests',
    'BeMusicSeeker.Tests.ShellShutdownWorkflowOwnerTests',
    'BeMusicSeeker.Tests.StartupSettingsSnapshotTests')
$functionalProcessGlobalLifecycleClasses = @(
    'BeMusicSeeker.Tests.BassNativeRuntimeTests',
    'BeMusicSeeker.Tests.NLogWrapperTests')
$functionalFeatureProcessGlobalStateClasses = @(
    'BeMusicSeeker.Tests.AudioContractsTests',
    'BeMusicSeeker.Tests.AudioDeviceTestWorkflowOwnerTests',
    'BeMusicSeeker.Tests.BmsLibraryInstallEstimationServiceTests',
    'BeMusicSeeker.Tests.CatalogMutationOwnerTests',
    'BeMusicSeeker.Tests.ChartListVirtualViewTests',
    'BeMusicSeeker.Tests.InstallDestinationStateOwnerTests',
    'BeMusicSeeker.Tests.InstalledOnlyResourceOverwriteValidationTests',
    'BeMusicSeeker.Tests.LibraryFileScanPipelineOwnerTests',
    'BeMusicSeeker.Tests.Lr2PlayHistorySchemaServiceTests',
    'BeMusicSeeker.Tests.Lr2PlayHistorySchemaUiTests',
    'BeMusicSeeker.Tests.MainWindowExternalShellTests',
    'BeMusicSeeker.Tests.MainWindowViewModelStartupProgressTests',
    'BeMusicSeeker.Tests.PlayHistoryReadModelTests',
    'BeMusicSeeker.Tests.PlaylistOperationNotificationOwnerTests',
    'BeMusicSeeker.Tests.PlaylistUrlAcquisitionOwnershipTests',
    'BeMusicSeeker.Tests.PlaylistUrlCompletionTests')
$functionalMethodLevelPreWaveClasses = @(
    # These I/O-heavy fixtures own a distinct temporary database and directory
    # per test. Keep them in the dedicated MethodLevel pre-wave to avoid
    # cross-shard I/O contention; their explicit non-parallel settings tests
    # remain protected by DoNotParallelize.
    'BeMusicSeeker.Tests.BmsLibraryFolderRenameRefreshTests',
    'BeMusicSeeker.Tests.BmsLibraryPendingPackageRegroupTests',
    'BeMusicSeeker.Tests.AppSchemaPreflightServiceTests',
    'BeMusicSeeker.Tests.BmsLibraryMaintenanceServiceTests',
    'BeMusicSeeker.Tests.BmsLibraryDuplicateServiceTests',
    'BeMusicSeeker.Tests.BmsPlaylistExternalLoadTests',
    'BeMusicSeeker.Tests.PlaylistViewPipelineTests')
$functionalTestClassShards = @(
    [pscustomobject]@{
        # This collectible ALC contract must run in a testhost that has never
        # initialized the shared WPF Application or resolved WPF resources.
        Name = 'bass-collectible-load-context'
        Workers = 1
        Classes = @(
            $functionalBassCollectibleLoadContextClass)
    },
    [pscustomobject]@{
        # Dedicated fixture groups consume one active worker per testhost.
        # Keep each short-lived external host at one worker while the remaining
        # host uses every logical processor. This bounded overlap was faster and
        # stable in repeated Functional runs; subtracting these workers would
        # leave the remaining host under-provisioned after the external hosts exit.
        Name = 'library-chart-classwide'
        Workers = 1
        Classes = @(
            'BeMusicSeeker.Tests.BmsLibraryInitializationServiceTests',
            'BeMusicSeeker.Tests.BmsLibraryZeroNoteRefreshTests',
            'BeMusicSeeker.Tests.ChartInfoMetadataTests')
    },
    [pscustomobject]@{
        Name = 'lr2-songdb-sync'
        Workers = 1
        Classes = @(
            'BeMusicSeeker.Tests.BmsLibraryLr2SongDbSyncTests')
    },
    [pscustomobject]@{
        # ClassLevel scope keeps each fixture serial while allowing these three
        # independently owned database/filesystem fixtures to run concurrently.
        Name = 'owned-db-file-class-level'
        Workers = 3
        Scope = 'ClassLevel'
        Classes = @(
            'BeMusicSeeker.Tests.BmsLibraryIrServiceTests',
            'BeMusicSeeker.Tests.PackageInstallWorkflowOwnerTests',
            'BeMusicSeeker.Tests.Lr2SongDbSyncServiceTests')
    },
    [pscustomobject]@{
        Name = 'owned-chart-collection'
        Workers = 1
        Classes = @(
            'BeMusicSeeker.Tests.OwnedChartCollectionStateTests')
    },
    [pscustomobject]@{
        Name = 'playlist-update'
        Workers = 1
        Classes = @(
            'BeMusicSeeker.Tests.BmsPlaylistUpdateTests')
    },
    [pscustomobject]@{
        Name = 'presentation-workspace'
        Workers = 1
        Classes = @(
            'BeMusicSeeker.Tests.PlaybackPanelViewModelTests',
            'BeMusicSeeker.Tests.PlaylistWorkspaceViewModelTests',
            'BeMusicSeeker.Tests.LibraryFolderTreeViewModelTests')
    },
    [pscustomobject]@{
        # These fixtures share application/settings/presentation state that is
        # not isolated per class. ClassLevel serializes each fixture while the
        # dedicated testhost remains concurrent with the other hosts.
        Name = 'settings-presentation-classwide'
        Workers = 1
        Scope = 'ClassLevel'
        Classes = $functionalSettingsPresentationClasswideClasses
    },
    [pscustomobject]@{
        # These fixtures mutate process-global native or logging lifecycle
        # state and therefore require a single serial host.
        Name = 'process-global-lifecycle'
        Workers = 1
        Scope = 'ClassLevel'
        Classes = $functionalProcessGlobalLifecycleClasses
    },
    [pscustomobject]@{
        # This is a Functional topology group for measured headroom and
        # owner-local resource isolation. Five members retain class-wide
        # DoNotParallelize safety boundaries for arbitrary Quick filters;
        # the remaining members are isolated here by the one-worker host.
        Name = 'feature-process-global-state'
        Workers = 1
        Scope = 'ClassLevel'
        Classes = $functionalFeatureProcessGlobalStateClasses
    })
$functionalExclusiveTestClasses = @(
    # This test temporarily replaces the repository-local portable user.config.
    # Run it before any testhost that could read settings from the same file.
    'BeMusicSeeker.Tests.PlayerPanelStateSettingsCompatibilityTests')
$functionalRemainingShardWorkers = [Math]::Max(
    1,
    [Environment]::ProcessorCount)

function Assert-FunctionalShardConfiguration {
    $names = @($functionalTestClassShards | ForEach-Object { $_.Name })
    if (@($names | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
        throw 'Functional test shard names must not be empty.'
    }
    if (($names | Sort-Object -Unique).Count -ne $names.Count) {
        throw 'Functional test shard names must be unique.'
    }

    $workers = @($functionalTestClassShards | ForEach-Object { $_.Workers })
    if (@($workers | Where-Object { $_ -isnot [int] -or $_ -lt 1 }).Count -gt 0) {
        throw 'Functional test shard workers must be positive integers.'
    }
    $ownedDbFileShards = @($functionalTestClassShards |
        Where-Object { $_.Name -ceq 'owned-db-file-class-level' })
    if ($ownedDbFileShards.Count -ne 1) {
        throw 'Functional owned database/file tests must have exactly one dedicated shard.'
    }
    $ownedDbFileShard = $ownedDbFileShards[0]
    $requiredOwnedDbFileClasses = @(
        'BeMusicSeeker.Tests.BmsLibraryIrServiceTests',
        'BeMusicSeeker.Tests.PackageInstallWorkflowOwnerTests',
        'BeMusicSeeker.Tests.Lr2SongDbSyncServiceTests')
    $ownedDbFileClasses = @($ownedDbFileShard.Classes)
    if ($ownedDbFileClasses.Count -ne $requiredOwnedDbFileClasses.Count -or
        @(Compare-Object `
            -ReferenceObject $requiredOwnedDbFileClasses `
            -DifferenceObject $ownedDbFileClasses `
            -CaseSensitive).Count -ne 0) {
        throw 'Functional owned database/file shard must contain exactly the approved three test classes.'
    }
    if ($ownedDbFileShard.Workers -ne 3 -or $ownedDbFileShard.Scope -cne 'ClassLevel') {
        throw 'Functional owned database/file shard must use three workers with ClassLevel scope.'
    }
    if (@($functionalTestClassShards |
        Where-Object {
            $_.Name -cne 'owned-db-file-class-level' -and
            $_.Workers -ne 1 }).Count -gt 0) {
        throw 'All other Functional external test shards must use exactly one worker.'
    }
    if ($functionalRemainingShardWorkers -ne
        [Math]::Max(1, [Environment]::ProcessorCount)) {
        throw 'Functional remaining test shard must use every logical processor.'
    }

    if (@($functionalTestClassShards | Where-Object { @($_.Classes).Count -eq 0 }).Count -gt 0) {
        throw 'Functional test shards must contain at least one class selector.'
    }
    $shardClasses = @($functionalTestClassShards | ForEach-Object { $_.Classes })
    if (@($shardClasses | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
        throw 'Functional test shard class selectors must not be empty.'
    }
    if (($shardClasses | Sort-Object -Unique).Count -ne $shardClasses.Count) {
        throw 'Functional test shard classes must belong to exactly one shard.'
    }

    $exactSingleWorkerClassLevelShards = @(
        [pscustomobject]@{
            Name = 'settings-presentation-classwide'
            Classes = @(
                'BeMusicSeeker.Tests.ApplicationCompositionTests',
                'BeMusicSeeker.Tests.ApplicationSettingsLifecycleTests',
                'BeMusicSeeker.Tests.ApplicationUiSchedulerBoundaryTests',
                'BeMusicSeeker.Tests.BeatorajaBmtOptionsSnapshotTests',
                'BeMusicSeeker.Tests.BmsLibraryOptionsSnapshotTests',
                'BeMusicSeeker.Tests.CustomFolderOutputSettingsSnapshotTests',
                'BeMusicSeeker.Tests.MainWindowViewSettingsBoundaryTests',
                'BeMusicSeeker.Tests.PlayerSettingsGatewayTests',
                'BeMusicSeeker.Tests.PlaylistUrlCompletionOptionsSnapshotTests',
                'BeMusicSeeker.Tests.ResourceIconContractTests',
                'BeMusicSeeker.Tests.SettingDialogCustomFolderOutputBaseTests',
                'BeMusicSeeker.Tests.SettingDialogEditCompletionTests',
                'BeMusicSeeker.Tests.SettingDialogOpenCommandTests',
                'BeMusicSeeker.Tests.SettingsWindowPresentationTests',
                'BeMusicSeeker.Tests.ShellShutdownWorkflowOwnerTests',
                'BeMusicSeeker.Tests.StartupSettingsSnapshotTests')
        },
        [pscustomobject]@{
            Name = 'process-global-lifecycle'
            Classes = @(
                'BeMusicSeeker.Tests.BassNativeRuntimeTests',
                'BeMusicSeeker.Tests.NLogWrapperTests')
        },
        [pscustomobject]@{
            Name = 'feature-process-global-state'
            Classes = @(
                'BeMusicSeeker.Tests.AudioContractsTests',
                'BeMusicSeeker.Tests.AudioDeviceTestWorkflowOwnerTests',
                'BeMusicSeeker.Tests.BmsLibraryInstallEstimationServiceTests',
                'BeMusicSeeker.Tests.CatalogMutationOwnerTests',
                'BeMusicSeeker.Tests.ChartListVirtualViewTests',
                'BeMusicSeeker.Tests.InstallDestinationStateOwnerTests',
                'BeMusicSeeker.Tests.InstalledOnlyResourceOverwriteValidationTests',
                'BeMusicSeeker.Tests.LibraryFileScanPipelineOwnerTests',
                'BeMusicSeeker.Tests.Lr2PlayHistorySchemaServiceTests',
                'BeMusicSeeker.Tests.Lr2PlayHistorySchemaUiTests',
                'BeMusicSeeker.Tests.MainWindowExternalShellTests',
                'BeMusicSeeker.Tests.MainWindowViewModelStartupProgressTests',
                'BeMusicSeeker.Tests.PlayHistoryReadModelTests',
                'BeMusicSeeker.Tests.PlaylistOperationNotificationOwnerTests',
                'BeMusicSeeker.Tests.PlaylistUrlAcquisitionOwnershipTests',
                'BeMusicSeeker.Tests.PlaylistUrlCompletionTests')
        })
    foreach ($requiredShard in $exactSingleWorkerClassLevelShards) {
        $matchingShards = @($functionalTestClassShards |
            Where-Object { $_.Name -ceq $requiredShard.Name })
        if ($matchingShards.Count -ne 1) {
            throw "Functional $($requiredShard.Name) tests must have exactly one dedicated shard."
        }
        $actualClasses = @($matchingShards[0].Classes)
        $requiredClasses = @($requiredShard.Classes)
        if ($actualClasses.Count -ne $requiredClasses.Count -or
            @(Compare-Object `
                -ReferenceObject $requiredClasses `
                -DifferenceObject $actualClasses `
                -CaseSensitive).Count -ne 0) {
            throw "Functional $($requiredShard.Name) shard must contain exactly its approved test classes."
        }
        if ($matchingShards[0].Workers -ne 1 -or $matchingShards[0].Scope -cne 'ClassLevel') {
            throw "Functional $($requiredShard.Name) shard must use one worker with ClassLevel scope."
        }
    }

    if (@($functionalMethodLevelPreWaveClasses).Count -eq 0) {
        throw 'Functional method-level pre-wave must contain at least one class selector.'
    }
    if (@($functionalMethodLevelPreWaveClasses |
        Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
        throw 'Functional method-level pre-wave class selectors must not be empty.'
    }
    if (@($functionalMethodLevelPreWaveClasses | Sort-Object -Unique).Count -ne
        $functionalMethodLevelPreWaveClasses.Count) {
        throw 'Functional method-level pre-wave classes must be unique.'
    }
    $requiredMethodLevelPreWaveClasses = @(
        'BeMusicSeeker.Tests.BmsLibraryFolderRenameRefreshTests',
        'BeMusicSeeker.Tests.BmsLibraryPendingPackageRegroupTests',
        'BeMusicSeeker.Tests.AppSchemaPreflightServiceTests',
        'BeMusicSeeker.Tests.BmsLibraryMaintenanceServiceTests',
        'BeMusicSeeker.Tests.BmsLibraryDuplicateServiceTests',
        'BeMusicSeeker.Tests.BmsPlaylistExternalLoadTests',
        'BeMusicSeeker.Tests.PlaylistViewPipelineTests')
    if ($functionalMethodLevelPreWaveClasses.Count -ne $requiredMethodLevelPreWaveClasses.Count -or
        @(Compare-Object `
            -ReferenceObject $requiredMethodLevelPreWaveClasses `
            -DifferenceObject $functionalMethodLevelPreWaveClasses `
            -CaseSensitive).Count -ne 0) {
        throw 'Functional method-level pre-wave must contain exactly its approved seven test classes.'
    }

    $bassCollectibleShards = @($functionalTestClassShards |
        Where-Object { $_.Name -eq 'bass-collectible-load-context' })
    if ($bassCollectibleShards.Count -ne 1) {
        throw 'Functional BASS collectible load-context tests must have exactly one dedicated shard.'
    }
    $bassCollectibleClasses = @($bassCollectibleShards[0].Classes)
    if ($bassCollectibleClasses.Count -ne 1 -or
        $bassCollectibleClasses[0] -cne $functionalBassCollectibleLoadContextClass) {
        throw 'Functional BASS collectible load-context shard must contain only BassCollectibleLoadContextTests.'
    }

    if (@($functionalExclusiveTestClasses).Count -eq 0) {
        throw 'Functional exclusive tests must contain at least one class selector.'
    }
    if (@($functionalExclusiveTestClasses |
        Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
        throw 'Functional exclusive test class selectors must not be empty.'
    }
    if (@($functionalExclusiveTestClasses | Sort-Object -Unique).Count -ne
        $functionalExclusiveTestClasses.Count) {
        throw 'Functional exclusive test classes must be unique.'
    }

    $allClasses = @($shardClasses) +
        @($functionalExclusiveTestClasses) +
        @($functionalMethodLevelPreWaveClasses)
    for ($leftIndex = 0; $leftIndex -lt $allClasses.Count; $leftIndex++) {
        for ($rightIndex = $leftIndex + 1; $rightIndex -lt $allClasses.Count; $rightIndex++) {
            $left = $allClasses[$leftIndex]
            $right = $allClasses[$rightIndex]
            if ($left.Contains($right, [StringComparison]::Ordinal) -or
                $right.Contains($left, [StringComparison]::Ordinal)) {
                throw "Functional test class selectors overlap: '$left' and '$right'."
            }
        }
    }
}
. (Join-Path $repoRoot 'scripts\portable-package-layout.ps1')

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)]
        [string]$Command,

        [Parameter(ValueFromRemainingArguments)]
        [string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $Command $($Arguments -join ' ')"
    }
}

function Get-TrackedWorkingTreeFingerprint {
    $diff = @(& git -C $repoRoot -c core.autocrlf=false diff --binary --full-index HEAD --)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to fingerprint tracked files (exit code $LASTEXITCODE)."
    }

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($diff -join "`n")
    return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes))
}

function Get-RemainingBudgetSeconds {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Stopwatch]$Stopwatch,

        [Parameter(Mandatory)]
        [int]$BudgetSeconds
    )

    $remaining = $BudgetSeconds `
        - $functionalCleanupReserveSeconds `
        - $Stopwatch.Elapsed.TotalSeconds
    if ($remaining -le 0) {
        throw "Functional verification cannot retain the ${functionalCleanupReserveSeconds}-second cleanup reserve within the $BudgetSeconds-second command budget."
    }

    return [Math]::Max(1, [int][Math]::Floor($remaining))
}

function Write-TestDiagnosticSummary {
    param(
        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory,

        [Parameter(Mandatory)]
        [string]$StandardOutputPath
    )

    $sequenceFiles = @(
        Get-ChildItem -LiteralPath $DiagnosticsDirectory -Filter 'Sequence*.xml' -File -Recurse -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending)
    if ($sequenceFiles.Count -gt 0) {
        Write-Warning "Blame sequence (active/last tests): $($sequenceFiles[0].FullName)"
        try {
            [xml]$sequence = Get-Content -LiteralPath $sequenceFiles[0].FullName -Raw
            $testNames = @($sequence.SelectNodes('//Test') | ForEach-Object { $_.Name })
            if ($testNames.Count -gt 0) {
                Write-Warning "Last blame-observed tests:`n$($testNames | Select-Object -Last 10 | ForEach-Object { '  ' + $_ } | Out-String)"
            }
        }
        catch {
            Write-Warning "Unable to parse blame sequence: $($_.Exception.Message)"
        }
    }

    if (Test-Path -LiteralPath $StandardOutputPath -PathType Leaf) {
        $lastOutput = @(Get-Content -LiteralPath $StandardOutputPath | Select-Object -Last 30)
        if ($lastOutput.Count -gt 0) {
            Write-Warning "Last test output:`n$($lastOutput -join [Environment]::NewLine)"
        }
    }
}

function Invoke-MonitoredCommand {
    param(
        [Parameter(Mandatory)]
        [string]$Label,

        [Parameter(Mandatory)]
        [string]$CommandPath,

        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$WorkingDirectory,

        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory,

        [Parameter(Mandatory)]
        [int]$TimeoutSeconds,

        [switch]$IsTestCommand
    )

    [void](New-Item -ItemType Directory -Path $DiagnosticsDirectory -Force)
    $standardOutputPath = Join-Path $DiagnosticsDirectory 'stdout.log'
    $standardErrorPath = Join-Path $DiagnosticsDirectory 'stderr.log'
    $stageStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $process = [System.Diagnostics.Process]::new()
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $CommandPath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }
    $process.StartInfo = $startInfo

    $timedOut = $false
    $exitCode = $null
    $standardOutput = [string]::Empty
    $standardError = [string]::Empty
    try {
        Write-Host "$Label (timeout ${TimeoutSeconds}s): $CommandPath $($Arguments -join ' ')"
        if (-not $process.Start()) {
            throw "Unable to start ${Label}: $CommandPath"
        }

        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $timedOut = $true
            Write-Warning "$Label exceeded ${TimeoutSeconds}s after $([Math]::Round($stageStopwatch.Elapsed.TotalSeconds, 1))s. Stopping process tree PID $($process.Id)."
            try {
                $process.Kill($true)
            }
            catch {
                Write-Warning "Managed process-tree termination failed: $($_.Exception.Message)"
                & taskkill.exe /PID $process.Id /T /F 2>$null | Out-Null
            }
            if (-not $process.WaitForExit(10000)) {
                throw "$Label process tree did not exit within the 10-second cleanup window."
            }
        }
        else {
            $process.WaitForExit()
            $exitCode = $process.ExitCode
        }

        $standardOutput = $standardOutputTask.GetAwaiter().GetResult()
        $standardError = $standardErrorTask.GetAwaiter().GetResult()
    }
    finally {
        $stageStopwatch.Stop()
        [System.IO.File]::WriteAllText(
            $standardOutputPath,
            $standardOutput,
            [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText(
            $standardErrorPath,
            $standardError,
            [System.Text.UTF8Encoding]::new($false))
        $process.Dispose()
    }

    if (-not [string]::IsNullOrEmpty($standardOutput)) {
        Write-Host $standardOutput -NoNewline
    }
    if (-not [string]::IsNullOrEmpty($standardError)) {
        if (-not $timedOut -and $exitCode -eq 0) {
            Write-Warning $standardError.TrimEnd()
        }
        else {
            Write-Error $standardError -ErrorAction Continue
        }
    }

    Write-Host "$Label elapsed: $([Math]::Round($stageStopwatch.Elapsed.TotalSeconds, 1))s; diagnostics: $DiagnosticsDirectory"
    if ($timedOut -or $exitCode -ne 0) {
        if ($IsTestCommand) {
            Write-TestDiagnosticSummary `
                -DiagnosticsDirectory $DiagnosticsDirectory `
                -StandardOutputPath $standardOutputPath
        }
        if ($timedOut) {
            throw "$Label exceeded the ${TimeoutSeconds}-second timeout. Diagnostics: $DiagnosticsDirectory"
        }
        throw "$Label failed with exit code $exitCode. Diagnostics: $DiagnosticsDirectory"
    }
}

function Invoke-BudgetedCommand {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Stopwatch]$Stopwatch,

        [Parameter(Mandatory)]
        [int]$BudgetSeconds,

        [Parameter(Mandatory)]
        [string]$Label,

        [Parameter(Mandatory)]
        [string]$CommandPath,

        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory,

        [switch]$IsTestCommand
    )

    $remainingSeconds = Get-RemainingBudgetSeconds -Stopwatch $Stopwatch -BudgetSeconds $BudgetSeconds
    Invoke-MonitoredCommand `
        -Label $Label `
        -CommandPath $CommandPath `
        -Arguments $Arguments `
        -WorkingDirectory $repoRoot `
        -DiagnosticsDirectory $DiagnosticsDirectory `
        -TimeoutSeconds $remainingSeconds `
        -IsTestCommand:$IsTestCommand
}

function Write-MSTestParallelRunSettings {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Workers,

        [ValidateSet('ClassLevel', 'MethodLevel')]
        [string]$Scope = 'ClassLevel'
    )

    $document = [System.Xml.XmlDocument]::new()
    $document.LoadXml(@"
<RunSettings>
  <MSTest>
    <Parallelize>
      <Workers>$Workers</Workers>
      <Scope>$Scope</Scope>
    </Parallelize>
  </MSTest>
</RunSettings>
"@)
    $writerSettings = [System.Xml.XmlWriterSettings]::new()
    $writerSettings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $writerSettings.Indent = $true
    $writer = [System.Xml.XmlWriter]::Create($Path, $writerSettings)
    try {
        $document.Save($writer)
    }
    finally {
        $writer.Dispose()
    }
}

function Get-TestArguments {
    param(
        [Parameter(Mandatory)]
        [string]$Filter,

        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory,

        [Parameter(Mandatory)]
        [int]$TimeoutSeconds,

        [string]$RunSettingsPath,

        [switch]$NoBuild
    )

    $arguments = @(
        'test',
        $solution,
        '/p:Configuration=Release',
        '/p:Platform=x64',
        '--no-restore',
        '--results-directory',
        $DiagnosticsDirectory,
        '--logger',
        'trx;LogFileName=results.trx',
        '--logger',
        'console;verbosity=normal',
        '--blame-crash',
        '--blame-hang',
        '--blame-hang-timeout',
        "${testHangTimeoutSeconds}s",
        '--blame-hang-dump-type',
        'mini',
        '--filter',
        $Filter)
    if (-not [string]::IsNullOrWhiteSpace($RunSettingsPath)) {
        $arguments += @('--settings', $RunSettingsPath)
    }
    if ($NoBuild) {
        $arguments += '--no-build'
    }
    return $arguments
}

function Invoke-TestLane {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$Filter,

        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory,

        [int]$TimeoutSeconds = 180,

        [string]$RunSettingsPath,

        [switch]$NoBuild
    )

    $arguments = Get-TestArguments `
        -Filter $Filter `
        -DiagnosticsDirectory $DiagnosticsDirectory `
        -TimeoutSeconds $TimeoutSeconds `
        -RunSettingsPath $RunSettingsPath `
        -NoBuild:$NoBuild
    Invoke-MonitoredCommand `
        -Label "$Name test lane" `
        -CommandPath 'dotnet' `
        -Arguments $arguments `
        -WorkingDirectory $repoRoot `
        -DiagnosticsDirectory $DiagnosticsDirectory `
        -TimeoutSeconds $TimeoutSeconds `
        -IsTestCommand
}

function Invoke-ParallelFunctionalTestShards {
    param(
        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory,

        [Parameter(Mandatory)]
        [int]$TimeoutSeconds
    )

    Assert-FunctionalShardConfiguration
    $assignedClasses = @(
        @($functionalTestClassShards | ForEach-Object { $_.Classes }) +
        @($functionalExclusiveTestClasses) +
        @($functionalMethodLevelPreWaveClasses))
    $remainingClassFilter = ($assignedClasses |
        ForEach-Object { "FullyQualifiedName!~$_" }) -join '&'
    $shards = @(
        [pscustomobject]@{
            Name = 'remaining'
            Filter = "($functionalFilter)&($remainingClassFilter)"
            Workers = $functionalRemainingShardWorkers
            Scope = 'ClassLevel'
        })
    $shards += @(
        $functionalTestClassShards | ForEach-Object {
            $classFilter = ($_.Classes |
                ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
            $scope = if ($_.PSObject.Properties.Name -contains 'Scope') {
                $_.Scope
            }
            else {
                'ClassLevel'
            }
            [pscustomobject]@{
                Name = $_.Name
                Filter = "($functionalFilter)&($classFilter)"
                Workers = $_.Workers
                Scope = $scope
            }
        })

    [void](New-Item -ItemType Directory -Path $DiagnosticsDirectory -Force)
    $stageStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $entries = @()
    $timedOut = $false
    $timedOutShardNames = @()
    $launchFailure = $null
    $failedShard = $null
    $failedShardName = $null
    $failedShardExitCode = $null
    $cleanupFailures = [System.Collections.Generic.List[string]]::new()

    try {
        $exclusiveDirectory = Join-Path $DiagnosticsDirectory 'exclusive-portable-settings'
        $exclusiveClassFilter = ($functionalExclusiveTestClasses |
            ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
        $exclusiveTimeoutSeconds = [Math]::Max(
            1,
            [int][Math]::Floor($TimeoutSeconds - $stageStopwatch.Elapsed.TotalSeconds))
        Invoke-TestLane `
            -Name 'Functional exclusive portable settings' `
            -Filter "($functionalFilter)&($exclusiveClassFilter)" `
            -DiagnosticsDirectory $exclusiveDirectory `
            -TimeoutSeconds $exclusiveTimeoutSeconds `
            -NoBuild

        $preWaveDirectory = Join-Path $DiagnosticsDirectory 'method-level-pre-wave'
        [void](New-Item -ItemType Directory -Path $preWaveDirectory -Force)
        $preWaveRunSettingsPath = Join-Path $preWaveDirectory 'parallel.runsettings'
        Write-MSTestParallelRunSettings `
            -Path $preWaveRunSettingsPath `
            -Workers $functionalRemainingShardWorkers `
            -Scope 'MethodLevel'
        $preWaveClassFilter = ($functionalMethodLevelPreWaveClasses |
            ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
        $preWaveTimeoutSeconds = [int][Math]::Floor(
            $TimeoutSeconds - $stageStopwatch.Elapsed.TotalSeconds)
        if ($preWaveTimeoutSeconds -le 0) {
            throw "Functional test phase exhausted its ${TimeoutSeconds}-second timeout before the method-level pre-wave."
        }
        Invoke-TestLane `
            -Name 'Functional method-level pre-wave' `
            -Filter "($functionalFilter)&($preWaveClassFilter)" `
            -DiagnosticsDirectory $preWaveDirectory `
            -TimeoutSeconds $preWaveTimeoutSeconds `
            -RunSettingsPath $preWaveRunSettingsPath `
            -NoBuild

        foreach ($shard in $shards) {
            $shardDirectory = Join-Path $DiagnosticsDirectory $shard.Name
            [void](New-Item -ItemType Directory -Path $shardDirectory -Force)
            $runSettingsPath = Join-Path $shardDirectory 'parallel.runsettings'
            Write-MSTestParallelRunSettings `
                -Path $runSettingsPath `
                -Workers $shard.Workers `
                -Scope $shard.Scope
            $arguments = Get-TestArguments `
                -Filter $shard.Filter `
                -DiagnosticsDirectory $shardDirectory `
                -TimeoutSeconds $TimeoutSeconds `
                -RunSettingsPath $runSettingsPath `
                -NoBuild
            $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
            $startInfo.FileName = 'dotnet'
            $startInfo.WorkingDirectory = $repoRoot
            $startInfo.UseShellExecute = $false
            $startInfo.CreateNoWindow = $true
            $startInfo.RedirectStandardOutput = $true
            $startInfo.RedirectStandardError = $true
            foreach ($argument in $arguments) {
                [void]$startInfo.ArgumentList.Add($argument)
            }
            $process = [System.Diagnostics.Process]::new()
            $process.StartInfo = $startInfo
            if (-not $process.Start()) {
                throw "Unable to start functional test shard '$($shard.Name)'."
            }
            $entries += [pscustomobject]@{
                Name = $shard.Name
                Directory = $shardDirectory
                Process = $process
                ProcessId = $process.Id
                StandardOutputTask = $process.StandardOutput.ReadToEndAsync()
                StandardErrorTask = $process.StandardError.ReadToEndAsync()
            }
        }

        $shardSummary = ($shards | ForEach-Object {
            "$($_.Name)=$($_.Workers) workers"
        }) -join ', '
        Write-Host "Functional test shards (global timeout ${TimeoutSeconds}s): $shardSummary"
        while ($true) {
            $failedShard = $entries |
                Where-Object { $_.Process.HasExited -and $_.Process.ExitCode -ne 0 } |
                Select-Object -First 1
            if ($null -ne $failedShard) {
                break
            }

            $runningEntries = @($entries | Where-Object { -not $_.Process.HasExited })
            if ($runningEntries.Count -eq 0) {
                break
            }
            if ($stageStopwatch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                $timedOut = $true
                $timedOutShardNames = @($runningEntries | ForEach-Object { $_.Name })
                Write-Warning "Functional test phase exceeded ${TimeoutSeconds}s. Stopping shards: $($timedOutShardNames -join ', ')"
                break
            }
            Start-Sleep -Milliseconds 100
        }
    }
    catch {
        $launchFailure = $_
    }
    finally {
        $fallbackProcesses = @()
        if ($null -ne $failedShard) {
            $failedShardName = $failedShard.Name
            $failedShardExitCode = $failedShard.Process.ExitCode
        }
        if ($timedOut -or $null -ne $failedShard -or $null -ne $launchFailure) {
            $runningEntries = @($entries | Where-Object { -not $_.Process.HasExited })
            foreach ($entry in $runningEntries) {
                try {
                    $entry.Process.Kill($true)
                }
                catch {
                    Write-Warning "$($entry.Name): managed process-tree termination failed for PID $($entry.ProcessId): $($_.Exception.Message)"
                    try {
                        $taskkillStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
                        $taskkillStartInfo.FileName = 'taskkill.exe'
                        $taskkillStartInfo.UseShellExecute = $false
                        $taskkillStartInfo.CreateNoWindow = $true
                        foreach ($argument in @('/PID', [string]$entry.ProcessId, '/T', '/F')) {
                            [void]$taskkillStartInfo.ArgumentList.Add($argument)
                        }
                        $taskkillProcess = [System.Diagnostics.Process]::new()
                        $taskkillProcess.StartInfo = $taskkillStartInfo
                        if (-not $taskkillProcess.Start()) {
                            throw "Unable to start taskkill.exe for PID $($entry.ProcessId)."
                        }
                        $fallbackProcesses += [pscustomobject]@{
                            Name = $entry.Name
                            Process = $taskkillProcess
                            ProcessId = $taskkillProcess.Id
                            TargetProcessId = $entry.ProcessId
                        }
                    }
                    catch {
                        $cleanupFailures.Add(
                            "$($entry.Name): taskkill fallback could not start for PID $($entry.ProcessId): $($_.Exception.Message)")
                    }
                }
            }

            $cleanupStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            while ($cleanupStopwatch.Elapsed.TotalSeconds -lt $functionalProcessCleanupSeconds) {
                $activeEntries = @($runningEntries | Where-Object { -not $_.Process.HasExited })
                $activeFallbacks = @($fallbackProcesses | Where-Object { -not $_.Process.HasExited })
                if ($activeEntries.Count -eq 0 -and $activeFallbacks.Count -eq 0) {
                    break
                }
                Start-Sleep -Milliseconds 50
            }
            $cleanupStopwatch.Stop()

            $activeEntriesAtDeadline = @(
                $runningEntries | Where-Object { -not $_.Process.HasExited })
            foreach ($entry in $activeEntriesAtDeadline) {
                $cleanupFailures.Add(
                    "$($entry.Name): PID $($entry.ProcessId) remained active after the shared ${functionalProcessCleanupSeconds}-second process cleanup deadline")
            }
            $activeFallbacksAtDeadline = @(
                $fallbackProcesses | Where-Object { -not $_.Process.HasExited })
            foreach ($fallback in $activeFallbacksAtDeadline) {
                $cleanupFailures.Add(
                    "$($fallback.Name): taskkill helper PID $($fallback.ProcessId) for target PID $($fallback.TargetProcessId) remained active after the shared ${functionalProcessCleanupSeconds}-second process cleanup deadline")
                try {
                    $fallback.Process.Kill($true)
                }
                catch {
                    $cleanupFailures.Add(
                        "$($fallback.Name): taskkill helper for PID $($fallback.TargetProcessId) could not be stopped: $($_.Exception.Message)")
                }
            }

            $helperCleanupMilliseconds = [Math]::Max(
                0,
                [int](($functionalCleanupReserveSeconds - $cleanupStopwatch.Elapsed.TotalSeconds) * 1000))
            $helperCleanupStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            while ($helperCleanupStopwatch.ElapsedMilliseconds -lt $helperCleanupMilliseconds) {
                $activeFallbacks = @(
                    $activeFallbacksAtDeadline | Where-Object { -not $_.Process.HasExited })
                if ($activeFallbacks.Count -eq 0) {
                    break
                }
                Start-Sleep -Milliseconds 50
            }
            $helperCleanupStopwatch.Stop()
            foreach ($fallback in $activeFallbacksAtDeadline | Where-Object { -not $_.Process.HasExited }) {
                $cleanupFailures.Add(
                    "$($fallback.Name): taskkill helper PID $($fallback.ProcessId) for target PID $($fallback.TargetProcessId) remained active after the shared ${functionalCleanupReserveSeconds}-second cleanup reserve")
            }
        }

        foreach ($fallback in $fallbackProcesses) {
            try {
                if (-not $fallback.Process.HasExited) {
                    continue
                }
                $fallback.Process.WaitForExit()
            }
            finally {
                if ($fallback.Process.HasExited) {
                    $fallback.Process.Dispose()
                }
            }
        }

        foreach ($entry in $entries) {
            try {
                if (-not $entry.Process.HasExited) {
                    continue
                }
                $entry.Process.WaitForExit()
                $standardOutput = $entry.StandardOutputTask.GetAwaiter().GetResult()
                $standardError = $entry.StandardErrorTask.GetAwaiter().GetResult()
                $standardOutputPath = Join-Path $entry.Directory 'stdout.log'
                $standardErrorPath = Join-Path $entry.Directory 'stderr.log'
                [System.IO.File]::WriteAllText(
                    $standardOutputPath,
                    $standardOutput,
                    [System.Text.UTF8Encoding]::new($false))
                [System.IO.File]::WriteAllText(
                    $standardErrorPath,
                    $standardError,
                    [System.Text.UTF8Encoding]::new($false))

                Write-Host "Test shard: $($entry.Name); exit code: $($entry.Process.ExitCode)"
                if (-not $timedOut -and -not [string]::IsNullOrEmpty($standardOutput)) {
                    Write-Host $standardOutput -NoNewline
                }
                if (-not [string]::IsNullOrEmpty($standardError)) {
                    if (-not $timedOut -and $entry.Process.ExitCode -eq 0) {
                        Write-Warning $standardError.TrimEnd()
                    }
                    else {
                        Write-Error $standardError -ErrorAction Continue
                    }
                }

                if (($timedOutShardNames -contains $entry.Name) -or $entry.Process.ExitCode -ne 0) {
                    Write-TestDiagnosticSummary `
                        -DiagnosticsDirectory $entry.Directory `
                        -StandardOutputPath $standardOutputPath
                }
            }
            catch {
                $cleanupFailures.Add(
                    "$($entry.Name): cleanup/output collection failed: $($_.Exception.Message)")
            }
            finally {
                $entry.Process.Dispose()
            }
        }
        $stageStopwatch.Stop()
    }

    Write-Host "Functional test phase elapsed: $([Math]::Round($stageStopwatch.Elapsed.TotalSeconds, 1))s; diagnostics: $DiagnosticsDirectory"
    if ($cleanupFailures.Count -gt 0) {
        throw "Functional test shard cleanup failed: $($cleanupFailures -join '; ')"
    }
    if ($null -ne $launchFailure) {
        throw $launchFailure
    }
    if ($timedOut) {
        throw "Functional test phase exceeded the ${TimeoutSeconds}-second timeout. Diagnostics: $DiagnosticsDirectory"
    }
    if ($null -ne $failedShardName) {
        throw "Functional test shard '$failedShardName' failed with exit code $failedShardExitCode. Diagnostics: $(Join-Path $DiagnosticsDirectory $failedShardName)"
    }
}

function Assert-ReleaseOutputLayout {
    param(
        [Parameter(Mandatory)]
        [string]$ExecutablePath
    )

    $outputDirectory = Split-Path -Parent $ExecutablePath
    foreach ($hostFileName in @(
        'BeMusicSeeker.deps.json',
        'BeMusicSeeker.runtimeconfig.json',
        'Livet.Core.dll',
        'Livet.EventListeners.dll',
        'Livet.Messaging.dll',
        'Livet.Mvvm.dll',
        'Microsoft.Xaml.Behaviors.dll',
        'Newtonsoft.Json.dll',
        'NLog.dll',
        'SevenZipExtractor.dll')) {
        $hostFilePath = Join-Path $outputDirectory $hostFileName
        if (-not (Test-Path -LiteralPath $hostFilePath -PathType Leaf)) {
            throw "Release output host file is missing: $hostFilePath"
        }
    }

    foreach ($removedNLogAddon in @('NLog.Database.dll', 'NLog.WindowsEventLog.dll')) {
        $removedNLogAddonPath = Join-Path $outputDirectory $removedNLogAddon
        if (Test-Path -LiteralPath $removedNLogAddonPath -PathType Leaf) {
            throw "Release output contains removed NLog addon: $removedNLogAddonPath"
        }
    }

    foreach ($removedWpfLegacyAssembly in @(
        'System.Windows.Interactivity.dll',
        'Microsoft.Expression.Interactions.dll',
        'Microsoft.Expression.Drawing.dll',
        'Microsoft.Expression.Effects.dll',
        'MetroRadiance.dll',
        'MetroRadiance.Core.dll',
        'MetroRadiance.Chrome.dll',
        'Microsoft.WindowsAPICodePack.dll',
        'Microsoft.WindowsAPICodePack.Shell.dll',
        'QuickConverter.dll')) {
        $removedWpfLegacyAssemblyPath = Join-Path $outputDirectory $removedWpfLegacyAssembly
        if (Test-Path -LiteralPath $removedWpfLegacyAssemblyPath -PathType Leaf) {
            throw "Release output contains retired WPF legacy assembly: $removedWpfLegacyAssemblyPath"
        }
    }
}

function Invoke-SelfContainedPublishVerification {
    if (Test-Path -LiteralPath $scdPublishRoot) {
        Remove-Item -LiteralPath $scdPublishRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $scdAppPublishOutput, $scdUpdaterPublishOutput -Force | Out-Null

    Invoke-CheckedCommand dotnet publish (Join-Path $repoRoot 'BeMusicSeeker.csproj') '/p:Configuration=Release' '/p:Platform=x64' '-r' 'win-x64' '--self-contained' 'true' '--no-restore' '-p:PublishProfile=WinX64SelfContained' "-p:PublishDir=$scdAppPublishOutput"
    Invoke-CheckedCommand dotnet publish (Join-Path $repoRoot 'BeMusicSeeker.Updater\BeMusicSeeker.Updater.csproj') '/p:Configuration=Release' '/p:Platform=x64' '-r' 'win-x64' '--self-contained' 'true' '--no-restore' '-p:PublishProfile=WinX64SelfContainedSingleFile' "-p:PublishDir=$scdUpdaterPublishOutput"

    foreach ($requiredPath in Get-PortableMainAppRequiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $scdAppPublishOutput $requiredPath) -PathType Leaf)) {
            throw "Self-contained app publish output is missing: $requiredPath"
        }
    }

    foreach ($forbiddenPath in @(
        'BeMusicSeeker.dll',
        'BeMusicSeeker.deps.json',
        'BeMusicSeeker.runtimeconfig.json')) {
        if (Test-Path -LiteralPath (Join-Path $scdAppPublishOutput $forbiddenPath) -PathType Leaf) {
            throw "Single-file app publish output contains a companion file: $forbiddenPath"
        }
    }
    $requiredSdkNativeRootFiles = [System.Collections.Generic.HashSet[string]]::new(
        [string[]](Get-PortableRequiredSdkNativeRootFiles),
        [System.StringComparer]::OrdinalIgnoreCase)
    $unexpectedRootDlls = @(
        Get-ChildItem -LiteralPath $scdAppPublishOutput -File -Filter '*.dll' |
            Where-Object { -not $requiredSdkNativeRootFiles.Contains($_.Name) }
    )
    if ($unexpectedRootDlls.Count -gt 0) {
        throw "Self-contained app publish output contains an unknown root DLL: $($unexpectedRootDlls.Name -join ', ')"
    }

    $updaterExecutable = Join-Path $scdUpdaterPublishOutput 'BeMusicSeeker.Updater.exe'
    if (-not (Test-Path -LiteralPath $updaterExecutable -PathType Leaf)) {
        throw "Self-contained updater publish output is missing: $updaterExecutable"
    }
    foreach ($companion in @(
        'BeMusicSeeker.Updater.dll',
        'BeMusicSeeker.Updater.deps.json',
        'BeMusicSeeker.Updater.runtimeconfig.json')) {
        if (Test-Path -LiteralPath (Join-Path $scdUpdaterPublishOutput $companion)) {
            throw "Single-file updater publish output contains a companion file: $companion"
        }
    }

    $versionProcess = Start-Process -FilePath $updaterExecutable -WorkingDirectory $scdUpdaterPublishOutput -ArgumentList '--version' -PassThru -Wait -NoNewWindow
    if ($versionProcess.ExitCode -ne 0) {
        throw "Self-contained updater --version failed with exit code $($versionProcess.ExitCode)."
    }
}

function Invoke-ExistingDataAcceptance {
    if (-not (Test-Path -LiteralPath $existingDataAcceptanceScript -PathType Leaf)) {
        throw "Existing-data acceptance runner is missing: $existingDataAcceptanceScript"
    }
    $acceptanceOutputDirectory = Join-Path $verificationArtifactsDirectory 'net10-existing-data'
    Invoke-CheckedCommand pwsh '-NoProfile' '-File' $existingDataAcceptanceScript `
        '-AppPublishRoot' $scdAppPublishOutput `
        '-OutputDirectory' $acceptanceOutputDirectory
}

function Invoke-UpdateAcceptance {
    if (-not (Test-Path -LiteralPath $updateAcceptanceScript -PathType Leaf)) {
        throw "Update acceptance runner is missing: $updateAcceptanceScript"
    }
    $acceptanceOutputDirectory = Join-Path $verificationArtifactsDirectory 'net10-update'
    Invoke-CheckedCommand pwsh '-NoProfile' '-File' $updateAcceptanceScript `
        '-OutputDirectory' $acceptanceOutputDirectory
}

function Assert-RepositoryWhitespace {
    Invoke-CheckedCommand git diff '--check' 'HEAD' '--'

    $untrackedFiles = @(git ls-files --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to enumerate untracked files (exit code $LASTEXITCODE)."
    }

    if ($untrackedFiles.Count -eq 0) {
        return
    }

    $emptyFile = New-TemporaryFile
    try {
        foreach ($untrackedFile in $untrackedFiles) {
            $checkOutput = @(& git -c core.autocrlf=false -c core.whitespace=cr-at-eol diff --no-index --check -- $emptyFile.FullName $untrackedFile 2>&1)
            $checkExitCode = $LASTEXITCODE
            if ($checkOutput.Count -gt 0) {
                throw "Whitespace error in untracked file '$untrackedFile':`n$($checkOutput -join [Environment]::NewLine)"
            }
            if ($checkExitCode -gt 1) {
                throw "Unable to inspect untracked file '$untrackedFile' (exit code $checkExitCode)."
            }
            $global:LASTEXITCODE = 0
        }
    }
    finally {
        Remove-Item -LiteralPath $emptyFile.FullName -Force
    }
}

function Assert-BuiltOutputs {
    if (-not (Test-Path -LiteralPath $uiExecutable -PathType Leaf)) {
        throw "Release UI executable was not produced: $uiExecutable"
    }
    Assert-ReleaseOutputLayout -ExecutablePath (Resolve-Path -LiteralPath $uiExecutable).Path
}

function Invoke-StandaloneTestVerification {
    param(
        [Parameter(Mandatory)]
        [string]$Filter,

        [Parameter(Mandatory)]
        [string]$DiagnosticsRoot,

        [switch]$UseFunctionalShards
    )

    $budget = $FunctionalTimeoutSeconds
    Invoke-BudgetedCommand `
        -Stopwatch $commandStopwatch `
        -BudgetSeconds $budget `
        -Label 'Locked restore' `
        -CommandPath 'dotnet' `
        -Arguments @('restore', $solution, '-r', 'win-x64', '--locked-mode', '-p:PublishReadyToRun=true') `
        -DiagnosticsDirectory (Join-Path $DiagnosticsRoot 'restore')

    $testDirectory = Join-Path $DiagnosticsRoot 'functional'
    if ($UseFunctionalShards) {
        Invoke-BudgetedCommand `
            -Stopwatch $commandStopwatch `
            -BudgetSeconds $budget `
            -Label 'Functional build' `
            -CommandPath 'dotnet' `
            -Arguments @('build', $solution, '/p:Configuration=Release', '/p:Platform=x64', '--no-restore') `
            -DiagnosticsDirectory (Join-Path $DiagnosticsRoot 'build')
        Assert-BuiltOutputs
        $remaining = Get-RemainingBudgetSeconds -Stopwatch $commandStopwatch -BudgetSeconds $budget
        Invoke-ParallelFunctionalTestShards `
            -DiagnosticsDirectory $testDirectory `
            -TimeoutSeconds $remaining
    }
    else {
        [void](New-Item -ItemType Directory -Path $testDirectory -Force)
        $remaining = Get-RemainingBudgetSeconds -Stopwatch $commandStopwatch -BudgetSeconds $budget
        $testArguments = Get-TestArguments `
            -Filter $Filter `
            -DiagnosticsDirectory $testDirectory `
            -TimeoutSeconds $remaining
        Invoke-BudgetedCommand `
            -Stopwatch $commandStopwatch `
            -BudgetSeconds $budget `
            -Label 'Filtered build and test' `
            -CommandPath 'dotnet' `
            -Arguments $testArguments `
            -DiagnosticsDirectory $testDirectory `
            -IsTestCommand
        Assert-BuiltOutputs
    }

    Assert-RepositoryWhitespace

    if ($commandStopwatch.Elapsed.TotalSeconds -gt $budget) {
        throw "Functional verification exceeded the $budget-second command budget after repository checks."
    }
    Write-Host "Functional command elapsed: $([Math]::Round($commandStopwatch.Elapsed.TotalSeconds, 1))s / ${budget}s"
}

if ($Mode -eq 'Functional' -and -not [string]::IsNullOrWhiteSpace($TestFilter)) {
    throw 'Use Quick mode for a filtered test iteration. Functional mode always runs the canonical functional lane.'
}
if ($Mode -eq 'Full' -and -not [string]::IsNullOrWhiteSpace($TestFilter)) {
    throw 'Full mode does not accept TestFilter. Use Quick mode for an explicit opt-in lane.'
}

$trackedStateBefore = Get-TrackedWorkingTreeFingerprint
$verificationFailure = $null
$testDiagnosticsDirectory = Join-Path $verificationArtifactsDirectory (
    'tests-' + $Mode.ToLowerInvariant() + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
[void](New-Item -ItemType Directory -Path $testDiagnosticsDirectory -Force)

Push-Location $repoRoot
try {
    if ($Mode -eq 'Full') {
        Invoke-CheckedCommand dotnet restore $solution '-r' 'win-x64' '--locked-mode' '-p:PublishReadyToRun=true'
        Invoke-CheckedCommand dotnet tool restore
        Invoke-CheckedCommand dotnet build $solution '/p:Configuration=Release' '/p:Platform=x64' '--no-restore'

        foreach ($toolExecutable in $toolExecutables) {
            if (-not (Test-Path -LiteralPath $toolExecutable -PathType Leaf)) {
                throw "Release tool executable was not produced: $toolExecutable"
            }
            Invoke-CheckedCommand $toolExecutable '--help'
        }

        Assert-BuiltOutputs
        Invoke-ParallelFunctionalTestShards `
            -DiagnosticsDirectory (Join-Path $testDiagnosticsDirectory 'functional') `
            -TimeoutSeconds 180

        Write-Host "Self-contained publish verification output: $scdPublishRoot"
        Invoke-SelfContainedPublishVerification
        Invoke-ExistingDataAcceptance
        Invoke-UpdateAcceptance
        $env:BMS_SCD_APP_PUBLISH_ROOT = $scdAppPublishOutput
        $env:BMS_SCD_UPDATER_PUBLISH_ROOT = $scdUpdaterPublishOutput

        Invoke-TestLane `
            -Name 'Process integration' `
            -Filter 'TestCategory=ProcessIntegration' `
            -DiagnosticsDirectory (Join-Path $testDiagnosticsDirectory 'process-integration') `
            -NoBuild
        Invoke-TestLane `
            -Name 'Release acceptance' `
            -Filter 'TestCategory=ReleaseAcceptance' `
            -DiagnosticsDirectory (Join-Path $testDiagnosticsDirectory 'release-acceptance') `
            -NoBuild

        Invoke-CheckedCommand dotnet format whitespace $solution '--verify-no-changes' '--no-restore' '--verbosity' 'minimal'

        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (-not (Test-Path -LiteralPath $vswhere)) {
            throw "vswhere.exe was not found: $vswhere"
        }
        $msbuildPath = & $vswhere -version '[17.0,18.0)' -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin' | Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($msbuildPath)) {
            throw 'Visual Studio 2022 MSBuild 17 was not found.'
        }
        Invoke-CheckedCommand dotnet roslynator analyze $solution '--msbuild-path' $msbuildPath '--properties' 'Configuration=Release' '--severity-level' 'warning' '--ignore-compiler-diagnostics' '--verbosity' 'minimal'
        Assert-RepositoryWhitespace
    }
    else {
        $effectiveFilter = if ($Mode -eq 'Quick' -and -not [string]::IsNullOrWhiteSpace($TestFilter)) {
            $TestFilter
        }
        else {
            $functionalFilter
        }
        Invoke-StandaloneTestVerification `
            -Filter $effectiveFilter `
            -DiagnosticsRoot $testDiagnosticsDirectory `
            -UseFunctionalShards:([string]::IsNullOrWhiteSpace($TestFilter))
    }
}
catch {
    $verificationFailure = $_
}
finally {
    Remove-Item Env:BMS_SCD_APP_PUBLISH_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:BMS_SCD_UPDATER_PUBLISH_ROOT -ErrorAction SilentlyContinue
    Pop-Location
}

$trackedStateAfter = Get-TrackedWorkingTreeFingerprint
if ($trackedStateAfter -ne $trackedStateBefore) {
    $failureContext = if ($null -ne $verificationFailure) {
        " Original verification failure: $($verificationFailure.Exception.Message)"
    }
    else {
        [string]::Empty
    }
    throw "Verification changed one or more tracked files. Inspect git diff before continuing.$failureContext"
}
Write-Host "Tracked working tree fingerprint unchanged: $trackedStateAfter"

if ($null -ne $verificationFailure) {
    throw $verificationFailure
}
