# ファイル操作と DB 操作の整合性・補償契約

最終更新: 2026-09-14

## 1. 目的と適用範囲

本資料は、アプリ内で filesystem（FS）と DB を変更する操作について、不整合を減らす設計、補償の範囲、利用者へ伝える結果、受け入れる制限の正本とする。ライブラリ削除だけでなく、導入、移動、rename、merge、生成ファイルと関連 DB の更新にも適用する。`FileDbMutationBoundary.cs` を使うこと自体を全操作の要件にはしない。

これは共通の設計・レビュー契約であり、全既存経路が対応済みであるという宣言ではない。現行の限定的な補償契約は section 4、operation-scoped batching への移行は [mutation session 実装計画](../plan/library-mutation-session-batching-plan.md)、その他の整合性未達は [整合性契約の適用計画](../plan/file-db-consistency-follow-up.md) に分ける。`FSDB-SESSION` は採用済みの正常系設計契約であり、移行単位ごとに production behavior とテストを更新する。

並行性と ownership は [workflow-concurrency-and-complexity.md](workflow-concurrency-and-complexity.md)、ライブラリ操作の lease、finalization、notification の詳細は [library-mutation-boundary.md](library-mutation-boundary.md) に従う。DB 内だけの transaction、updater の backup／journal／rollback、取り込み前の一時ファイルの cleanup は、それぞれの既存契約を維持する。本資料を、それらの保証を取り除く根拠にはしない。

## 2. 保証することと受け入れる制限

| ID | 契約 |
| --- | --- |
| `FSDB-ORDER` | 確認済みの対象と計画を一つの command owner が扱い、既存の競合操作の排他境界内で FS、DB、canonical state、通知の順序を明示する。事前に判定できる不正入力や未承認の対象は、破壊的な I/O より前に拒否する。 |
| `FSDB-SESSION` | 複数対象を含む一回の利用者ライブラリ変更は、一つの logical mutation session に複数 change を蓄積する。item ごとの FS I/O や後続判断が逐次でも、例外・補償の存在だけを理由に canonical DB apply、index/cache反映、required publication を item ごとに完結しない。複数 durable surface は別 transaction のままでよい。 |
| `FSDB-FACTS` | 成功、失敗、部分完了の判断を、対象操作で確認できた FS の結果、DB commit、必要な内部反映の事実に基づける。未確認の結果を成功または変更なしと推測しない。 |
| `FSDB-FORWARD` | 既に確認できた FS 操作や session 内の成功 change を、操作全体を原子的に見せる目的で一律に巻き戻す必要はない。保持されたデータと現在の状態から安全に整合を取り直すことを既定とする。移行中の限定補償は section 4 に従う。 |
| `FSDB-REPORT` | 捕捉した不整合・部分失敗を通常の完了として黙って扱わない。対象、完了が確認できた段階、未完了または未確認の範囲、次に取れる対応を、利用可能な terminal result／UI と診断へ伝える。 |
| `FSDB-LIMITS` | FS と DB を跨ぐ原子性、全失敗地点からの自動復旧、復旧完了までの時間、再起動を跨ぐ自動収束は保証しない。走査結果が 0 件の場合の保護により DB が収束しないことも許容する。 |

FS と DB は同じ transaction に参加しない。DB の commit 成功は FS 全体の成功を意味せず、FS API の成功も DB の更新を保証しない。実行順序を入れ替えるだけでは、この制限は解消しない。source retention や staging は失敗時の損失を減らす手段であり、原子性の根拠ではない。

契約が対象とするのは、サポートする production ingress から到達し、稼働中のプロセスで観測・捕捉できる結果である。プロセス強制終了、電源断、媒体故障、外部変更との TOCTOU、通知先やログ出力先も同時に利用不能となる場合まで、結果の永続記録・必達通知を保証しない。既存の process-exclusive DB／single writer 等の入口 assumption も維持する。

これらの制限は、通常経路の順序誤り、既知の failure の握りつぶし、承認範囲外への削除、保持できる唯一のデータの不用意な破棄を許可するものではない。

## 3. 不整合を減らす設計

1. **破壊的処理前に対象と意味を決める。** stable identity、実際に使う path、上書き・削除の承認範囲を既存 owner の入口で確定する。既に検証された invariant の下流で、同じ確認や version token を増やさない。DB の試し書き等で「後の commit は失敗しない」と保証しようとしない。
2. **同じ計画の事実を各段階へ渡す。** 実際の destination、削除・保持した対象を FS と DB の両方で使う。途中で別の path を再計算したり、失敗後に対象を拡大したりしない。DB 行の削除には旧行の exact key、追加・更新には現在の exact path を使い分ける。同じ実体へ解決されることを理由に別の DB 行まで対象へ加えない。上流が複数行を対象と確認した場合は各 exact key を明示し、DB・storage rows・owned collection へ同じ対象集合を渡す。比較と relink は [path-identity.md](path-identity.md) に従う。
3. **単一 DB 内では集合と transaction を使う。** 一体で保存すべき関連行は既存 gateway／owner の transaction にまとめる。複数 change の exact key / path を一括 request として渡し、item loop 内で同じ table 全走査や transaction 開始を繰り返さない。DB 内の rollback と FS に対する補償は別の契約とし、DB を rollback しただけで FS も元に戻ったと扱わない。複数 DB や外部出力も一つの commit と見なさない。
4. **正常系の粒度を例外中心に決めない。** FS、SQLite、canonical memory の例外は捕捉・報告するが、通常は発生しない障害を完全に補償するためだけに multi-change 操作を per-item durable commit へ分解しない。後続判断に必要な先行成功は session-local facts / overlay で表現する。
5. **必要な順序を owner 内で閉じる。** session durable result、canonical memory／関連参照の必須反映、任意の UI notification を区別する。必要な内部反映の失敗を、通知失敗や単なる cleanup 残りへ格下げしない。model lock／DB transaction を長時間の FS I/O や UI 待機のために保持しない。
6. **失敗後に被害を増やさない。** 状態を確定できない対象の後続破壊的処理と、それに依存する成功処理を止める。既に確認済みの成功 change は session result に保持し、必要ならその集合を一回だけ canonical apply してから terminal failure を返す。全 item の一括 rollback、補償の補償、automatic replay、新しい global fault latch を共通要件にしない。

生成ファイルを一つの destination へ保存する場合は、destination と同じディレクトリに所有する一意の staging file を作成し、serializer の close 後に既存 destination を `File.Replace`、初回 destination を `File.Move` で公開する。公開前に destination を削除せず、公開失敗時は既存 bytes を保全する。staging の cleanup 失敗は主原因を置き換えず、主原因・cleanup 原因・残存 staging path を診断へ残す。保存成功の通知は公開完了後にだけ行い、この規則へ DB と FS の複数ファイル transaction、永続 retry、crash recovery を追加しない。

FS はファイルの実在・内容の正本だが、DB の全情報を再生成できるとは限らない。利用者が編集した項目、playlist、導入状態等を「FS に合わせる」という理由で一括破棄しない。どの field が再走査・再生成可能かは各機能の契約に従う。

### 3.1 SQLite transaction の失敗伝播

`SQLiteConnectionEx.Commit()` と `BmsLibraryDbGateway.ExecuteSongDbTransaction()` は、次の契約で `FSDB-FACTS` / `FSDB-REPORT` を満たす。LR2 folder 同期の直接 commit と、gateway を経由する catalog / install / metadata 更新の共通境界に適用する。

- **Commit は一度だけ呼ぶ。** `SQLiteConnectionEx.Commit()` は基底の `Commit()` を一度だけ呼び、Busy / Locked を含む例外をそのまま返す。sqlite-net は commit 失敗時に内部の transaction 状態を解除して rollback を試みるため、同じ `Commit()` の再呼出しは SQL を実行せず正常終了し得る。これを保存成功の証拠にせず、transaction 全体の自動 replay も追加しない。
- **Primary failure を保持する。** `ExecuteSongDbTransaction()` は action または commit の例外に対して、元の savepoint への rollback を一度だけ best-effort で試みる。既に全体が rollback 済みの場合を含め、追加 rollback の例外で元の例外を置き換えず、元の stack trace を保って再送出する。二次例外は利用可能な `NLogWrapper` の通常診断へ送り、診断出力の例外も primary failure を置き換えない。
- **失敗後の成功処理へ進まない。** 呼出し元は既存の failure / compensation 経路へ進み、この失敗した transaction に対する durable receipt、成功件数、commit 後の canonical 反映を作らない。rollback 成功や全データ不変を推測せず、先行する別 transaction の確定済み結果は保持する。

DML の既存限定 retry、SQLite の `BusyTimeout`、DB schema、process lock、接続 lifetime / shutdown drain は変更しない。生 SQL の COMMIT への置換、独自 transaction state、再帰的 rollback、FS の追加補償は導入しない。接続 close 失敗の既存契約も維持する。

## 4. 補償と前方回復の範囲

前方回復とは、失敗後の状態と保持されたデータを起点に、原因を除いた後で再走査、関連 DB の反映、再生成または手動対応を選ぶことである。失敗した command を同じ引数で自動再実行することや、元の削除意図を後から無条件に完遂することではない。

新規の FS+DB 操作に、成功した FS 操作の rollback を共通要件として課さない。一方、既存の補償を取り除く場合は、その操作の source、既存 destination、上書きデータの保全と UI 結果を改めて決め、機能仕様・テストと一緒に変更する。

| 範囲 | 現行契約と共通方針との関係 |
| --- | --- |
| 移行前の `FileDbMutationExecutor` を使う導入、folder move、merge、自動 rename 等 | 現行routeでは source を item の DB durable receipt まで保持し、一回限りの best-effort 補償を行う。この per-item receipt は移行中の既存挙動であり、multi-change 操作の恒久要件ではない。[mutation session 実装計画](../plan/library-mutation-session-batching-plan.md) の対象routeは、physical success factsの収集とoperation-scoped canonical applyへ置き換える。 |
| session-routed multi-change 操作 | deterministicな拒否・skipはchangeに含めず通常結果へ集約する。予期しないFS failureは不確定なitem以降のunsafeな処理を止め、確認済み成功changeを保持する。DB / required internal apply failureでは成功と推測せず、full filesystem rollbackを新設せずに対象・段階・確認済み変更をterminalへ返す。 |
| 残存する executor の補償失敗 | primary failure と補償 failure、復旧に必要な path を残して手動対応へ移す。補償の補償、再帰的 rollback、無条件 cleanup は行わない。 |
| session canonical apply 後、または既に確定した別操作 | rollback／compensation に戻らない。内部反映失敗と cleanup 失敗を区別して現在状態を保持する。残存物の cleanup に失敗しても、元の導入を fresh install としてやり直さない。 |
| ライブラリ削除など、FS の削除が先に確定する操作 | 消したファイルの復元や trash の自動取り出しを要求しない。DB 反映失敗を明示し、後の対応は現在の対象・実在状態から判断する。既存 executor へ形式的に統合するためだけに staging／backup を追加しない。 |
| 生成ファイルと DB／外部 DB の反映 | 必須出力と再生成可能な派生物を区別する。前段の commit を後段失敗で取り消すことを一律に要求せず、未完了の出力・同期を明示する。機能固有の stronger contract がある場合は維持する。 |

共通方針のための persistent journal、pending-operation table、crash replay、定期 retry、永続的な repair queue は必須としない。追加する場合は、必要な利用者挙動、保存対象、再開時の安全性と終了条件を別の decision として承認する。

## 5. 結果と利用者への表示

以下は区別すべき意味であり、全操作に新しい共通 enum や result hierarchy を実装する指示ではない。既存 receipt／typed result を使い、caller が必要な違いを失わないことを契約とする。

| 観測結果 | 終了時の扱い |
| --- | --- |
| 変更前に拒否・失敗 | 変更なしが確認できる場合だけ、そのように伝える。承認済み no-op と failure を混同しない。 |
| FS を一部または全部変更し、DB 反映に失敗 | 部分完了・要確認として対象と確認済み FS 変更を伝える。ファイルが復元された、または DB が反映済みと推測しない。 |
| FS または DB の結果を確定できない | 未確認の範囲を明示し、成功／未変更のどちらにも倒さない。この区別を既存の failure detail で表せるなら新しい state は作らない。 |
| DB commit 済みで必要な内部反映が失敗 | commit 済みという事実を保持し、内部反映失敗として伝える。通常完了にせず、FS／DB の rollback や元 command の replay へ戻さない。 |
| 必須の反映は完了し、cleanup だけ失敗 | 完了部分と残存物・cleanup failure を分けて伝える。既存の `CompletedWithCleanupFailure` は通常完了だけに潰さない。 |
| 必須の反映と cleanup が完了し、任意通知だけ失敗 | durable success を failure に変更しない。利用可能な既存診断へ通知失敗を記録し、再帰的な通知／復旧機構は追加しない。 |

結果に必要な情報は、operation の種類、対象 path／件数、確認済みの変更、未完了の段階、primary error、および存在する場合の補償・cleanup error／recovery paths とする。既存の request／receipt から得られる情報を使い、表示用のために新しい永続状態や全件監査ログを作らない。未処理 item を成功件数へ含めない。現行 `RecoveryPaths` は計画と実行中に追跡した path の集合であり、すべての残存物の実在を検証した一覧ではないため、確認候補として案内する。

捕捉した部分失敗について、ログだけを残して通常完了を表示するのは不十分である。利用可能な UI の terminal path で要確認を伝え、詳細が多ければ既存ログの参照を案内する。後続処理の例外で primary error を置き換えず、lease／lock を解放してから通知する。表示文言の追加時は既存の多言語リソース契約に従う。

UI、shutdown、logger 自身の failure を含めた必達保証は設けない。これは、通常利用可能な通知経路へ結果を渡し忘れる実装や、捕捉した failure を成功へ変換する実装を正当化しない。

### 導入・マージの宛先型衝突（AUD-01）

置換許可は、ファイルとディレクトリの間の型変更を許可しない。異なる型の既存宛先は、退避・公開より前に拒否する。共通 executor は計画全体を最初の変更より前に検証し、各宛先の退避直前にも確認する。削除専用の対象を配置先として検証しない。

導入では、既存の採番・宛先解決とコンポーネントの除外規則を適用した上で、パッケージ全体を読み取り専用で検証する。スマート上書きの判定、source の削除、ディレクトリ作成、DB 反映より先に検証を完了する。型衝突したパッケージは元と宛先の内容・登録・保留状態を維持し、成功件数や成功後 cleanup に含めない。独立した他のパッケージは継続できる。同型上書き・既存の譜面採番・新規フォルダー採番は維持し、衝突回避のための新しい同梱ファイル改名は追加しない。

重複フォルダーマージでは、選択した source フォルダーから宛先への既存の確定単位全体を、最初の変更前に検証する。一部だけの統合成功は作らない。共通パッケージサービス自身も変更前に同じ検証を行う。

通常の事前拒否は、外側の読み取り専用検証で検出した場合に限る。操作単位に集約し、変更 lease・DB 使用権・transaction・同期 lock を解放してから既存通知経路で Warning / OK を一回表示する。理由、拒否件数、source・導入先・衝突パスを示し、詳細は先頭5件と残件数、ログには全検出分を記録する。他のパッケージが成功した場合だけ成功件数を添える。通常キャンセルでも既検出情報は残し、終了中は新しいモーダルを開始しない。

executor が実行時に検出した型衝突は、既存の実行失敗・補償・復旧通知へ渡す。内包する例外の型だけで、補償失敗を無変更の事前拒否へ格下げしない。外部変更の継続監視や新しい復旧保証は設けない。

#### Verification map: AUD-01

`BmsLibraryPackageInstallServiceTests` は smart ON/OFF、順序付き候補、実保留導入の成功・拒否混在と FS/SQLite/一覧保全を検証する。`BmsLibraryDuplicateServiceTests` は実マージの一確定単位と登録保全、`ResilientFileMutationServiceTests` は両方向の executor 拒否、退避直前検査、既存補償と復旧失敗を検証する。いずれも固有の temp filesystem / DB と既存 I/O 境界を使う。

`FileDbMutationReportTests`、`PendingPackageWorkflowOwnerTests`、`PackageInstallWorkflowOwnerTests` は集約警告、成功0の案内抑制、実ownerからterminalへの資源解放後の表示、cancel/shutdown、実行失敗との分類を保証する。`LocalizationResourceParityTests` は全言語の key・placeholder を確認する。既存の通常 Functional lane で実行し、実モーダルや固定待機を追加しない。

## 6. 後から安全に整合を取る条件

自動的・無条件の最終収束は保証しない。DB 書込み不能、権限、媒体の接続等の原因が解消され、現在状態を安全に読めることが再整合の前提となる。通常は失敗しない操作の失敗を一過性と決めつけず、同じ command の単純 retry を標準の回復策にしない。既存の低レベル I/O の限定 retry を、この文書だけで削除・拡張しない。

再整合を行うときは、次を守る。

- 新しい明示 request として、対象の現在 identity／path、現在の承認範囲、必要な排他を確認する。古い計画の破壊的 replay はしない。同じ path に別のファイルが作られていても、過去の削除対象と自動的に見なさない。
- 「存在しない」と「存在を確認できない」を区別する。アクセス拒否、媒体切断、不完全な走査を欠落確定として扱い、DB 行や利用者データを削除しない。
- 完全かつ信頼できる走査で再構成可能な情報だけを、既存の再読込・再生成経路で反映する。再走査できない内容や対象が曖昧な内容は、手動確認へ移してよい。
- 空走査の保護は維持する。検出 0 件で既存 catalog がある場合、保護によって DB 行が残ることを許容する。最後の譜面の削除後も含め、「再起動すれば必ず直る」「再読込で必ず消える」と案内しない。
- 利用者へ案内する対応は、その機能で実際に利用可能かつ安全なものに限る。安全な再反映経路がない場合は、原因解消と対象確認・手動対応が必要であることを伝え、未実装の修復ボタンや必ず成功する再試行を約束しない。

現行の空走査保護は `BmsLibraryInitializationService.ApplyFileScanDiff`／`ShouldSkipEmptyScanWithExistingDb` にあり、startup と `ReloadFileDiff` の共通経路に作用する。この保護の存在や、保護が収束より優先されること自体は不具合ではない。

正常な差分反映の対象範囲では [path identity の収束規則](path-identity.md#convergence) を用い、case-only とそれ以外の path 差分を同じ exact set の差分として扱う。これは上記の安全条件を外す意味ではない。軽量 `ReloadFileDiff` は DB を再読込しないため、外部 DB 編集を取り込む必要がある場合は `FullReinitialize` を使う。局所操作や任意の再読込が、外部編集を含む全 DB を必ず修復するとは案内しない。

### 登録ディレクトリとファイル差分の入力

起動、手動ファイル差分リロード、初期化再実行では、利用不能な登録 BMS ルートを走査入力から除外して続行しない。要求した登録、実際の chart / resource 走査ルート、LR2 カスタム出力ベースを区別する。登録は存在判定より前に捕捉し、親子の別登録も必須検査対象として保持する。設定の読込み・表示・保存で欠落登録を自動解除しない。不正な非空パスや設定読込み失敗を空の成功入力に変換しない。

BMS ルートはディレクトリ属性と直下列挙の開始を確認し、空でも利用可能なら受け入れる。BMS ルートへの書込みは要求しない。LR2 連携時の出力ベースには、自己所有の新規一時ファイルによる作成・書込み・削除の確認も行う。検査で欠落ディレクトリを作成せず、既存ファイルを変更しない。出力ベースと管理下の未生成 playlist 子フォルダを混同しない。用途の詳細は [playlist-data-and-export-flow.md](playlist-data-and-export-flow.md) に従う。

更新入口の検査を通した immutable な要求を、同じ操作の走査と差分反映へ渡す。途中で現在存在するルートだけを取り直さない。外部媒体は走査中にも切断され得るため、prefetch 完了後・差分反映前に同じ要求集合を読取り専用で再確認する。失敗時は型付き失敗を伝え、当該差分と成功後の LR2 同期・playlist 参照更新を開始しない。利用不能を DB 書込み失敗に分類せず、最後の正常 catalog を欠落走査で置き換えない。

この確認は非再帰であり、全ファイルの権限、将来の接続維持、最終確認直後の切断、FS と DB の原子性を保証しない。既存の不完全走査・空走査保護を併用する。既に確定した別操作や、late failure より前の schema / metadata 更新を巻き戻す保証は増やさない。

### Verification map: 登録ディレクトリの保全

`LibraryDirectoryPreflightTests` は configured 入力、path の意味、用途別の読取り・書込み検査と自己所有 probe の failure / cleanup を検証する。`BmsLibraryDirectoryAvailabilityTests` は production の `ReloadFileDiff` / `Reinitialize` を使い、一部ルートの実退避、prefetch 中の切断、DB / catalog 保全、復旧後の再試行を検証する。`LibraryFileScanPipelineOwnerTests` は操作 request を共有した差分適用前の read-only 再検査を補う。いずれも通常 Functional 対象で、明示 options / captured scanner と test 所有の temp DB / filesystem を用い、live Everything や実共有の接続状態には依存しない。

## 7. レビューと検証の終了基準

レビューは [codex-agent-workflow.md](codex-agent-workflow.md) の reachability／impact gate と次の分類を併用する。理論上あらゆる API が throw できることだけを根拠に、保証範囲を増やさない。

| 分類 | 判断と対応 |
| --- | --- |
| 契約違反 | production route、成立する入口 assumption、観測結果と破られる契約 ID／機能契約を示す。例: commit failure を成功表示する、DB と違う destination を使う、commit 後に補償する、確認不能な欠落から削除する。実際の影響に応じて修正対象とする。 |
| 許容済みの残留リスク | 非原子性、一回の補償後にも残る不整合、成功済み FS を戻さないこと、空走査保護、crash recovery／必達通知の非保証。必要な停止・結果伝達が守られるなら、それ自体を blocking finding にしない。 |
| 既存未達・対象外 | 今回の変更が導入・悪化させておらず、今回の受入条件にも含まれない gap。既存の追跡先へ結び付け、文書だけの作業に無関係な実装を混ぜない。 |
| 保証を増やす提案 | 新しい journal、automatic retry、rollback、永続 UI 状態、全体停止、必ず収束する修復等。必要性と費用を別途判断し、承認済み仕様の bugfix と混同しない。 |

同じ owner 境界で同じ結果情報が失われる問題は、代表する production route と影響範囲を添えた一つの finding／作業単位にまとめる。catch 箇所ごとに別の recovery subsystem を要求しない。ただし、異なる authority や利用者データへの影響を持つ独立した違反までまとめて隠さない。

受入済みの制限を再度 blocking とするには、前提が production で成立しない証拠、新しい observable impact、または保証を変更する明示要件を示す。新しい例外仮説やより強い保証の好みだけでは、解決済みの判断を開き直さない。

実装を変更する unit の受入条件では、到達する phase boundary を選び、FS failure／partial change、DB commit failure、commit 後の必須反映 failure、cleanup failure のうち該当する結果と caller／UI への伝達を検証する。既存の補償を触る場合だけ、その所有者、一回限りの試行、補償 failure も対象とする。全 API の全組合せ、無条件収束、保護された 0 件からの削除を要求しない。

テスト追加・期待値変更には [test-authoring-contract.md](test-authoring-contract.md) と承認済み Test Contract Packet を用いる。今回の仕様整理には runtime test の追加・変更を含めない。
