# テスト運用方針

最終更新: 2026-08-03

この文書は BeMusicSeeker のテスト lane、標準コマンド、時間予算の正本である。機能回帰を短時間で検出する通常検証と、性能測定、大容量データ、外部プロセス、publish / update の受入検証を分離し、テスト追加によって通常検証が際限なく長時間化しないようにする。

## 運用目標

- 通常の機能検証は `verify-refactor.ps1 -Mode Functional` のコマンド全体を 180 秒以内で完了させる。個々の testhost や shard ごとの 180 秒ではない。
- Functional は、追跡対象ファイルを変更せず、実行順序や並列度によらず決定的に成功する。
- CPU と I/O は、安定性を維持できる範囲で十分に利用して wall-clock time を短縮する。マシン負荷を抑えることだけを理由に並列度を制限しない。
- リソース競合で不安定になる場合は、共有 state、fixture ownership、固定待ち、process / file / port の競合を修正する。
- timeout 時は process tree を停止し、active または last observed test、経過時間、標準出力・標準エラー、console progress / TRX / blame artifact の場所を残す。
- runner、lane、並列化、fixture 配置を変更した場合は、Functional を同一条件で 3 回連続実行し、各回が 180 秒以内であることを確認する。

## 標準コマンド

### Functional: 通常の機能検証

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional
```

この呼び出しの開始から終了までが 180 秒の時間予算である。solution の locked restore、build、論理的に一つの通常 test phase を含み、`dotnet test` は restore 済みの dependency graph を使う。内部 sharding の有無にかかわらず、時間予算は command 全体に一度だけ適用する。Functional からは `Performance`、`LargeFixture`、`ParserCompatibilityFull`、`ParserCompatibilitySlow`、`ProductionDiffFull`、`ProcessIntegration`、`ReleaseAcceptance` を除外する。

RID を指定しない素の `dotnet test BeMusicSeeker.sln /p:Configuration=Release` は標準入口ではない。この repository の lock file は `win-x64` dependency graph を含むため、素の restore は tracked lock file を書き換え、後続の publish policy test を壊すことがある。検証は標準スクリプトの locked `win-x64` restore と `--no-restore` test route を使う。

### Quick: 反復中の対象テスト

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter '<MSTest filter>'
```

実装中は変更した invariant に対応する filter を指定する。filter なしの Quick は後方互換のため Functional と同じ通常機能検証を行うが、全体確認では意図を明確にするため Functional を使う。

### Full: release / distribution 受入検証

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Full
```

Full は Functional に加えて、tool / analyzer、`ProcessIntegration`、self-contained publish、既存データ起動受入、update package 受入を実行する。Functional の 180 秒予算とは別の release 検証であり、通常のコード変更ごとには実行しない。Performance、LargeFixture、parser full / slow は Full にも自動では含めず、変更対象に応じて明示実行する。

Full 内でも Functional phase は build の直後、publish や update acceptance より前に実行する。release artifact workload の CPU / I/O の影響を通常機能検証へ持ち込まず、Functional の 180 秒予算を同じ条件で評価するためである。

## テスト lane

| lane | `TestCategory` | Functional | Full | 用途 |
| --- | --- | --- | --- | --- |
| 機能検証 | 省略または機能名 | 実行 | 実行 | 小さい合成入力による決定的な回帰検証 |
| 軽量互換検証 | `Compatibility` | 実行 | 実行 | 少数 fixture による parser / schema 互換 |
| 外部プロセス統合 | `ProcessIntegration` | 除外 | 明示実行 | updater、別 process、PowerShell 等との実統合 |
| リリース受入 | `ReleaseAcceptance` | 除外 | 明示実行 | publish artifact、update package、実アプリ smoke |
| 性能測定 | `Performance` / `Net10Performance` | 除外 | 除外 | 変更前後の性能比較と退行調査 |
| 大容量 fixture | `LargeFixture` | 除外 | 除外 | 実 DB や大量 fixture の互換性検証 |
| Parser full | `ParserCompatibilityFull` | 除外 | 除外 | 実譜面全件の参照実装互換 |
| Production diff | `ProductionDiffFull` | 除外 | 除外 | production data との差分調査 |
| Parser slow | `ParserCompatibilitySlow` | 除外 | 除外 | 既知の巨大・低速 fixture |

カテゴリはテストの保証内容ではなく実行特性を表す。たとえば軽量な互換性テストは `Compatibility` のまま Functional に含める。複数 lane に該当するときは、より重い実行特性のカテゴリを追加する。

## Functional に含めるテスト

- 小さい合成入力または少数 fixture で完結する機能テスト。
- in-memory / temp directory / deterministic fake で閉じる DB、filesystem、audio、dispatcher、network boundary のテスト。
- test ごとの固有 resource を所有し、並列実行しても競合しないテスト。
- timeout が機能契約そのものである場合を除き、wall clock の偶然ではなく signal、barrier、fake clock、完了通知で同期するテスト。

Functional に含めないもの:

- 処理時間や比率そのものを測定する benchmark / performance test。
- 実譜面数百〜数千件、巨大 DB、大量コピーを必要とする互換性棚卸し。
- updater や別の `dotnet` / PowerShell process、publish artifact、実アプリを起動する end-to-end 受入検証。
- 固定 sleep の経過だけで非同期処理の完了を推定するテスト。

## 非同期・並列テストの規則

- MSTest の assembly-level parallel execution を標準とする。`DoNotParallelize` は process-wide singleton、固定 port、外部アプリなど、分離できない共有 resource が実在する場合だけ使い、理由をコメントまたは fixture contract で示す。
- 固定 `Thread.Sleep` や余裕時間としての長い `Task.Delay` を待機手段にしない。`TaskCompletionSource`、`ManualResetEventSlim`、channel、fake scheduler / clock など、観測対象の state transition と直接結び付く同期を使う。
- deadlock / cancellation timeout は機能テストに含めてよいが、通常完了を固定時間で待つのではなく、短い failure watchdog と決定的な完了 signal を組み合わせる。
- 性能閾値、応答時間分布、throughput は機能 assertion と混ぜず Performance lane へ置く。
- flaky、timeout、明白な長時間化を発見したら本筋を一旦止め、再実行だけで済ませない。共有 state、固定待ち、競合、I/O、入力規模、timeout 根拠を調査してから戻る。

## Parser・実データ互換検証

Parser の通常検証は小さい合成譜面、少数の実 fixture、既知 edge case を使う。full 検証は次のように明示して行う。

```powershell
$env:BMS_TEST_CHART_INFO_FULL = '1'
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'TestCategory=ParserCompatibilityFull'

$env:BMS_TEST_PRODUCTION_DIFF_FULL = '1'
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'TestCategory=ProductionDiffFull'

$env:BMS_TEST_CHART_INFO_SLOW = '1'
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'TestCategory=ParserCompatibilitySlow'
```

`ChartInfoParser`、`ChartInfoBuildService`、BMSON/BMS decode、`chart_info` schema 投影を変更した場合は該当 full lane を追加する。大容量 fixture の詳細は [TestData README](../../BeMusicSeeker.Tests/TestData/README.md) を参照する。

## 新しいテストを追加するとき

- 先にそのテストが保証する observable behavior と failure contract を一文で示す。
- Functional に入れるなら、少数の合成データと deterministic fake で再現できないかを確認する。
- 実データでしか再現しない場合も、最小 fixture に切り出せるなら全件 fixture に依存しない。
- 外部 process、実 app、publish artifact が必要なら対応する lane を付け、Functional へ混ぜない。
- 性能を測る場合は機能 assertion から分離し、`Performance` と opt-in 条件を付ける。
- `DoNotParallelize`、固定待ち、大容量 output copy を追加する場合は、必要性と通常検証の時間予算への影響をレビュー対象にする。

## 現状の改善 backlog

以下は既知の改善候補だが、Functional の最低目標達成に不要なら一括整理しない。

- 既存の `DoNotParallelize` を共有 resource の実在性で監査し、class 全体指定を resource 単位の分離へ縮小する。
- `Task.Delay`、`Thread.Sleep`、`WaitOne` 等を、通常完了待ち、failure watchdog、性能 assertion に分類し、通常完了待ちを決定的な signal へ置換する。
- updater 以外の process 起動テストを `ProcessIntegration` と軽量 contract test に分離する。
- 大容量 TestData の output copy を opt-in lane 専用にし、通常 build/test の I/O を削減する。
- 性能テストを専用 project または明示実行 profile へ移し、履歴比較可能な測定結果を保存する。
- test ごとの実行時間と flaky 履歴を継続収集し、Functional の 180 秒予算を超える前に増加を検出する。
