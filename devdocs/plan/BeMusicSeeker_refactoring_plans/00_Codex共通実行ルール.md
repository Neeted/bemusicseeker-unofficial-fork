# Codex 共通実行ルール

[リファクタリング完了計画](./BeMusicSeekerリファクタリング計画.md)に基づいて、Codex が自走して実装・検証・静的レビュー・commit を繰り返すためのルールである。

## 権限と禁止事項

- outcome 内の implementation unit は、検証とサブエージェント静的レビュー後に自発的に commit してよい。
- unrelated な既存差分を変更、stage、commit しない。
- Refactoring Completion Gate 前は、ユーザーから依頼されても `git push`、tag、release、publish、version 更新を行わない。Gate 後もユーザーの明示指示なしには行わない。
- DB schema、setting key、serialized value、外部ファイル形式、UI observable behavior、public compatibility API を変更する必要が生じたら、実装前にユーザーへ確認する。
- 意味の変わる fallback を追加しない。失敗を隠すより、既存の失敗契約を維持して明示的に失敗させる。
- C# symbol rename は text replacement ではなく semantic rename / compiler-driven edit を使う。

## 作業開始

1. [PLAN_STATUS](./PLAN_STATUS.md) の active outcome と acceptance criteria を読む。
2. `git status --short` で既存差分を確認する。
3. active outcome がなければ、総合計画の ordered backlog から最初の未完・非 blocked outcome を選ぶ。
4. outcome 内の次 implementation unit を内部作業計画へ分解する。
5. checkpoint、decision、inventory、調査メモを新規作成せず実装へ進む。

future outcome を先に詳細設計しない。active outcome に必要な範囲だけ、実ソースと behavior test を読んで判断する。

## Implementation unit の条件

implementation unit は次を満たすまとまりにする。

- 同じ outcome の acceptance criteria を少なくとも 1 つ前進させる。
- build 可能で、関連 behavior を検証できる。
- 新しい abstraction を追加する場合、production 経路へ接続する。
- 旧 owner の責務、旧 route、旧 binding、旧 test seam のいずれかを減らす。
- 構造変更と意図的な挙動変更を混ぜない。

DTO、interface、result、planner、host、diagnostics API の追加だけで implementation unit を完了しない。安全上どうしても scaffolding commit が必要な場合も、同じ outcome を継続し、直後の unit で通常経路への接続と旧経路削除を行う。

## 実装・レビュー・commit ループ

1. implementation unit を実装し、必要な behavior test を追加または更新する。
2. 関連 build / targeted test を実行する。
3. format / whitespace と `git diff --check` を確認する。
4. サブエージェントに未コミット差分の静的レビューを依頼する。レビュー担当は編集・build・test・format・analyzer・commit を行わない。
5. 重大指摘を修正する。
6. 修正の影響を受ける build / test を再実行する。
7. 重大指摘がなくなるまで同じサブエージェントまたは別のサブエージェントへ再レビューを依頼する。
8. この unit で outcome を閉じる場合は、開始 commit からの全変更と現行コードを対象に full test とサブエージェント静的アーキテクチャレビューを行う。指摘があれば同じ unit で修正・再テスト・再レビューする。
9. outcome の全 acceptance criteria を満たした場合は、`PLAN_STATUS.md` で当該 outcome を `completed`、次 outcome を `ready` にする。Active outcome セクションを次 outcome の目的 / acceptance criteria / non-goals へ置き換え、Next action も更新する。この status 更新を含む最終未コミット差分を再レビューする。
10. 最終差分に対して必要な build / test / format / analyzer / `git diff --check` を実行する。
11. outcome ID を含む commit message で commit する。commit body に実行した test と review 結果を記録する。
12. outcome が未完なら docs-only checkpoint を挟まず同じ outcome の次 unit へ、完了したなら `ready` にした次 outcome へ進む。

`GATE-01` だけは完了時に `completed` ではなく `gate met` とし、Active outcome を `none`、次 outcome を設定しない。Release Freeze は `gate met / explicit release instruction required` と記録し、`.NET 10` migration plan またはリリース作業を自動開始しない。

レビュー修正後の再テストを省略しない。レビュー前の test 結果を最終差分の検証結果として扱わない。

## 標準確認

PowerShell 7 で実行する。

```powershell
dotnet build .\BeMusicSeeker.sln /p:Configuration=Release
dotnet test .\BeMusicSeeker.sln /p:Configuration=Release
dotnet format whitespace .\BeMusicSeeker.sln --verify-no-changes --no-restore --verbosity minimal
$msbuildPath = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -version "[17.0,18.0)" -products * -requires Microsoft.Component.MSBuild -find "MSBuild\Current\Bin"
dotnet roslynator analyze .\BeMusicSeeker.sln --msbuild-path $msbuildPath --properties Configuration=Release --severity-level warning --verbosity minimal
git diff --check
```

小さい unit は targeted test を先に実行してよい。共有 model、root ViewModel、DB、file system、settings、dispatcher、lock / concurrency に触れた場合は commit 前に full test を実行する。すべての outcome 完了時に full test を実行する。

SDK は `global.json` の .NET SDK 10 系を使う。Roslynator 0.12.0 が MSBuild 18 で動作しない間は、上記のとおり Visual Studio 2022 / MSBuild 17 を指定する。analyzer Gate はコマンドが正常終了し、今回差分による warning が増えていないこととする。既存 warning は `.codex/AGENTS.md` の分類規則に従い、info 診断は目的を定めた棚卸しでだけ扱う。

### UI outcome の smoke check

UI observable behavior に触れる outcome の完了時は、自動テストに加えて変更範囲に該当する次の操作を確認する。

- application startup、initial scan / reload、正常 shutdown。
- regular chart / playlist detail / playlist summary / play history の表示切替。
- table の sort、filter、selection、context action、drag-drop。
- settings dialog の open、edit、save、再表示。
- playback panel / external player host の主要操作。
- progress 表示、cancel、失敗時の dialog / status。

Codex が操作可能な環境では自走して確認する。外部 player、実データ、資格情報などが必要で確認できない項目だけを、outcome 完了前の manual evidence としてユーザーへ依頼する。

## サブエージェント静的レビュー

依頼文は次を基本形にする。

```text
現在の未コミット差分を静的レビューしてください。
編集、build、test、format、analyzer、commit は禁止です。
git diff / git status / rg / Get-Content などの読み取りだけを使ってください。

確認事項:
- active outcome の acceptance criteria を実際に前進させているか
- state と behavior の owner が明確になり、root の責務が減っているか
- abstraction / host / adapter / DTO を増やしただけになっていないか
- 旧 route、旧 binding、root relay、callback host、test-only production seam が不要に残っていないか
- 挙動、DB schema、setting key、serialized value、外部形式、lock ordering を意図せず変えていないか
- View / global singleton / Settings / NLog / DB / Dispatcher への依存方向を悪化させていないか
- private 実装配置を固定する brittle test を増やしていないか
- .NET 10 migration blocker を増やしていないか

重大度順にファイルと行番号を付けて返してください。
問題がなければ「重大な指摘なし」と返してください。
```

重大指摘には少なくとも次を含む。

- build / test / runtime behavior を壊す可能性が高い。
- persistence、serialization、UI observable behavior、public compatibility を意図せず変える。
- responsibility owner が増える、循環する、または root に残ったままになる。
- production の通常経路で使わない abstraction や test 専用 seam を追加する。
- global dependency、UI technology、DB connection、lock、Dispatcher の漏出を増やす。
- Gate の測定値だけを partial split や file move で満たす。

## Outcome 完了判定

implementation unit の積み重ねだけで outcome を自動完了にしない。開始 commit からの全差分と現行コードを確認し、総合計画の Outcome completion rule をすべて満たすことを確認する。

完了候補の unit を commit する前に、サブエージェントへ開始 commit 以降の commit 済み変更、現在の未コミット差分、現行コードをまとめて静的レビューさせる。重大指摘の修正、再テスト、再レビュー、`PLAN_STATUS.md` の完了更新を同じ最終 unit に含める。完了だけを記録する docs-only commit は作らない。

## 計画資料と ADR

active な計画資料は次の 4 ファイルに限定する。

- `BeMusicSeekerリファクタリング計画.md`
- `00_Codex共通実行ルール.md`
- `PLAN_STATUS.md`
- `DOTNET10_MIGRATION_BLOCKERS.md`

`PLAN_STATUS.md` は baseline、active outcome と acceptance criteria、outcome states、Gate scorecard、active outcome blocker、Next action だけを持つ。完了履歴、テスト件数、行数推移、次 seam の調査ログは Git commit に残す。

永続判断は原則として関連する正本の target / Gate / blocker policy へ反映する。独立 ADR は次をすべて満たす場合だけ、4文書制を拡張する理由とともにユーザーへ提案し、承認後に追加する。

- 複数の現実的な選択肢がある。
- outcome 完了後も判断理由を参照する必要がある。
- public API、persistence / schema、serialization、UI observable behavior、lock / concurrency、互換性、`.NET 10` migration policy のいずれかに影響する。

「次に切る helper」「次の private reflection test」「一時的な class / interface 配置」は ADR にしない。

## 自走と escalation

Codex は、active outcome の範囲内で設計・実装・test・review・commit を継続する。次の場合だけユーザーへ確認する。

- observable behavior、public compatibility、persisted data の意味を変える必要がある。
- 目標アーキテクチャまたは ordered backlog を実質的に変更する必要がある。
- 外部資産、資格情報、手動 UI 操作など、Codex だけでは取得できない情報が必要である。
- 安全な選択肢を調査しても、複数案の trade-off をユーザーが決める必要がある。

単に実装が大きい、難しい、時間がかかる、追加調査が必要という理由では停止しない。outcome を implementation unit に分けて進める。
