namespace BeMusicSeeker.Models.Update;

/// <summary>更新ZIPの取得進捗です。</summary>
/// <param name="ReceivedBytes">受信済みのバイト数です。</param>
/// <param name="TotalBytes">合計のバイト数です。資産の <see cref="UpdateAssetInfo.SizeBytes"/> を表します。</param>
internal readonly record struct UpdateDownloadProgress(long ReceivedBytes, long TotalBytes);
