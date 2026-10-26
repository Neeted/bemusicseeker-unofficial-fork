using System.IO;
using System.Linq;
using System.Reflection;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>実ライブラリの変更入口を使いながら、リソース管理主体の反映結果を観測します。</summary>
internal static class LibraryResourceIndexTestSupport
{
    /// <summary>
    /// 初回走査の準備とsnapshotの観測に限って既存管理主体を取得します。
    /// 検査する変更はライブラリの公開入口へ渡し、非公開の変更処理を直接起動しません。
    /// </summary>
    internal static LibraryResourceIndexOwner GetOwner(BMSLibrary library)
    {
        FieldInfo field = typeof(BMSLibrary).GetField(
            "libraryResourceIndexOwner", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (LibraryResourceIndexOwner)field.GetValue(library)!;
    }

    /// <summary>既知の実ファイルを作り、通常parserで所持token付きの共通譜面を準備します。</summary>
    internal static ChartFile CreateChart(string directory, string name, string references = "")
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, "#PLAYER 1\n#TITLE " + name + "\n#BPM 120\n" + references);
        return BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path)) with { Token = new OwnedChartToken() };
    }

    /// <summary>期待集合の決定とは分けて、既存の相対資源key正規化/hash契約だけを使います。</summary>
    internal static uint ResourceHash(string path) => ChartResourceKeyHash.GetLookupHash(ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(path));

    /// <summary>テストで作った配置から明記した六分類と、索引Entryの実集合を照合します。製品scanを正解生成に使いません。</summary>
    internal static void AssertEntry(DirectoryResourceLookupCache cache, string directory,
        string[] audio, string[] image, string[] movie, string[] selfAudio, string[] selfImage, string[] selfMovie)
    {
        DirectoryResourceLookupCache.Entry entry = cache.GetEntryOrNull(directory);
        Assert.IsNotNull(entry, directory);
        string[][] expected = [audio, image, movie, selfAudio, selfImage, selfMovie];
        uint[][] actual = [entry.AudioRelativePathHashArray, entry.ImageRelativePathHashArray, entry.MovieRelativePathHashArray,
            entry.SelfOwnedAudioRelativePathHashArray, entry.SelfOwnedImageRelativePathHashArray, entry.SelfOwnedMovieRelativePathHashArray];
        for (int category = 0; category < expected.Length; category++)
        {
            CollectionAssert.AreEquivalent(expected[category].Select(ResourceHash).Distinct().ToArray(), actual[category], directory + " category=" + category);
        }
    }
}
