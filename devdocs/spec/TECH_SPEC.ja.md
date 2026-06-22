# BeMusicSeeker 現行技術仕様概要

この資料は現行仕様への入口です。詳細な正本は `spec/` 配下の機能別資料を参照してください。

## 技術スタック

- .NET Framework 4.7.2 / C#
- WPF / Livet / MetroRadiance
- SQLite / sqlite-net
- NLog
- Everything SDK 3 + `EverythingBridge_x64.dll`
- SevenZipExtractor
- BASS.NET

## Logging

通常起動で `log/application.log` と `log/install-performance.log` を出力する。既定ログレベルは `INFO` で、ログファイルはサイズベースで `log/archive/` へローテーションする。

ログ基盤とファイル出力設定は `Ribbit.Logging.NLogWrapper` に集約する。個別機能は `application.log` へ直接追記しない。

詳細は [logging-policy.md](logging-policy.md) を参照する。

## 起動の主要境界

起動は次の境界を分けて扱う。

- `startup_install_estimation_ready`
  - catalog、destination resource index、pending package state が揃った状態。
- `startup_ready_operable`
  - UI refresh が終わり操作可能になった状態。
- `startup_initialization_complete`
  - startup background task まで完了した状態。

導入可能 readiness は playlist、score/ranking、chart_info、maintenance hydration を待たない。ただし destination resource index と reverse lookup surface は readiness 前に完成している必要がある。

詳細は [startup-initialization-flow.md](startup-initialization-flow.md) を参照する。

## File Enumeration / Resource Index

通常起動では Everything native bridge を使い、root 配下の chart / audio / image / movie を列挙する。

- `EBridge_ScanChartAndResources` が fixed scan の main path。
- native packed result は chart-relative resource key と reverse lookup surface を含む。
- managed 側は `LibraryResourceIndex` / `DirectoryResourceLookupCache` を直接構築する。
- `ChartScanResult` は通常起動では chart path / chart directory carrier として使う。
- Everything unavailable 時は managed fallback scan から同じ semantics の index を作る。

Everything scan の背景メモは `../everything/` を参照する。

## Data Model

主な正本:

- LR2 song DB
  - BMS catalog。
- app extension tables
  - install state、maintenance、IR data、chart digest、bmson catalog、chart_info。
- memory catalog
  - BMS / BMSON owner objects。
- resource index
  - `LibraryResourceIndex`
  - `DirectoryResourceLookupCache`

詳細は [data-and-indexes.md](data-and-indexes.md) を参照する。

## Install Estimation

導入先推定は `BmsLibraryInstallEstimationService` が担当する。

入力:

- pending package source surface。
- owned catalog / installed package state。
- destination `DirectoryResourceLookupCache`。
- chart_info metadata。

評価:

- category 別 chart-relative resource key を使う。
- package 通常推定は `candidate + bundled` の effective health を使う。
- merge / reinstall correction は candidate-only で評価する。
- confidence / warning / suggestions を返す。
- resource index がない場合は `resource_index_unavailable` とする。

詳細は [install-estimation-current-logic.md](install-estimation-current-logic.md) を参照する。

## Startup Background

startup background scheduler は lane と dependency を持つ。

- `read_hydration`
  - playlist entries、chart_info、maintenance。
- `playlist_followup`
  - URL completion、playlist reference apply、external playlist sync。
- `dependent_maintenance`
  - chart_info / maintenance hydration 完了後の installable maintenance。

background task は単に expected phase から外して短く見せる対象ではない。`startup_background_summary` で task 別 elapsed を観測する。

## Documentation Priority

現行仕様はこの `spec/` ディレクトリを優先する。計画・履歴は `../plan/` を参照する。
