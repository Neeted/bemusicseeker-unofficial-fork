#nullable enable
using System;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Runtime;

namespace BeMusicSeeker;

/// <summary>製品プロセスが所有する一つの管理一時セッションへの入口です。</summary>
public static class TempDirectoryPublisher
{
    private static readonly TemporaryDirectorySession Session = new(ApplicationPathPolicy.Current.TempDirectory);

    /// <summary>用途未指定の独立した管理一時ディレクトリを作ります。</summary>
    public static string Get() => Session.Get("work");

    /// <summary>指定用途の管理一時ディレクトリを、生存leaseの確立後に返します。終了開始後は取得できません。</summary>
    public static string Get(string? purpose) => Session.Get(purpose);

    /// <summary>現在のプロセスのセッション配下かを判定します。</summary>
    public static bool IsManagedPath(string? path) => Session.IsManagedPath(path);

    /// <summary>現在のプロセスの管理一時パスだけを削除し、通常の削除失敗は警告先へ通知します。</summary>
    public static bool TryDeleteManagedPath(string? path, Action<string>? logInfo = null, Action<string, Exception>? logWarning = null)
        => Session.TryDeleteManagedPath(path, logInfo, logWarning);

    /// <summary>生存していない既知の管理セッションの回収を開始し、実処理の完了を返します。</summary>
    public static Task StartCleanupStaleDirectoriesAsync(Action<string>? logInfo = null, Action<string, Exception>? logWarning = null)
        => Session.StartCleanupStaleDirectoriesAsync(logInfo, logWarning);

    /// <summary>新しい取得を停止し、開始済み回収を待って現在のセッションを解放します。繰返し呼出しは同じ完了を返します。</summary>
    public static Task RemoveAllAsync(Action<string, Exception>? logWarning = null) => Session.RemoveAllAsync(logWarning);
}
