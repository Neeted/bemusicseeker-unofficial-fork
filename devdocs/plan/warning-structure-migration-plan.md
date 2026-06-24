# WARNING 構造化移行計画

## 目的

譜面行の `WARNING` 表示を、単純な文字列連結から、種類・優先度・行ハイライト・ダイジェスト表示を持つ構造化 warning へ移行する。

構造化後は warning の種類を identity として扱い、一覧セルの digest、tooltip 詳細、行色を同じ warning collection から算出する。固定行高の一覧では digest を主表示にし、詳細は tooltip に寄せる。

## 現状

Phase 1-7 は実装済み。`WARNING` 列は `WarningDigestText` を表示し、tooltip は `WarningTooltipText`、sort は `WarningDigestText` を使う。

`BMSFile.warning` と `ChartWarningLegacyClassifier` は撤去済み。warning 表示・tooltip・digest・行ハイライトは structured warning だけから算出する。

根拠:

- `song` table は `LR2SongDB.song` として作成・保存され、warning 列は存在しない。
- `UpsertSongs()` は `typeof(LR2SongDB.song)` で保存するため、`BMSFile` の追加プロパティは song DB に書かれない。
- pending/install table は `install.path` と `delete_parent` のみを保存する。
- maintenance table にも warning 列は存在しない。
- pending package は install row からパスを読み直し、起動時・復元時に warning を再初期化する。
- pending install destination 編集状態、pending chart snapshot、playlist snapshot は structured warning snapshot または表示用 digest/tooltip を扱う。

このため、Phase 6 に DB migration は不要だった。

## 実装済み

- Phase 1: `ChartWarningKind` / `ChartWarningCategory` / `ChartWarning` / `ChartWarningCollection` を追加。
- Phase 2: `WarningDigestText` / `WarningTooltipText` を追加し、WARNING 列を digest/tooltip 分離。
- Phase 3: 導入先推定 warning を structured warning として設定し、導入後・導入先確定後に category 削除する経路を追加。
- Phase 4: nested / resource / package-state warning を structured warning へ移行。
- Phase 5: duplicate / zero-note warning を structured warning へ移行し、互換フラグは既存ロジック用に維持。
- Phase 6: legacy `BMSFile.warning` / classifier / legacy kind/category を削除し、snapshot とテストを structured warning 前提へ更新。
- Phase 7: `HasLowConfidenceInstallWarning` / `HasZeroNoteMismatchWarning` / `IsHashDuplicated` を structured warning から導出する互換 alias に変更。
- 後続の ChartFile 抽象化整理で、これらの `BMSFile` warning 表示 alias は削除済み。現行仕様は `devdocs/spec/warning-model.md` を参照。

## 現在の warning 生成元

- nested chart
  - `NestedChartFileInPackage` structured warning。
- resource health
  - `ResourceHealth` category だけを remove + rebuild。
  - WAV/BGA/MOVIE は digest に `リソース不足` として出る。
  - STAGEFILE/BACKBMP/BANNER は tooltip には出るが digest には出さない。
  - resource digest は `instl_dst` 未設定時のみ表示。
- package-state
  - `AlreadyInstalled`、`SingleBmsFile`、`SingleBmsonFile` structured warning。
- duplicate
  - `DuplicateChart` structured warning。
  - 旧 `IsHashDuplicated` alias は削除済み。判定は `DuplicateChart` の有無を見る。
- zero-note mismatch
  - `ZeroNoteMismatch` structured warning。
  - 旧 `HasZeroNoteMismatchWarning` alias は BMSFile から削除済み。row 表示は `ChartFile.Warnings` を見る。
- install estimation
  - ambiguous / metadata mismatch / reinstall not improved / installed destination resolve failed を structured warning として設定する。
  - 旧 `HasLowConfidenceInstallWarning` alias は削除済み。low-confidence 判定は warning kind の集合から明示的に行い、`InstalledDestinationResolveFailed` は含めない。
  - 導入先確定、導入成功、推定状態 clear では `InstallEstimation` category と suggestions を消す。

## Phase 6 実装結果

Phase 6 では legacy warning 互換を完全撤去した。外部永続化がないため、既存 DB や pending table の migration は行っていない。

### Phase 6A: structured warning snapshot

- pending install destination 編集状態は structured warning snapshot を保持し、restore 時に `Warnings.ReplaceAll(...)` 相当で戻す。
- `PendingChartEntry.CreateFromBmsFile()` は structured warnings のみをコピーする。
- playlist snapshot row / library row は `DisplayWarning` / `WarningDigestText` / `WarningTooltipText` を使う。

### Phase 6B: legacy warning 生成停止

- `PackageChartEntry.ApplyInstallEstimationResult(...)` は `SetWarning()` / `ReplaceWarningsByCategory()` 相当の structured warning のみを使う。
- `ApplyInstalledDestinationResolveFailedToPackageUnsafe()` は対象 `PackageChartEntry` に structured warning のみを設定する。
- `AppendWarningLine()`、duplicate 用 legacy helper、`RemoveInstallEstimationWarnings()` は削除済み。
- 以降の新規実装では `SetWarning()` / `ClearWarning()` / `ClearWarningsByCategory()` / `ReplaceWarningsByCategory()` を使う。

### Phase 6C: legacy classifier と `BMSFile.warning` の削除

- `BMSFile.warning` backing field / property は削除済み。
- `ChartWarningLegacyClassifier` は削除済み。
- `ChartWarningKind.LegacyText` と `ChartWarningCategory.Legacy` は削除済み。
- Phase 6 時点の `ChartWarningCollection.EnumerateEffectiveWarnings()` は structured warnings と互換フラグ由来の仮想 warning を統合していた。
- `ClearWarning()` / `ClearWarningsByCategory()` / `ReplaceWarningsByCategory()` は structured warning だけを操作する。
- `DisplayWarning` / `WarningDigestText` / `WarningTooltipText` / `HasHighlightedWarning` は structured warnings と必要な互換フラグから算出していた。

## Phase 7 実装結果

Phase 7 では warning 表示状態の source of truth を structured warning に統一した。当時は `HasLowConfidenceInstallWarning` / `HasZeroNoteMismatchWarning` / `IsHashDuplicated` の public property 名を残し、backing field を持たず、対応する `ChartWarningKind` の有無から導出していた。

その後の ChartFile 抽象化整理で、`BMSFile` の warning 表示 alias は削除した。現在は BMS storage row の warning state を `BMSFile.Warnings` と mutation helper に限定し、通常一覧 / playlist detail の表示値は `ChartFile.Warnings` / `ChartWarningProjectionFormatter` から算出する。

- `ChartWarningCollection.EnumerateEffectiveWarnings()` は stored structured warning だけを列挙する。
- duplicate / zero-note / install estimation の生成元は `SetWarning()` / `ClearWarning()` / `ClearWarningsByCategory()` を直接使う。
- pending chart snapshot と pending install destination 編集状態は structured warning snapshot と suggestions だけをコピーする。
- 低信頼候補選択の preserve 判定は、低信頼 install-estimation warning と `InstallDestinationSuggestions` の一致で行う。
- 旧 alias setter は削除済み。warning を変更する production code は `SetWarning()` / `ClearWarning()` / `ClearWarningsByCategory()` を使う。

## Phase 6-7 テスト方針と確認観点

- persistence
  - song DB / install table / maintenance table に warning が保存されないことを確認する。
  - pending package reload で warning が保存値ではなく再初期化処理から復元されること。
- install estimation
  - ambiguous / metadata mismatch / reinstall not improved / resolve failed が structured warning として表示されること。
  - digest / tooltip / highlight が structured warning だけで維持されること。
  - 手動導入先確定、導入成功、推定状態クリアで `InstallEstimation` category と suggestions が消えること。
- UI 編集状態
  - pending install destination 編集の cancel/restore で structured warning が失われないこと。
  - 候補から選択する場合は従来通り低信頼 warning と suggestions を維持すること。
- snapshot / row
  - pending chart snapshot、playlist detail snapshot、library row が `warning` ではなく `DisplayWarning` / `WarningDigestText` / `WarningTooltipText` を使うこと。
- legacy removal
  - `ChartWarningLegacyClassifier` が存在しないこと。
  - `BMSFile.warning`、`LegacyText`、`ChartWarningCategory.Legacy`、`AppendWarningLine`、`RemoveInstallEstimationWarnings` が残っていないこと。
  - tests は `Warnings.Contains(kind)`、digest、tooltip、highlight を検証すること。
- 回帰
  - `dotnet test BeMusicSeeker.sln /p:Configuration=Release`
  - `dotnet build BeMusicSeeker.sln /p:Configuration=Release`

## 残リスク

- `warning` 名の reflection / column / test helper が残っているとビルドまたは UI 表示で壊れる。禁止対象シンボルの `rg` 確認を継続する。
- `ChartWarningCollection` は structured warning を保持するが、DB 永続化はしない。起動時に必要な warning は既存の初期化処理で再構築する前提。

## 今後の改善候補

- `DisplayWarning` の役割整理
  - WARNING 列の主表示は `WarningDigestText` なので、`DisplayWarning` は互換・詳細表示 alias としての用途を明文化する。
- warning 定義の調整
  - priority、digest label、highlight、resource digest 条件は実利用を見て調整する。
- テスト helper 整理
  - install estimation warning / suggestions / digest / tooltip の繰り返し assertion を helper 化する。
- 現行仕様資料の分離
  - 作成済み: `devdocs/spec/warning-model.md`
