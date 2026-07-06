# REF-MVP Release Freeze and Gate Decision

作成日: 2026-07-07

## 決定

Refactoring MVP Gate 通過まで、リリース作業を凍結する。

Codex の実装単位は、DTO / checkpoint 単体ではなく、1 workflow / 1 responsibility boundary へ引き上げる。

## 理由

- `MainWindowViewModel`、`MainWindow.cs`、`BMSLibrary` の責務分離が未完了のまま release / `.NET 10` 本移行へ進むと、移行 blocker が巨大クラス内の未整理ロジックと混ざる。
- 旧 `L-3c-*` の DTO / checkpoint 単位は progress は出るが、MVVM としての責務分離という本来の成果へ届きにくい。
- リリース作業を freeze することで、version / release notes / publish script 変更へ作業が逸れることを防ぐ。

## Gate 通過の意味

Gate 通過は「完璧な最終構造」ではなく、次の状態を指す。

- root ViewModel / code-behind / domain facade の主要 workflow が child ViewModel / coordinator / service へ移っている。
- `.NET 10` 移行 blocker が adapter / gateway / native dependency / config / settings / external process host の課題として説明できる。
- build / test / format / `git diff --check` / サブエージェント静的レビューが通る。

## 禁止事項

- release / version 関連ファイルのリリース目的更新。
- Git tag / GitHub Release / Release draft / 配布 package 作成。
- publish / release script 実行。
- `net10.0-windows` 本移行。

## 例外

- build / test のためのローカル artifact 作成。
- release freeze ルール自体の docs 更新。
- release 関連コードの refactor blocker 調査。
