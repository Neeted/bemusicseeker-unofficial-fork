using BeMusicSeeker.TestSupport.Verification;

namespace BeMusicSeeker.TestSupport.Scenarios;

/// <summary>通常シナリオの実行体と起動前入力が、同じ候補または日常buildの資産を参照するためのパス選択です。</summary>
internal static class ScenarioApplicationPaths
{
    /// <summary>Fullが検査済みCurrent ZIPを展開した、run専有installationの絶対パスです。</summary>
    internal const string EnvironmentVariable = "BMS_TEST_APPLICATION_ROOT";

    /// <summary>候補指定時は明示installation、日常・局所では同じbuildの製品fixtureを参照します。</summary>
    internal static string RootDirectory => ResolveRoot(
        Environment.GetEnvironmentVariable(DistributionCatalog.EnvironmentVariable),
        Environment.GetEnvironmentVariable(EnvironmentVariable),
        Path.Combine(AppContext.BaseDirectory, "fixtures", "application"));

    /// <summary>通常シナリオで起動する製品の絶対パスです。明示exeを使う更新・故障fixtureは置換しません。</summary>
    internal static string ExecutablePath => Path.Combine(RootDirectory, "BeMusicSeeker.exe");

    /// <summary>実行体と同じinstallationに同梱された言語辞書です。</summary>
    internal static string LanguageDirectory => Path.Combine(RootDirectory, "lang");

    /// <summary>候補の片方だけの指定を拒否し、欠落を日常buildへ読み替えません。環境を変更せず呼出元の入力だけを解決します。</summary>
    internal static string ResolveRoot(string? catalogPath, string? applicationRoot, string buildRoot)
    {
        if (catalogPath is null && applicationRoot is null) { return Path.GetFullPath(buildRoot); }
        if (string.IsNullOrWhiteSpace(catalogPath) || !Path.IsPathFullyQualified(catalogPath)
            || string.IsNullOrWhiteSpace(applicationRoot) || !Path.IsPathFullyQualified(applicationRoot))
        {
            throw new InvalidOperationException("候補の通常起動には配布catalogとその展開rootを絶対パスで指定してください。");
        }
        return Path.GetFullPath(applicationRoot);
    }
}
