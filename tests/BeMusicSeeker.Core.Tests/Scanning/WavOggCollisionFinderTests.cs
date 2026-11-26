using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 同名の WAV と OGG の組の集計規則（管理側）と、Everything の結果の採用・切替え・失敗の選択を、固定の入力で確認します。
/// ネイティブ側の集計は <c>native/EverythingBridge/tests/EverythingBridgeQueryTests.cpp</c> が同じ条件表で確認し、
/// 二つの経路が同じ結果を返すことをこの二つの表で分担します。
/// </summary>
[TestClass]
public sealed class WavOggCollisionFinderTests
{
    /// <summary>
    /// 保証：同じディレクトリで拡張子を除いた名前が大文字小文字を無視して一致する <c>.wav</c> と <c>.ogg</c> だけが組になり、
    /// 表記は <c>.wav</c> のものになる。複数のピリオドを持つ名前は最後のピリオドで拡張子を分け、空の名前・片方だけ・別のディレクトリ・
    /// <c>.wav</c> という名前のディレクトリ・<c>.mp3</c>/<c>.flac</c> だけの名前は組にならない。結果はディレクトリ、名前の序数順。
    /// 検出する誤り：拡張子の大文字小文字の見落とし、複数ピリオドの名前の誤分割、空の名前の計上、ディレクトリをまたいだ組の成立。
    /// </summary>
    [TestMethod]
    public void Collect_PairsOnlyWavAndOggWithSameDirectoryAndName()
    {
        (string Label, string[] Paths, (string Directory, string Name)[] Expected)[] table =
        [
            ("名前の大文字小文字を無視", [@"C:\a\Foo.wav", @"C:\a\foo.ogg"], [(@"C:\a", "Foo")]),
            ("大文字の拡張子", [@"C:\a\bar.WAV", @"C:\a\bar.OGG"], [(@"C:\a", "bar")]),
            ("複数ピリオドの名前", [@"C:\a\foo.v2.wav", @"C:\a\foo.v2.ogg"], [(@"C:\a", "foo.v2")]),
            ("mp3とflacが並ぶ名前", [@"C:\a\baz.wav", @"C:\a\baz.mp3", @"C:\a\baz.flac", @"C:\a\baz.ogg"], [(@"C:\a", "baz")]),
            ("oggが先でも表記はwav", [@"C:\a\q.ogg", @"C:\a\Q.wav"], [(@"C:\a", "Q")]),
            ("ディレクトリの大文字小文字を無視し表記はwav", [@"C:\A\k.wav", @"C:\a\K.OGG"], [(@"C:\A", "k")]),
            ("日本語", [@"C:\日本語\音.wav", @"C:\日本語\音.ogg"], [(@"C:\日本語", "音")]),
            ("ドライブ直下", [@"C:\r.wav", @"C:\r.ogg"], [(@"C:\", "r")]),
            ("名前が空", [@"C:\a\.wav", @"C:\a\.ogg"], []),
            ("片方の形式だけ", [@"C:\a\w.wav", @"C:\a\o.ogg"], []),
            ("別のディレクトリ", [@"C:\a\x.wav", @"C:\b\x.ogg"], []),
            ("wavという名前のディレクトリ", [@"C:\a\x.wav\z.wav", @"C:\a\x.ogg"], []),
            ("mp3とflacだけ", [@"C:\a\m.mp3", @"C:\a\m.flac"], []),
            ("ディレクトリにだけピリオド", [@"C:\a.b\wav", @"C:\a.b\ogg"], []),
            ("並び：ディレクトリ、名前の順",
                [@"C:\b\z.wav", @"C:\b\z.ogg", @"C:\a\y.wav", @"C:\a\y.ogg", @"C:\a\b.wav", @"C:\a\b.ogg"],
                [(@"C:\a", "b"), (@"C:\a", "y"), (@"C:\b", "z")]),
            ("入力なし", [], []),
        ];

        foreach ((string label, string[] paths, (string Directory, string Name)[] expected) in table)
        {
            WavOggCollision[] actual = [.. WavOggCollisionAggregator.Collect(paths)];
            CollectionAssert.AreEqual(
                expected.Select(pair => new WavOggCollision(pair.Directory, pair.Name)).ToArray(), actual, label);
        }
    }

    /// <summary>
    /// 保証：Everything の問い合わせが成功した結果は0組でもそのまま採用して管理側の列挙を実行せず、問い合わせの失敗（利用できない場合を含む）だけ
    /// 管理側の列挙へ一回切り替え、入口がない場合・契約不一致・版の不一致は切り替えずにその失敗を返す。
    /// 検出する誤り：0組を理由にした歩き直し、失敗時の切替えの欠落、配置異常の隠蔽。
    /// </summary>
    [TestMethod]
    [DataRow(true, 0, "", false)]
    [DataRow(true, 2, "", false)]
    [DataRow(false, 0, "bridge_wav_ogg_query_failed:3", true)]
    [DataRow(false, 0, "bridge_wav_ogg_query_failed:2", true)]
    [DataRow(false, 0, "bridge_wav_ogg_exception:error", true)]
    [DataRow(false, 0, "bridge_wav_ogg_collision_export_missing", false)]
    [DataRow(false, 0, "bridge_contract_mismatch:expected=2:actual=1", false)]
    [DataRow(false, 0, "bridge_header_size_mismatch:expected=80:actual=72", false)]
    public void SelectResult_AdoptsSuccessAndFallsBackOnlyOnOrdinaryFailure(
        bool everythingSucceeded, int everythingCollisionCount, string everythingFailureReason, bool expectsManagedFallback)
    {
        WavOggCollisionSearchResult everything = everythingSucceeded
            ? WavOggCollisionSearchResult.Succeeded(
                EverythingNative.GroupedEnumerationBackendName,
                [.. Enumerable.Range(0, everythingCollisionCount).Select(index => new WavOggCollision(@"C:\a", "n" + index))], 1L, 1L)
            : WavOggCollisionSearchResult.Failed(EverythingNative.GroupedEnumerationBackendName, everythingFailureReason);
        var managed = WavOggCollisionSearchResult.Succeeded("fast", [new WavOggCollision(@"C:\managed", "only")], 1L, 1L);
        int managedCount = 0;

        WavOggCollisionSearchResult selected = WavOggCollisionFinder.SelectResult(everything, () =>
        {
            managedCount++;
            return managed;
        });

        Assert.AreEqual(expectsManagedFallback ? 1 : 0, managedCount);
        Assert.AreSame(expectsManagedFallback ? managed : everything, selected);
        if (!everythingSucceeded && !expectsManagedFallback)
        {
            Assert.IsFalse(selected.Success, "配置の異常は失敗として返す");
        }
    }
}
