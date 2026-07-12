using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models;

/// <summary>
/// プレイリスト URL 補完 workflow が一回の操作で参照する設定値を保持します。
/// </summary>
internal sealed class PlaylistUrlCompletionOptionsSnapshot
{
    public bool EnablePlaylistUrlCompletion { get; init; }

    public string PlaylistMd5UrlMappingTsvUri { get; init; }

    public bool EnableStellaFullPlaylistUrlCompletion { get; init; }

    public bool OverwritePlaylistUrlsWithCompletion { get; init; }

    internal static PlaylistUrlCompletionOptionsSnapshot CreateCurrent()
    {
        return new PlaylistUrlCompletionOptionsSnapshot
        {
            EnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion,
            PlaylistMd5UrlMappingTsvUri = Settings.Default.PlaylistMd5UrlMappingTsvUri,
            EnableStellaFullPlaylistUrlCompletion = Settings.Default.EnableStellaFullPlaylistUrlCompletion,
            OverwritePlaylistUrlsWithCompletion = Settings.Default.OverwritePlaylistUrlsWithCompletion
        };
    }
}
