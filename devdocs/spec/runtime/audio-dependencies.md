# 音声基盤の依存関係

## 目的と適用範囲

音声基盤で使用するマネージドパッケージとネイティブDLLの組合せを定めます。配布元の最新版を表す一覧ではなく、このリポジトリが採用している版の契約です。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### マネージドパッケージ

Windows x64を対象とし、`ManagedBass`、`ManagedBass.Mix`、`ManagedBass.Fx`、`ManagedBass.Enc`、`ManagedBass.Asio`、`ManagedBass.Wasapi` を全て正確に `4.0.2` に固定します。中央のパッケージ設定と各プロジェクトの固定済み依存ファイルを一致させます。

通常のフレームワーク依存ビルドでは六つのマネージドアセンブリを出力ルートへ置きます。単一ファイルの配布では内部へまとめ、別のラッパーDLLとして添付しません。正規化済みマネージドライセンス本文のSHA-256は `41810CB2403489DB4FB5B2F961B78DC3629CE5B9DF06251D939A89ED3FB05063` です。ライセンス本文と第三者の告知をこの仕様へ複製しません。

### ネイティブDLL

DLLは `vendor/native/x64` で所有し、通常出力・配布とも `libs/x64` に置きます。アーカイブ内の一意なx64の項目を採用し、AMD64のPE形式と部品固有の版取得結果を検証します。

| 部品 | 版 / `GetVersion` | 配布元 | アーカイブSHA-256 | 採用ファイル | DLLのSHA-256 | 出力先 |
| --- | --- | --- | --- | --- | --- | --- |
| `bass.dll` | 2.4.18.3 / `0x02041203` | https://www.un4seen.com/files/bass24.zip | `3A03EC9A33D0F4F9D167660DA51C8BB1432E8977496995455AB137277D69636E` | `bass24\x64\bass.dll` | `FEBB2CF1882D554C3A958280777DA0B69F07DE6E262DF271DE11C56E4A54AFD4` | `libs/x64/bass.dll` |
| `bassmix.dll` | 2.4.12.0 / `0x02040C00` | https://www.un4seen.com/files/bassmix24.zip | `C22D3D6135B5D14AF23AE1D54100BE6C30FE500D9B0F253B5EBC7E9130DBAD85` | `bassmix24\x64\bassmix.dll` | `F782CAE8090700A456C9E7AEAA7770C3B90CB60A1E765C4B3CBAE739D3B4D58D` | `libs/x64/bassmix.dll` |
| `bassenc.dll` | 2.4.17.0 / `0x02041100` | https://www.un4seen.com/files/bassenc24.zip | `7A4EC4A92A03D74479192FA3D9C22B69CA8EFFBDC46D8F6CA045D18E8C3566A7` | `bassenc24\x64\bassenc.dll` | `9D8EE8D750DEF93E927E62E35D02A4CC8457C509CFA561C47AED3381691F51F8` | `libs/x64/bassenc.dll` |
| `basswasapi.dll` | 2.4.4.1 / `0x02040401` | https://www.un4seen.com/files/basswasapi24.zip | `4BA99200EBEF8DCA11CC99CBA9B5DC3E51A1C467E570DE2CBC0631A038F7EA2D` | `basswasapi24\x64\basswasapi.dll` | `6F0869C11431E01F759FBE1CD6080299C833C519EB8AB1FEAE12106907B1FBD1` | `libs/x64/basswasapi.dll` |
| `bass_fx.dll` | 2.4.12.6 / `0x02040C06` | https://www.un4seen.com/files/z/0/bass_fx24.zip | `A4BAF602865941963127ACB15ED12627D108189F99C2757970432AE7DA0366CD` | `bass_fx24\x64\bass_fx.dll` | `A6E1847EEF52D882B4137AF514D834C2E220DACEB417C821D1E502FB7A34C84A` | `libs/x64/bass_fx.dll` |
| `bassasio.dll` | 1.4.3.0 / `0x01040300` | https://www.un4seen.com/files/bassasio14.zip | `54BFE2F051338BB016B4CA08F840B93E72EE7EDFC9BF0245F08D7EDC6C72F45D` | `bassasio14\x64\bassasio.dll` | `73BF79C8ECCD63DEA8EB3E3E9B5FFE6F9406DEB9BBCCCC7557CA54F5013B4B96` | `libs/x64/bassasio.dll` |

### 更新と配布の境界

マネージドパッケージは呼出しの定義を供給し、ネイティブDLL一式はアプリ側で所有・検証します。出所を混同しません。現在の実装はBASS.NETの登録呼出しや登録情報を必要としません。更新処理が旧配置の `libs/Bass.Net.dll` を除去することは、旧インストールの互換清掃です。

依存更新では版、取得結果、アーカイブとDLLのハッシュ、PE形式、採用ファイル、固定済みパッケージ、出力を一つの互換単位として変更します。検証・コピーの失敗で戻す単位も六つのDLL全体です。一部だけ違う版にして配布しません。

ロード、正確なハンドルへの接続、版検証、ASIO・WASAPI、オフライン変換、形式の交渉、後片付けを既存の境界で検証します。物理機器不要のPCM取得、機器を使う確認、配布と更新、クリーンな環境のロードは別の確認範囲です。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 読込み、PE・版・版の整数表現と実DLL | [`BassNativeRuntime`](../../../BeMusicSeeker/Ribbit/Media/Audio/BassNativeRuntime.cs)、[`BassVersionPacking`](../../../BeMusicSeeker/Ribbit/Media/Audio/BassVersionPacking.cs) | [`BassNativeRuntimeTests`](../../../BeMusicSeeker.Tests/Playback/BassNativeRuntimeTests.cs)、[`BassCollectibleLoadContextTests`](../../../BeMusicSeeker.Tests/Playback/BassCollectibleLoadContextTests.cs) |
| 配布物、依存定義、DLLの版とハッシュ | プロジェクトの依存定義、固定済み依存ファイル、配布処理 | [`ManagedDependencyOutputPolicyTests`](../../../BeMusicSeeker.Tests/Verification/ManagedDependencyOutputPolicyTests.cs)、[配布検証](../development/testing.md) |

## 関連資料

[音声実行基盤](audio.md)、[音声変換](audio-conversion.md)、[採用理由](../../decisions/managedbass-adoption.md)を参照します。
