using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// フォルダ統合の確認画面に出す、統合元の配下にあるフォルダの件数を数えます。
/// 統合は配下を相対配置のまま移すため、利用者が操作前に影響の大きさを知れるようにします。
/// </summary>
internal static class NestedFolderCounter
{
    /// <summary>
    /// 指定フォルダの配下（全階層）にあるフォルダの件数を、背景スレッドで数えて返します。
    /// 隠しフォルダ・システムフォルダも数え、ファイルは数えません。再解析ポイント（ジャンクションなど）はそれ自体を一件と数え、その先はたどりません。
    /// アクセスが拒否されたフォルダは中を数えずに続け、起点のアクセスが拒否された場合も例外にせず0を返します（確認画面には配下の案内を添えません）。
    /// 起点が存在しない場合や、列挙中に配下のフォルダが消えた場合など、アクセス拒否以外の失敗は例外を返します。
    /// </summary>
    /// <param name="directory">統合元のフォルダ。</param>
    /// <returns>配下のフォルダの件数。</returns>
    internal static Task<int> CountAsync(string directory)
    {
        return Task.Run(() => Count(directory));
    }

    private static int Count(string directory)
    {
        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };
        int count = 0;
        var pending = new Stack<string>();
        pending.Push(LongPathFileSystem.ToExtendedPath(directory));
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            IEnumerable<(string Path, bool IsReparsePoint)> children = new FileSystemEnumerable<(string, bool)>(
                current,
                static (ref FileSystemEntry entry) => (entry.ToFullPath(), (entry.Attributes & FileAttributes.ReparsePoint) != 0),
                options)
            {
                ShouldIncludePredicate = static (ref FileSystemEntry entry) => entry.IsDirectory,
            };
            foreach ((string path, bool isReparsePoint) in children)
            {
                count++;
                if (!isReparsePoint)
                {
                    pending.Push(path);
                }
            }
        }
        return count;
    }
}
