# PreToolUse フック: コミットと dev への統合の直前に、完了判定の確認事項を文脈へ添える。
# 実行は止めない。判断は責任者に残し、確認事項がコンテキストから抜けることだけを防ぐ。
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)

# 設定側の `if` は解析できない複合コマンドでも発火するため、実際のコマンドをここでも判定する。
$command = ''
try {
    $command = [string]([Console]::In.ReadToEnd() | ConvertFrom-Json).tool_input.command
}
catch {
    exit 0
}
if ($command -notmatch '(^|[;&|\s])git\s+(commit|merge\s+--ff-only)(\s|$)') {
    exit 0
}

$context = @'
コミット・統合の前に確認します（正本: devdocs/spec/development/agent-workflow.md の「工程の適用範囲」「完了判定」）。
- 区分に応じた実行検証の成功と最終レビューの合格が、いまコミット・統合する最終差分に適用できるか。軽微な修正で省略した工程は報告に含める。
- 一つの実装単位だけを含み、無関係な差分が混ざっていないか。
- 件名は適切なプレフィックスと日本語の現在形、本文は箇条書きか。
'@
$result = @{
    hookSpecificOutput = @{
        hookEventName     = 'PreToolUse'
        additionalContext = $context
    }
}
[Console]::Out.Write(($result | ConvertTo-Json -Depth 4 -Compress))
