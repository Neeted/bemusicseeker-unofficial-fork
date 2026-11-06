# 初期化・再読込みの進捗

## 目的と適用範囲

ステータスバーの表示行、`StartupProgressWorkflowOwner` が管理する親進捗、操作の識別、完了の順序、追加処理の表示を定めます。表示対象の一覧は[ステータスバーに表示する処理](statusbar-progress.md)、処理の内容・準備条件・並列実行と依存は[起動仕様](startup.md)を正本とします。

## 用語

[共通用語集](../glossary.md)と[起動仕様の用語](startup.md#用語)を参照します。コードの識別子は原表記を使います。

| 用語 | 意味 |
| --- | --- |
| 親進捗 | 必須初期化・再読込みの操作全体の進捗。予定した段階の完了数で表す。 |
| 追加親 | 「起動に伴う追加処理」の親行。対象の起動が受理した追加処理の終端数で表す。 |
| 表示識別 | 受付時に捕捉した表示世代・発生元の操作トークン・要求元・同じ管理主体内の要求版の組。通知を行と親へ対応付けるために使う。 |

## 仕様

### 所有と計算

`StartupProgressWorkflowOwner` は、初期化・再読込みの予定、完了、失敗、操作トークンと親のゲージを所有します。`OperationProgressHubViewModel` は、各機能が送る事実を表示行へ投影します。画面の親は処理の接続と配置を担当し、表示のために処理の受付・保存・実行順を画面へ移しません。

必須初期化・再読込みの親進捗は、開始時に予定する段階を固定し、最大値をその件数、現在値を予定済みかつ完了済みの件数とします。

- 後から予約した処理で分母を増やしません。
- 不要な段階は、スキップの確定後に完了とします。
- 背景への要求が必要な段階を、要求・スキップの決定前に完了させません。

追加親は[追加処理の段階と終端](#追加処理の段階と終端)に従います。

操作トークンは、共通のセマフォの取得後、実際の開始の直前に作ります。初回はモデルの作成後に基準を捕捉します。遅延表示、フォルダの更新、外部同期、参照の反映は、予約時と現在のトークンが一致する場合だけ段階を進めます。

必須の終端の公開順序と局所結果は[起動の流れと完了境界](startup.md#起動の流れと完了境界)に従います。UIへの反映の予約だけを成功に数えず、終了による取消・実際のUIの失敗・旧いトークンを成功として完了させません。記録用のストップウォッチや背景のidleも、必須の完了条件にしません。

### 操作ごとの予定

| 操作 | 必須の対象 |
| --- | --- |
| `Startup` | 開始、DB・列挙・差分、データ・必須UI・操作解禁、保存済みスコア・譜面情報・プレイリスト項目。必要な参照と出力・LR2・後片付けは、直接待った手続きの終端に含める。 |
| `FullReinitialize` | 既存の組のDB・列挙・差分、保存済みスコア・譜面情報・項目・参照、必須UIと操作解禁。 |
| `ReloadFileDiff` | 開始、列挙・差分、項目・参照、必要な表示と操作解禁。 |
| `ScoreOnly` | 開始、保存済みスコア・必要な表示と操作解禁。 |
| `ReloadTables` | 開始、項目・参照・利用者が要求した外部同期、必要な表示と操作解禁。 |

段階数は表示のための集約です。順位・保守・導入可能譜面の保守・譜面情報の補完などの任意処理は親の初期の予定へ入れず、要求の同一性を持つ後続・独立した行として追跡します。親進捗は、直接待った必須の手続きと必須UIの終端で完了し、ログ・スケジューラーの登録の閉鎖・全体のidleを成功条件にしません。

### 段階の意味

| 識別子 | 完了する条件 |
| --- | --- |
| `CoreInitializeStarted` | 操作を開始した。 |
| `LibraryDatabaseLoadDone` | カタログDBの読込みを完了した。 |
| `LibraryFileEnumerationDone` | 譜面・リソースの列挙を完了した。 |
| `LibraryFileDiffDone` | 差分を反映した。 |
| `StartupReadyData` | 導入判定用のデータがそろった。 |
| `StartupReadyUi` | 親のLとPの解放後に、必須の表示と実際のホスト接続のUIのTaskを終えた。 |
| `StartupReadyOperable` | 必須処理・親の解放・必須UIとホスト接続が終わり、成功の公開と通常入力の解禁が成立した。 |
| `ScoreHydrationDone` | 現在のスコアを適用した。 |
| `ChartInfoHydrationDone` | 現在の譜面情報を読み込んだ。 |
| `PlaylistEntriesHydrationDone` | ローカルのプレイリスト項目を読み込んだ。 |
| `PlaylistReferenceApplied` | 参照を適用した。 |
| `ExternalPlaylistSyncDone` | 外部同期を終えた。 |

必須の進捗は、親の受付の解放後の必須UIと成功の公開で完了します。後続が残っていても必須の段階は完了でき、LR2の段階の件数・単独の失敗を、親の分母やローカル準備の成功へ混ぜません。

### 行の単位と配置

実行中の親の名前は「初期化」「ファイル差分の再読込み」「スコアの再読込み」「プレイリストの再読込み」「全再初期化」で固定し、操作可能への到達で切り替えません。完了・失敗は各操作名へ状態を添えます。

- 通常の親に、未完了の子を一つ選んだ補足は出さず、明示的な失敗理由だけを保持します。理由が空なら、推測した子の名前を補いません。
- 画面準備は、起動のデータ準備の完了から操作可能まで子行で示します。

並行する独立した処理は、それぞれ一行で同時に表示します。

- 初期化など全体の進捗が必要な操作は親行、そこで実行中の処理はインデントした子行とし、原則二段までにします。
- 逐次進む一つの処理の読取り・解析・反映は、同じ行の段階の変更で表します。内部のワーカーや個々のファイルは独立した行にしません。

各行は、左のゲージ、残りの幅を使う処理名・状態・件数・対象、必要な末尾の取消で構成します。対象名は幅に応じて省略し、全文をツールチップへ渡します。ステータスバー全体を固定の一行の高さにせず、詳細の文字列にも固定の幅を設けません。画面の実測の高さを配置に使い、行数から別の高さの状態を管理しません。

表示順は処理の意味ごとに固定し、件数の更新で並べ替えません。

- 実行中の行を中心に表示し、進めない理由がある待機はその理由を示します。未開始の全ての予定を子行として並べる必要はありません。
- 同じ仕事のスケジューラーの通知と機能専用の通知は、一つの行へ統合します。独立したURL取得、導入、推定を、優先順位で一つの表示枠へまとめません。
- 取消は、対象行の本来の所有者へ届きます。LR2の明示的な同期は設定画面から行います。

プレイリストも要求元の処理を区別します。外部同期、通常の表URLの取込み、beatorajaの表URLの取込み、選択した表の再同期、サマリーの一括編集は、それぞれの開始・進捗・終端を同じ要求元の識別へ渡します。既存の操作IDがある場合は保持し、受付が一つに制限される経路に、表示用の新しい操作の台帳を追加しません。外部同期のスケジューラーの行は、同じ外部同期の専用行がある場合だけ統合し、別の取込み・編集の行を理由に隠しません。

### 件数と通知

#### 表示識別の捕捉

表示識別は受付時に捕捉し、通知を受信した時点の現在の操作で付け直しません。

- 再利用されるワーカーは、各周の要求版・識別・通知先を同じ既存のロックで取り出します。
- 呼出しの文脈がない新規の受付は、その時点の表示世代とトークン0で独立した要求とし、旧い文脈がある要求の世代を付け直しません。
- スケジューラーの版と、機能の要求版を横断して比較しません。
- 参照・外部同期・項目の読込みの共用の完了スロットには、表示用の要求元と実際の要求版を添え、既存の最大版と完了の計算を保ちます。
- 読込みの受領や外部同期の各実行周から起こす参照・出力・修復などの表示の発生元は、その要求に捕捉した世代と操作を引き継ぎ、後続自身の既存の要求版を添えます。機能側のトークン0と完了の計算は変えません。
- 表示識別のトークン0を現在の親と一致する扱いにせず、スケジューラーの予約の世代をリセットしても、発生元を付け直しません。

プレイリストの専用の通知は、既存の `Source`・`OperationId` と、捕捉した表示識別の組で区別します。同じ組の開始・更新・終端だけが同じ行を所有します。別の要求は同じ `Source` でも併存でき、旧い要求の終端で新しい要求の行を消しません。既存の要求版・操作IDがない一般の手動操作は、既存の排他・範囲・UI通知の順序を維持し、表示用の採番や台帳を増やしません。

#### 件数の単位

確認対象、差分の解析対象、反映するDB行数は別の単位です。処理名と件数の単位を対応させ、候補の総数を変更件数として表示しません。総数が確定していない探索・読込みは不定のゲージにし、探索途中の結果件数から総数を推測しません。親のゲージは完了した段階数であり、子の対象件数や実時間の割合ではありません。

初期化の詳細は処理別の最新値として集約し、並行するDB読込みと探索の開始通知を互いに上書きしません。通知は送出時に捕捉した操作の識別を保持し、置き換えられた操作の通知を現在の行へ適用しません。

#### 同じワーカーが複数の要求を処理する場合

| 処理 | 契約 |
| --- | --- |
| 譜面情報の読込み・補完 | 一つのワーカーが後から予約された要求も処理する。進捗の件数にはその処理が捕捉した要求版を保持し、現在の親操作が必要とする要求版と一致する値だけを反映する。ワーカーの開始時点や通知の受信時点の操作の識別で、新しい要求や旧い要求を読み替えない。 |
| スコア・順位の更新 | 受付時に表示世代を捕捉し、ワーカーは各周の要求版とその通知先を一緒に取り出す。同じワーカーが次の要求を処理しても、旧い要求の終端と新しい要求の開始を混ぜない。表示識別が現在の予定・追跡要求と一致する場合だけ子行にし、それ以外は独立した行とする。 |

表示のために、実行・依存の解除・収束の条件を変えません。

#### 逆順の通知と最新値の集約

外部同期 `external_playlist_sync` と選択した表の再同期 `playlist_manual_reload` は、同じ要求の識別の有効な通知の間で、処理済みの件数が小さい通知をDispatcherへの投入前に棄却します。

- 件数と直近の終了名は、同じ採用済みの通知から表示します。同数では最新の通知を採用します。
- 終端の通知は件数の比較の対象外とし、0件から始まる次の要求や、別の要求・世代の既存の識別を保ちます。
- 全ての中間値の描画は保証しません。分子・補足・複合の終端は[ステータスバーの処理行](statusbar-progress.md#実行中の処理行)に従います。

共通の通知の集約は最新値を使い、短い処理の開始・中間値が描画されることを必須にしません。実処理の完了は表示の排出を待たず、保存の成功・失敗を処理済みの件数100%から判断しません。通常の成功の行は終端で消え、初期化の親の完了の余韻は維持します。LR2の未完了・失敗など、利用者の操作が必要な状態は、他の進捗と同時に残します。

### 言語変更への追従

各処理は、段階・対象・件数・進捗値・時刻・URI・診断などの事実と、操作の種別を保持します。種別には、URL取得か外部探索か、プレイリストの外部同期・手動の再読込み・取込み・beatorajaのURL取込み・属性の保存・外部属性の初期化・カスタムフォルダの出力・修復・BMTの出力か、取込みの段階、カスタムフォルダの処理の段階を含みます。

- 作業名・書式・段階名・段階と対象名の連結・LR2とプレイリストの同期状態の文言は、保持した事実から、表示側が読取りのたびに現在のリソースで作ります。
- 生成元は翻訳済みの文字列を保持せず、翻訳済みの文字列から引数を逆算しません。
- 利用者名、URI、パス、`ScannerLabel`、導入の表示名、一般の例外のメッセージなどの原文は変えません。

進捗行と `StartupProgress` の表示の言語追従については、`ResourceService.Current` の `Resources` の通知を `OperationProgressHubViewModel` だけが購読します。購読はメイン画面の寿命（`AttachLanguagePresentation` / `DetachLanguagePresentation`）に従い、閉鎖中・切離し後は何も通知しません。

言語の通知を受けたHubは、実行中の `StartupProgress` の表示値を現在の状態から現在の言語で再適用し、`Rows` を一回だけ再通知します。

- 再適用は、完了の記録・完了の通知・非表示の予約・操作の準備を行わず、非アクティブの親には何も出しません。
- 失敗理由は、登録フォルダの検査の失敗なら事実として保持して現在の言語のリソースから作り、一般の例外は技術的な診断として原文のまま保持します。

言語の変更を理由に、再走査・同期・取得・DBの読込み・出力・保存・再推定を実行せず、段階・対象・件数・進捗値・取消の状態・要求の世代を変えません。言語の変更の前に投入された通知や次の通常の進捗も、現在の言語になります。この更新は任意の表示更新であり、再適用や通知の例外を、言語変更の呼出元や他の購読者へ伝播させず、[表示と後続処理の区別](#表示と後続処理の区別)の故障区間の扱いに従います。

### 表示と後続処理の区別

`OperationProgressHubViewModel` の `Rows`・`HasRows`・起動の背景表示の変更通知は、任意の表示更新です。購読者の例外でも、受理済みの管理主体の実処理・結果・次の受付を止めません。必須の参照・索引・公開の故障を、この扱いへ広げません。

行の更新は高頻度のため、同じ表示通知の連続した故障区間では、元の原因を一度だけ局所診断し、三つの通知が全て成功したときだけ次の区間へ戻します。診断先の失敗で再通知や同期的なログの連打を作りません。この状態は表示通知の寿命の中だけで使い、操作の通知済みの台帳や再実行の条件にしません。

表示は少なくとも、開始、DBの読込み、列挙、差分、画面準備、各種情報の読込み、必須処理の完了を区別します。件数を持つ子の処理は、処理済みの件数・総数と対象を出せます。

`startup_post_initialization_maintenance_complete` は進捗の段階ではありません。成立条件は[起動の計測の記録名](startup.md#計測の記録名)に従います。ログや集計が新しい仕事を開始せず、必要でないLR2の要求への依存を作りません。

操作可能の直前から、起動の操作トークンと表示世代を持つ追加親を有効にします。別の永続状態や処理件数として扱いません。必須初期化の親行が終了しても後続が残る場合は、起動後の処理のグループで追います。プレイリストやLR2などの独立した行の存在を理由に隠しません。

専用のLR2の行は、必須の起動・再初期化・差分再読込みで直接待つ実行中は、同じ要求の親へ対応させます。終端後の警告・未完・失敗は独立した行として表示します。初期化の親の実行中・完了の余韻中も同時に表示し、親の成功や分母へ混ぜません。

作業スレッドからの後続の完了も、表示の終端だけを既存のUIへの配送経路へ渡します。終端の送出時に操作トークンとスケジューラーの世代を捕捉し、UIへ反映する直前に両方の一致を検査します。本体の収束・完了の記録は表示の排出を待たず、古い終端で次の操作の背景の行を消しません。

### 追加処理の段階と終端

追加処理の受付・集約・実終端の正本はスケジューラーと事前準備の管理主体であり、並列実行と依存は[起動の追加処理の並列実行と依存](startup.md#追加処理の並列実行と依存)に従います。追加親は、それらの小さい変更不能なスナップショットをHubへ渡されて表示します。UI専用の永続的な台帳は持ちません。

#### 段階の数え方

追加親の1段階は、受付時に捕捉した起動の操作トークンとスケジューラーの世代が一致する、論理的な後続の仕事1件です。

| 値 | 契約 |
| --- | --- |
| 最大値 | 受理した仕事の数。後続から派生する新しい仕事は、元の起動トークンと世代が一致するときだけ追加するため、増える場合がある。 |
| 現在値 | 正常終了・失敗・開始前の破棄のいずれかへ、実際に終端した仕事の数。 |
| 表示 | `[完了段階数/総段階数]` の確定ゲージ。100%は全件の成功を意味せず、失敗は既存の診断・結果へ残す。譜面数・ファイル数・所要時間は分母にせず、子行は自身の進捗の単位を維持する。 |

- 省略・無効化された未登録の仕事は含めません。
- 同じ名前の未開始の要求の置換・集約は1件とし、診断用の受付回数を分母へ流用しません。
- `StartupPostInitializationWarmupOwner` の一連の事前準備は1段階とし、`playlist_virtual_order_prewarm` の予約・実行を重ねて数えません。予約の拒否・開始前の取消・失敗も、既存の完了通知によって一度だけ終端します。

#### 所属

受付の識別は、追加の仕事の同期的な一括登録の期間だけ明示し、範囲外の手動の要求へ漏らしません。

- 実行時に旧い要求の識別を付け直さず、処理名や親の表示中という理由だけで所属させません。
- 外部同期の業務上の操作トークン0は、表示の発生元を消す指示ではなく、専用の通知へ同じ起動の識別を引き継ぎます。同じ実際の仕事の専用・汎用の通知は1行に統合します。
- 順位の更新、手動の要求、明示的なLR2同期、旧い世代は、追加親の分母にも子行にも含めません。

#### 消去

追加親は、登録の閉鎖・開始済みで、対象の起動の受理した仕事と事前準備がすべて実終端したときに消します。

- 登録の開放中の0件や、一時的な100%では消しません。
- スケジューラー全体のidleを消去の条件にせず、独立した仕事が残っていても対象の起動の終端で消します。独立した処理や独立した読取りの遅延表示で、表示の寿命を延ばしません。
- 追加親の寿命は、`startup_post_initialization_maintenance_complete` の記録や、終了の待機が持つ全ての追跡対象の実終端とは区別します（[終了の待機](shutdown.md#終了準備で待つもの)）。
- 別の操作が起動を置き換えたときは、古い表示を無効にします。終了時の画面の破棄の後までプロセス内の値が残ることは許容します。

親子は既存の共通の行テンプレートを使い、子の左の余白20pxを維持します。

### LR2フォルダの確認と反映

差分内に準備済みのLR2フォルダの反映がある場合は、空白でない実体化済みの要求パスの数を、確認対象のファイルの分母として固定します。変更不要と判定したファイルも確認済みに数え、譜面の差分の解析対象の数と区別します。

通知値は0から総数の範囲で進み、保存前の準備済みの件数を保存の完了と読み替えません。通知は他の処理と同じ最新値の集約へ渡し、先頭の中間値の専用の保持や、描画の機会の保証を持ちません。

反映の成功後に `LibraryFileDiffCompletedVersion` を進め、表示のスケジューラーの排出を待たせません。反映の失敗を成功の完了へ変換せず、進捗の通知の例外でも、DBの反映・取消・失敗の意味を変えません。

### LR2楽曲DB全体同期の段階

#### 事前のプレイリスト準備

事前のプレイリスト準備は、表の投影を表の数、物理的な出力を実際のファイルの数で通知し、変更不要のファイルも確認済みに含めます。

- 実体の探索、出力後の整理、ディレクトリ情報の構成、出力状態の保存、準備結果の構成は、不定の段階です。
- 表ごとの不要なファイル・空のディレクトリの再帰的な整理は独立した不定の段階を残し、次の表の出力では同じ全体のファイル数の有限の段階へ戻します。物理的な整理の順序は変えません。
- 段階の件数だけを通知し、保存位置を持たない全体のカーソル・総数は未指定にします。
- 共通の出力処理へLR2専用の通知を接続し、独立したプレイリスト操作の既存の複合通知・失敗の契約は維持します。

#### 全体同期

全体同期は同じLR2の行を更新し、段階の実際の対象数を分母、確認・処理済みの数を分子にします。

| 段階の種類 | 段階 |
| --- | --- |
| 対象件数を使う段階 | カスタム定義の読込み、ディレクトリ情報と `folderinfo.txt` の読込み、検索ルート・通常フォルダ・カスタムフォルダ・親の行の生成、投影の構成、利用者の値の引継ぎ、楽曲の処理。 |
| 不定の段階 | 入力の準備・探索、初回のDB構造の準備、投影の一括検証、既存のDB行の取得、フォルダ表の全体の保存、入力の最終確認、同期状態の確定。 |

- 対象外のパスを省いた場合も、その対象の確認は処理済みに数えます。件数や単位が変わる境界で段階を分け、生成した行数と入力の候補数を混ぜません。
- 楽曲は準備の終了後に有限の段階を開始し、同じ対象の短いチャンクの保存は楽曲の処理に含めて、同じ役割と、実際の対象の分母・処理済みの分子を維持します。楽曲の読取り・計算とチャンクの保存は並行できます。
- 表示のための再走査、追加の複製、待機、保存方式の変更は行いません。

#### 表示値と通知

進捗の文言とゲージは `StageProcessedCount` / `StageTotalCount` だけを使い、総数がない段階で全体の保存カーソルを借りません。詳細の保存位置は、ラベル付きの診断として残します。既知の現行・旧い段階の識別子は、進捗と詳細の両方で、役割に対応するリソースへ変換し、未知の保存済みの識別子は情報を保持します。準備の100%は、保存の成功・正常な終端を意味しません。

通知の粒度は処理速度を優先して間引けます。

- 件数が確定した対象ありの段階は、件数付きの開始と末尾を通知し、同名の件数なしの開始を重ねません。
- 既知の0件の件数の段階は通知を省略し、量が不定の作業へ読み替えません。
- 並行する楽曲の処理・保存の公開スナップショットは、一回の通知の段階名と件数から構成し、個別の観測プロパティを読み戻して異なる通知の値を混ぜません。
- 共通の最新値の集約により、全ての中間値の描画や、厳密な時機・通知順・数値の絶対的な単調増加は保証しません。処理の完了条件にUIの排出を加えません。

Playlist・通常フォルダ・差分更新・出力先の設定変更・カタログの書込みの失敗から届く既知の固定の段階も、進捗と詳細を処理の役割のリソースへ対応付けます。診断は保存形式を変えず、既知の段階と一致する既定の先頭 `ID: ` だけを表示時に置き換えます。未知のID、他の先頭の形式、元の例外の本文・型・パス・引数・内部例外は保持します。

### 失敗と変更時の制約

失敗は操作を失敗の状態にし、通常の使用中と再試行可能な失敗を分けます。進捗の完了を理由に、作業スレッドからUIの集合を直接変えません。計測を短く見せるために必須処理を後続へ移したり、後続の通信を起動の進捗へ戻して通常の操作を止めたりしません。

初期化の親の表示は、配送時に既存の有効な状態と操作の識別子を検査し、同じ操作の現在の状態から解決します。失敗前に予約した表示更新が失敗後に実行されても、現在の失敗名と明示された原因を、非失敗の表示や空の詳細へ戻しません。完了の記録、完了の通知、表示の配送、完了表示を後で消す処理の予約は既存の順序を維持し、実処理の終端を親のゲージの表示の排出へ従属させません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 予定と完了の集合、古い通知の拒否、表示と失敗 | [`StartupProgressWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Startup/StartupProgressWorkflowOwner.cs) | [`MainWindowViewModelStartupProgressTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/MainWindow/MainWindowViewModelStartupProgressTests.cs) の `StartupProgress_QueuedPresentationCannotOverwriteCurrentFailure` は同じ操作の未完了段階による表示更新を保持し、失敗を先に反映してから実行しても失敗名・原因が残ることを確認する。古い操作の識別子、完了表示を後で消す処理、各操作名は既存の進捗管理の検査系列が分担する。終了時の取消と実際の故障、元例外・次の受付は起動仕様の実構成系列へ残し、完了通知と表示配送の厳密な相対時刻は静的に確認する。 |
| 譜面情報の要求版と進捗件数の一体送出、一致する要求だけの親投影 | [`CatalogChartInfoOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogChartInfoOwner.cs)、[`StartupProgressWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Startup/StartupProgressWorkflowOwner.cs) | [`ChartInfoInlineHydrationTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/ChartInfo/ChartInfoInlineHydrationTests.cs) は同じワーカーの旧要求・後続要求を区別し、[`MainWindowViewModelStartupProgressTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/MainWindow/MainWindowViewModelStartupProgressTests.cs) / [`MainWindowViewModelStartupProgressTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowViewModelStartupProgressTests.cs) は不一致の件数・対象が現在の子行を更新しないことを確認する。 |
| スコア・順位の要求ごとの表示世代捕捉、親所属と独立した終結 | [`BMSLibrary`](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs)、[`StartupBackgroundTaskSchedulerOwner`](../../../BeMusicSeeker/ViewModels/Startup/StartupBackgroundTaskSchedulerOwner.cs)、[`OperationProgressHubViewModel`](../../../BeMusicSeeker/ViewModels/MainWindow/OperationProgressHubViewModel.cs) | [`BmsLibraryIrStartupTests`](../../../tests/BeMusicSeeker.Windows.Tests/Startup/BmsLibraryIrStartupTests.cs) は後続要求と通知例外の隔離、[`StartupBackgroundTaskSchedulerOwnerTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Startup/StartupBackgroundTaskSchedulerOwnerTests.cs) は受付時の世代と非重複、[`StartupBackgroundTaskSchedulerOwnerTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Startup/StartupBackgroundTaskSchedulerOwnerTests.cs) の `PlaylistUrlCompletion_RealAcceptanceCapturesIndependentCurrentGenerationOrRetainsOldContext` は文脈のない受付が現在の世代を独立して捕捉し、旧い文脈を持つ受付の世代を保つことを確認する。[`OperationProgressHubViewModelTests`](../../../tests/BeMusicSeeker.Core.Tests/MainWindow/OperationProgressHubViewModelTests.cs) は予定・要求版による所属、[`MainWindowViewModelStartupProgressTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/MainWindow/MainWindowViewModelStartupProgressTests.cs) / [`MainWindowViewModelStartupProgressTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowViewModelStartupProgressTests.cs) は独立順位処理で後続表示を延命しないことを確認する。Coreの `StartupBackgroundTaskSchedulerOwnerTests.AdditionalProgress_GarbageCollectionIgnoresIndependentRankingButWaitsForStartupAndRequiredWork` は実スケジューラーへ起動仕事・必須仕事・独立順位・メンテナンス処理・GCを登録し、必須仕事と明示依存を待つ一方、順位のgate保持中でも追加親の集計が実終端し、全体の `IsFullyIdle` はfalseのままであることを確認する。 |
| 追加親の論理仕事数、集約・正常・失敗・破棄と一回の事前計算 | [`StartupBackgroundTaskSchedulerOwner`](../../../BeMusicSeeker/ViewModels/Startup/StartupBackgroundTaskSchedulerOwner.cs)、[`StartupPostInitializationWarmupOwner`](../../../BeMusicSeeker/ViewModels/Startup/StartupPostInitializationWarmupOwner.cs) | [`StartupBackgroundTaskSchedulerOwnerTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Startup/StartupBackgroundTaskSchedulerOwnerTests.cs) の `AdditionalProgress_CountsLogicalWorkAndTerminalStatesWithoutLeakingScope` は実受付・同名集約・派生受付・実worker終端から段階スナップショットを確認し、[`StartupPostInitializationWarmupOwnerTests`](../../../tests/BeMusicSeeker.Core.Tests/Startup/StartupPostInitializationWarmupOwnerTests.cs)は予約拒否・開始前取消・失敗でも一度だけ終端することと実行権の回収を分担する。同じクラスの `PostInitializationCompletionRequiresSchedulingClosureAndFullIdle` の全体終端保証は追加親の消去条件へ転用しない。 |
| 実UI後の任意登録、実Taskの追跡と対象起動だけの背景表示終了 | [`MainWindowViewModel`](../../../BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.cs)、[`OperationProgressHubViewModel`](../../../BeMusicSeeker/ViewModels/MainWindow/OperationProgressHubViewModel.cs) | [`MainWindowViewModelStartupProgressTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/MainWindow/MainWindowViewModelStartupProgressTests.cs) / [`MainWindowViewModelStartupProgressTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowViewModelStartupProgressTests.cs)は実起動・ツリー全再初期化のUI gate保持中に任意開始がないこと、成功公開・解禁後の完了案内待機中のHTTP開始、設定によらないリスト取得と実起動の子所属を確認する。Hubの既存テストは段階スナップショットからの数値・確定表示、登録開放中0件・一時100%の保持と独立仕事を待たない消去、旧token・世代・遅延通知の拒否を分担する。実WPFのBinding・インデントは[表示一覧の対応表](statusbar-progress.md#実装とテストの対応)に従う。 |
| LR2未完了状態と初期化の同時表示、親計算からの独立と失敗表示 | [`OperationProgressHubViewModel`](../../../BeMusicSeeker/ViewModels/MainWindow/OperationProgressHubViewModel.cs) | [`OperationProgressHubViewModelTests`](../../../tests/BeMusicSeeker.Core.Tests/MainWindow/OperationProgressHubViewModelTests.cs) |
| LR2反映の保存結果と表示排出に依存しない差分完了 | [`BMSLibrary`](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs) | [`BmsLibraryLr2SongDbSyncTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs) / [`BmsLibraryLr2SongDbSyncTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs) は実DB結果・完了版とUI排出前の未通知、排出後の最新モデル状態・通知例外隔離を確認する。[`MainWindowViewModelStartupProgressTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/MainWindow/MainWindowViewModelStartupProgressTests.cs) / [`MainWindowViewModelStartupProgressTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowViewModelStartupProgressTests.cs) は差分完了の親反映を確認し、LR2表示は [`Lr2SongDbSyncStatusMapperTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Lr2/Lr2SongDbSyncStatusMapperTests.cs) とHubのLR2ケースで確認する。 |
| LR2全体同期の実段階・件数と保存前の未確定 | [`Lr2SongDbSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncService.cs)、[`Lr2FolderTableReconciliationService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2FolderTableReconciliationService.cs) | [`Lr2SongDbSyncServiceTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Lr2/Lr2SongDbSyncServiceTests.cs) の `SyncService_ReportsActualStageTargetsAndKeepsPreparationSeparateFromCommit` は小さい実DBと通常・カスタム・楽曲の複数入力から通知を観測し、分母を入力から判定する。通知例外・空投影は既存ケース、実QueueとUI排出の独立は `BmsLibraryLr2SongDbSyncTests`（Core）が担う。 |
| LR2の翻訳・保存位置から独立した段階進捗 | [`Lr2SongDbSyncStatusMapper`](../../../BeMusicSeeker/ViewModels/Lr2/Lr2SongDbSyncStatusMapper.cs) | [`Lr2SongDbSyncStatusMapperTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Lr2/Lr2SongDbSyncStatusMapperTests.cs) は既知・旧・未知・空段階と段階総数なし・0・正数を、[`Lr2SongDbSyncStatusMapperTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Lr2/Lr2SongDbSyncStatusMapperTests.cs) は共有表示文化圏を保存・復元して日本語と英語を確認する。辞書整合は `LocalizationResourceParityTests`、有限→不定→正常終端と親計算の独立は `OperationProgressHubViewModelTests`、Bindingは既存の `MainWindowProgressStatusBarWpfTests` が担う。 |
| 進捗行・StartupProgressの言語追従、再通知の一回化、Detach・閉鎖・非アクティブでの無通知 | [`OperationProgressHubViewModel`](../../../BeMusicSeeker/ViewModels/MainWindow/OperationProgressHubViewModel.cs)（`AttachLanguagePresentation` / `DetachLanguagePresentation`）、[`StartupProgressWorkflowOwner.ReapplyPresentationForLanguageChange`](../../../BeMusicSeeker/ViewModels/Startup/StartupProgressWorkflowOwner.cs)、[`MainWindow`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) | [`OperationProgressHubLanguageTests`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/OperationProgressHubLanguageTests.cs) は、全playlist種別・StartupProgress・詳細行・推定・背景・追加親・LR2・URL取得を同時に投入した往復で行の文言が現在語になり、行の事実・呼出し回数・Rows通知回数が変わらないこと、保留通知と次の通常進捗が現在語であること、Detach・閉鎖・非アクティブで通知しないこと、StartupProgressの各状態（実行中・完了待ち・登録フォルダ検査の失敗・一般の例外）の補足を確認する。`MainWindowProgressStatusBarWpfTests.ProgressStatusBar_LanguageChangeUpdatesBoundRowsAndStopsAfterClose` は実status barのTextBlockとtooltipの追従と閉鎖後の無通知、`MainWindowViewModelStartupProgressTests.StartupPreflightFailure_ProgressReasonFollowsLanguage`（Windows）は実起動の登録フォルダ検査失敗の理由の追従を確認する。 |
| 進捗の生成元が種別・段階・生の対象名の事実だけを渡すこと | [`PlaylistSyncProgressSnapshot`](../../../BeMusicSeeker/Models/Playlist/PlaylistSyncProgressSnapshot.cs)、`PlaylistUrlDownloadStatusSnapshot`、`PlaylistCustomFolderOutputMaintenanceOwner`、`PlaylistWorkspaceViewModel` の進捗生成元 | `BmsPlaylistCustomFolderOutputTests.ReOutputCustomFoldersAndCommitHeadersToDB_ProgressReportsStageFactsWithRawTableName`（Core）、`PlaylistSummaryBulkEditTests`（Core）、`PlaylistWorkspaceExternalSourceTests`（Core）、`BmsPlaylistExternalReloadTests`（Core）、`PlaylistUrlAcquisitionOwnershipTests.PlaylistUrlProgressAttachedHubPublishesActiveCancelAndTerminalStates`（Core）は、各通知の種別・段階・生の対象名・URIと、URL取得・外部探索の取消後も同じ種別の書式であることを確認する。 |
| 処理別の行、伸縮する配置と行固有の操作 | [`MainWindowViewModel`](../../../BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.cs)、[`MainWindow.xaml`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.xaml) | [`MainWindowProgressStatusBarWpfTests`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowProgressStatusBarWpfTests.cs) |
| プレイリストの要求元ごとの生産・終端、同じ外部同期だけの表示統合 | [`PlaylistWorkspaceViewModel.SyncProgress`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.SyncProgress.cs)、[`OperationProgressHubViewModel`](../../../BeMusicSeeker/ViewModels/MainWindow/OperationProgressHubViewModel.cs) | [`PlaylistWorkspaceExternalSourceTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistWorkspaceExternalSourceTests.cs)、[`BmsPlaylistExternalReloadTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsPlaylistExternalReloadTests.cs) / [`BmsPlaylistExternalReloadTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/BmsPlaylistExternalReloadTests.cs)、[`PlaylistSummaryBulkEditTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistSummaryBulkEditTests.cs) は実操作からの進捗・終端を確認する。[`MainWindowPlaylistWorkspaceWpfTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) の `MainWindowPlaylistSync_UsesHttpAndUiTerminalBeforeAcceptingAnotherEdit` は実Queue外部同期の要求元・対象・終端と既存の受付・UI終端を確認する。[`OperationProgressHubViewModelTests`](../../../tests/BeMusicSeeker.Core.Tests/MainWindow/OperationProgressHubViewModelTests.cs) の `PlaylistRows_SyncAndBeatorajaImportRemainIndependentInEitherCompletionOrder` は進捗受付境界への開始・更新・終端入力で、併存・同源だけの統合・両完了順の片側保持・保留表示排出後の消去を確認する。 |
| 行の任意購読先故障と受理済み管理主体・次操作の継続 | [`OperationProgressHubViewModel`](../../../BeMusicSeeker/ViewModels/MainWindow/OperationProgressHubViewModel.cs) | [`OperationProgressHubViewModelTests`](../../../tests/BeMusicSeeker.Core.Tests/MainWindow/OperationProgressHubViewModelTests.cs) の `ThrowingRowsObserverDoesNotStopAcceptedOwnerOrNextOperation` が実導入管理主体の終端・行消去・次受付を確認する。既存の `StartupBackgroundInitializationPresentation_IsolatesThrowingSubscriber` が起動背景表示の通知分離を担う。連続故障時の診断頻度は局所配線の静的点検に分担する。 |
| LR2事前準備の件数通知と保存カーソル未指定 | [`PlaylistCustomFolderOutputOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistCustomFolderOutputOwner.cs)、[`Lr2SongDbSyncStatusMapper`](../../../BeMusicSeeker/ViewModels/Lr2/Lr2SongDbSyncStatusMapper.cs) | [`PlaylistCustomFolderOutputOwnerTests`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistCustomFolderOutputOwnerTests.cs) は、2表3ファイル（1件変更不要）の実出力、不要ファイル・空子ディレクトリの再帰整理と有限復帰、専用通知例外での同等結果を確認する。`BmsPlaylistCustomFolderOutputTests.Lr2SongDbSyncPreparation_ReportsActualTablesAndFilesThroughWorkflowWithoutSavingFolderRows`（Core）は、実Workflowから通知・保存済み出力状態・準備入力・folder表不変・予約解放までを確認し、空表と空ファイル投影での件数段階の通知省略も確認する。保存位置なしの表示は `Lr2SongDbSyncStatusMapperTests.Create_PreparationStageCountsDoNotAppearAsSavedPosition`（Core）が確認する。 |
| LR2楽曲の有限段階とチャンク保存、公開状態の一体性 | [`Lr2SongDbSyncService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongDbSyncService.cs) | `Lr2SongDbSyncServiceTests.SyncService_RealSongUpdatesKeepFiniteRoleAcrossChunkCommits` は、小さい実譜面1001件を実writerで更新し、複数チャンクの保存前後の有限段階と実対象分母、生成列更新・利用者列保持を確認する。複数入力の段階ケースは同名の0/0通知を含む全イベントを検査し、空入力が件数段階を通知しないことを確認する。Hubは、同じ楽曲役割の有限継続と独立した最終確認の不定表示を分担する。`BmsLibraryLr2SongDbSyncTests.ProgressPublication_InterleavedOwnerUpdatesKeepStageAndCountsFromOneNotification`（Core）は、管理主体の観測プロパティ更新境界で別通知を挟んでも、段階名と件数が一回の通知に対応することを確認する。 |

## 関連資料

[起動](startup.md)、[設定](settings.md)、[終了](shutdown.md)、[性能](../core/performance-and-scale.md)を参照します。
