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

`AutoInstallAfterAutomaticEstimation` はキー欠損時にfalseを使い、既存利用者の自動導入を更新だけで有効にしません。保存したtrue・falseは、新しいproviderでも同じ値へ復元します。操作と編集の契約は[設定仕様](settings.md#取り込み後の自動推定に続く導入)を参照します。

### 項目保存と公開

`Settings.Commit` は対象項目だけをproviderへ直接渡し、成功後に対象だけを既存readerへ公開します。同値は保存せず、保存失敗では確定値を変更しません。全体 `Reload` は保存処理に使いません。二媒体を扱う局所担当は選択保存と公開を分け、全媒体成功後に整合組を公開します。

LR2 XMLの検索登録は最新文書のユーザールートと管理出力rootを保ち、関連する登録変更を同じ文書の一回保存へまとめます。日常の表プロパティ変更・集約編集・削除・root同期も書込み時に最新文書を読み、局所確定前から保持した文書で別の登録を戻しません。

同じ媒体の読取りから置換までをproviderの短い保存境界で直列化します。実処理・通知待ちの間は保持しません。起動移行、終了、位置捕捉、右クリックなど画面外の `Save` も変更済み項目だけを渡し、別wrapperが直近に保存した無関係な値と未知キーを保ちます。

### 起動時の回復

最初の設定値取得より前に、設定ファイルを検証します。XMLの構文が壊れている場合に限り、元ファイルを衝突しない `user.config.broken-*` へ移動して保全し、設定を初期化する旨と保全先を警告した後に最小構造を作ります。既存の保全ファイルを上書きしません。壊れたXMLから一部の設定や表示言語だけを救出せず、同じ起動中に古い設定を自動移入しません。

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

再生位置の変更はメモリー上に保持し、停止、次曲、プレイヤー変更のたびに保存しません。局所項目保存には混ぜず、画面外の保存で変更済み項目として渡します。設定を閉じても実行中に得た再生位置を取り消しません。正常終了では最終位置を取得してから一回保存します。保存失敗は診断・通知しつつ終了を進め、強制終了時の最終位置の永続化は保証しません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 既存内容の保全と実際のI/O失敗 | [`PortableSettingsProvider`](../../../BeMusicSeeker/Properties/PortableSettingsProvider.cs) | [`PortableSettingsPersistenceTests`](../../../BeMusicSeeker.Tests/Settings/PortableSettingsPersistenceTests.cs) |
| 自動推定後導入のキー欠損と有効・無効の保存 | [`Settings`](../../../BeMusicSeeker/Properties/Settings.cs)、`PortableSettingsProvider` | `PortableSettingsPersistenceTests.MissingFileAndIndividualKeyUseDefaults` は新規と旧設定の欠損時OFF、`SaveRoundTripsThroughFreshGeneratedSettingsAndPreservesUnknownKeys` はON/OFFを新providerで読み戻すことを確認する。 |
| 起動時の致命的失敗・警告・回復 | [`ApplicationSettingsLifecycle`](../../../BeMusicSeeker/Models/Settings/ApplicationSettingsLifecycle.cs) | [`ApplicationSettingsLifecycleTests`](../../../BeMusicSeeker.Tests/Settings/ApplicationSettingsLifecycleTests.cs) |
| 旧設定の移入と正規化 | [`LegacyUserConfigMigrator`](../../../BeMusicSeeker/Properties/LegacyUserConfigMigrator.cs) | [`PortableSettingsMigrationTests`](../../../BeMusicSeeker.Tests/Settings/PortableSettingsMigrationTests.cs)、[`PlayerPanelStateSettingsCompatibilityTests`](../../../BeMusicSeeker.Tests/Playback/PlayerPanelStateSettingsCompatibilityTests.cs) |
| LR2 XMLの一回保存・後の確定登録の保全 | [局所出力確定](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs)、[`PlaylistCustomFolderOutputMaintenanceOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistCustomFolderOutputMaintenanceOwner.cs)、[表プロパティ保存](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertySaveService.cs)、[集約編集](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistSummaryBulkEdit.cs)、[表削除](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistRemovalWorkflowOwner.cs) | [`SettingDialogEditCompletionTests.OutputConfirmation_LatestSelectionResavesBothMediaAfterPartialFailure`](../../../BeMusicSeeker.Tests/Settings/SettingDialogOutputConfirmationTests.cs) は部分保存後の最新入力と同値明示保存を実媒体で確認する。表操作の保存・出力は [`BmsPlaylistMigrationAndRegistrationTests`](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistMigrationAndRegistrationTests.cs)、[`PlaylistWorkspaceActionWorkflowTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs)、[`PlaylistSummaryBulkEditTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistSummaryBulkEditTests.cs) が分担する。 |
| 保存時の画面と値の保持 | [`SettingsDialogViewModel`](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs) | [`SettingsDialogBehaviorTests`](../../../BeMusicSeeker.Tests/Settings/SettingsDialogBehaviorTests.cs) |

## 関連資料

[設定の編集と反映](settings.md)、[終了処理](shutdown.md)を参照します。
