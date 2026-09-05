# BMT manifest の所有情報と失敗通知 — 完了記録

2026-09-06 完了。現行の実装契約は [playlist-data-and-export-flow.md](../spec/playlist-data-and-export-flow.md) の Managed Cleanup と tableURL 同期を正本とする。以下は実装前に凍結した判断・テスト契約と、この変更固有の検証証跡を保存する履歴である。

## 目的と承認範囲

- 削除失敗で物理ファイルの所有情報を失わず、次の通常 cleanup で回収できるようにする。
- manifest を同じディレクトリの一時ファイルから置換し、保存失敗で既存台帳を壊さない。
- manifest の読取り・破損・保存失敗を隠さず、原本を保全して対象出力先の処理を停止し通知する。
- ユーザーは推奨方針による実装と必要な commit を承認済み。出力先変更時の旧フォルダの削除失敗は残留を許容し、通知する。旧フォルダを自動回収しない。
- 自動隔離、修復、永続的な再試行キュー、バックアップ、複数ファイルのトランザクション保証は追加しない。

## 調査時点

- base: `62fb76ee453d373ae0224ba5892c447ccf816192`。開始時の worktree は clean。
- `BmtTableExportService`: CleanupManagedFiles、RemoveManagedPlaylist、UpdateManagedPlaylistUrlOwnership、UpdateManifest、AddManagedFile の削除結果と台帳更新。
- `PlaylistBmtOutputOwner`: 全体出力、個別出力・削除、旧出力先 cleanup、URL 同期、通知。
- `devdocs/spec/playlist-data-and-export-flow.md`: files は物理 cleanup 台帳、playlists は現行 URL 台帳。管理外ファイル、共有参照、URL-only、旧 manifest、長いパスの互換性を維持する。

## Production ingress と観測対象

- プレイリスト削除: PlaylistRemovalWorkflowOwner → BMSPlaylist.RemoveBMSTable → QueueBeatorajaBmtRemoveForTable → RemoveManagedPlaylist。
- BMT OUTPUT OFF / 編集: PlaylistWorkspaceViewModel → owner の個別出力 queue → RemoveManagedPlaylist / UpdateManagedPlaylistUrlOwnership / ExportTableData。
- 設定変更: SettingsDialogViewModel → QueueBeatorajaBmtExportAll(cleanupTablePath) → CleanupManagedFiles。
- 起動・再同期・全出力: owner → CreateExportPlan → ExportTableDataSet → UpdateManifest。
- OS のファイルロック・アクセス拒否で削除や保存が失敗すると、残留ファイル、manifest の所有情報、完了件数、URL 同期、利用者通知に影響する。

## 設計判断

1. `files` に未削除ファイルを残す。`playlists` は現在の出力対象へ更新する。専用の pending 状態は増やさない。
2. 全体更新は現在の出力ファイルと未削除の旧ファイルを保存する。cleanup を行わない場合も旧 files を忘却しない。
3. 削除失敗は完了件数に加えない。不存在は cleanup 完了として扱い、存在確認の false を I/O 成功の根拠にしない。
4. 全 cleanup の部分失敗では台帳を保存し、未削除がある限り manifest を消さない。
5. 削除の部分失敗は結果へ集約し、owner がファイル操作の lock を抜けた後に通知する。読取り・保存の失敗も通知する。
6. URL 同期の要否を物理ファイルの書込み・削除件数に依存させない。
7. 既存 LongPathFileSystem の同一ディレクトリ置換を利用し、直接上書きへの fallback は設けない。
8. 不正 manifest を新規出力で上書きする前に読取り失敗として拒否する。BMT 出力ファイルと manifest 全体を一括 commit する保証は対象外。
9. 通知は owner の immutable な失敗報告を既存 PlaylistOperationNotificationPresentationRequested へ接続する。AsyncLocal の元操作 session をバックグラウンド処理へ流用しない。
10. ユーザー回答により、既知の旧形式のみ許容し、不正な構造・未対応形式は原本を残して停止・通知する。部分読取りでの続行はしない。

### 既知形式の受理範囲

- top-level は object、`files` は必須 array（空可）、`playlists` は省略可、存在時は object（null は不可）。files-only の旧形式は受理する。
- `schemaVersion` 省略は旧形式 0 とし、明示整数 0 / 1 / 2 を受理する。未知 schema は拒否する。exporterVersion の差異は schema 不明と混同せず cache miss とする。
- files の各要素は非空 string の安全な出力先直下 `.bmt` basename に限る。path component を落として読み替えない。重複するファイル名は大文字小文字を無視してまとめてよい。
- playlists の key は非空、value は object、url は非空 string。file は省略 / null / 空 string を URL-only として許容し、それ以外は同じ basename 検証を行う。playlist が参照する file は物理台帳へ取り込む。
- JSON object の重複 property は拒否する。未知の追加 metadata は許容する。既知の任意 cache field は省略 / null の既存 default を許容し、値がある場合は宣言された string / 整数型を要求する。無関係な cache 値の domain validation は増やさない。
- RemovedCount はファイル cleanup 完了件数（既不存在を含む）。失敗、共有参照による非削除、manifest 自体の削除は含めない。

## 作業単位と検証

- 計画点検: plan-clarifier 完了。旧形式の validation 境界はユーザー回答と上記の具体化により確定済み。
- テスト契約: test-contract-designer の独立 oracle と negative control を承認して凍結する。
- 実装: persistence、failure、ownership を一体で扱うため frontier worker 1 つ。service、owner、既存 VM 通知接続、resources、6 言語、feature spec、関連 fixture を同じ単位で扱う。ファイル数は翻訳の同期分を含むため、並列 worker への分割は行わない。
- 候補 fixture: BmtTableExportServiceTests、BmsPlaylistMigrationAndRegistrationTests（既存 BMT owner queue の scheduler 捕捉を利用）、PlaylistWorkspacePersistenceCommandTests（既存通知 event）、LocalizationResourceParityTests。BmsPlaylistCustomFolderOutputTests には今回利用できる BMT owner queue coverage がないため追加先にはしない。既存 GUID temp directory、process-local 設定の所有、Task/event 完了 signal を利用する。
- 反復: 関連 filter の verify-refactor.ps1 -Mode Quick。修正前の red、または新しい seam が必要な場合は targeted negative control。
- 最終: Functional 1 回、git diff --check、UTF-8/LF/参照確認、その後 worktree を凍結して fresh static review。Full、アプリ起動、release は対象外。
- 再計画条件: 新しい永続状態・復旧動作が必要、承認済み旧形式との互換性を維持できない、通知を lock 外へ出せない、production ingress で再現できない test seam が必要。

### 実装 ownership

単一 frontier worker が次を所有する。root は計画・統合・最終検証だけを担当する。

- `BeMusicSeeker/Models/BmtTableExportService.cs`: 物理所有台帳、削除結果、読取り検証、atomic 保存、旧失敗隠蔽 route の退役。
- `BeMusicSeeker/Models/BmsLibraryInternal/PlaylistBmtOutputOwner.cs`: 結果と例外の集約、URL 同期、lock 外の immutable 失敗報告。必要な BMT 固有 result 型はこの既存 file 内に閉じる。
- `BeMusicSeeker/ViewModels/MainWindowViewModel.cs`: BMT 失敗通知 callback の composition のみ。
- `BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.Mutations.cs`: 既存 notification presentation event への接続のみ。
- `BeMusicSeeker/Properties/Resources.resx`、`BeMusicSeeker/Properties/Resources.cs`、`lang/en-US.json`、`lang/fr-FR.json`、`lang/ja-JP.json`、`lang/ko-KR.json`、`lang/zh-CN.json`、`lang/zh-TW.json`: 利用者向け失敗通知の言語同期。
- `BeMusicSeeker.Tests/BmtTableExportServiceTests.cs`、`BeMusicSeeker.Tests/BmsPlaylistMigrationAndRegistrationTests.cs`、`BeMusicSeeker.Tests/PlaylistWorkspacePersistenceCommandTests.cs`、`BeMusicSeeker.Tests/LocalizationResourceParityTests.cs`: packet に対応する既存 coverage の拡張。
- `devdocs/spec/playlist-data-and-export-flow.md`: 現行契約、受け入れる残留、verification map の更新。

上記以外の production / test / infrastructure は事前に root へ範囲不足を返す。新たな共通 filesystem 抽象化、notification framework、scheduler は追加しない。変更する API の XML documentation と説明コメント、仕様は原則日本語にする。

### 検証入口

反復 filter: `FullyQualifiedName~BmtTableExportServiceTests|FullyQualifiedName~BmsPlaylistMigrationAndRegistrationTests|FullyQualifiedName~PlaylistWorkspacePersistenceCommandTests|FullyQualifiedName~LocalizationResourceParityTests`。失敗再現ではこのうち対象 test 名だけへ絞る。

worker は production 修正前に実行可能な台帳保持・破損拒否・保存失敗の regression を red 確認し、新しい result / callback が必要な assertion は packet の targeted negative control を使う。最後に上記 filter の Quick を実行して handoff する。root は Functional と fresh review を担当し、worker と検証を同時実行しない。

## Test Contract Packet

Packet ID: `BMT-MANIFEST-FAILURE-1`。change class: bugfix。2026-09-06 に test-contract-designer の Phase A / B を root が承認し、以下を実装前に凍結した。authority / reachability gap はなし。

独立 authority はユーザー承認、明示 root decision、feature spec の Managed Cleanup / tableURL 同期。Phase A では production body、既存 expected / snapshot、翻訳値、runtime output を oracle に使っていない。Phase B は到達経路、signature、fixture、helper、完了 signal の確認だけに用いた。validation の具体化は上記 root decision による。

| Contract ID | 必須 invariant / outcome | 誤実装と evidence | 配置 |
| --- | --- | --- | --- |
| BMT-O | 5 経路すべてで削除失敗ファイルを files に保持し、playlists は現行対象へ更新。cleanup=false でも旧 files 保持。全 cleanup 部分失敗では manifest 保持。RemovedCount は成功・既不存在のみ。後の通常 cleanup で回収可能 | delete-share を拒否した BMT と削除可能 BMT を混在。試みただけの台帳除去 / 件数加算 / currentFiles のみ保存 / manifest 無条件削除を検出。既存 consumer で base red | BmtTableExportServiceTests を extend |
| BMT-P | 同じ directory の temp から atomic publish。置換失敗時に旧 manifest bytes と失敗結果を保持。直接上書き fallback なし。所有 temp を後始末 | manifest の読取り・直接書込みを許し delete-share を拒否した handle で置換を失敗させ、操作前後 bytes を比較。read failure を atomic failure の evidence にしない。既存 consumer で base red | 同上 |
| BMT-V | 上記既知形式以外、構文破損、読取り不能は原本と既存 BMT を保全して拒否。新 BMT も生成前に停止。部分 salvage なし | corrupt bytes、型不正、未知 schema、重複 property、危険 filename、正常 / 異常の混在、読取拒否 handle。個別 / 全出力 consumer で base red | 同上 |
| BMT-C | schema なし / 0 / 1 / 2、files-only、URL-only、共有 file、管理外 file、長パスを維持。contentHash は no-op 根拠にしない | 既存近傍 coverage を保持して不足だけ extend。base green は可。変更部の過剰拒否 / shared-reference 無視 / 長パス迂回は対象 negative control で検出 | 同上 |
| BMT-N | 物理件数ゼロでも確定した現行 URL 所有権を同期。削除 / 読取 / 保存 failure が対象と原因を識別可能な通知へ到達。owner と manifest の file-operation lock 外で配送。元 AsyncLocal session の終了に依存しない | 実 scheduler Func<Task> を捕捉し元 session 終了後に await、config と failure receipt / Workspace event を観測。Changed による同期省略、callback 脱落、元 session 依存を targeted mutant で検出。lock 外は fresh static review でも確認 | BmsPlaylistMigrationAndRegistrationTests、PlaylistWorkspacePersistenceCommandTests を extend |
| BMT-L | resx / accessor / 6 言語の key parity、非空、必要な対象・原因 placeholder と format 成立 | 既存 format helper を extend。必須 placeholder / key 欠落の negative control。翻訳全文の exact copy はしない | LocalizationResourceParityTests を extend |

### 許容差と対象外

- JSON の整形・配列順、temp 名、例外 / result / receipt の具体型、通知文章・集約単位・配送 timing は固定しない。
- 旧出力先の残留と、独立する新出力先の処理継続を許容する。旧フォルダの自動回収は追加しない。
- 保存失敗前に変更済みの BMT 本体の rollback、config 保存の新しい再試行、BMT と manifest の一括 transaction は要求しない。
- input と失敗後 manifest の bytes 同一性は原本保全そのもの、schema field / version / basename は永続形式そのものが authority。翻訳全文、source text、private reflection、broad snapshot、characterization は追加しない。

### Coverage / safety / negative control

- BMT-O は全 cleanup、個別削除、URL-only 移行、全体出力、個別出力による名前変更の 5 consumer を列挙して閉じる。private UpdateManifest / AddManagedFile は直接呼ばない。
- サービス fixture は GUID temp directory と自身の file handle を所有し、同期 return / throw で完了確認する。handle は finally / using で閉じてから temp cleanup。
- owner fixture は既存 serial-state-a と class-wide DNP、GUID DB / config / output、実 scheduler Func<Task> 捕捉を利用。Workspace fixture は既存設定・dispatcher 所有を継承する。新 fixture / lane / DNP は追加しない。
- 正常完了は Task / event で待ち、fixed sleep、成功推定 timeout、test 側 production logic 複製は使わない。必要な negative lock watchdog は失敗検出だけに使う。
- 既存 API で実行可能な disk regression は production 修正前に red を得る。新 result / callback が必要な assertion だけは base で構造上実行不能の理由を示し、対象 mutant を一時導入して落ちることを確認して戻す。compile / setup error は red に数えない。
- 既存成功系 test の退役予定はなし。旧失敗隠蔽 route は production 側で退役する。
- designer は test / build / mutant を実行していない。worker が各 Contract ID の evidence を handoff する。

### BMT-P の evidence strategy 補足

既存 writer は FileShare.None であるため、read handle + FileShare.ReadWrite（delete 共有なし）の fixture は旧実装でも直接書込みを拒否する。これを atomic 性の base red と主張しない。root は原本保全 oracle を変更せず、head で置換失敗・原本 bytes 保全・temp 後始末を確認し、atomic publish を FileShare.ReadWrite の直接上書きへ一時置換した targeted mutant が失敗する evidence を代替として承認した。BMT-O / V の既存 consumer red は別途取得する。

## 検証記録

- implementation-worker-frontier による指定 17 path の実装を root が統合確認。新 fixture / lane / 永続状態の追加なし。旧 catch-all salvage、所有無条件除去、manifest 直接上書き、Changed による URL 同期省略を退役。
- `artifacts/verification/tests-quick-20260906-001134`: production 修正前に 19 fail / 1 pass。所有喪失、破損の未拒否、読取り拒否時の BMT 新規生成が red。atomic ケースの base green は原子性の証明に使用しない。
- `tests-quick-20260906-002917`: 件数、共有参照、直接上書き、owner / Workspace 通知、Changed gate、placeholder の targeted mutant 14 件すべてを検出。
- `tests-quick-20260906-003210`: atomic-only mutant が原本 bytes の不一致で失敗。確認用の変更はすべて復元済み。
- `tests-quick-20260906-003432`: 最終 4 fixture Quick は 138 / 138 pass。test elapsed 5.7299 秒、filtered build / test 36.3 秒。timeout なし。途中の compile typo と fixture 型判定修正は red evidence に含めない。
- UTF-8 / LF、git diff --check を確認済み。
- `tests-functional-20260906-003856`: root が `pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional` を一回実行し成功。6 host 合計 4,498 件、成功 4,485 件、skip 13 件、失敗 0 件。retained ExitTime による test execution は 183.4 秒（180 秒 reporting target 超過、300 秒以内）。timeout / retry なし、tracked fingerprint 不変。restore / build の NU1510 警告 2 件は既存の package reference に関するもので変更していない。
- fresh repo-static-review は 5 cleanup 経路、atomic 保存、破損拒否、URL 同期、lock 外通知、caller / consumer、テストと多言語、packet / TRX 証跡の整合を確認し、blocking finding / recommendation ともになし。review 中は root の repository 操作を停止した。review 後の変更は本記録の完了状態と証跡の追記のみ。
