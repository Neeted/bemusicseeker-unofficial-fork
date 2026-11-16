---
name: parallel-units
description: 複数の実装単位を別ワークツリーで並列に進めるとき、作業ブランチを dev へ統合するときに使う。並列作業の上限と所有、担当の完了待ち、複数ワークツリーの検証、統合手順を正本から読み込む。
allowed-tools: PowerShell(pwsh -NoProfile -File .claude/scripts/print-sections.ps1 *)
shell: powershell
---

# 並列作業と統合

以下は、正本の該当節を起動時に取り出したものです。契約は正本にあり、本書には複製していません。節の本文が展開されていない場合や途中で切れている場合は、正本の該当節を直接読みます。

## 正本の該当節

!`pwsh -NoProfile -File .claude/scripts/print-sections.ps1 devdocs/spec/development/agent-workflow.md "並列作業" "担当の完了待ち"`

!`pwsh -NoProfile -File .claude/scripts/print-sections.ps1 devdocs/spec/development/testing.md "複数ワークツリーの検証"`

!`pwsh -NoProfile -File .claude/scripts/print-sections.ps1 devdocs/spec/development/release.md "通常の作業ブランチの統合"`
