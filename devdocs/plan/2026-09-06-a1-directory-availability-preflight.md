# A1: 登録ディレクトリ利用不能時の更新停止

状態: **完了（2026-09-06）**。この文書は実施時の判断、凍結した test oracle、検証・review 証跡の履歴である。現行契約の正本は [file-db-consistency.md](../spec/file-db-consistency.md)、[startup-initialization-flow.md](../spec/startup-initialization-flow.md)、[playlist-data-and-export-flow.md](../spec/playlist-data-and-export-flow.md) とする。

## 作業の基準と判断

- 開始 HEAD: `b5fb95276c9c1557802364ed8d55abeaaa5992c6`。開始時の作業ツリーは clean。
- 利用者の依頼は A1 の実装計画を現行コードと照合し、必要な調整後に実装すること。添付計画と旧会話は検討資料として扱う。A2 / A3、version、公開操作は対象外。commit は必要なら許可されている。
- Goal: 一部の登録 BMS ルートが利用不能でも、それを登録解除や譜面削除と解釈しない。初期化・リロードを明示的に停止し、登録と既存データを保つ。
- 変更分類は不具合修正と失敗契約の明確化。恒久テストは既存更新と追加。既存の不完全走査・空走査保護では、走査入力から先に欠落ルートが脱落する不具合を識別できないため、production 入口からのデータ保全と後続更新の抑止を保証する。

## 現行経路と到達条件

`MainWindowViewModel.InitializeAsync` は出力先の検索ルート修復を schema / profile / library 構築より先に行う。修復は config 保存と出力先の作成につながる。早期検査はこの修復より前に置く。

手動メニュー・設定保存から `ReloadFileDiffAsync` → `BMSLibrary.ReloadFileDiff`、再初期化から `InitializeCore(FullReinitialize)` に入る。`StandaloneBmsRootPathSettings`、`LR2Config`、`Lr2SearchRootSnapshotOwner.Capture` の存在フィルタによって一部の登録が脱落した後、`LibraryFileScanPipelineOwner` → `FileScanParseCommitOwner` が既存 catalog 全体との削除差分を確定できる。ルート A に譜面が残り、登録を変えず B のドライブを切断する条件で、B の DB 行と利用者保存情報に影響する。

外部 filesystem は走査中にも切断され得る。DB の既存 single-writer / mutation lease は維持するが、外部ディレクトリが操作中不変とは仮定しない。

## 確定した意味上の決定

1. 起動時スキャン ON / OFF、手動差分リロード、FullReinitialize、設定保存後の同じリロード入口を保護する。ScoreOnly と独立した他操作は対象外。
2. 全登録 BMS ルートを存在による除外前に捕捉する。欠落登録は表示・保存で維持する。完全未設定は従来の初期設定導線へ戻す。
3. LR2 連携時は通常・追加・ルート型の全出力ベースも検査する。standalone の使わない LR2 設定は検査しない。管理ベース配下の個別生成子フォルダは必須登録にしない。
4. BMS ルートは属性取得・直下列挙の開始のみ。空ディレクトリは成功し、書込みは要求しない。出力ベースのみ自己所有の新規一時ファイルによる作成・書込み・削除を確認する。既存ファイルを変更せず、欠落ディレクトリを検査で作成しない。
5. LR2 相対パス、drive root、UNC、extended path の意味を保つ。不正な非空パスや読めない設定は成功・空へ隠さない。構成エラーには既存の設定案内を利用できる。
6. 検査した immutable request をその操作の走査へ渡す。通常出力ベースの chart/resource 検索互換を維持し、追加・ルート型は既存どおり chart 対象から除く。ユーザー登録の親子は必須検査対象として残す。
7. prefetch 完了後、差分反映前に同じ要求集合の読取り確認を行い、観測できた切断で差分適用を止める。出力への書込み probe は繰り返さない。最終確認直後の物理切断まで原子的に防ぐ保証はしない。
8. model は型付き失敗を throw する。警告の唯一の所有者は shell。early / late とも全 gate・suppression の解放後に警告する。startup helper は型付き停止を外へ渡し、外側で false にする。manual は再 throw し、settings pending を維持、UI event terminal が通知済み停止を受ける。
9. 警告は用途・パス・理由・再接続と設定修正の案内を含む。early の副作用前停止と late の差分停止の文言を区別する。全言語を同時に更新する。
10. global write-disable、永続的な検査キャッシュ、独自 retry / timeout、全件 rollback、全サブフォルダ事前走査は追加しない。別操作の完了済み更新を巻き戻さない。

## 計画からの調整と ownership

計画点検は read-only の `plan-clarifier` で実施済み。指摘された警告 owner の曖昧さは上記 8 で解決した。利用者の追加判断を要する問題は現時点でない。

書込み worker は一つとし、次の順で作業する。共通 request の構築責任は U1 の service / configured capture に一本化し、U3 は呼出し接続だけを所有する。原案の U1 / U3、U2 / U4 の責務重複をなくす。

- U1: configured 入力と共通検査。`StandaloneBmsRootPathSettings.cs`、`LR2Config.cs`、`BmsLibraryInternal/LibraryDirectoryPreflightService.cs`（新規）、`Lr2SynchronizationPorts.cs`、`BmsLibraryOptionsSnapshot.cs`、必要な `CustomFolderOutputBaseRegistry.cs`、`Utils/LongPathFileSystem.cs`。raw additional JSON が非 strict options 作成で失われるため、操作 snapshot に元の設定を保持し更新入口で strict に読む最小対応を許可する。非更新 consumer の失敗仕様を広く変えない。
- U3: model / scan の保護。`BMSLibrary.cs`、`LibraryFileScanPipelineOwner.cs`。既存の root owner を直接利用できるため、無関係な LR2 port や owner に forwarding API を増やさない。mutation reservation を throwing 検査前から確実に所有する。
- U2 / U4: startup / shell / settings の保護と復旧。`StartupSettingsSnapshot.cs`、`MainWindowViewModel.cs`、`SettingsDialogViewModel.cs`、必要な既存 `MainWindow/StartupLibraryInitializationWorkflowOwner.cs`、`LibraryDirectoryWarningFormatter.cs`（新規）、`Views/MainWindow.cs`、全言語 resources。利用不能登録の保持と新規追加候補の存在 validation を分ける。
- テストの writable path は独立 packet の coverage ledger で指定する。共有 fixture の無関係な assertions を変更しない。実装 worker は stage / commit / Functional / Full を実行しない。
- root は本計画と `devdocs/spec/startup-initialization-flow.md`、`file-db-consistency.md`、`playlist-data-and-export-flow.md`、`docs/manual.ja.md` を所有する。恒久契約は spec、操作案内は manual に置く。

S の到達性点検で、`StandaloneLibraryDatabase.EnsurePortableSongDb` が静的な `PortableSettingsPath` に依存し、composition が既に持つ `ApplicationPathSnapshot` を受け取っていないことを確認した。D08 の実 outer ingress を所有 temp の新規・既存 DB で確認するため、この一箇所を既存 composition の path snapshot を必須引数として受ける境界へ変更してよい。production の入力値は同一で、global path policy の差替えや新しい path provider / fallback は追加しない。この mechanics 調整に oracle の変更はない。`Models/BmsLibraryInternal/StandaloneLibraryDatabase.cs` を S の writable path に含める。

## 完了条件と検証

独立した `test-contract-designer` が oracle-first で packet を設計し、root が承認後に実装を委譲する。判定値は利用者要件と上記決定から導き、現行 expected / source text / 翻訳文言をコピーしない。

必要な evidence は production ReloadFileDiff の A/B 退避と DB sentinel 保全（base red / head pass、構造上不可なら対象 mutant）と、early startup の副作用前・scan OFF 検査を識別する mutant 一つ。全契約へ機械的に mutant を要求しない。テストは所有する temp DB / config / directories と既存 WPF dispatcher を使用し、Task / gate completion で同期する。

反復は関連 filter の `scripts/verify-refactor.ps1 -Mode Quick`。最終は `-Mode Functional` を一回。失敗・timeout の分類と再実行は testing-strategy に従う。release 操作ではないため Full は今回必須にしない。`git diff --check`、文書の UTF-8 / LF・参照を確認する。実装 agent 完了後に snapshot を凍結して fresh static review を行い、review 中は root も read / edit / build / test を停止する。

停止して再計画する条件: 新たな最初の DB 副作用、管理子フォルダ判定の互換性矛盾、データ削除を伴う設定移行、global 停止状態の必要性、gate 解放後に失敗を正しく伝えられない証拠、承認した oracle と executable production seam の矛盾。単なる局所的な API 名・fixture の違いは worker が意味を変えず適合する。

## Test Contract Packet / 実行証跡

### 承認済み packet: A1-DIR-PREFLIGHT revision 1

`test-contract-designer` が repository / implementation / 既存 expected / 翻訳を読む前に、上記の利用者要件と root decision だけで oracle を凍結した。その後の限定的な repository-fit 点検を含め、root が 2026-09-06 に承認した。分類は bugfix + stable failure contract。D01–D14 は behavior、D15 は behavior + resource schema / compiled accessor consistency。製品 semantics の未決事項なし。

共通 allowed variation は、意味を保つパス表記、表示・列挙順、内部型名、指定した副作用・cleanup 境界以外の call order、probe 名・書込 byte、翻訳全文・段落・key 名、処理時間である。最終確認後の原子性、late の全 DB 不変、全 rollback は期待しない。private reflection / source-text / localized exact-copy / broad snapshot / characterization の新規例外はない。test が事前に所有した config bytes、DB sentinel、既存 file の不変比較は durable behavior の検証である。

| ID | 独立 authority / production ingress と必須 outcome | 検出する誤実装 / evidence |
| --- | --- | --- |
| D01 | decision 2・6。設定読込み・表示・保存 → standalone adapter / startup snapshot / settings session。A/B の B が missing でも登録保持、親子別登録も検査前に脱落させない | Deserialize / Serialize / 親子集約で消す。isolated round trip、表示 collection、保存 readback |
| D02 | decision 3・5・6。startup / reload → raw LR2 config → request → scanner。相対は LR2 root 基準、drive / UNC / extended の意味維持、missing user root を検査。normal chart 互換、additional/root-type chart 除外 | cwd 基準、drive root 切詰め、存在 filter。実 config、scanner input 観測。UNC 実共有は要求しない |
| D03 | decision 4。更新入口 → probe。属性と直下列挙開始可能なら空も成功、BMS root に create/write/delete なし | empty failure、BMS 書込み必須化。実 temp 空 root と狭い port の write-call ledger |
| D04 | decision 5。更新入口 → raw request → probe。missing / file-as-directory / 属性拒否 / 列挙開始拒否を path/use/cause のある typed failure にする。不正 path / config / additional JSON は空成功にしない | Exists だけ、MoveNext 未実行、catch-to-empty。実 missing/file、狭い I/O fault、raw malformed config を操作入口へ。一般構成エラーは既存案内で可 |
| D05 | decision 3・4。linked startup / reload / reinitialize → output request。全 base のどれか一つ不可用なら停止し欠落 base を自動生成しない | additional 省略、repair 先行。用途別 missing matrix、directory/config readback |
| D06 | decision 3。standalone 更新入口。正常 BMS roots の場合、残存 LR2 output 不在だけでは拒否しない | mode 無視。missing LR2 設定の対照成功 |
| D07 | decision 3。linked 更新入口 → 登録用途分類。正常 base 配下の未生成 playlist child だけでは停止しない | managed child を必須 user root とする。実 config/request の対照成功 |
| D08 | decision 1・8。外側 MainWindowViewModel.InitializeAsync → early guard。scan ON/OFF とも false、repair/schema/profile standalone DB 作成/attach/backup/最適化/scheduler 前に停止。既存 DB/config 不変、新規 DB 未作成 | scan OFF skip、repair 後 guard。実 outer Task + durable readback / 境界 call ledger。新規 DB case に作成済み DB helper を使わない |
| D09 | decision 1・7・8。manual/settings → shell workflow → production ReloadFileDiff / Reinitialize → InitializeCore。正常 A/B catalog の B 退避で typed failure、BMS/bmson/favorite/tag/adddate/folder sentinel と最後の正常 catalog 保持、成功後 queue なし | A だけ走査し B 削除、void success。実 facade / temp A/B / DB、base red/head pass |
| D10 | decision 6・7。production reload/initialization → BeginFileScanRequest → prefetch → ApplyActiveFileScan → diff。同じ immutable 要求を保持、prefetch 後 diff 前の read-only 再検査。B 切断+A-only complete 結果でも diff の削除・更新なし。write probe 繰返しなし | Success だけ信用、Exists-filter 再取得。captured scanner signal 中の実 B 退避と production facade DB readback。参照 identity は要求しない |
| D11 | decision 8。manual/settings shell および外側 InitializeAsync → model failure → cleanup → warning → UI terminal。全 gate/lease/suppression 解放後一回 warning、manual rethrow/settings pending、UI terminal のみ通知済み停止を処理。startup early/late false/retryable/成功連鎖なし | helper 内 warning、gate 中 dialog、return、二重 warning。warning callback 内 admission、Task failure/pending/progress/queue を観測。inner helper 直呼びだけで outer gate 証明にしない |
| D12 | decision 2・8。明示登録解除・保存または再接続 → 同じ操作。失敗後に修正を反映し正常完了。暗黙解除、自動 retry、失敗 cache なし | missing 非表示、gate/cache が次回拒否。D09/D11 から二段階 retry、保存と operation completion |
| D13 | decision 2。first startup → 外側 InitializeAsync → 初期設定表示。完全未設定は従来の言語・設定導線、更新副作用なし | 空 path 障害を繰返す。実 outer Task、初回 presentation、false/未 attach |
| D14 | decision 4。output preflight → create/write/delete。自己所有新規 file のみ、既存不変、成功時残留なし、create 不成立 path を delete しない。write/cleanup failure は明示、dir 作成なし | 既存再利用、create失敗後delete、cleanup握潰し。実temp既存sentinelと狭いport故障matrix。実ユーザーACL不要 |
| D15 | decision 9。typed failure → formatter/presenter → resx/accessor/全6locale。用途/path/理由/再接続/正しい設定先、key/nonempty/placeholder/format一致、late DB全不変としない | 全用途BMS誘導、path欠落。presentation facts/formatterの用途・context、resource parity。翻訳意味は静的確認し全文複製しない |

### Coverage ledger と unit 境界

全て通常 Functional 対象、既存 runner の lane / shard を維持し新しい DNP は増やさない。M は共通入力/probeと model のデータ保全（U1+U3）、S は startup/shell/settings/localization（U2+U4）。同一 writer に逐次渡す。

| ID / unit | 配置 | 共有 resource / completion / 退役 |
| --- | --- | --- |
| D01 adapter M、表示保存 D01/D12 S | SettingsDialogBehaviorTests extend、保存retryは SettingDialogEditCompletionTests extend | isolated settings/temp config、Task/save readback。getter名固定source断言が実際に影響する時だけbehaviorへ置換 |
| D02–D07/D14 M | LibraryDirectoryPreflightTests new（DB/UIとfault matrixのownerを分離） | instance fake / 実temp、同期return/throw/dispose、remaining。update存在filterを退役 |
| D02 scan互換 M | BmsLibraryLr2SongDbSyncTests extend または新facade fixture | 明示options/captured scanner/固有DB、model/scanner完了、remaining-bms-library。別read Capture coverage維持 |
| D09/D10 M、再接続D12 M | BmsLibraryDirectoryAvailabilityTests new（実facade登録消失とDB保全を集約） | Lr2SongDbSyncTestSupport.TestDatabaseScope / TestBmsLibrary再利用、scanner entered/release signal、DB readback、remaining-bms-library。incomplete scan既存tests維持 |
| D10 owner M | LibraryFileScanPipelineOwnerTests 必要差分extend | 既存localization/laneと固有temp、Begin/Apply+prefetch completion。直接ApplyFileScanDiffだけでは代替しない |
| D08/D13 S、outer D11/D12 S | SettingDialogEditCompletionTests extend | 既存composition/presentation、固有app path/config、serial-state-a、outer InitializeAsync Task/副作用signal/readback |
| D09/D11 queue S | FileDiffReloadWorkflowOwnerTests extend | instance ports、ReloadAsync Task/queue count、remaining。generic failureが重複ならtypedへ更新可 |
| D11 progress S | MainWindowViewModelStartupProgressTests 必要差分extend | 既存isolated shell/dispatcher、Task/progress。inner helper testをouter gate証明にしない |
| D11 lease S | StartupLibraryInitializationWorkflowOwnerTests 既存再利用、必要時extend | instance semaphore、release→acquire Task、idempotent release維持 |
| D11 UI terminal S | MainWindowTreePresentationWpfTests 等の既存該当command/event fixtureへ必要最小限extend | 既存dispatcher/presentation、実event完了/通知数/未処理例外なし。test-only catchでterminalを代替しない |
| D15 S | LocalizationResourceParityTests extend、必要なら LibraryDirectoryWarningFormatterTests new | read-only resources、同期format/parity、presentation facts。新規fixtureはformatter責務の分離が理由 |

fixture/data builder/assertion/狭いport/signal の mechanics は適合可。既存 `TestBmsLibrary` の明示 options provider / captured scanner constructor を使い、local Everything / 実ユーザー settings / ACL / fixed sleep / private reflection に依存しない。`StartupLibraryProfileTests` / `StartupLibraryFailureContractTests` の直接試験を early outer ingress の代替にしない。新 fixture の Verification map は root が feature spec に統合する。

### 必要な negative evidence

- D09: 正常A/Bとsentinelを作り、Bだけ実退避して既存production ReloadFileDiff。同じ保全assertionがbase正常returnまたはsentinel変更で失敗することを記録。compile/setup/native bridge failureはredではない。構造不可なら理由を記録し、required rootsをExists-filterしてguardを迂回するmutantで識別力を確認。
- D08: scan OFF + 欠落output baseでearly guardをrepair後へ移すmutantを一つ実行。config/base作成の副作用をtestが検出すること。通常coverageはON/OFF両方。
- D10のA-only complete scannerは実切断を表す通常failure fixture。追加mutant義務なし。他contractもmutantはnot applicable。mutantは必ず戻してからQuick/統合検証。
- D15 resource parity / compiled accessor / placeholderはroot多言語契約に基づくartifact整合性。ownerはresource/formatter、当該通知API廃止時に退役する。

### 実行証跡

#### M 完了

- D09 base red: `artifacts/verification/tests-quick-20260906-133833`。実 `ReloadFileDiff` が requested roots 2 / existing roots 1 の状態で B の song 行を削除し、事前に設定した sentinel 保全 assertion が失敗した。compile / setup failure と区別した。
- M final Quick: `artifacts/verification/tests-quick-20260906-142357/functional/results.trx`、147 / 147 pass。filter は `LibraryDirectoryPreflightTests|BmsLibraryDirectoryAvailabilityTests|LibraryFileScanPipelineOwnerTests|BmsLibraryLr2SongDbSyncTests|BmsLibraryOptionsSnapshotTests`。
- D09 の Reload / Full と song / bmson / folder / favorite / tag / adddate / canonical catalog 保全、D10 の prefetch 中実退避と output probe 非再実行、D12 の復旧後 retry、D01–D07 / D14 の共通検査を実装した。M には settings 表示保存と shell の D01 / D09 / D12 を含めず、S へ引き継ぐ。
- root の handoff 照合で、同操作 options の再取得、output probe の配置、全 output の read-only 再検査、追加 JSON の drive root 保全を修正し、最終 Quick に含めた。旧 read 用 Capture の既存テストは維持した。
- API は `LibraryDirectoryPreflightService.CreateRequest(registeredRoots, scanRoots, options)` と `EnsureAvailable(request, probeOutputBases)`。後者の false は write probe を省略するだけで全対象の属性・直下列挙は行う。例外は `LibraryDirectoryPreflightException` に用途・パス・原因・出力種別・probe path・cleanup exception を保持する。型名の差は packet の allowed variation。

#### S 実装の補足

S の writable production は上記 U2/U4 に加え `ViewModels/MainWindow/StartupProgressWorkflowOwner.cs` と `Models/BmsLibraryInternal/StandaloneLibraryDatabase.cs`。既存進捗 owner の FullReinitialize は failure cleanup の retryable 対象から外れているため、今回の型付き停止の cleanup に必要な範囲で対応する。新しい global failure state は増やさない。

startup は `StartupSettingsSnapshot` と `CustomFolderOutputSettingsSnapshot` の同じ操作用値から request を組み立て、config の読込みと検査を background で所有・await する。M の Deserialize / Serialize は configured 値を保持する意味になり、Normalize は新規候補の存在確認用に維持された。S では session getter に残る存在フィルタ・互換性による暗黙除外を落とし、validation と候補追加の既存拒否を維持する。

新しい警告は既存の注入可能な `IUiDialogService` を使う shell の async route を優先する。進捗の failure sublabel へ新しい例外の内部診断文を直接表示せず、localized lead を使う。通常の既存エラー presentation まで一律に変更しない。UI terminal は reload / reinitialize / root 追加 / root 解除の既存 event を閉じ、settings では pending を保持する。

D08 の mutant は test 所有 temp と既存 dialog / factory 境界に閉じる。必要なら出力 repair で直らない missing BMS root も組み合わせ、wrong variant が予期せず実ユーザー DB / dialog / native bridge へ進むことを防ぐ。検出 oracle は repair による config / output base の変更であり、単なる別エラーではない。

#### S handoff と統合時の evidence 補強

- S combined Quick: `artifacts/verification/tests-quick-20260906-160428/functional/results.trx`、175 / 175 pass。startup / settings / manual update / WPF terminal / formatter / localization の関連検証。
- D08: 実 `InitializeAsync` の scan ON / OFF、test 所有の新規 / 既存 DB を確認した。`tests-quick-20260906-152625` の repair 後検査 mutant は出力ベース作成・config 変更を検出して red、順序復元後の `tests-quick-20260906-152719` は pass。
- S の反復中に発生した test compile / UI dispatcher / standalone output settings 隔離の問題は fixture mechanics として修正した。製品の failure contract を変更していない。
- root 統合点検で D11 の証拠不足を確認した。warning callback 内では次回 `InitializeAsync` の Task を開始するだけで、待機が最初の初期化の完了後にあると、warning 後に gate を解放する wrong implementation も通り得る。また、callback 内の assertion exception は production の notification failure logging に捕捉され得る。callback は facts を記録し、test 本体が warning 完了前の再入場 / 完了を判定する形へ補強する。
- 上記の具体的な識別力不足に基づき、root は D11 の evidence addendum を承認した。outer startup gate の解放を warning 後へ移す targeted variant を一つだけ用い、補強 test の red と復元後 pass を確認する。D11 の observable oracle、allowed variation、production semantics は revision 1 から不変。他の failure へ一律の mutant 義務は増やさない。negative lock の watchdog と、gate 解放後の pending retry の drain / cleanup は test が所有する。
- D11 UI terminal は reload / reinitialize / unregister の実イベントを確認する。add-root picker は現行 production 内で直接注入できないため、picker 以降の catch route を静的に点検し、同じ settings → shell reload の失敗伝搬を関連 behavior test で補う。picker 用の新しい production abstraction は追加しない。
- D11 final Quick: `artifacts/verification/tests-quick-20260906-163144/functional/results.trx`、89 / 89 pass（`SettingDialogEditCompletionTests|FileDiffReloadWorkflowOwnerTests`）。warning callback 内で bounded watchdog を使い、warning 中の retry 完了・成功を facts として保存する。warning 後に pending retry を drain してから test 本体で判定する。
- D11 negative control: `tests-quick-20260906-162938` は warning 中の retry 完了 assertion を一次失敗として検出し、shutdown / temp cleanup まで到達した。先行 `tests-quick-20260906-162016` は shutdown timeout が一次失敗を置換したため有効な red evidence に採用していない。production の gate は warning 前の解放へ復元済み。

#### 統合検証と既存テストの整理

- `tests-functional-20260906-163610` は test 実行前の analyzer で RCS1194 により停止した。型付き例外の用途・パス・原因を必須とするため、情報のない constructor を追加せず、同種例外の既存方針に従う理由付き `SuppressMessage` を付けた。
- `tests-functional-20260906-163806` は format / analyzer / build を通過。6 host 合計 4,582 tests 中 4,568 pass / 13 skip / 1 fail。failure は `StartupMainWindowTypedRouteTests.MainWindowInitializeAsync_UsesTypedStartupConstructionOwnerRoute` の直接 IL 呼出位置 assertion のみ。timeout ではなく deterministic assertion failure。runner の test execution elapsed は failure 時に unavailable。
- 上記既存テストは `InitializeAsync` state machine から構築 owner への直接 call を固定し、今回の outer / core 分割を拒否した。root は **削除のみ・代替追加不要** と判断した。private method の配置自体は今回の安定契約ではなく、構築・失敗伝搬は既存 `StartupLibraryProfileTests` / `StartupLibraryFailureContractTests`、外側 startup は D08/D11 の実行検証により既に保証される。expected を新しい private method へ追従させない。test-authoring contract section 1/2 に従い、この削除には追加 packet を要求しない。feature Verification map も同時に更新する。
- 削除後の `tests-quick-20260906-164659` は関連 82 / 82 pass。ただし root が同時に spec / plan を更新したため runner の fingerprint check が失敗し、command 全体の成功証跡には採用しない。以降は文書を含む書込みを停止し、最終 Functional で当該 coverage も再確認する。
- 最終 Functional: `pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional`、`artifacts/verification/tests-functional-20260906-164855`、exit 0。restore / tool / format / analyzer / build と全6 host が成功し、4,568 pass / 13 skip / 0 fail（合計 4,581）。test execution の retained-ExitTime 実測は **191秒**（180秒 reporting target 超過、300秒 hard budget 内）。tracked fingerprint は `821E3C08001475FA0B814090A25F85FDF21D5EF1E22BE5BC09F380F70F88D94E` のまま不変。Full / opt-in は対象外で実行していない。

#### 静的 review と修正

- 第1回 fresh static review は全差分・consumer・packet を対象とし、production correctness の blocking finding はなし。D04 に P2 test gap を1件指摘した。`LibraryDirectoryPreflightTests` の列挙 failure fake が enumerable 取得時に即 throw するため、production が `MoveNext()` を省略しても失敗 test を通る。
- root は指摘を採用し、既存 fake の失敗を最初の列挙開始へ遅延させる test-only 修正を承認した。D04 の既存 oracle（型付き failure・path・用途・書込みなし）を維持し、新規 production seam や assertion semantics は追加しない。修正後は当該 fixture の filtered Quick と fresh static review を行う。通常機能・release lane の前提を変えないため、同じ production snapshot の全体 Functional は再実行しない。
- R1 修正後 Quick: `artifacts/verification/tests-quick-20260906-170052/functional/results.trx`、`FullyQualifiedName~LibraryDirectoryPreflightTests`、13 / 13 pass、command exit 0、tracked fingerprint 不変。変更は同 fixture の iterator mechanics のみで、最初の `MoveNext` を省略すると既存 failure assertion が失敗する。`git diff --check` も成功。
- 第2回 fresh static review は R1 修正差分、D04 / 近傍 D03・D14、production の列挙・dispose 境界、plan と検証方針を確認し、**blocking finding なし**。R1 P2 の解消を確認した。レビュー担当は read-only とし、root の repository 操作も review 中は停止した。
- 実装・検証・review を完了。最終確認直後の外部切断に対する FS / DB の原子性は保証対象外のままである。
- 追加依頼に従い、`docs/manual.md` に日本語版と同じ利用不能時の案内を追記し、A1 修正と同じ commit に含める。追加作業は文書のみで、ユーザー指定により再 review は行わず、UTF-8 / LF・参照・whitespace を確認する。push / version 更新・公開操作は対象外。
