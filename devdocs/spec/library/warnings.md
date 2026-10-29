# 譜面の警告

## 目的と適用範囲

警告の種類、保持する場所、一覧の要約・詳細・強調表示を定めます。操作そのものの失敗報告と、譜面に付く警告は区別します。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

警告の「要約」は一覧セルの短いラベル、「詳細」はツールチップの全文、「分類」は関連する警告をまとめて更新する `Category` です。

## 仕様

### 状態の正本

警告は不変の `ChartWarning` の集合です。所持カタログの共通現在値、保留の `PackageChartEntry`、一時的な `ChartFile` の投影が、それぞれ担当する種類を保持します。自由文や表示用の別名プロパティ、DB保存行を状態の正本にしません。BMS・BMSONとも共通値と専門状態の投影から表示します。

通常一覧と導入直後の新規一覧のリソース不足は、メンテナンス情報から作る `ResourceHealthIndexSnapshot` を表示時に投影します。DBに保存する警告ではありません。LR2互換性は保守行の互換性情報から別の `Lr2Compatibility` 分類へ投影し、リソース状態の索引や無視設定へ混ぜません。

一覧の管理主体は、リソース状態を投影する表示の準備で索引を現在の入力へ合わせます。行の読取りや診断ログは索引を構築しません。未構築・失効状態のまま警告なしとして表示や警告順を確定しないため、行を再利用する表示と、警告・保守の表示だけの更新も同じ準備を行います。詳細は[一覧表示](../ui/table-view.md#絞込みと画面更新)に従います。

メンテナンス情報の既定値や、BMSON解析直後の文字コードだけの行は仮の値です。要求数と存在数が揃うまで計算済みのリソース状態として扱いません。DB由来の一部カテゴリだけの結果は、そのカテゴリの表示に使えます。

### 要約・詳細・強調

一覧の要約は `[種類数] ラベル1, ラベル2` の形で、`Priority` の昇順です。同じラベルは重複表示しませんが、種類数は有効な警告の種類を数えます。例えばWAVとBGAが不足していれば `[2] リソース不足` です。

詳細は `ShowInTooltip=true` のメッセージを優先順位順に改行して表示します。`DisplayWarning` と `WarningTooltipText` は詳細、`WarningDigestText` は要約です。一件でも `HighlightRow=true` なら行を強調します。

保留の `ResourceHealth` は導入先が設定済みでも不足の要約・詳細を表示します。導入先は `ChartFile.InstallDestination` を通してパッケージ・一時状態から明示的に重ね、DBから読んだ共通基本値に含まれると仮定しません。

### リソース状態の評価

推定済み保留の改名・純移動では、警告kindとMessageを推定時の診断としてそのまま保持します。詳細には推定時点の旧pathが残り得ますが、現在の候補・配置pathは成功先へ追従します。自由文のpath置換や追加の書式引数履歴は保持しません。削除・統合での候補失効と確定DSTの優先は[変更操作](mutations.md#操作ごとの反映範囲)に従い、DST空の元健康度復元は既存の登録時snapshotを使います。

所持BMSの再調査で文字コードが確定して再復号する場合、[songの限定保存](../integration/lr2-song-db.md#songの生成)を使い、生メタデータ5列と捕捉mtimeの `date` だけを保守値と同じトランザクションへ渡します。詳細情報の有無や復号文字列の同値によって、BPM・ノート数等の対象外列を再生成しません。

一覧への所属判定は警告集合を変更せず、メンテナンス情報に基づく副作用のない索引で行います。BMSONは現在のMD5と一致する計算済みのメンテナンス情報を使い、未計算・旧ハッシュなら参照と現在のファイル状態から一時的に評価します。索引を優先し、必要な場合だけ実ファイルを確認します。

保留の健康度とリソース警告は、設定済みの `INSTL DST`、未設定なら先頭候補、どちらもなければ入力元配置を基準とする一時状態です。設定先・候補では実際に持ち込む同梱資源を加算し、各譜面固有の使用集合を照合します。単一BMS・BMSONの親全体は同梱としません。推定・編集・候補選択・空編集の完了時には従来どおり評価し、明示クリアは後述の計算済み結果を復元します。導入成功時に分類単位で解除し、通常・新規一覧では導入後のメンテナンス情報から表示します。保留の警告を所持カタログやDB行へ書き戻しません。

保留登録時には、既存の初期評価で計算した元配置の `ResourceHealthMaintenanceSnapshot` を項目に一件保持します。自動導入候補から設定・失敗・取消等により後で保留へ回る項目も同じ初期評価で確定します。起動時の保留復元ではDBからこの結果を読み戻さず、既存の起動時評価で確定します。候補切り替え・再推定・通常の健康度更新は、この元配置結果を上書きしません。

確定DSTが存続する削除・統合の候補失効では、候補metadata・曖昧性・候補音源の評価に依存する推定警告を解除します。同じInstallEstimation分類でも、入力元の推定不能 `UnsupportedResourcePath`、入力面探索上限 `SourceSurfaceScanLimitExceeded`、所持先解決失敗 `InstalledDestinationResolveFailed` は候補評価の失効とは独立した診断なので、通常の手動DST確定後も、確定DSTが統合先へ追従した後もkind/Messageを保持します。scope外の確定DSTと診断にも作用させません。DST空への元配置復元、成功全体削除scope内のDST解除、利用者の明示全クリアでは、この三診断を含む従来の分類全体の解除を維持します。

保留の「インストール先をクリア」は、導入先・代表情報・候補・`InstallEstimation` 警告を消し、保存済みの音声・画像・動画の定義数/存在数と補助画像3役割の定義/存在値を復元します。`ResourceHealth` 警告は復元した値から再生成し、現在の警告無視設定を維持します。文字コード・修正状態・LR2互換性情報と、他分類の警告は現在値を維持します。配置は元の譜面ディレクトリの `Current` へ戻し、捕捉済み同梱索引と完全性を保持します。

この復元では、譜面の再解析、要求キーの生成、`Chart.Resources` の再列挙、入力元の列挙・物理存在確認を行いません。登録後の外部ファイルの追加・削除はクリア後の健康度へ反映しません。各値の未判定nullと既知0件はそのまま復元し、初期結果自体の欠落を再計算・0件成功で救済しません。必要な初期化を登録入口で成立させ、当該項目の保存値を取得してから消去を始めます。

保留の親子重複を分割・統合した新単位は、その取得結果の全定義で入れ子条件を判定してから、現在の配置で元健康度と警告を作ります。異なる旧単位の配置依存診断・元健康度を引き継ぎません。同単位の指定保持と同path・同SHAだけの確定復号は[物理範囲の保持](install-estimation.md#検出した物理範囲の保持)に従い、導入成功時の`PackageLayout`解除は既存の成功投影を使います。

元配置結果はdetached要求・部分削除の残存項目へ不変参照として引き継ぎ、現在健康度評価と分けます。保留の内部解除と所持フォルダ削除に伴う保留導入先の解除も同じ復元操作を使います。通常所持の再導入先クリアには適用せず、空編集では候補と推定警告を残し、先頭候補があればその健康度を評価します。導入・リソースのみ導入・清掃のみの成功確定後は元結果を破棄し、失敗・取消で保留に残る項目は保持します。永続化・候補別cache・背景再計算は追加しません。

推定結果の反映には、評価で使った同梱と候補の照合入力を共有し、未取得の候補索引を反映時の最新索引で補いません。現在配置と通常所持の物理的な存在確認は既存の二値判定を維持します。I/Oで存在を確認できない場合もfalseとなる既存の限界があります。

子譜面を含むパッケージの源側音源充足も、各譜面の相対配置を付けたルート要求で判定します。候補編集では相対位置の取得を後続健康度更新と共有し、入力元の未捕捉を表示のために読み直しません。譜面別の健康度と警告は個別の原参照を同じ相対配置へ照合し、親直下や兄弟の同名へ読み替えません。

導入の必須後処理が全て成功したら、今回の所持現在値を加入済みentryへ渡し、通常・新規一覧で同じ計算済みのWAV・BGA・MOVIE充足値を表示します。要求数0件の充足値は対象外のnullで、表示は `-` です。検査済み0件と未検査・未解決は保存件数と取得状態で区別し、nullを旧100%で補いません。保存値と算式は[リソース健全性](../core/data-and-indexes.md#リソース健全性)に従います。

不足警告の分数は使用ファイルキーの存在数／要求数です。未使用定義の欠損と未定義の使用IDは不足警告へ含めません。これはBMS/PMSのWAV/BMP IDと、BMSONのBGA/LAYER/POORイベントのヘッダー未定義IDに共通です。定義済みの不正パスや空nameは照合不能として扱い、架空のファイル名で不足件数を作りません。

起動時のメンテナンス情報読込みはDBの結果を適用するもので、全リソースの再検査ではありません。不足分を追加した後は、選択譜面または全譜面の再スキャンで再評価します。計算結果が既存行と同じならDBを書き直しません。所持currentに反映する保守・警告の実変更はカタログ版と対象差分を同時確定し、登録済みの同じ結果で該当行を更新します。通常一覧とplaylistは同じ現在値・専門警告の投影を使い、検査事実に保存した古いfull譜面を表示値の正本にしません。

保留の選択再スキャンでは、所持分の必要な保存が成功してから、要求に保持した同じ `PackageChartEntry` へ結果を反映し、既に生成した表示行も更新します。配置先・候補がなければ入力元の健康度とLR2警告を採用します。配置先・候補がある場合、入力元の結果をその項目の寿命内で使い、表示は捕捉済みの同梱文脈と現行の候補索引で配置の健康度を再評価します。資源参照が未取得なら既存の取得入口で読み直し、古い参照へ戻しません。登録時の元配置結果、導入先・候補・所属を変えず、配置再評価では現在の文字コードと修正済み状態を保持します。選択保留のリソース再調査は文字コードの自動検出で利用者指定や未確定状態を上書きせず、非CP932のBMSでもsong・maintenance行を新設・更新しません。入力元の件数・警告が同値でも、成功評価を同じentryへ渡して新しい資源参照で配置を再評価します。この配送のために同値DB書込みや所持カタログ版の更新を増やしません。

選択保留の再調査は、入力元の今回の評価を現在項目と比較し、保存用の集合へ含めません。保存前DB行との比較専用読込みは行わず、既存行の有無・ハッシュ・健康度にかかわらず書き込みません。文字コードと修正印の計算入力は現在の項目から引き継ぎます。入力元の件数・警告が同値でも、成功評価を `EvaluatedPendingCharts` へ渡して新しい参照で配置を再評価します。文字コードの保持期間と所持混在の保存失敗境界は[譜面読取り](chart-file-reading.md#保留項目の文字コード)に従います。

実対象が選択保留だけなら、所持の資源入力は変わりません。構築済みで有効な所持健康度索引は、保守入力の保護範囲を終えた後も同じsnapshotを維持します。未構築・失効済みの索引をこの処理で有効化せず、所持混在の再スキャンは既存の差分反映を使います。子階層の保留譜面では、再スキャン後も捕捉した相対位置と同梱文脈を保持します。入力元のファイル削除を評価しても、候補先の表示は捕捉済み同梱を最終配置の相対位置で照合します。

### 警告の定義

| 種類（コード識別子） | 分類 | 優先順位 | 要約表示 | 行の強調 | 要約の表示条件 |
| --- | --- | ---: | --- | --- | --- |
| `ResourceBearingNestedCharts` | `PackageLayout` | 9 | `資源付き入れ子（導入不可）` | true | 元packageの[資源付き入れ子条件](drop-install.md#資源を持つ入れ子の導入禁止)が成立する全行 |
| `NestedChartFileInPackage` | `PackageLayout` | 10 | `サブフォルダ譜面` | false | 該当する警告がある場合 |
| `ChartInfoParseFailure` | `ChartMetadata` | 15 | `メタデータ解析エラー` | true | 該当する警告がある場合 |
| `Lr2PathEncodingUnsupported` | `Lr2Compatibility` | 18 | `LR2パス非対応` | true | 該当する警告がある場合 |
| `Lr2PathTooLong` | `Lr2Compatibility` | 19 | `LR2パス長超過` | true | 該当する警告がある場合 |
| `Lr2ResourcePathUnsupported` | `Lr2Compatibility` | 21 | `LR2リソース非対応` | true | 該当する警告がある場合 |
| `Lr2ResourcePathTooLong` | `Lr2Compatibility` | 22 | `LR2リソースパス長超過` | true | 該当する警告がある場合 |
| `ZeroNoteMismatch` | `ChartContent` | 20 | `ゼロノート不整合` | true | 該当する警告がある場合 |
| `DuplicateChart` | `Duplicate` | 30 | `重複譜面` | true | 該当する警告がある場合 |
| `InstallEstimationAmbiguous` | `InstallEstimation` | 40 | `推定先複数` | true | 該当する警告がある場合 |
| `InstallEstimationMetadataMismatch` | `InstallEstimation` | 41 | `TITLE/ARTIST未確認` | true | 該当する警告がある場合 |
| `InstallEstimationUnrelated` | `InstallEstimation` | 42 | `作品との関連性なし` | true | 明示保持した先頭候補がUnrelatedの場合 |
| `InstallEstimationMetadataUnavailable` | `InstallEstimation` | 42 | `作品情報の比較不能` | true | 明示保持した先頭候補がUnavailableの場合 |
| `InstallEstimationAudioMissing` | `InstallEstimation` | 42 | `導入後音源不足` | true | 選択候補で各譜面の設定音源閾値を満たさず自動設定を見送る場合 |
| `InstallEstimationAudioUnverifiable` | `InstallEstimation` | 42 | `導入後音源未確認` | true | 有効候補への音源完全性を確認できない場合 |
| `InstallEstimationReinstallNotImproved` | `InstallEstimation` | 43 | `再導入改善なし` | true | 該当する警告がある場合 |
| `InstalledDestinationAmbiguous` | `InstallEstimation` | 43 | `導入先複数` | true | 該当する警告がある場合 |
| `InstalledDestinationAutoAppliedAmbiguous` | `InstallEstimation` | 43 | `導入先複数` | true | 該当する警告がある場合 |
| `UnsupportedResourcePath` | `InstallEstimation` | 44 | `リソースパス非対応` | false | 該当する警告がある場合 |
| `InstalledDestinationResolveFailed` | `InstallEstimation` | 45 | `導入先不明` | false | 該当する警告がある場合 |
| `SourceSurfaceScanLimitExceeded` | `InstallEstimation` | 46 | `探索上限` | false | 該当する警告がある場合 |
| `InstallEstimationLowConfidence` | `InstallEstimation` | 47 | `導入先推定` | true | 該当する警告がある場合 |
| `AlreadyInstalled` | `InstalledState` | 50 | `既に導入済み` | false | 該当する警告がある場合 |
| `SingleBmsFile` | `PackageLayout` | 60 | `単体BMS` | false | 該当する警告がある場合 |
| `SingleBmsonFile` | `PackageLayout` | 60 | `単体BMSON` | false | 該当する警告がある場合 |
| `ResourceWavMissing` | `ResourceHealth` | 80 | `リソース不足` | false | 該当する警告がある場合（保留は設定後も表示） |
| `ResourceBgaMissing` | `ResourceHealth` | 80 | `リソース不足` | false | 該当する警告がある場合（保留は設定後も表示） |
| `ResourceMovieMissing` | `ResourceHealth` | 80 | `リソース不足` | false | 該当する警告がある場合（保留は設定後も表示） |
| `ResourceStagefileMissing` | `ResourceHealth` | 83 | `画像不足` | false | 要約には出さず、ツールチップだけに表示 |
| `ResourceBackbmpMissing` | `ResourceHealth` | 83 | `画像不足` | false | 要約には出さず、ツールチップだけに表示 |
| `ResourceBannerMissing` | `ResourceHealth` | 83 | `画像不足` | false | 要約には出さず、ツールチップだけに表示 |

選択された有効候補への音源完全性はTITLE/ARTISTと独立して評価します。閾値未達と照合不能・未取得を区別し、見送る場合は推定対象群の全行へ理由を付けます。Missingでも設定閾値を満たして自動設定できた場合は見送り警告を付けず、一般のResourceWavMissingを保持します。自身のWAVが100%の行にも付け、候補なしでは付けません。現在の配置での不足警告とは別の結果です。理由と評価先は元の対象行へ保持し、別パッケージの行へ広げません。

### 種類ごとの更新

| 種類・分類 | 生成と解除 |
| --- | --- |
| `InstallEstimation` | 再推定で分類単位に置換し、明示クリア・導入成功で解除する。候補選択・手動入力・空編集では推定時の理由を保持する。複数候補の自動適用と未適用は別の種類とし、候補を詳細に残す |
| `UnsupportedResourcePath` | 親参照 `..` がある場合に付け、推定しない |
| `SourceSurfaceScanLimitExceeded` | ディレクトリパッケージ自身の50000件上限超過で付ける。単一ファイルの親の規模では付けない |
| `DuplicateChart` | 重複の種類だけを設定・解除する |
| `ZeroNoteMismatch` | BMSの詳細情報が0ノートで、本文に可視ノートらしい記述がある場合に付ける。情報未生成または0でなくなれば解除する |
| `ChartInfoParseFailure` | 解析エラー画面の表示用行へ付け、通常の元行を変更しない |
| `Lr2Compatibility` | 評価済みのメンテナンス情報から分類単位で生成する。未評価の仮データを付けただけでは既存の直接警告を消さない |
| `PackageLayout` | 発見・復元時に単体BMS・単体BMSON・パッケージ内の子譜面へ付け、当該譜面の導入成功時に解除する。保留・失敗・未処理の項目では元の種類とメッセージを保持する |
| 既所持 | 発見・復元時に対象項目へ付ける。リソース状態と排他的にしない |

導入前の構造警告の解除は、DB確定前にはdetachedな準備項目だけへ適用し、確定後に成功したlive項目へ反映します。部分失敗でも先行成功分だけを解除し、失敗・未処理項目へ解除を広げません。成功先の健康度は既存の保守で再評価し、不足が続く場合のリソース警告は維持します。

LR2互換性では、譜面の完全パスとリソース名のCP932表現、LR2の従来の長さ制限を区別します。アプリ自身が長パスを読めるなら登録を続けます。親参照はLR2非対応の理由にせず、移動時に再解析が必要な情報として保持します。親参照がない場合は、保存した相対パスの最大バイト長と移動後のディレクトリで軽量に再評価できます。

LR2用パスを表現できないことを理由にBMS行を削除しません。CRCの生成、相対パス補正の条件、既存行の保持は[LR2楽曲DB](../integration/lr2-song-db.md#songの生成)を正本とします。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 確定DSTの存続時は候補評価だけ失効し、非候補診断三種のkind/Messageを保持 | `PackageChartEntry.ApplyLibraryFolderChange`、既存の診断生成・手動DST設定、`PackageStateMutationApplier` | [`BmsLibraryStateApplierTests.ApplyPackageReferenceFacts_ConfirmedDestinationPreservesSourceDiagnostics`](../../../BeMusicSeeker.Tests/Install/BmsLibraryStateApplierTests.cs) が三診断の実producer→実手動確定→merge参照facts→実applierを通し、統合先DST/配置・健康度・診断保持、scope外不変、次の全体削除scopeでは従来全clearを確認する。候補MetadataMismatchの解除は既存 `ApplyPackageReferenceFacts_InvalidationPrioritizesConfirmedDestination`、実上限・入力元不備・所持先解決失敗の生産判断は既存推定系列、明示全clearは `ClearInstallDestinations_ClearsResolveFailedWarning` と元健康度系列が分担する。候補metadata・曖昧・音源等の既存kindと選択規則の対応は静的照合を併用する。 |
| 成功した改名・純移動の現在配置path追従と推定時診断保持、候補失効時の元健康度復元 | `PackageChartEntry.ApplyLibraryFolderChange`、`ChartResourcePlacement.Relocate`、`BmsLibraryPackageInstallService.ClearPendingInstallDestination` | [`BmsLibraryStateApplierTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryStateApplierTests.cs) の `ApplyPackageReferenceFacts_FolderMovePreservesCandidatePlacementAndDiagnostics` がkind/Message不変と新配置を、`ApplyPackageReferenceFacts_InvalidationPrioritizesConfirmedDestination` が確定DST優先・登録時復元・他分類保持を確認する。復元12値の条件表は既存の明示クリア系列、物理成功scopeの接続は[変更操作の対応表](mutations.md#実装とテストの対応)へ分担する。 |
| 再復号のメタデータ限定保存と詳細未付与の起動状態 | `CatalogMaintenanceOwner`、`CatalogMutationOwner`、`SongMetadataUpdate` | [LR2楽曲DBの対応表](../integration/lr2-song-db.md#実装とテストの対応)の `ApplyEncoding_RealStoreAfterDbLoadPreservesUnchangedSongColumns` と `RescanAsync_RealStoreAfterDbLoadPreservesUnchangedSongColumns` が、本番操作・実DB読込み・詳細hydrate後の更新5列/dateと対象外全列の保持を確認する。 |
| 保留の明示クリアで登録時健康度を復元、外部変更非反映、非健康度の現在値保持 | `PackageChartEntry`、`BmsLibraryPackageInstallService`、`PendingPackageWorkflowOwner`、`BMSLibrary` | `BmsLibraryPackageInstallServiceTests` が全資源/未判定/0件/不足・候補切り替え・登録後の外部変更・文字コード/LR2/警告分類を確認する。初期確定は同クラスと `BmsLibraryInitializationInstallTests`、detached/部分削除は既存Install系列へ分担し、実メニューから生成済み行への更新は `MainWindowPackageMaintenanceWpfTests` で確認する。クリアから解析・資源列挙・存在確認へ非到達、成功時の参照破棄は静的確認を併用する。 |
| 種類、優先順位、要約の重複抑止、詳細、強調 | [`ChartWarning`](../../../BeMusicSeeker/Models/Chart/ChartWarning.cs)、[`ChartWarningCollection`](../../../BeMusicSeeker/Models/Chart/ChartWarning.cs)、[`ChartWarningProjectionFormatter`](../../../BeMusicSeeker/ViewModels/ChartList/ChartWarningProjectionFormatter.cs) | [`ChartWarningCollectionTests`](../../../BeMusicSeeker.Tests/Chart/ChartWarningCollectionTests.cs) |
| 保留の配置優先・同梱合成・欠損と未確認・音源見送り理由 | `ChartResourcePlacement`、`BmsLibraryPackageInstallService`、`PackageChartEntry` | `BmsLibraryPackageInstallServiceTests.RefreshPendingResourceHealth_SelectsPlacementAndCombinesAllResourceKinds` と `RefreshPendingResourceHealth_DistinguishesUnknownZeroAndSingleFileScope` は全資源と配置・未確認・0件・単一ファイルを確認する。`BmsLibraryPendingPackageRegroupTests.SearchAndEdit_GroupAudioReasonRemainsIndependentOfCurrentHealth` は実推定と編集・再推定・クリアの配送を確認する。要約の併記・順序・強調は `ChartWarningCollectionTests` へ分担する。 |
| BMS・BMSON・保留の状態分離 | [`ChartFileProjection`](../../../BeMusicSeeker/Models/Chart/ChartFileProjection.cs)、[`ChartFileTransientState`](../../../BeMusicSeeker/Models/Chart/ChartFileTransientState.cs)、[`PackageChartEntry`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/PackageChartEntry.cs) | [`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs)、[`ChartInfoInstallFailureRetryTests`](../../../BeMusicSeeker.Tests/ChartInfo/ChartInfoInstallFailureRetryTests.cs) |
| 導入成功時の構造警告解除と失敗・保留時の保持 | [`PackageChartEntry.ClearPostInstallState`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/PackageChartEntry.cs) | [`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) の `ForceInstallPendingPackages_SingleChartsAndDirectoryUseReservedNewFolders` は実取込みの単体BMS/BMSON警告と成功後の所持・live項目での解除、`PrecommitFailureRetainsSourceAndRestoresPackageOwner` は準備だけの解除と失敗時の元kind/message保持、`PreservesPhysicalSuccessPrefixAndUnprocessedSuffix` は成功分だけの解除を確認する。[`AutomaticContinuation_NestedChartUsesFinalAudioPlacement`](../../../BeMusicSeeker.Tests/Install/PackageInstallWorkflowOwnerAutomaticContinuationTests.cs) は子譜面の成功時解除・保留時保持と音源不足を分担する。BMSON子譜面への生成は既存 `ApplyNestedChartFileWarnings_AddsNestedWarningWithoutMaterializingAdapterlessBmsonEntries` が担当する。 `PreservesPhysicalSuccessPrefixAndUnprocessedSuffix` はCP932非対応の元配置で実評価したLR2診断の種類・メッセージも失敗・未処理項目に保持する。 |
| 導入後の不足継続・解消と初回表示 | [`LibraryMutationSession`](../../../BeMusicSeeker/Models/Library/BMSLibrary.LibraryMutationOwner.Session.cs)、[`PackageLifecycleOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/PackageLifecycleOwner.CurrentCharts.cs)、[`RegularChartListOwner`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs) | [`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) の `InstallPendingPackages_ProjectsCurrentResourceWarningsInNewAndNormalViews`。実Force・推定先導入したBMS/BMSONのDB件数、所持現在値・entry・新規・通常行のCalculatedと既知の充足値・同token、一時警告解除と要約・詳細、成功後の所持・entry・新規・通常行で構造警告が復活しないこと、通知時のリース解放を、構築済み・未構築の索引で確認する。 [`PendingFixedEncoding_InstallPersistsOnlySuccessfulPlacementAndSurvivesRescan`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceEncodingTests.cs) はCP932非対応名から対応名への導入と逆方向で、実保留評価したLR2投影の所有解除と、再調査前の所持・同じlive項目の診断一致を確認する。要求0件の対象外表示も確認する。 |
| 使用件数、単純率、未解決警告と参照解放後の投影 | [`BmsLibraryMaintenanceService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Maintenance/BmsLibraryMaintenanceService.cs)、[`ChartFileProjection`](../../../BeMusicSeeker/Models/Chart/ChartFileProjection.cs) | [`BmsLibraryMaintenanceServiceTests`](../../../BeMusicSeeker.Tests/Maintenance/BmsLibraryMaintenanceServiceTests.cs) で使用集合・キャッシュ・保存・警告生産を確認する。旧100%から未判定への投影と通常一覧への接続、DB復元後の保存同値再スキャンによる詳細取得・DB非書込み・対象通知・同値再通知抑制を含める。 |
| 選択保留の再スキャンと既存表示行、入力元と導入先の分離 | `ChartResourceHealthRequest`、`SelectedChartResourceHealthWorkflowOwner`、`LibraryMutationOwner`、`PackageChartEntry` | `SelectedChartResourceHealthWorkflowOwnerTests.RescanAsync_RealStoreUpdatesPendingCurrentAndExistingRow` は保留のみ・所持混在で実再調査・保留無書込み・所持保存・現在値・既存表示行を確認する。単体譜面の推定先では入力元100%と表示0%を区別し、新しい参照・LR2警告解除・自動検出と異なるBig5指定・修正印の真偽・登録時結果・非選択項目・所属保持を確認する。非CP932入力で保留のsong非新設と所持の再復号保存を併せて確認する。`RescanAsync_WhenOnlyResourceReferencesChange_ReevaluatesPendingDestinationWithoutWritingSameValues` は入力元件数・警告が同値の参照変更で、同値DB書込みと所持版更新を増やさず候補表示を再評価することを確認する。配置優先の条件分岐と表示行単独の追従は既存Install系列と `ChartListVirtualViewTests` が分担する。 |
| 選択保留のDB行不変、所持索引の維持 | `CatalogMaintenanceOwner`、`BmsLibraryMaintenanceService` | `SelectedChartResourceHealthWorkflowOwnerTests.RescanAsync_KeepsStoredRowsUntouchedAndOwnedIndex` は既存行・配置表示・今回入力元の三者を分け、音源削除、音源追加、同値、未登録、別hashの5条件を確認する。全条件で保留path限定のSQLite INSERT/UPDATE拒否と読取り専用接続の既存行不変・新設なしを観測し、全条件で非選択所持を含む有効な索引を再構築しない取得から先に観測して同じsnapshotと投影を維持する。 |
| 子階層の保留再スキャンと捕捉同梱の保持 | `ChartResourcePlacement`、`BmsLibraryPackageInstallService`、`LibraryMutationOwner` | `SelectedChartResourceHealthWorkflowOwnerTests.RescanAsync_NestedPendingChartKeepsCapturedBundledPlacement` は入力元の子音源を削除し、DB新設なしと捕捉同梱による同じentry・既存行の1/1を区別する。packageを渡さない再評価でも導入先の子ディレクトリ、相対位置、同梱完全性、候補、元配置、所属を保持する。候補/同梱と親/子の条件分岐は導入推定系列へ分担する。 |
| 現在のメンテナンス情報、差分更新と再評価 | [`ResourceHealthIndexOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ResourceHealth/ResourceHealthIndexOwner.cs)、[`ResourceHealthWarningProjection`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ResourceHealth/ResourceHealthWarningProjection.cs) | [`ResourceHealthIndexOwnerTests`](../../../BeMusicSeeker.Tests/ResourceHealth/ResourceHealthIndexOwnerTests.cs)、[`ResourceHealthFullOwnedTargetFreshnessTests`](../../../BeMusicSeeker.Tests/ResourceHealth/ResourceHealthFullOwnedTargetFreshnessTests.cs) |
| LR2のパス・リソース互換性 | [`Lr2SongRowEnricher`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongRowEnricher.cs) | [`BmsLibraryLr2SongDbSyncTests`](../../../BeMusicSeeker.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs)、[`ChartWarningCollectionTests`](../../../BeMusicSeeker.Tests/Chart/ChartWarningCollectionTests.cs) |
| BMSの追加、相対パス補正、表現不能なパスの行の保持 | [`Lr2SongRowEnricher`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongRowEnricher.cs)、[`BMSLibrary`](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs) | [`BmsLibraryInitializationInstallTests`](../../../BeMusicSeeker.Tests/Startup/BmsLibraryInitializationInstallTests.cs)、[`BmsLibraryInitializationLoadTests`](../../../BeMusicSeeker.Tests/Startup/BmsLibraryInitializationLoadTests.cs) |

## 関連資料

[共通譜面モデル](chart-model.md)、[導入推定](install-estimation.md)、[譜面ファイルの読取り](chart-file-reading.md)、[一覧表示](../ui/table-view.md)を参照します。
