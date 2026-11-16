using System.Text;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数の実行区分で同じ準備・観測を共有します。</summary>
internal static class ResilientFileMutationServiceTestSupport
{
    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal static readonly FileMutationOptions targetOnlyFileMutationOptions = new(ReadOnlyNormalizationScope.TargetOnly);

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal static readonly FileMutationOptions recursiveDirectoryTreeFileMutationOptions = new(ReadOnlyNormalizationScope.RecursiveDirectoryTree);

    /// <summary>元の最小長と各segmentの長さを保ち、専用root内に長パス条件を作ります。</summary>
    internal static string BuildLongDirectoryPath(string tempDirectoryPath, string leafName)
    {
        string path = tempDirectoryPath;
        for (int i = 0; path.Length < 285; i++)
        {
            path = Path.Combine(path, "segment_" + i.ToString("00") + "_" + new string('a', 32));
        }
        return Path.Combine(path, leafName);
    }

    /// <summary>長パスに対応する正式なファイル入口からUTF-8入力を書き込みます。</summary>
    internal static void WriteAllText(string path, string contents)
    {
        using FileStream stream = LongPathFileSystem.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(contents);
    }

    /// <summary>長パスに対応する正式なファイル入口から入力内容を読み取ります。</summary>
    internal static string ReadAllText(string path)
    {
        using FileStream stream = LongPathFileSystem.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

}
