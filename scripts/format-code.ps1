[CmdletBinding()]
param(
    [ValidateSet('Changed', 'All')]
    [string]$Scope = 'Changed',

    [switch]$NoRestore
)

# Git の表示用クォートや改行による分割を介さず、ファイル名をそのまま受け取る。
function Get-RepositoryGitPaths {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory,

        [Parameter(Mandatory)]
        [object]$DeadlinePolicy
    )

    $result = Invoke-VerificationMonitoredCommand `
        -Label 'Git ファイル一覧' `
        -CommandPath 'git' `
        -Arguments (@('-C', $RepositoryRoot) + $Arguments) `
        -WorkingDirectory $RepositoryRoot `
        -DiagnosticsDirectory $DiagnosticsDirectory `
        -DeadlinePolicy $DeadlinePolicy 6>$null
    return ([string]$result.StandardOutput).Split(
        [char]0, [StringSplitOptions]::RemoveEmptyEntries)
}

# 手動実行と標準検証が、対象選択・整形順・失敗判定を共有する。
function Invoke-RepositoryFormatting {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [ValidateSet('Changed', 'All')]
        [string]$Scope,

        [Parameter(Mandatory)]
        [string]$DiagnosticsDirectory,

        [Parameter(Mandatory)]
        [object]$DeadlinePolicy,

        [switch]$NoRestore
    )

    $excludedRoots = @('artifacts/verification', 'bin', 'obj', '.tmp')
    $commonArguments = @('--verbosity', 'minimal', '--exclude') + $excludedRoots
    if ($Scope -eq 'Changed') {
        $paths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $queries = @(
            @('diff', '--name-only', '--diff-filter=d', '-z', '--'),
            @('diff', '--cached', '--name-only', '--diff-filter=d', '-z', '--'),
            @('ls-files', '--others', '--exclude-standard', '-z', '--'))
        for ($index = 0; $index -lt $queries.Count; $index++) {
            foreach ($path in @(Get-RepositoryGitPaths `
                    -RepositoryRoot $RepositoryRoot `
                    -Arguments $queries[$index] `
                    -DiagnosticsDirectory (Join-Path $DiagnosticsDirectory "git-$index") `
                    -DeadlinePolicy $DeadlinePolicy)) {
                [void]$paths.Add($path)
            }
        }

        $files = [string[]]@($paths | Where-Object {
                $path = $_
                $excluded = @($excludedRoots | Where-Object {
                        $path -like "$_/*" -or $path -like "*/$_/*"
                    }).Count -gt 0
                -not $excluded -and
                $path.EndsWith('.cs', [StringComparison]::OrdinalIgnoreCase) -and
                (Test-Path -LiteralPath (Join-Path $RepositoryRoot $path) -PathType Leaf)
            })
        [Array]::Sort($files, [StringComparer]::Ordinal)
        if ($files.Count -eq 0) {
            Write-Host '変更された C# ファイルがないため、自動整形を省略します。'
            return
        }

        Write-Host "自動整形: 変更された C# ファイル $($files.Count) 件"
        # 空の --include は全件になるため、対象ゼロではここへ進めない。
        $commonArguments += @('--include') + @($files | ForEach-Object { "./$_" })
    }
    else {
        Write-Host '自動整形: 全体（生成先を除く）'
    }

    $solutionPath = Join-Path $RepositoryRoot 'BeMusicSeeker.sln'
    if (-not $NoRestore) {
        [void](Invoke-VerificationMonitoredCommand `
                -Label 'Locked restore' `
                -CommandPath 'dotnet' `
                -Arguments @('restore', $solutionPath, '-r', 'win-x64', '--locked-mode', '-p:PublishReadyToRun=true') `
                -WorkingDirectory $RepositoryRoot `
                -DiagnosticsDirectory (Join-Path $DiagnosticsDirectory 'restore') `
                -DeadlinePolicy $DeadlinePolicy)
    }

    # 構文の修正後に空白・改行を揃え、再検査だけの formatter 起動は行わない。
    foreach ($category in @('style', 'whitespace')) {
        $arguments = if ($category -eq 'style') {
            @('format', 'style', $solutionPath, '--severity', 'error', '--no-restore')
        }
        else {
            @('format', 'whitespace', $RepositoryRoot, '--folder')
        }
        [void](Invoke-VerificationMonitoredCommand `
                -Label "dotnet format $category" `
                -CommandPath 'dotnet' `
                -Arguments ($arguments + $commonArguments) `
                -WorkingDirectory $RepositoryRoot `
                -DiagnosticsDirectory (Join-Path $DiagnosticsDirectory $category) `
                -DeadlinePolicy $DeadlinePolicy)
    }
}

if ($MyInvocation.InvocationName -eq '.') {
    return
}

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'verification-process-lifecycle.ps1')
. (Join-Path $PSScriptRoot 'verification-runner-contract.ps1')
$formatPhase = @((Get-VerificationRunnerContract).PhaseDescriptors | Where-Object Name -eq 'format')[0]
$deadlinePolicy = Resolve-VerificationDeadlinePair -TimeoutSeconds $formatPhase.BudgetSeconds
$diagnosticsDirectory = Join-Path $repositoryRoot (
    'artifacts/verification/format-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
Invoke-RepositoryFormatting `
    -RepositoryRoot $repositoryRoot `
    -Scope $Scope `
    -DiagnosticsDirectory $diagnosticsDirectory `
    -DeadlinePolicy $deadlinePolicy `
    -NoRestore:$NoRestore
