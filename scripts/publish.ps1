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
$configuration = "Release"
$platform = "x64"
$targetFramework = "net472"
$buildOutput = Join-Path $devRoot "bin\$platform\$configuration\$targetFramework"
$distDir = Join-Path $devRoot "dist"
$stagingRoot = Join-Path $distDir "_staging"
$publicRepoOwner = "Neeted"
$publicRepoName = "bemusicseeker-unofficial-fork"

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
    if ($extension -ne ".7z") {
        throw "自動アップデート対象の metadata 同梱パッケージは .7z のみ対応します: $sourcePath"
    }
    $targetName = "chart-info-metadata.7z"

    return [PSCustomObject]@{
        SourcePath = $sourcePath
        TargetName = $targetName
    }
}

function Get-UpdateAssetKindPriority($kind) {
    if ($kind -eq "app") { return 0 }
    if ($kind -eq "app-with-metadata") { return 1 }
    return 99
}

function Sort-UpdateManifestAssets($assets) {
    return ,@($assets | Sort-Object @{ Expression = { Get-UpdateAssetKindPriority $_.kind } }, fileName)
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

function Join-PackageRelativePath($root, $relativePath) {
    return Join-Path $root ($relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
}

function Assert-AppPackageLayout($targetStagingDir, $requiresMetadataArchive) {
    $requiredFiles = @(
        "BeMusicSeeker.exe",
        "BeMusicSeeker.exe.config",
        "BeMusicSeeker.Updater.exe",
        "libs/SevenZipExtractor.dll",
        "libs/OggVorbis.NET64.dll",
        "libs/x64/7z.dll",
        "libs/x64/bass.dll",
        "libs/x64/sqlite3.dll",
        "native/Everything3_x64.dll",
        "native/EverythingBridge_x64.dll",
        "lang/ja-JP.json"
    )
    foreach ($relativePath in $requiredFiles) {
        $path = Join-PackageRelativePath $targetStagingDir $relativePath
        if (-not (Test-Path $path -PathType Leaf)) {
            throw "release package の必須ファイルが見つかりません: $relativePath"
        }
    }

    $forbiddenPaths = @(
        "x86",
        "x64",
        "libs/x86",
        "libs/x64/OggVorbis.NET64.dll",
        "SevenZipExtractor.dll",
        "OggVorbis.NET64.dll",
        "imported_metadata",
        "chart-info-metadata.db"
    )
    foreach ($relativePath in $forbiddenPaths) {
        $path = Join-PackageRelativePath $targetStagingDir $relativePath
        if (Test-Path $path) {
            throw "release package に禁止された配置が残っています: $relativePath"
        }
    }

    $metadataArchivePath = Join-PackageRelativePath $targetStagingDir "chart-info-metadata.7z"
    if ($requiresMetadataArchive) {
        if (-not (Test-Path $metadataArchivePath -PathType Leaf)) {
            throw "metadata 同梱 release package に chart-info-metadata.7z がありません。"
        }
    }
    elseif (Test-Path $metadataArchivePath) {
        throw "通常版 release package に chart-info-metadata.7z が含まれています。"
    }
}

function Copy-AppFilesToStaging($targetStagingDir) {
    if (Test-Path $targetStagingDir) { Remove-Item $targetStagingDir -Recurse -Force }
    New-Item -ItemType Directory -Path $targetStagingDir -Force | Out-Null

    # アプリ本体のコピー (config, .pdb, *.log は除外)
    Copy-Item (Join-Path $buildOutput "BeMusicSeeker.exe")        $targetStagingDir
    Copy-Item (Join-Path $buildOutput "BeMusicSeeker.exe.config") $targetStagingDir
    Copy-Item (Join-Path $buildOutput "BeMusicSeeker.Updater.exe") $targetStagingDir
    Copy-Item (Join-Path $buildOutput "libs")   (Join-Path $targetStagingDir "libs")   -Recurse
    Copy-Item (Join-Path $buildOutput "native") (Join-Path $targetStagingDir "native") -Recurse
    Copy-Item (Join-Path $buildOutput "lang")   (Join-Path $targetStagingDir "lang")   -Recurse

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

function Get-ReleaseAssetMetadata($assetPath, $version, $packageSuffix) {
    $asset = Get-Item $assetPath
    $tag = "v$version"
    $fileName = $asset.Name
    $downloadUrl = "https://github.com/$publicRepoOwner/$publicRepoName/releases/download/$tag/$fileName"
    $isMetadataPackage = -not [string]::IsNullOrWhiteSpace($packageSuffix)

    return [PSCustomObject]@{
        kind = if ($isMetadataPackage) { "app-with-metadata" } else { "app" }
        label = if ($isMetadataPackage) { "App with metadata bundle" } else { "App only" }
        fileName = $fileName
        url = $downloadUrl
        sha256 = (Get-FileHash -Path $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        sizeBytes = $asset.Length
        includesChartInfoMetadata = $isMetadataPackage
    }
}

function New-UpdateManifestCandidate($version, $assetMetadata) {
    $tag = "v$version"
    $manifest = [PSCustomObject]@{
        schemaVersion = 1
        version = $version
        releaseTag = $tag
        releasePageUrl = "https://github.com/$publicRepoOwner/$publicRepoName/releases/tag/$tag"
        packageFormatVersion = 1
        publishedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        minimumUpdaterVersion = "1"
        assets = (Sort-UpdateManifestAssets $assetMetadata)
    }

    $manifestPath = Join-Path $distDir "update-$tag.json"
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -Path $manifestPath -Encoding UTF8
    Write-Host "  update manifest 候補を作成: $manifestPath" -ForegroundColor Green
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

    Assert-AppPackageLayout $targetStagingDir ($metadataInfo -ne $null)
    New-ManagedFilesManifest $targetStagingDir

    $zipName = "bemusicseeker-unofficial-fork-v$version$packageSuffix.zip"
    $zipPath = Join-Path $distDir $zipName

    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Write-Host "  パッケージ作成中: $zipName"
    Compress-Archive -Path "$targetStagingDir\*" -DestinationPath $zipPath -CompressionLevel Optimal

    Remove-Item $targetStagingDir -Recurse -Force

    Write-Host "  パッケージ作成完了: $zipPath" -ForegroundColor Green
    return $zipPath
}

function New-ManagedFilesManifest($targetStagingDir) {
    $manifestPath = Join-Path $targetStagingDir "update-managed-files.txt"
    $root = [System.IO.Path]::GetFullPath($targetStagingDir).TrimEnd('\', '/')
    $paths = Get-ChildItem $targetStagingDir -Recurse -File |
        ForEach-Object {
            $fullName = [System.IO.Path]::GetFullPath($_.FullName)
            $relative = $fullName.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
            if ($relative -ne "update-managed-files.txt") {
                $relative
            }
        } |
        Sort-Object
    Set-Content -Path $manifestPath -Value $paths -Encoding UTF8
    Write-Host "  managed files manifest を作成: update-managed-files.txt"
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
        dotnet build BeMusicSeeker.csproj -c $configuration -p:Platform=$platform | Out-Host
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
    $assetMetadata = @()

    $appPackage = New-ZipPackage $version "" $null
    $packages += $appPackage
    $assetMetadata += Get-ReleaseAssetMetadata $appPackage $version ""

    if ($IncludeMetadata) {
        $metadataPackage = New-ZipPackage $version $MetadataPackageSuffix $metadataInfo
        $packages += $metadataPackage
        $assetMetadata += Get-ReleaseAssetMetadata $metadataPackage $version $MetadataPackageSuffix
    }

    New-UpdateManifestCandidate $version $assetMetadata

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
    $version = Get-AppVersion
    $currentVersionPattern = "bemusicseeker-unofficial-fork-v$version*.zip"
    Get-ChildItem $pubDist -Filter $currentVersionPattern -File | ForEach-Object {
        Remove-Item $_.FullName -Force
        Write-Host "  削除: dist\$($_.Name)"
    }

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
