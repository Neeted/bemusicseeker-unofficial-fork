# Vorbis復号bridge

libogg 1.3.6、libvorbisfile 1.3.7を静的リンクしたWindows x64 DLLです。C ABI 1はopaque handleと固定幅整数だけを公開し、復号中は呼出元が入力メモリを保持します。取得アーカイブのSHA-256はCMakeLists.txtで検証します。

Visual Studio 18、MSVC toolset 14.51.36231（コンパイラー19.51.36256.0）、CMake 4.3.1-msvc1で生成したDLLを採用しています。Release、静的CRT、`/fp:precise`です。

```powershell
cmake --preset windows-x64-release -S native/VorbisBridge
cmake --build native/VorbisBridge/build --config Release
Get-FileHash native/VorbisBridge/build/Release/bms_vorbis.dll -Algorithm SHA256
```

生成DLLを`vendor/native/x64/bms_vorbis.dll`へコピーするときは、[依存仕様](../../devdocs/spec/runtime/audio-dependencies.md)と実DLL検査のhash・版・ABIを同じ変更で更新します。PEのタイムスタンプ等が変わる再ビルドに対してバイト一致を自動保証するものではありません。

`tools/vorbis_reference.cpp`はbridgeを呼ばず直接`ov_read_float`で参照float PCMを生成します。生成方法と入力・出力・依存・コンパイラー・採用DLLの実測値は[参照manifest](../../BeMusicSeeker.Tests/TestData/audio/vorbis-reference-manifest.json)を参照します。C#ラッパーの検証と復号アルゴリズムの独立証明は区別します。
