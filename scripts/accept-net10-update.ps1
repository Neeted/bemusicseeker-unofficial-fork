[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ArtifactManifestPath,
    [string]$FixtureRoot,
    [string]$OutputDirectory,
    [string]$SqliteAssemblyRoot,
    [ValidateRange(1, 300)]
    [int]$TimeoutSeconds = 240,
    [DateTime]$ExecutionDeadlineUtc = [DateTime]::MinValue,
    [DateTime]$CleanupDeadlineUtc = [DateTime]::MinValue,
    [switch]$KeepSandbox
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($FixtureRoot)) {
    $FixtureRoot = Join-Path $repoRoot 'devdocs\acceptance\net10-existing-data'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts\verification\net10-update'
}
if ([string]::IsNullOrWhiteSpace($SqliteAssemblyRoot)) {
    $SqliteAssemblyRoot = Join-Path $repoRoot 'BeMusicSeeker.Tests\bin\x64\Release\net10.0-windows'
}

. (Join-Path $repoRoot 'scripts\portable-package-layout.ps1')
. (Join-Path $repoRoot 'scripts\distribution-artifact.ps1')
. (Join-Path $repoRoot 'scripts\verification-process-lifecycle.ps1')
. (Join-Path $repoRoot 'scripts\acceptance-settings-fixture.ps1')

$script:deadlinePolicy = Resolve-VerificationDeadlinePair `
    -TimeoutSeconds $TimeoutSeconds `
    -ExecutionDeadlineUtc $ExecutionDeadlineUtc `
    -CleanupDeadlineUtc $CleanupDeadlineUtc

$artifactManifest = Read-DistributionArtifactManifest -ManifestPath $ArtifactManifestPath

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class BeMusicSeekerUpdateAcceptanceWindowMessage
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
}
'@

function Resolve-FullPath {
    param([Parameter(Mandatory)][string]$Path)
    return [IO.Path]::GetFullPath($Path)
}

function Assert-File {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required update acceptance file is missing: $Path"
    }
}

function Assert-Directory {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "Required update acceptance directory is missing: $Path"
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-ManifestSettings {
    param([Parameter(Mandatory)][string]$Path)
    $document = [Xml.XmlDocument]::new()
    $document.Load($Path)
    $section = $document.SelectSingleNode('/configuration/userSettings/BeMusicSeeker.Properties.Settings')
    if ($null -eq $section) {
        throw "Portable settings section is missing: $Path"
    }
    $result = @{}
    foreach ($setting in $section.SelectNodes('setting')) {
        $result[[string]$setting.GetAttribute('name')] = [string]$setting.SelectSingleNode('value').InnerText
    }
    return $result
}

$sqliteRuntimeLoaded = $false
function Initialize-SqliteRuntime {
    if ($script:sqliteRuntimeLoaded) {
        return
    }
    foreach ($name in @('SQLitePCLRaw.core.dll', 'SQLitePCLRaw.batteries_v2.dll', 'SQLite-net.dll', 'BeMusicSeeker.dll')) {
        Assert-File (Join-Path $SqliteAssemblyRoot $name)
        Add-Type -Path (Join-Path $SqliteAssemblyRoot $name) -ErrorAction SilentlyContinue
    }
    $nativeRoot = Join-Path $SqliteAssemblyRoot 'runtimes\win-x64\native'
    if (Test-Path -LiteralPath $nativeRoot -PathType Container) {
        $env:PATH = $nativeRoot + [IO.Path]::PathSeparator + $env:PATH
    }
    [SQLitePCL.Batteries_V2]::Init()
    $script:sqliteRuntimeLoaded = $true
}

function Copy-LegacyDatabaseFixture {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$InstallPath,
        [Parameter(Mandatory)][string]$BmsRoot
    )
    Initialize-SqliteRuntime
    $fixturePath = Join-Path $FixtureRoot ([string]$script:manifest.database.fixtureFile)
    Assert-File $fixturePath
    if ((Get-Sha256 -Path $fixturePath) -ne [string]$script:manifest.database.fixtureSha256) {
        throw "Tracked legacy database fixture hash does not match the manifest: $fixturePath"
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    Copy-Item -LiteralPath $fixturePath -Destination $Path -Force
    $database = [SQLite.SQLiteConnection]::new(
        $Path,
        [SQLite.SQLiteOpenFlags]::ReadWrite -bor [SQLite.SQLiteOpenFlags]::FullMutex,
        $true)
    try {
        $row = $script:manifest.database
        $songPath = Join-Path $BmsRoot ([string]$row.songRelativePath)
        $song = [int]$database.Execute(
            'UPDATE song SET path = ? WHERE hash = ? AND title = ?',
            [object[]]@([string]$songPath, [string]$row.songHash, [string]$row.songTitle))
        $maintenance = [int]$database.Execute(
            'UPDATE maintenance SET path = ? WHERE hash = ?',
            [object[]]@([string]$songPath, [string]$row.songHash))
        $install = [int]$database.Execute(
            'UPDATE install SET path = ? WHERE path = ?',
            [object[]]@([string]$InstallPath, '__E1_INSTALL_PATH__'))
        if ($song -ne 1 -or $maintenance -ne 1 -or $install -ne 1) {
            throw "Legacy database fixture relocation failed: song=$song maintenance=$maintenance install=$install"
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
        [Parameter(Mandatory)][string]$InstallPath
    )
    Initialize-SqliteRuntime
    Assert-File $Path
    $database = [SQLite.SQLiteConnection]::new(
        $Path,
        [SQLite.SQLiteOpenFlags]::ReadOnly -bor [SQLite.SQLiteOpenFlags]::FullMutex,
        $true)
    try {
        $row = $script:manifest.database
        $values = [ordered]@{
            song = [int]$database.ExecuteScalar[int]('SELECT COUNT(*) FROM song WHERE hash = ? AND title = ?', @([string]$row.songHash, [string]$row.songTitle))
            # The folder row is persisted by title, while legacy and current
            # owners may normalize its path to a sandbox-absolute directory.
            # Anchor it to the fixture directory so generated LR2 presentation
            # folders with the same display title are not mistaken for it.
            folder = [int]$database.ExecuteScalar[int](
                'SELECT COUNT(*) FROM folder WHERE title = ? AND (path = ? OR path LIKE ?)',
                @([string]$row.folderTitle, [string]$row.folderRelativePath, '%\Fixture\'))
            install = [int]$database.ExecuteScalar[int]('SELECT COUNT(*) FROM install WHERE path = ?', @([string]$InstallPath))
            playlist = [int]$database.ExecuteScalar[int]('SELECT COUNT(*) FROM playlist WHERE playlist_id = ? AND name = ?', @([int]$row.playlistId, [string]$row.playlistName))
            course = [int]$database.ExecuteScalar[int]('SELECT COUNT(*) FROM playlist_course WHERE course_id = ? AND playlist_id = ? AND course_json = ?', @([int]$row.courseId, [int]$row.playlistId, [string]$row.courseJson))
            entry = [int]$database.ExecuteScalar[int]('SELECT COUNT(*) FROM playlist_entry WHERE playlist_id = ? AND md5 = ? AND title = ?', @([int]$row.playlistId, [string]$row.entryMd5, [string]$row.entryTitle))
        }
        foreach ($name in $values.Keys) {
            if ($values[$name] -ne 1) {
                throw "Update acceptance semantic row was not preserved: $name=$($values[$name]) path=$Path"
            }
        }
        return $values
    }
    finally {
        $database.Close()
        $database.Dispose()
    }
}

function Prepare-LogDirectory {
    param([Parameter(Mandatory)][string]$LogDirectory)
    New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null
    foreach ($log in @(Get-ChildItem -LiteralPath $LogDirectory -Filter '*.log' -File -ErrorAction SilentlyContinue)) {
        $archive = Join-Path $LogDirectory ('prior-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $archive -Force | Out-Null
        Move-Item -LiteralPath $log.FullName -Destination (Join-Path $archive $log.Name) -Force
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
        $script:deadlinePolicy.ExecutionDeadlineUtc
    }
    else { $DeadlineUtc.ToUniversalTime() }
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($Process.HasExited) {
            throw "Application exited before startup_ready_operable (exit code $($Process.ExitCode))."
        }
        foreach ($log in @(Get-ChildItem -LiteralPath $LogDirectory -Filter '*.log' -File -ErrorAction SilentlyContinue)) {
            $content = Get-Content -LiteralPath $log.FullName -Raw -ErrorAction SilentlyContinue
            if ($content -match 'startup_setting_validation_failed|app_schema_preflight_prompt_show|Startup library initialization failure notification') {
                throw "Application reported a startup blocker. Log: $($log.FullName)"
            }
            if ($content -match 'startup_ready_operable') {
                return $log.FullName
            }
        }
        $remainingMilliseconds = Get-VerificationRemainingMilliseconds `
            -DeadlineUtc $deadline `
            -OperationName 'application startup-ready wait'
        Start-Sleep -Milliseconds ([Math]::Min(250, $remainingMilliseconds))
        $Process.Refresh()
    }
    throw "Application did not reach startup_ready_operable before the acceptance execution deadline. Logs: $LogDirectory"
}

function Wait-ForMainWindow {
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
            throw "Application exited before exposing a main window (exit code $($Process.ExitCode))."
        }
        $Process.Refresh()
        if ($Process.MainWindowHandle -ne 0) {
            return
        }
        $remainingMilliseconds = Get-VerificationRemainingMilliseconds `
            -DeadlineUtc $deadline `
            -OperationName 'application main-window wait'
        Start-Sleep -Milliseconds ([Math]::Min(250, $remainingMilliseconds))
    }
    throw 'Application did not expose a main window before the acceptance execution deadline.'
}



function Start-IsolatedApplication {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string]$ProfileRoot,
        [Parameter(Mandatory)][string]$LogDirectory,
        [Parameter(Mandatory)][object]$DeadlinePolicy,
        [System.Collections.IList]$OwnedProcessRecords
    )
    Prepare-LogDirectory -LogDirectory $LogDirectory
    $localAppData = Join-Path $ProfileRoot 'user\AppData\Local'
    $temp = Join-Path $ProfileRoot 'temp'
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    $started = Start-VerificationRedirectedProcess `
        -FileName $Executable `
        -WorkingDirectory (Split-Path -Parent $Executable) `
        -Environment @{
            LOCALAPPDATA = $localAppData
            USERPROFILE = Join-Path $ProfileRoot 'user'
            TEMP = $temp
            TMP = $temp
        } `
        -DeadlinePolicy $DeadlinePolicy `
        -DiagnosticsDirectory (Join-Path $LogDirectory 'process') `
        -OwnedProcessRecords $OwnedProcessRecords
    try {
        try {
            $remainingMilliseconds = Get-VerificationRemainingMilliseconds `
                -DeadlineUtc $DeadlinePolicy.ExecutionDeadlineUtc `
                -OperationName 'application input-idle wait'
            [void]$started.Process.WaitForInputIdle([Math]::Min(30000, $remainingMilliseconds))
        }
        catch { }
        [void](Wait-ForStartupReady -Process $started.Process -LogDirectory $LogDirectory -DeadlineUtc $DeadlinePolicy.ExecutionDeadlineUtc)
        Wait-ForMainWindow -Process $started.Process -DeadlineUtc $DeadlinePolicy.ExecutionDeadlineUtc
        return $started
    }
    catch {
        $primaryException = $_.Exception
        try {
            $cleanup = Stop-VerificationOwnedProcessRecord `
                -Started $started `
                -CleanupDeadlineUtc $DeadlinePolicy.CleanupDeadlineUtc `
                -OperationName 'net10-update application startup cleanup'
            if (-not $cleanup.Succeeded) {
                $primaryException.Data['VerificationSecondaryDiagnostics'] = @($cleanup.Diagnostics)
            }
        }
        catch {
            $primaryException.Data['VerificationSecondaryDiagnostics'] = @($_.Exception.ToString())
        }
        throw $primaryException
    }
}

function Close-IsolatedApplication {
    param(
        [Parameter(Mandatory)][object]$Started,
        [Parameter(Mandatory)][string]$DiagnosticsDirectory,
        [Parameter(Mandatory)][object]$DeadlinePolicy
    )
    try {
        $requested = $Started.Process.CloseMainWindow()
        if (-not $requested) {
            $handle = $Started.Process.MainWindowHandle
            if ($handle -eq 0 -or -not [BeMusicSeekerUpdateAcceptanceWindowMessage]::PostMessage($handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)) {
                throw 'Application did not accept a graceful shutdown request.'
            }
        }
        $result = Complete-VerificationRedirectedProcess `
            -Started $Started `
            -DiagnosticsDirectory $DiagnosticsDirectory `
            -DeadlinePolicy $DeadlinePolicy `
            -LifecycleName 'net10-update-application' `
            -TerminateProcessTree
        if ($null -ne $result.PrimaryFailureKind) {
            throw (Get-VerificationLifecycleFailureMessage `
                    -Label 'Application graceful shutdown' `
                    -Result $result `
                    -TimeoutSeconds $DeadlinePolicy.TimeoutSeconds)
        }
        if (@($result.SecondaryDiagnostics).Count -gt 0) {
            throw "Application lifecycle diagnostics reported failure: $(@($result.SecondaryDiagnostics) -join '; ')"
        }
        if ($null -ne $result.ExitCode -and [int]$result.ExitCode -ne 0) {
            throw "Application shutdown returned exit code $($result.ExitCode)."
        }
        return $result
    }
    catch {
        $primaryException = $_.Exception
        try {
            $cleanup = Stop-VerificationOwnedProcessRecord `
                -Started $Started `
                -CleanupDeadlineUtc $DeadlinePolicy.CleanupDeadlineUtc `
                -OperationName 'net10-update application cleanup'
            if (-not $cleanup.Succeeded) {
                $primaryException.Data['VerificationSecondaryDiagnostics'] = @($cleanup.Diagnostics)
            }
        }
        catch {
            $primaryException.Data['VerificationSecondaryDiagnostics'] = @($_.Exception.ToString())
        }
        throw $primaryException
    }
}


function Start-UpdaterHandshake {
    param(
        [Parameter(Mandatory)][string]$UpdaterExecutable,
        [Parameter(Mandatory)][string]$AppRoot,
        [Parameter(Mandatory)][string]$ProfileRoot,
        [Parameter(Mandatory)][string]$PackagePath,
        [Parameter(Mandatory)][int]$ApplicationProcessId,
        [Parameter(Mandatory)][object]$DeadlinePolicy,
        [System.Collections.IList]$OwnedProcessRecords
    )
    [void](Get-VerificationRemainingMilliseconds -DeadlineUtc $DeadlinePolicy.ExecutionDeadlineUtc -OperationName 'updater process start')
    $workRoot = Join-Path $AppRoot 'update_work'
    $current = Join-Path $workRoot 'current'
    $downloads = Join-Path $workRoot 'downloads'
    New-Item -ItemType Directory -Path $current, $downloads -Force | Out-Null
    $downloadedPackage = Join-Path $downloads ([IO.Path]::GetFileName($PackagePath))
    Copy-Item -LiteralPath $PackagePath -Destination $downloadedPackage -Force
    Assert-File $downloadedPackage
    $preparedUpdater = Join-Path $current 'BeMusicSeeker.Updater.exe'
    Copy-Item -LiteralPath $UpdaterExecutable -Destination $preparedUpdater -Force
    Assert-File $preparedUpdater
    $ready = Join-Path $current 'updater-ready.txt'
    $decision = Join-Path $current 'updater-decision.txt'
    Remove-Item -LiteralPath $ready, $decision -Force -ErrorAction SilentlyContinue
    $localAppData = Join-Path $ProfileRoot 'user\AppData\Local'
    $userProfile = Join-Path $ProfileRoot 'user'
    $temp = Join-Path $ProfileRoot 'temp'
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    $arguments = @(
        '--app-dir', $AppRoot,
        '--package', $downloadedPackage,
        '--backup-dir', (Join-Path $AppRoot 'update_backup'),
        '--ready-file', $ready,
        '--decision-file', $decision,
        '--pid', ([string]$ApplicationProcessId),
        '--restart-exe', (Join-Path $AppRoot 'BeMusicSeeker.exe'))
    $environment = @{
        LOCALAPPDATA = $localAppData
        USERPROFILE = $userProfile
        TEMP = $temp
        TMP = $temp
    }
    $started = Start-VerificationRedirectedProcess -FileName $preparedUpdater -Arguments $arguments -WorkingDirectory $current -Environment $environment -DeadlinePolicy $DeadlinePolicy -DiagnosticsDirectory (Join-Path $current 'process') -OwnedProcessRecords $OwnedProcessRecords
    try {
        while (-not (Test-Path -LiteralPath $ready -PathType Leaf)) {
            [void](Get-VerificationRemainingMilliseconds -DeadlineUtc $DeadlinePolicy.ExecutionDeadlineUtc -OperationName 'updater ready handshake')
            if ($started.Process.HasExited) {
                $result = Complete-VerificationRedirectedProcess -Started $started -DiagnosticsDirectory (Join-Path $current 'process') -DeadlinePolicy $DeadlinePolicy -LifecycleName 'net10-update-updater'
                throw "Updater exited before ready handshake (exit=$($result.ExitCode), failure=$($result.PrimaryFailureKind), stderr=$($result.StandardError))"
            }
            Start-Sleep -Milliseconds 100
        }
        return [pscustomobject]@{
            Process = $started.Process
            Started = $started
            ReadyPath = $ready
            DecisionPath = $decision
            DownloadedPackage = $downloadedPackage
            PreparedUpdater = $preparedUpdater
            WorkingDirectory = $current
            StandardOutput = $null
            StandardError = $null
            LifecycleResult = $null
        }
    }
    catch {
        $primaryException = $_.Exception
        try {
            $cleanup = Stop-VerificationOwnedProcessRecord -Started $started -CleanupDeadlineUtc $DeadlinePolicy.CleanupDeadlineUtc -OperationName 'net10-update updater handshake cleanup'
            if (-not $cleanup.Succeeded) {
                $primaryException.Data['VerificationSecondaryDiagnostics'] = @($cleanup.Diagnostics)
            }
        }
        catch {
            $primaryException.Data['VerificationSecondaryDiagnostics'] = @($_.Exception.ToString())
        }
        throw $primaryException
    }
}

function Approve-UpdaterHandshake {
    param([Parameter(Mandatory)]$Handshake)
    [IO.File]::WriteAllText($Handshake.DecisionPath, 'proceed', [Text.UTF8Encoding]::new($false))
}


function Wait-UpdaterExit {
    param(
        [Parameter(Mandatory)]$Handshake,
        [Parameter(Mandatory)][object]$DeadlinePolicy
    )
    try {
        $result = Complete-VerificationRedirectedProcess `
            -Started $Handshake.Started `
            -DiagnosticsDirectory (Join-Path $Handshake.WorkingDirectory 'process') `
            -DeadlinePolicy $DeadlinePolicy `
            -LifecycleName 'net10-update-updater'
        $Handshake.StandardOutput = $result.StandardOutput
        $Handshake.StandardError = $result.StandardError
        $Handshake.LifecycleResult = $result
        if ($result.PrimaryFailureKind -ceq 'timeout') {
            throw (Get-VerificationLifecycleFailureMessage `
                    -Label 'Updater completion' `
                    -Result $result `
                    -TimeoutSeconds $DeadlinePolicy.TimeoutSeconds)
        }
        if (@($result.SecondaryDiagnostics).Count -gt 0) {
            throw "Updater lifecycle diagnostics reported failure: $(@($result.SecondaryDiagnostics) -join '; ')"
        }
        return [int]$result.ExitCode
    }
    catch {
        $primaryException = $_.Exception
        if (-not $Handshake.Started.LifecycleCompleted) {
            try {
                $cleanup = Stop-VerificationOwnedProcessRecord `
                    -Started $Handshake.Started `
                    -CleanupDeadlineUtc $DeadlinePolicy.CleanupDeadlineUtc `
                    -OperationName 'net10-update updater completion cleanup'
                if (-not $cleanup.Succeeded) {
                    $primaryException.Data['VerificationSecondaryDiagnostics'] = @($cleanup.Diagnostics)
                }
            }
            catch {
                $primaryException.Data['VerificationSecondaryDiagnostics'] = @($_.Exception.ToString())
            }
        }
        throw $primaryException
    }
}

function Wait-RestartedApplication {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][DateTime]$DeadlineUtc,
        [System.Collections.IList]$OwnedProcessRecords
    )
    $normalized = (Resolve-FullPath $Executable)
    $deadline = $DeadlineUtc.ToUniversalTime()
    while ([DateTime]::UtcNow -lt $deadline) {
        $candidate = Find-RunningApplication -Executable $normalized
        if ($null -ne $candidate) {
            $record = New-VerificationOwnedProcessRecord `
                -Process $candidate `
                -CommandIdentity "restarted application: $normalized" `
                -DiagnosticsDirectory (Join-Path (Split-Path -Parent $normalized) 'process')
            if ($null -ne $OwnedProcessRecords) {
                [void]$OwnedProcessRecords.Add($record)
            }
            return $record
        }
        $remainingMilliseconds = Get-VerificationRemainingMilliseconds `
            -DeadlineUtc $deadline `
            -OperationName 'restarted application wait'
        Start-Sleep -Milliseconds ([Math]::Min(250, $remainingMilliseconds))
    }
    throw "Updater did not restart the application before the acceptance execution deadline: $Executable"
}

function Find-RunningApplication {
    param([Parameter(Mandatory)][string]$Executable)
    $normalized = Resolve-FullPath $Executable
    foreach ($candidate in @(Get-Process -ErrorAction SilentlyContinue)) {
        $matches = $false
        try {
            if (-not $candidate.HasExited) {
                $candidate.Refresh()
                $processPath = $candidate.MainModule.FileName
                $matches = -not [string]::IsNullOrWhiteSpace($processPath) -and
                    (Resolve-FullPath $processPath) -eq $normalized
            }
        }
        catch { }
        if ($matches) {
            return $candidate
        }
        try { $candidate.Dispose() } catch { }
    }
    return $null
}

function Archive-LogsBeforeRestart {
    param([Parameter(Mandatory)][string]$LogDirectory)
    $archive = Join-Path $LogDirectory ('prior-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $archive -Force | Out-Null
    foreach ($log in @(Get-ChildItem -LiteralPath $LogDirectory -Filter '*.log' -File -ErrorAction SilentlyContinue)) {
        Move-Item -LiteralPath $log.FullName -Destination (Join-Path $archive $log.Name) -Force
    }
}

function Expand-AppPackage {
    param([Parameter(Mandatory)][string]$PackagePath, [Parameter(Mandatory)][string]$Destination)
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Expand-Archive -LiteralPath $PackagePath -DestinationPath $Destination -Force
    Assert-File (Join-Path $Destination 'BeMusicSeeker.exe')
    Assert-File (Join-Path $Destination 'BeMusicSeeker.Updater.exe')
}

function Prepare-Profile {
    param(
        [Parameter(Mandatory)][string]$ProfileRoot,
        [Parameter(Mandatory)][string]$AppRoot
    )
    $bmsRoot = Join-Path $ProfileRoot 'bms'
    New-Item -ItemType Directory -Path (Join-Path $bmsRoot 'Fixture') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $FixtureRoot 'fixture.bms') -Destination (Join-Path $bmsRoot 'Fixture\e1-fixture.bms') -Force
    $packageRoot = Join-Path $ProfileRoot 'install\Fixture\package'
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $FixtureRoot 'package\e1-fixture-package.marker') -Destination (Join-Path $packageRoot 'e1-fixture-package.marker') -Force
    Copy-Item -LiteralPath (Join-Path $FixtureRoot 'fixture.bms') -Destination (Join-Path $packageRoot 'e1-fixture.bms') -Force
    $databasePath = Join-Path $AppRoot 'data\song.db'
    $settings = @{
        AssemblyVersion = [string]$script:manifest.settings.assemblyVersion
        Lang = [string]$script:manifest.settings.language
        AppearanceTheme = [string]$script:manifest.settings.appearanceTheme
        OperationModeLR2DB = 'False'
        BMSInstallDir = $bmsRoot
        BMSRootPath = $bmsRoot
        TableListURL = 'https://example.invalid/e2-update-table-list'
        EnablePlaylistUrlCompletion = 'False'
        EnableStellaFullPlaylistUrlCompletion = 'False'
        SkipInitPlaylistLoad = 'True'
    }
    $installPath = $packageRoot
    Copy-LegacyDatabaseFixture -Path $databasePath -InstallPath $installPath -BmsRoot $bmsRoot
    $legacyConfigRelativePath = 'BeMusicSeeker.exe_Url_E2Standalone\1.0.0.0\user.config'
    $legacyConfig = Join-Path (Join-Path $ProfileRoot 'user\AppData\Local\BeMusicSeeker') $legacyConfigRelativePath
    Write-LegacyConfig -Path $legacyConfig -Settings $settings
    $marker = Join-Path $AppRoot 'user-data\e2-unmanaged.marker'
    New-Item -ItemType Directory -Path (Split-Path -Parent $marker) -Force | Out-Null
    [IO.File]::WriteAllText($marker, "E2 unmanaged marker standalone`n", [Text.UTF8Encoding]::new($false))
    return [pscustomobject]@{
        BmsRoot = $bmsRoot
        DatabasePath = $databasePath
        InstallPath = $installPath
        LegacyConfigPath = $legacyConfig
        MarkerPath = $marker
    }
}

function Get-ProfileState {
    param([Parameter(Mandatory)]$Profile)
    $portable = Join-Path $Profile.AppRoot 'config\user.config'
    return [ordered]@{
        Database = Get-DatabaseSemanticSnapshot -Path $Profile.DatabasePath -InstallPath $Profile.InstallPath
        PortableSettings = if (Test-Path -LiteralPath $portable -PathType Leaf) { Get-ManifestSettings -Path $portable } else { @{} }
        LegacyConfigSha256 = Get-Sha256 -Path $Profile.LegacyConfigPath
        MarkerSha256 = Get-Sha256 -Path $Profile.MarkerPath
    }
}

function Assert-SemanticStateEqual {
    param([Parameter(Mandatory)]$Before, [Parameter(Mandatory)]$After, [Parameter(Mandatory)][string]$Label)
    if ((ConvertTo-Json $Before.Database -Compress) -ne (ConvertTo-Json $After.Database -Compress)) { throw "Database state changed across update: $Label" }
    if ($Before.LegacyConfigSha256 -ne $After.LegacyConfigSha256 -or $Before.MarkerSha256 -ne $After.MarkerSha256) { throw "Unmanaged/persisted state changed across update: $Label" }
    foreach ($name in @('OperationModeLR2DB', 'Lang', 'AppearanceTheme', 'BMSRootPath', 'BMSInstallDir', 'LR2RootPath', 'LR2SongDBPath', 'LR2ConfigXmlPath')) {
        if ([string]$Before.PortableSettings[$name] -ne [string]$After.PortableSettings[$name]) { throw "Portable setting changed across update: $Label $name" }
    }
}

$FixtureRoot = Resolve-FullPath $FixtureRoot
$OutputDirectory = Resolve-FullPath $OutputDirectory
Assert-Directory $FixtureRoot
Assert-File (Join-Path $FixtureRoot 'fixture-manifest.json')
Assert-File (Join-Path $FixtureRoot 'fixture.bms')
Assert-File (Join-Path $FixtureRoot 'legacy-song.db')
Assert-File (Join-Path $FixtureRoot 'package\e1-fixture-package.marker')
$script:manifest = Get-Content -LiteralPath (Join-Path $FixtureRoot 'fixture-manifest.json') -Raw | ConvertFrom-Json
$script:manifestHash = Get-Sha256 -Path (Join-Path $FixtureRoot 'fixture-manifest.json')
$script:sandboxRoot = Join-Path ([IO.Path]::GetTempPath()) ('BeMusicSeeker-net10-update-' + [Guid]::NewGuid().ToString('N'))
$script:receiptPath = Join-Path (Join-Path $OutputDirectory 'receipt') 'update-acceptance.json'
New-Item -ItemType Directory -Path (Split-Path -Parent $script:receiptPath) -Force | Out-Null
$script:failed = $false
$script:primaryError = $null
$script:ownedProcessRecords = [Collections.Generic.List[object]]::new()

try {
    $current = [pscustomobject]@{
        Commit = [string]$artifactManifest.Current.commit
        Version = [string]$artifactManifest.Current.version
        PackagePath = [string]$artifactManifest.Current.packagePath
        PackageSha256 = [string]$artifactManifest.Current.packageSha256
        AppPublishRoot = [string]$artifactManifest.Current.appRoot
    }

    $successRoot = Join-Path $script:sandboxRoot 'success'
    $successApp = Join-Path $successRoot 'app'
    Expand-AppPackage -PackagePath $current.PackagePath -Destination $successApp
    $expectedExecutableHash = Get-Sha256 -Path (Join-Path $current.AppPublishRoot 'BeMusicSeeker.exe')
    # A removed managed file makes this a real apply/cleanup check even though
    # the source and target application versions are the same.
    $obsoleteRelativePath = 'verification-obsolete.txt'
    $obsoletePath = Join-Path $successApp $obsoleteRelativePath
    [IO.File]::WriteAllText($obsoletePath, 'obsolete managed file', [Text.UTF8Encoding]::new($false))
    Add-Content -LiteralPath (Join-Path $successApp 'update-managed-files.txt') -Value "`n$obsoleteRelativePath" -Encoding utf8
    $successProfile = Prepare-Profile -ProfileRoot $successRoot -AppRoot $successApp
    $successLog = Join-Path $successApp 'log'
    $runningProcess = Start-IsolatedApplication `
        -Executable (Join-Path $successApp 'BeMusicSeeker.exe') `
        -ProfileRoot $successRoot `
        -LogDirectory $successLog `
        -DeadlinePolicy $script:deadlinePolicy `
        -OwnedProcessRecords $script:ownedProcessRecords
    $beforeSuccess = Get-ProfileState -Profile ([pscustomobject]@{ AppRoot = $successApp; DatabasePath = $successProfile.DatabasePath; InstallPath = $successProfile.InstallPath; LegacyConfigPath = $successProfile.LegacyConfigPath; MarkerPath = $successProfile.MarkerPath })
    # Public v2.1.6.0 migration is covered by accept-v216-first-hop.ps1.
    # This checks the current updater's handshake, apply and real app restart.
    $successUpdater = Start-UpdaterHandshake `
        -UpdaterExecutable (Join-Path $successApp 'BeMusicSeeker.Updater.exe') `
        -AppRoot $successApp `
        -ProfileRoot $successRoot `
        -PackagePath $current.PackagePath `
        -ApplicationProcessId $runningProcess.ProcessId `
        -DeadlinePolicy $script:deadlinePolicy `
        -OwnedProcessRecords $script:ownedProcessRecords
    Close-IsolatedApplication `
        -Started $runningProcess `
        -DiagnosticsDirectory (Join-Path $successLog 'process') `
        -DeadlinePolicy $script:deadlinePolicy
    Archive-LogsBeforeRestart -LogDirectory $successLog
    Approve-UpdaterHandshake -Handshake $successUpdater
    $successExitCode = Wait-UpdaterExit `
        -Handshake $successUpdater `
        -DeadlinePolicy $script:deadlinePolicy
    if ($successExitCode -ne 0) {
        throw "Current updater did not complete update: exit=$successExitCode stdout=$($successUpdater.StandardOutput) stderr=$($successUpdater.StandardError)"
    }
    $currentProcess = Wait-RestartedApplication `
        -Executable (Join-Path $successApp 'BeMusicSeeker.exe') `
        -DeadlineUtc $script:deadlinePolicy.ExecutionDeadlineUtc `
        -OwnedProcessRecords $script:ownedProcessRecords
    try {
        [void](Wait-ForStartupReady `
                -Process $currentProcess.Process `
                -LogDirectory $successLog `
                -DeadlineUtc $script:deadlinePolicy.ExecutionDeadlineUtc)
        Wait-ForMainWindow -Process $currentProcess.Process -DeadlineUtc $script:deadlinePolicy.ExecutionDeadlineUtc
    }
    catch {
        $primaryException = $_.Exception
        try {
            $cleanup = Stop-VerificationOwnedProcessRecord -Started $currentProcess -CleanupDeadlineUtc $script:deadlinePolicy.CleanupDeadlineUtc -OperationName 'net10-update restarted application cleanup'
            if (-not $cleanup.Succeeded) {
                $primaryException.Data['VerificationSecondaryDiagnostics'] = @($cleanup.Diagnostics)
            }
        }
        catch {
            $primaryException.Data['VerificationSecondaryDiagnostics'] = @($_.Exception.ToString())
        }
        throw $primaryException
    }
    Close-IsolatedApplication `
        -Started $currentProcess `
        -DiagnosticsDirectory (Join-Path $successLog 'process') `
        -DeadlinePolicy $script:deadlinePolicy
    $successAfter = Get-ProfileState -Profile ([pscustomobject]@{ AppRoot = $successApp; DatabasePath = $successProfile.DatabasePath; InstallPath = $successProfile.InstallPath; LegacyConfigPath = $successProfile.LegacyConfigPath; MarkerPath = $successProfile.MarkerPath })
    Assert-SemanticStateEqual -Before $beforeSuccess -After $successAfter -Label 'current updater success'
    Assert-PortableSingleFilePayloadLayout $successApp
    if ((Get-Sha256 -Path (Join-Path $successApp 'BeMusicSeeker.exe')) -ne $expectedExecutableHash) {
        throw 'The applied application executable does not match the current publish output.'
    }
    if (Test-Path -LiteralPath $obsoletePath) {
        throw 'The updater did not remove the obsolete managed file.'
    }
    Assert-File (Join-Path $successApp 'BeMusicSeeker.Updater.exe')
    if (Test-Path -LiteralPath (Join-Path $successApp 'BeMusicSeeker.Updater.dll')) { throw 'Current single-file updater left a companion DLL.' }

    $receipt = [ordered]@{
        schemaVersion = 1
        status = 'passed'
        scenario = 'current-updater-smoke'
        currentCommit = $current.Commit
        currentVersion = $current.Version
        currentPackageSha256 = $current.PackageSha256
        artifactManifestPath = $artifactManifest.ManifestPath
        artifactId = $artifactManifest.ArtifactId
        artifactRunId = $artifactManifest.RunId
        fixtureManifestSha256 = $script:manifestHash
        success = [ordered]@{
            updaterExitCode = $successExitCode
            updaterWorkingDirectory = $successUpdater.WorkingDirectory
            updaterPreparedPath = $successUpdater.PreparedUpdater
            downloadedPackageFileName = [IO.Path]::GetFileName($successUpdater.DownloadedPackage)
            updaterStandardOutput = $successUpdater.StandardOutput
            updaterStandardError = $successUpdater.StandardError
            automaticRestartObserved = $true
            startupReadyOperable = $true
            mainWindowObserved = $true
            gracefulShutdown = $true
            semanticDataPreserved = $true
        }
    }
    [IO.File]::WriteAllText($script:receiptPath, ($receipt | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    Write-Host "NET10 update acceptance passed: $script:receiptPath"
}
catch {
    $script:failed = $true
    $failureErrorRecord = $_
    $script:primaryError = $failureErrorRecord
    $failure = [ordered]@{
        schemaVersion = 1
        status = 'failed'
        artifactManifestPath = $artifactManifest.ManifestPath
        artifactId = $artifactManifest.ArtifactId
        artifactRunId = $artifactManifest.RunId
        fixtureManifestSha256 = $script:manifestHash
        sandboxRoot = $script:sandboxRoot
        error = $failureErrorRecord.Exception.ToString()
    }
    try {
        [IO.File]::WriteAllText($script:receiptPath, ($failure | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    }
    catch {
        Add-VerificationExceptionSecondaryDiagnostic `
            -Exception $failureErrorRecord.Exception `
            -Diagnostic "failure receipt write failed: $($_.Exception.ToString())"
    }
    throw $failureErrorRecord
}
finally {
    $sandboxCleanupDeadline = if ($script:failed) {
        $script:deadlinePolicy.CleanupDeadlineUtc
    }
    else {
        $script:deadlinePolicy.ExecutionDeadlineUtc
    }
    $ownedCleanupException = $null
    try {
        Stop-VerificationOwnedProcessRecords `
            -StartedProcesses $script:ownedProcessRecords `
            -CleanupDeadlineUtc $script:deadlinePolicy.CleanupDeadlineUtc
    }
    catch {
        $ownedCleanupException = $_.Exception
    }
    if ($null -ne $ownedCleanupException) {
        if ($null -ne $script:primaryError) {
            Add-VerificationExceptionSecondaryDiagnostic `
                -Exception $script:primaryError.Exception `
                -Diagnostic $ownedCleanupException.ToString()
        }
        else {
            throw $ownedCleanupException
        }
    }
    if ($KeepSandbox -or $script:failed) {
        Write-Host "NET10 update acceptance sandbox retained: $script:sandboxRoot"
    }
    elseif (Test-Path -LiteralPath $script:sandboxRoot) {
        if ([DateTime]::UtcNow -ge $sandboxCleanupDeadline) {
            throw 'Successful update acceptance sandbox cleanup reached its execution deadline.'
        }
        Remove-Item -LiteralPath $script:sandboxRoot -Recurse -Force -ErrorAction Stop
        if ([DateTime]::UtcNow -ge $sandboxCleanupDeadline) {
            throw 'Successful update acceptance sandbox cleanup crossed its execution deadline.'
        }
    }
}
