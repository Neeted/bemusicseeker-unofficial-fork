---
name: review-cycle
description: 実装と局所検証が済んで凍結・予備レビュー・最終レビューへ進むとき、またはレビューの指摘や検証失敗を受けて修正へ戻るときに使う。レビューの進行、差し戻し先、反復回数と再計画、完了判定を正本から読み込む。
allowed-tools: PowerShell(pwsh -NoProfile -File .claude/scripts/print-sections.ps1 *)
shell: powershell
---

# 凍結・レビュー・完了判定

以下は、正本の該当節を起動時に取り出したものです。契約は正本にあり、本書には複製していません。節の本文が展開されていない場合や途中で切れている場合は、正本の該当節を直接読みます。

## 進め方

1. 「レビューの進行」の開始条件を満たしてから凍結します。`verify` の完了は待ちません。
2. 予備レビューは毎回新しい `preflight-static-review`、最終レビューは区分に応じて `repo-static-review` か `critical-static-review` を呼びます。自己点検で置き換えません。
3. 指摘を受けたら、修正を始める前に「不備の差し戻し」と「反復回数と再計画」で差し戻し先と回数を確認します。
4. 「完了判定」の両方の合格を確認してから、実装単位ごとにコミットします。

レビューを領域で分ける場合は、正本の「領域単位の凍結」も読みます。担当の呼び方と待ち方は `CLAUDE.md` の「担当の呼出しと待ち方」に従います。

## 正本の該当節

!`pwsh -NoProfile -File .claude/scripts/print-sections.ps1 devdocs/spec/development/agent-workflow.md "レビューの進行" "予備レビュー" "最終レビュー"`

!`pwsh -NoProfile -File .claude/scripts/print-sections.ps1 devdocs/spec/development/agent-workflow.md "不備の差し戻し" "反復回数と再計画" "完了判定"`
