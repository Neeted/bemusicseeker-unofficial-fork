param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path,
    [switch]$RunBuild,
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-AssemblyIdentity {
    param([string]$Path)
    try {
        $an = [System.Reflection.AssemblyName]::GetAssemblyName($Path)
        $pktBytes = $an.GetPublicKeyToken()
        $pkt = if ($pktBytes -and $pktBytes.Length -gt 0) { ($pktBytes | ForEach-Object { $_.ToString('x2') }) -join '' } else { '' }
        return [PSCustomObject]@{
            Name = $an.Name
            Version = $an.Version.ToString()
            PublicKeyToken = $pkt
        }
    }
    catch {
        return [PSCustomObject]@{ Name=''; Version=''; PublicKeyToken='' }
    }
}

$errors = New-Object System.Collections.Generic.List[string]

$requiredX86 = @('7z.dll','bass.dll','bass_fx.dll','bassasio.dll','bassenc.dll','bassmix.dll','basswasapi.dll','OggVorbis.NET.dll','sqlite3.dll')
$requiredX64 = @('7z.dll','bass.dll','bass_fx.dll','bassasio.dll','bassenc.dll','bassmix.dll','basswasapi.dll','OggVorbis.NET64.dll','sqlite3.dll')

foreach ($f in $requiredX86) {
    $p = Join-Path $RepoRoot ("vendor/native/x86/" + $f)
    if (-not (Test-Path $p)) { $errors.Add("missing vendor x86: $f") }
}
foreach ($f in $requiredX64) {
    $p = Join-Path $RepoRoot ("vendor/native/x64/" + $f)
    if (-not (Test-Path $p)) { $errors.Add("missing vendor x64: $f") }
}

$libs = Join-Path $RepoRoot 'libs'
$out = Join-Path $RepoRoot ("bin/$Configuration/net472")

$compatManaged = @('NLog.dll')
foreach ($dll in $compatManaged) {
    $libPath = Join-Path $libs $dll
    $outPath = Join-Path $out $dll
    if ((Test-Path $libPath) -and (Test-Path $outPath)) {
        $a = Get-AssemblyIdentity -Path $libPath
        $b = Get-AssemblyIdentity -Path $outPath
        if ($a.Name -ne $b.Name -or $a.Version -ne $b.Version -or $a.PublicKeyToken -ne $b.PublicKeyToken) {
            $errors.Add("identity mismatch: $dll lib($($a.Name),$($a.Version),$($a.PublicKeyToken)) out($($b.Name),$($b.Version),$($b.PublicKeyToken))")
        }
    }
}

if ($RunBuild) {
    Push-Location $RepoRoot
    try {
        & msbuild BeMusicSeeker-decomp.sln /p:Configuration=$Configuration /m | Out-Host
    }
    finally {
        Pop-Location
    }
}

if ($errors.Count -gt 0) {
    Write-Host 'VERIFY: FAIL' -ForegroundColor Red
    $errors | ForEach-Object { Write-Host " - $_" -ForegroundColor Red }
    exit 1
}

Write-Host 'VERIFY: PASS' -ForegroundColor Green
