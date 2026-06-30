<#
.SYNOPSIS
    公開用リポジトリでのリリースドラフト作成と公開を自動化するスクリプト

.DESCRIPTION
    -CreateDraft:
      1. 開発用リポジトリの AssemblyInformationalVersion と公開用リポジトリの dist/*.zip から update.json を生成
      2. 変更を Release vX.X.X.X としてコミットし、タグを作成
      3. タグだけを push して GitHub Release draft を作成 / 更新

    -UpdateDraftBody:
      1. 既存 draft Release が未公開であることを確認
      2. 開発用リポジトリの release notes だけを draft Release 本文へ反映

    -RecreateDraft:
      1. 既存 draft Release と tag を削除
      2. 公開用リポジトリの release commit を origin/main へ戻す（作業ツリーの publish 済みファイルは保持）
      3. 現在の作業ツリーから Release vX.X.X.X commit / tag / draft Release を作り直す

    -CreatePrereleaseDraft:
      1. 現在の publish 済みパッケージとリリースノートから preview tag の draft Release を作成 / 更新
      2. ローカル commit / tag、update.json、公開ブランチは変更しない
      3. GitHub 上で手動で pre-release に変更して検証する
      4. tag 名は vX.X.X.X-<PreviewSuffix>。既定値は preview.1
         例: .\scripts\release.ps1 -CreatePrereleaseDraft -PreviewSuffix preview.2

    -PublishDraft:
      1. Release、tag、現在の HEAD が同じ release commit を指すことを確認
      2. draft Release を publish して release asset を公開
      3. 同じリリースコミットを公開ブランチへ push し、raw GitHub の update.json/version.txt が新バージョンを返すことを確認
#>
param(
    [switch]$CreateDraft,
    [switch]$UpdateDraftBody,
    [switch]$RecreateDraft,
    [switch]$CreatePrereleaseDraft,
    [switch]$PublishDraft,
    [string]$PreviewSuffix = "preview.1"
)

$ErrorActionPreference = "Stop"

$devRoot = "D:\work\BeMusicSeeker-decomp"
$pubRoot = "D:\github\bemusicseeker-unofficial-fork"
$publicRepoOwner = "Neeted"
$publicRepoName = "bemusicseeker-unofficial-fork"
$publicBranch = "main"

function Assert-ExactlyOneMode {
    $modeCount = @($CreateDraft, $UpdateDraftBody, $RecreateDraft, $CreatePrereleaseDraft, $PublishDraft).Where({ $_ }).Count
    if ($modeCount -ne 1) {
        throw "実行モードを 1 つ指定してください: -CreateDraft, -UpdateDraftBody, -RecreateDraft, -CreatePrereleaseDraft, -PublishDraft"
    }
}

function Assert-GhAuthenticated {
    Write-Host "  gh コマンドの認証状態を確認中..."
    gh auth status 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "gh コマンドがインストールされていないか、認証されていません。事前に 'gh auth login' を実行してください。"
    }
    Write-Host "  gh 認証 OK" -ForegroundColor Green
}

function Get-ReleaseAssetNamePriority($name, $tag) {
    if ($name -eq "bemusicseeker-unofficial-fork-$tag.zip") { return 0 }
    if ($name -eq "bemusicseeker-unofficial-fork-$tag-with-metadata.zip") { return 1 }
    return 99
}

function Get-UpdateAssetKindPriority($kind) {
    if ($kind -eq "app") { return 0 }
    if ($kind -eq "app-with-metadata") { return 1 }
    return 99
}

function Sort-UpdateManifestAssets($assets) {
    return ,@($assets | Sort-Object @{ Expression = { Get-UpdateAssetKindPriority $_.kind } }, fileName)
}

function Get-AppVersion {
    $asmInfoPath = Join-Path $devRoot "Properties\AssemblyInfo.cs"
    if (-not (Test-Path $asmInfoPath -PathType Leaf)) {
        throw "AssemblyInfo.cs が見つかりません: $asmInfoPath"
    }

    $content = Get-Content $asmInfoPath -Raw
    if ($content -match 'AssemblyInformationalVersion\("([^"]+)"\)') {
        $version = $Matches[1].Trim()
    }
    else {
        throw "AssemblyInformationalVersion が見つかりません: $asmInfoPath"
    }

    try {
        [void][System.Version]::Parse($version)
    }
    catch {
        throw "AssemblyInformationalVersion が System.Version として解釈できません: $version"
    }
    return $version
}

function Get-ZipEntryNames($asset) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($asset.FullName)
    try {
        $entries = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $isDirectoryEntry = $entry.FullName.EndsWith("/", [System.StringComparison]::Ordinal) -or
                $entry.FullName.EndsWith("\", [System.StringComparison]::Ordinal)
            $normalized = $entry.FullName.Replace('\', '/').Trim('/')
            if ($isDirectoryEntry) {
                $normalized = "$normalized/"
            }
            if (-not [string]::IsNullOrWhiteSpace($normalized)) {
                [void]$entries.Add($normalized)
            }
        }
        return ,$entries
    }
    finally {
        $archive.Dispose()
    }
}

function Test-ZipEntryOrDescendantExists($entryNames, $relativePath) {
    $normalized = $relativePath.Replace('\', '/').Trim('/')
    return $entryNames.Contains($normalized) -or
        @($entryNames | Where-Object { $_.StartsWith("$normalized/", [System.StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
}

function Assert-ReleasePackageLayout($asset) {
    $entryNames = Get-ZipEntryNames $asset

    $requiredFiles = @(
        "BeMusicSeeker.exe",
        "BeMusicSeeker.exe.config",
        "BeMusicSeeker.Updater.exe",
        "test.mp3",
        "libs/SevenZipExtractor.dll",
        "libs/OggVorbis.NET64.dll",
        "libs/x64/7z.dll",
        "libs/x64/bass.dll",
        "libs/x64/sqlite3.dll",
        "native/Everything3_x64.dll",
        "native/EverythingBridge_x64.dll",
        "lang/ja-JP.json",
        "update-managed-files.txt"
    )
    foreach ($relativePath in $requiredFiles) {
        if (-not $entryNames.Contains($relativePath)) {
            throw "release asset に必須ファイルがありません: $($asset.Name): $relativePath"
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
        if (Test-ZipEntryOrDescendantExists $entryNames $relativePath) {
            throw "release asset に禁止された配置が残っています: $($asset.Name): $relativePath"
        }
    }

    $isMetadataPackage = $asset.Name -like "*-with-metadata.zip"
    $hasMetadataArchive = $entryNames.Contains("chart-info-metadata.7z")
    if ($isMetadataPackage -and -not $hasMetadataArchive) {
        throw "metadata 同梱 release asset に chart-info-metadata.7z がありません: $($asset.Name)"
    }
    if (-not $isMetadataPackage -and $hasMetadataArchive) {
        throw "通常版 release asset に chart-info-metadata.7z が含まれています: $($asset.Name)"
    }
}

function Get-ReleaseContext {
    param(
        [bool]$RequireAssets = $true
    )

    $version = Get-AppVersion
    $tag = "v$version"
    $notesPath = Join-Path $devRoot "release notes\$tag リリースノート.md"
    if (-not (Test-Path $notesPath -PathType Leaf)) {
        throw "リリースノートが見つかりません: $notesPath"
    }

    $zipPattern = "bemusicseeker-unofficial-fork-${tag}*.zip"
    $releaseAssets = @()
    if (Test-Path "dist" -PathType Container) {
        $releaseAssets = @(Get-ChildItem -Path "dist" -Filter $zipPattern -File |
            Sort-Object @{ Expression = { Get-ReleaseAssetNamePriority $_.Name $tag } }, Name)
    }
    if ($RequireAssets -and $releaseAssets.Count -eq 0) {
        throw "リリース用パッケージが見つかりません: dist\$zipPattern`n事前に publish.ps1 を実行してください。"
    }
    if ($RequireAssets) {
        foreach ($asset in $releaseAssets) {
            Assert-ReleasePackageLayout $asset
        }
    }

    return [PSCustomObject]@{
        Version = $version
        Tag = $tag
        BaseTag = $tag
        NotesPath = $notesPath
        ReleaseAssets = $releaseAssets
    }
}

function Get-PrereleaseDraftContext($context) {
    if ([string]::IsNullOrWhiteSpace($PreviewSuffix)) {
        throw "-PreviewSuffix には空でない値を指定してください。"
    }

    $normalizedSuffix = $PreviewSuffix.Trim()
    if ($normalizedSuffix.StartsWith("-")) {
        $normalizedSuffix = $normalizedSuffix.Substring(1)
    }
    if ([string]::IsNullOrWhiteSpace($normalizedSuffix)) {
        throw "-PreviewSuffix には空でない値を指定してください。"
    }

    return [PSCustomObject]@{
        Version = $context.Version
        Tag = "$($context.BaseTag)-$normalizedSuffix"
        BaseTag = $context.BaseTag
        NotesPath = $context.NotesPath
        ReleaseAssets = $context.ReleaseAssets
    }
}

function Get-ReleaseAssetMetadata($asset, $context) {
    $normalName = "bemusicseeker-unofficial-fork-$($context.Tag).zip"
    $metadataName = "bemusicseeker-unofficial-fork-$($context.Tag)-with-metadata.zip"

    if ($asset.Name -eq $normalName) {
        $kind = "app"
        $label = "App only"
        $includesChartInfoMetadata = $false
    }
    elseif ($asset.Name -eq $metadataName) {
        $kind = "app-with-metadata"
        $label = "App with metadata bundle"
        $includesChartInfoMetadata = $true
    }
    else {
        throw "自動アップデート manifest に含められない asset 名です: $($asset.Name)"
    }

    return [PSCustomObject]@{
        kind = $kind
        label = $label
        fileName = $asset.Name
        url = "https://github.com/$publicRepoOwner/$publicRepoName/releases/download/$($context.Tag)/$($asset.Name)"
        sha256 = (Get-FileHash -Path $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        sizeBytes = $asset.Length
        includesChartInfoMetadata = $includesChartInfoMetadata
    }
}

function New-UpdateManifest($context) {
    $assetMetadata = Sort-UpdateManifestAssets @($context.ReleaseAssets | ForEach-Object { Get-ReleaseAssetMetadata $_ $context })
    if (-not ($assetMetadata | Where-Object { $_.kind -eq "app" })) {
        throw "通常版パッケージが見つかりません。update.json には本体のみ asset が必要です。"
    }

    $manifest = [PSCustomObject]@{
        schemaVersion = 1
        version = $context.Version
        releaseTag = $context.Tag
        releasePageUrl = "https://github.com/$publicRepoOwner/$publicRepoName/releases/tag/$($context.Tag)"
        packageFormatVersion = 1
        publishedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        minimumUpdaterVersion = "1"
        assets = $assetMetadata
    }

    $manifestPath = Join-Path $pubRoot "update.json"
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -Path $manifestPath -Encoding UTF8
    Write-Host "  update.json を生成: $manifestPath" -ForegroundColor Green
}

function Write-PublicVersionText($context) {
    $versionPath = Join-Path $pubRoot "version.txt"
    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($versionPath, $context.Version, $utf8NoBom)
    Write-Host "  version.txt を生成: $versionPath" -ForegroundColor Green
}

function Get-RawPublicFileUrl($relativePath) {
    $escapedPath = (($relativePath -split '[\\/]') | ForEach-Object { [uri]::EscapeDataString($_) }) -join "/"
    $cacheBuster = [uri]::EscapeDataString((Get-Date).ToUniversalTime().Ticks.ToString())
    return "https://raw.githubusercontent.com/$publicRepoOwner/$publicRepoName/$publicBranch/$escapedPath`?cb=$cacheBuster"
}

function Get-RawPublicFileText($relativePath) {
    $url = Get-RawPublicFileUrl $relativePath
    try {
        $response = Invoke-WebRequest -Uri $url -Headers @{ "Cache-Control" = "no-cache"; "Pragma" = "no-cache" }
    }
    catch {
        throw "raw GitHub の取得に失敗しました: $url`n$($_.Exception.Message)"
    }

    if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
        throw "raw GitHub が成功ステータスを返しませんでした: $url status=$($response.StatusCode)"
    }
    return [string]$response.Content
}

function Assert-RawReleaseFilesPublished($context) {
    Write-Host "  raw GitHub の update.json/version.txt 反映を確認中..."

    $manifestText = Get-RawPublicFileText "update.json"
    try {
        $manifest = $manifestText | ConvertFrom-Json
    }
    catch {
        throw "raw update.json の JSON parse に失敗しました。`n$($_.Exception.Message)"
    }

    if ($manifest.version -ne $context.Version) {
        throw "raw update.json の version が期待値と一致しません: actual=$($manifest.version) expected=$($context.Version)"
    }
    if ($manifest.releaseTag -ne $context.Tag) {
        throw "raw update.json の releaseTag が期待値と一致しません: actual=$($manifest.releaseTag) expected=$($context.Tag)"
    }

    $versionText = (Get-RawPublicFileText "version.txt").Trim()
    if ($versionText -ne $context.Version) {
        throw "raw version.txt が期待値と一致しません: actual=$versionText expected=$($context.Version)"
    }

    Write-Host "  raw GitHub 反映 OK" -ForegroundColor Green
}

function Assert-UpdateManifestCanBeGenerated($context) {
    $assetMetadata = @($context.ReleaseAssets | ForEach-Object { Get-ReleaseAssetMetadata $_ $context })
    if (-not ($assetMetadata | Where-Object { $_.kind -eq "app" })) {
        throw "通常版パッケージが見つかりません。update.json には本体のみ asset が必要です。"
    }
}

function Get-ReleaseExists($tag) {
    gh release view $tag 2>&1 | Out-Null
    return $LASTEXITCODE -eq 0
}

function Get-ReleaseIsDraft($tag) {
    $isDraft = gh release view $tag --json isDraft -q ".isDraft"
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub Release の状態取得に失敗しました: $tag"
    }
    return $isDraft.Trim().ToLowerInvariant() -eq "true"
}

function Get-ExpectedReleaseAssetNames($context) {
    return @($context.ReleaseAssets | ForEach-Object { $_.Name })
}

function Sync-ReleaseAssets($context) {
    $expectedNames = @(Get-ExpectedReleaseAssetNames $context)
    $remoteAssetsJson = gh release view $context.Tag --json assets
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub Release asset の取得に失敗しました: $($context.Tag)"
    }

    $remoteAssets = $remoteAssetsJson | ConvertFrom-Json
    $remoteNames = @($remoteAssets.assets | ForEach-Object { $_.name })
    foreach ($remoteName in $remoteNames) {
        if ($expectedNames -notcontains $remoteName) {
            Write-Host "  余剰 release asset を削除します: $remoteName"
            gh release delete-asset $context.Tag $remoteName --yes
            if ($LASTEXITCODE -ne 0) { throw "余剰 release asset の削除に失敗しました: $remoteName" }
        }
    }

    $assetPaths = @($context.ReleaseAssets | ForEach-Object { $_.FullName })
    gh release upload $context.Tag @assetPaths --clobber
    if ($LASTEXITCODE -ne 0) { throw "GitHub Release asset のアップロードに失敗しました。" }

    Assert-ReleaseAssetsMatchLocal $context
}

function Assert-ReleaseAssetsMatchLocal($context) {
    $remoteAssetsJson = gh release view $context.Tag --json assets
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub Release asset の取得に失敗しました: $($context.Tag)"
    }

    $remoteAssets = @((($remoteAssetsJson | ConvertFrom-Json).assets) | Sort-Object name)
    $expectedAssets = @($context.ReleaseAssets | Sort-Object Name)
    if ($remoteAssets.Count -ne $expectedAssets.Count) {
        throw "GitHub Release asset 数が update.json と一致しません: remote=$($remoteAssets.Count) expected=$($expectedAssets.Count)"
    }

    for ($i = 0; $i -lt $expectedAssets.Count; $i++) {
        $expected = $expectedAssets[$i]
        $remote = $remoteAssets[$i]
        if ($remote.name -ne $expected.Name) {
            throw "GitHub Release asset 名が update.json と一致しません: remote=$($remote.name) expected=$($expected.Name)"
        }
        if ([int64]$remote.size -ne [int64]$expected.Length) {
            throw "GitHub Release asset size が update.json と一致しません: $($expected.Name)"
        }
    }
}

function Sync-DraftReleaseContent($context) {
    gh release edit $context.Tag --title $context.Tag --notes-file $context.NotesPath --draft
    if ($LASTEXITCODE -ne 0) { throw "GitHub Release draft の更新に失敗しました: $($context.Tag)" }
    Sync-ReleaseAssets $context
}

function Assert-RemoteTagMatchesLocal($tag) {
    $localCommit = (git rev-parse "$tag^{commit}").Trim()
    $remoteTagLine = git ls-remote --tags origin "refs/tags/$tag"
    if ([string]::IsNullOrWhiteSpace($remoteTagLine)) {
        throw "remote tag が見つかりません: $tag"
    }

    $remoteCommit = ($remoteTagLine -split "\s+")[0]
    if ($remoteCommit -ne $localCommit) {
        throw "remote tag と local tag の commit が一致しません: local=$localCommit remote=$remoteCommit"
    }
}

function Assert-HeadMatchesReleaseTag($tag) {
    $headCommit = (git rev-parse "HEAD").Trim()
    $tagCommit = (git rev-parse "$tag^{commit}").Trim()
    if ($headCommit -ne $tagCommit) {
        throw "現在の HEAD は release tag と一致しません: tag=$tag head=$headCommit tagCommit=$tagCommit`n-PublishDraft は -CreateDraft / -RecreateDraft で作成した release commit から実行してください。"
    }
}

function Get-RemoteTagCommit($tag) {
    $remoteTagLine = git ls-remote --tags origin "refs/tags/$tag"
    if ([string]::IsNullOrWhiteSpace($remoteTagLine)) {
        return $null
    }
    return ($remoteTagLine -split "\s+")[0]
}

function Get-LocalTagCommit($tag) {
    $existingTag = git tag -l $tag
    if (-not $existingTag) {
        return $null
    }
    return (git rev-parse "$tag^{commit}").Trim()
}

function Get-RemoteBranchCommit {
    $remoteLine = git ls-remote --heads origin "refs/heads/$publicBranch"
    if ([string]::IsNullOrWhiteSpace($remoteLine)) {
        throw "remote branch が見つかりません: $publicBranch"
    }
    return ($remoteLine -split "\s+")[0]
}

function Assert-GitCommitObjectAvailable($commit, $label) {
    git cat-file -e "$commit^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "$label の commit object が local repo にありません: $commit`n先に公開用リポジトリで 'git fetch origin $publicBranch --tags' を実行してください。"
    }
}

function Test-GitCommitIsAncestor($ancestorCommit, $descendantCommit) {
    Assert-GitCommitObjectAvailable $ancestorCommit "ancestor"
    Assert-GitCommitObjectAvailable $descendantCommit "descendant"
    git merge-base --is-ancestor $ancestorCommit $descendantCommit
    if ($LASTEXITCODE -eq 0) {
        return $true
    }
    if ($LASTEXITCODE -eq 1) {
        return $false
    }
    throw "commit の祖先判定に失敗しました: ancestor=$ancestorCommit descendant=$descendantCommit"
}

function Assert-OnPublicBranch {
    $currentBranch = (git branch --show-current).Trim()
    if ($currentBranch -ne $publicBranch) {
        throw "公開用リポジトリは $publicBranch ブランチで実行してください。現在のブランチ: $currentBranch"
    }
}

function Assert-RemoteBranchMatchesLocalHead {
    $localCommit = (git rev-parse "HEAD").Trim()
    $remoteCommit = Get-RemoteBranchCommit
    if ($remoteCommit -ne $localCommit) {
        throw "remote $publicBranch は現在の release commit を指していません: local=$localCommit remote=$remoteCommit"
    }
}

function Assert-DraftReleaseCanBeRecreated($context) {
    if (Get-ReleaseExists $context.Tag) {
        if (-not (Get-ReleaseIsDraft $context.Tag)) {
            throw "GitHub Release $($context.Tag) は既に公開済みです。再作成できません。"
        }
    }

    $remoteBranchCommit = Get-RemoteBranchCommit
    $remoteTagCommit = Get-RemoteTagCommit $context.Tag
    if ($remoteTagCommit -and (Test-GitCommitIsAncestor $remoteTagCommit $remoteBranchCommit)) {
        throw "remote $publicBranch が $($context.Tag) の commit を含んでいます。既に公開ブランチへ反映済みの tag は再作成できません。"
    }
}

function Assert-ReleaseCommitCanBeResetForRecreate($context) {
    $remoteBranchCommit = Get-RemoteBranchCommit
    $localHeadCommit = (git rev-parse "HEAD").Trim()
    if ($localHeadCommit -eq $remoteBranchCommit) {
        return
    }

    if (-not (Test-GitCommitIsAncestor $remoteBranchCommit $localHeadCommit)) {
        throw "現在の HEAD は origin/$publicBranch からの直系ではありません。release commit を安全に外せないため停止します。"
    }

    $aheadCommits = @(git rev-list --reverse "$remoteBranchCommit..HEAD")
    if ($aheadCommits.Count -ne 1) {
        throw "origin/$publicBranch からの ahead commit が 1 件ではありません: count=$($aheadCommits.Count)。-RecreateDraft は直前の release commit だけを外す場合に限定します。"
    }

    $localTagCommit = Get-LocalTagCommit $context.Tag
    if ($localTagCommit -and $localTagCommit -ne $localHeadCommit) {
        throw "local tag $($context.Tag) が現在の HEAD を指していません: tag=$localTagCommit head=$localHeadCommit"
    }

    $remoteTagCommit = Get-RemoteTagCommit $context.Tag
    if ($remoteTagCommit -and $remoteTagCommit -ne $localHeadCommit) {
        throw "remote tag $($context.Tag) が現在の HEAD を指していません: tag=$remoteTagCommit head=$localHeadCommit"
    }
}

function Assert-RecreateDraftPreconditions($context) {
    Assert-DraftReleaseCanBeRecreated $context
    Assert-ReleaseCommitCanBeResetForRecreate $context
    Assert-UpdateManifestCanBeGenerated $context
}

function Remove-DraftReleaseAndTags($context) {
    if (Get-ReleaseExists $context.Tag) {
        if (-not (Get-ReleaseIsDraft $context.Tag)) {
            throw "GitHub Release $($context.Tag) は既に公開済みです。削除できません。"
        }
        Write-Host "  既存 draft Release を削除します..."
        gh release delete $context.Tag --yes
        if ($LASTEXITCODE -ne 0) { throw "GitHub Release draft の削除に失敗しました。" }
    }
    else {
        Write-Host "  既存 draft Release はありません" -ForegroundColor Yellow
    }

    if (Get-RemoteTagCommit $context.Tag) {
        Write-Host "  remote tag $($context.Tag) を削除します..."
        git push origin ":refs/tags/$($context.Tag)"
        if ($LASTEXITCODE -ne 0) { throw "remote tag の削除に失敗しました: $($context.Tag)" }
    }
    else {
        Write-Host "  remote tag $($context.Tag) はありません" -ForegroundColor Yellow
    }

    if (Get-LocalTagCommit $context.Tag) {
        Write-Host "  local tag $($context.Tag) を削除します..."
        git tag -d $context.Tag
        if ($LASTEXITCODE -ne 0) { throw "local tag の削除に失敗しました: $($context.Tag)" }
    }
    else {
        Write-Host "  local tag $($context.Tag) はありません" -ForegroundColor Yellow
    }
}

function Reset-ReleaseCommitPreservingWorktree {
    $remoteBranchCommit = Get-RemoteBranchCommit
    $localHeadCommit = (git rev-parse "HEAD").Trim()
    if ($localHeadCommit -eq $remoteBranchCommit) {
        Write-Host "  HEAD は既に origin/$publicBranch と一致しています" -ForegroundColor Yellow
        return
    }

    $aheadCount = (git rev-list --count "$remoteBranchCommit..HEAD").Trim()
    Write-Host "  release commit を外します: origin/$publicBranch..HEAD=$aheadCount commit(s)"
    git reset --mixed $remoteBranchCommit
    if ($LASTEXITCODE -ne 0) { throw "release commit の取り消しに失敗しました。" }
    Write-Host "  作業ツリーのファイルは保持しました" -ForegroundColor Green
}

function New-ReleaseCommitAndTag($context) {
    git add .
    $status = @(git status --porcelain)
    if ($status.Count -gt 0) {
        Write-Host "  変更をコミットします..."
        git commit -m "Release $($context.Tag)"
        if ($LASTEXITCODE -ne 0) { throw "リリースコミットの作成に失敗しました。" }
        Write-Host "  コミット完了" -ForegroundColor Green
    }
    else {
        Write-Host "  コミットする変更はありません（現在の HEAD を使います）" -ForegroundColor Yellow
    }

    $existingTag = git tag -l $context.Tag
    if ($existingTag) {
        $tagCommit = (git rev-parse "$($context.Tag)^{commit}").Trim()
        $headCommit = (git rev-parse "HEAD").Trim()
        if ($tagCommit -ne $headCommit) {
            throw "既存 tag $($context.Tag) が現在の HEAD を指していません。"
        }
        Write-Host "  タグ $($context.Tag) は現在の HEAD を指しています" -ForegroundColor Yellow
    }
    else {
        Write-Host "  タグ $($context.Tag) を作成します..."
        git tag $context.Tag
        if ($LASTEXITCODE -ne 0) { throw "タグ作成に失敗しました。" }
        Write-Host "  タグ作成完了" -ForegroundColor Green
    }
}

function Invoke-CreateDraft($context) {
    Write-Host "`n=== ドラフトリリースの作成 ===" -ForegroundColor Cyan
    Assert-OnPublicBranch
    New-UpdateManifest $context
    Write-PublicVersionText $context
    New-ReleaseCommitAndTag $context

    Write-Host "  タグだけを push します..."
    git push origin $context.Tag
    if ($LASTEXITCODE -ne 0) { throw "tag の push に失敗しました。" }
    Assert-RemoteTagMatchesLocal $context.Tag

    if (Get-ReleaseExists $context.Tag) {
        if (-not (Get-ReleaseIsDraft $context.Tag)) {
            throw "GitHub Release $($context.Tag) は既に公開済みです。"
        }

        Write-Host "  既存 draft Release を更新します..."
        Sync-DraftReleaseContent $context
    }
    else {
        Write-Host "  GitHub Release draft を作成します..."
        $assetPaths = @($context.ReleaseAssets | ForEach-Object { $_.FullName })
        gh release create $context.Tag @assetPaths --title $context.Tag --notes-file $context.NotesPath --verify-tag --draft
        if ($LASTEXITCODE -ne 0) { throw "GitHub Release draft の作成に失敗しました。" }
        Assert-ReleaseAssetsMatchLocal $context
    }

    Write-Host "  draft Release 作成完了。公開ブランチはまだ push していません。" -ForegroundColor Green
}

function Invoke-CreatePrereleaseDraft($context) {
    Write-Host "`n=== プレリリース検証用ドラフトの作成 ===" -ForegroundColor Cyan
    Assert-OnPublicBranch
    $previewContext = Get-PrereleaseDraftContext $context
    Write-Host "  preview tag: $($previewContext.Tag)"
    Write-Host "  base release: $($previewContext.BaseTag)"

    if (Get-ReleaseExists $previewContext.Tag) {
        if (-not (Get-ReleaseIsDraft $previewContext.Tag)) {
            throw "GitHub Release $($previewContext.Tag) は既に公開済みです。検証用 draft として更新できません。"
        }

        Write-Host "  既存 preview draft Release を更新します..."
        Sync-DraftReleaseContent $previewContext
    }
    else {
        if (Get-RemoteTagCommit $previewContext.Tag) {
            throw "remote tag $($previewContext.Tag) は既に存在します。別の -PreviewSuffix を指定してください。"
        }

        Write-Host "  preview draft Release を作成します..."
        $assetPaths = @($previewContext.ReleaseAssets | ForEach-Object { $_.FullName })
        gh release create $previewContext.Tag @assetPaths --target $publicBranch --title $previewContext.Tag --notes-file $previewContext.NotesPath --draft
        if ($LASTEXITCODE -ne 0) { throw "preview draft Release の作成に失敗しました。" }
        Assert-ReleaseAssetsMatchLocal $previewContext
    }

    Write-Host "  preview draft Release 作成完了。ローカル commit/tag と公開ブランチは変更していません。" -ForegroundColor Green
}

function Invoke-UpdateDraftBody($context) {
    Write-Host "`n=== ドラフトリリース本文の更新 ===" -ForegroundColor Cyan
    Assert-OnPublicBranch
    if (-not (Get-ReleaseExists $context.Tag)) {
        throw "GitHub Release draft が見つかりません: $($context.Tag)"
    }
    if (-not (Get-ReleaseIsDraft $context.Tag)) {
        throw "GitHub Release $($context.Tag) は既に公開済みです。"
    }

    gh release edit $context.Tag --title $context.Tag --notes-file $context.NotesPath --draft
    if ($LASTEXITCODE -ne 0) { throw "GitHub Release draft 本文の更新に失敗しました。" }
    Write-Host "  draft Release 本文を更新しました。" -ForegroundColor Green
}

function Invoke-RecreateDraft($context) {
    Write-Host "`n=== ドラフトリリースの再作成 ===" -ForegroundColor Cyan
    Assert-OnPublicBranch
    Assert-RecreateDraftPreconditions $context
    Remove-DraftReleaseAndTags $context
    Reset-ReleaseCommitPreservingWorktree
    New-UpdateManifest $context
    Write-PublicVersionText $context
    New-ReleaseCommitAndTag $context

    Write-Host "  タグだけを push します..."
    git push origin $context.Tag
    if ($LASTEXITCODE -ne 0) { throw "tag の push に失敗しました。" }
    Assert-RemoteTagMatchesLocal $context.Tag

    Write-Host "  GitHub Release draft を作成します..."
    $assetPaths = @($context.ReleaseAssets | ForEach-Object { $_.FullName })
    gh release create $context.Tag @assetPaths --title $context.Tag --notes-file $context.NotesPath --verify-tag --draft
    if ($LASTEXITCODE -ne 0) { throw "GitHub Release draft の作成に失敗しました。" }
    Assert-ReleaseAssetsMatchLocal $context

    Write-Host "  draft Release 再作成完了。公開ブランチはまだ push していません。" -ForegroundColor Green
}

function Invoke-PublishDraft($context) {
    Write-Host "`n=== ドラフトリリースの公開 ===" -ForegroundColor Cyan
    Assert-OnPublicBranch
    if (-not (Get-ReleaseExists $context.Tag)) {
        throw "GitHub Release draft が見つかりません: $($context.Tag)"
    }
    $isDraft = Get-ReleaseIsDraft $context.Tag

    Assert-RemoteTagMatchesLocal $context.Tag
    Assert-HeadMatchesReleaseTag $context.Tag
    Assert-ReleaseAssetsMatchLocal $context

    if ($isDraft) {
        Write-Host "  draft Release を publish します..."
        gh release edit $context.Tag --draft=false --title $context.Tag --notes-file $context.NotesPath
        if ($LASTEXITCODE -ne 0) { throw "GitHub Release draft の publish に失敗しました。" }
    }
    else {
        Write-Host "  GitHub Release は既に公開済みです。raw update.json/version.txt の反映を再試行します。" -ForegroundColor Yellow
    }

    Write-Host "  リリースコミットを公開ブランチへ push します..."
    git push origin "HEAD:refs/heads/$publicBranch"
    if ($LASTEXITCODE -ne 0) { throw "公開ブランチへの push に失敗しました。" }
    Assert-RemoteBranchMatchesLocalHead
    Assert-RawReleaseFilesPublished $context

    Write-Host "  Release と raw update.json/version.txt の公開が完了しました。" -ForegroundColor Green
}

Write-Host "=== ステップ 0: 前提条件の確認 ===" -ForegroundColor Cyan
Assert-ExactlyOneMode
Assert-GhAuthenticated

if (-not (Test-Path $pubRoot)) {
    throw "公開用リポジトリが見つかりません: $pubRoot"
}

Push-Location $pubRoot
try {
    Write-Host "  作業ディレクトリ: $PWD"

    Write-Host "`n=== バージョンとパッケージの確認 ===" -ForegroundColor Cyan
    $context = Get-ReleaseContext -RequireAssets:(-not $UpdateDraftBody)
    Write-Host "  対象バージョン: $($context.Tag)"
    Write-Host "  リリースノート: $($context.NotesPath)"
    foreach ($asset in $context.ReleaseAssets) {
        Write-Host "  asset: dist\$($asset.Name)"
    }

    if ($CreateDraft) {
        Invoke-CreateDraft $context
    }
    elseif ($UpdateDraftBody) {
        Invoke-UpdateDraftBody $context
    }
    elseif ($RecreateDraft) {
        Invoke-RecreateDraft $context
    }
    elseif ($CreatePrereleaseDraft) {
        Invoke-CreatePrereleaseDraft $context
    }
    else {
        Invoke-PublishDraft $context
    }
}
finally {
    Pop-Location
}
