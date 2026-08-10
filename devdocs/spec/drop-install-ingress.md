# ドラッグ＆ドロップ導入 ingress

## 目的

WPF `FileDrop` が返すパスは、Explorer などの通常ファイルだけでなく、アーカイバーが Drop のために一時展開した短命なファイルやフォルダーを指すことがある。導入処理は非同期キューで実行するため、Drop callback の終了後まで借用元パスが存在するとは仮定しない。

入力を受け付けるかどうかと、アプリが入力を削除してよいかどうかは別の契約である。`TempDirectoryPublisher.IsManagedPath` は現在セッションにおける削除 ownership の判定であり、導入入力の allowlist ではない。読み取り可能な通常ファイルとフォルダーは保存場所だけを理由に拒否しない。

## path classification

| 入力 | 分類 | queue へ渡す path | Drop acquisition の削除 ownership |
| --- | --- | --- | --- |
| 現在セッションの managed path | managed | 元 path | なし |
| system temp 外 | stable borrowed | 元 path | なし |
| system temp 配下で現在セッションの managed path ではない | external transient borrowed | managed ingress にコピーした path | acquisition が新規作成した ingress root のみ |

`system temp` は production では `Path.GetTempPath()` を正規化した root である。`%TEMP%\BeMusicSeeker` にユーザーが手動配置した path も、現在セッションの managed path でなければ external transient としてコピーするが、元 path は削除しない。

## acquire-before-enqueue

`FileDrop` の path snapshot は `DroppedInstallIngressMaterializer` が Drop callback 中に同期 acquisition する。external transient source が一つでもある場合だけ、`TempDirectoryPublisher.Get("drop-ingress")` で batch 固有 root を作る。コピー自体を、元 source の lifetime を確保しないまま `Task.Run` や install worker へ送らない。

コピー先は正規化済み system-temp root からの相対 path を managed ingress root に結合する。同じ source tree 内の相対配置を維持し、basename に平坦化しない。destination が ingress root 外へ出る path、system-temp root 自体、destination の祖先となる source directory は拒否する。source path の祖先と列挙した全 entry で reparse point を拒否し、junction や directory symlink を再帰しない。

acquisition は batch atomic である。正規化、存在確認、安全性検証、または一件でもコピーに失敗した場合は request を作らず、作成済み ingress root だけを best-effort で削除する。external original は成功、失敗、cancel のいずれでも move または delete しない。cleanup 失敗はログへ残すが、元の acquisition failure の意味を置き換えない。

## queue ownership

acquisition 成功時の request は durable path、user-visible original path、acquisition が作成した managed ingress root を保持する。stable path と別 producer の既存 managed path は、この request の cleanup 対象に含めない。

ownership は次の一方向に遷移する。

1. acquisition 完了時は request が unconsumed ingress root を所有する。
2. `PackageInstallWorkflowOwner.TryEnqueue` 成功時に queue が request を受け取る。shutdown 中の拒否では request を abandon し、root を削除する。
3. pending cancellation、library generation 切替、shutdown、または installer 呼び出し前の cancellation / generation mismatch では `TryAbandonUnconsumedSources` が root を一度だけ削除する。
4. installer 呼び出し直前に `TransferSourceOwnershipToInstaller` を行う。以後、queue `finally` の abandon は no-op である。

queue lock 内では filesystem I/O、cleanup、cancellation callback、dialog、status callback を実行しない。pending request は lock 内で snapshot / remove し、lock 外で cleanup する。queue が idle を公開するのは abandon cleanup 完了後である。cleanup 失敗は cancel、generation 切替、shutdown の結果を failure に変えない。

library generation 切替と shutdown は current queue context の admission を閉じる。admission が close より先に成立した request は enqueue 成功として扱い、切替側または enqueue 側の補償 cancellation が旧 queue から回収する。close が先に成立した request は enqueue を拒否し、caller ownership のまま abandon する。通常の cancel epoch 中に enqueue された request は受理するが、同じ epoch の cleanup 対象とする。epoch close と pending の再取得は同じ queue lock acquisition で決定し、処理も cleanup もされない request を残さない。

installer handoff 後は既存の managed-temp / pending package lifecycle が source を所有する。archive 展開失敗、展開後 cancel、package 非検出では既存 cleanup が managed input の回収を試みる。pending package が managed source を参照する場合は pending 削除まで保持する。partial mutation または例外では pending source を壊す可能性があるため queue は無条件削除せず、残った session allocation は終了時または次回起動時 cleanup に委ねる。

## WPF terminal behavior

baseline の supported format は WPF `DataFormats.FileDrop` である。DragOver は `GetDataPresent(DataFormats.FileDrop, autoConvert: true)` が true の場合だけ `Copy` を advertise する。Drop は同じ presence check と `GetData(DataFormats.FileDrop, autoConvert: true)` を使う。

Drop は常に `Handled = true` とする。acquisition と enqueue の両方が成功した場合だけ `Effects = Copy` とし、pending tree を展開する。playlist URL download 中、unsupported format、acquisition failure、enqueue rejection は `Effects = None` とし、多言語 UI feedback を表示する。unsupported actual Drop の format summary は診断ログへ出してよいが、DragOver hot path では記録しない。

`FileGroupDescriptorW` / `FileContents` だけを提示する virtual-file-only source は現在の非対象である。実装していない format を DragOver で `Copy` として advertise しない。

## テスト契約

テストは production の global temp session に依存せず、system-temp root、managed 判定、root factory、cleanup を注入する。固定 sleep は使わず、queue の待機や cancellation は barrier / event で同期する。少なくとも transient copy と original 消失後の可読性、stable / managed passthrough、relative layout、atomic rollback、安全でない source の拒否、pending cancel、generation 切替、shutdown rejection、installer handoff 後の非削除、failure 後の queue 継続を observable behavior として検証する。
