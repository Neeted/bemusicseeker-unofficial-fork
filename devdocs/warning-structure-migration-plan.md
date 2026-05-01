# WARNING 構造化移行計画

## 目的

譜面行の `WARNING` 表示を、単純な文字列連結から、種類・優先度・行ハイライト・ダイジェスト表示を持つ構造化 warning へ移行する。

構造化後は warning の種類を identity として扱い、一覧セルの digest、tooltip 詳細、行色を同じ warning collection から算出する。固定行高の一覧では digest を主表示にし、詳細は tooltip に寄せる。

## 現状

Phase 1-6 は実装済み。`WARNING` 列は `WarningDigestText` を表示し、tooltip は `WarningTooltipText`、sort は `WarningDigestText` を使う。

`BMSFile.warning` と `ChartWarningLegacyClassifier` は撤去済み。warning 表示・tooltip・digest・行ハイライトは structured warning と既存互換フラグから算出する。

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
  - `IsHashDuplicated` は互換フラグとして維持。
- zero-note mismatch
  - `ZeroNoteMismatch` structured warning。
  - `HasZeroNoteMismatchWarning` は互換フラグとして維持。
- install estimation
  - ambiguous / metadata mismatch / reinstall not improved / installed destination resolve failed を structured warning として設定する。
  - 導入先確定、導入成功、推定状態 clear では `InstallEstimation` category、suggestions、low-confidence flag を消す。

## Phase 6 実装結果

Phase 6 では legacy warning 互換を完全撤去した。外部永続化がないため、既存 DB や pending table の migration は行っていない。

### Phase 6A: structured warning snapshot

- pending install destination 編集状態は structured warning snapshot を保持し、restore 時に `Warnings.ReplaceAll(...)` 相当で戻す。
- `PendingChartEntry.CreateFromBmsFile()` は structured warnings のみをコピーする。
- playlist snapshot row / library row は `DisplayWarning` / `WarningDigestText` / `WarningTooltipText` を使う。

### Phase 6B: legacy warning 生成停止

- `BMSLibrary.ApplyInstallEstimationResultToFiles()` は `SetWarning()` のみを使う。
- `ApplyInstalledDestinationResolveFailedToFilesUnsafe()` も structured warning のみを設定する。
- `AppendWarningLine()`、duplicate 用 legacy helper、`RemoveInstallEstimationWarnings()` は削除済み。
- 以降の新規実装では `SetWarning()` / `ClearWarning()` / `ClearWarningsByCategory()` / `ReplaceWarningsByCategory()` を使う。

### Phase 6C: legacy classifier と `BMSFile.warning` の削除

- `BMSFile.warning` backing field / property は削除済み。
- `ChartWarningLegacyClassifier` は削除済み。
- `ChartWarningKind.LegacyText` と `ChartWarningCategory.Legacy` は削除済み。
- `ChartWarningCollection.EnumerateEffectiveWarnings()` は structured warnings と互換フラグ由来の仮想 warning だけを統合する。
- `ClearWarning()` / `ClearWarningsByCategory()` / `ReplaceWarningsByCategory()` は structured warning だけを操作する。
- `DisplayWarning` / `WarningDigestText` / `WarningTooltipText` / `HasHighlightedWarning` は structured warnings と必要な互換フラグだけから算出する。

`HasLowConfidenceInstallWarning` / `HasZeroNoteMismatchWarning` / `IsHashDuplicated` は Phase 6 では削除しない。検索、通知、既存フィルタ、既存 UI 状態に関わるため、別途専用の整理計画で扱う。

## Phase 6 テスト方針と確認観点

- persistence
  - song DB / install table / maintenance table に warning が保存されないことを確認する。
  - pending package reload で warning が保存値ではなく再初期化処理から復元されること。
- install estimation
  - ambiguous / metadata mismatch / reinstall not improved / resolve failed が structured warning として表示されること。
  - digest / tooltip / highlight が structured warning だけで維持されること。
  - 手動導入先確定、導入成功、推定状態クリアで `InstallEstimation` category、suggestions、low-confidence flag が消えること。
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
  - `dotnet test BeMusicSeeker-decomp.sln /p:Configuration=Release`
  - `dotnet build BeMusicSeeker-decomp.sln /p:Configuration=Release`

## 残リスク

- `warning` 名の reflection / column / test helper が残っているとビルドまたは UI 表示で壊れる。禁止対象シンボルの `rg` 確認を継続する。
- `Warnings.Contains(kind)` は stored structured warning だけを見る。互換フラグ由来の warning を検証する場合は digest / tooltip / highlight を見る。
- `ChartWarningCollection` は structured warning を保持するが、DB 永続化はしない。起動時に必要な warning は既存の初期化処理で再構築する前提。
