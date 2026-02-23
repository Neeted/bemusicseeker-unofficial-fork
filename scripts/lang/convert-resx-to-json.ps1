param(
  [Parameter(Mandatory = $true)]
  [string]$InputResx,
  [Parameter(Mandatory = $true)]
  [string]$OutputJson
)

if (!(Test-Path -LiteralPath $InputResx)) {
  throw "Input resx not found: $InputResx"
}

[xml]$resx = Get-Content -LiteralPath $InputResx
$map = [ordered]@{}

foreach ($data in $resx.root.data) {
  $name = [string]$data.name
  $type = [string]$data.type
  if ([string]::IsNullOrWhiteSpace($name)) {
    continue
  }
  if (![string]::IsNullOrWhiteSpace($type)) {
    continue
  }
  $valueNode = $data.value
  if ($null -eq $valueNode) {
    continue
  }
  $map[$name] = [string]$valueNode
}

$outDir = Split-Path -Parent $OutputJson
if (![string]::IsNullOrWhiteSpace($outDir)) {
  New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}

$json = $map | ConvertTo-Json -Depth 3
Set-Content -LiteralPath $OutputJson -Value $json -Encoding UTF8
