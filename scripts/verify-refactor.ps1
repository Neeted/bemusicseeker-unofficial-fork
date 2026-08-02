[CmdletBinding()]
param(
    [ValidateSet('Quick', 'Full')]
    [string]$Mode = 'Quick',

    [string]$TestFilter
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'BeMusicSeeker.sln'
$uiExecutable = Join-Path $repoRoot 'bin\x64\Release\net10.0-windows\BeMusicSeeker.exe'
$toolProjects = @(
    (Join-Path $repoRoot 'tools\chart-info-compare\ChartInfoCompare.csproj'),
    (Join-Path $repoRoot 'tools\chart-info-export\ChartInfoExport.csproj'))
$toolExecutables = @(
    (Join-Path $repoRoot 'tools\chart-info-compare\bin\x64\Release\net10.0\ChartInfoCompare.exe'),
    (Join-Path $repoRoot 'tools\chart-info-export\bin\x64\Release\net10.0\ChartInfoExport.exe'))
$verificationArtifactsDirectory = Join-Path $repoRoot 'artifacts\verification'
$scdPublishRoot = Join-Path $repoRoot 'artifacts\publish'
$scdAppPublishOutput = Join-Path $scdPublishRoot 'app'
$scdUpdaterPublishOutput = Join-Path $scdPublishRoot 'updater'
$existingDataAcceptanceScript = Join-Path $repoRoot 'scripts\accept-net10-existing-data.ps1'
$updateAcceptanceScript = Join-Path $repoRoot 'scripts\accept-net10-update.ps1'
$testTimeoutSeconds = 180
# Each test host already uses class-level parallelism. Bounding concurrent hosts
# prevents their worker pools from oversubscribing the machine under Full load.
$maximumConcurrentFullTestShards = 2
$isolatedFullTestClassShards = @(
    [pscustomobject]@{
        Name = 'library-sync'
        RunSeparately = $true
        Classes = @(
            'BeMusicSeeker.Tests.BmsLibraryLr2SongDbSyncTests',
            'BeMusicSeeker.Tests.BmsLibraryInitializationServiceTests',
            'BeMusicSeeker.Tests.ChartInfoMetadataTests',
            'BeMusicSeeker.Tests.BmsLibraryZeroNoteRefreshTests')
    },
    [pscustomobject]@{
        Name = 'presentation-workspace'
        RunSeparately = $false
        Classes = @(
            'BeMusicSeeker.Tests.BmsPlaylistUpdateTests',
            'BeMusicSeeker.Tests.PlaybackPanelViewModelTests',
            'BeMusicSeeker.Tests.PlaylistWorkspaceViewModelTests',
            'BeMusicSeeker.Tests.LibraryFolderTreeViewModelTests')
    },
    [pscustomobject]@{
        Name = 'catalog-maintenance'
        RunSeparately = $false
        Classes = @(
            'BeMusicSeeker.Tests.BmsLibraryFolderRenameRefreshTests',
            'BeMusicSeeker.Tests.BmsLibraryPendingPackageRegroupTests',
            'BeMusicSeeker.Tests.OwnedChartCollectionStateTests',
            'BeMusicSeeker.Tests.AppSchemaPreflightServiceTests',
            'BeMusicSeeker.Tests.BmsLibraryMaintenanceServiceTests',
            'BeMusicSeeker.Tests.BmsLibraryIrServiceTests')
    })
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

function Write-TestProcessOutput {
    param(
        [Parameter(Mandatory)]
        [string]$StandardOutputPath,

        [Parameter(Mandatory)]
        [string]$StandardErrorPath
    )

    if (Test-Path -LiteralPath $StandardOutputPath -PathType Leaf) {
        $standardOutput = Get-Content -LiteralPath $StandardOutputPath -Raw
        if (-not [string]::IsNullOrEmpty($standardOutput)) {
            Write-Host $standardOutput -NoNewline
        }
    }

    if (Test-Path -LiteralPath $StandardErrorPath -PathType Leaf) {
        $standardError = Get-Content -LiteralPath $StandardErrorPath -Raw
        if (-not [string]::IsNullOrEmpty($standardError)) {
            Write-Error $standardError -ErrorAction Continue
        }
    }
}

function Invoke-MonitoredTestCommand {
    param(
        [Parameter(Mandatory)]
        [string]$CommandPath,

        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$WorkingDirectory,

        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory
    )

    $standardOutputPath = Join-Path $DiagnosticsDirectory 'stdout.log'
    $standardErrorPath = Join-Path $DiagnosticsDirectory 'stderr.log'
    $process = $null
    $timedOut = $false
    $exitCode = $null

    try {
        $process = Start-Process `
            -FilePath $CommandPath `
            -ArgumentList $Arguments `
            -WorkingDirectory $WorkingDirectory `
            -RedirectStandardOutput $standardOutputPath `
            -RedirectStandardError $standardErrorPath `
            -PassThru

        if (-not $process.WaitForExit($testTimeoutSeconds * 1000)) {
            $timedOut = $true
            Write-Warning "dotnet test did not return within $testTimeoutSeconds seconds. Stopping the test process tree."
            & taskkill.exe /PID $process.Id /T /F 2>$null | Out-Null
        }
        else {
            $exitCode = $process.ExitCode
        }
    }
    finally {
        if ($null -ne $process) {
            $process.Dispose()
        }
    }

    Write-TestProcessOutput -StandardOutputPath $standardOutputPath -StandardErrorPath $standardErrorPath

    if ($timedOut) {
        throw "dotnet test exceeded the $testTimeoutSeconds-second response timeout. Test output: $DiagnosticsDirectory"
    }

    if ($exitCode -ne 0) {
        throw "dotnet test failed with exit code $exitCode. Test output: $DiagnosticsDirectory"
    }
}

function Invoke-MonitoredFullTestCommands {
    param(
        [Parameter(Mandatory)]
        [string]$CommandPath,

        [Parameter(Mandatory)]
        [string[]]$CommonArguments,

        [Parameter(Mandatory)]
        [string]$WorkingDirectory,

        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory
    )

    $isolatedFullTestClasses = @(
        $isolatedFullTestClassShards |
            ForEach-Object { $_.Classes })
    $remainingFilter = ($isolatedFullTestClasses |
        ForEach-Object { "FullyQualifiedName!~$_" }) -join '&'

    foreach ($shard in ($isolatedFullTestClassShards |
        Where-Object { $_.RunSeparately })) {
        $shardDirectory = Join-Path $DiagnosticsDirectory $shard.Name
        [void](New-Item -ItemType Directory -Path $shardDirectory -Force)
        $arguments = $CommonArguments + @(
            '--results-directory',
            $shardDirectory,
            '--filter',
            (($shard.Classes |
                ForEach-Object { "FullyQualifiedName~$_" }) -join '|'))
        Write-Host "Test shard: $($shard.Name) (dedicated)"
        Invoke-MonitoredTestCommand `
            -CommandPath $CommandPath `
            -Arguments $arguments `
            -WorkingDirectory $WorkingDirectory `
            -DiagnosticsDirectory $shardDirectory
    }

    $shards = @(
        [pscustomobject]@{ Name = 'remaining'; Filter = $remainingFilter })
    $shards += @(
        $isolatedFullTestClassShards |
            Where-Object { -not $_.RunSeparately } |
            ForEach-Object {
                [pscustomobject]@{
                    Name = $_.Name
                    Filter = ($_.Classes |
                        ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
                }
            })
    for ($waveStart = 0;
        $waveStart -lt $shards.Count;
        $waveStart += $maximumConcurrentFullTestShards) {
        $waveEnd = [Math]::Min(
            $waveStart + $maximumConcurrentFullTestShards,
            $shards.Count)
        $waveShards = @($shards[$waveStart..($waveEnd - 1)])
        Write-Host "Test shard wave: $(($waveShards.Name) -join ', ')"

        $processes = @()
        $timedOut = $false
        $launchFailure = $null
        $failedShard = $null
        $cleanupFailures = [System.Collections.Generic.List[string]]::new()

        try {
            foreach ($shard in $waveShards) {
                $shardDirectory = Join-Path $DiagnosticsDirectory $shard.Name
                [void](New-Item -ItemType Directory -Path $shardDirectory -Force)
                $standardOutputPath = Join-Path $shardDirectory 'stdout.log'
                $standardErrorPath = Join-Path $shardDirectory 'stderr.log'
                $arguments = $CommonArguments + @(
                    '--results-directory',
                    $shardDirectory,
                    '--filter',
                    $shard.Filter)
                $process = Start-Process `
                    -FilePath $CommandPath `
                    -ArgumentList $arguments `
                    -WorkingDirectory $WorkingDirectory `
                    -RedirectStandardOutput $standardOutputPath `
                    -RedirectStandardError $standardErrorPath `
                    -PassThru
                $processes += [pscustomobject]@{
                    Name = $shard.Name
                    Process = $process
                    StandardOutputPath = $standardOutputPath
                    StandardErrorPath = $standardErrorPath
                }
            }

            $waitTasks = [System.Threading.Tasks.Task[]]@(
                $processes | ForEach-Object { $_.Process.WaitForExitAsync() })
            if (-not [System.Threading.Tasks.Task]::WaitAll(
                $waitTasks,
                $testTimeoutSeconds * 1000)) {
                $timedOut = $true
                $timedOutShardNames = @(
                    $processes |
                        Where-Object { -not $_.Process.HasExited } |
                        ForEach-Object { $_.Name })
                $timeoutMessage =
                    "dotnet test shards did not return within $testTimeoutSeconds seconds. " +
                    "Stopping: " +
                    ($timedOutShardNames -join ', ')
                Write-Warning $timeoutMessage
            }
        }
        catch {
            $launchFailure = $_
        }
        finally {
            foreach ($entry in $processes) {
                try {
                    if (($timedOut -or $null -ne $launchFailure) -and
                        -not $entry.Process.HasExited) {
                        try {
                            $entry.Process.Kill($true)
                        }
                        catch {
                            $cleanupFailures.Add(
                                "$($entry.Name): process-tree termination failed: $($_.Exception.Message)")
                        }
                    }
                    try {
                        if (-not $entry.Process.WaitForExit(10000)) {
                            $cleanupFailures.Add(
                                "$($entry.Name): process did not exit within the 10-second cleanup window")
                        }
                    }
                    catch {
                        $cleanupFailures.Add(
                            "$($entry.Name): exit observation failed: $($_.Exception.Message)")
                    }
                    Write-Host "Test shard: $($entry.Name)"
                    Write-TestProcessOutput `
                        -StandardOutputPath $entry.StandardOutputPath `
                        -StandardErrorPath $entry.StandardErrorPath
                    if (-not $timedOut -and
                        $null -eq $launchFailure -and
                        $null -eq $failedShard -and
                        $entry.Process.HasExited -and
                        $entry.Process.ExitCode -ne 0) {
                        $failedShard = [pscustomobject]@{
                            Name = $entry.Name
                            ExitCode = $entry.Process.ExitCode
                        }
                    }
                }
                catch {
                    $cleanupFailures.Add(
                        "$($entry.Name): cleanup/output collection failed: $($_.Exception.Message)")
                }
                finally {
                    try {
                        $entry.Process.Dispose()
                    }
                    catch {
                        $cleanupFailures.Add(
                            "$($entry.Name): process disposal failed: $($_.Exception.Message)")
                    }
                }
            }
        }
        if ($cleanupFailures.Count -gt 0) {
            throw "Test shard cleanup failed: $($cleanupFailures -join '; ')"
        }
        if ($null -ne $launchFailure) {
            throw $launchFailure
        }
        if ($timedOut) {
            throw "dotnet test exceeded the $testTimeoutSeconds-second response timeout. Test output: $DiagnosticsDirectory"
        }
        if ($null -ne $failedShard) {
            throw "dotnet test shard '$($failedShard.Name)' failed with exit code $($failedShard.ExitCode). Test output: $DiagnosticsDirectory"
        }
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

Push-Location $repoRoot
try {
    if ($Mode -eq 'Full') {
        Invoke-CheckedCommand dotnet restore $solution '-r' 'win-x64' '--locked-mode' '-p:PublishReadyToRun=true'
        Invoke-CheckedCommand dotnet tool restore
    }

    # Build, format, and analyzer commands run to completion. Only dotnet test
    # has the simple 180-second command-response timeout described above.
    Invoke-CheckedCommand dotnet build $solution '/p:Configuration=Release' '/p:Platform=x64' '--no-restore'

    foreach ($toolProject in $toolProjects) {
        Invoke-CheckedCommand dotnet build $toolProject '/p:Configuration=Release' '/p:Platform=x64' '--no-restore'
    }

    if (-not (Test-Path -LiteralPath $uiExecutable -PathType Leaf)) {
        throw "Release UI executable was not produced: $uiExecutable"
    }

    foreach ($toolExecutable in $toolExecutables) {
        if (-not (Test-Path -LiteralPath $toolExecutable -PathType Leaf)) {
            throw "Release tool executable was not produced: $toolExecutable"
        }

        Invoke-CheckedCommand $toolExecutable '--help'
    }

    if ($Mode -eq 'Full') {
        Write-Host "Self-contained publish verification output: $scdPublishRoot"
        Invoke-SelfContainedPublishVerification
        Invoke-ExistingDataAcceptance
        Invoke-UpdateAcceptance
        $env:BMS_SCD_APP_PUBLISH_ROOT = $scdAppPublishOutput
        $env:BMS_SCD_UPDATER_PUBLISH_ROOT = $scdUpdaterPublishOutput
    }

    $resolvedUiExecutable = (Resolve-Path -LiteralPath $uiExecutable).Path
    Write-Host "Release UI executable: $resolvedUiExecutable"
    Assert-ReleaseOutputLayout -ExecutablePath $resolvedUiExecutable

    $testDiagnosticsDirectory = Join-Path $verificationArtifactsDirectory (
        'tests-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    [void](New-Item -ItemType Directory -Path $testDiagnosticsDirectory -Force)

    $testArguments = @(
        'test',
        $solution,
        '/p:Configuration=Release',
        '/p:Platform=x64',
        '--no-build',
        '--no-restore',
        '--blame')
    if ($Mode -eq 'Quick' -and -not [string]::IsNullOrWhiteSpace($TestFilter)) {
        $testArguments += @('--filter', $TestFilter)
    }

    if ([string]::IsNullOrWhiteSpace($TestFilter)) {
        Invoke-MonitoredFullTestCommands `
            -CommandPath 'dotnet' `
            -CommonArguments $testArguments `
            -WorkingDirectory $repoRoot `
            -DiagnosticsDirectory $testDiagnosticsDirectory
    }
    else {
        $testArguments += @(
            '--results-directory',
            $testDiagnosticsDirectory)
        Invoke-MonitoredTestCommand `
            -CommandPath 'dotnet' `
            -Arguments $testArguments `
            -WorkingDirectory $repoRoot `
            -DiagnosticsDirectory $testDiagnosticsDirectory
    }

    Remove-Item Env:BMS_SCD_APP_PUBLISH_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:BMS_SCD_UPDATER_PUBLISH_ROOT -ErrorAction SilentlyContinue

    Invoke-CheckedCommand dotnet format whitespace $solution '--verify-no-changes' '--no-restore' '--verbosity' 'minimal'

    if ($Mode -eq 'Full') {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (-not (Test-Path -LiteralPath $vswhere)) {
            throw "vswhere.exe was not found: $vswhere"
        }

        $msbuildPath = & $vswhere -version '[17.0,18.0)' -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin' | Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($msbuildPath)) {
            throw 'Visual Studio 2022 MSBuild 17 was not found.'
        }

        Invoke-CheckedCommand dotnet roslynator analyze $solution '--msbuild-path' $msbuildPath '--properties' 'Configuration=Release' '--severity-level' 'warning' '--ignore-compiler-diagnostics' '--verbosity' 'minimal'
    }

    Invoke-CheckedCommand git diff '--check' 'HEAD' '--'

    $untrackedFiles = @(git ls-files --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to enumerate untracked files (exit code $LASTEXITCODE)."
    }

    if ($untrackedFiles.Count -gt 0) {
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
                # `git diff --no-index` returns 1 when the expected comparison
                # contains differences. Do not leak that success-path code to
                # callers that invoke this script in the current pwsh process.
                $global:LASTEXITCODE = 0
            }
        }
        finally {
            Remove-Item -LiteralPath $emptyFile.FullName -Force
        }
    }
}
finally {
    Pop-Location
}
