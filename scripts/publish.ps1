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
    [string]$PublicSiteUrl = "https://neeted.github.io/bemusicseeker-unofficial-fork",
    [string]$PublicRepositoryRoot
)

$ErrorActionPreference = "Stop"

# パス定義
$devRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$pubRoot = if ([string]::IsNullOrWhiteSpace($PublicRepositoryRoot)) {
    Join-Path (Split-Path -Parent $devRoot) "bemusicseeker-unofficial-fork"
}
else {
    [System.IO.Path]::GetFullPath($PublicRepositoryRoot)
}
$configuration = "Release"
$platform = "x64"
$solution = Join-Path $devRoot "BeMusicSeeker.sln"
$appProject = Join-Path $devRoot "BeMusicSeeker.csproj"
$updaterProject = Join-Path $devRoot "BeMusicSeeker.Updater\BeMusicSeeker.Updater.csproj"
$appPublishOutput = Join-Path $devRoot "artifacts\publish\app"
$updaterPublishOutput = Join-Path $devRoot "artifacts\publish\updater"
$distDir = Join-Path $devRoot "dist"
$stagingRoot = Join-Path $distDir "_staging"
$publicRepoOwner = "Neeted"
$publicRepoName = "bemusicseeker-unofficial-fork"

. (Join-Path $PSScriptRoot "portable-package-layout.ps1")

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)]
        [string]$Command,

        [Parameter(ValueFromRemainingArguments)]
        [string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $Command $($Arguments -join ' ')"
    }
}

function Invoke-SelfContainedPublish {
    if (Test-Path $appPublishOutput) { Remove-Item $appPublishOutput -Recurse -Force }
    if (Test-Path $updaterPublishOutput) { Remove-Item $updaterPublishOutput -Recurse -Force }
    New-Item -ItemType Directory -Path $appPublishOutput, $updaterPublishOutput -Force | Out-Null

    Write-Host "  main app の managed-bundle ReadyToRun SCD を publish 中..."
    & dotnet publish $appProject `
        --configuration $configuration `
        --runtime win-x64 `
        --self-contained true `
        --no-restore `
        --property:Platform=$platform `
        --property:PublishProfile=WinX64SelfContained `
        --property:PublishDir=$appPublishOutput
    if ($LASTEXITCODE -ne 0) { throw "main app の Self-contained publish に失敗しました" }

    Write-Host "  updater の single-file SCD を publish 中..."
    & dotnet publish $updaterProject `
        --configuration $configuration `
        --runtime win-x64 `
        --self-contained true `
        --no-restore `
        --property:Platform=$platform `
        --property:PublishProfile=WinX64SelfContainedSingleFile `
        --property:PublishDir=$updaterPublishOutput
    if ($LASTEXITCODE -ne 0) { throw "updater の Self-contained single-file publish に失敗しました" }

    Assert-SelfContainedPublishLayout $appPublishOutput $updaterPublishOutput
}

function Assert-SelfContainedPublishLayout($appOutput, $updaterOutput) {
    foreach ($required in Get-PortableMainAppRequiredFiles) {
        if (-not (Test-Path (Join-Path $appOutput $required) -PathType Leaf)) {
            throw "Self-contained app publish output is missing: $required"
        }
    }

    foreach ($forbidden in @(
        "BeMusicSeeker.dll",
        "BeMusicSeeker.deps.json",
        "BeMusicSeeker.runtimeconfig.json")) {
        if (Test-Path (Join-Path $appOutput $forbidden)) {
            throw "Single-file app publish output contains a companion payload: $forbidden"
        }
    }

    $requiredSdkNativeRootFiles = [System.Collections.Generic.HashSet[string]]::new(
        [string[]](Get-PortableRequiredSdkNativeRootFiles),
        [System.StringComparer]::OrdinalIgnoreCase)
    $unexpectedRootDlls = @(
        Get-ChildItem $appOutput -File -Filter "*.dll" |
            Where-Object { -not $requiredSdkNativeRootFiles.Contains($_.Name) }
    )
    if ($unexpectedRootDlls.Count -gt 0) {
        throw "Self-contained app publish output contains an unknown root DLL: $($unexpectedRootDlls.Name -join ', ')"
    }

    $updaterExecutable = Join-Path $updaterOutput "BeMusicSeeker.Updater.exe"
    if (-not (Test-Path $updaterExecutable -PathType Leaf)) {
        throw "Self-contained updater publish output is missing: BeMusicSeeker.Updater.exe"
    }
    foreach ($legacyCompanion in @(
        "BeMusicSeeker.Updater.dll",
        "BeMusicSeeker.Updater.deps.json",
        "BeMusicSeeker.Updater.runtimeconfig.json")) {
        if (Test-Path (Join-Path $updaterOutput $legacyCompanion)) {
            throw "Single-file updater publish output contains a companion payload: $legacyCompanion"
        }
    }
}

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

function Copy-AppFilesToStaging($targetStagingDir) {
    if (Test-Path $targetStagingDir) { Remove-Item $targetStagingDir -Recurse -Force }
    New-Item -ItemType Directory -Path $targetStagingDir -Force | Out-Null

    function Copy-PublishedFile($sourceRoot, $relativePath) {
        $sourcePath = Join-Path $sourceRoot $relativePath
        if (-not (Test-Path $sourcePath -PathType Leaf)) {
            throw "Self-contained publish output の必須ファイルが見つかりません: $relativePath"
        }
        $destinationPath = Join-Path $targetStagingDir $relativePath
        $destinationDirectory = Split-Path $destinationPath -Parent
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        Copy-Item $sourcePath $destinationPath -Force
    }

    # app managed-bundle SCD と隣接native runtimeをコピーし、mutable data、debug symbol、updater payload は除外する。
    $mutableTopLevelNames = @("config", "data", "log", "logs", "update_backup", "update_work", "imported_metadata")
    foreach ($sourceFile in Get-ChildItem $appPublishOutput -File -Recurse) {
        $relativePath = [System.IO.Path]::GetRelativePath($appPublishOutput, $sourceFile.FullName)
        $normalized = $relativePath.Replace('\', '/')
        $topLevel = $normalized.Split('/')[0]
        if ($mutableTopLevelNames -contains $topLevel -or
            $normalized -like "*.pdb" -or
            $normalized -like "BeMusicSeeker.Updater.*") {
            continue
        }
        $destinationPath = Join-Path $targetStagingDir $relativePath
        New-Item -ItemType Directory -Path (Split-Path $destinationPath -Parent) -Force | Out-Null
        Copy-Item $sourceFile.FullName $destinationPath -Force
    }

    Copy-PublishedFile $updaterPublishOutput "BeMusicSeeker.Updater.exe"

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

    Assert-PortableStagingLayout $targetStagingDir ($metadataInfo -ne $null)
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

    # clean Self-contained publish
    if (-not $SkipBuild) {
        Write-Host "  配布用 Self-contained publish 中..."
        Push-Location $devRoot
        Invoke-CheckedCommand dotnet restore $solution '-r' 'win-x64' '--locked-mode'
        Invoke-SelfContainedPublish
        Pop-Location
        Write-Host "  Self-contained publish 完了" -ForegroundColor Green
    }
    else {
        Write-Host "  publish をスキップしました" -ForegroundColor Yellow
        Assert-SelfContainedPublishLayout $appPublishOutput $updaterPublishOutput
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

    # 配布元 asset を先に列挙・検証してから公開先を変更する
    $pubDist = Join-Path $pubRoot "dist"
    if (-not (Test-Path $pubDist)) { New-Item -ItemType Directory -Path $pubDist -Force | Out-Null }
    $releaseAssets = @()
    if ($releasePackagePaths -ne $null -and $releasePackagePaths.Count -gt 0) {
        $releaseAssets = @($releasePackagePaths | ForEach-Object { Get-Item $_ })
    }
    else {
        $releaseAssets = @(Get-ChildItem (Join-Path $distDir "*.zip") -File)
    }
    if ($releaseAssets.Count -eq 0) {
        throw "検証対象の release package がありません。先に publish.ps1 -PackageOnly を実行してください。"
    }
    foreach ($asset in $releaseAssets) {
        Assert-PortableReleasePackageLayout $asset.FullName
    }

    $version = Get-AppVersion
    $currentVersionPattern = "bemusicseeker-unofficial-fork-v$version*.zip"
    Get-ChildItem $pubDist -Filter $currentVersionPattern -File | ForEach-Object {
        Remove-Item $_.FullName -Force
        Write-Host "  削除: dist\$($_.Name)"
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
    $files = @("README.md", "README.ja.md", "LICENSE", "ThirdPartyNotices.txt", "ThirdPartyNotices.ja.txt")
    foreach ($f in $files) {
        Copy-Item (Join-Path $devRoot $f) (Join-Path $pubRoot $f) -Force
        Write-Host "  コピー: $f"
    }
    $publicVersionPath = Join-Path $pubRoot "version.txt"
    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($publicVersionPath, $version, $utf8NoBom)
    Write-Host "  生成: version.txt"

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
