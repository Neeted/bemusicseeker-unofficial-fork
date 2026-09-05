# 非同期ワークフロー・並行性・version token の複雑性管理指針

- **文書種別:** 共通の非同期・並行性設計契約
- **正本配置:** `devdocs/spec/workflow-concurrency-and-complexity.md`
- **対象:** UI、ViewModel、scheduler、domain owner、DB／filesystem mutation、cache／projection、background task
- **状態:** Active

## 1. 目的

この文書は、GUI の応答性を維持しながら、非同期ワークフロー、並行操作、snapshot、deferred publication、version／generation token がアプリ全体へ過剰に増殖することを防ぐための指針を定める。

特に、次の問題を避けることを目的とする。

- UI thread を止めないことと、domain mutation を同時実行可能にすることの混同。
- model lock を短くすることと、操作全体の論理 ownership まで短くすることの混同。
- 古い UI 表示からの入力を受け付けることと、その操作を何としても完遂することの混同。
- owner 間の順序や依存関係を、広い version token の一致だけで推測する設計。
- commit 後の callback、notification、cache invalidation が、先に作成した snapshot／receipt を意図せず無効化する構造。
- 個別の race 修正として token、retry、再検証、例外規則を継ぎ足し、全体の状態空間を増やし続けること。
- 実装、テスト、レビューが同じ誤った並行性前提を共有し、重大な不整合を通過させること。

この指針は全面的な即時リファクタリングを要求しない。今後の変更で複雑性を増やさず、触れた workflow から段階的に整理するための正本とする。

## 2. 要約

基本方針は次のとおり。

1. **UI の非同期性と domain mutation の並行性を分けて考える。**
2. **UI thread は止めないが、競合する mutation は queue／lane で直列化してよい。**
3. **model lock は短く保つが、操作全体の論理 ownership は完了まで保持してよい。**
4. **古い UI 入力は authoritative owner の入口で stable identity から現在の対象へ再解決し、その後に対象の意味を再解釈しない。**
5. **version token は read-only／latest-wins／cache validation に限定し、durable mutation の順序保証の代替にしない。**
6. **durable surface は単一 owner、明示的 transaction、immutable plan、typed result で管理する。**
7. **新しい並行性保証が明示要件でない場合、直列化を既定とする。**
8. **既存 workflow は一括置換せず、今後触る単位から token、callback、writer、状態を減らす。**

## 3. 背景と問題認識

### 3.1 UI を止めないことは、mutation を並行実行することではない

GUI アプリとして必要なのは、主に次の保証である。

```text
UI thread で長時間の DB、filesystem、network、解析処理を行わない。
```

これは、次を保証することとは異なる。

```text
状態を変更する複数の操作を、同じ正本に対して同時実行できる。
```

UI thread を応答可能に保ったまま、競合操作を queue へ入れることはできる。

```text
ユーザーが操作Aを開始
  -> background laneでAを実行
  -> UIは応答可能

Aの途中で操作Bを入力
  -> 入力は受理
  -> Bは「Aの完了待ち」としてqueue
  -> UIは応答可能

A完了
  -> Bを現在状態から再解決して実行
```

この構造では UI は止まらない。一方、domain mutation の意味上の順序は明確であり、広い version token で後から整合性を推測する必要がない。

### 3.2 短い lock と短い ownership は別である

長時間 I/O 中に collection lock や DB transaction を保持しないことは妥当である。しかし、操作全体の論理 ownership まで解放する必要はない。

推奨形は次のとおり。

```text
logical mutation laneを取得
  -> 短いlockでimmutable input snapshotを取得
  -> lockを解放
  -> 長時間のI/O／解析
  -> immutable mutation planを作成
  -> 短いlock／transactionで検証・commit
  -> publicationを直列に完了
  -> laneを解放
```

長く保持するのは model lock ではなく、「同じ durable surface を変更する別操作を開始させない」という論理 ownership である。

### 3.3 古い表示からの入力受理と、古い意図の強制完遂は別である

古い UI 表示から command を受け付ける場合でも、各 phase で何度も version を比較し、対象を再解釈し続ける必要はない。

推奨する契約は次のとおり。

```text
UIはstable identityと意図を送る
  -> command開始時に現在の正本から一度だけ再解決
  -> 対象が既に存在しなければ「既に変更されています」と終了
  -> 解決後はmutation lane内で一つの操作として完了
```

「入力を受理する」とは、必ず旧表示どおりの対象へ適用することではない。現在状態で安全に再解決できない場合は、明示的な stale-input result を返してよい。

### 3.4 deferred callback は因果関係を見えにくくする

次のような分割は lock 時間を短くできる一方、順序をコードの字面から追いにくくする。

```text
DB commit
  -> post-lease effectを登録
  -> lease解放
  -> notification
  -> collection version更新
  -> cache invalidation
  -> background warmup
  -> UI publication
```

callback ごとに「まだ有効か」を確認し始めると、operation token、collection version、cache generation、scheduler generation が増える。

可能な限り、一つの workflow owner が次を明示的な順序で完了させる。

```text
prepare
  -> commit
  -> authoritative in-memory apply
  -> required publication
  -> best-effort notification
  -> terminal result
```

必須の publication と best-effort notification を同じ callback list へ混在させない。

### 3.5 広い version は依存関係の代替になりやすい

ある計算が BMS row だけに依存する場合、BMS row revision だけが invalidation source であるべきである。

`OwnedCollectionVersion` のように複数 surface を含む広い token を利用すると、無関係な変更でも正しい結果が stale 扱いになる。局所的には安全寄りでも、次の問題を生む。

- valid result の不要な discard。
- 高価な再走査・再解析。
- version を進める callback の順序依存。
- reason ごとの例外規則。
- token 間の包含関係や整合性の推測。
- 「一致しているのに誤っている」「不一致だが本当は有効」の双方。

依存 surface を正確に表せない場合、より広い token を追加する前に、workflow ownership または入力 DTO の見直しを優先する。

### 3.6 局所的に合理的な修正が全体を複雑化する

次の変更は単独では合理的に見える。

```text
古いUI結果を防ぐためrequestVersion追加
重複実行を防ぐためgeneration追加
古いcacheを防ぐためinvalidationVersion追加
snapshot staleを防ぐためcollectionVersion追加
shutdown後のapplyを防ぐためoperationToken追加
```

しかし、各 token の owner、increment point、validity interval、包含関係、破棄条件が統合されないと、token が logical clock の集合として振る舞い始める。

単一プロセスの GUI アプリで、分散システムに近い eventual consistency を構築しないことを既定とする。

## 4. 用語の区別

`version`、`generation`、`token` という命名だけでは意味を判断できないため、用途を次のように区別する。

| 種類 | 意味 | 主な利用先 | result discard |
|---|---|---|---|
| UI request generation | 最新の検索・sort・preview要求 | View／read model | 可 |
| Operation identity | 同じ画面上の旧operation完了通知を識別 | progress／terminal apply | 条件付きで可 |
| Model revision | authoritative sourceが変更されたか | snapshot validation | mutationでは原則fail/requeue |
| Cache generation | 派生cacheがどのsourceに対応するか | read-only cache | 可 |
| Publication sequence | notificationの表示順 | UI presentation | 可 |
| Durable schema version | DB形式・migration世代 | persistence | discard用途ではない |
| Compatibility signature | 生成規則の互換性 | one-shot migration／rebuild | 明示migration判断 |

新しい token を追加する場合、上記のどの種類かを必ず明示する。複数種類を一つの `Version` へ兼用しない。

## 5. 規範的な設計原則

以下の `MUST`、`SHOULD`、`MAY` は、今後の変更と触れた既存範囲に適用する。

### 5.1 UI responsiveness

- UI thread は長時間の DB、filesystem、network、解析を実行してはならない。
- UI を応答可能にするために、競合 mutation の同時実行を保証してはならない。
- 待機中の command は queue 状態として表示してよい。
- read-only 表示、検索、sort、preview は latest-wins で並行実行してよい。

### 5.2 Mutation ownership

- durable surface ごとに authoritative writer owner を一つ定める。
- 同じ durable surface を変更する競合 workflow は、原則として同じ logical mutation lane へ入れる。
- lock は短く保つが、logical lane は workflow の terminal state まで保持してよい。
- operation gate 保持中に UI thread、dialog、event subscriber、別 owner の同期完了を待ってはならない。

### 5.3 Input and snapshot

- stale な UI object は authoritative owner の入口で stable identity から現在の対象へ一度だけ再解決し、その後の phase で利用者の意図や対象 identity を再解釈しない。
- 外部変更、path safety、transaction precondition など feature spec が要求する lease／commit 境界の invariant は別途再検証する。
- 長時間処理には immutable snapshot または immutable request を渡す。
- snapshot の依存 surface を列挙できない場合、広い version token で補わず、request boundary を見直す。
- stale input は明示的な terminal result として返し、暗黙の retry loop を追加しない。

### 5.4 Commit and publication

FS と DB を跨ぐ場合の成功・部分失敗、限定補償、前方回復、非収束の許容範囲は [file-db-consistency.md](file-db-consistency.md) に従う。ここでいう commit の順序は複数 surface の原子性を意味しない。

- multi-step、破壊的、または複数 durable surface を跨ぐ mutation は immutable mutation plan を作成してから適用する。単一 owner 内の単純な atomic update は typed request と transaction invariant で足りる。
- commit 前の検証、durable commit、authoritative in-memory apply、required publication の順序を一つの owner が定義する。
- required publication failure を通常の `Completed` に埋め込まない。
- best-effort notification は durable success と分離し、失敗しても正本の意味を変えないものに限定する。
- callback list に必須処理と optional 処理を混在させない。

### 5.5 Concurrency default

- 明示的なユーザー要件または性能要件がない競合 mutation は、直列化を既定とする。
- 「UIを止めたくない」だけを、mutation concurrency の根拠にしてはならない。
- 同時実行を導入する場合、操作互換性表、競合時の結果、commit 順序、failure contract を先に定める。
- 同時実行による利益が、状態数・token・retry・test matrix の増加を上回ることを説明できなければ導入しない。

## 6. 操作種別ごとの共通既定

| 組合せ | 共通既定 |
|---|---|
| 同じ durable surface を変更する操作同士 | 同じ owner／lane で直列化する |
| schema migration と DB mutation | feature spec で queue または reject と利用者向け結果を一意に決める |
| authoritative state を変更しない独立 read | 並行実行してよい |

個別機能の observable behavior は対象 feature spec を正本とする。共通既定と異なる並行性を採用する場合は、`devdocs/decisions/` に理由と代替案を記録する。

## 7. version token 使用ポリシー

### 7.1 推奨される用途

version／generation token は、結果を安全に捨てられる read-only／presentation 処理で有効である。

- keyword search。
- sort order／virtual view。
- preview、thumbnail、補助表示。
- read-only cache。
- coalesced refresh。
- UI terminal apply の旧operation識別。
- sourceが明確な派生index。

### 7.2 原則禁止する用途

次の正しさを version 一致だけで保証してはならない。

- durable DB mutation の commit 順序。
- filesystem mutation の対象決定。
- migration完了条件。
- 全表置換の入力完全性。
- 複数owner間の必須publication順序。
- 削除対象集合の正当性。
- user-owned fieldの保持。
- transaction成功の判定。
- retry／resume開始位置。
- required post-commit処理の成功。

### 7.3 新しい token を追加・拡張するための必須情報

新しい token、既存 token の意味拡張、比較箇所の追加には、次を同じ変更で記録する。

| 項目 | 必須内容 |
|---|---|
| Token kind | UI request / operation / model revision / cache / publication / durable schema |
| Owner | 誰が値を進めるか |
| Source surface | 何の変更を表すか |
| Increment points | どのcommit／publication後に進むか |
| Validity interval | いつからいつまで有効か |
| Consumers | 誰が何の判断に使うか |
| Mismatch behavior | discard / requeue / fail / rebuild のどれか |
| Unrelated changes | 何が変わっても無効化されないか |
| Runtime evidence | 到達可能な race／性能問題の診断に必要な場合の published／consumed／discarded evidence |
| Retirement condition | 一時的tokenなら削除条件 |

この表を埋められない場合、token を追加しない。

### 7.4 broad token の利用条件

複数 surface を含む broad token は、read-only cache 全体を保守的に無効化する場合に限り許容する。

- narrow fact／receipt の正当性判定に使わない。
- durable mutation の成功条件に使わない。
- unrelated surface の変更で高価な再処理が起きる場合、source-specific revisionへ分離する。
- broad token と narrow token の双方を比較する場合、なぜ両方が必要かを記録する。

### 7.5 reason文字列を制御に使わない

`reason.StartsWith(...)` のような文字列判定で concurrency、receipt消費、retry可否を決めない。

制御上意味がある場合は enum、typed request、typed origin を使う。

```csharp
internal enum WorkflowOrigin
{
    Startup,
    UserReload,
    Retry,
    ManualMaintenance
}
```

ログ用の reason と制御用の typed value を分離する。

## 8. 推奨ワークフロー形

### 8.1 Read-only latest-wins workflow

```text
UI request generation発行
  -> immutable query作成
  -> background計算
  -> generation一致ならUI apply
  -> 不一致なら結果を破棄
```

適用条件:

- durable stateを変更しない。
- 結果破棄が利用者データを失わない。
- 再計算可能。

### 8.2 Durable mutation workflow

```text
command受付
  -> logical mutation lane取得
  -> current authoritative stateから対象再解決
  -> immutable snapshot取得
  -> I/O／解析
  -> immutable mutation plan作成
  -> commit前invariant検証
  -> DB／filesystem commit
  -> authoritative model apply
  -> required publication
  -> typed terminal result
  -> best-effort notification
  -> lane解放
```

version mismatch による silent discard を terminal success にしない。

### 8.3 Cache rebuild workflow

```text
source-specific revision取得
  -> immutable source snapshot取得
  -> lock外でcache構築
  -> 同じsource revisionならpublish
  -> 不一致なら破棄または一回coalesce
```

自動retryは無制限にしない。高頻度更新時は latest requested revision を一つだけ保持する。

## 9. Mutation lane の考え方

lane は UI threadを止めるlockではない。意味上競合するcommandを並べるlogical ownerである。

候補例:

```text
CatalogMutationLane
LibraryFileMutationLane
PlaylistMutationLane
Lr2DatabaseMutationLane
SettingsPersistenceLane
PresentationLane
```

lane 数は増やしすぎない。新しい lane を作る前に、既存 lane との互換性を検討する。

### 9.1 操作互換性表

cross-workflow 変更では、対象 feature spec または計画に、実行中の操作、新規操作、queue／reject／coalesce／parallel の一意な方針、選択理由を記録する。表にない組合せを「何となく並行可能」とみなさない。

## 10. 新規変更・機能追加の指針

### 10.1 変更開始時

次を確認する。

1. durable／authoritative surface は何か。
2. writer owner は誰か。
3. UI responsiveness と mutation concurrency のどちらが本当に必要か。
4. 競合操作は queue／reject／coalesce／parallel のどれか。
5. command開始時に一度再解決すれば足りないか。
6. logical ownershipをworkflow全体で保持できない理由があるか。
7. version tokenなしで明示的順序にできないか。
8. callbackをowner内の直列処理へ戻せないか。
9. success／failure／partial success のterminal stateは何か。
10. user-owned dataは何か。

### 10.2 既定の判断

要件が曖昧な場合は次を既定とする。

- UI thread外で実行する。
- 競合mutationはqueueする。
- read-only処理だけ並行化する。
- stale UI inputは開始時に再解決する。
- tokenを追加しない。
- retry／resumeを追加しない。
- best-effort処理をsuccess条件に混ぜない。

### 10.3 version token追加の代替案

新しいtokenを追加する前に、順に検討する。

1. 同じlaneで直列化できないか。
2. ownerを一つへ統合できないか。
3. immutable requestへ必要なfactを直接含められないか。
4. commit後のtyped receiptを直接渡せないか。
5. callbackを明示的なawait順へ戻せないか。
6. source-specific revisionに限定できないか。
7. 結果を捨てられるread-only処理か。

1～6で解決できず、7が成立する場合にtokenを使う。

## 11. 禁止・警戒パターン

以下を新しく追加する場合は原則としてreplanする。

- narrow factをglobal collection versionで検証する。
- commit前にreceiptを発行し、後続callbackがそのversionを進める。
- 同じtable／file surfaceを複数ownerが直接書く。
- `Completed`へfailure detailを埋め込むが、callerが見ない。
- version mismatch時に無制限retryする。
- string reasonでreceipt／retry／ownershipを制御する。
- stale判定のたびに別versionを追加する。
- snapshotのsource surfaceが説明できない。
- cache invalidationとauthoritative mutationを同じversionで表す。
- UI操作を拒否したくないという理由だけでmutationを並行化する。
- callback list内の順序が正しさに影響するが、型とtestに表れていない。
- 旧routeと新routeが同じdurable surfaceを両方更新する。
- 到達しないdefensive stateのためにrepair／resume／fallbackを追加する。

## 12. Completion と runtime invariant

例外が投げられなかっただけで `Completed` にしない。

workflowごとに、直接得られるfactからcompletion invariantを定義する。

例:

```text
processed + skipped + failed == total
required source count == accounted source count
user-owned update count == 0
required publication count == expected publication count
mutation plan destination count == durable destination count
pending required callback count == 0
```

version tokenの一致を、入力完全性やdurable successの代わりにしない。

### 12.1 ログの役割

高リスクな durable／cross-owner workflow では、適用可能な terminal fact をログに残す。

- operation id。
- workflow origin。
- lane acquisition／wait。
- authoritative input revision。
- token publish／consume／discardと理由。
- processed／skipped／failed／total。
- commit開始／完了。
- required publication完了。
- terminal state。

単純な atomic update や per-item／per-consume hot path へ、これらを一律に追加しない。既存の operation identity や terminal count で原因を区別できる場合は、新しい counter やログを増やさない。

矛盾する件数を単にWARNとして残さず、completion invariant違反へ接続する。

## 13. テストとレビューの観点

### 13.1 production ingressを通す

内部serviceへ理想的なrequestを直接渡すtestだけで完了しない。

非同期順序、post-lease effect、version increment、scheduler enrollmentが関係する変更は、実際の入口を通す。

```text
UI／startup ingress
  -> owner
  -> scheduler／lane
  -> DB／filesystem commit
  -> deferred publication
  -> next workflow enrollment
  -> terminal state
```

### 13.2 timeline review

reviewerは各重要workflowについて、少なくとも次のtraceを一つ作る。

```text
T0 snapshot取得
T1 別操作またはnotification
T2 version進行
T3 commit
T4 publication
T5 consumer検証
```

各時点で、何がauthoritativeか、何が無効化されるか、silent discardが安全かを確認する。

### 13.3 旧test契約を疑う

ownership、writer、全表置換、source of truthを変更した場合、旧testをそのままgreenにすることを目的にしない。

各旧testについて次を判断する。

- 現在も正しいcontractか。
- 旧owner分割だけを固定していないか。
- 現実のproduction ingressを通るか。
- 実装の現在値を写経していないか。
- 退役または逆向きに置換すべきか。

### 13.4 Red evidence／negative control

ordering／token の test evidence は `test-authoring-contract.md` と承認済み Test Contract Packet に従う。production ingress から再現可能な bugfix は base-fail／head-pass を原則とし、base で構造上実行できない場合または packet が指定した場合だけ targeted negative control を用いる。

## 14. 複雑性予算

変更ごとに、少なくとも次の増減を確認する。

| 項目 | 追加時の扱い |
|---|---|
| durable state | 互換性・migration・削除条件を要求 |
| version／generation token | 依存表とownerを要求 |
| mutation lane | 操作互換性表を要求 |
| deferred callback | required／best-effort分類と順序を要求 |
| retry／resume branch | 明示的な利用者要件を要求 |
| writer owner | 既存writerとの統合を優先 |
| terminal state | caller／UI／logの全伝播を要求 |
| cache／projection | authoritative sourceとinvalidatorを要求 |

新しい状態やtokenを追加する変更では、可能なら不要になったものを同じ変更または追跡issueで退役させる。

計測対象の例:

```text
version token数
広いtokenのconsumer数
deferred callback登録点
同じdurable surfaceのwriter数
mutation lane数
retry／resume状態数
Completed系terminal state数
owner間callback edge数
```

数値自体を品質目標にはしない。増加傾向と、説明できないedgeを発見するために使う。

## 15. 今後の変更用チェックリスト

### Author checklist

- [ ] UI responsivenessとdomain concurrencyを分けて説明した。
- [ ] authoritative surfaceとwriter ownerを特定した。
- [ ] 競合操作をqueue／reject／parallelのどれにするか決めた。
- [ ] logical ownershipの範囲を定めた。
- [ ] stale UI input の再解決と、lease／commit 境界の invariant 再検証を区別した。
- [ ] version tokenなしで順序を表現できないか検討した。
- [ ] tokenを追加する場合、kind／owner／source／increment／consumerを記録した。
- [ ] required publicationとbest-effort notificationを分けた。
- [ ] `Completed` invariantを明示した。
- [ ] Test Contract Packet が要求する production ingress test を追加または更新した。
- [ ] 旧test契約が現在も正しいか再評価した。
- [ ] 増えたstate／token／callbackと、退役したものを記録した。

### Reviewer checklist

- [ ] 「UIを止めない」がmutation並行化の根拠になっていない。
- [ ] broad versionがnarrow factの正当性判定に使われていない。
- [ ] token発行後に、必要なconsumer前で同じtokenが進む経路がない。
- [ ] required callbackの実行順が明示されている。
- [ ] stale resultのdiscardがdurable dataを失わない。
- [ ] queueすべき競合mutationが楽観的に同時実行されていない。
- [ ] command開始後に対象の意味を各phaseで再解釈していない。
- [ ] 同じsurfaceのwriterが増えていない。
- [ ] successが例外なしだけで決まっていない。
- [ ] Test Contract Packet が要求する production ingress の時系列 test がある。
- [ ] base-fail／head-pass、または packet が指定した targeted negative control が plausible wrong implementation を区別する。

## 16. AI／Codexを利用する場合の境界

この指針は、モデルによるレビューだけでsystem-wide correctnessを保証できるという前提を置かない。

- モデルは「UIを非同期にする」という要件から、mutation concurrencyを推測してはならない。
- concurrency、ownership、transaction、completion semanticsは、承認済みspec／decisionから一意に決まる必要がある。
- 仕様、実装、test、reviewを同じモデル群が作った場合、それらを独立した証拠とみなさない。
- cross-owner、DB ownership、全表置換、migration、retry／resume変更は、人間または明示的authorityがdecision listを閉じる。
- 要件が不明な場合、モデルはtokenやfallbackを追加せず、直列化案またはreplanを返す。
- static reviewはdiffの局所説明だけでなく、production ingressのtimelineと操作互換性を確認する。

## 17. 保守方法

- この文書のownerは、architecture／workflow boundaryを変更する作業のroot ownerとする。
- version token、mutation lane、deferred callbackの新設時に、この文書の更新要否を確認する。
- feature固有の例を本文へ増やしすぎない。共通原則へ一般化できない内容はfeature specへ置く。
- 同じ内容を`AGENTS.md`、`architecture.md`、`workflows.md`へ重複管理しない。
- 実装と乖離した理想論にしない。例外が恒久化した場合はdecisionへ記録し、正本へ反映する。
