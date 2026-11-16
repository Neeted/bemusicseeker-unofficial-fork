---
name: unit-workflow
description: 作業ツリーを変更する依頼（製品コード・テスト・スクリプト・文書・設定）に着手するとき、最初の編集の前に使う。工程の適用範囲、役割と順序、要件の確定、重大変更の判定、担当への引継ぎを正本から読み込む。
allowed-tools: PowerShell(pwsh -NoProfile -File .claude/scripts/print-sections.ps1 *)
shell: powershell
---

# 実装単位の開始

以下は、正本の該当節を起動時に取り出したものです。契約は正本にあり、本書には複製していません。節の本文が展開されていない場合や途中で切れている場合は、正本の該当節を直接読みます。

## 進め方

1. 「工程の適用範囲」で依頼の区分を決め、着手時の説明に区分を一言含めます。
2. 軽微な修正は「工程の適用範囲」が示す確認と報告を行います。通常変更と重大変更は「役割と順序」の順に進めます。各工程で呼ぶ担当と渡す入力は、下の各節に従います。
3. 利用者に見える挙動・保証の未確定事項は、設計を確定する前に質問します。
4. 実装と局所検証が済んだら `review-cycle`、実行する検証の範囲は `verify-scope`、並列に進める場合は `parallel-units` を呼びます。

担当の呼び方と待ち方は `CLAUDE.md` の「担当の呼出しと待ち方」に従います。

## 正本の該当節

!`pwsh -NoProfile -File .claude/scripts/print-sections.ps1 devdocs/spec/development/agent-workflow.md "工程の適用範囲" "役割と順序" "担当の共通契約" "要件と到達可能性" "重大変更の判定"`

!`pwsh -NoProfile -File .claude/scripts/print-sections.ps1 devdocs/spec/development/agent-workflow.md "設計相談" "テスト設計の要否" "実装担当への入力" "引継ぎの点検"`
