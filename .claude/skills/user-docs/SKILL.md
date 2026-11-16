---
name: user-docs
description: リリースノート、ユーザーマニュアル、利用者向けREADME・操作ガイドを作成・編集するとき、最初の編集の前に使う。利用者向け文書の編集方針と文書変更の確認を正本から読み込む。
allowed-tools: PowerShell(pwsh -NoProfile -File .claude/scripts/print-sections.ps1 *)
shell: powershell
---

# 利用者向け文書の編集

以下は、正本の該当節を起動時に取り出したものです。契約は正本にあり、本書には複製していません。節の本文が展開されていない場合や途中で切れている場合は、正本の該当節を直接読みます。

書き始める前に下の編集方針を読み、書き終えたら方針の各項目と照合します。文章の粒度は、方針が示す参考のリリースノートに合わせます。

## 正本の該当節

!`pwsh -NoProfile -File .claude/scripts/print-sections.ps1 devdocs/README.md "利用者向け文書の編集方針" "文書変更の確認"`
