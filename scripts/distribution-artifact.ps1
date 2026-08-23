function Resolve-DistributionFullPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'Distribution artifact path is required.'
    }
    return [System.IO.Path]::GetFullPath($Path)
}

function Get-DistributionSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $resolvedPath = Resolve-DistributionFullPath $Path
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "Distribution artifact file is missing: $resolvedPath"
    }
    return (Get-FileHash -LiteralPath $resolvedPath -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-DistributionTreeSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    $rootPath = Resolve-DistributionFullPath $Root
    if (-not (Test-Path -LiteralPath $rootPath -PathType Container)) {
        throw "Distribution artifact directory is missing: $rootPath"
    }

    $normalizedRoot = $rootPath.TrimEnd('\', '/')
    $relativePaths = [System.Collections.Generic.List[string]]::new()
    foreach ($file in @(Get-ChildItem -LiteralPath $normalizedRoot -Recurse -File)) {
        $relativePath = $file.FullName.Substring($normalizedRoot.Length).TrimStart('\', '/').Replace('\', '/')
        [void]$relativePaths.Add($relativePath)
    }
    $relativePaths.Sort([StringComparer]::Ordinal)

    $entries = [System.Collections.Generic.List[string]]::new()
    foreach ($relativePath in $relativePaths) {
        $filePath = Join-Path $normalizedRoot ($relativePath.Replace('/', [IO.Path]::DirectorySeparatorChar))
        [void]$entries.Add("$relativePath`t$(Get-DistributionSha256 $filePath)")
    }

    $payload = [Text.Encoding]::UTF8.GetBytes($entries -join "`n")
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($payload)).ToLowerInvariant()
}

function Assert-DistributionPathWithinRoot {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $rootPath = (Resolve-DistributionFullPath $Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $pathValue = Resolve-DistributionFullPath $Path
    if (-not $pathValue.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description must remain under the distribution artifact root: $pathValue"
    }
    return $pathValue
}

function Assert-DistributionPackagePath {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath,

        [Parameter(Mandatory)]
        [string]$Version,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $resolvedPath = Resolve-DistributionFullPath $PackagePath
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "$Description package is missing: $resolvedPath"
    }
    $expectedName = "bemusicseeker-unofficial-fork-v$Version.zip"
    if ([IO.Path]::GetFileName($resolvedPath) -cne $expectedName) {
        throw "$Description package name does not match version '$Version': $resolvedPath"
    }
    return $resolvedPath
}

function Get-DistributionManifestValue {
    param(
        [Parameter(Mandatory)]
        [object]$Object,

        [Parameter(Mandatory)]
        [string]$Name
    )

    if ($Object -is [System.Collections.IDictionary]) {
        if (-not $Object.Contains($Name)) {
            throw "Distribution manifest property is missing: $Name"
        }
        return $Object[$Name]
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        throw "Distribution manifest property is missing: $Name"
    }
    return $property.Value
}

function New-DistributionArtifactManifest {
    param(
        [Parameter(Mandatory)]
        [string]$ArtifactRoot,

        [Parameter(Mandatory)]
        [string]$RunId,

        [Parameter(Mandatory)]
        [string]$ArtifactId,

        [Parameter(Mandatory)]
        [string]$CurrentAppRoot,

        [Parameter(Mandatory)]
        [string]$CurrentUpdaterRoot,

        [Parameter(Mandatory)]
        [string]$CurrentPackagePath,

        [Parameter(Mandatory)]
        [string]$CurrentVersion,

        [Parameter(Mandatory)]
        [string]$CurrentCommit,

        [Parameter(Mandatory)]
        [string]$BaselinePackagePath,

        [Parameter(Mandatory)]
        [string]$BaselineVersion,

        [Parameter(Mandatory)]
        [string]$BaselineCommit
    )

    if ([string]::IsNullOrWhiteSpace($RunId) -or [string]::IsNullOrWhiteSpace($ArtifactId)) {
        throw 'Distribution manifest run and artifact IDs are required.'
    }
    $artifactRootPath = Resolve-DistributionFullPath $ArtifactRoot
    New-Item -ItemType Directory -Path $artifactRootPath -Force | Out-Null
    $currentAppPath = Assert-DistributionPathWithinRoot $artifactRootPath $CurrentAppRoot 'Current app root'
    $currentUpdaterPath = Assert-DistributionPathWithinRoot $artifactRootPath $CurrentUpdaterRoot 'Current updater root'
    $currentPackage = Assert-DistributionPathWithinRoot $artifactRootPath $CurrentPackagePath 'Current package'
    $baselinePackage = Assert-DistributionPathWithinRoot $artifactRootPath $BaselinePackagePath 'Baseline package'
    if (-not (Test-Path -LiteralPath $currentAppPath -PathType Container)) {
        throw "Current app root is missing: $currentAppPath"
    }
    if (-not (Test-Path -LiteralPath $currentUpdaterPath -PathType Container)) {
        throw "Current updater root is missing: $currentUpdaterPath"
    }
    Assert-DistributionPackagePath $currentPackage $CurrentVersion 'Current' | Out-Null
    Assert-DistributionPackagePath $baselinePackage $BaselineVersion 'Baseline' | Out-Null

    $manifestPath = Join-Path $artifactRootPath 'distribution-manifest.json'
    $manifestHashPath = Join-Path $artifactRootPath 'distribution-manifest.sha256'
    $manifest = [ordered]@{
        schemaVersion = 1
        manifestType = 'BeMusicSeeker.FullDistribution'
        runId = $RunId
        artifactId = $ArtifactId
        artifactRoot = $artifactRootPath
        manifestPath = $manifestPath
        manifestHashPath = $manifestHashPath
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        current = [ordered]@{
            version = $CurrentVersion
            commit = $CurrentCommit
            appRoot = $currentAppPath
            updaterRoot = $currentUpdaterPath
            packagePath = $currentPackage
            appTreeSha256 = Get-DistributionTreeSha256 $currentAppPath
            updaterTreeSha256 = Get-DistributionTreeSha256 $currentUpdaterPath
            packageSha256 = Get-DistributionSha256 $currentPackage
        }
        baseline = [ordered]@{
            version = $BaselineVersion
            commit = $BaselineCommit
            packagePath = $baselinePackage
            packageSha256 = Get-DistributionSha256 $baselinePackage
        }
    }
    $json = $manifest | ConvertTo-Json -Depth 16
    [IO.File]::WriteAllText($manifestPath, $json, [Text.UTF8Encoding]::new($false))
    $manifestHash = Get-DistributionSha256 $manifestPath
    [IO.File]::WriteAllText(
        $manifestHashPath,
        $manifestHash + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    return Read-DistributionArtifactManifest -ManifestPath $manifestPath
}

function Read-DistributionArtifactManifest {
    param(
        [Parameter(Mandatory)]
        [string]$ManifestPath
    )

    $resolvedManifestPath = Resolve-DistributionFullPath $ManifestPath
    if (-not (Test-Path -LiteralPath $resolvedManifestPath -PathType Leaf)) {
        throw "Distribution manifest is missing: $resolvedManifestPath"
    }
    $manifestHashPath = Join-Path (Split-Path -Parent $resolvedManifestPath) 'distribution-manifest.sha256'
    if (-not (Test-Path -LiteralPath $manifestHashPath -PathType Leaf)) {
        throw "Distribution manifest seal is missing: $manifestHashPath"
    }
    $expectedHash = (Get-Content -LiteralPath $manifestHashPath -Raw).Trim().ToLowerInvariant()
    $actualHash = Get-DistributionSha256 $resolvedManifestPath
    if ($expectedHash -cne $actualHash) {
        throw "Distribution manifest seal does not match: $resolvedManifestPath"
    }

    try {
        $manifest = Get-Content -LiteralPath $resolvedManifestPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "Distribution manifest is not valid JSON: $resolvedManifestPath"
    }
    if ([int](Get-DistributionManifestValue $manifest 'schemaVersion') -ne 1 -or
        (Get-DistributionManifestValue $manifest 'manifestType') -cne 'BeMusicSeeker.FullDistribution') {
        throw "Unsupported distribution manifest schema: $resolvedManifestPath"
    }
    if ((Resolve-DistributionFullPath (Get-DistributionManifestValue $manifest 'manifestPath')) -cne $resolvedManifestPath -or
        (Resolve-DistributionFullPath (Get-DistributionManifestValue $manifest 'manifestHashPath')) -cne (Resolve-DistributionFullPath $manifestHashPath)) {
        throw "Distribution manifest paths do not match their sealed location: $resolvedManifestPath"
    }

    return [pscustomobject]@{
        Manifest = $manifest
        ManifestPath = $resolvedManifestPath
        ManifestHashPath = Resolve-DistributionFullPath $manifestHashPath
        ManifestSha256 = $actualHash
        ArtifactRoot = Resolve-DistributionFullPath (Get-DistributionManifestValue $manifest 'artifactRoot')
        RunId = [string](Get-DistributionManifestValue $manifest 'runId')
        ArtifactId = [string](Get-DistributionManifestValue $manifest 'artifactId')
        Current = Get-DistributionManifestValue $manifest 'current'
        Baseline = Get-DistributionManifestValue $manifest 'baseline'
    }
}

function Assert-DistributionArtifactManifest {
    param(
        [Parameter(Mandatory)]
        [object]$ArtifactManifest,

        [string]$ExpectedArtifactId,

        [string]$ExpectedRunId
    )

    $loaded = if ($ArtifactManifest.PSObject.Properties['Manifest']) {
        $ArtifactManifest
    }
    else {
        throw 'Distribution artifact manifest must be loaded through Read-DistributionArtifactManifest.'
    }
    $manifest = $loaded.Manifest
    if (-not [string]::IsNullOrWhiteSpace($ExpectedArtifactId) -and
        $loaded.ArtifactId -cne $ExpectedArtifactId) {
        throw "Distribution artifact ID mismatch: expected=$ExpectedArtifactId actual=$($loaded.ArtifactId)"
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedRunId) -and
        $loaded.RunId -cne $ExpectedRunId) {
        throw "Distribution run ID mismatch: expected=$ExpectedRunId actual=$($loaded.RunId)"
    }
    $artifactRoot = $loaded.ArtifactRoot
    if ((Resolve-DistributionFullPath (Get-DistributionManifestValue $manifest 'artifactRoot')) -cne $artifactRoot) {
        throw 'Distribution artifact root is not canonical.'
    }

    $current = $loaded.Current
    $baseline = $loaded.Baseline
    $currentAppRoot = Assert-DistributionPathWithinRoot $artifactRoot (Get-DistributionManifestValue $current 'appRoot') 'Current app root'
    $currentUpdaterRoot = Assert-DistributionPathWithinRoot $artifactRoot (Get-DistributionManifestValue $current 'updaterRoot') 'Current updater root'
    $currentPackage = Assert-DistributionPathWithinRoot $artifactRoot (Get-DistributionManifestValue $current 'packagePath') 'Current package'
    $baselinePackage = Assert-DistributionPathWithinRoot $artifactRoot (Get-DistributionManifestValue $baseline 'packagePath') 'Baseline package'
    $currentVersion = [string](Get-DistributionManifestValue $current 'version')
    $baselineVersion = [string](Get-DistributionManifestValue $baseline 'version')
    Assert-DistributionPackagePath $currentPackage $currentVersion 'Current' | Out-Null
    Assert-DistributionPackagePath $baselinePackage $baselineVersion 'Baseline' | Out-Null
    if (-not (Test-Path -LiteralPath $currentAppRoot -PathType Container) -or
        (Get-DistributionTreeSha256 $currentAppRoot) -cne [string](Get-DistributionManifestValue $current 'appTreeSha256')) {
        throw 'Current app artifact is missing or tampered.'
    }
    if (-not (Test-Path -LiteralPath $currentUpdaterRoot -PathType Container) -or
        (Get-DistributionTreeSha256 $currentUpdaterRoot) -cne [string](Get-DistributionManifestValue $current 'updaterTreeSha256')) {
        throw 'Current updater artifact is missing or tampered.'
    }
    if ((Get-DistributionSha256 $currentPackage) -cne [string](Get-DistributionManifestValue $current 'packageSha256')) {
        throw 'Current package artifact is missing or tampered.'
    }
    if ((Get-DistributionSha256 $baselinePackage) -cne [string](Get-DistributionManifestValue $baseline 'packageSha256')) {
        throw 'Baseline package artifact is missing or tampered.'
    }
    return $loaded
}

function Assert-DistributionArtifactIdentity {
    param(
        [Parameter(Mandatory)]
        [object]$ArtifactManifest,

        [Parameter(Mandatory)]
        [string]$ExpectedRunId,

        [Parameter(Mandatory)]
        [string]$ExpectedArtifactId,

        [Parameter(Mandatory)]
        [string]$ExpectedManifestSha256,

        [Parameter(Mandatory)]
        [string]$ExpectedManifestSeal
    )

    if ([string]::IsNullOrWhiteSpace($ExpectedManifestSha256) -or
        [string]::IsNullOrWhiteSpace($ExpectedManifestSeal)) {
        throw 'Expected distribution manifest identity hash and seal are required.'
    }

    $expectedManifestSha256Value = $ExpectedManifestSha256.Trim().ToLowerInvariant()
    $expectedManifestSealValue = $ExpectedManifestSeal.Trim().ToLowerInvariant()
    if ($expectedManifestSha256Value -cne $expectedManifestSealValue) {
        throw "Expected distribution manifest hash and seal differ: hash=$expectedManifestSha256Value seal=$expectedManifestSealValue"
    }

    $loaded = Assert-DistributionArtifactManifest `
        -ArtifactManifest $ArtifactManifest `
        -ExpectedRunId $ExpectedRunId `
        -ExpectedArtifactId $ExpectedArtifactId
    if ($loaded.ManifestSha256 -cne $expectedManifestSha256Value) {
        throw "Distribution manifest SHA-256 mismatch: expected=$expectedManifestSha256Value actual=$($loaded.ManifestSha256)"
    }

    $actualManifestSeal = (Get-Content -LiteralPath $loaded.ManifestHashPath -Raw).Trim().ToLowerInvariant()
    if ($actualManifestSeal -cne $expectedManifestSealValue) {
        throw "Distribution manifest seal mismatch: expected=$expectedManifestSealValue actual=$actualManifestSeal"
    }
    return $loaded
}
