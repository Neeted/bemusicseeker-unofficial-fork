[CmdletBinding()]
param(
    [string]$AppPublishRoot,
    [string]$FixtureRoot,
    [string]$OutputDirectory,
    [string]$SqliteAssemblyRoot,
    [string]$UpdateManifestUrl,
    [switch]$KeepSandbox,
    [switch]$ImportFunctionsOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class BeMusicSeekerAcceptanceWindowMessage
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
}
'@

if ([string]::IsNullOrWhiteSpace($AppPublishRoot)) {
    $AppPublishRoot = $env:BMS_SCD_APP_PUBLISH_ROOT
}
if ([string]::IsNullOrWhiteSpace($AppPublishRoot)) {
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

function Get-TreeSha256 {
    param([Parameter(Mandatory)][string]$Root)

    $rootPath = Resolve-FullPath $Root
    $entries = foreach ($file in Get-ChildItem -LiteralPath $rootPath -Recurse -File | Sort-Object FullName) {
        $relativePath = $file.FullName.Substring($rootPath.Length).TrimStart('\\').Replace('\\', '/')
        "$relativePath`t$((Get-Sha256 -Path $file.FullName))"
    }
    $payload = [Text.Encoding]::UTF8.GetBytes(($entries -join "`n"))
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($hash.ComputeHash($payload))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $hash.Dispose()
    }
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

function Write-LegacyConfig {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][hashtable]$Settings
    )

    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $document = [Xml.XmlDocument]::new()
    $configuration = $document.CreateElement('configuration')
    [void]$document.AppendChild($configuration)
    $userSettings = $document.CreateElement('userSettings')
    [void]$configuration.AppendChild($userSettings)
    $section = $document.CreateElement('BeMusicSeeker.Properties.Settings')
    [void]$userSettings.AppendChild($section)
    foreach ($name in $Settings.Keys) {
        $setting = $document.CreateElement('setting')
        $setting.SetAttribute('name', [string]$name)
        $setting.SetAttribute('serializeAs', 'String')
        $value = $document.CreateElement('value')
        $value.InnerText = [string]$Settings[$name]
        [void]$setting.AppendChild($value)
        [void]$section.AppendChild($setting)
    }
    $document.Save($Path)
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

function Wait-ForStartupReady {
    param(
        [Parameter(Mandatory)][Diagnostics.Process]$Process,
        [Parameter(Mandatory)][string]$LogDirectory,
        [int]$TimeoutSeconds = 180,
        [DateTime]$DeadlineUtc = [DateTime]::MinValue
    )

    $deadline = if ($DeadlineUtc -eq [DateTime]::MinValue) {
        [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    }
    else {
        $DeadlineUtc
    }
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($Process.HasExited) {
            throw "Self-contained app exited before startup_ready_operable (exit code $($Process.ExitCode))."
        }
        $logs = @(Get-ChildItem -LiteralPath $LogDirectory -Filter '*.log' -File -ErrorAction SilentlyContinue)
        foreach ($log in $logs) {
            $content = Get-Content -LiteralPath $log.FullName -Raw -ErrorAction SilentlyContinue
            if ($content -match 'startup_setting_validation_failed|app_schema_preflight_prompt_show|Startup library initialization failure notification') {
                throw "Self-contained app reported a startup blocker before startup_ready_operable. Log: $($log.FullName)"
            }
            if ($content -match 'startup_ready_operable') {
                return $log.FullName
            }
        }
        Start-Sleep -Milliseconds 250
        $Process.Refresh()
    }
    throw "Self-contained app did not reach startup_ready_operable within $TimeoutSeconds seconds. Logs: $LogDirectory"
}

function Wait-ForMainWindowHandle {
    param(
        [Parameter(Mandatory)][Diagnostics.Process]$Process,
        [int]$TimeoutSeconds = 30,
        [DateTime]$DeadlineUtc = [DateTime]::MinValue
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    if ($DeadlineUtc -ne [DateTime]::MinValue -and $DeadlineUtc -lt $deadline) {
        $deadline = $DeadlineUtc
    }
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($Process.HasExited) {
            throw "Self-contained app exited after startup_ready_operable (exit code $($Process.ExitCode))."
        }
        $Process.Refresh()
        if ($Process.MainWindowHandle -ne 0) {
            return
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Self-contained app did not expose a main window handle within $TimeoutSeconds seconds."
}

function Wait-ForLr2SyncEvent {
    param(
        [Parameter(Mandatory)][Diagnostics.Process]$Process,
        [Parameter(Mandatory)][string]$LogDirectory,
        [Parameter(Mandatory)][ValidateSet('CompletedThisRun', 'AlreadyCompleted')][string]$Expectation,
        [Parameter(Mandatory)][DateTime]$DeadlineUtc
    )

    $expectedPattern = if ($Expectation -eq 'CompletedThisRun') {
        'lr2_song_db_sync completed reason=post_startup_startup_initialization_ready(?=\s|$).*\bstage=completed(?=\s|$).*\bdetail=completed(?=\s|$)'
    }
    else {
        'lr2_song_db_sync_status evaluate reason=post_startup_startup_initialization_ready enabled=true force=false status=Completed storedStatus=Completed(?=\s|$)'
    }
    $terminalPattern = 'startup_initialization_complete elapsedMs=[0-9]+(?=\s|$)'
    $failurePattern = 'lr2_song_db_sync (?:prepare_failed|failed|cancelled|incomplete)(?=\s|$)|lr2_song_db_sync_status evaluate(?=\s|$).*\b(?:status|storedStatus)=(?:Failed|Cancelled|Incomplete)(?=\s|$)|startup_background_task failed name=(?:lr2_song_db_sync|lr2_song_db_sync_enrollment)(?=\s|$)'
    $matchedExpectedEvent = $null
    $matchedTerminalEvent = $null
    while ([DateTime]::UtcNow -lt $DeadlineUtc) {
        if ($Process.HasExited) {
            throw "Self-contained app exited before the expected LR2 sync event '$Expectation' (exit code $($Process.ExitCode))."
        }
        $logs = @(Get-ChildItem -LiteralPath $LogDirectory -Filter '*.log' -File -ErrorAction SilentlyContinue)
        foreach ($log in $logs) {
            $lines = @(Get-Content -LiteralPath $log.FullName -ErrorAction SilentlyContinue)
            foreach ($line in $lines) {
                if ($line -cmatch $failurePattern) {
                    throw "LR2 sync reported a terminal failure before '$Expectation'. Log: $($log.FullName) Event: $line"
                }
                if ($null -eq $matchedExpectedEvent -and $line -cmatch $expectedPattern) {
                    $matchedExpectedEvent = [ordered]@{
                        expectation = $Expectation
                        event = if ($Expectation -eq 'CompletedThisRun') { 'lr2_song_db_sync_completed' } else { 'lr2_song_db_sync_already_completed' }
                        logPath = $log.FullName
                        logLine = [string]$line
                    }
                }
                if ($null -eq $matchedTerminalEvent -and $line -cmatch $terminalPattern) {
                    $matchedTerminalEvent = [ordered]@{
                        event = 'startup_initialization_complete'
                        logPath = $log.FullName
                        logLine = [string]$line
                    }
                }
            }
        }
        if ($null -ne $matchedExpectedEvent -and $null -ne $matchedTerminalEvent) {
            $Process.Refresh()
            if ($Process.HasExited) {
                throw "Self-contained app exited after reporting LR2 sync events but before graceful shutdown (exit code $($Process.ExitCode))."
            }
            return [ordered]@{
                expectation = $matchedExpectedEvent.expectation
                event = $matchedExpectedEvent.event
                logPath = $matchedExpectedEvent.logPath
                logLine = $matchedExpectedEvent.logLine
                terminalEvent = $matchedTerminalEvent.event
                terminalLogPath = $matchedTerminalEvent.logPath
                terminalLogLine = $matchedTerminalEvent.logLine
            }
        }
        Start-Sleep -Milliseconds 250
        $Process.Refresh()
    }
    $observedExpectedEvent = if ($null -eq $matchedExpectedEvent) { 'none' } else { $matchedExpectedEvent.event }
    $observedTerminalEvent = if ($null -eq $matchedTerminalEvent) { 'none' } else { $matchedTerminalEvent.event }
    throw "Self-contained app did not report the complete LR2 startup event sequence for '$Expectation' before the 180-second run deadline. expectedEvent=$observedExpectedEvent terminalEvent=$observedTerminalEvent Logs: $LogDirectory"
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
        [ValidateSet('None', 'CompletedThisRun', 'AlreadyCompleted')][string]$Lr2SyncExpectation = 'None'
    )

    Prepare-LogDirectoryForRun -LogDirectory $LogDirectory
    Assert-NoCompetingApplication
    $runDeadlineUtc = [DateTime]::UtcNow.AddSeconds(180)
    $process = [Diagnostics.Process]::new()
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $AppExecutable
    $startInfo.WorkingDirectory = Split-Path -Parent $AppExecutable
    $startInfo.UseShellExecute = $false
    $startInfo.Environment['LOCALAPPDATA'] = $LocalAppData
    $userProfile = Split-Path -Parent (Split-Path -Parent $LocalAppData)
    $startInfo.Environment['USERPROFILE'] = $userProfile
    $startInfo.Environment['TEMP'] = Join-Path $ProfileRoot 'temp'
    $startInfo.Environment['TMP'] = Join-Path $ProfileRoot 'temp'
    if (-not [string]::IsNullOrWhiteSpace($UpdateManifestUrl)) {
        $startInfo.ArgumentList.Add("--update-manifest-url=$UpdateManifestUrl")
    }
    New-Item -ItemType Directory -Path $startInfo.Environment['TEMP'] -Force | Out-Null
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "Unable to start acceptance app: $AppExecutable"
    }
    try {
        $readyLog = Wait-ForStartupReady -Process $process -LogDirectory $LogDirectory -DeadlineUtc $runDeadlineUtc
        Wait-ForMainWindowHandle -Process $process -DeadlineUtc $runDeadlineUtc
        $waitedEvent = if ($Lr2SyncExpectation -eq 'None') {
            $null
        }
        else {
            Wait-ForLr2SyncEvent `
                -Process $process `
                -LogDirectory $LogDirectory `
                -Expectation $Lr2SyncExpectation `
                -DeadlineUtc $runDeadlineUtc
        }
        $closeRequested = $process.CloseMainWindow()
        $shutdownRequest = 'close_main_window'
        if (-not $closeRequested) {
            $windowHandle = $process.MainWindowHandle
            if ($windowHandle -eq 0 -or -not [BeMusicSeekerAcceptanceWindowMessage]::PostMessage($windowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)) {
                throw 'Self-contained app did not accept a graceful shutdown request.'
            }
            $shutdownRequest = 'wm_close_fallback'
        }
        if (-not $process.WaitForExit(120000)) {
            $process.Kill()
            throw 'Self-contained app did not exit after graceful shutdown request.'
        }
        if ($process.ExitCode -ne 0) {
            throw "Self-contained app shutdown returned exit code $($process.ExitCode)."
        }
        return [ordered]@{
            readyLog = $readyLog
            waitedEvent = $waitedEvent
            exitCode = $process.ExitCode
            shutdownRequest = $shutdownRequest
        }
    }
    finally {
        if (-not $process.HasExited) {
            $process.Kill()
            $process.WaitForExit()
        }
        $process.Dispose()
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

if ($ImportFunctionsOnly) {
    return
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

$appPublishTreeHash = Get-TreeSha256 -Root $AppPublishRoot
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
        $firstRunExpectation = if ($profileKind -eq 'Lr2') { 'CompletedThisRun' } else { 'None' }
        $firstRun = Invoke-ProfileRun `
            -ProfileRoot $profileRoot `
            -AppExecutable $appExecutable `
            -LocalAppData $localAppData `
            -LogDirectory $logDirectory `
            -Lr2SyncExpectation $firstRunExpectation
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

        $secondRunExpectation = if ($profileKind -eq 'Lr2') { 'AlreadyCompleted' } else { 'None' }
        $secondRun = Invoke-ProfileRun `
            -ProfileRoot $profileRoot `
            -AppExecutable $appExecutable `
            -LocalAppData $localAppData `
            -LogDirectory $logDirectory `
            -Lr2SyncExpectation $secondRunExpectation
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
        appPublishTreeSha256 = $appPublishTreeHash
        fixtureManifestSha256 = $manifestHash
        fixtureDatabaseSha256 = $fixtureDatabaseHash
        profiles = $profileReceipts
    }
    [IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    Write-Host "Existing-data SCD acceptance passed: $receiptPath"
}
catch {
    $failed = $true
    $failure = [ordered]@{
        schemaVersion = 1
        status = 'failed'
        sourceCommit = [string]$script:manifest.provenance.sourceCommit
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        appPublishRoot = $AppPublishRoot
        appPublishTreeSha256 = $appPublishTreeHash
        fixtureManifestSha256 = $manifestHash
        fixtureDatabaseSha256 = $fixtureDatabaseHash
        error = $_.Exception.ToString()
        sandboxRoot = $sandboxRoot
    }
    [IO.File]::WriteAllText($receiptPath, ($failure | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    throw
}
finally {
    if ($KeepSandbox -or $failed) {
        Write-Host "Existing-data acceptance sandbox retained: $sandboxRoot"
    }
    elseif (Test-Path -LiteralPath $sandboxRoot) {
        Remove-Item -LiteralPath $sandboxRoot -Recurse -Force
    }
}
