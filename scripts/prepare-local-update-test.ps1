<#
.SYNOPSIS
    --update-manifest-url= 用のローカル自動アップデート検証環境を作成します。

.DESCRIPTION
    既存の release zip をローカル HTTP 配信用ディレクトリへコピーし、現在のアプリより大きい
    検証用 version を持つ update.json を生成します。

    -StartServer を付けると 127.0.0.1 で Python http.server を起動し、manifest URL を表示します。
    終了するときは -StopServer を使います。
#>
param(
    [string]$Version = "999.0.0.0",
    [int]$Port = 8765,
    [string]$PackagePath,
    [string]$OutputDir = "artifacts\local-update-test",
    [switch]$StartServer,
    [switch]$StopServer
)

$ErrorActionPreference = "Stop"

$devRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$distDir = Join-Path $devRoot "dist"

function Get-AppVersion {
    $asmInfoPath = Join-Path $devRoot "Properties\AssemblyInfo.cs"
    $content = Get-Content $asmInfoPath -Raw
    if ($content -match 'AssemblyInformationalVersion\("([^"]+)"\)') {
        return $Matches[1]
    }
    throw "AssemblyInformationalVersion が見つかりません: $asmInfoPath"
}

function Resolve-PackagePath {
    if (-not [string]::IsNullOrWhiteSpace($PackagePath)) {
        $resolved = $PackagePath
        if (-not [System.IO.Path]::IsPathRooted($resolved)) {
            $resolved = Join-Path $devRoot $resolved
        }
        $resolved = [System.IO.Path]::GetFullPath($resolved)
        if (-not (Test-Path $resolved -PathType Leaf)) {
            throw "PackagePath が見つかりません: $resolved"
        }
        return $resolved
    }

    $currentVersion = Get-AppVersion
    $defaultName = "bemusicseeker-unofficial-fork-v$currentVersion.zip"
    $defaultPath = Join-Path $distDir $defaultName
    if (-not (Test-Path $defaultPath -PathType Leaf)) {
        throw "通常版 package が見つかりません: $defaultPath`n先に .\scripts\publish.ps1 -PackageOnly -SkipDocHtml を実行してください。"
    }
    return [System.IO.Path]::GetFullPath($defaultPath)
}

function Stop-ExistingServer($pidPath) {
    if (-not (Test-Path $pidPath -PathType Leaf)) {
        Write-Host "local update test server は起動していません: $pidPath" -ForegroundColor Yellow
        return
    }

    $processIdText = (Get-Content $pidPath -Raw).Trim()
    $processId = 0
    if ([int]::TryParse($processIdText, [ref]$processId)) {
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($process -ne $null) {
            Stop-Process -Id $processId
            Write-Host "local update test server を停止しました: PID $processId" -ForegroundColor Green
        }
    }
    Remove-Item $pidPath -Force
}

$resolvedOutputDir = $OutputDir
if (-not [System.IO.Path]::IsPathRooted($resolvedOutputDir)) {
    $resolvedOutputDir = Join-Path $devRoot $resolvedOutputDir
}
$resolvedOutputDir = [System.IO.Path]::GetFullPath($resolvedOutputDir)
$pidPath = Join-Path $resolvedOutputDir "server.pid"

if ($StopServer) {
    Stop-ExistingServer $pidPath
    if (-not $StartServer) {
        return
    }
}

New-Item -ItemType Directory -Path $resolvedOutputDir -Force | Out-Null

$sourcePackagePath = Resolve-PackagePath
$localPackageName = "bemusicseeker-local-update-test.zip"
$localPackagePath = Join-Path $resolvedOutputDir $localPackageName
Copy-Item $sourcePackagePath $localPackagePath -Force

$asset = Get-Item $localPackagePath
$tag = "v$Version"
$baseUrl = "http://127.0.0.1:$Port"
$manifest = [PSCustomObject]@{
    schemaVersion = 1
    version = $Version
    releaseTag = $tag
    releasePageUrl = "$baseUrl/"
    packageFormatVersion = 1
    publishedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    minimumUpdaterVersion = "1"
    assets = @(
        [PSCustomObject]@{
            kind = "app"
            label = "ローカル検証用 package"
            fileName = $localPackageName
            url = "$baseUrl/$localPackageName"
            sha256 = (Get-FileHash -Path $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            sizeBytes = $asset.Length
            includesChartInfoMetadata = $false
        }
    )
}

$manifestPath = Join-Path $resolvedOutputDir "update.json"
$manifest | ConvertTo-Json -Depth 8 | Set-Content -Path $manifestPath -Encoding UTF8

Write-Host "local update manifest を作成しました:" -ForegroundColor Green
Write-Host "  $manifestPath"
Write-Host "package:"
Write-Host "  $localPackagePath"
Write-Host ""
Write-Host "manifest URL:"
Write-Host "  $baseUrl/update.json"
Write-Host ""
Write-Host "app 起動例:"
Write-Host "  .\bin\Release\net472\BeMusicSeeker.exe --update-manifest-url=$baseUrl/update.json"

if ($StartServer) {
    Stop-ExistingServer $pidPath
    $pythonCommand = Get-Command python -ErrorAction SilentlyContinue
    if ($pythonCommand -eq $null) {
        $pythonCommand = Get-Command py -ErrorAction SilentlyContinue
    }
    if ($pythonCommand -eq $null) {
        throw "python または py が見つかりません。別途 HTTP server で $resolvedOutputDir を配信してください。"
    }

    if ($pythonCommand.Name -eq "py.exe" -or $pythonCommand.Name -eq "py") {
        $argumentList = @("-3", "-m", "http.server", $Port.ToString(), "--bind", "127.0.0.1", "--directory", $resolvedOutputDir)
    }
    else {
        $argumentList = @("-m", "http.server", $Port.ToString(), "--bind", "127.0.0.1", "--directory", $resolvedOutputDir)
    }

    $serverProcess = Start-Process -FilePath $pythonCommand.Source -ArgumentList $argumentList -WindowStyle Hidden -PassThru
    Set-Content -Path $pidPath -Value $serverProcess.Id -Encoding ASCII
    Write-Host ""
    Write-Host "local update test server を起動しました: PID $($serverProcess.Id)" -ForegroundColor Green
    Write-Host "停止:"
    Write-Host "  .\scripts\prepare-local-update-test.ps1 -StopServer"
}
