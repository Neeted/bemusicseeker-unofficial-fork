namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Score Viewer 登録のために必要な最小情報を保持します。
/// 実ファイル未所持の playlist 行では hash のみを持ちます。
/// </summary>
internal sealed class ScoreViewerTarget
{
    internal string Hash { get; }

    internal string Path { get; }

    internal string Title { get; }

    internal ScoreViewerTarget(string hash, string path, string title)
    {
        Hash = hash;
        Path = path;
        Title = title;
    }
}
