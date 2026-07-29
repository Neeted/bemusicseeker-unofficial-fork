[CmdletBinding()]
param(
    [string]$OutputRoot,
    [string]$FixtureRoot,
    [string]$SqliteAssemblyRoot,
    [int]$StartupTimeoutSeconds = 180,
    [switch]$DescribeCandidates,
    [switch]$ImportFunctionsOnly,
    [switch]$SmokeOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$benchmarkImportFunctionsOnly = $ImportFunctionsOnly

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $outputRelativePath = if ($SmokeOnly) {
        'artifacts\performance\net10-distribution-smoke'
    }
    else {
        'artifacts\performance\net10-distribution'
    }
    $OutputRoot = Join-Path $repoRoot $outputRelativePath
}
if ([string]::IsNullOrWhiteSpace($FixtureRoot)) {
    $FixtureRoot = Join-Path $repoRoot 'devdocs\acceptance\net10-existing-data'
}
if ([string]::IsNullOrWhiteSpace($SqliteAssemblyRoot)) {
    $SqliteAssemblyRoot = Join-Path $repoRoot 'BeMusicSeeker.Tests\bin\x64\Release\net10.0-windows'
}

$candidateMatrix = @(
    [ordered]@{ name = 'folder-il'; family = 'folder'; publishSingleFile = $false; nativeSelfExtract = $false; readyToRun = $false },
    [ordered]@{ name = 'folder-r2r'; family = 'folder'; publishSingleFile = $false; nativeSelfExtract = $false; readyToRun = $true },
    [ordered]@{ name = 'bundle-il'; family = 'bundle'; publishSingleFile = $true; nativeSelfExtract = $false; readyToRun = $false },
    [ordered]@{ name = 'bundle-r2r'; family = 'bundle'; publishSingleFile = $true; nativeSelfExtract = $false; readyToRun = $true },
    [ordered]@{ name = 'extract-il'; family = 'extract'; publishSingleFile = $true; nativeSelfExtract = $true; readyToRun = $false },
    [ordered]@{ name = 'extract-r2r'; family = 'extract'; publishSingleFile = $true; nativeSelfExtract = $true; readyToRun = $true }
)

$benchmarkContract = [ordered]@{
    schemaVersion = 2
    candidates = $candidateMatrix
    warmupRuns = 1
    warmCacheMeasuredRuns = 3
    freshInstallMeasuredRuns = 3
    additionalRunsPerAmbiguousPhase = 2
    practicalStartupDifference = 'max(500 ms, 10%)'
    practicalWorkingSetDifference = 'max(16 MiB, 10%)'
    equivalentDefault = 'bundle-r2r'
    startupMetric = 'process-start-to-startup-ready-operable-ms'
    smoke = [ordered]@{
        candidate = 'extract-r2r'
        freshRuns = 1
        warmRuns = 1
    }
}

if ($DescribeCandidates) {
    $benchmarkContract | ConvertTo-Json -Depth 8
    return
}

if ($StartupTimeoutSeconds -lt 1) {
    throw 'StartupTimeoutSeconds must be positive.'
}

if ($null -eq ('BeMusicSeekerDistributionBenchmarkWindowMessage' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class BeMusicSeekerDistributionBenchmarkWindowMessage
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
}
'@
}

if ($null -eq ('BeMusicSeekerDistributionBenchmarkSqliteMasterRow' -as [type])) {
    Add-Type -TypeDefinition @'
public sealed class BeMusicSeekerDistributionBenchmarkSqliteMasterRow
{
    public string Type { get; set; }
    public string Name { get; set; }
    public string Sql { get; set; }
}
'@
}

if ($null -eq ('BeMusicSeekerDistributionBenchmarkManifestServer' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public sealed class BeMusicSeekerDistributionBenchmarkManifestServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly Thread thread;
    private volatile bool stopping;

    public BeMusicSeekerDistributionBenchmarkManifestServer()
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        thread = new Thread(Serve) { IsBackground = true, Name = "BeMusicSeeker benchmark manifest" };
        thread.Start();
    }

    public int Port { get; }

    public string ManifestJson { get; set; } = "{}";

    private void Serve()
    {
        while (!stopping)
        {
            try
            {
                using TcpClient client = listener.AcceptTcpClient();
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                using NetworkStream stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                while (!string.IsNullOrEmpty(reader.ReadLine()))
                {
                }

                byte[] body = Encoding.UTF8.GetBytes(ManifestJson);
                byte[] header = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: application/json; charset=utf-8\r\n" +
                    "Content-Length: " + body.Length + "\r\n" +
                    "Connection: close\r\n\r\n");
                stream.Write(header, 0, header.Length);
                stream.Write(body, 0, body.Length);
            }
            catch (SocketException) when (stopping)
            {
                return;
            }
            catch (ObjectDisposedException) when (stopping)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        stopping = true;
        listener.Stop();
        if (thread.IsAlive)
        {
            thread.Join(5000);
        }
    }
}
'@
}

function ConvertTo-Ms {
    param([Parameter(Mandatory)][TimeSpan]$Elapsed)
    return [Math]::Round($Elapsed.TotalMilliseconds, 3)
}

function Get-DirectoryMetrics {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return [ordered]@{ bytes = 0L; files = 0 }
    }
    $files = @(Get-ChildItem -LiteralPath $Path -Recurse -File)
    $bytes = 0L
    foreach ($file in $files) {
        $bytes += [long]$file.Length
    }
    return [ordered]@{
        bytes = $bytes
        files = $files.Count
    }
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(ValueFromRemainingArguments)][string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($ArgumentList -join ' ')"
    }
}

function Remove-DirectoryWithRetry {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force
            return
        }
        catch {
            if ($attempt -eq 10) {
                throw
            }
            Start-Sleep -Milliseconds 500
        }
    }
}

function Assert-BenchmarkOutputRootIsSafe {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$RepositoryRoot
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $performanceRoot = [IO.Path]::GetFullPath(
        (Join-Path $RepositoryRoot 'artifacts\performance'))
    $performanceRootPrefix =
        $performanceRoot.TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith(
            $performanceRootPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Benchmark OutputRoot must be a dedicated child of '$performanceRoot': '$fullPath'."
    }
}

function Write-JsonNoBom {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Value,
        [int]$Depth = 16
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [IO.File]::WriteAllText(
        $Path,
        ($Value | ConvertTo-Json -Depth $Depth),
        [Text.UTF8Encoding]::new($false))
}

function Get-ObjectSha256 {
    param([Parameter(Mandatory)]$Value)

    $json = $Value | ConvertTo-Json -Depth 12 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Export-RunCsv {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [Collections.IEnumerable]$RunResults,
        [Parameter(Mandatory)][string]$Path
    )

    $rows = @(
        foreach ($run in $RunResults) {
            [pscustomobject][ordered]@{
                candidate = $run.candidate
                phase = $run.phase
                round = $run.round
                sequence = $run.sequence
                generatedUtc = $run.generatedUtc
                mainWindowHandleMs = $run.mainWindowHandleMs
                inputIdleMs = $run.inputIdleMs
                mainWindowReadyMs = $run.mainWindowReadyMs
                startupReadyOperableMs = $run.startupReadyOperableMs
                peakWorkingSetAtReadyBytes = $run.peakWorkingSetAtReadyBytes
                processorTimeAtReadyMs = $run.processorTimeAtReadyMs
                extractedBytes = $run.extractedBytes
                extractedFiles = $run.extractedFiles
                readyLog = $run.readyLog
                shutdownRequest = $run.shutdownRequest
                exitCode = $run.exitCode
                semanticStateJson = if ($null -eq $run.semanticState) {
                    $null
                }
                else {
                    $run.semanticState | ConvertTo-Json -Depth 8 -Compress
                }
                failure = $run.failure
            }
        }
    )
    if ($rows.Count -eq 0) {
        [IO.File]::WriteAllText($Path, '', [Text.UTF8Encoding]::new($false))
        return
    }
    $rows | Export-Csv -LiteralPath $Path -NoTypeInformation -Encoding utf8NoBOM
}

function Get-BenchmarkEnvironment {
    $gitCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to resolve the benchmark source commit.'
    }
    $dirty = @(
        & git -C $repoRoot -c core.quotepath=false status --porcelain --untracked-files=all
    ).Count -gt 0
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to determine the benchmark source worktree state.'
    }

    return [ordered]@{
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        operatingSystem = [Environment]::OSVersion.VersionString
        windowsBuild = [Environment]::OSVersion.Version.Build
        processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        processor = $env:PROCESSOR_IDENTIFIER
        logicalProcessorCount = [Environment]::ProcessorCount
        installedMemoryBytes = [long](Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
        sdkVersion = (& dotnet --version).Trim()
        gitCommit = $gitCommit
        dirty = $dirty
        defenderAndNetworkCondition = 'not changed by harness; all candidates measured in the same session'
        osPageCache = 'not cleared; fresh-install is not called cold'
    }
}

function Get-Percentile {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [double[]]$Values,
        [Parameter(Mandatory)][ValidateRange(0.0, 1.0)][double]$Percentile
    )

    if ($Values.Count -eq 0) {
        return $null
    }
    $sorted = @($Values | Sort-Object)
    $rank = [Math]::Ceiling($Percentile * $sorted.Count)
    $index = [Math]::Max(0, [Math]::Min($sorted.Count - 1, $rank - 1))
    return [Math]::Round([double]$sorted[$index], 3)
}

function Get-RotatedCandidates {
    param(
        [Parameter(Mandatory)][object[]]$Candidates,
        [Parameter(Mandatory)][int]$Round
    )

    $start = ($Round - 1) % $Candidates.Count
    return @(
        for ($offset = 0; $offset -lt $Candidates.Count; $offset++) {
            $Candidates[($start + $offset) % $Candidates.Count]
        }
    )
}

function Publish-Candidates {
    param([Parameter(Mandatory)][object[]]$Candidates)

    Invoke-CheckedCommand dotnet restore `
        (Join-Path $repoRoot 'BeMusicSeeker.csproj') `
        '--runtime' 'win-x64' `
        '--locked-mode' `
        '-p:PublishReadyToRun=true'

    $publishRoot = Join-Path $OutputRoot 'publish'
    New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
    foreach ($candidate in $Candidates) {
        $candidateOutput = Join-Path $publishRoot $candidate.name
        if (Test-Path -LiteralPath $candidateOutput) {
            Remove-Item -LiteralPath $candidateOutput -Recurse -Force
        }
        New-Item -ItemType Directory -Path $candidateOutput -Force | Out-Null

        $properties = @(
            '/p:Configuration=Release',
            '/p:Platform=x64',
            '--runtime', 'win-x64',
            '--self-contained', 'true',
            '--no-restore',
            '-p:PublishTrimmed=false',
            '-p:PublishReadyToRunComposite=false',
            '-p:EnableCompressionInSingleFile=false',
            '-p:IncludeAllContentForSelfExtract=false',
            '-p:DebugType=None',
            '-p:DebugSymbols=false',
            "-p:PublishSingleFile=$($candidate.publishSingleFile.ToString().ToLowerInvariant())",
            "-p:IncludeNativeLibrariesForSelfExtract=$($candidate.nativeSelfExtract.ToString().ToLowerInvariant())",
            "-p:PublishReadyToRun=$($candidate.readyToRun.ToString().ToLowerInvariant())",
            "-p:PublishDir=$candidateOutput"
        )
        Invoke-CheckedCommand dotnet publish (Join-Path $repoRoot 'BeMusicSeeker.csproj') @properties
    }
}

function New-BenchmarkManifestServer {
    $server = [BeMusicSeekerDistributionBenchmarkManifestServer]::new()
    $baseUrl = "http://127.0.0.1:$($server.Port)"
    $server.ManifestJson = [ordered]@{
        schemaVersion = 1
        version = '0.0.0.0'
        releaseTag = 'v0.0.0.0'
        releasePageUrl = "$baseUrl/"
        packageFormatVersion = 1
        publishedAt = '2000-01-01T00:00:00Z'
        minimumUpdaterVersion = '1'
        assets = @(
            [ordered]@{
                kind = 'app'
                label = 'Benchmark no-update fixture'
                fileName = 'unused.zip'
                url = "$baseUrl/unused.zip"
                sha256 = '0000000000000000000000000000000000000000000000000000000000000000'
                sizeBytes = 1
                includesChartInfoMetadata = $false
            }
        )
    } | ConvertTo-Json -Depth 8 -Compress
    $script:updateManifestUrl = "$baseUrl/update.json"
    return $server
}

function New-StandaloneProfile {
    param(
        [Parameter(Mandatory)][object]$Candidate,
        [Parameter(Mandatory)][string]$ProfileRoot,
        [Parameter(Mandatory)][string]$ExtractRoot
    )

    if (Test-Path -LiteralPath $ProfileRoot) {
        Remove-Item -LiteralPath $ProfileRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $ProfileRoot -Force | Out-Null

    $appRoot = Join-Path $ProfileRoot 'app'
    $publishRoot = Join-Path (Join-Path $OutputRoot 'publish') $Candidate.name
    Copy-Item -LiteralPath $publishRoot -Destination $appRoot -Recurse -Force

    $profile = @($script:manifest.profiles | Where-Object { -not [bool]$_.operationModeLr2Db })[0]
    if ($null -eq $profile) {
        throw 'The existing-data fixture does not contain a standalone profile.'
    }

    $localAppData = Join-Path $ProfileRoot 'user\AppData\Local'
    $bmsRoot = Join-Path $ProfileRoot ([string]$profile.bmsRootDirectory)
    $bmsFixturePath = Join-Path $bmsRoot $script:manifest.database.songRelativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $bmsFixturePath) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $FixtureRoot 'fixture.bms') -Destination $bmsFixturePath -Force

    $installPath = Join-Path $ProfileRoot 'install\Fixture\package'
    New-Item -ItemType Directory -Path $installPath -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $FixtureRoot 'package\e1-fixture-package.marker') -Destination (Join-Path $installPath 'e1-fixture-package.marker') -Force
    Copy-Item -LiteralPath (Join-Path $FixtureRoot 'fixture.bms') -Destination (Join-Path $installPath 'e1-fixture.bms') -Force

    $databasePath = Join-Path $appRoot 'data\song.db'
    Copy-LegacyDatabaseFixture -Path $databasePath -Manifest $script:manifest -InstallPath $installPath -BmsRoot $bmsRoot

    $settings = @{
        AssemblyVersion = [string]$script:manifest.settings.assemblyVersion
        Lang = [string]$script:manifest.settings.language
        AppearanceTheme = [string]$script:manifest.settings.appearanceTheme
        OperationModeLR2DB = 'False'
        BMSInstallDir = $bmsRoot
        BMSRootPath = $bmsRoot
        TableListURL = 'https://example.invalid/net10-distribution-benchmark'
        EnablePlaylistUrlCompletion = 'False'
        EnableStellaFullPlaylistUrlCompletion = 'False'
        SkipInitPlaylistLoad = 'True'
        ScanBmsFilesOnStartup = 'False'
    }
    $legacyConfigPath = Join-Path (Join-Path $localAppData 'BeMusicSeeker') ([string]$profile.legacyConfigDirectory)
    $legacyConfigPath = Join-Path $legacyConfigPath 'user.config'
    Write-LegacyConfig -Path $legacyConfigPath -Settings $settings

    if (Test-Path -LiteralPath $ExtractRoot) {
        Remove-Item -LiteralPath $ExtractRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $ExtractRoot -Force | Out-Null

    return [ordered]@{
        root = $ProfileRoot
        appRoot = $appRoot
        executable = Join-Path $appRoot 'BeMusicSeeker.exe'
        localAppData = $localAppData
        logDirectory = Join-Path $appRoot 'log'
        databasePath = $databasePath
        installPath = $installPath
        portableSettingsPath = Join-Path $appRoot 'config\user.config'
        legacyConfigPath = $legacyConfigPath
        legacyConfigSha256 = Get-Sha256 -Path $legacyConfigPath
        bmsFixturePath = $bmsFixturePath
        bmsFixtureSha256 = Get-Sha256 -Path $bmsFixturePath
        extractRoot = $ExtractRoot
        manifestProfile = $profile
    }
}

function Get-DatabaseInventorySnapshot {
    param([Parameter(Mandatory)][string]$Path)

    Initialize-SqliteRuntime
    $flags = [SQLite.SQLiteOpenFlags]::ReadOnly -bor [SQLite.SQLiteOpenFlags]::FullMutex
    $database = [SQLite.SQLiteConnection]::new($Path, $flags, $true)
    try {
        $masterRows = @(
            $database.Query[BeMusicSeekerDistributionBenchmarkSqliteMasterRow](
                "SELECT type AS Type, name AS Name, sql AS Sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name")
        )
        $schema = @(
            $masterRows |
                ForEach-Object { "$($_.Type)|$($_.Name)|$($_.Sql)" }
        )
        $rowCounts = [ordered]@{}
        foreach ($table in @($masterRows | Where-Object { $_.Type -eq 'table' } | Sort-Object Name)) {
            $quotedName = '"' + ([string]$table.Name).Replace('"', '""') + '"'
            $rowCounts[[string]$table.Name] = $database.ExecuteScalar[long]("SELECT COUNT(*) FROM $quotedName")
        }
        return [ordered]@{
            userVersion = $database.ExecuteScalar[long]('PRAGMA user_version')
            applicationId = $database.ExecuteScalar[long]('PRAGMA application_id')
            schema = $schema
            tableRowCounts = $rowCounts
        }
    }
    finally {
        $database.Close()
        $database.Dispose()
    }
}

function Get-ProfileSemanticSnapshot {
    param([Parameter(Mandatory)]$Profile)

    $settingsPath = if (Test-Path -LiteralPath $Profile.portableSettingsPath -PathType Leaf) {
        $Profile.portableSettingsPath
    }
    else {
        $Profile.legacyConfigPath
    }
    Assert-File $settingsPath
    $settingsMap = Get-ManifestSettingMap -Path $settingsPath
    Assert-ProfileSettings `
        -Settings $settingsMap `
        -ManifestProfile $Profile.manifestProfile `
        -ProfileRoot $Profile.root

    $orderedSettings = [ordered]@{}
    foreach ($name in @($settingsMap.Keys | Sort-Object)) {
        $orderedSettings[[string]$name] = [string]$settingsMap[$name]
    }

    return [ordered]@{
        settingsSource = if ($settingsPath -eq $Profile.portableSettingsPath) { 'portable' } else { 'legacy' }
        persistedSettings = $orderedSettings
        database = Get-DatabaseSemanticSnapshot `
            -Path $Profile.databasePath `
            -Manifest $script:manifest `
            -InstallPath $Profile.installPath
        databaseInventory = Get-DatabaseInventorySnapshot -Path $Profile.databasePath
        legacyConfigSha256 = Get-Sha256 -Path $Profile.legacyConfigPath
        bmsFixtureSha256 = Get-Sha256 -Path $Profile.bmsFixturePath
    }
}

function Assert-ProfileSemanticSnapshotPreserved {
    param(
        [Parameter(Mandatory)]$Before,
        [Parameter(Mandatory)]$After
    )

    foreach ($name in $Before.persistedSettings.Keys) {
        if ($name -eq 'AssemblyVersion' -and $Before.settingsSource -eq 'legacy') {
            # The first startup advances the application's migration marker.
            continue
        }
        if (-not $After.persistedSettings.Contains($name)) {
            throw "Persisted setting disappeared during benchmark startup: $name"
        }
        if ([string]$After.persistedSettings[$name] -ne [string]$Before.persistedSettings[$name]) {
            throw "Persisted setting changed during benchmark startup: $name"
        }
    }

    if (($Before.database | ConvertTo-Json -Compress) -ne ($After.database | ConvertTo-Json -Compress)) {
        throw 'Database semantic snapshot changed during benchmark startup.'
    }
    if ($Before.databaseInventory.userVersion -ne $After.databaseInventory.userVersion -or
        $Before.databaseInventory.applicationId -ne $After.databaseInventory.applicationId) {
        throw 'Database version identity changed during benchmark startup.'
    }
    foreach ($schemaEntry in $Before.databaseInventory.schema) {
        if ($schemaEntry -notin $After.databaseInventory.schema) {
            throw 'An existing database schema entry changed during benchmark startup.'
        }
    }
    $durableTables = @(
        'app_schema_version',
        'folder',
        'install',
        'maintenance',
        'playlist',
        'playlist_course',
        'playlist_entry',
        'song'
    )
    foreach ($tableName in $durableTables) {
        if (-not $Before.databaseInventory.tableRowCounts.Contains($tableName)) {
            continue
        }
        if (-not $After.databaseInventory.tableRowCounts.Contains($tableName) -or
            $Before.databaseInventory.tableRowCounts[$tableName] -ne
            $After.databaseInventory.tableRowCounts[$tableName]) {
            throw "Durable database rows changed during benchmark startup: $tableName"
        }
    }
    if ($Before.legacyConfigSha256 -ne $After.legacyConfigSha256) {
        throw 'Legacy user.config changed during benchmark startup.'
    }
    if ($Before.bmsFixtureSha256 -ne $After.bmsFixtureSha256) {
        throw 'BMS fixture changed during benchmark startup.'
    }
}

function Prepare-RunLogDirectory {
    param(
        [Parameter(Mandatory)][string]$LogDirectory,
        [Parameter(Mandatory)][string]$RawLogDirectory
    )

    if (Test-Path -LiteralPath $RawLogDirectory) {
        Remove-Item -LiteralPath $RawLogDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $RawLogDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $LogDirectory) {
        Get-ChildItem -LiteralPath $LogDirectory -File -ErrorAction SilentlyContinue |
            Remove-Item -Force
    }
    else {
        New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null
    }
}

function Copy-RunLogs {
    param(
        [Parameter(Mandatory)][string]$LogDirectory,
        [Parameter(Mandatory)][string]$RawLogDirectory
    )

    if (Test-Path -LiteralPath $LogDirectory -PathType Container) {
        foreach ($log in Get-ChildItem -LiteralPath $LogDirectory -File -ErrorAction SilentlyContinue) {
            Copy-Item -LiteralPath $log.FullName -Destination (Join-Path $RawLogDirectory $log.Name) -Force
        }
    }
}

function Find-StartupLogState {
    param(
        [Parameter(Mandatory)][string]$LogDirectory,
        [Parameter(Mandatory)][hashtable]$ObservedLengths
    )

    foreach ($name in @('install-performance.log', 'application.log')) {
        $path = Join-Path $LogDirectory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            continue
        }
        $length = (Get-Item -LiteralPath $path).Length
        if ($ObservedLengths.ContainsKey($path) -and $ObservedLengths[$path] -eq $length) {
            continue
        }
        $ObservedLengths[$path] = $length
        $content = Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue
        if ($content -match 'startup_setting_validation_failed|app_schema_preflight_prompt_show|Startup library initialization failure notification|startup_update workflow failed|fallback warning queued|file scan incomplete warning queued|empty scan with existing db warning queued') {
            return [ordered]@{ blocker = $true; ready = $false; path = $path; content = $content }
        }
        if ($content -match 'startup_ready_operable') {
            return [ordered]@{ blocker = $false; ready = $true; path = $path; content = $content }
        }
    }
    return [ordered]@{ blocker = $false; ready = $false; path = $null; content = $null }
}

function Invoke-MeasuredStartup {
    param(
        [Parameter(Mandatory)][object]$Candidate,
        [Parameter(Mandatory)]$Profile,
        [Parameter(Mandatory)][string]$RawLogDirectory
    )

    Prepare-RunLogDirectory -LogDirectory $Profile.logDirectory -RawLogDirectory $RawLogDirectory
    $process = [Diagnostics.Process]::new()
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Profile.executable
    $startInfo.WorkingDirectory = $Profile.appRoot
    $startInfo.UseShellExecute = $false
    $startInfo.Environment['LOCALAPPDATA'] = $Profile.localAppData
    $startInfo.Environment['USERPROFILE'] = Split-Path -Parent (Split-Path -Parent $Profile.localAppData)
    $startInfo.Environment['TEMP'] = Join-Path $Profile.root 'temp'
    $startInfo.Environment['TMP'] = Join-Path $Profile.root 'temp'
    $startInfo.ArgumentList.Add("--update-manifest-url=$script:updateManifestUrl")
    if ($Candidate.nativeSelfExtract) {
        $startInfo.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $Profile.extractRoot
    }
    New-Item -ItemType Directory -Path $startInfo.Environment['TEMP'] -Force | Out-Null
    $process.StartInfo = $startInfo

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    if (-not $process.Start()) {
        throw "Unable to start benchmark app: $($Profile.executable)"
    }

    $mainWindowHandleMs = $null
    $inputIdleMs = $null
    $startupReadyMs = $null
    $peakWorkingSetAtReady = $null
    $processorTimeAtReadyMs = $null
    $readyLog = $null
    $failure = $null
    $shutdownRequest = $null
    $observedLogLengths = @{}

    try {
        $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
        while ([DateTime]::UtcNow -lt $deadline) {
            if ($process.HasExited) {
                throw "App exited before benchmark readiness (exit code $($process.ExitCode))."
            }
            $process.Refresh()
            if ($null -eq $mainWindowHandleMs -and $process.MainWindowHandle -ne 0) {
                $mainWindowHandleMs = ConvertTo-Ms $stopwatch.Elapsed
            }
            if ($null -eq $inputIdleMs -and $process.MainWindowHandle -ne 0) {
                try {
                    if ($process.WaitForInputIdle(1)) {
                        $inputIdleMs = ConvertTo-Ms $stopwatch.Elapsed
                    }
                }
                catch {
                    # WPF can expose its handle before an input queue is observable.
                }
            }

            if ($null -eq $startupReadyMs) {
                $logState = Find-StartupLogState `
                    -LogDirectory $Profile.logDirectory `
                    -ObservedLengths $observedLogLengths
                if ($logState.blocker) {
                    throw "App reported a startup blocker: $($logState.path)"
                }
                if ($logState.ready) {
                    $startupReadyMs = ConvertTo-Ms $stopwatch.Elapsed
                    $readyLog = $logState.path
                    $process.Refresh()
                    $peakWorkingSetAtReady = [long]$process.PeakWorkingSet64
                    $processorTimeAtReadyMs = ConvertTo-Ms $process.TotalProcessorTime
                }
            }

            if ($null -ne $startupReadyMs -and $null -ne $mainWindowHandleMs -and $null -ne $inputIdleMs) {
                break
            }
            Start-Sleep -Milliseconds 10
        }

        if ($null -eq $startupReadyMs) {
            throw "App did not reach startup_ready_operable within $StartupTimeoutSeconds seconds."
        }
        if ($null -eq $mainWindowHandleMs) {
            throw "App did not expose a main window handle within $StartupTimeoutSeconds seconds."
        }
        if ($null -eq $inputIdleMs) {
            throw "App did not reach input idle within $StartupTimeoutSeconds seconds."
        }

        if ($process.CloseMainWindow()) {
            $shutdownRequest = 'close_main_window'
        }
        elseif ([BeMusicSeekerDistributionBenchmarkWindowMessage]::PostMessage(
            $process.MainWindowHandle,
            0x0010,
            [IntPtr]::Zero,
            [IntPtr]::Zero)) {
            $shutdownRequest = 'wm_close_fallback'
        }
        else {
            throw 'App did not accept a graceful shutdown request.'
        }

        if (-not $process.WaitForExit(120000)) {
            throw 'App did not exit after graceful shutdown request.'
        }
        if ($process.ExitCode -ne 0) {
            throw "App shutdown returned exit code $($process.ExitCode)."
        }
    }
    catch {
        $failure = $_.Exception.ToString()
    }
    finally {
        Copy-RunLogs -LogDirectory $Profile.logDirectory -RawLogDirectory $RawLogDirectory
        if (-not $process.HasExited) {
            $process.Kill()
            $process.WaitForExit()
        }
        $exitCode = if ($process.HasExited) { $process.ExitCode } else { $null }
        $process.Dispose()
        $stopwatch.Stop()
    }

    $extractMetrics = Get-DirectoryMetrics -Path $Profile.extractRoot
    return [ordered]@{
        mainWindowHandleMs = $mainWindowHandleMs
        inputIdleMs = $inputIdleMs
        mainWindowReadyMs = if ($null -ne $inputIdleMs) { $inputIdleMs } else { $mainWindowHandleMs }
        startupReadyOperableMs = $startupReadyMs
        peakWorkingSetAtReadyBytes = $peakWorkingSetAtReady
        processorTimeAtReadyMs = $processorTimeAtReadyMs
        readyLog = $readyLog
        shutdownRequest = $shutdownRequest
        exitCode = $exitCode
        extractedBytes = [long]$extractMetrics.bytes
        extractedFiles = [int]$extractMetrics.files
        failure = $failure
    }
}

function Invoke-BenchmarkRun {
    param(
        [Parameter(Mandatory)][object]$Candidate,
        [Parameter(Mandatory)][ValidateSet('warmup', 'warm-cache', 'fresh-install', 'additional-warm-cache', 'additional-fresh-install')][string]$Phase,
        [Parameter(Mandatory)][int]$Round,
        [Parameter(Mandatory)][int]$Sequence,
        $WarmProfile,
        [switch]$VerifySemanticState
    )

    $isFresh = $Phase -in @('fresh-install', 'additional-fresh-install')
    if ($isFresh) {
        $profileRoot = Join-Path $OutputRoot "runs\$Phase\round-$('{0:D2}' -f $Round)\$($Candidate.name)"
        $extractRoot = Join-Path $OutputRoot "extract\$Phase\round-$('{0:D2}' -f $Round)\$($Candidate.name)"
        $profile = New-StandaloneProfile -Candidate $Candidate -ProfileRoot $profileRoot -ExtractRoot $extractRoot
    }
    else {
        $profile = $WarmProfile
    }

    $rawLogDirectory = Join-Path $OutputRoot "raw\$Phase\sequence-$('{0:D4}' -f $Sequence)-$($Candidate.name)-round-$('{0:D2}' -f $Round)"
    $beforeSemanticState = $null
    try {
        if ($VerifySemanticState) {
            $beforeSemanticState = Get-ProfileSemanticSnapshot -Profile $profile
        }
        $measurement = Invoke-MeasuredStartup -Candidate $Candidate -Profile $profile -RawLogDirectory $rawLogDirectory
    }
    catch {
        $measurement = [ordered]@{
            mainWindowHandleMs = $null
            inputIdleMs = $null
            mainWindowReadyMs = $null
            startupReadyOperableMs = $null
            peakWorkingSetAtReadyBytes = $null
            processorTimeAtReadyMs = $null
            readyLog = $null
            shutdownRequest = $null
            exitCode = $null
            extractedBytes = 0L
            extractedFiles = 0
            failure = $_.Exception.ToString()
        }
    }
    $semanticState = $null
    if ($null -eq $measurement.failure -and $VerifySemanticState) {
        try {
            $afterSemanticState = Get-ProfileSemanticSnapshot -Profile $profile
            Assert-ProfileSemanticSnapshotPreserved `
                -Before $beforeSemanticState `
                -After $afterSemanticState
            $afterBaselineSettings = [ordered]@{}
            $beforeBaselineSettings = [ordered]@{}
            foreach ($name in $beforeSemanticState.persistedSettings.Keys) {
                if ($name -eq 'AssemblyVersion' -and $beforeSemanticState.settingsSource -eq 'legacy') {
                    continue
                }
                $beforeBaselineSettings[$name] = $beforeSemanticState.persistedSettings[$name]
                $afterBaselineSettings[$name] = $afterSemanticState.persistedSettings[$name]
            }
            $semanticState = [ordered]@{
                preserved = $true
                beforeSettingsSource = $beforeSemanticState.settingsSource
                afterSettingsSource = $afterSemanticState.settingsSource
                persistedSettingCount = $beforeBaselineSettings.Count
                beforePersistedSettingsSha256 = Get-ObjectSha256 -Value $beforeBaselineSettings
                afterPersistedSettingsSha256 = Get-ObjectSha256 -Value $afterBaselineSettings
                databaseSemanticSha256 = Get-ObjectSha256 -Value $afterSemanticState.database
                databaseInventorySha256 = Get-ObjectSha256 -Value $afterSemanticState.databaseInventory
                legacyConfigSha256 = $afterSemanticState.legacyConfigSha256
                bmsFixtureSha256 = $afterSemanticState.bmsFixtureSha256
            }
        }
        catch {
            $measurement.failure = $_.Exception.ToString()
        }
    }
    elseif ($null -eq $measurement.failure) {
        $semanticState = [ordered]@{ verified = $false; reason = 'timed-run-does-not-preload-fixture' }
    }

    return [ordered]@{
        candidate = $Candidate.name
        phase = $Phase
        round = $Round
        sequence = $Sequence
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        mainWindowHandleMs = $measurement.mainWindowHandleMs
        inputIdleMs = $measurement.inputIdleMs
        mainWindowReadyMs = $measurement.mainWindowReadyMs
        startupReadyOperableMs = $measurement.startupReadyOperableMs
        peakWorkingSetAtReadyBytes = $measurement.peakWorkingSetAtReadyBytes
        processorTimeAtReadyMs = $measurement.processorTimeAtReadyMs
        extractedBytes = $measurement.extractedBytes
        extractedFiles = $measurement.extractedFiles
        readyLog = $measurement.readyLog
        shutdownRequest = $measurement.shutdownRequest
        exitCode = $measurement.exitCode
        semanticState = $semanticState
        failure = $measurement.failure
    }
}

function Invoke-RoundRobinPhase {
    param(
        [Parameter(Mandatory)][object[]]$Candidates,
        [Parameter(Mandatory)][string]$Phase,
        [Parameter(Mandatory)][int]$RunCount,
        [Parameter(Mandatory)][hashtable]$WarmProfiles,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [Collections.Generic.List[object]]$RunResults,
        [Parameter(Mandatory)][ref]$Sequence,
        [switch]$VerifySemanticState
    )

    for ($round = 1; $round -le $RunCount; $round++) {
        foreach ($candidate in Get-RotatedCandidates -Candidates $Candidates -Round $round) {
            $Sequence.Value++
            Write-Host "Benchmark $Phase round $round/$RunCount candidate=$($candidate.name)"
            $warmProfile = if ($WarmProfiles.ContainsKey($candidate.name)) { $WarmProfiles[$candidate.name] } else { $null }
            $result = Invoke-BenchmarkRun `
                -Candidate $candidate `
                -Phase $Phase `
                -Round $round `
                -Sequence $Sequence.Value `
                -WarmProfile $warmProfile `
                -VerifySemanticState:$VerifySemanticState
            $RunResults.Add($result)
        }
    }
}

function Assert-BenchmarkRunsSucceeded {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [Collections.IEnumerable]$Runs
    )

    foreach ($run in $Runs) {
        if ($null -ne $run.failure -or $null -eq $run.semanticState) {
            throw "Benchmark run failed: candidate=$($run.candidate) phase=$($run.phase) round=$($run.round) failure=$($run.failure)"
        }
    }
}

function Get-CandidateSummaries {
    param(
        [Parameter(Mandatory)][object[]]$Candidates,
        [Parameter(Mandatory)][Collections.Generic.List[object]]$RunResults
    )

    $summaries = [Collections.Generic.List[object]]::new()
    foreach ($candidate in $Candidates) {
        $allCandidateRuns = @($RunResults | Where-Object { $_.candidate -eq $candidate.name })
        $candidateRuns = @($allCandidateRuns | Where-Object { $_.phase -ne 'warmup' })
        $failedRuns = @(
            $allCandidateRuns |
                Where-Object { $null -ne $_.failure -or $null -eq $_.semanticState }
        )
        $phaseSummaries = [ordered]@{}
        foreach ($phaseName in @('warm-cache', 'fresh-install')) {
            $phases = if ($phaseName -eq 'warm-cache') {
                @('warm-cache', 'additional-warm-cache')
            }
            else {
                @('fresh-install', 'additional-fresh-install')
            }
            $runs = @($candidateRuns | Where-Object { $_.phase -in $phases -and $null -eq $_.failure })
            $ready = [double[]]@($runs | ForEach-Object { [double]$_.startupReadyOperableMs })
            $window = [double[]]@($runs | ForEach-Object { [double]$_.mainWindowReadyMs })
            $workingSet = [double[]]@($runs | ForEach-Object { [double]$_.peakWorkingSetAtReadyBytes })
            $cpu = [double[]]@($runs | ForEach-Object { [double]$_.processorTimeAtReadyMs })
            $phaseSummaries[$phaseName] = [ordered]@{
                successfulRuns = $runs.Count
                medianStartupReadyOperableMs = Get-Percentile -Values $ready -Percentile 0.5
                minStartupReadyOperableMs = if ($ready.Count -gt 0) { [Math]::Round([double](($ready | Measure-Object -Minimum).Minimum), 3) } else { $null }
                maxStartupReadyOperableMs = if ($ready.Count -gt 0) { [Math]::Round([double](($ready | Measure-Object -Maximum).Maximum), 3) } else { $null }
                medianMainWindowReadyMs = Get-Percentile -Values $window -Percentile 0.5
                medianPeakWorkingSetAtReadyBytes = Get-Percentile -Values $workingSet -Percentile 0.5
                medianProcessorTimeAtReadyMs = Get-Percentile -Values $cpu -Percentile 0.5
            }
        }

        $publishRoot = Join-Path (Join-Path $OutputRoot 'publish') $candidate.name
        $publishMetrics = Get-DirectoryMetrics -Path $publishRoot
        $viable = $failedRuns.Count -eq 0 -and
            $phaseSummaries['warm-cache'].successfulRuns -gt 0 -and
            $phaseSummaries['fresh-install'].successfulRuns -gt 0
        $selectionMetric = if ($viable) {
            [Math]::Max(
                [double]$phaseSummaries['warm-cache'].medianStartupReadyOperableMs,
                [double]$phaseSummaries['fresh-install'].medianStartupReadyOperableMs)
        }
        else {
            $null
        }
        $workingSetMetric = if ($viable) {
            [Math]::Max(
                [double]$phaseSummaries['warm-cache'].medianPeakWorkingSetAtReadyBytes,
                [double]$phaseSummaries['fresh-install'].medianPeakWorkingSetAtReadyBytes)
        }
        else {
            $null
        }

        $summaries.Add([ordered]@{
            name = $candidate.name
            family = $candidate.family
            publishSingleFile = $candidate.publishSingleFile
            nativeSelfExtract = $candidate.nativeSelfExtract
            readyToRun = $candidate.readyToRun
            viable = $viable
            failedRuns = $failedRuns.Count
            failureMessages = @($failedRuns | ForEach-Object { $_.failure } | Select-Object -Unique)
            publishBytes = [long]$publishMetrics.bytes
            publishFiles = [int]$publishMetrics.files
            phases = $phaseSummaries
            selectionMetricMs = $selectionMetric
            workingSetMetricBytes = $workingSetMetric
        })
    }
    return $summaries
}

function Get-PracticalStartupThresholdMs {
    param([Parameter(Mandatory)][double]$BaselineMs)

    return [Math]::Max(500.0, $BaselineMs * 0.10)
}

function Get-PracticalWorkingSetThresholdBytes {
    param([Parameter(Mandatory)][double]$BaselineBytes)

    return [Math]::Max(16MB, $BaselineBytes * 0.10)
}

function Test-PracticalDominates {
    param(
        [Parameter(Mandatory)]$Candidate,
        [Parameter(Mandatory)]$Other
    )

    $candidateWarm = [double]$Candidate.phases.'warm-cache'.medianStartupReadyOperableMs
    $otherWarm = [double]$Other.phases.'warm-cache'.medianStartupReadyOperableMs
    $warmThreshold = Get-PracticalStartupThresholdMs -BaselineMs ([Math]::Min($candidateWarm, $otherWarm))
    $candidateFresh = [double]$Candidate.phases.'fresh-install'.medianStartupReadyOperableMs
    $otherFresh = [double]$Other.phases.'fresh-install'.medianStartupReadyOperableMs
    $freshThreshold = Get-PracticalStartupThresholdMs -BaselineMs ([Math]::Min($candidateFresh, $otherFresh))
    $candidateWorkingSet = [double]$Candidate.workingSetMetricBytes
    $otherWorkingSet = [double]$Other.workingSetMetricBytes
    $workingSetThreshold = Get-PracticalWorkingSetThresholdBytes -BaselineBytes (
        [Math]::Min($candidateWorkingSet, $otherWorkingSet))

    $hasNoPracticalRegression =
        $candidateWarm -le $otherWarm + $warmThreshold -and
        $candidateFresh -le $otherFresh + $freshThreshold -and
        $candidateWorkingSet -le $otherWorkingSet + $workingSetThreshold
    $hasPracticalAdvantage =
        $candidateWarm + $warmThreshold -le $otherWarm -or
        $candidateFresh + $freshThreshold -le $otherFresh -or
        $candidateWorkingSet + $workingSetThreshold -le $otherWorkingSet
    return $hasNoPracticalRegression -and $hasPracticalAdvantage
}

function Get-AdditionalMeasurementDecision {
    param([Parameter(Mandatory)][object[]]$Summaries)

    $ranked = @(
        $Summaries |
            Where-Object { [bool]$_.viable } |
            Sort-Object { [double]$_.selectionMetricMs }
    )
    if ($ranked.Count -lt 2) {
        return [ordered]@{ required = $false; candidates = @(); phases = @(); reason = 'fewer-than-two-viable-candidates' }
    }
    $first = $ranked[0]
    $second = $ranked[1]
    $ambiguousPhases = [Collections.Generic.List[string]]::new()
    foreach ($phase in @('warm-cache', 'fresh-install')) {
        $firstMedian = [double]$first.phases.$phase.medianStartupReadyOperableMs
        $secondMedian = [double]$second.phases.$phase.medianStartupReadyOperableMs
        $baseline = [Math]::Min($firstMedian, $secondMedian)
        $threshold = Get-PracticalStartupThresholdMs -BaselineMs $baseline
        $difference = [Math]::Abs($firstMedian - $secondMedian)
        if ($difference -ge $threshold * 0.8 -and $difference -le $threshold * 1.2) {
            $ambiguousPhases.Add($phase)
        }
    }
    $required = $ambiguousPhases.Count -gt 0
    return [ordered]@{
        required = $required
        candidates = if ($required) { @($first.name, $second.name) } else { @() }
        phases = @($ambiguousPhases)
        reason = if ($required) {
            'top-candidates-near-practical-difference-boundary'
        }
        else {
            'additional-runs-would-not-change-practical-tier'
        }
    }
}

function Get-Recommendation {
    param(
        [Parameter(Mandatory)][object[]]$Summaries,
        [Parameter(Mandatory)]$AdditionalDecision
    )

    $ranked = @(
        $Summaries |
            Where-Object { [bool]$_.viable } |
            Sort-Object { [double]$_.selectionMetricMs }
    )
    if ($ranked.Count -eq 0) {
        return [ordered]@{ selected = $null; reason = 'no-viable-candidate' }
    }
    $bestWarm = [double]((
        $ranked |
            ForEach-Object { [double]$_.phases.'warm-cache'.medianStartupReadyOperableMs } |
            Measure-Object -Minimum
    ).Minimum)
    $bestFresh = [double]((
        $ranked |
            ForEach-Object { [double]$_.phases.'fresh-install'.medianStartupReadyOperableMs } |
            Measure-Object -Minimum
    ).Minimum)
    $bestWorkingSet = [double]((
        $ranked |
            ForEach-Object { [double]$_.workingSetMetricBytes } |
            Measure-Object -Minimum
    ).Minimum)
    $warmThresholdMs = Get-PracticalStartupThresholdMs -BaselineMs $bestWarm
    $freshThresholdMs = Get-PracticalStartupThresholdMs -BaselineMs $bestFresh
    $workingSetThresholdBytes = Get-PracticalWorkingSetThresholdBytes -BaselineBytes $bestWorkingSet
    $practicalTier = @(
        $ranked |
            Where-Object {
                [double]$_.phases.'warm-cache'.medianStartupReadyOperableMs -le $bestWarm + $warmThresholdMs -and
                [double]$_.phases.'fresh-install'.medianStartupReadyOperableMs -le $bestFresh + $freshThresholdMs -and
                [double]$_.workingSetMetricBytes -le $bestWorkingSet + $workingSetThresholdBytes
            }
    )
    $tradeoff = $false
    if ($practicalTier.Count -eq 0) {
        $tradeoff = $true
        $practicalTier = @(
            $ranked |
                Where-Object {
                    $candidate = $_
                    @(
                        $ranked |
                            Where-Object {
                                $_.name -ne $candidate.name -and
                                (Test-PracticalDominates -Candidate $_ -Other $candidate)
                            }
                    ).Count -eq 0
                }
        )
    }
    if ($practicalTier.Count -eq 1) {
        return [ordered]@{
            selected = $practicalTier[0].name
            reason = if ($tradeoff) {
                'only-nondominated-practical-tradeoff'
            }
            else {
                'clear-balanced-practical-advantage'
            }
            practicalTier = @($practicalTier.name)
            thresholds = [ordered]@{
                warmMs = $warmThresholdMs
                freshMs = $freshThresholdMs
                workingSetBytes = $workingSetThresholdBytes
            }
            additionalMeasurement = $AdditionalDecision
        }
    }
    if ($tradeoff) {
        return [ordered]@{
            selected = $null
            reason = 'fresh-warm-practical-tradeoff-requires-product-decision'
            practicalTier = @($practicalTier.name)
            thresholds = [ordered]@{
                warmMs = $warmThresholdMs
                freshMs = $freshThresholdMs
                workingSetBytes = $workingSetThresholdBytes
            }
            additionalMeasurement = $AdditionalDecision
        }
    }

    $preferred = @($practicalTier)
    $noExtract = @($preferred | Where-Object { -not [bool]$_.nativeSelfExtract })
    if ($noExtract.Count -gt 0) {
        $preferred = $noExtract
    }
    $bundle = @($preferred | Where-Object { [string]$_.family -eq 'bundle' })
    if ($bundle.Count -gt 0) {
        $preferred = $bundle
    }

    $r2r = @($preferred | Where-Object { [bool]$_.readyToRun })
    $il = @($preferred | Where-Object { -not [bool]$_.readyToRun })
    if ($r2r.Count -gt 0 -and $il.Count -gt 0) {
        $bestR2r = @($r2r | Sort-Object { [double]$_.selectionMetricMs })[0]
        $bestIl = @($il | Sort-Object { [double]$_.selectionMetricMs })[0]
        $startupRegressionLimit = Get-PracticalStartupThresholdMs -BaselineMs ([double]$bestIl.selectionMetricMs)
        $workingSetRegressionLimit = [Math]::Max(16MB, [double]$bestIl.workingSetMetricBytes * 0.10)
        if ([double]$bestR2r.selectionMetricMs -le [double]$bestIl.selectionMetricMs + $startupRegressionLimit -and
            [double]$bestR2r.workingSetMetricBytes -le [double]$bestIl.workingSetMetricBytes + $workingSetRegressionLimit) {
            $preferred = @($bestR2r)
        }
        else {
            $preferred = @($bestIl)
        }
    }
    elseif ($r2r.Count -gt 0) {
        $preferred = $r2r
    }

    $preferenceOrder = @('bundle-r2r', 'bundle-il', 'folder-r2r', 'folder-il', 'extract-r2r', 'extract-il')
    $selected = $null
    foreach ($name in $preferenceOrder) {
        $match = @($preferred | Where-Object { $_.name -eq $name })
        if ($match.Count -gt 0) {
            $selected = $match[0]
            break
        }
    }
    if ($null -eq $selected) {
        throw 'No candidate remained after applying the practical layout preferences.'
    }

    return [ordered]@{
        selected = $selected.name
        reason = 'practical-performance-equivalent-general-layout-preference'
        practicalTier = @($practicalTier.name)
        thresholds = [ordered]@{
            warmMs = $warmThresholdMs
            freshMs = $freshThresholdMs
            workingSetBytes = $workingSetThresholdBytes
        }
        preferences = @(
            'avoid-native-self-extract'
            'prefer-managed-bundle-when-equivalent'
            'prefer-r2r-without-practical-regression'
        )
        additionalMeasurement = $AdditionalDecision
    }
}

function Get-ReproductionCommand {
    $scriptPath = Join-Path $PSScriptRoot 'benchmark-net10-distribution.ps1'
    return @(
        'pwsh -NoProfile -NonInteractive -File'
        "`"$scriptPath`""
        '-OutputRoot'
        "`"$OutputRoot`""
        '-FixtureRoot'
        "`"$FixtureRoot`""
        '-SqliteAssemblyRoot'
        "`"$SqliteAssemblyRoot`""
        '-StartupTimeoutSeconds'
        $StartupTimeoutSeconds
        $(if ($SmokeOnly) { '-SmokeOnly' })
    ) -join ' '
}

$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$FixtureRoot = [IO.Path]::GetFullPath($FixtureRoot)
$SqliteAssemblyRoot = [IO.Path]::GetFullPath($SqliteAssemblyRoot)
. (Join-Path $PSScriptRoot 'accept-net10-existing-data.ps1') `
    -FixtureRoot $FixtureRoot `
    -SqliteAssemblyRoot $SqliteAssemblyRoot `
    -ImportFunctionsOnly

$script:manifest = Get-Content -LiteralPath (Join-Path $FixtureRoot 'fixture-manifest.json') -Raw | ConvertFrom-Json
if ([int]$script:manifest.schemaVersion -ne 1) {
    throw "Unsupported existing-data fixture schema: $($script:manifest.schemaVersion)"
}

if ($benchmarkImportFunctionsOnly) {
    return
}

Assert-BenchmarkOutputRootIsSafe -Path $OutputRoot -RepositoryRoot $repoRoot
if (Test-Path -LiteralPath $OutputRoot) {
    Remove-DirectoryWithRetry -Path $OutputRoot
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

$runResults = [Collections.Generic.List[object]]::new()
$sequence = 0
$failed = $false
$manifestServer = $null
try {
    $manifestServer = New-BenchmarkManifestServer
    $viableCandidates = if ($SmokeOnly) {
        @($candidateMatrix | Where-Object { $_.name -eq 'extract-r2r' })
    }
    else {
        @($candidateMatrix)
    }
    Publish-Candidates -Candidates $viableCandidates

    $warmProfiles = @{}
    foreach ($candidate in $viableCandidates) {
        $profileRoot = Join-Path $OutputRoot "runs\warm-cache\$($candidate.name)"
        $extractRoot = Join-Path $OutputRoot "extract\warm-cache\$($candidate.name)"
        $warmProfiles[$candidate.name] = New-StandaloneProfile `
            -Candidate $candidate `
            -ProfileRoot $profileRoot `
            -ExtractRoot $extractRoot
    }

    $warmupRunCount = 1
    $warmCacheRunCount = if ($SmokeOnly) { 1 } else { 3 }
    $freshInstallRunCount = if ($SmokeOnly) { 1 } else { 3 }
    Invoke-RoundRobinPhase `
        -Candidates $viableCandidates `
        -Phase 'warmup' `
        -RunCount $warmupRunCount `
        -WarmProfiles $warmProfiles `
        -RunResults $runResults `
        -Sequence ([ref]$sequence) `
        -VerifySemanticState:$SmokeOnly
    Invoke-RoundRobinPhase `
        -Candidates $viableCandidates `
        -Phase 'warm-cache' `
        -RunCount $warmCacheRunCount `
        -WarmProfiles $warmProfiles `
        -RunResults $runResults `
        -Sequence ([ref]$sequence)
    if (-not $SmokeOnly) {
        Invoke-RoundRobinPhase `
            -Candidates $viableCandidates `
            -Phase 'fresh-install' `
            -RunCount $freshInstallRunCount `
            -WarmProfiles $warmProfiles `
            -RunResults $runResults `
            -Sequence ([ref]$sequence)
    }

    Assert-BenchmarkRunsSucceeded -Runs $runResults
    $summaries = if ($SmokeOnly) {
        @()
    }
    else {
        Get-CandidateSummaries -Candidates $candidateMatrix -RunResults $runResults
    }
    $completedCandidateCount = if ($SmokeOnly) {
        1
    }
    else {
        @($summaries | Where-Object { [bool]$_.viable }).Count
    }
    if ($completedCandidateCount -eq 0) {
        throw 'No distribution candidate completed the base benchmark contract without a failure.'
    }
    $additionalDecision = if ($SmokeOnly) {
        [ordered]@{
            required = $false
            candidates = @()
            phases = @()
            reason = 'smoke-does-not-select-or-extend'
        }
    }
    else {
        Get-AdditionalMeasurementDecision -Summaries $summaries
    }
    if ($additionalDecision.required) {
        $additionalCandidates = @($candidateMatrix | Where-Object { $_.name -in $additionalDecision.candidates })
        foreach ($phase in @($additionalDecision.phases)) {
            Invoke-RoundRobinPhase `
                -Candidates $additionalCandidates `
                -Phase "additional-$phase" `
                -RunCount 2 `
                -WarmProfiles $warmProfiles `
                -RunResults $runResults `
                -Sequence ([ref]$sequence)
        }
        Assert-BenchmarkRunsSucceeded -Runs $runResults
        $summaries = Get-CandidateSummaries `
            -Candidates $candidateMatrix `
            -RunResults $runResults
    }

    $recommendation = if ($SmokeOnly) {
        $null
    }
    else {
        Get-Recommendation -Summaries $summaries -AdditionalDecision $additionalDecision
    }
    $report = [ordered]@{
        schemaVersion = 2
        status = 'passed'
        mode = if ($SmokeOnly) { 'smoke' } else { 'practical-comparison' }
        command = Get-ReproductionCommand
        outputRoot = $OutputRoot
        contract = $benchmarkContract
        environment = Get-BenchmarkEnvironment
        additionalMeasurement = $additionalDecision
        recommendation = $recommendation
        candidates = $summaries
        runs = $runResults
    }
    Export-RunCsv -RunResults $runResults -Path (Join-Path $OutputRoot 'runs.csv')
    Write-JsonNoBom -Path (Join-Path $OutputRoot 'report.json') -Value $report -Depth 20
    Write-Host "$(if ($SmokeOnly) { 'Distribution benchmark smoke passed' } else { 'Distribution benchmark comparison passed' }): $(Join-Path $OutputRoot 'report.json')"
}
catch {
    $failed = $true
    $failureReport = [ordered]@{
        schemaVersion = 2
        status = 'failed'
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        error = $_.Exception.ToString()
        environment = Get-BenchmarkEnvironment
        contract = $benchmarkContract
        runs = $runResults
    }
    Write-JsonNoBom -Path (Join-Path $OutputRoot 'report.json') -Value $failureReport -Depth 20
    try {
        Export-RunCsv -RunResults $runResults -Path (Join-Path $OutputRoot 'runs.csv')
    }
    catch {
        Write-Warning "Unable to export failed benchmark runs to CSV: $($_.Exception.Message)"
    }
    throw
}
finally {
    if ($null -ne $manifestServer) {
        $manifestServer.Dispose()
    }
    if ($failed) {
        Write-Host "Failed benchmark artifacts retained: $OutputRoot"
    }
}
