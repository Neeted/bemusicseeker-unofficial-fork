using System.Text;

namespace BeMusicSeeker.TestSupport.Files;

/// <summary>別プロセスが一時名からの名前変更で公開した完成ファイルを、公開直後でも一回で読み取ります。</summary>
/// <remarks>
/// Windowsでは完成名が見えた後も、名前変更に使った <c>DELETE</c> 権限のハンドルが閉じるまで残る区間があります。
/// この区間に削除の共有を許さない開き方（<see cref="File.ReadAllText(string)"/> など）をすると共有違反になるため、
/// 読取りは <see cref="FileShare.Read"/> と <see cref="FileShare.Delete"/> を許可します。書込みの共有は許可しません。
/// 内容を閉じてから名前を変える公開だけが対象です。完成名へ直接書くファイルは、書込み側の終端を待ってから読みます。
/// </remarks>
public static class PublishedFile
{
    /// <summary>名前変更と共有できる読取り専用のストリームを開きます。呼出し側が破棄します。</summary>
    /// <param name="path">完成名のパス。存在の確認は呼出し側が済ませます。</param>
    public static FileStream OpenRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    /// <summary>内容の全体を文字列で返します。文字コードの判定は <see cref="File.ReadAllText(string)"/> と同じ（BOMの検出、既定はUTF-8）です。</summary>
    /// <param name="path">完成名のパス。存在の確認は呼出し側が済ませます。</param>
    public static string ReadAllText(string path)
    {
        using FileStream stream = OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
