using System.Reflection;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>ライブラリの短期ロックを、呼出元のスレッドが保持していないことを確かめる各実行区分共通の補助です。</summary>
internal static class BmsLibraryLockProbe
{
    /// <summary>別のスレッドから、モデルの短期ロック（譜面・保留・初期化）の排他取得が待たずにできることで、呼出元スレッドが保持していないことを確かめます。</summary>
    internal static bool AreModelLocksFree(BMSLibrary library)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        ReaderWriterLockSlimWrapper[] locks =
        [
            (ReaderWriterLockSlimWrapper)typeof(BMSLibrary).GetProperty("rwlockBMSFiles", flags)!.GetValue(library)!,
            (ReaderWriterLockSlimWrapper)typeof(BMSLibrary).GetField("rwlockPendingInstallCharts", flags)!.GetValue(library)!,
            (ReaderWriterLockSlimWrapper)typeof(BMSLibrary).GetField("rwlockBMSFilesInitializedMin", flags)!.GetValue(library)!
        ];
        return Task.Run(() =>
        {
            foreach (ReaderWriterLockSlimWrapper gate in locks)
            {
                using (gate.GetWriterGuard()) { }
            }
        }).Wait(TimeSpan.FromSeconds(10));
    }
}
