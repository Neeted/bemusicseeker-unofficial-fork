using System;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>保留復元または明示一覧初期化がDB確定前に失敗し、旧登録と既存一覧を保ったことを示す短命な失敗です。権限・終了取消・確定後公開の失敗には使いません。</summary>
internal sealed class PendingPackageRestoreException : Exception
{
    /// <summary>選択提示と既存診断へ渡す元の読取・DB原因を保持します。</summary>
    internal PendingPackageRestoreException(Exception cause)
        : base(Resources.Error_PendingRestoreUncommitted, cause)
    {
    }
}
