param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "EverythingBridge.vcxproj"
if (!(Test-Path $project)) {
    throw "Project not found: $project"
}

$vswhere = Join-Path "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer" "vswhere.exe"
if (!(Test-Path $vswhere)) {
    throw "vswhere.exe not found. Please install Visual Studio Build Tools."
}

$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($msbuild)) {
    throw "MSBuild.exe not found."
}

& $msbuild $project /t:Build /p:Configuration=$Configuration /p:Platform=x64 /m

$output = Join-Path (Join-Path $PSScriptRoot "..") "EverythingBridge_x64.dll"
if (Test-Path $output) {
    Write-Host "Built: $output"
} else {
    throw "Build completed but output was not found: $output"
}
