---
name: verify-scope
description: 実行する検証の範囲（局所MTP実行・dev・verify・full）を選ぶとき、検証の失敗・時間超過・不安定な結果を扱うときに使う。検証方針、実行範囲の選択、実行期限と失敗分類、不安定なテストの調べ方を正本から読み込む。
allowed-tools: PowerShell(pwsh -NoProfile -File .claude/scripts/print-sections.ps1 *)
shell: powershell
---

# 検証範囲の選択と失敗の扱い

以下は、正本の該当節を起動時に取り出したものです。契約は正本にあり、本書には複製していません。節の本文が展開されていない場合や途中で切れている場合は、正本の該当節を直接読みます。

標準入口の対象・必須ID・制限時間の一覧は `tools/BeMusicSeeker.Verify/README.md` が正本です。`verify` など数分を超える実行はバックグラウンドで開始し、完了通知を待ちます。複数ワークツリーで検証する場合は `parallel-units` を呼びます。

## 正本の該当節

!`pwsh -NoProfile -File .claude/scripts/print-sections.ps1 devdocs/spec/development/testing.md "検証方針" "実行範囲の選択" "実行期限と失敗分類"`

!`pwsh -NoProfile -File .claude/scripts/print-sections.ps1 devdocs/spec/development/test-authoring.md "非同期処理と不安定なテスト"`
