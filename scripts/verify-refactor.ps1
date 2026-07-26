[CmdletBinding()]
param(
    [ValidateSet('Quick', 'Full')]
    [string]$Mode = 'Quick',

    [string]$TestFilter
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'BeMusicSeeker.sln'
$uiExecutable = Join-Path $repoRoot 'bin\x64\Release\net472\BeMusicSeeker.exe'
$verificationArtifactsDirectory = Join-Path $repoRoot 'artifacts\verification'
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
    $updaterExecutable = Join-Path $outputDirectory 'BeMusicSeeker.Updater.exe'
    if (-not (Test-Path -LiteralPath $updaterExecutable -PathType Leaf)) {
        throw "Release output updater executable was not produced: $updaterExecutable"
    }

    $libsUpdaterExecutable = Join-Path (Join-Path $outputDirectory 'libs') 'BeMusicSeeker.Updater.exe'
    if (Test-Path -LiteralPath $libsUpdaterExecutable) {
        throw "Release output updater executable must remain at the deployment root: $libsUpdaterExecutable"
    }

    $rootManagedAssemblies = @(Get-ChildItem -LiteralPath $outputDirectory -Filter '*.dll' -File -ErrorAction SilentlyContinue)
    if ($rootManagedAssemblies.Count -gt 0) {
        $names = $rootManagedAssemblies | Select-Object -ExpandProperty Name
        throw "Release output contains managed assemblies outside libs: $($names -join ', ')"
    }

    $managedDependencyDirectory = Join-Path $outputDirectory 'libs'
    if (-not (Test-Path -LiteralPath $managedDependencyDirectory -PathType Container)) {
        throw "Release output managed dependency directory was not produced: $managedDependencyDirectory"
    }

    foreach ($managedDependencyName in @(
        'Livet.dll',
        'Newtonsoft.Json.dll',
        'SevenZipExtractor.dll',
        'OggVorbis.NET64.dll')) {
        $managedDependencyPath = Join-Path $managedDependencyDirectory $managedDependencyName
        if (-not (Test-Path -LiteralPath $managedDependencyPath -PathType Leaf)) {
            throw "Release output managed dependency is missing from libs: $managedDependencyPath"
        }
    }

    foreach ($legacyDirectoryName in @('x86', 'x64')) {
        $legacyDirectory = Join-Path $outputDirectory $legacyDirectoryName
        if (Test-Path -LiteralPath $legacyDirectory) {
            throw "Release output contains legacy native directory: $legacyDirectory"
        }
    }

    $legacyManagedNativeDirectory = Join-Path $managedDependencyDirectory 'x86'
    if (Test-Path -LiteralPath $legacyManagedNativeDirectory) {
        throw "Release output contains legacy managed native directory: $legacyManagedNativeDirectory"
    }
}

Push-Location $repoRoot
try {
    if ($Mode -eq 'Full') {
        Invoke-CheckedCommand dotnet restore $solution
        Invoke-CheckedCommand dotnet tool restore
    }

    # Build, format, and analyzer commands run to completion. Only dotnet test
    # has the simple 300-second command-response timeout described above.
    Invoke-CheckedCommand dotnet build $solution '/p:Configuration=Release' '/p:Platform=x64' '--no-restore'

    if (-not (Test-Path -LiteralPath $uiExecutable -PathType Leaf)) {
        throw "Release UI smoke executable was not produced: $uiExecutable"
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

        Invoke-CheckedCommand dotnet roslynator analyze $solution '--msbuild-path' $msbuildPath '--properties' 'Configuration=Release' '--severity-level' 'warning' '--verbosity' 'minimal'
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
                $checkOutput = @(& git -c core.autocrlf=false diff --no-index --check -- $emptyFile.FullName $untrackedFile 2>&1)
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
