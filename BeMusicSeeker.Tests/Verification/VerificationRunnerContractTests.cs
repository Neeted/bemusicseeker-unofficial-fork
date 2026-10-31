using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[TestCategory("ProcessIntegration")]
public sealed class VerificationRunnerContractTests
{
    /// <summary>
    /// 実DLLの通常対象を過不足なく割り当て、順序変更・無関係な追加削除・別プロセスで既存所属が変わる誤りを検出する。
    /// </summary>
    /// <remarks>本番の計画・検査・runsettings生成を通し、対象集合は計画の自己申告ではなく先に取得したメタデータから求める。</remarks>
    [TestMethod]
    public void FunctionalShardPlanKeepsExactCoverageAndStableMembership()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker-FunctionalPlanTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using JsonDocument first = ReadFunctionalPlanProbe(root, "variations");
            string[] excludedCategories = ["Net10Performance", "Performance", "LargeFixture", "ParserCompatibilityFull", "ParserCompatibilitySlow", "ProductionDiffFull", "ProcessIntegration", "ReleaseAcceptance", "EnvironmentIntegration"];
            JsonElement[] selectedMetadata = GetProperty(first.RootElement, "Metadata").EnumerateArray()
                .Where(item => !GetProperty(item, "Categories").EnumerateArray().Select(GetRequiredString).Intersect(excludedCategories).Any()).ToArray();
            string[] expectedNames = selectedMetadata
                .Where(item => !GetProperty(item, "DoNotParallelize").GetBoolean()
                    && GetRequiredString(GetProperty(item, "ClassName")) is not
                        ("BeMusicSeeker.Tests.PlayerPanelStateSettingsCompatibilityTests" or "BeMusicSeeker.Tests.BassCollectibleLoadContextTests"))
                .Select(item => GetRequiredString(GetProperty(item, "FullyQualifiedName")))
                .ToArray();
            JsonElement plans = GetProperty(first.RootElement, "Plans");
            Dictionary<string, string> baseline = ReadParallelMembership(plans[0], expectedNames);
            CollectionAssert.AreEquivalent(selectedMetadata.Select(item => GetRequiredString(GetProperty(item, "FullyQualifiedName"))).ToArray(),
                GetProperty(plans[0], "Shards").EnumerateArray().SelectMany(shard => GetProperty(shard, "TestNames").EnumerateArray().Select(GetRequiredString)).ToArray());
            foreach (JsonElement plan in plans.EnumerateArray())
            {
                Assert.IsFalse(GetProperty(plan, "Shards").EnumerateArray()
                    .SelectMany(shard => GetProperty(shard, "TestNames").EnumerateArray().Select(GetRequiredString))
                    .Any(name => name.StartsWith("Synthetic.Excluded.", StringComparison.Ordinal)));
            }
            AssertSameMembership(baseline, ReadParallelMembership(plans[1], expectedNames));

            string addedName = GetRequiredString(GetProperty(first.RootElement, "AddedName"));
            Assert.IsTrue(StringComparer.Ordinal.Compare(addedName, expectedNames.Min(StringComparer.Ordinal)) < 0);
            Dictionary<string, string> added = ReadParallelMembership(plans[2], expectedNames.Append(addedName).ToArray());
            AssertSameMembership(baseline, added);
            string removedName = GetRequiredString(GetProperty(first.RootElement, "RemovedName"));
            Dictionary<string, string> removed = ReadParallelMembership(plans[3], expectedNames.Where(name => name != removedName).ToArray());
            AssertSameMembership(removed, baseline);

            using JsonDocument secondProcess = ReadFunctionalPlanProbe(root, "baseline");
            AssertSameMembership(baseline, ReadParallelMembership(GetProperty(secondProcess.RootElement, "Plans")[0], expectedNames));

            string[] hostNames = ["portable-settings", "bass-collectible", "shared-state-a", "shared-state-b", "parallel-a", "parallel-b"];
            CollectionAssert.AreEqual(hostNames, GetProperty(plans[0], "Shards").EnumerateArray()
                .Select(shard => GetRequiredString(GetProperty(shard, "Name"))).ToArray());
            const string categoryFilter = "TestCategory!=Net10Performance&TestCategory!=Performance&TestCategory!=LargeFixture&TestCategory!=ParserCompatibilityFull&TestCategory!=ParserCompatibilitySlow&TestCategory!=ProductionDiffFull&TestCategory!=ProcessIntegration&TestCategory!=ReleaseAcceptance&TestCategory!=EnvironmentIntegration";
            foreach (JsonElement shard in GetProperty(plans[0], "Shards").EnumerateArray())
            {
                string hostName = GetRequiredString(GetProperty(shard, "Name"));
                var settings = XElement.Load(Path.Combine(root, hostName + ".runsettings"));
                XElement parallelize = GetRequiredElement(GetRequiredElement(settings, "MSTest"), "Parallelize");
                Assert.AreEqual(hostName.StartsWith("parallel-", StringComparison.Ordinal) ? Math.Max(1, Environment.ProcessorCount) : 1,
                    (int)GetRequiredElement(parallelize, "Workers"));
                Assert.AreEqual("MethodLevel", GetRequiredElement(parallelize, "Scope").Value);
                string filter = GetRequiredElement(GetRequiredElement(settings, "RunConfiguration"), "TestCaseFilter").Value;
                string prefix = "(" + categoryFilter + ")&(";
                Assert.IsTrue(filter.StartsWith(prefix, StringComparison.Ordinal) && filter.EndsWith(')'));
                string[] includes = filter[prefix.Length..^1].Split('|');
                Assert.IsTrue(includes.All(include => include.StartsWith("FullyQualifiedName=", StringComparison.Ordinal)));
                CollectionAssert.AreEquivalent(GetProperty(shard, "TestNames").EnumerateArray().Select(GetRequiredString).ToArray(),
                    includes.Select(include => include["FullyQualifiedName=".Length..]).ToArray());
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>通常対象一件では両側非空を作れず、対象を複製したり空側を許して成功にする誤りを検出する。</summary>
    [TestMethod]
    public void FunctionalShardPlanRejectsSingleRegularMethod()
    {
        using JsonDocument result = ReadFunctionalPlanProbe(Path.GetTempPath(), "single");
        Assert.IsTrue(GetProperty(result.RootElement, "Rejected").GetBoolean(), result.RootElement.GetRawText());
        StringAssert.Contains(GetRequiredString(GetProperty(result.RootElement, "Message")), "two nonempty parallel queues");
    }

    /// <summary>実PEのclass/methodカテゴリを和集合で読み取り、型初期化やテスト実行なしで重複を除去します。</summary>
    [TestMethod]
    public void MetadataReaderUnionsClassAndMethodCategoriesWithoutExecutingAssembly()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker-MetadataCategories", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string command = $$"""
                $ErrorActionPreference = 'Stop'
                . {{QuotePowerShellLiteral(Path.Combine(FindRepositoryRoot(), "scripts", "verification-test-discovery.ps1"))}}
                $source = @'
                using System;
                namespace Microsoft.VisualStudio.TestTools.UnitTesting {
                    public sealed class TestClassAttribute : Attribute { }
                    public sealed class TestMethodAttribute : Attribute { }
                    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
                    public sealed class TestCategoryAttribute : Attribute { public TestCategoryAttribute(string value) { } }
                }
                [Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]
                [Microsoft.VisualStudio.TestTools.UnitTesting.TestCategory("ClassOnly")]
                [Microsoft.VisualStudio.TestTools.UnitTesting.TestCategory("Shared")]
                public sealed class CategoryFixture {
                    static CategoryFixture() { throw new Exception("This assembly must not execute."); }
                    [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
                    [Microsoft.VisualStudio.TestTools.UnitTesting.TestCategory("MethodOnly")]
                    [Microsoft.VisualStudio.TestTools.UnitTesting.TestCategory("Shared")]
                    public void Example() { throw new Exception("This test must not execute."); }
                }
                '@
                Add-Type -TypeDefinition $source -OutputAssembly {{QuotePowerShellLiteral(Path.Combine(root, "fixture.dll"))}}
                @(Get-VerificationTestMetadata -AssemblyPath {{QuotePowerShellLiteral(Path.Combine(root, "fixture.dll"))}}) | ConvertTo-Json -Depth 5 -Compress
                """;
            using JsonDocument result = ReadPowerShellJson(["-Command", command]);
            Assert.AreEqual("CategoryFixture.Example", GetRequiredString(GetProperty(result.RootElement, "FullyQualifiedName")));
            CollectionAssert.AreEquivalent(new[] { "ClassOnly", "MethodOnly", "Shared" },
                GetProperty(result.RootElement, "Categories").EnumerateArray().Select(GetRequiredString).ToArray());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>選択methodは一件以上の全Passed行を要求し、空・欠落・未知・不正TRX・run-level失敗で成功にしません。</summary>
    /// <remarks>DataRowは事前展開せず、同じmethodの複数行の実結果を共有parserとgateで照合します。</remarks>
    [TestMethod]
    public void SelectedOutcomeGateRequiresAllRowsAndValidCompleteReceipts()
    {
        const string first = "Synthetic.Selected.First";
        const string second = "Synthetic.Selected.Second";
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker-SelectedOutcomes", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "results.trx");
            WriteSyntheticTrx(path, (first, "Passed"), (first, "Passed"), (second, "Passed"));
            using (JsonDocument passed = ReadSelectedOutcomeProbe(path, first, second))
            { Assert.IsTrue(GetProperty(passed.RootElement, "Passed").GetBoolean(), passed.RootElement.GetRawText()); }
            foreach (string outcome in new[] { "Skipped", "Inconclusive", "NotExecuted", "Failed", "Error", "Aborted", "" })
            {
                WriteSyntheticTrx(path, (first, "Passed"), (first, outcome), (second, "Passed"));
                using JsonDocument failed = ReadSelectedOutcomeProbe(path, first, second);
                AssertGateFailure(failed, "not Passed");
            }
            WriteSyntheticTrx(path, (first, "Passed"));
            using (JsonDocument missing = ReadSelectedOutcomeProbe(path, first, second)) { AssertGateFailure(missing, "missing"); }
            WriteSyntheticTrx(path, (first, "Passed"), (second, "Passed"), ("Synthetic.Unknown.Method", "Passed"));
            using (JsonDocument unknown = ReadSelectedOutcomeProbe(path, first, second)) { AssertGateFailure(unknown, "Unknown"); }
            WriteSyntheticTrx(path);
            using (JsonDocument empty = ReadSelectedOutcomeProbe(path, first, second)) { AssertGateFailure(empty, "missing"); }
            foreach (string runOutcome in new[] { "Failed", "Aborted", "Error", "Inconclusive" })
            {
                WriteSyntheticTrx(path, (first, "Passed"), (second, "Passed"));
                var trx = XDocument.Load(path);
                GetRequiredElement(trx.Root ?? throw new AssertFailedException(), "ResultSummary").SetAttributeValue("outcome", runOutcome);
                trx.Save(path);
                using JsonDocument failed = ReadSelectedOutcomeProbe(path, first, second);
                AssertGateFailure(failed, "run-level");
            }
            foreach (string invalid in new[] { "not XML", "<Other />", "<TestRun><Results /></TestRun>" })
            {
                File.WriteAllText(path, invalid);
                using JsonDocument failed = ReadSelectedOutcomeProbe(path, first, second);
                AssertGateFailure(failed);
            }
            foreach (bool missingDefinition in new[] { false, true })
            {
                WriteSyntheticTrx(path, (first, "Passed"), (second, "Passed"));
                var trx = XDocument.Load(path);
                XElement definitions = GetRequiredElement(trx.Root ?? throw new AssertFailedException(), "TestDefinitions");
                if (missingDefinition) { definitions.Remove(); }
                else
                {
                    XElement definition = definitions.Elements("UnitTest").First();
                    definition.SetAttributeValue("name", first);
                    GetRequiredElement(definition, "TestMethod").Remove();
                }
                trx.Save(path);
                using JsonDocument failed = ReadSelectedOutcomeProbe(path, first, second);
                AssertGateFailure(failed, "missing");
            }
            File.Delete(path);
            using JsonDocument absent = ReadSelectedOutcomeProbe(path, first, second);
            AssertGateFailure(absent, "missing");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>環境許可をFullまたはfilter付きQuickだけで受け付け、通常QuickのOR条件全体へ専用カテゴリ除外を結合します。</summary>
    /// <remarks>typed probeで本番の入口判断・filter生成を読み込み、専用テストを起動せず許可表を検査します。</remarks>
    [TestMethod]
    public void EnvironmentAuthorizationAndQuickFilterRequireExplicitScope()
    {
        string body = """
            $observations = @()
            foreach ($mode in @('Quick', 'Functional', 'Full')) {
                foreach ($filtered in @($false, $true)) {
                    foreach ($allow in @($false, $true)) {
                        $filter = if ($filtered) { 'FullyQualifiedName=A|FullyQualifiedName=B' } else { '' }
                        $caught = $null
                        try { Assert-VerificationModeAuthorization -RequestedMode $mode -RequestedFilter $filter -AllowEnvironment $allow }
                        catch { $caught = $_.Exception.Message }
                        $observations += [pscustomobject]@{ Mode = $mode; Filtered = $filtered; Allow = $allow; Accepted = $null -eq $caught }
                    }
                }
            }
            [pscustomobject]@{
                Observations = $observations
                Restricted = Get-VerificationQuickFilter -Filter 'FullyQualifiedName=A|FullyQualifiedName=B' -AllowEnvironment $false
                Explicit = Get-VerificationQuickFilter -Filter 'FullyQualifiedName=A|FullyQualifiedName=B' -AllowEnvironment $true
                EnvironmentBudget = (Get-VerificationPhaseDescriptor -Name 'EnvironmentIntegration').BudgetSeconds
                OrdinaryProcessNames = @(Get-VerificationCategoryTestNames -Category 'ProcessIntegration')
                EnvironmentNames = @(Get-VerificationCategoryTestNames -Category 'EnvironmentIntegration')
            } | ConvertTo-Json -Depth 6 -Compress
            """;
        using JsonDocument result = ReadRunnerProbe(body);
        foreach (JsonElement item in GetProperty(result.RootElement, "Observations").EnumerateArray())
        {
            string mode = GetRequiredString(GetProperty(item, "Mode"));
            bool filtered = GetProperty(item, "Filtered").GetBoolean();
            bool allow = GetProperty(item, "Allow").GetBoolean();
            bool expected = mode switch { "Quick" => filtered || !allow, "Functional" => !filtered && !allow, "Full" => !filtered && allow, _ => false };
            Assert.AreEqual(expected, GetProperty(item, "Accepted").GetBoolean(), item.GetRawText());
        }
        Assert.AreEqual("(FullyQualifiedName=A|FullyQualifiedName=B)&TestCategory!=EnvironmentIntegration", GetRequiredString(GetProperty(result.RootElement, "Restricted")));
        Assert.AreEqual("FullyQualifiedName=A|FullyQualifiedName=B", GetRequiredString(GetProperty(result.RootElement, "Explicit")));
        Assert.AreEqual(180, GetProperty(result.RootElement, "EnvironmentBudget").GetInt32());
        string[] ordinary = GetProperty(result.RootElement, "OrdinaryProcessNames").EnumerateArray().Select(GetRequiredString).ToArray();
        string[] environment = GetProperty(result.RootElement, "EnvironmentNames").EnumerateArray().Select(GetRequiredString).ToArray();
        Assert.IsTrue(ordinary.Length > 0 && environment.Length > 0);
        Assert.IsFalse(ordinary.Intersect(environment).Any());
        Assert.IsTrue(environment.Any(name => name.StartsWith("BeMusicSeeker.Tests.UpdaterPackageSyncTests.", StringComparison.Ordinal)));
    }

    /// <summary>Fullの合算は専用phaseの実TRXも要求し、他phaseが全Passedでも専用phase未実施を成功にしません。</summary>
    [TestMethod]
    public void FullOutcomeGateRejectsMissingEnvironmentPhase()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker-FullOutcome", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (string host in new[] { "portable-settings", "bass-collectible", "shared-state-a", "shared-state-b", "parallel-a", "parallel-b" })
            {
                string directory = Path.Combine(root, "functional", host);
                Directory.CreateDirectory(directory);
                WriteSyntheticTrx(Path.Combine(directory, "results.trx"), ("Synthetic.Normal.Method", "Passed"));
            }
            foreach ((string segment, string name) in new[] { ("process-integration", "ProcessIntegration"), ("environment-integration", "EnvironmentIntegration"), ("release-acceptance", "ReleaseAcceptance") })
            {
                string directory = Path.Combine(root, segment, "test");
                Directory.CreateDirectory(directory);
                WriteSyntheticTrx(Path.Combine(directory, "results.trx"), ("Synthetic." + name + ".Method", "Passed"));
            }
            string roster = Path.Combine(root, "roster.json");
            WriteSyntheticRoster(roster, ("required", "Synthetic.ReleaseAcceptance.Method"));
            string body = $$"""
                $v216ArtifactMetadataPath = {{QuotePowerShellLiteral(roster)}}
                function Get-FunctionalTestMetadata { [pscustomobject]@{ FullyQualifiedName = 'Synthetic.Normal.Method' } }
                function Get-VerificationCategoryTestNames { param($Category) "Synthetic.$Category.Method" }
                $first = Assert-FullVerificationTestOutcomes -DiagnosticsRoot {{QuotePowerShellLiteral(root)}} -ReceiptPath {{QuotePowerShellLiteral(Path.Combine(root, "receipt.json"))}}
                [IO.File]::Delete({{QuotePowerShellLiteral(Path.Combine(root, "environment-integration", "test", "results.trx"))}})
                $caught = $null
                try { $null = Assert-FullVerificationTestOutcomes -DiagnosticsRoot {{QuotePowerShellLiteral(root)}} -ReceiptPath {{QuotePowerShellLiteral(Path.Combine(root, "receipt.json"))}} }
                catch { $caught = $_.Exception.Message }
                [pscustomobject]@{ FirstPassed = $first.Passed; Rejected = $null -ne $caught; Message = [string]$caught } | ConvertTo-Json -Compress
                """;
            using JsonDocument result = ReadRunnerProbe(body);
            Assert.IsTrue(GetProperty(result.RootElement, "FirstPassed").GetBoolean());
            Assert.IsTrue(GetProperty(result.RootElement, "Rejected").GetBoolean());
            StringAssert.Contains(GetRequiredString(GetProperty(result.RootElement, "Message")), "environment-integration");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static JsonDocument ReadRunnerProbe(string body)
    {
        string root = FindRepositoryRoot();
        return ReadPowerShellJson(["-Command", $$"""
            $ErrorActionPreference = 'Stop'
            . {{QuotePowerShellLiteral(Path.Combine(root, "scripts", "verification-process-lifecycle.ps1"))}}
            $signal = [System.Threading.ManualResetEventSlim]::new($false)
            try {
                $guard = [VerificationPostStartFaultGuard]::new($signal)
                . {{QuotePowerShellLiteral(Path.Combine(root, "scripts", "verify-refactor.ps1"))}} -InternalTestGuard $guard
                {{body}}
            } finally { $signal.Dispose() }
            """]);
    }

    private static JsonDocument ReadSelectedOutcomeProbe(string path, params string[] selected)
    {
        string command = $$"""
            $ErrorActionPreference = 'Stop'
            . {{QuotePowerShellLiteral(Path.Combine(FindRepositoryRoot(), "scripts", "verification-test-outcomes.ps1"))}}
            $caught = $null
            try { $null = Assert-VerificationTestOutcomes -ResultPaths @({{QuotePowerShellLiteral(path)}}) -SelectedFqns @({{string.Join(", ", selected.Select(QuotePowerShellLiteral))}}) }
            catch { $caught = $_.Exception.Message }
            [pscustomobject]@{ Passed = $null -eq $caught; Message = [string]$caught } | ConvertTo-Json -Compress
            """;
        return ReadPowerShellJson(["-Command", command]);
    }

    private static Dictionary<string, string> ReadParallelMembership(JsonElement plan, string[] expectedNames)
    {
        var membership = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonElement shard in GetProperty(plan, "Shards").EnumerateArray()
            .Where(shard => GetRequiredString(GetProperty(shard, "Name")) is "parallel-a" or "parallel-b"))
        {
            string hostName = GetRequiredString(GetProperty(shard, "Name"));
            JsonElement names = GetProperty(shard, "TestNames");
            Assert.IsTrue(names.GetArrayLength() > 0, hostName);
            foreach (JsonElement name in names.EnumerateArray())
            {
                string fullyQualifiedName = GetRequiredString(name);
                Assert.IsTrue(membership.TryAdd(fullyQualifiedName, hostName), "Duplicate exact FQN: " + fullyQualifiedName);
            }
        }
        CollectionAssert.AreEquivalent(expectedNames, membership.Keys.ToArray());
        return membership;
    }

    private static string GetRequiredString(JsonElement element)
    {
        string? value = element.GetString();
        Assert.IsNotNull(value, "Required JSON string was null: " + element.GetRawText());
        return value;
    }

    private static XElement GetRequiredElement(XElement parent, string name)
    {
        XElement? element = parent.Element(name);
        Assert.IsNotNull(element, "Required XML element was missing: " + name);
        return element;
    }

    private static void AssertSameMembership(Dictionary<string, string> expected, Dictionary<string, string> actual)
    {
        foreach (KeyValuePair<string, string> item in expected)
        {
            Assert.IsTrue(actual.TryGetValue(item.Key, out string? hostName), item.Key);
            Assert.AreEqual(item.Value, hostName, item.Key);
        }
    }

    private static JsonDocument ReadFunctionalPlanProbe(string directory, string scenario)
    {
        string repositoryRoot = FindRepositoryRoot();
        string command = $$"""
            $ErrorActionPreference = 'Stop'
            . {{QuotePowerShellLiteral(Path.Combine(repositoryRoot, "scripts", "verification-process-lifecycle.ps1"))}}
            $signal = [System.Threading.ManualResetEventSlim]::new($false)
            try {
                $guard = [VerificationPostStartFaultGuard]::new($signal)
                . {{QuotePowerShellLiteral(Path.Combine(repositoryRoot, "scripts", "verify-refactor.ps1"))}} -InternalTestGuard $guard
                $originalMetadata = @(Get-VerificationTestMetadata -AssemblyPath $functionalTestAssemblyPath)
                $ordinary = @(Get-FunctionalTestMetadata | Where-Object {
                    -not $_.DoNotParallelize -and $_.ClassName -cne 'BeMusicSeeker.Tests.PlayerPanelStateSettingsCompatibilityTests' -and
                    $_.ClassName -cne 'BeMusicSeeker.Tests.BassCollectibleLoadContextTests'
                })
                if ({{QuotePowerShellLiteral(scenario)}} -eq 'single') {
                    $script:probeMetadata = @($originalMetadata | Where-Object {
                        $_.DoNotParallelize -or $_.ClassName -ceq 'BeMusicSeeker.Tests.PlayerPanelStateSettingsCompatibilityTests' -or
                        $_.ClassName -ceq 'BeMusicSeeker.Tests.BassCollectibleLoadContextTests'
                    }) + @($ordinary[0])
                    function Get-VerificationTestMetadata { param($AssemblyPath) $script:probeMetadata }
                    $caught = $null
                    try { $null = New-FunctionalShardPlan } catch { $caught = $_.Exception.Message }
                    [pscustomobject]@{ Rejected = $null -ne $caught; Message = $caught } | ConvertTo-Json -Compress
                    return
                }
                $baseline = New-FunctionalShardPlan
                Assert-FunctionalShardConfiguration -Plan $baseline
                $plans = @($baseline)
                $addedName = 'A.Unrelated.Method'
                $removedName = $ordinary[0].FullyQualifiedName
                if ({{QuotePowerShellLiteral(scenario)}} -eq 'variations') {
                    foreach ($shard in $baseline.Shards) {
                        Write-MSTestParallelRunSettings -Path (Join-Path {{QuotePowerShellLiteral(directory)}} ($shard.Name + '.runsettings')) `
                            -Workers $shard.Workers -Scope $shard.Scope -TestCaseFilter $shard.Filter
                    }
                    function Get-VerificationTestMetadata { param($AssemblyPath) $script:probeMetadata }
                    $script:probeMetadata = $originalMetadata.Clone()
                    [array]::Reverse($script:probeMetadata)
                    $plans += New-FunctionalShardPlan
                    $script:probeMetadata = @([pscustomobject]@{ FullyQualifiedName = $addedName; ClassName = 'A.Unrelated'; DoNotParallelize = $false; Categories = @() }) + $originalMetadata
                    foreach ($category in $functionalExcludedCategories) {
                        $script:probeMetadata += [pscustomobject]@{ FullyQualifiedName = "Synthetic.Excluded.$category"; ClassName = "Synthetic.Excluded"; DoNotParallelize = $false; Categories = @($category) }
                    }
                    $added = New-FunctionalShardPlan
                    Assert-FunctionalShardConfiguration -Plan $added
                    $plans += $added
                    $script:probeMetadata = @($originalMetadata | Where-Object FullyQualifiedName -cne $removedName)
                    $deleted = New-FunctionalShardPlan
                    Assert-FunctionalShardConfiguration -Plan $deleted
                    $plans += $deleted
                }
                [pscustomobject]@{
                    Metadata = @($originalMetadata | Select-Object FullyQualifiedName, ClassName, DoNotParallelize, Categories)
                    AddedName = $addedName
                    RemovedName = $removedName
                    Plans = @($plans | ForEach-Object { [pscustomobject]@{
                        Shards = @($_.Shards | Select-Object Name, TestNames)
                    } })
                } | ConvertTo-Json -Depth 8 -Compress
            } finally { $signal.Dispose() }
            """;
        return ReadPowerShellJson(new[] { "-Command", command });
    }

    /// <summary>
    /// 必須FQNの大文字小文字を含む完全一致と一件だけのPassedを要求し、欠落・重複・非成功をリリース合格にする誤りを検出する。
    /// </summary>
    /// <remarks>合成TRX/rosterを実PowerShellの合否判定へ渡し、検証runnerが消費する形式と全Passed判定の接続を確認する。</remarks>
    [TestMethod]
    public void ReleaseOutcomeGateRequiresExactPassedCardinality()
    {
        const string requiredFqn = "BeMusicSeeker.Tests.Synthetic.Required.ReleaseOutcome";
        string root = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker-VerificationOutcomeContractTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string emptyResults = Path.Combine(root, "empty.trx");
            WriteSyntheticTrx(emptyResults);
            using JsonDocument missing = ReadOutcomeGateProbe(emptyResults, requiredFqn);
            AssertGateFailure(missing, "missing");

            string duplicateResults = Path.Combine(root, "duplicate.trx");
            WriteSyntheticTrx(duplicateResults, (requiredFqn, "Passed"), (requiredFqn, "Passed"));
            using JsonDocument duplicate = ReadOutcomeGateProbe(duplicateResults, requiredFqn);
            AssertGateFailure(duplicate, "duplicate");

            string caseVariantResults = Path.Combine(root, "case-variant.trx");
            WriteSyntheticTrx(caseVariantResults, (requiredFqn.ToLowerInvariant(), "Passed"));
            using JsonDocument caseVariant = ReadOutcomeGateProbe(caseVariantResults, requiredFqn);
            AssertGateFailure(caseVariant, "missing");

            foreach (string outcome in new[] { "Skipped", "Inconclusive", "NotExecuted", "Failed" })
            {
                string nonPassedResults = Path.Combine(root, outcome + ".trx");
                WriteSyntheticTrx(nonPassedResults, (requiredFqn, outcome));
                using JsonDocument nonPassed = ReadOutcomeGateProbe(nonPassedResults, requiredFqn);
                AssertGateFailure(nonPassed, outcome);
            }

            string passedResults = Path.Combine(root, "passed.trx");
            WriteSyntheticTrx(passedResults, (requiredFqn, "Passed"));
            using JsonDocument passed = ReadOutcomeGateProbe(passedResults, requiredFqn);
            Assert.IsTrue(GetProperty(passed.RootElement, "Passed").GetBoolean(), passed.RootElement.GetRawText());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>旧optional許可記述が入力に残っても、どの非Passed結果もリリース成功へ変換しません。</summary>
    [TestMethod]
    public void ReleaseOutcomeGateRejectsNonPassedEvenWithLegacyOptionalRoster()
    {
        const string requiredFqn = "Synthetic.Required.Method";
        const string legacyFqn = "Synthetic.Legacy.Method";
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker-LegacyOutcomeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string rosterPath = Path.Combine(root, "legacy-roster.json");
            File.WriteAllText(rosterPath, JsonSerializer.Serialize(new
            {
                required = new[] { new { fullyQualifiedName = requiredFqn } },
                optionalSkipAllowlist = new[] { new { fullyQualifiedName = legacyFqn, reason = "legacy provisioned dependency" } }
            }));
            foreach (string outcome in new[] { "Skipped", "Inconclusive", "NotExecuted", "Failed", "Error", "Aborted" })
            {
                string path = Path.Combine(root, outcome + ".trx");
                WriteSyntheticTrx(path, (requiredFqn, "Passed"), (legacyFqn, outcome));
                using JsonDocument result = ReadOutcomeRosterGateProbe(path, rosterPath);
                AssertGateFailure(result, outcome);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// 指定したrosterの必須ケースを実結果と照合し、既定一覧だけを使って指定入力を無視する誤りを検出する。
    /// </summary>
    /// <remarks>合成TRX/rosterを実PowerShellの合否判定へ渡し、検証runnerが消費する形式と全Passed判定の接続を確認する。</remarks>
    [TestMethod]
    public void ReleaseOutcomeGateLoadsRequiredRosterFromSuppliedPath()
    {
        const string requiredFqn = "BeMusicSeeker.Tests.Synthetic.Roster.ReleaseOutcome";
        string root = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker-VerificationRosterContractTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string resultsPath = Path.Combine(root, "passed.trx");
            string rosterPath = Path.Combine(root, "roster.json");
            WriteSyntheticTrx(resultsPath, (requiredFqn, "Passed"));
            WriteSyntheticRoster(rosterPath, ("SYNTHETIC-REQUIRED", requiredFqn));

            using JsonDocument result = ReadOutcomeRosterGateProbe(resultsPath, rosterPath);
            Assert.IsTrue(GetProperty(result.RootElement, "Passed").GetBoolean(), result.RootElement.GetRawText());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static JsonDocument ReadOutcomeGateProbe(string resultPath, string requiredFqn)
    {
        string command = $$"""
            $ErrorActionPreference = 'Stop'
            . {{QuotePowerShellLiteral(Path.Combine(FindRepositoryRoot(), "scripts", "verification-test-outcomes.ps1"))}}
            $caught = $null
            try { $gate = Assert-VerificationTestOutcomes -ResultPaths @({{QuotePowerShellLiteral(resultPath)}}) -RequiredFqns @({{QuotePowerShellLiteral(requiredFqn)}}) }
            catch { $caught = $_.Exception.Message }
            [pscustomobject]@{ Passed = $null -eq $caught; Message = [string]$caught } | ConvertTo-Json -Compress
            """;
        return ReadPowerShellJson(["-Command", command]);
    }

    private static JsonDocument ReadOutcomeRosterGateProbe(string resultPath, string rosterPath)
    {
        string repositoryRoot = FindRepositoryRoot();
        string helperPath = QuotePowerShellLiteral(Path.Combine(repositoryRoot, "scripts", "verification-test-outcomes.ps1"));
        string resultLiteral = QuotePowerShellLiteral(resultPath);
        string rosterLiteral = QuotePowerShellLiteral(rosterPath);
        string command = string.Join(
            Environment.NewLine,
            "Set-StrictMode -Version Latest",
            "$ErrorActionPreference = 'Stop'",
            ". " + helperPath,
            "$caught = $null",
            "try {",
            "  $gate = Assert-VerificationTestOutcomes -ResultPaths @(" + resultLiteral + ") -RosterPath " + rosterLiteral,
            "} catch { $caught = $_.Exception.Message }",
            "[pscustomobject]@{ Passed = $null -eq $caught; Message = if ($null -eq $caught) { [string]::Empty } else { [string]$caught } } | ConvertTo-Json -Depth 8 -Compress");
        return ReadPowerShellJson(new[] { "-Command", command });
    }

    private static void AssertGateFailure(JsonDocument result, string expectedMessagePart)
    {
        AssertGateFailure(result);
        StringAssert.Contains(
            GetRequiredString(GetProperty(result.RootElement, "Message")),
            expectedMessagePart,
            result.RootElement.GetRawText());
    }

    private static void AssertGateFailure(JsonDocument result)
    {
        Assert.IsFalse(GetProperty(result.RootElement, "Passed").GetBoolean(), result.RootElement.GetRawText());
    }

    private static void WriteSyntheticTrx(string path, params (string FullyQualifiedName, string Outcome)[] results)
    {
        var trx = new XDocument(new XElement("TestRun",
            new XElement("Results", results.Select((result, index) => new XElement("UnitTestResult",
                new XAttribute("testId", index), new XAttribute("outcome", result.Outcome)))),
            new XElement("TestDefinitions", results.Select((result, index) =>
            {
                int separator = result.FullyQualifiedName.LastIndexOf('.');
                return new XElement("UnitTest", new XAttribute("id", index),
                    new XElement("TestMethod", new XAttribute("className", result.FullyQualifiedName[..separator]),
                        new XAttribute("name", result.FullyQualifiedName[(separator + 1)..])));
            })),
            new XElement("ResultSummary", new XAttribute("outcome", "Completed"))));
        trx.Save(path);
    }

    private static void WriteSyntheticRoster(string path, params (string ContractId, string FullyQualifiedName)[] requiredEntries)
    {
        string json = JsonSerializer.Serialize(new
        {
            releaseOutcome = new
            {
                schemaVersion = 1,
                required = requiredEntries
                    .Select(entry => new
                    {
                        contractId = entry.ContractId,
                        fullyQualifiedName = entry.FullyQualifiedName
                    })
                    .ToArray()
            }
        });
        File.WriteAllText(path, json, Encoding.UTF8);
    }

    private static JsonDocument ReadPowerShellJson(IReadOnlyList<string> arguments)
    {
        const int processTimeoutMilliseconds = 60_000;
        const int cleanupTimeoutMilliseconds = 5_000;
        const int streamTimeoutMilliseconds = 5_000;
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        Assert.IsTrue(process.Start(), "The PowerShell outcome-gate process did not start.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(processTimeoutMilliseconds))
        {
            string cleanup = StopProcessTree(process, cleanupTimeoutMilliseconds);
            Assert.Fail(
                $"The PowerShell outcome-gate process exceeded the {processTimeoutMilliseconds / 1000}-second timeout. " +
                $"Process cleanup: {cleanup}. stdout: {GetCompletedTaskValue(outputTask)} stderr: {GetCompletedTaskValue(errorTask)}");
            return default!;
        }

        try
        {
            if (!Task.WaitAll(new Task[] { outputTask, errorTask }, streamTimeoutMilliseconds))
            {
                string cleanup = StopProcessTree(process, cleanupTimeoutMilliseconds);
                Assert.Fail(
                    $"The PowerShell outcome-gate output did not close within the {streamTimeoutMilliseconds / 1000}-second timeout. " +
                    $"Process cleanup: {cleanup}. stdout: {GetCompletedTaskValue(outputTask)} stderr: {GetCompletedTaskValue(errorTask)}");
                return default!;
            }
        }
        catch (Exception exception) when (exception is not AssertFailedException)
        {
            string cleanup = StopProcessTree(process, cleanupTimeoutMilliseconds);
            Assert.Fail($"The PowerShell outcome-gate output failed: {exception}. Process cleanup: {cleanup}");
            return default!;
        }

        string output = outputTask.GetAwaiter().GetResult();
        string error = errorTask.GetAwaiter().GetResult();
        Assert.AreEqual(0, process.ExitCode, error);
        Assert.IsTrue(string.IsNullOrWhiteSpace(error), error);
        return JsonDocument.Parse(output);
    }

    private static string QuotePowerShellLiteral(string value)
    {
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static string StopProcessTree(Process process, int cleanupTimeoutMilliseconds)
    {
        var diagnostics = new List<string>();
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception)
        {
            diagnostics.Add("managed tree termination failed: " + exception.Message);
        }

        try
        {
            if (!process.HasExited && !process.WaitForExit(cleanupTimeoutMilliseconds))
            {
                diagnostics.Add($"PowerShell PID {process.Id} remained active after {cleanupTimeoutMilliseconds / 1000}s.");
            }
        }
        catch (Exception exception)
        {
            diagnostics.Add("process cleanup wait failed: " + exception.Message);
        }

        return diagnostics.Count == 0 ? "completed" : string.Join("; ", diagnostics);
    }

    private static string GetCompletedTaskValue(Task<string> task)
    {
        return task.Status == TaskStatus.RanToCompletion ? task.Result : "<unavailable>";
    }

    private static JsonElement GetProperty(JsonElement element, string name)
    {
        Assert.IsTrue(element.TryGetProperty(name, out JsonElement value), $"Outcome property was not found: {name}");
        return value;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
