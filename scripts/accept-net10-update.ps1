[CmdletBinding()]
param(
    [string]$BaselineCommit = 'ab9d97ed3f53dab80fb2894f20f44abdfb6fed32',
    [string]$FixtureRoot,
    [string]$OutputDirectory,
    [string]$SqliteAssemblyRoot,
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

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)][string]$Command,
        [Parameter(ValueFromRemainingArguments)][string[]]$Arguments
    )
    & $Command @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $Command $($Arguments -join ' ')"
    }
}

function Write-LegacyConfig {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][hashtable]$Settings
    )
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
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
    param([Parameter(Mandatory)][string]$Path)
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
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
    $pathElement.InnerText = 'bms\'
    [void]$jukebox.AppendChild($pathElement)
    [void]$config.AppendChild($jukebox)
    $document.Save($Path)
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
        [int]$TimeoutSeconds = 180
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
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
        Start-Sleep -Milliseconds 250
        $Process.Refresh()
    }
    throw "Application did not reach startup_ready_operable within $TimeoutSeconds seconds. Logs: $LogDirectory"
}

function Wait-ForMainWindow {
    param([Parameter(Mandatory)][Diagnostics.Process]$Process, [int]$TimeoutSeconds = 30)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($Process.HasExited) {
            throw "Application exited before exposing a main window (exit code $($Process.ExitCode))."
        }
        $Process.Refresh()
        if ($Process.MainWindowHandle -ne 0) {
            return
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Application did not expose a main window within $TimeoutSeconds seconds."
}

function Start-IsolatedApplication {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string]$ProfileRoot,
        [Parameter(Mandatory)][string]$LogDirectory
    )
    Prepare-LogDirectory -LogDirectory $LogDirectory
    $localAppData = Join-Path $ProfileRoot 'user\AppData\Local'
    $temp = Join-Path $ProfileRoot 'temp'
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    $startInfo.WorkingDirectory = Split-Path -Parent $Executable
    $startInfo.UseShellExecute = $false
    $startInfo.Environment['LOCALAPPDATA'] = $localAppData
    $startInfo.Environment['USERPROFILE'] = Join-Path $ProfileRoot 'user'
    $startInfo.Environment['TEMP'] = $temp
    $startInfo.Environment['TMP'] = $temp
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        $process.Dispose()
        throw "Unable to start application: $Executable"
    }
    try {
        try { [void]$process.WaitForInputIdle(30000) } catch { }
        [void](Wait-ForStartupReady -Process $process -LogDirectory $LogDirectory)
        Wait-ForMainWindow -Process $process
        return $process
    }
    catch {
        if (-not $process.HasExited) {
            $process.Kill()
            $process.WaitForExit()
        }
        $process.Dispose()
        throw
    }
}

function Close-IsolatedApplication {
    param([Parameter(Mandatory)][Diagnostics.Process]$Process)
    try {
        $requested = $Process.CloseMainWindow()
        if (-not $requested) {
            $handle = $Process.MainWindowHandle
            if ($handle -eq 0 -or -not [BeMusicSeekerUpdateAcceptanceWindowMessage]::PostMessage($handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)) {
                throw 'Application did not accept a graceful shutdown request.'
            }
        }
        if (-not $Process.WaitForExit(120000)) {
            throw 'Application did not exit after a graceful shutdown request.'
        }
        $exitCode = $Process.ExitCode
        if ($null -ne $exitCode -and [int]$exitCode -ne 0) {
            throw "Application shutdown returned exit code $exitCode."
        }
    }
    finally {
        if (-not $Process.HasExited) {
            $Process.Kill()
            $Process.WaitForExit()
        }
        $Process.Dispose()
    }
}

function Start-UpdaterHandshake {
    param(
        [Parameter(Mandatory)][string]$UpdaterExecutable,
        [Parameter(Mandatory)][string]$AppRoot,
        [Parameter(Mandatory)][string]$PackagePath,
        [Parameter(Mandatory)][int]$ApplicationProcessId
    )
    $workRoot = Join-Path $AppRoot 'update_work'
    $current = Join-Path $workRoot 'current'
    $downloads = Join-Path $workRoot 'downloads'
    New-Item -ItemType Directory -Path $current, $downloads -Force | Out-Null
    $downloadedPackage = Join-Path $downloads ([IO.Path]::GetFileName($PackagePath))
    Copy-Item -LiteralPath $PackagePath -Destination $downloadedPackage -Force
    $ready = Join-Path $current 'updater-ready.txt'
    $decision = Join-Path $current 'updater-decision.txt'
    Remove-Item -LiteralPath $ready, $decision -Force -ErrorAction SilentlyContinue
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $UpdaterExecutable
    $startInfo.WorkingDirectory = $AppRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($value in @(
            '--app-dir', $AppRoot,
            '--package', $downloadedPackage,
            '--backup-dir', (Join-Path $AppRoot 'update_backup'),
            '--ready-file', $ready,
            '--decision-file', $decision,
            '--pid', ([string]$ApplicationProcessId),
            '--restart-exe', (Join-Path $AppRoot 'BeMusicSeeker.exe'))) {
        [void]$startInfo.ArgumentList.Add([string]$value)
    }
    $updater = [Diagnostics.Process]::new()
    $updater.StartInfo = $startInfo
    if (-not $updater.Start()) {
        $updater.Dispose()
        throw "Unable to start updater: $UpdaterExecutable"
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(180)
    while (-not (Test-Path -LiteralPath $ready -PathType Leaf) -and [DateTime]::UtcNow -lt $deadline) {
        if ($updater.HasExited) {
            $stdout = $updater.StandardOutput.ReadToEnd()
            $stderr = $updater.StandardError.ReadToEnd()
            throw "Updater exited before ready handshake (exit code $($updater.ExitCode)). stdout=$stdout stderr=$stderr"
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $ready -PathType Leaf)) {
        throw 'Updater did not publish the ready handshake within 180 seconds.'
    }
    return [pscustomobject]@{
        Process = $updater
        ReadyPath = $ready
        DecisionPath = $decision
        DownloadedPackage = $downloadedPackage
    }
}

function Approve-UpdaterHandshake {
    param([Parameter(Mandatory)]$Handshake)
    [IO.File]::WriteAllText($Handshake.DecisionPath, 'proceed', [Text.UTF8Encoding]::new($false))
}

function Wait-UpdaterExit {
    param([Parameter(Mandatory)]$Handshake)
    try {
        if (-not $Handshake.Process.WaitForExit(180000)) {
            throw 'Updater did not exit within 180 seconds.'
        }
        return [int]$Handshake.Process.ExitCode
    }
    finally {
        $Handshake.Process.Dispose()
    }
}

function Stop-SandboxProcesses {
    param([Parameter(Mandatory)][string]$SandboxRoot)
    $normalizedRoot = (Resolve-FullPath $SandboxRoot).TrimEnd('\') + '\'
    foreach ($process in @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
            $_.Name -in @('BeMusicSeeker.exe', 'BeMusicSeeker.Updater.exe') -and
            $_.ExecutablePath -and
            (Resolve-FullPath $_.ExecutablePath).StartsWith($normalizedRoot, [StringComparison]::OrdinalIgnoreCase)
        })) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

function Wait-RestartedApplication {
    param([Parameter(Mandatory)][string]$Executable, [int]$TimeoutSeconds = 180)
    $normalized = (Resolve-FullPath $Executable)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        foreach ($candidate in @(Get-Process -Name 'BeMusicSeeker' -ErrorAction SilentlyContinue)) {
            try {
                if ($candidate.MainModule.FileName -and (Resolve-FullPath $candidate.MainModule.FileName) -eq $normalized) {
                    return $candidate
                }
            }
            catch { }
            finally {
                if ($candidate.HasExited) { $candidate.Dispose() }
            }
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Updater did not restart the application: $Executable"
}

function Archive-LogsBeforeRestart {
    param([Parameter(Mandatory)][string]$LogDirectory)
    $archive = Join-Path $LogDirectory ('prior-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $archive -Force | Out-Null
    foreach ($log in @(Get-ChildItem -LiteralPath $LogDirectory -Filter '*.log' -File -ErrorAction SilentlyContinue)) {
        Move-Item -LiteralPath $log.FullName -Destination (Join-Path $archive $log.Name) -Force
    }
}

function Export-BaselinePackage {
    param([Parameter(Mandatory)][string]$Root)
    $archive = Join-Path $Root 'baseline-source.zip'
    $source = Join-Path $Root 'source'
    New-Item -ItemType Directory -Path $source -Force | Out-Null
    Invoke-CheckedCommand git 'archive' '--format=zip' "--output=$archive" $BaselineCommit
    Expand-Archive -LiteralPath $archive -DestinationPath $source -Force
    Invoke-CheckedCommand dotnet 'restore' (Join-Path $source 'BeMusicSeeker.sln') '-r' 'win-x64' '--locked-mode'
    $publish = Join-Path $source 'scripts\publish.ps1'
    Invoke-CheckedCommand pwsh '-NoProfile' '-File' $publish '-PackageOnly' '-SkipDocHtml'
    $package = Get-ChildItem -LiteralPath (Join-Path $source 'dist') -Filter '*.zip' -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $package) {
        throw 'Baseline publish did not produce a release package.'
    }
    return [pscustomobject]@{
        Commit = $BaselineCommit
        SourceRoot = $source
        PackagePath = $package.FullName
        PackageSha256 = Get-Sha256 -Path $package.FullName
    }
}

function Export-CurrentPackage {
    $publish = Join-Path $repoRoot 'scripts\publish.ps1'
    Invoke-CheckedCommand pwsh '-NoProfile' '-File' $publish '-PackageOnly' '-SkipDocHtml'
    $package = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'dist') -Filter '*.zip' -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $package) {
        throw 'Current publish did not produce a release package.'
    }
    return [pscustomobject]@{
        PackagePath = $package.FullName
        PackageSha256 = Get-Sha256 -Path $package.FullName
        AppPublishRoot = Join-Path $repoRoot 'artifacts\publish\app'
        UpdaterPublishRoot = Join-Path $repoRoot 'artifacts\publish\updater'
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
        [Parameter(Mandatory)][string]$AppRoot,
        [Parameter(Mandatory)][string]$ProfileName
    )
    $bmsRoot = Join-Path $ProfileRoot 'bms'
    if ($ProfileName -eq 'lr2') { $bmsRoot = Join-Path $ProfileRoot 'lr2\bms' }
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
        OperationModeLR2DB = ([bool]($ProfileName -eq 'lr2')).ToString()
        BMSInstallDir = $bmsRoot
        BMSRootPath = $bmsRoot
        TableListURL = 'https://example.invalid/e2-update-table-list'
        EnablePlaylistUrlCompletion = 'False'
        EnableStellaFullPlaylistUrlCompletion = 'False'
        SkipInitPlaylistLoad = 'True'
    }
    if ($ProfileName -eq 'lr2') {
        $lr2Root = Join-Path $ProfileRoot 'lr2'
        $databasePath = Join-Path $ProfileRoot 'lr2\LR2files\song.db'
        $lr2Config = Join-Path $ProfileRoot 'lr2\LR2files\Config\config.xml'
        New-Item -ItemType Directory -Path (Join-Path $ProfileRoot 'custom-output'), (Join-Path $ProfileRoot 'custom-root-output') -Force | Out-Null
        Write-Lr2Config -Path $lr2Config
        $settings.LR2RootPath = $lr2Root
        $settings.LR2SongDBPath = $databasePath
        $settings.LR2ConfigXmlPath = $lr2Config
        $settings.LR2CustomFolderOutputBaseDir = Join-Path $ProfileRoot 'custom-output'
        $settings.LR2CustomFolderOutputBaseDirRootType = Join-Path $ProfileRoot 'custom-root-output'
    }
    $installPath = $packageRoot
    Copy-LegacyDatabaseFixture -Path $databasePath -InstallPath $installPath -BmsRoot $bmsRoot
    $legacyConfigRelativePath = if ($ProfileName -eq 'lr2') { 'BeMusicSeeker.exe_Url_E2Lr2\1.0.0.0\user.config' } else { 'BeMusicSeeker.exe_Url_E2Standalone\1.0.0.0\user.config' }
    $legacyConfig = Join-Path (Join-Path $ProfileRoot 'user\AppData\Local\BeMusicSeeker') $legacyConfigRelativePath
    Write-LegacyConfig -Path $legacyConfig -Settings $settings
    $marker = Join-Path $AppRoot 'user-data\e2-unmanaged.marker'
    New-Item -ItemType Directory -Path (Split-Path -Parent $marker) -Force | Out-Null
    [IO.File]::WriteAllText($marker, "E2 unmanaged marker $ProfileName`n", [Text.UTF8Encoding]::new($false))
    return [pscustomobject]@{
        Name = $ProfileName
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
        PortableSettingsSha256 = if (Test-Path -LiteralPath $portable -PathType Leaf) { Get-Sha256 -Path $portable } else { $null }
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

function Wait-CurrentRestart {
    param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$LogDirectory)
    $process = Wait-RestartedApplication -Executable $Executable
    try {
        [void](Wait-ForStartupReady -Process $process -LogDirectory $LogDirectory)
        Wait-ForMainWindow -Process $process
        return $process
    }
    catch {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
        throw
    }
}

function New-FaultPackage {
    param([Parameter(Mandatory)][string]$CurrentPackagePath, [Parameter(Mandatory)][string]$Destination)
    $extracted = Join-Path (Split-Path -Parent $Destination) ('fault-extracted-' + [Guid]::NewGuid().ToString('N'))
    Expand-AppPackage -PackagePath $CurrentPackagePath -Destination $extracted
    [IO.File]::WriteAllBytes((Join-Path $extracted 'BeMusicSeeker.exe'), [Text.Encoding]::ASCII.GetBytes('not-a-windows-executable'))
    Compress-Archive -Path (Join-Path $extracted '*') -DestinationPath $Destination -CompressionLevel Optimal
    Remove-Item -LiteralPath $extracted -Recurse -Force
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

try {
    $baselineRoot = Join-Path $script:sandboxRoot 'baseline'
    New-Item -ItemType Directory -Path $baselineRoot -Force | Out-Null
    $baseline = Export-BaselinePackage -Root $baselineRoot
    $current = Export-CurrentPackage
    $script:currentAppPublishRoot = Resolve-FullPath $current.AppPublishRoot
    Assert-Directory $script:currentAppPublishRoot

    $successRoot = Join-Path $script:sandboxRoot 'success'
    $successApp = Join-Path $successRoot 'app'
    Expand-AppPackage -PackagePath $baseline.PackagePath -Destination $successApp
    $baselineExecutableHash = Get-Sha256 -Path (Join-Path $successApp 'BeMusicSeeker.exe')
    $successProfile = Prepare-Profile -ProfileRoot $successRoot -AppRoot $successApp -ProfileName 'standalone'
    $successLog = Join-Path $successApp 'log'
    $oldProcess = Start-IsolatedApplication -Executable (Join-Path $successApp 'BeMusicSeeker.exe') -ProfileRoot $successRoot -LogDirectory $successLog
    $beforeSuccess = Get-ProfileState -Profile ([pscustomobject]@{ AppRoot = $successApp; DatabasePath = $successProfile.DatabasePath; InstallPath = $successProfile.InstallPath; LegacyConfigPath = $successProfile.LegacyConfigPath; MarkerPath = $successProfile.MarkerPath })
    # The committed .NET 10 baseline exercises the current updater handshake
    # without rebuilding the retired net472 application.
    $successUpdater = Start-UpdaterHandshake -UpdaterExecutable (Join-Path $successApp 'BeMusicSeeker.Updater.exe') -AppRoot $successApp -PackagePath $current.PackagePath -ApplicationProcessId $oldProcess.Id
    Close-IsolatedApplication -Process $oldProcess
    Archive-LogsBeforeRestart -LogDirectory $successLog
    Approve-UpdaterHandshake -Handshake $successUpdater
    $successExitCode = Wait-UpdaterExit -Handshake $successUpdater
    if ($successExitCode -ne 0) { throw "Baseline updater did not complete update: exit=$successExitCode" }
    $currentProcess = Wait-CurrentRestart -Executable (Join-Path $successApp 'BeMusicSeeker.exe') -LogDirectory $successLog
    Close-IsolatedApplication -Process $currentProcess
    $successAfter = Get-ProfileState -Profile ([pscustomobject]@{ AppRoot = $successApp; DatabasePath = $successProfile.DatabasePath; InstallPath = $successProfile.InstallPath; LegacyConfigPath = $successProfile.LegacyConfigPath; MarkerPath = $successProfile.MarkerPath })
    Assert-SemanticStateEqual -Before $beforeSuccess -After $successAfter -Label 'old-to-new success'
    Assert-PortableSingleFilePayloadLayout $successApp
    $successManaged = Get-TreeSha256 -Root $successApp
    if ((Get-Sha256 -Path (Join-Path $successApp 'BeMusicSeeker.exe')) -eq $baselineExecutableHash) { throw 'Baseline-to-current update did not replace the application executable.' }
    Assert-File (Join-Path $successApp 'BeMusicSeeker.Updater.exe')
    if (Test-Path -LiteralPath (Join-Path $successApp 'BeMusicSeeker.Updater.dll')) { throw 'Current single-file updater left a companion DLL.' }

    $rollbackRoot = Join-Path $script:sandboxRoot 'rollback'
    $rollbackApp = Join-Path $rollbackRoot 'app'
    Expand-AppPackage -PackagePath $current.PackagePath -Destination $rollbackApp
    # Keep rollback focused on updater transaction semantics. LR2 startup
    # legitimately rebuilds derived folder rows, which is covered by E1 and
    # must not be mistaken for update data loss here.
    $rollbackProfile = Prepare-Profile -ProfileRoot $rollbackRoot -AppRoot $rollbackApp -ProfileName 'standalone'
    $rollbackLog = Join-Path $rollbackApp 'log'
    $faultPackage = Join-Path $rollbackRoot 'fault-package.zip'
    New-FaultPackage -CurrentPackagePath $current.PackagePath -Destination $faultPackage
    $rollbackProcess = Start-IsolatedApplication -Executable (Join-Path $rollbackApp 'BeMusicSeeker.exe') -ProfileRoot $rollbackRoot -LogDirectory $rollbackLog
    $beforeRollback = Get-ProfileState -Profile ([pscustomobject]@{ AppRoot = $rollbackApp; DatabasePath = $rollbackProfile.DatabasePath; InstallPath = $rollbackProfile.InstallPath; LegacyConfigPath = $rollbackProfile.LegacyConfigPath; MarkerPath = $rollbackProfile.MarkerPath })
    $rollbackOldExeHash = Get-Sha256 -Path (Join-Path $rollbackApp 'BeMusicSeeker.exe')
    $rollbackHandshake = Start-UpdaterHandshake -UpdaterExecutable (Join-Path $rollbackApp 'BeMusicSeeker.Updater.exe') -AppRoot $rollbackApp -PackagePath $faultPackage -ApplicationProcessId $rollbackProcess.Id
    Close-IsolatedApplication -Process $rollbackProcess
    Archive-LogsBeforeRestart -LogDirectory $rollbackLog
    Approve-UpdaterHandshake -Handshake $rollbackHandshake
    $rollbackExitCode = Wait-UpdaterExit -Handshake $rollbackHandshake
    if ($rollbackExitCode -eq 0) { throw 'Fault package unexpectedly completed as a successful update.' }
    $failureReceiptCandidates = @(
        (Join-Path $rollbackApp 'update_work\update-failure.txt'),
        (Join-Path $rollbackApp 'update_work\update-failure.txt.tmp')
    )
    $failureReceiptPath = $failureReceiptCandidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($failureReceiptPath)) {
        throw 'Fault-package rollback did not persist an update failure receipt.'
    }
    if ([string]::IsNullOrWhiteSpace((Get-Content -LiteralPath $failureReceiptPath -Raw))) {
        throw "Fault-package rollback persisted an empty update failure receipt: $failureReceiptPath"
    }
    $failureReceiptHash = Get-Sha256 -Path $failureReceiptPath
    $recoveredProcess = Wait-CurrentRestart -Executable (Join-Path $rollbackApp 'BeMusicSeeker.exe') -LogDirectory $rollbackLog
    Close-IsolatedApplication -Process $recoveredProcess
    $rollbackAfter = Get-ProfileState -Profile ([pscustomobject]@{ AppRoot = $rollbackApp; DatabasePath = $rollbackProfile.DatabasePath; InstallPath = $rollbackProfile.InstallPath; LegacyConfigPath = $rollbackProfile.LegacyConfigPath; MarkerPath = $rollbackProfile.MarkerPath })
    Assert-SemanticStateEqual -Before $beforeRollback -After $rollbackAfter -Label 'fault-package rollback'
    if ((Get-Sha256 -Path (Join-Path $rollbackApp 'BeMusicSeeker.exe')) -ne $rollbackOldExeHash) { throw 'Rollback did not restore the previous application executable.' }
    foreach ($residual in @('update_work\update-transaction.json', 'update_work\update-transaction.lock', 'update_work\extracted', 'update_work\downloads')) {
        if (Test-Path -LiteralPath (Join-Path $rollbackApp $residual)) { throw "Rollback left transaction residue: $residual" }
    }

    $receipt = [ordered]@{
        schemaVersion = 1
        status = 'passed'
        baselineCommit = $baseline.Commit
        baselinePackageSha256 = $baseline.PackageSha256
        currentPackageSha256 = $current.PackageSha256
        currentAppPublishTreeSha256 = Get-TreeSha256 -Root $current.AppPublishRoot
        fixtureManifestSha256 = $script:manifestHash
        success = [ordered]@{ updaterExitCode = $successExitCode; appTreeSha256 = $successManaged; semanticDataPreserved = $true }
        rollback = [ordered]@{ updaterExitCode = $rollbackExitCode; failureReceiptFileName = [IO.Path]::GetFileName($failureReceiptPath); failureReceiptSha256 = $failureReceiptHash; restoredExecutableSha256 = $rollbackOldExeHash; semanticDataPreserved = $true }
    }
    [IO.File]::WriteAllText($script:receiptPath, ($receipt | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    Write-Host "NET10 update acceptance passed: $script:receiptPath"
}
catch {
    $script:failed = $true
    $failure = [ordered]@{
        schemaVersion = 1
        status = 'failed'
        baselineCommit = $BaselineCommit
        fixtureManifestSha256 = $script:manifestHash
        sandboxRoot = $script:sandboxRoot
        error = $_.Exception.ToString()
    }
    [IO.File]::WriteAllText($script:receiptPath, ($failure | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    throw
}
finally {
    Stop-SandboxProcesses -SandboxRoot $script:sandboxRoot
    if ($KeepSandbox -or $script:failed) {
        Write-Host "NET10 update acceptance sandbox retained: $script:sandboxRoot"
    }
    elseif (Test-Path -LiteralPath $script:sandboxRoot) {
        Remove-Item -LiteralPath $script:sandboxRoot -Recurse -Force
    }
}
