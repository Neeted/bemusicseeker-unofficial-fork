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

# This manifest passes the current publish outputs between Full phases. It is
# not a tamper monitor: each acceptance works on its own sandbox copy.
function New-DistributionArtifactManifest {
    param(
        [Parameter(Mandatory)][string]$ArtifactRoot,
        [Parameter(Mandatory)][string]$RunId,
        [Parameter(Mandatory)][string]$ArtifactId,
        [Parameter(Mandatory)][string]$CurrentAppRoot,
        [Parameter(Mandatory)][string]$CurrentUpdaterRoot,
        [Parameter(Mandatory)][string]$CurrentPackagePath,
        [Parameter(Mandatory)][string]$CurrentVersion,
        [Parameter(Mandatory)][string]$CurrentCommit
    )

    $artifactRootPath = Resolve-DistributionFullPath $ArtifactRoot
    New-Item -ItemType Directory -Path $artifactRootPath -Force | Out-Null
    $manifestPath = Join-Path $artifactRootPath 'distribution-manifest.json'
    $manifest = [ordered]@{
        schemaVersion = 2
        manifestType = 'BeMusicSeeker.FullDistribution'
        runId = $RunId
        artifactId = $ArtifactId
        artifactRoot = $artifactRootPath
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        current = [ordered]@{
            version = $CurrentVersion
            commit = $CurrentCommit
            appRoot = Resolve-DistributionFullPath $CurrentAppRoot
            updaterRoot = Resolve-DistributionFullPath $CurrentUpdaterRoot
            packagePath = Resolve-DistributionFullPath $CurrentPackagePath
            packageSha256 = Get-DistributionSha256 $CurrentPackagePath
        }
    }
    [IO.File]::WriteAllText(
        $manifestPath,
        ($manifest | ConvertTo-Json -Depth 8),
        [Text.UTF8Encoding]::new($false))
    return Read-DistributionArtifactManifest -ManifestPath $manifestPath
}

function Read-DistributionArtifactManifest {
    param([Parameter(Mandatory)][string]$ManifestPath)

    $resolvedManifestPath = Resolve-DistributionFullPath $ManifestPath
    if (-not (Test-Path -LiteralPath $resolvedManifestPath -PathType Leaf)) {
        throw "Distribution manifest is missing: $resolvedManifestPath"
    }
    $manifest = Get-Content -LiteralPath $resolvedManifestPath -Raw | ConvertFrom-Json
    if ([int](Get-DistributionManifestValue $manifest 'schemaVersion') -ne 2 -or
        (Get-DistributionManifestValue $manifest 'manifestType') -cne 'BeMusicSeeker.FullDistribution') {
        throw "Unsupported distribution manifest schema: $resolvedManifestPath"
    }
    $loaded = [pscustomobject]@{
        ManifestPath = $resolvedManifestPath
        ArtifactRoot = Resolve-DistributionFullPath (Get-DistributionManifestValue $manifest 'artifactRoot')
        RunId = [string](Get-DistributionManifestValue $manifest 'runId')
        ArtifactId = [string](Get-DistributionManifestValue $manifest 'artifactId')
        Current = Get-DistributionManifestValue $manifest 'current'
    }
    $current = $loaded.Current
    $appRoot = Assert-DistributionPathWithinRoot $loaded.ArtifactRoot $current.appRoot 'Current app root'
    $updaterRoot = Assert-DistributionPathWithinRoot $loaded.ArtifactRoot $current.updaterRoot 'Current updater root'
    $packagePath = Assert-DistributionPathWithinRoot $loaded.ArtifactRoot $current.packagePath 'Current package'
    foreach ($directory in @($appRoot, $updaterRoot)) {
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
            throw "Distribution artifact directory is missing: $directory"
        }
    }
    Assert-DistributionPackagePath $packagePath $current.version 'Current' | Out-Null
    return $loaded
}

# The public v2.1.6.0 first-hop is deliberately kept outside the generated
# current distribution manifest.  A release lane must consume one
# pinned, checked-in identity and must not discover a newer or locally-built
# candidate when that identity is unavailable.
function Read-V216ArtifactMetadata {
    param(
        [Parameter(Mandatory)]
        [string]$MetadataPath,

        [string]$RepositoryRoot
    )

    $resolvedMetadataPath = Resolve-DistributionFullPath $MetadataPath
    if (-not (Test-Path -LiteralPath $resolvedMetadataPath -PathType Leaf)) {
        throw "Pinned v2.1.6.0 artifact metadata is missing: $resolvedMetadataPath"
    }

    try {
        $metadata = Get-Content -LiteralPath $resolvedMetadataPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "Pinned v2.1.6.0 artifact metadata is not valid JSON: $resolvedMetadataPath"
    }

    if ([int](Get-DistributionManifestValue $metadata 'schemaVersion') -ne 1 -or
        (Get-DistributionManifestValue $metadata 'manifestType') -cne 'BeMusicSeeker.V216Artifact') {
        throw "Unsupported pinned v2.1.6.0 artifact metadata schema: $resolvedMetadataPath"
    }

    $artifactId = [string](Get-DistributionManifestValue $metadata 'artifactId')
    $version = [string](Get-DistributionManifestValue $metadata 'version')
    $fileName = [string](Get-DistributionManifestValue $metadata 'fileName')
    $artifactPath = [string](Get-DistributionManifestValue $metadata 'artifactPath')
    $downloadUrl = [string](Get-DistributionManifestValue $metadata 'downloadUrl')
    $expectedSize = [long](Get-DistributionManifestValue $metadata 'sizeBytes')
    $expectedSha256 = [string](Get-DistributionManifestValue $metadata 'sha256')
    $sealed = [bool](Get-DistributionManifestValue $metadata 'sealed')
    $packageFormatVersion = [int](Get-DistributionManifestValue $metadata 'packageFormatVersion')
    $expectedDownloadUrl = 'https://github.com/Neeted/bemusicseeker-unofficial-fork/releases/download/v2.1.6.0/bemusicseeker-unofficial-fork-v2.1.6.0.zip'
    $downloadUri = $null
    $hasAbsoluteHttpsDownloadUrl = [Uri]::TryCreate(
        $downloadUrl,
        [UriKind]::Absolute,
        [ref]$downloadUri)

    if ($artifactId -cne 'public-v2.1.6.0' -or
        $version -cne '2.1.6.0' -or
        $fileName -cne 'published-bemusicseeker-unofficial-fork-v2.1.6.0.zip' -or
        [string]::IsNullOrWhiteSpace($artifactPath) -or
        -not $hasAbsoluteHttpsDownloadUrl -or
        $downloadUri.Scheme -cne 'https' -or
        $downloadUrl -cne $expectedDownloadUrl -or
        $packageFormatVersion -ne 1 -or
        $expectedSize -ne 11260709 -or
        $expectedSha256.Trim().ToUpperInvariant() -cne 'C2C460B6757478816912A59FEA535209B2A960528C8996FFE12225EC7CED7BB2' -or
        -not $sealed) {
        throw "Pinned v2.1.6.0 artifact metadata identity is invalid: $resolvedMetadataPath"
    }

    $rootPath = if (-not [string]::IsNullOrWhiteSpace($RepositoryRoot)) {
        Resolve-DistributionFullPath $RepositoryRoot
    }
    else {
        $candidate = Get-Item -LiteralPath $resolvedMetadataPath
        while ($null -ne $candidate -and -not (Test-Path -LiteralPath (Join-Path $candidate.FullName 'BeMusicSeeker.sln') -PathType Leaf)) {
            $candidate = $candidate.Parent
        }
        if ($null -eq $candidate) {
            throw "Unable to locate repository root for pinned v2.1.6.0 artifact metadata: $resolvedMetadataPath"
        }
        $candidate.FullName
    }
    if (-not (Test-Path -LiteralPath (Join-Path $rootPath 'BeMusicSeeker.sln') -PathType Leaf)) {
        throw "Pinned v2.1.6.0 artifact repository root is invalid: $rootPath"
    }

    if ([IO.Path]::IsPathRooted($artifactPath)) {
        $resolvedArtifactPath = Resolve-DistributionFullPath $artifactPath
    }
    else {
        $resolvedArtifactPath = Resolve-DistributionFullPath (Join-Path $rootPath $artifactPath)
    }
    if ([IO.Path]::GetFileName($resolvedArtifactPath) -cne $fileName) {
        throw "Pinned v2.1.6.0 artifact file name does not match metadata: $resolvedArtifactPath"
    }

    return [pscustomobject][ordered]@{
        Metadata = $metadata
        MetadataPath = $resolvedMetadataPath
        RepositoryRoot = $rootPath
        ArtifactId = $artifactId
        Version = $version
        PackageFormatVersion = $packageFormatVersion
        FileName = $fileName
        DownloadUrl = $downloadUrl
        ArtifactPath = $resolvedArtifactPath
        ExpectedSizeBytes = $expectedSize
        ExpectedSha256 = $expectedSha256.Trim().ToLowerInvariant()
        Sealed = $sealed
    }
}

function Assert-V216ArtifactIdentity {
    param(
        [Parameter(Mandatory)]
        [string]$MetadataPath,

        [string]$RepositoryRoot
    )

    $metadata = Read-V216ArtifactMetadata -MetadataPath $MetadataPath -RepositoryRoot $RepositoryRoot
    if ($metadata.PackageFormatVersion -ne 1) {
        throw "Pinned v2.1.6.0 artifact package format is unsupported: $($metadata.PackageFormatVersion)"
    }
    if (-not (Test-Path -LiteralPath $metadata.ArtifactPath -PathType Leaf)) {
        throw "Pinned v2.1.6.0 artifact is missing: $($metadata.ArtifactPath)"
    }

    $actualSize = (Get-Item -LiteralPath $metadata.ArtifactPath).Length
    if ($actualSize -ne $metadata.ExpectedSizeBytes) {
        throw "Pinned v2.1.6.0 artifact size mismatch: expected=$($metadata.ExpectedSizeBytes) actual=$actualSize path=$($metadata.ArtifactPath)"
    }
    $actualSha256 = Get-DistributionSha256 $metadata.ArtifactPath
    if ($actualSha256.ToUpperInvariant() -cne $metadata.ExpectedSha256.ToUpperInvariant()) {
        throw "Pinned v2.1.6.0 artifact SHA-256 mismatch: expected=$($metadata.ExpectedSha256) actual=$actualSha256 path=$($metadata.ArtifactPath)"
    }
    return $metadata
}
