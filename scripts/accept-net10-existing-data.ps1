[CmdletBinding()]
param(
    [string]$AppPublishRoot,
    [string]$ArtifactManifestPath,
    [string]$FixtureRoot,
    [string]$OutputDirectory,
    [string]$SqliteAssemblyRoot,
    [string]$UpdateManifestUrl,
    [ValidateRange(1, 300)]
    [int]$TimeoutSeconds = 180,
    [DateTime]$ExecutionDeadlineUtc = [DateTime]::MinValue,
    [DateTime]$CleanupDeadlineUtc = [DateTime]::MinValue,
    [switch]$KeepSandbox
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $repoRoot 'scripts\distribution-artifact.ps1')
. (Join-Path $repoRoot 'scripts\verification-process-lifecycle.ps1')
. (Join-Path $repoRoot 'scripts\verification-ui-automation.ps1')
. (Join-Path $repoRoot 'scripts\acceptance-settings-fixture.ps1')

$artifactManifestMode = -not [string]::IsNullOrWhiteSpace($ArtifactManifestPath)
$artifactManifest = $null
if ($artifactManifestMode) {
    $artifactManifest = Read-DistributionArtifactManifest -ManifestPath $ArtifactManifestPath
    $manifestAppPublishRoot = [IO.Path]::GetFullPath([string]$artifactManifest.Current.appRoot)
    if (-not [string]::IsNullOrWhiteSpace($AppPublishRoot) -and
        [IO.Path]::GetFullPath($AppPublishRoot) -cne $manifestAppPublishRoot) {
        throw "Explicit artifact manifest app root does not match AppPublishRoot: expected=$manifestAppPublishRoot actual=$AppPublishRoot"
    }
    $AppPublishRoot = $manifestAppPublishRoot
}

if (-not ('BeMusicSeekerAcceptanceWindowMessage' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class BeMusicSeekerAcceptanceWindowMessage
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
}
'@
}

if (-not $artifactManifestMode -and [string]::IsNullOrWhiteSpace($AppPublishRoot)) {
    $AppPublishRoot = $env:BMS_SCD_APP_PUBLISH_ROOT
}
if (-not $artifactManifestMode -and [string]::IsNullOrWhiteSpace($AppPublishRoot)) {
    $AppPublishRoot = Join-Path $repoRoot 'artifacts\publish\app'
}
if ([string]::IsNullOrWhiteSpace($FixtureRoot)) {
    $FixtureRoot = Join-Path $repoRoot 'devdocs\acceptance\net10-existing-data'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts\verification\net10-existing-data'
}
if ([string]::IsNullOrWhiteSpace($SqliteAssemblyRoot)) {
    $SqliteAssemblyRoot = if (Test-Path -LiteralPath (Join-Path $AppPublishRoot 'SQLitePCLRaw.core.dll') -PathType Leaf) {
        $AppPublishRoot
    }
    else {
        Join-Path $repoRoot 'BeMusicSeeker.Tests\bin\x64\Release\net10.0-windows'
    }
}

$script:deadlinePolicy = Resolve-VerificationDeadlinePair `
    -TimeoutSeconds $TimeoutSeconds `
    -ExecutionDeadlineUtc $ExecutionDeadlineUtc `
    -CleanupDeadlineUtc $CleanupDeadlineUtc

function Resolve-FullPath {
    param([Parameter(Mandatory)][string]$Path)
    return [IO.Path]::GetFullPath($Path)
}

function Assert-File {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required acceptance file is missing: $Path"
    }
}

function Assert-Directory {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "Required acceptance directory is missing: $Path"
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-ManifestSettingMap {
    param([Parameter(Mandatory)][string]$Path)

    $document = [Xml.XmlDocument]::new()
    $document.Load($Path)
    $section = $document.SelectSingleNode('/configuration/userSettings/BeMusicSeeker.Properties.Settings')
    if ($null -eq $section) {
        throw "Portable settings section is missing: $Path"
    }
    $map = @{}
    foreach ($setting in $section.SelectNodes('setting')) {
        $map[[string]$setting.GetAttribute('name')] = [string]$setting.SelectSingleNode('value').InnerText
    }
    return $map
}

function Write-Lr2Config {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$BmsRelativePath
    )

    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $document = [Xml.XmlDocument]::new()
    $config = $document.CreateElement('config')
    [void]$document.AppendChild($config)
    $system = $document.CreateElement('system')
    $autoreload = $document.CreateElement('autoreload')
    $autoreload.InnerText = '0'
    [void]$system.AppendChild($autoreload)
    [void]$config.AppendChild($system)
    $jukebox = $document.CreateElement('jukebox')
    $pathElement = $document.CreateElement('path')
    $pathElement.InnerText = $BmsRelativePath.TrimEnd('\\') + '\\'
    [void]$jukebox.AppendChild($pathElement)
    [void]$config.AppendChild($jukebox)
    $document.Save($Path)
}

$script:sqliteRuntimeLoaded = $false
function Initialize-SqliteRuntime {
    if ($script:sqliteRuntimeLoaded) {
        return
    }
    $assemblyRoot = Resolve-FullPath $SqliteAssemblyRoot
    foreach ($name in @('SQLitePCLRaw.core.dll', 'SQLitePCLRaw.batteries_v2.dll', 'SQLite-net.dll', 'BeMusicSeeker.dll')) {
        Assert-File (Join-Path $assemblyRoot $name)
        Add-Type -Path (Join-Path $assemblyRoot $name) -ErrorAction SilentlyContinue
    }
    $nativeRoot = Join-Path $assemblyRoot 'runtimes\win-x64\native'
    if (Test-Path -LiteralPath $nativeRoot -PathType Container) {
        $env:PATH = $nativeRoot + [IO.Path]::PathSeparator + $env:PATH
    }
    [SQLitePCL.Batteries_V2]::Init()
    $script:sqliteRuntimeLoaded = $true
}

function Copy-LegacyDatabaseFixture {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Manifest,
        [Parameter(Mandatory)][string]$InstallPath,
        [Parameter(Mandatory)][string]$BmsRoot
    )

    Initialize-SqliteRuntime
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $fixtureFile = [string]$Manifest.database.fixtureFile
    if ([string]::IsNullOrWhiteSpace($fixtureFile)) {
        throw 'Existing-data fixture manifest must name a tracked database fixture.'
    }
    $fixturePath = Join-Path $FixtureRoot $fixtureFile
    Assert-File $fixturePath
    $expectedFixtureHash = [string]$Manifest.database.fixtureSha256
    if ([string]::IsNullOrWhiteSpace($expectedFixtureHash) -or (Get-Sha256 -Path $fixturePath) -ne $expectedFixtureHash.ToLowerInvariant()) {
        throw "Tracked legacy database fixture hash does not match the manifest: $fixturePath"
    }
    Copy-Item -LiteralPath $fixturePath -Destination $Path -Force
    $flags = [SQLite.SQLiteOpenFlags]::ReadWrite -bor [SQLite.SQLiteOpenFlags]::FullMutex
    $database = [SQLite.SQLiteConnection]::new($Path, $flags, $true)
    try {
        $row = $Manifest.database
        $songPath = Join-Path $BmsRoot ([string]$row.songRelativePath)
        $songUpdates = [int]$database.Execute(
            'UPDATE song SET path = ? WHERE hash = ? AND title = ?',
            [object[]]@([string]$songPath, [string]$row.songHash, [string]$row.songTitle))
        $maintenanceUpdates = [int]$database.Execute(
            'UPDATE maintenance SET path = ? WHERE hash = ?',
            [object[]]@([string]$songPath, [string]$row.songHash))
        $installUpdates = [int]$database.Execute(
            'UPDATE install SET path = ? WHERE path = ?',
            [object[]]@([string]$InstallPath, '__E1_INSTALL_PATH__'))
        if ($songUpdates -ne 1 -or $maintenanceUpdates -ne 1 -or $installUpdates -ne 1) {
            throw "Legacy database fixture relocation failed: song=$songUpdates maintenance=$maintenanceUpdates install=$installUpdates"
        }
    }
    finally {
        $database.Close()
        $database.Dispose()
    }
}

function Get-DatabaseSemanticSnapshot {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Manifest,
        [Parameter(Mandatory)][string]$InstallPath,
        [ValidateSet('Standalone', 'Lr2')][string]$ProfileKind = 'Standalone',
        [string]$BmsRoot
    )

    Initialize-SqliteRuntime
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Acceptance database is missing after shutdown: $Path"
    }
    $flags = [SQLite.SQLiteOpenFlags]::ReadOnly -bor [SQLite.SQLiteOpenFlags]::FullMutex
    $database = [SQLite.SQLiteConnection]::new($Path, $flags, $true)
    try {
        $row = $Manifest.database
        $songPath = if ([string]::IsNullOrWhiteSpace($BmsRoot)) {
            $null
        }
        else {
            Join-Path $BmsRoot ([string]$row.songRelativePath)
        }
        $songCount = if ($null -eq $songPath) {
            $database.ExecuteScalar[int](
                'SELECT COUNT(*) FROM song WHERE hash = ? AND title = ?',
                [object[]]@([string]$row.songHash, [string]$row.songTitle))
        }
        else {
            $database.ExecuteScalar[int](
                'SELECT COUNT(*) FROM song WHERE hash = ? AND title = ? AND path = ?',
                [object[]]@([string]$row.songHash, [string]$row.songTitle, [string]$songPath))
        }
        $maintenanceCount = if ($null -eq $songPath) {
            $database.ExecuteScalar[int](
                'SELECT COUNT(*) FROM maintenance WHERE hash = ?',
                [object[]]@([string]$row.songHash))
        }
        else {
            $database.ExecuteScalar[int](
                'SELECT COUNT(*) FROM maintenance WHERE hash = ? AND path = ?',
                [object[]]@([string]$row.songHash, [string]$songPath))
        }
        $legacyFolderCount = $database.ExecuteScalar[int](
            'SELECT COUNT(*) FROM folder WHERE title = ? AND path = ?',
            [object[]]@([string]$row.folderTitle, [string]$row.folderRelativePath))
        $legacyFolderPathCount = $database.ExecuteScalar[int](
            'SELECT COUNT(*) FROM folder WHERE path = ?',
            [object[]]@([string]$row.folderRelativePath))
        $installCount = $database.ExecuteScalar[int]('SELECT COUNT(*) FROM install WHERE path = ?', [object[]]@([string]$InstallPath))
        $playlistCount = $database.ExecuteScalar[int]('SELECT COUNT(*) FROM playlist WHERE playlist_id = ? AND name = ? AND symbol = ?', [object[]]@([int]$row.playlistId, [string]$row.playlistName, [string]$row.playlistSymbol))
        $courseCount = $database.ExecuteScalar[int]('SELECT COUNT(*) FROM playlist_course WHERE course_id = ? AND playlist_id = ? AND course_json = ?', [object[]]@([int]$row.courseId, [int]$row.playlistId, [string]$row.courseJson))
        $entryCount = $database.ExecuteScalar[int]('SELECT COUNT(*) FROM playlist_entry WHERE playlist_id = ? AND md5 = ? AND title = ? AND folder = ?', [object[]]@([int]$row.playlistId, [string]$row.entryMd5, [string]$row.entryTitle, [string]$row.entryFolder))
        $result = [ordered]@{
            song = $songCount
            maintenance = $maintenanceCount
            folder = $legacyFolderCount
            legacyFolder = $legacyFolderCount
            legacyFolderPath = $legacyFolderPathCount
            install = $installCount
            playlist = $playlistCount
            playlistCourse = $courseCount
            playlistEntry = $entryCount
        }
        foreach ($key in @('song', 'maintenance', 'install', 'playlist', 'playlistCourse', 'playlistEntry')) {
            if ($result[$key] -ne 1) {
                throw "Existing-data semantic row was not preserved: $key count=$($result[$key]) path=$Path"
            }
        }
        if ($ProfileKind -eq 'Standalone') {
            if ($legacyFolderCount -ne 1) {
                throw "Standalone legacy folder row was not preserved: count=$legacyFolderCount path=$Path"
            }
            return $result
        }

        if ([string]::IsNullOrWhiteSpace($BmsRoot)) {
            throw 'LR2 database semantic validation requires BmsRoot.'
        }
        if ($legacyFolderPathCount -ne 0) {
            throw "LR2 sync retained the relative legacy folder path: count=$legacyFolderPathCount path=$Path"
        }
        $fixtureDirectory = Join-Path $BmsRoot 'Fixture'
        Assert-Directory $fixtureDirectory
        $canonicalFolderPath = (Resolve-FullPath $fixtureDirectory).TrimEnd('\') + '\'
        $expectedFolderDate = ([DateTimeOffset](Get-Item -LiteralPath $fixtureDirectory).LastWriteTimeUtc).ToUnixTimeSeconds()
        $canonicalFolderCount = $database.ExecuteScalar[int](
            'SELECT COUNT(*) FROM folder WHERE title = ? AND path = ? AND type = ? AND date = ?',
            [object[]]@([string]'Fixture', [string]$canonicalFolderPath, [int]1, [long]$expectedFolderDate))
        $statusRowCount = $database.ExecuteScalar[int](
            'SELECT COUNT(*) FROM lr2_song_db_sync_status WHERE name = ?',
            [object[]]@([string]'default'))
        $statusCompletedCount = $database.ExecuteScalar[int](
            "SELECT COUNT(*) FROM lr2_song_db_sync_status WHERE name = ? AND status = ? AND stage = ? AND last_error = '' AND signature IS NOT NULL AND signature <> '' AND run_id IS NOT NULL AND run_id <> '' AND processed_cursor IS NOT NULL AND total_count IS NOT NULL AND total_count > 0 AND processed_cursor = total_count AND updated_at IS NOT NULL AND completed_at IS NOT NULL",
            [object[]]@([string]'default', [string]'Completed', [string]'completed'))
        $statusValue = $database.ExecuteScalar[string](
            'SELECT status FROM lr2_song_db_sync_status WHERE name = ?',
            [object[]]@([string]'default'))
        $processedCursor = $database.ExecuteScalar[int](
            'SELECT processed_cursor FROM lr2_song_db_sync_status WHERE name = ?',
            [object[]]@([string]'default'))
        $totalCount = $database.ExecuteScalar[int](
            'SELECT total_count FROM lr2_song_db_sync_status WHERE name = ?',
            [object[]]@([string]'default'))
        $result.canonicalFolder = $canonicalFolderCount
        $result.canonicalFolderPath = $canonicalFolderPath
        $result.canonicalFolderDate = $expectedFolderDate
        $result.lr2SyncStatusDefault = $statusRowCount
        $result.lr2SyncStatusCompleted = $statusCompletedCount
        $result.lr2SyncStatus = $statusValue
        $result.lr2SyncProcessedCursor = $processedCursor
        $result.lr2SyncTotalCount = $totalCount
        if ($canonicalFolderCount -ne 1) {
            throw "LR2 canonical folder row is invalid: count=$canonicalFolderCount path=$canonicalFolderPath date=$expectedFolderDate database=$Path"
        }
        if ($statusRowCount -ne 1 -or $statusCompletedCount -ne 1) {
            throw "LR2 sync status row is invalid: defaultCount=$statusRowCount completedCount=$statusCompletedCount path=$Path"
        }
        return $result
    }
    finally {
        $database.Close()
        $database.Dispose()
    }
}

function Wait-ForStartupCompletion {
    param(
        [Parameter(Mandatory)][Diagnostics.Process]$Process,
        [Parameter(Mandatory)][string]$LogDirectory,
        [int]$TimeoutSeconds = 180,
        [DateTime]$DeadlineUtc = [DateTime]::MinValue
    )

    $deadline = if ($DeadlineUtc -eq [DateTime]::MinValue) {
        $script:deadlinePolicy.ExecutionDeadlineUtc
    }
    else { $DeadlineUtc.ToUniversalTime() }
    $readyLog = $null
    $postInitializationCompletionObserved = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($Process.HasExited) {
            if ($null -ne $readyLog -and -not $postInitializationCompletionObserved) {
                throw "Self-contained app exited after startup_ready_operable before startup_post_initialization_maintenance_complete (exit code $($Process.ExitCode))."
            }
            throw "Self-contained app exited before startup_ready_operable (exit code $($Process.ExitCode))."
        }
        $logs = @(Get-ChildItem -LiteralPath $LogDirectory -Filter '*.log' -File -ErrorAction SilentlyContinue)
        foreach ($log in $logs) {
            $content = Get-Content -LiteralPath $log.FullName -Raw -ErrorAction SilentlyContinue
            if ($content -match 'startup_setting_validation_failed|app_schema_preflight_prompt_show|Startup library initialization failure notification') {
                throw "Self-contained app reported a startup blocker while waiting for startup completion. Log: $($log.FullName)"
            }
            if ($null -eq $readyLog -and $content -match 'startup_ready_operable') {
                $readyLog = $log.FullName
            }
            if ($content -match 'startup_post_initialization_maintenance_complete') {
                $postInitializationCompletionObserved = $true
            }
        }
        if ($null -ne $readyLog -and $postInitializationCompletionObserved) {
            return $readyLog
        }
        Start-Sleep -Milliseconds 250
        $Process.Refresh()
    }
    if ($null -eq $readyLog) {
        throw "Self-contained app did not reach startup_ready_operable before the acceptance execution deadline. Logs: $LogDirectory"
    }
    throw "Self-contained app did not reach startup_post_initialization_maintenance_complete after startup_ready_operable before the acceptance execution deadline. Logs: $LogDirectory"
}

function Wait-ForMainWindowHandle {
    param(
        [Parameter(Mandatory)][Diagnostics.Process]$Process,
        [int]$TimeoutSeconds = 30,
        [DateTime]$DeadlineUtc = [DateTime]::MinValue
    )

    $deadline = if ($DeadlineUtc -eq [DateTime]::MinValue) {
        $script:deadlinePolicy.ExecutionDeadlineUtc
    }
    else { $DeadlineUtc.ToUniversalTime() }
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($Process.HasExited) {
            throw "Self-contained app exited after startup completion (exit code $($Process.ExitCode))."
        }
        $Process.Refresh()
        if ($Process.MainWindowHandle -ne 0) {
            return [IntPtr]$Process.MainWindowHandle
        }
        Start-Sleep -Milliseconds 250
    }
    throw 'Self-contained app did not expose a main window handle before the acceptance execution deadline.'
}

function Prepare-LogDirectoryForRun {
    param([Parameter(Mandatory)][string]$LogDirectory)

    New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null
    $existingLogs = @(Get-ChildItem -LiteralPath $LogDirectory -Filter '*.log' -File -ErrorAction SilentlyContinue)
    if ($existingLogs.Count -eq 0) {
        return
    }
    $archiveDirectory = Join-Path $LogDirectory ('prior-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $archiveDirectory -Force | Out-Null
    foreach ($log in $existingLogs) {
        Move-Item -LiteralPath $log.FullName -Destination (Join-Path $archiveDirectory $log.Name) -Force
    }
}

function Assert-NoCompetingApplication {
    $runningProcesses = @(Get-Process -Name 'BeMusicSeeker' -ErrorAction SilentlyContinue)
    if ($runningProcesses.Count -gt 0) {
        throw "Existing-data acceptance requires no other BeMusicSeeker process to be running. PIDs: $($runningProcesses.Id -join ', ')"
    }
}

function Invoke-ProfileRun {
    param(
        [Parameter(Mandatory)][string]$ProfileRoot,
        [Parameter(Mandatory)][string]$AppExecutable,
        [Parameter(Mandatory)][string]$LocalAppData,
        [Parameter(Mandatory)][string]$LogDirectory,
        [Parameter(Mandatory)][object]$DeadlinePolicy
    )

    $executionDeadline = $DeadlinePolicy.ExecutionDeadlineUtc
    Prepare-LogDirectoryForRun -LogDirectory $LogDirectory
    Assert-NoCompetingApplication
    [void](Get-VerificationRemainingMilliseconds -DeadlineUtc $executionDeadline -OperationName 'acceptance profile process start')
    $userProfile = Split-Path -Parent (Split-Path -Parent $LocalAppData)
    $tempDirectory = Join-Path $ProfileRoot 'temp'
    New-Item -ItemType Directory -Path $tempDirectory -Force | Out-Null
    $arguments = if ([string]::IsNullOrWhiteSpace($UpdateManifestUrl)) {
        @()
    }
    else {
        @("--update-manifest-url=$UpdateManifestUrl")
    }
    $diagnosticsDirectory = Join-Path $LogDirectory 'process'
    $started = Start-VerificationRedirectedProcess `
        -FileName $AppExecutable `
        -Arguments $arguments `
        -WorkingDirectory (Split-Path -Parent $AppExecutable) `
        -Environment @{
            LOCALAPPDATA = $LocalAppData
            USERPROFILE = $userProfile
            TEMP = $tempDirectory
            TMP = $tempDirectory
        } `
        -DeadlinePolicy $DeadlinePolicy `
        -DiagnosticsDirectory $diagnosticsDirectory
    $process = $started.Process
    try {
        $readyLog = Wait-ForStartupCompletion -Process $process -LogDirectory $LogDirectory -DeadlineUtc $executionDeadline
        $capturedMainWindowHandle = Wait-ForMainWindowHandle -Process $process -DeadlineUtc $executionDeadline
        Assert-VerificationNoUnexpectedOwnedModal `
            -ProcessId $process.Id `
            -MainWindowHandle $capturedMainWindowHandle

        if ($capturedMainWindowHandle -eq 0 -or
            -not [BeMusicSeekerAcceptanceWindowMessage]::PostMessage(
                $capturedMainWindowHandle,
                0x0010,
                [IntPtr]::Zero,
                [IntPtr]::Zero)) {
            throw 'Self-contained app did not accept a graceful shutdown request for the captured main window.'
        }
        $shutdownRequest = 'captured_main_window_wm_close'
        $lifecycleResult = Complete-VerificationRedirectedProcess `
            -Started $started `
            -DiagnosticsDirectory $diagnosticsDirectory `
            -DeadlinePolicy $DeadlinePolicy `
            -LifecycleName 'net10-existing-data'
        if ($null -ne $lifecycleResult.PrimaryFailureKind) {
            throw (Get-VerificationLifecycleFailureMessage `
                    -Label 'Self-contained app graceful shutdown' `
                    -Result $lifecycleResult `
                    -TimeoutSeconds $DeadlinePolicy.TimeoutSeconds)
        }
        if (@($lifecycleResult.SecondaryDiagnostics).Count -gt 0) {
            throw "Self-contained app lifecycle diagnostics reported failure: $(@($lifecycleResult.SecondaryDiagnostics) -join '; ')"
        }
        if ($lifecycleResult.ExitCode -ne 0) {
            throw "Self-contained app shutdown returned exit code $($lifecycleResult.ExitCode)."
        }
        return [ordered]@{
            readyLog = $readyLog
            exitCode = $lifecycleResult.ExitCode
            shutdownRequest = $shutdownRequest
        }
    }
    catch {
        $primaryError = $_.Exception
        $cleanupDiagnostics = [System.Collections.Generic.List[string]]::new()
        try {
            $cleanup = Stop-VerificationOwnedProcessRecord `
                -Started $started `
                -CleanupDeadlineUtc $DeadlinePolicy.CleanupDeadlineUtc `
                -OperationName 'net10-existing-data profile cleanup'
            foreach ($diagnostic in @($cleanup.Diagnostics)) {
                $cleanupDiagnostics.Add([string]$diagnostic)
            }
        }
        catch {
            $cleanupDiagnostics.Add($_.Exception.ToString())
        }
        foreach ($diagnostic in $cleanupDiagnostics) {
            Add-VerificationExceptionSecondaryDiagnostic `
                -Exception $primaryError `
                -Diagnostic ([string]$diagnostic)
        }
        throw $primaryError
    }
}

function Get-PortableSettingsSnapshot {
    param([Parameter(Mandatory)][string]$Path)
    $map = Get-ManifestSettingMap -Path $Path
    return [ordered]@{
        AssemblyVersion = $map['AssemblyVersion']
        Lang = $map['Lang']
        AppearanceTheme = $map['AppearanceTheme']
        OperationModeLR2DB = $map['OperationModeLR2DB']
        BMSRootPath = $map['BMSRootPath']
        BMSInstallDir = $map['BMSInstallDir']
        LR2RootPath = $map['LR2RootPath']
        LR2SongDBPath = $map['LR2SongDBPath']
        LR2ConfigXmlPath = $map['LR2ConfigXmlPath']
    }
}

function Assert-ProfileSettings {
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary]$Settings,
        [Parameter(Mandatory)]$ManifestProfile,
        [Parameter(Mandatory)][string]$ProfileRoot
    )

    $expectedMode = ([bool]$ManifestProfile.operationModeLr2Db).ToString()
    if ($Settings.OperationModeLR2DB -ne $expectedMode) {
        throw "OperationModeLR2DB changed unexpectedly: expected=$expectedMode actual=$($Settings.OperationModeLR2DB)"
    }
    if ($Settings.Lang -ne [string]$script:manifest.settings.language -or $Settings.AppearanceTheme -ne [string]$script:manifest.settings.appearanceTheme) {
        throw 'Language or appearance theme did not survive existing-data startup.'
    }
    $expectedBmsRoot = if ([bool]$ManifestProfile.operationModeLr2Db) {
        Join-Path $ProfileRoot 'lr2\bms'
    }
    else {
        Join-Path $ProfileRoot ([string]$ManifestProfile.bmsRootDirectory)
    }
    if ($Settings.BMSInstallDir -ne $expectedBmsRoot) {
        throw "BMSInstallDir was not preserved: $($Settings.BMSInstallDir)"
    }
    if (-not [bool]$ManifestProfile.operationModeLr2Db) {
        if ($Settings.BMSRootPath -ne (Join-Path $ProfileRoot ([string]$ManifestProfile.bmsRootDirectory))) {
            throw "Standalone BMSRootPath was not preserved: $($Settings.BMSRootPath)"
        }
    }
    else {
        if ($Settings.LR2RootPath -ne (Join-Path $ProfileRoot ([string]$ManifestProfile.lr2RootDirectory))) {
            throw "LR2RootPath was not preserved: $($Settings.LR2RootPath)"
        }
    }
}

$AppPublishRoot = Resolve-FullPath $AppPublishRoot
$FixtureRoot = Resolve-FullPath $FixtureRoot
$OutputDirectory = Resolve-FullPath $OutputDirectory
Assert-Directory $AppPublishRoot
Assert-File (Join-Path $AppPublishRoot 'BeMusicSeeker.exe')
Assert-File (Join-Path $FixtureRoot 'fixture-manifest.json')
Assert-File (Join-Path $FixtureRoot 'fixture.bms')
Assert-File (Join-Path $FixtureRoot 'legacy-song.db')
Assert-File (Join-Path $FixtureRoot 'package\e1-fixture-package.marker')
foreach ($mutableDirectory in @('config', 'data', 'log')) {
    $mutablePath = Join-Path $AppPublishRoot $mutableDirectory
    if (Test-Path -LiteralPath $mutablePath) {
        throw "Self-contained publish root contains mutable application state; publish must be clean: $mutablePath"
    }
}

$script:manifest = Get-Content -LiteralPath (Join-Path $FixtureRoot 'fixture-manifest.json') -Raw | ConvertFrom-Json
if ([int]$script:manifest.schemaVersion -ne 1) {
    throw "Unsupported existing-data fixture manifest schema: $($script:manifest.schemaVersion)"
}
if ([string]::IsNullOrWhiteSpace([string]$script:manifest.provenance.sourceCommit)) {
    throw 'Existing-data fixture provenance must identify its source commit.'
}

$manifestHash = Get-Sha256 -Path (Join-Path $FixtureRoot 'fixture-manifest.json')
$fixtureDatabasePath = Join-Path $FixtureRoot ([string]$script:manifest.database.fixtureFile)
$fixtureDatabaseHash = Get-Sha256 -Path $fixtureDatabasePath
$receiptDirectory = Join-Path $OutputDirectory 'receipt'
New-Item -ItemType Directory -Path $receiptDirectory -Force | Out-Null
$receiptPath = Join-Path $receiptDirectory 'existing-data-acceptance.json'
$sandboxRoot = Join-Path ([IO.Path]::GetTempPath()) ('BeMusicSeeker-net10-existing-data-' + [Guid]::NewGuid().ToString('N'))
$profileReceipts = [Collections.Generic.List[object]]::new()
$failed = $false

try {
    New-Item -ItemType Directory -Path $sandboxRoot -Force | Out-Null
    foreach ($profile in $script:manifest.profiles) {
        $profileRoot = Join-Path $sandboxRoot ([string]$profile.name)
        $appRoot = Join-Path $profileRoot 'app'
        $localAppData = Join-Path $profileRoot 'user\AppData\Local'
        $logDirectory = Join-Path $appRoot 'log'
        New-Item -ItemType Directory -Path $profileRoot -Force | Out-Null
        Copy-Item -LiteralPath $AppPublishRoot -Destination $appRoot -Recurse -Force

        $bmsRoot = if ([bool]$profile.operationModeLr2Db) {
            Join-Path $profileRoot 'lr2\bms'
        }
        else {
            Join-Path $profileRoot ([string]$profile.bmsRootDirectory)
        }
        New-Item -ItemType Directory -Path (Join-Path $bmsRoot 'Fixture') -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $FixtureRoot 'fixture.bms') -Destination (Join-Path $bmsRoot $script:manifest.database.songRelativePath) -Force
        $packageRoot = Join-Path $profileRoot 'install\Fixture\package'
        New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $FixtureRoot 'package\e1-fixture-package.marker') -Destination (Join-Path $packageRoot 'e1-fixture-package.marker') -Force
        Copy-Item -LiteralPath (Join-Path $FixtureRoot 'fixture.bms') -Destination (Join-Path $packageRoot 'e1-fixture.bms') -Force

        $databasePath = Join-Path $appRoot 'data\song.db'
        $settings = @{
            AssemblyVersion = [string]$script:manifest.settings.assemblyVersion
            Lang = [string]$script:manifest.settings.language
            AppearanceTheme = [string]$script:manifest.settings.appearanceTheme
            OperationModeLR2DB = ([bool]$profile.operationModeLr2Db).ToString()
            BMSInstallDir = $bmsRoot
            BMSRootPath = $bmsRoot
            TableListURL = 'https://example.invalid/e1-existing-data-table-list'
            EnablePlaylistUrlCompletion = 'False'
            EnableStellaFullPlaylistUrlCompletion = 'False'
            SkipInitPlaylistLoad = 'True'
            ScanBmsFilesOnStartup = 'False'
        }
        if ([bool]$profile.operationModeLr2Db) {
            $lr2Root = Join-Path $profileRoot ([string]$profile.lr2RootDirectory)
            $databasePath = Join-Path $profileRoot ([string]$profile.lr2SongDbRelativePath)
            $lr2ConfigPath = Join-Path $profileRoot ([string]$profile.lr2ConfigRelativePath)
            $normalOutputBase = Join-Path $profileRoot 'custom-output'
            $rootOutputBase = Join-Path $profileRoot 'custom-root-output'
            New-Item -ItemType Directory -Path $normalOutputBase -Force | Out-Null
            New-Item -ItemType Directory -Path $rootOutputBase -Force | Out-Null
            Write-Lr2Config -Path $lr2ConfigPath -BmsRelativePath 'bms'
            $settings.LR2RootPath = $lr2Root
            $settings.LR2SongDBPath = $databasePath
            $settings.LR2ConfigXmlPath = $lr2ConfigPath
            $settings.LR2CustomFolderOutputBaseDir = $normalOutputBase
            $settings.LR2CustomFolderOutputBaseDirRootType = $rootOutputBase
        }
        $installPath = Join-Path $profileRoot 'install\Fixture\package'
        Copy-LegacyDatabaseFixture -Path $databasePath -Manifest $script:manifest -InstallPath $installPath -BmsRoot $bmsRoot

        $legacyConfigPath = Join-Path (Join-Path $localAppData 'BeMusicSeeker') ([string]$profile.legacyConfigDirectory)
        $legacyConfigPath = Join-Path $legacyConfigPath 'user.config'
        Write-LegacyConfig -Path $legacyConfigPath -Settings $settings
        $legacyConfigHash = Get-Sha256 -Path $legacyConfigPath
        $bmsFixturePath = Join-Path $bmsRoot $script:manifest.database.songRelativePath
        $bmsFixtureHash = Get-Sha256 -Path $bmsFixturePath

        $appExecutable = Join-Path $appRoot 'BeMusicSeeker.exe'
        $profileKind = if ([bool]$profile.operationModeLr2Db) { 'Lr2' } else { 'Standalone' }
        $firstRun = Invoke-ProfileRun `
            -ProfileRoot $profileRoot `
            -AppExecutable $appExecutable `
            -LocalAppData $localAppData `
            -LogDirectory $logDirectory `
            -DeadlinePolicy $script:deadlinePolicy
        $portableSettingsPath = Join-Path $appRoot 'config\user.config'
        Assert-File $portableSettingsPath
        $firstSettings = Get-PortableSettingsSnapshot -Path $portableSettingsPath
        Assert-ProfileSettings -Settings $firstSettings -ManifestProfile $profile -ProfileRoot $profileRoot
        $firstDatabase = Get-DatabaseSemanticSnapshot `
            -Path $databasePath `
            -Manifest $script:manifest `
            -InstallPath $installPath `
            -ProfileKind $profileKind `
            -BmsRoot $bmsRoot
        $firstSettingsHash = Get-Sha256 -Path $portableSettingsPath

        $secondRun = Invoke-ProfileRun `
            -ProfileRoot $profileRoot `
            -AppExecutable $appExecutable `
            -LocalAppData $localAppData `
            -LogDirectory $logDirectory `
            -DeadlinePolicy $script:deadlinePolicy
        $secondSettings = Get-PortableSettingsSnapshot -Path $portableSettingsPath
        Assert-ProfileSettings -Settings $secondSettings -ManifestProfile $profile -ProfileRoot $profileRoot
        $secondDatabase = Get-DatabaseSemanticSnapshot `
            -Path $databasePath `
            -Manifest $script:manifest `
            -InstallPath $installPath `
            -ProfileKind $profileKind `
            -BmsRoot $bmsRoot
        $secondSettingsHash = Get-Sha256 -Path $portableSettingsPath
        if ($firstSettingsHash -ne $secondSettingsHash) {
            throw "Portable settings changed on an otherwise identical second shutdown: profile=$($profile.name)"
        }
        if ((Get-Sha256 -Path $legacyConfigPath) -ne $legacyConfigHash) {
            throw "Legacy user.config was modified by migration: profile=$($profile.name)"
        }
        if ((Get-Sha256 -Path $bmsFixturePath) -ne $bmsFixtureHash) {
            throw "BMS fixture was modified by startup: profile=$($profile.name)"
        }

        $profileReceipts.Add([ordered]@{
            name = [string]$profile.name
            profileKind = $profileKind
            firstRun = $firstRun
            secondRun = $secondRun
            portableSettingsPath = $portableSettingsPath
            portableSettingsSha256 = $secondSettingsHash
            legacyUserConfigSha256 = $legacyConfigHash
            bmsFixtureSha256 = $bmsFixtureHash
            firstDatabase = $firstDatabase
            secondDatabase = $secondDatabase
        })
    }

    $receipt = [ordered]@{
        schemaVersion = 1
        status = 'passed'
        sourceCommit = [string]$script:manifest.provenance.sourceCommit
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        operatingSystem = [Environment]::OSVersion.VersionString
        processArchitecture = [Environment]::Is64BitProcess ? 'x64' : 'x86'
        appPublishRoot = $AppPublishRoot
        artifactManifestPath = if ($artifactManifestMode) { $artifactManifest.ManifestPath } else { $null }
        artifactId = if ($artifactManifestMode) { $artifactManifest.ArtifactId } else { $null }
        artifactRunId = if ($artifactManifestMode) { $artifactManifest.RunId } else { $null }
        fixtureManifestSha256 = $manifestHash
        fixtureDatabaseSha256 = $fixtureDatabaseHash
        profiles = $profileReceipts
    }
    [IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    Write-Host "Existing-data SCD acceptance passed: $receiptPath"
}
catch {
    $failed = $true
    $failureErrorRecord = $_
    $failure = [ordered]@{
        schemaVersion = 1
        status = 'failed'
        sourceCommit = [string]$script:manifest.provenance.sourceCommit
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        appPublishRoot = $AppPublishRoot
        artifactManifestPath = if ($artifactManifestMode) { $artifactManifest.ManifestPath } else { $null }
        artifactId = if ($artifactManifestMode) { $artifactManifest.ArtifactId } else { $null }
        artifactRunId = if ($artifactManifestMode) { $artifactManifest.RunId } else { $null }
        fixtureManifestSha256 = $manifestHash
        fixtureDatabaseSha256 = $fixtureDatabaseHash
        error = $failureErrorRecord.Exception.ToString()
        sandboxRoot = $sandboxRoot
    }
    try {
        [IO.File]::WriteAllText($receiptPath, ($failure | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    }
    catch {
        Add-VerificationExceptionSecondaryDiagnostic `
            -Exception $failureErrorRecord.Exception `
            -Diagnostic "failure receipt write failed: $($_.Exception.ToString())"
    }
    throw $failureErrorRecord
}
finally {
    if ($KeepSandbox -or $failed) {
        Write-Host "Existing-data acceptance sandbox retained: $sandboxRoot"
    }
    elseif (Test-Path -LiteralPath $sandboxRoot) {
        [void](Get-VerificationRemainingMilliseconds `
                -DeadlineUtc $script:deadlinePolicy.ExecutionDeadlineUtc `
                -OperationName 'successful acceptance sandbox cleanup')
        Remove-Item -LiteralPath $sandboxRoot -Recurse -Force -ErrorAction Stop
        if ([DateTime]::UtcNow -ge $script:deadlinePolicy.ExecutionDeadlineUtc) {
            throw 'Successful acceptance sandbox cleanup crossed the execution deadline.'
        }
    }
}
