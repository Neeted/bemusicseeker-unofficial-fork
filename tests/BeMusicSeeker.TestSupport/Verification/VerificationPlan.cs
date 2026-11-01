using System.Xml.Linq;

namespace BeMusicSeeker.TestSupport.Verification;

/// <summary>発見とケースの分配をMTPに任せる、固定module単位の実行要求です。</summary>
internal sealed record VerificationModule(string Name, string? Filter, int Workers, bool UsesUi)
{
    /// <summary>リポジトリ内のtest projectです。</summary>
    internal string ProjectPath => $"tests/BeMusicSeeker.{Name}.Tests/BeMusicSeeker.{Name}.Tests.csproj";

    /// <summary>実行専有の標準MSTest設定を生成します。</summary>
    internal XDocument CreateRunSettings() => new(new XElement("RunSettings", new XElement("MSTest",
        new XElement("Parallelize", new XElement("Workers", Workers), new XElement("Scope", "MethodLevel")))));
}

/// <summary>profileの集合、configuration、資源に基づく実行順だけを定めます。</summary>
internal sealed record VerificationPlan(string Profile, string Configuration, bool Format, IReadOnlyList<VerificationModule> Modules, int Budget)
{
    /// <summary>固定profileを解決します。空moduleや環境不足を除外するfallbackは設けません。</summary>
    internal static VerificationPlan Create(string profile, int processors, string? suite = null)
    {
        int budget = Math.Clamp(processors, 1, 16);
        bool full = profile == "full";
        VerificationModule[] modules = profile switch
        {
            "dev" or "verify" or "full" => [
                new("Core", null, Math.Max(1, budget - 1), false),
                new("Windows", null, 1, true),
                new("Scenarios", full ? null : "TestCategory=Daily", 1, true),
                .. full ? new[] { new VerificationModule("Desktop.E2E", null, 1, true) } : []],
            "compatibility" => [new("Compatibility", SuiteFilter(suite), 1, false)],
            "performance" => [new("Performance", SuiteFilter(suite), 1, false)],
            _ => throw new ArgumentException($"未知のprofileです: {profile}"),
        };
        if (suite is not null && profile is not ("compatibility" or "performance"))
        {
            throw new ArgumentException("--suite は compatibility/performance で指定します。");
        }
        return new VerificationPlan(profile, profile is "dev" or "verify" ? "Debug" : "Release",
            profile is "verify" or "full", modules, budget);
    }

    /// <summary>標準solution filterの選択先です。実行中のVerify自身は含めません。</summary>
    internal string SolutionFilter => $"tests/verification-{(Profile is "dev" or "verify" ? "dev" : Profile)}.slnf";

    private static string SuiteFilter(string? suite)
    {
        if (string.IsNullOrWhiteSpace(suite) || suite.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("--suite に対象suite名を指定してください。");
        }
        return $"TestCategory=Suite.{suite}";
    }
}
