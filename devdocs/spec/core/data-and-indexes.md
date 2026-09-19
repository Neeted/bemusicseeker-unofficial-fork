# データと索引

## 目的と適用範囲

保存データ、所持譜面集合、用途別の索引について、所有権・識別条件・更新・公開の境界を定めます。少数の変更に対する仕事量と全件処理の受入条件は、[性能とデータ規模](performance-and-scale.md)を正本とします。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

| 用語 | 意味 |
| --- | --- |
| 完全一致パス | 各保存・索引境界が持つ正規化済みのキー。大小文字の比較規則を別の曖昧な同一性へ広げません。詳細は[パスの識別規則](path-identity.md)を参照します。 |
| 所属数 | 同じハッシュやディレクトリに対応する保存主体の数。候補の有無だけとは区別します。 |
| 構造共有 | 変更のない部分を旧スナップショットと共有し、変更箇所への経路だけを置き換えること。 |

## 仕様

### 保存データの責務

| データ | 用途と制約 |
| --- | --- |
| `song` / `folder` | LR2互換のBMSカタログ。アプリ内の保存主体は `BMSFile` です。 |
| `bmson_song` | アプリ独自のBMSONカタログ。LR2の `song` に互換行を作りません。 |
| `chart_digest_map` | BMSのMD5とSHA-256・譜面情報を結ぶ部分的なキャッシュ。初回走査前に完全である必要はありません。実ファイルを読む処理で必要な範囲を補います。 |
| `chart_info` / 解析失敗の記録 | 解析版とファイルの状態に応じた譜面情報。読込み後はセッション索引に反映します。LR2同期の状態を鮮度判定に流用しません。 |
| `maintenance` | 保存済みのリソース健全性とBMS固有の文字コード情報。未計算状態とは区別します。 |
| プレイリストのヘッダー・項目 | 操作と保存・出力の正本。所持判定には項目の選択キーを使います。 |
| `ir_score` / `ir_data` | LR2IRのプレイヤースコアと順位キャッシュ。異なる取得元・更新条件を持ちます。 |

`ChartFile` はこれらのDB行ではなく、用途別の共通読取りモデルです。保存主体との関係は[譜面の共通モデル](../library/chart-model.md)を参照します。

### 保存行の格納と順序

`CatalogStorageRowsOwner` はBMS・BMSON別の変更不能な列と、パス完全一致・保存主体の索引を所有します。局所更新は対象を直接探し、順序キーの二分探索で位置を求めます。読取り専用ビューとスナップショットは列のルートと版を捕捉し、全行を複製しません。

捕捉済みビューの所属・順序・版は固定しますが、要素は現在の保存主体を参照します。そのため、後の正規の移転による保存主体のパス変更は見えます。項目自身のパス・ハッシュを固定する索引スナップショットとは異なります。

| 更新 | 保存行の順序規則 |
| --- | --- |
| 全置換 | 入力の順序と未正規化の行を保持します。呼出側の可変一覧は共有しません。 |
| BMSの追加・更新 | 同じ完全一致パスの旧行を除き、入力順にBMS列の末尾へ追加します。BMSON列だけの並べ替えは行いません。 |
| 未正規化BMSONへの初回追加・更新 | 有効な完全一致パスの先の行を採用し、現在の保存主体のパスを大文字小文字を区別せず安定的に並べます。同じキーの置換では同順位内の位置を保持します。 |
| 移転 | DBと保存主体の反映後、明示的な旧新パスで索引を更新します。変更後のパスから旧キーを推測せず、格納位置を維持します。 |

BMSONの実移転後、次のBMSON追加・更新で必要な全BMSONの正規化は維持します。この例外に全BMSの走査を含めたり、通常の各パッケージで繰り返したりしません。明示的な全置換の版・通知は、同じ入力ビューであることを理由に省略しません。

### 所持譜面集合の識別と順序

`OwnedChartCollectionState` はパスとMD5のある行を採用し、形式をまたぐ同一パスではBMSを優先します。同MD5の別配置は残します。最初に除外した重複行を、先の行の削除後に自動昇格させません。

正本の列も変更不能なルートを共有します。BMSは残存行の相対順を保ち、追加・更新を入力順にBMS末尾、BMSONの前へ置きます。BMSONの追加・更新は生成時に捕捉した `ChartFile.Path` で大文字小文字を区別せず並べ、置換は同順位の末尾へ移します。保存行側の同順位規則とは区別します。

全置換後の最初の追加・更新では、BMSだけの更新でもBMSON部分を一度整列します。BMS部分の列・順序キー・索引は共有し、全BMSの複製や再採番をしません。移転だけの操作は捕捉済みパスと格納位置を保持し、画面の並べ替えは現在の保存主体のパスを使います。

パス完全一致の局所問合せは、別の参照索引を新規構築せずに対象へ到達します。`LibraryChartRefIndexSnapshot` は関係するパス・ディレクトリだけを現在の安定した順序キーで並べ、全譜面の順位表を作りません。

### 共通変更と派生索引

`LibraryMutationOwner` は保存行の変更結果から、追加・削除・移転・ハッシュ変更・導入先変更・保守対象を一度組み立て、`DispatchOwnedChartCollectionMutation(...)` から必要な索引へ渡します。各索引が保存行のsetterや任意のコールバックで別々に判断する経路は増やしません。

通常の変更では、旧パス・旧ハッシュを保存主体の変更前に捕捉し、保存行、所持集合、構築済み索引を同じ変更境界で同期します。未構築の索引は、変更のためだけには構築しません。全置換、旧新の対応不足、保存主体の外部差替え、未対応の変更、途中失敗では必要な無効化を行います。ただし、識別条件違反を無効化で隠しません。

保存行の参照と版は同じロックで捕捉・更新します。差分は変更前の版が一致する場合にだけ適用し、反映済みの版を記録します。取得のたびに保存行と所持集合を全件照合しません。全置換では次の保存行に対応する集合へ置き換えます。

譜面情報の書込みは別の順序を持ちます。DBへ確定する前に現在の保存主体を変更せず、成功した結果に従って保存主体、ハッシュ関連索引、譜面情報のセッション索引、通知の順に反映します。詳細は[譜面情報](../library/chart-info.md)を参照します。

| 索引・表示状態 | 更新方針 |
| --- | --- |
| MD5の所持数 | 追加・削除・MD5変更を集約します。同MD5の置換やSHA-256だけの変更は相殺し、不要な更新をしません。 |
| 導入済みディレクトリ | MD5・SHA-256ごとの所属数、既知ディレクトリを対象だけ更新します。 |
| 実パスの参照・子孫数 | 実際の所属・移転を反映します。予定の導入先を混ぜません。 |
| 導入先の一時状態 | 導入先変更と元の所持主体・パッケージ項目の消失を反映します。実ファイルの存在や子孫数の根拠にはしません。 |
| 親フォルダ候補 | 現行キャッシュを無効化します。捕捉済みパスを再利用し、出力先の正規化は一回の構築につき一度行います。 |
| プレイリストの所持ハッシュ・参照解決 | 構築済み索引へ対象ハッシュの差分を反映し、必要な世代と内容の版を分けます。 |
| プレイリスト参照の表示更新 | 選択キーに一致する所持参照だけをその都度取り出します。全参照一覧を複製してから絞り込みません。 |
| リソース健全性 | 対象の差分を優先し、入力不足時は無効化・延期・必要な全件処理を選びます。 |
| 重複行・グループ | 行は構築済みの場合に差分更新し、グループ結果を無効化します。MD5変更とSHA-256のみの変更を区別します。 |
| 通常一覧 | 所持集合の変更と、警告・保守・導入先・参照表示の変更を別の依存世代として通知します。表示値だけの変更で元一覧の世代を進めません。 |

削除だけの通常通知は、削除したBMS・BMSON保存主体を渡し、行キャッシュの該当部分だけを除きます。追加・移転・全置換など削除差分で表せない場合は必要な全体同期を使います。`NormalLibraryRefreshNotificationVersion` が通常一覧の更新入口であり、保存行プロパティの変更通知を重複した更新入口にしません。

### ハッシュと導入済みディレクトリ

索引の状態、ロック、初期化状態、世代、読取り・構築・反映は `CatalogOwnedCollectionOwner` が所有します。BMSLibraryの公開窓口や利用側に同じ世代・索引の正本を重複して置きません。

`PrimaryHashLookupState` はMD5の件数だけを扱います。既所持判定や安全な削除のために、全ディレクトリ索引や詳細譜面を作りません。`InstalledChartLookupIndexState` はMD5・SHA-256からディレクトリへの所属数、ディレクトリ内の異なるMD5数、既知ディレクトリを持ちます。同じハッシュを持つ最後の保存主体が消えるまで候補を保持します。

所属が変わったハッシュだけを、大文字小文字を区別しない候補順で更新します。スナップショットと除外付きの検索結果は変更不能なルートを共有し、後続変更で変わりません。作成時の全map複製や全候補の再整列をしません。`DirectoryReferenceCount` は更新済みの所属数を返します。初回構築と、呼出側が全既知ディレクトリを明示列挙する仕事は残ります。

通常削除と統合は、種類・完全一致パス・変更前MD5・SHA-256を同じ確定事実としてカタログと索引へ渡します。`PathCleanup` を理由に既知の旧ハッシュを捨てません。DBだけの整理と所持主体の除去を区別し、元に所持譜面のない統合は検索索引の取得前に終了します。

### プレイリストの所持・解決索引

所持ハッシュ索引はMD5・SHA-256それぞれの保存主体数を持ち、最後の主体が消えた場合にだけ所属を除きます。両ハッシュ集合が同じなら内容の `Version` を維持し、所持数集計を再利用します。保存行・所持集合の版は内容の版と別に管理します。

参照解決索引は、種類と完全一致パスで全候補を保持します。代表は現在パスの大文字小文字を区別しない最小値、同値なら所持集合で先のものです。代表の削除で次候補を選び、最後の候補がなくなると未解決にします。項目のMD5がある場合はMD5だけで解決し、未解決でもSHA-256へ切り替えません。

構築済み索引は、導入・移転・削除・ハッシュ変更の対象候補だけを更新します。旧スナップショットのパス・ハッシュ・代表は固定します。全置換、旧事実不足、全置換後の初回BMSON順序正規化では既存の全無効化を維持し、その後の通常更新は差分に戻します。

ハッシュ差分は通知前に反映します。既に現在の元データから再構築した索引へ旧差分を重ねず、正常に反映したハッシュ変更区間の終了時に再度無効化しません。元の版が違う結果や失敗した結果は公開しません。同世代の先行読込みTask共有は既存の管理主体に残し、利用機能ごとの別キャッシュを作りません。

### リソース索引と所有関係

`LibraryResourceIndexOwner` は、現在の `LibraryResourceIndex`、`DirectoryResourceLookupCache`、世代をまとめて所有します。長寿命の利用側は過去のキャッシュ実体を保持せず、現在のスナップショットまたは変更命令を使います。ファイル操作は索引のロック外で実行し、成功事実を反映します。

索引のキーは拡張子を除いた譜面相対パスです。`foo.wav` は `foo`、`sound/foo.wav` は `sound/foo` とし、単なるファイル名だけでは一致させません。音声・画像・動画を分けます。祖先から見えるリソースを含む集合と、最も近い譜面ディレクトリが所有する集合（`SelfOwned`）も分けます。

| 構造 | 共有と更新の規則 |
| --- | --- |
| ディレクトリ項目 | 変更不能な `Entry`、パスから順序番号へのmap、存在する番号だけの順序付きmapを共有します。全ディレクトリの複製・再採番をしません。 |
| 項目の位置 | 一つの未公開命令内では最後に削除した位置を再利用します。空き位置は次の命令へ持ち越さず、既存の候補順を守ります。欠番を列挙しません。 |
| 逆引き | 所有権を移した変更不能な初期mapと、キーごとの最新値だけを持つ共有可能な差分mapを使います。前世代を連鎖して探索せず、初期mapと差分の二層で解決します。 |
| 更新・公開 | 変更キーへの木の経路と必要な候補配列だけを置き換えます。複製直後の初回更新や公開時にも、全件変換・全件固定化を行いません。 |
| 入力の所有権 | 所有権移転が明示されたネイティブの構築結果を除き、入力配列を複製します。公開後に呼出側が変更せず、内部配列も外へ公開しません。 |

移動・名前変更は、対象項目のハッシュに対応する計算済み候補だけを調べ、位置を保ってパスを置換します。最終候補列が順序も含めて同じなら逆引きを書き換えません。空リソースの項目追加は項目格納だけを変更できます。

ただし、リソースの集合が同じだけでは変更なしと判定しません。`SelfOwned` だけの変更でも、既存の除去後追加の規則で `[A, B]` が `[B, A]` になる場合は更新します。未計算と計算済みの空候補、ハッシュ0、大小文字、遅延計算と全構築の意味を維持します。`updatedHashes` は除去・追加の遷移件数であり、正味の書込み回数ではありません。

一回の変更セッションで成功した導入・削除・統合をまとめ、必要な逆引き反映を一度行います。後続パッケージの判定には先行の**物理成功**を操作内の一時状態として渡し、パッケージごとの正本公開を通信手段にしません。元フォルダ除去と統合先の追加も一つの未公開変更にまとめ、同じ最終構成なら世代を維持します。

フォルダ削除は物理削除に成功した `DeletedFolderPaths` だけを集約し、命令全体で項目を一回走査して祖先集合と照合します。重複・親子の対象を二重計上せず、失敗・未実行対象は除きません。入力列挙や反映の失敗では旧索引・世代を維持しますが、先行する物理削除を取り消した保証にはしません。

初期mapの置換前データは索引の寿命内に残り、差分は同じキーの最新値だけを保持します。明示走査・全置換で初期mapを入れ替えます。自動圧縮、世代台帳、操作後への必須更新の延期は追加しません。

通常起動のネイティブ経路は `EBridge_ScanChartAndResources` の圧縮された結果から直接索引を作り、`ChartScanResult` にリソース辞書を重複して具体化しません。Everythingを利用できない場合の管理側走査とテスト用の統合では、カテゴリ別辞書を持つ経路を使います。逆引きは導入準備完了前に完成させ、保留パッケージの推定へ初回構築を持ち越しません。

### リソース健全性

有効な保守情報はDBから反映した `DbHydrated` または計算済みの `Calculated` です。未計算の仮値を健全性索引の正本にしません。通常起動では保存済み情報を読み、欠落・古い対象だけを後続保守へ回します。

構築済みの `ResourceHealthIndexSnapshot` は、種類と完全一致パスから対象・ハッシュ・警告へ直接到達します。対象の差分だけを更新し、健康状態への変更では警告だけを除いて対象数を維持し、譜面除去では所属も除きます。更新する警告は入力順に末尾へ追加し、未変更項目の順序と旧スナップショットを保ちます。全所属の複製、ハッシュ変更対象ごとの全キー探索、getterでの全件具体化はしません。

再利用できるスナップショットがない保守一覧の読取りは、初期化済み最小情報の読取りと保存行の読取り範囲内で対象を捕捉します。所持集合だけが構築済みでも同期を省略せず、カタログ書込み途中の状態を空一覧として確定しません。再利用できる場合に新しい待機は加えません。

変更なし、無効化、延期、差分、必要な全件構築を入力条件で選びます。全件入力の取得条件、版の確認、失敗時の非公開を守り、移転・パス整理で必要な無効化を局所最適化のために省略しません。

### 起動とIRデータ

導入準備までに必要な読取りは、パス・ハッシュ・時刻・フォルダ・所持関係などの最小カタログに限定します。全件の保守情報と譜面情報はこの必須経路へ戻しません。プレイリストのヘッダーとスコアを先に読み、項目は `playlist_entries_hydration` で反映します。

LR2ID確定後のプレイヤースコアXMLは、要求開始から本文受信まで一つの30秒期限を使います。終了要求は本文待機にも伝播します。同じプレイヤー・スコアDBへの先行取得は失敗結果も共有し、直後の再取得をしません。成功した空スコアと失敗を区別し、通信失敗・期限超過・取消では既存の `ir_score`、ハッシュ情報、現在のスコアを保持します。通常の結果型・ログ・背景処理の状態で伝え、新しい確認画面や再試行を加えません。

`ir_score` は未送信検出に使います。XMLのスコア実体から正規化ハッシュを作り、取得時刻で変動する `lastupdate` は除きます。`ir_data` はローカル順位XML由来のキャッシュであり、`UpdateLr2IrRankingCacheOnStartup=false` なら起動時に走査・読込み・更新をしません。

順位XMLは共通の一回走査の解析器を使い、`id`、`clear`、`notes`、`combo`、`pg`、`gr`、`minbp` が0以上の整数である行だけを集計します。ファイル時刻と末尾の `lastupdate` で再読込みを判定します。オフライン順位の計算器は必要時だけ作り、同じハッシュを起動時に読んでいれば再利用します。

初回の順位保存は、対象LR2IDの既存行がないことをトランザクション内で確認し、重複除去した行を一括挿入します。差分更新は `(hash, lr2id)` の削除・挿入を使います。互換性のため一意制約は加えず、`ir_data_idx(lr2id)` と非一意の `ir_data_idx_lr2id_hash(lr2id, hash)` を維持します。順位取得・反映は導入準備完了の待機条件にしません。

### DB読取り・スキーマ・削除

読取り用接続はスキーマを作成・修復しません。書込みを伴う整理・解析補完・ファイル差分反映は、書込み可能なトランザクションへ分けます。少数のパス・ハッシュの問合せを全表の具体化へ広げません。

`song` の `hashidx(hash)`、`parentidx(parent)`、アプリ側の `song_idx_folder(folder)` は通常のスキーマ準備でも不足を補います。`EnsureAppOwnedSchema()` はアプリ用の表・索引を現行化し、`app_schema_version(name='app_schema')` に `1` を記録します。既存のハッシュ行を保って修復する場合は `RepairAppOwnedSchema()` を使います。どちらも全譜面を読んで `chart_digest_map` を埋める処理ではありません。

初回連携で存在しないアプリ表を追加する場合は修復警告を出しません。既存の `playlist_entry` へのSHA-256列・索引追加、一意索引の作り直し、既存アプリデータの版記録・現行化は警告対象です。メタデータ配布用の `chart_info_schema_version` とDB内の `app_schema` は別の契約です。

「BeMusicSeeker関連データをLR2データベースから削除」は、`BeMusicSeekerOwnedTableNames` の表、対応する `sqlite_sequence` 行、ネイティブ表上のアプリ用索引を削除し、LR2の `song`、`folder`、`score` 等は残します。初期化・再読込み・背景更新中は受理せず、処理中は設定操作を無効化し、成功後に終了します。アプリ表・索引を増やす場合はこの一覧と回帰テストも更新します。

### パス整理とハッシュの所有関係

大量削除と局所の `PathCleanup` は、対象パス・ハッシュを一時表へ集め、`song`、`bmson_song`、`maintenance`、`chart_digest_map` を同じトランザクションで集合更新します。移転して残る保存主体の移動先は完全一致で保護し、対応する `song` がなくても対象の保守行を削除します。

BMSの削除前ハッシュは対象パスの一時表から主キーを引き、ハッシュ索引全体を走査しません。削除後に同MD5の `song` が残る場合はハッシュ情報を保持します。BMSONの整理はBMSONと保守の行に限定し、LR2行・ハッシュ対応表の保存主体を作りません。行ごとの孤児判定や全カタログの具体化に戻しません。

譜面情報の補完保存では、パスとMD5が一致する既存BMS行の `level`、`difficulty`、`maxbpm`、`minbpm`、`bga`、`exlevel`、`longnote`、`random`、`karinotes` だけを更新します。基本列・利用者管理列を再生成せず、欠落行を挿入しません。所持主体のない `chart_info` もメタデータキャッシュとして保持し、補完を理由に全削除しません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 保存行の順序、移転、一定差分の仕事量 | [`CatalogStorageRowsOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/CatalogStorageRowsOwner.cs)、[`CatalogStorageIndexedSequence`](../../../BeMusicSeeker/Models/BmsLibraryInternal/CatalogStorageIndexedSequence.cs)、[`CatalogMutationOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/CatalogMutationOwner.cs) | [`CatalogMutationOwnerTests`](../../../BeMusicSeeker.Tests/CatalogMutationOwnerTests.cs) |
| 所持集合の順序、完全一致参照、未構築索引 | [`OwnedChartCollectionState`](../../../BeMusicSeeker/Models/BmsLibraryInternal/OwnedChartCollectionState.cs)、[`LibraryChartRefIndexSnapshot`](../../../BeMusicSeeker/Models/BmsLibraryInternal/LibraryChartRefIndexSnapshot.cs) | [`OwnedChartCollectionLookupMembershipTests`](../../../BeMusicSeeker.Tests/OwnedChartCollectionLookupMembershipTests.cs)、[`OwnedChartCollectionReferenceIndexTests`](../../../BeMusicSeeker.Tests/OwnedChartCollectionReferenceIndexTests.cs) |
| 導入済み索引、最後の所持主体、旧スナップショット | [`InstalledChartLookupIndexState`](../../../BeMusicSeeker/Models/BmsLibraryInternal/InstalledChartLookupIndexSnapshot.cs)、[`CatalogOwnedCollectionOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/CatalogOwnedCollectionOwner.cs) | [`BmsLibraryInstallEstimationServiceTests`](../../../BeMusicSeeker.Tests/BmsLibraryInstallEstimationServiceTests.cs)、[`OwnedChartCollectionInstalledOverlayTests`](../../../BeMusicSeeker.Tests/OwnedChartCollectionInstalledOverlayTests.cs)、[`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/BmsLibraryPackageInstallServiceTests.cs) の `OverwritePendingInstalledOnlyPackagesResources_UsesWarmCatalogWithoutChartDelta`、`InstallChartPackagesAuto_UsesPreflightDestinationAndWarmDelta`、`InstallPendingPackagesToEstimatedDestinations_UsesPreflightDestinationAndWarmDelta`、`ForceInstallPendingPackages_UsesPreflightDestinationAndWarmDelta`。背景16件で全件列挙0件を確認し、譜面差分を伴う3経路ではprimary hash更新1〜8件、resource-only経路では既存snapshot維持を確認します。 |
| 所持ハッシュとプレイリスト代表の差分・通知順 | [`CatalogOwnedCollectionOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/CatalogOwnedCollectionOwner.cs)、[`PlaylistLibraryResolveIndexSnapshot`](../../../BeMusicSeeker/Models/BmsLibraryInternal/PlaylistLibraryResolveIndexSnapshot.cs) | [`PlaylistSummaryOwnedHashTests`](../../../BeMusicSeeker.Tests/PlaylistSummaryOwnedHashTests.cs)、[`PlaylistSummaryMutationAndWarmTests`](../../../BeMusicSeeker.Tests/PlaylistSummaryMutationAndWarmTests.cs)、[`PlaylistSummaryResolveIndexTests`](../../../BeMusicSeeker.Tests/PlaylistSummaryResolveIndexTests.cs)、[`OwnedChartCollectionInlineDigestTests`](../../../BeMusicSeeker.Tests/OwnedChartCollectionInlineDigestTests.cs) の `BuildInlineChartInfo_DispatchesDigestMutationToOwnedAdjacentIndexes`、`BuildInlineChartInfo_WarmDigestDeltaStaysLocalAcrossTwoOperations`、`BuildInlineChartInfo_ShaOnlyChangeUpdatesShaLookupAndResourceHealthWithoutPrimaryLookupRebuild`、[`BmsLibraryDuplicateServiceTests`](../../../BeMusicSeeker.Tests/BmsLibraryDuplicateServiceTests.cs) の `MergeChartDirectory_TwoWarmOperationsKeepIndexesCurrentWithoutFullRebuild`。二回の局所差分、旧snapshot、通知時点のMD5/SHA-256解決とSHAだけの更新を確認する。背景16件で全件列挙0件とprimary hash更新1〜8件を確認します。 |
| 参照索引と親フォルダ候補 | [`BmsLibraryParentFolderCacheService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryParentFolderCacheService.cs) | [`BmsLibraryParentFolderCacheServiceTests`](../../../BeMusicSeeker.Tests/BmsLibraryParentFolderCacheServiceTests.cs)、[`PlaylistViewPipelineTests`](../../../BeMusicSeeker.Tests/PlaylistViewPipelineTests.cs)、[`PlaylistWorkspaceDetailRefreshTests`](../../../BeMusicSeeker.Tests/PlaylistWorkspaceDetailRefreshTests.cs) |
| リソースの構造共有、候補順、変更なしの書込み抑制 | [`DirectoryResourceLookupCache`](../../../BeMusicSeeker/Models/BmsLibraryInternal/DirectoryResourceLookupCache.cs)、[`ResourceReverseLookupMap`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ResourceReverseLookupMap.cs)、[`DirectoryResourceEntryStore`](../../../BeMusicSeeker/Models/BmsLibraryInternal/DirectoryResourceEntryStore.cs) | [`DirectoryResourceLookupCacheTests`](../../../BeMusicSeeker.Tests/DirectoryResourceLookupCacheTests.cs)、[`ResourceReverseLookupMapTests`](../../../BeMusicSeeker.Tests/ResourceReverseLookupMapTests.cs) |
| 成功した変更だけの一括公開、失敗時の旧索引保持 | [`LibraryResourceIndexOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/LibraryResourceIndexOwner.cs)、[`LibraryMutationOwner`](../../../BeMusicSeeker/Models/BMSLibrary.LibraryMutationOwner.cs) | [`LibraryResourceIndexOwnerTests`](../../../BeMusicSeeker.Tests/LibraryResourceIndexOwnerTests.cs)、[`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/BmsLibraryPackageInstallServiceTests.cs)、[`OwnedChartCollectionLibraryMutationTests`](../../../BeMusicSeeker.Tests/OwnedChartCollectionLibraryMutationTests.cs) |
| 健全性の差分、警告順、未計算情報と書込みの同期 | [`ResourceHealthIndexSnapshot`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ResourceHealthWarningProjection.cs)、[`ResourceHealthIndexOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ResourceHealthIndexOwner.cs) | [`BmsLibraryMaintenanceServiceTests`](../../../BeMusicSeeker.Tests/BmsLibraryMaintenanceServiceTests.cs)、[`ResourceHealthIndexOwnerTests`](../../../BeMusicSeeker.Tests/ResourceHealthIndexOwnerTests.cs)、[`ResourceHealthFullOwnedTargetFreshnessTests`](../../../BeMusicSeeker.Tests/ResourceHealthFullOwnedTargetFreshnessTests.cs) |
| IR取得の単一期限・取消、失敗時の既存データ保持 | [`BmsLibraryIrService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryIrService.cs) | [`AppHttpClientTests`](../../../BeMusicSeeker.Tests/AppHttpClientTests.cs)、[`BmsLibraryIrServiceTests`](../../../BeMusicSeeker.Tests/BmsLibraryIrServiceTests.cs)、[`BmsLibraryIrStartupTests`](../../../BeMusicSeeker.Tests/BmsLibraryIrStartupTests.cs) |
| 局所パス整理・旧ハッシュ捕捉・残存所有者の保護 | [`BmsLibraryDbGateway`](../../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDbGateway.cs) | [`CatalogMutationOwnerTests`](../../../BeMusicSeeker.Tests/CatalogMutationOwnerTests.cs) の `ApplyCatalogMutation_PathCleanupUsesBoundedExactSetAndPreservesDigestOwnership`。実接続のPROFILEで `FULLSCAN_STEP` / `VM_STEP` を観測し、補助的に問合せ計画も確認します。 |

## 関連資料

[譜面の共通モデル](../library/chart-model.md)、[変更セッション](../library/mutations.md)、[起動](../runtime/startup.md)、[性能とデータ規模](performance-and-scale.md)。
