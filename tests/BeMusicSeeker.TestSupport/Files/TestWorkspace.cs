namespace BeMusicSeeker.TestSupport.Files;

/// <summary>一回のケースが変更するファイル領域だけを所有します。</summary>
public sealed class TestWorkspace : IDisposable
{
    private bool preserve;

    private TestWorkspace(string runRoot)
    {
        CaseId = Guid.NewGuid().ToString("N")[..12];
        RootDirectory = Path.Combine(Path.GetFullPath(runRoot), CaseId);
        Directory.CreateDirectory(RootDirectory);
    }

    /// <summary>表示名やworker番号から独立した短いケース識別子です。</summary>
    public string CaseId { get; }

    /// <summary>このケースだけが変更するディレクトリです。</summary>
    public string RootDirectory { get; }

    /// <summary>指定した実行領域の下に新しいケース領域を作ります。</summary>
    public static TestWorkspace Create(string runRoot) => new(runRoot);

    /// <summary>ケース内の相対パスを絶対パスへ解決します。rooted入力と、正規化後に所有領域外となる入力は拒否します。</summary>
    public string GetPath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("ケース内の相対パスを指定してください。", nameof(relativePath));
        }
        string resolved = Path.GetFullPath(Path.Combine(RootDirectory, relativePath));
        if (!string.Equals(resolved, RootDirectory, StringComparison.OrdinalIgnoreCase)
            && !resolved.StartsWith(RootDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("パスがケースの所有領域外を指しています。", nameof(relativePath));
        }
        return resolved;
    }

    /// <summary>診断用にこの領域を残します。DB・Window・プロセスの終端責任は変更しません。</summary>
    public void PreserveArtifacts() => preserve = true;

    /// <summary>他の資源を終端した後に呼び、自分の領域だけを削除します。削除失敗は呼出元へ伝えます。</summary>
    public void Dispose()
    {
        if (!preserve && Directory.Exists(RootDirectory))
        {
            Directory.Delete(RootDirectory, recursive: true);
        }
    }
}

/// <summary>直接実行とrunner経由で共用する、プロセス寿命の変更不能な実行領域です。</summary>
public static class TestRunDirectory
{
    /// <summary>runnerが子プロセスにだけ渡す実行領域の環境変数名です。</summary>
    public const string EnvironmentVariable = "BMS_TEST_RUN_ROOT";

    /// <summary>runnerの専有領域、または直接実行用に新規作成した領域です。</summary>
    public static string RootDirectory { get; } = CreateRoot();

    private static string CreateRoot()
    {
        string root = Environment.GetEnvironmentVariable(EnvironmentVariable)
            ?? Path.Combine(Path.GetTempPath(), "bms-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        return Path.GetFullPath(root);
    }
}
