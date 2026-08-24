[CmdletBinding()]
param(
    [ValidateSet('Quick', 'Functional', 'Full')]
    [string]$Mode = 'Quick',

    [string]$TestFilter,

    # This seam only lowers the canonical budget so the timeout path can be
    # verified without waiting three minutes. It cannot relax the policy limit.
    [ValidateRange(1, 180)]
    [int]$FunctionalTimeoutSeconds = 180,

    # Internal probe-only guard.  A normal CLI string cannot satisfy the typed guard
    # checked by Invoke-MonitoredCommand; no environment variable enables this seam.
    [object]$InternalTestGuard
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
$existingDataAcceptanceScript = Join-Path $repoRoot 'scripts\accept-net10-existing-data.ps1'
$updateAcceptanceScript = Join-Path $repoRoot 'scripts\accept-net10-update.ps1'
$testHangTimeoutSeconds = 120
$functionalCleanupReserveSeconds = 10
$monitoredCommandCleanupSeconds = 5
. (Join-Path $PSScriptRoot 'verification-runner-contract.ps1')
. (Join-Path $PSScriptRoot 'verification-process-lifecycle.ps1')
. (Join-Path $PSScriptRoot 'distribution-artifact.ps1')
$verificationRunnerContract = Get-VerificationRunnerContract
Assert-VerificationRunnerContract -Contract $verificationRunnerContract

function Get-RepositoryFormatArguments {
    param(
        [Parameter(Mandatory)]
        [string]$WorkspaceRoot
    )

    $formatContract = $verificationRunnerContract.Full.RepositoryFormat
    if ($formatContract.WorkspaceKind -cne 'folder' -or
        $formatContract.ProjectEvaluation -cne 'none' -or
        -not [bool]$formatContract.VerifiesAllGenuineWorkspaceFiles) {
        throw 'Repository format contract must use folder mode for the genuine workspace only.'
    }

    $arguments = @(
        'format'
        'whitespace'
        $WorkspaceRoot
        '--folder'
        '--verify-no-changes'
        '--verbosity'
        'minimal')
    foreach ($excludedRoot in @($formatContract.GeneratedRootExclusions)) {
        $arguments += @('--exclude', $excludedRoot)
    }
    return $arguments
}

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
$functionalCompiledWpfClasswideClasses = @(
    'BeMusicSeeker.Tests.LoadPlaylistURIDialogTests',
    'BeMusicSeeker.Tests.MainWindowChartPresentationWpfTests',
    'BeMusicSeeker.Tests.MainWindowPackageMaintenanceWpfTests',
    'BeMusicSeeker.Tests.MainWindowPlaybackWpfTests',
    'BeMusicSeeker.Tests.MainWindowPlayHistoryWpfTests',
    'BeMusicSeeker.Tests.MainWindowPlaylistWorkspaceWpfTests',
    'BeMusicSeeker.Tests.MainWindowProgressStatusBarWpfTests',
    'BeMusicSeeker.Tests.MainWindowSelectedChartContextMenuWpfTests',
    'BeMusicSeeker.Tests.MainWindowTreePresentationWpfTests',
    'BeMusicSeeker.Tests.MainWindowViewHostTests',
    'BeMusicSeeker.Tests.SettingsWindowCompiledBehaviorTests',
    'BeMusicSeeker.Tests.UiDialogCoordinatorWpfTests')
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
        # Dedicated fixture groups have explicit worker contracts. Keep the
        # library/chart classes in one ClassLevel testhost while using two
        # workers; other single-worker hosts stay at one while the explicit
        # owned database/file contract remains three. The remaining host uses
        # every logical processor. This bounded overlap was faster and stable
        # in repeated Functional runs; subtracting these workers would leave
        # the remaining host under-provisioned after the external hosts exit.
        Name = 'library-chart-classwide'
        Workers = 2
        Scope = 'ClassLevel'
        Classes = @(
            'BeMusicSeeker.Tests.BmsLibraryInitializationServiceTests',
            'BeMusicSeeker.Tests.BmsLibraryZeroNoteRefreshTests',
            'BeMusicSeeker.Tests.ChartInfoMetadataTests')
    },
    [pscustomobject]@{
        Name = 'lr2-songdb-sync'
        Workers = 1
        Scope = 'ClassLevel'
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
        # These constructor-only compiled WPF fixtures share process-scoped
        # WPF resources, cursor state, and self-completing modal test seams.
        # Keep them class-serial in their own host without starting the app.
        Name = 'compiled-wpf-classwide'
        Workers = 1
        Scope = 'ClassLevel'
        Classes = $functionalCompiledWpfClasswideClasses
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
    $libraryChartClasswideShards = @($functionalTestClassShards |
        Where-Object { $_.Name -ceq 'library-chart-classwide' })
    if ($libraryChartClasswideShards.Count -ne 1) {
        throw 'Functional library/chart tests must have exactly one dedicated shard.'
    }
    $requiredLibraryChartClasswideClasses = @(
        'BeMusicSeeker.Tests.BmsLibraryInitializationServiceTests',
        'BeMusicSeeker.Tests.ChartInfoMetadataTests',
        'BeMusicSeeker.Tests.BmsLibraryZeroNoteRefreshTests')
    $libraryChartClasswideShard = $libraryChartClasswideShards[0]
    $libraryChartClasswideClasses = @($libraryChartClasswideShard.Classes)
    if ($libraryChartClasswideClasses.Count -ne $requiredLibraryChartClasswideClasses.Count -or
        @(Compare-Object `
            -ReferenceObject $requiredLibraryChartClasswideClasses `
            -DifferenceObject $libraryChartClasswideClasses `
            -CaseSensitive).Count -ne 0) {
        throw 'Functional library/chart shard must contain exactly the approved three test classes.'
    }
    if ($libraryChartClasswideShard.Workers -ne 2 -or
        $libraryChartClasswideShard.Scope -cne 'ClassLevel') {
        throw 'Functional library/chart shard must use two workers with ClassLevel scope.'
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
    $lr2SongDbShards = @($functionalTestClassShards |
        Where-Object { $_.Name -ceq 'lr2-songdb-sync' })
    if ($lr2SongDbShards.Count -ne 1) {
        throw 'Functional LR2 song database tests must have exactly one dedicated shard.'
    }
    $lr2SongDbShard = $lr2SongDbShards[0]
    $requiredLr2SongDbClasses = @(
        'BeMusicSeeker.Tests.BmsLibraryLr2SongDbSyncTests')
    $lr2SongDbClasses = @($lr2SongDbShard.Classes)
    if ($lr2SongDbClasses.Count -ne $requiredLr2SongDbClasses.Count -or
        @(Compare-Object `
            -ReferenceObject $requiredLr2SongDbClasses `
            -DifferenceObject $lr2SongDbClasses `
            -CaseSensitive).Count -ne 0) {
        throw 'Functional LR2 song database shard must contain exactly its approved test class.'
    }
    if ($lr2SongDbShard.Workers -ne 1 -or $lr2SongDbShard.Scope -cne 'ClassLevel') {
        throw 'Functional LR2 song database shard must use one worker with ClassLevel scope.'
    }
    $allowedMultiWorkerShards = @(
        'library-chart-classwide',
        'owned-db-file-class-level')
    if (@($functionalTestClassShards |
        Where-Object {
            $scope = if ($_.PSObject.Properties.Name -contains 'Scope') {
                $_.Scope
            }
            else {
                'ClassLevel'
            }
            $allowedMultiWorkerShards -cnotcontains $_.Name -and
            ($_.Workers -ne 1 -or $scope -cne 'ClassLevel') }).Count -gt 0) {
        throw 'All Functional external test shards except the approved library/chart and owned database/file contracts must use one worker with ClassLevel scope.'
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
        },
        [pscustomobject]@{
            # Keep this literal contract independent from the route declaration
            # so drift cannot silently return compiled WPF fixtures to remaining.
            Name = 'compiled-wpf-classwide'
            Classes = @(
                'BeMusicSeeker.Tests.LoadPlaylistURIDialogTests',
                'BeMusicSeeker.Tests.MainWindowChartPresentationWpfTests',
                'BeMusicSeeker.Tests.MainWindowPackageMaintenanceWpfTests',
                'BeMusicSeeker.Tests.MainWindowPlaybackWpfTests',
                'BeMusicSeeker.Tests.MainWindowPlayHistoryWpfTests',
                'BeMusicSeeker.Tests.MainWindowPlaylistWorkspaceWpfTests',
                'BeMusicSeeker.Tests.MainWindowProgressStatusBarWpfTests',
                'BeMusicSeeker.Tests.MainWindowSelectedChartContextMenuWpfTests',
                'BeMusicSeeker.Tests.MainWindowTreePresentationWpfTests',
                'BeMusicSeeker.Tests.MainWindowViewHostTests',
                'BeMusicSeeker.Tests.SettingsWindowCompiledBehaviorTests',
                'BeMusicSeeker.Tests.UiDialogCoordinatorWpfTests')
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

    $assignedClasses = @($shardClasses) +
        @($functionalExclusiveTestClasses) +
        @($functionalMethodLevelPreWaveClasses)
    if (($assignedClasses | Sort-Object -Unique).Count -ne $assignedClasses.Count) {
        throw 'Functional assigned test classes must be excluded from remaining and belong to exactly one route.'
    }
    for ($leftIndex = 0; $leftIndex -lt $assignedClasses.Count; $leftIndex++) {
        for ($rightIndex = $leftIndex + 1; $rightIndex -lt $assignedClasses.Count; $rightIndex++) {
            $left = $assignedClasses[$leftIndex]
            $right = $assignedClasses[$rightIndex]
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

        [DateTime]$ProcessDeadlineUtc,

        [DateTime]$PhaseDeadlineUtc,

        [DateTime]$CleanupDeadlineUtc,

        [object]$PostStartFaultGuard,

        [switch]$IsTestCommand
    )

    [void](New-Item -ItemType Directory -Path $DiagnosticsDirectory -Force)
    $stageStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $process = [System.Diagnostics.Process]::new()
    $processStarted = $false
    $standardOutputTask = $null
    $standardErrorTask = $null
    $identity = $null
    $commandIdentity = "$CommandPath $($Arguments -join ' ')"
    $lifecycleResult = $null
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

    try {
        Write-Host "$Label (timeout ${TimeoutSeconds}s): $CommandPath $($Arguments -join ' ')"
        if (-not $process.Start()) {
            throw "Unable to start ${Label}: $CommandPath"
        }
        $processStarted = $true

        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        $identity = Get-VerificationProcessIdentity -Process $process -CommandIdentity $commandIdentity
        if ($null -ne $PostStartFaultGuard) {
            if ($PostStartFaultGuard -isnot [VerificationPostStartFaultGuard]) {
                throw 'Post-start fault guard was not created by the internal deterministic probe.'
            }
            if ($PostStartFaultGuard.TryConsumeSignal()) {
                throw "Internal post-start fault injection for $Label."
            }
        }
        $processDeadline = if ($PSBoundParameters.ContainsKey('ProcessDeadlineUtc')) {
            $ProcessDeadlineUtc
        }
        else {
            [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        }
        $phaseDeadline = if ($PSBoundParameters.ContainsKey('PhaseDeadlineUtc')) {
            $PhaseDeadlineUtc
        }
        elseif ($PSBoundParameters.ContainsKey('CleanupDeadlineUtc')) {
            $CleanupDeadlineUtc
        }
        else {
            $processDeadline.AddSeconds($monitoredCommandCleanupSeconds)
        }
        $cleanupDeadline = $phaseDeadline
        if ($PSBoundParameters.ContainsKey('CleanupDeadlineUtc') -and
            $CleanupDeadlineUtc -lt $cleanupDeadline) {
            $cleanupDeadline = $CleanupDeadlineUtc
        }
        $lifecycleResult = Invoke-BoundedProcessLifecycle `
            -Process $process `
            -StandardOutputTask $standardOutputTask `
            -StandardErrorTask $standardErrorTask `
            -RootProcessId $identity.ProcessId `
            -RootProcessIdentity ("$($identity.StartTimeUtcTicks)|$($identity.ProcessId)") `
            -CommandIdentity $commandIdentity `
            -DiagnosticsDirectory $DiagnosticsDirectory `
            -ProcessDeadlineUtc $processDeadline `
            -PhaseDeadlineUtc $phaseDeadline `
            -CleanupDeadlineUtc $cleanupDeadline `
            -TerminateProcessTree:$false
    }
    catch {
        $primaryException = $_.Exception
        $stageStopwatch.Stop()
        $postStartDiagnostics = [System.Collections.Generic.List[string]]::new()
        if ($processStarted -and $null -ne $identity -and
            $null -ne $standardOutputTask -and $null -ne $standardErrorTask) {
            $postStartDiagnostics.Add("post-start-exception: $($primaryException.Message)")
            $postStartCleanupDeadlineUtc = if ($PSBoundParameters.ContainsKey('CleanupDeadlineUtc')) {
                $CleanupDeadlineUtc
            }
            elseif ($PSBoundParameters.ContainsKey('PhaseDeadlineUtc')) {
                $PhaseDeadlineUtc
            }
            else {
                [DateTime]::UtcNow.AddSeconds($monitoredCommandCleanupSeconds)
            }
            $postStartProcessDeadlineUtc = [DateTime]::UtcNow
            try {
                $lifecycleResult = Invoke-BoundedProcessLifecycle `
                    -Process $process `
                    -StandardOutputTask $standardOutputTask `
                    -StandardErrorTask $standardErrorTask `
                    -RootProcessId $identity.ProcessId `
                    -RootProcessIdentity ("$($identity.StartTimeUtcTicks)|$($identity.ProcessId)") `
                    -CommandIdentity $commandIdentity `
                    -DiagnosticsDirectory $DiagnosticsDirectory `
                    -ProcessDeadlineUtc $postStartProcessDeadlineUtc `
                    -PhaseDeadlineUtc $postStartCleanupDeadlineUtc `
                    -CleanupDeadlineUtc $postStartCleanupDeadlineUtc `
                    -TerminateProcessTree `
                    -PreserveExistingArtifactOnEmpty `
                    -InitialCleanupDiagnostics @("post-start-exception: $($primaryException.Message)")
                $primaryException.Data['VerificationRootProcessId'] = $identity.ProcessId
                $primaryException.Data['VerificationRootProcessIdentity'] = "$($identity.StartTimeUtcTicks)|$($identity.ProcessId)"
                $primaryException.Data['VerificationLifecycleResult'] = $lifecycleResult
                foreach ($diagnostic in @($lifecycleResult.SecondaryDiagnostics)) {
                    $postStartDiagnostics.Add([string]$diagnostic)
                }
                if (@($lifecycleResult.SecondaryDiagnostics).Count -gt 0) {
                    Write-Warning "$Label post-start cleanup diagnostics: $(@($lifecycleResult.SecondaryDiagnostics) -join '; ')"
                }
            }
            catch {
                $postStartDiagnostics.Add("post-start-cleanup: $($_.Exception.Message)")
                Write-Warning "$Label post-start cleanup failed: $($_.Exception.Message)"
            }
        }
        else {
            # A start failure has no owned process or stream task.  Preserve the old
            # startup diagnostic route, but never use it after Process.Start succeeded.
            $startupCleanupDeadlineUtc = [DateTime]::UtcNow.AddSeconds(5)
            if ($PSBoundParameters.ContainsKey('PhaseDeadlineUtc') -and
                $PhaseDeadlineUtc -lt $startupCleanupDeadlineUtc) {
                $startupCleanupDeadlineUtc = $PhaseDeadlineUtc
            }
            try {
                if ([DateTime]::UtcNow -lt $startupCleanupDeadlineUtc) {
                    foreach ($streamName in @('stdout', 'stderr')) {
                        $writeTask = [System.IO.File]::WriteAllTextAsync(
                            (Join-Path $DiagnosticsDirectory "$streamName.log"),
                            [string]::Empty,
                            [System.Text.UTF8Encoding]::new($false))
                        [void](Wait-VerificationCleanupTask `
                                -Task $writeTask `
                                -OperationName "$Label $streamName startup diagnostic write" `
                                -CleanupDeadlineUtc $startupCleanupDeadlineUtc `
                                -CleanupDiagnostics $postStartDiagnostics)
                    }
                }
                else {
                    $postStartDiagnostics.Add(
                        "$Label startup diagnostic persistence skipped after cleanup deadline; elapsed $($stageStopwatch.ElapsedMilliseconds)ms")
                }
            }
            catch {
                $postStartDiagnostics.Add("$Label startup diagnostics write failed: $($_.Exception.Message)")
            }
            try {
                if ([DateTime]::UtcNow -lt $startupCleanupDeadlineUtc) {
                    [void](Dispose-VerificationProcessHandleBounded `
                            -Process $process `
                            -OperationName "$Label startup process dispose" `
                            -CleanupDeadlineUtc $startupCleanupDeadlineUtc `
                            -CleanupDiagnostics $postStartDiagnostics)
                }
            }
            catch {
                $postStartDiagnostics.Add("$Label process dispose failed before start: $($_.Exception.Message)")
            }
        }
        foreach ($diagnostic in @($postStartDiagnostics)) {
            if (-not $primaryException.Data.Contains('VerificationSecondaryDiagnostics')) {
                $primaryException.Data['VerificationSecondaryDiagnostics'] = [System.Collections.Generic.List[string]]::new()
            }
            [void]$primaryException.Data['VerificationSecondaryDiagnostics'].Add([string]$diagnostic)
        }
        throw $primaryException
    }

    $stageStopwatch.Stop()
    if (-not [string]::IsNullOrEmpty($lifecycleResult.StandardOutput)) {
        Write-Host $lifecycleResult.StandardOutput -NoNewline
    }
    if (-not [string]::IsNullOrEmpty($lifecycleResult.StandardError)) {
        if ($null -eq $lifecycleResult.PrimaryFailureKind) {
            Write-Warning $lifecycleResult.StandardError.TrimEnd()
        }
        else {
            Write-Error $lifecycleResult.StandardError -ErrorAction Continue
        }
    }

    Write-Host "$Label elapsed: $([Math]::Round($stageStopwatch.Elapsed.TotalSeconds, 1))s; diagnostics: $DiagnosticsDirectory"
    if ($null -ne $lifecycleResult.PrimaryFailureKind) {
        $failureMessage = Get-VerificationLifecycleFailureMessage `
            -Label $Label `
            -Result $lifecycleResult `
            -TimeoutSeconds $TimeoutSeconds
        if (@($lifecycleResult.SecondaryDiagnostics).Count -gt 0) {
            Write-Warning "$Label secondary lifecycle diagnostics: $(@($lifecycleResult.SecondaryDiagnostics) -join '; ')"
        }
        throw "$failureMessage Diagnostics: $DiagnosticsDirectory"
    }
    if (@($lifecycleResult.SecondaryDiagnostics).Count -gt 0) {
        throw "$(Get-VerificationLifecycleFailureMessage -Label $Label -Result $lifecycleResult) Diagnostics: $DiagnosticsDirectory"
    }
}

function Get-FullPhaseDescriptor {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    $descriptor = @($verificationRunnerContract.Full.PhaseDescriptors |
        Where-Object { $_.Name -ceq $Name })
    if ($descriptor.Count -ne 1) {
        throw "Full runner phase descriptor is missing or duplicated: $Name"
    }
    return $descriptor[0]
}

function Get-FullPhaseRemainingSeconds {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Stopwatch]$Stopwatch,

        [Parameter(Mandatory)]
        [int]$BudgetSeconds,

        [Parameter(Mandatory)]
        [string]$PhaseName
    )

    $remaining = $BudgetSeconds - $Stopwatch.Elapsed.TotalSeconds
    if ($remaining -le 0) {
        throw "Full phase '$PhaseName' exhausted its ${BudgetSeconds}-second budget."
    }
    return [Math]::Max(1, [int][Math]::Floor($remaining))
}

function Invoke-FullPhaseCommand {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Stopwatch]$Stopwatch,

        [Parameter(Mandatory)]
        [int]$BudgetSeconds,

        [Parameter(Mandatory)]
        [string]$PhaseName,

        [Parameter(Mandatory)]
        [string]$Label,

        [Parameter(Mandatory)]
        [string]$CommandPath,

        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory,

        [string]$WorkingDirectory,

        [switch]$IsTestCommand
    )

    $remainingSeconds = Get-FullPhaseRemainingSeconds `
        -Stopwatch $Stopwatch `
        -BudgetSeconds $BudgetSeconds `
        -PhaseName $PhaseName
    if ([string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $WorkingDirectory = $repoRoot
    }
    # The descriptor's remaining time is the absolute phase budget.  Reserve the
    # ordinary monitored-command cleanup cap inside that budget rather than adding it
    # after the phase has expired.
    $phaseDeadlineUtc = [DateTime]::UtcNow.AddSeconds($remainingSeconds)
    $processDeadlineUtc = $phaseDeadlineUtc.AddSeconds(-$monitoredCommandCleanupSeconds)
    if ($processDeadlineUtc -lt [DateTime]::UtcNow) {
        $processDeadlineUtc = [DateTime]::UtcNow
    }
    Invoke-MonitoredCommand `
        -Label $Label `
        -CommandPath $CommandPath `
        -Arguments $Arguments `
        -WorkingDirectory $WorkingDirectory `
        -DiagnosticsDirectory $DiagnosticsDirectory `
        -TimeoutSeconds $remainingSeconds `
        -ProcessDeadlineUtc $processDeadlineUtc `
        -PhaseDeadlineUtc $phaseDeadlineUtc `
        -IsTestCommand:$IsTestCommand
}

function Write-FullPhaseResult {
    param(
        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory,

        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Result
    )

    try {
        [void](New-Item -ItemType Directory -Path $DiagnosticsDirectory -Force)
        [IO.File]::WriteAllText(
            (Join-Path $DiagnosticsDirectory 'phase-result.json'),
            ($Result | ConvertTo-Json -Depth 12),
            [Text.UTF8Encoding]::new($false))
    }
    catch {
        Write-Warning "Unable to write Full phase result '$DiagnosticsDirectory': $($_.Exception.Message)"
    }
}

function Invoke-MonitoredFullPhase {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$DiagnosticsRoot,

        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [scriptblock]$Cleanup
    )

    $descriptor = Get-FullPhaseDescriptor -Name $Name
    $phaseDirectory = Join-Path $DiagnosticsRoot $descriptor.DiagnosticsSegment
    [void](New-Item -ItemType Directory -Path $phaseDirectory -Force)
    $phaseStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $primaryFailure = $null
    $cleanupFailure = $null
    $actionOutput = @()
    try {
        try {
            $actionOutput = @(& $Action $phaseStopwatch $phaseDirectory)
        }
        catch {
            $primaryFailure = $_.Exception
        }
    }
    finally {
        if ($null -ne $Cleanup) {
            try {
                & $Cleanup
            }
            catch {
                $cleanupFailure = $_.Exception
            }
        }
        $phaseStopwatch.Stop()
        if ($null -eq $primaryFailure -and
            $phaseStopwatch.Elapsed.TotalSeconds -gt [int]$descriptor.BudgetSeconds) {
            $primaryFailure = [TimeoutException]::new(
                "Full phase '$Name' exceeded the $($descriptor.BudgetSeconds)-second budget.")
        }
        $result = [ordered]@{
            schemaVersion = 1
            phase = $Name
            budgetSeconds = [int]$descriptor.BudgetSeconds
            elapsedSeconds = [Math]::Round($phaseStopwatch.Elapsed.TotalSeconds, 3)
            status = if ($null -eq $primaryFailure -and $null -eq $cleanupFailure) { 'passed' } elseif ($null -ne $primaryFailure) { 'failed' } else { 'cleanup-failed' }
            primaryFailure = if ($null -ne $primaryFailure) { $primaryFailure.ToString() } else { $null }
            cleanupFailure = if ($null -ne $cleanupFailure) { $cleanupFailure.ToString() } else { $null }
        }
        Write-FullPhaseResult -DiagnosticsDirectory $phaseDirectory -Result $result
    }

    if ($null -ne $primaryFailure) {
        if ($null -ne $cleanupFailure) {
            Write-Warning "Full phase '$Name' cleanup also failed after the primary failure: $($cleanupFailure.Message)"
        }
        throw $primaryFailure
    }
    if ($null -ne $cleanupFailure) {
        throw $cleanupFailure
    }
    return $actionOutput
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

        [DateTime]$ProcessDeadlineUtc,

        [DateTime]$PhaseDeadlineUtc,

        [DateTime]$CleanupDeadlineUtc,

        [switch]$ReserveCleanupInsideTimeout,

        [switch]$NoBuild
    )

    $arguments = Get-TestArguments `
        -Filter $Filter `
        -DiagnosticsDirectory $DiagnosticsDirectory `
        -TimeoutSeconds $TimeoutSeconds `
        -RunSettingsPath $RunSettingsPath `
        -NoBuild:$NoBuild
    $invokeParameters = @{
        Label = "$Name test lane"
        CommandPath = 'dotnet'
        Arguments = $arguments
        WorkingDirectory = $repoRoot
        DiagnosticsDirectory = $DiagnosticsDirectory
        TimeoutSeconds = $TimeoutSeconds
        IsTestCommand = $true
    }
    if ($PSBoundParameters.ContainsKey('CleanupDeadlineUtc')) {
        $invokeParameters.CleanupDeadlineUtc = $CleanupDeadlineUtc
    }
    if ($PSBoundParameters.ContainsKey('ProcessDeadlineUtc')) {
        $invokeParameters.ProcessDeadlineUtc = $ProcessDeadlineUtc
    }
    if ($PSBoundParameters.ContainsKey('PhaseDeadlineUtc')) {
        $invokeParameters.PhaseDeadlineUtc = $PhaseDeadlineUtc
    }
    if ($ReserveCleanupInsideTimeout) {
        $phaseDeadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        $processDeadline = $phaseDeadline.AddSeconds(-$monitoredCommandCleanupSeconds)
        if ($processDeadline -lt [DateTime]::UtcNow) {
            $processDeadline = [DateTime]::UtcNow
        }
        $invokeParameters.ProcessDeadlineUtc = $processDeadline
        $invokeParameters.PhaseDeadlineUtc = $phaseDeadline
    }
    Invoke-MonitoredCommand @invokeParameters
}

function Get-FunctionalPhaseRemainingSeconds {
    param(
        [Parameter(Mandatory)]
        [DateTime]$ProcessDeadlineUtc,

        [Parameter(Mandatory)]
        [string]$PhaseName
    )

    $remaining = ($ProcessDeadlineUtc - [DateTime]::UtcNow).TotalSeconds
    if ($remaining -le 0) {
        throw "Functional test phase reached its process deadline before $PhaseName."
    }

    return [Math]::Max(1, [int][Math]::Floor($remaining))
}

function Assert-FunctionalOrchestrationConfiguration {
    param(
        [Parameter(Mandatory)]
        [object[]]$Shards,

        [Parameter(Mandatory)]
        [object[]]$FanoutShards
    )

    $shardEntries = @()
    if ($null -ne $Shards) {
        $shardEntries = @($Shards)
    }
    $fanoutEntries = @()
    if ($null -ne $FanoutShards) {
        $fanoutEntries = @($FanoutShards)
    }
    $shardNames = @($shardEntries | ForEach-Object { $_.Name })
    if (@($shardNames | Sort-Object -Unique).Count -ne $shardNames.Count) {
        throw 'Functional orchestration shards must have unique names.'
    }
    $expectedShardEntryCount = @($functionalTestClassShards).Count + 1
    if ($shardEntries.Count -ne $expectedShardEntryCount) {
        throw "Functional orchestration must preserve $expectedShardEntryCount total shard process entries."
    }

    $lr2Shards = @($shardEntries |
        Where-Object { $_.Name -ceq 'lr2-songdb-sync' })
    if ($lr2Shards.Count -ne 1) {
        throw 'Functional orchestration must contain exactly one lr2-songdb-sync entry.'
    }

    $expectedFanoutNames = @($shardEntries |
        Where-Object { $_.Name -cne 'lr2-songdb-sync' } |
        ForEach-Object { $_.Name })
    $actualFanoutNames = @($fanoutEntries | ForEach-Object { $_.Name })
    if (@($actualFanoutNames | Where-Object { $_ -ceq 'lr2-songdb-sync' }).Count -ne 0) {
        throw 'Functional fanout must exclude the already-started lr2-songdb-sync entry.'
    }
    if ($actualFanoutNames.Count -ne $expectedFanoutNames.Count -or
        @(Compare-Object `
            -ReferenceObject $expectedFanoutNames `
            -DifferenceObject $actualFanoutNames `
            -CaseSensitive).Count -ne 0) {
        throw 'Functional fanout must contain each non-LR2 shard exactly once.'
    }
}

function Assert-FunctionalOrchestrationPhaseOrder {
    param(
        [AllowNull()]
        [AllowEmptyCollection()]
        [object]$PhaseTrace,

        [switch]$AllowPrefix
    )

    $phaseEntries = @()
    if ($null -ne $PhaseTrace) {
        $phaseEntries = @($PhaseTrace)
    }
    $expectedPhases = @(
        'exclusive-portable-settings',
        'lr2-songdb-sync',
        'method-level-pre-wave',
        'remaining-and-non-lr2-shards')
    if ($phaseEntries.Count -gt $expectedPhases.Count) {
        throw 'Functional orchestration contains an unexpected phase.'
    }
    if (@($phaseEntries | Sort-Object -Unique).Count -ne $phaseEntries.Count) {
        throw 'Functional orchestration phases must not be repeated.'
    }
    for ($phaseIndex = 0; $phaseIndex -lt $phaseEntries.Count; $phaseIndex++) {
        if ($phaseEntries[$phaseIndex] -cne $expectedPhases[$phaseIndex]) {
            throw "Functional orchestration phase '$($phaseEntries[$phaseIndex])' is out of order."
        }
    }
    if (-not $AllowPrefix -and $phaseEntries.Count -ne $expectedPhases.Count) {
        throw 'Functional orchestration must complete exclusive -> LR2 -> pre-wave -> fanout order.'
    }
}

function Get-FunctionalLr2EntryState {
    param(
        [AllowNull()]
        [object]$Entry
    )

    if ($null -eq $Entry -or $null -eq $Entry.Process) {
        return [pscustomobject]@{
            State = 'Invalid'
            Detail = 'The LR2 process entry or process handle is missing.'
        }
    }

    try {
        if ($Entry.PSObject.Properties.Name -contains 'Canceled' -and
            [bool]$Entry.Canceled) {
            return [pscustomobject]@{
                State = 'Canceled'
                Detail = 'The LR2 process entry was canceled before fanout.'
            }
        }
        $hasExited = $Entry.Process.HasExited
        if ($hasExited -isnot [bool]) {
            return [pscustomobject]@{
                State = 'Invalid'
                Detail = "The LR2 process reported an invalid HasExited state '$hasExited'."
            }
        }
        if (-not $hasExited) {
            return [pscustomobject]@{
                State = 'Running'
                Detail = 'The LR2 process is still running and remains in the common result set.'
            }
        }

        $exitCode = $Entry.Process.ExitCode
        if ($exitCode -isnot [int]) {
            return [pscustomobject]@{
                State = 'Invalid'
                Detail = "The LR2 process reported an invalid exit code '$exitCode'."
            }
        }
        if ($exitCode -eq 0) {
            return [pscustomobject]@{
                State = 'Succeeded'
                Detail = 'The LR2 process exited successfully and remains accounted once.'
            }
        }
        return [pscustomobject]@{
            State = 'Failed'
            Detail = "The LR2 process exited with code $exitCode (failure or cancellation)."
        }
    }
    catch {
        return [pscustomobject]@{
            State = 'Invalid'
            Detail = "The LR2 process state could not be inspected: $($_.Exception.Message)"
        }
    }
}

function Assert-FunctionalLr2CanProceedToFanout {
    param(
        [Parameter(Mandatory)]
        [object]$Entry
    )

    $state = Get-FunctionalLr2EntryState -Entry $Entry
    if ($state.State -in @('Failed', 'Canceled', 'Invalid')) {
        throw "Functional LR2 shard cannot enter fanout ($($state.State)): $($state.Detail)"
    }
    return $state
}

function Start-FunctionalShardProcess {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Shard,

        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory,

        [Parameter(Mandatory)]
        [int]$TimeoutSeconds
    )

    $runSettingsPath = Join-Path $DiagnosticsDirectory 'parallel.runsettings'
    Write-MSTestParallelRunSettings `
        -Path $runSettingsPath `
        -Workers $Shard.Workers `
        -Scope $Shard.Scope
    $arguments = Get-TestArguments `
        -Filter $Shard.Filter `
        -DiagnosticsDirectory $DiagnosticsDirectory `
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
    $started = $false
    try {
        if (-not $process.Start()) {
            throw "Unable to start functional test shard '$($Shard.Name)'."
        }
        $started = $true
        $commandIdentity = "dotnet $($arguments -join ' ')"
        $identity = Get-VerificationProcessIdentity -Process $process -CommandIdentity $commandIdentity
        return [pscustomobject]@{
            Name = $Shard.Name
            Directory = $DiagnosticsDirectory
            Process = $process
            ProcessId = $process.Id
            RootProcessIdentity = "$($identity.StartTimeUtcTicks)|$($identity.ProcessId)"
            CommandIdentity = $commandIdentity
            StandardOutputTask = $process.StandardOutput.ReadToEndAsync()
            StandardErrorTask = $process.StandardError.ReadToEndAsync()
        }
    }
    catch {
        if ($started -and -not $process.HasExited) {
            try {
                $process.Kill($true)
            }
            catch {
                Write-Warning "$($Shard.Name): failed to stop a process whose entry could not be recorded: $($_.Exception.Message)"
            }
        }
        $process.Dispose()
        throw
    }
}

function Invoke-ParallelFunctionalTestShards {
    param(
        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory,

        [Parameter(Mandatory)]
        [int]$TimeoutSeconds,

        [Parameter(Mandatory)]
        [DateTime]$ProcessDeadlineUtc,

        [Parameter(Mandatory)]
        [DateTime]$CleanupDeadlineUtc
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
    $lr2Shard = @($shards | Where-Object { $_.Name -ceq 'lr2-songdb-sync' })[0]
    $fanoutShards = @($shards | Where-Object { $_.Name -cne 'lr2-songdb-sync' })
    Assert-FunctionalOrchestrationConfiguration `
        -Shards $shards `
        -FanoutShards $fanoutShards

    # Allocate every Functional entry directory before any monitored process is launched.
    # Lifecycle cleanup receives only pre-created paths and therefore cannot begin a new
    # filesystem operation for a later entry after the shared cleanup deadline.
    [void](New-Item -ItemType Directory -Path $DiagnosticsDirectory -Force)
    $lr2DiagnosticsDirectory = Join-Path $DiagnosticsDirectory 'lr2-songdb-sync'
    $exclusiveDirectory = Join-Path $DiagnosticsDirectory 'exclusive-portable-settings'
    $preWaveDirectory = Join-Path $DiagnosticsDirectory 'method-level-pre-wave'
    $shardDirectories = @($shards | ForEach-Object { Join-Path $DiagnosticsDirectory $_.Name })
    foreach ($directory in @($lr2DiagnosticsDirectory, $exclusiveDirectory, $preWaveDirectory) + $shardDirectories) {
        [void](New-Item -ItemType Directory -Path $directory -Force)
    }
    $stageStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $entries = @()
    $phaseTrace = [System.Collections.Generic.List[string]]::new()
    $lr2Entry = $null
    $timedOut = $false
    $timedOutShardNames = @()
    $launchFailure = $null
    $failedShard = $null
    $failedShardName = $null
    $failedShardExitCode = $null
    $cleanupFailures = [System.Collections.Generic.List[string]]::new()

    try {
        $exclusiveClassFilter = ($functionalExclusiveTestClasses |
            ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
        $exclusiveTimeoutSeconds = Get-FunctionalPhaseRemainingSeconds `
            -ProcessDeadlineUtc $ProcessDeadlineUtc `
            -PhaseName 'exclusive portable settings'
        Invoke-TestLane `
            -Name 'Functional exclusive portable settings' `
            -Filter "($functionalFilter)&($exclusiveClassFilter)" `
            -DiagnosticsDirectory $exclusiveDirectory `
            -TimeoutSeconds $exclusiveTimeoutSeconds `
            -CleanupDeadlineUtc $CleanupDeadlineUtc `
            -NoBuild

        [void]$phaseTrace.Add('exclusive-portable-settings')
        Assert-FunctionalOrchestrationPhaseOrder -PhaseTrace @($phaseTrace.ToArray()) -AllowPrefix

        [void]$phaseTrace.Add('lr2-songdb-sync')
        Assert-FunctionalOrchestrationPhaseOrder -PhaseTrace @($phaseTrace.ToArray()) -AllowPrefix
        try {
            $lr2TimeoutSeconds = Get-FunctionalPhaseRemainingSeconds `
                -ProcessDeadlineUtc $ProcessDeadlineUtc `
                -PhaseName 'lr2-songdb-sync'
            $lr2Entry = Start-FunctionalShardProcess `
                -Shard $lr2Shard `
                -DiagnosticsDirectory $lr2DiagnosticsDirectory `
                -TimeoutSeconds $lr2TimeoutSeconds
            $entries += $lr2Entry
        }
        catch {
            throw
        }

        $preWaveRunSettingsPath = Join-Path $preWaveDirectory 'parallel.runsettings'
        Write-MSTestParallelRunSettings `
            -Path $preWaveRunSettingsPath `
            -Workers $functionalRemainingShardWorkers `
            -Scope 'MethodLevel'
        $preWaveClassFilter = ($functionalMethodLevelPreWaveClasses |
            ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
        $preWaveTimeoutSeconds = Get-FunctionalPhaseRemainingSeconds `
            -ProcessDeadlineUtc $ProcessDeadlineUtc `
            -PhaseName 'method-level pre-wave'
        [void]$phaseTrace.Add('method-level-pre-wave')
        Assert-FunctionalOrchestrationPhaseOrder -PhaseTrace @($phaseTrace.ToArray()) -AllowPrefix
        Invoke-TestLane `
            -Name 'Functional method-level pre-wave' `
            -Filter "($functionalFilter)&($preWaveClassFilter)" `
            -DiagnosticsDirectory $preWaveDirectory `
            -TimeoutSeconds $preWaveTimeoutSeconds `
            -RunSettingsPath $preWaveRunSettingsPath `
            -CleanupDeadlineUtc $CleanupDeadlineUtc `
            -NoBuild

        $lr2PreFanoutState = Assert-FunctionalLr2CanProceedToFanout -Entry $lr2Entry
        if (@($entries | Where-Object { $_.Name -ceq 'lr2-songdb-sync' }).Count -ne 1) {
            throw 'Functional LR2 result accounting must retain exactly one existing entry before fanout.'
        }
        Write-Host "Functional LR2 state before fanout: $($lr2PreFanoutState.State); $($lr2PreFanoutState.Detail)"
        [void]$phaseTrace.Add('remaining-and-non-lr2-shards')
        Assert-FunctionalOrchestrationPhaseOrder -PhaseTrace @($phaseTrace.ToArray())
        foreach ($shard in $fanoutShards) {
            $shardDirectory = Join-Path $DiagnosticsDirectory $shard.Name
            $shardTimeoutSeconds = Get-FunctionalPhaseRemainingSeconds `
                -ProcessDeadlineUtc $ProcessDeadlineUtc `
                -PhaseName $shard.Name
            $entries += Start-FunctionalShardProcess `
                -Shard $shard `
                -DiagnosticsDirectory $shardDirectory `
                -TimeoutSeconds $shardTimeoutSeconds
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
            if ([DateTime]::UtcNow -ge $ProcessDeadlineUtc) {
                $timedOut = $true
                $timedOutShardNames = @($runningEntries | ForEach-Object { $_.Name })
                Write-Warning "Functional test phase reached its process deadline before the shared ${functionalCleanupReserveSeconds}-second cleanup reserve. Stopping shards: $($timedOutShardNames -join ', ')"
                break
            }
            Start-Sleep -Milliseconds 100
        }
    }
    catch {
        $launchFailure = $_
    }
    finally {
        if ($null -ne $failedShard) {
            $failedShardName = $failedShard.Name
            $failedShardExitCode = $failedShard.Process.ExitCode
        }
        $forceCleanup = $timedOut -or
            $null -ne $failedShardName -or
            $null -ne $launchFailure

        $functionalCleanup = Invoke-VerificationFunctionalCleanup `
            -Entries $entries `
            -CleanupDeadlineUtc $CleanupDeadlineUtc `
            -StopRoots:$forceCleanup
        foreach ($fanoutFailure in @($functionalCleanup.FanoutFailures)) {
            $cleanupFailures.Add($fanoutFailure)
        }
        foreach ($entryResult in @($functionalCleanup.EntryResults)) {
            $entry = $entryResult.Entry
            if ($null -ne $entryResult.Error) {
                $cleanupFailures.Add(
                    "$($entry.Name): cleanup/output collection failed: $($entryResult.Error.Exception.Message)")
                continue
            }

            $lifecycleResult = $entryResult.Result
            if (@($lifecycleResult.SecondaryDiagnostics).Count -gt 0) {
                foreach ($diagnostic in @($lifecycleResult.SecondaryDiagnostics)) {
                    $cleanupFailures.Add("$($entry.Name): $diagnostic")
                }
            }
            if ($null -eq $failedShardName -and $lifecycleResult.PrimaryFailureKind -ceq 'nonzero-exit') {
                $failedShardName = $entry.Name
                $failedShardExitCode = $lifecycleResult.ExitCode
            }
            if ($lifecycleResult.PrimaryFailureKind -ceq 'timeout' -and -not $timedOut) {
                $cleanupFailures.Add("$($entry.Name): bounded process lifecycle timed out during cleanup")
            }

            $standardOutput = $lifecycleResult.StandardOutput
            $standardError = $lifecycleResult.StandardError

            Write-Host "Test shard: $($entry.Name); exit code: $($lifecycleResult.ExitCode)"
            if (-not $timedOut -and -not [string]::IsNullOrEmpty($standardOutput)) {
                Write-Host $standardOutput -NoNewline
            }
            if (-not [string]::IsNullOrEmpty($standardError)) {
                if (-not $timedOut -and $lifecycleResult.ExitCode -eq 0) {
                    Write-Warning $standardError.TrimEnd()
                }
                else {
                    Write-Error $standardError -ErrorAction Continue
                }
            }
        }
        $stageStopwatch.Stop()
    }

    Write-Host "Functional test phase elapsed: $([Math]::Round($stageStopwatch.Elapsed.TotalSeconds, 1))s; diagnostics: $DiagnosticsDirectory"
    $primaryFailure = $null
    if ($null -ne $launchFailure) {
        $primaryFailure = $launchFailure
    }
    elseif ($timedOut) {
        $primaryFailure = [Exception]::new(
            "Functional test phase exceeded the ${TimeoutSeconds}-second timeout. Diagnostics: $DiagnosticsDirectory")
    }
    elseif ($null -ne $failedShardName) {
        $primaryFailure = [Exception]::new(
            "Functional test shard '$failedShardName' failed with exit code $failedShardExitCode. Diagnostics: $(Join-Path $DiagnosticsDirectory $failedShardName)")
    }
    if ($cleanupFailures.Count -gt 0) {
        if ($null -ne $primaryFailure) {
            Write-Warning "Functional test shard cleanup also failed after the primary failure: $($cleanupFailures -join '; ')"
        }
        else {
            throw "Functional test shard cleanup failed: $($cleanupFailures -join '; ')"
        }
    }
    if ($null -ne $primaryFailure) {
        throw $primaryFailure
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

function Get-AssemblyInformationalVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    $assemblyInfoPath = Join-Path $Root 'Properties\AssemblyInfo.cs'
    if (-not (Test-Path -LiteralPath $assemblyInfoPath -PathType Leaf)) {
        throw "AssemblyInfo.cs is missing: $assemblyInfoPath"
    }
    $content = Get-Content -LiteralPath $assemblyInfoPath -Raw
    if ($content -notmatch 'AssemblyInformationalVersion\("([^"]+)"\)') {
        throw "AssemblyInformationalVersion is missing: $assemblyInfoPath"
    }
    return $Matches[1]
}

function Get-RepositoryHeadCommit {
    $commit = (& git -C $repoRoot rev-parse HEAD 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) {
        throw "Unable to resolve the current repository commit: $commit"
    }
    return $commit
}

function Get-ExactReleasePackage {
    param(
        [Parameter(Mandatory)]
        [string]$DistributionDirectory,

        [Parameter(Mandatory)]
        [string]$Version,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $packagePath = Join-Path $DistributionDirectory "bemusicseeker-unofficial-fork-v$Version.zip"
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "$Description exact release package is missing: $packagePath"
    }
    return (Resolve-Path -LiteralPath $packagePath).Path
}

function Invoke-CurrentDistributionPublish {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Stopwatch]$Stopwatch,

        [Parameter(Mandatory)]
        [int]$BudgetSeconds,

        [Parameter(Mandatory)]
        [string]$PhaseDirectory,

        [Parameter(Mandatory)]
        [string]$ArtifactRoot
    )

    $publishScript = Join-Path $repoRoot 'scripts\publish.ps1'
    if (-not (Test-Path -LiteralPath $publishScript -PathType Leaf)) {
        throw "Distribution publish script is missing: $publishScript"
    }
    [void](New-Item -ItemType Directory -Path $ArtifactRoot -Force)
    Invoke-FullPhaseCommand `
        -Stopwatch $Stopwatch `
        -BudgetSeconds $BudgetSeconds `
        -PhaseName 'current-distribution-publish' `
        -Label 'Current distribution publish' `
        -CommandPath 'pwsh' `
        -Arguments @('-NoProfile', '-File', $publishScript, '-PackageOnly', '-SkipDocHtml', '-ArtifactRoot', $ArtifactRoot) `
        -DiagnosticsDirectory (Join-Path $PhaseDirectory 'publish')

    $version = Get-AssemblyInformationalVersion -Root $repoRoot
    $appRoot = Join-Path $ArtifactRoot 'app'
    $updaterRoot = Join-Path $ArtifactRoot 'updater'
    $packagePath = Get-ExactReleasePackage -DistributionDirectory (Join-Path $ArtifactRoot 'dist') -Version $version -Description 'Current'
    foreach ($requiredRoot in @($appRoot, $updaterRoot)) {
        if (-not (Test-Path -LiteralPath $requiredRoot -PathType Container)) {
            throw "Current distribution publish root is missing: $requiredRoot"
        }
    }
    return [pscustomobject]@{
        Version = $version
        Commit = Get-RepositoryHeadCommit
        AppRoot = $appRoot
        UpdaterRoot = $updaterRoot
        PackagePath = $packagePath
    }
}

function Invoke-BaselinePreparation {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Stopwatch]$Stopwatch,

        [Parameter(Mandatory)]
        [int]$BudgetSeconds,

        [Parameter(Mandatory)]
        [string]$PhaseDirectory,

        [Parameter(Mandatory)]
        [string]$ArtifactRoot,

        [Parameter(Mandatory)]
        [string]$BaselineCommit,

        [Parameter(Mandatory)]
        [object]$Current,

        [Parameter(Mandatory)]
        [string]$RunId,

        [Parameter(Mandatory)]
        [string]$ArtifactId
    )

    $baselineWorkRoot = Join-Path ([IO.Path]::GetTempPath()) (
        'BeMusicSeeker-baseline-' + $RunId + '-' + [Guid]::NewGuid().ToString('N'))
    $repositoryRootWithSeparator = ([IO.Path]::GetFullPath($repoRoot)).TrimEnd('\') + '\'
    if ([IO.Path]::GetFullPath($baselineWorkRoot).StartsWith(
            $repositoryRootWithSeparator,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Baseline preparation work root must be outside the repository: $baselineWorkRoot"
    }

    $archivePath = Join-Path $PhaseDirectory 'baseline-source.zip'
    $sourceRoot = Join-Path $baselineWorkRoot 'source'
    $primaryError = $null
    try {
        [void](New-Item -ItemType Directory -Path $sourceRoot -Force)
        Invoke-FullPhaseCommand `
        -Stopwatch $Stopwatch `
        -BudgetSeconds $BudgetSeconds `
        -PhaseName 'baseline-preparation' `
        -Label 'Baseline source archive' `
        -CommandPath 'git' `
        -Arguments @('-C', $repoRoot, 'archive', '--format=zip', "--output=$archivePath", $BaselineCommit) `
        -DiagnosticsDirectory (Join-Path $PhaseDirectory 'archive')
    Expand-Archive -LiteralPath $archivePath -DestinationPath $sourceRoot -Force

    $baselinePublishScript = Join-Path $sourceRoot 'scripts\publish.ps1'
    if (-not (Test-Path -LiteralPath $baselinePublishScript -PathType Leaf)) {
        throw "Baseline publish script is missing from checkout: $baselinePublishScript"
    }
    Invoke-FullPhaseCommand `
        -Stopwatch $Stopwatch `
        -BudgetSeconds $BudgetSeconds `
        -PhaseName 'baseline-preparation' `
        -Label 'Baseline distribution publish' `
        -CommandPath 'pwsh' `
        -Arguments @('-NoProfile', '-File', $baselinePublishScript, '-PackageOnly', '-SkipDocHtml') `
        -DiagnosticsDirectory (Join-Path $PhaseDirectory 'publish')

    $baselineVersion = Get-AssemblyInformationalVersion -Root $sourceRoot
    $sourcePackagePath = Get-ExactReleasePackage `
        -DistributionDirectory (Join-Path $sourceRoot 'dist') `
        -Version $baselineVersion `
        -Description 'Baseline'
    $baselinePackageDirectory = Join-Path $ArtifactRoot 'baseline\package'
    [void](New-Item -ItemType Directory -Path $baselinePackageDirectory -Force)
    $baselinePackagePath = Join-Path $baselinePackageDirectory ([IO.Path]::GetFileName($sourcePackagePath))
    Copy-Item -LiteralPath $sourcePackagePath -Destination $baselinePackagePath -Force

    $manifest = New-DistributionArtifactManifest `
        -ArtifactRoot $ArtifactRoot `
        -RunId $RunId `
        -ArtifactId $ArtifactId `
        -CurrentAppRoot $Current.AppRoot `
        -CurrentUpdaterRoot $Current.UpdaterRoot `
        -CurrentPackagePath $Current.PackagePath `
        -CurrentVersion $Current.Version `
        -CurrentCommit $Current.Commit `
        -BaselinePackagePath $baselinePackagePath `
        -BaselineVersion $baselineVersion `
        -BaselineCommit $BaselineCommit
    Assert-DistributionArtifactManifest `
        -ArtifactManifest $manifest `
        -ExpectedRunId $RunId `
        -ExpectedArtifactId $ArtifactId | Out-Null
    return $manifest
    }
    catch {
        $primaryError = $_
        throw
    }
    finally {
        if (Test-Path -LiteralPath $baselineWorkRoot) {
            try {
                Remove-Item -LiteralPath $baselineWorkRoot -Recurse -Force -ErrorAction Stop
            }
            catch {
                if ($null -eq $primaryError) {
                    throw
                }
                Write-Warning "Baseline preparation cleanup failed after a primary failure: $baselineWorkRoot. $($_.Exception.Message)"
            }
        }
    }
}

function Invoke-ExistingDataAcceptance {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Stopwatch]$Stopwatch,

        [Parameter(Mandatory)]
        [int]$BudgetSeconds,

        [Parameter(Mandatory)]
        [string]$PhaseDirectory,

        [Parameter(Mandatory)]
        [string]$ArtifactManifestPath
    )

    if (-not (Test-Path -LiteralPath $existingDataAcceptanceScript -PathType Leaf)) {
        throw "Existing-data acceptance runner is missing: $existingDataAcceptanceScript"
    }
    $outputDirectory = Join-Path $PhaseDirectory 'acceptance'
    Invoke-FullPhaseCommand `
        -Stopwatch $Stopwatch `
        -BudgetSeconds $BudgetSeconds `
        -PhaseName 'existing-data' `
        -Label 'Existing-data acceptance' `
        -CommandPath 'pwsh' `
        -Arguments @('-NoProfile', '-File', $existingDataAcceptanceScript, '-ArtifactManifestPath', $ArtifactManifestPath, '-OutputDirectory', $outputDirectory) `
        -DiagnosticsDirectory (Join-Path $PhaseDirectory 'command')
}

function Invoke-UpdateAcceptance {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Stopwatch]$Stopwatch,

        [Parameter(Mandatory)]
        [int]$BudgetSeconds,

        [Parameter(Mandatory)]
        [string]$PhaseDirectory,

        [Parameter(Mandatory)]
        [string]$ArtifactManifestPath
    )

    if (-not (Test-Path -LiteralPath $updateAcceptanceScript -PathType Leaf)) {
        throw "Update acceptance runner is missing: $updateAcceptanceScript"
    }
    $outputDirectory = Join-Path $PhaseDirectory 'acceptance'
    Invoke-FullPhaseCommand `
        -Stopwatch $Stopwatch `
        -BudgetSeconds $BudgetSeconds `
        -PhaseName 'update' `
        -Label 'Update acceptance' `
        -CommandPath 'pwsh' `
        -Arguments @('-NoProfile', '-File', $updateAcceptanceScript, '-ArtifactManifestPath', $ArtifactManifestPath, '-OutputDirectory', $outputDirectory) `
        -DiagnosticsDirectory (Join-Path $PhaseDirectory 'command')
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

function Invoke-CanonicalFunctionalVerification {
    param(
        [Parameter(Mandatory)]
        [string]$DiagnosticsRoot,

        [Parameter(Mandatory)]
        [ValidateRange(1, 180)]
        [int]$TimeoutSeconds
    )

    [void](New-Item -ItemType Directory -Path $DiagnosticsRoot -Force)
    $functionalStartedUtc = [DateTime]::UtcNow
    $functionalProcessDeadlineUtc = $functionalStartedUtc.AddSeconds(
        $TimeoutSeconds - $functionalCleanupReserveSeconds)
    $functionalCleanupDeadlineUtc = $functionalStartedUtc.AddSeconds($TimeoutSeconds)
    $functionalStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        Invoke-BudgetedCommand `
            -Stopwatch $functionalStopwatch `
            -BudgetSeconds $TimeoutSeconds `
            -Label 'Locked restore' `
            -CommandPath 'dotnet' `
            -Arguments @('restore', $solution, '-r', 'win-x64', '--locked-mode', '-p:PublishReadyToRun=true') `
            -DiagnosticsDirectory (Join-Path $DiagnosticsRoot 'restore')
        Invoke-BudgetedCommand `
            -Stopwatch $functionalStopwatch `
            -BudgetSeconds $TimeoutSeconds `
            -Label 'Functional build' `
            -CommandPath 'dotnet' `
            -Arguments @('build', $solution, '/p:Configuration=Release', '/p:Platform=x64', '--no-restore') `
            -DiagnosticsDirectory (Join-Path $DiagnosticsRoot 'build')
        Assert-BuiltOutputs
        [void](Get-RemainingBudgetSeconds `
            -Stopwatch $functionalStopwatch `
            -BudgetSeconds $TimeoutSeconds)
        Invoke-ParallelFunctionalTestShards `
            -DiagnosticsDirectory (Join-Path $DiagnosticsRoot 'functional') `
            -TimeoutSeconds $TimeoutSeconds `
            -ProcessDeadlineUtc $functionalProcessDeadlineUtc `
            -CleanupDeadlineUtc $functionalCleanupDeadlineUtc
        Assert-RepositoryWhitespace
    }
    finally {
        $functionalStopwatch.Stop()
        Write-Host "Canonical Functional elapsed: $([Math]::Round($functionalStopwatch.Elapsed.TotalSeconds, 1))s / ${TimeoutSeconds}s; diagnostics: $DiagnosticsRoot"
    }

    if ($functionalStopwatch.Elapsed.TotalSeconds -gt $TimeoutSeconds) {
        throw "Canonical Functional verification exceeded the $TimeoutSeconds-second command budget after repository checks."
    }
}

function Invoke-FilteredQuickVerification {
    param(
        [Parameter(Mandatory)]
        [string]$Filter,

        [Parameter(Mandatory)]
        [string]$DiagnosticsRoot,

        [Parameter(Mandatory)]
        [ValidateRange(1, 180)]
        [int]$TimeoutSeconds
    )

    [void](New-Item -ItemType Directory -Path $DiagnosticsRoot -Force)
    $filteredQuickStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        Invoke-BudgetedCommand `
            -Stopwatch $filteredQuickStopwatch `
            -BudgetSeconds $TimeoutSeconds `
            -Label 'Locked restore' `
            -CommandPath 'dotnet' `
            -Arguments @('restore', $solution, '-r', 'win-x64', '--locked-mode', '-p:PublishReadyToRun=true') `
            -DiagnosticsDirectory (Join-Path $DiagnosticsRoot 'restore')

        $testDirectory = Join-Path $DiagnosticsRoot 'functional'
        [void](New-Item -ItemType Directory -Path $testDirectory -Force)
        $remaining = Get-RemainingBudgetSeconds `
            -Stopwatch $filteredQuickStopwatch `
            -BudgetSeconds $TimeoutSeconds
        $testArguments = Get-TestArguments `
            -Filter $Filter `
            -DiagnosticsDirectory $testDirectory `
            -TimeoutSeconds $remaining
        Invoke-BudgetedCommand `
            -Stopwatch $filteredQuickStopwatch `
            -BudgetSeconds $TimeoutSeconds `
            -Label 'Filtered build and test' `
            -CommandPath 'dotnet' `
            -Arguments $testArguments `
            -DiagnosticsDirectory $testDirectory `
            -IsTestCommand
        Assert-BuiltOutputs
        Assert-RepositoryWhitespace
    }
    finally {
        $filteredQuickStopwatch.Stop()
    }

    if ($filteredQuickStopwatch.Elapsed.TotalSeconds -gt $TimeoutSeconds) {
        throw "Filtered Quick verification exceeded the $TimeoutSeconds-second command budget after repository checks."
    }
}

# Dot-sourced deterministic probes use the actual caller functions but must not enter a
# normal CLI route.  The object type is defined by verification-process-lifecycle.ps1;
# passing a string or environment variable cannot activate this return path.
if ($null -ne $InternalTestGuard -and
    $InternalTestGuard -is [VerificationPostStartFaultGuard]) {
    return
}

if ($Mode -eq 'Functional' -and -not [string]::IsNullOrWhiteSpace($TestFilter)) {
    throw 'Use Quick mode for a filtered test iteration. Functional mode always runs the canonical functional lane.'
}
if ($Mode -eq 'Full' -and -not [string]::IsNullOrWhiteSpace($TestFilter)) {
    throw 'Full mode does not accept TestFilter. Use Quick mode for an explicit opt-in lane.'
}

$trackedStateBefore = Get-TrackedWorkingTreeFingerprint
$verificationFailure = $null
$baselineCommit = 'ab9d97ed3f53dab80fb2894f20f44abdfb6fed32'
$testDiagnosticsDirectory = Join-Path $verificationArtifactsDirectory (
    'tests-' + $Mode.ToLowerInvariant() + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
[void](New-Item -ItemType Directory -Path $testDiagnosticsDirectory -Force)
$canonicalFunctionalRequested =
    $Mode -eq 'Functional' -or
    $Mode -eq 'Full' -or
    ($Mode -eq 'Quick' -and [string]::IsNullOrWhiteSpace($TestFilter))
$previousAppPublishRoot = [Environment]::GetEnvironmentVariable('BMS_SCD_APP_PUBLISH_ROOT', 'Process')
$previousUpdaterPublishRoot = [Environment]::GetEnvironmentVariable('BMS_SCD_UPDATER_PUBLISH_ROOT', 'Process')

Push-Location $repoRoot
try {
    if ($Mode -eq 'Full') {
        [void](Invoke-MonitoredFullPhase -Name 'tool-restore' -DiagnosticsRoot $testDiagnosticsDirectory -Action {
            param($phaseStopwatch, $phaseDirectory)
            $descriptor = Get-FullPhaseDescriptor -Name 'tool-restore'
            Invoke-FullPhaseCommand `
                -Stopwatch $phaseStopwatch `
                -BudgetSeconds $descriptor.BudgetSeconds `
                -PhaseName 'tool-restore' `
                -Label 'Tool restore' `
                -CommandPath 'dotnet' `
                -Arguments @('tool', 'restore') `
                -DiagnosticsDirectory (Join-Path $phaseDirectory 'command')
        })
    }

    if ($canonicalFunctionalRequested) {
        Invoke-CanonicalFunctionalVerification `
            -DiagnosticsRoot $testDiagnosticsDirectory `
            -TimeoutSeconds $FunctionalTimeoutSeconds
    }

    if ($Mode -eq 'Full') {
        [void](Invoke-MonitoredFullPhase -Name 'tool-smoke' -DiagnosticsRoot $testDiagnosticsDirectory -Action {
            param($phaseStopwatch, $phaseDirectory)
            $descriptor = Get-FullPhaseDescriptor -Name 'tool-smoke'
            foreach ($toolExecutable in $toolExecutables) {
                if (-not (Test-Path -LiteralPath $toolExecutable -PathType Leaf)) {
                    throw "Release tool executable was not produced: $toolExecutable"
                }
                $toolName = [IO.Path]::GetFileNameWithoutExtension($toolExecutable)
                Invoke-FullPhaseCommand `
                    -Stopwatch $phaseStopwatch `
                    -BudgetSeconds $descriptor.BudgetSeconds `
                    -PhaseName 'tool-smoke' `
                    -Label "Tool smoke $toolName" `
                    -CommandPath $toolExecutable `
                    -Arguments @('--help') `
                    -DiagnosticsDirectory (Join-Path $phaseDirectory $toolName)
            }
        })

        $fullDistributionRoot = Join-Path $testDiagnosticsDirectory 'distribution'
        $currentDistributionRoot = Join-Path $fullDistributionRoot 'current'
        $fullRunId = Split-Path -Leaf $testDiagnosticsDirectory
        $fullArtifactId = $fullRunId + '-distribution'
        $current = @(Invoke-MonitoredFullPhase -Name 'current-distribution-publish' -DiagnosticsRoot $testDiagnosticsDirectory -Action {
            param($phaseStopwatch, $phaseDirectory)
            $descriptor = Get-FullPhaseDescriptor -Name 'current-distribution-publish'
            Invoke-CurrentDistributionPublish `
                -Stopwatch $phaseStopwatch `
                -BudgetSeconds $descriptor.BudgetSeconds `
                -PhaseDirectory $phaseDirectory `
                -ArtifactRoot $currentDistributionRoot
        })[-1]

        $artifactManifest = @(Invoke-MonitoredFullPhase -Name 'baseline-preparation' -DiagnosticsRoot $testDiagnosticsDirectory -Action {
            param($phaseStopwatch, $phaseDirectory)
            $descriptor = Get-FullPhaseDescriptor -Name 'baseline-preparation'
            Invoke-BaselinePreparation `
                -Stopwatch $phaseStopwatch `
                -BudgetSeconds $descriptor.BudgetSeconds `
                -PhaseDirectory $phaseDirectory `
                -ArtifactRoot $fullDistributionRoot `
                -BaselineCommit $baselineCommit `
                -Current $current `
                -RunId $fullRunId `
                -ArtifactId $fullArtifactId
        })[-1]
        $artifactManifestPath = $artifactManifest.ManifestPath
        $expectedFullRunId = $fullRunId
        $expectedFullArtifactId = $fullArtifactId
        $expectedFullManifestSha256 = [string]$artifactManifest.ManifestSha256
        $expectedFullManifestSeal = (Get-Content -LiteralPath $artifactManifest.ManifestHashPath -Raw).Trim().ToLowerInvariant()
        Assert-DistributionArtifactIdentity `
            -ArtifactManifest $artifactManifest `
            -ExpectedRunId $expectedFullRunId `
            -ExpectedArtifactId $expectedFullArtifactId `
            -ExpectedManifestSha256 $expectedFullManifestSha256 `
            -ExpectedManifestSeal $expectedFullManifestSeal | Out-Null
        [Environment]::SetEnvironmentVariable('BMS_SCD_APP_PUBLISH_ROOT', [string]$artifactManifest.Current.appRoot, 'Process')
        [Environment]::SetEnvironmentVariable('BMS_SCD_UPDATER_PUBLISH_ROOT', [string]$artifactManifest.Current.updaterRoot, 'Process')

        [void](Invoke-MonitoredFullPhase -Name 'existing-data' -DiagnosticsRoot $testDiagnosticsDirectory -Action {
            param($phaseStopwatch, $phaseDirectory)
            $descriptor = Get-FullPhaseDescriptor -Name 'existing-data'
            Assert-DistributionArtifactIdentity `
                -ArtifactManifest (Read-DistributionArtifactManifest -ManifestPath $artifactManifestPath) `
                -ExpectedRunId $expectedFullRunId `
                -ExpectedArtifactId $expectedFullArtifactId `
                -ExpectedManifestSha256 $expectedFullManifestSha256 `
                -ExpectedManifestSeal $expectedFullManifestSeal | Out-Null
            Invoke-ExistingDataAcceptance `
                -Stopwatch $phaseStopwatch `
                -BudgetSeconds $descriptor.BudgetSeconds `
                -PhaseDirectory $phaseDirectory `
                -ArtifactManifestPath $artifactManifestPath
            Assert-DistributionArtifactIdentity `
                -ArtifactManifest (Read-DistributionArtifactManifest -ManifestPath $artifactManifestPath) `
                -ExpectedRunId $expectedFullRunId `
                -ExpectedArtifactId $expectedFullArtifactId `
                -ExpectedManifestSha256 $expectedFullManifestSha256 `
                -ExpectedManifestSeal $expectedFullManifestSeal | Out-Null
        })
        [void](Invoke-MonitoredFullPhase -Name 'update' -DiagnosticsRoot $testDiagnosticsDirectory -Action {
            param($phaseStopwatch, $phaseDirectory)
            $descriptor = Get-FullPhaseDescriptor -Name 'update'
            Assert-DistributionArtifactIdentity `
                -ArtifactManifest (Read-DistributionArtifactManifest -ManifestPath $artifactManifestPath) `
                -ExpectedRunId $expectedFullRunId `
                -ExpectedArtifactId $expectedFullArtifactId `
                -ExpectedManifestSha256 $expectedFullManifestSha256 `
                -ExpectedManifestSeal $expectedFullManifestSeal | Out-Null
            Invoke-UpdateAcceptance `
                -Stopwatch $phaseStopwatch `
                -BudgetSeconds $descriptor.BudgetSeconds `
                -PhaseDirectory $phaseDirectory `
                -ArtifactManifestPath $artifactManifestPath
            Assert-DistributionArtifactIdentity `
                -ArtifactManifest (Read-DistributionArtifactManifest -ManifestPath $artifactManifestPath) `
                -ExpectedRunId $expectedFullRunId `
                -ExpectedArtifactId $expectedFullArtifactId `
                -ExpectedManifestSha256 $expectedFullManifestSha256 `
                -ExpectedManifestSeal $expectedFullManifestSeal | Out-Null
        })

        [void](Invoke-MonitoredFullPhase -Name 'ProcessIntegration' -DiagnosticsRoot $testDiagnosticsDirectory -Action {
            param($phaseStopwatch, $phaseDirectory)
            $descriptor = Get-FullPhaseDescriptor -Name 'ProcessIntegration'
            Assert-DistributionArtifactIdentity `
                -ArtifactManifest (Read-DistributionArtifactManifest -ManifestPath $artifactManifestPath) `
                -ExpectedRunId $expectedFullRunId `
                -ExpectedArtifactId $expectedFullArtifactId `
                -ExpectedManifestSha256 $expectedFullManifestSha256 `
                -ExpectedManifestSeal $expectedFullManifestSeal | Out-Null
            $timeout = Get-FullPhaseRemainingSeconds -Stopwatch $phaseStopwatch -BudgetSeconds $descriptor.BudgetSeconds -PhaseName 'ProcessIntegration test lane'
            Invoke-TestLane `
                -Name 'Process integration' `
                -Filter 'TestCategory=ProcessIntegration' `
                -DiagnosticsDirectory (Join-Path $phaseDirectory 'test') `
                -TimeoutSeconds $timeout `
                -ReserveCleanupInsideTimeout `
                -NoBuild
            Assert-DistributionArtifactIdentity `
                -ArtifactManifest (Read-DistributionArtifactManifest -ManifestPath $artifactManifestPath) `
                -ExpectedRunId $expectedFullRunId `
                -ExpectedArtifactId $expectedFullArtifactId `
                -ExpectedManifestSha256 $expectedFullManifestSha256 `
                -ExpectedManifestSeal $expectedFullManifestSeal | Out-Null
        })
        [void](Invoke-MonitoredFullPhase -Name 'ReleaseAcceptance' -DiagnosticsRoot $testDiagnosticsDirectory -Action {
            param($phaseStopwatch, $phaseDirectory)
            $descriptor = Get-FullPhaseDescriptor -Name 'ReleaseAcceptance'
            Assert-DistributionArtifactIdentity `
                -ArtifactManifest (Read-DistributionArtifactManifest -ManifestPath $artifactManifestPath) `
                -ExpectedRunId $expectedFullRunId `
                -ExpectedArtifactId $expectedFullArtifactId `
                -ExpectedManifestSha256 $expectedFullManifestSha256 `
                -ExpectedManifestSeal $expectedFullManifestSeal | Out-Null
            $timeout = Get-FullPhaseRemainingSeconds -Stopwatch $phaseStopwatch -BudgetSeconds $descriptor.BudgetSeconds -PhaseName 'ReleaseAcceptance test lane'
            Invoke-TestLane `
                -Name 'Release acceptance' `
                -Filter 'TestCategory=ReleaseAcceptance' `
                -DiagnosticsDirectory (Join-Path $phaseDirectory 'test') `
                -TimeoutSeconds $timeout `
                -ReserveCleanupInsideTimeout `
                -NoBuild
            Assert-DistributionArtifactIdentity `
                -ArtifactManifest (Read-DistributionArtifactManifest -ManifestPath $artifactManifestPath) `
                -ExpectedRunId $expectedFullRunId `
                -ExpectedArtifactId $expectedFullArtifactId `
                -ExpectedManifestSha256 $expectedFullManifestSha256 `
                -ExpectedManifestSeal $expectedFullManifestSeal | Out-Null
        })

        [void](Invoke-MonitoredFullPhase -Name 'format' -DiagnosticsRoot $testDiagnosticsDirectory -Action {
            param($phaseStopwatch, $phaseDirectory)
            $descriptor = Get-FullPhaseDescriptor -Name 'format'
            Invoke-FullPhaseCommand `
                -Stopwatch $phaseStopwatch `
                -BudgetSeconds $descriptor.BudgetSeconds `
                -PhaseName 'format' `
                -Label 'dotnet format' `
                -CommandPath 'dotnet' `
                -Arguments (Get-RepositoryFormatArguments -WorkspaceRoot $repoRoot) `
                -DiagnosticsDirectory (Join-Path $phaseDirectory 'command')
        })

        [void](Invoke-MonitoredFullPhase -Name 'analyzer' -DiagnosticsRoot $testDiagnosticsDirectory -Action {
            param($phaseStopwatch, $phaseDirectory)
            $descriptor = Get-FullPhaseDescriptor -Name 'analyzer'
            $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
            $vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
            if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
                throw "vswhere.exe was not found: $vswhere"
            }
            $msbuildPath = (& $vswhere -version '[17.0,18.0)' -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin' | Select-Object -First 1).ToString().Trim()
            if ([string]::IsNullOrWhiteSpace($msbuildPath)) {
                throw 'Visual Studio 2022 MSBuild 17 was not found.'
            }
            Invoke-FullPhaseCommand `
                -Stopwatch $phaseStopwatch `
                -BudgetSeconds $descriptor.BudgetSeconds `
                -PhaseName 'analyzer' `
                -Label 'roslynator analyzer' `
                -CommandPath 'dotnet' `
                -Arguments @('roslynator', 'analyze', $solution, '--msbuild-path', $msbuildPath, '--properties', 'Configuration=Release', '--severity-level', 'warning', '--ignore-compiler-diagnostics', '--verbosity', 'minimal') `
                -DiagnosticsDirectory (Join-Path $phaseDirectory 'command')
        })
        Assert-DistributionArtifactIdentity `
            -ArtifactManifest (Read-DistributionArtifactManifest -ManifestPath $artifactManifestPath) `
            -ExpectedRunId $expectedFullRunId `
            -ExpectedArtifactId $expectedFullArtifactId `
            -ExpectedManifestSha256 $expectedFullManifestSha256 `
            -ExpectedManifestSeal $expectedFullManifestSeal | Out-Null
        Assert-RepositoryWhitespace
    }
    elseif (-not $canonicalFunctionalRequested) {
        $effectiveFilter = $TestFilter
        Invoke-FilteredQuickVerification `
            -Filter $effectiveFilter `
            -DiagnosticsRoot $testDiagnosticsDirectory `
            -TimeoutSeconds $FunctionalTimeoutSeconds
    }
}
catch {
    $verificationFailure = $_
}
finally {
    $environmentRestoreFailures = [System.Collections.Generic.List[string]]::new()
    foreach ($environmentEntry in @(
        [pscustomobject]@{ Name = 'BMS_SCD_APP_PUBLISH_ROOT'; Value = $previousAppPublishRoot },
        [pscustomobject]@{ Name = 'BMS_SCD_UPDATER_PUBLISH_ROOT'; Value = $previousUpdaterPublishRoot })) {
        try {
            [Environment]::SetEnvironmentVariable($environmentEntry.Name, $environmentEntry.Value, 'Process')
        }
        catch {
            [void]$environmentRestoreFailures.Add("$($environmentEntry.Name): $($_.Exception.Message)")
        }
    }
    try {
        Pop-Location
    }
    catch {
        [void]$environmentRestoreFailures.Add("working directory: $($_.Exception.Message)")
    }
    if ($environmentRestoreFailures.Count -gt 0) {
        $cleanupException = [Exception]::new("Full runner cleanup failed: $($environmentRestoreFailures -join '; ')")
        if ($null -eq $verificationFailure) {
            $verificationFailure = $cleanupException
        }
        else {
            Write-Warning $cleanupException.Message
        }
    }
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
