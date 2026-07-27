Set-StrictMode -Version Latest

$script:RequiredManagedRootFiles = @(
    "Bass.Net.dll",
    "Livet.Core.dll",
    "Livet.EventListeners.dll",
    "Livet.Messaging.dll",
    "Livet.Mvvm.dll",
    "Microsoft.Xaml.Behaviors.dll",
    "Newtonsoft.Json.dll",
    "NLog.dll",
    "NVorbis.dll",
    "SgmlReaderDll.dll",
    "SevenZipExtractor.dll",
    "SQLite-net.dll",
    "SQLitePCLRaw.batteries_v2.dll",
    "SQLitePCLRaw.core.dll",
    "SQLitePCLRaw.provider.e_sqlite3.dll"
)

$script:RequiredBassNativeFiles = @(
    "libs/x64/bass.dll",
    "libs/x64/bassasio.dll",
    "libs/x64/bassenc.dll",
    "libs/x64/bassmix.dll",
    "libs/x64/basswasapi.dll",
    "libs/x64/bass_fx.dll"
)

$script:RequiredLanguageFiles = @(
    "lang/en-US.json",
    "lang/fr-FR.json",
    "lang/ja-JP.json",
    "lang/ko-KR.json",
    "lang/zh-CN.json",
    "lang/zh-TW.json"
)

function Join-PortablePackageRelativePath($root, $relativePath) {
    return Join-Path $root ($relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
}

function Get-PortableRequiredFiles {
    return @(
        "BeMusicSeeker.exe",
        "BeMusicSeeker.dll",
        "BeMusicSeeker.deps.json",
        "BeMusicSeeker.runtimeconfig.json",
        "BeMusicSeeker.dll.config",
        "BeMusicSeeker.Updater.exe",
        "BeMusicSeeker.Updater.dll",
        "BeMusicSeeker.Updater.deps.json",
        "BeMusicSeeker.Updater.runtimeconfig.json",
        "test.mp3",
        "runtimes/win-x64/native/e_sqlite3.dll",
        "libs/x64/7z.dll",
        "native/Everything3_x64.dll",
        "native/EverythingBridge_x64.dll"
    ) + $script:RequiredBassNativeFiles + $script:RequiredLanguageFiles + $script:RequiredManagedRootFiles
}

function Get-PortableForbiddenPaths {
    return @(
        "libs/x86",
        "x86",
        "x86/sqlite3.dll",
        "x86/7z.dll",
        "x86/bass.dll",
        "x86/bass_fx.dll",
        "x86/bassasio.dll",
        "x86/bassenc.dll",
        "x86/bassmix.dll",
        "x86/basswasapi.dll",
        "libs/x86/sqlite3.dll",
        "libs/x86/7z.dll",
        "libs/x86/bass.dll",
        "libs/x86/bass_fx.dll",
        "libs/x86/bassasio.dll",
        "libs/x86/bassenc.dll",
        "libs/x86/bassmix.dll",
        "libs/x86/basswasapi.dll",
        "libs/Bass.Net.dll",
        "libs/DynamicJson.dll",
        "libs/IniLibrary.dll",
        "libs/Livet.dll",
        "libs/Livet.Extensions.dll",
        "libs/MetroRadiance.Chrome.dll",
        "libs/MetroRadiance.Core.dll",
        "libs/MetroRadiance.dll",
        "libs/Microsoft.Expression.Drawing.dll",
        "libs/Microsoft.Expression.Effects.dll",
        "libs/Microsoft.Expression.Interactions.dll",
        "MetroRadiance.Chrome.dll",
        "MetroRadiance.Core.dll",
        "MetroRadiance.dll",
        "Microsoft.Expression.Drawing.dll",
        "Microsoft.Expression.Effects.dll",
        "Microsoft.Expression.Interactions.dll",
        "QuickConverter.dll",
        "System.Windows.Interactivity.dll",
        "libs/Microsoft.WindowsAPICodePack.dll",
        "libs/Microsoft.WindowsAPICodePack.Shell.dll",
        "Microsoft.WindowsAPICodePack.dll",
        "Microsoft.WindowsAPICodePack.Shell.dll",
        "libs/Newtonsoft.Json.dll",
        "libs/NLog.Database.dll",
        "libs/NLog.dll",
        "libs/NLog.WindowsEventLog.dll",
        "libs/QuickConverter.dll",
        "libs/SgmlReaderDll.dll",
        "libs/SevenZipExtractor.dll",
        "libs/OggVorbis.NET64.dll",
        "x64/sqlite3.dll",
        "libs/x64/sqlite3.dll",
        "libs/System.Collections.Immutable.dll",
        "libs/System.Resources.Extensions.dll",
        "libs/System.Memory.dll",
        "libs/System.Buffers.dll",
        "libs/System.Numerics.Vectors.dll",
        "libs/System.Runtime.CompilerServices.Unsafe.dll",
        "libs/System.Windows.Interactivity.dll",
        "libs/sqlite.net.dll",
        "BeMusicSeeker.exe.config",
        "libs/x64/OggVorbis.NET64.dll",
        "x64/OggVorbis.NET64.dll",
        "x64/7z.dll",
        "x64/bass.dll",
        "x64/bass_fx.dll",
        "x64/bassasio.dll",
        "x64/bassenc.dll",
        "x64/bassmix.dll",
        "x64/basswasapi.dll",
        "OggVorbis.NET.dll",
        "config",
        "data",
        "log",
        "logs",
        "update_backup",
        "update_work",
        "imported_metadata",
        "chart-info-metadata.db"
    )
}

function Assert-PortableStagingLayout($targetStagingDirectory, [bool]$requiresMetadataArchive) {
    foreach ($relativePath in Get-PortableRequiredFiles) {
        $path = Join-PortablePackageRelativePath $targetStagingDirectory $relativePath
        if (-not (Test-Path $path -PathType Leaf)) {
            throw "release package の必須ファイルが見つかりません: $relativePath"
        }
    }

    foreach ($relativePath in Get-PortableForbiddenPaths) {
        $path = Join-PortablePackageRelativePath $targetStagingDirectory $relativePath
        if (Test-Path $path) {
            throw "release package に禁止された配置が残っています: $relativePath"
        }
    }

    $languageDirectory = Join-PortablePackageRelativePath $targetStagingDirectory "lang"
    foreach ($languageFile in Get-ChildItem $languageDirectory -File -Recurse) {
        $relativePath = [System.IO.Path]::GetRelativePath($targetStagingDirectory, $languageFile.FullName).Replace('\', '/')
        if ($script:RequiredLanguageFiles -notcontains $relativePath) {
            throw "release package に未知の language catalog が含まれています: $relativePath"
        }
    }

    $metadataArchivePath = Join-PortablePackageRelativePath $targetStagingDirectory "chart-info-metadata.7z"
    if ($requiresMetadataArchive) {
        if (-not (Test-Path $metadataArchivePath -PathType Leaf)) {
            throw "metadata 同梱 release package に chart-info-metadata.7z がありません。"
        }
    }
    elseif (Test-Path $metadataArchivePath) {
        throw "通常版 release package に chart-info-metadata.7z が含まれています。"
    }
}

function Get-PortableZipEntryNames($assetPath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead((Get-Item $assetPath).FullName)
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

function Test-PortableZipEntryOrDescendantExists($entryNames, $relativePath) {
    $normalized = $relativePath.Replace('\', '/').Trim('/')
    return $entryNames.Contains($normalized) -or
        @($entryNames | Where-Object { $_.StartsWith("$normalized/", [System.StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
}

function Assert-PortableReleasePackageLayout($assetPath) {
    $asset = Get-Item $assetPath
    $entryNames = Get-PortableZipEntryNames $asset.FullName

    foreach ($relativePath in (Get-PortableRequiredFiles) + "update-managed-files.txt") {
        if (-not $entryNames.Contains($relativePath)) {
            throw "release asset に必須ファイルがありません: $($asset.Name): $relativePath"
        }
    }

    foreach ($relativePath in Get-PortableForbiddenPaths) {
        if (Test-PortableZipEntryOrDescendantExists $entryNames $relativePath) {
            throw "release asset に禁止された配置が残っています: $($asset.Name): $relativePath"
        }
    }

    foreach ($entryName in $entryNames) {
        $normalizedEntryName = $entryName.TrimEnd('/')
        $isKnownLanguage = $script:RequiredLanguageFiles -contains $normalizedEntryName
        if ($normalizedEntryName.StartsWith("lang/", [System.StringComparison]::OrdinalIgnoreCase) -and -not $isKnownLanguage) {
            throw "release asset に未知の language catalog が含まれています: $($asset.Name): $normalizedEntryName"
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
