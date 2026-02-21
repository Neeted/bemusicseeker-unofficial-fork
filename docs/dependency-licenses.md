# Dependency License Audit Ledger (GitHub公開監査)

最終更新: 2026-02-21  
対象: `BeMusicSeeker.csproj`, `packages.lock.json`, `libs/`, `vendor/native/`, `native/`, `resources/`, `bin/Release/net472/`

この文書は技術的なライセンス監査記録であり、法的助言ではありません。

## 1. 監査結果サマリー

- Green (公開可): 14
- Yellow (条件付き公開可): 9
- Red (公開ブロッカー): 0

公開ブロッカー (Red):
- (none)

## 2. 監査判定基準

- Green: 一次情報に基づきライセンスと再配布条件が確認できる。
- Yellow: 一次情報はあるが、現物バイナリとの紐付けや同梱文書が不足している。
- Red: 出典・ライセンス・再配布条件のいずれかが確定できない。

## 3. 配布実体と notices の対応

配布実体は次で確認:
- `bin/Release/net472/*.dll`
- `bin/Release/net472/x86/*.dll`
- `bin/Release/net472/x64/*.dll`
- `resources/*.ttf`, `resources/*.otf`

対応方針:
- すべて `ThirdPartyNotices.txt` の `Component` に明記。
- ライセンス本文は `third_party/licenses/` に同梱。

## 4. 依存ライセンス監査台帳

| Status | Component(s) | License | Primary source | Notes / Required action |
|---|---|---|---|---|
| YELLOW | `bass.dll`, `bass_fx.dll`, `bassasio.dll`, `bassenc.dll`, `bassmix.dll`, `basswasapi.dll` | Proprietary | `https://www.un4seen.com/bass.html`, `https://www.un4seen.com/doc/bass/bass.html` | 非商用利用は原則可、商用はライセンス購入が必要。MIT対象外として制約を継続明記。 |
| YELLOW | `Bass.Net.dll` | Vendor wrapper terms | `https://www.radio42.com/bass/bass_purchase.html`, `https://www.radio42.com/bass/bass_register.html` | 個人・非商用・非収益アプリでの利用は許容される文言を確認。ただし条件付きのため notice 明記と BASS本体条項の同時遵守が必要。 |
| GREEN | `7z.dll` (x86/x64) | LGPL (+ unRAR restriction) | https://www.7-zip.org/ | LGPL本文とunRAR制限を同梱。 |
| GREEN | `SevenZipExtractor.dll` | MIT | `https://www.nuget.org/packages/SevenZipExtractor/1.0.12`, `https://github.com/adoconnection/SevenZipExtractor` | 現物DLLとNuGet 1.0.12 (`lib/net45`) のSHA256一致を確認。 |
| YELLOW | `System.Windows.Interactivity.dll`, `Microsoft.Expression.*.dll` | Microsoft SDK/EULA | https://www.microsoft.com/en-us/download/details.aspx?id=10801 | SDK再配布条項の現行確認を継続。 |
| GREEN | `Microsoft.WindowsAPICodePack.dll`, `Microsoft.WindowsAPICodePack.Shell.dll` | MS-PL | https://www.nuget.org/packages/WindowsAPICodePack/ | MS-PL本文を同梱。 |
| GREEN | `Livet.dll`, `Livet.Extensions.dll` | zlib/libpng | https://github.com/runceel/Livet | zlib本文同梱。 |
| GREEN | `MetroRadiance.dll`, `MetroRadiance.Core.dll`, `MetroRadiance.Chrome.dll` | MIT | https://github.com/Grabacr07/MetroRadiance | MIT本文同梱。 |
| GREEN | `QuickConverter.dll` | MIT | https://github.com/joshuafeist/QuickConverter | MIT本文同梱。 |
| GREEN | `DynamicJson.dll` | MS-PL | https://github.com/neuecc/DynamicJson | MS-PL本文同梱。 |
| YELLOW | `SgmlReaderDll.dll` | Apache-2.0 / MS-PL | https://github.com/MindTouch/SGMLReader | 現物DLLの出自系統を追加確認。 |
| GREEN | `sqlite3.dll`, `sqlite.net.dll` | sqlite3: Public Domain / sqlite.net: fork-origin provenance accepted | `https://www.sqlite.org/`, original install evidence | sqlite.net.dll は CodeView path/タイムスタンプ根拠で fork-origin 同系譜として採用。 |
| GREEN | `NLog.dll` | BSD-3-Clause | https://github.com/NLog/NLog | BSD本文同梱。 |
| GREEN | `System.Collections.Immutable.dll` | MIT | NuGet package metadata | MIT本文同梱。 |
| GREEN | `System.Resources.Extensions.dll` | MIT | NuGet package metadata | MIT本文同梱。 |
| GREEN | `System.Memory.dll`, `System.Buffers.dll`, `System.Numerics.Vectors.dll`, `System.Runtime.CompilerServices.Unsafe.dll` | MIT | NuGet package metadata | MIT本文同梱。 |
| GREEN | `Everything3_x64.dll` | MIT | `./.tmp/Everything-SDK-3.0.0.9/include/Everything3.h` header | MITヘッダ記載を根拠化。 |
| GREEN | `EverythingBridge_x64.dll` (optional) | MIT (project) | `native/EverythingBridge/*` | 自作ブリッジ。 |
| YELLOW | `OggVorbis.NET.dll`, `OggVorbis.NET64.dll` | BSD-3-Clause style (wrapper + libogg/libvorbis notices) | `https://github.com/ttsuki/OggVorbis.NET`, local clone: `D:\\github-clone\\OggVorbis.NET` | upstreamリポジトリとCOPYINGは確認済み。厳密SHA一致は未確認のため同一系譜扱いで条件付き。 |
| GREEN | `IniLibrary.dll` | fork-origin provenance accepted | original install evidence | CodeView path/タイムスタンプ根拠で fork-origin 同系譜として採用。 |
| YELLOW | `ligaturesymbols-2.11.otf` | OFL 1.1 (expected) | https://kudakurage.com/ligature_symbols/ | 原本アーカイブとRFN要件確認。 |
| YELLOW | `sovjetboxbd_v0_9.otf` | OFL 1.1 (expected) | (URL未固定) | upstream URL/著作権者を固定。 |
| YELLOW | `commodore-20rounded-20v1.2.ttf` | Freeware (re-distribution clause pending) | https://www.dafont.com/commodore-64.font | 作者・100% Free表示は確認済み。再配布条項の明示文書確認を継続。 |

## 5. 変更実施内容 (この監査サイクル)

1. `LICENSE` を純MIT本文へ是正 (第三者例外条項を除去)
2. `ThirdPartyNotices.txt` を再構築 (Component/Copyright/License/Source/Notes/Status)
3. `third_party/licenses/` を新設し、主要ライセンス本文を同梱
4. `README.md` を新設し、MIT適用範囲と第三者依存優先関係を明記
5. `scripts/deps/check-third-party-notices.ps1` を追加し、配布実体と notices 差分を検出

## 6. GitHub公開前の残タスク

必須 (Red 解消):
(none)

実行手順は `docs/red-item-resolution-playbook.md` を参照。

推奨 (Yellow 解消):
1. `Bass.Net.dll` の条項変更監視（radio42側規約更新時の再監査）
2. Blend SDK再配布条項の参照元を固定
3. `SgmlReaderDll.dll` の系統 (Apache-2.0 か MS-PL か) を固定
4. `sovjetboxbd_v0_9.otf` の出典URL固定
5. `commodore-20rounded-20v1.2.ttf` の再配布条項を一次文書で固定
6. `OggVorbis.NET*.dll` の厳密ビルド出典（コミット/ビルド手順）を証跡化
7. `IniLibrary.dll` / `sqlite.net.dll` の fork-origin 判断証跡を継続保全

## 7. 参照一次情報

- NuGet metadata: `NLog 4.4.3`, `System.Resources.Extensions 8.0.0`, `System.Memory 4.5.5`, `System.Buffers 4.5.1`, `System.Numerics.Vectors 4.5.0`, `System.Runtime.CompilerServices.Unsafe 4.5.3`
- Everything SDK header: `./.tmp/Everything-SDK-3.0.0.9/include/Everything3.h`
- BASS official: https://www.un4seen.com/
- 7-Zip official: https://www.7-zip.org/
- SIL OFL text: https://openfontlicense.org/open-font-license-official-text/

## 8. Red項目の実行プレイブック

Red解消の実務手順は以下に分離:

- `docs/red-item-resolution-playbook.md`

このプレイブックには、Red項目ごとの以下が含まれる:

1. URL候補（一次情報優先）
2. 検証コマンド（SHA256照合・メタ情報抽出）
3. 完了条件（Red -> Yellow/Green の判定条件）

## 9. 最新実行に基づく判定確定反映 (2026-02-21)

`docs/red-item-resolution-playbook.md` の実行結果セクションに基づき、以下を正式反映:

1. `SevenZipExtractor.dll`: **Red -> Green**
2. `OggVorbis.NET.dll`, `OggVorbis.NET64.dll`: **Red -> Yellow**
3. `IniLibrary.dll`: **Red -> Green**
4. `commodore-20rounded-20v1.2.ttf`: **Red -> Yellow**

注記:
- `ThirdPartyNotices.txt` と本台帳のステータスは同期済み。
