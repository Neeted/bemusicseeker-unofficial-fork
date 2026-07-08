# REF-MVP-C36 LR2 Sync Input Builder Helper Ownership Decision

作成日: 2026-07-07

## 決定

Lane C は `REF-MVP-C36: LR2 sync input builder helper ownership cleanup` に進む。

C35 後の builder delegate surface は中間状態として許容する。ただし、`Lr2SongDbSyncInputBuilder` が既に所有している folder info / directory entry / text file directory selection まで `BMSLibrary` から delegate で受け取る必要は薄い。

そのため、次は full service extraction ではなく、builder-owned selection helper と、その helper が使う input surface merge / overlay helper を `BmsLibraryInternal` 側へ移す。

## 理由

- C35 により `CreateLr2SongDbSyncInput` は root-state snapshot 採取と builder 呼び出しに近づいた。
- folder info / directory entry / text file directory selection は root mutable state や DB access を直接扱わず、input surface composition に属する。
- generic dependency/context object を作るだけでは delegate surface を包むだけになりやすく、責務削減としての効果が弱い。
- folder candidate selection と directory target selection は app-managed output scope / settings / LR2 folder parent target の境界に近いため、今回の範囲には含めない。

## C36 の範囲

- `Lr2SongDbSyncInputBuilder` が次の selection を直接担当する。
  - folder info candidate selection
  - directory entry selection
  - text file directory selection
- selection helper が使う merge / overlay / normalization helper を `BmsLibraryInternal` の shared helper に移す。
- `BMSLibrary.CreateLr2SongDbSyncInput` から builder に渡す delegate を減らす。
- private `CreateLr2SongDbSyncInput` entry point、log item、timing、DB schema、setting name、serialized/public surface は変えない。

## C36 で触らないもの

- folder candidate selection。
- directory target selection。
- row/root/settings/scan/prepared/app-managed scope snapshot 採取。
- scan surface / prepared surface / file-diff freshness の mutable cache と世代管理。
- queue / run / status / cancel / reservation / mutation block。
- DB write / chart_info projection / UI warning dispatch。

## C36 完了後

C36 完了後に `Lr2SongDbSyncInputBuilder` の残り delegate surface と `BMSLibrary` の root 残存責務を再確認する。

次に LR2 sync input 周りを続ける場合は、folder candidate / directory target selection の境界を 1 ticket だけ整理できるかを検討する。明確な責務削減が見込めない場合は、Lane C の別 workflow、例えば maintenance、folder / file operation、package install follow-up へ移る。
