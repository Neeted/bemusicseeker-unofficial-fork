# 非同期ワークフロー・並行性・version token の複雑性管理指針

- **文書種別:** 共通の非同期・並行性設計契約
- **正本配置:** `devdocs/spec/workflow-concurrency-and-complexity.md`
- **対象:** UI、ViewModel、scheduler、domain owner、DB／filesystem mutation、cache／projection、background task
- **状態:** Active（設計・改修時の運用契約）
- **方針更新:** 2026-09-10

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

以下は改修時に守る採用済みの受付方針であり、現在の全入口が実装済みであることを表さない。現行の具体的な到達経路は feature spec、適用状況は [安全性改善計画](../plan/v3-safety-improvements-plan.md#concurrency-application) と [LR2 startup 手続き化計画](../plan/lr2-startup-procedural-orchestration-plan.md) で管理する。未実装の目標を現行動作・検証済みの保証として書き換えない。

### 性能要件との関係

性能上の優先順位と規模は [performance-and-scale.md](performance-and-scale.md) に従う。ここでいう UI responsiveness は UI thread の所有権・非同期待ち・確定済み表示の契約であり、UI に CPU を譲るために処理速度を落とす目標ではない。受理済み1操作内の独立計算を十分な並列度で実行することは、競合する新規 mutation を同時受理することとは別である。

短い lock や immutable snapshot を実現するために、操作ごとに全 catalog / 巨大 reverse root を複製しない。対象件数・依存範囲・コピーと再構築の回数を明示し、必要な逐次境界を残したまま余分な仕事を減らす。現在の lane / worker 数を、低 CPU 使用率のための恒久的上限と解釈しない。

## 2. 要約

基本方針は次のとおり。

1. **UI の非同期性と domain mutation の並行性を分けて考える。**
2. **UI thread は止めないが、競合する新規変更要求は Busy で未実行終了させる。暗黙の後続 queue を既定にしない。**
3. **model lock は短く保つが、操作全体の論理 ownership は完了まで保持してよい。**
4. **古い UI 入力は authoritative owner の入口で stable identity から現在の対象へ再解決し、その後に対象の意味を再解釈しない。**
5. **version token は read-only／latest-wins／cache validation に限定し、durable mutation の順序保証の代替にしない。**
6. **durable surface は単一 owner、明示的 transaction、immutable plan、typed result で管理する。**
7. **新しい並行性は要件なしに増やさない。section 6 の機能上必要な queue と既存の並行操作も、一律の排他強化で失わせない。**
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

通常の変更要求は次を既定とする。

```text
ユーザーが操作Aを開始
  -> 実行ownerで非待機の操作受付を取得
  -> AをUI threadを占有せずに実行。確定済み一覧は閲覧可能

Aの途中で競合する新規操作Bを入力
  -> Busyで未受理・未実行とする。Bの副作用や待機項目を作らない
  -> Aの完了後にBを自動実行しない

A完了後、利用者が改めてBを要求
  -> 受付を取得し、現在の正本から対象を解決して実行
```

導入中の追加 ZIP は必要な機能なので、既存の導入 queue で受理・順次実行する例外とする。自動推定など既に受理した仕事の順序待ちも、新規手動要求の Busy 拒否とは別である。受理済みの仕事を捨てる口実にこの既定を使わない。

### 3.2 短い lock と短い ownership は別である

長時間 I/O 中に collection lock や DB transaction を保持しないことは妥当である。しかし、操作全体の論理 ownership まで解放する必要はない。

推奨形は次のとおり。

```text
非待機でlogical mutation admissionを取得（競合中はBusyで終了）
  -> 短いlockでimmutable input snapshotを取得
  -> lockを解放
  -> 長時間のI/O／解析
  -> immutable mutation planを作成
  -> 短いlock／transactionで検証・commit
  -> 必要な正本反映・cleanup・結果を確定
  -> admissionを解放し、表示更新を既存経路へ通知
```

長く保持するのは model lock ではなく、競合する別操作を受理しないための論理 ownership である。表示通知の順序は feature の契約に従うが、全cacheの更新完了を待つ必要はない。論理受付、低層のデータ保護、進捗表示のactivityを混同せず、単一の巨大lockへ統合しない。

### 3.3 古い表示からの入力受理と、古い意図の強制完遂は別である

古い UI 表示から command を受け付ける場合でも、各 phase で何度も version を比較し、対象を再解釈し続ける必要はない。

推奨する契約は次のとおり。

```text
UIはstable identity・選択範囲・意図を送る
  -> 操作受付を取得してから現在の正本へ一度だけ再解決
  -> 対象が既に存在しなければ「既に変更されています」と終了
  -> 解決後はmutation lane内で一つの操作として完了
```

「入力を受理する」とは、必ず旧表示どおりの対象へ適用することではない。現在状態で安全に再解決できない場合は、明示的な stale-input result を返してよい。選択行番号、古い path、同一hashだけで別配置の譜面へ読み替えず、確認済みの対象集合を新しい検索結果へ黙って拡大しない。

閲覧に使う「古い表示」は以前の確定済みデータであり、workerが変更途中のmutable collectionを無保護で読むことではない。既存のread modelを利用し、操作ごとに全ライブラリを複製するsnapshot基盤は追加しない。

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
| UI request generation | 最新の検索・sort・副作用のない補助表示要求 | View／read model | 可 |
| Operation identity | 同じ画面上の旧operation完了通知を識別 | progress／terminal apply | 条件付きで可 |
| Model revision | authoritative sourceが変更されたか | snapshot validation | mutationでは未適用を明示。暗黙requeueなし |
| Cache generation | 派生cacheがどのsourceに対応するか | read-only cache | 可 |
| Publication sequence | notificationの表示順 | UI presentation | 可 |
| Durable schema version | DB形式・migration世代 | persistence | discard用途ではない |
| Compatibility signature | 生成規則の互換性 | one-shot migration／rebuild | 明示migration判断 |

新しい token を追加する場合、上記のどの種類かを必ず明示する。複数種類を一つの `Version` へ兼用しない。

## 5. 規範的な設計原則

以下の `MUST`、`SHOULD`、`MAY` は、今後の変更と触れた既存範囲に適用する。

### 5.1 UI responsiveness

- UI thread は長時間の DB、filesystem、network、解析で占有しない。外部アーカイバの入力寿命を守る [Drop acquisition](drop-install-ingress.md#acquire-before-enqueue) など、既存の限定された同期入口を機械的に非同期化しない。
- UI を応答可能にするために、競合 mutation の同時実行やキュー受理を保証してはならない。
- Busy は未受理・未実行、queued は受理済みと区別して表示する。ボタン無効化だけに頼らず、実行 owner 入口でも受付を閉じる。
- 確定済みread model上の閲覧・選択・検索・sortは継続してよい。常時最新であることを要求しない。
- 「preview」「background」という名称だけでread-onlyに分類しない。一時コピー、LR2設定保存、録音、chart-infoのDB補完等は変更・資源所有を伴う。画面切替後の遅い読取り結果を除外する既存identityまで削除しない。

### 5.2 Mutation ownership

- durable surface ごとに authoritative writer owner を一つ定める。
- 未承認の競合する新規変更は既存の論理受付で Busy 拒否する。同じDB/tableかどうかだけで並行許可を決めず、section 6 の例外を守る。writerの責務分割は、操作の同時受理を必要としない。
- lock は短く保つが、logical admission は必要な変更・補償・cleanupが終端するまで保持してよい。取消要求の受領だけで解放しない。
- operation gate 保持中に UI thread、dialog、event subscriber、別 owner の同期完了を待ってはならない。論理受付を持ってasyncに待つ場合も、相手が同じ受付を再取得する循環を作らない。既存leaseの引渡し境界を使い、ambientな再入回避を追加しない。

### 5.3 Input and snapshot

- stale な UI object は操作受付を取得した authoritative owner の入口で stable identity から現在の対象へ一度だけ再解決し、その後の phase で利用者の意図や対象 identity を再解釈しない。確認ダイアログの前後で受付を解放する既存契約では、再取得後に確認済み範囲を照合する。
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

- 明示要件のない競合する新規 mutation は、非待機の Busy 拒否を既定とする。後続操作の自動実行を約束しない。
- 「UIを止めたくない」だけを mutation concurrency の根拠にせず、「単純化したい」だけを承認済み機能の縮退の根拠にしない。
- section 6 の例外を維持したうえで、触る入口の許可／拒否、受理済み仕事の所有、commit 順序、failure contract を先に定める。全機能の組合せ表や汎用schedulerを新設しない。
- 新しい同時実行による利益が状態数・token・retry・test matrixの増加を上回ることを説明できなければ導入しない。一件のbatch内の既存解析並列は、利用者操作の並行性とは別に扱う。

## 6. 操作種別ごとの共通既定と維持する例外

以下を採用済みの製品上の境界とする。通常のBusy既定より具体的な行を優先する。これは新しい互換性管理エンジンや全アプリ共通の排他gateを要求するものではない。

| 実行中／対象 | 受付・維持する挙動 | 境界・参照 |
|---|---|---|
| 通常の変更中に、別の競合変更 | 新規要求はBusyで未実行終了。導入中の所持譜面削除・登録ルート適用などを同時実行する要件はない | UIの閲覧は継続。停止・取消・終了要求は現在の操作の終端制御へ接続する |
| 起動中、一覧が先に表示された | 必要なlocal初期化と、有効なLR2連携で必要な同期が終端するまで変更受付を待たせてよい。未受理操作を後で自動実行する予約は作らない | optional online同期・全cache warmupの完了を一律に待たせない。設定画面の利用は下記の例外。適用は [既存startup計画](../plan/lr2-startup-procedural-orchestration-plan.md) |
| 導入中の追加ZIPドロップ | 既存の導入queueへ予約できることを維持する。先行導入と追加分の実変更を同時実行する要件ではない | [drop-install-ingress.md](drop-install-ingress.md) の入力確保・順序・cancel/drain・所有を維持。起動前やURL取得中等の既存拒否を解除する要件ではない |
| 保留へ追加・復元された譜面 | 既存の対象条件に従って自動的に導入先推定を開始・終端する。推定中の保留一覧閲覧・選択は可能、追加の手動推定・削除等はBusyで断る | 受理した自動推定を競合だけで失い、手動再推定を必須にしない。[推定仕様](install-estimation-current-logic.md) の候補評価・batch内並列を維持。一般的な失敗後の自動retryは追加しない |
| 導入中の新規一時試聴・録音／録音中の別変更 | Busyで断り、後で自動再生・自動変更しない | 試聴中に競合する変更へ進む場合は、既存Stop→実際の再生終了・設定復元・一時資源cleanup→変更の順序を優先。停止不能なら変更を始めない。通常再生との新しい並行性は追加しない |
| 手動の外部表同期・URL取得の通信待ち | プレイリスト編集はBusyで断ってよい。一方、ライブラリ操作は現在の実入口が許可し安全に成立する範囲を維持し、通信中という理由だけで一括禁止しない | プレイリスト内容の編集と、行を入口にした所持譜面の操作を区別。[プレイリスト仕様](playlist-data-and-export-flow.md)・[URL取得仕様](playlist-url-download-resolution.md)。URL取得と追加drop等の既存拒否、適用段階のfile/DB lease、LR2実DB同期の排他は維持 |
| 変更操作中の設定画面 | 開く・編集する・既存のCancel／close／UI previewを現状どおり利用できる。全体Busyを理由に入口を閉じたり、全面read-onlyにしたりしない | 開くこととSave／適用／schema操作は別。これらの既存のavailability・拒否・失敗後retryを維持する。[設定仕様](settings-change-impact-and-startup-operations.md) |
| authoritative stateを変えない閲覧・計算 | 確定済み入力で継続してよい。必要なら既存のlatest-winsを使う | 一覧の鮮度を正本変更の認可に使わない。workerが変更途中のmodelを無保護で読まない |

### 6.1 例外を拡大せずに適用する

通信待ち中に維持するライブラリ操作は、UI／ownerの現行入口、既存受付、実際の適用段階までを対象unitで記録する。「同じDBではないから安全」やprivate APIの並行呼出しだけを根拠にしない。逆に、この確認を理由に未調査の全ライブラリ操作を先に無効化しない。具体的な安全性違反が見つかった組合せだけ、保全を満たす局所修正または再計画とする。新しい並行性や一般的なfetch/apply分割基盤は要求しない。

設定画面のmodal表示はbackground workerの停止を意味しない。既存の`Settings.Default`兼用編集バッファをread-only snapshotと仮定せず、触る操作が必要とする実行時設定は既存のprofile／operation入力から取得する。設定画面の利用を禁止したり全設定draft基盤を新設したりして回避せず、途中で未保存値を読み直す問題が実入口で成立する範囲だけを修正する。保存拒否時のsnapshot復元等は設定仕様に従う。

自動推定の完了とは、対象条件に沿った推定結果・推定不能・取消・失敗のいずれかへ終端することであり、導入先が必ず見つかることや自動インストールを意味しない。追加・復元から受理済みbatchへのhandoffと、後から来る手動操作を分ける。後者はBusyで断り、前者は既存ownerで所有したまま開始順を整える。既存queue以外の汎用retry／resumeを追加しない。

この表にない並行性を新設する場合は、対象feature specとdecisionに理由・代替案を記録する。既存キューの削除や設定画面の利用制限も、単なる実装整理ではなく製品挙動の変更として扱う。

## 7. version token 使用ポリシー

### 7.1 推奨される用途

version／generation token は、結果を安全に捨てられる read-only／presentation 処理で有効である。

- keyword search。
- sort order／virtual view。
- 副作用のないthumbnail、補助表示。試聴・録音の資源所有は含めない。
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
command受付判定
  -> 非待機のlogical admission取得（競合はBusy、受理済みqueueは別契約）
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

## 9. 論理受付と既存queue

logical admissionはUI threadを止めるlockでも、後続要求を必ず保存するqueueでもない。既存の受付を共有してBusy拒否できる範囲を先に検討する。section 6の例外があるため、全操作を長時間保持する単一global gateへ入れる案も既定にしない。

ownerの責務分割、低層のDB/file lease、表示用activityを保ちながら、入口ごとに何を受理するかを明確にする。新しいlane／scheduler／compatibility managerを、機能名の違いだけを理由に作らない。

### 9.1 対象範囲の操作互換性

cross-workflow変更では、触る実入口についてsection 6の既定と例外、競合時の結果、受理済み仕事の終端を計画へ記録する。拒否だけでなく、維持する追加drop・自動推定・設定画面・既存の通信との並行操作も確認する。全アプリの組合せ表を機械的に生成する必要はない。

## 10. 新規変更・機能追加の指針

### 10.1 変更開始時

次を確認する。

1. durable／authoritative surface は何か。
2. writer owner は誰か。
3. UI responsiveness と mutation concurrency のどちらが本当に必要か。
4. 新規要求はBusyで拒否できるか。section 6の例外、受理済みqueue、既存の並行操作を維持する範囲は何か。
5. command開始時に一度再解決すれば足りないか。
6. logical ownershipをworkflow全体で保持できない理由があるか。
7. version tokenなしで明示的順序にできないか。
8. callbackをowner内の直列処理へ戻せないか。
9. success／failure／partial success のterminal stateは何か。
10. user-owned dataは何か。

### 10.2 既定の判断

要件が曖昧な場合は次を既定とする。

- UI thread外で実行する。
- 未承認の競合する新規mutationはBusyで未実行終了し、暗黙queueを作らない。
- 確定済み表示の閲覧とsection 6の例外を維持する。既存機能をglobal Busy化で縮退させない。
- stale UI inputは開始時に再解決する。
- tokenを追加しない。
- retry／resumeを追加しない。
- best-effort処理をsuccess条件に混ぜない。

### 10.3 version token追加の代替案

新しいtokenを追加する前に、順に検討する。

1. 承認済みの例外を維持しつつ、既存受付で未受理の競合をBusy拒否できないか。
2. 必要な処理順を既存のworkflow owner内に閉じられないか。
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
- 排他を単純化するため、必要な追加ZIP queue、保留の自動推定、設定画面、現在許可する通信中のライブラリ操作を一括停止する。
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
- admissionの取得／Busy拒否、または既存queueの受理／待機。
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

ordering／token の test evidence は `test-authoring-contract.md` と、必要性判断で適用される承認済み Test Contract Packet に従う。恒久テストを必要とした production ingress から再現可能な bugfix では red が原則有用だが、実施要否は必要性と test の識別力に対する具体的なリスクで決める。base で構造上実行できないことだけでは targeted negative control を要求しない。bugfix の red の代替、または識別力に具体的なリスクがあり計画で必要と判断した場合だけ targeted negative control を用いる。非 bugfix に red / mutant を一律要求せず、未実施だけを finding の根拠にしない。通常の不正入力／failure test と test 識別力のための mutant 実行は区別する。

## 14. 複雑性予算

変更ごとに、少なくとも次の増減を確認する。

| 項目 | 追加時の扱い |
|---|---|
| durable state | 互換性・migration・削除条件を要求 |
| version／generation token | 依存表とownerを要求 |
| mutation lane | 既存受付では足りない理由と、対象範囲の既定・例外を要求 |
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
- [ ] [性能要件](performance-and-scale.md)に沿い、対象規模・差分・全件コピー/再構築の回数を示した。低CPU使用率だけを理由に受理済み処理を減速していない。
- [ ] authoritative surfaceとwriter ownerを特定した。
- [ ] 新規要求のBusy拒否と受理済み仕事を区別し、section 6の必要なqueue・並行操作・設定画面を維持した。
- [ ] logical ownershipの範囲を定めた。
- [ ] stale UI input の再解決と、lease／commit 境界の invariant 再検証を区別した。
- [ ] version tokenなしで順序を表現できないか検討した。
- [ ] tokenを追加する場合、kind／owner／source／increment／consumerを記録した。
- [ ] required publicationとbest-effort notificationを分けた。
- [ ] `Completed` invariantを明示した。
- [ ] 必要性判断で追加・更新が必要とした production ingress test について、適用される Test Contract Packet の要求を満たした。
- [ ] 旧test契約が現在も正しいか再評価した。
- [ ] 増えたstate／token／callbackと、退役したものを記録した。

### Reviewer checklist

- [ ] 「UIを止めない」がmutation並行化の根拠になっていない。
- [ ] broad versionがnarrow factの正当性判定に使われていない。
- [ ] token発行後に、必要なconsumer前で同じtokenが進む経路がない。
- [ ] required callbackの実行順が明示されている。
- [ ] stale resultのdiscardがdurable dataを失わない。
- [ ] Busy拒否する操作に暗黙queueがなく、拒否側の副作用がない。受理済みの自動推定・導入queueを失わず、維持すべき入口を一括禁止していない。
- [ ] command開始後に対象の意味を各phaseで再解釈していない。
- [ ] 同じsurfaceのwriterが増えていない。
- [ ] successが例外なしだけで決まっていない。
- [ ] 適用される Test Contract Packet が要求する production ingress の時系列 test がある。
- [ ] 適用される場合、base-fail／head-pass、または必要性判断で選んだ targeted negative control が plausible wrong implementation を区別する。red / negative-control の未実施だけを finding の根拠にしない。

## 16. AI／Codexを利用する場合の境界

この指針は、モデルによるレビューだけでsystem-wide correctnessを保証できるという前提を置かない。

- モデルは「UIを非同期にする」という要件から、mutation concurrencyを推測してはならない。
- concurrency、ownership、transaction、completion semanticsは、承認済みspec／decisionから一意に決まる必要がある。
- 仕様、実装、test、reviewを同じモデル群が作った場合、それらを独立した証拠とみなさない。
- cross-owner、DB ownership、全表置換、migration、retry／resume変更は、人間または明示的authorityがdecision listを閉じる。
- 要件が不明な場合、モデルはtokenやfallbackを追加せず、承認済み例外を維持したBusy拒否案またはreplanを返す。設定画面の閉鎖や既存の並行性の縮退を無断で代替案にしない。
- static reviewはdiffの局所説明だけでなく、production ingressのtimelineと操作互換性を確認する。

## 17. 保守方法

- この文書のownerは、architecture／workflow boundaryを変更する作業のroot ownerとする。
- version token、mutation lane、deferred callbackの新設時に、この文書の更新要否を確認する。
- feature固有の例を本文へ増やしすぎない。共通原則へ一般化できない内容はfeature specへ置く。
- 同じ内容を`AGENTS.md`、`architecture.md`、`workflows.md`へ重複管理しない。
- 実装と乖離した理想論にしない。例外が恒久化した場合はdecisionへ記録し、正本へ反映する。
