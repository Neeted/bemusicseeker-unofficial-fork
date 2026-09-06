[CmdletBinding()]
param(
    [ValidateSet('Quick', 'Functional', 'Full')]
    [string]$Mode = 'Quick',

    [string]$TestFilter,

    # This seam may lower the canonical budget so the timeout path can be
    # verified without waiting five minutes. It cannot relax the policy limit.
    [ValidateRange(1, 300)]
    [int]$FunctionalTimeoutSeconds = 300,

    # Internal probe-only guard.  A normal CLI string cannot satisfy the typed guard
    # checked by Invoke-MonitoredCommand; no environment variable enables this seam.
    [object]$InternalTestGuard
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'BeMusicSeeker.sln'
$uiExecutable = Join-Path $repoRoot 'bin\x64\Release\net10.0-windows\BeMusicSeeker.exe'
$toolExecutables = @(
    (Join-Path $repoRoot 'tools\chart-info-compare\bin\x64\Release\net10.0\ChartInfoCompare.exe'),
    (Join-Path $repoRoot 'tools\chart-info-export\bin\x64\Release\net10.0\ChartInfoExport.exe'))
$verificationArtifactsDirectory = Join-Path $repoRoot 'artifacts\verification'
$existingDataAcceptanceScript = Join-Path $repoRoot 'scripts\accept-net10-existing-data.ps1'
$updateAcceptanceScript = Join-Path $repoRoot 'scripts\accept-net10-update.ps1'
$v216FirstHopAcceptanceScript = Join-Path $repoRoot 'scripts\accept-v216-first-hop.ps1'
$v216ArtifactMetadataPath = Join-Path $repoRoot 'devdocs\acceptance\v216-first-hop\artifact.json'
$functionalReportingTargetSeconds = 180
. (Join-Path $PSScriptRoot 'verification-runner-contract.ps1')
. (Join-Path $PSScriptRoot 'verification-process-lifecycle.ps1')
. (Join-Path $PSScriptRoot 'distribution-artifact.ps1')
. (Join-Path $PSScriptRoot 'verification-test-outcomes.ps1')
$verificationRunnerContract = Get-VerificationRunnerContract

function Get-RepositoryFormatArguments {
    param(
        [Parameter(Mandatory)]
        [string]$WorkspaceRoot
    )

    $formatContract = $verificationRunnerContract.RepositoryFormat
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

$functionalPortableSettingsClass =
    'BeMusicSeeker.Tests.PlayerPanelStateSettingsCompatibilityTests'
$functionalBassCollectibleLoadContextClass =
    'BeMusicSeeker.Tests.BassCollectibleLoadContextTests'
$functionalSettingsForegroundInteractionClass =
    'BeMusicSeeker.Tests.SettingsForegroundInteractionTests'
$functionalSerialStateAClasses = @(
    'BeMusicSeeker.Tests.SettingsForegroundInteractionTests'
    'BeMusicSeeker.Tests.SettingDialogEditCompletionTests'
    'BeMusicSeeker.Tests.SettingsWindowPresentationTests'
    'BeMusicSeeker.Tests.ApplicationCompositionTests'
    'BeMusicSeeker.Tests.ApplicationSettingsLifecycleTests'
    'BeMusicSeeker.Tests.ApplicationUiSchedulerBoundaryTests'
    'BeMusicSeeker.Tests.BeatorajaBmtOptionsSnapshotTests'
    'BeMusicSeeker.Tests.BmsLibraryOptionsSnapshotTests'
    'BeMusicSeeker.Tests.CustomFolderOutputSettingsSnapshotTests'
    'BeMusicSeeker.Tests.MainWindowViewSettingsBoundaryTests'
    'BeMusicSeeker.Tests.PlayerSettingsGatewayTests'
    'BeMusicSeeker.Tests.PlaylistUrlCompletionOptionsSnapshotTests'
    'BeMusicSeeker.Tests.ResourceIconContractTests'
    'BeMusicSeeker.Tests.SettingDialogCustomFolderOutputBaseTests'
    'BeMusicSeeker.Tests.SettingDialogOpenCommandTests'
    'BeMusicSeeker.Tests.ShellShutdownWorkflowOwnerTests'
    'BeMusicSeeker.Tests.StartupSettingsSnapshotTests'
    'BeMusicSeeker.Tests.BmsPlaylistExternalReloadTests'
    'BeMusicSeeker.Tests.BmsPlaylistCustomFolderOutputTests'
    'BeMusicSeeker.Tests.BmsPlaylistPersistenceLifecycleTests'
    'BeMusicSeeker.Tests.BmsPlaylistMigrationAndRegistrationTests'
    'BeMusicSeeker.Tests.BassNativeRuntimeTests'
    'BeMusicSeeker.Tests.NLogWrapperTests')
$functionalSerialStateBClasses = @(
    'BeMusicSeeker.Tests.BmsLibraryLr2SongDbSyncTests'
    'BeMusicSeeker.Tests.LoadPlaylistURIDialogTests'
    'BeMusicSeeker.Tests.MainWindowChartPresentationWpfTests'
    'BeMusicSeeker.Tests.MainWindowPackageMaintenanceWpfTests'
    'BeMusicSeeker.Tests.MainWindowPlaybackWpfTests'
    'BeMusicSeeker.Tests.MainWindowPlayHistoryWpfTests'
    'BeMusicSeeker.Tests.MainWindowPlaylistWorkspaceWpfTests'
    'BeMusicSeeker.Tests.MainWindowProgressStatusBarWpfTests'
    'BeMusicSeeker.Tests.MainWindowSelectedChartContextMenuWpfTests'
    'BeMusicSeeker.Tests.MainWindowTreePresentationWpfTests'
    'BeMusicSeeker.Tests.MainWindowViewHostTests'
    'BeMusicSeeker.Tests.SettingsWindowCompiledBehaviorTests'
    'BeMusicSeeker.Tests.UiDialogCoordinatorWpfTests'
    'BeMusicSeeker.Tests.PlaybackPanelViewModelTests'
    'BeMusicSeeker.Tests.InstalledOnlyResourceOverwriteValidationTests'
    'BeMusicSeeker.Tests.LibraryFileScanPipelineOwnerTests'
    'BeMusicSeeker.Tests.Lr2PlayHistorySchemaUiTests'
    'BeMusicSeeker.Tests.MainWindowExternalShellTests'
    'BeMusicSeeker.Tests.PlayHistoryReadModelTests'
    'BeMusicSeeker.Tests.ApplicationStartupCompositionOwnerTests')
$functionalSettingsForegroundInteractionMethods = @(
    'BeMusicSeeker.Tests.SettingsForegroundInteractionTests.SettingsWindow_NavigationSupportsKeyboardAutomationAndResetsPageScroll'
    'BeMusicSeeker.Tests.SettingsForegroundInteractionTests.SettingsComboBox_HitTestingPreservesWholeSurfaceAndEditableTextRoutes'
    'BeMusicSeeker.Tests.SettingsForegroundInteractionTests.SettingsControlDictionary_OverridesOuterImplicitStylesAndMaterializesClosedRoutes'
    'BeMusicSeeker.Tests.SettingsForegroundInteractionTests.SettingsWindow_ManualResyncClosesAndQueuesForcedWorkflow'
    'BeMusicSeeker.Tests.SettingsForegroundInteractionTests.Lr2AdvancedPathsDialog_EnterCommitsFocusedEditorBeforeAccepting'
    'BeMusicSeeker.Tests.SettingsForegroundInteractionTests.Lr2AdvancedPathsDialog_EnterKeepsDialogOpenWhenFocusedCandidateIsRejected'
    'BeMusicSeeker.Tests.SettingsForegroundInteractionTests.Lr2AdvancedPathsDialog_InitialInvalidTupleStaysOpenAndFocusesRejectedEditor')
$functionalBmsLibrarySelector =
    'FullyQualifiedName~BeMusicSeeker.Tests.BmsLibrary'
$functionalRemainingShardWorkers = [Math]::Max(
    1,
    [Environment]::ProcessorCount)
$functionalHostNames = @(
    'portable-settings'
    'bass-collectible'
    'serial-state-a'
    'serial-state-b'
    'remaining-bms-library'
    'remaining')

function New-FunctionalShardPlan {
    $assignedClasses = @(
        $functionalPortableSettingsClass
        $functionalBassCollectibleLoadContextClass
        $functionalSerialStateAClasses
        $functionalSerialStateBClasses)
    $remainingClassFilter = ($assignedClasses |
        ForEach-Object { "FullyQualifiedName!~$_" }) -join '&'
    $remainingBaseFilter = "($functionalFilter)&($remainingClassFilter)"
    $hostDefinitions = @(
        [pscustomobject]@{
            Name = 'portable-settings'
            Workers = 1
            Scope = 'ClassLevel'
            Classes = [string[]]@($functionalPortableSettingsClass)
        }
        [pscustomobject]@{
            Name = 'bass-collectible'
            Workers = 1
            Scope = 'ClassLevel'
            Classes = [string[]]@($functionalBassCollectibleLoadContextClass)
        }
        [pscustomobject]@{
            Name = 'serial-state-a'
            Workers = 1
            Scope = 'ClassLevel'
            Classes = [string[]]@($functionalSerialStateAClasses)
        }
        [pscustomobject]@{
            Name = 'serial-state-b'
            Workers = 1
            Scope = 'ClassLevel'
            Classes = [string[]]@($functionalSerialStateBClasses)
        }
        [pscustomobject]@{
            Name = 'remaining-bms-library'
            Workers = $functionalRemainingShardWorkers
            Scope = 'ClassLevel'
            Classes = [string[]]@()
            ExcludedClasses = [string[]]$assignedClasses
            SelectorPolarity = 'Positive'
            Routing = 'logical-prefix'
        }
        [pscustomobject]@{
            Name = 'remaining'
            Workers = $functionalRemainingShardWorkers
            Scope = 'ClassLevel'
            Classes = [string[]]@()
            ExcludedClasses = [string[]]$assignedClasses
            SelectorPolarity = 'Negative'
            Routing = 'logical-prefix'
        })
    $shards = @($hostDefinitions | ForEach-Object {
        $classes = [string[]]@($_.Classes)
        $hasLogicalPrefixSelector = $_.PSObject.Properties.Name -contains 'SelectorPolarity'
        $selectorFilter = if (-not $hasLogicalPrefixSelector) {
            [string]::Empty
        }
        elseif ([string]$_.SelectorPolarity -ceq 'Positive') {
            $functionalBmsLibrarySelector
        }
        else {
            $functionalBmsLibrarySelector.Replace('~', '!~', [StringComparison]::Ordinal)
        }
        $filter = if ($hasLogicalPrefixSelector) {
            "($remainingBaseFilter)&($selectorFilter)"
        }
        elseif ($classes.Count -eq 0) {
            [string]::Empty
        }
        else {
            $classFilter = ($classes | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
            "($functionalFilter)&($classFilter)"
        }
        [pscustomobject][ordered]@{
            Name = $_.Name
            Workers = [int]$_.Workers
            Scope = [string]$_.Scope
            Classes = $classes
            BaseFilter = if ($hasLogicalPrefixSelector) { $remainingBaseFilter } else { [string]::Empty }
            Selector = if ($hasLogicalPrefixSelector) { $functionalBmsLibrarySelector } else { [string]::Empty }
            SelectorPolarity = if ($hasLogicalPrefixSelector) { [string]$_.SelectorPolarity } else { [string]::Empty }
            SelectorFilter = $selectorFilter
            Routing = if ($hasLogicalPrefixSelector) { [string]$_.Routing } else { [string]::Empty }
            Filter = $filter
            ExcludedClasses = if ($_.PSObject.Properties.Name -contains 'ExcludedClasses') {
                [string[]]$_.ExcludedClasses
            }
            else {
                [string[]]@()
            }
        }
    })
    return [pscustomobject][ordered]@{
        Shards = [object[]]$shards
        ForegroundInteractionMethods = [string[]]$functionalSettingsForegroundInteractionMethods
    }
}

function Assert-FunctionalShardConfiguration {
    param(
        [Parameter(Mandatory)]
        [object]$Plan
    )

    if ($null -eq $Plan.Shards -or
        $null -eq $Plan.ForegroundInteractionMethods) {
        throw 'Functional launch plan must contain one executable shard array and the foreground allowlist.'
    }

    $shards = @($Plan.Shards)
    if ($shards.Count -ne $functionalHostNames.Count) {
        throw 'Functional launch plan must contain portable, BASS, serial A, serial B, BmsLibrary remaining, and remaining exactly once.'
    }
    $names = @($shards | ForEach-Object { [string]$_.Name })
    if (@($names | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0 -or
        ($names | Sort-Object -Unique).Count -ne $names.Count) {
        throw 'Functional launch host names must be nonempty and unique.'
    }
    if (@(Compare-Object -ReferenceObject $functionalHostNames -DifferenceObject $names -CaseSensitive).Count -ne 0) {
        throw 'Functional launch hosts must use the exact portable/BASS/serial A/serial B/BmsLibrary remaining/remaining names.'
    }
    if (($functionalHostNames -join '|') -cne ($names -join '|')) {
        throw 'Functional launch hosts must preserve portable-first then Bass/A/B/BmsLibrary remaining/remaining order.'
    }

    $remainingPartitionNames = @('remaining-bms-library', 'remaining')
    $assignedClasses = @()
    foreach ($shard in $shards) {
        $classes = [string[]]@($shard.Classes)
        if ($remainingPartitionNames -contains [string]$shard.Name) {
            if ($classes.Count -ne 0) {
                throw "Functional $($shard.Name) host must rely on logical-prefix routing rather than class selectors."
            }
            continue
        }
        if (@($shard.ExcludedClasses).Count -ne 0) {
            throw "Functional host '$($shard.Name)' must not carry remaining-only exclusions."
        }
        if ($classes.Count -eq 0 -or
            @($classes | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
            throw "Functional host '$($shard.Name)' must contain nonempty class selectors."
        }
        $assignedClasses += $classes
        $classFilter = ($classes | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
        $expectedFilter = "($functionalFilter)&($classFilter)"
        if ($shard.Filter -cne $expectedFilter) {
            throw "Functional host '$($shard.Name)' must carry the exact executable class filter."
        }
    }

    $expectedAssignedClasses = @(
        $functionalPortableSettingsClass
        $functionalBassCollectibleLoadContextClass
        $functionalSerialStateAClasses
        $functionalSerialStateBClasses)
    if ($assignedClasses.Count -ne 45 -or
        $expectedAssignedClasses.Count -ne 45 -or
        @(Compare-Object -ReferenceObject $expectedAssignedClasses -DifferenceObject $assignedClasses -CaseSensitive).Count -ne 0) {
        throw 'Functional assigned selectors must be the exact portable/BASS/serial A/serial B set of 45 classes.'
    }
    if (($assignedClasses | Sort-Object -Unique).Count -ne $assignedClasses.Count) {
        throw 'Functional assigned selectors must be unique and logically disjoint.'
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

    $remainingExclusionFilter = ($expectedAssignedClasses |
        ForEach-Object { "FullyQualifiedName!~$_" }) -join '&'
    $remainingBaseFilter = "($functionalFilter)&($remainingExclusionFilter)"
    $remainingPartitions = @($shards | Where-Object {
        $remainingPartitionNames -contains [string]$_.Name
    })
    if ($remainingPartitions.Count -ne 2) {
        throw 'Functional remaining must be exactly the two logical-prefix partition hosts.'
    }
    $positiveSelectorFilter = $functionalBmsLibrarySelector
    $negativeSelectorFilter = $functionalBmsLibrarySelector.Replace('~', '!~', [StringComparison]::Ordinal)
    $positivePartition = @($remainingPartitions | Where-Object { $_.SelectorPolarity -ceq 'Positive' })
    $negativePartition = @($remainingPartitions | Where-Object { $_.SelectorPolarity -ceq 'Negative' })
    if ($positivePartition.Count -ne 1 -or $negativePartition.Count -ne 1) {
        throw 'Functional BmsLibrary remaining partitions must have one positive and one negative selector.'
    }
    foreach ($partition in $remainingPartitions) {
        if ($partition.BaseFilter -cne $remainingBaseFilter -or
            $partition.Selector -cne $functionalBmsLibrarySelector -or
            $partition.Routing -cne 'logical-prefix' -or
            $partition.Workers -ne $functionalRemainingShardWorkers -or
            $partition.Scope -ne 'ClassLevel' -or
            @($partition.Classes).Count -ne 0 -or
            @($partition.ExcludedClasses).Count -ne 45 -or
            @(Compare-Object -ReferenceObject $expectedAssignedClasses -DifferenceObject @($partition.ExcludedClasses) -CaseSensitive).Count -ne 0) {
            throw "Functional BmsLibrary partition '$($partition.Name)' must share R, use ProcessorCount/ClassLevel, and have no exact class allowlist."
        }
    }
    if ($positivePartition[0].SelectorFilter -cne $positiveSelectorFilter -or
        $negativePartition[0].SelectorFilter -cne $negativeSelectorFilter -or
        $positivePartition[0].SelectorFilter -ceq $negativePartition[0].SelectorFilter -or
        $positivePartition[0].SelectorPolarity -ceq $negativePartition[0].SelectorPolarity) {
        throw 'Functional BmsLibrary partitions must use the common selector with opposite polarity.'
    }
    foreach ($partition in $remainingPartitions) {
        if ($partition.Filter -cne "($remainingBaseFilter)&($($partition.SelectorFilter))") {
            throw "Functional BmsLibrary partition '$($partition.Name)' must be generated from the shared exclusion base and selector predicate."
        }
    }
    # The shared base plus one positive and one negative predicate is the complete,
    # disjoint logical partition of R; no exact BmsLibrary class allowlist can widen it.
    if ($positivePartition[0].BaseFilter -cne $negativePartition[0].BaseFilter) {
        throw 'Functional BmsLibrary partitions must have a disjoint union over one shared R base filter.'
    }

    $portableShard = @($shards | Where-Object { $_.Name -ceq 'portable-settings' })[0]
    $bassShard = @($shards | Where-Object { $_.Name -ceq 'bass-collectible' })[0]
    $serialA = @($shards | Where-Object { $_.Name -ceq 'serial-state-a' })[0]
    $serialB = @($shards | Where-Object { $_.Name -ceq 'serial-state-b' })[0]
    if ($portableShard.Workers -ne 1 -or $portableShard.Scope -ne 'ClassLevel' -or
        @($portableShard.Classes).Count -ne 1 -or
        $portableShard.Classes[0] -ne $functionalPortableSettingsClass) {
        throw 'Functional portable settings host must own its exact single class with one ClassLevel worker.'
    }
    if ($bassShard.Workers -ne 1 -or $bassShard.Scope -ne 'ClassLevel' -or
        @($bassShard.Classes).Count -ne 1 -or
        $bassShard.Classes[0] -ne $functionalBassCollectibleLoadContextClass) {
        throw 'Functional BASS host must own its exact single class with one ClassLevel worker.'
    }
    foreach ($pair in @(
            [pscustomobject]@{ Name = 'serial-state-a'; Shard = $serialA; Classes = $functionalSerialStateAClasses }
            [pscustomobject]@{ Name = 'serial-state-b'; Shard = $serialB; Classes = $functionalSerialStateBClasses })) {
        if ($pair.Shard.Workers -ne 1 -or $pair.Shard.Scope -ne 'ClassLevel' -or
            @($pair.Shard.Classes).Count -ne @($pair.Classes).Count -or
            @(Compare-Object -ReferenceObject @($pair.Classes) -DifferenceObject @($pair.Shard.Classes) -CaseSensitive).Count -ne 0) {
            throw "Functional $($pair.Name) must preserve its exact Unit4e class selectors with one ClassLevel worker."
        }
    }

    $foregroundMethods = [string[]]@($Plan.ForegroundInteractionMethods)
    if ($foregroundMethods.Count -ne 7 -or
        @(Compare-Object -ReferenceObject $functionalSettingsForegroundInteractionMethods -DifferenceObject $foregroundMethods -CaseSensitive).Count -ne 0) {
        throw 'Functional foreground interaction allowlist must contain the exact seven current SettingsForegroundInteractionTests methods.'
    }
    foreach ($method in $foregroundMethods) {
        if (-not $method.StartsWith(
                "$functionalSettingsForegroundInteractionClass.",
                [StringComparison]::Ordinal)) {
            throw 'Functional foreground interaction methods must all belong to SettingsForegroundInteractionTests.'
        }
    }
    if (@($serialA.Classes) -notcontains $functionalSettingsForegroundInteractionClass) {
        throw 'Functional serial-state-a must own the foreground interaction fixture.'
    }
    foreach ($shard in $shards | Where-Object { $_.Name -cne 'serial-state-a' }) {
        if (@($shard.Classes) -contains $functionalSettingsForegroundInteractionClass) {
            throw "Foreground interaction fixture leaked into host '$($shard.Name)'."
        }
    }

    $retiredFqns = @(
        'BeMusicSeeker.Tests.BmsPlaylistUpdateTests'
        'BeMusicSeeker.Tests.PlaylistWorkspaceViewModelTests'
        'BeMusicSeeker.Tests.OwnedChartCollectionStateTests'
        'BeMusicSeeker.Tests.PlaylistSummaryAggregationTests'
        'BeMusicSeeker.Tests.RegularChartListOwnerTests'
        'BeMusicSeeker.Tests.ChartInfoMetadataTests'
        'BeMusicSeeker.Tests.ChartInfoMetadataSchemaExportImportTests'
        'BeMusicSeeker.Tests.ChartInfoParserBehaviorTests'
        'BeMusicSeeker.Tests.ChartInfoBackfillStorageTests'
        'BeMusicSeeker.Tests.ChartInfoInlineHydrationTests'
        'BeMusicSeeker.Tests.ChartInfoInstallFailureRetryTests'
        'BeMusicSeeker.Tests.BmsLibraryInitializationServiceTests'
        'BeMusicSeeker.Tests.StartupLibraryConstructionOwnerTests'
        'BeMusicSeeker.Tests.SettingsWindowPresentationTests.SettingsWindow_ManualResyncClosesAndQueuesForcedWorkflow'
        'BeMusicSeeker.Tests.SettingDialogEditCompletionTests.Lr2AdvancedPathsDialog_EnterCommitsFocusedEditorBeforeAccepting'
        'BeMusicSeeker.Tests.SettingDialogEditCompletionTests.Lr2AdvancedPathsDialog_EnterKeepsDialogOpenWhenFocusedCandidateIsRejected'
        'BeMusicSeeker.Tests.SettingDialogEditCompletionTests.Lr2AdvancedPathsDialog_InitialInvalidTupleStaysOpenAndFocusesRejectedEditor')
    $routeText = @(
        $shards | ForEach-Object {
            @($_.Classes)
            [string]$_.Filter
            @($_.ExcludedClasses)
        }) -join [Environment]::NewLine
    foreach ($retiredFqn in $retiredFqns) {
        if ($routeText.Contains($retiredFqn, [StringComparison]::Ordinal)) {
            throw "Retired Functional selector remains in the executable launch plan: $retiredFqn"
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

function New-FunctionalDeadlinePolicy {
    param(
        [Parameter(Mandatory)]
        [DateTime]$StartUtc,

        [Parameter(Mandatory)]
        [ValidateRange(1, 300)]
        [int]$TimeoutSeconds
    )

    $pair = Resolve-VerificationDeadlinePair `
        -StartUtc $StartUtc `
        -TimeoutSeconds $TimeoutSeconds
    return [pscustomobject][ordered]@{
        StartUtc = $pair.StartUtc
        TimeoutSeconds = $pair.TimeoutSeconds
        ExecutionDeadlineUtc = $pair.ExecutionDeadlineUtc
        FailureCleanupDeadlineUtc = $pair.CleanupDeadlineUtc
    }
}

function Get-RemainingBudgetSeconds {
    param(
        [System.Diagnostics.Stopwatch]$Stopwatch,

        [int]$BudgetSeconds,

        [DateTime]$DeadlineUtc,

        [DateTime]$NowUtc = [DateTime]::UtcNow
    )

    if ($PSBoundParameters.ContainsKey('DeadlineUtc')) {
        $remaining = ($DeadlineUtc.ToUniversalTime() - $NowUtc.ToUniversalTime()).TotalSeconds
    }
    else {
        if ($null -eq $Stopwatch -or -not $PSBoundParameters.ContainsKey('BudgetSeconds')) {
            throw 'A stopwatch and budget or an absolute execution deadline are required.'
        }
        $remaining = $BudgetSeconds - $Stopwatch.Elapsed.TotalSeconds
    }
    if ($remaining -le 0) {
        throw 'Functional verification reached its configured execution deadline.'
    }

    return [Math]::Max(1, [int][Math]::Floor($remaining))
}

function Assert-FunctionalExecutionDeadline {
    param(
        [Parameter(Mandatory)]
        [object]$DeadlinePolicy,

        [Parameter(Mandatory)]
        [string]$StageName
    )

    if ([DateTime]::UtcNow -ge $DeadlinePolicy.ExecutionDeadlineUtc) {
        throw "Functional execution deadline reached before $StageName. Configured execution deadline: $($DeadlinePolicy.ExecutionDeadlineUtc.ToString('O')). Failure cleanup deadline: $($DeadlinePolicy.FailureCleanupDeadlineUtc.ToString('O'))."
    }
}

function Get-FunctionalProcessExitTimeUtc {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process
    )

    try {
        if (-not $Process.HasExited) {
            return $null
        }

        # ExitTime is read from the retained handle after HasExited has refreshed the
        # process state.  It is the only timestamp that can classify an exit observed
        # after the polling deadline without turning a late exit into a success.
        return $Process.ExitTime.ToUniversalTime()
    }
    catch {
        # A process can disappear between HasExited and ExitTime.  Treat an unavailable
        # timestamp as still running; the absolute deadline will then fail closed.
        return $null
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
        [ValidateRange(1, 300)]
        [int]$TimeoutSeconds,

        [object]$PostStartFaultGuard
    )

    $deadlinePolicy = Resolve-VerificationDeadlinePair -TimeoutSeconds $TimeoutSeconds

    $result = Invoke-VerificationMonitoredCommand `
        -Label $Label `
        -CommandPath $CommandPath `
        -Arguments $Arguments `
        -WorkingDirectory $WorkingDirectory `
        -DiagnosticsDirectory $DiagnosticsDirectory `
        -DeadlinePolicy $deadlinePolicy `
        -PostStartFaultGuard $PostStartFaultGuard
    Write-Host "$Label elapsed: $([Math]::Round($result.ElapsedMilliseconds / 1000, 1))s; diagnostics: $DiagnosticsDirectory"
}
function Get-VerificationPhaseDescriptor {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    $descriptor = @($verificationRunnerContract.PhaseDescriptors |
        Where-Object { $_.Name -ceq $Name })
    if ($descriptor.Count -ne 1) {
        throw "Verification runner phase descriptor is missing or duplicated: $Name"
    }
    return $descriptor[0]
}

function Invoke-VerificationPhaseCommand {
    param(
        [Parameter(Mandatory)]
        [string]$Label,

        [Parameter(Mandatory)]
        [string]$CommandPath,

        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory,

        [Parameter(Mandatory)]
        [object]$DeadlinePolicy,

        [string]$WorkingDirectory
    )

    if ([string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $WorkingDirectory = $repoRoot
    }
    Invoke-VerificationMonitoredCommand `
        -Label $Label `
        -CommandPath $CommandPath `
        -Arguments $Arguments `
        -WorkingDirectory $WorkingDirectory `
        -DiagnosticsDirectory $DiagnosticsDirectory `
        -DeadlinePolicy $DeadlinePolicy
}

function Write-VerificationPhaseResult {
    param(
        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory,

        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Result
    )

    [void](New-Item -ItemType Directory -Path $DiagnosticsDirectory -Force)
    $path = Join-Path $DiagnosticsDirectory 'phase-result.json'
    [IO.File]::WriteAllText(
        $path,
        ($Result | ConvertTo-Json -Depth 12),
        [Text.UTF8Encoding]::new($false))
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Verification phase result was not written: $path"
    }
}

function Invoke-MonitoredVerificationPhase {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$DiagnosticsRoot,

        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [scriptblock]$Cleanup
    )

    $descriptor = Get-VerificationPhaseDescriptor -Name $Name
    $phaseDeadlinePolicy = Resolve-VerificationDeadlinePair `
        -TimeoutSeconds ([int]$descriptor.BudgetSeconds)
    $phaseStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $phaseDirectory = Join-Path $DiagnosticsRoot $descriptor.DiagnosticsSegment
    [void](New-Item -ItemType Directory -Path $phaseDirectory -Force)
    $primaryFailure = $null
    $cleanupFailure = $null
    $actionOutput = @()
    try {
        try {
            $actionOutput = @(& $Action $phaseStopwatch $phaseDirectory $phaseDeadlinePolicy)
            if ([DateTime]::UtcNow -ge $phaseDeadlinePolicy.ExecutionDeadlineUtc -or
                $phaseStopwatch.Elapsed.TotalSeconds -gt [int]$descriptor.BudgetSeconds) {
                $primaryFailure = [TimeoutException]::new(
                    "Verification phase '$Name' exceeded the $($descriptor.BudgetSeconds)-second budget.")
            }
        }
        catch {
            if ($null -eq $primaryFailure) {
                $primaryFailure = $_.Exception
            }
        }
    }
    finally {
        if ($null -ne $Cleanup) {
            try {
                & $Cleanup $phaseDeadlinePolicy $phaseDirectory
            }
            catch {
                $cleanupFailure = $_.Exception
            }
        }
        $phaseStopwatch.Stop()
        if ($null -eq $primaryFailure -and
            ([DateTime]::UtcNow -ge $phaseDeadlinePolicy.ExecutionDeadlineUtc -or
            $phaseStopwatch.Elapsed.TotalSeconds -gt [int]$descriptor.BudgetSeconds)) {
            $primaryFailure = [TimeoutException]::new(
                "Verification phase '$Name' exceeded the $($descriptor.BudgetSeconds)-second budget.")
        }
        $result = [ordered]@{
            schemaVersion = 1
            phase = $Name
            budgetSeconds = [int]$descriptor.BudgetSeconds
            executionDeadlineUtc = $phaseDeadlinePolicy.ExecutionDeadlineUtc.ToString('O')
            cleanupDeadlineUtc = $phaseDeadlinePolicy.CleanupDeadlineUtc.ToString('O')
            elapsedSeconds = [Math]::Round($phaseStopwatch.Elapsed.TotalSeconds, 3)
            status = if ($null -eq $primaryFailure -and $null -eq $cleanupFailure) { 'passed' } elseif ($null -ne $primaryFailure) { 'failed' } else { 'cleanup-failed' }
            primaryFailure = if ($null -ne $primaryFailure) { $primaryFailure.ToString() } else { $null }
            cleanupFailure = if ($null -ne $cleanupFailure) { $cleanupFailure.ToString() } else { $null }
        }
        try {
            Write-VerificationPhaseResult -DiagnosticsDirectory $phaseDirectory -Result $result
        }
        catch {
            # A phase-result write is part of the phase's durable outcome. Preserve an
            # existing primary failure, but never let a write failure disappear.
            if ($null -eq $primaryFailure) {
                $primaryFailure = $_.Exception
            }
            elseif ($null -eq $cleanupFailure) {
                $cleanupFailure = $_.Exception
            }
            else {
                Write-Warning "Verification phase '$Name' result persistence also failed: $($_.Exception.Message)"
            }
        }
    }

    if ($null -ne $primaryFailure) {
        if ($null -ne $cleanupFailure) {
            Write-Warning "Verification phase '$Name' cleanup also failed after the primary failure: $($cleanupFailure.Message)"
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
        [string]$DiagnosticsDirectory
    )

    $remainingParameters = @{
        Stopwatch = $Stopwatch
        BudgetSeconds = $BudgetSeconds
    }
    $remainingSeconds = Get-RemainingBudgetSeconds @remainingParameters
    $invokeParameters = @{
        Label = $Label
        CommandPath = $CommandPath
        Arguments = $Arguments
        WorkingDirectory = $repoRoot
        DiagnosticsDirectory = $DiagnosticsDirectory
        TimeoutSeconds = $remainingSeconds
    }
    Invoke-MonitoredCommand @invokeParameters
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

        [string]$RunSettingsPath,

        [Parameter(Mandatory)]
        [object]$DeadlinePolicy,

        [switch]$NoBuild
    )

    $arguments = Get-TestArguments `
        -Filter $Filter `
        -DiagnosticsDirectory $DiagnosticsDirectory `
        -RunSettingsPath $RunSettingsPath `
        -NoBuild:$NoBuild
    Invoke-VerificationMonitoredCommand `
        -Label "$Name test lane" `
        -CommandPath 'dotnet' `
        -Arguments $arguments `
        -WorkingDirectory $repoRoot `
        -DiagnosticsDirectory $DiagnosticsDirectory `
        -DeadlinePolicy $DeadlinePolicy
}

function Start-FunctionalShardProcess {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Shard,

        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory,

        [Parameter(Mandatory)]
        [System.Collections.IList]$Entries,

        [Parameter(Mandatory)]
        [System.Collections.IList]$OwnedProcessRecords,

        [object]$PostStartFaultGuard,

        [Parameter(Mandatory)]
        [string]$RunSettingsPath
    )

    $arguments = Get-TestArguments `
        -Filter $Shard.Filter `
        -DiagnosticsDirectory $DiagnosticsDirectory `
        -RunSettingsPath $RunSettingsPath `
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
    $ownershipRecord = $null
    try {
        if (-not $process.Start()) {
            throw "Unable to start functional test shard '$($Shard.Name)'."
        }
        $started = $true
        $commandIdentity = "dotnet $($arguments -join ' ')"
        # Retain the raw Process handle before any subsequent start/stream/identity
        # operation can throw.  The caller's single ownership ledger then cleans up
        # both fully recorded entries and a process whose entry construction failed.
        $ownershipRecord = [pscustomobject][ordered]@{
            Name = $Shard.Name
            Directory = $DiagnosticsDirectory
            Process = $process
            ProcessId = $process.Id
            RootProcessIdentity = $null
            CommandIdentity = $commandIdentity
            StandardOutputTask = $null
            StandardErrorTask = $null
            Entry = $null
        }
        [void]$OwnedProcessRecords.Add($ownershipRecord)
        if ($null -ne $PostStartFaultGuard) {
            if ($PostStartFaultGuard -isnot [VerificationPostStartFaultGuard]) {
                throw 'Post-start fault guard was not created by the internal deterministic probe.'
            }
            if ($PostStartFaultGuard.TryConsumeSignal()) {
                throw "Internal post-start fault injection for $($Shard.Name)."
            }
        }
        $identity = Get-VerificationProcessIdentity -Process $process -CommandIdentity $commandIdentity
        $ownershipRecord.RootProcessIdentity = "$($identity.StartTimeUtcTicks)|$($identity.ProcessId)"
        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        $ownershipRecord.StandardOutputTask = $standardOutputTask
        $ownershipRecord.StandardErrorTask = $standardErrorTask
        $entry = [pscustomobject]@{
            Name = $Shard.Name
            Directory = $DiagnosticsDirectory
            Process = $process
            ProcessId = $process.Id
            RootProcessIdentity = "$($identity.StartTimeUtcTicks)|$($identity.ProcessId)"
            CommandIdentity = $commandIdentity
            StandardOutputTask = $standardOutputTask
            StandardErrorTask = $standardErrorTask
        }
        $ownershipRecord.Entry = $entry
        [void]$Entries.Add($entry)
        return $entry
    }
    catch {
        if (-not $started) {
            $process.Dispose()
        }
        else {
            Write-Warning "$($Shard.Name): start/entry construction failed; retaining exact process ownership for common cleanup: $($_.Exception.Message)"
        }
        throw
    }
}

function Convert-FunctionalRawOwnershipRecordsToEntries {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IList]$Entries,

        [Parameter(Mandatory)]
        [System.Collections.IList]$OwnedProcessRecords,

        [Parameter(Mandatory)]
        [object]$CleanupFailures
    )

    foreach ($record in @($OwnedProcessRecords)) {
        if ($null -ne $record.Entry) {
            if (@($Entries | Where-Object { [object]::ReferenceEquals($_, $record.Entry) }).Count -eq 0) {
                [void]$Entries.Add($record.Entry)
            }
            continue
        }
        if ($null -eq $record.Process) {
            $CleanupFailures.Add("$($record.Name): started process ownership record did not retain a process handle")
            continue
        }
        try {
            $identity = Get-VerificationProcessIdentity `
                -Process $record.Process `
                -CommandIdentity $record.CommandIdentity
            if ($null -eq $identity.StartTimeUtcTicks) {
                throw 'Process creation identity was unavailable after start failure.'
            }
            $record.RootProcessIdentity = "$($identity.StartTimeUtcTicks)|$($identity.ProcessId)"
            $record.Entry = [pscustomobject]@{
                Name = $record.Name
                Directory = $record.Directory
                Process = $record.Process
                ProcessId = $record.ProcessId
                RootProcessIdentity = $record.RootProcessIdentity
                CommandIdentity = $record.CommandIdentity
                StandardOutputTask = if ($null -ne $record.StandardOutputTask) {
                    $record.StandardOutputTask
                }
                else {
                    [System.Threading.Tasks.Task[string]]::FromResult([string]::Empty)
                }
                StandardErrorTask = if ($null -ne $record.StandardErrorTask) {
                    $record.StandardErrorTask
                }
                else {
                    [System.Threading.Tasks.Task[string]]::FromResult([string]::Empty)
                }
            }
            [void]$Entries.Add($record.Entry)
        }
        catch {
            $CleanupFailures.Add(
                "$($record.Name): raw process ownership could not be promoted to an exact lifecycle entry: $($_.Exception.Message)")
        }
    }
}

function Invoke-ParallelFunctionalTestShards {
    param(
        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory,

        [Parameter(Mandatory)]
        [ValidateRange(1, 300)]
        [int]$TimeoutSeconds
    )

    # Build once, validate once, and keep every descriptor as the object consumed by
    # the launch loops. There is no metadata-only fanout or second allowlist route.
    $shardPlan = New-FunctionalShardPlan
    Assert-FunctionalShardConfiguration -Plan $shardPlan
    $shards = @($shardPlan.Shards)
    $portableShard = @($shards | Where-Object { $_.Name -ceq 'portable-settings' })[0]
    $fanoutShards = @($shards | Where-Object { $_.Name -cne 'portable-settings' })
    if ($fanoutShards.Count -ne 5) {
        throw 'Functional launch plan must start exactly five hosts after portable settings completes.'
    }
    foreach ($fanoutShard in $fanoutShards) {
        if (@($shards | Where-Object { [object]::ReferenceEquals($_, $fanoutShard) }).Count -ne 1) {
            throw 'Functional fanout must use the exact validated host objects.'
        }
    }

    # Allocate every Functional entry directory before any monitored process is launched.
    # Lifecycle cleanup receives only pre-created paths and therefore cannot begin a new
    # filesystem operation for a later entry after the shared cleanup deadline.
    [void](New-Item -ItemType Directory -Path $DiagnosticsDirectory -Force)
    $shardDirectories = @($shards | ForEach-Object { Join-Path $DiagnosticsDirectory $_.Name })
    foreach ($directory in $shardDirectories) {
        [void](New-Item -ItemType Directory -Path $directory -Force)
    }

    $shardRunSettingsPaths = @{}
    foreach ($shard in $shards) {
        $shardDirectory = Join-Path $DiagnosticsDirectory $shard.Name
        $runSettingsPath = Join-Path $shardDirectory 'parallel.runsettings'
        Write-MSTestParallelRunSettings -Path $runSettingsPath -Workers $shard.Workers -Scope $shard.Scope
        $shardRunSettingsPaths[$shard.Name] = $runSettingsPath
    }
    $portableDirectory = Join-Path $DiagnosticsDirectory $portableShard.Name
    $portableRunSettingsPath = [string]$shardRunSettingsPaths[$portableShard.Name]
    $entries = [System.Collections.Generic.List[object]]::new()
    $ownedProcessRecords = [System.Collections.Generic.List[object]]::new()
    $timedOut = $false
    $timedOutHostNames = @()
    $launchFailure = $null
    $failedHost = $null
    $failedHostName = $null
    $failedHostExitCode = $null
    $cleanupFailures = [System.Collections.Generic.List[string]]::new()
    # Retain each actual process ExitTime once observed.  Polling can observe a
    # completed host after the deadline, and that observation delay is outside the
    # successful execution interval.
    $retainedFunctionalExitTimesUtc = @{}
    $testExecutionStopwatch = $null
    $deadlinePolicy = $null

    try {
        # The Functional budget starts at the portable dotnet test boundary.  Everything
        # above this point is test setup; the same policy then covers portable completion,
        # fanout launch, and every test host until the last host exits.
        $testExecutionStartedUtc = [DateTime]::UtcNow
        $deadlinePolicy = New-FunctionalDeadlinePolicy `
            -StartUtc $testExecutionStartedUtc `
            -TimeoutSeconds $TimeoutSeconds
        $executionDeadlineUtc = [DateTime]$deadlinePolicy.ExecutionDeadlineUtc
        $failureCleanupDeadlineUtc = [DateTime]$deadlinePolicy.FailureCleanupDeadlineUtc
        $testExecutionStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $portableEntry = Start-FunctionalShardProcess `
            -Shard $portableShard `
            -DiagnosticsDirectory $portableDirectory `
            -Entries $entries `
            -OwnedProcessRecords $ownedProcessRecords `
            -PostStartFaultGuard $InternalTestGuard `
            -RunSettingsPath $portableRunSettingsPath

        $portableExitTimeUtc = $null
        while ($true) {
            if ($retainedFunctionalExitTimesUtc.ContainsKey($portableEntry.Name)) {
                $portableExitTimeUtc = $retainedFunctionalExitTimesUtc[$portableEntry.Name]
            }
            else {
                $portableExitTimeUtc = Get-FunctionalProcessExitTimeUtc -Process $portableEntry.Process
                if ($null -ne $portableExitTimeUtc) {
                    $retainedFunctionalExitTimesUtc[$portableEntry.Name] = $portableExitTimeUtc
                }
            }
            if ($null -ne $portableExitTimeUtc) {
                break
            }
            if ([DateTime]::UtcNow -ge $executionDeadlineUtc) {
                $timedOut = $true
                $timedOutHostNames = @($portableEntry.Name)
                Write-Warning "Functional portable test host reached the configured execution deadline $($executionDeadlineUtc.ToString('O')). Owned-process cleanup is bounded by $($failureCleanupDeadlineUtc.ToString('O'))."
                break
            }
            Start-Sleep -Milliseconds 100
        }

        if (-not $timedOut -and $null -ne $portableExitTimeUtc) {
            if ($portableExitTimeUtc -gt $executionDeadlineUtc) {
                $timedOut = $true
                $timedOutHostNames = @($portableEntry.Name)
                Write-Warning "Functional portable test host exited at $($portableExitTimeUtc.ToString('O')), after the configured execution deadline $($executionDeadlineUtc.ToString('O')). Owned-process cleanup is bounded by $($failureCleanupDeadlineUtc.ToString('O'))."
            }
            elseif ($portableEntry.Process.ExitCode -ne 0) {
                $failedHost = $portableEntry
            }
        }

        # The five hosts begin from this same validated array without a phase barrier.
        if (-not $timedOut -and $null -eq $failedHost -and $null -ne $portableExitTimeUtc) {
            foreach ($fanoutShard in $fanoutShards) {
                Assert-FunctionalExecutionDeadline -DeadlinePolicy $deadlinePolicy -StageName "starting $($fanoutShard.Name)"
                $shardDirectory = Join-Path $DiagnosticsDirectory $fanoutShard.Name
                [void](Start-FunctionalShardProcess `
                    -Shard $fanoutShard `
                    -DiagnosticsDirectory $shardDirectory `
                    -Entries $entries `
                    -OwnedProcessRecords $ownedProcessRecords `
                    -PostStartFaultGuard $InternalTestGuard `
                    -RunSettingsPath ([string]$shardRunSettingsPaths[$fanoutShard.Name]))
            }
        }

        $hostSummary = ($shards | ForEach-Object { "$($_.Name)=$($_.Workers) workers" }) -join ', '
        Write-Host "Functional test hosts (execution deadline $($executionDeadlineUtc.ToString('O')); failure cleanup deadline $($failureCleanupDeadlineUtc.ToString('O')); configured budget ${TimeoutSeconds}s): $hostSummary"
        if (-not $timedOut -and $null -eq $failedHost) {
            while ($true) {
                $exitObservations = @($entries | ForEach-Object {
                        $exitTimeUtc = if ($retainedFunctionalExitTimesUtc.ContainsKey($_.Name)) {
                            $retainedFunctionalExitTimesUtc[$_.Name]
                        }
                        else {
                            Get-FunctionalProcessExitTimeUtc -Process $_.Process
                        }
                        if ($null -ne $exitTimeUtc) {
                            $retainedFunctionalExitTimesUtc[$_.Name] = $exitTimeUtc
                        }
                        [pscustomobject]@{
                            Entry = $_
                            ExitTimeUtc = $exitTimeUtc
                        }
                    })
                $failedHost = $exitObservations |
                    Where-Object {
                        $null -ne $_.ExitTimeUtc -and
                        $_.ExitTimeUtc -le $executionDeadlineUtc -and
                        $_.Entry.Process.ExitCode -ne 0
                    } |
                    Select-Object -ExpandProperty Entry -First 1
                if ($null -ne $failedHost) {
                    break
                }

                $notCompletedWithinDeadline = @($exitObservations | Where-Object {
                        $null -eq $_.ExitTimeUtc -or $_.ExitTimeUtc -gt $executionDeadlineUtc
                    })
                if ($notCompletedWithinDeadline.Count -eq 0) {
                    break
                }
                if ([DateTime]::UtcNow -ge $executionDeadlineUtc) {
                    $timedOut = $true
                    $timedOutHostNames = @($notCompletedWithinDeadline | ForEach-Object { $_.Entry.Name })
                    Write-Warning "Functional test hosts did not all exit within the configured execution deadline $($executionDeadlineUtc.ToString('O')). Stopping hosts: $($timedOutHostNames -join ', '). Owned-process cleanup is bounded by $($failureCleanupDeadlineUtc.ToString('O'))."
                    break
                }
                Start-Sleep -Milliseconds 100
            }
        }
    }
    catch {
        $launchFailure = $_
    }
    finally {
        if ($null -ne $testExecutionStopwatch -and $testExecutionStopwatch.IsRunning) {
            # Cleanup, output collection, and diagnostic persistence are outside the
            # successful test execution interval; failures still use the absolute +10s
            # cutoff passed to the shared process lifecycle.
            $testExecutionStopwatch.Stop()
        }
        Convert-FunctionalRawOwnershipRecordsToEntries -Entries $entries -OwnedProcessRecords $ownedProcessRecords -CleanupFailures $cleanupFailures
        if ($null -ne $failedHost) {
            $failedHostName = $failedHost.Name
            $failedHostExitCode = $failedHost.Process.ExitCode
        }
        $forceCleanup = $timedOut -or $null -ne $failedHostName -or $null -ne $launchFailure

        $functionalCleanup = Invoke-VerificationFunctionalCleanup `
            -Entries $entries `
            -ExecutionDeadlineUtc $executionDeadlineUtc `
            -CleanupDeadlineUtc $failureCleanupDeadlineUtc `
            -FailureCleanup:$forceCleanup `
            -RetainedExitTimesUtc $retainedFunctionalExitTimesUtc `
            -StopRoots:$forceCleanup
        foreach ($fanoutFailure in @($functionalCleanup.FanoutFailures)) {
            $cleanupFailures.Add($fanoutFailure)
        }
        foreach ($entryResult in @($functionalCleanup.EntryResults)) {
            $entry = $entryResult.Entry
            if ($null -ne $entryResult.Error) {
                $cleanupFailures.Add("$($entry.Name): cleanup/output collection failed: $($entryResult.Error.Exception.Message)")
                continue
            }

            $lifecycleResult = $entryResult.Result
            if (@($lifecycleResult.SecondaryDiagnostics).Count -gt 0) {
                foreach ($diagnostic in @($lifecycleResult.SecondaryDiagnostics)) {
                    $cleanupFailures.Add("$($entry.Name): $diagnostic")
                }
            }
            if ($null -eq $failedHostName -and $lifecycleResult.PrimaryFailureKind -ceq 'nonzero-exit') {
                $failedHostName = $entry.Name
                $failedHostExitCode = $lifecycleResult.ExitCode
            }
            if ($lifecycleResult.PrimaryFailureKind -ceq 'timeout' -and -not $timedOut) {
                $cleanupFailures.Add("$($entry.Name): bounded process lifecycle timed out during cleanup")
            }

            $standardOutput = $lifecycleResult.StandardOutput
            $standardError = $lifecycleResult.StandardError

            Write-Host "Test host: $($entry.Name); exit code: $($lifecycleResult.ExitCode)"
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
    }

    $requiredFunctionalHostNames = @($shards | ForEach-Object { $_.Name })
    $missingFunctionalExitTimeHostNames = @($requiredFunctionalHostNames | Where-Object {
            -not $retainedFunctionalExitTimesUtc.ContainsKey($_)
        })
    $apparentFunctionalSuccess =
        $null -eq $launchFailure -and
        -not $timedOut -and
        $null -eq $failedHostName -and
        $cleanupFailures.Count -eq 0
    $functionalExitTimeUtc = if ($apparentFunctionalSuccess -and $missingFunctionalExitTimeHostNames.Count -eq 0) {
        @($retainedFunctionalExitTimesUtc.Values | Sort-Object)[-1]
    }
    else {
        $null
    }
    $testExecutionElapsedSeconds = if ($timedOut -and $null -ne $deadlinePolicy) {
        # A timeout ends the interval at the absolute execution deadline.  The
        # cleanup window and the poll that discovers the timeout are outside it.
        ($deadlinePolicy.ExecutionDeadlineUtc - $deadlinePolicy.StartUtc).TotalSeconds
    }
    elseif ($null -ne $functionalExitTimeUtc -and $null -ne $deadlinePolicy) {
        # Use the same portable-boundary StartUtc and the latest retained process
        # ExitTime that drives success classification; never substitute poll time.
        ($functionalExitTimeUtc - $deadlinePolicy.StartUtc).TotalSeconds
    }
    else {
        $null
    }
    $elapsedDisplay = if ($null -eq $testExecutionElapsedSeconds) {
        'unavailable'
    }
    else {
        "$([Math]::Round($testExecutionElapsedSeconds, 1))s"
    }
    Write-Host "Functional test execution elapsed: $elapsedDisplay; diagnostics: $DiagnosticsDirectory"
    if ($apparentFunctionalSuccess -and
        $null -ne $testExecutionElapsedSeconds -and
        $testExecutionElapsedSeconds -gt $functionalReportingTargetSeconds) {
        Write-Warning "Functional test execution exceeded ${functionalReportingTargetSeconds}s target; actual retained-ExitTime-derived elapsed is $elapsedDisplay. This actual duration must be included in the user-facing report."
    }
    $primaryFailure = $null
    if ($null -ne $launchFailure) {
        $primaryFailure = $launchFailure
    }
    elseif ($timedOut) {
        $primaryFailure = [Exception]::new("Functional test execution exceeded the configured execution deadline $($executionDeadlineUtc.ToString('O')) within the ${TimeoutSeconds}-second test budget. Failure cleanup was bounded by $($failureCleanupDeadlineUtc.ToString('O')). Diagnostics: $DiagnosticsDirectory")
    }
    elseif ($null -ne $failedHostName) {
        $primaryFailure = [Exception]::new("Functional test host '$failedHostName' failed with exit code $failedHostExitCode. Diagnostics: $(Join-Path $DiagnosticsDirectory $failedHostName)")
    }
    elseif ($apparentFunctionalSuccess -and $null -ne $deadlinePolicy -and $missingFunctionalExitTimeHostNames.Count -gt 0) {
        $primaryFailure = [Exception]::new("Functional test execution elapsed could not be determined because retained process ExitTime was unavailable for host(s): $($missingFunctionalExitTimeHostNames -join ', '). Diagnostics: $DiagnosticsDirectory")
    }
    if ($cleanupFailures.Count -gt 0) {
        if ($null -ne $primaryFailure) {
            Write-Warning "Functional test host cleanup also failed after the primary failure: $($cleanupFailures -join '; ')"
        }
        else {
            throw "Functional test host cleanup failed: $($cleanupFailures -join '; ')"
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
    param(
        [Parameter(Mandatory)]
        [object]$DeadlinePolicy,

        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory
    )

    $result = Invoke-VerificationMonitoredCommand `
        -Label 'Current repository commit identity' `
        -CommandPath 'git' `
        -Arguments @('-C', $repoRoot, 'rev-parse', 'HEAD') `
        -WorkingDirectory $repoRoot `
        -DiagnosticsDirectory $DiagnosticsDirectory `
        -DeadlinePolicy $DeadlinePolicy
    $commit = ([string]$result.StandardOutput).Trim()
    if ([string]::IsNullOrWhiteSpace($commit)) {
        throw 'Unable to resolve the current repository commit.'
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
        [string]$PhaseDirectory,

        [Parameter(Mandatory)]
        [string]$ArtifactRoot,

        [Parameter(Mandatory)]
        [object]$DeadlinePolicy
    )

    $publishScript = Join-Path $repoRoot 'scripts\publish.ps1'
    if (-not (Test-Path -LiteralPath $publishScript -PathType Leaf)) {
        throw "Distribution publish script is missing: $publishScript"
    }
    [void](New-Item -ItemType Directory -Path $ArtifactRoot -Force)
    Invoke-VerificationPhaseCommand `
        -Label 'Current distribution publish' `
        -CommandPath 'pwsh' `
        -Arguments @('-NoProfile', '-File', $publishScript, '-PackageOnly', '-SkipDocHtml', '-ArtifactRoot', $ArtifactRoot) `
        -DiagnosticsDirectory (Join-Path $PhaseDirectory 'publish') `
        -DeadlinePolicy $DeadlinePolicy

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
        Commit = Get-RepositoryHeadCommit `
            -DeadlinePolicy $DeadlinePolicy `
            -DiagnosticsDirectory (Join-Path $PhaseDirectory 'commit')
        AppRoot = $appRoot
        UpdaterRoot = $updaterRoot
        PackagePath = $packagePath
    }
}

function Invoke-BaselinePreparation {
    param(
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
        [string]$ArtifactId,

        [Parameter(Mandatory)]
        [object]$DeadlinePolicy
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
        Invoke-VerificationPhaseCommand `
        -Label 'Baseline source archive' `
        -CommandPath 'git' `
        -Arguments @('-C', $repoRoot, 'archive', '--format=zip', "--output=$archivePath", $BaselineCommit) `
        -DiagnosticsDirectory (Join-Path $PhaseDirectory 'archive') `
        -DeadlinePolicy $DeadlinePolicy
    Expand-Archive -LiteralPath $archivePath -DestinationPath $sourceRoot -Force

    $baselinePublishScript = Join-Path $sourceRoot 'scripts\publish.ps1'
    if (-not (Test-Path -LiteralPath $baselinePublishScript -PathType Leaf)) {
        throw "Baseline publish script is missing from checkout: $baselinePublishScript"
    }
    Invoke-VerificationPhaseCommand `
        -Label 'Baseline distribution publish' `
        -CommandPath 'pwsh' `
        -Arguments @('-NoProfile', '-File', $baselinePublishScript, '-PackageOnly', '-SkipDocHtml') `
        -DiagnosticsDirectory (Join-Path $PhaseDirectory 'publish') `
        -DeadlinePolicy $DeadlinePolicy

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

function Invoke-V216ArtifactCachePreparation {
    param(
        [Parameter(Mandatory)]
        [string]$PhaseDirectory,

        [Parameter(Mandatory)]
        [object]$DeadlinePolicy
    )

    # Read and validate the checked-in identity before inspecting the cache.  A cache
    # miss is the only state that authorizes this phase to perform a download.
    $metadata = Read-V216ArtifactMetadata `
        -MetadataPath $v216ArtifactMetadataPath `
        -RepositoryRoot $repoRoot
    $cachePath = [string]$metadata.ArtifactPath
    if (Test-Path -LiteralPath $cachePath -PathType Leaf) {
        [void](Assert-V216ArtifactIdentity `
                -MetadataPath $v216ArtifactMetadataPath `
                -RepositoryRoot $repoRoot)
        Write-Host "v2.1.6.0 artifact cache hit: $cachePath"
        return [pscustomobject][ordered]@{
            Status = 'hit'
            ArtifactPath = $cachePath
            DownloadUrl = [string]$metadata.DownloadUrl
        }
    }
    if (Test-Path -LiteralPath $cachePath) {
        throw "Pinned v2.1.6.0 artifact cache path is not a file: $cachePath"
    }

    $cacheDirectory = Split-Path -Parent $cachePath
    [void](New-Item -ItemType Directory -Path $cacheDirectory -Force)
    $temporaryDownloadPath = Join-Path $cacheDirectory (
        '.' + $metadata.FileName + '.' + [Guid]::NewGuid().ToString('N') + '.download')
    $primaryError = $null
    try {
        Write-Host "v2.1.6.0 artifact cache miss; downloading the pinned artifact to a temporary file."
        [void](Invoke-VerificationPhaseCommand `
                -Label 'v2.1.6.0 artifact cache download' `
                -CommandPath 'curl.exe' `
                -Arguments @(
                    '-q'
                    '--fail'
                    '--silent'
                    '--show-error'
                    '--location'
                    '--proto'
                    '=https'
                    '--proto-redir'
                    '=https'
                    '--output'
                    $temporaryDownloadPath
                    [string]$metadata.DownloadUrl) `
                -DiagnosticsDirectory (Join-Path $PhaseDirectory 'download') `
                -DeadlinePolicy $DeadlinePolicy)

        if (-not (Test-Path -LiteralPath $temporaryDownloadPath -PathType Leaf)) {
            throw "Pinned v2.1.6.0 artifact download did not produce a file: $temporaryDownloadPath"
        }
        $downloadedSize = (Get-Item -LiteralPath $temporaryDownloadPath).Length
        if ($downloadedSize -ne $metadata.ExpectedSizeBytes) {
            throw "Pinned v2.1.6.0 artifact download size mismatch: expected=$($metadata.ExpectedSizeBytes) actual=$downloadedSize"
        }
        $downloadedSha256 = Get-DistributionSha256 $temporaryDownloadPath
        if ($downloadedSha256.ToUpperInvariant() -cne $metadata.ExpectedSha256.ToUpperInvariant()) {
            throw "Pinned v2.1.6.0 artifact download SHA-256 mismatch: expected=$($metadata.ExpectedSha256) actual=$downloadedSha256"
        }
        if (Test-Path -LiteralPath $cachePath) {
            throw "Pinned v2.1.6.0 artifact cache path appeared during download: $cachePath"
        }

        # The temporary file and canonical cache share a directory, so this publish is an
        # atomic rename and no partially downloaded bytes become visible at the cache path.
        [IO.File]::Move($temporaryDownloadPath, $cachePath)
        [void](Assert-V216ArtifactIdentity `
                -MetadataPath $v216ArtifactMetadataPath `
                -RepositoryRoot $repoRoot)
        Write-Host "v2.1.6.0 artifact cache prepared: $cachePath"
        return [pscustomobject][ordered]@{
            Status = 'miss-downloaded'
            ArtifactPath = $cachePath
            DownloadUrl = [string]$metadata.DownloadUrl
        }
    }
    catch {
        $primaryError = $_.Exception
        throw
    }
    finally {
        if (Test-Path -LiteralPath $temporaryDownloadPath) {
            try {
                Remove-Item -LiteralPath $temporaryDownloadPath -Force -ErrorAction Stop
            }
            catch {
                if ($null -eq $primaryError) {
                    throw
                }
                Write-Warning "v2.1.6.0 artifact temporary download cleanup failed after a primary failure: $temporaryDownloadPath. $($_.Exception.Message)"
            }
        }
    }
}

function Invoke-ExistingDataAcceptance {
    param(
        [Parameter(Mandatory)]
        [string]$PhaseDirectory,

        [Parameter(Mandatory)]
        [string]$ArtifactManifestPath,

        [Parameter(Mandatory)]
        [object]$DeadlinePolicy
    )

    if (-not (Test-Path -LiteralPath $existingDataAcceptanceScript -PathType Leaf)) {
        throw "Existing-data acceptance runner is missing: $existingDataAcceptanceScript"
    }
    $outputDirectory = Join-Path $PhaseDirectory 'acceptance'
    Invoke-VerificationPhaseCommand `
        -Label 'Existing-data acceptance' `
        -CommandPath 'pwsh' `
        -Arguments @(
            '-NoProfile'
            '-File'
            $existingDataAcceptanceScript
            '-ArtifactManifestPath'
            $ArtifactManifestPath
            '-OutputDirectory'
            $outputDirectory
            '-ExecutionDeadlineUtc'
            $DeadlinePolicy.ExecutionDeadlineUtc.ToString('O')
            '-CleanupDeadlineUtc'
            $DeadlinePolicy.CleanupDeadlineUtc.ToString('O')) `
        -DiagnosticsDirectory (Join-Path $PhaseDirectory 'command') `
        -DeadlinePolicy $DeadlinePolicy
}

function Invoke-UpdateAcceptance {
    param(
        [Parameter(Mandatory)]
        [string]$PhaseDirectory,

        [Parameter(Mandatory)]
        [string]$ArtifactManifestPath,

        [Parameter(Mandatory)]
        [object]$DeadlinePolicy
    )

    if (-not (Test-Path -LiteralPath $updateAcceptanceScript -PathType Leaf)) {
        throw "Update acceptance runner is missing: $updateAcceptanceScript"
    }
    $outputDirectory = Join-Path $PhaseDirectory 'acceptance'
    Invoke-VerificationPhaseCommand `
        -Label 'Update acceptance' `
        -CommandPath 'pwsh' `
        -Arguments @(
            '-NoProfile'
            '-File'
            $updateAcceptanceScript
            '-ArtifactManifestPath'
            $ArtifactManifestPath
            '-OutputDirectory'
            $outputDirectory
            '-ExecutionDeadlineUtc'
            $DeadlinePolicy.ExecutionDeadlineUtc.ToString('O')
            '-CleanupDeadlineUtc'
            $DeadlinePolicy.CleanupDeadlineUtc.ToString('O')) `
        -DiagnosticsDirectory (Join-Path $PhaseDirectory 'command') `
        -DeadlinePolicy $DeadlinePolicy
}

function Invoke-V216FirstHopAcceptance {
    param(
        [Parameter(Mandatory)]
        [string]$PhaseDirectory,

        [Parameter(Mandatory)]
        [string]$ArtifactManifestPath,

        [Parameter(Mandatory)]
        [object]$DeadlinePolicy
    )

    if (-not (Test-Path -LiteralPath $v216FirstHopAcceptanceScript -PathType Leaf)) {
        throw "v2.1.6.0 first-hop acceptance runner is missing: $v216FirstHopAcceptanceScript"
    }
    $pinned = Assert-V216ArtifactIdentity `
        -MetadataPath $v216ArtifactMetadataPath `
        -RepositoryRoot $repoRoot
    $artifactManifest = Read-DistributionArtifactManifest -ManifestPath $ArtifactManifestPath
    $currentPackagePath = [string]$artifactManifest.Current.packagePath
    $currentVersion = [string]$artifactManifest.Current.version
    if ([string]::IsNullOrWhiteSpace($currentPackagePath) -or
        [string]::IsNullOrWhiteSpace($currentVersion)) {
        throw 'Current distribution manifest does not identify a release package for v2.1.6.0 first-hop acceptance.'
    }
    $outputDirectory = Join-Path $PhaseDirectory 'v216-first-hop'
    Invoke-VerificationPhaseCommand `
        -Label 'v2.1.6.0 first-hop acceptance' `
        -CommandPath 'pwsh' `
        -Arguments @(
            '-NoProfile'
            '-File'
            $v216FirstHopAcceptanceScript
            '-ArtifactMetadataPath'
            $pinned.MetadataPath
            '-CurrentPackagePath'
            $currentPackagePath
            '-CurrentVersion'
            $currentVersion
            '-OutputDirectory'
            $outputDirectory
            '-ExecutionDeadlineUtc'
            $DeadlinePolicy.ExecutionDeadlineUtc.ToString('O')
            '-CleanupDeadlineUtc'
            $DeadlinePolicy.CleanupDeadlineUtc.ToString('O')) `
        -DiagnosticsDirectory (Join-Path $PhaseDirectory 'v216-first-hop-command') `
        -DeadlinePolicy $DeadlinePolicy

    $receiptPath = Join-Path $outputDirectory 'v216-first-hop-acceptance.json'
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
        throw "v2.1.6.0 first-hop acceptance receipt is missing: $receiptPath"
    }
    return (Resolve-Path -LiteralPath $receiptPath).Path
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

# 長いテストの前に静的診断を解消する。Quick の反復にはこの検査を追加しない。
function Invoke-StaticVerification {
    param(
        [Parameter(Mandatory)]
        [string]$DiagnosticsRoot
    )

    [void](Invoke-MonitoredVerificationPhase -Name 'tool-restore' -DiagnosticsRoot $DiagnosticsRoot -Action {
        param($phaseStopwatch, $phaseDirectory, $deadlinePolicy)
        Invoke-VerificationPhaseCommand `
            -Label 'Tool restore' `
            -CommandPath 'dotnet' `
            -Arguments @('tool', 'restore') `
            -DiagnosticsDirectory (Join-Path $phaseDirectory 'command') `
            -DeadlinePolicy $deadlinePolicy
    })

    [void](Invoke-MonitoredVerificationPhase -Name 'format' -DiagnosticsRoot $DiagnosticsRoot -Action {
        param($phaseStopwatch, $phaseDirectory, $deadlinePolicy)
        Invoke-VerificationPhaseCommand `
            -Label 'dotnet format' `
            -CommandPath 'dotnet' `
            -Arguments (Get-RepositoryFormatArguments -WorkspaceRoot $repoRoot) `
            -DiagnosticsDirectory (Join-Path $phaseDirectory 'command') `
            -DeadlinePolicy $deadlinePolicy
    })

    [void](Invoke-MonitoredVerificationPhase -Name 'analyzer' -DiagnosticsRoot $DiagnosticsRoot -Action {
        param($phaseStopwatch, $phaseDirectory, $deadlinePolicy)
        $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
        $vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
            throw "vswhere.exe was not found: $vswhere"
        }
        $vswhereResult = Invoke-VerificationPhaseCommand `
            -Label 'Visual Studio MSBuild discovery' `
            -CommandPath $vswhere `
            -Arguments @('-version', '[17.0,18.0)', '-products', '*', '-requires', 'Microsoft.Component.MSBuild', '-find', 'MSBuild\Current\Bin') `
            -DiagnosticsDirectory (Join-Path $phaseDirectory 'vswhere') `
            -DeadlinePolicy $deadlinePolicy
        $msbuildPath = @(([string]$vswhereResult.StandardOutput -split "\r?\n") |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Select-Object -First 1)[0]
        $msbuildPath = ([string]$msbuildPath).Trim()
        if ([string]::IsNullOrWhiteSpace($msbuildPath)) {
            throw 'Visual Studio 2022 MSBuild 17 was not found.'
        }
        Invoke-VerificationPhaseCommand `
            -Label 'roslynator analyzer' `
            -CommandPath 'dotnet' `
            -Arguments @('roslynator', 'analyze', $solution, '--msbuild-path', $msbuildPath, '--properties', 'Configuration=Release', '--severity-level', 'warning', '--ignore-compiler-diagnostics', '--verbosity', 'normal') `
            -DiagnosticsDirectory (Join-Path $phaseDirectory 'command') `
            -DeadlinePolicy $deadlinePolicy
    })
}

function Invoke-CanonicalFunctionalVerification {
    param(
        [Parameter(Mandatory)]
        [string]$DiagnosticsRoot,

        [Parameter(Mandatory)]
        [ValidateRange(1, 300)]
        [int]$TimeoutSeconds
    )

    [void](New-Item -ItemType Directory -Path $DiagnosticsRoot -Force)
    try {
        # Restore and build keep their existing per-command bounded lifecycle, but they
        # deliberately do not participate in the Functional test execution deadline.
        $restoreStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        Invoke-BudgetedCommand `
            -Stopwatch $restoreStopwatch `
            -BudgetSeconds $TimeoutSeconds `
            -Label 'Locked restore' `
            -CommandPath 'dotnet' `
            -Arguments @('restore', $solution, '-r', 'win-x64', '--locked-mode', '-p:PublishReadyToRun=true') `
            -DiagnosticsDirectory (Join-Path $DiagnosticsRoot 'restore')
        $restoreStopwatch.Stop()

        if ($Mode -in @('Functional', 'Full')) {
            Invoke-StaticVerification -DiagnosticsRoot $DiagnosticsRoot
        }

        $buildStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        Invoke-BudgetedCommand `
            -Stopwatch $buildStopwatch `
            -BudgetSeconds $TimeoutSeconds `
            -Label 'Functional build' `
            -CommandPath 'dotnet' `
            -Arguments @('build', $solution, '/p:Configuration=Release', '/p:Platform=x64', '--no-restore') `
            -DiagnosticsDirectory (Join-Path $DiagnosticsRoot 'build')
        $buildStopwatch.Stop()

        Assert-BuiltOutputs
        Invoke-ParallelFunctionalTestShards `
            -DiagnosticsDirectory (Join-Path $DiagnosticsRoot 'functional') `
            -TimeoutSeconds $TimeoutSeconds
        Assert-RepositoryWhitespace
    }
    finally {
        Write-Host "Canonical Functional completed; test execution budget: ${TimeoutSeconds}s; diagnostics: $DiagnosticsRoot"
    }
}

function Invoke-FilteredQuickVerification {
    param(
        [Parameter(Mandatory)]
        [string]$Filter,

        [Parameter(Mandatory)]
        [string]$DiagnosticsRoot,

        [Parameter(Mandatory)]
        [ValidateRange(1, 300)]
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
        $testArguments = Get-TestArguments `
            -Filter $Filter `
            -DiagnosticsDirectory $testDirectory
        Invoke-BudgetedCommand `
            -Stopwatch $filteredQuickStopwatch `
            -BudgetSeconds $TimeoutSeconds `
            -Label 'Filtered build and test' `
            -CommandPath 'dotnet' `
            -Arguments $testArguments `
            -DiagnosticsDirectory $testDirectory
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
    if ($canonicalFunctionalRequested) {
        Invoke-CanonicalFunctionalVerification `
            -DiagnosticsRoot $testDiagnosticsDirectory `
            -TimeoutSeconds $FunctionalTimeoutSeconds
    }

    if ($Mode -eq 'Full') {
        [void](Invoke-MonitoredVerificationPhase -Name 'tool-smoke' -DiagnosticsRoot $testDiagnosticsDirectory -Action {
            param($phaseStopwatch, $phaseDirectory, $deadlinePolicy)
            foreach ($toolExecutable in $toolExecutables) {
                if (-not (Test-Path -LiteralPath $toolExecutable -PathType Leaf)) {
                    throw "Release tool executable was not produced: $toolExecutable"
                }
                $toolName = [IO.Path]::GetFileNameWithoutExtension($toolExecutable)
                Invoke-VerificationPhaseCommand `
                    -Label "Tool smoke $toolName" `
                    -CommandPath $toolExecutable `
                    -Arguments @('--help') `
                    -DiagnosticsDirectory (Join-Path $phaseDirectory $toolName) `
                    -DeadlinePolicy $deadlinePolicy
            }
        })

        $fullDistributionRoot = Join-Path $testDiagnosticsDirectory 'distribution'
        $currentDistributionRoot = Join-Path $fullDistributionRoot 'current'
        $fullRunId = Split-Path -Leaf $testDiagnosticsDirectory
        $fullArtifactId = $fullRunId + '-distribution'
        $current = @(Invoke-MonitoredVerificationPhase -Name 'current-distribution-publish' -DiagnosticsRoot $testDiagnosticsDirectory -Action {
            param($phaseStopwatch, $phaseDirectory, $deadlinePolicy)
            Invoke-CurrentDistributionPublish `
                -PhaseDirectory $phaseDirectory `
                -ArtifactRoot $currentDistributionRoot `
                -DeadlinePolicy $deadlinePolicy
        })[-1]

        $artifactManifest = @(Invoke-MonitoredVerificationPhase -Name 'baseline-preparation' -DiagnosticsRoot $testDiagnosticsDirectory -Action {
            param($phaseStopwatch, $phaseDirectory, $deadlinePolicy)
            Invoke-BaselinePreparation `
                -PhaseDirectory $phaseDirectory `
                -ArtifactRoot $fullDistributionRoot `
                -BaselineCommit $baselineCommit `
                -Current $current `
                -RunId $fullRunId `
                -ArtifactId $fullArtifactId `
                -DeadlinePolicy $deadlinePolicy
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

        [void](Invoke-MonitoredVerificationPhase -Name 'existing-data' -DiagnosticsRoot $testDiagnosticsDirectory -Action {
            param($phaseStopwatch, $phaseDirectory, $deadlinePolicy)
            Assert-DistributionArtifactIdentity `
                -ArtifactManifest (Read-DistributionArtifactManifest -ManifestPath $artifactManifestPath) `
                -ExpectedRunId $expectedFullRunId `
                -ExpectedArtifactId $expectedFullArtifactId `
                -ExpectedManifestSha256 $expectedFullManifestSha256 `
                -ExpectedManifestSeal $expectedFullManifestSeal | Out-Null
            Invoke-ExistingDataAcceptance `
                -PhaseDirectory $phaseDirectory `
                -ArtifactManifestPath $artifactManifestPath `
                -DeadlinePolicy $deadlinePolicy
            Assert-DistributionArtifactIdentity `
                -ArtifactManifest (Read-DistributionArtifactManifest -ManifestPath $artifactManifestPath) `
                -ExpectedRunId $expectedFullRunId `
                -ExpectedArtifactId $expectedFullArtifactId `
                -ExpectedManifestSha256 $expectedFullManifestSha256 `
                -ExpectedManifestSeal $expectedFullManifestSeal | Out-Null
        })
        [void](Invoke-MonitoredVerificationPhase -Name 'update' -DiagnosticsRoot $testDiagnosticsDirectory -Action {
            param($phaseStopwatch, $phaseDirectory, $deadlinePolicy)
            Assert-DistributionArtifactIdentity `
                -ArtifactManifest (Read-DistributionArtifactManifest -ManifestPath $artifactManifestPath) `
                -ExpectedRunId $expectedFullRunId `
                -ExpectedArtifactId $expectedFullArtifactId `
                -ExpectedManifestSha256 $expectedFullManifestSha256 `
                -ExpectedManifestSeal $expectedFullManifestSeal | Out-Null
            Invoke-UpdateAcceptance `
                -PhaseDirectory $phaseDirectory `
                -ArtifactManifestPath $artifactManifestPath `
                -DeadlinePolicy $deadlinePolicy
            Assert-DistributionArtifactIdentity `
                -ArtifactManifest (Read-DistributionArtifactManifest -ManifestPath $artifactManifestPath) `
                -ExpectedRunId $expectedFullRunId `
                -ExpectedArtifactId $expectedFullArtifactId `
                -ExpectedManifestSha256 $expectedFullManifestSha256 `
                -ExpectedManifestSeal $expectedFullManifestSeal | Out-Null
        })

        [void](Invoke-MonitoredVerificationPhase -Name 'v216-cache-preparation' -DiagnosticsRoot $testDiagnosticsDirectory -Action {
            param($phaseStopwatch, $phaseDirectory, $deadlinePolicy)
            Invoke-V216ArtifactCachePreparation `
                -PhaseDirectory $phaseDirectory `
                -DeadlinePolicy $deadlinePolicy
        })

        $processIntegrationResultsPath = $null
        $releaseAcceptanceResultsPath = $null
        $v216FirstHopAcceptanceReceiptPath = $null
        $v216ReceiptInputs = @($verificationRunnerContract.ReleaseOutcomeGate.ReceiptInputs)
        if ($v216ReceiptInputs.Count -ne 1) {
            throw 'Release outcome gate must declare exactly one v2.1.6.0 first-hop receipt input.'
        }
        $expectedV216FirstHopAcceptanceReceiptPath = [IO.Path]::GetFullPath(
            (Join-Path $testDiagnosticsDirectory ([string]$v216ReceiptInputs[0].RelativePath).Replace('/', '\')))
        $processIntegrationResultsPath = @(Invoke-MonitoredVerificationPhase -Name 'ProcessIntegration' -DiagnosticsRoot $testDiagnosticsDirectory -Action {
            param($phaseStopwatch, $phaseDirectory, $deadlinePolicy)
            Assert-DistributionArtifactIdentity `
                -ArtifactManifest (Read-DistributionArtifactManifest -ManifestPath $artifactManifestPath) `
                -ExpectedRunId $expectedFullRunId `
                -ExpectedArtifactId $expectedFullArtifactId `
                -ExpectedManifestSha256 $expectedFullManifestSha256 `
                -ExpectedManifestSeal $expectedFullManifestSeal | Out-Null
            $processIntegrationTestDirectory = Join-Path $phaseDirectory 'test'
            $processIntegrationResultsPath = Join-Path $processIntegrationTestDirectory 'results.trx'
            Invoke-TestLane `
                -Name 'Process integration' `
                -Filter 'TestCategory=ProcessIntegration' `
                -DiagnosticsDirectory $processIntegrationTestDirectory `
                -DeadlinePolicy $deadlinePolicy `
                -NoBuild
            Assert-DistributionArtifactIdentity `
                -ArtifactManifest (Read-DistributionArtifactManifest -ManifestPath $artifactManifestPath) `
                -ExpectedRunId $expectedFullRunId `
                -ExpectedArtifactId $expectedFullArtifactId `
                -ExpectedManifestSha256 $expectedFullManifestSha256 `
                -ExpectedManifestSeal $expectedFullManifestSeal | Out-Null
            return $processIntegrationResultsPath
        })[-1]
        $releaseAcceptancePaths = @(Invoke-MonitoredVerificationPhase -Name 'ReleaseAcceptance' -DiagnosticsRoot $testDiagnosticsDirectory -Action {
            param($phaseStopwatch, $phaseDirectory, $deadlinePolicy)
            Assert-DistributionArtifactIdentity `
                -ArtifactManifest (Read-DistributionArtifactManifest -ManifestPath $artifactManifestPath) `
                -ExpectedRunId $expectedFullRunId `
                -ExpectedArtifactId $expectedFullArtifactId `
                -ExpectedManifestSha256 $expectedFullManifestSha256 `
                -ExpectedManifestSeal $expectedFullManifestSeal | Out-Null
            $v216FirstHopAcceptanceReceiptPath = @(Invoke-V216FirstHopAcceptance `
                    -PhaseDirectory $phaseDirectory `
                    -ArtifactManifestPath $artifactManifestPath `
                    -DeadlinePolicy $deadlinePolicy)[-1]
            if ([string]::IsNullOrWhiteSpace([string]$v216FirstHopAcceptanceReceiptPath) -or
                [IO.Path]::GetFullPath([string]$v216FirstHopAcceptanceReceiptPath) -cne $expectedV216FirstHopAcceptanceReceiptPath -or
                -not (Test-Path -LiteralPath $expectedV216FirstHopAcceptanceReceiptPath -PathType Leaf)) {
                throw "v2.1.6.0 first-hop acceptance receipt path is missing or unexpected: $expectedV216FirstHopAcceptanceReceiptPath"
            }
            Assert-V216ArtifactIdentity `
                -MetadataPath $v216ArtifactMetadataPath `
                -RepositoryRoot $repoRoot | Out-Null
            $releaseAcceptanceTestDirectory = Join-Path $phaseDirectory 'test'
            $releaseAcceptanceResultsPath = Join-Path $releaseAcceptanceTestDirectory 'results.trx'
            Invoke-TestLane `
                -Name 'Release acceptance' `
                -Filter 'TestCategory=ReleaseAcceptance' `
                -DiagnosticsDirectory $releaseAcceptanceTestDirectory `
                -DeadlinePolicy $deadlinePolicy `
                -NoBuild
            $functionalResultsDirectory = Join-Path $testDiagnosticsDirectory 'functional'
            if (-not (Test-Path -LiteralPath $functionalResultsDirectory -PathType Container)) {
                throw "Canonical Functional result directory is missing for the release outcome gate: $functionalResultsDirectory"
            }
            $functionalResultPaths = @(
                Get-ChildItem -LiteralPath $functionalResultsDirectory -Recurse -File -Filter 'results.trx' |
                    Sort-Object -Property FullName |
                    ForEach-Object { $_.FullName })
            if ($functionalResultPaths.Count -eq 0) {
                throw "Canonical Functional produced no TRX result receipts for the release outcome gate: $functionalResultsDirectory"
            }
            $outcomeResultPaths = @(
                $functionalResultPaths +
                $processIntegrationResultsPath +
                $releaseAcceptanceResultsPath +
                $v216FirstHopAcceptanceReceiptPath)
            [void](Assert-VerificationTestOutcomes `
                    -ResultPaths $outcomeResultPaths `
                    -RosterPath $v216ArtifactMetadataPath `
                    -ReceiptPath (Join-Path $phaseDirectory 'release-outcomes.json'))
            Assert-DistributionArtifactIdentity `
                -ArtifactManifest (Read-DistributionArtifactManifest -ManifestPath $artifactManifestPath) `
                -ExpectedRunId $expectedFullRunId `
                -ExpectedArtifactId $expectedFullArtifactId `
                -ExpectedManifestSha256 $expectedFullManifestSha256 `
                -ExpectedManifestSeal $expectedFullManifestSeal | Out-Null
            return [pscustomobject][ordered]@{
                AcceptanceReceiptPath = $v216FirstHopAcceptanceReceiptPath
                ResultsPath = $releaseAcceptanceResultsPath
            }
        })[-1]
        $v216FirstHopAcceptanceReceiptPath = [string]$releaseAcceptancePaths.AcceptanceReceiptPath
        $releaseAcceptanceResultsPath = [string]$releaseAcceptancePaths.ResultsPath

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
