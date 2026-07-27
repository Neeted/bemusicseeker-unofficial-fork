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
$testTimeoutSeconds = 300

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

function Invoke-SelfContainedPublishSmoke {
    if (Test-Path -LiteralPath $scdPublishRoot) {
        Remove-Item -LiteralPath $scdPublishRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $scdAppPublishOutput, $scdUpdaterPublishOutput -Force | Out-Null

    Invoke-CheckedCommand dotnet publish (Join-Path $repoRoot 'BeMusicSeeker.csproj') '/p:Configuration=Release' '/p:Platform=x64' '-r' 'win-x64' '--self-contained' 'true' '--no-restore' '-p:PublishProfile=WinX64SelfContained' "-p:PublishDir=$scdAppPublishOutput"
    Invoke-CheckedCommand dotnet publish (Join-Path $repoRoot 'BeMusicSeeker.Updater\BeMusicSeeker.Updater.csproj') '/p:Configuration=Release' '/p:Platform=x64' '-r' 'win-x64' '--self-contained' 'true' '--no-restore' '-p:PublishProfile=WinX64SelfContainedSingleFile' "-p:PublishDir=$scdUpdaterPublishOutput"

    foreach ($requiredPath in @(
        'BeMusicSeeker.exe',
        'BeMusicSeeker.runtimeconfig.json',
        'e_sqlite3.dll',
        'native\Everything3_x64.dll',
        'native\EverythingBridge_x64.dll',
        'libs\x64\7z.dll',
        'libs\x64\bass.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $scdAppPublishOutput $requiredPath) -PathType Leaf)) {
            throw "Self-contained app publish output is missing: $requiredPath"
        }
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

    $appExecutable = Join-Path $scdAppPublishOutput 'BeMusicSeeker.exe'
    $appProcess = Start-Process -FilePath $appExecutable -WorkingDirectory $scdAppPublishOutput -PassThru
    try {
        [void]$appProcess.WaitForInputIdle(30000)
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            Start-Sleep -Milliseconds 250
            $appProcess.Refresh()
        } while ($appProcess.MainWindowHandle -eq 0 -and -not $appProcess.HasExited -and [DateTime]::UtcNow -lt $deadline)
        if ($appProcess.HasExited) {
            throw "Self-contained app exited before UI smoke completed (exit code $($appProcess.ExitCode))."
        }
        if ($appProcess.MainWindowHandle -eq 0) {
            throw 'Self-contained app did not expose a main window.'
        }
        $appProcess.CloseMainWindow() | Out-Null
        if (-not $appProcess.WaitForExit(30000)) {
            throw 'Self-contained app did not exit after UI smoke close.'
        }
        if ($appProcess.ExitCode -ne 0) {
            throw "Self-contained app UI smoke failed with exit code $($appProcess.ExitCode)."
        }
    }
    finally {
        if (-not $appProcess.HasExited) {
            $appProcess.Kill()
        }
        $appProcess.Dispose()
    }
}

Push-Location $repoRoot
try {
    if ($Mode -eq 'Full') {
        Invoke-CheckedCommand dotnet restore $solution '-r' 'win-x64' '--locked-mode'
        Invoke-CheckedCommand dotnet tool restore
    }

    # Build, format, and analyzer commands run to completion. Only dotnet test
    # has the simple 300-second command-response timeout described above.
    Invoke-CheckedCommand dotnet build $solution '/p:Configuration=Release' '/p:Platform=x64' '--no-restore'

    foreach ($toolProject in $toolProjects) {
        Invoke-CheckedCommand dotnet build $toolProject '/p:Configuration=Release' '/p:Platform=x64' '--no-restore'
    }

    if (-not (Test-Path -LiteralPath $uiExecutable -PathType Leaf)) {
        throw "Release UI smoke executable was not produced: $uiExecutable"
    }

    foreach ($toolExecutable in $toolExecutables) {
        if (-not (Test-Path -LiteralPath $toolExecutable -PathType Leaf)) {
            throw "Release tool executable was not produced: $toolExecutable"
        }

        Invoke-CheckedCommand $toolExecutable '--help'
    }

    if ($Mode -eq 'Full') {
        Write-Host "Self-contained publish smoke output: $scdPublishRoot"
        Invoke-SelfContainedPublishSmoke
        $env:BMS_SCD_APP_PUBLISH_ROOT = $scdAppPublishOutput
        $env:BMS_SCD_UPDATER_PUBLISH_ROOT = $scdUpdaterPublishOutput
    }

    $resolvedUiExecutable = (Resolve-Path -LiteralPath $uiExecutable).Path
    Write-Host "Release UI smoke executable: $resolvedUiExecutable"
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
        '--results-directory',
        $testDiagnosticsDirectory,
        '--blame')
    if ($Mode -eq 'Quick' -and -not [string]::IsNullOrWhiteSpace($TestFilter)) {
        $testArguments += @('--filter', $TestFilter)
    }

    Invoke-MonitoredTestCommand `
        -CommandPath 'dotnet' `
        -Arguments $testArguments `
        -WorkingDirectory $repoRoot `
        -DiagnosticsDirectory $testDiagnosticsDirectory

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
