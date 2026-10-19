# データと索引

## 目的と適用範囲

保存データ、所持譜面集合、用途別の索引について、所有権・識別条件・更新・公開の境界を定めます。少数の変更に対する仕事量と全件処理の受入条件は、[性能とデータ規模](performance-and-scale.md)を正本とします。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

| 用語 | 意味 |
| --- | --- |
| 完全一致パス | 各保存・索引境界が持つ正規化済みのキー。大小文字の比較規則を別の曖昧な同一性へ広げません。詳細は[パスの識別規則](path-identity.md)を参照します。 |
| 所属数 | 同じハッシュやディレクトリに対応する所持項目の数。候補の有無だけとは区別します。 |
| 構造共有 | 変更のない部分を旧スナップショットと共有し、変更箇所への経路だけを置き換えること。 |

## 仕様

### 保存データの責務

| データ | 用途と制約 |
| --- | --- |
| `song` / `folder` | LR2互換のBMSカタログ。保存行型はDB境界に限定し、アプリの現在値は `ChartFile` です。 |
| `bmson_song` | アプリ独自のBMSONカタログ。LR2の `song` に互換行を作りません。 |
| `chart_digest_map` | BMSのMD5とSHA-256・譜面情報を結ぶ部分的なキャッシュ。初回走査前に完全である必要はありません。実ファイルを読む処理で必要な範囲を補います。 |
| `chart_info` / 解析失敗の記録 | 解析版とファイルの状態に応じた譜面情報。読込み後はセッション索引に反映します。LR2同期の状態を鮮度判定に流用しません。 |
| `maintenance` | 保存済みのリソース健全性とBMS固有の文字コード情報。未計算状態とは区別します。 |
| プレイリストのヘッダー・項目 | 操作と保存・出力の正本。所持判定には項目の選択キーを使います。 |
| `ir_score` / `ir_data` | LR2IRのプレイヤースコアと順位キャッシュ。異なる取得元・更新条件を持ちます。 |

`ChartFile` はこれらのDB行ではなく、用途別の共通読取りモデルです。所持項目と保存行の関係は[譜面の共通モデル](../library/chart-model.md)を参照します。

### 共通現在値の格納・識別・順序

`OwnedChartCollectionState` はパスとMD5のある共通値を採用し、形式をまたぐ同じDB exact pathではBMSを優先します。同MD5の別配置は残し、除外した重複を先の項目の削除後に自動昇格させません。DBの `Ordinal` と物理対象の `OrdinalIgnoreCase` を混同しません。

`CatalogOwnedCollectionOwner` が唯一の現在値集合・排他・集合版・派生索引を所有します。`CatalogStorageIndexedSequence` の変更不能なルートを共有し、tokenから既存sequence entryへ到達します。パス完全一致索引と派生参照索引から対象を直接探し、順序キーの二分探索で位置を求めます。捕捉済みビューは所属・順序・版に加えて要素の値も固定し、後の現在値適用・移転で変わりません。形式別ビューを捕捉するために全件を複製しません。

通常一覧の添字再利用には、所属・基準順序の変更を示す `MembershipOrderIdentity` を捕捉値へ渡します。加入・離脱・順序の正規化・全置換で識別を替え、同じ位置への基本値更新と内部移転では維持します。現在値の鮮度を示すカタログ版とは用途を分け、同件数の別所属へ旧添字を流用しません。

形式別範囲ビューの列挙・検索・コピーは、その範囲だけを読みます。先頭開始は通常の列挙器を使い、途中開始は上限256件の局所バッファ一つを再利用して `ImmutableList.CopyTo` で順次取得します。範囲外の前置項目と末端項目を訪問せず、全列挙では必要な各項目を一回取得します。早期終了時の範囲内先読みも `ObserveEntryVisit` に含め、indexer取得の `ObserveAccess`、開始の `ObserveEnumeration`、明示結果具体化の `ObserveMaterialization` と区別します。明示具体化0件でも、この一時バッファは存在します。

通常一覧の基準入力はBMS、BMSONの順にカタログの保持列を結び付けた捕捉値です。通常一覧だけのBMSONのpath順整列は行わず、指定列と題名が同値の場合もこの保持順を使います。指定列の並べ替えでは現在の値を評価し、プレイリストの代表選択は既存のカタログ順序に従います。呼出側の `includeBmsonRows` は捕捉前に渡し、BMS専用一覧ではBMSONを列挙しません。

| 更新 | 識別と順序 |
| --- | --- |
| DB全再読込み | 境界で一回共通値へ変換し、新しいtokenを発行します。入力の相対順と従来の除外条件を維持します。 |
| BMS追加・再解析置換 | 新しいtokenを発行し、旧exact pathを除いて入力順にBMS末尾、BMSONの前へ置きます。 |
| BMSON追加・再解析置換 | 新しいtokenを発行し、捕捉したpathを大小文字を区別せず並べ、置換は同順位の末尾へ移します。 |
| 通常値の適用・詳細由来の基本値更新 | 同じtokenと位置へ新しい不変値を一回適用します。旧捕捉値は変更しません。 |
| 内部移転 | 同じtokenと格納位置を継承し、明示的な旧新pathで現在値・索引を更新します。旧キーを新pathから推測しません。 |
| 差分変更なし | 現在値とtokenを継承します。外部移転のhash再接続は利用者列だけを復元し、tokenは継承しません。 |

全置換後の最初の追加・更新ではBMSON部分だけを一度整列します。順序キーが変わる生存tokenを、値の変更とは別に同じ確定結果へ含めます。BMSの列・順序キー・索引を共有し、全BMSを複製・再採番しません。移転は捕捉済みの順序位置を保ち、画面の並べ替えには共通現在値のpathを使います。`LibraryChartRefIndexSnapshot` の局所更新でも、全譜面の順位表を作りません。

`CatalogRevision` は所属・識別・順序と、health・encoding・警告・検査の由来を含む所持現在値の実変更で進めます。一回の確定batchで一回進み、対象なし・同じtokenの同値再評価では進みません。tokenを再発行する全再読込みは識別の実変更です。明示的にtokenを継承する同値入力とは区別します。スコアなど独立した正本の版は維持します。

### 共通変更と派生索引

`CatalogOwnedCollectionOwner` が既存writer内で現在値、必要な候補・ハッシュ索引、版と `CatalogCommitReceipt` を同時に確定します。receiptは `BaseRevision`、`Revision`、対象のbefore/after、値とは独立した `OrderChangedTokens`、全置換の区別と確定時の構造共有snapshotを持ちます。順序変更も同じ一回の確定へ含め、値変更を偽造しません。同じ短い集合排他内で既存publisherへ登録し、排他解放後に配送を起動します。`LibraryMutationOwner` は操作の成功集合と必須後処理をまとめます。

通常の変更では旧path/hashを不変の変更事実として捕捉し、DB確定後だけ共通現在値へ一回適用します。構築済み索引へ同じ旧新事実を渡し、排他権を解放してから登録済みの同じreceiptを配送します。未構築索引は変更のためだけに構築しません。全置換、必要な旧新対応の不足、途中失敗では従来どおり必要な無効化を行い、識別条件違反を無効化で隠しません。

共通値と集合版は同じ既存排他で捕捉・適用します。DB失敗・確定前取消しで現在値を先行変更しません。詳細書込みもDB確定、同tokenへの必要な基本値更新、ハッシュ依存索引、詳細セッション索引、解放後通知の順に行います。[譜面情報](../library/chart-info.md)を参照します。

| 索引・表示状態 | 更新方針 |
| --- | --- |
| MD5の所持数 | 追加・削除・MD5変更を集約します。同MD5の置換やSHA-256だけの変更は相殺し、不要な更新をしません。 |
| 導入済みディレクトリ | MD5・SHA-256ごとの所属数、既知ディレクトリを対象だけ更新します。 |
| 実パスの参照・子孫数 | 実際の所属・移転を反映します。予定の導入先を混ぜません。 |
| 導入先の一時状態 | 導入先変更と元の所持主体・パッケージ項目の消失を反映します。実ファイルの存在や子孫数の根拠にはしません。 |
| 親フォルダ候補 | 現行キャッシュを無効化します。捕捉済みパスを再利用し、出力先の正規化は一回の構築につき一度行います。 |
| プレイリストの所持ハッシュ・参照解決 | 構築済み索引へtoken・path・digest・候補順の実変更だけを局所反映します。表示値は保持せず、必要な候補と現在値を共通読取り区間で捕捉します。 |
| プレイリスト参照の表示更新 | 選択キーに一致する所持参照だけをその都度取り出します。全参照一覧を複製してから絞り込みません。 |
| リソース健全性 | 対象の差分を優先し、入力不足時は無効化・延期・必要な全件処理を選びます。 |
| 重複行・グループ | 共通現在値の捕捉sequenceとハッシュ別の不変派生索引を共有します。構築済みの索引は変更したハッシュの要素だけを更新し、グループ結果を無効化します。局所現在値更新のために全件を走査・複製しません。 |
| 通常一覧 | 確定した同じカタログsnapshotと対象差分を渡します。鮮度と更新範囲を分け、版の変化だけで全体を再構築しません。 |

通常通知は `NormalLibraryRefreshNotificationVersion` を入口にし、必要な更新契機・変更範囲を渡します。通常一覧の所属・現在値はreceiptに含む同じ確定時のカタログsnapshotを入力とし、過去の追加・削除通知を再生して所属を再現する独立集合は持ちません。差分は実体化済み行や依存する順序の局所更新に使い、次の明示表示要求も現在のカタログから構築できます。重複警告の直接更新も、確定済みreceiptを既存キューへ登録し、全排他解放後に同じ通知入口を起動します。確定後の処理・通知先の失敗でreceiptを削除しません。未公開の補助通知だけの破棄は、確定receiptを含まない通知に限定します。通知不達の常時監視や自動再試行は行いません。

`NormalLibraryRowCache` は所持tokenをキーに表示行を再利用し、所属の根拠にはしません。詳細は既存の詳細版と変更MD5/SHA-256、スコアは既存の版と変更キー、保守・警告は既存通知と対象、Packageは `ProjectionVersion` とdeferralで更新します。明示的な全置換だけをReset境界とし、局所変更で全sourceのReset、全catalog走査、全件投影・リソース解析を行いません。


scoreの既存lookupは可変 `BMSScore` を共有し、全score値の不変snapshotとは扱いません。局所setterと全lookup再公開は `scoreSubscriptionGate → lockScoreSnapshot` の順で、新lookupの公開前に必要な購読を接続し、既存 `ScoreSnapshotVersion` と `ScoreSnapshotChange(BaseVersion, Version, Md5Keys, Sha256Keys)` の通常queue登録を同じ排他内で確定します。全再公開の対象は旧新keyのunionで、空keyの版進行も結果として保持します。純通知は解放後に通常のwakeを起動し、直接score通知の別配送経路を置きません。catalog resetの整理と未公開legacy通知の破棄は、独立score rangeを削除しません。

score版は表示へ反映済みの位置です。可変lookupから最新値を読めても、その版までの全変更keyを反映していないsourceを最新版として採用しません。modelの実setter・全lookup公開・source切替・空key・reset前後と16/128件のlookup/複写仕事量は `ScoreSnapshotPublicationTests`、購読差替えと終了解除・純通知失敗後の成績保持は `BmsLibraryIrStartupTests`、`StartupRequiredInitializationTests`、`BmsLibrarySavedScoreAvailabilityTests`、`BmsLibraryIrServiceTests` が確認します。通常一覧とworkspaceの反映済み位置・待機は[一覧](../ui/table-view.md)へ従います。

導入済み項目の参照索引は `PackageLifecycleOwner` が既存の所属ロック内で管理します。tokenに対応するlive entry参照と所属package参照集合だけを持ち、共通現在値・保存行・独立版・永続識別を追加しません。確定値の局所反映では対応entryだけを訪問し、全導入済み列の走査・entry複製・lazy構築をしません。DB確定、共通現在値への一回適用、既存索引反映の後で、同じ確定receiptのAfterを対応entryへ共有します。移転の不完全なpath要求や後続currentの再捕捉で補いません。項目とモデルの排他を解放してから既存の通知を公開します。

#### 変更事実から索引・表示への反映

実線はラベルに示す変更情報の受渡し、破線は公開前の順序条件です。操作の変更事実はDB確定後に `CatalogOwnedCollectionOwner` へ適用し、同じ境界で現在値・版・旧新差分を確定します。構築済み索引と専門依存の更新はその旧新差分に従い、保存行や所持集合の変更通知から再推測しません。索引反映と無効化の枝は変更に応じて必要な側だけを扱い、両方が対象になる場合もあります。通知への破線の合流は、その変更で対象になった内部反映・無効化の完了を表し、全枝を毎回実行するAND条件ではありません。

```mermaid
flowchart TB
    Facts["確定した旧新の変更事実"] -->|操作単位で集約| Owner["CatalogOwnedCollectionOwner：現在値・版・不変結果の確定"]
    Owner -->|正本更新| Catalog["DB確定後の共通現在値集合"]
    Owner -->|同じ確定事実を差分反映| Indexes["構築済みの派生索引"]
    Owner -->|必要な無効化・延期| Lazy["未構築・失効中の索引と専門キャッシュ"]
    Owner -->|通知対象を決定| Notifications["登録済みの不変結果"]
    Catalog -.-> Notifications
    Indexes -.-> Notifications
    Lazy -.-> Notifications
    Notifications -->|排他権解放後に公開| View["読取りモデル・一覧表示"]
```

導入要求の再検査は、要求対象の完全一致pathにいたtokenとdigestだけを比較します。無関係なTitle・healthの確定で要求を拒否しません。LR2通常folderの件数・実パス捕捉は既存のBMS path所属rootを共有し、所属の変化と表示値の変化を区別します。

未構築の索引を変更のためだけに構築しません。カタログ版が進むことと、高価な派生索引を更新することは別の判断です。譜面情報の書込み順序は[譜面情報](../library/chart-info.md#保存する列と公開順序)を参照します。

### ハッシュと導入済みディレクトリ

索引の状態、ロック、初期化状態、世代、読取り・構築・反映は `CatalogOwnedCollectionOwner` が所有します。BMSLibraryの公開窓口や利用側に同じ世代・索引の正本を重複して置きません。

`PrimaryHashLookupState` はMD5の件数だけを扱います。既所持判定や安全な削除のために、全ディレクトリ索引や詳細譜面を作りません。`InstalledChartLookupIndexState` はMD5・SHA-256からディレクトリへの所属数、ディレクトリ内の異なるMD5数、既知ディレクトリを持ちます。同じハッシュを持つ最後の所持項目が消えるまで候補を保持します。

所属が変わったハッシュだけを、大文字小文字を区別しない候補順で更新します。スナップショットと除外付きの検索結果は変更不能なルートを共有し、後続変更で変わりません。作成時の全map複製や全候補の再整列をしません。`DirectoryReferenceCount` は更新済みの所属数を返します。初回構築と、呼出側が全既知ディレクトリを明示列挙する仕事は残ります。

通常削除と統合は、種類・完全一致パス・変更前MD5・SHA-256を同じ確定事実としてカタログと索引へ渡します。`PathCleanup` を理由に既知の旧ハッシュを捨てません。DBだけの整理と所持主体の除去を区別し、元に所持譜面のない統合は検索索引の取得前に終了します。

### プレイリストの所持・解決索引

プレイリスト参照解決索引は `CatalogOwnedCollectionOwner` が所有し、候補のtoken・種類・完全一致path・digest・順序だけを保持します。`ChartFile` の表示値を索引へ複写しません。`CapturePlaylistCatalogReadSnapshot` は候補解決と必要tokenの現在値・カタログ版を同じ読取り区間で捕捉し、退役tokenを同pathの別項目へ救済しません。内容だけの更新は候補rootの内容版を維持します。確定receiptのsnapshotは、その時点でwarmな既存候補rootもO(1)で共有します。`TryCapturePlaylistReadSnapshot` はそのrootと同じsnapshotのtoken現在値だけを読み、後続の候補・現在値を混ぜません。coldな索引は変更のために構築せず、明示全置換で破棄した索引は次の全構築で取得します。

確定後の候補変更と後続currentの混在防止、cold・全置換の非構築は `CatalogMaintenanceCurrentValueTests.ReceiptSnapshot_ResolvesCapturedCandidateAfterLaterCommit` と `CurrentCommit_DoesNotBuildColdIndexesAndTracksOnlyAnalysisInputs` が確認します。重複警告の配送・通知先失敗は `BmsLibraryDuplicateServiceTests.SearchDuplicateChartGroups_PublishesCommittedWarningsAfterLocksRelease`、確定後例外と補助通知削除は `CatalogMaintenanceCurrentValueTests.CommitQueue_KeptAfterApplyFailureAndLegacyNotificationClear` が確認します。

所持ハッシュ索引はMD5・SHA-256それぞれの所持項目数を持ち、最後の項目が消えた場合にだけ所属を除きます。両ハッシュ集合が同じなら内容の `Version` を維持し、所持数集計を再利用します。共通集合版はハッシュ所属内容の版と別に管理します。

参照解決索引は、種類と完全一致パスで全候補を保持します。代表は現在パスの大文字小文字を区別しない最小値、同値なら所持集合で先のものです。代表の削除で次候補を選び、最後の候補がなくなると未解決にします。項目のMD5がある場合はMD5だけで解決し、未解決でもSHA-256へ切り替えません。

構築済み索引は、導入・移転・削除・ハッシュ変更と、基準順序が変わる対象候補だけを更新します。初回BMSON順序正規化も、実際に位置が変わるtokenと、移転前の捕捉pathを更新する必要がある候補だけを同じwriter内で更新してからsnapshotを捕捉します。既存Ordinalを保持し、キーの比較modeの表現差だけで無関係な候補や表示行を更新しません。receiptには実際の位置変更だけを含め、候補キーだけの再捕捉対象は確定中の一時入力に限ります。候補の同path比較は両キーへ共通のmodeを適用し、旧新キーが混在しても比較の対称性と新規構築した候補順を保ちます。receipt登録後に索引だけを無効化しません。旧スナップショットのパス・ハッシュ・代表は固定します。全置換と旧事実不足では必要な全無効化を維持します。順序だけの変更は、所持hash集合の内容版や詳細解析入力を失効させません。

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

#### 祖先から見える集合と自身の所有分

次は `A` と `A/B` にそれぞれ譜面がある例です。実線はディレクトリ包含、破線はリソースの見え方を表します。ファイル名やパスは説明用であり、特定の配置を必須にしません。

```mermaid
flowchart TB
    A["A：譜面ディレクトリ"] --> Sound["A/sound：リソースだけのフォルダ"]
    Sound --> Kick["A/sound/kick.wav"]
    A --> B["A/B：譜面ディレクトリ"]
    B --> Snare["A/B/snare.wav"]
    Kick -.->|相対キー sound/kick| OwnA["Aから見える・AのSelfOwned"]
    Snare -.->|相対キー B/snare| VisibleA["Aから見えるがAのSelfOwnedではない"]
    Snare -.->|相対キー snare| OwnB["Bから見える・BのSelfOwned"]
```

自身の所有者は最も近い譜面ディレクトリです。`A/sound` はリソースだけなので導入候補にはなりません。候補の抑制にこの区別を使う条件は[導入先推定](../library/install-estimation.md#祖先候補の抑制)に従います。

### リソース健全性

存在件数は取得済みの `ChartFile.Resources` の使用参照だけから索引化した検索集合で計算します。同じ種別の `sound.wav` と `sound.ogg` は検索・分母では1件、元記述は2件保持します。`foo.v2.wav` の `LookupKey` は `foo.v2` のまま使い、集約や照合で再度拡張子を除去しません。任意画像は通常のBGAの分母へ入れず、stagefile・backbmp・bannerの用途別に計算します。両形式とも補助画像の空白でない元記述を定義ありとし、不正パス・親参照などで解決できなければ存在falseとして補助画像不足を通知します。未指定・null・空文字は対象外です。

WAV/BGA/MOVIEの健康度は使用ファイルキーの存在数／要求数です。平方根によるペナルティは加えず、不足が一件でもあれば整数表示を100%にしません。要求0件は対象外としてnullを返し、画面は `-` を表示します。新しく計算した0件は既存maintenance列へ0/0を保存し、旧保存値の定義0・存在nullも検査済みとして扱います。健康度のnullだけで検査済みかを判断しません。

定義済みの使用参照が不正パスや空キーで照合不能なら、該当カテゴリの件数をnull/nullにして未判定を表します。未定義IDは要求へ加えません。補助画像3役割の検査がすべて完了し、各カテゴリの件数が取得済み（旧0/nullを含む）またはnull/nullなら、リソース評価を一度終えた状態として扱います。片側だけ欠落した件数や補助検査の未取得は検査済みにしません。既存列から取得状態を導き、追加の永続状態や一時的な取得済みフラグを持ちません。

この形の未解決結果はDB読込み後も起動時の再解析対象にしません。同じ保存形の古い部分データは区別できないため、自動補完せず手動再スキャンで更新します。補助検査の未取得や不完全な件数を持つ従来の補完対象は維持します。明示された未判定の投影を旧100%で補いません。既存DBの件数は手動再スキャンまで旧基準を含んでよく、スキーマ変更・移行・読込み時失効や、基準変更を理由とする起動時再計算は追加しません。

同じディレクトリと種別・用途別の検索集合で共有するのは存在件数です。定義の有無は用途別にキーと区別し、未知拡張子・空キーから親参照理由を作りません。未取得の表示用保守情報は件数未確定のまま扱い、健全性の計算やファイル取得を開始しません。LR2の長さ・文字コード・親参照は各譜面の全元記述から毎回評価し、存在件数キャッシュに依存しません。原文と解析状態の正本は[共通譜面](../library/chart-model.md#譜面情報とリソース保守)です。

有効な保守情報はDBから反映した `DbHydrated` または計算済みの `Calculated` です。未計算の仮値を健全性索引の正本にしません。通常起動では保存済み情報を読み、欠落・古い対象だけを後続保守へ回します。

構築済みの `ResourceHealthIndexSnapshot` は、種類と完全一致パスから対象参照・ハッシュ・警告の検査事実へ直接到達します。対象にfull `ChartFile` を保持せず、表示と操作対象の現在値は共通捕捉口から取得します。鮮度は実資源入力の `InputVersion` で守り、カタログ版を二重保持しません。無関係なTitle等の確定で正常な資源差分を捨てません。対象の差分だけを更新し、健康状態への変更では警告だけを除いて対象数を維持し、譜面除去では所属も除きます。更新する警告は入力順に末尾へ追加し、未変更項目の順序と旧スナップショットを保ちます。全所属の複製、ハッシュ変更対象ごとの全キー探索、getterでの全件具体化はしません。

再利用できるスナップショットがない保守一覧の読取りは、初期化済み最小情報の読取りと共通現在値の読取り範囲内で対象を捕捉します。カタログ書込み途中の状態を空一覧として確定しません。再利用できる場合に新しい待機は加えません。

変更なし、無効化、延期、差分、必要な全件構築を入力条件で選びます。全件入力の取得条件、版の確認、失敗時の非公開を守り、移転・パス整理で必要な無効化を局所最適化のために省略しません。

`DeltaOnUpdates` は現在の索引へ差分を適用し、未構築・失効中や差分を適用できない場合は全件構築せず失効させます。`DeferOnUpdates` も更新中の構築を行いません。その後の表示準備が現在の索引を構築し、同じ入力に対する再表示は再利用します。構築済み索引への導入を不必要に失効させないための反映順序は[変更仕様](../library/mutations.md#操作ごとの反映範囲)に従います。

### 起動とIRデータ

導入準備までに必要な読取りは、パス・ハッシュ・時刻・フォルダ・所持関係などの最小カタログに限定します。全件の保守情報と譜面情報はこの必須経路へ戻しません。プレイリストのヘッダーを独立に読み、項目は最小カタログと必要な譜面情報・スコアの準備後に同じ親操作で直接反映して、保存済み出力を完了します。

LR2ID確定後のプレイヤースコアXMLは、要求開始から本文受信まで一つの30秒期限を使います。終了要求は本文待機にも伝播します。取得は必須UI・操作解禁後の順位work内で一回行い、その結果を同じ実行へ直接渡します。共有prefetch状態や失敗時の直後再取得を持ちません。通信・parse・cache読取りはLを持たず、DB更新とモデル確定だけL取得後の停止・入力一致確認を経て行います。成功した空スコアと失敗を区別し、通信失敗・期限超過・取消では既存の `ir_score`、ハッシュ情報、現在のスコアを保持します。通常の結果型・ログ・背景処理の状態で伝え、新しい確認画面や再試行を加えません。

`ir_score` は未送信検出に使います。XMLのスコア実体から正規化ハッシュを作り、取得時刻で変動する `lastupdate` は除きます。`ir_data` はローカル順位XML由来のキャッシュであり、`UpdateLr2IrRankingCacheOnStartup=false` なら起動時に走査・読込み・更新をしません。

順位XMLは共通の一回走査の解析器を使い、`id`、`clear`、`notes`、`combo`、`pg`、`gr`、`minbp` が0以上の整数である行だけを集計します。ファイル時刻と末尾の `lastupdate` で再読込みを判定します。オフライン順位の計算器は必要時だけ作り、同じハッシュを起動時に読んでいれば再利用します。

初回の順位保存は、対象LR2IDの既存行がないことをトランザクション内で確認し、重複除去した行を一括挿入します。差分更新は `(hash, lr2id)` の削除・挿入を使います。互換性のため一意制約は加えず、`ir_data_idx(lr2id)` と非一意の `ir_data_idx_lr2id_hash(lr2id, hash)` を維持します。順位取得・反映は導入準備完了の待機条件にしません。

### DB読取り・スキーマ・削除

読取り用接続はスキーマを作成・修復しません。書込みを伴う整理・解析補完・ファイル差分反映は、書込み可能なトランザクションへ分けます。少数のパス・ハッシュの問合せを全表の具体化へ広げません。

`song` の `hashidx(hash)`、`parentidx(parent)`、アプリ側の `song_idx_folder(folder)` は通常のスキーマ準備でも不足を補います。`EnsureAppOwnedSchema()` はアプリ用の表・索引を現行化し、`app_schema_version(name='app_schema')` に `1` を記録します。既存のハッシュ行を保って修復する場合は `RepairAppOwnedSchema()` を使います。どちらも全譜面を読んで `chart_digest_map` を埋める処理ではありません。

`EnsureLibraryStartupSchema()` は、起動に必要な既存の表・索引・版を一つのトランザクションで準備します。DDLごとの確定を繰り返さず、途中失敗では今回の構造・版変更を巻き戻し、既存データと元の失敗を保持します。阻害条件を除いた次の明示実行で準備でき、成功後の繰返しも既存行を保持します。スキーマの定義や版、他のスキーマ操作の確定境界は変更しません。

初回連携で存在しないアプリ表を追加する場合は修復警告を出しません。既存の `playlist_entry` へのSHA-256列・索引追加、一意索引の作り直し、既存アプリデータの版記録・現行化は警告対象です。メタデータ配布用の `chart_info_schema_version` とDB内の `app_schema` は別の契約です。

「BeMusicSeeker関連データをLR2データベースから削除」は、`BeMusicSeekerOwnedTableNames` の表、対応する `sqlite_sequence` 行、ネイティブ表上のアプリ用索引を削除し、LR2の `song`、`folder`、`score` 等は残します。初期化・再読込み・背景更新中は受理せず、処理中は設定操作を無効化し、成功後に終了します。アプリ表・索引を増やす場合はこの一覧と回帰テストも更新します。

### パス整理とハッシュの所有関係

大量削除と局所の `PathCleanup` は、対象パス・ハッシュを一時表へ集め、`song`、`bmson_song`、`maintenance`、`chart_digest_map` を同じトランザクションで集合更新します。移転して残る保存主体の移動先は完全一致で保護し、対応する `song` がなくても対象の保守行を削除します。

BMSの削除前ハッシュは対象パスの一時表から主キーを引き、ハッシュ索引全体を走査しません。削除後に同MD5の `song` が残る場合はハッシュ情報を保持します。BMSONの整理はBMSONと保守の行に限定し、LR2行・ハッシュ対応表の保存主体を作りません。行ごとの孤児判定や全カタログの具体化に戻しません。

譜面情報の補完保存では、パスとMD5が一致する既存BMS行の `level`、`difficulty`、`maxbpm`、`minbpm`、`bga`、`exlevel`、`longnote`、`random`、`karinotes` だけを更新します。基本列・利用者管理列を再生成せず、欠落行を挿入しません。所持主体のない `chart_info` もメタデータキャッシュとして保持し、補完を理由に全削除しません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 全現在値の確定版、同値、確定順、旧snapshotと局所仕事量 | [`CatalogOwnedCollectionOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogOwnedCollectionOwner.Commit.cs) | [`CatalogMaintenanceCurrentValueTests`](../../../BeMusicSeeker.Tests/Catalog/CatalogMaintenanceCurrentValueTests.cs) は共通writer・実キューで旧新値・退役と背景16/128件を確認する。実DBの保守・encodingは [`BmsLibraryMaintenanceServiceTests`](../../../BeMusicSeeker.Tests/Maintenance/BmsLibraryMaintenanceServiceTests.cs) が担う。 |
| 初回BMSON順序の確定とwarm候補の整合 | [`CatalogOwnedCollectionOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogOwnedCollectionOwner.Commit.cs)、[`OwnedChartCollectionState`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/OwnedChartCollectionState.cs) | [`CatalogMaintenanceCurrentValueTests.FirstUpsert_CommitsExistingBmsonOrderAndWarmCandidatesTogether`](../../../BeMusicSeeker.Tests/Catalog/CatalogMaintenanceCurrentValueTests.cs) は背景16/128件で順序差分、同じ確定候補、旧snapshot不変、次の通常upsertの局所性を確認する。同じクラスの整列済み・単一BMSONでは既存候補のremove/addなしを実処理地点で確認し、逆順後の位置不変キーと後続の大小文字を区別した移転、位置不変のcaptured path再捕捉は保持済み索引と新規構築の全候補順で照合する。表示順と選択への接続は [`RegularChartNormalLibraryRefreshTests`](../../../BeMusicSeeker.Tests/ChartList/RegularChartNormalLibraryRefreshTests.cs) が担う。 |
| 保存行の順序、移転、一定差分の仕事量 | [`CatalogOwnedCollectionOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogOwnedCollectionOwner.cs)、[`CatalogStorageIndexedSequence`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogStorageIndexedSequence.cs)、[`CatalogMutationOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogMutationOwner.cs) | [`CatalogMutationOwnerTests`](../../../BeMusicSeeker.Tests/Catalog/CatalogMutationOwnerTests.cs) |
| 範囲だけの取得と通常一覧の形式選択・捕捉順 | [`CatalogStorageIndexedSequence`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogStorageIndexedSequence.cs)、[`OwnedChartCollectionState`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/OwnedChartCollectionState.cs)、[`RegularChartListOwner`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs) | [`CatalogMutationOwnerTests`](../../../BeMusicSeeker.Tests/Catalog/CatalogMutationOwnerTests.cs) の `FormatRangeOperations_VisitOnlyRequestedBmsonEntriesRegardlessOfBmsBackground`、`RangeEnumerationAndCapture_PreserveBoundsOrderAndCapturedValues`、`NormalSourceCaptureAndProjection_VisitEachIncludedEntryOnce`。BMSON64件・BMS背景16/128件で範囲内訪問、相対位置、明示コピー、捕捉前から本番行生成までの仕事量を確認します。[`OwnedChartCollectionProjectionTests`](../../../BeMusicSeeker.Tests/Catalog/OwnedChartCollectionProjectionTests.cs) の `CaptureSnapshot_PreservesCatalogOrderAndExcludesPathlessRows` が形式別の捕捉順、内部移転後の位置・token継承と旧捕捉値を確認します。 |
| 所持集合の順序、完全一致参照、未構築索引 | [`OwnedChartCollectionState`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/OwnedChartCollectionState.cs)、[`LibraryChartRefIndexSnapshot`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/LibraryChartRefIndexSnapshot.cs) | [`OwnedChartCollectionLookupMembershipTests`](../../../BeMusicSeeker.Tests/Catalog/OwnedChartCollectionLookupMembershipTests.cs)、[`OwnedChartCollectionReferenceIndexTests`](../../../BeMusicSeeker.Tests/Catalog/OwnedChartCollectionReferenceIndexTests.cs) |
| 導入済み索引、最後の所持主体、旧スナップショット | [`InstalledChartLookupIndexState`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/InstalledChartLookupIndexSnapshot.cs)、[`CatalogOwnedCollectionOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogOwnedCollectionOwner.cs) | [`BmsLibraryInstallEstimationServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryInstallEstimationServiceTests.cs)、[`OwnedChartCollectionInstalledOverlayTests`](../../../BeMusicSeeker.Tests/Catalog/OwnedChartCollectionInstalledOverlayTests.cs)、[`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) の `OverwritePendingInstalledOnlyPackagesResources_UsesWarmCatalogWithoutChartDelta`、`InstallChartPackagesAuto_UsesPreflightDestinationAndWarmDelta`、`InstallPendingPackagesToEstimatedDestinations_UsesPreflightDestinationAndWarmDelta`、`ForceInstallPendingPackages_UsesPreflightDestinationAndWarmDelta`。背景16件で全件列挙0件を確認し、譜面差分を伴う3経路ではprimary hash更新1〜8件、resource-only経路では既存snapshot維持を確認します。 |
| 所持ハッシュとプレイリスト代表の差分・通知順 | [`CatalogOwnedCollectionOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogOwnedCollectionOwner.cs)、[`PlaylistLibraryResolveIndexSnapshot`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistLibraryResolveIndexSnapshot.cs) | [`PlaylistSummaryOwnedHashTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryOwnedHashTests.cs)、[`PlaylistSummaryMutationAndWarmTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryMutationAndWarmTests.cs)、[`PlaylistSummaryResolveIndexTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryResolveIndexTests.cs)、[`OwnedChartCollectionInlineDigestTests`](../../../BeMusicSeeker.Tests/Catalog/OwnedChartCollectionInlineDigestTests.cs) の `BuildInlineChartInfo_DispatchesDigestMutationToOwnedAdjacentIndexes`、`BuildInlineChartInfo_WarmDigestDeltaStaysLocalAcrossTwoOperations`、`BuildInlineChartInfo_ShaOnlyChangeUpdatesShaLookupAndResourceHealthWithoutPrimaryLookupRebuild`、[`BmsLibraryDuplicateServiceTests`](../../../BeMusicSeeker.Tests/Maintenance/BmsLibraryDuplicateServiceTests.cs) の `MergeChartDirectory_TwoWarmOperationsKeepIndexesCurrentWithoutFullRebuild`。二回の局所差分、旧snapshot、通知時点のMD5/SHA-256解決とSHAだけの更新を確認する。背景16件で全件列挙0件とprimary hash更新1〜8件を確認します。 |
| 参照索引と親フォルダ候補 | [`BmsLibraryParentFolderCacheService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/BmsLibraryParentFolderCacheService.cs) | [`BmsLibraryParentFolderCacheServiceTests`](../../../BeMusicSeeker.Tests/Catalog/BmsLibraryParentFolderCacheServiceTests.cs)、[`PlaylistViewPipelineTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistViewPipelineTests.cs)、[`PlaylistWorkspaceDetailRefreshTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceDetailRefreshTests.cs) |
| リソースの構造共有、候補順、変更なしの書込み抑制 | [`DirectoryResourceLookupCache`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Resources/DirectoryResourceLookupCache.cs)、[`ResourceReverseLookupMap`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Resources/ResourceReverseLookupMap.cs)、[`DirectoryResourceEntryStore`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Resources/DirectoryResourceEntryStore.cs) | [`DirectoryResourceLookupCacheTests`](../../../BeMusicSeeker.Tests/Resources/DirectoryResourceLookupCacheTests.cs)、[`ResourceReverseLookupMapTests`](../../../BeMusicSeeker.Tests/Resources/ResourceReverseLookupMapTests.cs) |
| 成功した変更だけの一括公開、失敗時の旧索引保持 | [`LibraryResourceIndexOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Resources/LibraryResourceIndexOwner.cs)、[`LibraryMutationOwner`](../../../BeMusicSeeker/Models/Library/BMSLibrary.LibraryMutationOwner.cs) | [`LibraryResourceIndexOwnerTests`](../../../BeMusicSeeker.Tests/Resources/LibraryResourceIndexOwnerTests.cs)、[`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs)、[`OwnedChartCollectionLibraryMutationTests`](../../../BeMusicSeeker.Tests/Catalog/OwnedChartCollectionLibraryMutationTests.cs) |
| 健全性の差分、警告順、未計算情報と書込みの同期 | [`ResourceHealthIndexSnapshot`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ResourceHealth/ResourceHealthWarningProjection.cs)、[`ResourceHealthIndexOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ResourceHealth/ResourceHealthIndexOwner.cs) | [`BmsLibraryMaintenanceServiceTests`](../../../BeMusicSeeker.Tests/Maintenance/BmsLibraryMaintenanceServiceTests.cs)、[`ResourceHealthIndexOwnerTests`](../../../BeMusicSeeker.Tests/ResourceHealth/ResourceHealthIndexOwnerTests.cs)、[`ResourceHealthFullOwnedTargetFreshnessTests`](../../../BeMusicSeeker.Tests/ResourceHealth/ResourceHealthFullOwnedTargetFreshnessTests.cs) |
| 使用ファイル件数・単純充足率・要求0件・未解決・旧保存値 | [`ResourceHealthMaintenanceSnapshot`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ResourceHealth/ResourceHealthMaintenanceSnapshot.cs)、[`BmsLibraryMaintenanceService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Maintenance/BmsLibraryMaintenanceService.cs) | [`BmsLibraryMaintenanceServiceTests`](../../../BeMusicSeeker.Tests/Maintenance/BmsLibraryMaintenanceServiceTests.cs) で件数・キャッシュ共有と未解決の分離・保存・未判定投影を確認する。既存列からの検査完了の導出と不完全な組合せの区別も保守テストで確認する。旧保存値と未解決結果のDB復元後の通常補完省略・強制再評価は [`BmsLibraryInitializationLoadTests`](../../../BeMusicSeeker.Tests/Startup/BmsLibraryInitializationLoadTests.cs)。追加の移行やmarkerの不在は静的確認。 |
| 起動スキーマ準備の一括確定・途中失敗・既存行保持・明示再実行 | [`BmsLibraryDbGateway.EnsureLibraryStartupSchema`](../../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDbGateway.cs) | [`AppSchemaPreflightServiceTests`](../../../BeMusicSeeker.Tests/Startup/AppSchemaPreflightServiceTests.cs) で成功後の表・索引・版、後段SQL障害による構造・列・版の巻戻しと元行保持、障害除去後の明示再実行を新しい読取り専用接続から確認する。 |
| 導入差分の通知前反映と統合後の遅延構築 | [`LibraryMutationOwner`](../../../BeMusicSeeker/Models/Library/BMSLibrary.LibraryMutationOwner.Common.cs)、[`RegularChartListOwner`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs) | [`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) の `UsesPreflightDestinationAndWarmDelta` を含む自動・推定先・強制導入テストで現在性・局所差分・旧snapshotを確認する。[`BmsLibraryDuplicateServiceTests`](../../../BeMusicSeeker.Tests/Maintenance/BmsLibraryDuplicateServiceTests.cs) の `MergeChartDirectory_RechecksResourcesWithinAcceptedOperation` でBMS/BMSONの実統合後の未構築状態、初回表示の構築、同世代の再利用を確認する。 |
| IR取得の単一期限・取消、失敗時の既存データ保持 | [`BmsLibraryIrService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Ir/BmsLibraryIrService.cs) | [`AppHttpClientTests`](../../../BeMusicSeeker.Tests/Runtime/AppHttpClientTests.cs)、[`BmsLibraryIrServiceTests`](../../../BeMusicSeeker.Tests/Ir/BmsLibraryIrServiceTests.cs)、[`BmsLibraryIrStartupTests`](../../../BeMusicSeeker.Tests/Startup/BmsLibraryIrStartupTests.cs) |
| 局所パス整理・旧ハッシュ捕捉・残存所有者の保護 | [`BmsLibraryDbGateway`](../../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDbGateway.cs) | [`CatalogMutationOwnerTests`](../../../BeMusicSeeker.Tests/Catalog/CatalogMutationOwnerTests.cs) の `ApplyCatalogMutation_PathCleanupUsesBoundedExactSetAndPreservesDigestOwnership`。実接続のPROFILEで `FULLSCAN_STEP` / `VM_STEP` を観測し、補助的に問合せ計画も確認します。 |

## 関連資料

[譜面の共通モデル](../library/chart-model.md)、[変更セッション](../library/mutations.md)、[起動](../runtime/startup.md)、[性能とデータ規模](performance-and-scale.md)。
