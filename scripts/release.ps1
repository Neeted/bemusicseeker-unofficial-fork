<#
.SYNOPSIS
    同じリポジトリの配布物からリリース下書きを作成・公開するスクリプト

.DESCRIPTION
    -CreateDraft は main 上の配布物から update.json と GitHub Pages 用の HTML を
    生成し、生成物だけをコミットしてタグと draft Release を作成します。

    -PublishDraft はタグ、HEAD、配布 ZIP、追跡中の update.json、Release 資産を照合し、
    Release の公開後に main へ通常の push を行います。

    -CreatePrereleaseDraft は dev の HEAD に明示した preview tag を付け、main と追跡中の
    配布情報を変更せずに prerelease draft を作成します。
#>
param(
    [switch]$CreateDraft,
    [switch]$CreatePrereleaseDraft,
    [switch]$PublishDraft,
    [string]$PreviewSuffix = "preview.1"
)

$ErrorActionPreference = "Stop"

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$publicRepoOwner = "Neeted"
$publicRepoName = "bemusicseeker-unofficial-fork"
$publicBranch = "main"
$developmentBranch = "dev"
$publicSiteUrl = "https://neeted.github.io/bemusicseeker-unofficial-fork"
$distDir = Join-Path $root "dist"

Push-Location $root

try {

. (Join-Path $PSScriptRoot "portable-package-layout.ps1")

function Invoke-GhOutput {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $output = @(& gh @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "gh command failed with exit code $exitCode`: gh $($Arguments -join ' ')`n$($output -join [Environment]::NewLine)"
    }
    return [string]::Join([Environment]::NewLine, [string[]]$output)
}

function Invoke-GhChecked {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    & gh @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "gh command failed with exit code $LASTEXITCODE`: gh $($Arguments -join ' ')"
    }
}

function Invoke-GitChecked {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git command failed with exit code $LASTEXITCODE`: git $($Arguments -join ' ')"
    }
}

function Get-GitOutput {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $output = @(& git @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "git command failed with exit code $exitCode`: git $($Arguments -join ' ')`n$($output -join [Environment]::NewLine)"
    }
    return [string]::Join([Environment]::NewLine, [string[]]$output)
}

function Get-GitHead {
    return (Get-GitOutput @("rev-parse", "HEAD")).Trim()
}

function Assert-ExactlyOneMode {
    $modeCount = @($CreateDraft, $CreatePrereleaseDraft, $PublishDraft).Where({ $_ }).Count
    if ($modeCount -ne 1) {
        throw "実行モードを 1 つ指定してください: -CreateDraft, -CreatePrereleaseDraft, -PublishDraft"
    }
}

function Assert-GhAuthenticated {
    Write-Host "  gh コマンドの認証状態を確認中..."
    & gh auth status 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "gh コマンドがインストールされていないか、認証されていません。事前に 'gh auth login' を実行してください。"
    }
    Write-Host "  gh 認証 OK" -ForegroundColor Green
}

function Assert-OnBranch {
    param([Parameter(Mandatory)][string]$ExpectedBranch)

    $currentBranch = (Get-GitOutput @("branch", "--show-current")).Trim()
    if ($currentBranch -ne $ExpectedBranch) {
        throw "$ExpectedBranch ブランチで実行してください。現在のブランチ: $currentBranch"
    }
}

function Assert-WorkingTreeClean {
    $status = @(git status --porcelain)
    if ($LASTEXITCODE -ne 0) {
        throw "git status の取得に失敗しました。"
    }
    if ($status.Count -gt 0) {
        throw "作業ツリーに未コミットの差分があります。無関係な差分を含めずに実行してください。`n$($status -join [Environment]::NewLine)"
    }
}

function Get-AppVersion {
    $asmInfoPath = Join-Path $root "Properties\AssemblyInfo.cs"
    if (-not (Test-Path -LiteralPath $asmInfoPath -PathType Leaf)) {
        throw "AssemblyInfo.cs が見つかりません: $asmInfoPath"
    }

    $content = Get-Content -LiteralPath $asmInfoPath -Raw
    if ($content -notmatch 'AssemblyInformationalVersion\("([^"]+)"\)') {
        throw "AssemblyInformationalVersion が見つかりません: $asmInfoPath"
    }

    $version = $Matches[1].Trim()
    try {
        [void][System.Version]::Parse($version)
    }
    catch {
        throw "AssemblyInformationalVersion が System.Version として解釈できません: $version"
    }
    return $version
}

function Get-ReleaseAssetNamePriority {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Tag
    )

    if ($Name -eq "bemusicseeker-unofficial-fork-$Tag.zip") { return 0 }
    if ($Name -eq "bemusicseeker-unofficial-fork-$Tag-with-metadata.zip") { return 1 }
    return 99
}

function Get-UpdateAssetKindPriority {
    param([Parameter(Mandatory)][string]$Kind)

    if ($Kind -eq "app") { return 0 }
    if ($Kind -eq "app-with-metadata") { return 1 }
    return 99
}

function Sort-UpdateManifestAssets {
    param([Parameter(Mandatory)][object[]]$Assets)

    return ,@($Assets | Sort-Object @{ Expression = { Get-UpdateAssetKindPriority ([string]$_.kind) } }, fileName)
}

function Get-ReleaseContext {
    $version = Get-AppVersion
    $tag = "v$version"
    $notesPath = Join-Path $root "release notes\$tag リリースノート.md"
    if (-not (Test-Path -LiteralPath $notesPath -PathType Leaf)) {
        throw "リリースノートが見つかりません: $notesPath"
    }

    $normalName = "bemusicseeker-unofficial-fork-$tag.zip"
    $metadataName = "bemusicseeker-unofficial-fork-$tag-with-metadata.zip"
    $releaseAssets = @()
    if (Test-Path -LiteralPath $distDir -PathType Container) {
        $releaseAssets = @(Get-ChildItem -LiteralPath $distDir -File -Filter "bemusicseeker-unofficial-fork-$tag*.zip" |
            Sort-Object @{ Expression = { Get-ReleaseAssetNamePriority $_.Name $tag } }, Name)
    }

    foreach ($asset in $releaseAssets) {
        if ($asset.Name -ne $normalName -and $asset.Name -ne $metadataName) {
            throw "対象版の配布 ZIP 名が不正です: $($asset.Name)"
        }
    }

    $normalAsset = @($releaseAssets | Where-Object { $_.Name -eq $normalName })
    if ($normalAsset.Count -ne 1) {
        throw "通常版パッケージが見つかりません: $([IO.Path]::Combine('dist', $normalName))`n先に publish.ps1 を実行してください。"
    }
    foreach ($asset in $releaseAssets) {
        Assert-PortableReleasePackageLayout $asset.FullName
    }

    return [PSCustomObject]@{
        Version = $version
        Tag = $tag
        NotesPath = $notesPath
        ReleaseAssets = @($releaseAssets)
    }
}

function Get-PrereleaseTag {
    param([Parameter(Mandatory)][string]$BaseTag)

    $suffix = $PreviewSuffix.Trim()
    if ($suffix.StartsWith("-")) {
        $suffix = $suffix.Substring(1)
    }
    if ([string]::IsNullOrWhiteSpace($suffix) -or $suffix -match "\s") {
        throw "-PreviewSuffix には空白を含まない値を指定してください。"
    }
    return "$BaseTag-$suffix"
}

function Get-ReleaseAssetMetadata {
    param(
        [Parameter(Mandatory)]$Asset,
        [Parameter(Mandatory)]$Context,
        [Parameter(Mandatory)][string]$Tag
    )

    $normalName = "bemusicseeker-unofficial-fork-$($Context.Tag).zip"
    $metadataName = "bemusicseeker-unofficial-fork-$($Context.Tag)-with-metadata.zip"
    if ($Asset.Name -eq $normalName) {
        $kind = "app"
        $label = "App only"
        $includesMetadata = $false
    }
    elseif ($Asset.Name -eq $metadataName) {
        $kind = "app-with-metadata"
        $label = "App with metadata bundle"
        $includesMetadata = $true
    }
    else {
        throw "自動アップデート manifest に含められない asset 名です: $($Asset.Name)"
    }

    return [PSCustomObject]@{
        kind = $kind
        label = $label
        fileName = $Asset.Name
        url = "https://github.com/$publicRepoOwner/$publicRepoName/releases/download/$Tag/$($Asset.Name)"
        sha256 = (Get-FileHash -LiteralPath $Asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        sizeBytes = [int64]$Asset.Length
        includesChartInfoMetadata = $includesMetadata
    }
}

function Get-ExistingManifestPublishedAt {
    param(
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$ReleaseTag
    )

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        return $null
    }
    $document = [System.Text.Json.JsonDocument]::Parse((Get-Content -LiteralPath $ManifestPath -Raw))
    try {
        $rootElement = $document.RootElement
        $versionElement = [System.Text.Json.JsonElement]::new()
        $releaseTagElement = [System.Text.Json.JsonElement]::new()
        if (-not $rootElement.TryGetProperty("version", [ref]$versionElement) -or
            -not $rootElement.TryGetProperty("releaseTag", [ref]$releaseTagElement) -or
            $versionElement.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
            $releaseTagElement.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
            $versionElement.GetString() -cne $Version -or
            $releaseTagElement.GetString() -cne $ReleaseTag) {
            return $null
        }

        $publishedAtElement = [System.Text.Json.JsonElement]::new()
        if ($rootElement.TryGetProperty("publishedAt", [ref]$publishedAtElement) -and
            $publishedAtElement.ValueKind -eq [System.Text.Json.JsonValueKind]::String) {
            $publishedAt = $publishedAtElement.GetString()
            if (-not [string]::IsNullOrWhiteSpace($publishedAt)) {
                return $publishedAt
            }
        }
    }
    finally {
        $document.Dispose()
    }
    return $null
}

function New-UpdateManifest {
    param(
        [Parameter(Mandatory)]$Context,
        [Parameter(Mandatory)][string]$Tag
    )

    $metadata = @($Context.ReleaseAssets | ForEach-Object { Get-ReleaseAssetMetadata $_ $Context $Tag })
    if (-not ($metadata | Where-Object { $_.kind -eq "app" })) {
        throw "通常版パッケージが見つかりません。update.json には本体のみ asset が必要です。"
    }

    $manifestPath = Join-Path $root "update.json"
    $publishedAt = Get-ExistingManifestPublishedAt $manifestPath $Context.Version $Tag
    if ([string]::IsNullOrWhiteSpace($publishedAt)) {
        $publishedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    }

    return [PSCustomObject]@{
        schemaVersion = 1
        version = $Context.Version
        releaseTag = $Tag
        releasePageUrl = "https://github.com/$publicRepoOwner/$publicRepoName/releases/tag/$Tag"
        packageFormatVersion = 1
        publishedAt = $publishedAt
        minimumUpdaterVersion = "1"
        assets = (Sort-UpdateManifestAssets $metadata)
    }
}

function Write-UpdateManifest {
    param([Parameter(Mandatory)]$Manifest)

    $manifestPath = Join-Path $root "update.json"
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($manifestPath, ($Manifest | ConvertTo-Json -Depth 8), $utf8NoBom)
    Write-Host "  update.json を生成: $manifestPath" -ForegroundColor Green
}

function Build-PublicDocSite {
    Write-Host "  Pages HTML docs を生成中..."
    Push-Location $root
    try {
        $generatedDocs = @(uv run scripts\build-doc-html.py --source-root $root --output-root (Join-Path $root "docs") --site --site-url $publicSiteUrl)
        $exitCode = $LASTEXITCODE
        foreach ($doc in $generatedDocs) {
            Write-Host "    docs\$doc"
        }
        if ($exitCode -ne 0) {
            throw "Pages HTML docs の生成に失敗しました"
        }
    }
    finally {
        Pop-Location
    }
    $noJekyllPath = Join-Path $root "docs\.nojekyll"
    if (-not (Test-Path -LiteralPath $noJekyllPath -PathType Leaf)) {
        New-Item -ItemType File -Path $noJekyllPath -Force | Out-Null
    }
    Write-Host "  Pages HTML docs 生成完了" -ForegroundColor Green
}

function Get-GeneratedArtifactPaths {
    $paths = @("update.json")
    $docsRoot = Join-Path $root "docs"
    if (Test-Path -LiteralPath $docsRoot -PathType Container) {
        $paths += @(Get-ChildItem -LiteralPath $docsRoot -File |
            Where-Object { $_.Extension -ieq ".html" -or $_.Name -eq ".nojekyll" } |
            Sort-Object Name |
            ForEach-Object { "docs/$($_.Name)" })
    }
    return @($paths)
}

function Stage-GeneratedArtifacts {
    $paths = @(Get-GeneratedArtifactPaths)
    Invoke-GitChecked (@("add", "--") + $paths)
    $staged = @(git diff --cached --name-only)
    if ($LASTEXITCODE -ne 0) {
        throw "生成物の staged 差分を取得できません。"
    }
    $allowed = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $paths) { [void]$allowed.Add($path.Replace('\', '/')) }
    foreach ($path in $staged) {
        if (-not $allowed.Contains(([string]$path).Replace('\', '/'))) {
            throw "生成物以外のファイルをリリースコミットへ含めようとしています: $path"
        }
    }
    return $staged
}

function Get-LocalTagCommit {
    param([Parameter(Mandatory)][string]$Tag)

    $output = @(& git rev-parse --verify "$Tag^{commit}" 2>$null)
    if ($LASTEXITCODE -ne 0 -or $output.Count -eq 0) {
        return $null
    }
    return ([string]$output[0]).Trim()
}

function Get-RemoteTagCommit {
    param([Parameter(Mandatory)][string]$Tag)

    $output = @(& git ls-remote --tags origin "refs/tags/$Tag^{}" "refs/tags/$Tag" 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw "remote tag の確認に失敗しました: $Tag"
    }
    $lines = @($output | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    if ($lines.Count -eq 0) {
        return $null
    }
    $peeled = @($lines | Where-Object { [string]$_ -match "refs/tags/$([regex]::Escape($Tag))\^\{\}" })
    $line = if ($peeled.Count -gt 0) { [string]$peeled[0] } else { [string]$lines[0] }
    return (($line -split "\s+")[0]).Trim()
}

function Get-RemoteBranchCommit {
    param([Parameter(Mandatory)][string]$Branch)

    $line = (& git ls-remote --heads origin "refs/heads/$Branch" 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace([string]$line)) {
        throw "remote branch が見つかりません: $Branch"
    }
    return (([string]$line -split "\s+")[0]).Trim()
}

function Assert-CommitObjectAvailable {
    param(
        [Parameter(Mandatory)][string]$Commit,
        [Parameter(Mandatory)][string]$Label
    )

    & git cat-file -e "$Commit^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "$Label の commit object が local repo にありません: $Commit"
    }
}

function Assert-RemoteBranchIsAncestorOfHead {
    param([Parameter(Mandatory)][string]$Branch)

    $remoteCommit = Get-RemoteBranchCommit $Branch
    $headCommit = Get-GitHead
    Assert-CommitObjectAvailable $remoteCommit "remote $Branch"
    & git merge-base --is-ancestor $remoteCommit $headCommit 2>$null
    if ($LASTEXITCODE -eq 0) { return }
    if ($LASTEXITCODE -eq 1) {
        throw "remote $Branch は現在の HEAD の祖先ではありません: remote=$remoteCommit head=$headCommit"
    }
    throw "remote $Branch の祖先判定に失敗しました。"
}

function Assert-RemoteTagMatchesLocal {
    param([Parameter(Mandatory)][string]$Tag)

    $localCommit = Get-LocalTagCommit $Tag
    if ($null -eq $localCommit) {
        throw "local tag が見つかりません: $Tag"
    }
    $remoteCommit = Get-RemoteTagCommit $Tag
    if ($null -eq $remoteCommit) {
        throw "remote tag が見つかりません: $Tag"
    }
    if ($remoteCommit -ne $localCommit) {
        throw "remote tag と local tag の commit が一致しません: local=$localCommit remote=$remoteCommit"
    }
}

function Assert-TagDoesNotPointElsewhere {
    param(
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$ExpectedCommit
    )

    $localCommit = Get-LocalTagCommit $Tag
    if ($null -ne $localCommit -and $localCommit -ne $ExpectedCommit) {
        throw "既存 tag $Tag は別の commit を指しています。tag=$localCommit expected=$ExpectedCommit"
    }
    $remoteCommit = Get-RemoteTagCommit $Tag
    if ($null -ne $remoteCommit -and $remoteCommit -ne $ExpectedCommit) {
        throw "remote tag $Tag は別の commit を指しています。tag=$remoteCommit expected=$ExpectedCommit"
    }
}

function Assert-HeadMatchesTag {
    param([Parameter(Mandatory)][string]$Tag)

    $headCommit = Get-GitHead
    $tagCommit = Get-LocalTagCommit $Tag
    if ($null -eq $tagCommit -or $headCommit -ne $tagCommit) {
        throw "現在の HEAD は release tag と一致しません: tag=$Tag head=$headCommit tagCommit=$tagCommit"
    }
}

function Assert-RemoteBranchMatchesLocalHead {
    param([Parameter(Mandatory)][string]$Branch)

    $localCommit = Get-GitHead
    $remoteCommit = Get-RemoteBranchCommit $Branch
    if ($remoteCommit -ne $localCommit) {
        throw "remote $Branch は現在の release commit を指していません: local=$localCommit remote=$remoteCommit"
    }
}

function Get-ReleaseInfo {
    param([Parameter(Mandatory)][string]$Tag)

    $json = Invoke-GhOutput @("release", "view", $Tag, "--json", "isDraft,assets")
    return ($json | ConvertFrom-Json)
}

function Test-ReleaseExists {
    param([Parameter(Mandatory)][string]$Tag)

    $null = @(& gh release view $Tag 2>&1)
    $exitCode = $LASTEXITCODE
    return $exitCode -eq 0
}

function Assert-ReleaseAssetsMatchLocal {
    param([Parameter(Mandatory)]$Context)

    $info = Get-ReleaseInfo $Context.Tag
    $remoteAssets = @($info.assets | Sort-Object name)
    $expectedAssets = @($Context.ReleaseAssets | Sort-Object Name)
    if ($remoteAssets.Count -ne $expectedAssets.Count) {
        throw "GitHub Release asset 数が local zip と一致しません: remote=$($remoteAssets.Count) expected=$($expectedAssets.Count)"
    }
    for ($index = 0; $index -lt $expectedAssets.Count; $index++) {
        $expected = $expectedAssets[$index]
        $remote = $remoteAssets[$index]
        if ([string]$remote.name -ne $expected.Name) {
            throw "GitHub Release asset 名が local zip と一致しません: remote=$($remote.name) expected=$($expected.Name)"
        }
        if ([int64]$remote.size -ne [int64]$expected.Length) {
            throw "GitHub Release asset size が local zip と一致しません: $($expected.Name)"
        }
    }
}

function Assert-UpdateManifestMatchesLocal {
    param([Parameter(Mandatory)]$Context)

    $manifestPath = Join-Path $root "update.json"
    $tracked = @(& git ls-files --error-unmatch -- update.json 2>$null)
    if ($LASTEXITCODE -ne 0 -or $tracked.Count -eq 0) {
        throw "追跡中の update.json が見つかりません。"
    }
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "update.json が見つかりません: $manifestPath"
    }

    try {
        $actual = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "update.json を読み取れません: $manifestPath"
    }
    $expected = New-UpdateManifest $Context $Context.Tag
    foreach ($property in @("schemaVersion", "version", "releaseTag", "releasePageUrl", "packageFormatVersion", "minimumUpdaterVersion")) {
        if ([string]$actual.$property -cne [string]$expected.$property) {
            throw "update.json の $property が local zip と一致しません。"
        }
    }
    if ([string]::IsNullOrWhiteSpace([string]$actual.publishedAt)) {
        throw "update.json の publishedAt が空です。"
    }

    $actualAssets = @($actual.assets | Sort-Object @{ Expression = { Get-UpdateAssetKindPriority ([string]$_.kind) } }, fileName)
    $expectedAssets = @($expected.assets | Sort-Object @{ Expression = { Get-UpdateAssetKindPriority ([string]$_.kind) } }, fileName)
    if ($actualAssets.Count -ne $expectedAssets.Count) {
        throw "update.json の asset 数が local zip と一致しません。"
    }
    for ($index = 0; $index -lt $expectedAssets.Count; $index++) {
        $actualAsset = $actualAssets[$index]
        $expectedAsset = $expectedAssets[$index]
        foreach ($property in @("kind", "label", "fileName", "url", "sha256", "sizeBytes", "includesChartInfoMetadata")) {
            if ([string]$actualAsset.$property -cne [string]$expectedAsset.$property) {
                throw "update.json の asset $property が local zip と一致しません: $($expectedAsset.fileName)"
            }
        }
    }
}

function New-LocalTag {
    param(
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$ExpectedCommit
    )

    $existing = Get-LocalTagCommit $Tag
    if ($null -ne $existing) {
        if ($existing -ne $ExpectedCommit) {
            throw "既存 tag $Tag が現在の HEAD を指していません: tag=$existing head=$ExpectedCommit"
        }
        Write-Host "  タグ $Tag は現在の HEAD を指しています" -ForegroundColor Yellow
        return
    }
    Invoke-GitChecked @("tag", $Tag)
    Write-Host "  タグ $Tag を作成しました" -ForegroundColor Green
}

function Push-Tag {
    param([Parameter(Mandatory)][string]$Tag)

    Invoke-GitChecked @("push", "origin", "refs/tags/${Tag}:refs/tags/${Tag}")
    Assert-RemoteTagMatchesLocal $Tag
}

function Sync-DraftReleaseContent {
    param([Parameter(Mandatory)]$Context)

    $expectedNames = @($Context.ReleaseAssets | ForEach-Object { $_.Name })
    $existing = Get-ReleaseInfo $Context.Tag
    foreach ($remoteAsset in @($existing.assets)) {
        if ($expectedNames -notcontains [string]$remoteAsset.name) {
            Write-Host "  余剰 release asset を削除: $($remoteAsset.name)"
            Invoke-GhChecked @("release", "delete-asset", $Context.Tag, [string]$remoteAsset.name, "--yes")
        }
    }
    Invoke-GhChecked @("release", "edit", $Context.Tag, "--title", $Context.Tag, "--notes-file", $Context.NotesPath, "--draft")
    $assetPaths = @($Context.ReleaseAssets | ForEach-Object { $_.FullName })
    Invoke-GhChecked (@("release", "upload", $Context.Tag) + $assetPaths + @("--clobber"))
    Assert-ReleaseAssetsMatchLocal $Context
}

function Invoke-CreateDraft {
    param([Parameter(Mandatory)]$Context)

    Write-Host "`n=== ドラフトリリースの作成 ===" -ForegroundColor Cyan
    Assert-OnBranch $publicBranch
    Assert-WorkingTreeClean
    $sourceHead = Get-GitHead
    Assert-RemoteBranchIsAncestorOfHead $publicBranch
    Assert-TagDoesNotPointElsewhere $Context.Tag $sourceHead

    $manifest = New-UpdateManifest $Context $Context.Tag
    Write-UpdateManifest $manifest
    Build-PublicDocSite

    $staged = @(Stage-GeneratedArtifacts)
    $existingLocalTag = Get-LocalTagCommit $Context.Tag
    if ($null -ne $existingLocalTag -and $staged.Count -gt 0) {
        throw "既存 tag $($Context.Tag) は現在の HEAD を指していますが、生成物に差分があります。tag を付け替えずに停止します。"
    }
    if ($staged.Count -gt 0) {
        Invoke-GitChecked @("commit", "-m", "Release $($Context.Tag)")
    }

    $releaseHead = Get-GitHead
    New-LocalTag $Context.Tag $releaseHead
    Push-Tag $Context.Tag

    if (Test-ReleaseExists $Context.Tag) {
        $info = Get-ReleaseInfo $Context.Tag
        if (-not [bool]$info.isDraft) {
            throw "GitHub Release $($Context.Tag) は既に公開済みです。"
        }
        Sync-DraftReleaseContent $Context
    }
    else {
        $assetPaths = @($Context.ReleaseAssets | ForEach-Object { $_.FullName })
        Invoke-GhChecked (@("release", "create", $Context.Tag) + $assetPaths + @("--title", $Context.Tag, "--notes-file", $Context.NotesPath, "--verify-tag", "--draft"))
        Assert-ReleaseAssetsMatchLocal $Context
    }

    Assert-WorkingTreeClean
    Write-Host "  draft Release 作成完了。main は push していません。" -ForegroundColor Green
}

function Invoke-CreatePrereleaseDraft {
    param([Parameter(Mandatory)]$Context)

    Write-Host "`n=== プレリリース検証用ドラフトの作成 ===" -ForegroundColor Cyan
    Assert-OnBranch $developmentBranch
    Assert-WorkingTreeClean
    $sourceHead = Get-GitHead
    $previewTag = Get-PrereleaseTag $Context.Tag
    Assert-TagDoesNotPointElsewhere $previewTag $sourceHead

    New-LocalTag $previewTag $sourceHead
    Push-Tag $previewTag

    $previewContext = [PSCustomObject]@{
        Tag = $previewTag
        NotesPath = $Context.NotesPath
        ReleaseAssets = $Context.ReleaseAssets
    }
    if (Test-ReleaseExists $previewTag) {
        $info = Get-ReleaseInfo $previewTag
        if (-not [bool]$info.isDraft) {
            throw "GitHub Release $previewTag は既に公開済みです。"
        }
        Invoke-GhChecked @("release", "edit", $previewTag, "--title", $previewTag, "--notes-file", $previewContext.NotesPath, "--draft", "--prerelease")
        Assert-ReleaseAssetsMatchLocal ([PSCustomObject]@{ Tag = $previewTag; ReleaseAssets = $Context.ReleaseAssets })
    }
    else {
        $assetPaths = @($Context.ReleaseAssets | ForEach-Object { $_.FullName })
        Invoke-GhChecked (@("release", "create", $previewTag) + $assetPaths + @("--target", $developmentBranch, "--title", $previewTag, "--notes-file", $Context.NotesPath, "--verify-tag", "--draft", "--prerelease"))
        Assert-ReleaseAssetsMatchLocal ([PSCustomObject]@{ Tag = $previewTag; ReleaseAssets = $Context.ReleaseAssets })
    }

    Assert-WorkingTreeClean
    if ((Get-GitHead) -ne $sourceHead) {
        throw "preview 作成中に source HEAD が変わりました。"
    }
    Write-Host "  preview draft Release 作成完了。dev のソースと正式版の追跡ファイルは変更していません。" -ForegroundColor Green
}

function Invoke-PublishDraft {
    param([Parameter(Mandatory)]$Context)

    Write-Host "`n=== ドラフトリリースの公開 ===" -ForegroundColor Cyan
    Assert-OnBranch $publicBranch
    Assert-WorkingTreeClean
    Assert-HeadMatchesTag $Context.Tag
    Assert-RemoteTagMatchesLocal $Context.Tag
    Assert-UpdateManifestMatchesLocal $Context
    Assert-RemoteBranchIsAncestorOfHead $publicBranch

    if (-not (Test-ReleaseExists $Context.Tag)) {
        throw "GitHub Release draft が見つかりません: $($Context.Tag)"
    }
    $info = Get-ReleaseInfo $Context.Tag
    $isDraft = [bool]$info.isDraft
    Assert-ReleaseAssetsMatchLocal $Context

    if ($isDraft) {
        Write-Host "  draft Release を publish します..."
        Invoke-GhChecked @("release", "edit", $Context.Tag, "--draft=false")
    }
    else {
        Write-Host "  GitHub Release は既に公開済みです。" -ForegroundColor Yellow
    }
    Assert-ReleaseAssetsMatchLocal $Context

    Write-Host "  release commit を main へ通常 push します..."
    Invoke-GitChecked @("push", "origin", "HEAD:refs/heads/$publicBranch")
    Assert-RemoteBranchMatchesLocalHead $publicBranch
    Write-Host "  Release と main への反映が完了しました。" -ForegroundColor Green
}

Write-Host "=== 前提条件の確認 ===" -ForegroundColor Cyan
Assert-ExactlyOneMode
Assert-GhAuthenticated

$context = Get-ReleaseContext
Write-Host "  対象バージョン: $($context.Tag)"
Write-Host "  リリースノート: $($context.NotesPath)"
foreach ($asset in $context.ReleaseAssets) {
    Write-Host "  asset: dist\$($asset.Name)"
}

if ($CreateDraft) {
    Invoke-CreateDraft $context
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
