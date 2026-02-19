param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path,
    [string]$InstallRoot = "C:\Users\kazuk\AppData\Local\Programs\BeMusicSeeker",
    [string]$OutputPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $RepoRoot "docs/dependency-inventory.md"
}

function Get-AssemblyIdentity {
    param([string]$Path)
    try {
        $an = [System.Reflection.AssemblyName]::GetAssemblyName($Path)
        $pktBytes = $an.GetPublicKeyToken()
        $pkt = if ($pktBytes -and $pktBytes.Length -gt 0) { ($pktBytes | ForEach-Object { $_.ToString('x2') }) -join '' } else { '' }
        return [PSCustomObject]@{
            AssemblyName = $an.Name
            AssemblyVersion = $an.Version.ToString()
            PublicKeyToken = $pkt
        }
    }
    catch {
        return [PSCustomObject]@{
            AssemblyName = ''
            AssemblyVersion = ''
            PublicKeyToken = ''
        }
    }
}

function Get-DllRows {
    param([string]$Dir, [string]$Scope)
    if (-not (Test-Path $Dir)) { return @() }
    $rows = foreach ($f in Get-ChildItem -Path $Dir -File -Filter *.dll | Sort-Object Name) {
        $vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($f.FullName)
        $id = Get-AssemblyIdentity -Path $f.FullName
        $sha = (Get-FileHash -Algorithm SHA256 -Path $f.FullName).Hash.ToLowerInvariant()
        [PSCustomObject]@{
            Scope = $Scope
            Name = $f.Name
            FileVersion = ($vi.FileVersion ?? '')
            ProductVersion = ($vi.ProductVersion ?? '')
            AssemblyName = $id.AssemblyName
            AssemblyVersion = $id.AssemblyVersion
            PublicKeyToken = $id.PublicKeyToken
            SHA256 = $sha
            Length = $f.Length
            FullPath = $f.FullName
        }
    }
    return $rows
}

function To-MarkdownTable {
    param([object[]]$Rows, [string[]]$Columns)
    if (-not $Rows -or $Rows.Count -eq 0) {
        return "(none)`n"
    }
    $header = '| ' + ($Columns -join ' | ') + ' |'
    $sep = '| ' + (($Columns | ForEach-Object { '---' }) -join ' | ') + ' |'
    $lines = @($header, $sep)
    foreach ($r in $Rows) {
        $vals = foreach ($c in $Columns) {
            $v = ''
            if ($null -ne $r.PSObject.Properties[$c]) { $v = [string]$r.$c }
            $v = $v.Replace('|','\|').Replace("`r",' ').Replace("`n",' ')
            $v
        }
        $lines += '| ' + ($vals -join ' | ') + ' |'
    }
    return ($lines -join "`n") + "`n"
}

$csprojPath = Join-Path $RepoRoot 'BeMusicSeeker.csproj'
[xml]$csproj = Get-Content -Path $csprojPath

$refRows = @()
$referenceNodes = $csproj.SelectNodes('//Project/ItemGroup/Reference')
foreach ($r in $referenceNodes) {
    $hintNode = $r.SelectSingleNode('HintPath')
    $hint = if ($hintNode) { [string]$hintNode.InnerText } else { '' }
    $refRows += [PSCustomObject]@{
        Include = [string]$r.Include
        Type = $(if ($hint) { 'HintPath' } else { 'Framework' })
        HintPath = $hint
    }
}
$packageNodes = $csproj.SelectNodes('//Project/ItemGroup/PackageReference')
foreach ($p in $packageNodes) {
    $version = ''
    if ($p.Version) {
        $version = [string]$p.Version
    } else {
        $versionNode = $p.SelectSingleNode('Version')
        if ($versionNode) { $version = [string]$versionNode.InnerText }
    }
    $refRows += [PSCustomObject]@{
        Include = [string]$p.Include
        Type = 'PackageReference'
        HintPath = $version
    }
}
$refRows = $refRows | Sort-Object Type,Include

$repoLibs = Get-DllRows -Dir (Join-Path $RepoRoot 'libs') -Scope 'repo:libs'
$installRootDlls = Get-DllRows -Dir $InstallRoot -Scope 'install:root'
$installX86Dlls = Get-DllRows -Dir (Join-Path $InstallRoot 'x86') -Scope 'install:x86'
$installX64Dlls = Get-DllRows -Dir (Join-Path $InstallRoot 'x64') -Scope 'install:x64'

$repoVendorX86 = Join-Path $RepoRoot 'vendor/native/x86'
$repoVendorX64 = Join-Path $RepoRoot 'vendor/native/x64'

$requiredNativeX86 = @('7z.dll','bass.dll','bass_fx.dll','bassasio.dll','bassenc.dll','bassmix.dll','basswasapi.dll','OggVorbis.NET.dll','sqlite3.dll')
$requiredNativeX64 = @('7z.dll','bass.dll','bass_fx.dll','bassasio.dll','bassenc.dll','bassmix.dll','basswasapi.dll','OggVorbis.NET64.dll','sqlite3.dll')

$missingNative = @()
foreach ($n in $requiredNativeX86) {
    if (-not (Test-Path (Join-Path $repoVendorX86 $n))) {
        $missingNative += [PSCustomObject]@{ Scope='vendor/native/x86'; Name=$n; Status='missing' }
    }
}
foreach ($n in $requiredNativeX64) {
    if (-not (Test-Path (Join-Path $repoVendorX64 $n))) {
        $missingNative += [PSCustomObject]@{ Scope='vendor/native/x64'; Name=$n; Status='missing' }
    }
}

$managedCompare = @()
$installRootByName = @{}
foreach ($d in $installRootDlls) { $installRootByName[$d.Name.ToLowerInvariant()] = $d }
foreach ($lib in $repoLibs) {
    $key = $lib.Name.ToLowerInvariant()
    if ($installRootByName.ContainsKey($key)) {
        $inst = $installRootByName[$key]
        $status = if ($lib.SHA256 -eq $inst.SHA256) { 'match' } else { 'mismatch' }
        $managedCompare += [PSCustomObject]@{
            Name = $lib.Name
            RepoFileVersion = $lib.FileVersion
            InstallFileVersion = $inst.FileVersion
            RepoAssemblyVersion = $lib.AssemblyVersion
            InstallAssemblyVersion = $inst.AssemblyVersion
            Status = $status
        }
    }
    else {
        $managedCompare += [PSCustomObject]@{
            Name = $lib.Name
            RepoFileVersion = $lib.FileVersion
            InstallFileVersion = ''
            RepoAssemblyVersion = $lib.AssemblyVersion
            InstallAssemblyVersion = ''
            Status = 'install-missing'
        }
    }
}
$managedCompare = $managedCompare | Sort-Object Name

$md = @()
$md += '# Dependency Inventory'
$md += ''
$md += "GeneratedAt: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ssK')"
$md += "RepoRoot: $RepoRoot"
$md += "InstallRoot: $InstallRoot"
$md += ''
$md += '## Project References (BeMusicSeeker.csproj)'
$md += ''
$md += (To-MarkdownTable -Rows $refRows -Columns @('Include','Type','HintPath'))
$md += ''
$md += '## Managed DLLs (repo libs vs install root)'
$md += ''
$md += (To-MarkdownTable -Rows $managedCompare -Columns @('Name','RepoFileVersion','InstallFileVersion','RepoAssemblyVersion','InstallAssemblyVersion','Status'))
$md += ''
$md += '## Repo libs DLL details'
$md += ''
$md += (To-MarkdownTable -Rows $repoLibs -Columns @('Name','FileVersion','ProductVersion','AssemblyName','AssemblyVersion','PublicKeyToken','SHA256','Length'))
$md += ''
$md += '## Install root DLL details'
$md += ''
$md += (To-MarkdownTable -Rows $installRootDlls -Columns @('Name','FileVersion','ProductVersion','AssemblyName','AssemblyVersion','PublicKeyToken','SHA256','Length'))
$md += ''
$md += '## Install x86 native DLL details'
$md += ''
$md += (To-MarkdownTable -Rows $installX86Dlls -Columns @('Name','FileVersion','ProductVersion','AssemblyName','AssemblyVersion','PublicKeyToken','SHA256','Length'))
$md += ''
$md += '## Install x64 native DLL details'
$md += ''
$md += (To-MarkdownTable -Rows $installX64Dlls -Columns @('Name','FileVersion','ProductVersion','AssemblyName','AssemblyVersion','PublicKeyToken','SHA256','Length'))
$md += ''
$md += '## Missing native DLLs in repo vendor path'
$md += ''
$md += (To-MarkdownTable -Rows $missingNative -Columns @('Scope','Name','Status'))

Set-Content -Path $OutputPath -Value ($md -join "`n") -Encoding UTF8
Write-Host "Wrote: $OutputPath"
