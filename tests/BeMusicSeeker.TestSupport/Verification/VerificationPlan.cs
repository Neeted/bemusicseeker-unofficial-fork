using System.Xml.Linq;

namespace BeMusicSeeker.TestSupport.Verification;

/// <summary>発見とケースの分配をMTPに任せる、固定module単位の実行要求です。</summary>
internal sealed record VerificationModule(string Name, string? Filter, int Workers, bool UsesUi)
{
    /// <summary>同じassemblyの分割実行を区別する、出力と結果照合の識別子です。</summary>
    internal string ExecutionId { get; init; } = Name;

    /// <summary>リポジトリ内のtest projectです。</summary>
    internal string ProjectPath => $"tests/BeMusicSeeker.{Name}.Tests/BeMusicSeeker.{Name}.Tests.csproj";

    /// <summary>実行専有の標準MSTest設定を生成します。</summary>
    internal XDocument CreateRunSettings() => new(new XElement("RunSettings", new XElement("MSTest",
        new XElement("Parallelize", new XElement("Workers", Workers), new XElement("Scope", "MethodLevel")))));
}

/// <summary>profileの集合、configuration、資源に基づく実行順だけを定めます。</summary>
internal sealed record VerificationPlan(string Profile, string Configuration, bool Format, IReadOnlyList<VerificationModule> Modules, int Budget)
{
    private static readonly IReadOnlyDictionary<string, TimeSpan> CompatibilitySuites = new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
    {
        ["DriveRoot"] = TimeSpan.FromSeconds(180),
        ["DirectorySymbolicLink"] = TimeSpan.FromSeconds(180),
        ["CrossVolume"] = TimeSpan.FromSeconds(60),
        ["Junction"] = TimeSpan.FromSeconds(60),
        ["install-acl"] = TimeSpan.FromSeconds(60),
        ["everything-native-path-identity"] = TimeSpan.FromSeconds(60),
        ["audio-encoders"] = TimeSpan.FromSeconds(60),
        ["audio-encoder-child"] = TimeSpan.FromSeconds(60),
        ["chart-info-bms"] = TimeSpan.FromSeconds(60),
        ["chart-info-bmson"] = TimeSpan.FromSeconds(60),
        ["chart-info-production"] = TimeSpan.FromSeconds(60),
        ["chart-info-production-slow"] = TimeSpan.FromSeconds(60),
        ["chart-info-production-latest"] = TimeSpan.FromSeconds(60),
        ["chart-info-edge"] = TimeSpan.FromSeconds(60),
        ["chart-info-archive"] = TimeSpan.FromSeconds(60),
        ["chartstring-java"] = TimeSpan.FromSeconds(60),
    };

    private static readonly IReadOnlyDictionary<string, TimeSpan> PerformanceSuites = new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
    {
        ["scan-commit"] = TimeSpan.FromSeconds(180),
        ["lr2-sync"] = TimeSpan.FromSeconds(180),
        ["folder-mutation"] = TimeSpan.FromSeconds(180),
        ["copy-tracking"] = TimeSpan.FromSeconds(60),
        ["bmson-continuation"] = TimeSpan.FromSeconds(60),
        ["row-cache"] = TimeSpan.FromSeconds(180),
        ["playlist-summary"] = TimeSpan.FromSeconds(300),
        ["install-estimation"] = TimeSpan.FromSeconds(600),
        ["audio"] = TimeSpan.FromSeconds(300),
    };

    /// <summary>明示選択されたsuite名です。日常・Fullでは指定しません。</summary>
    internal string? Suite { get; private init; }

    /// <summary>固定profileを解決します。空moduleや環境不足を除外するfallbackは設けません。</summary>
    internal static VerificationPlan Create(string profile, int processors, string? suite = null, string? filter = null)
    {
        int budget = Math.Clamp(processors, 1, 16);
        bool full = profile == "full";
        if (profile == "compatibility" && (suite is null || !CompatibilitySuites.ContainsKey(suite)))
        {
            throw new ArgumentException($"--suite に登録済みのCompatibility suite名を指定してください: {suite}");
        }
        if (profile == "performance" && (suite is null || !PerformanceSuites.ContainsKey(suite)))
        {
            throw new ArgumentException($"--suite に登録済みのPerformance suite名を指定してください: {suite}");
        }
        if (profile == "performance" && filter is not null)
        {
            throw new ArgumentException("正式performanceでは --filter を指定できません。局所検証は対象projectを直接実行してください。");
        }
        if (full && filter is not null)
        {
            throw new ArgumentException("正式fullでは --filter を指定できません。局所検証は対象projectを直接実行してください。");
        }
        VerificationModule[] modules = profile switch
        {
            "dev" or "verify" or "full" => [
                new("Core", null, Math.Max(1, budget - Math.Clamp(budget / 4, 1, 3) - 1), false),
                new("Windows", "TestCategory=Windows.Settings", 1, true) { ExecutionId = "Windows.Settings" },
                new("Windows", "TestCategory=Windows.Shell", 1, true) { ExecutionId = "Windows.Shell" },
                new("Windows", "(TestCategory!=Windows.Settings)&(TestCategory!=Windows.Shell)", 1, true) { ExecutionId = "Windows.Remaining" },
                new("Scenarios", full ? null : "TestCategory=Daily", 1, true)],
            "compatibility" => [new("Compatibility", SuiteFilter(suite), 1, true)],
            "performance" => [new("Performance", SuiteFilter(suite), 1, true)],
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
        return new VerificationPlan(profile, "Release",
            profile is "verify" or "full", modules, budget)
        { Suite = suite };
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
            ["Scenarios"] = ["SC-01", "SC-02", "SC-09", "SC-10A", "SC-10B", "SC-11", "SC-13", "SC-14", "SC-ExistingData", "SC-CurrentUpdate"],
        },
        _ => new Dictionary<string, IReadOnlyList<string>>(),
    };

    /// <summary>全test moduleに一度だけ与える上限です。明示suiteは起動・前提検査・通常回収まで共有し、速度の合否閾値とは区別します。</summary>
    internal TimeSpan TestTimeout => Profile switch
    {
        "compatibility" => CompatibilitySuites[Suite ?? throw new InvalidOperationException("Compatibility suiteが未指定です。")],
        "performance" => PerformanceSuites[Suite ?? throw new InvalidOperationException("Performance suiteが未指定です。")],
        _ => TimeSpan.FromSeconds(Profile == "full" ? 1380 : 600),
    };

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
