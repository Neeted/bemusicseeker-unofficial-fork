# v3 安全性改善計画

Status: Active

基準コミット: `2fbc7a72450ed325f3554a59f105c4c10d6d6ca9`（BMS-001〜BMS-030）

追加項目の確認コミット: `487132ae220c2a66345845720b91b71d19f58d11`（BMS-031〜BMS-033）

対象: v3.0.0.0 のリリース前修正と、リリース後の保守改善

## Goal / scope

ファイル・DB の保全、操作の終端、失敗結果の表示、および実際の寿命と利用者説明の一致を改善する。リリース前対象は **BMS-001 と BMS-004**。その他は保守対応または限定改善として管理し、同じファイルを触る理由だけでリリース前の完了条件に加えない。

DB／ファイルシステムはアプリの排他的利用を前提とする。外部変更への防御は、BMS-012 の削除前確認など個別に示す小さい範囲に限る。BMS-020・BMS-027 は説明改善、BMS-014 は既存防御の成立確認を先行する。

事象・ソース参照は各項目に対応する上記コミットの静的確認に基づく。修正・検証の進捗は各項目とリンク先の作業記録を参照する。以下の受入条件は本計画の到達目標であり、個別作業記録で承認・検証されるまでは Test Contract Packet や検証実績として扱わない。現行契約は [spec/](../spec/README.md)、開発・検証運用は [AGENTS.md](../../AGENTS.md) に従う。

## 受付方針と適用範囲

採用済みの受付要件は [共通並行性契約 section 6](../spec/workflow-concurrency-and-complexity.md#6-操作種別ごとの共通既定と維持する例外) を正本とする。Busyによる新規競合要求の拒否と、必要な既存機能の維持を対で扱う。全アプリの変更を一つの長時間gateへ集める計画ではない。

一覧は既存の確定済み表示を使って閲覧できればよい。表示から変更へ進むときは、実行ownerの受付後に現在の対象を解決する。モデルlockの保持中にUI完了を待たない。設定画面は変更処理中も現状どおり開き、編集・Cancel等の既存導線を使う。設定適用と画面表示の受付を混同しない。

本計画は全入口の新しい受付が実装済みであることを意味しない。各不具合は対象入口と背景writerまでを追い、既存防御を使って閉じる。既存の導入予約、自動推定、通信中に許可されるライブラリ操作を、拒否側のテストを通すために削除しない。受理済みの仕事の終端義務は保持する。

<a id="concurrency-application"></a>
### 受付方針の適用単位

設計・運用文書への方針反映とruntime改修は別に記録する。下記は関連改修を進める際の範囲であり、全項目の着手前に共通基盤を作る工程ではない。起動再構成や各機能全体の受付変更を、リリース前のBMS-001／BMS-004へ無条件に追加しない。

| 適用単位 | 主な入口・writable pathの候補 | 完了の判定・既存計画との分担 | 状態 |
|---|---|---|---|
| 起動の閲覧／変更受付分離 | [既存startup計画](lr2-startup-procedural-orchestration-plan.md) のunits。MainWindowViewModel、startup owner、BMSLibrary.Lr2SynchronizationOwner | 必須local・必要LR2処理中は変更を未実行Busyとする。設定画面は開ける。optional online／全cache完了へ待機を広げない。standalone・同期無効・失敗後の復旧・保留復元の自動推定を確認 | 未実装、既存計画で追跡 |
| プレイリスト編集と通信の受付 | [BMS-004](#bms-004)、[BMS-029](#bms-029)、[BMS-031](#bms-031)。PlaylistWorkspaceViewModel.Mutations／DetailEditing／Reload／PlaylistUrlAcquisition、BMSPlaylist、PlaylistAggregatePersistenceOwner | 通信中の編集は断り、現行の実入口で許可されるライブラリ操作は維持。詳細セル編集もモデル変更・DB保存前に必要な受付を取得し、後続競合は未実行Busyとする。通信後の適用は既存file/DB境界を守る。一覧全体・設定画面・ライブラリを一律に止めない | BMS-004完了。本文取消のBMS-029、詳細セル編集のBMS-031は未着手 |
| 保留の自動推定と手動操作 | [BMS-016](#bms-016)。PackageLifecycleOwner、PendingInstallEstimateQueueProcessor、PendingPackageWorkflowOwner、対応View terminal | 保留追加・復元から自動推定へ進み、推定中の手動推定・削除だけをBusyで断る。受理済み自動batchを失わない。既存batch内並列を変更しない | 未実装、BMS-016で追跡 |
| 設定画面の利用維持 | [BMS-001](#bms-001)・[BMS-011](#bms-011)等で触る設定／処理入口。SettingsDialogViewModel、ApplicationComposition、MainWindowの設定表示、対象owner | 表示・編集・既存Cancel／UI previewを保ち、Save拒否時のsnapshot復元・失敗後retry等を設定仕様どおり確認。必要な実行時設定の取得は触る入口に限定し、全設定draft基盤を作らない | 互換性条件、独立の全面改修なし |
| 試聴・録音と導入の受付 | [BMS-017](#bms-017)〜[BMS-019](#bms-019)、[BMS-021](#bms-021)の関連入口。PlaybackPanelViewModel、PackageInstallWorkflowOwner、SelectedChartMutationWorkflowOwner、録音owner | 新規の競合試聴・録音・変更はBusy。試聴から変更へは既存Stopと実cleanupを完了してから進む。録音の強制中断や拒否した再生の自動予約を追加しない | 未実装、対象入口で確認・不足だけ改修 |

追加ZIPの予約は新しい実装単位を作らず、[drop-install-ingress.md](../spec/drop-install-ingress.md)の既存ownerを保全する。導入・推定・取消の受付を触るunitでは「A導入中にBのZIPを追加し、順次処理と入力ownershipが維持される」経路を確認する。cancel drain、URL取得中、起動前等の既存拒否まで解除しない。

各unitでは、拒否する実入口と維持する実入口を選び、失敗・取消・shutdown・遅いUI通知まで記録する。前後の実挙動を基準コミットと作業HEADで照合し、既存の許可範囲を全件立証する前提の大規模調査には広げない。維持対象に具体的な安全性違反が見つかった場合は、その組合せの改修範囲を明示して再計画する。

## 進捗と着手順

リリース前対象の BMS-001 と BMS-004 は完了済み。保守対応では BMS-031 の詳細セル編集を優先する。BMS-007 は SQL バックアップを移行・復旧に使う前に対応する。BMS-032 は保守対応、BMS-033 は限定改善とし、追加3件をリリース前の必須条件には加えない。領域内の順序は各節に示す。

工数は、既存コードを把握した実装者による局所調査・実装・関連検証・必要な文書更新の概算人時。横断検証、独立レビュー、新しい OS／native 試験環境の構築は別枠であり、重複作業があるため単純合算しない。BMS-014 は確認のみの工数で、違反が見つかった場合の実装は別見積り。

| 項目 | 改善内容 | 対応時期 | 進捗 | 概算人時 |
|---|---|---|---|---|
| [BMS-001](#bms-001) | LR2試聴による保存済み検索ルートの巻戻しを防ぐ | リリース前 | 完了（2026-09-09） | 8〜16 |
| [BMS-002](#bms-002) | LR2 XML・プレイリストバックアップの保存失敗時に旧ファイルを保全する | 保守対応 | 完了（2026-09-09） | 8〜16 |
| [BMS-003](#bms-003) | LR2プロセス開始失敗でも試聴用設定を復元する | 限定改善 | 完了（2026-09-09） | 8〜16 |
| [BMS-004](#bms-004) | プレイリスト編集と更新反映のUI・collection lock循環待ちを解消する | リリース前 | 完了（2026-09-09） | 16〜32 |
| [BMS-005](#bms-005) | プレイリストDB保存失敗時にlive編集内容も整合させる | 保守対応 | 完了（2026-09-09） | 16〜32 |
| [BMS-006](#bms-006) | フォルダ管理型プレイリストの一括ドロップを分割投入と整合させる | 保守対応 | 完了（2026-09-09） | 8〜16 |
| [BMS-007](#bms-007) | プレイリストSQLバックアップの文字列・数値・NULLの型を保つ | 保守対応 | 未着手 | 8〜16 |
| [BMS-008](#bms-008) | beatoraja Table URL同期の失敗を部分失敗として利用者へ返す | 保守対応 | 未着手 | 8〜16 |
| [BMS-009](#bms-009) | BMT出力要求の置換でも旧出力先cleanupを落とさない | 限定改善 | 未着手 | 8〜16 |
| [BMS-010](#bms-010) | 拡張子変更・文字コード設定の上位失敗をUIへ通知する | 保守対応 | 未着手 | 4〜8 |
| [BMS-011](#bms-011) | 文字化け修正の同期I/OをUIから外し、競合操作の直列化を維持する | 保守対応 | 未着手 | 8〜16 |
| [BMS-012](#bms-012) | 拡張子変更の重複削除を現在の内容確認に限定する | 限定改善 | 未着手 | 4〜8 |
| [BMS-013](#bms-013) | 解析不能譜面を含むパッケージを既所持のみと判定して恒久削除しない | 保守対応 | 未着手 | 12〜24 |
| [BMS-014](#bms-014) | 手動復旧を伴う一括導入で未着手パッケージが保留に残ることを確認する | 限定改善 | 成立確認待ち | 4〜8 |
| [BMS-015](#bms-015) | 主失敗・補償・cleanupの原因をreceipt内で保持する | 限定改善 | 未着手 | 4〜8 |
| [BMS-016](#bms-016) | 保留の自動推定を維持し、推定中の追加手動変更を拒否する | 限定改善 | 未着手 | 8〜16 |
| [BMS-017](#bms-017) | 一時導入試聴の受付拒否で未開始sessionを終了させる | 保守対応 | 未着手 | 4〜8 |
| [BMS-018](#bms-018) | 一時導入試聴の追加リソースを相対パスのままコピーする | 保守対応 | 未着手 | 8〜16 |
| [BMS-019](#bms-019) | 一時コピー失敗が残した部分ファイルを後始末対象に含める | 限定改善 | 未着手 | 4〜8 |
| [BMS-020](#bms-020) | 一時展開パッケージの保留が終了を跨がないことを明記する | 限定改善 | 未着手 | 2〜4 |
| [BMS-021](#bms-021) | 録音失敗後のDeviceVolumeを復元し後続出力への倍率累積を防ぐ | 限定改善 | 未着手 | 4〜8 |
| [BMS-022](#bms-022) | LR2バックアップ後のDB最適化失敗を結果として消費する | 保守対応 | 未着手 | 4〜8 |
| [BMS-023](#bms-023) | SQLite内部待機と外側retryに操作単位の上限を設ける | 保守対応 | 未着手 | 16〜32 |
| [BMS-024](#bms-024) | アプリ関連データ削除前にwriterを止め、所有テーブルを漏れなく削除する | 保守対応 | 未着手 | 12〜24 |
| [BMS-025](#bms-025) | スコア読込み不能を主一覧で未プレイと区別する | 保守対応 | 未着手 | 8〜16 |
| [BMS-026](#bms-026) | LR2バックアップでScore内のdirectory linkを辿らない | 限定改善 | 未着手 | 4〜8 |
| [BMS-027](#bms-027) | バックアップ保存先は専用フォルダであることと日付フォルダ削除を警告する | 限定改善 | 未着手 | 2〜4 |
| [BMS-028](#bms-028) | 未来日付の世代を自動バックアップ間隔判定から除外する | 限定改善 | 未着手 | 4〜8 |
| [BMS-029](#bms-029) | 単発URLダウンロードの本文待機をキャンセル・期限で終端できるようにする | 保守対応 | 未着手 | 8〜16 |
| [BMS-030](#bms-030) | アーカイブ展開と外部HTML／JSON取得に小さな資源予算を設ける | 限定改善 | 未着手 | 16〜32 |
| [BMS-031](#bms-031) | プレイリスト詳細セル編集の受付を保存前に揃え、後続競合を未実行で拒否する | 保守対応（優先） | 未着手 | 16〜32 |
| [BMS-032](#bms-032) | 外部ビューアINIの書込み・復元失敗時に既存設定を保全し、失敗を通知する | 保守対応 | 未着手 | 8〜16 |
| [BMS-033](#bms-033) | JSONエクスポートのheaderとdataに同一保存先を指定した場合は書込み前に拒否する | 限定改善 | 未着手 | 2〜4 |

## 改修単位と writable path

各項のソース表の「改修」「改修候補」が writable path の予定範囲。「参照」は到達経路や既存防御の確認先。記載したメソッド・責務、検証候補、仕様反映先の必要な部分を対象とし、ファイル全体の整理は含めない。表示文言の共通リソースは AGENTS.md の既存の変更範囲を使う。

ソースリンクはリポジトリ内への相対参照。既存項目の行番号は基準コミットに限り、作業 HEAD では記載したシンボルから照合する。BMS-031〜BMS-033 は行番号を固定せず、追加項目の確認コミットで照合したシンボル・責務を示す。検証候補は既存 fixture の所在であり、受入条件を検証済みという意味ではない。

| 領域 | 対象 | 共有する主な変更面 |
|---|---|---|
| [LR2 設定と外部試聴](#lr2-settings) | [BMS-001](#bms-001)、[BMS-002](#bms-002)、[BMS-003](#bms-003)、[BMS-032](#bms-032) | LR2Config、LR2body、設定 composition、uBMplay／BMIIDXView2015 の INI 保存 |
| [プレイリスト編集](#playlist-edit) | [BMS-004](#bms-004)、[BMS-005](#bms-005)、[BMS-006](#bms-006)、[BMS-031](#bms-031) | PlaylistWorkspaceViewModel.Mutations／DetailEditing、BMSPlaylist、BMSTable、PlaylistDetailRow |
| [プレイリストのバックアップ・互換出力](#playlist-output) | [BMS-007](#bms-007)、[BMS-008](#bms-008)、[BMS-009](#bms-009)、[BMS-033](#bms-033) | Dump／Restore、PlaylistBmtOutputOwner、PlaylistWorkspaceViewModel.PlaylistExport |
| [所持譜面の文字コード・拡張子変更](#chart-mutations) | [BMS-010](#bms-010)、[BMS-011](#bms-011)、[BMS-012](#bms-012) | SelectedChartMutationWorkflowOwner、View terminal、collision 判定 |
| [保留パッケージと導入結果](#pending-install) | [BMS-013](#bms-013)、[BMS-014](#bms-014)、[BMS-015](#bms-015)、[BMS-016](#bms-016) | PackageInstallService、保留 snapshot、FileDbMutationBoundary、推定 queue |
| [一時試聴・展開領域・録音](#preview-audio) | [BMS-017](#bms-017)、[BMS-018](#bms-018)、[BMS-019](#bms-019)、[BMS-020](#bms-020)、[BMS-021](#bms-021) | PlaybackPanelViewModel、temporarilyCopyFiles、録音 writer |
| [DB 後処理・終了・スコア表示](#db-lifecycle) | [BMS-022](#bms-022)、[BMS-023](#bms-023)、[BMS-024](#bms-024)、[BMS-025](#bms-025) | Backup、SQLiteConnectionEx、uninstall、BMSLibrary の終了・score 状態 |
| [LR2 バックアップの探索・世代管理](#lr2-backup) | [BMS-026](#bms-026)、[BMS-027](#bms-027)、[BMS-028](#bms-028) | Backup、BackupTests、バックアップ設定ページ |
| [外部取得の終了条件・資源予算](#external-input) | [BMS-029](#bms-029)、[BMS-030](#bms-030) | PlaylistUrlAcquisitionWorkflow、AppHttpClient、SevenZipArchiveExtractor |

BMS-002 と BMS-007 はバックアップ保存、BMS-022 と BMS-026〜BMS-028 は Backup.cs、BMS-023 と BMS-024 は DB 終端を領域横断で共有する。以下の推奨順は編集をまとめる順序であり、具体的な API 依存がない項目を一律にブロックしない。

<a id="lr2-settings"></a>
## LR2 設定と外部試聴

2026-09-08 の依頼で BMS-001 / BMS-002 / BMS-003 に着手。実装経路を照合して3件の妥当性を確認した。具体的な所有境界、採用した順序、承認済みテスト契約と検証は [作業記録](2026-09-08-lr2-settings-safety.md) にまとめる。保存公開を先行し、続いて正本管理と開始失敗時復元を同じ試聴単位で扱う。SQL の値変換を扱う BMS-007 は今回変更しない。

2026-09-09 に BMS-001〜BMS-003 の実装・受入を完了。この3件の問題記述は修正前の調査記録として残す。保存した設定を正本にする境界、試聴5値の復元、同一directoryからの原子的公開を現行specへ反映し、Functional（201.3秒、失敗0）とreview修正後の関連Quick・fresh static reviewを完了した。

<a id="bms-001"></a>
### BMS-001 — LR2試聴による保存済み検索ルートの巻戻しを防ぐ

**事象・影響:** LR2bodyが保持する古いconfig.xml全体を試聴開始・終了時に保存するため、別の設定オブジェクトで正常に保存した検索ルートが消える。全I/O成功・逐次操作でも成立する。譜面本体の物理削除ではない。

**成立条件:** LR2連携でLR2bodyプレイヤーを生成した後、アプリ内で検索ルートを追加または削除し、そのプレイヤーで試聴する。外部変更や同時操作は不要。

**到達経路:**

1. 設定保存でApplicationCompositionが独立したLR2ConfigをLR2bodyへ渡す。設定画面も別のlr2ConfigValueを保持する。
2. 検索ルート変更は設定側のXMLを更新・保存するが、ルート変更だけではプレイヤーを再生成しない。
3. LR2body.setConfig／restoreConfigが古いXML全体をSaveし、後から保存された非所有フィールドも巻き戻す。

**修正方針:**

- LR2設定の書込み正本と順序を既存composition／owner内で揃える。共有する場合は編集ダイアログの未保存値を試聴が保存しない境界を設ける。
- 代案は、保存済みの最新XMLに試聴が所有する画面・音量等のフィールドだけを適用する方式。復元時も所有フィールドだけを戻す。
- 正常終了・開始失敗のどちらでも非所有フィールドを巻き戻さない。開始失敗時の一時値復元の完全化はBMS-003で別に扱う。保存順をUIスレッドの同期待機で保証しない。

**保全条件・対象外:** 原子的なファイル公開だけでは、保存する XML が古い問題は直らない。BMS-002 の保存途中の保全、BMS-003 の開始失敗時復元とは別に完了判定する。設定画面の現行の表示・編集を禁止して回避しない。検索ルート等をプレイヤーの旧snapshotへ逆コピーして辻褄を合わせない。

**並行性の制約:** Busy受付だけでは直らない逐次操作の不具合である。設定画面は現状維持とし、保存済み正本、画面内の未保存編集、試聴が所有する一時fieldを区別する。未保存のSettings.DefaultやXMLを試聴が正本として保存しない。全設定draft基盤や汎用XML merge engineを新設せず、既存の保存・Cancel・試聴復元の対象field境界で閉じる。

**採用方針:** 最新保存 XML の試聴所有5項目だけを更新する。起動待ち中の設定読み込みに一時値が混ざらないよう、既存の復元値を試聴期間だけ LR2Config 側で所有する。具体的な境界と回帰条件は [作業記録](2026-09-08-lr2-settings-safety.md) を参照。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/ViewModels/ApplicationComposition.cs:541–567](../../BeMusicSeeker/ViewModels/ApplicationComposition.cs#L541-L567) | 改修：LR2Configの生成とプレイヤーcomposition |
| [BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs:5807–5821](../../BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs#L5807-L5821) | 改修：検索ルート保存 |
| [BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs:6727–6744](../../BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs#L6727-L6744) | 改修：プレイヤー再生成条件 |
| [BeMusicSeeker/Models/LR2/LR2body.cs:364–416](../../BeMusicSeeker/Models/LR2/LR2body.cs#L364-L416) | 改修：setConfig / restoreConfig |
| [BeMusicSeeker/Models/LR2/LR2Config.cs](../../BeMusicSeeker/Models/LR2/LR2Config.cs) | 改修候補：保存済み設定を扱う契約。採用する正本方式に必要な部分 |

**受入条件（案）:**

- [x] **BMS-001-AC1** — production同様に独立設定オブジェクトを用意し、検索ルート追加→試聴開始→正常終了後も追加ルートがXMLに残る。
- [x] **BMS-001-AC2** — 検索ルート削除→試聴、試聴開始失敗の双方でも削除済みルートが復活しない。
- [x] **BMS-001-AC3** — 試聴対象外の設定と、設定画面でキャンセルした未保存値を変えない。試聴中も既存の設定表示・編集・Cancel経路が利用でき、プレイヤーが未保存値を永続化しない。正常終了時の一時値復元を維持し、開始失敗時の復元漏れはBMS-003として別に評価する。

**検証候補:** [LR2ConfigTests.cs](../../BeMusicSeeker.Tests/LR2ConfigTests.cs)、[PlaybackPanelViewModelTests.cs](../../BeMusicSeeker.Tests/PlaybackPanelViewModelTests.cs)、[ExternalPlayerProcessGatewayTests.cs](../../BeMusicSeeker.Tests/ExternalPlayerProcessGatewayTests.cs)。

**仕様反映先:** [external-chart-launch.md](../spec/external-chart-launch.md)、[settings-change-impact-and-startup-operations.md](../spec/settings-change-impact-and-startup-operations.md)。

**工数の根拠:** 設定の所有境界、編集キャンセルとの互換性、開始・終了の回帰確認が中心。単なるSave置換より広い。 見積り確度は中。

**続けて整理する項目:** [BMS-002](#bms-002)：同じ LR2Config 保存面。XML の正本とファイル公開の原子性は別々に修正する。 [BMS-003](#bms-003)：試聴用設定の所有境界を決めた後、開始失敗時の復元を続ける。

<a id="bms-002"></a>
### BMS-002 — LR2 XML・プレイリストバックアップの保存失敗時に旧ファイルを保全する

**事象・影響:** LR2Config.Saveと検索ルート削除保存、プレイリストSQLバックアップが保存先へ直接上書きする。途中失敗時に旧ファイルを保全できず、メモリXMLだけ戻してもディスクは戻らない。

**成立条件:** 既存の保存先があり、シリアライズ後の書込み・置換等でI/O失敗する。対象はLR2 XML とプレイリスト SQL バックアップの二系統であり、全リポジトリの保存処理一括改修ではない。

**到達経路:**

1. 設定保存／検索ルート削除→LR2ConfigのXDocument.Save(ConfigPath,...)。
2. プレイリストバックアップ→dump生成→PlaylistBackupのFile.WriteAllTextで選択済み保存先へ直接書く。
3. 書込み途中に失敗すると旧内容が失われ、例外時のXMLオブジェクト復元ではファイルを回復できない。

**修正方針:**

- 保存先と同じディレクトリの一意なstagingへ全内容を書き、ストリームを閉じた後、既存のLongPathFileSystemの置換能力を評価して公開する。既存／初回保存を区別する。
- 公開失敗時は旧ファイルを保全し、この試行が所有する一時ファイルだけを回収する。主失敗とcleanup失敗を区別して返す。
- XMLのencoding・改行・SaveOptions、SQLバックアップの読込み互換性を維持し、成功通知は公開完了後とする。

**保全条件・対象外:** 電源断耐久、複数プロセス競合、世代ジャーナル追加は対象外。サポート対象FSで保証できる公開方式を確認する。 config内容の古さはBMS-001、SQL値の誤変換はBMS-007の別問題。

**採用方針:** 同一ディレクトリの staging を閉じてから、既存宛先は置換、初回は移動で公開する。XML の宣言 encoding / SaveOptions.None、SQL の UTF-8 BOM なしを維持する。具体的な保存・診断契約は [作業記録](2026-09-08-lr2-settings-safety.md) を参照。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/Models/LR2/LR2Config.cs:322–345](../../BeMusicSeeker/Models/LR2/LR2Config.cs#L322-L345) | 改修：RemoveBMSSearchDirectoriesAndSave |
| [BeMusicSeeker/Models/LR2/LR2Config.cs:512–518](../../BeMusicSeeker/Models/LR2/LR2Config.cs#L512-L518) | 改修：Save |
| [BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.PlaylistBackup.cs:70–102](../../BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.PlaylistBackup.cs#L70-L102) | 改修：バックアップ書込みと通知 |
| [BeMusicSeeker/Models/Utils/LongPathFileSystem.cs:425–485](../../BeMusicSeeker/Models/Utils/LongPathFileSystem.cs#L425-L485) | 参照：既存の同一FS置換経路 |

**受入条件（案）:**

- [x] **BMS-002-AC1** — 既存ファイルについてstage書込み失敗／公開失敗を注入すると旧内容がbyte単位で残り、成功通知しない。
- [x] **BMS-002-AC2** — 初回保存の失敗で不完全な本番ファイルを公開しない。成功したXML／SQLは既存readerで読める。
- [x] **BMS-002-AC3** — cleanupも失敗した場合は主原因と残存pathを保持し、無関係なファイルを削除しない。

**検証候補:** [LR2ConfigTests.cs](../../BeMusicSeeker.Tests/LR2ConfigTests.cs)、[PlaylistWorkspacePersistenceCommandTests.cs](../../BeMusicSeeker.Tests/PlaylistWorkspacePersistenceCommandTests.cs)、[ResilientFileMutationServiceTests.cs](../../BeMusicSeeker.Tests/ResilientFileMutationServiceTests.cs)。

**仕様反映先:** [file-db-consistency.md](../spec/file-db-consistency.md)、[path-length-and-io.md](../spec/path-length-and-io.md)、[playlist-data-and-export-flow.md](../spec/playlist-data-and-export-flow.md)。

**工数の根拠:** 2保存面の置換と障害注入。利用可能な既存seamの範囲で上下する。 見積り確度は中。

**続けて整理する項目:** [BMS-001](#bms-001)：設定内容の正本を維持したまま保存方式を変更する。 [BMS-007](#bms-007)：同じバックアップ入口で、ファイル公開と SQL の型保持を分けて実装する。

<a id="bms-003"></a>
### BMS-003 — LR2プロセス開始失敗でも試聴用設定を復元する

**事象・影響:** 試聴用設定の保存後にProcess.Startが失敗すると、未起動processへの停止操作も例外になり、画面・音量等の一時変更が設定に残る。設定保存そのものの失敗とは別。

**成立条件:** LR2設定の変更保存は成功し、その後OSによるプロセス開始が失敗する。native/OS障害の実行再現は未実施。

**到達経路:**

1. LR2bodyの再生開始→setConfigで一時設定を保存→外部process開始。
2. 開始例外により正常開始後・Exitedの復元経路へ到達しない。
3. 失敗cleanupが未関連付けprocessのCloseMainWindow等で例外になり、設定復元より先に抜ける。

**修正方針:**

- 設定変更を開始成否と独立したscopeとして所有し、開始前後の全失敗で復元を試みる。
- processが実際に開始した事実を区別し、未開始processに実行中専用操作をしない。
- 主失敗を保持したまま復元失敗を添え、再生sessionとprocess参照を終端状態へ片付ける。

**保全条件・対象外:** BMS-001で決める最新XML／対象フィールドの契約を利用する。古い全文復元を強化しない。 アプリが起動していないLR2を探索・強制終了しない。

**採用方針:** Start 成功前後を区別し、未開始 process の停止 API に依存せず設定復元を試みる。開始失敗と復元失敗、対象 config path を既存の失敗通知へ渡す。具体的な終端・回帰条件は [作業記録](2026-09-08-lr2-settings-safety.md) を参照。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/Models/LR2/LR2body.cs:133–180](../../BeMusicSeeker/Models/LR2/LR2body.cs#L133-L180) | 改修：stopと例外経路 |
| [BeMusicSeeker/Models/LR2/LR2body.cs:217–263](../../BeMusicSeeker/Models/LR2/LR2body.cs#L217-L263) | 改修：開始前の設定変更 |
| [BeMusicSeeker/Models/LR2/LR2body.cs:364–416](../../BeMusicSeeker/Models/LR2/LR2body.cs#L364-L416) | 改修：設定変更・復元 |

**受入条件（案）:**

- [x] **BMS-003-AC1** — 設定保存成功→process開始失敗で、試聴が変更した値が開始前へ戻り、非所有値は保持される。
- [x] **BMS-003-AC2** — 未開始processの停止APIに依存せずcleanupが終了し、次の再生を開始できる。
- [x] **BMS-003-AC3** — 復元も失敗した場合に開始失敗の原因を失わず、利用者が設定残留を判断できる。

**検証候補:** [ExternalPlayerProcessGatewayTests.cs](../../BeMusicSeeker.Tests/ExternalPlayerProcessGatewayTests.cs)、[PlaybackPanelViewModelTests.cs](../../BeMusicSeeker.Tests/PlaybackPanelViewModelTests.cs)、[LR2ConfigTests.cs](../../BeMusicSeeker.Tests/LR2ConfigTests.cs)。

**仕様反映先:** [external-chart-launch.md](../spec/external-chart-launch.md)、[application-shutdown.md](../spec/application-shutdown.md)。

**工数の根拠:** process lifecycleと設定scopeをまたぐが、既存gatewayで開始失敗を表現できる。 見積り確度は中。

**続けて整理する項目:** [BMS-001](#bms-001)：同じ LR2body の設定変更・復元。古い XML 全体の復元を強化しない。

<a id="bms-032"></a>
### BMS-032 — 外部ビューアINIの書込み・復元失敗時に既存設定を保全し、失敗を通知する

**事象・影響:** uBMplayとBMIIDXView2015は、試聴開始時に既存INIへ直接上書きする。書込み途中のI/O失敗で旧設定を失い得るが、その失敗を通知せず起動へ進む。uBMplayは書込み失敗時に読み取っていた元byte列も破棄し、後の復元を「復元不要」として成功扱いする。復元時の直接上書きにも途中失敗時の保全がない。

**成立条件・操作例:** 試聴プレイヤーにuBMplayまたはBMIIDXView2015を選び、譜面を試聴する。既存INIを読み取った後、設定書込みの途中で容量不足等のI/O障害が起きる場合が対象。別プロセスによるINIの同時編集や、すべての書込み例外での破損を前提にはしない。

**到達経路:** 試聴開始 → uBMplayの `TemporarilyRewriteSettings` またはBMIIDXView2015の `temporarilyRewriteSettings` → 本番INIへの直接書込み → 例外を捕捉して処理続行 → 外部プレイヤー起動。uBMplayの `RevertSettings` は元byte列が失われていると書込みをせず成功を返す。

**修正方針:**

- 既存の `AtomicFileWriter` を使い、INIと同じディレクトリの一意なstagingへ書き切ってから公開する。uBMplayの復元も同じ保存方式に揃える。stage書込み・公開失敗では既存ファイルを保全し、当該試行の一時ファイルだけを回収する。
- 読み取れた元byte列を保存例外を理由に破棄しない。uBMplayの既存試聴scopeで、復元が必要な状態と成功後に復元対象を解放する境界を維持する。復元失敗を「復元不要」と読み替えない。
- 設定反映に失敗した試聴要求は通常起動へ進めず、対象INIと原因を既存の再生失敗通知へ返す。準備済みの未開始process参照等は既存の終了経路で片付け、cleanup失敗が主原因を隠さないようにする。復元失敗もログだけで終わらせず、設定が残ったことを利用者が判断できる通知にする。
- Shift_JIS、改行、音量の範囲、無関係なsection・key・コメントの保持を維持する。uBMplayで不足section／keyを補完した場合、保存成功後に補完内容を残す既存の挙動は変更しない。

**保全条件・対象外:** BMIIDXView2015へ新しい設定復元の寿命を追加しない。全設定の統合管理、外部編集とのmerge、永続復元ジャーナル、新しい自動再試行、電源断耐久の保証は追加しない。既存INIがない場合の初回保存でも不完全な本番ファイルを公開しない。正常時のprocess所有・停止・設定補完の契約を維持する。

**変更範囲・参照:**

| ソース（シンボル・責務で照合） | 扱い・対象 |
|---|---|
| [uBMplay.cs](../../BeMusicSeeker/Models/uBMplay.cs) | 改修：TemporarilyRewriteSettings、RevertSettings、PlayStartと既存終了経路への失敗伝播 |
| [BMIIDXView2015.cs](../../BeMusicSeeker/Models/BMIIDXView2015.cs) | 改修：temporarilyRewriteSettings、PlayStartでの設定反映失敗と未開始processのcleanup |
| [AtomicFileWriter.cs](../../BeMusicSeeker/Models/Utils/AtomicFileWriter.cs) | 参照：同一ディレクトリからの公開、失敗時の旧内容保全と一時ファイルcleanup。原則として既存APIを再利用 |

**受入条件（案）:**

- [ ] **BMS-032-AC1** — 両プレイヤーの設定反映でstage書込み失敗／公開失敗を注入すると、既存INIがbyte単位で残る。その試聴要求の起動へ進まず、設定反映失敗が利用者へ通知される。
- [ ] **BMS-032-AC2** — 復元対象の全項目を持つuBMplay INIは正常終了時に元byte列へ戻る。復元のstage書込み／公開に失敗しても不完全なINIへ置き換えず、元byte列を保持し、復元失敗を明示する。
- [ ] **BMS-032-AC3** — uBMplayの不足section／key補完、Shift_JISの日本語、既存改行・無関係な設定、音量範囲を維持する。初回保存失敗で不完全なINIを公開せず、補完保存の成功後は従来どおり補完内容が残る。
- [ ] **BMS-032-AC4** — 設定反映失敗とcleanup失敗が重なっても主原因と対象pathを保持し、他のファイルや所有していないprocessを片付けない。既存の停止・終了・次の明示試聴へ進める所有状態を保つ。

**検証候補:** [UbmplaySettingsTests.cs](../../BeMusicSeeker.Tests/UbmplaySettingsTests.cs)、[ExternalPlayerProcessGatewayTests.cs](../../BeMusicSeeker.Tests/ExternalPlayerProcessGatewayTests.cs)、[PlaybackPanelViewModelTests.cs](../../BeMusicSeeker.Tests/PlaybackPanelViewModelTests.cs)、[AtomicFileWriterTests.cs](../../BeMusicSeeker.Tests/AtomicFileWriterTests.cs)。既存のINI互換性ケースを維持し、共通writerの単体検証だけで起動抑止・利用者通知の検証を代替しない。

**仕様反映先:** [external-chart-launch.md](../spec/external-chart-launch.md)、[file-db-consistency.md](../spec/file-db-consistency.md)。INI固有の変更・復元範囲と失敗通知を記載し、共通の公開方式は既存契約を参照する。

**工数の根拠:** 2プレイヤーの保存・復元と失敗伝播、既存gatewayを使う起動抑止の確認が中心。共通writerの新設は含めない。見積り確度は中。

**関連項目:** [BMS-002](#bms-002) のファイル公開方式を再利用するが、LR2 XML・SQLバックアップの完了状態とは分ける。[BMS-003](#bms-003) のLR2開始失敗時復元を再実装する項目ではない。

<a id="playlist-edit"></a>
## プレイリスト編集

BMS-004 の通知・lock 境界を先行する。続いて BMS-005 の DB 失敗時整合、BMS-006 のバッチ内分類を別の完了条件で進める。Mutations partial、BMSPlaylist、BMSTable と共通テストが主な共有面。

2026-09-09 に BMS-004〜BMS-006 の実装・受入を完了。この3件の事象・到達経路は修正前の調査記録として残す。実入口を照合して対象を絞り、編集受付と通知、DB保存失敗時のlive復元、一括dropの分類を修正した。現行契約は [playlist-data-and-export-flow.md](../spec/playlist-data-and-export-flow.md)、妥当性確認・検証・最終レビューの証跡は [作業記録](2026-09-09-playlist-edit-safety.md) を参照。Functionalは4648成功・11スキップ・0失敗、177.8秒。

**既存計画・仕様との境界:** プレイリスト復元の worker／UI 分離は [既存の応答性改善記録](2026-09-06-wait-responsiveness.md)にある。本領域は編集の残課題を扱い、復元の非同期化をやり直す計画ではない。BMS-031 は詳細セル編集を既存の受付・保存契約へ接続する追加項目であり、完了済みのフォルダ編集・entry削除・dropの改修をやり直さない。

<a id="bms-004"></a>
### BMS-004 — プレイリスト編集と更新反映のUI・collection lock循環待ちを解消する

**事象・影響:** ローカル編集workerが一覧readerを保持したままUI同期通知を待ち、UIが別表更新のため同じwriterを待つとデッドロックする。正常DB・通常操作の組合せで成立し、メイン画面と通常終了が無応答になる。

**成立条件:** 外部表Bのリロード待ち中にローカル表Aをフォルダ名変更等で編集する。Aのreader保持中に、Bの差替えUI callbackが先にwriter待ちへ入る。頻度は未測定。

**到達経路:**

1. RenameFolderAsync等→Task.Run→collection reader取得→編集→PublishEntriesChanged。finallyまでreaderが残る。
2. 詳細更新イベント→MainWindowViewModel→同期Dispatcher.InvokeでUI完了待ち。
3. Bの更新反映UI callback→同じcollection writer待ち。UIはAの通知を処理できず、Aはreaderを解放できない。

**修正方針:**

- ローカル編集の同型入口で、対象確認・DB 保存・メモリ反映・通知の境界を整理する。一覧 reader を保持したまま PublishEntriesChanged から UI 完了を待たない構成へ変更する。
- ローカル編集と手動リロード／URL取得・外部表更新反映の実入口を照合し、通信待ちを含めプレイリスト編集を非待機のBusyとして断る。別表の編集まで並行成功させる必要はない。一方、現行の実入口で許可されるライブラリ操作を止めるglobal gateを通信全体へ掛けない。受理済み同期・commit済み結果は元ownerが終端まで扱い、適用時の既存file/DB境界とactive identity／revision判定を維持する。
- UI callbackで同期writer待ちになる箇所も対になる順序として確認する。

**保全条件・対象外:** InvokeをBeginInvokeへ機械置換するだけ、全面lock削除、広いgenerationの追加で隠さない。 UI応答性の改善を、未承認の同時mutation許可と解釈しない。

**採用した方式:** store単位で編集を受け付け、保存・通知・cleanupの終端まで所有する。後続の手動編集はBusyで拒否し、受理済み自動同期は既存queue内で受付解放を待つ。通知前に一覧lockを解放し、受付後に現在の対象identityを解決する。通信中のライブラリ操作・設定画面の維持は [作業記録](2026-09-09-playlist-edit-safety.md) に実入口と検証を記録した。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.Mutations.cs:64–103](../../BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.Mutations.cs#L64-L103) | 改修：RenameFolderAsync / reader範囲 |
| [BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.Mutations.cs:559–568](../../BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.Mutations.cs#L559-L568) | 改修：PublishEntriesChanged |
| [BeMusicSeeker/ViewModels/MainWindowViewModel.cs:3610–3638](../../BeMusicSeeker/ViewModels/MainWindowViewModel.cs#L3610-L3638) | 改修：詳細更新UI通知 |
| [BeMusicSeeker/Models/BmsLibraryInternal/PlaylistAggregatePersistenceOwner.cs:959–981](../../BeMusicSeeker/Models/BmsLibraryInternal/PlaylistAggregatePersistenceOwner.cs#L959-L981) | 改修：UI callbackでwriter取得 |
| [BeMusicSeeker/Models/BMSPlaylist.cs](../../BeMusicSeeker/Models/BMSPlaylist.cs) | 改修候補：ローカル編集受付・保存・通知の接続範囲 |
| [BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.Reload.cs](../../BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.Reload.cs) | 改修候補：手動リロードの受付・終端と編集可否の接続 |
| [BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.PlaylistUrlAcquisition.cs](../../BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.PlaylistUrlAcquisition.cs) | 改修候補：既存取得状態と編集可否。本文取消はBMS-029 |

**受入条件（確認済み）:**

- [x] **BMS-004-AC1** — 実入口から手動リロード／URL取得の通信とUI反映を制御し、別表を含む後続のプレイリスト編集がBusyで未実行終了する。Dispatcherの閲覧・進捗処理は継続し、受理済み背景更新等の残る共有lock経路でもUI待ちとreader／writer待ちが循環しない。
- [x] **BMS-004-AC2** — 受理した操作の保存・正本反映・結果通知が終端し、操作受付・lock が残らない。Busy で拒否した操作は DB／model を変更せず、先行完了後に自動起動しない。利用者が改めて要求すれば現在状態を使って実行できる。
- [x] **BMS-004-AC3** — フォルダ追加・名前変更・entry 削除等の同型入口にも同じ受付・終端方針が適用される。古い一覧からの選択は受付後に現在の表・対象へ解決し、消失または同一性を確定できない場合は未実行を明示する。表示の遅れを理由に別対象へ読み替えない。
- [x] **BMS-004-AC4** — 通信だけを停止した状態で、対象unitが現行許可を確認したライブラリ操作と設定画面の入口が引き続き利用できる。通信完了後の適用は既存leaseに従って成功／失敗を終端する。プレイリスト編集の拒否を、一覧全体や所持譜面操作の一括無効化で代替しない。

**検証候補:** [PlaylistWorkspacePersistenceCommandTests.cs](../../BeMusicSeeker.Tests/PlaylistWorkspacePersistenceCommandTests.cs)、[MainWindowPlaylistWorkspaceWpfTests.cs](../../BeMusicSeeker.Tests/MainWindowPlaylistWorkspaceWpfTests.cs)、[BmsPlaylistExternalReloadTests.cs](../../BeMusicSeeker.Tests/BmsPlaylistExternalReloadTests.cs)。

**仕様反映先:** [playlist-data-and-export-flow.md](../spec/playlist-data-and-export-flow.md)、[application-shutdown.md](../spec/application-shutdown.md)。

**工数の根拠:** 複数編集入口、publication順序、実Dispatcherの決定的回帰確認、既存の通信との並行性保全を含む。16〜32人時は既存受付・通知を再利用する想定。別の互換性管理基盤が必要になった場合はこの枠へ継ぎ足さず再計画する。見積り確度は中。

**続けて整理する項目:** [BMS-005](#bms-005)：同じ編集処理の lock・DB・live 反映を扱うため、通知境界の後に失敗境界を整理する。 [BMS-006](#bms-006)：同じ Mutations partial とテスト群を変更する。

<a id="bms-005"></a>
### BMS-005 — プレイリストDB保存失敗時にlive編集内容も整合させる

**事象・影響:** liveなBMSTableを先に変更しDB保存に失敗すると、DBだけrollbackされ、entries・folder・更新情報がメモリに残る。次の無関係な成功保存へ、失敗した削除等が混入する。エラー通知は存在する。

**成立条件:** ローカル表のフォルダ編集／entry削除等でDB保存が失敗し、その後に別の編集を保存する。譜面本体の削除ではない。

**到達経路:**

1. ApplyLocalTableMutation→ApplyLocalTableMutationCoreでlive表を変更。
2. ReplaceTablesWithEntriesが失敗してDBをrollback。Aggregate側が戻すのはplaylist_idのみ。
3. 次の全置換保存は変更済みlive entriesを使い、先に失敗した変更もcommitする。先に再起動すればDB旧状態へ戻る。

**修正方針:**

- 既存の編集snapshot／rollback方式を調査し、durable commit前の失敗に限りentries、folder order、header、revision等を一貫して戻す。
- または変更候補をliveと分離し、DB確定後にliveへ反映する。mutation laneで順序を閉じ、snapshot取得のためにUIを待たない。
- DB確定後のcustom-folder／BMT等の派生出力失敗は、DB失敗と区別したresult・通知にする。

**保全条件・対象外:** 派生出力失敗を理由にcommit済みDBを巻き戻さない。単にReloadを無条件実行して利用者の別編集を消さない。 内部revisionの値そのものより、stale判定と次回保存の正しさを保証する。

**採用した方式:** 操作開始時の snapshot で、変更した live state を object identity を保って復元する。DB commit 後の出力失敗では保存内容を戻さない。現行契約は [playlist-data-and-export-flow.md](../spec/playlist-data-and-export-flow.md)、検証証跡は [作業記録](2026-09-09-playlist-edit-safety.md) を参照。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/Models/BMSPlaylist.cs:4873–4900](../../BeMusicSeeker/Models/BMSPlaylist.cs#L4873-L4900) | 改修：ApplyLocalTableMutationの変更・保存順 |
| [BeMusicSeeker/Models/BMSTable.cs:1061–1084](../../BeMusicSeeker/Models/BMSTable.cs#L1061-L1084) | 改修：フォルダ編集 |
| [BeMusicSeeker/Models/BMSTable.cs:1149–1171](../../BeMusicSeeker/Models/BMSTable.cs#L1149-L1171) | 改修：entry削除 |
| [BeMusicSeeker/Models/BmsLibraryInternal/PlaylistAggregatePersistenceOwner.cs:629–646](../../BeMusicSeeker/Models/BmsLibraryInternal/PlaylistAggregatePersistenceOwner.cs#L629-L646) | 改修：失敗時playlist_id補償 |

**受入条件（確認済み）:**

- [x] **BMS-005-AC1** — 各ローカル編集のDB失敗直後に、liveとDBがどちらも失敗前の内容になる。
- [x] **BMS-005-AC2** — 直後の無関係な成功保存と再起動の双方で、失敗した変更が混入しない。
- [x] **BMS-005-AC3** — DB成功後の派生出力失敗では、保存済み編集を保持し、出力だけの失敗を通知する。

**検証候補:** [PlaylistWorkspacePersistenceCommandTests.cs](../../BeMusicSeeker.Tests/PlaylistWorkspacePersistenceCommandTests.cs)、[BmsPlaylistPersistenceLifecycleTests.cs](../../BeMusicSeeker.Tests/BmsPlaylistPersistenceLifecycleTests.cs)、[PlaylistSummaryBulkEditTests.cs](../../BeMusicSeeker.Tests/PlaylistSummaryBulkEditTests.cs)。

**仕様反映先:** [playlist-data-and-export-flow.md](../spec/playlist-data-and-export-flow.md)。

**工数の根拠:** rollback対象が複数のmutable stateに及び、DB後の派生失敗との区別が必要。 見積り確度は中。

**続けて整理する項目:** [BMS-004](#bms-004)：一覧 lock と通知の整理を取り込んでから rollback／候補分離を実装する。 [BMS-006](#bms-006)：ドロップの予定集合を live へ早期適用しない構造を共有する。

<a id="bms-006"></a>
### BMS-006 — フォルダ管理型プレイリストの一括ドロップを分割投入と整合させる

**事象・影響:** 同じ曲に属する別ディレクトリの譜面を一括投入すると、同じ順序で2回投入した場合には1フォルダとなる組が2フォルダへ分裂する。入力のバッチ境界で分類結果が変わる。

**成立条件:** root-folder管理型のローカル表へ、orgMd5等で同じ曲と判定できる複数directory groupをドロップする。一般の同名タイトルをすべて同じ曲と扱う話ではない。

**到達経路:**

1. Workspaceのroot-folderドロップ→BuildRootFolderDropMutationsで入力をdirectory単位にgroup化。
2. 各groupの既存フォルダ探索はtable.folder_list／table.entriesだけを見る。先に計画した未適用folder mutationを次のgroupが参照しない。
3. 一括では両方が新フォルダ扱い。分割では1回目の適用済みentriesを2回目が見て既存フォルダへ合流する。

**修正方針:**

- 1回のドロップ内のworking setへ、先行groupの予定フォルダと曲identityを反映し、後続groupもそれを参照する。
- 新規フォルダは仮のstable keyで参照し、表示名衝突のsuffix処理と適用順を既存の規則へ合わせる。
- 曲の同一性は既存orgMd5ルールを維持する。必要以上のタイトル推測・全表再分類はしない。

**保全条件・対象外:** 既存データを自動で一括統合するmigrationは含めない。ユーザーの手動フォルダ分類を再解釈しない。 順序の違う入力まで常に同じ結果にする要件は追加せず、同じ順序のバッチ分割不変性を対象とする。

**採用した方式:** 予定 folder と entry を保持する作業集合で、後続 directory を既存と同じ分類・順序・命名規則で計画する。削除履歴だけの folder は候補に含めない。現行契約は [playlist-data-and-export-flow.md](../spec/playlist-data-and-export-flow.md)、検証証跡は [作業記録](2026-09-09-playlist-edit-safety.md) を参照。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.Mutations.cs:219–256](../../BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.Mutations.cs#L219-L256) | 改修：root-folder drop plan作成 |
| [BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.Mutations.cs:389–427](../../BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.Mutations.cs#L389-L427) | 改修：BuildRootFolderDropMutations |
| [BeMusicSeeker/Models/BMSPlaylist.cs:4624–4735](../../BeMusicSeeker/Models/BMSPlaylist.cs#L4624-L4735) | 改修：ApplyPlaylistDropMutation |

**受入条件（確認済み）:**

- [x] **BMS-006-AC1** — 同一の順序で一括投入した結果と2回に分けた結果で、曲グループ所属と重複排除後のentriesが一致する。
- [x] **BMS-006-AC2** — 別曲だが同名タイトル、orgMd5なし、既存フォルダあり、フォルダ名衝突を区別し、誤統合しない。
- [x] **BMS-006-AC3** — 新しいworking setを計画段階でliveへ公開しない。既存のDB失敗後rollback不備はBMS-005へ分離し、その修正後に両契約を統合確認する。

**検証候補:** [PlaylistWorkspacePersistenceCommandTests.cs](../../BeMusicSeeker.Tests/PlaylistWorkspacePersistenceCommandTests.cs)、[MainWindowPlaylistWorkspaceWpfTests.cs](../../BeMusicSeeker.Tests/MainWindowPlaylistWorkspaceWpfTests.cs)、[BmsPlaylistPersistenceLifecycleTests.cs](../../BeMusicSeeker.Tests/BmsPlaylistPersistenceLifecycleTests.cs)。

**仕様反映先:** [playlist-data-and-export-flow.md](../spec/playlist-data-and-export-flow.md)、[custom-table-view.md](../spec/custom-table-view.md)。

**工数の根拠:** 入力内working setと新規フォルダ参照を追加し、同一性・命名互換を回帰確認する。 見積り確度は中。

**続けて整理する項目:** [BMS-005](#bms-005)：予定集合の計画と durable 保存後の反映境界を合わせる。 [BMS-004](#bms-004)：同じ Mutations partial の変更を統合する。

<a id="bms-031"></a>
### BMS-031 — プレイリスト詳細セル編集の受付を保存前に揃え、後続競合を未実行で拒否する

**事象・影響:** 詳細セル編集は、モデルを変更してDBへ保存した後に、LR2カスタムフォルダ出力のための変更受付を取得する。ここでBusyになると、保存済みなのに内部entryだけを旧値へ戻し、DB・モデル・表示が食い違う。その後の別のプレイリスト保存で旧値が再保存され、確定済みの編集が消え得る。DB保存自体の失敗でも、表示セルだけが編集後の値を保ち、ログだけでは利用者が失敗に気付けない経路がある。

**成立条件・操作例:** LR2 DB連携でカスタムフォルダ出力が有効なローカルプレイリストを開き、曲Aの `MEMO` を確定した後、その出力中に曲Bの `MEMO` を確定する。後続編集のDB保存が先に通り、出力受付でBusyになるタイミングで成立する。別途BMSを導入する操作は必要ない。対象は詳細の `ENTRY LEVEL`、`URL1`、`URL2`、`COMMENT`、`MEMO` の確定経路であり、外部同期プレイリストは従来どおり `MEMO` のみ編集可能とする。

**到達経路:** 詳細セル確定 → `CompleteDetailEdit` が表示行とentryへ入力を適用 → `CommitBMSTableEntry` がDBを保存 → LR2出力の受付取得 → Busy例外 → 呼出し元がentryだけを復元する。プレイリスト変更の論理受付を通らず、DB保存後に別の受付を要求する順序が問題である。

**修正方針:**

- 入力は対象の同一性・編集項目・入力値として受け取り、表示行やentryへ先に適用しない。既存のプレイリスト変更受付を非待機で取得し、その後に現在の対象を解決して必要な読込みを行う。
- LR2出力が必要な操作では、モデル変更・DB保存の前に既存のライブラリ変更受付も非待機で取得する。一方でも取得できなければ取得済みの受付を解放し、副作用なしのBusyとして終了する。スタンドアローン等、LR2出力が不要な場合にその受付を追加しない。
- 必要な受付を確保した操作を先行とし、後続の競合するセル編集やライブラリ変更を未実行で拒否する。逆にライブラリ変更が先行する場合はセル編集を保存前に断る。DB commitの早さで優先順位を決めず、拒否した操作を後で自動実行しない。
- 取得済みのライブラリ変更権限は既存の `LibraryFileMutationCapability` で出力処理へ渡し、途中で再取得しない。プレイリスト変更受付は保存・必要な反映・通知・cleanupの終端まで保持する。collection／table lock、DB scope、ライブラリ変更leaseを解放してからUI通知へ進み、UIを同期的に待つ循環を作らない。
- DB保存失敗では、この操作が変更したモデル・表示行・詳細source snapshotを旧値へ整合させ、保存失敗を通知する。DB保存後の実際の出力I/O失敗では確定値を保ち、既存の `PlaylistMutationPostCommitException` 等の保存契約・通知経路を再利用する。すべての例外でentryを旧値へ戻す処理を改める。競合による保存後Busyを結果分類で吸収する方式にはしない。

**保全条件・対象外:** 単一entryの保存を表全体の書換えへ拡大しない。新しい結果型・永続状態・retry queue・広いversion token・global gateは追加しない。非同期BMT出力は既存の独立した終端のままとし、その完了までセル編集受付を延長しない。編集可能な列、URL等の入力検証、対象の同一性規則を維持する。外部通信待ち全体をライブラリ変更受付で囲まず、確定済み一覧の閲覧・設定画面・追加ZIPの予約等、共通並行性契約section 6の例外を維持する。

**変更範囲・参照:**

| ソース（シンボル・責務で照合） | 扱い・対象 |
|---|---|
| [PlaylistWorkspaceViewModel.DetailEditing.cs](../../BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.DetailEditing.cs) | 改修：CompleteDetailEdit、CommitRowAndSynchronizeSourceAsync、入力の適用時点と表示整合 |
| [PlaylistWorkspaceViewModel.Mutations.cs](../../BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.Mutations.cs) | 参照／改修候補：既存のプレイリスト変更受付と通知終端の再利用 |
| [BMSPlaylist.cs](../../BeMusicSeeker/Models/BMSPlaylist.cs) | 改修：CommitBMSTableEntryの受付・保存・LR2出力順序。既存のApplyLocalTableMutationと確定後失敗契約を参照 |
| [PlaylistDetailRow.cs](../../BeMusicSeeker/ViewModels/PlaylistDetailRow.cs) | 改修候補：編集値の適用・失敗時の表示復元に必要な範囲 |
| [MainWindowViewModel.cs](../../BeMusicSeeker/ViewModels/MainWindowViewModel.cs) | 改修候補：セル確定要求と失敗通知の接続のみ。業務判断はWorkspace／ownerに置く |
| [PlaylistAggregatePersistenceOwner.cs](../../BeMusicSeeker/Models/BmsLibraryInternal/PlaylistAggregatePersistenceOwner.cs)、[PlaylistCustomFolderOutputMaintenanceOwner.cs](../../BeMusicSeeker/Models/BmsLibraryInternal/PlaylistCustomFolderOutputMaintenanceOwner.cs) | 参照：CommitEntry、取得済み変更権限を使うLR2出力、post-lease通知 |

**受入条件（案）:**

- [ ] **BMS-031-AC1** — 実際の詳細セル確定入口から、先行編集の保存・LR2出力中に後続編集を要求すると、後続はDB・モデル・表示の確定値を変えずBusyとなる。先行編集は完了し、後続は自動実行されない。
- [ ] **BMS-031-AC2** — セル編集が必要な受付を確保した後は、後続の競合ライブラリ変更が未実行Busyとなり、セル編集が保存後にBusyへ転落しない。ライブラリ変更が先行した場合はセル編集をDB保存前に拒否する。
- [ ] **BMS-031-AC3** — 実際のDB保存失敗で、DB・entry・表示行・詳細source snapshotが旧値で一致し、利用者へ保存失敗が通知される。その後の独立した成功編集とDB再オープンでも失敗した入力が混入しない。
- [ ] **BMS-031-AC4** — DB保存後の実際のLR2出力失敗では、DB・モデル・表示に確定値を保ち、Busyではなく出力失敗を通知する。その後の別のプレイリスト保存とDB再オープンでも確定済み編集が消えない。
- [ ] **BMS-031-AC5** — スタンドアローンとLR2連携、ローカル表と外部同期表の編集可否を維持する。確定済み一覧・設定画面を一律に閉じず、通信待ち中の既存許可操作と非同期BMT出力の独立した終端を維持する。
- [ ] **BMS-031-AC6** — 成功・Busy・保存失敗・出力失敗の各終端で受付とlockが残らず、次の明示操作と通常終了へ進める。UI通知待ちとmodel lock待ちの循環を作らない。

**検証候補:** [PlaylistViewPipelineTests.cs](../../BeMusicSeeker.Tests/PlaylistViewPipelineTests.cs)、[PlaylistWorkspacePersistenceCommandTests.cs](../../BeMusicSeeker.Tests/PlaylistWorkspacePersistenceCommandTests.cs)、[BmsPlaylistPersistenceLifecycleTests.cs](../../BeMusicSeeker.Tests/BmsPlaylistPersistenceLifecycleTests.cs)、[MainWindowPlaylistWorkspaceWpfTests.cs](../../BeMusicSeeker.Tests/MainWindowPlaylistWorkspaceWpfTests.cs)。既存fixtureを拡張し、実入口から保存・出力の進行点を明示的に同期する。固定待ちや手動の早押しに依存せず、モデル単独の復元確認でDB・表示・通知の確認を代替しない。

**仕様反映先:** [playlist-data-and-export-flow.md](../spec/playlist-data-and-export-flow.md)、[custom-table-view.md](../spec/custom-table-view.md)。受付の正本は [workflow-concurrency-and-complexity.md](../spec/workflow-concurrency-and-complexity.md) とし、詳細セル入口への適用範囲を機能仕様へ反映する。

**工数の根拠:** 既存受付への接続、入力適用時点の変更、DB・表示整合と競合順序の回帰確認が中心。新しい並行性基盤の作成は含めない。見積り確度は中。

**関連項目:** [BMS-004](#bms-004) の受付・通知終端と [BMS-005](#bms-005) の保存契約を再利用する。通信本文の取消・期限を扱う [BMS-029](#bms-029) とは別の完了条件とする。

<a id="playlist-output"></a>
## プレイリストのバックアップ・互換出力

BMS-007 は移行・復旧でバックアップを使用する前に対応する。BMT 側は BMS-008 の部分失敗結果を先に整理し、BMS-009 の未実行 cleanup 保持を続ける。BMS-033 の手動 JSON エクスポートは独立した入力検証として扱い、SQL dump や BMT 要求管理の変更を前提にしない。

**既存計画・仕様との境界:** [BMT manifest の完了記録](bmt-manifest-failure-handling.md)の所有情報・保存失敗契約を維持する。BMS-009 は、受け付け済みなのに実行前に消える cleanup が対象。一度失敗した旧フォルダの自動回収は追加しない。

<a id="bms-007"></a>
### BMS-007 — プレイリストSQLバックアップの文字列・数値・NULLの型を保つ

**事象・影響:** Dump<T>が値を文字列化しdouble.TryParseでSQL引用の要否を決める。数値に見えるTEXTが引用されず、復元不能または復元成功後の値変更になる。バックアップのI/O成功だけでは復元可能性を保証しない。

**成立条件:** 表名・folder・title等に1,000、1e3、1.00等を含む。復元前に本修正を優先する。既存の不正SQLに対するrollbackはあり、復元エラーだけで現在DBが失われるとは評価しない。

**到達経路:**

1. Workspaceバックアップ→PlaylistPersistenceRepository→LR2SongDBExtended.Dump<T>。
2. 全値をstringとして読み、数値解析成功の値を引用なしでSQL化。1,000は値区切り、1e3等は別表記の数値になる。
3. 復元は生成済みSQLをtransactionで実行。有効な数値化はcommitされ、成功通知される。

**修正方針:**

- 元の型／SQLite storage typeに従ってリテラル化し、TEXTは文字列escape、数値は不変culture、NULLはNULLにする。既存ライブラリの型読出しまたはSQLite側quote等を適合評価する。
- 対象テーブルの実schemaとNULL/BLOB等の到達可能な型を確認し、汎用SQL serializerを新設しない。
- 現在有効なバックアップの復元互換を保つ。既に失われた元の文字列表記を推測修復しない。

**保全条件・対象外:** TryParseへ例外文字列を足す対処ではない。直接上書き対策BMS-002とは別に完了させる。 未知の任意SQLを安全に実行する汎用sandbox化は対象外。

**決定待ち:** 対象 schema の到達可能な型を保持できる読出し・SQL リテラル生成方式。有効な既存 dump の互換範囲。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/Models/LR2/LR2SongDBExtended.cs:1467–1485](../../BeMusicSeeker/Models/LR2/LR2SongDBExtended.cs#L1467-L1485) | 改修：Dump<T> |
| [BeMusicSeeker/Models/BmsLibraryInternal/PlaylistPersistenceRepository.cs:487–543](../../BeMusicSeeker/Models/BmsLibraryInternal/PlaylistPersistenceRepository.cs#L487-L543) | 改修：Backup / Restore |
| [BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.PlaylistRestore.cs:18–42](../../BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.PlaylistRestore.cs#L18-L42) | 参照：復元結果の通知 |

**受入条件（案）:**

- [ ] **BMS-007-AC1** — 実際のdump→restoreで、1,000／1e3／1.00／先頭ゼロ／引用符／日本語／NULL／整数を含む値と型が一致する。
- [ ] **BMS-007-AC2** — 数値cultureを変更しても復元結果が一致し、不正なSQLを生成しない。
- [ ] **BMS-007-AC3** — 有効な既存形式は復元でき、不正dumpは現DBを変更せず失敗通知する。

**検証候補:** [PlaylistWorkspacePersistenceCommandTests.cs](../../BeMusicSeeker.Tests/PlaylistWorkspacePersistenceCommandTests.cs)、[Lr2PlaylistEntryPersistenceTests.cs](../../BeMusicSeeker.Tests/Lr2PlaylistEntryPersistenceTests.cs)、[BmsPlaylistPersistenceLifecycleTests.cs](../../BeMusicSeeker.Tests/BmsPlaylistPersistenceLifecycleTests.cs)。

**仕様反映先:** [playlist-data-and-export-flow.md](../spec/playlist-data-and-export-flow.md)、[data-and-indexes.md](../spec/data-and-indexes.md)。

**工数の根拠:** 型保持と旧形式の互換確認が主作業。ファイル保存の原子化は別見積り。 見積り確度は中。

**続けて整理する項目:** [BMS-002](#bms-002)：バックアップの値表現と公開の失敗を独立に検証できる順序で続ける。

<a id="bms-008"></a>
### BMS-008 — beatoraja Table URL同期の失敗を部分失敗として利用者へ返す

**事象・影響:** .bmt生成後のTable URL同期例外がwarning logだけに落ち、外側の失敗一覧へ渡らない。構成済みconfigを読めない経路でも同期を黙って取りやめるため、出力成功に見えてbeatoraja側へ登録されない。

**成立条件:** beatoraja連携が有効で、出力に続く構成読込み／Table URL保存が失敗する。機能未設定・意図的に同期対象外のケースは失敗扱いしない。

**到達経路:**

1. BMT出力／cleanup→PlaylistBmtOutputOwner.SyncManagedTableUrls。
2. root/configの有効性チェックでreturn、またはSyncTableUrlsの例外をcatchしてlogWarningのみ。
3. 外側failuresは更新されず、URL同期未完了がUIへ伝わらない。

**修正方針:**

- URL同期の結果を、未構成による対象外・成功・読み書き失敗・世代不採用に分け、構成済み対象の失敗を出力結果へ合流させる。
- 生成成功を破棄せず「生成済み／URL同期失敗」の部分結果として既存notification経路で1回通知する。
- 対象config/pathと原因をログに残し、UI文言はresource化する。自動無限retryは追加しない。

**保全条件・対象外:** 世代が古くなったための意図した不採用をエラーにしない。ユーザー手入力URLを消さない。 BMS-009の旧root cleanup保全とは別の完了条件。

**決定待ち:** 未設定・同期無効・失敗・古い要求の不採用を区別する結果表現と、既存の出力通知へ集約する位置。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/Models/BmsLibraryInternal/PlaylistBmtOutputOwner.cs:793–832](../../BeMusicSeeker/Models/BmsLibraryInternal/PlaylistBmtOutputOwner.cs#L793-L832) | 改修：SyncManagedTableUrls |
| [BeMusicSeeker/Models/BmsLibraryInternal/PlaylistBmtOutputOwner.cs:251–310](../../BeMusicSeeker/Models/BmsLibraryInternal/PlaylistBmtOutputOwner.cs#L251-L310) | 改修：全出力のfailure収集 |
| [BeMusicSeeker/Models/BmsLibraryInternal/PlaylistOperationNotificationOwner.cs](../../BeMusicSeeker/Models/BmsLibraryInternal/PlaylistOperationNotificationOwner.cs) | 改修候補：部分失敗結果の既存通知への接続 |

**受入条件（案）:**

- [ ] **BMS-008-AC1** — 構成済みconfigの読込み失敗とSyncTableUrls書込み例外で、UIに部分失敗が届き、生成済み.bmtは保持される。
- [ ] **BMS-008-AC2** — 未構成／同期無効は従来どおり正常な対象外となり、誤警告しない。
- [ ] **BMS-008-AC3** — 同一操作の失敗を重複ダイアログにせず、再実行成功後は未完了状態が解消される。

**検証候補:** [BmtTableExportServiceTests.cs](../../BeMusicSeeker.Tests/BmtTableExportServiceTests.cs)、[BmsPlaylistCustomFolderOutputTests.cs](../../BeMusicSeeker.Tests/BmsPlaylistCustomFolderOutputTests.cs)、[PlaylistOperationNotificationOwnerTests.cs](../../BeMusicSeeker.Tests/PlaylistOperationNotificationOwnerTests.cs)。

**仕様反映先:** [playlist-data-and-export-flow.md](../spec/playlist-data-and-export-flow.md)、[beatoraja-table-url-import.md](../spec/beatoraja-table-url-import.md)。

**工数の根拠:** 結果型・複数呼出元・UI通知と翻訳整合を調整する。 見積り確度は中。

**続けて整理する項目:** [BMS-009](#bms-009)：同じ BMT owner の失敗集約と要求処理。部分結果の表現を合わせる。

<a id="bms-009"></a>
### BMS-009 — BMT出力要求の置換でも旧出力先cleanupを落とさない

**事象・影響:** 旧root Aをcleanupする全出力要求が、旧root情報なしの新しい全出力要求に世代で置換されると、Aのmanifestと管理生成物が残る。新root Bへの出力自体は成功し得る。

**成立条件:** 出力先A→B設定変更後、最初のworkerがcleanupを始める前に外部同期完了等から別の全出力が予約される。短い競合窓が必要。

**到達経路:**

1. 設定変更→QueueBeatorajaBmtExportAll(reason, cleanupTablePath:A)。
2. 後続の全出力要求がgenerationを進める。旧workerはstale判定でcleanup前にreturnする。
3. 最新要求はAを知らず、Bだけ生成してAの管理物が残る。

**修正方針:**

- 最新内容への置換が許される生成要求と、設定変更時に受け付けた未実行の旧 root cleanup を分ける。既存 owner 内で当該要求が完了するまで必要な root を保持する。
- A→B→C 等で複数の未実行 cleanup が重なる場合も、現在の出力 root と管理外ファイルを保護する。一度実行して失敗した旧 root の自動再試行・次回起動時回収には拡張しない。
- cleanup失敗はBMS-008と同様に主出力と分けて観測可能にする。

**保全条件・対象外:** 対象は要求の置換で消える未実行 cleanup。一度試みて失敗した旧出力先を残して通知する現行仕様は維持し、永続キュー・起動時 replay・旧フォルダの自動探索は追加しない。旧 Table URL の残留を本件の確定影響とはしない。

**並行性の制約:** 新規出力先変更を Busy 拒否にしても、既に受理した旧出力先 cleanup は消してよい処理にならない。表示更新の latest-wins と、実ファイルを整理する要求の完了を分ける。専用の汎用ジョブ台帳は新設しない。

**決定待ち:** 未実行の旧 root cleanup を同じ owner 内で保持する方式。失敗済み cleanup の再試行キューにはしない。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/Models/BmsLibraryInternal/PlaylistBmtOutputOwner.cs:211–285](../../BeMusicSeeker/Models/BmsLibraryInternal/PlaylistBmtOutputOwner.cs#L211-L285) | 改修：QueueBeatorajaBmtExportAll / generation |
| [BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs:6919–6941](../../BeMusicSeeker/ViewModels/SettingsDialogViewModel.cs#L6919-L6941) | 参照：旧出力先つき要求 |
| [BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.ExternalPlaylistSync.cs:109–147](../../BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.ExternalPlaylistSync.cs#L109-L147) | 参照：同期後の全出力要求 |

**受入条件（案）:**

- [ ] **BMS-009-AC1** — 先行cleanup前に後続要求を明示的に投入しても旧root Aの管理物が回収され、Bの最終生成物が残る。
- [ ] **BMS-009-AC2** — A→B→C、同root重複要求、現rootへの再設定で誤削除しない。非管理ファイルは保持される。
- [ ] **BMS-009-AC3** — cleanup を実行して失敗した場合は残留を通知し、新出力先で確定済みの生成物を巻き戻さない。失敗した旧 root の自動再試行を追加しない。

**検証候補:** [BmtTableExportServiceTests.cs](../../BeMusicSeeker.Tests/BmtTableExportServiceTests.cs)、[BmsPlaylistCustomFolderOutputTests.cs](../../BeMusicSeeker.Tests/BmsPlaylistCustomFolderOutputTests.cs)、[PlaylistOperationNotificationOwnerTests.cs](../../BeMusicSeeker.Tests/PlaylistOperationNotificationOwnerTests.cs)。

**仕様反映先:** [playlist-data-and-export-flow.md](../spec/playlist-data-and-export-flow.md)。

**工数の根拠:** 要求併合の意味とcleanup所有範囲を固定した順序テストが必要。 見積り確度は中。

**続けて整理する項目:** [BMS-008](#bms-008)：同じ owner と失敗通知。未実行 cleanup を保持しても失敗の通知を落とさない。

<a id="bms-033"></a>
### BMS-033 — JSONエクスポートのheaderとdataに同一保存先を指定した場合は書込み前に拒否する

**事象・影響:** プレイリストのJSONエクスポートで、headerとdataの保存先が同じかを検査していない。両方に同じファイルを指定すると、headerを書いた直後にdataで上書きし、有効な2ファイルの組を生成しないまま正常終了する。元のプレイリストDBは残るため、別の保存先で出力し直せる。

**成立条件・操作例:** 「headerの保存先」「dataの保存先」の2回の保存ダイアログで、同じ未作成の `table.json` を指定する。実際の書込みは両方の選択後なので、この場合は既存ファイルの上書き警告でも防げない。

**到達経路:** `ExportPlaylistTableAsync` で2つの保存先を受け取る → `ExportPlaylistTableCoreAsync` がnullだけを確認 → `ExportPlaylistTable` が同じパスへheader、dataの順で書き込む。

**修正方針:**

- 2つの保存先が確定した後、既存のパス正規化・比較規則を使って絶対パスを照合する。通常のWindowsパスで大文字小文字や `.`／`..` の表記だけが異なる同一宛先も拒否する。
- 同一宛先なら、どちらのファイルも書き込まず、既存の出力失敗通知経路で別々の保存先が必要であることを通知する。検証は出力処理の入口で行い、保存ダイアログの警告だけに依存しない。再選択は利用者の明示操作とする。

**保全条件・対象外:** 既存宛先、プレイリストDB、モデルの `Data_url` を変更せずに拒否する。二ファイル全体のトランザクション化、既存出力の世代バックアップ、hardlink／symlink等の別名追跡は追加しない。異なる宛先へのJSON形式・出力順序・既存の保存失敗通知・一時的な `Data_url` の復元を維持する。

**変更範囲・参照:**

| ソース（シンボル・責務で照合） | 扱い・対象 |
|---|---|
| [PlaylistWorkspaceViewModel.PlaylistExport.cs](../../BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.PlaylistExport.cs) | 改修：ExportPlaylistTableAsync／ExportPlaylistTableCoreAsyncの保存先検証と出力失敗通知 |
| [BMSTable.cs](../../BeMusicSeeker/Models/BMSTable.cs) | 参照：HeaderToJson／DataToJson。JSON形式・内容の変更は対象外 |

**受入条件（案）:**

- [ ] **BMS-033-AC1** — 実際の2回の保存先選択から同じ未作成パスを指定すると、ファイルを作成せず、保存先が重複していることを通知する。既存の同じパスを選んだ場合も元byte列を変えない。
- [ ] **BMS-033-AC2** — 大文字小文字や `.`／`..` の表記だけが異なる同一宛先を拒否し、同じbasenameでもディレクトリが異なる宛先は受け入れる。
- [ ] **BMS-033-AC3** — 異なる宛先への出力では、headerとdataがそれぞれ期待するJSON構造を持ち、既存のURL保持・一時URL復元を維持する。どちらかの保存ダイアログの取消、保存先重複の拒否でDB・モデル・既存ファイルを変更しない。

**検証候補:** [PlaylistWorkspacePersistenceCommandTests.cs](../../BeMusicSeeker.Tests/PlaylistWorkspacePersistenceCommandTests.cs)。既存の保存ダイアログfixtureを使い、ファイル内容・非作成・通知を確認する。BMT出力やSQLバックアップのfixtureへ統合しない。

**仕様反映先:** [playlist-data-and-export-flow.md](../spec/playlist-data-and-export-flow.md)、[docs/manual.ja.md](../../docs/manual.ja.md) のJSONエクスポート説明。

**工数の根拠:** 保存前のパス比較と拒否通知、既存の出力fixtureへのケース追加に限定する。見積り確度は高。

**関連項目:** [BMS-007](#bms-007) のSQLバックアップ、[BMS-008](#bms-008)・[BMS-009](#bms-009) のBMT出力とは別の手動出力入口として完了判定する。

<a id="chart-mutations"></a>
## 所持譜面の文字コード・拡張子変更

BMS-010 の結果通知を先に閉じ、BMS-011 の非同期経路へ引き継ぐ。BMS-012 の hash 確認は衝突時の恒久削除判定に限定した別単位。MainWindow terminal と SelectedChartMutationWorkflowOwner を共有する。

**既存計画・仕様との境界:** [FS/DB 結果伝達の完了記録](file-db-consistency-follow-up.md)にある既存 report／receipt consumer を利用する。UI 応答性を理由に移動・削除・導入を並行実行させない。

<a id="bms-010"></a>
### BMS-010 — 拡張子変更・文字コード設定の上位失敗をUIへ通知する

**事象・影響:** 個別ファイルエラーではなく、その後のDB更新等の例外がownerでFailedになり、ViewではObserveFaultによるログだけで終わる。利用者に処理の失敗・部分適用が分からない。

**成立条件:** 対象操作の上位処理やDB反映が失敗する。既に個別ファイルエラーが通知されるケースへの二重通知を避ける。

**到達経路:**

1. 右クリック拡張子変更／文字化け修正→SelectedChartMutationWorkflowOwner。
2. ownerが例外をSelectedChartMutationResult.Failedとして返す。
3. MainWindowの結果消費がTask.FromException(...).ObserveFaultだけで、UI終端通知に渡さない。

**修正方針:**

- 結果の終端を既存feature owner／dialog gatewayへ集約し、上位failure・receiptを1回通知する。
- 既存のファイル別通知済み情報と区別する。ファイル変更済みだがDB失敗等の部分適用を「何も変わっていない」と表示しない。
- BMS-011の非同期化に先行して、成功・失敗・受付拒否の結果消費を揃える。

**保全条件・対象外:** MainWindowにdomain処理を追加しない。移動・削除等の別workflowを通知の見た目だけで一括再設計しない。

**決定待ち:** 個別ファイル通知と上位失敗通知の担当分担。部分適用・受付拒否を含む終端ごとの通知。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/Views/MainWindow.cs:7650–7671](../../BeMusicSeeker/Views/MainWindow.cs#L7650-L7671) | 改修：拡張子変更結果の消費 |
| [BeMusicSeeker/Views/MainWindow.cs:7720–7741](../../BeMusicSeeker/Views/MainWindow.cs#L7720-L7741) | 改修：fixEncodingSelectedBMS |
| [BeMusicSeeker/ViewModels/MainWindow/SelectedChartMutationWorkflowOwner.cs:602–642](../../BeMusicSeeker/ViewModels/MainWindow/SelectedChartMutationWorkflowOwner.cs#L602-L642) | 改修：ApplyEncodingのFailed結果 |
| [BeMusicSeeker/Views/MainWindowSelectedChartContextMenuTerminals.cs](../../BeMusicSeeker/Views/MainWindowSelectedChartContextMenuTerminals.cs) | 改修候補：結果を既存 View terminal へ渡す接続 |

**受入条件（案）:**

- [ ] **BMS-010-AC1** — 実View terminal相当の入口からDB更新例外を返すと、利用者向け失敗通知が1回届く。
- [ ] **BMS-010-AC2** — 個別ファイルエラーが通知済みのとき重複せず、成功時にはエラーを出さない。
- [ ] **BMS-010-AC3** — failure後もoperation gateとUI suppressionが解放され、次操作が可能になる。

**検証候補:** [SelectedChartMutationWorkflowOwnerTests.cs](../../BeMusicSeeker.Tests/SelectedChartMutationWorkflowOwnerTests.cs)、[MainWindowPendingPackageMutationViewTerminalTests.cs](../../BeMusicSeeker.Tests/MainWindowPendingPackageMutationViewTerminalTests.cs)。

**仕様反映先:** [warning-model.md](../spec/warning-model.md)、[dialog-route-inventory.md](../spec/dialog-route-inventory.md)、[library-mutation-boundary.md](../spec/library-mutation-boundary.md)。

**工数の根拠:** 既存resultのUI終端接続・重複排除・resource追加が中心。 見積り確度は中。

**続けて整理する項目:** [BMS-011](#bms-011)：同じ ApplyEncoding terminal。先に定めた結果消費を非同期経路へ持ち越す。 [BMS-012](#bms-012)：拡張子変更の再 hash 失敗を既存の上位失敗通知へ接続する。

<a id="bms-011"></a>
### BMS-011 — 文字化け修正の同期I/OをUIから外し、競合操作の直列化を維持する

**事象・影響:** 右クリックの文字コード設定が同期でファイル読込み・解析・DB反映を行い、多数対象ではUIスレッドを占有する。非同期化だけで既存の同時実行禁止を外すと、別の破壊的操作との競合を新設してしまう。

**成立条件:** 所持BMSを多数選択して文字化け修正／encoding指定を実行する。入力の破損やDB異常は必要ない。

**到達経路:**

1. View.fixEncodingSelectedBMS→同期terminal.ApplyEncoding→owner.ApplyEncoding。
2. BMSLibrary.SetBMSFilesEncoding→CatalogMaintenanceOwner→maintenance serviceの読込み・DB書込みが呼出元UI上で進む。
3. 完了までWPFの通常入力・描画が処理されにくくなる。

**修正方針:**

- UIで選択対象とencodingをimmutable requestとして取得し、ownerのasync経路でI/Oを実行する。View固有更新だけをUIへ戻す。
- 既存 chart-file operation gate と library mutation lease の責任を維持し、対象操作の終端まで新たな移動・削除・導入・手動 LR2 同期等の競合要求を Busy として拒否する。既に受理した導入キューや必須の LR2 同期は、その元の owner が定める終端義務を維持し、未受理要求と同じ扱いで捨てない。Task.Run や await 化を理由に暗黙の後続キューを作らない。論理的な操作権と thread-affine な lock を区別し、UI 通知時に後者を持ち越さない。
- BMS-010の失敗表示、成功時refresh、終了時の追跡を維持する。キャンセルがcommit境界に対応しないなら安易に途中中断を追加しない。

**保全条件・対象外:** Task.Run内からWPF mutable collectionへ直接触れない。UIが応答することを、別mutationの同時許可と混同しない。 単純な同期APIを全呼出元で一斉廃止する必要はないが、production UI routeを確実に切り替える。

**実装時に決める事項:** UIで固定する選択要求、既存操作権の寿命、終了待機への接続。設定画面の現行利用を保ったまま、処理で必要なencoding等を開始時の入力に固定する。キャンセルを追加する場合の安全な中断境界。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/Views/MainWindow.cs:7720–7741](../../BeMusicSeeker/Views/MainWindow.cs#L7720-L7741) | 改修：fixEncodingSelectedBMS |
| [BeMusicSeeker/Views/MainWindowSelectedChartContextMenuTerminals.cs:94–121](../../BeMusicSeeker/Views/MainWindowSelectedChartContextMenuTerminals.cs#L94-L121) | 改修：同期ApplyEncoding terminal |
| [BeMusicSeeker/ViewModels/MainWindow/SelectedChartMutationWorkflowOwner.cs:602–642](../../BeMusicSeeker/ViewModels/MainWindow/SelectedChartMutationWorkflowOwner.cs#L602-L642) | 改修：ApplyEncoding |
| [BeMusicSeeker/Models/BMSLibrary.cs:11730–11757](../../BeMusicSeeker/Models/BMSLibrary.cs#L11730-L11757) | 参照：SetBMSFilesEncoding |
| [BeMusicSeeker/Models/BmsLibraryInternal/CatalogMaintenanceOwner.cs:336–374](../../BeMusicSeeker/Models/BmsLibraryInternal/CatalogMaintenanceOwner.cs#L336-L374) | 参照：ApplyEncodingの読込み・DB準備 |

**受入条件（案）:**

- [ ] **BMS-011-AC1** — 制御可能な I/O 待機中でも Dispatcher 上の無関係な callback と、保持済み一覧の閲覧・選択が実行できる。表示は前回確定分でよく、変更途中の mutable state を直接読まない。
- [ ] **BMS-011-AC2** — UIと実owner入口の両方で、未開始の競合変更がBusyで拒否され、後続の副作用・暗黙キュー・自動実行がない。設定画面の表示・編集・既存Cancelは利用でき、Save拒否等は設定仕様に従う。既に受理した導入queueは失われず、導入実行中の追加ZIP予約も退行させない。
- [ ] **BMS-011-AC3** — 正常・DB失敗・開始拒否でgateが解放され、encodingと必要なDB／表示が契約どおり整合する。
- [ ] **BMS-011-AC4** — 終了処理がworkerを見失わず、UIでTask.Wait等の同期待ちを追加しない。

**検証候補:** [SelectedChartMutationWorkflowOwnerTests.cs](../../BeMusicSeeker.Tests/SelectedChartMutationWorkflowOwnerTests.cs)、[MainWindowPendingPackageMutationViewTerminalTests.cs](../../BeMusicSeeker.Tests/MainWindowPendingPackageMutationViewTerminalTests.cs)、[ChartMutationActivityOwnerTests.cs](../../BeMusicSeeker.Tests/ChartMutationActivityOwnerTests.cs)。

**仕様反映先:** [library-mutation-boundary.md](../spec/library-mutation-boundary.md)、[application-shutdown.md](../spec/application-shutdown.md)。

**工数の根拠:** async APIとUI terminal、既存gate、shutdown、回帰fixtureをまたぐ。 見積り確度は中。

**続けて整理する項目:** [BMS-010](#bms-010)：失敗・拒否の終端を維持したまま非同期化する。 [BMS-012](#bms-012)：同じ owner と操作権の回帰範囲を共有する。

<a id="bms-012"></a>
### BMS-012 — 拡張子変更の重複削除を現在の内容確認に限定する

**事象・影響:** 拡張子変更先に衝突があると、以前読み込んだsource hashを現在のsource内容とみなし、相手の現在hashと一致しただけでsourceを恒久削除し得る。読み込んだ後にsourceを編集した場合に成立する。

**成立条件:** 所持譜面を外部編集・保存し、再読込み前に拡張子変更を実行する。変更先の既存ファイルが編集前ハッシュと一致する場合が対象。ロード後の外部編集は排他的な通常操作の前提から外れるため、衝突時だけの安価な防御に限定する。

**到達経路:**

1. 拡張子変更UI→SelectedChartMutationWorkflowOwner→libraryのrename coordinator。
2. BmsLibraryLibraryFileOperationsService.ResolveFileCollisionWithSuffixがsourceHashHintを採用し、非空ならsourceを再hashしない。
3. 現在のdestination hashと古いhintが一致→DuplicateMatched→DeleteFileDirect(sourcePath)。

**修正方針:**

- 削除候補となる衝突経路に限ってsourceの現在内容を読み、destinationの現在内容と比較する。snapshot hashを破壊的判断の根拠にしない。
- 読込み不能なら重複とみなさず、既存suffix退避または明示失敗を選ぶ。読み直しは対象衝突だけに閉じる。
- 現在hashを計算済みの場合は同一操作内で再利用し、全譜面の常時再hashはしない。

**保全条件・対象外:** 外部変更の常時監視、全譜面の再ハッシュ、汎用 TOCTOU 対策は追加しない。恒久削除を判断する直前の衝突経路だけを修正範囲とする。

**決定待ち:** 現在の source を読めない場合、suffix 回避を使うか明示失敗にするか。どちらも恒久削除はしない。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/ViewModels/MainWindow/SelectedChartMutationWorkflowOwner.cs:425–508](../../BeMusicSeeker/ViewModels/MainWindow/SelectedChartMutationWorkflowOwner.cs#L425-L508) | 参照：RenameInvalidExtensionsAsync入口 |
| [BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryLibraryFileOperationsService.cs:1358–1446](../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryLibraryFileOperationsService.cs#L1358-L1446) | 改修：ProcessInvalidExtensionRename / DeleteFileDirect |
| [BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryLibraryFileOperationsService.cs:1459–1518](../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryLibraryFileOperationsService.cs#L1459-L1518) | 改修：ResolveFileCollisionWithSuffix |

**受入条件（案）:**

- [ ] **BMS-012-AC1** — ロード後にsourceを編集し、destinationが古い内容の場合、sourceが削除されず現在内容を保全する。
- [ ] **BMS-012-AC2** — 両方の現在内容が一致する場合だけ従来の重複扱いを許容する。hash取得不能は削除判定にならない。
- [ ] **BMS-012-AC3** — 正常なsuffix採番とrename後のDB更新・失敗通知を維持する。

**検証候補:** [BmsLibraryLibraryFileOperationsServiceTests.cs](../../BeMusicSeeker.Tests/BmsLibraryLibraryFileOperationsServiceTests.cs)、[SelectedChartMutationWorkflowOwnerTests.cs](../../BeMusicSeeker.Tests/SelectedChartMutationWorkflowOwnerTests.cs)、[BmsLibraryPackageInstallServiceTests.cs](../../BeMusicSeeker.Tests/BmsLibraryPackageInstallServiceTests.cs)。

**仕様反映先:** [file-db-consistency.md](../spec/file-db-consistency.md)、[library-mutation-boundary.md](../spec/library-mutation-boundary.md)、[duplicate-file-check.md](../spec/duplicate-file-check.md)。

**工数の根拠:** 削除判断のhash入力を限定修正し、古いsnapshotの回帰例を加える。 見積り確度は中。

**続けて整理する項目:** [BMS-010](#bms-010)：再読込み不能の終端をログだけで終わらせない。

<a id="pending-install"></a>
## 保留パッケージと導入結果

BMS-014 の全件同一推定先での成立確認を先行し、重複実装の要否を確定する。BMS-013 の削除前完全性、BMS-015 の原因保持、BMS-016 の受付競合時の終端方針は独立した単位。

**既存計画・仕様との境界:** [推定導入の逐次実行記録](2026-09-08-r2-committed-install-hashes.md)と[パッケージ移動の統合記録](2026-09-08-r5-package-file-mutation.md)には成功実績・停止後の未着手保持がある。BMS-014 はその防御を全件同一推定先で確認する項目であり、既存の group 実行や失敗集合の補集合方式へ戻さない。

<a id="bms-013"></a>
### BMS-013 — 解析不能譜面を含むパッケージを既所持のみと判定して恒久削除しない

**事象・影響:** 読込み・解析に失敗した譜面が一覧から除外され、残った読める譜面がすべて既所持なら、パッケージ全体が恒久削除対象になる。不明な内容まで既所持とみなす点が問題。

**成立条件:** 読める既所持譜面と、読めない／解析不能な譜面が同じ保留パッケージにあり、「既所持譜面のみのパッケージをごみ箱を経由せずに削除」を実行する。

**到達経路:**

1. パッケージ探索／ChartEntries構築で解析に成功したentriesを得る。
2. PendingPackageWorkflowOwner.DeleteInstalledOnlyPendingPackageSourcesAsync→GetPendingPackagesContainingOnlyInstalledCharts。
3. entries.All(isInstalledChart)だけで対象選択→パッケージsourceを直接削除。読めなかった譜面もフォルダとともに失われる。

**修正方針:**

- 探索snapshotで「候補譜面の列挙・読込みが完全か」を保持し、完全性が不明／失敗のパッケージを既所持のみの削除候補から外す。
- 既存のdiscovery結果に必要最小限のfailure／completenessを追加し、削除入口で判断する。削除時に毎回全ライブラリを再解析しない。
- スキップ数・理由を利用者へ示し、別の明示的ファイル削除機能は従来契約のままとする。

**保全条件・対象外:** 解析エラー譜面の仕様解釈や自動修復は対象外。明示的な「すべてのファイルを削除」と、この安全判定付き機能を混同しない。 将来形式のすべてを勝手に譜面扱いしない。現行対応拡張子と列挙範囲を根拠にする。

**決定待ち:** 列挙・読込み・解析の完全性を運ぶ既存 snapshot と、その再走査時の更新方法。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/ViewModels/MainWindow/PendingPackageWorkflowOwner.cs:774–810](../../BeMusicSeeker/ViewModels/MainWindow/PendingPackageWorkflowOwner.cs#L774-L810) | 改修：DeleteInstalledOnlyPendingPackageSourcesAsync |
| [BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryPackageInstallService.cs:370–388](../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryPackageInstallService.cs#L370-L388) | 改修：GetPendingPackagesContainingOnlyInstalledCharts |
| [BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryPackageInstallService.cs:458–489](../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryPackageInstallService.cs#L458-L489) | 改修：探索時のparsedChartEntriesとcomplete判定 |
| [BeMusicSeeker/Models/ChartPackage.cs:41–50](../../BeMusicSeeker/Models/ChartPackage.cs#L41-L50) | 改修：ChartEntriesのsnapshot参照 |
| [docs/manual.ja.md:866–874](../../docs/manual.ja.md#L866-L874) | 改修：既所持のみ直接削除の利用者契約 |
| [BeMusicSeeker/Models/BmsLibraryInternal/PackageInstallEstimationSnapshot.cs:216–249](../../BeMusicSeeker/Models/BmsLibraryInternal/PackageInstallEstimationSnapshot.cs#L216-L249) | 改修：BuildPackageChartDiscoverySnapshot / 完全性の伝達範囲 |
| [BeMusicSeeker/Models/BmsLibraryInternal/ChartPackageDiscoveryResult.cs](../../BeMusicSeeker/Models/BmsLibraryInternal/ChartPackageDiscoveryResult.cs) | 改修候補：既存 discovery 結果で完全性を表現する場合の型接続 |

**受入条件（案）:**

- [ ] **BMS-013-AC1** — 読める既所持1件＋読込み失敗1件のpackageで、sourceを変更せず保留に残し、理由を通知する。
- [ ] **BMS-013-AC2** — 全候補を完全に読めて全件既所持なら従来どおり対象になる。未所持／0件／列挙失敗は安全側に除外する。
- [ ] **BMS-013-AC3** — 失敗を除去して再走査できた後は古い不完全snapshotに固定されず、正しい対象判定に戻る。

**検証候補:** [BmsLibraryPackageInstallServiceTests.cs](../../BeMusicSeeker.Tests/BmsLibraryPackageInstallServiceTests.cs)、[PendingPackageWorkflowOwnerTests.cs](../../BeMusicSeeker.Tests/PendingPackageWorkflowOwnerTests.cs)、[BmsLibraryInitializationInstallTests.cs](../../BeMusicSeeker.Tests/BmsLibraryInitializationInstallTests.cs)。

**仕様反映先:** [drop-install-ingress.md](../spec/drop-install-ingress.md)、[install-estimation-current-logic.md](../spec/install-estimation-current-logic.md)、[file-db-consistency.md](../spec/file-db-consistency.md)。

**工数の根拠:** 完全性情報を探索から削除入口まで運び、キャッシュ更新と全体削除条件を確認する。 見積り確度は中。

**続けて整理する項目:** [BMS-014](#bms-014)：同じ保留除外・source 保全の確認。ただし削除可否と未着手保持は別条件。 [BMS-016](#bms-016)：discovery／保留 snapshot に関係する変更を分離する。

<a id="bms-014"></a>
### BMS-014 — 手動復旧を伴う一括導入で未着手パッケージが保留に残ることを確認する

**事象・影響:** 同じ推定先へ A・B・C を一括導入し、A は成功、B は主処理と補償の両方が失敗して ManualRecoveryRequired、C は未着手となる条件で、C の保留状態を失わないことを確認する。基準コミットには成功分だけを除外し、手動復旧で後続を止める防御があるため、不具合の存在を断定せず検証タスクとして扱う。

**成立条件:** A・B・C の推定導入先をすべて同一にする。B の DB／ファイル主処理と補償に失敗を注入し、実際の推定先インストール入口から呼び出す。未着手 C を失敗・成功のどちらにも混ぜないことを観測する。

**到達経路:**

1. 推定先インストール UI → PendingPackageWorkflowOwner.InstallPendingAsync → store.ManualInstallPackagesWithReceipt → BMSLibrary の receipt 付き入口。
2. 基準コミットでは、ExecuteEstimatedInstallBatchPlanがSelectedPendingPackagesを1件ずつ処理し、packageSucceededの場合だけPendingPackagesToRemoveへ追加する。
3. packageRequiresTerminalStop で break し、上位は PendingPackagesToRemove の明示集合だけを保留一覧から除去する。参照テストは A/C が同じ導入先、B が別の導入先を使うため、全件同一導入先の停止境界を別に確認する。

**修正方針:**

- 最初の作業単位は成立条件の確認と既存テスト範囲の評価。全件同一導入先を設定し、実入口から source／DB／保留行／receipt を観測する。
- 既存防御で満たされる場合は production code を変更しない。必要な既存テストの条件拡張または既存検証の確認で閉じる。
- 再現した場合だけ、完了・失敗・未着手を明示したpackage結果から保留除外集合を作る。要求集合−失敗集合を成功集合とみなさない。Bの手動復旧資料を保持し、Cを開始しない。

**保全条件・対象外:** 基準コミットの防御を重複実装しない。検証で不変条件を満たす場合はコード変更なしで完了できる。 新しい自動再開・永続replay・未着手の擬似成功化は導入しない。Bを通常の安全な再試行対象と誤表示しない。

**決定待ち:** 全件同一推定先での既存防御の有効性。違反が確認された場合に限り、production の改修範囲を追加する。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryPackageInstallService.cs:2342–2354](../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryPackageInstallService.cs#L2342-L2354) | 参照：package単位の反復 |
| [BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryPackageInstallService.cs:2417–2472](../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryPackageInstallService.cs#L2417-L2472) | 参照：成功集合への追加とterminal stop |
| [BeMusicSeeker/Models/BMSLibrary.PackageInstall.cs:2060–2088](../../BeMusicSeeker/Models/BMSLibrary.PackageInstall.cs#L2060-L2088) | 参照：一件のInstallWorkPackageを実行 |
| [BeMusicSeeker/Models/BMSLibrary.PackageInstall.cs:2130–2145](../../BeMusicSeeker/Models/BMSLibrary.PackageInstall.cs#L2130-L2145) | 参照：明示PendingPackagesToRemoveによる除外 |
| [BeMusicSeeker.Tests/BmsLibraryPackageInstallServiceTests.cs:575–670](../../BeMusicSeeker.Tests/BmsLibraryPackageInstallServiceTests.cs#L575-L670) | 検証候補：既存prefix/suffix保持テスト。Bは別destination |

**受入条件（案）:**

- [ ] **BMS-014-AC1** — A成功／B ManualRecoveryRequired／C未着手で、Aだけが保留除外され、BとCは失われない。CのsourceとDB行に変更がない。
- [ ] **BMS-014-AC2** — Aのdurable結果とBのrecovery pathを保持し、UIが一括成功としない。
- [ ] **BMS-014-AC3** — 同一推定先、異なる推定先、元パッケージ保持／削除設定について、関係する最小の組合せで停止境界を確認する。
- [ ] **BMS-014-AC4** — 既存防御で受入条件を満たす場合は、成立条件・確認結果・既存テストの範囲を記録して完了し、同じ防御を重複実装しない。

**検証候補:** [BmsLibraryPackageInstallServiceTests.cs](../../BeMusicSeeker.Tests/BmsLibraryPackageInstallServiceTests.cs)、[PendingPackageWorkflowOwnerTests.cs](../../BeMusicSeeker.Tests/PendingPackageWorkflowOwnerTests.cs)。

**仕様確認先（実装変更は条件確認後）:** [file-db-consistency.md](../spec/file-db-consistency.md)、[library-mutation-boundary.md](../spec/library-mutation-boundary.md)、[install-estimation-current-logic.md](../spec/install-estimation-current-logic.md)。

**工数の根拠:** 4–8人時は再現確認・coverage評価のみ。再現して修正が必要なら追加16–32人時を暫定枠とし再見積りする。 見積り確度は低。

**続けて整理する項目:** [BMS-013](#bms-013)：保留行と source が残ることを確認する fixture が近い。 [BMS-015](#bms-015)：複合障害時の receipt と復旧情報を確認する境界が近い。

<a id="bms-015"></a>
### BMS-015 — 主失敗・補償・cleanupの原因をreceipt内で保持する

**事象・影響:** DB確定前の主失敗を補償できても、その後のstaging／backup cleanupが失敗すると、receiptの例外はcleanupFailuresだけを含み、元の失敗原因が失われる。復旧時の判断材料が欠落する。

**成立条件:** 主処理失敗→補償成功→cleanup失敗という複合障害。ManualRecoveryRequiredへ移ること自体を誤りとしているわけではない。

**到達経路:**

1. FileDbMutationBoundary.HandlePrecommitFailure(failure)→CompensateOnce(failure)。
2. 補償receiptがmanual recoveryでなければstage／backupをbest-effort削除。
3. cleanup失敗時にnew AggregateException(cleanupFailures)だけで新receiptを作り、failureを含めない。

**修正方針:**

- 元failureとcleanupFailuresを同じ例外木または既存typed failure factへ保持し、phaseを区別して報告する。
- 補償を二重実行せず、manual recoveryの残存pathとdurableCommit:falseを維持する。
- report／loggerで主原因が参照可能かまで確認する。新規の汎用例外frameworkは作らない。

**保全条件・対象外:** ManualRecoveryRequiredを単純Failed／成功へ落とさない。cleanupの再試行回数を増やす話ではない。

**決定待ち:** 主失敗・cleanup 失敗・phase を保持する例外／結果の最小構造。残存 path の既存表現は維持する。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/Models/BmsLibraryInternal/FileDbMutationBoundary.cs:531–560](../../BeMusicSeeker/Models/BmsLibraryInternal/FileDbMutationBoundary.cs#L531-L560) | 改修：HandlePrecommitFailure |
| [BeMusicSeeker/Models/BmsLibraryInternal/FileDbMutationBoundary.cs:561–601](../../BeMusicSeeker/Models/BmsLibraryInternal/FileDbMutationBoundary.cs#L561-L601) | 改修：CompensateOnce入口 |

**受入条件（案）:**

- [ ] **BMS-015-AC1** — 異なる識別子の主失敗とcleanup失敗を注入し、返るreceiptから両方とphaseが確認できる。
- [ ] **BMS-015-AC2** — 補償は一度のみ、残存stage／backupのrecovery pathが保持される。
- [ ] **BMS-015-AC3** — cleanup成功時は既存の主失敗結果を保ち、主原因の重複追加を避ける。

**検証候補:** [BmsLibraryMutationBoundaryTests.cs](../../BeMusicSeeker.Tests/BmsLibraryMutationBoundaryTests.cs)、[FileDbMutationReportTests.cs](../../BeMusicSeeker.Tests/FileDbMutationReportTests.cs)。

**仕様反映先:** [file-db-consistency.md](../spec/file-db-consistency.md)、[library-mutation-boundary.md](../spec/library-mutation-boundary.md)、[warning-model.md](../spec/warning-model.md)。

**工数の根拠:** 例外構築の修正は小さいが、receiptとterminal reportの回帰確認を含める。 見積り確度は中。

**続けて整理する項目:** [BMS-014](#bms-014)：導入停止時の receipt と主原因を確認する障害設定を共有できる。

<a id="bms-016"></a>
### BMS-016 — 保留の自動推定を維持し、推定中の追加手動変更を拒否する

**事象・影響:** 背景推定queueがbatchを取り出した後、前景保留操作との受付競合で例外になる。ログは延期と表現するがbatchは戻らず、後で自動実行する所有も残らない。UI防止策があるため広い発生頻度は想定しないが、保留へ入れた譜面の自動推定という機能を失う。

**対象ユースケース:** 本体未導入・音源不足等で保留へ分類された譜面を、既存の対象条件に従って自動推定する。処理中も保留一覧を閲覧・選択できるが、その選択に対する追加の手動推定・削除等は受け付けなくてよい。拒否した手動操作を後で自動実行する機能は不要である。自動推定の結果が候補なし／推定不能になる通常の条件は変更しない。

**成立条件:** 起動復元・追加から自動推定が予約され、別の前景保留操作が共有非待機受付を保持している時点でbatchが開始する。保留公開から自動処理の開始までの隙間も対象とし、実入口で成立する順序を確認する。

**到達経路:**

1. 保留追加／起動復元→PackageLifecycleOwner.TryEnqueuePendingEstimateBatch→既存queueへ自動要求を渡す。
2. PendingInstallEstimateQueueProcessor.ProcessLoopがDequeueしてactiveBatchへ移す。
3. BMSLibrary.ProcessPendingInstallEstimateBatchが前景と共有する受付を取れず例外となり、queueはbatchFailedの後にactiveを消す。

**修正方針:**

- 自動推定が未着手・実行中であることと、新規の手動保留操作の受付を既存ownerで接続する。推定中に手動操作を割り込ませず、UIと実ownerの双方でBusyを返す。表示更新が遅れてもowner側で断れるようにする。
- 保留公開／予約から推定開始までの責任を閉じる。元の導入・復元処理の終了後にhandoffするか、既存queueが受付可能になるまで未着手batchを保持する案を選ぶ。元処理が受付を保持したまま、同じ受付を必要とする推定を待つ循環を作らない。
- 受理済み自動batchは既存の順序で自動的に処理されることを維持し、Busyだけで捨てない。試行前の受付待ちと、本当に開始した推定の失敗を分ける。即時再投入loop、実行途中のreplay、一般的な例外retryは追加しない。
- 導入中の追加ZIPは既存導入queueへ受け付ける。導入と自動推定の実行順を整えるために、その予約機能やbatch内の候補／package計算並列を削除しない。

**保全条件・対象外:** 手動再推定を必須とする縮退、汎用scheduler、永続queue、アプリ再起動後の途中再開保証は追加しない。既存の推定対象条件、候補評価、推定不能、取消・終了の意味を維持する。推定完了は自動インストールや候補の存在を保証するものではない。

**実装時に決める事項:** 元処理からのhandoffと既存queueの未着手所有の接続位置、手動Busyの開始・解除位置。自動推定の維持は採用済みの要件であり、未決なのは接続方法である。既存の進捗表示を排他の根拠にせず、PackageLifecycleOwnerの受付を正本にできる最小案を選ぶ。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/Models/PendingInstallEstimateQueueProcessor.cs:115–188](../../BeMusicSeeker/Models/PendingInstallEstimateQueueProcessor.cs#L115-L188) | 改修：ProcessLoop / Dequeue / 未着手所有と終端 |
| [BeMusicSeeker/Models/BMSLibrary.cs:3325–3356](../../BeMusicSeeker/Models/BMSLibrary.cs#L3325-L3356) | 改修：背景推定batchの受付境界 |
| [BeMusicSeeker/Models/BmsLibraryInternal/PackageLifecycleOwner.cs](../../BeMusicSeeker/Models/BmsLibraryInternal/PackageLifecycleOwner.cs) | 改修候補：TryEnterPendingOperation / TryEnqueuePendingEstimateBatch、共有受付とhandoff |
| [BeMusicSeeker/ViewModels/MainWindow/PendingPackageWorkflowOwner.cs](../../BeMusicSeeker/ViewModels/MainWindow/PendingPackageWorkflowOwner.cs) | 改修候補：手動推定・削除等の受付／拒否結果 |
| [BeMusicSeeker/Views/MainWindow.cs](../../BeMusicSeeker/Views/MainWindow.cs) | 改修候補：保留操作menu／terminalの対象箇所だけ。画面全体を無効化しない |

**受入条件（案）:**

- [ ] **BMS-016-AC1** — 実際の保留追加・復元入口から対象譜面の自動推定が始まり、手動再要求なしに結果・推定不能等へ終端する。元処理が一時的に受付を持つ場合も、受理済み未着手batchを失わず、元処理の終了後に処理できる。
- [ ] **BMS-016-AC2** — 推定中の一覧閲覧・選択は可能で、追加の手動推定・削除は実入口でBusyとなり、副作用や手動要求の待機項目が作られない。推定終了後に拒否済み操作が自動起動せず、新しい明示操作だけを受理する。公開／開始の隙間と遅いUI更新でも同じ受付境界を保つ。
- [ ] **BMS-016-AC3** — 取消／終了で既存ownerが自動要求・worker・gateを終端し、新しい推定を始めない。実行を開始した推定失敗を延期と誤表示せず、再投入loopを作らない。消失した対象を別packageへ読み替えない。
- [ ] **BMS-016-AC4** — 導入Aの処理中にBのZIPを予約する実入口を通し、既存の順序で導入・保留分類と必要な自動推定が進む。相互待ち、受理した入力の消失、重複処理がなく、既存batch内並列の契約を変えない。

**検証候補:** [PendingInstallEstimateQueueProcessorTests.cs](../../BeMusicSeeker.Tests/PendingInstallEstimateQueueProcessorTests.cs)、[PendingPackageWorkflowOwnerTests.cs](../../BeMusicSeeker.Tests/PendingPackageWorkflowOwnerTests.cs)、[BmsLibraryInitializationInstallTests.cs](../../BeMusicSeeker.Tests/BmsLibraryInitializationInstallTests.cs)。追加dropのfixtureは [drop-install-ingress.md](../spec/drop-install-ingress.md) の既存coverageから選ぶ。

**仕様反映先:** [install-estimation-current-logic.md](../spec/install-estimation-current-logic.md)、[library-mutation-boundary.md](../spec/library-mutation-boundary.md)、[application-shutdown.md](../spec/application-shutdown.md)。

**工数の根拠:** 既存queueと共有受付の接続、手動Busy、公開から開始の隙間、取消／shutdown、追加ZIPの保全確認を含む8〜16人時。新しいschedulerを作る見積りではない。見積り確度は中。

**続けて整理する項目:** [BMS-017](#bms-017)：未受理の手動要求を副作用前に閉じる考え方を共有する。[BMS-024](#bms-024)：背景推定queueの終端が削除前drainの対象になる場合に接続を調整する。

<a id="preview-audio"></a>
## 一時試聴・展開領域・録音

BMS-017 の受付拒否を終端させた後、BMS-018 と BMS-019 の相対配置・所有記録をまとめて整理できる。BMS-020 は説明のみ。BMS-021 の録音音量復元は別の資源を扱う独立単位。

**既存計画・仕様との境界:** 一時展開の寿命は [managed-temp-files.md](../spec/managed-temp-files.md) の現行契約を維持する。BMS-020 を終了を跨ぐ保留ストレージの実装へ拡張しない。

<a id="bms-017"></a>
### BMS-017 — 一時導入試聴の受付拒否で未開始sessionを終了させる

**事象・影響:** BeginPlaybackでsessionを始めた後に一時コピー用の共通ファイル操作権が取れずreturnすると、実再生が始まっていないsessionが残る。別の導入処理中の保留譜面試聴で成立する。

**成立条件:** 保留譜面に有効な推定導入先があり、別のpackage導入等がchartFileOperationsを保持している間にその譜面を試聴する。

**到達経路:**

1. PlaybackPanelViewModelの再生入口→BeginPlaybackでgeneration／sessionを作る。
2. 推定先があるためStartTemporarilyInstalledChartへ進む。
3. TryEnterがfalseでそのままreturn。呼出元catchに入らず、開始済みsessionの停止／終端が呼ばれない。

**修正方針:**

- 必要な受付取得をsession開始前へ移すか、拒否を明示結果にして当該generationだけを終端処理する。
- 古い要求の拒否処理が後から始まった別sessionを止めないよう、既存generation照合を使う。
- 受付中であることを既存の通知／状態表現で示し、コピーや再生を実行済みと表示しない。

**保全条件・対象外:** 競合解消後の自動試聴replayを追加しない。アプリ全体のStopを無条件に呼び、無関係な再生を止めない。

**並行性の制約:** 導入中の新規一時試聴・録音、録音中の競合変更はBusyで断る。可能な範囲で副作用とsession開始前に受付を判定し、拒否済み試聴を待機queueへ入れない。試聴から競合変更へ進む既存経路ではStopと実cleanupを完了してから進める。受理済み試聴の遅いexit／cleanupを識別するsession identityは残す。設定画面を開くことは競合変更の実行とは扱わない。

**決定待ち:** 受付を session 開始前へ移すか、拒否された generation だけを終端するか。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/ViewModels/MainWindow/PlaybackPanelViewModel.cs:1365–1394](../../BeMusicSeeker/ViewModels/MainWindow/PlaybackPanelViewModel.cs#L1365-L1394) | 改修：BeginPlayback→一時試聴 |
| [BeMusicSeeker/ViewModels/MainWindow/PlaybackPanelViewModel.cs:1453–1471](../../BeMusicSeeker/ViewModels/MainWindow/PlaybackPanelViewModel.cs#L1453-L1471) | 改修：StartTemporarilyInstalledChartのTryEnter |

**受入条件（案）:**

- [ ] **BMS-017-AC1** — 共通file operation gateを別操作が保持中に試聴要求すると、session・timer・選択／再生状態が未開始として終端する。
- [ ] **BMS-017-AC2** — source名変更・一時コピー・player開始を一切行わず、gateは競合側の所有のまま。
- [ ] **BMS-017-AC3** — 受付解放後の新しい試聴が成功し、古い拒否cleanupで新sessionが停止しない。

**検証候補:** [PlaybackPanelViewModelTests.cs](../../BeMusicSeeker.Tests/PlaybackPanelViewModelTests.cs)、[MainWindowPlaybackWpfTests.cs](../../BeMusicSeeker.Tests/MainWindowPlaybackWpfTests.cs)、[ChartMutationActivityOwnerTests.cs](../../BeMusicSeeker.Tests/ChartMutationActivityOwnerTests.cs)。

**仕様反映先:** [playback-panel-presentation.md](../spec/playback-panel-presentation.md)、[external-chart-launch.md](../spec/external-chart-launch.md)。

**工数の根拠:** 拒否時の明示終端とgeneration保護を既存session管理へ接続する。 見積り確度は中。

**続けて整理する項目:** [BMS-018](#bms-018)：先に受付拒否の終端を閉じ、一時コピーの変更へ進む。 [BMS-019](#bms-019)：拒否時にはコピー先を所有していないことを保つ。

<a id="bms-018"></a>
### BMS-018 — 一時導入試聴の追加リソースを相対パスのままコピーする

**事象・影響:** 保留差分の一時試聴はpackage直下のファイルしかコピーせず、extra/new.wav等を参照する正常な差分で音源が欠ける。正式導入の再帰・相対パス保持と一致しない。

**成立条件:** 差分がpackageサブフォルダ内の追加音源等を参照し、推定導入先にはそのリソースがない。内蔵プレイヤーのbmson対応要求ではない。

**到達経路:**

1. 保留差分の試聴→StartTemporarilyInstalledChart→TopDirectoryOnlyで許可拡張子を列挙。
2. temporarilyCopyFilesがbasenameで導入先へ平坦コピー。サブフォルダ内のリソースは列挙されない。
3. 内蔵playerが相対path／basename fallbackで見つけられず、nullとして一部音を鳴らさない。

**修正方針:**

- package rootと相対pathを持つ一時コピーmanifestを作り、正式導入と互換の相対配置を保つ。許可拡張子の範囲は維持する。
- 既存destinationを上書きせず、作成したファイルと必要な空ディレクトリだけを試聴scopeで管理する。
- BMS-019の途中生成物管理を同時に設計し、正常・開始失敗・停止のすべてで所有物だけを回収する。

**保全条件・対象外:** TopDirectoryOnlyをAllDirectoriesへ変えるだけではbasename平坦化が残る。全リソース形式対応・任意path越境コピーは追加しない。 既存リソースをcleanupで削除しない。ディレクトリ再解析点等は既存の安全境界を迂回しない。

**決定待ち:** コピー元 root・相対 path・新規作成物を保持する一時的な作業一覧。保存形式や永続 manifest は追加しない。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/ViewModels/MainWindow/PlaybackPanelViewModel.cs:1498–1527](../../BeMusicSeeker/ViewModels/MainWindow/PlaybackPanelViewModel.cs#L1498-L1527) | 改修：一時試聴の列挙・コピー |
| [BeMusicSeeker/ViewModels/temporarilyCopyFiles.cs:75–128](../../BeMusicSeeker/ViewModels/temporarilyCopyFiles.cs#L75-L128) | 改修：basename化とcopy |
| [BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryPackageInstallService.cs:213–224](../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryPackageInstallService.cs#L213-L224) | 参照：正式導入の相対配置参照 |
| [Ribbit/BMS/BMSAutoPlayer.cs:39–85](../../Ribbit/BMS/BMSAutoPlayer.cs#L39-L85) | 参照：リソース解決 |

**受入条件（案）:**

- [ ] **BMS-018-AC1** — サブフォルダ追加音源を含む差分で、一時試聴と正式導入が同じ相対リソースを解決する。
- [ ] **BMS-018-AC2** — 別サブフォルダの同名ファイルを区別し、既存destinationファイルはbyte単位で不変。
- [ ] **BMS-018-AC3** — 正常終了・開始失敗・停止で、この試聴が作ったものだけが回収され、sourceと既存空でないdirectoryは残る。

**検証候補:** [TemporaryCopyFilesTests.cs](../../BeMusicSeeker.Tests/TemporaryCopyFilesTests.cs)、[PlaybackPanelViewModelTests.cs](../../BeMusicSeeker.Tests/PlaybackPanelViewModelTests.cs)、[BmsLibraryPackageInstallServiceTests.cs](../../BeMusicSeeker.Tests/BmsLibraryPackageInstallServiceTests.cs)。

**仕様反映先:** [external-chart-launch.md](../spec/external-chart-launch.md)、[managed-temp-files.md](../spec/managed-temp-files.md)、[path-identity.md](../spec/path-identity.md)。

**工数の根拠:** 再帰列挙だけでなく相対manifestとcleanup所有を合わせる必要がある。 見積り確度は中。

**続けて整理する項目:** [BMS-019](#bms-019)：同じ一時コピーの相対 path と所有記録を扱う。一つの改修単位にまとめやすい。 [BMS-017](#bms-017)：受付・session の寿命を先に揃える。

<a id="bms-019"></a>
### BMS-019 — 一時コピー失敗が残した部分ファイルを後始末対象に含める

**事象・影響:** 一時コピーは成功後にだけdestinationをcleanup対象へ追加する。コピー先生成後のI/O失敗で部分ファイルが残った場合、そのpathが登録されず導入先に残る。次回の存在チェックでコピーが省略され得る。

**成立条件:** コピーがdestinationを生成した後に失敗し、その部分ファイルが残る障害態様。すべてのFile.Copy失敗で残留すると断定しない。

**到達経路:**

1. temporarilyCopyFilesで既存destinationを除外→CopyFileを実行。
2. 成功後のcopiedFiles等への登録前に例外。catchはDisposeするが、未登録のpathは対象外。
3. 導入先に残骸が残り、後続試聴で既存ファイル扱いになる。

**修正方針:**

- 存在しなかったdestinationについて、この試行が新規作成を試みたpathをcopy前に管理する。既存ファイルは所有集合に入れない。
- 失敗時にも当該pathを存在確認して回収し、親directoryは自分で作った空のものだけ削除する。
- cleanup失敗は元のcopy失敗と残存pathを保持し、再生sessionを正しく終端する。

**保全条件・対象外:** 外部変更に対する全面監視は不要。既存ファイルを「回収できるから」と先に削除する実装にしない。

**決定待ち:** コピー失敗時の作成物判定と、cleanup 失敗を元の例外とともに返す既存経路。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/ViewModels/temporarilyCopyFiles.cs:30–52](../../BeMusicSeeker/ViewModels/temporarilyCopyFiles.cs#L30-L52) | 改修：Dispose対象 |
| [BeMusicSeeker/ViewModels/temporarilyCopyFiles.cs:82–128](../../BeMusicSeeker/ViewModels/temporarilyCopyFiles.cs#L82-L128) | 改修：成功後登録とcatch |
| [BeMusicSeeker/Models/Utils/ResilientFileMutationService.cs:60–83](../../BeMusicSeeker/Models/Utils/ResilientFileMutationService.cs#L60-L83) | 参照：同repoの部分出力想定 |

**受入条件（案）:**

- [ ] **BMS-019-AC1** — 実コピーseamが一部を書いてから失敗する条件で、sourceと既存destinationは不変、試行の部分ファイルだけが回収される。
- [ ] **BMS-019-AC2** — cleanupも失敗した場合は主原因と残存pathを通知し、失敗を成功へ変換しない。
- [ ] **BMS-019-AC3** — 正常コピー／既存fileスキップの挙動と、後続試聴の再実行可能性を維持する。

**検証候補:** [TemporaryCopyFilesTests.cs](../../BeMusicSeeker.Tests/TemporaryCopyFilesTests.cs)、[ResilientFileMutationServiceTests.cs](../../BeMusicSeeker.Tests/ResilientFileMutationServiceTests.cs)、[PlaybackPanelViewModelTests.cs](../../BeMusicSeeker.Tests/PlaybackPanelViewModelTests.cs)。

**仕様反映先:** [managed-temp-files.md](../spec/managed-temp-files.md)、[file-db-consistency.md](../spec/file-db-consistency.md)。

**工数の根拠:** copy前の所有記録とfault injection。018と同時なら重複作業が減る。 見積り確度は中。

**続けて整理する項目:** [BMS-018](#bms-018)：相対配置と途中生成物の記録を同じ作業一覧へまとめる。

<a id="bms-020"></a>
### BMS-020 — 一時展開パッケージの保留が終了を跨がないことを明記する

**事象・影響:** 管理用一時領域内の保留パッケージは通常終了でcleanupされ、次回は欠落行として除外される。これは現行仕様だが、マニュアルから終了を跨いで保留を維持できるように読める。

**成立条件:** アーカイブ／URLから管理一時領域へ展開されたpackageを保留したまま終了する。直接指定したユーザーフォルダの保留と区別する。

**到達経路:**

1. 展開→一時directory内packageの保留行を保存。
2. 通常終了→TempDirectoryPublisher.RemoveAllがsession所有の一時directoryを削除。
3. 次回初期化→missing pathの保留行を削除。元のユーザー所有アーカイブまでこの終了cleanupで消すわけではない。

**修正方針:**

- docs/manual.ja.md の保留操作と一時ファイル FAQ に、管理一時領域内の保留は終了で失われ、次回は再投入／再取得が必要である旨を書く。
- 通常フォルダの保留、ユーザー所有 ZIP、URL 取得した一時 ZIP の所有と寿命を分け、終了時 cleanup の対象を明示する。
- 文書で対象の違いを説明することを基本とする。保留画面への UI 補足が必要と判断した場合だけ、表示箇所と文言に変更範囲を追加する。

**保全条件・対象外:** 実装を恒久保留ストレージへ変更しない。session跨ぎの復旧機構や既存temp cleanupの無効化は対象外。

**決定待ち:** 保留操作節と FAQ の説明位置。UI 補足を追加するかは文書修正後の表示上の必要性で判断する。

writable path の初期範囲は docs/manual.ja.md。TempDirectoryPublisher と初期化処理は寿命を確認する参照先で、変更しない。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/TempDirectoryPublisher.cs:90–103](../../BeMusicSeeker/TempDirectoryPublisher.cs#L90-L103) | 参照：RemoveAll |
| [BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryInitializationService.cs:2463–2474](../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryInitializationService.cs#L2463-L2474) | 参照：missing path除外 |
| [devdocs/spec/managed-temp-files.md:27–37](../spec/managed-temp-files.md#L27-L37) | 参照：session cleanupの契約 |
| [docs/manual.ja.md:1063–1073](../../docs/manual.ja.md#L1063-L1073) | 改修：一時ファイルFAQ |

**受入条件（案）:**

- [ ] **BMS-020-AC1** — 利用者資料だけで「どの保留が終了で失われるか」「元ファイルの扱い」「次回どう再開するか」が判別できる。
- [ ] **BMS-020-AC2** — devdocs/spec/managed-temp-files.mdの現行寿命契約と矛盾せず、通常ユーザーフォルダが一律消えるとは書かない。
- [ ] **BMS-020-AC3** — 保留・展開・終了 cleanup の runtime 挙動を変更せずに説明を整える。UI 補足を採用する場合も、恒久保留や自動復旧を保証する表示にしない。

**検証候補:** [TempDirectoryPublisherTests.cs](../../BeMusicSeeker.Tests/TempDirectoryPublisherTests.cs)、[LocalizationResourceParityTests.cs](../../BeMusicSeeker.Tests/LocalizationResourceParityTests.cs)。

**仕様確認先（runtime 変更なし）:** [managed-temp-files.md](../spec/managed-temp-files.md)、[drop-install-ingress.md](../spec/drop-install-ingress.md)。

**工数の根拠:** 文書中心の改善。常時警告ダイアログの新設を含めず、必要最小限のUI補足まで。 見積り確度は高。

**続けて整理する項目:** [BMS-018](#bms-018)：試聴中だけの導入先コピーと、アプリ終了までの展開用領域を説明で区別する。 [BMS-027](#bms-027)：同じ利用者マニュアルの文書改善として編集を調整する。

<a id="bms-021"></a>
### BMS-021 — 録音失敗後のDeviceVolumeを復元し後続出力への倍率累積を防ぐ

**事象・影響:** 一括音声変換が1つのaudio sessionを使い回す一方、BMSAutoPlayWriterは正常末尾でしかDeviceVolumeを復元しない。1件失敗後もbatchが続くと、後続で成功した音声の音量に前件の倍率が累積する。最初の失敗通知は存在する。

**成立条件:** ノーマライズNONE、音量倍率が1以外で、倍率適用後のencoder準備・開始・書込みが失敗し、native cleanupは成功して次のファイルへ進む。

**到達経路:**

1. SelectedChartAudioConversionWorkflowOwnerがaudio sessionを1つ作り複数ファイルを変換。
2. BMSAutoPlayWriterが元DeviceVolumeを退避し倍率を適用→encoder処理が例外。正常末尾の復元を通らない。
3. ownerは失敗を記録しnative cleanup後に次へ進み、残った音量へ再度倍率が掛かる。0.5設定なら本来V×0.5の次件がV×0.25になる。

**修正方針:**

- 音量変更範囲をtry/finally等の局所scopeで閉じ、成功・失敗・取消のいずれも元値を戻す。
- 復元が失敗したら不確定なaudio sessionで次へ進まないよう、既存の継続可否判定へ接続する。主失敗は保持する。
- native resourceの停止・解放順序を維持し、音量復元だけのために音声処理全体を作り替えない。

**保全条件・対象外:** 実nativeでの障害頻度・出力波形は未確認。固定の音量値で毎回初期化して利用者設定を消さない。

**決定待ち:** 音量復元が失敗した場合の継続不可判定を、既存 native cleanup の継続判断へ接続する位置。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [Ribbit/BMS/BMSAutoPlayWriter.cs:70–129](../../Ribbit/BMS/BMSAutoPlayWriter.cs#L70-L129) | 改修：DeviceVolume退避・適用・復元 |
| [BeMusicSeeker/ViewModels/MainWindow/SelectedChartAudioConversionWorkflowOwner.cs:571–582](../../BeMusicSeeker/ViewModels/MainWindow/SelectedChartAudioConversionWorkflowOwner.cs#L571-L582) | 改修：batch共通session |
| [BeMusicSeeker/ViewModels/MainWindow/SelectedChartAudioConversionWorkflowOwner.cs:634–664](../../BeMusicSeeker/ViewModels/MainWindow/SelectedChartAudioConversionWorkflowOwner.cs#L634-L664) | 改修：単件失敗と継続 |
| [BeMusicSeeker/ViewModels/MainWindow/SelectedChartAudioConversionWorkflowOwner.cs:775–816](../../BeMusicSeeker/ViewModels/MainWindow/SelectedChartAudioConversionWorkflowOwner.cs#L775-L816) | 改修：native cleanup |

**受入条件（案）:**

- [ ] **BMS-021-AC1** — 1件目を倍率適用後に失敗させ、2件目は元音量×指定倍率で成功し、失敗回数で音量が累積しない。
- [ ] **BMS-021-AC2** — 成功／取消／encoder準備失敗の終端で元DeviceVolumeへ戻る。
- [ ] **BMS-021-AC3** — 復元またはnative cleanupが失敗して安全に継続できない場合、後続を開始せず主原因とcleanup原因を保持する。

**検証候補:** [SelectedChartAudioConversionWorkflowOwnerTests.cs](../../BeMusicSeeker.Tests/SelectedChartAudioConversionWorkflowOwnerTests.cs)、[PlaybackPanelViewModelTests.cs](../../BeMusicSeeker.Tests/PlaybackPanelViewModelTests.cs)。

**仕様反映先:** [audio-runtime-phase1.md](../spec/audio-runtime-phase1.md)、[bass-runtime-dependency-set.md](../spec/bass-runtime-dependency-set.md)。

**工数の根拠:** 復元scopeの局所修正と共通sessionの失敗継続テスト。実native検証が必要なら別laneを計画。 見積り確度は中。

<a id="db-lifecycle"></a>
## DB 後処理・終了・スコア表示

BMS-022 は局所的な結果通知として完了できる。BMS-023 と BMS-024 は実 DB 処理の終了条件を調整する。BMS-024 内も所有テーブル漏れと削除前停止を分ける。BMS-025 は主一覧の表示状態として独立。

**既存計画・仕様との境界:** [既存の待機改善](2026-09-06-wait-responsiveness.md)は SQLite の待機予算を対象外としているため、BMS-023 はその残課題を扱う。[LR2 起動手順の再構成計画](lr2-startup-procedural-orchestration-plan.md)は未実装であり、本領域の前提や同時実装範囲には含めない。

<a id="bms-022"></a>
### BMS-022 — LR2バックアップ後のDB最適化失敗を結果として消費する

**事象・影響:** RebuildDatabaseはVACUUM／REINDEX失敗をfalseで返すが、起動時バックアップの呼出元は戻り値を捨てる。バックアップは成功していても、続く最適化の失敗を利用者が認識できない。

**成立条件:** LR2バックアップを有効にし、取得条件を満たす起動でバックアップ公開が成功した後、song.dbまたは選択score DBの最適化が失敗する。

**到達経路:**

1. MainWindowViewModel初期化→SaveSelectedBackupsWithResult。
2. result.Savedの場合にBackup.RebuildDatabaseを実行。内部catchがfalseを返す。
3. 呼出元はboolを判定せずバックアップ結果だけを返すため、外側catchでも失敗を拾えない。

**修正方針:**

- 最低限boolを消費して最適化失敗warningをバックアップ結果へ追加する。必要なら失敗DBとphaseを持つ小さいtyped resultにする。
- 「バックアップ保存成功／最適化失敗」を分け、取得済み世代は成功のまま保全する。
- ログに対象と原因を残し、UIをresource化する。最適化が必須と誤解させる成功表示を修正する。

**保全条件・対象外:** バックアップをrollbackしたり、最適化失敗だけで起動を停止しない。DB schemaやVACUUM方式の全面変更はしない。

**決定待ち:** bool を警告へ伝える最小修正か、失敗 DB／段階を持つ小さい結果型か。バックアップ成功は取り消さない。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/Models/LR2/Backup.cs:229–255](../../BeMusicSeeker/Models/LR2/Backup.cs#L229-L255) | 改修：RebuildDatabase |
| [BeMusicSeeker/ViewModels/MainWindowViewModel.cs:4566–4595](../../BeMusicSeeker/ViewModels/MainWindowViewModel.cs#L4566-L4595) | 改修：戻り値未消費の呼出し |
| [docs/manual.ja.md:1020–1024](../../docs/manual.ja.md#L1020-L1024) | 改修：バックアップ後の最適化説明 |

**受入条件（案）:**

- [ ] **BMS-022-AC1** — バックアップ成功後に最適化を失敗させると、取得世代が残り、最適化のみの警告がUIへ届く。
- [ ] **BMS-022-AC2** — 最適化成功／未取得で最適化非実行のケースに誤警告しない。
- [ ] **BMS-022-AC3** — 複数DBの一部失敗を少なくとも全成功と誤報せず、原因がログから追える。

**検証候補:** [BackupTests.cs](../../BeMusicSeeker.Tests/BackupTests.cs)、[StartupLibraryInitializationWorkflowOwnerTests.cs](../../BeMusicSeeker.Tests/StartupLibraryInitializationWorkflowOwnerTests.cs)、[LocalizationResourceParityTests.cs](../../BeMusicSeeker.Tests/LocalizationResourceParityTests.cs)。

**仕様反映先:** [lr2-backup.md](../spec/lr2-backup.md)、[startup-initialization-flow.md](../spec/startup-initialization-flow.md)、[warning-model.md](../spec/warning-model.md)。

**工数の根拠:** 戻り値の上位伝播と部分成功通知が中心。詳細なper-DB結果を追加する場合は上限側。 見積り確度は中。

**続けて整理する項目:** [BMS-023](#bms-023)：DB 待機を変更した場合の最適化失敗も通知できるようにする。 [BMS-026](#bms-026)：同じ Backup.cs・BackupTests の変更を調整する。 [BMS-028](#bms-028)：取得しない場合に最適化へ進まない条件を保つ。

<a id="bms-023"></a>
### BMS-023 — SQLite内部待機と外側retryに操作単位の上限を設ける

**事象・影響:** SQLite BusyTimeoutが60秒の接続で、Delete／InsertOrReplaceの外側retryが初回に加えて最大10回、1秒間隔で繰り返される。1操作で内部待機が積み上がり、操作完了や終了drainが長期化する。

**成立条件:** 対象のSQLite処理がBusy／Lockedを返す。排他的利用を前提に、内部競合や失敗時の安価な終了条件を扱い、外部DB writerの同時利用を新仕様にしない。

**到達経路:**

1. LR2SongDBExtendedの接続生成でBusyTimeout=60秒。
2. SQLiteConnectionEx.Delete／InsertOrReplace→RetryIfLockedOrBusy→RetryHelper。
3. 各試行のSQLite待機に外側のsleep/retryが重なり、tracked connection／mutationの終端を待つshutdownも遅れる。

**修正方針:**

- 対象操作で許す総待機budgetと1回のSQLite待機を同じownerで整合させ、残余時間に応じて内部timeout／retry回数を制限する。
- 外側待機にcancel／shutdownを反映し、実行中SQLやtransactionを見失わずに終端する。await側だけtimeoutして実処理を放置しない。
- Commitは現行の一度だけ実行する契約を維持する。非冪等な処理、commit後の結果不明を自動replayしない。

**保全条件・対象外:** 60秒×11回は各試行が満額待った場合の構造上の積上げであり、全操作の実測時間ではない。 timeoutを一律ゼロにする、別threadのconnectionを強制Disposeする、全DB呼出しへ巨大retry基盤を追加する、といった修正はしない。

**複雑性の制約:** 総待機上限は保証したい結果であり、汎用 deadline coordinator の新設を指示しない。既存 retry 回数と1回の BusyTimeout の小さい整合だけで対象上限を満たせるかを先に検討する。同期 SQLite 呼出しの実行中を任意時点で即時中断できるとは仮定しない。

**決定待ち:** 総待機上限、一回の BusyTimeout、取消可能な境界の具体値・根拠。同期 SQLite API の能力確認後に確定する。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/Models/LR2/LR2SongDBExtended.cs:1384–1405](../../BeMusicSeeker/Models/LR2/LR2SongDBExtended.cs#L1384-L1405) | 改修：BusyTimeout |
| [BeMusicSeeker/Models/LR2/SQLiteConnectionEx.cs:26–83](../../BeMusicSeeker/Models/LR2/SQLiteConnectionEx.cs#L26-L83) | 改修：RetryIfLockedOrBusy / DML / Commit |
| [Ribbit/Util/RetryHelper.cs:59–101](../../Ribbit/Util/RetryHelper.cs#L59-L101) | 改修：retry回数と待機 |

**受入条件（案）:**

- [ ] **BMS-023-AC1** — 制御可能なBusy／Locked応答と時計／待機seamで、試行数と総budgetが上限内に収まり、数分の実sleepを使わず検証できる。
- [ ] **BMS-023-AC2** — cancel／shutdownでは未開始retryを行わず、進行中のDB処理を追跡したまま安全なrollback／終了へ到達する。
- [ ] **BMS-023-AC3** — 通常の一過性busyは定義した範囲で成功可能。Commit失敗をretryで成功へ化けさせない。

**検証候補:** [BmsLibraryMutationBoundaryTests.cs](../../BeMusicSeeker.Tests/BmsLibraryMutationBoundaryTests.cs)、[BmsPlaylistPersistenceLifecycleTests.cs](../../BeMusicSeeker.Tests/BmsPlaylistPersistenceLifecycleTests.cs)、[ApplicationDataUninstallWorkflowOwnerTests.cs](../../BeMusicSeeker.Tests/ApplicationDataUninstallWorkflowOwnerTests.cs)。

**仕様反映先:** [data-and-indexes.md](../spec/data-and-indexes.md)、[application-shutdown.md](../spec/application-shutdown.md)、[file-db-consistency.md](../spec/file-db-consistency.md)。

**工数の根拠:** SQLite同期API・transaction・shutdownの契約が絡む。budget値とcancel能力の確認により再見積りが必要。 見積り確度は低。

**続けて整理する項目:** [BMS-024](#bms-024)：削除前 drain は実 SQL 終了まで待つ。予算超過で待機側だけを切り離さない。

<a id="bms-024"></a>
### BMS-024 — アプリ関連データ削除前にwriterを止め、所有テーブルを漏れなく削除する

**事象・影響:** アンインストール対象一覧にplaylist_custom_folder_output_statusがなく、正常終了でも残る。さらに削除前にbackground writerを止めないため、成功ダイアログ中にIR取得が戻ると削除済みtableを再作成できる。同じ機能の2つの原因を1issueのsubtaskとして扱う。

**成立条件:** LR2 DBのアプリ関連データ削除を実行。対象漏れは通常経路、再作成はIR等のbackground処理が通信待ちのまま削除が進む場合。LR2 本来のスコア喪失ではなく、アプリ管理データの残存が対象。

**到達経路:**

1. 通常schema準備でplaylist_custom_folder_output_statusを作るが、BeMusicSeekerOwnedTableNamesに入っていない。
2. ApplicationDataUninstallWorkflowOwner→DB削除→成功ダイアログ→終了要求の順で、先行workerのdrainが先にない。
3. IR通信が戻りBmsLibraryDbGatewayのEnsure/Create経路でtableを再作成し、削除完了後にデータが残る。

**修正方針:**

- subtask A：実際のschema作成経路と所有table一覧を照合し、不足している所有tableを削除対象へ追加する。外部tableは触れない。
- subtask B：削除確認の承認後に新規writer受付停止→対象background workerをcancel／drain→DB削除→成功通知→終了の順へする。必要な既存shutdown機構を再利用する。
- 停止中の失敗は削除成功と報告せず、実行中writerを残したままDBだけ消さない。

**保全条件・対象外:** restore／一般reloadとuninstallを同じresetとして扱わない。全DB fileを削除しない。 ユーザーが削除をキャンセルした時点では通常動作を止めない。drainをUI同期waitで実装しない。

**並行性の制約:** 前景メニューの Busy 化だけでは background writer を停止できない。既存の停止要求・tracked task の完了待ちを削除前に接続する。削除承認後の遅い HTTP／DB 完了による再作成を防ぐが、別の全ジョブ管理基盤は追加しない。

**決定待ち:** 対象 writer と停止手順、停止後に削除が失敗した場合の終端。アプリ全終了後ではなく、DB が使用可能な間に必要な停止を完了する。

表一覧の補完と writer 停止は別の原因。削除前 drain の接続に限って shell／BMSLibrary 側を変更し、起動全体の再構成は含めない。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/Models/BmsLibraryInternal/PlaylistPersistenceRepository.cs:92–134](../../BeMusicSeeker/Models/BmsLibraryInternal/PlaylistPersistenceRepository.cs#L92-L134) | 参照：raw SQLによるstatus table生成 |
| [BeMusicSeeker/Models/LR2/LR2SongDBExtended.cs:13–30](../../BeMusicSeeker/Models/LR2/LR2SongDBExtended.cs#L13-L30) | 改修：BeMusicSeekerOwnedTableNames |
| [BeMusicSeeker/Models/LR2/LR2SongDBExtended.cs:1423–1434](../../BeMusicSeeker/Models/LR2/LR2SongDBExtended.cs#L1423-L1434) | 改修：Uninstall |
| [BeMusicSeeker/ViewModels/ApplicationDataUninstallWorkflowOwner.cs:87–150](../../BeMusicSeeker/ViewModels/ApplicationDataUninstallWorkflowOwner.cs#L87-L150) | 改修：削除と終了の順序 |
| [BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryIrService.cs:473–537](../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryIrService.cs#L473-L537) | 参照：通信後の書込み |
| [BeMusicSeeker/ViewModels/MainWindow/ShellShutdownWorkflowOwner.cs](../../BeMusicSeeker/ViewModels/MainWindow/ShellShutdownWorkflowOwner.cs) | 改修候補：削除前の writer 停止・drain と既存終了手順の接続のみ |
| [BeMusicSeeker/ViewModels/MainWindowViewModel.cs](../../BeMusicSeeker/ViewModels/MainWindowViewModel.cs) | 改修候補：削除前停止要求・結果の composition 接続のみ |
| [BeMusicSeeker/Models/BMSLibrary.cs](../../BeMusicSeeker/Models/BMSLibrary.cs) | 改修候補：既存 shutdown／worker 終了要求の接続のみ |

**受入条件（案）:**

- [ ] **BMS-024-AC1** — 実schema準備後にuninstallし、sqlite_masterでアプリ所有tableがなく、LR2本来・無関係tableが残る。
- [ ] **BMS-024-AC2** — IR通信を明示的に保留したまま削除承認→通信完了の順序を固定しても、削除後の再作成が発生しない。
- [ ] **BMS-024-AC3** — 削除キャンセル、worker終了失敗、DB削除失敗、成功ダイアログ中の各状態を区別し、一括成功と偽装しない。

**検証候補:** [LR2SongDBExtendedUninstallTests.cs](../../BeMusicSeeker.Tests/LR2SongDBExtendedUninstallTests.cs)、[ApplicationDataUninstallWorkflowOwnerTests.cs](../../BeMusicSeeker.Tests/ApplicationDataUninstallWorkflowOwnerTests.cs)、[BmsLibraryInitializationLoadTests.cs](../../BeMusicSeeker.Tests/BmsLibraryInitializationLoadTests.cs)。

**仕様反映先:** [data-and-indexes.md](../spec/data-and-indexes.md)、[application-shutdown.md](../spec/application-shutdown.md)、[startup-initialization-flow.md](../spec/startup-initialization-flow.md)。

**工数の根拠:** 対象一覧の修正は小さいが、先行writer停止と失敗時終端を安全に組み込む方が主コスト。 見積り確度は中。

**続けて整理する項目:** [BMS-023](#bms-023)：DB 終了条件を共有するが、待機予算の全面修正は削除対象漏れ修正の前提ではない。 [BMS-016](#bms-016)：背景推定の取消・完了待ちを含める場合に同じ終端を使う。

<a id="bms-025"></a>
### BMS-025 — スコア読込み不能を主一覧で未プレイと区別する

**事象・影響:** 構成済みscore DBの読込み失敗はtyped Failedで返るが、主一覧へ渡す過程で空スコア集合になり、所持曲が未プレイ相当に見える。元DBは変更されない。ランプビューアには失敗表示がある。

**成立条件:** 構成済みscore DBが存在するものの読めない／有効なDBでない。正常な空DBやscore未設定とは区別する。

**到達経路:**

1. BmsLibraryInitializationServiceがscore load Failedを返す。
2. BMSLibraryがログ後に空集合へ置換し、主一覧用snapshotでfailureを区別しない。
3. ChartScoreSnapshotは所持pathあり＋score nullをNO_PLAY相当にするため、読めない状態が未プレイ表示へ変わる。

**修正方針:**

- score sourceの読込み状態を主一覧snapshot／presentationまで保持し、正常空・未設定・読込み失敗を区別する。
- 最小案は失敗状態と警告を明示し、未プレイ値を正常データとして表示しないこと。旧snapshotを残すなら同じscore対象だけに限定しstaleを示す。
- ランプビューア等で既に使う失敗表現との一貫性を保つ。再読込み成功で警告を解除する。

**保全条件・対象外:** score.db修復・書換え、未プレイへのデータ変換はしない。前の別プレイヤーのscoreをfallbackとして表示しない。 フィルタや集計へ新しい不明カテゴリを追加する場合は、主一覧の表示だけの変更とは分けて意味と影響を仕様化する。

**表示鮮度の制約:** 常時最新化は要求しない。成功した旧 snapshot を残す案を採る場合も、読込み失敗と同じ対象の旧表示であることを区別し、取得不能を NO_PLAY と読み替えない。表示上の古さと正本書込みの認可を別扱いにする。

**決定待ち:** 主一覧の読込み不能表示と、同一 source の古い snapshot を保持するか。フィルタ・集計を変える場合は別途意味を決める。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryInitializationService.cs:2266–2327](../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryInitializationService.cs#L2266-L2327) | 参照：score load結果 |
| [BeMusicSeeker/Models/BMSLibrary.cs:5884–5941](../../BeMusicSeeker/Models/BMSLibrary.cs#L5884-L5941) | 改修：失敗時の空score化 |
| [BeMusicSeeker/Models/BMSLibrary.cs:4811–4837](../../BeMusicSeeker/Models/BMSLibrary.cs#L4811-L4837) | 改修：主一覧snapshot |
| [BeMusicSeeker/Models/ChartScoreSnapshot.cs:74–95](../../BeMusicSeeker/Models/ChartScoreSnapshot.cs#L74-L95) | 改修：NO_PLAYへの変換 |
| [BeMusicSeeker/ViewModels/MainWindowViewModel.cs](../../BeMusicSeeker/ViewModels/MainWindowViewModel.cs) | 改修候補：主一覧への読込み状態・通知の接続のみ |

**受入条件（案）:**

- [ ] **BMS-025-AC1** — 主一覧の読込み失敗が、正常なスコア0件／未プレイと利用者に区別できる。
- [ ] **BMS-025-AC2** — 同一sourceの再読込み成功後に正しいscoreと状態へ戻る。source切替で旧scoreを誤表示しない。
- [ ] **BMS-025-AC3** — 元DBは不変で、既存ランプビューアの失敗表示も維持される。

**検証候補:** [BmsLibraryInitializationLoadTests.cs](../../BeMusicSeeker.Tests/BmsLibraryInitializationLoadTests.cs)、[ScoreOnlyReloadWorkflowOwnerTests.cs](../../BeMusicSeeker.Tests/ScoreOnlyReloadWorkflowOwnerTests.cs)、[PlaylistLampHistoricalScoreSnapshotTests.cs](../../BeMusicSeeker.Tests/PlaylistLampHistoricalScoreSnapshotTests.cs)。

**仕様反映先:** [startup-initialization-flow.md](../spec/startup-initialization-flow.md)、[playlist-lamp-viewer.md](../spec/playlist-lamp-viewer.md)、[warning-model.md](../spec/warning-model.md)。

**工数の根拠:** failure stateの伝播とUI、stale snapshotの互換確認が必要。 見積り確度は中。

<a id="lr2-backup"></a>
## LR2 バックアップの探索・世代管理

BMS-027 は文書・設定 UI だけで進める。BMS-026 と BMS-028 は同じ Backup.cs とテストを続けて整理し、BMS-022 の後処理通知と合わせて結果を確認する。

**既存計画・仕様との境界:** [バックアップ公開の完了記録](lr2-backup-publication.md)と[対象選択の完了記録](lr2-backup-target-selection.md)の staging 公開・選択済み欠落の失敗・公開後の世代整理を維持する。将来日付の間隔判定は現行仕様との差分を明示して変更する。

<a id="bms-026"></a>
### BMS-026 — LR2バックアップでScore内のdirectory linkを辿らない

**事象・影響:** Backupのdirectoryコピーが再帰CopyDirectoryへ委譲され、子directoryのreparse pointを拒否しない。Score配下のlinkを辿り範囲外をコピーしたり、循環で処理・容量が膨らむ可能性がある。

**成立条件:** バックアップ対象のScore directoryまたは配下にdirectory link／junction等がある。通常利用外の安価な防御として扱う。

**到達経路:**

1. 起動時LR2バックアップ→SaveBackupsWithResultでScoreをdirectoryとしてコピー。
2. LongPathFileSystem.CopyDirectoryが各GetDirectoriesの結果へそのまま再帰。
3. 再解析点の判別がなく、想定したScore treeの外へ進み得る。

**修正方針:**

- バックアップ専用の探索入口でルートディレクトリ／子directoryのreparse pointを検査し、見つけたら公開前に選択バックアップを失敗として返す案を既定とする。
- 既存世代と元データを保全し、当該stageを回収。黙ってlink先を除外して完全バックアップ成功と通知しない。
- 共通CopyDirectoryに拒否を入れる場合は全call siteを調べ、別用途の仕様を変えない範囲に限定する。

**保全条件・対象外:** 実体pathを追って複雑な循環グラフを解決しない。リンク複製機能の追加も対象外。 外部FS変更監視やクラウド同期対応へ拡張しない。

**決定待ち:** 推奨は link を検出した選択バックアップの公開前失敗。バックアップ専用検査で閉じるか、共通コピー API の呼出条件を限定して変更するか。

初期案は Backup.cs 側の対象検査。共通 CopyDirectory の挙動を変更する案を選ぶ場合は、他用途との互換性を確認して writable path を追加する。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/Models/LR2/Backup.cs:180–200](../../BeMusicSeeker/Models/LR2/Backup.cs#L180-L200) | 改修：directory sourceコピー |
| [BeMusicSeeker/Models/Utils/LongPathFileSystem.cs:222–245](../../BeMusicSeeker/Models/Utils/LongPathFileSystem.cs#L222-L245) | 参照：CopyDirectoryの再帰 |

**受入条件（案）:**

- [ ] **BMS-026-AC1** — source ルートディレクトリ／子directoryにlinkがあるとlink先へコピーせず、既存世代を保持して理由を通知する。
- [ ] **BMS-026-AC2** — 通常の深いdirectoryとlong pathはバックアップできる。
- [ ] **BMS-026-AC3** — リンク先のファイルは一切変更せず、失敗した当該 staging だけを後始末の対象とする。

**検証候補:** [BackupTests.cs](../../BeMusicSeeker.Tests/BackupTests.cs)、[ResilientFileMutationServiceTests.cs](../../BeMusicSeeker.Tests/ResilientFileMutationServiceTests.cs)。

**仕様反映先:** [lr2-backup.md](../spec/lr2-backup.md)、[path-length-and-io.md](../spec/path-length-and-io.md)、[file-db-consistency.md](../spec/file-db-consistency.md)。

**工数の根拠:** バックアップ入口で拒否する限定案。共通helper全用途変更を選ぶ場合は追加調査が必要。 見積り確度は中。

**続けて整理する項目:** [BMS-022](#bms-022)：同じ Backup.cs の取得結果と後処理結果を混同しない。 [BMS-028](#bms-028)：公開・既存世代保全の検証を共有する。

<a id="bms-027"></a>
### BMS-027 — バックアップ保存先は専用フォルダであることと日付フォルダ削除を警告する

**事象・影響:** 保存先直下で日付形式に一致するフォルダをバックアップ世代とみなすため、共有保存先の無関係な日付フォルダも世代整理で削除され得る。専用保存先という運用前提を文書・UI で明示する。

**成立条件:** バックアップ保存先を他用途と共有し、アプリの世代名と同じ形式の日付folderがある。新世代保存が成功し保持世代整理の対象になる。

**到達経路:**

1. SaveBackupsWithResultが保存先直下のdirectory名を日付regexで認定・降順化。
2. 新世代公開後、保持数を超えるgenerationを直接削除。
3. アプリ作成かどうかの判定はなく、無関係な同形式folderも該当する。

**修正方針:**

- 保存先選択欄の説明／注意とmanualのバックアップ節へ「専用の空フォルダを使う」「直下の同形式日付folderは自動削除対象」を明記する。
- 既存利用者にも、バックアップ設定ページで保存先の注意が確認できるようにする。
- 世代形式・保持数・実行タイミングを実装と照合して説明し、実際に所有判定があるような文言にしない。

**保全条件・対象外:** 所有 marker、manifest、移行判定、既存日付フォルダの取り込み・隔離ロジックは追加しない。対象は文書・UI の説明に限定する。 警告追加で既存の削除riskが技術的に解消したとは書かない。

**決定待ち:** 保存先欄の注意文とマニュアルの記載位置。世代の所有判定・移行・削除処理は変更しない。

Backup.cs は現行の判定・削除を確認する参照先のみ。設定 UI の表示変更と runtime の世代整理変更を混ぜない。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/Models/LR2/Backup.cs:163–174](../../BeMusicSeeker/Models/LR2/Backup.cs#L163-L174) | 参照：日付名だけの世代認定 |
| [BeMusicSeeker/Models/LR2/Backup.cs:209–226](../../BeMusicSeeker/Models/LR2/Backup.cs#L209-L226) | 参照：公開後の世代削除 |
| [docs/manual.ja.md:1020–1028](../../docs/manual.ja.md#L1020-L1028) | 改修：バックアップ説明 |
| [BeMusicSeeker/Views/Settings/Pages/BackupSettingsPage.xaml](../../BeMusicSeeker/Views/Settings/Pages/BackupSettingsPage.xaml) | 改修：保存先の説明・注意表示のみ |

**受入条件（案）:**

- [ ] **BMS-027-AC1** — 保存先を選ぶ利用者が、共有folderと同形式の日付folderが危険であることを操作前に把握できる。
- [ ] **BMS-027-AC2** — マニュアルとUIに専用folder使用の具体的注意があり、設定済み利用者も確認できる。
- [ ] **BMS-027-AC3** — 世代判定・削除処理は変更しない。文書・UI の注意だけで、誤削除の可能性が技術的に解消したとは表示しない。

**検証候補:** [LocalizationResourceParityTests.cs](../../BeMusicSeeker.Tests/LocalizationResourceParityTests.cs)、[BackupTests.cs](../../BeMusicSeeker.Tests/BackupTests.cs)。

**仕様確認先（runtime 変更なし）:** [lr2-backup.md](../spec/lr2-backup.md)。

**工数の根拠:** 文書・設定UIの短い警告に限定。所有判定codeの変更を含めない。 見積り確度は高。

**続けて整理する項目:** [BMS-028](#bms-028)：注意表示だけでは未来日付の skip は直らない。世代判定の変更とは別に完了する。 [BMS-020](#bms-020)：文書・必要な UI 文言の編集を調整する。

<a id="bms-028"></a>
### BMS-028 — 未来日付の世代を自動バックアップ間隔判定から除外する

**事象・影響:** 世代日付を降順にし先頭との経過時間で取得可否を決めるため、未来日付folderがあると差分が負となり、長期間バックアップをスキップする。

**成立条件:** 時計の一時的な誤り等で未来日付の世代が保存先に存在し、時計が通常日に戻った後に自動取得する。無関係なfolderを勝手に削除して直すことはしない。

**到達経路:**

1. SaveBackupsWithResultが日付世代をdescendingで列挙。
2. today-generations[0] < minimumIntervalを判定し、先頭が未来でもそのまま比較。
3. 未来日に追い付くまで未到来扱いのSaved:falseが続く。

**修正方針:**

- 取得間隔の基準はtoday以下の有効な最新世代だけとする。未来世代の検出は警告または診断として扱う。
- 未来世代を取得間隔から除く修正だけで、直後の世代整理がそのフォルダを削除しないようにする。推奨案は未来世代を自動整理からも除外し、当日以前の世代に保持数を適用する。未来世代の残置で全フォルダ数が設定数を超える扱いは、現行仕様からの変更として確定する。
- 同日公開済みfolderの非上書き契約と最低1日間隔を維持する。

**保全条件・対象外:** 未来世代の削除・改名、時計同期、既存フォルダ全般の移行・修復は追加しない。通常日付の公開方式と保存失敗時の既存世代保全は維持する。

**決定待ち:** 未来世代を間隔判定・自動整理から除外し、今日以前の世代に保持数を適用する案。未来分の残置による設定数超過の許容と、その説明・警告を保持数 1 の場合も含めて確定する。

現行の取得間隔・保持数は [lr2-backup.md](../spec/lr2-backup.md) にある。未来世代を取得判定から外す提案と、保持数の対象から外す提案を区別し、後者は決定事項として追記してから実装する。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/Models/LR2/Backup.cs:163–179](../../BeMusicSeeker/Models/LR2/Backup.cs#L163-L179) | 改修：世代順序と日付差によるskip |
| [BeMusicSeeker/Models/LR2/Backup.cs:209–226](../../BeMusicSeeker/Models/LR2/Backup.cs#L209-L226) | 改修：公開後の世代整理。未来世代を保持数の対象から外す案を選ぶ場合 |

**受入条件（案）:**

- [ ] **BMS-028-AC1** — 未来世代しかない場合でも当日世代を正常公開でき、未来folderを消さない。
- [ ] **BMS-028-AC2** — 過去世代＋未来世代では過去の最新から間隔を計算する。同日世代ありでは既存内容を上書きしない。
- [ ] **BMS-028-AC3** — 未来フォルダの存在を診断でき、決定した保持数の適用範囲と説明が一致する。通常日付の世代整理と保存失敗時の旧世代保全を維持する。

**検証候補:** [BackupTests.cs](../../BeMusicSeeker.Tests/BackupTests.cs)、[LocalizationResourceParityTests.cs](../../BeMusicSeeker.Tests/LocalizationResourceParityTests.cs)。

**仕様反映先:** [lr2-backup.md](../spec/lr2-backup.md)。

**工数の根拠:** 判定対象の日付filterと時計seam／固定入力テストが中心。 見積り確度は中。

**続けて整理する項目:** [BMS-026](#bms-026)：同じ Backup.cs の公開・世代保持境界を維持する。 [BMS-027](#bms-027)：未来フォルダを削除することを対処として案内しない。

<a id="external-input"></a>
## 外部取得の終了条件・資源予算

BMS-029 のキャンセルから本文 I/O 終了までを先に接続する。BMS-030 は HTML／JSON 受信量とアーカイブ展開量を分割し、受信側は同じ終端経路を使う。

**既存計画・仕様との境界:** 単発 URL 取得、外部プレイリスト同期、IR 取得は入口が異なる。共通 AppHttpClient の変更で、既存の本文期限・shutdown 取消契約を緩めない。

<a id="bms-029"></a>
### BMS-029 — 単発URLダウンロードの本文待機をキャンセル・期限で終端できるようにする

**事象・影響:** URL1／URL2からの単発自動取得はcanCancel:falseで、ResponseHeadersRead後の本文受信にも個別期限がない。ヘッダー後に相手が止まると操作受付が塞がったままになる。UI threadのデッドロックではない。

**成立条件:** プレイリスト詳細の単発URL自動ダウンロードで、サーバーがヘッダーを返した後に本文を止める。ブラウザ起動や複数URL取得の別入口と混同しない。

**到達経路:**

1. MainWindow URL列→DownloadSinglePlaylistUrlCandidateWithStatusAsyncが取消不可statusを公開。
2. OperationProgressHubがInstallPipelineCanCancelへ伝えて実キャンセルボタンを無効にする。
3. AppHttpClient.OpenReadAsync→ResponseHeadersRead→本文ReadAsync。本文用のdeadlineがなく、単発UIからもcancelできない。

**修正方針:**

- 単発でも既存の取消可能request／CTS／status経路を接続する。本文のReadAsyncへtokenが届くことを確認する。
- 必要な本文受信の総期限または無通信期限を明示し、実際のstream読込みとresponse disposalまで終端する。
- cancel／timeoutを通信失敗と区別し、一時downloadとoperation leaseを既存ownerで解放する。導入開始後のキャンセル可否はそのphase契約に従う。

**保全条件・対象外:** Task.WhenAnyだけで呼出元を抜けて、HTTPやfile writeを裏で走らせない。自動無限retryを加えない。 最大サイズはBMS-030の別条件。時間制限を容量制限の代わりにしない。

**並行性の制約:** プレイリスト編集の拒否はBMS-004の受付へ接続するが、通信待ち中に現行許可されるライブラリ操作と設定画面の表示・編集は維持する。URL取得中の追加drop等の既存拒否や導入時のfile/DB leaseは解除しない。取消を受けた瞬間ではなく実I/Oと所有するcleanupの終端で受付を戻す。通信全体を新しいglobal mutation gateで囲まず、新しい取得待機queueも作らない。

**決定待ち:** 単発キャンセルを本文 I/O へ接続する。自動期限も設ける場合は総時間・無通信時間の別と値の根拠。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.PlaylistUrlAcquisition.cs:581–599](../../BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.PlaylistUrlAcquisition.cs#L581-L599) | 改修：単発取得のcanCancel:false |
| [BeMusicSeeker/ViewModels/MainWindow/OperationProgressHubViewModel.cs:691–705](../../BeMusicSeeker/ViewModels/MainWindow/OperationProgressHubViewModel.cs#L691-L705) | 参照：UI cancel可否 |
| [Ribbit/Net/AppHttpClient.cs:462–479](../../Ribbit/Net/AppHttpClient.cs#L462-L479) | 改修：OpenReadAsync |
| [BeMusicSeeker/Models/PlaylistUrlAcquisitionWorkflow.cs:1235–1266](../../BeMusicSeeker/Models/PlaylistUrlAcquisitionWorkflow.cs#L1235-L1266) | 改修：本文ReadAsync |

**受入条件（案）:**

- [ ] **BMS-029-AC1** — 本文を明示的に停止するHTTP fixtureで、ユーザー取消が実ReadAsyncへ届き、response／一時file／操作受付が解放される。
- [ ] **BMS-029-AC2** — 期限方式を採用した場合、その超過を明示的に通知し、以後のURL取得とドロップが可能になる。
- [ ] **BMS-029-AC3** — 通常の低速だが進行中の取得、ヘッダー前取消、完了直前取消で誤った成功／二重終端にならない。
- [ ] **BMS-029-AC4** — 本文受信を停止した間も、対象unitで現行許可を確認したライブラリ操作・設定画面が利用できる。URL取得との既存の拒否関係は保ち、取得後の導入はその時点の正本・既存受付に従う。表示時の古い所持状態から導入先や削除対象を黙って決めない。

**検証候補:** [PlaylistUrlAcquisitionOwnershipTests.cs](../../BeMusicSeeker.Tests/PlaylistUrlAcquisitionOwnershipTests.cs)、[AppHttpClientTests.cs](../../BeMusicSeeker.Tests/AppHttpClientTests.cs)、[PlaylistWorkspaceActionWorkflowTests.cs](../../BeMusicSeeker.Tests/PlaylistWorkspaceActionWorkflowTests.cs)。

**仕様反映先:** [playlist-url-download-resolution.md](../spec/playlist-url-download-resolution.md)、[managed-temp-files.md](../spec/managed-temp-files.md)、[application-shutdown.md](../spec/application-shutdown.md)。

**工数の根拠:** UI statusから本文I/Oまでのtoken連結と決定的network fixtureが中心。 見積り確度は中。

**続けて整理する項目:** [BMS-004](#bms-004)：編集受付と通信中のライブラリ操作維持を揃える。[BMS-030](#bms-030)：本文受信の取消・後始末を先に揃え、実byte数上限を続ける。

<a id="bms-030"></a>
### BMS-030 — アーカイブ展開と外部HTML／JSON取得に小さな資源予算を設ける

**事象・影響:** アーカイブの展開後サイズと、外部プレイリスト HTML／JSON の読込み量にアプリ固有の上限がない。極端に大きい入力でディスク、メモリ、処理時間を消費し得る。通常入力との互換性を保ち、既存 API で実施できる有限な入力量の制限に絞って改善する。

**成立条件:** サイズが極端に大きいarchiveやplaylist応答を取り込む。悪意入力対策の全面設計や通常のnetwork停止は本件の中心ではない。

**到達経路:**

1. 導入入口→ExpandInstallSources→SevenZipArchiveExtractor。path越境とreparseの拒否はあるが、展開量のbudget判定なしでExtractへ進む。
2. 外部playlist同期→PlaylistExternalSyncOwner→AppHttpClient.GetStringAsync。
3. responseを全byte列／文字列へ溜めてHTML／JSONを解析し、入力量に比例して資源を消費する。

**修正方針:**

- subtask A：archiveの合計非圧縮byte数・entry数等を最小限のbudgetとして抽出前に検査。利用中APIが出力監視／中断を提供するなら実書込みも計測し、提供しなければ事前検査の保証範囲を明記する。
- subtask B：HTML／JSONの実受信byte数をstream読込みで計測して上限超過で停止。Content-Lengthだけを信じず、文字列化／parse前に抑止する。
- 予算超過はtypedな入力拒否として通知し、この試行のtempだけ回収する。native APIの能力が足りない場合、保証できないhard capを宣言しない。

**保全条件・対象外:** 正常資料の大きさを無視した小さい定数、圧縮率だけでの拒否、巨大汎用sandbox／子process broker／恒久queueは追加しない。 既存path traversal／reparse拒否を保つ。UIに多数の設定項目を増やすことは既定案にしない。

**決定待ち:** 展開量・entry 数・HTML／JSON 受信量の上限、サイズ不明入力の扱い、展開 API が提供する監視／中断能力。

**変更範囲・参照:**

| ソース（基準コミットの行番号） | 扱い・対象 |
|---|---|
| [BeMusicSeeker/Models/BmsLibraryInternal/SevenZipArchiveExtractor.cs:25–53](../../BeMusicSeeker/Models/BmsLibraryInternal/SevenZipArchiveExtractor.cs#L25-L53) | 改修：ExtractArchiveEntries |
| [BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryPackageInstallService.cs:788–805](../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryPackageInstallService.cs#L788-L805) | 改修：一時展開入口 |
| [BeMusicSeeker/Models/BmsLibraryInternal/PlaylistExternalSyncOwner.cs:164–190](../../BeMusicSeeker/Models/BmsLibraryInternal/PlaylistExternalSyncOwner.cs#L164-L190) | 改修：外部header取得 |
| [BeMusicSeeker/Models/BmsLibraryInternal/PlaylistExternalSyncOwner.cs:227–238](../../BeMusicSeeker/Models/BmsLibraryInternal/PlaylistExternalSyncOwner.cs#L227-L238) | 改修：data JSON取得 |
| [Ribbit/Net/AppHttpClient.cs:612–634](../../Ribbit/Net/AppHttpClient.cs#L612-L634) | 改修：応答byte蓄積 |

**受入条件（案）:**

- [ ] **BMS-030-AC1** — 決定した予算の直下・直上で受理と拒否を区別し、超過時には成功通知・通常導入・部分的なプレイリスト保存をしない。
- [ ] **BMS-030-AC2** — Content-Lengthなし／不一致の受信でも実byte上限を適用し、streamとtempを解放する。
- [ ] **BMS-030-AC3** — 展開量が上限を超えると判明したアーカイブは展開前に拒否し、情報不明／監視不能の入力の扱いと保証範囲が明示される。
- [ ] **BMS-030-AC4** — 通常の代表的packageとplaylistを上限内で受け入れ、既存のpath防御を維持する。

**検証候補:** [BmsLibraryPackageInstallServiceTests.cs](../../BeMusicSeeker.Tests/BmsLibraryPackageInstallServiceTests.cs)、[AppHttpClientTests.cs](../../BeMusicSeeker.Tests/AppHttpClientTests.cs)、[BmsPlaylistExternalLoadTests.cs](../../BeMusicSeeker.Tests/BmsPlaylistExternalLoadTests.cs)、[PlaylistUrlAcquisitionOwnershipTests.cs](../../BeMusicSeeker.Tests/PlaylistUrlAcquisitionOwnershipTests.cs)。

**仕様反映先:** [drop-install-ingress.md](../spec/drop-install-ingress.md)、[playlist-url-download-resolution.md](../spec/playlist-url-download-resolution.md)、[managed-temp-files.md](../spec/managed-temp-files.md)。

**工数の根拠:** archiveとHTTPの2subtask。native extraction APIの監視／中断能力と上限仕様によって増えるため計画時に再評価。 見積り確度は低。

**続けて整理する項目:** [BMS-029](#bms-029)：共通 HTTP 読込みの終端を維持して byte 数の制限を追加する。

## 実施記録

2026-09-08: 設計・運用文書に採用済み受付方針を反映し、本計画を追加。BMS-016は要件決定済みとして実装待ちへ移し、BMS-001／004／011／017／029等の保全・受入条件を調整した。起動再構成は既存計画へ接続する。

2026-09-08の計画追加時点では、項目ごとのruntime修正・受入条件の実行確認・ビルド・テスト・Fullの実行記録はなかった。その後の完了項目は、各項目とリンク先の作業記録を参照する。

2026-09-09: BMS-031〜BMS-033を未着手項目として追加。詳細セル編集の保存前受付、外部ビューアINIの保存保全、JSONエクスポートの同一保存先拒否を既存領域へ統合した。BMS-031は保守対応の優先項目、BMS-032は保守対応、BMS-033は限定改善とする。この追加は計画書のみの変更であり、3件の実装・受入検証は未実施。

文書の適用だけを不具合修正や新しい受付の実装完了と数えない。完了時の恒久契約は各項の仕様反映先へ統合し、実装コミット・検証証跡・残課題を各項目から追跡できるようにする。
