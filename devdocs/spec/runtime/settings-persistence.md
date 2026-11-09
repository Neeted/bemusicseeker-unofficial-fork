# 設定ファイルの保存と回復

## 目的と適用範囲

ポータブル設定ファイルの読込み、保存、破損時の起動、既存設定の移行を定めます。既存値を失った状態を正常な空設定として扱わないことを基本とします。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 所有者と保存先

正本は `config/user.config` です。単一起動用のミューテックスを取得する前に、設定の読込み・移行・回復・書込みを開始しません。アプリケーションのコンストラクターではパスとログを用意し、設定値へ先行アクセスしません。

保存は同じディレクトリの一時ファイルへ書き、成功した内容を置換または移動で公開します。成功を返した場合だけ保存完了です。読取り専用、共有違反、アクセス拒否、シリアライズ、構造不正、書込みの失敗は、対象パスと原因を持つ `PortableSettingsException` として返します。保存処理自身は画面を表示しません。後片付けの失敗で主原因を置き換えず、既存ファイルの内容を可能な限り保持します。

ファイルがない場合は既定値から開始できます。既存ファイルが読めない場合、不正なXMLや必要な構造の欠落は空設定ではありません。個々の設定キーがない場合の既定値適用とは区別します。

型付きのdouble・floatはOSの地域設定に依存せず保存し、異なる地域設定の新しいproviderからも同じ精度で復元します。数値を地域依存の中間文字列へ変換せず、既存providerの型付き保存を使います。未知の設定キーも保持します。

`SerializableVersion` の版文字列はInvariantの整数をドットで区切ります。LR2の表・環境設定で共通に読む `customfolder`・`titleflash`・`autoreload` は、前後空白と符号を含む既存整数文法をInvariantで解釈し、不正・欠落時の既定値を維持します。DB自動再読込みの手動専用値は整数 `0` として保存します。

`AutoInstallAfterAutomaticEstimation` はキー欠損時にfalseを使い、既存利用者の自動導入を更新だけで有効にしません。保存したtrue・falseは、新しいproviderでも同じ値へ復元します。操作と編集の契約は[設定仕様](settings.md#取り込み後の自動推定に続く導入)を参照します。

### 項目保存と公開

#### 共通の保存と公開

`Settings.Commit` は対象の項目だけを設定の保存処理（provider）へ直接渡し、成功した後に対象だけを既存の読取り口へ公開します。同値は保存せず、保存の失敗では確定値を変更しません。全体の `Reload` は保存処理に使いません。二つの媒体を扱う局所の担当は選択の保存と公開を分け、全ての媒体の成功後に整合した組を公開します。

同じ媒体の読取りから置換までを、保存処理の短い保存境界で直列化します。実処理・通知の待機の間は保持しません。起動時の移行、終了、位置の捕捉、右クリックなど、画面外の `Save` も変更済みの項目だけを渡し、別のラッパーが直近に保存した無関係な値と未知のキーを保ちます。

#### 動作モードと出力設定

動作モードの選択は、その回のモードと、確認後の最新の確定したLR2の三項目だけを保存し、無関係な確定値・未知のキーを保ちます。未設定・不正なパスもそのまま保持し、未確定の詳細を採用したり、XMLの成立をモードの保存の条件にしたりしません。保存の成功と、利用開始の必須設定の成立は別です。初回・修復中の設定の継続と、通常の利用中の終了・後継の起動は[設定仕様](settings.md#lr2の場所と設定xml)に従います。

出力設定は、その回のセクションの項目だけを保存・公開し、他の区分の未確定の候補と未公開の保存値を混ぜません。

- XMLの登録は、既存の `SaveRoots` の差分保存を一回使い、明示的な同値の保存だけ既存の `forceSave` で再保存します。
- 必要な全ての保存の後、受理した対象の配置を、LR2の所有者と構成の型付きの配置の公開へそのまま渡し、設定全体から再捕捉しません。
- プロファイルの未構築時の逐次の訂正は、旧い物理的な移行・削除・DBの整理を開始せず、現在の正本の該当する既知の完全一致の登録だけを調整します。

LR2のXMLの検索登録は、共有する所有者の採用済みの正本に、その回の差分だけを適用し、利用者のルートと管理出力のルートを保ちます。日常の表のプロパティの変更・集約編集・削除・ルートの同期も同じ正本を使い、外部の文書を再読込みせず、別の操作で成立済みの登録を戻しません。

#### 確定したBMS登録

確定した利用者のBMS登録の読取りと関連する項目の保存は、[`BmsRootRegistrationStore`](../../../BeMusicSeeker/Models/Settings/BmsRootRegistrationStore.cs) が担当します。

| 操作 | 保存と公開 |
| --- | --- |
| 設定画面の一覧の確定 | その回の最終の一覧と捕捉済みの導入先を関連する媒体へ保存し、全ての必要な保存の後だけ限定公開する。 |
| ライブラリの削除後の登録解除 | 最終の利用者の一覧だけを保存し、`BMSInstallDir` を書かない。単独動作は一覧と旧 `BMSRootPath` を一回で保存・公開し、LR2連携は共有する所有者の利用者の登録だけを保存・公開する。LR2の解除のために、変更しない `user.config` を再保存しない。 |

物理的な範囲から解除の対象・残存の一覧を求める判断は、ライブラリに置きます。

#### 走査で確認した登録の表記

走査で確認した登録の表記は、`SaveRootChanges` がその回の同じ場所の差分だけを保存します。

- 大文字小文字の違いも、通常の追加・削除の差分として扱い、同じ場所の置換は元の位置へ適用します。
- 欠落・親子・未確認の登録と管理の登録を全一覧で置き換えず、別の操作の保存済みで未公開の値を保持します。
- 単独動作は、既存の一覧と `BMSRootPath` を一組で保存し、変更したルートと同じ場所の `BMSInstallDir` だけをそろえます。
- 利用者の登録と管理の登録の双方で、確認したルート全体の接頭辞だけを適用し、通常・ROOT・追加の出力基点も同じ根拠の範囲へそろえます。未確認の後半部分と兄弟の基点には波及させず、物理的な移行は行いません。
- 保存直前のXMLの正本と設定の現在値を対象にするため、別の場所へ進んだ値を、古い操作の入力から再追加しません。公開済みの登録と既存の適用済みの配置は、それぞれの現在値へ同じ接頭辞だけを適用し、保存済みで未公開の配置を再捕捉しません。

順序は、XMLの保存、必要な設定項目の保存、全ての保存の成功後の項目の公開、LR2の所有者の登録・配置の公開、構成の適用済みの配置の差替え、成功の通知です。

#### LR2連携での媒体の順序と失敗

LR2連携は、XMLの差分保存、必要な設定項目の保存、その回の差分の限定公開の順です。

| 失敗の位置 | 結果 |
| --- | --- |
| XMLの保存 | DB処理へ進まない。 |
| XMLの成功後の設定の保存 | 保存した文書を保持し、その回の分を未公開とする。 |
| 登録の成立後のDBの失敗・取消 | 登録を巻き戻さない。 |
| 成功の通知 | 成立済みの保存を失敗へ読み替えない。 |

全ての必要な媒体の成立と限定公開の後に、小さい成功の通知を一回発行します。モデルは既存の親フォルダのキャッシュを失効させて既存のスケジューラーで通知し、設定は確定した一覧・導入先だけを通知します。編集中の候補は変更しません。

### 起動時の回復

最初の設定値取得より前に、設定ファイルを検証します。XMLの構文が壊れている場合に限り、元ファイルを衝突しない `user.config.broken-*` へ移動して保全し、設定を初期化する旨と保全先を警告した後に最小構造を作ります。既存の保全ファイルを上書きしません。壊れたXMLから一部の設定や表示言語だけを救出せず、同じ起動中に古い設定を自動移入しません。

保全名のUTC日時はGregorianの `yyyyMMddTHHmmssfffffffZ` とし、既存のGUID接尾辞を保ちます。共通作業ディレクトリのsession名もUTCの `yyyyMMddHHmmssfff` とPIDをInvariantで表し、既存のprefix・接尾辞・所有判定と回収条件を維持します。

保全後に新規作成が失敗した場合は起動失敗です。次回起動で正本がなく保全ファイルだけがある場合も、自動移入で回復を取り消さず、最小構造を作ります。有効な正本があれば常にそれを優先し、別の回復履歴ファイルを追加しません。

読込み、構造検証、保全、新規作成、旧設定の移入が失敗した場合は通常の設定取得・テーマ初期化・メイン画面作成へ進みません。終了時にはミューテックスを解放します。既に正しく読み込めた設定について、正規化値やバージョン記録の保存だけが失敗した場合は警告してメモリー上の正規化値で継続できます。ただし、その保存で再読込み自体が失敗した場合まで警告だけに格下げしません。

通常動作中の保存では、この破損回復を行いません。不正な設定ファイルは保存失敗として通知し、明示的な再試行を待ちます。

### 既存設定の正規化

`LegacyUserConfigMigrator` による旧設定の移入は、正本がないときだけ行います。既存・移入のいずれも、最初の設定値取得より前に同じ冪等な正規化を行います。

| 対象 | 現行の扱い |
| --- | --- |
| 再生パネルの旧 `MOVIE` ビット | `BMS` ビットへ変換し、コンパクト表示や未知のビットは保持する。保存値は `PlayerPanelState`（`BeMusicSeeker.Models`）の列挙子名または数値の文字列で、型の完全名を含まない。 |
| 廃止済みのブラウザー設定、一覧列設定キー | 既知の不要キーを削除する。 |
| 古い既定URL | 移入時に、対応する現行既定値へ置き換える。独自設定は対象外とする。 |
| LR2ルートが未設定の旧設定 | 設定XMLとsong DBの配置およびLR2本体を確認できる場合だけ補う。 |
| 対象となる旧版の言語設定 | 対応するOS文化圏を使い、非対応の場合は英語を使う。 |

正規化の保存に失敗しても、元のファイルを破壊せず、読み込めた値を正規化して利用します。通常起動のバージョン記録を、過去の段階ごとの移行手順として再実行しません。カスタムフォルダの再出力も過去のバージョンではなく、現在の出力欠落や内容変更で判断します。

### 保存失敗と実行中の設定

通常項目の保存失敗は当該項目の確定値・表示・次回入力を旧値に保ち、他項目を戻しません。局所グループは候補を保って明示再試行を待ちます。音量のpreviewは再生設定ストア内に保持し、操作終端で音量キーだけを保存します。保存失敗では最新の確定音量を表示と現在再生へ反映します。

LR2 XMLとuser.configの両方が必要な場合、成功済み媒体を巻き戻さず、必要媒体の成立前は稼働readerの旧公開値を保ちます。部分保存を全成功と通知しません。次の明示操作は、その時点の最新入力の関連項目を両媒体へ改めて保存でき、成功媒体省略の段階管理や過去要求の固定は行いません。入力修正を認め、未知キー・未知XML要素・管理登録・無関係な最新確定値を保ちます。通常閉鎖・再表示では媒体を再読込み・再保存せず、次回起動はdisk上の保存済み値を読みます。二媒体全体の原子性・自動巻戻し・修復台帳は追加しません。[設定の保存受付](settings.md#保存の受付と処理選択)を正本とし、出力先は必要な表header DBの保存も公開前に終えます。

BMTの異物理先変更は、新先の必要ファイルと管理情報を先に準備し、user.config保存成功で採用します。準備または設定保存の失敗では旧採用を維持し、新先の部分生成物の完全撤去は要求しません。採用後のURL切替・旧先整理の失敗では新採用を維持して部分失敗を通知します。[BMTのURL同期](../playlist/bmt-export.md#table-url同期)に従い、旧要求や成功段階を次操作へ保存せず、今回の最新入力を処理します。

再生位置の変更はメモリー上に保持し、停止、次曲、プレイヤー変更のたびに保存しません。局所項目保存には混ぜず、画面外の保存で変更済み項目として渡します。設定を閉じても実行中に得た再生位置を取り消しません。正常終了では最終位置を取得してから一回保存します。保存失敗は診断・通知しつつ終了を進め、強制終了時の最終位置の永続化は保証しません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 確認済み登録の差分保存・同所導入先・通知 | `BmsRootRegistrationStore.SaveRootChanges`、`Lr2RootChange` | `BmsRootRegistrationTests.RootSpellingChange_PreservesOtherRegistrationsAndPublishesAfterAllMedia` が両modeの登録・三種管理基点・関連/無関係導入先、XML/Settingsの部分失敗、未知属性保持と通知失敗を実媒体で確認する。`RootSpellingChange_DoesNotReAddReplacedSavedRootOrPublishUnrelatedPlacement` が保存済みB/公開Aの分離を確認する。`SettingsDialogBehaviorTests.FileDiffRootSpelling_PublishesConfirmedSettingsAndKeepsNextOutputPlacement` が標準compositionの既定provider・実file diff・次の再出力/差分、確定表示と候補・score取得世代/履歴の保持を確認する。`Lr2ConfigOwnerTests.CaseOnlyRootChange_ReplacesInPlaceAndPublishesOnlyItsDelta` が元順序と限定公開を確認する。 |
| 未設定LR2へのモード選択の限定保存 | [モード選択](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs)、[終了受付](../../../BeMusicSeeker/ViewModels/MainWindow/ShellShutdownWorkflowOwner.cs) | `SettingsDialogBehaviorTests.OperationModeWithoutActiveProfile_PreservesRawPathsAndAllowsExplicitSaveAfterFailure` が未設定・不正な生パス保持、実保存失敗と明示再操作、未知キー・別wrapperの無関係確定値を確認する。`MainWindowViewHostTests.MainWindowOperationModeRestart*` が実Close・プレイヤーの回収・後継起動を、既存の `PortableSettingsPersistenceTests` がprovider自体の限定公開を分担する。 |
| 確定BMS登録の共通保存・限定公開 | [`BmsRootRegistrationStore`](../../../BeMusicSeeker/Models/Settings/BmsRootRegistrationStore.cs) | `BmsRootRegistrationTests` が実config/XMLの片側失敗・最新/同値明示保存・未知XML・roots-only保存を、`SettingsDialogBehaviorTests.Lr2BmsConfirmation_PartialSaveCloseKeepsPublishedInputsUntilExplicitSave` が候補・通常Close/reopen・必要差分終端を分担する。 |
| 既存内容の保全と実際のI/O失敗 | [`PortableSettingsProvider`](../../../BeMusicSeeker/Properties/PortableSettingsProvider.cs) | [`PortableSettingsPersistenceTests`](../../../tests/BeMusicSeeker.Core.Tests/Settings/PortableSettingsPersistenceTests.cs) |
| 型付きdouble・floatの地域非依存な保存精度 | `PortableSettingsProvider`、[`Settings`](../../../BeMusicSeeker/Properties/Settings.cs) | `PortableSettingsPersistenceTests.SaveRoundTripsThroughFreshGeneratedSettingsAndPreservesUnknownKeys` は異文化で実保存後、新しいproviderからdouble・floatのbit一致、既存の有効値・未知キーと一時ファイル回収を確認する。 |
| 共通整数と管理名の地域非依存 | `LR2Config`、`PortableSettingsStartupFile`、`TempDirectoryPublisher` | `LR2ConfigTests.EnsureDatabaseAutoReloadManualOnly_ChangesExistingAutoReloadMode` は異文化の実XML整数・手動専用保存・新しい文書の読戻し、`PortableSettingsPersistenceTests.StartupCorruptionWarnsWithPreservedBackupBeforeCreatingDefaultsAndNeverImportsLegacy` はGregorian保全名と原文・警告順、`TempDirectoryPublisherTests.GetCreatesManagedDirectory` はsession名と実ディレクトリの所有・回収を確認する。 |
| 自動推定後導入のキー欠損と有効・無効の保存 | [`Settings`](../../../BeMusicSeeker/Properties/Settings.cs)、`PortableSettingsProvider` | `PortableSettingsPersistenceTests.MissingFileAndIndividualKeyUseDefaults` は新規と旧設定の欠損時OFF、`SaveRoundTripsThroughFreshGeneratedSettingsAndPreservesUnknownKeys` はON/OFFを新providerで読み戻すことを確認する。 |
| 起動時の致命的失敗・警告・回復 | [`ApplicationSettingsLifecycle`](../../../BeMusicSeeker/Models/Settings/ApplicationSettingsLifecycle.cs) | [`ApplicationSettingsLifecycleTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Settings/ApplicationSettingsLifecycleTests.cs) |
| 列設定のXML互換（クラス名・プロパティ名・列挙名・入れ子の構造を保ち、保存済みの幅・表示位置・表示状態を既定値へ戻さない） | [`Settings`](../../../BeMusicSeeker/Properties/Settings.cs)、[`CustomTableColumnSettings`](../../../BeMusicSeeker/Models/Settings/CustomTableColumnSettings.cs)、[`PlaylistSummaryColumnSettings`](../../../BeMusicSeeker/Models/Settings/PlaylistSummaryColumnSettings.cs) | [`CustomTableColumnSettingsTests`](../../../tests/BeMusicSeeker.Core.Tests/CustomTable/CustomTableColumnSettingsTests.cs) が、列設定の型を `BeMusicSeeker.ViewModels` に置いていた版が保存した実XMLの固定入力（[`saved-column-settings.user.config`](../../../tests/BeMusicSeeker.Core.Tests/CustomTable/TestData/saved-column-settings.user.config)）を読んで利用者の値を保ち、保存し直した要素名・列挙名・`serializeAs` が同じことを確認する。固定入力は保存形式を意図して変え移行仕様を定めた場合も旧形式の入力として残す。 |
| 旧設定の移入と正規化 | [`LegacyUserConfigMigrator`](../../../BeMusicSeeker/Properties/LegacyUserConfigMigrator.cs) | [`PortableSettingsMigrationTests`](../../../tests/BeMusicSeeker.Core.Tests/Settings/PortableSettingsMigrationTests.cs)、[`PlayerPanelStateSettingsCompatibilityTests`](../../../tests/BeMusicSeeker.Windows.Tests/Playback/PlayerPanelStateSettingsCompatibilityTests.cs) |
| LR2 XMLの一回保存・後の確定登録の保全 | [局所出力確定](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs)、[`PlaylistCustomFolderOutputMaintenanceOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistCustomFolderOutputMaintenanceOwner.cs)、[表プロパティ保存](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertySaveService.cs)、[集約編集](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistSummaryBulkEdit.cs)、[表削除](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistRemovalWorkflowOwner.cs) | [`SettingDialogOutputConfirmationTests.OutputConfirmation_LatestSelectionResavesBothMediaAfterPartialFailure`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingDialogOutputConfirmationTests.cs) は部分保存後の最新入力と同値明示保存を実媒体で確認する。表操作の保存・出力は [`BmsPlaylistMigrationAndRegistrationTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs) / [`BmsPlaylistMigrationAndRegistrationTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs)、[`PlaylistWorkspaceActionWorkflowTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs) / [`PlaylistWorkspaceActionWorkflowTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs)、[`PlaylistSummaryBulkEditTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistSummaryBulkEditTests.cs) / [`PlaylistSummaryBulkEditTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/PlaylistSummaryBulkEditTests.cs) が分担する。 |
| BMT準備後の設定採用・採用前後の実保存失敗 | [設定の局所確定](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs)、[`PlaylistBmtOutputOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistBmtOutputOwner.cs) | `SettingDialogEditCompletionTests.BmtSettingsAdoption_PreservesAdoptedMediaAndAllowsCurrentNextInput`（Windows）が実ファイル/manifest準備失敗、user.config置換失敗、採用後のURL/整理失敗をfresh readerと媒体で区別する。 |
| 保存時の画面と値の保持 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs) | [`SettingsDialogBehaviorTests`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingsDialogBehaviorTests.cs) |
| 通常配置のportable正本の読込と終了時の保持 | [`PortableSettingsProvider`](../../../BeMusicSeeker/Properties/PortableSettingsProvider.cs)、[`ApplicationSettingsLifecycle`](../../../BeMusicSeeker/Models/Settings/ApplicationSettingsLifecycle.cs) | [`NormalStartupDesktopTests`](../../../tests/BeMusicSeeker.Desktop.E2E.Tests/Startup/NormalStartupDesktopTests.cs)（SC-16）が、事前に置いた `config/user.config` の言語・テーマ・BMS登録を配布物の通常起動で読み、正常終了後も値と未知キーを保持することを確認する。 |

## 関連資料

[設定の編集と反映](settings.md)、[終了処理](shutdown.md)を参照します。
