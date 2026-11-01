using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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

    /// <summary>ログoptionと同じ先頭を持つ相対manifestは値として消費し、ログ指定へ再解釈しません。前Errorのみ・指定なし・後Warningを分担し、ログ結果と全引数の保持を共有ログ状態を変えずに確認します。</summary>
    [TestMethod]
    [DataRow("--test-session", "Error", null, NormalLogLevel.Error)]
    [DataRow("--TeSt-SeSsIoN", null, null, NormalLogLevel.Info)]
    [DataRow("--TEST-SESSION", "Error", "warning", NormalLogLevel.Warn)]
    public void ManifestValueWithLogOptionPrefixDoesNotOverrideCapturedLogging(string sessionSwitch, string? beforeLevel, string? afterLevel, NormalLogLevel expectedLevel)
    {
        (NormalLogLevel LogLevel, bool IsInfoLoggingEnabled, bool HasInvalidLogLevelValue, string InvalidLogLevelValue) processLoggingBefore = (CommandLineSwitches.LogLevel, CommandLineSwitches.IsInfoLoggingEnabled,
            CommandLineSwitches.HasInvalidLogLevelValue, CommandLineSwitches.InvalidLogLevelValue);
        string currentDirectory = Environment.CurrentDirectory;
        const string rootNamePrefix = "--log-level=case-";
        using var fixture = new ManifestFixture(currentDirectory, rootNamePrefix);
        fixture.Write(fixture.CreateDocument());
        string relativeManifest = Path.GetRelativePath(currentDirectory, fixture.ManifestPath);
        Assert.IsFalse(Path.IsPathFullyQualified(relativeManifest));
        StringAssert.StartsWith(relativeManifest, rootNamePrefix);
        Assert.IsTrue(File.Exists(relativeManifest), "実在する相対manifestを解析へ渡します。");
        var arguments = new List<string> { "unknown value" };
        if (beforeLevel is not null) { arguments.Add("--log-level=" + beforeLevel); }
        arguments.Add(sessionSwitch);
        int manifestArgumentIndex = arguments.Count;
        arguments.Add(relativeManifest);
        if (afterLevel is not null) { arguments.Add("--LOG-LEVEL=" + afterLevel); }
        string[] inputArguments = arguments.ToArray();
        string[] expectedRestartArguments = arguments.ToArray();
        expectedRestartArguments[manifestArgumentIndex] = fixture.ManifestPath;

        ApplicationRuntimeOptions options = ApplicationRuntimeOptionsParser.Capture(ExecutablePath, inputArguments);

        Assert.IsNotNull(options.TestSession);
        Assert.AreEqual(fixture.ManifestPath, options.TestSession.ManifestPath);
        Assert.AreEqual(expectedLevel, options.LogOptions.Level);
        Assert.AreEqual(string.Empty, options.LogOptions.InvalidValue);
        CollectionAssert.AreEqual(expectedRestartArguments, options.RestartArguments.ToArray());
        CollectionAssert.AreEqual(arguments, inputArguments, "呼出元の引数配列を書き換えません。");
        Assert.AreEqual(currentDirectory, Environment.CurrentDirectory);
        fixture.AssertOnlyManifestExists();
        Assert.AreEqual(processLoggingBefore, (CommandLineSwitches.LogLevel, CommandLineSwitches.IsInfoLoggingEnabled,
            CommandLineSwitches.HasInvalidLogLevelValue, CommandLineSwitches.InvalidLogLevelValue));
    }

    /// <summary>更新URLのoptionと同じ先頭を持つ相対manifestも値として一度だけ消費し、そのパスだけを絶対化します。URLの前指定のみ・指定なし・前後指定を分担し、それぞれ前URL・既定値・後URLを返して、他の引数・元配列・CWDを保持します。</summary>
    [TestMethod]
    [DataRow("--test-session", "https://example.invalid/before.json", null, "https://example.invalid/before.json")]
    [DataRow("--TeSt-SeSsIoN", null, null, null)]
    [DataRow("--TEST-SESSION", "https://example.invalid/before.json", "https://example.invalid/after.json", "https://example.invalid/after.json")]
    public void TestSessionCanonicalizesManifestAndPreservesOtherRestartArguments(string sessionSwitch, string? beforeUrl, string? afterUrl, string? expectedUrl)
    {
        string currentDirectory = Environment.CurrentDirectory;
        const string rootNamePrefix = "--update-manifest-url=case-";
        // CWDを変更せず相対パスを必ず入力できるよう、このケースだけCWD内の専有rootを使います。
        using var fixture = new ManifestFixture(currentDirectory, rootNamePrefix);
        fixture.Write(fixture.CreateDocument());
        string relativeManifest = Path.GetRelativePath(currentDirectory, fixture.ManifestPath);
        Assert.IsFalse(Path.IsPathFullyQualified(relativeManifest));
        StringAssert.StartsWith(relativeManifest, rootNamePrefix);
        Assert.IsTrue(File.Exists(relativeManifest), "実在する相対manifestを解析へ渡します。");
        var arguments = new List<string> { "unknown value" };
        if (beforeUrl is not null) { arguments.Add("--update-manifest-url=" + beforeUrl); }
        arguments.Add(sessionSwitch);
        int manifestArgumentIndex = arguments.Count;
        arguments.Add(relativeManifest);
        if (afterUrl is not null) { arguments.Add("--UPDATE-MANIFEST-URL=" + afterUrl); }
        string[] inputArguments = arguments.ToArray();
        string[] expectedRestartArguments = arguments.ToArray();
        expectedRestartArguments[manifestArgumentIndex] = fixture.ManifestPath;

        ApplicationRuntimeOptions options = ApplicationRuntimeOptionsParser.Capture(ExecutablePath, inputArguments);

        Assert.IsNotNull(options.TestSession);
        Assert.AreEqual(fixture.ManifestPath, options.TestSession.ManifestPath);
        CollectionAssert.AreEqual(expectedRestartArguments, options.RestartArguments.ToArray());
        CollectionAssert.AreEqual(arguments, inputArguments, "呼出元の引数配列を書き換えません。");
        Assert.AreEqual(expectedUrl ?? UpdateCheckService.DefaultManifestUrl, options.UpdateManifestUrl);
        Assert.AreEqual(currentDirectory, Environment.CurrentDirectory);
        fixture.AssertOnlyManifestExists();
    }

    /// <summary>最小manifestと、省略・空object・file-system指定のproviderを受理し、素材は実行体、言語・一時領域・移行元は専有profileの子へ解決します。</summary>
    [TestMethod]
    [DataRow(null)]
    [DataRow("{}")]
    [DataRow("{\"fileEnumeration\":\"file-system\"}")]
    public void MinimalSessionResolvesDefaultLayoutAndAcceptsSupportedProvider(string? providers)
    {
        using var fixture = new ManifestFixture();
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
        using var fixture = new ManifestFixture();
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
        using var fixture = new ManifestFixture();
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
        using var fixture = new ManifestFixture();
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
        using var fixture = new ManifestFixture();
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
        using var fixture = new ManifestFixture();
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
        using var fixture = new ManifestFixture();
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
        using var fixture = new ManifestFixture();
        foreach (string invalidUrl in new[] { "null", "\"\"", "\"  \"", "\"relative.json\"", "\"ftp://example.invalid/update.json\"", "42" })
        {
            JsonObject document = fixture.CreateDocument();
            document["updateManifestUrl"] = JsonNode.Parse(invalidUrl);
            fixture.AssertRejected(document.ToJsonString(), "--update-manifest-url=https://example.invalid/cli.json");
        }
    }

    private static string ExecutablePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("実行ファイルの位置を取得できません。");

    /// <summary>解析入力だけを専有rootに用意し、profileへの書込みを検出して全入力を回収します。</summary>
    private sealed class ManifestFixture : IDisposable
    {
        /// <summary>通常は一時領域、相対パスの検査では指定したCWDの下に、指定prefixとGUIDで専有rootを作ります。</summary>
        internal ManifestFixture(string? baseDirectory = null, string rootNamePrefix = "Bms runtime parser 検証 ")
        {
            Root = Path.Combine(baseDirectory ?? Path.GetTempPath(), rootNamePrefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        /// <summary>このfixtureだけが所有し、終了時に回収するディレクトリです。</summary>
        internal string Root { get; }

        /// <summary>解析時に未作成のまま保つprofileの絶対パスです。</summary>
        internal string ProfileDirectory => Path.Combine(Root, "profile");

        /// <summary>専有するmanifest入力の絶対パスです。</summary>
        internal string ManifestPath => Path.Combine(Root, "session.json");

        /// <summary>既定配置の判定と不正値への置換に使う、必須fieldだけの入力を返します。</summary>
        internal JsonObject CreateDocument() => new()
        {
            ["schemaVersion"] = 1,
            ["profileDirectory"] = ProfileDirectory,
            ["runId"] = "run-id",
            ["caseId"] = "case-id",
            ["controlPipeName"] = "control-pipe"
        };

        /// <summary>新しい解析入力を専有ファイルへ書き込みます。</summary>
        internal void Write(JsonObject document) => File.WriteAllText(ManifestPath, document.ToJsonString());

        /// <summary>実起動と同じparserへmanifest指定と追加引数を渡します。</summary>
        internal ApplicationRuntimeOptions Capture(params string[] additionalArguments)
            => ApplicationRuntimeOptionsParser.Capture(ExecutablePath, new[] { "--test-session", ManifestPath }.Concat(additionalArguments));

        /// <summary>入力失敗後もmanifestのbytesと、profile未作成を含む専有rootの内容を保持することを確認します。</summary>
        internal void AssertRejected(string json, params string[] additionalArguments)
        {
            File.WriteAllText(ManifestPath, json);
            byte[] before = File.ReadAllBytes(ManifestPath);
            Assert.Throws<Exception>(() => Capture(additionalArguments), json);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(ManifestPath));
            AssertOnlyManifestExists();
        }

        /// <summary>解析の前後にprofileなどの出力が作られず、専有rootに入力だけが残ることを確認します。</summary>
        internal void AssertOnlyManifestExists()
        {
            Assert.IsFalse(Directory.Exists(ProfileDirectory), "解析だけでprofileを作成しません。");
            CollectionAssert.AreEqual(new[] { ManifestPath }, Directory.GetFileSystemEntries(Root));
        }

        /// <summary>自分で生成した専有rootと入力だけを回収します。</summary>
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
