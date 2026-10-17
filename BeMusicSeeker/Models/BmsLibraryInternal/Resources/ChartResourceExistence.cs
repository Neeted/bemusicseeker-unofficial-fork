using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>健康度と譜面プロパティの物理ファイル照合を共有します。</summary>
internal static class ChartResourceExistence
{
    /// <summary>健康度と明示的な診断で、拡張子を除いた同じキーと種別別代替拡張子を使って存在を確認します。既存の判定不能はfalseとして扱います。</summary>
    internal static bool ExistsWithCompatibleExtensions(string chartDirectory, string file, IEnumerable<string> extensions)
    {
        if (string.IsNullOrWhiteSpace(chartDirectory) || string.IsNullOrWhiteSpace(file))
        {
            return false;
        }
        try
        {
            string fullPath = LongPathFileSystem.NormalizePathForStorage(Path.Combine(chartDirectory, file));
            string directory = Path.GetDirectoryName(fullPath) + Path.DirectorySeparatorChar;
            string basename = Path.GetFileName(fullPath);
            return LongPathFileSystem.FileExists(fullPath)
                || (extensions ?? []).Any(ext => !string.IsNullOrWhiteSpace(ext) && LongPathFileSystem.FileExists(directory + basename + ext));
        }
        catch
        {
            return false;
        }
    }

}
