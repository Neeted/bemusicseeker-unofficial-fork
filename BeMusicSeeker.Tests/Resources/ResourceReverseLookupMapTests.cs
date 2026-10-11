using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>差分操作の仕事量を局所的に観測し、背景全件の列挙と複製を検出する。</summary>
[TestClass]
public sealed class ResourceReverseLookupMapTests
{
    /// <summary>
    /// 保証：逆引きの分岐と差分更新は基底全件を列挙せず、更新キーだけを反映して旧世代と無関係キーを維持する。
    /// </summary>
    [TestMethod]
    public void ForkAndActualChanges_DoNotEnumerateOrCopyTheOwnedBase()
    {
        var baseline = new NonEnumerableBaseline(new Dictionary<uint, string[]>
        {
            [11u] = ["A"],
            [22u] = ["B"],
            [33u] = ["Unrelated"]
        });
        var initial = new ResourceReverseLookupMap(baseline);
        ResourceReverseLookupMap first = initial.Fork();
        Assert.IsTrue(first.Set(11u, ["A", "C"]));
        Assert.IsTrue(first.Set(44u, ["D"]));
        ResourceReverseLookupMap second = first.Fork();
        Assert.IsTrue(second.Set(22u, []));
        Assert.IsTrue(second.Set(44u, ["E"]));

        AssertCandidates(initial, 11u, "A");
        AssertCandidates(first, 11u, "A", "C");
        AssertCandidates(second, 11u, "A", "C");
        AssertCandidates(initial, 22u, "B");
        AssertCandidates(first, 22u, "B");
        AssertCandidates(second, 22u);
        AssertCandidates(first, 44u, "D");
        AssertCandidates(second, 44u, "E");
        AssertCandidates(second, 33u, "Unrelated");
        Assert.IsFalse(initial.ContainsKey(44u));
        Assert.IsTrue(second.ContainsKey(22u));
        Assert.IsFalse(second.ContainsKey(55u));
        Assert.AreEqual(3, initial.Count);
        Assert.AreEqual(4, first.Count);
        Assert.AreEqual(4, second.Count);
    }

    /// <summary>
    /// 保証：分岐後の元側・分岐側の書込みを相互に隔離し、分岐時点までの共通値を後の更新で上書きしない。
    /// </summary>
    [TestMethod]
    public void WritesAfterFork_AreIsolatedInBothDirections()
    {
        var original = new ResourceReverseLookupMap(new NonEnumerableBaseline(
            new Dictionary<uint, string[]> { [7u] = ["Initial"] }));
        original.Set(9u, ["Shared change"]);
        ResourceReverseLookupMap fork = original.Fork();

        original.Set(7u, ["Original only"]);
        original.Set(9u, ["Original changed"]);
        fork.Set(10u, ["Fork only"]);

        AssertCandidates(fork, 7u, "Initial");
        AssertCandidates(fork, 9u, "Shared change");
        AssertCandidates(original, 7u, "Original only");
        AssertCandidates(original, 9u, "Original changed");
        Assert.IsFalse(original.ContainsKey(10u));
        Assert.AreEqual(2, original.Count);
        Assert.AreEqual(3, fork.Count);
    }

    /// <summary>
    /// 保証：同じ候補列の再設定を無変更とし、候補順序の変化と調査済み欠落キーは意味ある差分として記録する。
    /// </summary>
    [TestMethod]
    public void IdenticalCandidates_DoNotWriteButOrderAndCachedAbsenceRemainMeaningful()
    {
        var map = new ResourceReverseLookupMap(new NonEnumerableBaseline(
            new Dictionary<uint, string[]> { [1u] = ["A", "B"] }));

        Assert.IsFalse(map.Set(1u, ["A", "B"]));
        Assert.IsTrue(map.Set(1u, ["B", "A"]));
        Assert.IsFalse(map.Set(1u, ["B", "A"]));
        Assert.IsFalse(map.ContainsKey(2u));
        Assert.IsTrue(map.Set(2u, []));
        Assert.IsTrue(map.ContainsKey(2u));
        Assert.IsFalse(map.Set(2u, []));
        AssertCandidates(map, 1u, "B", "A");
        AssertCandidates(map, 2u);
        Assert.AreEqual(2, map.Count);
    }

    /// <summary>
    /// 保証：明示したキー列挙は基底と最新差分を重複なく併合し、候補なしキーも残しつつ世代別の値を維持する。
    /// </summary>
    [TestMethod]
    public void ExplicitKeyEnumeration_CombinesBaseAndLatestChangesWithoutDuplicates()
    {
        var map = new ResourceReverseLookupMap(new Dictionary<uint, string[]>
        {
            [1u] = ["A"],
            [2u] = ["B"]
        });
        map.Set(1u, []);
        map.Set(3u, ["C"]);
        ResourceReverseLookupMap next = map.Fork();
        next.Set(3u, ["D"]);
        next.Set(4u, []);

        CollectionAssert.AreEquivalent(new uint[] { 1u, 2u, 3u }, map.Keys.ToArray());
        CollectionAssert.AreEquivalent(new uint[] { 1u, 2u, 3u, 4u }, next.Keys.ToArray());
        AssertCandidates(map, 3u, "C");
        AssertCandidates(next, 3u, "D");
        Assert.AreEqual(4, next.Count);
    }

    private static void AssertCandidates(ResourceReverseLookupMap map, uint hash, params string[] expected)
    {
        Assert.IsTrue(map.TryGetValue(hash, out string[] actual));
        CollectionAssert.AreEqual(expected, actual);
    }

    // The real map runs against this owned read-only input. Any attempt to enumerate the
    // background dictionary, including construction of a complete copy, fails immediately.
    // It supplies data only; it does not fake the update algorithm or report expected work counts.
    private sealed class NonEnumerableBaseline(IReadOnlyDictionary<uint, string[]> values)
        : IReadOnlyDictionary<uint, string[]>
    {
        public int Count => values.Count;
        public string[] this[uint key] => values[key];
        public IEnumerable<uint> Keys => throw new AssertFailedException("Incremental work enumerated the base keys.");
        public IEnumerable<string[]> Values => throw new AssertFailedException("Incremental work enumerated the base values.");
        public bool ContainsKey(uint key) => values.ContainsKey(key);
        public bool TryGetValue(uint key, out string[] value) => values.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<uint, string[]>> GetEnumerator() =>
            throw new AssertFailedException("Incremental work enumerated the base entries.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
