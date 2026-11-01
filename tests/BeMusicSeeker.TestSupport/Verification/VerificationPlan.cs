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
    internal static VerificationPlan Create(string profile, int processors, string? suite = null, string? filter = null)
    {
        int budget = Math.Clamp(processors, 1, 16);
        bool full = profile == "full";
        if (full && filter is not null)
        {
            throw new ArgumentException("正式fullでは --filter を指定できません。局所検証は対象projectを直接実行してください。");
        }
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
        if (filter is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filter);
            modules = modules.Select(module => module with
            {
                Filter = module.Filter is null ? filter : $"({module.Filter})&({filter})",
            }).ToArray();
        }
        return new VerificationPlan(profile, profile is "dev" or "verify" ? "Debug" : "Release",
            profile is "verify" or "full", modules, budget);
    }

    /// <summary>module集合を変えず、suiteと標準MTP filterの文字列を受け取ります。filter自体の文法はMTPへ委譲します。</summary>
    internal static VerificationPlan Parse(string[] args, int processors)
    {
        if (args.Length == 0 || args.Length % 2 == 0)
        {
            throw new ArgumentException("利用方法: <profile> [--suite 対象名] [--filter 標準MTP条件]");
        }
        string? suite = null;
        string? filter = null;
        for (int index = 1; index < args.Length; index += 2)
        {
            switch (args[index])
            {
                case "--suite" when suite is null:
                    suite = args[index + 1];
                    break;
                case "--filter" when filter is null:
                    filter = args[index + 1];
                    break;
                default:
                    throw new ArgumentException($"未知または重複したoptionです: {args[index]}");
            }
        }
        return Create(args[0], processors, suite, filter);
    }

    /// <summary>選択した利用フローの短IDです。SC-07はCompatibilityに所属し、DataRowや全FQNへ展開しません。</summary>
    internal IReadOnlyDictionary<string, IReadOnlyList<string>> RequiredScenarioIds => Profile switch
    {
        "dev" or "verify" => new Dictionary<string, IReadOnlyList<string>> { ["Scenarios"] = ["SC-01", "SC-02", "SC-09"] },
        "full" => new Dictionary<string, IReadOnlyList<string>>
        {
            ["Scenarios"] = ["SC-01", "SC-02", "SC-03", "SC-04", "SC-05", "SC-06", "SC-08", "SC-09", "SC-10", "SC-11", "SC-12", "SC-13", "SC-14"],
            ["Desktop.E2E"] = ["SC-15a", "SC-15b", "SC-16", "DT-01", "DT-02", "DT-03"],
        },
        _ => new Dictionary<string, IReadOnlyList<string>>(),
    };

    /// <summary>全test moduleに一度だけ与える上限です。Fullの600秒速度目標や配布準備時間とは区別します。</summary>
    internal TimeSpan TestTimeout => TimeSpan.FromSeconds(Profile == "full" ? 1380 : 360);

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
