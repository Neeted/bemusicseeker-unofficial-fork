---
name: dev-docs
description: devdocs 配下の仕様・計画・設計判断・測定資料を作成・編集するとき、最初の編集の前に使う。現行情報の維持、書き方、仕様書の書式、文書変更の確認を正本から読み込む。
allowed-tools: PowerShell(pwsh -NoProfile -File .claude/scripts/print-sections.ps1 *)
shell: powershell
---

# 開発資料の編集

以下は、正本の該当節を起動時に取り出したものです。契約は正本にあり、本書には複製していません。節の本文が展開されていない場合や途中で切れている場合は、正本の該当節を直接読みます。

計画を編集する場合は `devdocs/plan/README.md` の「運用」も読みます。

## 正本の該当節

!`pwsh -NoProfile -File .claude/scripts/print-sections.ps1 devdocs/README.md "現行情報の維持" "書き方" "文書変更の確認"`

!`pwsh -NoProfile -File .claude/scripts/print-sections.ps1 devdocs/spec/README.md "仕様書の書式"`
