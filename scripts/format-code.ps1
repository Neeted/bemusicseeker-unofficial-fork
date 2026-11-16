[CmdletBinding()]
param(
    [ValidateSet('Changed', 'All')]
    [string]$Scope = 'Changed',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$runner = Join-Path $repositoryRoot 'tools/BeMusicSeeker.Verify/bin/x64/Release/net10.0-windows/BeMusicSeeker.Verify.exe'
if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) {
    throw '手動整形にはReleaseのBeMusicSeeker.Verifyを先に構築してください。tools/BeMusicSeeker.Verify/README.mdを参照してください。'
}

# 引数は配列のまま渡し、外部の作業場所からもこのrepositoryを選びます。
$normalizedScope = if ($Scope -eq 'All') { 'All' } else { 'Changed' }
$runnerArguments = @('format', '--scope', $normalizedScope)
if ($NoRestore) { $runnerArguments += '--no-restore' }
Push-Location -LiteralPath $repositoryRoot
try {
    & $runner @runnerArguments
    $runnerExitCode = $LASTEXITCODE
}
finally { Pop-Location }
exit $runnerExitCode
