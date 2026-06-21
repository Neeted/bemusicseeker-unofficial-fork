<#
.SYNOPSIS
    公開用リポジトリでのリリースドラフト作成と公開を自動化するスクリプト

.DESCRIPTION
    -CreateDraft:
      1. 公開用リポジトリの version.txt と dist/*.zip から update.json を生成
      2. 変更を Release vX.X.X.X としてコミットし、タグを作成
      3. タグだけを push して GitHub Release draft を作成 / 更新

    -PublishDraft:
      1. draft Release と tag の整合性を確認
      2. draft Release を publish
      3. 同じリリースコミットを公開ブランチへ push し、raw GitHub の update.json/version.txt を公開
#>
param(
    [switch]$CreateDraft,
    [switch]$PublishDraft
)

$ErrorActionPreference = "Stop"

$devRoot = "D:\work\BeMusicSeeker-decomp"
$pubRoot = "D:\github\bemusicseeker-unofficial-fork"
$publicRepoOwner = "Neeted"
$publicRepoName = "bemusicseeker-unofficial-fork"
$publicBranch = "main"

function Assert-ExactlyOneMode {
    if (($CreateDraft -and $PublishDraft) -or (-not $CreateDraft -and -not $PublishDraft)) {
        throw "実行モードを 1 つ指定してください: -CreateDraft または -PublishDraft"
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

function Get-ReleaseContext {
    if (-not (Test-Path "version.txt")) {
        throw "version.txt が見つかりません。"
    }

    $version = (Get-Content "version.txt" -Raw).Trim()
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "version.txt が空です。"
    }

    $tag = "v$version"
    $notesPath = Join-Path $devRoot "release notes\$tag リリースノート.md"
    if (-not (Test-Path $notesPath -PathType Leaf)) {
        throw "リリースノートが見つかりません: $notesPath"
    }

    $zipPattern = "bemusicseeker-unofficial-fork-${tag}*.zip"
    $releaseAssets = @(Get-ChildItem -Path "dist" -Filter $zipPattern -File | Sort-Object Name)
    if ($releaseAssets.Count -eq 0) {
        throw "リリース用パッケージが見つかりません: dist\$zipPattern`n事前に publish.ps1 を実行してください。"
    }

    return [PSCustomObject]@{
        Version = $version
        Tag = $tag
        NotesPath = $notesPath
        ReleaseAssets = $releaseAssets
    }
}

function Get-ReleaseAssetMetadata($asset, $context) {
    $normalName = "bemusicseeker-unofficial-fork-$($context.Tag).zip"
    $metadataName = "bemusicseeker-unofficial-fork-$($context.Tag)-with-metadata.zip"

    if ($asset.Name -eq $normalName) {
        $kind = "app"
        $label = "本体のみ"
        $includesChartInfoMetadata = $false
    }
    elseif ($asset.Name -eq $metadataName) {
        $kind = "app-with-metadata"
        $label = "譜面解析済みメタデータ同梱版"
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
    $assetMetadata = @($context.ReleaseAssets | ForEach-Object { Get-ReleaseAssetMetadata $_ $context })
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

function Assert-OnPublicBranch {
    $currentBranch = (git branch --show-current).Trim()
    if ($currentBranch -ne $publicBranch) {
        throw "公開用リポジトリは $publicBranch ブランチで実行してください。現在のブランチ: $currentBranch"
    }
}

function Assert-RemoteBranchMatchesLocalHead {
    $localCommit = (git rev-parse "HEAD").Trim()
    $remoteLine = git ls-remote --heads origin "refs/heads/$publicBranch"
    if ([string]::IsNullOrWhiteSpace($remoteLine)) {
        throw "remote branch が見つかりません: $publicBranch"
    }

    $remoteCommit = ($remoteLine -split "\s+")[0]
    if ($remoteCommit -ne $localCommit) {
        throw "remote $publicBranch は現在の release commit を指していません: local=$localCommit remote=$remoteCommit"
    }
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
    New-ReleaseCommitAndTag $context

    Write-Host "  タグだけを push します..."
    git push origin $context.Tag
    if ($LASTEXITCODE -ne 0) { throw "tag の push に失敗しました。" }
    Assert-RemoteTagMatchesLocal $context.Tag

    $assetPaths = @($context.ReleaseAssets | ForEach-Object { $_.FullName })
    if (Get-ReleaseExists $context.Tag) {
        if (-not (Get-ReleaseIsDraft $context.Tag)) {
            throw "GitHub Release $($context.Tag) は既に公開済みです。"
        }

        Write-Host "  既存 draft Release を更新します..."
        gh release edit $context.Tag --title $context.Tag --notes-file $context.NotesPath --draft
        if ($LASTEXITCODE -ne 0) { throw "GitHub Release draft の更新に失敗しました。" }
        gh release upload $context.Tag @assetPaths --clobber
        if ($LASTEXITCODE -ne 0) { throw "GitHub Release asset のアップロードに失敗しました。" }
    }
    else {
        Write-Host "  GitHub Release draft を作成します..."
        gh release create $context.Tag @assetPaths --title $context.Tag --notes-file $context.NotesPath --verify-tag --draft
        if ($LASTEXITCODE -ne 0) { throw "GitHub Release draft の作成に失敗しました。" }
    }

    Write-Host "  draft Release 作成完了。公開ブランチはまだ push していません。" -ForegroundColor Green
}

function Invoke-PublishDraft($context) {
    Write-Host "`n=== ドラフトリリースの公開 ===" -ForegroundColor Cyan
    Assert-OnPublicBranch
    if (-not (Get-ReleaseExists $context.Tag)) {
        throw "GitHub Release draft が見つかりません: $($context.Tag)"
    }

    Assert-RemoteTagMatchesLocal $context.Tag

    Write-Host "  リリースコミットを公開ブランチへ push します..."
    git push origin "HEAD:refs/heads/$publicBranch"
    if ($LASTEXITCODE -ne 0) { throw "公開ブランチへの push に失敗しました。" }
    Assert-RemoteBranchMatchesLocalHead

    if (Get-ReleaseIsDraft $context.Tag) {
        Write-Host "  draft Release を publish します..."
        gh release edit $context.Tag --draft=false --title $context.Tag --notes-file $context.NotesPath
        if ($LASTEXITCODE -ne 0) { throw "GitHub Release draft の publish に失敗しました。" }
    }
    else {
        Write-Host "  GitHub Release は既に公開済みです。raw update.json/version.txt の push 済み状態を確認しました。" -ForegroundColor Yellow
    }

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
    $context = Get-ReleaseContext
    Write-Host "  対象バージョン: $($context.Tag)"
    Write-Host "  リリースノート: $($context.NotesPath)"
    foreach ($asset in $context.ReleaseAssets) {
        Write-Host "  asset: dist\$($asset.Name)"
    }

    if ($CreateDraft) {
        Invoke-CreateDraft $context
    }
    else {
        Invoke-PublishDraft $context
    }
}
finally {
    Pop-Location
}
