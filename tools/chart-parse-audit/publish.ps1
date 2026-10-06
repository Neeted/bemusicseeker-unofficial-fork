#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Out = '',
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
if ($Configuration -ne 'Release') { throw '比較パッケージはReleaseで作成します。' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if ([string]::IsNullOrWhiteSpace($Out)) { $Out = Join-Path $repo 'artifacts/chart-parse-audit/publish' }
$Out = [IO.Path]::GetFullPath($Out)
if ((Test-Path -LiteralPath $Out) -and @(Get-ChildItem -LiteralPath $Out -Force).Count -gt 0) { throw '出力先は空である必要があります。' }
$null = New-Item -ItemType Directory -Path $Out -Force
$package = Join-Path $Out 'chart-parse-audit-win-x64'
$null = New-Item -ItemType Directory -Path $package

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $($Arguments -join ' ')" }
}
$baselinePath = Join-Path $PSScriptRoot 'Legacy/baseline-manifest.json'
$runnerProject = Join-Path $PSScriptRoot 'Runner/ChartParseAudit.csproj'
Invoke-DotNet @('restore', $runnerProject, '--locked-mode', '-r', 'win-x64')
Invoke-DotNet @('publish', $runnerProject, '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true', '--no-restore', '-o', $package)
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.ja.md') -Destination $package
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'analyze.py') -Destination $package
Copy-Item -LiteralPath $baselinePath -Destination $package
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Legacy/connection.patch') -Destination $package
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $package 'LICENSE-BeMusicSeeker')
$licenses = Join-Path $package 'licenses'
$null = New-Item -ItemType Directory -Path $licenses
Copy-Item -LiteralPath (Join-Path $repo 'third_party/licenses') -Destination (Join-Path $licenses 'bundled-components') -Recurse
$packages = [Collections.Generic.HashSet[string]]::new()
foreach ($project in @('Runner', 'Legacy', 'Current', 'Shared')) {
    $assets = Get-Content -LiteralPath (Join-Path $PSScriptRoot "$project/obj/project.assets.json") -Raw | ConvertFrom-Json -AsHashtable
    foreach ($name in $assets.libraries.Keys) {
        $library = $assets.libraries[$name]
        if ($library.type -ne 'package' -or $packages.Contains($name)) { continue }
        foreach ($cache in $assets.packageFolders.Keys) {
            $directory = Join-Path $cache $library.path
            if (Test-Path -LiteralPath $directory) {
                $target = Join-Path $licenses ($name.Replace('/', '-'))
                $null = New-Item -ItemType Directory -Path $target
                foreach ($file in @(Get-ChildItem -LiteralPath $directory -File -Recurse | Where-Object { $_.Name -match '^(LICENSE|LICENCE|COPYING|NOTICE)' -or $_.Extension -eq '.nuspec' })) {
                    $relative = [IO.Path]::GetRelativePath($directory, $file.FullName)
                    $destination = Join-Path $target $relative
                    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
                    Copy-Item -LiteralPath $file.FullName -Destination $destination
                }
                $null = $packages.Add($name)
                break
            }
        }
    }
}
Compress-Archive -LiteralPath $package -DestinationPath (Join-Path $Out 'chart-parse-audit-win-x64.zip')
Write-Output $package
