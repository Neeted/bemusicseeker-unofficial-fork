using BeMusicSeeker.Models.Update;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>要求を記録して起動準備・復旧判断の検査へ供給する代替gatewayです。</summary>
internal sealed class RecordingUpdaterProcessGateway : IUpdaterProcessGateway
{
    /// <summary>最後に準備へ渡された型付き起動要求です。</summary>
    internal UpdaterProcessLaunchRequest Request { get; private set; } = null!;

    /// <summary>最後に復旧へ渡された実行ファイルのパスです。</summary>
    internal string RecoveryExecutablePath { get; private set; } = null!;

    /// <summary>最後に復旧へ渡されたアプリ領域です。</summary>
    internal string RecoveryApplicationDirectory { get; private set; } = null!;

    /// <summary>未起動の準備結果を同一実体で返します。</summary>
    internal FakePreparedUpdaterLaunch PreparedLaunch { get; } = new();

    /// <summary>実プロセスを作らず要求を記録し、保持済みの準備結果を返します。</summary>
    public IPreparedUpdaterLaunch Prepare(UpdaterProcessLaunchRequest request)
    {
        Request = request;
        return PreparedLaunch;
    }

    /// <summary>復旧対象を記録し、復旧成立を呼出元へ返します。</summary>
    public bool RecoverIncompleteTransaction(string executablePath, string applicationDirectory)
    {
        RecoveryExecutablePath = executablePath;
        RecoveryApplicationDirectory = applicationDirectory;
        return true;
    }
}

/// <summary>実起動をせず開始回数だけ観測する準備済み要求です。</summary>
internal sealed class FakePreparedUpdaterLaunch : IPreparedUpdaterLaunch
{
    /// <summary>Startが呼ばれた回数です。</summary>
    internal int StartCount { get; private set; }

    /// <summary>開始を一回記録し、空の成功receiptを返します。</summary>
    public UpdaterLaunchReceipt Start()
    {
        StartCount++;
        return new();
    }
}
