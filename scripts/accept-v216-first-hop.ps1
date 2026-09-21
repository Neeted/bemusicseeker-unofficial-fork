[CmdletBinding()]
param(
    [string]$ArtifactMetadataPath,

    [Parameter(Mandatory)]
    [string]$CurrentPackagePath,

    [string]$CurrentVersion,
    [string]$FixtureRoot,
    [string]$OutputDirectory,
    [string]$SqliteAssemblyRoot,

    [ValidateRange(30, 300)]
    [int]$TimeoutSeconds = 180,

    [DateTime]$ExecutionDeadlineUtc = [DateTime]::MinValue,
    [DateTime]$CleanupDeadlineUtc = [DateTime]::MinValue,

    [switch]$KeepSandbox
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ArtifactMetadataPath)) {
    $ArtifactMetadataPath = Join-Path $repoRoot 'devdocs\acceptance\v216-first-hop\artifact.json'
}
if ([string]::IsNullOrWhiteSpace($FixtureRoot)) {
    $FixtureRoot = Join-Path $repoRoot 'devdocs\acceptance\net10-existing-data'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts\verification\v216-first-hop'
}
if ([string]::IsNullOrWhiteSpace($SqliteAssemblyRoot)) {
    $SqliteAssemblyRoot = Join-Path $repoRoot 'BeMusicSeeker.Tests\bin\x64\Release\net10.0-windows'
}

. (Join-Path $repoRoot 'scripts\distribution-artifact.ps1')
. (Join-Path $repoRoot 'scripts\verification-process-lifecycle.ps1')
. (Join-Path $repoRoot 'scripts\portable-package-layout.ps1')
. (Join-Path $repoRoot 'scripts\verification-runner-contract.ps1')
. (Join-Path $repoRoot 'scripts\verification-ui-automation.ps1')
. (Join-Path $repoRoot 'scripts\acceptance-settings-fixture.ps1')
$verificationRunnerContract = Get-VerificationRunnerContract
$v216AcceptanceReceiptContract = $verificationRunnerContract.V216FirstHop.AcceptanceReceipt

$script:deadlinePolicy = Resolve-VerificationDeadlinePair `
    -TimeoutSeconds $TimeoutSeconds `
    -ExecutionDeadlineUtc $ExecutionDeadlineUtc `
    -CleanupDeadlineUtc $CleanupDeadlineUtc

if (-not ('BeMusicSeekerV216AcceptanceWindowMessage' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class BeMusicSeekerV216AcceptanceWindowMessage
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
}
'@
}

function Resolve-FullPath {
    param([Parameter(Mandatory)][string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'Acceptance path cannot be empty.'
    }
    return [IO.Path]::GetFullPath($Path)
}

function Assert-File {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required first-hop acceptance file is missing: $Path"
    }
}

function Assert-Directory {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "Required first-hop acceptance directory is missing: $Path"
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)
    Assert-File -Path $Path
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-TreeSha256 {
    param([Parameter(Mandatory)][string]$Root)

    $rootPath = Resolve-FullPath $Root
    Assert-Directory -Path $rootPath
    $normalizedRoot = $rootPath.TrimEnd('\', '/')
    $relativePaths = [System.Collections.Generic.List[string]]::new()
    foreach ($file in @(Get-ChildItem -LiteralPath $normalizedRoot -Recurse -File)) {
        $relativePath = $file.FullName.Substring($normalizedRoot.Length).TrimStart('\', '/').Replace('\', '/')
        [void]$relativePaths.Add($relativePath)
    }
    $relativePaths.Sort([StringComparer]::Ordinal)
    $entries = [System.Collections.Generic.List[string]]::new()
    foreach ($relativePath in $relativePaths) {
        $filePath = Join-Path $normalizedRoot ($relativePath.Replace('/', [IO.Path]::DirectorySeparatorChar))
        [void]$entries.Add("$relativePath`t$(Get-Sha256 -Path $filePath)")
    }
    $payload = [Text.Encoding]::UTF8.GetBytes($entries -join "`n")
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($payload)).ToLowerInvariant()
}

function Get-PreservedTrees {
    param([Parameter(Mandatory)][string]$AppRoot)
    return [ordered]@{
        data = Get-TreeSha256 -Root (Join-Path $AppRoot 'data')
        config = Get-TreeSha256 -Root (Join-Path $AppRoot 'config')
    }
}

function Assert-PreservedTreesEqual {
    param(
        [Parameter(Mandatory)][object]$Before,
        [Parameter(Mandatory)][object]$After,
        [Parameter(Mandatory)][string]$Label
    )
    foreach ($name in @('data', 'config')) {
        if ([string]$Before[$name] -cne [string]$After[$name]) {
            throw "Legacy updater changed preserved $name tree before v3 startup: $Label"
        }
    }
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

$script:sqliteRuntimeLoaded = $false
function Initialize-SqliteRuntime {
    if ($script:sqliteRuntimeLoaded) {
        return
    }
    foreach ($name in @('SQLitePCLRaw.core.dll', 'SQLitePCLRaw.batteries_v2.dll', 'SQLite-net.dll', 'BeMusicSeeker.dll')) {
        Assert-File -Path (Join-Path $SqliteAssemblyRoot $name)
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
    $fixturePath = Join-Path $FixtureRoot ([string]$script:fixtureManifest.database.fixtureFile)
    Assert-File -Path $fixturePath
    if ((Get-Sha256 -Path $fixturePath) -cne ([string]$script:fixtureManifest.database.fixtureSha256).ToLowerInvariant()) {
        throw "Tracked legacy database fixture hash does not match the manifest: $fixturePath"
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    Copy-Item -LiteralPath $fixturePath -Destination $Path -Force
    $database = [SQLite.SQLiteConnection]::new(
        $Path,
        [SQLite.SQLiteOpenFlags]::ReadWrite -bor [SQLite.SQLiteOpenFlags]::FullMutex,
        $true)
    try {
        $row = $script:fixtureManifest.database
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
    Assert-File -Path $Path
    $database = [SQLite.SQLiteConnection]::new(
        $Path,
        [SQLite.SQLiteOpenFlags]::ReadOnly -bor [SQLite.SQLiteOpenFlags]::FullMutex,
        $true)
    try {
        $row = $script:fixtureManifest.database
        $values = [ordered]@{
            song = [int]$database.ExecuteScalar[int]('SELECT COUNT(*) FROM song WHERE hash = ? AND title = ?', @([string]$row.songHash, [string]$row.songTitle))
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
                throw "First-hop semantic row was not preserved: $name=$($values[$name]) path=$Path"
            }
        }
        return $values
    }
    finally {
        $database.Close()
        $database.Dispose()
    }
}

function Prepare-LegacyProfile {
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
    $legacyExecutable = Join-Path $AppRoot 'BeMusicSeeker.exe'
    Assert-File -Path $legacyExecutable
    $legacyAssemblyVersion = [string](Get-Item -LiteralPath $legacyExecutable).VersionInfo.FileVersion
    if ($legacyAssemblyVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw "Legacy v2 executable does not expose a four-component file version: $legacyExecutable"
    }
    $settings = @{
        AssemblyVersion = $legacyAssemblyVersion
        Lang = [string]$script:fixtureManifest.settings.language
        AppearanceTheme = [string]$script:fixtureManifest.settings.appearanceTheme
        OperationModeLR2DB = 'False'
        BMSInstallDir = $bmsRoot
        BMSRootPath = $bmsRoot
        TableListURL = 'https://example.invalid/v216-first-hop-table-list'
        EnablePlaylistUrlCompletion = 'False'
        EnableStellaFullPlaylistUrlCompletion = 'False'
        SkipInitPlaylistLoad = 'True'
    }
    $installPath = $packageRoot
    Copy-LegacyDatabaseFixture -Path $databasePath -InstallPath $installPath -BmsRoot $bmsRoot

    $legacyConfigDirectory = 'BeMusicSeeker.exe_Url_V216FirstHop\1.0.0.0'
    $legacyConfig = Join-Path (Join-Path $ProfileRoot 'user\AppData\Local\BeMusicSeeker') (Join-Path $legacyConfigDirectory 'user.config')
    Write-LegacyConfig -Path $legacyConfig -Settings $settings

    foreach ($directory in @('config', 'data')) {
        New-Item -ItemType Directory -Path (Join-Path $AppRoot $directory) -Force | Out-Null
    }
    [IO.File]::WriteAllText(
        (Join-Path $AppRoot 'config\v216-config.marker'),
        "v216 config marker`n",
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $AppRoot 'data\v216-data.marker'),
        "v216 data marker`n",
        [Text.UTF8Encoding]::new($false))

    $unmanagedMarker = Join-Path $AppRoot 'user-data\v216-unmanaged.marker'
    New-Item -ItemType Directory -Path (Split-Path -Parent $unmanagedMarker) -Force | Out-Null
    [IO.File]::WriteAllText($unmanagedMarker, "v216 unmanaged marker`n", [Text.UTF8Encoding]::new($false))
    return [pscustomobject]@{
        ProfileRoot = $ProfileRoot
        AppRoot = $AppRoot
        BmsRoot = $bmsRoot
        DatabasePath = $databasePath
        InstallPath = $installPath
        LegacyConfigPath = $legacyConfig
        UnmanagedMarkerPath = $unmanagedMarker
        Settings = $settings
    }
}

function Get-ProfileState {
    param([Parameter(Mandatory)]$Profile)
    $portable = Join-Path $Profile.AppRoot 'config\user.config'
    return [ordered]@{
        Database = Get-DatabaseSemanticSnapshot -Path $Profile.DatabasePath -InstallPath $Profile.InstallPath
        PortableSettings = if (Test-Path -LiteralPath $portable -PathType Leaf) { Get-ManifestSettings -Path $portable } else { @{} }
        PortableSettingsSha256 = if (Test-Path -LiteralPath $portable -PathType Leaf) { Get-Sha256 -Path $portable } else { $null }
        LegacyConfigSha256 = Get-Sha256 -Path $Profile.LegacyConfigPath
        UnmanagedMarkerSha256 = Get-Sha256 -Path $Profile.UnmanagedMarkerPath
    }
}

function Assert-ProfileStatePreserved {
    param(
        [Parameter(Mandatory)][object]$Before,
        [Parameter(Mandatory)][object]$After,
        [Parameter(Mandatory)]$Profile,
        [Parameter(Mandatory)][string]$Label
    )
    if ((ConvertTo-Json $Before.Database -Compress) -ne (ConvertTo-Json $After.Database -Compress)) {
        throw "Database semantic state changed during first-hop update: $Label"
    }
    if ([string]$Before.LegacyConfigSha256 -cne [string]$After.LegacyConfigSha256 -or
        [string]$Before.UnmanagedMarkerSha256 -cne [string]$After.UnmanagedMarkerSha256) {
        throw "Legacy persisted or unmanaged state changed during first-hop update: $Label"
    }
    foreach ($name in @('OperationModeLR2DB', 'Lang', 'AppearanceTheme', 'BMSRootPath', 'BMSInstallDir')) {
        if ([string]::IsNullOrWhiteSpace([string]$After.PortableSettings[$name]) -or
            [string]$After.PortableSettings[$name] -cne [string]$Profile.Settings[$name]) {
            throw "Migrated setting was not preserved during first-hop update: $Label $name"
        }
    }
}

function Wait-ForWindow {
    param(
        [Parameter(Mandatory)]$Started,
        [Parameter(Mandatory)][DateTime]$DeadlineUtc
    )
    $deadline = $DeadlineUtc.ToUniversalTime()
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($Started.Process.HasExited) {
            throw "Legacy v2 application exited before close (exit code $($Started.Process.ExitCode))."
        }
        $Started.Process.Refresh()
        if ($Started.Process.MainWindowHandle -ne 0) {
            return [IntPtr]$Started.Process.MainWindowHandle
        }
        $remainingMilliseconds = Get-VerificationRemainingMilliseconds `
            -DeadlineUtc $deadline `
            -OperationName 'legacy application window wait'
        Start-Sleep -Milliseconds ([Math]::Min(250, $remainingMilliseconds))
    }
    throw 'Legacy v2 application did not expose a window before the acceptance execution deadline.'
}

function Wait-ForLegacyApplicationReady {
    param(
        [Parameter(Mandatory)]$Started,
        [Parameter(Mandatory)][string]$LogDirectory,
        [Parameter(Mandatory)][DateTime]$DeadlineUtc
    )

    $initializationComplete = $false
    $presentationFlushed = $false
    while ([DateTime]::UtcNow -lt $DeadlineUtc) {
        if ($Started.Process.HasExited) {
            throw "Legacy v2 application exited before startup completion (exit code $($Started.Process.ExitCode))."
        }
        foreach ($log in @(Get-ChildItem -LiteralPath $LogDirectory -Filter '*.log' -File -ErrorAction SilentlyContinue)) {
            $content = Get-Content -LiteralPath $log.FullName -Raw -ErrorAction SilentlyContinue
            if ($content -match '(?m)startup_initialization_complete\b') {
                $initializationComplete = $true
            }
            if ($content -match '(?m)startup_presentation_flush done\b') {
                $presentationFlushed = $true
            }
        }
        if ($initializationComplete -and $presentationFlushed) {
            return Wait-ForWindow -Started $Started -DeadlineUtc $DeadlineUtc
        }
        $remainingMilliseconds = Get-VerificationRemainingMilliseconds `
            -DeadlineUtc $DeadlineUtc `
            -OperationName 'legacy application startup completion wait'
        Start-Sleep -Milliseconds ([Math]::Min(250, $remainingMilliseconds))
        $Started.Process.Refresh()
    }
    throw 'Legacy v2 application did not reach startup_initialization_complete and startup_presentation_flush done before the acceptance execution deadline.'
}

function Close-RedirectedApplication {
    param(
        [Parameter(Mandatory)]$Started,
        [Parameter(Mandatory)][string]$DiagnosticsDirectory,
        [Parameter(Mandatory)][object]$DeadlinePolicy
    )
    $executionDeadline = $DeadlinePolicy.ExecutionDeadlineUtc
    try {
        [void](Wait-ForWindow -Started $Started -DeadlineUtc $executionDeadline)
        $requested = $Started.Process.CloseMainWindow()
        if (-not $requested) {
            $handle = $Started.Process.MainWindowHandle
            if ($handle -eq 0 -or -not [BeMusicSeekerV216AcceptanceWindowMessage]::PostMessage($handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)) {
                throw 'Legacy v2 application did not accept a graceful shutdown request.'
            }
        }
        $result = Complete-VerificationRedirectedProcess `
            -Started $Started `
            -DiagnosticsDirectory $DiagnosticsDirectory `
            -DeadlinePolicy $DeadlinePolicy `
            -LifecycleName 'v216-first-hop-legacy-application' `
            -TerminateProcessTree
        if ($null -ne $result.PrimaryFailureKind) {
            throw (Get-VerificationLifecycleFailureMessage `
                    -Label 'Legacy application graceful shutdown' `
                    -Result $result `
                    -TimeoutSeconds $DeadlinePolicy.TimeoutSeconds)
        }
        if (@($result.SecondaryDiagnostics).Count -gt 0) {
            throw "Legacy application lifecycle diagnostics reported failure: $(@($result.SecondaryDiagnostics) -join '; ')"
        }
        return $result
    }
    catch {
        $primaryException = $_.Exception
        try {
            $cleanup = Stop-VerificationOwnedProcessRecord `
                -Started $Started `
                -CleanupDeadlineUtc $DeadlinePolicy.CleanupDeadlineUtc `
                -OperationName 'v216-first-hop legacy application cleanup'
            foreach ($diagnostic in @($cleanup.Diagnostics)) {
                Add-VerificationExceptionSecondaryDiagnostic `
                    -Exception $primaryException `
                    -Diagnostic ([string]$diagnostic)
            }
        }
        catch {
            Add-VerificationExceptionSecondaryDiagnostic `
                -Exception $primaryException `
                -Diagnostic ($_.Exception.ToString())
        }
        throw $primaryException
    }
}

function Start-IsolatedV3Application {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string]$ProfileRoot,
        [Parameter(Mandatory)][string]$LogDirectory,
        [Parameter(Mandatory)][object]$DeadlinePolicy,
        [System.Collections.IList]$OwnedProcessRecords
    )
    $executionDeadline = $DeadlinePolicy.ExecutionDeadlineUtc
    [void](New-Item -ItemType Directory -Path $LogDirectory -Force)
    $localAppData = Join-Path $ProfileRoot 'user\AppData\Local'
    $temp = Join-Path $ProfileRoot 'temp'
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    $started = Start-VerificationRedirectedProcess `
        -FileName $Executable `
        -Arguments @() `
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
                -DeadlineUtc $executionDeadline `
                -OperationName 'v3 application input-idle wait'
            [void]$started.Process.WaitForInputIdle([Math]::Min(30000, $remainingMilliseconds))
        }
        catch { }
        $deadline = $executionDeadline
        $readyLog = $null
        while ([DateTime]::UtcNow -lt $deadline) {
            if ($started.Process.HasExited) {
                throw "v3 application exited before startup_ready_operable (exit code $($started.Process.ExitCode))."
            }
            foreach ($log in @(Get-ChildItem -LiteralPath $LogDirectory -Filter '*.log' -File -ErrorAction SilentlyContinue)) {
                $content = Get-Content -LiteralPath $log.FullName -Raw -ErrorAction SilentlyContinue
                if ($content -match 'startup_setting_validation_failed|app_schema_preflight_prompt_show|Startup library initialization failure notification') {
                    throw "v3 application reported a startup blocker before startup_ready_operable: $($log.FullName)"
                }
                if ($content -match 'startup_ready_operable') {
                    $readyLog = $log.FullName
                    break
                }
            }
            if ($null -ne $readyLog) {
                break
            }
            $remainingMilliseconds = Get-VerificationRemainingMilliseconds `
                -DeadlineUtc $executionDeadline `
                -OperationName 'v3 startup-ready wait'
            Start-Sleep -Milliseconds ([Math]::Min(250, $remainingMilliseconds))
            $started.Process.Refresh()
        }
        if ($null -eq $readyLog) {
            throw 'v3 application did not reach startup_ready_operable before the acceptance execution deadline. Logs: $LogDirectory'
        }
        [void](Wait-ForWindow -Started $started -DeadlineUtc $executionDeadline)
        $started | Add-Member -NotePropertyName ReadyLogPath -NotePropertyValue $readyLog
        return $started
    }
    catch {
        $primaryException = $_.Exception
        try {
            $cleanup = Stop-VerificationOwnedProcessRecord `
                -Started $started `
                -CleanupDeadlineUtc $DeadlinePolicy.CleanupDeadlineUtc `
                -OperationName 'v216-first-hop v3 startup cleanup'
            foreach ($diagnostic in @($cleanup.Diagnostics)) {
                Add-VerificationExceptionSecondaryDiagnostic `
                    -Exception $primaryException `
                    -Diagnostic ([string]$diagnostic)
            }
        }
        catch {
            Add-VerificationExceptionSecondaryDiagnostic `
                -Exception $primaryException `
                -Diagnostic ($_.Exception.ToString())
        }
        throw $primaryException
    }
}

function Close-IsolatedV3Application {
    param(
        [Parameter(Mandatory)]$Started,
        [Parameter(Mandatory)][string]$DiagnosticsDirectory,
        [Parameter(Mandatory)][object]$DeadlinePolicy
    )
    try {
        $Started.Process.Refresh()
        Assert-VerificationNoUnexpectedOwnedModal `
            -ProcessId $Started.Process.Id `
            -MainWindowHandle ([IntPtr]$Started.Process.MainWindowHandle)
        $requested = $Started.Process.CloseMainWindow()
        if (-not $requested) {
            $handle = $Started.Process.MainWindowHandle
            if ($handle -eq 0 -or -not [BeMusicSeekerV216AcceptanceWindowMessage]::PostMessage($handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)) {
                throw 'v3 application did not accept a graceful shutdown request.'
            }
        }
        $result = Complete-VerificationRedirectedProcess `
            -Started $Started `
            -DiagnosticsDirectory $DiagnosticsDirectory `
            -DeadlinePolicy $DeadlinePolicy `
            -LifecycleName 'v216-first-hop-v3-application' `
            -TerminateProcessTree
        if ($null -ne $result.PrimaryFailureKind) {
            throw (Get-VerificationLifecycleFailureMessage `
                    -Label 'v3 application graceful shutdown' `
                    -Result $result `
                    -TimeoutSeconds $DeadlinePolicy.TimeoutSeconds)
        }
        if (@($result.SecondaryDiagnostics).Count -gt 0) {
            throw "v3 application lifecycle diagnostics reported failure: $(@($result.SecondaryDiagnostics) -join '; ')"
        }
        if ($null -ne $result.ExitCode -and [int]$result.ExitCode -ne 0) {
            throw "v3 application shutdown returned exit code $($result.ExitCode)."
        }
        return $result
    }
    catch {
        $primaryException = $_.Exception
        try {
            $cleanup = Stop-VerificationOwnedProcessRecord `
                -Started $Started `
                -CleanupDeadlineUtc $DeadlinePolicy.CleanupDeadlineUtc `
                -OperationName 'v216-first-hop v3 application cleanup'
            foreach ($diagnostic in @($cleanup.Diagnostics)) {
                Add-VerificationExceptionSecondaryDiagnostic `
                    -Exception $primaryException `
                    -Diagnostic ([string]$diagnostic)
            }
        }
        catch {
            Add-VerificationExceptionSecondaryDiagnostic `
                -Exception $primaryException `
                -Diagnostic ($_.Exception.ToString())
        }
        throw $primaryException
    }
}

function Expand-AppPackage {
    param([Parameter(Mandatory)][string]$PackagePath, [Parameter(Mandatory)][string]$Destination)
    Assert-File -Path $PackagePath
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Expand-Archive -LiteralPath $PackagePath -DestinationPath $Destination -Force
    Assert-File -Path (Join-Path $Destination 'BeMusicSeeker.exe')
    Assert-File -Path (Join-Path $Destination 'BeMusicSeeker.Updater.exe')
}

function Copy-CurrentPackageIntoSandbox {
    param([Parameter(Mandatory)][string]$PackagePath, [Parameter(Mandatory)][string]$AppRoot)
    # Keep the package outside the legacy app's runtime tree.  The released v2
    # application removes its transient update_work directory during shutdown,
    # while protocol-1 accepts an arbitrary package path.
    $destination = Join-Path (Split-Path -Parent $AppRoot) ([IO.Path]::GetFileName($PackagePath))
    Copy-Item -LiteralPath $PackagePath -Destination $destination -Force
    return $destination
}

function New-LegacyUpdaterArguments {
    param(
        [Parameter(Mandatory)][string]$AppRoot,
        [Parameter(Mandatory)][string]$PackagePath,
        [Parameter(Mandatory)][int]$ProcessId
    )
    return @(
        '--app-dir', $AppRoot,
        '--package', $PackagePath,
        '--backup-dir', (Join-Path $AppRoot 'update_backup'),
        '--pid', ([string]$ProcessId))
}

function Assert-CurrentPackage {
    param([Parameter(Mandatory)][string]$Path)
    Assert-File -Path $Path
    $name = [IO.Path]::GetFileName($Path)
    if ($name -notmatch '^bemusicseeker-unofficial-fork-v([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)\.zip$') {
        throw "Current release package has an invalid exact versioned name: $Path"
    }
    $version = $Matches[1]
    if ($version -eq '2.1.6.0') {
        throw 'The current release package must not be the pinned v2.1.6.0 first-hop artifact.'
    }
    if (-not [string]::IsNullOrWhiteSpace($CurrentVersion) -and $version -cne $CurrentVersion) {
        throw "Current release package version mismatch: expected=$CurrentVersion actual=$version"
    }
    return [pscustomobject]@{
        Path = Resolve-FullPath $Path
        Version = $version
        Sha256 = Get-Sha256 -Path $Path
    }
}

$FixtureRoot = Resolve-FullPath $FixtureRoot
$OutputDirectory = Resolve-FullPath $OutputDirectory
$artifactMetadata = Assert-V216ArtifactIdentity -MetadataPath $ArtifactMetadataPath -RepositoryRoot $repoRoot
$currentPackage = Assert-CurrentPackage -Path (Resolve-FullPath $CurrentPackagePath)

Assert-Directory -Path $FixtureRoot
foreach ($fixturePath in @(
        'fixture-manifest.json',
        'fixture.bms',
        'legacy-song.db',
        'package\e1-fixture-package.marker')) {
    Assert-File -Path (Join-Path $FixtureRoot $fixturePath)
}
$script:fixtureManifest = Get-Content -LiteralPath (Join-Path $FixtureRoot 'fixture-manifest.json') -Raw | ConvertFrom-Json
$script:fixtureManifestHash = Get-Sha256 -Path (Join-Path $FixtureRoot 'fixture-manifest.json')
$script:sandboxRoot = Join-Path ([IO.Path]::GetTempPath()) ('BeMusicSeeker-v216-first-hop-' + [Guid]::NewGuid().ToString('N'))
$script:receiptPath = Join-Path $OutputDirectory 'v216-first-hop-acceptance.json'
$script:failed = $false
$script:primaryError = $null
$script:successReceipt = $null
$script:ownedProcessRecords = [Collections.Generic.List[object]]::new()

try {
    $successRoot = Join-Path $script:sandboxRoot 'success'
    $successApp = Join-Path $successRoot 'app'
    New-Item -ItemType Directory -Path $successRoot -Force | Out-Null
    Expand-AppPackage -PackagePath $artifactMetadata.ArtifactPath -Destination $successApp
    $legacyProfile = Prepare-LegacyProfile -ProfileRoot $successRoot -AppRoot $successApp
    $legacyLog = Join-Path $successApp 'log'

    # リリース済みv2を通常起動まで完了させ、プロトコル1の--pid updaterの通常終了待機中も維持する。
    New-Item -ItemType Directory -Path (Join-Path $successRoot 'temp') -Force | Out-Null
    $legacyApp = Start-VerificationRedirectedProcess `
        -FileName (Join-Path $successApp 'BeMusicSeeker.exe') `
        -Arguments @() `
        -WorkingDirectory $successApp `
        -Environment @{
            LOCALAPPDATA = Join-Path $successRoot 'user\AppData\Local'
            USERPROFILE = Join-Path $successRoot 'user'
            TEMP = Join-Path $successRoot 'temp'
            TMP = Join-Path $successRoot 'temp'
        } `
        -DeadlinePolicy $script:deadlinePolicy `
        -DiagnosticsDirectory (Join-Path $successRoot 'legacy-app') `
        -OwnedProcessRecords $script:ownedProcessRecords
    $legacyMainWindowHandle = Wait-ForLegacyApplicationReady `
        -Started $legacyApp `
        -LogDirectory $legacyLog `
        -DeadlineUtc $script:deadlinePolicy.ExecutionDeadlineUtc
    Assert-VerificationNoUnexpectedOwnedModal `
        -ProcessId $legacyApp.Process.Id `
        -MainWindowHandle $legacyMainWindowHandle
    if ($legacyApp.Process.HasExited) {
        throw 'Legacy v2 application exited after startup completion before the updater was started.'
    }
    $downloadedSuccessPackage = Copy-CurrentPackageIntoSandbox -PackagePath $currentPackage.Path -AppRoot $successApp
    $legacyArguments = New-LegacyUpdaterArguments -AppRoot $successApp -PackagePath $downloadedSuccessPackage -ProcessId $legacyApp.ProcessId
    $legacyUpdater = Start-VerificationRedirectedProcess `
        -FileName (Join-Path $successApp 'BeMusicSeeker.Updater.exe') `
        -Arguments $legacyArguments `
        -WorkingDirectory $successApp `
        -DeadlinePolicy $script:deadlinePolicy `
        -DiagnosticsDirectory (Join-Path $successRoot 'legacy-updater') `
        -OwnedProcessRecords $script:ownedProcessRecords
    $legacyCloseResult = Close-RedirectedApplication `
        -Started $legacyApp `
        -DiagnosticsDirectory (Join-Path $successRoot 'legacy-app') `
        -DeadlinePolicy $script:deadlinePolicy
    $beforeUpdaterTrees = Get-PreservedTrees -AppRoot $successApp
    $beforeV3State = Get-ProfileState -Profile $legacyProfile
    $legacyUpdaterResult = Complete-VerificationRedirectedProcess `
        -Started $legacyUpdater `
        -DiagnosticsDirectory (Join-Path $successRoot 'legacy-updater') `
        -DeadlinePolicy $script:deadlinePolicy `
        -LifecycleName 'v216-first-hop-legacy-updater'
    Assert-VerificationProcessResult `
        -Result $legacyUpdaterResult `
        -Label 'Released v2.1.6.0 updater success path' `
        -TimeoutSeconds $script:deadlinePolicy.TimeoutSeconds `
        -RequireSuccess
    if ($legacyUpdaterResult.ExitCode -ne 0) {
        throw "Released v2.1.6.0 updater failed on the protocol-1 success path: exit=$($legacyUpdaterResult.ExitCode) stderr=$($legacyUpdaterResult.StandardError)"
    }
    $afterUpdaterTrees = Get-PreservedTrees -AppRoot $successApp
    Assert-PreservedTreesEqual -Before $beforeUpdaterTrees -After $afterUpdaterTrees -Label 'protocol-1 success'

    $v3Log = Join-Path $successApp 'log'
    # v2 and v3 share app\log; isolate legacy readiness markers before starting v3.
    $legacyLogArchive = Join-Path $successRoot 'legacy-v2-log'
    if (Test-Path -LiteralPath $v3Log) {
        Move-Item -LiteralPath $v3Log -Destination $legacyLogArchive
    }
    New-Item -ItemType Directory -Path $v3Log -Force | Out-Null
    $v3App = Start-IsolatedV3Application `
        -Executable (Join-Path $successApp 'BeMusicSeeker.exe') `
        -ProfileRoot $successRoot `
        -LogDirectory $v3Log `
        -DeadlinePolicy $script:deadlinePolicy `
        -OwnedProcessRecords $script:ownedProcessRecords
    $v3CloseResult = Close-IsolatedV3Application `
        -Started $v3App `
        -DiagnosticsDirectory (Join-Path $successRoot 'v3-app') `
        -DeadlinePolicy $script:deadlinePolicy
    $afterV3State = Get-ProfileState -Profile $legacyProfile
    Assert-ProfileStatePreserved -Before $beforeV3State -After $afterV3State -Profile $legacyProfile -Label 'v2.1.6.0 to v3 first hop'
    Assert-PortableSingleFilePayloadLayout $successApp

    $lockRoot = Join-Path $script:sandboxRoot 'lock'
    $lockApp = Join-Path $lockRoot 'app'
    New-Item -ItemType Directory -Path $lockRoot -Force | Out-Null
    Expand-AppPackage -PackagePath $artifactMetadata.ArtifactPath -Destination $lockApp
    $lockProfile = Prepare-LegacyProfile -ProfileRoot $lockRoot -AppRoot $lockApp
    $downloadedLockPackage = Copy-CurrentPackageIntoSandbox -PackagePath $currentPackage.Path -AppRoot $lockApp
    $beforeLockTrees = Get-PreservedTrees -AppRoot $lockApp
    $lockedManagedPath = Join-Path $lockApp 'BeMusicSeeker.exe'
    $lockStream = [IO.File]::Open($lockedManagedPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $lockArguments = New-LegacyUpdaterArguments -AppRoot $lockApp -PackagePath $downloadedLockPackage -ProcessId 0
        $lockUpdater = Start-VerificationRedirectedProcess `
            -FileName (Join-Path $lockApp 'BeMusicSeeker.Updater.exe') `
            -Arguments $lockArguments `
            -WorkingDirectory $lockApp `
            -DeadlinePolicy $script:deadlinePolicy `
            -DiagnosticsDirectory (Join-Path $lockRoot 'legacy-updater') `
            -OwnedProcessRecords $script:ownedProcessRecords
        $lockUpdaterResult = Complete-VerificationRedirectedProcess `
            -Started $lockUpdater `
            -DiagnosticsDirectory (Join-Path $lockRoot 'legacy-updater') `
            -DeadlinePolicy $script:deadlinePolicy `
            -LifecycleName 'v216-first-hop-lock-updater'
    }
    finally {
        $lockStream.Dispose()
    }
    Assert-VerificationProcessResult `
        -Result $lockUpdaterResult `
        -Label 'Released v2.1.6.0 updater lock characterization' `
        -TimeoutSeconds $script:deadlinePolicy.TimeoutSeconds `
        -ExpectedPrimaryFailureKind 'nonzero-exit'
    if ($lockUpdaterResult.ExitCode -eq 0) {
        throw 'Released v2.1.6.0 updater unexpectedly succeeded while a managed file was locked.'
    }
    if ([string]::IsNullOrWhiteSpace($lockUpdaterResult.StandardError)) {
        throw 'Released v2.1.6.0 updater lock characterization did not produce a stderr diagnostic.'
    }
    $afterLockTrees = Get-PreservedTrees -AppRoot $lockApp
    Assert-PreservedTreesEqual -Before $beforeLockTrees -After $afterLockTrees -Label 'managed-file lock characterization'

    # The result receipt is intentionally assembled only after both real first-hop
    # assertions above have succeeded.  The release gate consumes these exact names
    # as results; the detailed happy/locked evidence remains alongside them below.
    $receiptResults = @(
        foreach ($requiredResult in @($v216AcceptanceReceiptContract.RequiredResults)) {
            [ordered]@{
                fullyQualifiedName = [string]$requiredResult.FullyQualifiedName
                outcome = [string]$v216AcceptanceReceiptContract.RequiredOutcome
            }
        })
    $script:successReceipt = [ordered]@{
        schemaVersion = 1
        manifestType = 'BeMusicSeeker.V216FirstHopAcceptance'
        status = 'passed'
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        results = $receiptResults
        artifact = [ordered]@{
            metadataPath = $artifactMetadata.MetadataPath
            artifactId = $artifactMetadata.ArtifactId
            version = $artifactMetadata.Version
            fileName = $artifactMetadata.FileName
            sizeBytes = $artifactMetadata.ExpectedSizeBytes
            sha256 = $artifactMetadata.ExpectedSha256.ToUpperInvariant()
            path = $artifactMetadata.ArtifactPath
        }
        currentPackage = [ordered]@{
            path = $currentPackage.Path
            version = $currentPackage.Version
            sha256 = $currentPackage.Sha256
        }
        fixtureManifestSha256 = $script:fixtureManifestHash
        happy = [ordered]@{
            contractId = 'UPD-V216-HAPPY'
            legacyUpdaterProtocol = 'protocol-1'
            updaterExitCode = [int]$legacyUpdaterResult.ExitCode
            updaterStdoutDrained = $true
            updaterStderrDrained = $true
            dataTreeSha256Before = $beforeUpdaterTrees.data
            dataTreeSha256After = $afterUpdaterTrees.data
            configTreeSha256Before = $beforeUpdaterTrees.config
            configTreeSha256After = $afterUpdaterTrees.config
            preservedTreesByteIdenticalBeforeV3 = $true
            startupReadyLog = $v3App.ReadyLogPath
            startupExitCode = [int]$v3CloseResult.ExitCode
            semanticStatePreserved = $true
        }
        locked = [ordered]@{
            contractId = 'UPD-V216-LOCK'
            updaterExitCode = [int]$lockUpdaterResult.ExitCode
            stderrNonEmpty = $true
            stderrDiagnosticSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($lockUpdaterResult.StandardError))).ToLowerInvariant()
            dataTreeSha256Before = $beforeLockTrees.data
            dataTreeSha256After = $afterLockTrees.data
            configTreeSha256Before = $beforeLockTrees.config
            configTreeSha256After = $afterLockTrees.config
            preservedTreesByteIdentical = $true
        }
    }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    [IO.File]::WriteAllText($script:receiptPath, ($script:successReceipt | ConvertTo-Json -Depth 16), [Text.UTF8Encoding]::new($false))
    Write-Host "v2.1.6.0 first-hop acceptance passed: $script:receiptPath"
}
catch {
    $script:failed = $true
    $failureErrorRecord = $_
    $script:primaryError = $failureErrorRecord
    $failure = [ordered]@{
        schemaVersion = 1
        manifestType = 'BeMusicSeeker.V216FirstHopAcceptance'
        status = 'failed'
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        artifactMetadataPath = $artifactMetadata.MetadataPath
        currentPackagePath = $currentPackage.Path
        fixtureManifestSha256 = $script:fixtureManifestHash
        sandboxRoot = $script:sandboxRoot
        error = $failureErrorRecord.Exception.ToString()
    }
    try {
        New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
        [IO.File]::WriteAllText($script:receiptPath, ($failure | ConvertTo-Json -Depth 16), [Text.UTF8Encoding]::new($false))
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
        if ($script:failed -and $null -ne $script:primaryError) {
            Add-VerificationExceptionSecondaryDiagnostic `
                -Exception $script:primaryError.Exception `
                -Diagnostic $ownedCleanupException.ToString()
        }
        else {
            $script:failed = $true
            throw $ownedCleanupException
        }
    }
    if ($KeepSandbox -or $script:failed) {
        Write-Host "v2.1.6.0 first-hop acceptance sandbox retained: $script:sandboxRoot"
    }
    elseif (Test-Path -LiteralPath $script:sandboxRoot) {
        if ([DateTime]::UtcNow -ge $sandboxCleanupDeadline) {
            throw 'Successful first-hop acceptance sandbox cleanup reached its execution deadline.'
        }
        Remove-Item -LiteralPath $script:sandboxRoot -Recurse -Force -ErrorAction Stop
        if ([DateTime]::UtcNow -ge $sandboxCleanupDeadline) {
            throw 'Successful first-hop acceptance sandbox cleanup crossed its execution deadline.'
        }
    }
}
