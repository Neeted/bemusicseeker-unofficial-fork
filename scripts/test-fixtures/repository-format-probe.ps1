[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Selection', 'Pipeline')]
    [string]$Probe
)

$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $sourceRoot 'scripts/verification-process-lifecycle.ps1')
. (Join-Path $sourceRoot 'scripts/format-code.ps1')
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('repository-format-' + [Guid]::NewGuid().ToString('N'))
$repoRoot = Join-Path $temporaryRoot 'repository'
[void][IO.Directory]::CreateDirectory($repoRoot)
$solution = Join-Path $repoRoot 'BeMusicSeeker.sln'
$diagnostics = Join-Path $temporaryRoot 'diagnostics'
$policy = Resolve-VerificationDeadlinePair -TimeoutSeconds 20
$realMonitoredCommand = ${function:Invoke-VerificationMonitoredCommand}
$commands = [System.Collections.Generic.List[object]]::new()
$events = [System.Collections.Generic.List[string]]::new()
$buildInputs = [System.Collections.Generic.List[string]]::new()
$failure = ''
$gitOutput = "Changed.cs`0"

# Selection は実 Git を使い、Pipeline は変更一覧を入力として与える。
# formatter の代替は受け取った順序と範囲だけを観測し、SDK 自体の修正品質は判定しない。
function Invoke-VerificationMonitoredCommand {
    param($Label, $CommandPath, [string[]]$Arguments, $WorkingDirectory, $DiagnosticsDirectory, $DeadlinePolicy)

    if (-not [object]::ReferenceEquals($policy, $DeadlinePolicy)) {
        throw '共有期限がコマンドごとに置き換わりました。'
    }
    if ($CommandPath -eq 'git') {
        if ($failure -eq 'git') { throw 'probe-git-failure' }
        if ($Probe -eq 'Selection') {
            return & $realMonitoredCommand @PSBoundParameters
        }
        return [pscustomobject]@{ StandardOutput = $gitOutput }
    }

    $stage = if ($Arguments[0] -eq 'restore') { 'restore' } else { $Arguments[1] }
    [void]$events.Add($stage)
    [void]$commands.Add([string[]]$Arguments)
    if ($failure -eq $stage) { throw "probe-$stage-failure" }
    if ($Probe -eq 'Pipeline' -and $stage -in @('style', 'whitespace')) {
        $target = Join-Path $repoRoot 'Changed.cs'
        $content = [IO.File]::ReadAllText($target)
        $content = if ($stage -eq 'style') { $content.Replace('unformatted', 'styled') } else { $content.Replace('styled', 'formatted') }
        [IO.File]::WriteAllText($target, $content)
    }
    return [pscustomobject]@{ StandardOutput = '' }
}

function Write-TestFile {
    param([string]$Path, [string]$Content = "initial`n")
    $fullPath = Join-Path $repoRoot $Path
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullPath))
    [IO.File]::WriteAllText($fullPath, $Content)
}

function Invoke-TestGit {
    param([string[]]$Arguments)
    & git -C $repoRoot @Arguments >$null
    if ($LASTEXITCODE -ne 0) { throw "Git fixture の準備に失敗しました: $($Arguments -join ' ')" }
}

try {
    if ($Probe -eq 'Selection') {
        # staged・unstaged・両方・HEADとの相殺・rename・削除を実際の作業ツリーで作る。
        Invoke-TestGit @('init', '--quiet', '--initial-branch=test', '--template=')
        Invoke-TestGit @('config', 'core.autocrlf', 'false')
        Invoke-TestGit @('config', 'core.quotepath', 'true')
        Write-TestFile '.gitignore' "/ignored/`n"
        foreach ($path in @('Staged.cs', 'Unstaged.cs', 'Both.cs', 'Cancelled.cs', 'Deleted.cs', 'Old.cs', 'Untouched.cs', 'src/bin/Tracked.cs')) {
            Write-TestFile $path
        }
        Invoke-TestGit @('add', '.')
        Invoke-TestGit @('-c', 'user.name=Formatting test', '-c', 'user.email=format-test@example.invalid', '-c', 'commit.gpgsign=false', 'commit', '--quiet', '-m', 'test fixture')
        foreach ($path in @('Staged.cs', 'Both.cs', 'Cancelled.cs')) { Write-TestFile $path "staged`n" }
        Invoke-TestGit @('add', 'Staged.cs', 'Both.cs', 'Cancelled.cs')
        foreach ($path in @('Unstaged.cs', 'Both.cs', 'src/bin/Tracked.cs')) { Write-TestFile $path "unstaged`n" }
        Write-TestFile 'Cancelled.cs'
        Invoke-TestGit @('mv', 'Old.cs', 'Renamed file.cs')
        Invoke-TestGit @('rm', '--quiet', 'Deleted.cs')
        foreach ($path in @('新 規.cs', 'File [1].cs', 'README.md', 'ignored/Skip.cs', 'obj/Skip.cs', '.tmp/Skip.cs', 'artifacts/verification/Skip.cs')) {
            Write-TestFile $path
        }
        Invoke-RepositoryFormatting -RepositoryRoot $repoRoot -Scope Changed -DiagnosticsDirectory $diagnostics -DeadlinePolicy $policy -NoRestore 6>$null
        [ordered]@{ commands = $commands.ToArray() } | ConvertTo-Json -Depth 8 -Compress
        return
    }

    # 重い復元・ビルド・テストは代替し、標準入口の実際の呼出関数を実行する。
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $sourceRoot 'scripts/verify-refactor.ps1'), [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) { throw ($parseErrors -join "`n") }
    foreach ($name in @('Invoke-RepositoryFormattingPhase', 'Invoke-CanonicalFunctionalVerification', 'Invoke-FilteredQuickVerification')) {
        $definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
        if ($null -eq $definition) { throw "実行関数がありません: $name" }
        . ([scriptblock]::Create($definition.Extent.Text))
    }
    function Invoke-MonitoredVerificationPhase {
        param($Name, $DiagnosticsRoot, $Action)
        & $Action ([Diagnostics.Stopwatch]::StartNew()) (Join-Path $DiagnosticsRoot $Name) $policy
    }
    function Invoke-BudgetedCommand {
        param($Stopwatch, $BudgetSeconds, $Label, $CommandPath, $Arguments, $DiagnosticsDirectory)
        [void]$events.Add($Arguments[0])
        if ($Arguments[0] -in @('build', 'test')) {
            [void]$buildInputs.Add([IO.File]::ReadAllText((Join-Path $repoRoot 'Changed.cs')))
        }
    }
    function Assert-RepositoryWhitespace {
        param($DiagnosticsDirectory, $DeadlinePolicy)
        [void]$events.Add('git-whitespace')
    }
    function Assert-BuiltOutputs { [void]$events.Add('outputs') }
    function Invoke-ParallelFunctionalTestShards {
        param($DiagnosticsDirectory, $TimeoutSeconds)
        [void]$events.Add('tests')
    }
    function Get-TestArguments {
        param($Filter, $DiagnosticsDirectory)
        return @('test')
    }

    $results = @(
        foreach ($scenario in @('Functional', 'Quick', 'Full', 'Empty', 'GitFailure', 'StyleFailure', 'WhitespaceFailure', 'Manual')) {
            $commands.Clear()
            $events.Clear()
            $buildInputs.Clear()
            Write-TestFile 'Changed.cs' 'unformatted'
            $Mode = if ($scenario -in @('Quick', 'Full')) { $scenario } else { 'Functional' }
            $gitOutput = if ($scenario -eq 'Empty') { '' } else { "Changed.cs`0" }
            $failure = switch ($scenario) {
                'GitFailure' { 'git' }
                'StyleFailure' { 'style' }
                'WhitespaceFailure' { 'whitespace' }
                default { '' }
            }
            $errorMessage = ''
            try {
                if ($scenario -eq 'Manual') {
                    Invoke-RepositoryFormatting -RepositoryRoot $repoRoot -Scope Changed -DiagnosticsDirectory $diagnostics -DeadlinePolicy $policy 6>$null
                }
                elseif ($Mode -eq 'Quick') {
                    Invoke-FilteredQuickVerification -Filter 'example' -DiagnosticsRoot $diagnostics -TimeoutSeconds 20 6>$null
                }
                else {
                    Invoke-CanonicalFunctionalVerification -DiagnosticsRoot $diagnostics -TimeoutSeconds 20 6>$null
                }
            }
            catch { $errorMessage = $_.Exception.Message }
            [pscustomobject]@{
                scenario = $scenario
                events = $events.ToArray()
                buildInputs = $buildInputs.ToArray()
                commands = $commands.ToArray()
                error = $errorMessage
            }
        })
    $results | ConvertTo-Json -Depth 8 -Compress
}
finally {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
}
