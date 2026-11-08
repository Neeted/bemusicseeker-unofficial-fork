using System.Reflection;
using System.Runtime.CompilerServices;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>
/// コンストラクターを通さず、ワークフローの同一性だけを使うライブラリを作ります。
/// モデルは窓口を必須にしているため、通知の窓口だけは予期しない呼出しを例外にする実装で明示的に接続します。
/// </summary>
internal static class UninitializedBmsLibrary
{
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>コンストラクター未実行のライブラリへ、通知の窓口だけを接続して返します。</summary>
    internal static BMSLibrary Create()
    {
        var library = (BMSLibrary)RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary));
        var dialogs = new UnexpectedBmsLibraryDialogService();
        Set(library, "dialogService", dialogs);
        Set(library, "scopedOperationDialogService", new ScopedOperationDialogCoordinator(dialogs));
        return library;
    }

    private static void Set(BMSLibrary library, string name, object value)
        => (typeof(BMSLibrary).GetField(name, InstanceFields)
            ?? throw new MissingFieldException(typeof(BMSLibrary).FullName, name)).SetValue(library, value);
}
