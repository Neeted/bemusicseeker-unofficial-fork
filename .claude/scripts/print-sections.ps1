# 正本のMarkdownから、指定した見出しの節だけを標準出力へ書き出す。
# スキルの起動時に正本の該当節をそのまま注入し、契約をスキル側へ複製しないために使う。
# 節が見つからない場合もスキルの起動を止めず、正本を直接読むよう促す行を出力する。
#
# 使い方: pwsh -NoProfile -File .claude/scripts/print-sections.ps1 <リポジトリ相対パス> <見出し> [<見出し> ...]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Path,

    [Parameter(Mandatory = $true, Position = 1, ValueFromRemainingArguments = $true)]
    [string[]]$Heading
)

$ErrorActionPreference = 'Stop'

# 既定のコンソール符号化（日本語環境ではCP932）のままだと、呼出し側で文字化けする。
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$fullPath = Join-Path $repositoryRoot $Path

if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
    [Console]::Out.WriteLine("【正本が見つかりません: $Path】参照先を確認し、該当する仕様を直接読んでください。")
    exit 0
}

$lines = [System.IO.File]::ReadAllLines($fullPath, [System.Text.Encoding]::UTF8)

# コードブロック内の `#` で始まる行を見出しとして扱わないよう、見出しの位置を先に求める。
$headings = [System.Collections.Generic.List[object]]::new()
$insideFence = $false
for ($index = 0; $index -lt $lines.Length; $index++) {
    $line = $lines[$index]
    if ($line -match '^\s*(```|~~~)') {
        $insideFence = -not $insideFence
        continue
    }
    if (-not $insideFence -and $line -match '^(#{1,6})\s+(.+?)\s*$') {
        $headings.Add([pscustomobject]@{ Index = $index; Level = $Matches[1].Length; Title = $Matches[2] })
    }
}

$output = [System.Text.StringBuilder]::new()
foreach ($name in $Heading) {
    $position = -1
    for ($i = 0; $i -lt $headings.Count; $i++) {
        if ($headings[$i].Title -ceq $name) {
            $position = $i
            break
        }
    }

    if ($position -lt 0) {
        [void]$output.AppendLine("【節が見つかりません: $Path の「$name」】見出しが変わった可能性があります。正本を直接読んでください。")
        [void]$output.AppendLine()
        continue
    }

    $start = $headings[$position]
    $end = $lines.Length
    for ($i = $position + 1; $i -lt $headings.Count; $i++) {
        if ($headings[$i].Level -le $start.Level) {
            $end = $headings[$i].Index
            break
        }
    }

    [void]$output.AppendLine("<!-- 正本: $Path の「$name」 -->")
    for ($index = $start.Index; $index -lt $end; $index++) {
        [void]$output.AppendLine($lines[$index])
    }
    [void]$output.AppendLine()
}

[Console]::Out.Write($output.ToString())
