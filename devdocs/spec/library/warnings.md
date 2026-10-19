# 譜面の警告

## 目的と適用範囲

警告の種類、保持する場所、一覧の要約・詳細・強調表示を定めます。操作そのものの失敗報告と、譜面に付く警告は区別します。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

警告の「要約」は一覧セルの短いラベル、「詳細」はツールチップの全文、「分類」は関連する警告をまとめて更新する `Category` です。

## 仕様

### 状態の正本

警告は不変の `ChartWarning` の集合です。所持カタログの共通現在値、保留の `PackageChartEntry`、一時的な `ChartFile` の投影が、それぞれ担当する種類を保持します。自由文や表示用の別名プロパティ、DB保存行を状態の正本にしません。BMS・BMSONとも共通値と専門状態の投影から表示します。

通常一覧と導入直後の新規一覧のリソース不足は、保守情報から作る `ResourceHealthIndexSnapshot` を表示時に投影します。DBに保存する警告ではありません。LR2互換性は保守行の互換性情報から別の `Lr2Compatibility` 分類へ投影し、リソース状態の索引や無視設定へ混ぜません。

一覧の管理主体は、リソース状態を投影する表示の準備で索引を現在の入力へ合わせます。行の読取りや診断ログは索引を構築しません。未構築・失効状態のまま警告なしとして表示や警告順を確定しないため、行を再利用する表示と、警告・保守の表示だけの更新も同じ準備を行います。詳細は[一覧表示](../ui/table-view.md#絞込みと画面更新)に従います。

保守情報の既定値や、BMSON解析直後の文字コードだけの行は仮の値です。要求数と存在数が揃うまで計算済みのリソース状態として扱いません。DB由来の一部カテゴリだけの結果は、そのカテゴリの表示に使えます。

### 要約・詳細・強調

一覧の要約は `[種類数] ラベル1, ラベル2` の形で、`Priority` の昇順です。同じラベルは重複表示しませんが、種類数は有効な警告の種類を数えます。例えばWAVとBGAが不足していれば `[2] リソース不足` です。

詳細は `ShowInTooltip=true` のメッセージを優先順位順に改行して表示します。`DisplayWarning` と `WarningTooltipText` は詳細、`WarningDigestText` は要約です。一件でも `HighlightRow=true` なら行を強調します。

保留の `ResourceHealth` は導入先が設定済みでも不足の要約・詳細を表示します。導入先は `ChartFile.InstallDestination` を通してパッケージ・一時状態から明示的に重ね、DBから読んだ共通基本値に含まれると仮定しません。

### リソース状態の評価

一覧への所属判定は警告集合を変更せず、保守情報に基づく副作用のない索引で行います。BMSONは現在のMD5と一致する計算済みの保守情報を使い、未計算・旧ハッシュなら参照と現在のファイル状態から一時的に評価します。索引を優先し、必要な場合だけ実ファイルを確認します。

保留の健康度とリソース警告は、設定済みの `INSTL DST`、未設定なら先頭候補、どちらもなければ現在の入力元配置を評価する一時状態です。設定先・候補では実際に持ち込む同梱資源を加算し、各譜面固有の使用集合を照合します。単一BMS・BMSONの親全体は同梱としません。推定・編集・候補選択・空編集・明示クリア・再グループ化の完了時に更新します。導入成功時に分類単位で解除し、通常・新規一覧では導入後の保守情報から表示します。保留の警告を所持カタログやDB行へ書き戻しません。

推定結果の反映には、評価で使った同梱と候補の照合入力を共有し、未取得の候補索引を反映時の最新索引で補いません。現在配置と通常所持の物理的な存在確認は既存の二値判定を維持します。I/Oで存在を確認できない場合もfalseとなる既存の限界があります。

導入の必須後処理が全て成功したら、今回の所持現在値を加入済みentryへ渡し、通常・新規一覧で同じ計算済みのWAV・BGA・MOVIE充足値を表示します。要求数0件の充足値は対象外のnullで、表示は `-` です。検査済み0件と未検査・未解決は保存件数と取得状態で区別し、nullを旧100%で補いません。保存値と算式は[リソース健全性](../core/data-and-indexes.md#リソース健全性)に従います。

不足警告の分数は使用ファイルキーの存在数／要求数です。未使用定義の欠損と未定義の使用IDは不足警告へ含めません。これはBMS/PMSのWAV/BMP IDと、BMSONのBGA/LAYER/POORイベントのヘッダー未定義IDに共通です。定義済みの不正パスや空nameは照合不能として扱い、架空のファイル名で不足件数を作りません。

起動時の保守情報読込みはDBの結果を適用するもので、全リソースの再検査ではありません。不足分を追加した後は、選択譜面または全譜面の再スキャンで再評価します。計算結果が既存行と同じならDBを書き直しません。所持currentに反映する保守・警告の実変更はカタログ版と対象差分を同時確定し、登録済みの同じ結果で該当行を更新します。通常一覧とplaylistは同じ現在値・専門警告の投影を使い、検査事実に保存した古いfull譜面を表示値の正本にしません。

### 警告の定義

| 種類（コード識別子） | 分類 | 優先順位 | 要約表示 | 行の強調 | 要約の表示条件 |
| --- | --- | ---: | --- | --- | --- |
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

選択された有効候補への音源完全性はTITLE/ARTISTと独立して評価します。閾値未達と照合不能・未取得を区別し、見送る場合は推定対象群の全行へ理由を付けます。Missingでも設定閾値を満たして自動設定できた場合は見送り警告を付けず、一般のResourceWavMissingを保持します。自身のWAVが100%の行にも付け、候補なしでは付けません。現在の配置での不足警告とは別の結果です。再グループ化では元の対象行の理由と評価先を引き継ぎ、新グループの他行へ広げません。

### 種類ごとの更新

| 種類・分類 | 生成と解除 |
| --- | --- |
| `InstallEstimation` | 再推定で分類単位に置換し、明示クリア・導入成功で解除する。候補選択・手動入力・空編集では推定時の理由を保持する。複数候補の自動適用と未適用は別の種類とし、候補を詳細に残す |
| `UnsupportedResourcePath` | 親参照 `..` がある場合に付け、推定しない |
| `SourceSurfaceScanLimitExceeded` | ディレクトリパッケージ自身の50000件上限超過で付ける。単一ファイルの親の規模では付けない |
| `DuplicateChart` | 重複の種類だけを設定・解除する |
| `ZeroNoteMismatch` | BMSの詳細情報が0ノートで、本文に可視ノートらしい記述がある場合に付ける。情報未生成または0でなくなれば解除する |
| `ChartInfoParseFailure` | 解析エラー画面の表示用行へ付け、通常の元行を変更しない |
| `Lr2Compatibility` | 評価済みの保守情報から分類単位で生成する。未評価の仮データを付けただけでは既存の直接警告を消さない |
| パッケージ構造・既所持 | 発見・復元時に対象項目へ付ける。リソース状態と排他的にしない |

LR2互換性では、譜面の完全パスとリソース名のCP932表現、LR2の従来の長さ制限を区別します。アプリ自身が長パスを読めるなら登録を続けます。親参照はLR2非対応の理由にせず、移動時に再解析が必要な情報として保持します。親参照がない場合は、保存した相対パスの最大バイト長と移動後のディレクトリで軽量に再評価できます。

LR2用パスを表現できないことを理由にBMS行を削除しません。CRCの生成、相対パス補正の条件、既存行の保持は[LR2楽曲DB](../integration/lr2-song-db.md#songの生成)を正本とします。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 種類、優先順位、要約の重複抑止、詳細、強調 | [`ChartWarning`](../../../BeMusicSeeker/Models/Chart/ChartWarning.cs)、[`ChartWarningCollection`](../../../BeMusicSeeker/Models/Chart/ChartWarning.cs)、[`ChartWarningProjectionFormatter`](../../../BeMusicSeeker/ViewModels/ChartList/ChartWarningProjectionFormatter.cs) | [`ChartWarningCollectionTests`](../../../BeMusicSeeker.Tests/Chart/ChartWarningCollectionTests.cs) |
| 保留の配置優先・同梱合成・欠損と未確認・音源見送り理由 | `ChartResourcePlacement`、`BmsLibraryPackageInstallService`、`PackageChartEntry` | `BmsLibraryPackageInstallServiceTests.RefreshPendingResourceHealth_SelectsPlacementAndCombinesAllResourceKinds` と `RefreshPendingResourceHealth_DistinguishesUnknownZeroAndSingleFileScope` は全資源と配置・未確認・0件・単一ファイルを確認する。`BmsLibraryPendingPackageRegroupTests.SearchAndEdit_GroupAudioReasonRemainsIndependentOfCurrentHealth` は実推定と編集・再推定・クリアの配送を確認する。`TryRegroupPendingPackagesForSourceDirectories_ReevaluatesBundledResourcesAndPreservesOriginalAudioReason` は同梱範囲の変更による不足解消と元行だけの理由保持を確認し、要約の併記・順序・強調は `ChartWarningCollectionTests` へ分担する。 |
| BMS・BMSON・保留の状態分離 | [`ChartFileProjection`](../../../BeMusicSeeker/Models/Chart/ChartFileProjection.cs)、[`ChartFileTransientState`](../../../BeMusicSeeker/Models/Chart/ChartFileTransientState.cs)、[`PackageChartEntry`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/PackageChartEntry.cs) | [`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs)、[`ChartInfoInstallFailureRetryTests`](../../../BeMusicSeeker.Tests/ChartInfo/ChartInfoInstallFailureRetryTests.cs) |
| 導入後の不足継続・解消と初回表示 | [`LibraryMutationSession`](../../../BeMusicSeeker/Models/Library/BMSLibrary.LibraryMutationOwner.Session.cs)、[`PackageLifecycleOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/PackageLifecycleOwner.CurrentCharts.cs)、[`RegularChartListOwner`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs) | [`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) の `InstallPendingPackages_ProjectsCurrentResourceWarningsInNewAndNormalViews`。実Force・推定先導入したBMS/BMSONのDB件数、所持現在値・entry・新規・通常行のCalculatedと既知の充足値・同token、一時警告解除と要約・詳細、通知時のリース解放を、構築済み・未構築の索引で確認する。要求0件の対象外表示も確認する。 |
| 使用件数、単純率、未解決警告と参照解放後の投影 | [`BmsLibraryMaintenanceService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Maintenance/BmsLibraryMaintenanceService.cs)、[`ChartFileProjection`](../../../BeMusicSeeker/Models/Chart/ChartFileProjection.cs) | [`BmsLibraryMaintenanceServiceTests`](../../../BeMusicSeeker.Tests/Maintenance/BmsLibraryMaintenanceServiceTests.cs) で使用集合・キャッシュ・保存・警告生産を確認する。旧100%から未判定への投影と通常一覧への接続、DB復元後の保存同値再スキャンによる詳細取得・DB非書込み・対象通知・同値再通知抑制を含める。 |
| 現在の保守情報、差分更新と再評価 | [`ResourceHealthIndexOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ResourceHealth/ResourceHealthIndexOwner.cs)、[`ResourceHealthWarningProjection`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ResourceHealth/ResourceHealthWarningProjection.cs) | [`ResourceHealthIndexOwnerTests`](../../../BeMusicSeeker.Tests/ResourceHealth/ResourceHealthIndexOwnerTests.cs)、[`ResourceHealthFullOwnedTargetFreshnessTests`](../../../BeMusicSeeker.Tests/ResourceHealth/ResourceHealthFullOwnedTargetFreshnessTests.cs) |
| LR2のパス・リソース互換性 | [`Lr2SongRowEnricher`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongRowEnricher.cs) | [`BmsLibraryLr2SongDbSyncTests`](../../../BeMusicSeeker.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs)、[`ChartWarningCollectionTests`](../../../BeMusicSeeker.Tests/Chart/ChartWarningCollectionTests.cs) |
| BMSの追加、相対パス補正、表現不能なパスの行の保持 | [`Lr2SongRowEnricher`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2/Lr2SongRowEnricher.cs)、[`BMSLibrary`](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs) | [`BmsLibraryInitializationInstallTests`](../../../BeMusicSeeker.Tests/Startup/BmsLibraryInitializationInstallTests.cs)、[`BmsLibraryInitializationLoadTests`](../../../BeMusicSeeker.Tests/Startup/BmsLibraryInitializationLoadTests.cs) |

## 関連資料

[共通譜面モデル](chart-model.md)、[導入推定](install-estimation.md)、[譜面ファイルの読取り](chart-file-reading.md)、[一覧表示](../ui/table-view.md)を参照します。
