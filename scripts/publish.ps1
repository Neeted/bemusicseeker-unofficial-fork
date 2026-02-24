<#
.SYNOPSIS
    リリースパッケージの作成と公開リポジトリへの同期を行うスクリプト

.DESCRIPTION
    1. クリーンビルド後、dist\ にリリース用 zip パッケージを作成
    2. パッケージおよび公開対象ファイルを公開リポジトリへコピー
#>
param(
    [switch]$SkipBuild,
    [switch]$PackageOnly,
    [switch]$SyncOnly
)

$ErrorActionPreference = "Stop"

# パス定義
$devRoot = "D:\work\BeMusicSeeker-decomp"
$pubRoot = "D:\github\bemusicseeker-unofficial-fork"
$buildOutput = Join-Path $devRoot "bin\Release\net472"
$distDir = Join-Path $devRoot "dist"
$stagingDir = Join-Path $distDir "_staging"

# AssemblyInformationalVersion を読み取る
function Get-AppVersion {
    $asmInfoPath = Join-Path $devRoot "Properties\AssemblyInfo.cs"
    $content = Get-Content $asmInfoPath -Raw
    if ($content -match 'AssemblyInformationalVersion\("([^"]+)"\)') {
        return $Matches[1]
    }
    throw "AssemblyInformationalVersion が見つかりません: $asmInfoPath"
}

# ========== ステップ 1: リリースパッケージの作成 ==========
function New-ReleasePackage {
    Write-Host "=== ステップ 1: リリースパッケージの作成 ===" -ForegroundColor Cyan

    # クリーンビルド
    if (-not $SkipBuild) {
        Write-Host "  ビルド中..."
        Push-Location $devRoot
        dotnet build -c Release BeMusicSeeker.csproj
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

    # ステージングディレクトリの準備
    if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
    New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

    # アプリ本体のコピー (config, .pdb, *.log は除外)
    Write-Host "  アプリ本体をコピー中..."
    Copy-Item (Join-Path $buildOutput "BeMusicSeeker.exe")        $stagingDir
    Copy-Item (Join-Path $buildOutput "BeMusicSeeker.exe.config") $stagingDir
    Copy-Item (Join-Path $buildOutput "libs")   (Join-Path $stagingDir "libs")   -Recurse
    Copy-Item (Join-Path $buildOutput "native") (Join-Path $stagingDir "native") -Recurse
    Copy-Item (Join-Path $buildOutput "lang")   (Join-Path $stagingDir "lang")   -Recurse

    # LaunchWithInfoLog.bat
    Copy-Item (Join-Path $devRoot "scripts\LaunchWithInfoLog.bat") $stagingDir

    # README, LICENSE, ThirdPartyNotices
    Copy-Item (Join-Path $devRoot "README.md")               $stagingDir
    Copy-Item (Join-Path $devRoot "README.ja.md")            $stagingDir
    Copy-Item (Join-Path $devRoot "LICENSE")                  $stagingDir
    Copy-Item (Join-Path $devRoot "ThirdPartyNotices.txt")    $stagingDir
    Copy-Item (Join-Path $devRoot "ThirdPartyNotices.ja.txt") $stagingDir

    # third_party
    Copy-Item (Join-Path $devRoot "third_party") (Join-Path $stagingDir "third_party") -Recurse

    # zip 作成
    $zipName = "bemusicseeker-unofficial-fork-v$version.zip"
    $zipPath = Join-Path $distDir $zipName

    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Write-Host "  パッケージ作成中: $zipName"
    Compress-Archive -Path "$stagingDir\*" -DestinationPath $zipPath -CompressionLevel Optimal

    # ステージングの削除
    Remove-Item $stagingDir -Recurse -Force

    Write-Host "  パッケージ作成完了: $zipPath" -ForegroundColor Green
    return $zipPath
}

# ========== ステップ 2: 公開リポジトリへコピー ==========
function Sync-PublicRepo {
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
    Get-ChildItem (Join-Path $distDir "*.zip") | ForEach-Object {
        Copy-Item $_.FullName $pubDist -Force
        Write-Host "  コピー: dist\$($_.Name)"
    }

    # ディレクトリのミラーリング
    Mirror-Directory "docs"
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
if (-not $SyncOnly) {
    New-ReleasePackage
}
if (-not $PackageOnly) {
    Sync-PublicRepo
}

Write-Host ""
Write-Host "全て完了しました。" -ForegroundColor Green
