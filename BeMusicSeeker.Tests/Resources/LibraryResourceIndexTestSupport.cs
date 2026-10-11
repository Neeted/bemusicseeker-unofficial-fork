using System.Reflection;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

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
}
