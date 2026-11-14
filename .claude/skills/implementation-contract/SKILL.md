---
name: implementation-contract
description: 担当定義の事前読込み用。実装担当の共通契約、入力、差し戻し、完了報告を正本から読み込む。
user-invocable: false
allowed-tools: PowerShell(pwsh -NoProfile -File .claude/scripts/print-sections.ps1 *)
shell: powershell
---

# 実装担当の契約

以下は、正本の該当節を起動時に取り出したものです。契約は正本にあり、本書には複製していません。節の本文が展開されていない場合や途中で切れている場合は、正本の該当節を直接読みます。

## 正本の該当節

!`pwsh -NoProfile -File .claude/scripts/print-sections.ps1 devdocs/spec/development/agent-workflow.md "担当の共通契約" "実装担当への入力" "不備の差し戻し" "実装担当の完了報告"`
