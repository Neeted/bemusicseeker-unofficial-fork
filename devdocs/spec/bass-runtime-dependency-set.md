# BASS runtime dependency set

更新日: 2026-08-09

## 固定する依存関係

- 対象アーキテクチャ: Windows x64 / AMD64 PE
- managed package set: `ManagedBass`, `ManagedBass.Mix`, `ManagedBass.Fx`, `ManagedBass.Enc`, `ManagedBass.Asio`, `ManagedBass.Wasapi` exact `4.0.2`
- package pages: https://www.nuget.org/packages/ManagedBass/4.0.2 and the matching `ManagedBass.*` packages
- normalized managed license text SHA-256: `41810CB2403489DB4FB5B2F961B78DC3629CE5B9DF06251D939A89ED3FB05063`
- managed assembly output: framework-dependent build root contains the six `ManagedBass*.dll` assemblies; single-file publish bundles managed assemblies and does not expose companion wrapper DLLs
- native output: `libs/x64`
- publish native output: `libs/x64`
- rollback unit: the complete six-file native set is backed up and restored as one set when validation or copy fails; a partial version set is not accepted

## Native components

All selected archive members are under the unique `x64` path, have an AMD64 PE machine header, and were checked through the component's exported `GetVersion` function before the vendor replacement.

| Component | Version / `GetVersion` | Official archive | Archive SHA256 | Selected member | DLL SHA256 | Output / publish path |
| --- | --- | --- | --- | --- | --- | --- |
| `bass.dll` | 2.4.18.3 / `0x02041203` | https://www.un4seen.com/files/bass24.zip | `3A03EC9A33D0F4F9D167660DA51C8BB1432E8977496995455AB137277D69636E` | `bass24\x64\bass.dll` | `FEBB2CF1882D554C3A958280777DA0B69F07DE6E262DF271DE11C56E4A54AFD4` | `libs/x64/bass.dll` |
| `bassmix.dll` | 2.4.12.0 / `0x02040C00` | https://www.un4seen.com/files/bassmix24.zip | `C22D3D6135B5D14AF23AE1D54100BE6C30FE500D9B0F253B5EBC7E9130DBAD85` | `bassmix24\x64\bassmix.dll` | `F782CAE8090700A456C9E7AEAA7770C3B90CB60A1E765C4B3CBAE739D3B4D58D` | `libs/x64/bassmix.dll` |
| `bassenc.dll` | 2.4.17.0 / `0x02041100` | https://www.un4seen.com/files/bassenc24.zip | `7A4EC4A92A03D74479192FA3D9C22B69CA8EFFBDC46D8F6CA045D18E8C3566A7` | `bassenc24\x64\bassenc.dll` | `9D8EE8D750DEF93E927E62E35D02A4CC8457C509CFA561C47AED3381691F51F8` | `libs/x64/bassenc.dll` |
| `basswasapi.dll` | 2.4.4.1 / `0x02040401` | https://www.un4seen.com/files/basswasapi24.zip | `4BA99200EBEF8DCA11CC99CBA9B5DC3E51A1C467E570DE2CBC0631A038F7EA2D` | `basswasapi24\x64\basswasapi.dll` | `6F0869C11431E01F759FBE1CD6080299C833C519EB8AB1FEAE12106907B1FBD1` | `libs/x64/basswasapi.dll` |
| `bass_fx.dll` | 2.4.12.6 / `0x02040C06` | https://www.un4seen.com/files/z/0/bass_fx24.zip | `A4BAF602865941963127ACB15ED12627D108189F99C2757970432AE7DA0366CD` | `bass_fx24\x64\bass_fx.dll` | `A6E1847EEF52D882B4137AF514D834C2E220DACEB417C821D1E502FB7A34C84A` | `libs/x64/bass_fx.dll` |
| `bassasio.dll` | 1.4.3.0 / `0x01040300` | https://www.un4seen.com/files/bassasio14.zip | `54BFE2F051338BB016B4CA08F840B93E72EE7EDFC9BF0245F08D7EDC6C72F45D` | `bassasio14\x64\bassasio.dll` | `73BF79C8ECCD63DEA8EB3E3E9B5FFE6F9406DEB9BBCCCC7557CA54F5013B4B96` | `libs/x64/bassasio.dll` |

The six DLLs are also copied from `vendor/native/x64` to the application publish directory by the existing publish target. Managed package provenance and native provenance are intentionally separate: the six ManagedBass packages supply the managed bindings, while the application owns and validates the x64 native set.

## ManagedBass migration closure invariant

The table above is the final dependency contract for the completed migration.
The six ManagedBass packages remain exact `4.0.2`, and every native version,
archive/member selection, SHA-256, x64 output path, and rollback-unit rule is
unchanged by the closeout. No native binary or package version is updated as
part of documentation closure. The current source and output policy contain no
managed BASS.NET wrapper, registration call, or registration material; the
updater's removal of an obsolete `libs/Bass.Net.dll` from an older installation
is compatibility cleanup only.

No registration credentials, registration values, license text, or third-party notice content is recorded in this specification.
