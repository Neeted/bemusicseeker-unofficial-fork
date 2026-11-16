using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>同じ機能の複数moduleが必要とする入力・観測だけを共用します。</summary>
internal static class PlaylistImportDialogTestSupport
{

    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal static PlaylistImportDialogViewModel Create() => new(_ => new(PlaylistImportStartStatus.Busy));
    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal static PlaylistImportCandidate C(string path, string name, string symbol = "", string tag1 = "A", string tag2 = "") => new(new("https://example.test/" + path), name, symbol, tag1, tag2);
    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal static IEnumerable<PlaylistImportItem> AllItems(IEnumerable<PlaylistImportItem> items) => items.SelectMany(x => new[] { x }.Concat(AllItems(x.Children)));

}
