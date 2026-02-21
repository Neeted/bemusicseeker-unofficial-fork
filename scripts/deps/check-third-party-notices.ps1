param(
    [string]$NoticesPath = "ThirdPartyNotices.txt",
    [string]$ReleaseRoot = "bin/Release/net472",
    [string]$ResourcesRoot = "resources"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $NoticesPath)) {
    throw "Notices file not found: $NoticesPath"
}
if (-not (Test-Path $ReleaseRoot)) {
    throw "Release root not found: $ReleaseRoot"
}
if (-not (Test-Path $ResourcesRoot)) {
    throw "Resources root not found: $ResourcesRoot"
}

$noticeComponents = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)

function Add-ComponentNamesFromText([string]$text) {
    if ([string]::IsNullOrWhiteSpace($text)) {
        return
    }
    $parts = $text.Split(',')
    foreach ($part in $parts) {
        $candidate = $part.Trim()
        if ($candidate -match '([A-Za-z0-9_.-]+\.(dll|ttf|otf))') {
            [void]$noticeComponents.Add($Matches[1])
        }
    }
}

$componentBuffer = $null
foreach ($line in Get-Content $NoticesPath) {
    if ($line -match '^Component:\s*(.+)$') {
        if ($componentBuffer) {
            Add-ComponentNamesFromText $componentBuffer
        }
        $componentBuffer = $Matches[1]
        continue
    }

    if ($componentBuffer -and $line -match '^\s{2,}.+') {
        $componentBuffer += ' ' + $line.Trim()
        continue
    }

    if ($componentBuffer) {
        Add-ComponentNamesFromText $componentBuffer
        $componentBuffer = $null
    }
}

if ($componentBuffer) {
    Add-ComponentNamesFromText $componentBuffer
}

$extensions = @("*.dll", "*.ttf", "*.otf")
$releaseFiles = Get-ChildItem -Path $ReleaseRoot -Recurse -File -Include $extensions |
    Select-Object -ExpandProperty Name

$resourceFiles = Get-ChildItem -Path $ResourcesRoot -Recurse -File -Include "*.ttf", "*.otf" |
    Select-Object -ExpandProperty Name

$files = @($releaseFiles + $resourceFiles) | Sort-Object -Unique

$exclude = @(
    "BeMusicSeeker.exe",
    "BeMusicSeeker.pdb",
    "BeMusicSeeker.exe.config"
)

$targets = $files | Where-Object { $_ -notin $exclude }

$missingInNotices = @()
foreach ($name in $targets) {
    if (-not $noticeComponents.Contains($name)) {
        $missingInNotices += $name
    }
}

$unusedInNotices = @()
foreach ($name in $noticeComponents) {
    if ($targets -notcontains $name) {
        $unusedInNotices += $name
    }
}

Write-Host "=== Third-party notices coverage check ==="
Write-Host "Release root: $ReleaseRoot"
Write-Host "Resources root: $ResourcesRoot"
Write-Host "Notices path: $NoticesPath"
Write-Host "Target component files: $($targets.Count)"
Write-Host "Notices component entries: $($noticeComponents.Count)"
Write-Host ""

if ($missingInNotices.Count -gt 0) {
    Write-Host "[ERROR] Files present in release but missing from notices:" -ForegroundColor Red
    $missingInNotices | Sort-Object | ForEach-Object { Write-Host "  - $_" }
} else {
    Write-Host "[OK] All release files are covered by notices." -ForegroundColor Green
}

Write-Host ""
if ($unusedInNotices.Count -gt 0) {
    Write-Host "[WARN] Files listed in notices but not found under scanned targets:" -ForegroundColor Yellow
    $unusedInNotices | Sort-Object | ForEach-Object { Write-Host "  - $_" }
} else {
    Write-Host "[OK] No unused component entries in notices." -ForegroundColor Green
}

if ($missingInNotices.Count -gt 0) {
    exit 1
}

exit 0
