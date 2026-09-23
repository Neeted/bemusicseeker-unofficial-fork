using System;
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

    internal static PlaylistUrlCompletionOptionsSnapshot CreateCurrent(Settings settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        return new PlaylistUrlCompletionOptionsSnapshot
        {
            EnablePlaylistUrlCompletion = settings.EnablePlaylistUrlCompletion,
            PlaylistMd5UrlMappingTsvUri = settings.PlaylistMd5UrlMappingTsvUri,
            EnableStellaFullPlaylistUrlCompletion = settings.EnableStellaFullPlaylistUrlCompletion,
            OverwritePlaylistUrlsWithCompletion = settings.OverwritePlaylistUrlsWithCompletion
        };
    }
}
