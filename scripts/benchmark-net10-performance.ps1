param(
    [ValidateSet('all', 'contract', 'list', 'startup', 'estimation', 'scan', 'parser')]
    [string]$Corpus = 'all',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [int]$Seed = 0xBEE501,
    [ValidateSet('small', 'medium', 'large')]
    [string[]]$Scale = @('small', 'medium', 'large'),
    [string]$Output = 'artifacts/performance/net10-engineering'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $repositoryRoot $Output
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

$filter = if ($Corpus -eq 'all') {
    'TestCategory=Net10Performance'
} elseif ($Corpus -eq 'contract') {
    '(TestCategory=Net10Performance)&(TestCategory=contract)'
} else {
    "(TestCategory=Net10Performance)&(TestCategory=$Corpus)"
}

$commandReceipt = [ordered]@{
    corpus = $Corpus
    configuration = $Configuration
    seed = $Seed
    scale = @($Scale)
    filter = $filter
    generatedAtUtc = [DateTime]::UtcNow.ToString('O')
}
$commandReceipt | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $outputRoot 'command.json') -Encoding utf8NoBOM

$previousSeed = $env:BMS_NET10_PERF_SEED
$previousScales = $env:BMS_NET10_PERF_SCALES
try {
    $env:BMS_NET10_PERF_SEED = $Seed.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:BMS_NET10_PERF_SCALES = $Scale -join ','
    & dotnet test (Join-Path $repositoryRoot 'BeMusicSeeker.Tests\BeMusicSeeker.Tests.csproj') `
        --no-restore `
        --configuration $Configuration `
        /p:Platform=x64 `
        --filter $filter `
        --logger "trx;LogFileName=net10-performance-$Corpus.trx" `
        --results-directory $outputRoot
    if ($LASTEXITCODE -ne 0) {
        throw "The .NET 10 performance corpus command failed with exit code $LASTEXITCODE."
    }
}
finally {
    $env:BMS_NET10_PERF_SEED = $previousSeed
    $env:BMS_NET10_PERF_SCALES = $previousScales
}
