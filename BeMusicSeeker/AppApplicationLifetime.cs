using System.Collections.Generic;
using BeMusicSeeker.Models;

namespace BeMusicSeeker;

internal sealed class AppApplicationLifetime : IApplicationLifetimePort
{
    private readonly App application;

    internal AppApplicationLifetime(App application)
    {
        this.application = application ?? throw new System.ArgumentNullException(nameof(application));
    }

    public bool IsFirstStartup => application.firstStartup;

    public void CompleteFirstStartup() => application.firstStartup = false;

    public void MarkCoordinatedShutdownStarted(string reason)
        => App.MarkCoordinatedShutdownStarted(reason);

    /// <summary>既存の業務終端後に、明示検証時だけ制御通信の回収を挟んでWPFを終了します。</summary>
    public void RequestShutdown() => application.RequestApplicationShutdown();

    /// <summary>アプリ全体が所有する一時ディレクトリの回収完了を返します。</summary>
    public System.Threading.Tasks.Task CompleteTemporaryDirectoriesAsync(System.Action<string, System.Exception> logWarning)
        => TempDirectoryPublisher.RemoveAllAsync(logWarning);

    /// <summary>通常終了の後片付けを通さず、保全物を残した最終終了へ接続します。</summary>
    public void ExitForUnrestoredDestination(System.Exception failure)
        => application.ExitForUnrestoredDestination(failure);

    /// <summary>
    /// 終端 cleanup 後に後継 process の起動だけを要求します。
    /// </summary>
    public System.Threading.Tasks.Task RestartApplicationAsync()
        => application.RestartApplicationAsync();
}

internal sealed class AppCultureCatalog : ICultureCatalog
{
    private readonly App application;

    internal AppCultureCatalog(App application)
    {
        this.application = application ?? throw new System.ArgumentNullException(nameof(application));
    }

    public IReadOnlyDictionary<string, string> Cultures
        => App.AvailableCultures;
}
