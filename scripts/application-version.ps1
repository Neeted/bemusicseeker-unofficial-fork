# 通常公開版の取得元を統一します。実行限定のoverrideを正本へ書き戻しません。
function Assert-ApplicationVersion {
    param([Parameter(Mandatory)][string]$Value)

    $parsed = $null
    if (-not [System.Version]::TryParse($Value, [ref]$parsed) -or
        $parsed.Revision -lt 0 -or $parsed.ToString(4) -cne $Value) {
        throw "ApplicationVersionには四成分の数値版が必要です: $Value"
    }
    return $Value
}

function Get-ApplicationVersion {
    param([Parameter(Mandatory)][string]$RepositoryRoot)

    $path = Join-Path $RepositoryRoot 'ApplicationVersion.props'
    [xml]$document = Get-Content -LiteralPath $path -Raw
    $values = @($document.SelectNodes('/Project/PropertyGroup/BeMusicSeekerAppVersion'))
    if ($values.Count -ne 1) {
        throw "ApplicationVersion.propsに版の正本が一つ必要です: $path"
    }
    return Assert-ApplicationVersion -Value $values[0].InnerText
}
