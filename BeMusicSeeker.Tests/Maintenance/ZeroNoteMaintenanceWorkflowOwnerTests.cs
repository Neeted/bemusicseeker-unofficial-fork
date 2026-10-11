using System;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ZeroNoteMaintenanceWorkflowOwnerTests
{
    /// <summary>
    /// ライブラリ未接続の再確認はfalseで終え、保守callbackを呼ばず、対象のない実行を開始する誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task RecheckAsync_WithoutAttachedLibraryIsNoOp()
    {
        var synchronizer = new ChartFileOperationSynchronizer();
        int recheckCount = 0;
        var owner = new ZeroNoteMaintenanceWorkflowOwner(
            () => null!,
            synchronizer,
            (_, capability) => recheckCount++);

        bool rechecked = await owner.RecheckAsync();

        Assert.IsFalse(rechecked);
        Assert.AreEqual(0, recheckCount);
    }

    /// <summary>
    /// 共通譜面排他の保持中は同期再確認を未開始で拒否し、解放後の新規要求で現在ライブラリを一回処理して、競合中の実行や永久拒否を検出する。
    /// </summary>
    [TestMethod]
    public void Recheck_FailsFastWhileSharedChartFileGateIsBusyThenRunsAfterRelease()
    {
        var synchronizer = new ChartFileOperationSynchronizer();
        var library = (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary));
        int recheckCount = 0;
        var owner = new ZeroNoteMaintenanceWorkflowOwner(
            () => library,
            synchronizer,
            (actualLibrary, _) =>
            {
                Assert.AreSame(library, actualLibrary);
                Interlocked.Increment(ref recheckCount);
            });
        Assert.IsTrue(synchronizer.TryEnter(out IDisposable incumbent));
        try
        {
            Assert.IsFalse(owner.Recheck());
            Assert.AreEqual(0, Volatile.Read(ref recheckCount));
        }
        finally
        {
            incumbent.Dispose();
        }

        Assert.IsTrue(owner.Recheck());
        Assert.AreEqual(1, Volatile.Read(ref recheckCount));
    }

    /// <summary>
    /// 受理した再確認の保守callbackが失敗すると元例外を非同期入口へ伝え、成功への読み替えを検出する。
    /// </summary>
    [TestMethod]
    public async Task RecheckAsync_PropagatesLibraryFailure()
    {
        var library = (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary));
        var owner = new ZeroNoteMaintenanceWorkflowOwner(
            () => library,
            new ChartFileOperationSynchronizer(),
            (_, capability) => throw new InvalidOperationException("recheck failed"));

        InvalidOperationException exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => owner.RecheckAsync());

        Assert.AreEqual("recheck failed", exception.Message);
    }
}
