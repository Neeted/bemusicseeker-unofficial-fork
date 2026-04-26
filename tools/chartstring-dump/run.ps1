[CmdletBinding(PositionalBinding = $false)]
param(
    [string] $JavaHome,
    [string] $JavaRuntimeHome,
    [string] $SongDataUpdaterRoot = "C:\Users\kazuk\IdeaProjects\songdata-updater",
    [string] $OutputDir = "artifacts\chartstring-dump",

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $ChartArgs
)

$ErrorActionPreference = "Stop"

function Resolve-JavaTool([string] $toolName) {
    $candidates = @()
    if ($JavaHome) {
        $candidates += Join-Path $JavaHome "bin\$toolName.exe"
    }
    if ($env:JAVA_HOME) {
        $candidates += Join-Path $env:JAVA_HOME "bin\$toolName.exe"
    }
    $command = Get-Command $toolName -ErrorAction SilentlyContinue
    if ($command) {
        $candidates += $command.Source
    }
    $knownRoots = @(
        "C:\Users\kazuk\.jdks\ms-17.0.16",
        "C:\Users\kazuk\.jdks\corretto-23.0.2",
        "C:\Users\kazuk\.jdks\corretto-24.0.2",
        "C:\Users\kazuk\.jdks\openjdk-24.0.2",
        "C:\Program Files\JetBrains\IntelliJ IDEA Community Edition 2025.2\jbr"
    )
    foreach ($root in $knownRoots) {
        $candidates += Join-Path $root "bin\$toolName.exe"
    }
    foreach ($candidate in $candidates | Select-Object -Unique) {
        if ($candidate -and (Test-Path $candidate)) {
            return $candidate
        }
    }
    throw "$toolName was not found. Set -JavaHome or JAVA_HOME."
}

if (-not $ChartArgs -or $ChartArgs.Count -eq 0) {
    throw "Pass chart paths, or use --list <file> after the script arguments."
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot "..\..")
$buildDir = Join-Path $repoRoot "artifacts\chartstring-dump\classes"
$jarPath = Join-Path $SongDataUpdaterRoot "lib\jbms-parser.jar"
$sourcePath = Join-Path $SongDataUpdaterRoot "src\main\java"

if (-not (Test-Path $jarPath)) {
    throw "jbms-parser.jar was not found: $jarPath"
}
if (-not (Test-Path $sourcePath)) {
    throw "songdata-updater sourcepath was not found: $sourcePath"
}

$javac = Resolve-JavaTool "javac"
$java = if ($JavaRuntimeHome) {
    $runtimeJava = Join-Path $JavaRuntimeHome "bin\java.exe"
    if (-not (Test-Path $runtimeJava)) {
        throw "java.exe was not found in JavaRuntimeHome: $JavaRuntimeHome"
    }
    $runtimeJava
} else {
    Resolve-JavaTool "java"
}
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $repoRoot $OutputDir) | Out-Null

& $javac -encoding UTF-8 -cp $jarPath -sourcepath $sourcePath -d $buildDir (Join-Path $scriptRoot "ChartStringDump.java")
if ($LASTEXITCODE -ne 0) {
    throw "javac failed with exit code $LASTEXITCODE"
}

& $java -cp "$buildDir;$jarPath" ChartStringDump --output-dir (Join-Path $repoRoot $OutputDir) @ChartArgs
if ($LASTEXITCODE -ne 0) {
    throw "java failed with exit code $LASTEXITCODE"
}
