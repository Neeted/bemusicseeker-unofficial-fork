#requires -Version 7.0
# native変更時の追加検証。通常C#ビルドにはC++コンパイラーを要求しません。
[CmdletBinding()]
param([switch]$BuildPathIdentityFixture)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runner = Join-Path $repositoryRoot 'tools/BeMusicSeeker.Verify/bin/x64/Release/net10.0-windows/BeMusicSeeker.Verify.exe'
if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) {
    throw 'native追加検証にはReleaseのBeMusicSeeker.Verifyを先に構築してください。native/EverythingBridge/README.mdを参照してください。'
}
$runnerArguments = @('native-query')
if ($BuildPathIdentityFixture) { $runnerArguments += '--build-path-identity-fixture' }
Push-Location -LiteralPath $repositoryRoot
try {
    & $runner @runnerArguments
    $runnerExitCode = $LASTEXITCODE
}
finally { Pop-Location }
exit $runnerExitCode
