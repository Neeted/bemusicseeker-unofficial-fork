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

`Settings.Commit` は対象項目だけをproviderへ直接渡し、成功後に対象だけを既存readerへ公開します。同値は保存せず、保存失敗では確定値を変更しません。全体 `Reload` は保存処理に使いません。二媒体を扱う局所担当は選択保存と公開を分け、全媒体成功後に整合組を公開します。

動作モードの選択は今回のモードと確認後の最新確定LR2三項目だけを保存し、無関係な確定値・未知キーを保ちます。未設定・不正なパスもそのまま保持し、未確定詳細を採用したりXMLの成立をモード保存の条件にしたりしません。保存成功と利用開始の必須設定成立は別で、初回・修復中の設定継続と通常利用中の終了・後継起動は[設定仕様](settings.md#lr2の場所と設定xml)に従います。

出力設定は今回のsectionの項目だけをPersist・Publishし、他区分の未確定候補と未公開保存値を混ぜません。XML登録は既存 `SaveRoots` の差分保存を一回使い、明示同値保存だけ既存forceSaveで再保存します。必要な全保存後、受理したtargetをownerとCompositionの型付き配置公開へそのまま渡し、Settings全体から再捕捉しません。profile未構築時の逐次訂正は旧物理移行・削除・DB pruneを開始せず、現在正本の当該既知exact登録だけを調整します。

LR2 XMLの検索登録は共有所有者の採用済み正本に今回の差分だけを適用し、利用者ルートと管理出力rootを保ちます。日常の表プロパティ変更・集約編集・削除・root同期も同じ正本を使い、外部文書を再読込みせず、別操作の成立済み登録を戻しません。

同じ媒体の読取りから置換までをproviderの短い保存境界で直列化します。実処理・通知待ちの間は保持しません。起動移行、終了、位置捕捉、右クリックなど画面外の `Save` も変更済み項目だけを渡し、別wrapperが直近に保存した無関係な値と未知キーを保ちます。

確定利用者BMS登録の読取りと関連項目保存は [`BmsRootRegistrationStore`](../../../BeMusicSeeker/Models/Settings/BmsRootRegistrationStore.cs) が担当します。Settingsの一覧確定は今回の最終一覧と捕捉済み導入先を関連媒体へ保存し、全必要保存後だけ限定公開します。ライブラリ削除後の登録解除は最終利用者一覧だけを保存し、BMSInstallDirを書きません。standaloneは一覧と旧BMSRootPathを一回で保存・公開し、LR2は共有ownerの利用者登録だけを保存・公開します。LR2解除のために変更しないuser.configを再保存しません。物理scopeから解除対象・残存一覧を求める判断はLibraryに置きます。

走査で確認した登録表記は `SaveRootChanges` が今回の同所差分だけを保存します。大小文字差も通常の追加・削除差分として扱い、同所の置換は元位置へ適用します。欠落・親子・未確認の登録と管理登録を全一覧で置換せず、別操作の保存済み未公開値を保持します。standaloneは既存の一覧と `BMSRootPath` を一組で保存し、当該変更rootと同所の `BMSInstallDir` だけを揃えます。 利用者登録と管理登録の双方で確認root全体のprefixだけを適用し、normal/root/additional基点も同じ根拠の範囲へ揃えます。未確認suffixと兄弟基点には波及させず、物理移行は行いません。保存直前のXML正本とSettings現在値を対象にするため、別場所へ進んだ値を古い操作入力から再追加しません。公開済み登録と既存applied配置はそれぞれの現在値へ同じprefixだけを適用し、保存済み未公開配置を再捕捉しません。順序はXML保存、必要Settings項目のPersist、全保存成功後の項目Publish、ownerの登録・配置Publish、Compositionのapplied配置の純差替え、成功通知です。

LR2はXMLの差分保存、必要なSettings項目の保存、今回差分の限定公開の順です。XML失敗ではDB処理へ進まず、XML成功後のSettings失敗では保存文書を保持して今回分を未公開とします。登録成立後にDB失敗・取消となっても登録は巻き戻しません。全必要媒体の成立と限定公開後に小さい成功通知を一回発行します。通知失敗で成立済み保存を失敗へ読み替えません。モデルは既存の親フォルダcacheを失効して既存schedulerで通知し、Settingsは確定一覧・導入先だけを通知します。編集中候補は変更しません。

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
| 再生パネルの旧 `MOVIE` ビット | `BMS` ビットへ変換し、コンパクト表示や未知のビットは保持する。 |
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
| 未設定LR2へのモード選択の限定保存 | [モード選択](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs)、[終了受付](../../../BeMusicSeeker/ViewModels/MainWindow/ShellShutdownWorkflowOwner.cs) | `SettingsDialogBehaviorTests.OperationModeWithoutActiveProfile_PreservesRawPathsAndAllowsExplicitSaveAfterFailure` が未設定・不正な生パス保持、実保存失敗と明示再操作、未知キー・別wrapperの無関係確定値を確認する。`MainWindowViewHostTests.MainWindowOperationModeRestart*` が実Close・player回収・後継起動を、既存の `PortableSettingsPersistenceTests` がprovider自体の限定公開を分担する。 |
| 確定BMS登録の共通保存・限定公開 | [`BmsRootRegistrationStore`](../../../BeMusicSeeker/Models/Settings/BmsRootRegistrationStore.cs) | `BmsRootRegistrationTests` が実config/XMLの片側失敗・最新/同値明示保存・未知XML・roots-only保存を、`SettingsDialogBehaviorTests.Lr2BmsConfirmation_PartialSaveCloseKeepsPublishedInputsUntilExplicitSave` が候補・通常Close/reopen・必要差分終端を分担する。 |
| 既存内容の保全と実際のI/O失敗 | [`PortableSettingsProvider`](../../../BeMusicSeeker/Properties/PortableSettingsProvider.cs) | [`PortableSettingsPersistenceTests`](../../../BeMusicSeeker.Tests/Settings/PortableSettingsPersistenceTests.cs) |
| 型付きdouble・floatの地域非依存な保存精度 | `PortableSettingsProvider`、[`Settings`](../../../BeMusicSeeker/Properties/Settings.cs) | `PortableSettingsPersistenceTests.SaveRoundTripsThroughFreshGeneratedSettingsAndPreservesUnknownKeys` は異文化で実保存後、新しいproviderからdouble・floatのbit一致、既存の有効値・未知キーと一時ファイル回収を確認する。 |
| 共通整数と管理名の地域非依存 | `LR2Config`、`PortableSettingsStartupFile`、`TempDirectoryPublisher` | `LR2ConfigTests.EnsureDatabaseAutoReloadManualOnly_ChangesExistingAutoReloadMode` は異文化の実XML整数・手動専用保存・新しい文書の読戻し、`PortableSettingsPersistenceTests.StartupCorruptionWarnsWithPreservedBackupBeforeCreatingDefaultsAndNeverImportsLegacy` はGregorian保全名と原文・警告順、`TempDirectoryPublisherTests.GetCreatesManagedDirectory` はsession名と実ディレクトリの所有・回収を確認する。 |
| 自動推定後導入のキー欠損と有効・無効の保存 | [`Settings`](../../../BeMusicSeeker/Properties/Settings.cs)、`PortableSettingsProvider` | `PortableSettingsPersistenceTests.MissingFileAndIndividualKeyUseDefaults` は新規と旧設定の欠損時OFF、`SaveRoundTripsThroughFreshGeneratedSettingsAndPreservesUnknownKeys` はON/OFFを新providerで読み戻すことを確認する。 |
| 起動時の致命的失敗・警告・回復 | [`ApplicationSettingsLifecycle`](../../../BeMusicSeeker/Models/Settings/ApplicationSettingsLifecycle.cs) | [`ApplicationSettingsLifecycleTests`](../../../BeMusicSeeker.Tests/Settings/ApplicationSettingsLifecycleTests.cs) |
| 旧設定の移入と正規化 | [`LegacyUserConfigMigrator`](../../../BeMusicSeeker/Properties/LegacyUserConfigMigrator.cs) | [`PortableSettingsMigrationTests`](../../../BeMusicSeeker.Tests/Settings/PortableSettingsMigrationTests.cs)、[`PlayerPanelStateSettingsCompatibilityTests`](../../../BeMusicSeeker.Tests/Playback/PlayerPanelStateSettingsCompatibilityTests.cs) |
| LR2 XMLの一回保存・後の確定登録の保全 | [局所出力確定](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs)、[`PlaylistCustomFolderOutputMaintenanceOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistCustomFolderOutputMaintenanceOwner.cs)、[表プロパティ保存](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertySaveService.cs)、[集約編集](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistSummaryBulkEdit.cs)、[表削除](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistRemovalWorkflowOwner.cs) | [`SettingDialogEditCompletionTests.OutputConfirmation_LatestSelectionResavesBothMediaAfterPartialFailure`](../../../BeMusicSeeker.Tests/Settings/SettingDialogOutputConfirmationTests.cs) は部分保存後の最新入力と同値明示保存を実媒体で確認する。表操作の保存・出力は [`BmsPlaylistMigrationAndRegistrationTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs)、[`PlaylistWorkspaceActionWorkflowTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs)、[`PlaylistSummaryBulkEditTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryBulkEditTests.cs) が分担する。 |
| BMT準備後の設定採用・採用前後の実保存失敗 | [設定の局所確定](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs)、[`PlaylistBmtOutputOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistBmtOutputOwner.cs) | `SettingDialogEditCompletionTests.BmtSettingsAdoption_PreservesAdoptedMediaAndAllowsCurrentNextInput` が実ファイル/manifest準備失敗、user.config置換失敗、採用後のURL/整理失敗をfresh readerと媒体で区別する。 |
| 保存時の画面と値の保持 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs) | [`SettingsDialogBehaviorTests`](../../../BeMusicSeeker.Tests/Settings/SettingsDialogBehaviorTests.cs) |

## 関連資料

[設定の編集と反映](settings.md)、[終了処理](shutdown.md)を参照します。
