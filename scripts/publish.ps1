<#
.SYNOPSIS
    リリースパッケージの作成と公開リポジトリへの同期を行うスクリプト

.DESCRIPTION
    1. クリーンビルド後、dist\ にリリース用 zip パッケージを作成
    2. Markdown 資料を HTML に変換して同梱
    3. -IncludeMetadata 指定時は chart_info metadata 同梱 zip も追加作成
    4. パッケージおよび公開対象ファイルを公開リポジトリへコピー
#>
param(
    [switch]$SkipBuild,
    [switch]$PackageOnly,
    [switch]$SyncOnly,
    [switch]$SkipDocHtml,
    [switch]$IncludeMetadata,
    [string]$MetadataSource = "artifacts\chart-info-metadata\latest\chart-info-metadata.7z",
    [string]$MetadataPackageSuffix = "-with-metadata",
    [string]$PublicSiteUrl = "https://neeted.github.io/bemusicseeker-unofficial-fork"
)

$ErrorActionPreference = "Stop"

# パス定義
$devRoot = "D:\work\BeMusicSeeker-decomp"
$pubRoot = "D:\github\bemusicseeker-unofficial-fork"
$buildOutput = Join-Path $devRoot "bin\Release\net472"
$distDir = Join-Path $devRoot "dist"
$stagingRoot = Join-Path $distDir "_staging"

# AssemblyInformationalVersion を読み取る
function Get-AppVersion {
    $asmInfoPath = Join-Path $devRoot "Properties\AssemblyInfo.cs"
    $content = Get-Content $asmInfoPath -Raw
    if ($content -match 'AssemblyInformationalVersion\("([^"]+)"\)') {
        return $Matches[1]
    }
    throw "AssemblyInformationalVersion が見つかりません: $asmInfoPath"
}

function Resolve-MetadataSource {
    if ([string]::IsNullOrWhiteSpace($MetadataSource)) {
        throw "MetadataSource が空です。"
    }

    $sourcePath = $MetadataSource
    if (-not [System.IO.Path]::IsPathRooted($sourcePath)) {
        $sourcePath = Join-Path $devRoot $sourcePath
    }
    $sourcePath = [System.IO.Path]::GetFullPath($sourcePath)
    if (-not (Test-Path $sourcePath -PathType Leaf)) {
        throw "metadata source が見つかりません: $sourcePath"
    }

    $extension = [System.IO.Path]::GetExtension($sourcePath).ToLowerInvariant()
    if ($extension -eq ".7z") {
        $targetName = "chart-info-metadata.7z"
    }
    elseif ($extension -eq ".db") {
        $targetName = "chart-info-metadata.db"
    }
    else {
        throw "metadata source は .7z または .db を指定してください: $sourcePath"
    }

    return [PSCustomObject]@{
        SourcePath = $sourcePath
        TargetName = $targetName
    }
}

function Build-DocHtml($targetStagingDir) {
    if ($SkipDocHtml) {
        Write-Host "  HTML docs 生成をスキップしました" -ForegroundColor Yellow
        return
    }

    Write-Host "  HTML docs を生成中..."
    Push-Location $devRoot
    try {
        $generatedDocs = @(uv run scripts\build-doc-html.py --source-root $devRoot --output-root $targetStagingDir)
        $exitCode = $LASTEXITCODE
        foreach ($doc in $generatedDocs) {
            Write-Host "    $doc"
        }
        if ($exitCode -ne 0) { throw "HTML docs の生成に失敗しました" }
    }
    finally {
        Pop-Location
    }
    Write-Host "  HTML docs 生成完了" -ForegroundColor Green
}

function Build-PublicDocSite($targetDocsDir) {
    if ($SkipDocHtml) {
        Write-Host "  Pages HTML docs 生成をスキップしました" -ForegroundColor Yellow
        return
    }

    Write-Host "  Pages HTML docs を生成中..."
    Push-Location $devRoot
    try {
        $generatedDocs = @(uv run scripts\build-doc-html.py --source-root $devRoot --output-root $targetDocsDir --site --site-url $PublicSiteUrl)
        $exitCode = $LASTEXITCODE
        foreach ($doc in $generatedDocs) {
            Write-Host "    docs\$doc"
        }
        if ($exitCode -ne 0) { throw "Pages HTML docs の生成に失敗しました" }
    }
    finally {
        Pop-Location
    }

    New-Item -ItemType File -Path (Join-Path $targetDocsDir ".nojekyll") -Force | Out-Null
    Write-Host "  Pages HTML docs 生成完了" -ForegroundColor Green
}

function Copy-AppFilesToStaging($targetStagingDir) {
    if (Test-Path $targetStagingDir) { Remove-Item $targetStagingDir -Recurse -Force }
    New-Item -ItemType Directory -Path $targetStagingDir -Force | Out-Null

    # アプリ本体のコピー (config, .pdb, *.log は除外)
    Copy-Item (Join-Path $buildOutput "BeMusicSeeker.exe")        $targetStagingDir
    Copy-Item (Join-Path $buildOutput "BeMusicSeeker.exe.config") $targetStagingDir
    Copy-Item (Join-Path $buildOutput "libs")   (Join-Path $targetStagingDir "libs")   -Recurse
    Copy-Item (Join-Path $buildOutput "native") (Join-Path $targetStagingDir "native") -Recurse
    Copy-Item (Join-Path $buildOutput "lang")   (Join-Path $targetStagingDir "lang")   -Recurse

    # LaunchWithInfoLog.bat
    Copy-Item (Join-Path $devRoot "scripts\LaunchWithInfoLog.bat") $targetStagingDir

    # README, LICENSE, ThirdPartyNotices
    Copy-Item (Join-Path $devRoot "README.md")               $targetStagingDir
    Copy-Item (Join-Path $devRoot "README.ja.md")            $targetStagingDir
    Copy-Item (Join-Path $devRoot "LICENSE")                  $targetStagingDir
    Copy-Item (Join-Path $devRoot "ThirdPartyNotices.txt")    $targetStagingDir
    Copy-Item (Join-Path $devRoot "ThirdPartyNotices.ja.txt") $targetStagingDir

    # third_party
    Copy-Item (Join-Path $devRoot "third_party") (Join-Path $targetStagingDir "third_party") -Recurse

    # docs source files and image assets
    Copy-Item (Join-Path $devRoot "docs") (Join-Path $targetStagingDir "docs") -Recurse

    # Markdown docs converted to HTML.
    Build-DocHtml $targetStagingDir
}

function New-ZipPackage($version, $packageSuffix, $metadataInfo) {
    $stagingName = "_staging"
    if (-not [string]::IsNullOrWhiteSpace($packageSuffix)) {
        $safeSuffix = $packageSuffix -replace '[^A-Za-z0-9_-]', '_'
        $stagingName = "_staging$safeSuffix"
    }
    $targetStagingDir = Join-Path $distDir $stagingName

    Write-Host "  アプリ本体をコピー中: $stagingName"
    Copy-AppFilesToStaging $targetStagingDir

    if ($metadataInfo -ne $null) {
        Write-Host "  metadata bundle をコピー中: $($metadataInfo.TargetName)"
        Copy-Item $metadataInfo.SourcePath (Join-Path $targetStagingDir $metadataInfo.TargetName) -Force
    }

    $zipName = "bemusicseeker-unofficial-fork-v$version$packageSuffix.zip"
    $zipPath = Join-Path $distDir $zipName

    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Write-Host "  パッケージ作成中: $zipName"
    Compress-Archive -Path "$targetStagingDir\*" -DestinationPath $zipPath -CompressionLevel Optimal

    Remove-Item $targetStagingDir -Recurse -Force

    Write-Host "  パッケージ作成完了: $zipPath" -ForegroundColor Green
    return $zipPath
}

# ========== ステップ 1: リリースパッケージの作成 ==========
function New-ReleasePackage {
    Write-Host "=== ステップ 1: リリースパッケージの作成 ===" -ForegroundColor Cyan

    $metadataInfo = $null
    if ($IncludeMetadata) {
        if ([string]::IsNullOrWhiteSpace($MetadataPackageSuffix)) {
            throw "MetadataPackageSuffix が空です。通常版 package を上書きしないため suffix を指定してください。"
        }
        $metadataInfo = Resolve-MetadataSource
        Write-Host "  metadata source: $($metadataInfo.SourcePath)"
    }

    # クリーンビルド
    if (-not $SkipBuild) {
        Write-Host "  ビルド中..."
        Push-Location $devRoot
        dotnet build -c Release BeMusicSeeker.csproj | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "ビルドに失敗しました" }
        Pop-Location
        Write-Host "  ビルド完了" -ForegroundColor Green
    }
    else {
        Write-Host "  ビルドをスキップしました" -ForegroundColor Yellow
    }

    # バージョン取得
    $version = Get-AppVersion
    Write-Host "  バージョン: $version"

    if (Test-Path $stagingRoot) { Remove-Item $stagingRoot -Recurse -Force }

    $packages = @()
    $packages += New-ZipPackage $version "" $null
    if ($IncludeMetadata) {
        $packages += New-ZipPackage $version $MetadataPackageSuffix $metadataInfo
    }

    return $packages
}

# ========== ステップ 2: 公開リポジトリへコピー ==========
function Sync-PublicRepo($releasePackagePaths) {
    Write-Host ""
    Write-Host "=== ステップ 2: 公開リポジトリへコピー ===" -ForegroundColor Cyan

    # ディレクトリのミラーリングコピー (既存を削除→新規コピー)
    function Mirror-Directory($srcName) {
        $src = Join-Path $devRoot  $srcName
        $dst = Join-Path $pubRoot  $srcName
        if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
        Copy-Item $src $dst -Recurse
        Write-Host "  コピー: $srcName"
    }

    # dist\ の zip をコピー (古い zip は削除しない、追加のみ)
    $pubDist = Join-Path $pubRoot "dist"
    if (-not (Test-Path $pubDist)) { New-Item -ItemType Directory -Path $pubDist -Force | Out-Null }
    $releaseAssets = @()
    if ($releasePackagePaths -ne $null -and $releasePackagePaths.Count -gt 0) {
        $releaseAssets = @($releasePackagePaths | ForEach-Object { Get-Item $_ })
    }
    else {
        $releaseAssets = @(Get-ChildItem (Join-Path $distDir "*.zip") -File)
    }
    foreach ($asset in $releaseAssets) {
        Copy-Item $asset.FullName $pubDist -Force
        Write-Host "  コピー: dist\$($asset.Name)"
    }

    # ディレクトリのミラーリング
    Mirror-Directory "docs"
    Build-PublicDocSite (Join-Path $pubRoot "docs")
    Mirror-Directory "third_party"
    Mirror-Directory "scripts"
    Mirror-Directory "lang"

    # 単体ファイルのコピー
    $files = @("README.md", "README.ja.md", "LICENSE", "ThirdPartyNotices.txt", "ThirdPartyNotices.ja.txt", "version.txt")
    foreach ($f in $files) {
        Copy-Item (Join-Path $devRoot $f) (Join-Path $pubRoot $f) -Force
        Write-Host "  コピー: $f"
    }

    Write-Host ""
    Write-Host "=== 同期完了 ===" -ForegroundColor Green
    Write-Host "公開リポジトリ: $pubRoot"
}

# ========== メイン ==========
$releasePackagePaths = @()
if (-not $SyncOnly) {
    $releasePackagePaths = @(New-ReleasePackage)
}
if (-not $PackageOnly) {
    Sync-PublicRepo $releasePackagePaths
}

Write-Host ""
Write-Host "全て完了しました。" -ForegroundColor Green
