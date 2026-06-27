# P2-04 Native interop / audio / 外部プロセス境界 後続検討

[総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md)

## 位置づけ

BeMusicSeeker は Everything native bridge、BASS / OggVorbis、Win32 API、外部プレイヤー、WindowsFormsHost など OS / native / external process 境界を複数持つ。`.NET 10` 移行ではここが runtime failure になりやすい。

ただし、どの API が本当に blocker になるかは [P0-04 .NET 10 移行準備](./P0-04_DotNet10_移行準備と依存関係整理計画.md) の dry-run と output-layout 調査後でないと確定しない。

## 詳細計画を検討する条件

- `.tmp/dotnet10/api-risk-inventory.md` が作成済み。
- `.tmp/dotnet10/output-layout.md` が作成済み。
- `net10.0-windows` dry-run blocker report がある。
- MainWindow playback panel / external player host が UserControl / adapter に分離済み。

## 方向性

- P/Invoke を `NativeInterop` namespace に集約する。
- native DLL load path を明示し、output layout と一致させる。
- audio player は `IBmsPlayer` / `IAudioPreviewPlayer` / `IExternalPlayerHost` に分ける。
- Everything search は optional capability として扱い、fallback path を明確化する。
- 外部 process / browser open は `IExternalProcessLauncher` 経由にする。

## 現時点で先行可能なこと

- P/Invoke / native DLL 参照を inventory 化する。
- `EverythingNative` の wrapper 使用箇所を一覧化する。
- external player host を P0-03 の `PlaybackPanelView` に閉じ込める。

## 現時点でやらないこと

- native DLL の配置を変更しない。
- BASS / OggVorbis の置換を先行しない。
- 外部プレイヤー挙動を UI split と同じ ticket で変えない。
