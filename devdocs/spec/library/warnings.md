# 譜面の警告

## 目的と適用範囲

警告の種類、保持する場所、一覧の要約・詳細・強調表示を定めます。操作そのものの失敗報告と、譜面に付く警告は区別します。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

警告の「要約」は一覧セルの短いラベル、「詳細」はツールチップの全文、「分類」は関連する警告をまとめて更新する `Category` です。

## 仕様

### 状態の正本

警告は `ChartWarning` の集合です。BMSの保存主体、保留パッケージの項目、一時的な `ChartFile` の投影が、それぞれ担当する種類を保持します。自由文や表示用の別名プロパティを状態の正本にしません。BMSONは `bmson_song` と `ChartFileTransientState` から表示し、BMS用の操作アダプターへ戻しません。

通常一覧と導入直後の新規一覧のリソース不足は、保守情報から作る `ResourceHealthIndexSnapshot` を表示時に投影します。DBに保存する警告ではありません。LR2互換性は保守行の互換性情報から別の `Lr2Compatibility` 分類へ投影し、リソース状態の索引や無視設定へ混ぜません。

一覧の管理主体は、リソース状態を投影する表示の準備で索引を現在の入力へ合わせます。行の読取りや診断ログは索引を構築しません。未構築・失効状態のまま警告なしとして表示や警告順を確定しないため、行を再利用する表示と、警告・保守の表示だけの更新も同じ準備を行います。詳細は[一覧表示](../ui/table-view.md#絞込みと画面更新)に従います。

保守情報の既定値や、BMSON解析直後の文字コードだけの行は仮の値です。要求数と存在数が揃うまで計算済みのリソース状態として扱いません。DB由来の一部カテゴリだけの結果は、そのカテゴリの表示に使えます。

### 要約・詳細・強調

一覧の要約は `[種類数] ラベル1, ラベル2` の形で、`Priority` の昇順です。同じラベルは重複表示しませんが、種類数は有効な警告の種類を数えます。例えばWAVとBGAが不足していれば `[2] リソース不足` です。

詳細は `ShowInTooltip=true` のメッセージを優先順位順に改行して表示します。`DisplayWarning` と `WarningTooltipText` は詳細、`WarningDigestText` は要約です。一件でも `HighlightRow=true` なら行を強調します。

`ResourceHealth` の要約は、その譜面の導入先が未設定の場合だけ表示します。設定後も詳細は残します。導入先は `ChartFile.InstallDestination` を通してパッケージ・一時状態から明示的に重ね、BMSの保存行や単純な `FromBmsFile` に含まれると仮定しません。

### リソース状態の評価

一覧への所属判定は警告集合を変更せず、保守情報に基づく副作用のない索引で行います。BMSONは現在のMD5と一致する計算済みの保守情報を使い、未計算・旧ハッシュなら参照と現在のファイル状態から一時的に評価します。索引を優先し、必要な場合だけ実ファイルを確認します。

保留のリソース警告は導入前の配置に対する一時状態です。導入成功時に分類単位で解除し、通常・新規一覧では導入後の保守情報から表示します。保留の警告をBMSの保存主体へ書き戻しません。

起動時の保守情報読込みはDBの結果を適用するもので、全リソースの再検査ではありません。不足分を追加した後は、選択譜面または全譜面の再スキャンで再評価します。計算結果が既存行と同じならDBを書き直しません。

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
| `InstallEstimationMetadataMismatch` | `InstallEstimation` | 41 | `TITLE/ARTIST不一致` | true | 該当する警告がある場合 |
| `InstallEstimationReinstallNotImproved` | `InstallEstimation` | 42 | `再導入改善なし` | true | 該当する警告がある場合 |
| `InstalledDestinationAmbiguous` | `InstallEstimation` | 43 | `導入先複数` | true | 該当する警告がある場合 |
| `InstalledDestinationAutoAppliedAmbiguous` | `InstallEstimation` | 43 | `導入先複数` | true | 該当する警告がある場合 |
| `UnsupportedResourcePath` | `InstallEstimation` | 44 | `リソースパス非対応` | false | 該当する警告がある場合 |
| `InstalledDestinationResolveFailed` | `InstallEstimation` | 45 | `導入先不明` | false | 該当する警告がある場合 |
| `SourceSurfaceScanLimitExceeded` | `InstallEstimation` | 46 | `探索上限` | false | 該当する警告がある場合 |
| `InstallEstimationLowConfidence` | `InstallEstimation` | 47 | `導入先推定` | true | 該当する警告がある場合 |
| `AlreadyInstalled` | `InstalledState` | 50 | `既に導入済み` | false | 該当する警告がある場合 |
| `SingleBmsFile` | `PackageLayout` | 60 | `単体BMS` | false | 該当する警告がある場合 |
| `SingleBmsonFile` | `PackageLayout` | 60 | `単体BMSON` | false | 該当する警告がある場合 |
| `ResourceWavMissing` | `ResourceHealth` | 80 | `リソース不足` | false | 該当する警告があり、導入先が未設定の場合 |
| `ResourceBgaMissing` | `ResourceHealth` | 80 | `リソース不足` | false | 該当する警告があり、導入先が未設定の場合 |
| `ResourceMovieMissing` | `ResourceHealth` | 80 | `リソース不足` | false | 該当する警告があり、導入先が未設定の場合 |
| `ResourceStagefileMissing` | `ResourceHealth` | 83 | `画像不足` | false | 要約には出さず、ツールチップだけに表示 |
| `ResourceBackbmpMissing` | `ResourceHealth` | 83 | `画像不足` | false | 要約には出さず、ツールチップだけに表示 |
| `ResourceBannerMissing` | `ResourceHealth` | 83 | `画像不足` | false | 要約には出さず、ツールチップだけに表示 |

### 種類ごとの更新

| 種類・分類 | 生成と解除 |
| --- | --- |
| `InstallEstimation` | 推定結果、手動確定、導入成功、推定解除で分類単位に更新する。複数候補の自動適用と未適用は別の種類とし、候補を詳細に残す |
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
| 種類、優先順位、要約の重複抑止、詳細、強調 | [`ChartWarning`](../../../BeMusicSeeker/Models/ChartWarning.cs)、[`ChartWarningCollection`](../../../BeMusicSeeker/Models/ChartWarning.cs)、[`ChartWarningProjectionFormatter`](../../../BeMusicSeeker/ViewModels/ChartWarningProjectionFormatter.cs) | [`ChartWarningCollectionTests`](../../../BeMusicSeeker.Tests/ChartWarningCollectionTests.cs) |
| BMS・BMSON・保留の状態分離 | [`ChartFileProjection`](../../../BeMusicSeeker/Models/ChartFileProjection.cs)、[`ChartFileTransientState`](../../../BeMusicSeeker/Models/ChartFileTransientState.cs)、[`PackageChartEntry`](../../../BeMusicSeeker/Models/BmsLibraryInternal/PackageChartEntry.cs) | [`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/BmsLibraryPackageInstallServiceTests.cs)、[`ChartInfoInstallFailureRetryTests`](../../../BeMusicSeeker.Tests/ChartInfoInstallFailureRetryTests.cs) |
| 導入後の不足継続・解消と初回表示 | [`RegularChartListOwner`](../../../BeMusicSeeker/ViewModels/MainWindow/RegularChartListOwner.cs) | [`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/BmsLibraryPackageInstallServiceTests.cs) の `InstallPendingPackages_ProjectsCurrentResourceWarningsInNewAndNormalViews`。実導入したBMS/BMSONの一時警告解除、導入後保守、新規・通常一覧の要約と詳細を、構築済み・未構築の索引で確認する。 |
| 現在の保守情報、差分更新と再評価 | [`ResourceHealthIndexOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ResourceHealthIndexOwner.cs)、[`ResourceHealthWarningProjection`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ResourceHealthWarningProjection.cs) | [`ResourceHealthIndexOwnerTests`](../../../BeMusicSeeker.Tests/ResourceHealthIndexOwnerTests.cs)、[`ResourceHealthFullOwnedTargetFreshnessTests`](../../../BeMusicSeeker.Tests/ResourceHealthFullOwnedTargetFreshnessTests.cs) |
| LR2のパス・リソース互換性 | [`Lr2SongRowEnricher`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongRowEnricher.cs) | [`BmsLibraryLr2SongDbSyncTests`](../../../BeMusicSeeker.Tests/BmsLibraryLr2SongDbSyncTests.cs)、[`ChartWarningCollectionTests`](../../../BeMusicSeeker.Tests/ChartWarningCollectionTests.cs) |
| BMSの追加、相対パス補正、表現不能なパスの行の保持 | [`Lr2SongRowEnricher`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongRowEnricher.cs)、[`BMSLibrary`](../../../BeMusicSeeker/Models/BMSLibrary.cs) | [`BmsLibraryInitializationInstallTests`](../../../BeMusicSeeker.Tests/BmsLibraryInitializationInstallTests.cs)、[`BmsLibraryInitializationLoadTests`](../../../BeMusicSeeker.Tests/BmsLibraryInitializationLoadTests.cs) |

## 関連資料

[共通譜面モデル](chart-model.md)、[導入推定](install-estimation.md)、[譜面ファイルの読取り](chart-file-reading.md)、[一覧表示](../ui/table-view.md)を参照します。
