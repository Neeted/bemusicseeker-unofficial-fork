[CmdletBinding()]
param(
    [ValidateSet('Quick', 'Full')]
    [string]$Mode = 'Quick',

    [string]$TestFilter
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'BeMusicSeeker.sln'

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)]
        [string]$Command,

        [Parameter(ValueFromRemainingArguments)]
        [string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $Command $($Arguments -join ' ')"
    }
}

Push-Location $repoRoot
try {
    if ($Mode -eq 'Full') {
        Invoke-CheckedCommand dotnet restore $solution
        Invoke-CheckedCommand dotnet tool restore
    }

    Invoke-CheckedCommand dotnet build $solution '/p:Configuration=Release' '--no-restore'

    $testArguments = @('test', $solution, '/p:Configuration=Release', '--no-build', '--no-restore')
    if ($Mode -eq 'Quick' -and -not [string]::IsNullOrWhiteSpace($TestFilter)) {
        $testArguments += @('--filter', $TestFilter)
    }
    Invoke-CheckedCommand dotnet @testArguments

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
