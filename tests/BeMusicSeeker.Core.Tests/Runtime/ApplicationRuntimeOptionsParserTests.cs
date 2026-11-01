using System.Text.Json.Nodes;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Runtime;
using BeMusicSeeker.TestSupport.Components;

namespace BeMusicSeeker.Tests;

/// <remarks>専有manifestだけを読み書きし、環境変数・CWD・ApplicationPathPolicyを変更しません。実起動とproviderの接続は別プロセスの起動検証へ分担します。</remarks>
[TestClass]
public sealed class ApplicationRuntimeOptionsParserTests
{
    /// <summary>通常起動は未知引数と不正な非空URLも保持し、更新URLは大小文字を区別せず最後の指定を採用し、未指定・空値・空白では従来どおり既定値を返します。</summary>
    [TestMethod]
    [DataRow(null, null)]
    [DataRow("", null)]
    [DataRow("  ", null)]
    [DataRow("not a URI", "not a URI")]
    [DataRow("https://example.invalid/last.json", "https://example.invalid/last.json")]
    public void NormalArgumentsPreserveUnknownValuesAndResolveLastUpdateUrl(string? lastUrl, string? expectedUrl)
    {
        string currentDirectory = Environment.CurrentDirectory;
        var arguments = new List<string> { "--unknown-option", "値 with spaces", "--other=value" };
        if (lastUrl is not null)
        {
            arguments.Add("--update-manifest-url=https://example.invalid/first.json");
            arguments.Add("--UPDATE-MANIFEST-URL=" + lastUrl);
        }

        ApplicationRuntimeOptions options = ApplicationRuntimeOptionsParser.Capture(ExecutablePath, arguments);

        Assert.IsNull(options.TestSession);
        Assert.AreEqual(expectedUrl ?? UpdateCheckService.DefaultManifestUrl, options.UpdateManifestUrl);
        CollectionAssert.AreEqual(arguments, options.RestartArguments.ToArray());
        Assert.AreEqual(currentDirectory, Environment.CurrentDirectory);
    }

    /// <summary>ログ指定は最後の値を大小文字を区別せず解釈し、未知値・空値・空白も切り詰めず保持してWarnにします。未指定はInfoとし、argvと共有ログgetterを変更しません。</summary>
    [TestMethod]
    [DataRow(null, NormalLogLevel.Info, "")]
    [DataRow("InFo", NormalLogLevel.Info, "")]
    [DataRow("WaRn", NormalLogLevel.Warn, "")]
    [DataRow("WaRnInG", NormalLogLevel.Warn, "")]
    [DataRow("ErRoR", NormalLogLevel.Error, "")]
    [DataRow("unknown Raw", NormalLogLevel.Warn, "unknown Raw")]
    [DataRow("", NormalLogLevel.Warn, "")]
    [DataRow("  ", NormalLogLevel.Warn, "  ")]
    [DataRow(" info ", NormalLogLevel.Warn, " info ")]
    public void NormalLogArgumentsCaptureLastValueWithoutChangingProcessLogging(string? raw, NormalLogLevel expectedLevel, string expectedInvalidValue)
    {
        (NormalLogLevel LogLevel, bool IsInfoLoggingEnabled, bool HasInvalidLogLevelValue, string InvalidLogLevelValue) processLoggingBefore = (CommandLineSwitches.LogLevel, CommandLineSwitches.IsInfoLoggingEnabled,
            CommandLineSwitches.HasInvalidLogLevelValue, CommandLineSwitches.InvalidLogLevelValue);
        var arguments = new List<string> { "--unknown-option", "値 with spaces" };
        if (raw is not null)
        {
            arguments.Add("--log-level=Error");
            arguments.Add("--LoG-LeVeL=" + raw);
        }

        ApplicationRuntimeOptions options = ApplicationRuntimeOptionsParser.Capture(ExecutablePath, arguments);

        Assert.AreEqual(expectedLevel, options.LogOptions.Level);
        Assert.AreEqual(expectedInvalidValue, options.LogOptions.InvalidValue);
        CollectionAssert.AreEqual(arguments, options.RestartArguments.ToArray());
        Assert.AreEqual(processLoggingBefore, (CommandLineSwitches.LogLevel, CommandLineSwitches.IsInfoLoggingEnabled,
            CommandLineSwitches.HasInvalidLogLevelValue, CommandLineSwitches.InvalidLogLevelValue));
    }





    /// <summary>最小manifestと、省略・空object・file-system指定のproviderを受理し、素材は実行体、言語・一時領域・移行元は専有profileの子へ解決します。</summary>
    [TestMethod]
    [DataRow(null)]
    [DataRow("{}")]
    [DataRow("{\"fileEnumeration\":\"file-system\"}")]
    public void MinimalSessionResolvesDefaultLayoutAndAcceptsSupportedProvider(string? providers)
    {
        using var fixture = new RuntimeOptionsManifestFixture();
        JsonObject document = fixture.CreateDocument();
        if (providers is not null) { document["providers"] = JsonNode.Parse(providers); }
        fixture.Write(document);

        ApplicationRuntimeOptions options = fixture.Capture();

        Assert.IsNotNull(options.TestSession);
        Assert.AreEqual("run-id", options.TestSession.RunId);
        Assert.AreEqual("case-id", options.TestSession.CaseId);
        Assert.AreEqual("control-pipe", options.TestSession.ControlPipeName);
        Assert.AreEqual(ExecutablePath, options.Paths.ExecutablePath);
        Assert.AreEqual(Path.GetDirectoryName(ExecutablePath), options.Paths.AssetsDirectory);
        Assert.AreEqual(fixture.ProfileDirectory, options.Paths.ProfileDirectory);
        Assert.AreEqual(Path.Combine(fixture.ProfileDirectory, "lang"), options.Paths.LanguageDirectory);
        Assert.AreEqual(Path.Combine(fixture.ProfileDirectory, "temp"), options.Paths.TempDirectory);
        Assert.AreEqual(Path.Combine(fixture.ProfileDirectory, "legacy"), options.Paths.LegacySettingsDirectory);
        Assert.AreEqual(UpdateCheckService.DefaultManifestUrl, options.UpdateManifestUrl);
        fixture.AssertOnlyManifestExists();
    }

    /// <summary>明示した絶対パスを正規化して個別に使い、manifestのHTTP(S)更新URLをCLIより優先します。解析だけではprofileや素材を作りません。</summary>
    [TestMethod]
    [DataRow("http://example.invalid/session.json")]
    [DataRow("https://example.invalid/session.json")]
    public void ExplicitSessionLayoutAndUpdateUrlTakePriority(string manifestUrl)
    {
        using var fixture = new RuntimeOptionsManifestFixture();
        JsonObject document = fixture.CreateDocument();
        document["profileDirectory"] = Path.Combine(fixture.Root, "unused", "..", "profile");
        document["assetsDirectory"] = Path.Combine(fixture.Root, "assets", ".");
        document["languageDirectory"] = Path.Combine(fixture.Root, "language", ".");
        document["tempDirectory"] = Path.Combine(fixture.Root, "temporary", ".");
        document["legacySettingsDirectory"] = Path.Combine(fixture.Root, "legacy", ".");
        document["updateManifestUrl"] = manifestUrl;
        fixture.Write(document);
        string currentDirectory = Environment.CurrentDirectory;

        ApplicationRuntimeOptions options = fixture.Capture("--update-manifest-url=https://example.invalid/cli.json");

        Assert.AreEqual(fixture.ProfileDirectory, options.Paths.ProfileDirectory);
        Assert.AreEqual(Path.Combine(fixture.Root, "assets"), options.Paths.AssetsDirectory);
        Assert.AreEqual(Path.Combine(fixture.Root, "language"), options.Paths.LanguageDirectory);
        Assert.AreEqual(Path.Combine(fixture.Root, "temporary"), options.Paths.TempDirectory);
        Assert.AreEqual(Path.Combine(fixture.Root, "legacy"), options.Paths.LegacySettingsDirectory);
        Assert.AreEqual(manifestUrl, options.UpdateManifestUrl);
        Assert.AreEqual(currentDirectory, Environment.CurrentDirectory);
        fixture.AssertOnlyManifestExists();
    }

    /// <summary>欠損・空値・重複・イコール形式・不正prefixを拒否し、通常起動への暗黙の切替やprofile作成を防ぎます。</summary>
    [TestMethod]
    public void InvalidTestSessionSwitchFormsAreRejectedWithoutCreatingProfile()
    {
        using var fixture = new RuntimeOptionsManifestFixture();
        fixture.Write(fixture.CreateDocument());
        string[][] invalidArguments =
        [
            ["--test-session"], ["--test-session", ""], ["--test-session", "  "],
            ["--test-session", fixture.ManifestPath, "--TEST-SESSION", fixture.ManifestPath],
            ["--test-session=" + fixture.ManifestPath], ["--test-session-extra", fixture.ManifestPath],
            ["--test-sessionx", fixture.ManifestPath]
        ];
        foreach (string[] arguments in invalidArguments)
        {
            Assert.Throws<Exception>(() => ApplicationRuntimeOptionsParser.Capture(ExecutablePath, arguments), string.Join(" | ", arguments));
            fixture.AssertOnlyManifestExists();
        }
    }

    /// <summary>manifestはJSON objectを要求し、schemaVersionの欠損・非整数・別版を拒否します。例外型の詳細に依存せず入力失敗を確認します。</summary>
    [TestMethod]
    public void InvalidManifestRootAndSchemaVersionAreRejected()
    {
        using var fixture = new RuntimeOptionsManifestFixture();
        foreach (string invalidRoot in new[] { "null", "[]", "true", "\"text\"", "{" })
        {
            fixture.AssertRejected(invalidRoot);
        }
        JsonObject missing = fixture.CreateDocument();
        missing.Remove("schemaVersion");
        fixture.AssertRejected(missing.ToJsonString());
        foreach (string invalidVersion in new[] { "null", "0", "2", "1.5", "\"1\"", "true", "[]", "{}" })
        {
            JsonObject document = fixture.CreateDocument();
            document["schemaVersion"] = JsonNode.Parse(invalidVersion);
            fixture.AssertRejected(document.ToJsonString());
        }
    }

    /// <summary>実行・ケース・通信の各識別子について、欠損・null・空白・非文字列を拒否し、識別できないセッションを受理しません。</summary>
    [TestMethod]
    [DataRow("runId")]
    [DataRow("caseId")]
    [DataRow("controlPipeName")]
    public void RequiredSessionIdentifiersRejectMissingOrInvalidValues(string field)
    {
        using var fixture = new RuntimeOptionsManifestFixture();
        JsonObject missing = fixture.CreateDocument();
        missing.Remove(field);
        fixture.AssertRejected(missing.ToJsonString());
        foreach (string invalidValue in new[] { "null", "\"\"", "\"  \"", "42", "true", "[]", "{}" })
        {
            JsonObject document = fixture.CreateDocument();
            document[field] = JsonNode.Parse(invalidValue);
            fixture.AssertRejected(document.ToJsonString());
        }
    }

    /// <summary>profileは必須とし、各配置を指定した場合はnull・空白・相対パス・非文字列を拒否します。任意配置の省略時の保証は最小manifestのケースへ分担します。</summary>
    [TestMethod]
    [DataRow("profileDirectory")]
    [DataRow("assetsDirectory")]
    [DataRow("languageDirectory")]
    [DataRow("tempDirectory")]
    [DataRow("legacySettingsDirectory")]
    public void SessionPathsRequireAbsoluteNonemptyStringsWhenSpecified(string field)
    {
        using var fixture = new RuntimeOptionsManifestFixture();
        if (field == "profileDirectory")
        {
            JsonObject missing = fixture.CreateDocument();
            missing.Remove(field);
            fixture.AssertRejected(missing.ToJsonString());
        }
        foreach (string invalidValue in new[] { "null", "\"\"", "\"  \"", "\"relative/path\"", "42", "true", "[]", "{}" })
        {
            JsonObject document = fixture.CreateDocument();
            document[field] = JsonNode.Parse(invalidValue);
            fixture.AssertRejected(document.ToJsonString());
        }
    }

    /// <summary>未知のfield・provider、同名JSON fieldの重複、providerの非object・非文字列・表記違いを拒否し、指定の取り違えを隠しません。</summary>
    [TestMethod]
    public void UnknownDuplicateOrUnsupportedManifestFieldsAndProvidersAreRejected()
    {
        using var fixture = new RuntimeOptionsManifestFixture();
        string prefix = fixture.CreateDocument().ToJsonString()[..^1] + ",";
        string[] invalidFields =
        [
            "\"unknown\":true", "\"runId\":\"duplicate\"", "\"schemaVersion\":1",
            "\"providers\":null", "\"providers\":[]", "\"providers\":\"file-system\"",
            "\"providers\":{\"unknown\":\"file-system\"}",
            "\"providers\":{\"fileEnumeration\":\"file-system\",\"fileEnumeration\":\"file-system\"}",
            "\"providers\":{\"fileEnumeration\":\"everything\"}", "\"providers\":{\"fileEnumeration\":\"FILE-SYSTEM\"}",
            "\"providers\":{\"fileEnumeration\":\"\"}", "\"providers\":{\"fileEnumeration\":null}",
            "\"providers\":{\"fileEnumeration\":42}", "\"providers\":{\"fileEnumeration\":{}}"
        ];
        foreach (string invalidField in invalidFields)
        {
            fixture.AssertRejected(prefix + invalidField + "}");
        }
    }

    /// <summary>manifestにURLを明示した場合はnull・空値・相対URL・HTTP(S)以外・非文字列を拒否し、CLIや既定URLへ置き換えません。</summary>
    [TestMethod]
    public void ExplicitManifestUpdateUrlRejectsInvalidValuesWithoutFallingBack()
    {
        using var fixture = new RuntimeOptionsManifestFixture();
        foreach (string invalidUrl in new[] { "null", "\"\"", "\"  \"", "\"relative.json\"", "\"ftp://example.invalid/update.json\"", "42" })
        {
            JsonObject document = fixture.CreateDocument();
            document["updateManifestUrl"] = JsonNode.Parse(invalidUrl);
            fixture.AssertRejected(document.ToJsonString(), "--update-manifest-url=https://example.invalid/cli.json");
        }
    }

    private static string ExecutablePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("実行ファイルの位置を取得できません。");

}
