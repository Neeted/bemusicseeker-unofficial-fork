<#
.SYNOPSIS
    --update-manifest-url= 用のローカル自動アップデート検証環境を作成します。

.DESCRIPTION
    既存の release zip をローカル HTTP 配信用ディレクトリへコピーし、現在のアプリより大きい
    検証用 version を持つ update.json を生成します。

    -MetadataBundleOnly を付けると、release.ps1 の同名指定と同じくメタデータ同梱版だけを
    更新情報へ載せます。

    -StartServer を付けると 127.0.0.1 で Python http.server を起動し、manifest URL を表示します。
    終了するときは -StopServer を使います。
#>
param(
    [string]$Version = "999.0.0.0",
    [int]$Port = 8765,
    [string]$PackagePath,
    [string]$MetadataPackagePath,
    [string]$OutputDir = "artifacts\local-update-test",
    [switch]$MetadataBundleOnly,
    [switch]$StartServer,
    [switch]$StopServer
)

$ErrorActionPreference = "Stop"

$devRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$distDir = Join-Path $devRoot "dist"

. (Join-Path $PSScriptRoot "application-version.ps1")

function Resolve-PackagePath($path, $defaultName, $requiredMessage) {
    if (-not [string]::IsNullOrWhiteSpace($path)) {
        $resolved = $path
        if (-not [System.IO.Path]::IsPathRooted($resolved)) {
            $resolved = Join-Path $devRoot $resolved
        }
        $resolved = [System.IO.Path]::GetFullPath($resolved)
        if (-not (Test-Path $resolved -PathType Leaf)) {
            throw "PackagePath が見つかりません: $resolved"
        }
        return $resolved
    }

    $defaultPath = Join-Path $distDir $defaultName
    if (-not (Test-Path $defaultPath -PathType Leaf)) {
        if ($requiredMessage -ne $null) {
            throw "$requiredMessage`: $defaultPath"
        }
        return $null
    }
    return [System.IO.Path]::GetFullPath($defaultPath)
}

function New-ManifestAsset($index, $localPackageName, $localPackagePath, $includesChartInfoMetadata, $baseUrl) {
    $asset = Get-Item $localPackagePath
    # kind は release.ps1 と同じく、v3.1.0.0 以前のアプリ向けの並び順の目印です。
    return [PSCustomObject]@{
        kind = if ($index -eq 0) { "app" } else { "app-with-metadata" }
        label = if ($includesChartInfoMetadata) { "App with metadata bundle" } else { "App only" }
        fileName = $localPackageName
        url = "$baseUrl/$localPackageName"
        sha256 = (Get-FileHash -Path $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        sizeBytes = $asset.Length
        includesChartInfoMetadata = $includesChartInfoMetadata
    }
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

$currentVersion = Get-ApplicationVersion -RepositoryRoot $devRoot
$sourcePackagePath = Resolve-PackagePath `
    $PackagePath `
    "bemusicseeker-unofficial-fork-v$currentVersion.zip" `
    "通常版 package が見つかりません。先に .\scripts\publish.ps1 -SkipDocHtml を実行してください。"
$sourceMetadataPackagePath = Resolve-PackagePath `
    $MetadataPackagePath `
    "bemusicseeker-unofficial-fork-v$currentVersion-with-metadata.zip" `
    $(if ($MetadataBundleOnly) { "-MetadataBundleOnly にはメタデータ同梱版 package が必要です" } else { $null })

$localPackageName = "bemusicseeker-local-update-test.zip"
$localPackagePath = Join-Path $resolvedOutputDir $localPackageName
Copy-Item $sourcePackagePath $localPackagePath -Force

$localMetadataPackagePath = $null
$localMetadataPackageName = "bemusicseeker-local-update-test-with-metadata.zip"
if ($sourceMetadataPackagePath -ne $null) {
    $localMetadataPackagePath = Join-Path $resolvedOutputDir $localMetadataPackageName
    Copy-Item $sourceMetadataPackagePath $localMetadataPackagePath -Force
}

$tag = "v$Version"
$baseUrl = "http://127.0.0.1:$Port"
$manifestAssets = @()
if (-not $MetadataBundleOnly) {
    $manifestAssets += New-ManifestAsset $manifestAssets.Count $localPackageName $localPackagePath $false $baseUrl
}
if ($localMetadataPackagePath -ne $null) {
    $manifestAssets += New-ManifestAsset $manifestAssets.Count $localMetadataPackageName $localMetadataPackagePath $true $baseUrl
}

$manifest = [PSCustomObject]@{
    schemaVersion = 1
    version = $Version
    releaseTag = $tag
    releasePageUrl = "$baseUrl/"
    packageFormatVersion = 1
    publishedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    minimumUpdaterVersion = "1"
    assets = $manifestAssets
}

$manifestPath = Join-Path $resolvedOutputDir "update.json"
$manifest | ConvertTo-Json -Depth 8 | Set-Content -Path $manifestPath -Encoding UTF8

Write-Host "local update manifest を作成しました:" -ForegroundColor Green
Write-Host "  $manifestPath"
Write-Host "package:"
Write-Host "  $localPackagePath"
if ($localMetadataPackagePath -ne $null) {
    Write-Host "metadata package:"
    Write-Host "  $localMetadataPackagePath"
}
Write-Host ""
Write-Host "manifest URL:"
Write-Host "  $baseUrl/update.json"
Write-Host ""
Write-Host "app 起動例（配布 ZIP を未使用の検証用ディレクトリへ展開し、同梱の Updater と一緒に使います）:"
Write-Host "  Expand-Archive -LiteralPath '$($sourcePackagePath.Replace("'", "''"))' -DestinationPath '.\artifacts\local-update-app'"
Write-Host "  .\artifacts\local-update-app\BeMusicSeeker.exe --update-manifest-url=$baseUrl/update.json"

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
        $argumentList = @("-3", "-m", "http.server", $Port.ToString(), "--bind", "127.0.0.1", "--directory", ".")
    }
    else {
        $argumentList = @("-m", "http.server", $Port.ToString(), "--bind", "127.0.0.1", "--directory", ".")
    }

    $serverProcess = Start-Process -FilePath $pythonCommand.Source -ArgumentList $argumentList -WorkingDirectory $resolvedOutputDir -WindowStyle Hidden -PassThru
    Set-Content -Path $pidPath -Value $serverProcess.Id -Encoding ASCII
    Write-Host ""
    Write-Host "local update test server を起動しました: PID $($serverProcess.Id)" -ForegroundColor Green
    Write-Host "停止:"
    Write-Host "  .\scripts\prepare-local-update-test.ps1 -OutputDir '$($resolvedOutputDir.Replace("'", "''"))' -StopServer"
}
