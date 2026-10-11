# 明示測定の入口：指定した規模でC#のNet10Performanceケースを実行する。
# contractは診断出力、estimationは候補リソース評価、parserは同位置音符の次音なし境界を検査する。
# この実行器の終了コードだけを、通常検証の成功や製品の速度保証として扱わない。
param(
    [ValidateSet('all', 'contract', 'estimation', 'parser')]
    [string]$Corpus = 'all',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string[]]$Scale = @('small', 'medium', 'large'),
    [string]$Output = 'artifacts/performance/net10-engineering'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $repositoryRoot $Output
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$normalizedScale = @(
    $Scale |
        ForEach-Object { $_ -split ',' } |
        ForEach-Object { $_.Trim().ToLowerInvariant() } |
        Where-Object { $_ } |
        Select-Object -Unique
)
$invalidScale = @($normalizedScale | Where-Object { $_ -notin @('small', 'medium', 'large') })
if ($normalizedScale.Count -eq 0 -or $invalidScale.Count -gt 0) {
    throw "Scale must contain only small, medium, or large."
}

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
    scale = @($normalizedScale)
    filter = $filter
    generatedAtUtc = [DateTime]::UtcNow.ToString('O')
}
$commandReceipt | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $outputRoot 'command.json') -Encoding utf8NoBOM

$previousScales = $env:BMS_NET10_PERF_SCALES
try {
    $env:BMS_NET10_PERF_SCALES = $normalizedScale -join ','
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
    $env:BMS_NET10_PERF_SCALES = $previousScales
}
