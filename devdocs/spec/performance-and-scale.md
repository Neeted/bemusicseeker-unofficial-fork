# データ規模と処理速度の性能要件

- **文書種別:** アプリ全体の性能要件・設計レビュー・性能受入の正本
- **状態:** Active（2026-09-10 採用。新規実装と改修対象へ適用）
- **対象:** catalog / DB、resource index、cache / snapshot / receipt、導入・削除・再スキャン、一覧・プレイリスト、startup / background work

この文書は「現行実装が既に満たしている性能」の宣言ではない。以下の規模を通常の設計・検証条件として扱い、変更時に守る要件を定める。未達の既存経路は測定結果・制約として明示し、今回触らない経路の全面改修まで一律に要求しない。

データの意味・所有権は [data-and-indexes.md](data-and-indexes.md)、操作受付と並行性は [workflow-concurrency-and-complexity.md](workflow-concurrency-and-complexity.md)、FS / DB の成功・部分失敗は [file-db-consistency.md](file-db-consistency.md) を正本とする。本書はそれらを緩めず、性能の優先順位、規模、評価方法を補う。

## 1. 最優先は処理速度

**性能上の最優先は、同じ仕事を正しく完了するまでの wall-clock time の短縮と throughput の向上である。省メモリ性や、UI 応答のために CPU 使用量を抑えることを優先しない。**

- CPU / I/O を積極的に利用し、索引の保持、計算結果の再利用、一括処理、操作内の独立計算の並列化で完了を早めてよい。高い CPU 使用率や大きい常駐 cache 自体を性能不合格としない。
- メモリ削減のために同じ DB query、file read、digest、parse、index build を繰り返したり、UI に CPU を譲ることだけを理由に worker 数を減らす、sleep / yield / debounce を追加する方針を既定にしない。
- 逆に、並列度の最大化自体も目標ではない。lock 競合、I/O 競合、GC、paging、過剰な一時コピー等が実測の完了時間を悪化させる場合は、その原因を削減する。queue / batch の上限は安全性と実測 throughput に基づいて決める。
- OOM、deadlock、データ破損、無期限の世代保持、取消・shutdown の契約破壊は許容しない。正しさ・既存の安全境界は性能と交換しない前提条件であり、省メモリ目標とは別である。
- WPF thread affinity、UI 上の確認・取消、確定済み表示、非同期実行の契約は維持する。worker で十分な CPU を使うことと、UI thread で DB / filesystem / 解析を長時間同期実行することは別である。
- first-useful-visible の短縮だけで処理全体の高速化としない。必要な仕事を background、lazy getter、次の操作へ移しただけなら、その完了時間と次操作への影響も示す。先読みの追加・延期・中断は、CPU 使用率ではなく操作時間・総完了時間への効果で判断する。

固定の RAM 上限、CPU 使用率上限、常駐 cache 最小化目標は本書では設定しない。特定の機能に必要な資源上限を設ける場合は、理由、対象、速度への影響を個別仕様に記録する。

## 2. 扱うデータ規模

### 2.1 代表的大規模プロファイル `library-reference-20260909`

[2026-09-09 の参照記録](../acceptance/performance-reference-2026-09-09.md) の件数を、設計時に少なくとも検討・検証する代表規模とする。「一部の特殊ユーザーだけの限界値」や、API の入力上限として扱わない。より大きな入力を暗黙に切り捨てたり、この件数で上限を固定しない。

| 対象 / 記号 | 設計・検証で扱う規模 | 数え方と注意 |
| --- | --- | --- |
| owned catalog `C` | 約21万譜面、BMS と BMSON の混在 | storage path / owner の数。同じ MD5 の複数配置を含み、distinct hash 数とは違う。 |
| 譜面ディレクトリ `D` | 約3万フォルダ | 譜面直下、祖先込み、filesystem 列挙の各 directory 数を分ける。 |
| resource reverse lookup `K` | 約800万台のキー | Audio / Image / Movie のカテゴリ別逆引きキー数の合計。実ファイル数でも DB row 数でもない。 |
| directory-relative resource entries `E` | 約1,200万台のエントリ | chart-directory ごとのカテゴリ別 resource-key 登録の合計。複数 directory からの所有関係を含む。 |
| resource enumeration hits `F` | 約1,200万台の hit | 列挙の仕事量。逆引きの distinct key と区別する。 |
| `song` / `bmson_song` | BMS 約21万行、BMSON 約1千行 | 同一 hash が複数 path に存在し得る。BMSON を LR2 `song` へ混入させない。 |
| `folder` 系の読込 | 約3万台の行 | LR2 normal folder、ancestor、custom output の対象差を記録する。 |
| `chart_digest_map` / `chart_info` / `maintenance` | 各約21万件級の読込・索引 | partial cache、ownerless metadata、parse failure を区別。表ごとの全件数が常に `C` と一致するとは仮定しない。 |
| playlist header / entry `L` | 約500表 / 約58万 entry | entry 数を owned 譜面数で上限化しない。複数表の同一譜面や未所持 entry がある。 |
| custom-folder output | 約31万 physical entry | DB row、表数、実出力 entry 数を区別する。 |
| score / IR | 約2万行級の読込 | この参照記録の部分的な観測であり、全履歴数や容量上限ではない。 |

exact 件数、ログ名、marker、checksum は参照記録に集約する。「約800万リソース」とだけ記載して `K`、`E`、`F` を混同しない。重い処理を説明するときは、その collection の単位と実件数を示す。

DB ファイルの bytes、index の bytes、平均 row / string / blob 長、逆引き bucket の候補 directory 数分布は添付ログだけでは確定できない。譜面数から DB 容量や RAM 必要量を作らない。DB / cache 性能の検証時には、実際の file size、table / projection row 数、関連 index、key 分布を別途記録する。

### 2.2 操作の差分はライブラリ全体より小さい

| 対象 / 記号 | 基準となる操作・入力 |
| --- | --- |
| 1操作のパッケージ数 `P` | 20 ZIP を一括投入。単一パッケージも別に確認する。20 は上限ではない。 |
| 1パッケージの譜面数 `c_p` | 複数譜面を含み得る。参照ログは1〜3譜面、20パッケージで24譜面。1 ZIP = 1譜面と仮定しない。 |
| 1パッケージのリソース数 `r_p` | 数百の音声・画像・動画等を同梱し得る。千件級も検証ケースに含めるが、実分布・最大値は未測定であり、上限を置く根拠にはしない。 |
| 操作差分 `ΔC / ΔD / ΔK` | 参照操作は追加・削除24譜面 / 削除20フォルダ。逆引き変更キー数は同梱ファイル数と一致せず、0の場合もある。 |

複数譜面が同じ resource を参照し得るため、`c_p * r_p` は package 内の distinct resource 数ではない。`File.Exists` fallback 回数も検証呼出回数であり、同梱された distinct 実ファイル数の証明にしない。

既存の機能固有の探索上限は別契約である。[導入先推定の source package surface](install-estimation-current-logic.md#source-package-surface) は通常10,000 file未満を想定し、directory package rootごとに50,000 entryで探索を打ち切り、部分結果を推定に使わず警告する。これは1回の推定用探索の安全境界であり、ライブラリ全体の容量や本書の代表規模の上限ではない。本書を根拠に上限・警告を削除せず、変更時は当該機能契約として判断する。

同じ content hash を別の場所へインストールした場合、内容解析の再利用と保存先 resource の存在検証は別である。既所持というだけで file mutation、保存先検証、必要な projection / warning 更新まで省略しない。

## 3. 規模を踏まえた設計要件

以下は新規経路と変更する経路の受入条件である。既存の未達は明示し、規模を隠した少量 test の成功だけで解消済みとしない。

### 3.1 全体処理と差分処理を分ける

局所操作の仕事量は、変更対象、関連 bucket、必要な祖先といった影響範囲に主として依存させる。`C`、`D`、`K`、`E`、`L` の全件処理がある場合は、その必要性と呼出回数を説明する。

| 経路 | 要件 |
| --- | --- |
| 起動・明示 full scan / full rebuild | 必要な全件列挙・構築は許容する。同じ世代の同じ情報を phase ごとに再読込・再生成しない。既存の freshness / fallback 契約は維持する。 |
| cold lookup の初回構築 | 初回コストを明示し、同一世代で再利用・single-flight する。各パッケージで初回構築を繰り返さない。 |
| 少数譜面の install / move / delete | パッケージ・譜面・フォルダの内側ループで全 catalog、全 reverse root、全 playlist entry を毎回列挙・コピー・sort する経路を既定にしない。 |
| 候補フォルダの確認・LR2 同期 | 対象 path、関連 folder、必要な ancestor の情報を既存索引から得る。少数の有無判定のためだけに全BMS一覧と全 ancestor lookup を毎回作らない。 |
| 多パッケージ・大量削除 | 同じ前処理・索引更新・通知を安全な操作境界で集約する。1操作全体を単一巨大 DB transaction にすることは要求しない。 |
| 全一覧の新規 filter / sort | 全件処理が必要な query と、可視行の表示・更新を分ける。無関係な変更で全 order / projection を捨て、直後に再構築しない。 |

特に `O(P * C)`、`O(P * K)`、`O(ΔD * K)` のように、小さな入力の反復回数と巨大な collection サイズが掛かる追加処理は退行リスクとしてレビューする。処理を別 owner、callback、constructor、property、`ToArray` / `ToDictionary` へ移しても仕事量は消えない。

全件処理が真に必要な例外では、対象規模、実行頻度、既存索引で代替できない理由、測定区間、比較結果を変更説明へ残す。「immutableだから」「安全側だから」だけを根拠に全件複製を選ばない。計算量だけで採否を決めず、定数項・bucket fan-out・実測速度も評価する。

### 3.2 Cache / snapshot / receipt

- immutable であることは「request ごとに全データを複製すること」を意味しない。既存世代の再利用、構造共有、差分、影響範囲だけの不変データを検討する。公開済みの可変配列を無保護で共有する近道は取らない。
- no-op 判定のために巨大 dictionary root を先にコピーして捨てない。影響 key / bucket を調べ、変更のないカテゴリでは root detach / 全件 materialize を避ける。必要な入力検証まで省くという意味ではない。
- 実変更でも、同じ操作のフォルダごとに全 root をコピーするのではなく、既存の安全な公開・失敗境界内で変更を集約する。中間 reader が必要な世代、部分成功、例外時に保持する旧 snapshot を壊さない。
- snapshot / receipt には consumer が必要な facts を持たせる。全 catalog パスの二重 sort や、既存索引と同じ lookup の作り直しを constructor の固定費にしない。
- cache の dependency / generation と invalidation を局所化し、無関係な変更で全体を invalidate しない。同じ失効へ複数の full rebuild / warmup を開始する代わりに、既存の single-flight / coalescing 境界を使う。
- 実際に再利用する索引をメモリに残すことは許容する。小さい常駐量のために再構築を増やさない。一方、不要な旧世代の保持や巨大な一時コピーによる GC / paging の遅延は、処理速度の問題として測る。

逆引きの更新が0件でも directory mapping は変わり得る。no-op の単位を index 全体、カテゴリ root、個別 bucket で区別し、既存の snapshot / generation 契約に従う。

### 3.3 DB / filesystem / package

- query の回数だけでなく、実際に読み取る・materialize する row 数と対象 index を確認する。少数 path / hash の処理を全 table 読込 + LINQ filter へ戻さない。parameterized query、一括 lookup、必要な列への projection、集合更新を既存 gateway 内で検討する。
- path identity、同一 hash の複数 owner、LR2 / standalone、partial cache を保持する。索引を速くするために key の意味や照合規則を変えない。
- 大量 delete / upsert では既存の集合 SQL、bulk write、chunk commit を再利用する。per-row の全体 orphan check や不要な transaction 開始を増やさない。ただし部分成功・durable receipt の境界は [file-db-consistency.md](file-db-consistency.md) に従う。
- 同じ package / directory の内容列挙、譜面 bytes、digest、解析済み情報は、有効な入力寿命と currentness の範囲で再利用する。数百 resource を含む package を、同梱譜面ごとに全列挙し直すことを既定にしない。
- 新しい保存先、外部変更、削除結果、parser / schema version 等の再検証が必要な場合は再利用条件を明示する。検証を省いて出力内容や警告が減った結果を高速化としない。
- ZIP 展開、物理ファイル移動・削除は、対象ファイル数だけでなく bytes、同一 / 別 volume、展開済みか、ごみ箱・削除方式、失敗・retry 数にも左右されるため、件数と条件を併記する。

### 3.4 並列度と逐次性

競合する利用者操作の同時受付、受理済み1操作内の計算並列度、Codex worker 数は別物である。既存の mutation admission、writer owner、DB / FS commit と回復境界は維持し、その内側の独立した読込・hash・parse・評価は速度向上のために並列化してよい。

新しい並列化では依存・共有先・停止・結果順序を示し、逐次のままにする場合は必要な整合性境界または実測の競合コストを示す。feature spec にある現在の worker / lane 数は現行設定であり、CPU を低使用率に保つための恒久的な性能上限ではない。変更時には当該仕様と安全性検証を揃える。

## 4. 性能として何を計測するか

### 4.1 主指標と完了条件

主指標は操作の経過時間。throughput は同じ仕事量に対する補助指標とし、譜面 / 秒、resource / 秒、bytes / 秒など単位を明示する。CPU time、allocation、GC、working set は原因調査に使い、低い数値そのものを成功条件にしない。

| 操作 | 主に比較する区間 / 結果 |
| --- | --- |
| ZIP 投入・保留準備 | accepted input から展開・発見・分類・保留登録、および必要な推定結果が利用可能になるまで。登録と推定完了は別 checkpoint としても記録。 |
| install | 確認後の操作受付から、必要な FS / DB 確定、canonical state、inline 情報、required publication / cleanup、terminal result まで。 |
| delete / move / merge | 確認後の操作受付から、対象処理、必要な catalog / resource index / LR2 同期、cleanup、terminal result まで。失敗件数と結果状態も比較。 |
| startup | install readiness、operable、required initialization、post work 完了を別々に記録。scheduler 外の ranking / XML / presentation の完了も分離して追跡。 |
| reload / maintenance / backfill | 対象発見から必要な read / compute / commit / apply 完了まで。changed / unchanged / skipped と総対象数を記録。 |
| 一覧・playlist | 要求受付から正しい query / sort 結果の適用と first useful render まで。後続集計・先読みがある場合はそのコストも別記。 |

ユーザーの確認回答待ちは主指標から分けるが、確認候補を計算する preflight は計算時間として記録する。queue / lock 待ち、operation 後の必要な表示反映、任意の先読みを区別し、待ち時間の外出しで改善を装わない。optional cache を全て更新しないと operation を完了できない、という新しい barrier は追加しない。

### 4.2 Marker の意味と処理量

計測の出力方式は [logging-policy.md](logging-policy.md) に従う。新設・変更する診断では次を満たす。

- operation / batch と package / phase を対応付け、開始・終了、入力・成功・失敗・skip 件数を追跡する。既存の operation identity を優先し、計測だけのために domain generation を増やさない。
- 全体 wall time、内包する phase time、並列 worker の累積処理時間、queue / lock 待ちを区別する。入れ子や並列 stage の時間を独立区間として合算しない。
- `moveMs` 等の名前と実際の計測範囲を揃える。範囲を変える場合は marker / 計測仕様の版を識別し、旧値と直接比較できないことを記録する。未計測値を0msの仕事として解釈させない。
- 局所操作で重い可能性のある箇所は、total / affected row・key・directory 数、全件走査 / root clone / rebuild の回数・対象件数・時間、DB query / materialized row 数、物理 I/O 件数・bytes を必要な範囲で集約する。
- 診断を per-resource の同期ログや全 path の文字列化にしない。既存の bounded logging を使用し、失われた marker がある比較は欠測として扱う。

## 5. 検証シナリオと既存 coverage

### 5.1 代表操作

必須の基本シナリオは、参照規模の同じ filesystem / DB / 設定断面へ次を行うことである。

```text
20 ZIP を保留画面に投入
  -> 既所持警告を無視してインストール
  -> 新規画面でその操作が追加した譜面群を削除
```

参照ログの結果は20パッケージ / 24譜面 / 20削除フォルダ。異なる合成 fixture では24件を固定せず、fixture が意図した複数譜面・共有 resource の件数を期待値にする。各測定前に断面を復元し、「削除したから元に戻った」と DB / 派生状態の一致を仮定しない。実ユーザーのライブラリではなく検証用の複製を用いる。

### 5.2 変更対象に応じて選ぶ追加ケース

| ケース | 検出したい問題 |
| --- | --- |
| `Δ` 固定で背景の `C / D / K / E / L` を小・中・参照規模へ変える | 1件・20件の局所操作に潜む全件走査・コピー。各 collection の件数を本当に増やす。 |
| 背景規模固定で1パッケージと20パッケージを比較 | パッケージごとの固定費、全体 index の反復構築。 |
| 1 / 3譜面と100 / 500 / 1,000 resource の合成 package | package 内共有 resource の重複列挙・重複評価。数値は検証用の選択であり、実ログの分布や入力上限ではない。 |
| 逆引きno-op、空カテゴリ、少数key変更、候補directoryが多いbucket | no-opでも全rootをコピーする処理、fan-out依存の見落とし。 |
| 同一MD5の複数配置、BMS / BMSON混在、入れ子folder、部分的な削除失敗 | 速度改善でidentity、残存owner、部分成功、旧snapshotの不変性を壊さないこと。 |
| cold / warm、初回構築 / 再利用、startup直後 / post work完了後 | cache条件や先読み競合を実装改善と混同しないこと。 |
| DB / 全体scan / playlist / sortを変更する場合の対応操作 | 20 ZIPシナリオだけでは通らない本来のhot pathの退行。 |

全組合せを毎回実行する要求ではない。変更が触る仕事量と failure / ownership に対応するケースを選ぶ。巨大 fixture は明示的な性能 lane に置き、通常 Functional を巨大化させない。小さい入力で検証できる結果の正しさ・旧世代不変・重複仕事の有無は、必要性判断の上で既存の近傍 test を使う。テスト名、private method、特定 collection 型を固定する source-text assertion は追加しない。

### 5.3 現在の performance corpus の範囲

既存入口は `scripts/benchmark-net10-performance.ps1`、category は `Net10Performance` である。

- `Net10PerformanceCorpus` の行数は small=1,000 / medium=25,000 / large=200,000。これは synthetic row corpus であり、large 指定で実 catalog / DB と800万キーの resource index が自動で構築されるわけではない。
- `BmsLibraryInstallEstimationServiceTests.EstimateInstallationDirectory_BuildsEachCandidateResourceViewOnce` は候補8 / 64 / 512件の resource view 構築回数を検証する。
- `Net10ScanParserPerformanceTests` は1,000 / 4,000 / 16,000 note の continuation lookup の仕事量を検証する。

これらの成功だけで、参照規模の startup / install / delete、DB / resource index 更新の速度を保証しない。本書の追加シナリオ、集約counter、代表操作の自動化が全て実装済みという意味ではない。未対応部分は実アプリの比較ログまたは対象ownerへ接続した明示benchmarkで補い、実行していなければ未検証と記録する。実行例と lane は [testing-strategy.md](testing-strategy.md) を参照する。

## 6. 退行の判定・変更時の提出内容

### 6.1 比較条件

baseline と候補は、変更前に決めた同じ条件で比較する。基準は通常、変更直前のcommit。同一機能の既知の旧版退行を調べる場合は、その旧版も別のbaselineとして併記する。遅い版だけを選び直して退行を消したことにしない。

記録する条件は、commit / build / publish設定、実行exe、CPU・論理processor数・RAM・storage、OS / runtime、DB / fixture identity、設定とUI列、Everythingの有無、入力ZIP・展開bytes、操作前cache / warmup状態、並行background work、ログ設定である。ネットワーク依存を含む場合はその結果・待ちを分ける。

同条件で反復し、baseline / candidate の順序を交互にする。通常は各5回以上を目安とし、中央値・範囲と全測定値を残す。cold / warm、startup直後 / idle は別の群にし、単発の値や1回の操作内の20個の異なるpackageを20回の反復測定とみなさない。少ない標本から安定したp95や統計的有意差を主張しない。実行回数・条件不足は結論の制約として明示する。

### 6.2 受入条件

- **同じ結果であること:** 対象・成功・失敗・skip件数、必要なDB / FS / warning / projectionと安全性が同等。必要処理や警告を落とした速度向上は不合格。
- **操作別に退行させないこと:** 再現する完了時間の増加を他操作の短縮や合計値で相殺しない。反復測定のばらつきで説明できない悪化は退行として報告し、未承認のまま「性能問題なし」としない。
- **局所変更で全体仕事を増やさないこと:** section 3 の全件反復、no-opコピー、不要な再構築が増えていないことを仕事量と代表規模で確認する。小規模で速いことだけを受入根拠にしない。
- **遅延の付替えでないこと:** deferred / lazy / UI非同期化では、操作terminal、必要な後処理と後続操作の完了時間も比較する。
- **未測定を明示すること:** 誤差・環境差で判定できない場合は「未判定」。機能testのみの成功は性能passではない。正しさ修正等のために遅延を受け入れる場合は、差・理由・代替案を明示して判断する。

普遍的な秒数SLOや固定の退行許容率は、基準hardware・データ・反復測定がない段階では設定しない。参照ログの実測秒数を上限予算や合格値に流用しない。将来固定budgetを定める場合は、上記条件・完了marker・根拠となる反復データと一緒に明記する。test runnerの300秒等の時間予算や、配布形式選定時の「5秒かつ15%」は、アプリ全体の退行許容値ではない。

### 6.3 開発・レビューの最小記録

cache / snapshot / receipt、DB query、package loop、publication、scheduler / 並列度を変更する場合、計画と完了報告に次を短く含める。

```text
対象操作 / production ingress / owner:
baseline / candidate / fixture / C,D,K,E,L / P,Δ:
全件処理と差分処理、呼出回数（変更前 -> 変更後）:
再利用するcache、失効条件、snapshot/receiptの範囲:
逐次性が必要な境界 / 並列計算の独立性:
操作terminal / 後処理 / first-visibleの計測範囲:
関連の機能検証 / 性能比較 / 結果同等性:
実測差とばらつき / 未検証 / 残る制約:
```

新しい常設台帳や全変更への巨大packetは要求しない。小さな文書・翻訳・非hot-path変更まで大規模benchmarkを機械的に実施しない。恒久testの追加・変更は [test-authoring-contract.md](test-authoring-contract.md)、開発時の計画・handoffは [codex-agent-workflow.md](codex-agent-workflow.md) に従う。
