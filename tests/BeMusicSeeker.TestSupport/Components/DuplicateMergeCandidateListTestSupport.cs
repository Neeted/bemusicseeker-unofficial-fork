using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>統合候補一覧の管理主体へメモリ上のグループを採用する準備を、複数の実行区分で共有します。</summary>
internal static class DuplicateMergeCandidateListTestSupport
{
    /// <summary>ライブラリへの問合せを持たない、一覧の構築入力の代替です。登録範囲のフォルダを指定できます。</summary>
    internal sealed class MemorySource(IEnumerable<string>? registered = null) : IDuplicateMergeCandidateListSource
    {
        private readonly HashSet<string> registeredRoots = new(registered ?? [], StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, (string Title, string Artist)> ResolveDestinationDisplayPairs(IReadOnlyList<string> folders, CancellationToken cancellationToken)
            => folders.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(folder => folder, _ => (string.Empty, string.Empty), StringComparer.OrdinalIgnoreCase);

        public Func<string, bool> CaptureRegisteredBmsRootPredicate() => registeredRoots.Contains;
    }

    /// <summary>フォルダ直下に置く、内容の同じ譜面の値を作ります。</summary>
    internal static ChartFile Chart(string folder, string name)
        => new(ChartFileKind.Bms, Path.Combine(folder, name + ".bms"), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null, "title", "raw", "artist", string.Empty,
            Path.GetFileName(folder), string.Empty, string.Empty, null, 0, null);

    /// <summary>フォルダごとの譜面数を指定してグループを作ります。譜面数の多いフォルダが既定のKEEPになります。</summary>
    internal static DuplicateGroup Group(params (string Folder, int Charts)[] folders)
        => new(
            [.. folders.SelectMany(folder => Enumerable.Range(0, folder.Charts).Select(index => Chart(folder.Folder, "chart" + index)))],
            [.. folders.Select(folder => folder.Folder)]);

    /// <summary>各フォルダに譜面一つずつのグループを作ります。</summary>
    internal static DuplicateGroup Group(params string[] folders) => Group([.. folders.Select(folder => (folder, 1))]);

    /// <summary>メモリ上のグループを一覧へ採用し、統合候補一覧を表示中の状態にします。</summary>
    internal static void Adopt(DuplicateMergeCandidateListOwner list, object library, IEnumerable<DuplicateGroup> groups, MemorySource? source = null)
    {
        list.AttachLibrary(library);
        list.SetTreeSelected(true);
        DuplicateMergeCandidateListPreparation preparation = DuplicateMergeCandidateListOwner.Prepare(
            library, groups, source ?? new MemorySource(), CancellationToken.None);
        Assert.IsTrue(list.Adopt(preparation));
    }
}
